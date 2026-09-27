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
        RoundedButton btnPreview, btnRec, btnStop, btnSettings, btnFolder, btnTheme, btnLog;
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
        bool autoRetried, retryRecording;

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
                if (e.KeyCode != Keys.F9) return;
                e.Handled = true;
                if (engine.IsRecording) { if (!finalizing) StopRecording(true); }
                else StartRecording();
            };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Theme.Apply(this, settings.DarkTheme);
            RefreshSourceList();
            RebuildMixer();

            engine.FrameReady += OnFrame;
            engine.AudioLevels += (id, rl, pl, rr, pr) => { if (IsHandleCreated) try { BeginInvoke(new Action(() => { if (mixerRows.TryGetValue(id, out var row)) row.Meter.SetLevels(rl, pl, rr, pr); })); } catch { } };
            engine.Stats += st => lastStats = st;
            engine.MonitorData += (d, n) => monitor.Add(d, n);
            engine.Log += l => { lock (runLog) { if (runLog.Length < 20000) runLog.AppendLine(l); } AppendLog(l); };
            engine.Exited += OnEngineExited;

            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (o, e) => UpdateStatus();
            timer.Start();
            restartTimer = new System.Windows.Forms.Timer { Interval = 450 };
            restartTimer.Tick += (o, e) => { restartTimer.Stop(); if (!engine.IsRecording) StartPreview(); };

            Load += (o, e) =>
            {
                ApplySplitters();
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
                    StopRecording(false);
                }
                engine.Stop();
                monitor.Stop();
                settings.WindowMax = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal) { settings.WindowW = ClientSize.Width; settings.WindowH = ClientSize.Height; }
                settings.ShowLog = !splitLog.Panel2Collapsed;
                settings.RightPanelHidden = splitMain.Panel2Collapsed;
                if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance;
                settings.MixerH = splitRight.Height - splitRight.SplitterDistance;
                if (!splitLog.Panel2Collapsed) settings.LogH = splitLog.Height - splitLog.SplitterDistance;
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
            btnPreview = Ui.Btn("▶   Anteprima", "normal", (o, e) => StartPreview());
            btnRec = Ui.Btn("⏺   Registra", "rec", (o, e) => StartRecording(), 130);
            new ToolTip().SetToolTip(btnRec, "Registra (F9)");
            btnStop = Ui.Btn("⏹   Stop", "normal", (o, e) => StopRecording(true)); btnStop.Enabled = false;
            new ToolTip().SetToolTip(btnStop, "Ferma registrazione (F9)");
            lblName = new Label { Text = "Nome file", AutoSize = true, Tag = "muted", Margin = new Padding(20, 10, 6, 0) };
            txtName = new TextBox { Width = 240, Margin = new Padding(0, 6, 0, 0), PlaceholderText = "es. Rossi_matrimonio_1994", Font = new Font("Segoe UI", 10f) };
            btnSettings = Ui.Btn("⚙   Uscita", "ghost", (o, e) => OpenSettings()); btnSettings.Margin = new Padding(20, 0, 8, 0);
            btnFolder = Ui.Btn("📁   Apri cartella", "ghost", (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Ui.IconBtn("◐", "Tema chiaro/scuro", (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); RefreshSourceList(); });
            btnLog = Ui.Btn("Log", "ghost", (o, e) => ToggleLog());
            btnPanels = Ui.IconBtn("◧", "Mostra/nascondi pannello Sorgenti e Mixer", (o, e) => ToggleRightPanel());
            flow.Controls.AddRange(new Control[] { btnPreview, btnRec, btnStop, lblName, txtName, btnSettings, btnFolder, btnTheme, btnPanels, btnLog });
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
            new ToolTip().SetToolTip(btnMonitor, "Monitoraggio audio: senti l'audio del grabber dalle casse (come OBS)");
            monBar.Controls.Add(btnMonitor);
            cardMixer.Controls.Add(mixer); cardMixer.Controls.Add(monBar);

            // ---- log ----
            cardLog = new Card { Dock = DockStyle.Fill, HeaderText = "LOG FFMPEG", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), WordWrap = false, BorderStyle = BorderStyle.None };
            cardLog.Controls.Add(txtLog);

            // ---- split: destra (sorgenti | mixer) ----
            splitRight = new SplitContainer { Size = new Size(400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 80, Panel2MinSize = 80 };
            splitRight.Panel1.Controls.Add(cardSources);
            splitRight.Panel2.Controls.Add(cardMixer);

            // ---- split: canvas | destra ----
            splitMain = new SplitContainer { Size = new Size(1400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = gap, Panel1MinSize = 240, Panel2MinSize = 200, Panel2Collapsed = settings.RightPanelHidden };
            splitMain.Panel1.Controls.Add(canvasCard);
            splitMain.Panel2.Controls.Add(splitRight);

            // ---- split: sopra | log ----
            splitLog = new SplitContainer { Size = new Size(1400, 900), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 160, Panel2MinSize = 60, Panel2Collapsed = !settings.ShowLog };
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

        void ApplySplitters()
        {
            try
            {
                if (!splitMain.Panel2Collapsed) splitMain.SplitterDistance = Math.Max(splitMain.Panel1MinSize, splitMain.Width - Math.Max(200, settings.RightPanelW) - splitMain.SplitterWidth);
                splitRight.SplitterDistance = Math.Max(splitRight.Panel1MinSize, splitRight.Height - Math.Max(80, settings.MixerH) - splitRight.SplitterWidth);
                if (!splitLog.Panel2Collapsed) splitLog.SplitterDistance = Math.Max(splitLog.Panel1MinSize, splitLog.Height - Math.Max(80, settings.LogH) - splitLog.SplitterWidth);
            }
            catch { }
        }

        void ToggleLog()
        {
            splitLog.Panel2Collapsed = !splitLog.Panel2Collapsed;
            if (!splitLog.Panel2Collapsed)
                try { splitLog.SplitterDistance = Math.Max(splitLog.Panel1MinSize, splitLog.Height - Math.Max(80, settings.LogH) - splitLog.SplitterWidth); } catch { }
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
            using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 130), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font };
            var l = new Label { Text = label, Left = 14, Top = 16, AutoSize = true };
            var t = new TextBox { Left = 14, Top = 40, Width = 350, Text = value };
            var ok = Ui.Btn("OK", "accent", null, 100); ok.Left = 158; ok.Top = 80; ok.DialogResult = DialogResult.OK;
            var ca = Ui.Btn("Annulla", "normal", null, 100); ca.Left = 264; ca.Top = 80; ca.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { l, t, ok, ca });
            f.AcceptButton = ok; f.CancelButton = ca;
            Theme.Apply(f, settings.DarkTheme);
            return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
        }

        void ToggleRightPanel()
        {
            if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance;
            splitMain.Panel2Collapsed = !splitMain.Panel2Collapsed;
            if (!splitMain.Panel2Collapsed) ApplySplitters();
            settings.RightPanelHidden = splitMain.Panel2Collapsed;
            settings.Save();
        }

        bool? compactState;
        /// <summary>Finestra stretta: pulsanti solo icona, così la barra ci sta anche su schermi piccoli o a metà schermo.</summary>
        void ApplyCompact()
        {
            if (btnPreview == null) return;
            bool compact = ClientSize.Width < 1180;
            if (compactState == compact) return;
            compactState = compact;
            btnPreview.Text = compact ? "▶" : "▶   Anteprima";
            btnRec.Text = compact ? "⏺  REC" : "⏺   Registra";
            btnRec.MinimumSize = new Size(compact ? 80 : 130, 36);
            btnStop.Text = compact ? "⏹" : "⏹   Stop";
            btnSettings.Text = compact ? "⚙" : "⚙   Uscita";
            btnFolder.Text = compact ? "📁" : "📁   Apri cartella";
            lblName.Visible = !compact;
            txtName.Width = compact ? 150 : 240;
            btnSettings.Margin = new Padding(compact ? 8 : 20, 0, 8, 0);
            foreach (var b in new[] { btnPreview, btnStop, btnSettings, btnFolder }) new ToolTip().SetToolTip(b, b == btnPreview ? "Anteprima" : b == btnStop ? "Stop (F9)" : b == btnSettings ? "Impostazioni di uscita" : "Apri cartella");
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
            var fb = canvas.Frame;
            if (src == null || !src.Visible || fb == null || !engine.IsRunning) return null;
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
            try { engine.Start(settings, null, PreviewWidth(), settings.AudioMonitor); StartMonitorIfNeeded(); }
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
            frames = 0; ResetMeters();
            lock (runLog) runLog.Clear();
            try { engine.Start(settings, recFile, PreviewWidth(), settings.AudioMonitor); StartMonitorIfNeeded(); }
            catch (Exception ex) { AppendLog("Errore avvio registrazione: " + ex.Message); return; }
            recStart = DateTime.Now;
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
            await Task.Run(() => engine.Stop());

            bool HasData(string f) => RecordedBytes(f) > 4096;

            if (!HasData(written))
            {
                try { foreach (var fx in RecordedFiles(written)) File.Delete(fx); } catch { }
                AppendLog("Registrazione NON salvata: ffmpeg non ha scritto niente (vedi errori sopra)");
                if (!retryRecording)
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

            finalizing = false;
            lblRec.Text = "";
            if (retryRecording && !IsDisposed) { retryRecording = false; SetButtons(); StartRecording(); return; }
            if (restartPreview && !IsDisposed) StartPreview();
            SetButtons();
        }

        void OpenSettings()
        {
            if (engine.IsRecording) { MessageBox.Show(this, "Ferma la registrazione prima di cambiare le impostazioni di uscita.", "VHSCapture"); return; }
            using var f = new SettingsForm(settings);
            var r = f.ShowDialog(this);
            Theme.Apply(this, settings.DarkTheme);
            if (r != DialogResult.OK) return;
            canvas.CanvasW = settings.CanvasW; canvas.CanvasH = settings.CanvasH;
            canvas.Invalidate();
            StartPreview();
        }

        void SetButtons()
        {
            bool rec = engine.IsRecording;
            btnPreview.Enabled = !rec && !finalizing;
            btnRec.Enabled = !rec && !finalizing;
            btnStop.Enabled = rec && !finalizing;
            btnSettings.Enabled = !rec && !finalizing;
            txtName.Enabled = !rec;
            UpdateSourceButtons();
        }

        // ---------------- eventi engine ----------------

        void OnFrame(FrameBuf bmp)
        {
            if (!IsHandleCreated || IsDisposed) { engine.FrameConsumed(); return; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        canvas.SetFrame(bmp);
                        canvas.Update();          // disegna subito questo frame
                        frames++; lastFrameAt = DateTime.Now; autoRetried = false;
                    }
                    finally { engine.FrameConsumed(); }
                }));
            }
            catch { engine.FrameConsumed(); }
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
                        string lg; lock (runLog) lg = runLog.ToString();
                        bool encFail = lg.Contains("Error while opening encoder") || lg.Contains("Could not open encoder") || lg.Contains("Error creating a MFX session");
                        if (encFail && settings.Encoder != "libx264")
                        {
                            AppendLog($"L'encoder {settings.Encoder} non si apre su questo PC: riprovo con x264 software");
                            settings.Encoder = "libx264"; settings.Save();
                            retryRecording = true;
                        }
                        else AppendLog("ATTENZIONE: ffmpeg è uscito durante la registrazione — chiudo il file");
                        StopRecording(!retryRecording);
                        return;
                    }
                    if (frames == 0 && code != 0 && !autoRetried && TryAutoFallback()) return;
                    if (frames == 0 && code != 0) { canvas.Message = "ffmpeg non è partito — vedi il Log qui sotto"; if (splitLog.Panel2Collapsed) ToggleLog(); }
                    else canvas.Message = "Anteprima ferma";
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
                if (src.InputSize != "auto" || src.InputFps != "auto")
                {
                    AppendLog($"\"{src.VideoDevice}\" non supporta {src.InputSize} @ {src.InputFps}: passo a risoluzione/fps automatici");
                    src.InputSize = "auto"; src.InputFps = "auto"; changed = true;
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
                try { monitor.Start(); } catch (Exception ex) { AppendLog("Ascolto audio non disponibile: " + ex.Message); }
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

        DateTime lastDiskCheck = DateTime.MinValue, lastCpuSample = DateTime.MinValue; long lastFree = -1;

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
                if (st != null) parts.Add($"{st.Fps:0.0} fps");
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
                var el = DateTime.Now - recStart;
                long size = 0;
                size = RecordedBytes(recFile);
                bool blink = (DateTime.Now.Millisecond / 500) % 2 == 0;
                lblRec.Fill = Theme.Rec; lblRec.ForeColor = Color.White;
                lblRec.Text = $"{(blink ? "●" : "○")} REC  {el:hh\\:mm\\:ss}   {Fmt(size)}   {Path.GetFileName(finalFile).Replace("_%03d", "")}";
                canvas.RecText = $"{(blink ? "●" : "○")}  REC  {el:hh\\:mm\\:ss}";
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
