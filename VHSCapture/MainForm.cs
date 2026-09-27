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

        Panel top, right, bottom;
        CanvasView canvas;
        Button btnPreview, btnRec, btnStop, btnSettings, btnFolder, btnTheme, btnLog;
        Button btnAdd, btnRemove, btnProps, btnUp, btnDown;
        TextBox txtName, txtLog;
        Label lblStatus, lblRec;
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
                settings.WindowW = ClientSize.Width; settings.WindowH = ClientSize.Height;
                settings.ShowLog = bottom.Visible;
                settings.Save();
            };
        }

        // ---------------- UI ----------------

        void BuildUi()
        {
            top = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10), Tag = "panel" };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            btnPreview = Btn("▶  Anteprima", null, (o, e) => StartPreview());
            btnRec = Btn("⏺  Registra", "rec", (o, e) => StartRecording());
            btnStop = Btn("⏹  Stop", null, (o, e) => StopRecording(true)); btnStop.Enabled = false;
            var lblName = new Label { Text = "Nome:", AutoSize = true, Margin = new Padding(18, 8, 4, 0) };
            txtName = new TextBox { Width = 220, Margin = new Padding(0, 4, 0, 0), PlaceholderText = "es. Rossi_matrimonio_1994" };
            btnSettings = Btn("⚙  Uscita", null, (o, e) => OpenSettings()); btnSettings.Margin = new Padding(18, 4, 4, 4);
            btnFolder = Btn("📁  Apri cartella", null, (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Btn("◐", null, (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); }); btnTheme.Width = 36;
            btnLog = Btn("Log", null, (o, e) => { bottom.Visible = !bottom.Visible; }); btnLog.Width = 50;
            flow.Controls.AddRange(new Control[] { btnPreview, btnRec, btnStop, lblName, txtName, btnSettings, btnFolder, btnTheme, btnLog });
            top.Controls.Add(flow);

            bottom = new Panel { Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(10, 0, 10, 8), Visible = settings.ShowLog, Tag = "panel" };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), WordWrap = false };
            bottom.Controls.Add(txtLog);

            var status = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 0, 12, 0), Tag = "panel" };
            lblRec = new Label { AutoSize = true, Dock = DockStyle.Left, Font = new Font("Segoe UI Semibold", 10f), Padding = new Padding(0, 8, 0, 0) };
            lblStatus = new Label { AutoSize = true, Dock = DockStyle.Right, Tag = "muted", Padding = new Padding(0, 8, 0, 0) };
            status.Controls.Add(lblStatus); status.Controls.Add(lblRec);

            // ---- pannello destro: sorgenti + mixer ----
            right = new Panel { Dock = DockStyle.Right, Width = 330, Padding = new Padding(10), Tag = "panel" };

            var lblSrc = new Label { Text = "SORGENTI", Dock = DockStyle.Top, Height = 22, Tag = "muted", Font = new Font("Segoe UI Semibold", 8.5f) };
            lvSources = new ListView { Dock = DockStyle.Top, Height = 190, View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, MultiSelect = false, HideSelection = false };
            lvSources.Columns.Add("Nome", 280);
            lvSources.ItemChecked += (o, e) => { if (syncingList) return; var s = e.Item.Tag as Source; if (s != null && s.Visible != e.Item.Checked) { if (engine.IsRecording) { syncingList = true; e.Item.Checked = s.Visible; syncingList = false; return; } s.Visible = e.Item.Checked; settings.Save(); RestartIfRunning(); canvas.Invalidate(); } };
            lvSources.SelectedIndexChanged += (o, e) => { if (syncingList) return; var s = lvSources.SelectedItems.Count > 0 ? lvSources.SelectedItems[0].Tag as Source : null; canvas.Select(s); UpdateSourceButtons(); };
            lvSources.DoubleClick += (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); };

            var srcBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            btnAdd = SmallBtn("＋", "Aggiungi sorgente", (o, e) => ShowAddMenu());
            btnRemove = SmallBtn("－", "Rimuovi sorgente", (o, e) => { if (canvas.Selected != null) RemoveSource(canvas.Selected); });
            btnProps = SmallBtn("⚙", "Proprietà", (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); });
            btnUp = SmallBtn("▲", "Porta sopra", (o, e) => MoveSource(+1));
            btnDown = SmallBtn("▼", "Porta sotto", (o, e) => MoveSource(-1));
            srcBtns.Controls.AddRange(new Control[] { btnAdd, btnRemove, btnProps, btnUp, btnDown });

            var lblMix = new Label { Text = "MIXER AUDIO", Dock = DockStyle.Top, Height = 22, Tag = "muted", Font = new Font("Segoe UI Semibold", 8.5f), Padding = new Padding(0, 8, 0, 0) };
            var mixHost = new Panel { Dock = DockStyle.Fill, Tag = "panel" };
            vu = new VuMeter { Dock = DockStyle.Right, Width = 30 };
            mixer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "panel" };
            mixHost.Controls.Add(mixer); mixHost.Controls.Add(vu);

            right.Controls.Add(mixHost);
            right.Controls.Add(lblMix);
            right.Controls.Add(srcBtns);
            right.Controls.Add(lvSources);
            right.Controls.Add(lblSrc);

            // ---- canvas ----
            canvas = new CanvasView { Dock = DockStyle.Fill, CanvasW = settings.CanvasW, CanvasH = settings.CanvasH, Sources = settings.Sources };
            canvas.Message = "Premi ▶ Anteprima";
            canvas.SelectionChanged += s => { SyncListSelection(s); UpdateSourceButtons(); };
            canvas.TransformChanged += (s, final) =>
            {
                engine.ApplyTransform(s);
                if (final) { settings.Save(); if (!engine.LiveControl) RestartIfRunning(); }
            };
            canvas.OpenProperties += s => EditSource(s);
            canvas.RemoveRequested += s => RemoveSource(s);

            Controls.Add(canvas);
            Controls.Add(right);
            Controls.Add(bottom);
            Controls.Add(status);
            Controls.Add(top);
        }

        static Button Btn(string text, string tag, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(8, 0, 8, 0), Tag = tag, Margin = new Padding(0, 4, 4, 4) };
            b.Click += click; return b;
        }
        static Button SmallBtn(string text, string tip, EventHandler click)
        {
            var b = new Button { Text = text, Width = 36, Height = 30, Margin = new Padding(0, 0, 4, 0) };
            b.Click += click;
            new ToolTip().SetToolTip(b, tip);
            return b;
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
            mixer.Controls.Clear();
            int y = 4;
            foreach (var s in settings.Sources.Where(x => x.HasAudio))
            {
                var row = new Panel { Left = 0, Top = y, Width = mixer.Width - 24, Height = 56, Tag = "panel", Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
                var name = new Label { Text = s.Name, Left = 0, Top = 0, AutoSize = true };
                var val = new Label { Text = s.VolumeDb + " dB", Left = row.Width - 60, Top = 0, Width = 60, TextAlign = ContentAlignment.TopRight, Tag = "muted", Anchor = AnchorStyles.Top | AnchorStyles.Right };
                var tb = new TrackBar { Left = 0, Top = 18, Width = row.Width - 60, Height = 30, Minimum = -60, Maximum = 12, Value = Math.Clamp((int)Math.Round(s.VolumeDb), -60, 12), TickStyle = TickStyle.None, AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Tag = s };
                var mute = new CheckBox { Text = "Muto", Left = row.Width - 56, Top = 22, AutoSize = true, Checked = s.Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right, Tag = s };
                tb.ValueChanged += (o, e) => { s.VolumeDb = tb.Value; val.Text = tb.Value + " dB"; engine.ApplyVolume(s); };
                tb.MouseUp += (o, e) => { settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                mute.CheckedChanged += (o, e) => { s.Muted = mute.Checked; engine.ApplyVolume(s); settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                row.Controls.AddRange(new Control[] { name, val, tb, mute });
                mixer.Controls.Add(row);
                y += 60;
            }
            if (mixer.Controls.Count == 0)
                mixer.Controls.Add(new Label { Text = "Nessuna sorgente audio", Left = 0, Top = 6, AutoSize = true, Tag = "muted" });
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
            lblRec.Text = "Chiusura file…"; lblRec.ForeColor = Theme.Fore;

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
                    if (frames == 0 && code != 0) canvas.Message = "ffmpeg non è partito: apri il Log per l'errore";
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
                lblRec.ForeColor = Theme.Rec;
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
                lblRec.ForeColor = Theme.Fore;
                lblRec.Text = engine.IsRunning ? "Anteprima" : "";
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
