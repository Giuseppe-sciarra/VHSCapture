using System;
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
        ListView lvSources;
        Panel mixer; VuMeter vu;
        System.Windows.Forms.Timer timer, restartTimer;

        DateTime recStart;
        string recFile, finalFile;
        bool finalizing, syncingList;
        int frames; DateTime lastFrameAt = DateTime.MinValue;

        public MainForm()
        {
            Text = "VHSCapture";
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(1000, 620);
            ClientSize = new Size(settings.WindowW, settings.WindowH);
            StartPosition = FormStartPosition.CenterScreen;
            if (settings.WindowMax) WindowState = FormWindowState.Maximized;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Theme.Apply(this, settings.DarkTheme);
            RefreshSourceList();
            RebuildMixer();

            engine.FrameReady += OnFrame;
            engine.AudioLevel += db => { if (IsHandleCreated) BeginInvoke(new Action(() => vu.SetLevel(db))); };
            engine.Log += AppendLog;
            engine.Exited += OnEngineExited;

            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (o, e) => UpdateStatus();
            timer.Start();
            restartTimer = new System.Windows.Forms.Timer { Interval = 450 };
            restartTimer.Tick += (o, e) => { restartTimer.Stop(); if (!engine.IsRecording) StartPreview(); };

            Load += (o, e) =>
            {
                ApplySplitters();
                if (!FFmpeg.Exists)
                {
                    MessageBox.Show(this, "ffmpeg.exe non trovato accanto a VHSCapture.exe.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (settings.Sources.Count == 0) AddSource(SourceType.Capture);
                else StartPreview();
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
                settings.WindowMax = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal) { settings.WindowW = ClientSize.Width; settings.WindowH = ClientSize.Height; }
                settings.ShowLog = !splitLog.Panel2Collapsed;
                settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance;
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
            btnStop = Ui.Btn("⏹   Stop", "normal", (o, e) => StopRecording(true)); btnStop.Enabled = false;
            var lblName = new Label { Text = "Nome file", AutoSize = true, Tag = "muted", Margin = new Padding(20, 10, 6, 0) };
            txtName = new TextBox { Width = 240, Margin = new Padding(0, 6, 0, 0), PlaceholderText = "es. Rossi_matrimonio_1994", Font = new Font("Segoe UI", 10f) };
            btnSettings = Ui.Btn("⚙   Uscita", "ghost", (o, e) => OpenSettings()); btnSettings.Margin = new Padding(20, 0, 8, 0);
            btnFolder = Ui.Btn("📁   Apri cartella", "ghost", (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Ui.IconBtn("◐", "Tema chiaro/scuro", (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); });
            btnLog = Ui.Btn("Log", "ghost", (o, e) => ToggleLog());
            flow.Controls.AddRange(new Control[] { btnPreview, btnRec, btnStop, lblName, txtName, btnSettings, btnFolder, btnTheme, btnLog });
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
            canvas.RemoveRequested += s2 => RemoveSource(s2);
            var canvasCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(8), Radius = 10 };
            canvasCard.Controls.Add(canvas);

            // ---- sorgenti ----
            cardSources = new Card { Dock = DockStyle.Fill, HeaderText = "SORGENTI", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            lvSources = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10f) };
            lvSources.Columns.Add("Nome", 600);
            lvSources.ItemChecked += (o, e) => { if (syncingList) return; var sx = e.Item.Tag as Source; if (sx != null && sx.Visible != e.Item.Checked) { if (engine.IsRecording) { syncingList = true; e.Item.Checked = sx.Visible; syncingList = false; return; } sx.Visible = e.Item.Checked; settings.Save(); RestartIfRunning(); canvas.Invalidate(); } };
            lvSources.SelectedIndexChanged += (o, e) => { if (syncingList) return; var sx = lvSources.SelectedItems.Count > 0 ? lvSources.SelectedItems[0].Tag as Source : null; canvas.Select(sx); UpdateSourceButtons(); };
            lvSources.DoubleClick += (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); };
            var srcBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Tag = "panel" };
            btnAdd = Ui.IconBtn("＋", "Aggiungi sorgente", (o, e) => ShowAddMenu());
            btnRemove = Ui.IconBtn("－", "Rimuovi sorgente", (o, e) => { if (canvas.Selected != null) RemoveSource(canvas.Selected); });
            btnProps = Ui.IconBtn("⚙", "Proprietà", (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); });
            btnUp = Ui.IconBtn("▲", "Porta sopra", (o, e) => MoveSource(+1));
            btnDown = Ui.IconBtn("▼", "Porta sotto", (o, e) => MoveSource(-1));
            srcBtns.Controls.AddRange(new Control[] { btnAdd, btnRemove, btnProps, btnUp, btnDown });
            cardSources.Controls.Add(lvSources);
            cardSources.Controls.Add(srcBtns);

            // ---- mixer ----
            cardMixer = new Card { Dock = DockStyle.Fill, HeaderText = "MIXER AUDIO", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            vu = new VuMeter { Dock = DockStyle.Right, Width = 30 };
            mixer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "panel" };
            cardMixer.Controls.Add(mixer); cardMixer.Controls.Add(vu);

            // ---- log ----
            cardLog = new Card { Dock = DockStyle.Fill, HeaderText = "LOG FFMPEG", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), WordWrap = false, BorderStyle = BorderStyle.None };
            cardLog.Controls.Add(txtLog);

            // ---- split: destra (sorgenti | mixer) ----
            splitRight = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 120, Panel2MinSize = 100 };
            splitRight.Panel1.Controls.Add(cardSources);
            splitRight.Panel2.Controls.Add(cardMixer);

            // ---- split: canvas | destra ----
            splitMain = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = gap, Panel1MinSize = 400, Panel2MinSize = 260 };
            splitMain.Panel1.Controls.Add(canvasCard);
            splitMain.Panel2.Controls.Add(splitRight);

            // ---- split: sopra | log ----
            splitLog = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 300, Panel2MinSize = 80, Panel2Collapsed = !settings.ShowLog };
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
                splitMain.SplitterDistance = Math.Max(splitMain.Panel1MinSize, splitMain.Width - Math.Max(260, settings.RightPanelW) - splitMain.SplitterWidth);
                splitRight.SplitterDistance = Math.Max(splitRight.Panel1MinSize, splitRight.Height - Math.Max(100, settings.MixerH) - splitRight.SplitterWidth);
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
            syncingList = true;
            lvSources.Items.Clear();
            // la lista mostra in alto la sorgente che sta sopra (come OBS)
            foreach (var s in settings.Sources.AsEnumerable().Reverse())
            {
                var it = new ListViewItem(s.Name + TypeSuffix(s)) { Tag = s, Checked = s.Visible };
                if (s == canvas?.Selected) it.Selected = true;
                lvSources.Items.Add(it);
            }
            syncingList = false;
            UpdateSourceButtons();
        }
        static string TypeSuffix(Source s) => s.Type switch { SourceType.Capture => "", SourceType.Image => "  (immagine)", _ => "  (colore)" };

        void SyncListSelection(Source s)
        {
            syncingList = true;
            foreach (ListViewItem it in lvSources.Items) it.Selected = it.Tag == s;
            syncingList = false;
        }

        void UpdateSourceButtons()
        {
            bool sel = canvas.Selected != null, rec = engine.IsRecording;
            btnRemove.Enabled = sel && !rec; btnProps.Enabled = sel;
            int i = sel ? settings.Sources.IndexOf(canvas.Selected) : -1;
            btnUp.Enabled = sel && !rec && i < settings.Sources.Count - 1;
            btnDown.Enabled = sel && !rec && i > 0;
            btnAdd.Enabled = !rec;
        }

        void ShowAddMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Dispositivo di cattura video (grabber USB)", null, (o, e) => AddSource(SourceType.Capture));
            m.Items.Add("Immagine (logo, sfondo…)", null, (o, e) => AddSource(SourceType.Image));
            m.Items.Add("Colore pieno", null, (o, e) => AddSource(SourceType.Color));
            m.Show(btnAdd, new Point(0, btnAdd.Height));
        }

        void AddSource(SourceType type)
        {
            if (engine.IsRecording) return;
            var s = new Source { Type = type };
            s.Name = type switch { SourceType.Capture => "Grabber USB", SourceType.Image => "Immagine", _ => "Colore" };
            if (type == SourceType.Capture) s.FitTo(settings.CanvasW, settings.CanvasH);
            else if (type == SourceType.Color) s.FillTo(settings.CanvasW, settings.CanvasH);
            else { s.W = 400; s.H = 300; s.Center(settings.CanvasW, settings.CanvasH); }

            using var f = new SourceForm(s, settings, LiveApply, false, WithDeviceFree, AppendLog);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            var res = f.Result;
            if (type == SourceType.Capture && res.X == s.X && res.W == s.W) res.FitTo(settings.CanvasW, settings.CanvasH); // ricalcola con la risoluzione scelta
            settings.Sources.Add(res);
            settings.Save();
            RefreshSourceList();
            RebuildMixer();
            canvas.Select(res);
            StartPreview();
        }

        void EditSource(Source s)
        {
            var before = s.Clone();
            using var f = new SourceForm(s, settings, LiveApply, engine.IsRecording, WithDeviceFree, AppendLog);
            if (f.ShowDialog(this) != DialogResult.OK) { LiveApply(before); return; }
            var res = f.Result;
            int i = settings.Sources.IndexOf(s);
            bool structural = !res.StructurallyEquals(before);
            settings.Sources[i] = res;
            canvas.Sources = settings.Sources;
            settings.Save();
            RefreshSourceList();
            RebuildMixer();
            canvas.Select(res);
            if (structural) RestartIfRunning();
            else { engine.ApplyTransform(res); engine.ApplyColor(res); engine.ApplyVolume(res); if (!engine.LiveControl) RestartIfRunning(); }
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
            int y = 0, w = Math.Max(120, mixer.ClientSize.Width - 4);
            foreach (var sx in settings.Sources.Where(x => x.HasAudio))
            {
                var row = new Panel { Left = 0, Top = y, Width = w, Height = 62, Tag = "panel", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
                var name = new Label { Text = sx.Name, Left = 0, Top = 2, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) };
                var val = new Label { Text = sx.VolumeDb.ToString("0") + " dB", Left = row.Width - 64, Top = 2, Width = 64, TextAlign = ContentAlignment.TopRight, Tag = "muted", Anchor = AnchorStyles.Top | AnchorStyles.Right };
                var tb = new TrackBar { Left = 0, Top = 22, Width = row.Width - 70, Height = 32, Minimum = -60, Maximum = 12, Value = Math.Clamp((int)Math.Round(sx.VolumeDb), -60, 12), TickStyle = TickStyle.None, AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Tag = sx };
                var mute = new CheckBox { Text = "Muto", Left = row.Width - 62, Top = 28, AutoSize = true, Checked = sx.Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right, Tag = sx };
                tb.ValueChanged += (o, e) => { sx.VolumeDb = tb.Value; val.Text = tb.Value + " dB"; engine.ApplyVolume(sx); };
                tb.MouseUp += (o, e) => { settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                mute.CheckedChanged += (o, e) => { sx.Muted = mute.Checked; engine.ApplyVolume(sx); settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                row.Controls.AddRange(new Control[] { name, val, tb, mute });
                mixer.Controls.Add(row);
                y += 66;
            }
            if (mixer.Controls.Count == 0)
                mixer.Controls.Add(new Label { Text = "Nessuna sorgente audio", Left = 0, Top = 4, AutoSize = true, Tag = "muted" });
            mixer.ResumeLayout();
            Theme.Apply(this, settings.DarkTheme);
        }

        void RefreshMixerValues()
        {
            foreach (Control row in mixer.Controls)
                foreach (Control c in row.Controls)
                {
                    if (c is TrackBar tb && tb.Tag is Source s) { int v = Math.Clamp((int)Math.Round(s.VolumeDb), -60, 12); if (tb.Value != v) tb.Value = v; }
                    if (c is CheckBox ch && ch.Tag is Source s2 && ch.Checked != s2.Muted) ch.Checked = s2.Muted;
                }
        }

        // ---------------- motore ----------------

        void StartPreview()
        {
            if (!FFmpeg.Exists || engine.IsRecording) return;
            restartTimer.Stop();
            frames = 0; vu.Reset();
            canvas.Message = settings.Sources.Any(x => x.Visible) ? "Avvio anteprima…" : "Nessuna sorgente: premi ＋ per aggiungere il grabber";
            canvas.SetFrame(null);
            try { engine.Start(settings, null); }
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
            finalFile = Path.Combine(folder, baseName + ".mp4");
            recFile = settings.SafeRecording ? Path.Combine(folder, baseName + ".mkv") : finalFile;

            restartTimer.Stop();
            frames = 0; vu.Reset();
            try { engine.Start(settings, recFile); }
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

            string written = recFile, final = finalFile;
            await Task.Run(() => engine.Stop());

            if (settings.SafeRecording && !string.Equals(written, final, StringComparison.OrdinalIgnoreCase))
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
            else AppendLog("Salvato: " + final);

            finalizing = false;
            lblRec.Text = "";
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

        void OnFrame(Bitmap bmp)
        {
            if (!IsHandleCreated || IsDisposed) { bmp.Dispose(); return; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    canvas.SetFrame(bmp);
                    frames++; lastFrameAt = DateTime.Now;
                }));
            }
            catch { bmp.Dispose(); }
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
                        AppendLog("ATTENZIONE: ffmpeg è uscito durante la registrazione — chiudo il file");
                        StopRecording(false);
                    }
                    if (frames == 0 && code != 0) { canvas.Message = "ffmpeg non è partito — vedi il Log qui sotto"; if (splitLog.Panel2Collapsed) ToggleLog(); }
                    else canvas.Message = "Anteprima ferma";
                    canvas.SetFrame(null);
                    vu.Reset();
                    SetButtons();
                }
            }));
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
            string enc = settings.Encoder + " " + (settings.RateControl == "CRF" ? $"CRF {settings.Crf}" : $"{settings.RateControl} {settings.VideoBitrate} kbps");
            string live = engine.IsRunning ? (engine.LiveControl ? "  ·  live" : "  ·  no-live") : "";
            lblStatus.Text = $"{settings.CanvasW}×{settings.CanvasH} @{settings.Fps}fps  ·  {enc}{live}  ·  {settings.ResolvedOutputFolder()}";

            if (engine.IsRecording)
            {
                var el = DateTime.Now - recStart;
                long size = 0;
                try { if (File.Exists(recFile)) size = new FileInfo(recFile).Length; } catch { }
                bool blink = (DateTime.Now.Millisecond / 500) % 2 == 0;
                lblRec.Fill = Theme.Rec; lblRec.ForeColor = Color.White;
                lblRec.Text = $"{(blink ? "●" : "○")} REC  {el:hh\\:mm\\:ss}   {Fmt(size)}   {Path.GetFileName(finalFile)}";
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
