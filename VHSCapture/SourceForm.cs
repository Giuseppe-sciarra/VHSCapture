using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>Proprietà di una sorgente: dispositivo/immagine/colore, trasformazione, correzione colore, audio. Le modifiche "live" vengono applicate subito.</summary>
    public class SourceForm : Form
    {
        public Source Result { get; private set; }     // copia modificata; null se annullato
        readonly Source snapshot;   // stato iniziale, per ripristino su Annulla
        readonly Source work;
        readonly AppSettings cfg;
        readonly Action<Source> onLive;               // callback per applicare al volo trasformazione/colore/volume
        readonly Action<Action> withDeviceFree;       // esegue un'azione con l'anteprima ferma (dispositivo libero)
        readonly Action<string> log;
        readonly bool structuralLocked;

        TextBox txtName;
        ComboBox cbVideo, cbAudio, cbSize, cbFps;
        NumericUpDown nRtBuf; CheckBox chkDeint;
        TextBox txtImage; Button btnImage;
        Panel colorSwatch; Button btnColor;
        NumericUpDown nX, nY, nW, nH;
        TrackBar tBri, tCon, tSat, tGam, tHue; Label lBri, lCon, lSat, lGam, lHue;
        TrackBar tVol; Label lVol; CheckBox chkMute;
        bool loading = true;

        public SourceForm(Source src, AppSettings settings, Action<Source> live, bool lockStructural, Action<Action> deviceFree, Action<string> logger)
        {
            snapshot = src.Clone(); work = src.Clone(); cfg = settings; onLive = live; structuralLocked = lockStructural;
            withDeviceFree = deviceFree; log = logger;
            Text = "Proprietà — " + src.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(660, 720);
            Build();
            LoadValues();
            loading = false;
            Theme.Apply(this, settings.DarkTheme);
        }

        void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---- generale ----
            var gGen = Group("Sorgente");
            var tGen = Grid(gGen);
            txtName = new TextBox();
            Row(tGen, "Nome", txtName, null);

            if (work.Type == SourceType.Capture)
            {
                cbVideo = Combo(); cbAudio = Combo();
                var btnRefresh = new Button { Text = "↻ Aggiorna", AutoSize = true };
                btnRefresh.Click += (o, e) => RefreshDevices(true);
                Row(tGen, "Dispositivo video", cbVideo, btnRefresh);
                Row(tGen, "Dispositivo audio", cbAudio, null);
                cbSize = Combo(); cbSize.DropDownStyle = ComboBoxStyle.DropDown;
                cbFps = Combo(); cbFps.DropDownStyle = ComboBoxStyle.DropDown;
                cbFps.Items.AddRange(new object[] { "auto", "5", "10", "15", "20", "23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "75", "90", "100", "120", "144" });
                Row(tGen, "Risoluzione ingresso", cbSize, Muted("720x576 per PAL"));
                Row(tGen, "Frame rate ingresso", cbFps, null);
                nRtBuf = Num(64, 4096, 64);
                Row(tGen, "Buffer cattura (MB)", nRtBuf, null);
                chkDeint = new CheckBox { Text = "Deinterlaccia (yadif) — consigliato per VHS", AutoSize = true };
                tGen.Controls.Add(chkDeint, 1, tGen.RowCount); tGen.SetColumnSpan(chkDeint, 2); tGen.RowCount++;
                cbVideo.SelectedIndexChanged += (o, e) => RefreshSizes();

                // pagine di configurazione del driver (come "Configura video" di OBS)
                var pDrv = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
                var bDrvVideo = new Button { Text = "Impostazioni driver video…", AutoSize = true };
                var bCross = new Button { Text = "Ingresso (Composito / S-Video)…", AutoSize = true };
                var bDrvAudio = new Button { Text = "Impostazioni driver audio…", AutoSize = true };
                bDrvVideo.Click += (o, e) => OpenDriverPage("video");
                bCross.Click += (o, e) => OpenDriverPage("crossbar");
                bDrvAudio.Click += (o, e) => OpenDriverPage("audio");
                pDrv.Controls.AddRange(new Control[] { bDrvVideo, bCross, bDrvAudio });
                tGen.Controls.Add(pDrv, 1, tGen.RowCount); tGen.SetColumnSpan(pDrv, 2); tGen.RowCount++;
                var lDrv = Muted("Le pagine sono del driver: standard video (PAL/NTSC), ingresso, luminosità hardware, ecc.");
                tGen.Controls.Add(lDrv, 1, tGen.RowCount); tGen.SetColumnSpan(lDrv, 2); tGen.RowCount++;

                if (structuralLocked) foreach (Control c in new Control[] { cbVideo, cbAudio, cbSize, cbFps, nRtBuf, chkDeint, btnRefresh, bCross }) c.Enabled = false;
            }
            else if (work.Type == SourceType.Image)
            {
                txtImage = new TextBox();
                btnImage = new Button { Text = "Sfoglia…", AutoSize = true };
                btnImage.Click += (o, e) =>
                {
                    using var d = new OpenFileDialog { Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Tutti|*.*" };
                    if (d.ShowDialog(this) == DialogResult.OK) txtImage.Text = d.FileName;
                };
                Row(tGen, "File immagine", txtImage, btnImage);
                if (structuralLocked) { txtImage.Enabled = false; btnImage.Enabled = false; }
            }
            else
            {
                colorSwatch = new Panel { Width = 60, Height = 24, BorderStyle = BorderStyle.FixedSingle };
                btnColor = new Button { Text = "Scegli colore…", AutoSize = true };
                btnColor.Click += (o, e) =>
                {
                    using var d = new ColorDialog { Color = colorSwatch.BackColor, FullOpen = true };
                    if (d.ShowDialog(this) == DialogResult.OK) colorSwatch.BackColor = d.Color;
                };
                Row(tGen, "Colore", colorSwatch, btnColor);
                if (structuralLocked) btnColor.Enabled = false;
            }
            root.Controls.Add(gGen);

            // ---- trasformazione ----
            var gTr = Group("Posizione e dimensione (pixel sul canvas " + cfg.CanvasW + "×" + cfg.CanvasH + ")");
            var tTr = Grid(gTr);
            nX = Num(-8000, 8000, 1); nY = Num(-8000, 8000, 1); nW = Num(16, 8000, 1); nH = Num(16, 8000, 1);
            var pXY = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            pXY.Controls.AddRange(new Control[] { Lab("X"), nX, Lab("Y"), nY });
            var pWH = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            pWH.Controls.AddRange(new Control[] { Lab("L"), nW, Lab("A"), nH });
            Row(tTr, "Posizione", pXY, null);
            Row(tTr, "Dimensione", pWH, null);
            var pBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            var bFit = new Button { Text = "Adatta allo schermo", AutoSize = true };
            var bFill = new Button { Text = "Riempi (stira)", AutoSize = true };
            var bCenter = new Button { Text = "Centra", AutoSize = true };
            var bNat = new Button { Text = "Dimensione originale", AutoSize = true };
            var b43 = new Button { Text = "Forza 4:3", AutoSize = true };
            var b169 = new Button { Text = "Forza 16:9", AutoSize = true };
            bFit.Click += (o, e) => { PullTransform(); work.FitTo(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bFill.Click += (o, e) => { PullTransform(); work.FillTo(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bCenter.Click += (o, e) => { PullTransform(); work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bNat.Click += (o, e) => { var (w, h) = work.NaturalSize(); work.W = w; work.H = h; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            b43.Click += (o, e) => { PullTransform(); work.W = work.H * 4 / 3; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            b169.Click += (o, e) => { PullTransform(); work.W = work.H * 16 / 9; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            pBtns.Controls.AddRange(new Control[] { bFit, bFill, bCenter, bNat, b43, b169 });
            tTr.Controls.Add(pBtns, 1, tTr.RowCount); tTr.SetColumnSpan(pBtns, 2); tTr.RowCount++;
            foreach (var n in new[] { nX, nY, nW, nH }) n.ValueChanged += (o, e) => { if (loading) return; PullTransform(); onLive?.Invoke(work); };
            root.Controls.Add(gTr);

            // ---- correzione colore ----
            var gCol = Group("Correzione colore");
            var tCol = Grid(gCol);
            (tBri, lBri) = Slider(tCol, "Luminosità", -100, 100, 0);
            (tCon, lCon) = Slider(tCol, "Contrasto", 0, 300, 100);
            (tSat, lSat) = Slider(tCol, "Saturazione", 0, 300, 100);
            (tGam, lGam) = Slider(tCol, "Gamma", 10, 300, 100);
            (tHue, lHue) = Slider(tCol, "Tonalità (°)", -180, 180, 0);
            if (work.Type != SourceType.Capture) { tCon.Enabled = false; tGam.Enabled = false; }
            var bReset = new Button { Text = "Ripristina colori", AutoSize = true };
            bReset.Click += (o, e) => { tBri.Value = 0; tCon.Value = 100; tSat.Value = 100; tGam.Value = 100; tHue.Value = 0; };
            tCol.Controls.Add(bReset, 1, tCol.RowCount); tCol.RowCount++;
            foreach (var t in new[] { tBri, tCon, tSat, tGam, tHue }) t.ValueChanged += (o, e) => { UpdateColorLabels(); if (loading) return; PullColor(); onLive?.Invoke(work); };
            root.Controls.Add(gCol);

            // ---- audio ----
            if (work.Type == SourceType.Capture)
            {
                var gAud = Group("Audio");
                var tAud = Grid(gAud);
                (tVol, lVol) = Slider(tAud, "Volume (dB)", -60, 12, 0);
                chkMute = new CheckBox { Text = "Muto", AutoSize = true };
                tAud.Controls.Add(chkMute, 1, tAud.RowCount); tAud.RowCount++;
                tVol.ValueChanged += (o, e) => { lVol.Text = tVol.Value + " dB"; if (loading) return; PullAudio(); onLive?.Invoke(work); };
                chkMute.CheckedChanged += (o, e) => { if (loading) return; PullAudio(); onLive?.Invoke(work); };
                root.Controls.Add(gAud);
            }

            // ---- pulsanti ----
            var pBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
            var btnOk = new Button { Text = "OK", Width = 100, Height = 30, Tag = "accent" };
            var btnCancel = new Button { Text = "Annulla", Width = 100, Height = 30 };
            btnOk.Click += (o, e) => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += (o, e) => { DialogResult = DialogResult.Cancel; Close(); };
            pBtn.Controls.Add(btnOk); pBtn.Controls.Add(btnCancel);
            Controls.Add(pBtn);
            AcceptButton = btnOk; CancelButton = btnCancel;

            FormClosed += (o, e) =>
            {
                // annullato: ripristina lo stato live originale
                if (DialogResult != DialogResult.OK) onLive?.Invoke(snapshot);
            };
        }

        void OpenDriverPage(string kind)
        {
            bool audio = kind == "audio";
            string dev = audio ? (cbAudio.Text.StartsWith("(") ? "" : cbAudio.Text) : (cbVideo.Text.StartsWith("(") ? "" : cbVideo.Text);
            if (string.IsNullOrEmpty(dev)) { MessageBox.Show(this, "Seleziona prima il dispositivo.", "VHSCapture"); return; }

            // 1) pagina nativa del driver (funziona anche con l'anteprima in corso)
            if (kind != "crossbar" && DShowProps.ShowNative(Handle, dev, !audio, log)) return;

            // 2) fallback: dialogo mostrato da ffmpeg, serve il dispositivo libero
            if (structuralLocked) { MessageBox.Show(this, "Ferma la registrazione per aprire questa pagina.", "VHSCapture"); return; }
            if (withDeviceFree != null) withDeviceFree(() => DShowProps.ShowViaFFmpeg(dev, kind, log));
            else DShowProps.ShowViaFFmpeg(dev, kind, log);
        }

        // ---------- helpers ----------
        static GroupBox Group(string t) => new GroupBox { Text = t, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10) };
        static TableLayoutPanel Grid(GroupBox g)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, RowCount = 0 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(t); return t;
        }
        static void Row(TableLayoutPanel t, string label, Control c, Control extra)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };
            int r = t.RowCount; t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, r);
            c.Margin = new Padding(0, 3, 6, 3);
            if (c is ComboBox || c is NumericUpDown) c.Width = 240;
            else if (c is TextBox) c.Dock = DockStyle.Fill;
            else c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c, 1, r);
            if (extra != null) { extra.Anchor = AnchorStyles.Left; extra.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(extra, 2, r); }
            t.RowCount = r + 1;
        }
        static (TrackBar, Label) Slider(TableLayoutPanel t, string label, int min, int max, int val)
        {
            var tb = new TrackBar { Minimum = min, Maximum = max, Value = val, TickStyle = TickStyle.None, Width = 300, AutoSize = false, Height = 28 };
            var lv = new Label { AutoSize = true, Width = 60 };
            Row(t, label, tb, lv);
            tb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            return (tb, lv);
        }
        static ComboBox Combo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        static NumericUpDown Num(int min, int max, int step) => new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Width = 80, Margin = new Padding(0, 0, 10, 0) };
        static Label Lab(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
        static Label Muted(string t) => new Label { Text = t, AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0) };
        static void Sel(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i].ToString(), v, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
        }

        // ---------- valori ----------
        void LoadValues()
        {
            txtName.Text = work.Name;
            if (work.Type == SourceType.Capture)
            {
                RefreshDevices(false);
                Sel(cbVideo, work.VideoDevice);
                Sel(cbAudio, string.IsNullOrEmpty(work.AudioDevice) ? "(nessuno)" : work.AudioDevice);
                RefreshSizes();
                cbSize.Text = work.InputSize; cbFps.Text = work.InputFps;
                nRtBuf.Value = Math.Clamp(work.RtBufMB, 64, 4096);
                chkDeint.Checked = work.Deinterlace;
                tVol.Value = Math.Clamp((int)Math.Round(work.VolumeDb), -60, 12); lVol.Text = tVol.Value + " dB";
                chkMute.Checked = work.Muted;
            }
            else if (work.Type == SourceType.Image) txtImage.Text = work.ImagePath;
            else { try { colorSwatch.BackColor = ColorTranslator.FromHtml(work.Color); } catch { } }

            PushTransform();
            tBri.Value = Math.Clamp((int)Math.Round(work.Brightness * 100), -100, 100);
            tCon.Value = Math.Clamp((int)Math.Round(work.Contrast * 100), 0, 300);
            tSat.Value = Math.Clamp((int)Math.Round(work.Saturation * 100), 0, 300);
            tGam.Value = Math.Clamp((int)Math.Round(work.Gamma * 100), 10, 300);
            tHue.Value = Math.Clamp((int)Math.Round(work.Hue), -180, 180);
            UpdateColorLabels();
        }

        void UpdateColorLabels()
        {
            lBri.Text = (tBri.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            lCon.Text = (tCon.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            lSat.Text = (tSat.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            lGam.Text = (tGam.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            lHue.Text = tHue.Value + "°";
        }

        void PushTransform()
        {
            bool was = loading; loading = true;
            nX.Value = Math.Clamp(work.X, -8000, 8000); nY.Value = Math.Clamp(work.Y, -8000, 8000);
            nW.Value = Math.Clamp(work.W, 16, 8000); nH.Value = Math.Clamp(work.H, 16, 8000);
            loading = was;
            if (!loading) onLive?.Invoke(work);
        }
        void PullTransform() { work.X = (int)nX.Value; work.Y = (int)nY.Value; work.W = (int)nW.Value; work.H = (int)nH.Value; }
        void PullColor()
        {
            work.Brightness = tBri.Value / 100.0; work.Contrast = tCon.Value / 100.0; work.Saturation = tSat.Value / 100.0;
            work.Gamma = tGam.Value / 100.0; work.Hue = tHue.Value;
        }
        void PullAudio() { work.VolumeDb = tVol.Value; work.Muted = chkMute.Checked; }

        bool Commit()
        {
            work.Name = string.IsNullOrWhiteSpace(txtName.Text) ? work.Name : txtName.Text.Trim();
            if (work.Type == SourceType.Capture)
            {
                if (cbVideo.SelectedItem == null || cbVideo.Text.StartsWith("("))
                { MessageBox.Show(this, "Seleziona un dispositivo video.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                work.VideoDevice = cbVideo.Text;
                work.AudioDevice = cbAudio.Text.StartsWith("(") ? "" : cbAudio.Text;
                work.InputSize = string.IsNullOrWhiteSpace(cbSize.Text) ? "auto" : cbSize.Text.Trim();
                work.InputFps = string.IsNullOrWhiteSpace(cbFps.Text) ? "auto" : cbFps.Text.Trim();
                work.RtBufMB = (int)nRtBuf.Value;
                work.Deinterlace = chkDeint.Checked;
                PullAudio();
            }
            else if (work.Type == SourceType.Image)
            {
                if (!File.Exists(txtImage.Text)) { MessageBox.Show(this, "File immagine non trovato.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
                work.ImagePath = txtImage.Text;
            }
            else work.Color = ColorTranslator.ToHtml(Color.FromArgb(colorSwatch.BackColor.R, colorSwatch.BackColor.G, colorSwatch.BackColor.B));
            PullTransform(); PullColor();
            Result = work;
            return true;
        }

        void RefreshDevices(bool keep)
        {
            string v = cbVideo.Text, a = cbAudio.Text;
            var (video, audio) = FFmpeg.ListDevices();
            cbVideo.Items.Clear(); cbAudio.Items.Clear();
            if (video.Count == 0) cbVideo.Items.Add("(nessun dispositivo video trovato)");
            foreach (var d in video) cbVideo.Items.Add(d);
            cbAudio.Items.Add("(nessuno)");
            foreach (var d in audio) cbAudio.Items.Add(d);
            if (keep) { Sel(cbVideo, v); Sel(cbAudio, a); }
            if (cbVideo.SelectedIndex < 0 && cbVideo.Items.Count > 0) cbVideo.SelectedIndex = 0;
            if (cbAudio.SelectedIndex < 0) cbAudio.SelectedIndex = 0;
        }

        void RefreshSizes()
        {
            string cur = cbSize.Text;
            cbSize.Items.Clear(); cbSize.Items.Add("auto");
            var found = new List<string>();
            if (cbVideo.SelectedItem != null && !cbVideo.Text.StartsWith("(")) found = FFmpeg.ListVideoSizes(cbVideo.Text);
            foreach (var d in new[] { "720x576", "720x480", "704x576", "704x480", "768x576", "640x480", "352x288", "352x240", "1280x720", "1920x1080", "2560x1440", "3840x2160" }) if (!found.Contains(d)) found.Add(d);
            foreach (var d in found) cbSize.Items.Add(d);
            cbSize.Text = string.IsNullOrEmpty(cur) ? "720x576" : cur;
        }
    }
}
