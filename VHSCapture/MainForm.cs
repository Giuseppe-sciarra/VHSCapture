using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VHSCapture
{
    public class MainForm : Form
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly CaptureEngine engine = new CaptureEngine();

        SplitContainer splitLog, splitMain, splitRight;
        Card top, cardSources, cardMixer, cardLog, status;
        CanvasView canvas;
        RoundedButton btnRec, btnPause, btnProfile, btnSettings, btnFolder, btnTheme, btnLog;
        DateTime? pausedSince; TimeSpan pausedTotal;
        RoundedButton btnAdd, btnRemove, btnProps, btnUp, btnDown;
        TextBox txtName, txtLog;
        Label lblStatus; Pill lblRec;
        SourceList srcList;
        RoundedButton btnPanels; Label lblName;
        Panel mixer;
        readonly Dictionary<string, MixerRow> mixerRows = new Dictionary<string, MixerRow>();
        System.Windows.Forms.Timer timer, restartTimer;

        DateTime recStart;
        string recFile, finalFile, recBase, recFolder;
        readonly AudioMonitor monitor = new AudioMonitor();
        EngineStats lastStats; double lastCpu;
        RoundedButton btnMonitor;
        bool finalizing, syncingList;
        int frames; DateTime lastFrameAt = DateTime.MinValue;
        readonly System.Text.StringBuilder runLog = new System.Text.StringBuilder();
        bool autoRetried, devicesResolved;
        // diagnostica anteprima
        readonly System.Diagnostics.Stopwatch paintClock = System.Diagnostics.Stopwatch.StartNew();
        double lastPaint, paintMaxGap, paintWindowStart; long painted;
        string previewDiag = "";
        readonly List<(double af, double ag, double pf, double pg, long dr, double src, double outf)> diagAcc = new List<(double, double, double, double, long, double, double)>();
        // fine cassetta
        DateTime? blankSince; string blankKind = ""; double blankStartRecSec = -1; int contentSamples; bool autoStopped;

        public MainForm()
        {
            // versione nel titolo: così si vede subito quale build sta girando
            var ver = typeof(MainForm).Assembly.GetName().Version;
            Text = $"VHSCapture  v{ver.Major}.{ver.Minor}.{ver.Build}";
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(640, 420);
            ClientSize = new Size(settings.WindowW, settings.WindowH);
            StartPosition = FormStartPosition.CenterScreen;
            if (settings.WindowMax) WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.F5) { e.Handled = true; if (!engine.IsRecording) StartPreview(); return; }
                if (e.KeyCode == Keys.F10) { e.Handled = true; TogglePause(); return; }
                if (e.KeyCode != Keys.F9) return;
                e.Handled = true;
                ToggleRecording();
            };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Theme.Apply(this, settings.DarkTheme);
            RefreshSourceList();
            RebuildMixer();

            engine.FrameAvailable += () => canvas?.NotifyFrame();
            engine.AudioLevels += (id, rl, pl, rr, pr) => { if (IsHandleCreated) try { BeginInvoke(new Action(() => { if (mixerRows.TryGetValue(id, out var row)) row.Meter.SetLevels(rl, pl, rr, pr); })); } catch { } };
            engine.Stats += st => lastStats = st;
            engine.SignalState += (id, blank, kind) => { if (IsHandleCreated) try { BeginInvoke(new Action(() => OnSignal(blank, kind))); } catch { } };
            engine.MonitorData += (d, n) => monitor.Add(d, n);
            engine.Log += l => { lock (runLog) { if (runLog.Length < 20000) runLog.AppendLine(l); } AppendLog(l); };
            engine.Exited += OnEngineExited;

            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (o, e) => UpdateStatus();
            timer.Start();
            restartTimer = new System.Windows.Forms.Timer { Interval = 450 };
            restartTimer.Tick += (o, e) => { restartTimer.Stop(); if (!engine.IsRecording) StartPreview(); };

            Shown += (o, e) =>
            {
                ApplySplitters();          // qui la finestra ha già la dimensione finale (anche se massimizzata)
                splittersReady = true;
            };
            Load += (o, e) =>
            {
                ApplyCompact();
                if (!FFmpeg.Exists)
                {
                    MessageBox.Show(this, "ffmpeg.exe non trovato accanto a VHSCapture.exe.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (settings.Sources.Count == 0) AddSource(SourceType.Capture);
                else StartPreview();
                CheckEncoderAsync();
            };
            FormClosing += (o, e) =>
            {
                if (engine.IsRecording)
                {
                    if (MessageBox.Show(this, "Stai registrando. Fermare e uscire?", "VHSCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    { e.Cancel = true; return; }
                    engine.StopRecording();   // chiude bene il file prima di uscire
                }
                engine.Stop();
                monitor.Stop();
                settings.WindowMax = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal) { settings.WindowW = ClientSize.Width; settings.WindowH = ClientSize.Height; }
                settings.ShowLog = !splitLog.Panel2Collapsed;
                settings.RightPanelHidden = splitMain.Panel2Collapsed;
                if (splittersReady)
                {
                    if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
                    settings.MixerH = splitRight.Height - splitRight.SplitterDistance - splitRight.SplitterWidth;
                    if (!splitLog.Panel2Collapsed) settings.LogH = splitLog.Height - splitLog.SplitterDistance - splitLog.SplitterWidth;
                }
                settings.Save();
            };
        }

        // ---------------- UI ----------------

        void BuildUi()
        {
            const int gap = 10;

            // ---- barra superiore ----
            top = new Card { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12, 12, 12, 12), Margin = new Padding(0), Radius = 10 };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Tag = "panel" };
            // come OBS: un solo pulsante. Rosso = avvia, chiaro = ferma. L'anteprima parte da sola (F5 per riavviarla).
            btnRec = Ui.Btn("⏺   Avvia registrazione", "rec", (o, e) => ToggleRecording(), 230);
            tips.SetToolTip(btnRec, "Avvia / ferma registrazione (F9)");
            btnPause = Ui.Btn("⏸   Pausa", "ghost", (o, e) => TogglePause(), 120);
            btnPause.Visible = false;
            tips.SetToolTip(btnPause, "Pausa / riprendi (F10): salti un pezzo senza fare due file");
            btnProfile = Ui.Btn("📼   Profilo", "ghost", (o, e) => ShowProfileMenu());
            tips.SetToolTip(btnProfile, "Imposta in un clic sorgente e registrazione per VHS, Hi8, MiniDV, NTSC…");

            lblName = new Label { Text = "Nome file", AutoSize = true, Tag = "muted", Margin = new Padding(20, 10, 6, 0) };
            txtName = new TextBox { Width = 240, Margin = new Padding(0, 6, 0, 0), PlaceholderText = "es. Rossi_matrimonio_1994", Font = new Font("Segoe UI", 10f) };
            btnSettings = Ui.Btn("⚙   Impostazioni", "ghost", (o, e) => OpenSettings()); btnSettings.Margin = new Padding(20, 0, 8, 0);
            btnFolder = Ui.Btn("📁   Apri cartella", "ghost", (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Ui.IconBtn("◐", "Tema chiaro/scuro", (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); RefreshSourceList(); });
            btnLog = Ui.Btn("Log", "ghost", (o, e) => ToggleLog());
            btnPanels = Ui.IconBtn("◧", "Mostra/nascondi pannello Sorgenti e Mixer", (o, e) => ToggleRightPanel());
            flow.Controls.AddRange(new Control[] { btnRec, btnPause, btnProfile, lblName, txtName, btnSettings, btnFolder, btnTheme, btnPanels, btnLog });
            Resize += (o, e) => ApplyCompact();
            top.Controls.Add(flow);

            // ---- barra di stato ----
            status = new Card { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 6, 12, 6), Radius = 10 };
            lblRec = new Pill { Dock = DockStyle.Left };
            lblStatus = new Label { AutoSize = true, Dock = DockStyle.Right, Tag = "muted", Padding = new Padding(0, 8, 0, 0) };
            status.Controls.Add(lblStatus); status.Controls.Add(lblRec);

            // ---- canvas ----
            canvas = new CanvasView { Dock = DockStyle.Fill, CanvasW = settings.CanvasW, CanvasH = settings.CanvasH, Sources = settings.Sources, Message = "Premi ▶ Anteprima" };
            canvas.SelectionChanged += s2 => { SyncListSelection(s2); UpdateSourceButtons(); };
            canvas.TransformChanged += (s2, final) =>
            {
                engine.ApplyTransform(s2);
                if (final) { settings.Save(); if (!engine.LiveControl) RestartIfRunning(); }
            };
            canvas.OpenProperties += s2 => EditSource(s2);
            canvas.InputSizeOf = s2 => engine.GetInputSize(s2.Id);
            canvas.FrameShown += OnFrameShown;
            canvas.FrameSource = engine;
            canvas.RemoveRequested += s2 => RemoveSource(s2);
            canvas.LockChanged += s2 => { settings.Save(); srcList.Invalidate(); canvas.Select(null); };
            var canvasCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(8), Radius = 10 };
            canvasCard.Controls.Add(canvas);

            // ---- sorgenti ----
            cardSources = new Card { Dock = DockStyle.Fill, HeaderText = "SORGENTI", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            srcList = new SourceList { Dock = DockStyle.Fill, Sources = settings.Sources };
            srcList.SelectionChanged += sx => { if (syncingList) return; canvas.Select(sx); UpdateSourceButtons(); };
            srcList.OpenProperties += sx => EditSource(sx);
            srcList.VisibilityToggled += sx =>
            {
                if (engine.IsRecording) return;
                sx.Visible = !sx.Visible; settings.Save(); RefreshSourceList(); RebuildMixer(); RestartIfRunning(); canvas.Invalidate();
            };
            srcList.LockToggled += sx => { sx.Locked = !sx.Locked; settings.Save(); canvas.Invalidate(); srcList.Invalidate(); };
            srcList.ContextRequested += (sx, pt) => ShowSourceMenu(sx, srcList, pt);
            var srcBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Tag = "panel" };
            btnAdd = Ui.IconBtn("＋", "Aggiungi sorgente", (o, e) => ShowAddMenu());
            btnRemove = Ui.IconBtn("－", "Rimuovi sorgente", (o, e) => { if (canvas.Selected != null) RemoveSource(canvas.Selected); });
            btnProps = Ui.IconBtn("⚙", "Proprietà", (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); });
            btnUp = Ui.IconBtn("▲", "Porta sopra", (o, e) => MoveSource(+1));
            btnDown = Ui.IconBtn("▼", "Porta sotto", (o, e) => MoveSource(-1));
            srcBtns.Controls.AddRange(new Control[] { btnAdd, btnRemove, btnProps, btnUp, btnDown });
            cardSources.Controls.Add(srcList);
            cardSources.Controls.Add(srcBtns);

            // ---- mixer ----
            cardMixer = new Card { Dock = DockStyle.Fill, HeaderText = "MIXER AUDIO", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            mixer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "panel" };
            mixer.Resize += (o, e) => LayoutMixer();
            var monBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, WrapContents = false, Padding = new Padding(0, 6, 0, 0), Tag = "panel" };
            btnMonitor = Ui.Btn(settings.AudioMonitor ? "🎧  Ascolto attivo" : "🎧  Ascolta", settings.AudioMonitor ? "accent" : "ghost", (o, e) => ToggleMonitor());
            tips.SetToolTip(btnMonitor, "Monitoraggio audio: senti l'audio del grabber dalle casse (come OBS)");
            monBar.Controls.Add(btnMonitor);
            cardMixer.Controls.Add(mixer); cardMixer.Controls.Add(monBar);

            // ---- log ----
            cardLog = new Card { Dock = DockStyle.Fill, HeaderText = "LOG FFMPEG", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), WordWrap = false, BorderStyle = BorderStyle.None };
            cardLog.Controls.Add(txtLog);

            // ---- split: destra (sorgenti | mixer) ----
            splitRight = new SplitContainer { Size = new Size(400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 80, Panel2MinSize = 80, FixedPanel = FixedPanel.Panel2 };
            splitRight.Panel1.Controls.Add(cardSources);
            splitRight.Panel2.Controls.Add(cardMixer);

            // ---- split: canvas | destra ----
            splitMain = new SplitContainer { Size = new Size(1400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = gap, Panel1MinSize = 240, Panel2MinSize = 200, Panel2Collapsed = settings.RightPanelHidden, FixedPanel = FixedPanel.Panel2 };
            splitMain.Panel1.Controls.Add(canvasCard);
            splitMain.Panel2.Controls.Add(splitRight);

            // ---- split: sopra | log ----
            splitLog = new SplitContainer { Size = new Size(1400, 900), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 160, Panel2MinSize = 60, Panel2Collapsed = !settings.ShowLog, FixedPanel = FixedPanel.Panel2 };
            splitMain.SplitterMoved += (o, e) => SaveSplitters();
            splitRight.SplitterMoved += (o, e) => SaveSplitters();
            splitLog.SplitterMoved += (o, e) => SaveSplitters();
            splitLog.Panel1.Controls.Add(splitMain);
            splitLog.Panel2.Controls.Add(cardLog);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(gap, gap, gap, gap) };
            body.Controls.Add(splitLog);

            var topWrap = new Panel { Dock = DockStyle.Top, Height = 62 + gap, Padding = new Padding(gap, gap, gap, 0) };
            topWrap.Controls.Add(top);
            var statusWrap = new Panel { Dock = DockStyle.Bottom, Height = 44 + gap, Padding = new Padding(gap, 0, gap, gap) };
            statusWrap.Controls.Add(status);

            Controls.Add(body);
            Controls.Add(statusWrap);
            Controls.Add(topWrap);
        }

        bool splittersReady;

        /// <summary>Salva le misure dei pannelli appena rilasci lo splitter (non solo alla chiusura).</summary>
        void SaveSplitters()
        {
            if (!splittersReady) return;
            if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
            settings.MixerH = splitRight.Height - splitRight.SplitterDistance - splitRight.SplitterWidth;
            if (!splitLog.Panel2Collapsed) settings.LogH = splitLog.Height - splitLog.SplitterDistance - splitLog.SplitterWidth;
            settings.Save();
        }

        void ApplySplitters()
        {
            bool was = splittersReady; splittersReady = false;   // le mie impostazioni non devono scatenare il salvataggio
            try
            {
                if (!splitMain.Panel2Collapsed) splitMain.SplitterDistance = Math.Max(splitMain.Panel1MinSize, splitMain.Width - Math.Max(200, settings.RightPanelW) - splitMain.SplitterWidth);
                splitRight.SplitterDistance = Math.Max(splitRight.Panel1MinSize, splitRight.Height - Math.Max(80, settings.MixerH) - splitRight.SplitterWidth);
                if (!splitLog.Panel2Collapsed) splitLog.SplitterDistance = Math.Max(splitLog.Panel1MinSize, splitLog.Height - Math.Max(60, settings.LogH) - splitLog.SplitterWidth);
            }
            catch { }
            splittersReady = was;
        }

        void ToggleLog()
        {
            splitLog.Panel2Collapsed = !splitLog.Panel2Collapsed;
            if (!splitLog.Panel2Collapsed) ApplySplitters();
        }

        // ---------------- sorgenti ----------------

        void RefreshSourceList()
        {
            srcList.Sources = settings.Sources;
            srcList.Select(canvas?.Selected);
            srcList.ReadOnlyStructure = engine.IsRecording;
            srcList.Invalidate();
            UpdateSourceButtons();
        }

        void SyncListSelection(Source s)
        {
            syncingList = true;
            srcList.Select(s);
            syncingList = false;
        }

        void ShowSourceMenu(Source s, Control owner, Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Proprietà…", null, (o, e) => EditSource(s));
            m.Items.Add("Rinomina…", null, (o, e) =>
            {
                string n = Prompt("Rinomina sorgente", "Nome", s.Name);
                if (!string.IsNullOrWhiteSpace(n)) { s.Name = n.Trim(); settings.Save(); RefreshSourceList(); RebuildMixer(); canvas.Invalidate(); }
            });
            m.Items.Add(new ToolStripSeparator());
            var vis = m.Items.Add(s.Visible ? "Nascondi" : "Mostra", null, (o, e) => { if (engine.IsRecording) return; s.Visible = !s.Visible; settings.Save(); RefreshSourceList(); RebuildMixer(); RestartIfRunning(); canvas.Invalidate(); });
            vis.Enabled = !engine.IsRecording;
            m.Items.Add(s.Locked ? "Sblocca" : "Blocca", null, (o, e) => { s.Locked = !s.Locked; settings.Save(); srcList.Invalidate(); canvas.Invalidate(); });
            m.Items.Add(new ToolStripSeparator());
            var up = m.Items.Add("Porta sopra", null, (o, e) => { canvas.Select(s); MoveSource(+1); });
            var dn = m.Items.Add("Porta sotto", null, (o, e) => { canvas.Select(s); MoveSource(-1); });
            up.Enabled = dn.Enabled = !engine.IsRecording;
            m.Items.Add(new ToolStripSeparator());
            var rm = m.Items.Add("Rimuovi", null, (o, e) => RemoveSource(s));
            rm.Enabled = !engine.IsRecording;
            Theme.StyleMenu(m);
            m.Show(owner, at);
        }

        string Prompt(string title, string label, string value)
        {
            using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(480, 150), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true, TopMost = true, Font = Font };
            var l = new Label { Text = label, Left = 14, Top = 14, Width = 452, Height = 36 };
            var t = new TextBox { Left = 14, Top = 56, Width = 452, Text = value, Font = new Font("Segoe UI", 11f), BorderStyle = BorderStyle.FixedSingle };
            var ok = Ui.Btn("OK", "accent", null, 100); ok.Left = 258; ok.Top = 100; ok.DialogResult = DialogResult.OK;
            var ca = Ui.Btn("Annulla", "normal", null, 100); ca.Left = 366; ca.Top = 100; ca.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { l, t, ok, ca });
            f.AcceptButton = ok; f.CancelButton = ca;
            Theme.Apply(f, settings.DarkTheme);
            return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
        }

        void ToggleRightPanel()
        {
            if (!splitMain.Panel2Collapsed && splittersReady) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
            splitMain.Panel2Collapsed = !splitMain.Panel2Collapsed;
            if (!splitMain.Panel2Collapsed) ApplySplitters();
            settings.RightPanelHidden = splitMain.Panel2Collapsed;
            settings.Save();
        }

        bool? compactState;
        readonly ToolTip tips = new ToolTip { InitialDelay = 600 };
        /// <summary>Finestra stretta: pulsanti solo icona, così la barra ci sta anche su schermi piccoli o a metà schermo.</summary>
        void ApplyCompact()
        {
            if (btnRec == null) return;
            bool compact = ClientSize.Width < 1180;
            if (compactState == compact) return;
            compactState = compact;
            UpdateRecButton();
            btnSettings.Text = compact ? "⚙" : "⚙   Impostazioni";
            btnFolder.Text = compact ? "📁" : "📁   Apri cartella";
            lblName.Visible = !compact;
            txtName.Width = compact ? 150 : 240;
            btnSettings.Margin = new Padding(compact ? 8 : 20, 0, 8, 0);
            tips.SetToolTip(btnSettings, compact ? "Impostazioni" : null);
            tips.SetToolTip(btnFolder, compact ? "Apri cartella" : null);
            top.PerformLayout();
        }

        void UpdateSourceButtons()
        {
            bool sel = canvas.Selected != null, rec = engine.IsRecording;
            btnRemove.Enabled = sel && !rec; btnProps.Enabled = sel;
            int i = sel ? settings.Sources.IndexOf(canvas.Selected) : -1;
            btnUp.Enabled = sel && !rec && i < settings.Sources.Count - 1;
            btnDown.Enabled = sel && !rec && i > 0;
            btnAdd.Enabled = !rec;
            if (srcList != null) srcList.ReadOnlyStructure = rec;
        }

        void ShowAddMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Dispositivo di cattura video (grabber USB)", null, (o, e) => AddSource(SourceType.Capture));
            m.Items.Add("Immagine (logo, sfondo…)", null, (o, e) => AddSource(SourceType.Image));
            m.Items.Add("Colore pieno", null, (o, e) => AddSource(SourceType.Color));
            Theme.StyleMenu(m);
            m.Show(btnAdd, new Point(0, btnAdd.Height));
        }

        void AddSource(SourceType type)
        {
            if (engine.IsRecording) return;
            var s = new Source { Type = type };
            s.Name = UniqueName(type switch { SourceType.Capture => "Grabber USB", SourceType.Image => "Immagine", _ => "Colore" });
            if (type == SourceType.Capture)
            {
                var (video, audio) = FFmpeg.ListDevices();
                // proponi il primo dispositivo non ancora usato
                s.VideoDevice = video.FirstOrDefault(v => !settings.Sources.Any(x => x.VideoDevice == v)) ?? video.FirstOrDefault() ?? "";
                s.AudioDevice = "";
                s.DeinterlaceMode = s.IsSD ? "yadif2x" : "off";
                s.FitTo(settings.CanvasW, settings.CanvasH);
            }
            else if (type == SourceType.Color) s.FillTo(settings.CanvasW, settings.CanvasH);
            else { s.W = 400; s.H = 300; s.Center(settings.CanvasW, settings.CanvasH); }

            // come OBS: la sorgente entra subito in scena, così la configuri vedendo l'anteprima
            bool canPreview = type != SourceType.Image;
            if (canPreview)
            {
                settings.Sources.Add(s);
                RefreshSourceList(); canvas.Select(s);
                StartPreview();
            }

            using var f = new SourceForm(s, settings, false, LiveApply, StructuralApply, CropPreview, WithDeviceFree, AppendLog, id => engine.GetInputInfo(id));
            var r = f.ShowDialog(this);
            if (r != DialogResult.OK)
            {
                if (canPreview) { settings.Sources.Remove(s); canvas.Select(null); RefreshSourceList(); RebuildMixer(); StartPreview(); }
                return;
            }
            var res = f.Result;
            if (canPreview)
            {
                s.CopyAllFrom(res);
            }
            else
            {
                settings.Sources.Add(res); s = res;
            }
            settings.Save();
            RefreshSourceList(); RebuildMixer();
            canvas.Select(s);
            StartPreview();
        }

        string UniqueName(string baseName)
        {
            if (!settings.Sources.Any(x => x.Name == baseName)) return baseName;
            for (int i = 2; ; i++) if (!settings.Sources.Any(x => x.Name == $"{baseName} {i}")) return $"{baseName} {i}";
        }

        void EditSource(Source s)
        {
            using var f = new SourceForm(s, settings, engine.IsRecording, LiveApply, StructuralApply, CropPreview, WithDeviceFree, AppendLog, id => engine.GetInputInfo(id));
            if (f.ShowDialog(this) != DialogResult.OK) { canvas.Invalidate(); RefreshMixerValues(); return; }
            var res = f.Result;
            bool structural = !res.StructurallyEquals(s);
            s.CopyAllFrom(res);
            settings.Save();
            RefreshSourceList(); RebuildMixer();
            canvas.Select(s);
            if (structural) RestartIfRunning();
            else { engine.ApplyTransform(s); engine.ApplyColor(s); engine.ApplyVolume(s); if (!engine.LiveControl) RestartIfRunning(); }
        }

        /// <summary>Dal dialogo: dispositivo/risoluzione/fps/deinterlaccio cambiati → aggiorna la sorgente in scena e riavvia l'anteprima.</summary>
        void StructuralApply(Source s)
        {
            var target = settings.Sources.FirstOrDefault(x => x.Id == s.Id);
            if (target == null || engine.IsRecording) return;
            if (target.StructurallyEquals(s)) return;
            target.CopyStructuralFrom(s);
            RebuildMixer();
            StartPreview();
        }

        /// <summary>Ritaglia dall'anteprima corrente la zona occupata dalla sorgente (per l'anteprima nel dialogo proprietà).</summary>
        Bitmap CropPreview(string id)
        {
            var src = settings.Sources.FirstOrDefault(x => x.Id == id);
            if (src == null || !src.Visible || !engine.IsRunning) return null;
            return canvas.WithFrame(fb => CropFrom(fb, src));
        }

        Bitmap CropFrom(FrameBuf fb, Source src)
        {
            if (fb == null) return null;
            if ((DateTime.Now - lastFrameAt).TotalSeconds > 2) return null;
            double sx = (double)fb.W / settings.CanvasW, sy = (double)fb.H / settings.CanvasH;
            var r = Rectangle.Intersect(new Rectangle((int)(src.X * sx), (int)(src.Y * sy), (int)Math.Ceiling(src.W * sx), (int)Math.Ceiling(src.H * sy)),
                                        new Rectangle(0, 0, fb.W, fb.H));
            if (r.Width < 4 || r.Height < 4) return null;
            try
            {
                var bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                for (int y = 0; y < r.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(fb.Data, ((r.Y + y) * fb.W + r.X) * 4, bd.Scan0 + y * bd.Stride, r.Width * 4);
                bmp.UnlockBits(bd);
                return bmp;
            }
            catch { return null; }
        }

        void RemoveSource(Source s)
        {
            if (engine.IsRecording) return;
            if (MessageBox.Show(this, $"Rimuovere \"{s.Name}\"?", "VHSCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            settings.Sources.Remove(s);
            settings.Save();
            canvas.Select(null);
            RefreshSourceList(); RebuildMixer();
            RestartIfRunning();
        }

        void MoveSource(int dir)
        {
            var s = canvas.Selected; if (s == null || engine.IsRecording) return;
            int i = settings.Sources.IndexOf(s), j = i + dir;
            if (j < 0 || j >= settings.Sources.Count) return;
            settings.Sources[i] = settings.Sources[j]; settings.Sources[j] = s;
            settings.Save();
            RefreshSourceList();
            RestartIfRunning();
        }

        /// <summary>Applica al volo (dal dialogo proprietà) trasformazione/colore/volume.</summary>
        void LiveApply(Source s)
        {
            var target = settings.Sources.FirstOrDefault(x => x.Id == s.Id);
            if (target != null && !ReferenceEquals(target, s)) target.CopyLiveFrom(s);
            var t = target ?? s;
            if (target != null && engine.IsRunning)
            {
                engine.ApplyTransform(t); engine.ApplyColor(t); engine.ApplyVolume(t);
                if (!engine.LiveControl) ScheduleRestart();
            }
            canvas.Invalidate();
            RefreshMixerValues();
        }

        // ---------------- mixer ----------------

        void RebuildMixer()
        {
            mixer.SuspendLayout();
            mixer.Controls.Clear();
            mixerRows.Clear();
            foreach (var sx in settings.Sources.Where(x => x.HasAudio))
            {
                var row = new MixerRow(sx);
                row.VolumeChanged += q => engine.ApplyVolume(q);
                row.VolumeCommitted += q => { settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                row.MuteChanged += q => { engine.ApplyVolume(q); settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                if (!sx.Visible) row.Enabled = false;
                mixer.Controls.Add(row);
                mixerRows[sx.Id] = row;
            }
            if (mixerRows.Count == 0)
                mixer.Controls.Add(new Label { Text = "Nessuna sorgente audio.\nNelle proprietà del grabber scegli il dispositivo audio.", Left = 0, Top = 4, AutoSize = true, Tag = "muted" });
            mixer.ResumeLayout();
            LayoutMixer();
            Theme.Apply(this, settings.DarkTheme);
        }

        void LayoutMixer()
        {
            int y = 0, w = Math.Max(160, mixer.ClientSize.Width - 2);
            foreach (var row in mixerRows.Values) { row.SetBounds(0, y, w, row.Height); y += row.Height + 6; }
        }

        void ResetMeters() { foreach (var r in mixerRows.Values) r.Meter.Reset(); }

        void RefreshMixerValues()
        {
            foreach (var row in mixerRows.Values) row.SyncFromSource();
        }

        // ---------------- motore ----------------

        void StartPreview()
        {
            if (!FFmpeg.Exists || engine.IsRecording) return;
            restartTimer.Stop();
            frames = 0; ResetMeters();
            lock (runLog) runLog.Clear();
            canvas.Message = settings.Sources.Any(x => x.Visible) ? "Avvio anteprima…" : "Nessuna sorgente: premi ＋ per aggiungere il grabber";
            canvas.SetFrame(null);
            try { engine.Start(settings, PreviewWidth(), settings.AudioMonitor); StartMonitorIfNeeded(); }
            catch (Exception ex) { AppendLog("Errore avvio: " + ex.Message); }
            SetButtons();
        }

        /// <summary>Ferma l'anteprima, esegue l'azione (es. dialogo del driver via ffmpeg), riavvia l'anteprima.</summary>
        void WithDeviceFree(Action a)
        {
            if (engine.IsRecording) return;
            bool was = engine.IsRunning;
            engine.Stop(); canvas.SetFrame(null); canvas.Message = "Dispositivo in uso dal dialogo del driver…"; canvas.Refresh();
            try { a(); } finally { if (was) StartPreview(); }
        }

        void RestartIfRunning() { if (engine.IsRecording) return; if (engine.IsRunning) StartPreview(); }
        void ScheduleRestart() { if (engine.IsRecording) return; restartTimer.Stop(); restartTimer.Start(); }

        void StartRecording()
        {
            if (!FFmpeg.Exists) return;
            if (engine.IsRecording || finalizing) return;
            if (!settings.Sources.Any(x => x.Visible)) { MessageBox.Show(this, "Aggiungi almeno una sorgente.", "VHSCapture"); return; }

            string folder = settings.ResolvedOutputFolder();
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "Cartella di destinazione non raggiungibile:\n" + folder, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string name = txtName.Text.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string baseName = string.IsNullOrEmpty(name) ? $"{settings.FilePrefix}_{stamp}" : $"{settings.FilePrefix}_{name}_{stamp}";
            recFolder = folder; recBase = baseName;
            finalFile = Path.Combine(folder, baseName + ".mp4");
            if (settings.SplitMinutes > 0 && !settings.SafeRecording)
                recFile = finalFile = Path.Combine(folder, baseName + "_%03d.mp4");   // divisione automatica: _000, _001, …
            else
                recFile = settings.SafeRecording ? Path.Combine(folder, baseName + ".mkv") : finalFile;

            restartTimer.Stop();
            // come OBS: la pipeline resta accesa, si attacca solo il muxer. Nessuno scatto, nessun frame perso.
            if (!engine.IsRunning) StartPreview();
            try { engine.StartRecording(settings, recFile); }
            catch (Exception ex) { AppendLog("Errore avvio registrazione: " + ex.Message); return; }
            recStart = DateTime.Now;
            blankSince = null; blankStartRecSec = -1; contentSamples = 0; autoStopped = false;
            pausedSince = null; pausedTotal = TimeSpan.Zero;
            AppendLog("Registrazione avviata: " + Path.GetFileName(recFile));
            SetButtons();
        }

        async void StopRecording(bool restartPreview)
        {
            if (!engine.IsRecording) return;
            finalizing = true;
            SetButtons();
            lblRec.Text = "Chiusura file…"; lblRec.Fill = Color.Transparent; lblRec.ForeColor = Theme.Fore;
            canvas.RecText = null; canvas.Invalidate();

            string written = recFile, final = finalFile;
            bool muxOk = await Task.Run(() => engine.StopRecording());
            if (!muxOk) AppendLog("Il muxer non è uscito pulito: controlla il file");

            bool HasData(string f) => RecordedBytes(f) > 4096;

            if (!HasData(written))
            {
                try { foreach (var fx in RecordedFiles(written)) File.Delete(fx); } catch { }
                AppendLog("Registrazione NON salvata: ffmpeg non ha scritto niente (vedi errori sopra)");
                MessageBox.Show(this, "La registrazione non è partita e non è stato salvato nulla.\nControlla il Log.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (settings.SafeRecording && !string.Equals(written, final, StringComparison.OrdinalIgnoreCase))
            {
                lblRec.Text = "Conversione in MP4 (senza ricodifica)…";
                bool ok = await Task.Run(() => FFmpeg.RemuxToMp4(written, final, AppendLog));
                if (ok) { try { File.Delete(written); } catch { } AppendLog("Salvato: " + final); }
                else
                {
                    AppendLog("Remux fallito: rimane il file MKV " + written);
                    MessageBox.Show(this, "Conversione MP4 fallita, il video è comunque salvo in:\n" + written, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (final.Contains("%03d")) AppendLog("Salvato in più parti: " + string.Join(", ", RecordedFiles(final).Select(Path.GetFileName)));
            else AppendLog("Salvato: " + final);

            // coda blu/nera: se lo stop è automatico la taglio (senza ricodifica)
            string mainFile = RecordedFiles(final).LastOrDefault();
            if (autoStopped && settings.TrimBlankTail && blankStartRecSec > 0 && mainFile != null && !final.Contains("%03d"))
            {
                lblRec.Text = "Taglio la coda blu…";
                // il file parte dal keyframe precedente al clic: margine = intervallo keyframe + 1 s
                double cut = blankStartRecSec + Math.Max(1, settings.KeyframeSec) + 1;
                bool ok = await Task.Run(() => FFmpeg.TrimFile(mainFile, cut, AppendLog));
                AppendLog(ok ? $"Coda blu tagliata: file lungo {TimeSpan.FromSeconds(cut):hh\\:mm\\:ss}" : "Coda blu non tagliata (il file è comunque salvo)");
            }

            // nome della cassetta: rinomina il file (niente più rinomina a mano in Esplora file)
            if (settings.AskNameAtEnd && RecordedFiles(final).Any())
            {
                string suggested = txtName.Text.Trim();
                string n = Prompt("Nome della cassetta", "Come si chiama questa cassetta? (Invio per confermare, Annulla per lasciare il nome automatico)", suggested);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    var renamed = RenameRecording(final, n.Trim());
                    if (renamed != null) { final = renamed; written = renamed; AppendLog("Rinominato: " + Path.GetFileName(renamed.Replace("%03d", "000"))); }
                }
                txtName.Text = "";
            }

            // controllo automatico dell'audio nel file: così non si resta col dubbio
            var toCheck = RecordedFiles(final).FirstOrDefault() ?? RecordedFiles(written).FirstOrDefault();
            if (toCheck != null && settings.Sources.Any(x => x.Visible && x.HasAudio))
            {
                lblRec.Text = "Controllo audio del file…";
                var (has, mean, max) = await Task.Run(() => FFmpeg.CheckAudio(toCheck));
                if (!has) AppendLog("⚠ ATTENZIONE: il file NON contiene la traccia audio");
                else if (max <= -60) AppendLog($"⚠ ATTENZIONE: l'audio nel file è SILENZIO (picco {max:0.0} dB) — controlla il dispositivo audio della sorgente");
                else AppendLog($"Audio nel file OK: medio {mean:0.0} dB, picco {max:0.0} dB" + (max >= -0.5 ? " — satura, abbassa il volume nel mixer" : ""));
            }

            finalizing = false;
            pausedSince = null; pausedTotal = TimeSpan.Zero;
            lblRec.Text = "";
            if (restartPreview && !IsDisposed && !engine.IsRunning) StartPreview();
            SetButtons();
        }

        void OpenSettings()
        {
            if (engine.IsRecording) { MessageBox.Show(this, "Ferma la registrazione prima di cambiare le impostazioni.", "VHSCapture"); return; }
            using var f = new SettingsForm(settings);
            var r = f.ShowDialog(this);
            Theme.Apply(this, settings.DarkTheme);
            if (r != DialogResult.OK) return;
            RefreshSourceList(); RebuildMixer();
            canvas.CanvasW = settings.CanvasW; canvas.CanvasH = settings.CanvasH;
            canvas.Invalidate();
            StartPreview();
        }

        // ===================== profili (come i profili di OBS) =====================
        record Prof(string Name, string Size, string InFps, string CanvasFps, string Deint, int CropB, bool Wide, string Format, string Note);

        static readonly Prof[] Profiles =
        {
            new Prof("VHS / S-VHS PAL", "720x576", "25", "50", "yadif2x", 8, false, "auto", null),
            new Prof("Hi8 / Video8 / Digital8 PAL", "720x576", "25", "50", "yadif2x", 8, false, "auto", null),
            new Prof("MiniDV PAL 4:3", "720x576", "25", "50", "yadif2x", 0, false, "auto", null),
            new Prof("MiniDV PAL 16:9", "720x576", "25", "50", "yadif2x", 0, true, "auto", null),
            null,
            new Prof("VHS / Hi8 NTSC", "720x480", "29.97", "59.94", "yadif2x", 6, false, "auto", "NTSC"),
            new Prof("MiniDV NTSC 4:3", "720x480", "29.97", "59.94", "yadif2x", 0, false, "auto", "NTSC"),
            new Prof("MiniDV NTSC 16:9", "720x480", "29.97", "59.94", "yadif2x", 0, true, "auto", "NTSC"),
            null,
            new Prof("Camera HDMI 1080p60", "1920x1080", "60", "60", "off", 0, true, "mjpeg", null),
        };

        void ShowProfileMenu()
        {
            if (engine.IsRecording) return;
            var m = new ContextMenuStrip();
            foreach (var p in Profiles)
            {
                if (p == null) { m.Items.Add(new ToolStripSeparator()); continue; }
                var it = new ToolStripMenuItem(p.Name) { Checked = settings.Profile == p.Name };
                var pp = p;
                it.Click += (o, e) => ApplyProfile(pp);
                m.Items.Add(it);
            }
            Theme.StyleMenu(m);
            m.Show(btnProfile, new Point(0, btnProfile.Height));
        }

        /// <summary>Imposta in un colpo sorgente (ingresso, deinterlaccio, ritaglio, proporzioni) e registrazione (canvas, fps).</summary>
        void ApplyProfile(Prof p)
        {
            var src = settings.Sources.FirstOrDefault(x => x.Type == SourceType.Capture);
            if (src == null) { MessageBox.Show(this, "Aggiungi prima il grabber come sorgente (＋).", "VHSCapture"); return; }
            src.InputSize = p.Size; src.InputFps = p.InFps; src.VideoFormat = p.Format;
            src.DeinterlaceMode = p.Deint;
            src.CropL = src.CropT = src.CropR = 0; src.CropB = p.CropB;
            settings.CanvasW = 1920; settings.CanvasH = 1080; settings.Fps = p.CanvasFps;
            if (p.Wide) src.FillTo(settings.CanvasW, settings.CanvasH); else src.FitTo(settings.CanvasW, settings.CanvasH);
            settings.Profile = p.Name;
            settings.Save();
            canvas.CanvasW = settings.CanvasW; canvas.CanvasH = settings.CanvasH;
            RefreshSourceList(); UpdateRecButton(); canvas.Invalidate();
            AppendLog($"Profilo \"{p.Name}\": ingresso {p.Size} @ {p.InFps}, {(p.Deint == "off" ? "senza deinterlaccio" : "Yadif 2x")}, canvas 1920×1080 @ {p.CanvasFps}, {(p.Wide ? "16:9 a tutto schermo" : "4:3 con bande laterali")}");
            if (p.Note == "NTSC")
                MessageBox.Show(this, "Profilo NTSC impostato.\n\nRicorda: il grabber va messo su NTSC_M (Proprietà sorgente → Driver video… → Standard video) e il lettore deve riprodurre davvero l'NTSC, altrimenti l'immagine esce in bianco e nero o scorre.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Information);
            StartPreview();
        }

        // ===================== dispositivi rinominati da Windows =====================
        /// <summary>
        /// Dopo un aggiornamento Windows reinstalla spesso i driver USB e rinomina il dispositivo
        /// (es. "USB Audio" → "2- USB Audio", "Microfono (USB Audio)" → "Microfono (3- USB Audio)").
        /// Qui lo ritrovo lo stesso confrontando il nome "pulito", e aggiorno le impostazioni.
        /// </summary>
        bool TryResolveDevices()
        {
            var (videos, audios) = FFmpeg.ListDevices();
            bool changed = false;
            foreach (var src in settings.Sources.Where(x => x.Type == SourceType.Capture))
            {
                var v = Resolve(src.VideoDevice, videos);
                if (v != null && v != src.VideoDevice) { AppendLog($"Windows ha rinominato il video \"{src.VideoDevice}\" in \"{v}\": ritrovato e aggiornato"); src.VideoDevice = v; changed = true; }
                else if (v == null && !string.IsNullOrEmpty(src.VideoDevice) && !videos.Contains(src.VideoDevice))
                    AppendLog($"⚠ Dispositivo video \"{src.VideoDevice}\" non trovato: è collegato? Scegline un altro nelle Proprietà della sorgente");
                if (!string.IsNullOrEmpty(src.AudioDevice))
                {
                    var a = Resolve(src.AudioDevice, audios);
                    if (a != null && a != src.AudioDevice) { AppendLog($"Windows ha rinominato l'audio \"{src.AudioDevice}\" in \"{a}\": ritrovato e aggiornato"); src.AudioDevice = a; changed = true; }
                    else if (a == null && !audios.Contains(src.AudioDevice))
                        AppendLog($"⚠ Dispositivo audio \"{src.AudioDevice}\" non trovato: è collegato? Scegline un altro nelle Proprietà della sorgente");
                }
            }
            if (changed) { settings.Save(); RefreshSourceList(); RebuildMixer(); }
            return changed;
        }

        static string NormDevice(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            n = System.Text.RegularExpressions.Regex.Replace(n, @"^\s*\d+\s*-\s*", "");          // "2- USB Audio"
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\(\s*\d+\s*-\s*", "(");         // "Microfono (2- USB Audio)"
            return n.Trim().ToLowerInvariant();
        }

        /// <summary>Il nome esatto se c'è; altrimenti l'unico dispositivo con lo stesso nome "pulito"; altrimenti null.</summary>
        static string Resolve(string saved, List<string> available)
        {
            if (string.IsNullOrEmpty(saved) || available.Contains(saved)) return saved;
            string ns = NormDevice(saved);
            var same = available.Where(d => NormDevice(d) == ns).ToList();
            if (same.Count >= 1) return same[0];
            var close = available.Where(d => NormDevice(d).Contains(ns) || ns.Contains(NormDevice(d))).ToList();
            return close.Count == 1 ? close[0] : null;
        }

        void ToggleRecording()
        {
            if (finalizing) return;
            if (engine.IsRecording) StopRecording(true);
            else StartRecording();
        }

        /// <summary>Pulsante unico: rosso "Avvia registrazione" / chiaro "Ferma registrazione" (colori diversi, come chiesto).</summary>
        void UpdateRecButton()
        {
            if (btnRec == null) return;
            bool compact = compactState == true, rec = engine.IsRecording;
            if (finalizing)
            {
                btnRec.Text = compact ? "…" : "Chiusura file…";
                btnRec.Variant = "stop"; btnRec.Enabled = false;
            }
            else if (rec)
            {
                btnRec.Text = compact ? "⏹  STOP" : "⏹   Ferma registrazione";
                btnRec.Variant = "stop"; btnRec.Enabled = true;
            }
            else
            {
                btnRec.Text = compact ? "⏺  REC" : "⏺   Avvia registrazione";
                btnRec.Variant = "rec"; btnRec.Enabled = true;
            }
            btnRec.MinimumSize = new Size(compact ? 96 : 230, 36);
            btnRec.Invalidate();
            if (btnPause != null)
            {
                bool p = pausedSince != null;
                btnPause.Visible = rec && !finalizing;
                btnPause.Text = compact ? (p ? "▶" : "⏸") : (p ? "▶   Riprendi" : "⏸   Pausa");
                btnPause.Variant = p ? "accent" : "ghost";
                btnPause.MinimumSize = new Size(compact ? 44 : 120, 36);
                btnPause.Invalidate();
            }
            if (btnProfile != null)
            {
                btnProfile.Text = compact ? "📼" : "📼   " + (string.IsNullOrEmpty(settings.Profile) ? "Profilo" : settings.Profile);
                btnProfile.Enabled = !rec && !finalizing;
            }
        }

        /// <summary>Tempo registrato davvero (senza le pause): coincide con la durata del file.</summary>
        TimeSpan RecElapsed()
        {
            var t = DateTime.Now - recStart - pausedTotal;
            if (pausedSince != null) t -= DateTime.Now - pausedSince.Value;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }

        void TogglePause()
        {
            if (!engine.IsRecording || finalizing) return;
            if (pausedSince == null)
            {
                engine.PauseRecording();
                pausedSince = DateTime.Now;
                AppendLog("Registrazione in pausa");
            }
            else
            {
                engine.ResumeRecording();
                pausedTotal += DateTime.Now - pausedSince.Value;
                pausedSince = null;
                blankSince = null;
                AppendLog($"Registrazione ripresa (riparte dall'ultimo keyframe, al massimo {settings.KeyframeSec} s prima)");
            }
            UpdateRecButton();
        }

        void SetButtons()
        {
            bool rec = engine.IsRecording;
            UpdateRecButton();
            btnSettings.Enabled = !rec && !finalizing;
            txtName.Enabled = !rec;
            UpdateSourceButtons();
        }

        // ---------------- eventi engine ----------------

        /// <summary>Dal thread di rendering: frame mostrato → restituito al motore + statistiche.</summary>
        void OnFrameShown()
        {
            System.Threading.Interlocked.Increment(ref frames);
            lastFrameAt = DateTime.Now; autoRetried = false; devicesResolved = false;
            double t = paintClock.Elapsed.TotalSeconds;
            if (lastPaint > 0) paintMaxGap = Math.Max(paintMaxGap, t - lastPaint);
            lastPaint = t; System.Threading.Interlocked.Increment(ref painted);
        }

        void OnEngineExited(int code)
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (code != 0 && code != 255 && !finalizing) AppendLog($"ffmpeg terminato con codice {code}");
                if (!engine.IsRunning && !finalizing)
                {
                    if (engine.IsRecording)
                    {
                        AppendLog("ATTENZIONE: la pipeline si è fermata durante la registrazione — chiudo il file");
                        StopRecording(true);
                        return;
                    }
                    // l'encoder ora è sempre acceso: se non si apre, passo a x264 e riparto
                    string lg0; lock (runLog) lg0 = runLog.ToString();
                    // paracadute VU: se la pipe dei livelli non si apre, riparto senza misuratori (anteprima e registrazione prima di tutto)
                    if (lg0.Contains("Could not find") && lg0.Contains("device with name") && !devicesResolved)
                    {
                        devicesResolved = true;
                        if (TryResolveDevices()) { StartPreview(); return; }
                    }
                    if (lg0.Contains("Could not bind ZMQ") && !autoRetried)
                    {
                        AppendLog("Porta del controllo live occupata: riparto");
                        autoRetried = true;
                        restartTimer.Stop(); restartTimer.Start();
                        return;
                    }
                    if (lg0.Contains("Could not open") && (lg0.Contains("vhscap_an_") || lg0.Contains("vhscap_fr_")) && !engine.AnalysisDisabled)
                    {
                        AppendLog("Rilevamento fine cassetta non disponibile su questo PC: riparto senza");
                        engine.AnalysisDisabled = true;
                        StartPreview();
                        return;
                    }
                    if (lg0.Contains("Could not open") && lg0.Contains("vhscap_me_") && !engine.MetersDisabled)
                    {
                        AppendLog("Misuratori audio non disponibili su questo PC: riparto senza VU");
                        engine.MetersDisabled = true;
                        StartPreview();
                        return;
                    }
                    // solo un vero errore di apertura dell'encoder (non gli errori a cascata dopo un filtro fallito)
                    bool graphFail = lg0.Contains("Error initializing filters") || lg0.Contains("Error reinitializing filters");
                    bool encFail = !graphFail && (lg0.Contains("Error while opening encoder") || lg0.Contains("Error creating a MFX session") || lg0.Contains("Could not open encoder") && lg0.Contains("[enc:h264_"));
                    if (encFail && settings.Encoder != "libx264" && !autoRetried)
                    {
                        AppendLog($"L'encoder {settings.Encoder} non si apre su questo PC: passo a x264 software");
                        settings.Encoder = "libx264"; settings.Save(); autoRetried = true;
                        StartPreview();
                        return;
                    }
                    if (frames == 0 && code != 0 && !autoRetried && TryAutoFallback()) return;
                    if (frames == 0 && code != 0) { canvas.Message = "ffmpeg non è partito — vedi il Log qui sotto (F5 per riprovare)"; if (splitLog.Panel2Collapsed) ToggleLog(); }
                    else canvas.Message = "Anteprima ferma — premi F5 per riavviarla";
                    canvas.SetFrame(null);
                    ResetMeters();
                    SetButtons();
                }
            }));
        }

        /// <summary>Se il dispositivo ha rifiutato risoluzione/fps, riprova una volta lasciando decidere al driver.</summary>
        bool TryAutoFallback()
        {
            string log; lock (runLog) log = runLog.ToString();
            if (!log.Contains("Could not set video options")) return false;
            bool changed = false;
            foreach (var src in settings.Sources.Where(x => x.Visible && x.Type == SourceType.Capture))
            {
                if (src.InputSize != "auto" || src.InputFps != "auto" || src.VideoFormat != "auto")
                {
                    string fmt = src.VideoFormat == "auto" ? "" : " in " + src.VideoFormat.ToUpperInvariant();
                    AppendLog($"\"{src.VideoDevice}\" non supporta {src.InputSize} @ {src.InputFps}{fmt}: passo a formato, risoluzione e fps automatici");
                    src.InputSize = "auto"; src.InputFps = "auto"; src.VideoFormat = "auto"; changed = true;
                }
            }
            if (!changed) return false;
            autoRetried = true;
            settings.Save();
            StartPreview();
            return true;
        }

        async void CheckEncoderAsync()
        {
            var list = await Task.Run(() => FFmpeg.ListWorkingH264Encoders());
            AppendLog("Encoder funzionanti su questo PC: " + string.Join(", ", list));
            if (!list.Contains(settings.Encoder))
            {
                AppendLog($"L'encoder {settings.Encoder} non funziona su questo PC: passo a {list[0]}");
                settings.Encoder = list[0];
                settings.Save();
            }
        }

        /// <summary>Rinomina la registrazione in "Nome cassetta.mp4" (o Nome_000.mp4… se divisa). Mai sovrascrivere: aggiunge (2), (3)…</summary>
        string RenameRecording(string pattern, string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var files = RecordedFiles(pattern).ToList();
            if (files.Count == 0) return null;
            string dir = Path.GetDirectoryName(files[0]), ext = Path.GetExtension(files[0]);
            string baseName = name;
            for (int n = 2; ; n++)
            {
                bool clash = files.Count == 1 ? File.Exists(Path.Combine(dir, baseName + ext))
                                              : Enumerable.Range(0, files.Count).Any(i => File.Exists(Path.Combine(dir, $"{baseName}_{i:000}{ext}")));
                if (!clash) break;
                baseName = $"{name} ({n})";
            }
            try
            {
                if (files.Count == 1)
                {
                    string dest = Path.Combine(dir, baseName + ext);
                    File.Move(files[0], dest);
                    return dest;
                }
                for (int i = 0; i < files.Count; i++) File.Move(files[i], Path.Combine(dir, $"{baseName}_{i:000}{ext}"));
                return Path.Combine(dir, baseName + "_%03d" + ext);
            }
            catch (Exception ex) { AppendLog("Rinomina non riuscita: " + ex.Message); return null; }
        }

        int PreviewWidth()
        {
            // anteprima ~ grande quanto il riquadro a schermo: disegno quasi 1:1, niente ridimensionamenti costosi
            double sc = Math.Min((double)Math.Max(1, canvas.ClientSize.Width) / settings.CanvasW, (double)Math.Max(1, canvas.ClientSize.Height) / settings.CanvasH);
            return (int)Math.Clamp(settings.CanvasW * sc, 480, 1280);
        }

        void ToggleMonitor()
        {
            settings.AudioMonitor = !settings.AudioMonitor;
            settings.Save();
            btnMonitor.Text = settings.AudioMonitor ? "🎧  Ascolto attivo" : "🎧  Ascolta";
            btnMonitor.Variant = settings.AudioMonitor ? "accent" : "ghost";
            btnMonitor.Invalidate();
            if (!settings.AudioMonitor) monitor.Stop();
            if (engine.IsRecording) { if (settings.AudioMonitor) AppendLog("L'ascolto partirà alla prossima anteprima (non interrompo la registrazione)"); return; }
            RestartIfRunning();
        }

        void StartMonitorIfNeeded()
        {
            if (settings.AudioMonitor && settings.Sources.Any(x => x.Visible && x.HasAudio))
            {
                try { monitor.DeviceNumber = settings.MonitorDevice; monitor.Start(); } catch (Exception ex) { AppendLog("Ascolto audio non disponibile: " + ex.Message); }
            }
            else monitor.Stop();
        }

        IEnumerable<string> RecordedFiles(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return Array.Empty<string>();
            if (!pattern.Contains("%03d")) return File.Exists(pattern) ? new[] { pattern } : Array.Empty<string>();
            try { return Directory.GetFiles(Path.GetDirectoryName(pattern), Path.GetFileName(pattern).Replace("%03d", "???")).OrderBy(x => x).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        long RecordedBytes(string pattern)
        {
            long t = 0;
            foreach (var f in RecordedFiles(pattern)) { try { t += new FileInfo(f).Length; } catch { } }
            return t;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

        static long FreeBytes(string folder)
        {
            try { if (GetDiskFreeSpaceEx(folder.EndsWith("\\") ? folder : folder + "\\", out ulong f, out _, out _)) return (long)f; } catch { }
            return -1;
        }

        DateTime lastDiagAt = DateTime.MinValue;
        DateTime lastDiskCheck = DateTime.MinValue, lastCpuSample = DateTime.MinValue; long lastFree = -1;

        /// <summary>
        /// Fine cassetta: se durante la registrazione arriva schermo blu/nero uniforme per N secondi, si ferma da sola.
        /// Si arma solo dopo almeno 10 s di immagine vera, così se premi Registra prima del Play non si ferma subito.
        /// </summary>
        void OnSignal(bool blank, string kind)
        {
            if (pausedSince != null) { blankSince = null; return; }   // in pausa lo stop automatico non vale
            if (!blank)
            {
                blankSince = null; blankKind = "";
                if (engine.IsRecording) contentSamples++;
                return;
            }
            if (blankSince == null)
            {
                blankSince = DateTime.Now; blankKind = kind;
                blankStartRecSec = engine.IsRecording ? RecElapsed().TotalSeconds : -1;
            }
            if (!engine.IsRecording || finalizing || !settings.AutoStopOnBlank) return;
            bool armed = contentSamples >= 20;   // 20 campioni a 2/s = 10 s di immagine vera
            if (armed && (DateTime.Now - blankSince.Value).TotalSeconds >= Math.Max(5, settings.AutoStopSeconds))
            {
                AppendLog($"Fine cassetta rilevata (schermo {blankKind} da {settings.AutoStopSeconds} s): fermo la registrazione");
                autoStopped = true;
                StopRecording(true);
            }
        }

        void AppendLog(string line)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (txtLog.TextLength > 200000) txtLog.Clear();
                    txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
                }));
            }
            catch { }
        }

        // ---------------- stato ----------------

        void UpdateStatus()
        {
            foreach (var r in mixerRows.Values) r.RefreshState(engine.IsRunning);
            string enc = settings.Encoder + " " + (settings.RateControl == "CRF" ? $"CRF {settings.Crf}" : $"{settings.RateControl} {settings.VideoBitrate} kbps");
            var parts = new List<string> { $"{settings.CanvasW}×{settings.CanvasH} @{settings.Fps}" };
            if (engine.IsRunning)
            {
                if ((DateTime.Now - lastCpuSample).TotalSeconds >= 1) { lastCpu = engine.CpuPercent(); lastCpuSample = DateTime.Now; }
                var st = lastStats;
                if (st != null) parts.Add($"uscita {st.Fps:0.0} fps");
                // ogni secondo: ritmo di arrivo da ffmpeg e ritmo di disegno a schermo, con la pausa più lunga
                if ((DateTime.Now - lastDiagAt).TotalSeconds >= 1)
                {
                    lastDiagAt = DateTime.Now;
                    var (afps, agap, adrop) = engine.TakeArrivalStats();
                    adrop += canvas.TakeLatencyDrops();
                    double now = paintClock.Elapsed.TotalSeconds;
                    double pfps = paintWindowStart > 0 ? painted / (now - paintWindowStart) : 0;
                    previewDiag = $"anteprima: arrivo {afps:0} fps (pausa max {agap:0} ms) · a schermo {pfps:0} fps (pausa max {paintMaxGap * 1000:0} ms)" + (adrop > 0 ? $" · saltati {adrop}" : "");
                    // ogni 5 s anche nel Log, così basta incollare il Log per la diagnosi
                    diagAcc.Add((afps, agap, pfps, paintMaxGap * 1000, adrop, engine.SourceFps, lastStats?.Fps ?? 0));
                    if (diagAcc.Count >= 5 && !settings.DiagLog) diagAcc.Clear();
                    if (diagAcc.Count >= 5)
                    {
                        var (rAvg, rMax) = canvas.TakeRenderStats();
                        AppendLog($"[diagnostica 5 s] sorgente {diagAcc.Average(d => d.src):0.0} fps · uscita {diagAcc.Average(d => d.outf):0.0} fps · " +
                                  $"arrivo anteprima {diagAcc.Average(d => d.af):0.0} fps (pausa max {diagAcc.Max(d => d.ag):0} ms) · " +
                                  $"a schermo {diagAcc.Average(d => d.pf):0.0} fps (pausa max {diagAcc.Max(d => d.pg):0} ms) · saltati {diagAcc.Sum(d => d.dr)} · " +
                                  $"disegno frame {rAvg:0.0} ms (max {rMax:0}) · CPU ffmpeg {lastCpu:0}%");
                        diagAcc.Clear();
                    }
                    painted = 0; paintMaxGap = 0; paintWindowStart = now;
                }
                if (previewDiag != "") parts.Add(previewDiag);
                double sf = engine.SourceFps;
                if (sf > 0)
                {
                    var capSrc = settings.Sources.FirstOrDefault(x => x.Visible && x.Type == SourceType.Capture);
                    double exp = 0;
                    var inf = capSrc != null ? engine.GetInputInfo(capSrc.Id) : null;   // "1280×720 · 30 fps · nv12"
                    if (inf != null) { var m = System.Text.RegularExpressions.Regex.Match(inf, @"([\d.]+) fps"); if (m.Success) double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out exp); }
                    string warn = exp > 0 && sf < exp * 0.85 ? $" ⚠ (dovrebbe mandarne {exp:0}: la sorgente rallenta)" : "";
                    parts.Add($"sorgente {sf:0.0} fps{warn}");
                }
                if (st != null && (st.Drop > 0 || st.Dup > 0)) parts.Add($"persi {st.Drop} · duplicati {st.Dup}");
                parts.Add($"CPU ffmpeg {lastCpu:0}%");
                if (engine.IsRecording) parts.Add(enc);
            }
            // spazio libero e tempo di registrazione residuo (come le Statistiche di OBS)
            if ((DateTime.Now - lastDiskCheck).TotalSeconds > 5) { lastFree = FreeBytes(settings.ResolvedOutputFolder()); lastDiskCheck = DateTime.Now; }
            if (lastFree >= 0)
            {
                double bps = (settings.RateControl == "CRF" ? 8000 : settings.VideoBitrate) * 1000.0 / 8 + settings.AudioBitrate * 1000.0 / 8;
                var left = TimeSpan.FromSeconds(lastFree / Math.Max(1, bps));
                parts.Add($"liberi {Fmt(lastFree)} (~{(int)left.TotalHours} h {left.Minutes:00} m)");
            }
            lblStatus.Text = string.Join("   ·   ", parts);

            if (engine.IsRecording)
            {
                var el = RecElapsed();
                bool isPaused = pausedSince != null;
                long size = 0;
                size = RecordedBytes(recFile);
                bool blink = (DateTime.Now.Millisecond / 500) % 2 == 0;
                lblRec.Fill = isPaused ? Color.FromArgb(215, 150, 20) : Theme.Rec; lblRec.ForeColor = Color.White;
                string tag = isPaused ? "⏸ IN PAUSA" : $"{(blink ? "●" : "○")} REC";
                lblRec.Text = $"{tag}  {el:hh\\:mm\\:ss}   {Fmt(size)}   {Path.GetFileName(finalFile).Replace("_%03d", "")}";
                canvas.RecText = isPaused ? $"⏸  IN PAUSA  {el:hh\\:mm\\:ss}" : $"{(blink ? "●" : "○")}  REC  {el:hh\\:mm\\:ss}";
                if (blankSince != null && settings.AutoStopOnBlank && !isPaused)
                {
                    int left = Math.Max(0, settings.AutoStopSeconds - (int)(DateTime.Now - blankSince.Value).TotalSeconds);
                    canvas.RecText += contentSamples >= 20 ? $"    schermo {blankKind}: stop tra {left} s" : $"    schermo {blankKind} (in attesa del Play)";
                }
                canvas.Invalidate();
                if (settings.MaxMinutes > 0 && el.TotalMinutes >= settings.MaxMinutes)
                {
                    AppendLog($"Stop automatico dopo {settings.MaxMinutes} minuti");
                    StopRecording(true);
                }
                if ((DateTime.Now - lastFrameAt).TotalSeconds > 5 && frames > 0)
                    lblRec.Text += "   ⚠ nessun frame da " + (int)(DateTime.Now - lastFrameAt).TotalSeconds + "s";
            }
            else if (!finalizing)
            {
                if (canvas.RecText != null) { canvas.RecText = null; canvas.Invalidate(); }
                lblRec.Fill = engine.IsRunning ? Theme.Accent : Color.Transparent;
                lblRec.ForeColor = engine.IsRunning ? Color.White : Theme.Fore;
                lblRec.Text = engine.IsRunning ? "●  Anteprima" : "";
            }
        }

        static string Fmt(long b)
        {
            if (b > 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b > 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            return (b / 1024.0).ToString("0") + " KB";
        }
    }
}
