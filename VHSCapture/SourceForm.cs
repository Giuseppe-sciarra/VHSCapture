using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>
    /// Proprietà di una sorgente, come in OBS: anteprima live in alto, sotto dispositivo/trasformazione/colore/audio.
    /// Le modifiche vengono applicate subito alla scena (live); Annulla ripristina lo stato iniziale.
    /// </summary>
    public class SourceForm : Form
    {
        public Source Result { get; private set; }
        readonly Source snapshot;          // stato iniziale (per Annulla)
        readonly Source work;              // copia su cui lavora il dialogo
        readonly AppSettings cfg;
        readonly Action<Source> onLive;          // posizione/colore/volume → applicati al volo
        readonly Action<Source> onStructural;    // dispositivo/risoluzione/fps/deinterlaccio → riavvio anteprima
        readonly Func<string, Bitmap> getPreview; // ritaglio della sorgente dall'anteprima corrente
        readonly Action<Action> withDeviceFree;
        readonly Action<string> log;
        readonly bool structuralLocked;

        PictureBox pv; Label pvMsg; DateTime lastPreviewOk = DateTime.MinValue; System.Windows.Forms.Timer pvTimer, structTimer;
        TextBox txtName;
        ComboBox cbVideo, cbAudio, cbSize, cbFps;
        NumericUpDown nRtBuf; ComboBox cbDeint, cbFormat, cbScale; NumericUpDown nCropL, nCropT, nCropR, nCropB, nAudioOff;
        Label lblInfo, lblModes;
        readonly Func<string, string> getInputInfo;
        // standard video (PAL/NTSC/Personalizzato)
        ComboBox cbStandard; Label lblStd; bool applyingStd;
        /// <summary>Frame rate della registrazione richiesto dallo standard scelto qui (null = nessuna richiesta).</summary>
        public string CanvasFpsRequest { get; private set; }
        TextBox txtImage;
        Panel colorSwatch;
        NumericUpDown nX, nY, nW, nH;
        TrackBar tBri, tCon, tSat, tGam, tHue; Label lBri, lCon, lSat, lGam, lHue;
        TrackBar tVol; Label lVol; CheckBox chkMute;
        bool loading = true;

        public SourceForm(Source src, AppSettings settings, bool lockStructural,
                          Action<Source> live, Action<Source> structural, Func<string, Bitmap> preview,
                          Action<Action> deviceFree, Action<string> logger, Func<string, string> inputInfo = null)
        {
            getInputInfo = inputInfo;
            snapshot = src.Clone(); work = src.Clone(); cfg = settings;
            onLive = live; onStructural = structural; getPreview = preview;
            withDeviceFree = deviceFree; log = logger; structuralLocked = lockStructural;

            Text = "Proprietà — " + src.Name;
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(760, 860); MinimumSize = new Size(520, 420);
            // all'apertura e a ogni ridimensionamento: testi a capo entro la finestra (niente più finestra da allargare)
            void FitAll(Control parent) { foreach (Control c in parent.Controls) { if (c is TableLayoutPanel tl && Equals(tl.Tag, "panel")) FitGrid(tl); if (c.HasChildren) FitAll(c); } }
            Shown += (o, e) => FitAll(this);
            Resize += (o, e) => FitAll(this);
            Build();
            LoadValues();
            loading = false;
            Theme.Apply(this, settings.DarkTheme);

            pvTimer = new System.Windows.Forms.Timer { Interval = 120 };
            pvTimer.Tick += (o, e) => UpdatePreview();
            pvTimer.Start();
            structTimer = new System.Windows.Forms.Timer { Interval = 600 };
            structTimer.Tick += (o, e) => { structTimer.Stop(); PullStructural(); structuralSent = true; onStructural?.Invoke(work); };

            if (cbStandard != null)
            {
                cbSize.TextChanged += (o, e) => DetectStandard();
                cbFps.TextChanged += (o, e) => DetectStandard();
                cbDeint.SelectedIndexChanged += (o, e) => DetectStandard();
                foreach (var n in new[] { nCropL, nCropT, nCropR, nCropB }) n.ValueChanged += (o, e) => DetectStandard();
                DetectStandard();
                if (structuralLocked) cbStandard.Enabled = false;   // in registrazione non si cambia
            }

            // se al caricamento qualcosa è stato corretto (es. risoluzione non supportata → auto) applicalo subito
            if (work.Type == SourceType.Capture && !structuralLocked && cbSize.Text != snapshot.InputSize) StructChanged();
        }

        bool structuralSent;

        /// <summary>Compila i campi con i valori dello standard (e chiede alla registrazione il suo frame rate).</summary>
        void ApplyStandard(VideoStandard v)
        {
            applyingStd = true;
            try
            {
                cbSize.Text = v.Size;
                cbFps.Text = v.InFps;
                cbDeint.SelectedIndex = 2;   // Yadif 2x
                nCropL.Value = 0; nCropT.Value = 0; nCropR.Value = 0; nCropB.Value = v.CropB;
                // proporzioni 4:3 al centro del canvas 1920x1080, bande ai lati
                work.InputSize = v.Size; PullCrop();
                work.FitTo(1920, 1080);
                PushTransform();
                CanvasFpsRequest = v.CanvasFps;
                PullCrop(); onLive?.Invoke(work);
                StructChanged();
            }
            finally { applyingStd = false; }
        }

        /// <summary>I valori attuali corrispondono a PAL, NTSC o sono stati personalizzati? Il menu lo mostra sempre.</summary>
        void DetectStandard()
        {
            if (cbStandard == null || applyingStd) return;
            var tmp = new Source
            {
                InputSize = string.IsNullOrWhiteSpace(cbSize.Text) ? "auto" : cbSize.Text.Trim(),
                InputFps = string.IsNullOrWhiteSpace(cbFps.Text) ? "auto" : cbFps.Text.Trim(),
                DeinterlaceMode = cbDeint.SelectedIndex switch { 1 => "yadif", 2 => "yadif2x", 3 => "bwdif", 4 => "bwdif2x", _ => "off" },
                CropL = (int)nCropL.Value, CropT = (int)nCropT.Value, CropR = (int)nCropR.Value, CropB = (int)nCropB.Value,
            };
            var d = VideoStandard.Detect(tmp);
            int idx = d == VideoStandard.PAL ? 0 : d == VideoStandard.NTSC ? 1 : 2;
            if (cbStandard.SelectedIndex != idx)
            {
                bool was = applyingStd; applyingStd = true;
                cbStandard.SelectedIndex = idx;
                applyingStd = was;
            }
            UpdateStdNote();
        }

        void UpdateStdNote()
        {
            if (lblStd == null) return;
            lblStd.Text = cbStandard.SelectedIndex switch
            {
                0 => "PAL  —  VHS, S-VHS, Hi8, Video8, MiniDV (Italia/Europa)\n" +
                     "Ingresso:          720 × 576  ·  25 fps (50 semiquadri)\n" +
                     "Deinterlaccio:   Yadif 2x  →  50 fotogrammi pieni\n" +
                     "Ritaglio:           8 righe in basso (striscia di rumore)\n" +
                     "Immagine:        4:3 al centro, bande nere ai lati\n" +
                     "Registrazione:  1920 × 1080  ·  50 fps",
                1 => "NTSC  —  cassette americane / giapponesi\n" +
                     "Ingresso:          720 × 480  ·  29,97 fps (59,94 semiquadri)\n" +
                     "Deinterlaccio:   Yadif 2x  →  59,94 fotogrammi pieni\n" +
                     "Ritaglio:           6 righe in basso\n" +
                     "Immagine:        4:3 al centro, bande nere ai lati\n" +
                     "Registrazione:  1920 × 1080  ·  59,94 fps\n" +
                     "⚠ Grabber su NTSC_M (Driver video…) e lettore che riproduca l'NTSC",
                _ => "Personalizzato  —  valori scelti a mano, lo standard non è applicato.\n" +
                     "Scegli PAL o NTSC per rimettere i valori standard.",
            };
            bool dark = Theme.Dark;
            lblStd.BackColor = cbStandard.SelectedIndex == 2
                ? (dark ? Color.FromArgb(60, 50, 30) : Color.FromArgb(255, 244, 220))     // personalizzato: tono ambra
                : (dark ? Color.FromArgb(32, 44, 60) : Color.FromArgb(230, 240, 252));    // standard: tono blu
            lblStd.ForeColor = dark ? Color.FromArgb(230, 230, 235) : Color.FromArgb(30, 30, 35);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            pvTimer?.Stop(); structTimer?.Stop();
            var old = pv.Image; pv.Image = null; old?.Dispose();
            if (DialogResult != DialogResult.OK)
            {
                // ripristina tutto com'era
                onLive?.Invoke(snapshot);
                if (structuralSent) onStructural?.Invoke(snapshot);
            }
            base.OnFormClosed(e);
        }

        // ================= UI =================
        void Build()
        {
            // pulsanti in basso
            var pBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 58, Padding = new Padding(12, 10, 12, 10) };
            var btnOk = Ui.Btn("OK", "accent", null, 110);
            var btnCancel = Ui.Btn("Annulla", "normal", null, 110);
            btnOk.Click += (o, e) => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += (o, e) => { DialogResult = DialogResult.Cancel; Close(); };
            pBtn.Controls.Add(btnOk); pBtn.Controls.Add(btnCancel);
            AcceptButton = btnOk; CancelButton = btnCancel;

            // anteprima in alto (fissa, non scorre)
            var pvCard = new Card { Dock = DockStyle.Top, Height = 280, Padding = new Padding(10), Radius = 10 };
            pv = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            pvMsg = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray, BackColor = Color.Black, Text = "In attesa dell'anteprima…", Tag = "keep" };
            pv.Controls.Add(pvMsg);
            pvCard.Controls.Add(pv);
            var pvWrap = new Panel { Dock = DockStyle.Top, Height = 292, Padding = new Padding(12, 12, 12, 0) };
            pvWrap.Controls.Add(pvCard);

            // contenuto scorrevole
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 10, 12, 4) };
            var stack = new List<Card>();

            // ---- sorgente ----
            var gGen = Group("Sorgente"); var tGen = Grid(gGen);
            txtName = new TextBox();
            Row(tGen, "Nome", txtName, null);
            if (work.Type == SourceType.Capture)
            {
                cbStandard = Combo();
                cbStandard.Items.AddRange(new object[] {
                    "PAL  —  720×576 @ 25  →  1080p @ 50",
                    "NTSC  —  720×480 @ 29,97  →  1080p @ 59,94",
                    "Personalizzato" });
                Row(tGen, "Standard video", cbStandard, null);
                cbStandard.Width = 360;
                // riquadro di riepilogo: a colpo d'occhio cosa fa lo standard scelto
                lblStd = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 2, 0, 8), Font = new Font("Segoe UI", 9.25f), Tag = "keep" };
                Full(tGen, lblStd);
                cbStandard.SelectedIndexChanged += (o, e) =>
                {
                    if (loading || applyingStd) return;
                    if (cbStandard.SelectedIndex == 0) ApplyStandard(VideoStandard.PAL);
                    else if (cbStandard.SelectedIndex == 1) ApplyStandard(VideoStandard.NTSC);
                    UpdateStdNote();
                };
                cbVideo = Combo(); cbAudio = Combo();
                var btnRefresh = Ui.Btn("↻ Aggiorna", "ghost");
                btnRefresh.Click += (o, e) => RefreshDevices(true);
                Row(tGen, "Dispositivo video", cbVideo, btnRefresh);
                Row(tGen, "Dispositivo audio", cbAudio, null);
                cbSize = Combo(); cbSize.DropDownStyle = ComboBoxStyle.DropDown;
                cbFps = Combo(); cbFps.DropDownStyle = ComboBoxStyle.DropDown;
                cbFps.Items.AddRange(new object[] { "auto", "5", "10", "15", "20", "23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "75", "90", "100", "120", "144" });
                Row(tGen, "Risoluzione ingresso", cbSize, Muted("720x576 per PAL, auto se non parte"));
                Row(tGen, "Frame rate ingresso", cbFps, null);
                cbFormat = Combo();
                cbFormat.Items.AddRange(new object[] { "Automatico", "MJPEG (per 1080p60 da grabber HDMI)", "YUY2 (non compresso)", "NV12" });
                Row(tGen, "Formato video", cbFormat, null);
                lblModes = Muted("", 560);
                Full(tGen, lblModes);
                cbDeint = Combo();
                cbDeint.Items.AddRange(new object[] { "Disattivato", "Yadif", "Yadif 2x (50p da VHS PAL, consigliato)", "Bwdif", "Bwdif 2x" });
                Row(tGen, "Deinterlacciamento", cbDeint, null);
                nRtBuf = Num(64, 4096, 64);
                Row(tGen, "Buffer cattura (MB)", nRtBuf, null);
                lblInfo = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 4), Font = new Font("Segoe UI Semibold", 9.5f), Text = "Ingresso effettivo: —" };
                Full(tGen, lblInfo);

                var bDrvVideo = Ui.Btn("Driver video…", "ghost");
                var bCross = Ui.Btn("Ingresso Composito / S-Video…", "ghost");
                var bDrvAudio = Ui.Btn("Driver audio…", "ghost");
                bDrvVideo.Click += (o, e) => OpenDriverPage("video");
                bCross.Click += (o, e) => OpenDriverPage("crossbar");
                bDrvAudio.Click += (o, e) => OpenDriverPage("audio");
                Full(tGen, ButtonRow(bDrvVideo, bCross, bDrvAudio));
                Full(tGen, Muted("Finestre del driver: standard video (PAL/NTSC), ingresso, regolazioni hardware.", 560));

                cbVideo.SelectedIndexChanged += (o, e) => { RefreshSizes(); StructChanged(); };
                cbAudio.SelectedIndexChanged += (o, e) => StructChanged();
                cbSize.TextChanged += (o, e) => StructChanged();
                cbFps.TextChanged += (o, e) => StructChanged();
                cbDeint.SelectedIndexChanged += (o, e) => StructChanged();
                cbFormat.SelectedIndexChanged += (o, e) => StructChanged();
                nRtBuf.ValueChanged += (o, e) => StructChanged();
                if (structuralLocked) foreach (Control c in new Control[] { cbVideo, cbAudio, cbSize, cbFps, nRtBuf, cbDeint, cbFormat, btnRefresh, bCross }) c.Enabled = false;
            }
            else if (work.Type == SourceType.Image)
            {
                txtImage = new TextBox();
                var btnImage = Ui.Btn("Sfoglia…", "ghost");
                btnImage.Click += (o, e) =>
                {
                    using var d = new OpenFileDialog { Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Tutti|*.*" };
                    if (d.ShowDialog(this) == DialogResult.OK) { txtImage.Text = d.FileName; StructChanged(); }
                };
                Row(tGen, "File immagine", txtImage, btnImage);
                if (structuralLocked) { txtImage.Enabled = false; btnImage.Enabled = false; }
            }
            else
            {
                colorSwatch = new Panel { Width = 60, Height = 26, BorderStyle = BorderStyle.FixedSingle };
                var btnColor = Ui.Btn("Scegli colore…", "ghost");
                btnColor.Click += (o, e) =>
                {
                    using var d = new ColorDialog { Color = colorSwatch.BackColor, FullOpen = true };
                    if (d.ShowDialog(this) == DialogResult.OK) { colorSwatch.BackColor = d.Color; StructChanged(); }
                };
                Row(tGen, "Colore", colorSwatch, btnColor);
                if (structuralLocked) btnColor.Enabled = false;
            }
            stack.Add(gGen);

            // ---- trasformazione ----
            var gTr = Group($"Posizione e dimensione  (canvas {cfg.CanvasW}×{cfg.CanvasH})"); var tTr = Grid(gTr);
            nX = Num(-8000, 8000, 1); nY = Num(-8000, 8000, 1); nW = Num(16, 8000, 1); nH = Num(16, 8000, 1);
            foreach (var n in new[] { nX, nY, nW, nH }) n.Width = 90;
            Row(tTr, "Posizione", Pair("X", nX, "Y", nY), null);
            Row(tTr, "Dimensione", Pair("L", nW, "A", nH), null);
            var bFit = Ui.Btn("Adatta allo schermo", "ghost");
            var bFill = Ui.Btn("Riempi (stira)", "ghost");
            var bCenter = Ui.Btn("Centra", "ghost");
            var bNat = Ui.Btn("Dimensione originale", "ghost");
            var b43 = Ui.Btn("Forza 4:3", "ghost");
            var b169 = Ui.Btn("Forza 16:9", "ghost");
            bFit.Click += (o, e) => { PullTransform(); work.FitTo(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bFill.Click += (o, e) => { PullTransform(); work.FillTo(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bCenter.Click += (o, e) => { PullTransform(); work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            bNat.Click += (o, e) => { var (w, h) = work.NaturalSize(); work.W = w; work.H = h; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            b43.Click += (o, e) => { PullTransform(); work.W = work.H * 4 / 3; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            b169.Click += (o, e) => { PullTransform(); work.W = work.H * 16 / 9; work.Center(cfg.CanvasW, cfg.CanvasH); PushTransform(); };
            Full(tTr, ButtonRow(bFit, bFill, bCenter));
            Full(tTr, ButtonRow(bNat, b43, b169));
            cbScale = Combo();
            cbScale.Items.AddRange(new object[] { "Bilineare (veloce)", "Bicubico", "Lanczos (più nitido)", "Area (per rimpicciolire)" });
            Row(tTr, "Filtro di scala", cbScale, null);
            cbScale.SelectedIndexChanged += (o, e) => StructChanged();
            if (structuralLocked) cbScale.Enabled = false;
            if (work.Type == SourceType.Capture)
            {
                nCropL = Num(0, 4000, 1); nCropT = Num(0, 4000, 1); nCropR = Num(0, 4000, 1); nCropB = Num(0, 4000, 1);
                foreach (var n in new[] { nCropL, nCropT, nCropR, nCropB }) n.Width = 80;
                Row(tTr, "Ritaglio sx / su", Pair("S", nCropL, "Su", nCropT), null);
                Row(tTr, "Ritaglio dx / giù", Pair("D", nCropR, "Giù", nCropB), null);
                var bVhs = Ui.Btn("Taglia rumore VHS in basso (8 px)", "ghost");
                bVhs.Click += (o, e) => { nCropB.Value = 8; };
                Full(tTr, ButtonRow(bVhs));
                Full(tTr, Muted("Suggerimento: sull'anteprima tieni premuto Alt e trascina una maniglia per ritagliare (come OBS).", 560));
                foreach (var n in new[] { nCropL, nCropT, nCropR, nCropB }) n.ValueChanged += (o, e) => { if (loading) return; PullCrop(); onLive?.Invoke(work); };
            }
            foreach (var n in new[] { nX, nY, nW, nH }) n.ValueChanged += (o, e) => { if (loading) return; PullTransform(); onLive?.Invoke(work); };
            stack.Add(gTr);

            // ---- colore ----
            var gCol = Group("Correzione colore"); var tCol = Grid(gCol);
            (tBri, lBri) = Slider(tCol, "Luminosità", -100, 100, 0);
            (tCon, lCon) = Slider(tCol, "Contrasto", 0, 300, 100);
            (tSat, lSat) = Slider(tCol, "Saturazione", 0, 300, 100);
            (tGam, lGam) = Slider(tCol, "Gamma", 10, 300, 100);
            (tHue, lHue) = Slider(tCol, "Tonalità (°)", -180, 180, 0);
            if (work.Type != SourceType.Capture) { tCon.Enabled = false; tGam.Enabled = false; }
            var bReset = Ui.Btn("Ripristina colori", "ghost");
            bReset.Click += (o, e) => { tBri.Value = 0; tCon.Value = 100; tSat.Value = 100; tGam.Value = 100; tHue.Value = 0; };
            Full(tCol, ButtonRow(bReset));
            foreach (var t in new[] { tBri, tCon, tSat, tGam, tHue }) t.ValueChanged += (o, e) => { UpdateColorLabels(); if (loading) return; PullColor(); onLive?.Invoke(work); };
            stack.Add(gCol);

            // ---- audio ----
            if (work.Type == SourceType.Capture)
            {
                var gAud = Group("Audio"); var tAud = Grid(gAud);
                (tVol, lVol) = Slider(tAud, "Volume (dB)", -60, 12, 0);
                chkMute = new CheckBox { Text = "Muto", AutoSize = true };
                Full(tAud, chkMute);
                nAudioOff = Num(-2000, 2000, 10);
                Row(tAud, "Ritardo audio (ms)", nAudioOff, Muted("positivo = audio più tardi"));
                nAudioOff.ValueChanged += (o, e) => StructChanged();
                if (structuralLocked) nAudioOff.Enabled = false;
                tVol.ValueChanged += (o, e) => { lVol.Text = tVol.Value + " dB"; if (loading) return; PullAudio(); onLive?.Invoke(work); };
                chkMute.CheckedChanged += (o, e) => { if (loading) return; PullAudio(); onLive?.Invoke(work); };
                stack.Add(gAud);
            }

            // Dock=Top impila al contrario: aggiungo in ordine inverso
            for (int i = stack.Count - 1; i >= 0; i--) scroll.Controls.Add(stack[i]);

            Controls.Add(scroll);
            Controls.Add(pvWrap);
            Controls.Add(pBtn);
        }

        // ================= helpers UI =================
        static Card Group(string t) => new Card { HeaderText = t, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 32, 14, 12), Margin = new Padding(0, 0, 0, 10) };

        static TableLayoutPanel Grid(Card g)
        {
            var t = new TableLayoutPanel { ColumnCount = 3, RowCount = 0, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(g.Padding.Left, g.Padding.Top), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Tag = "panel" };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(t);
            t.Width = Math.Max(300, g.ClientSize.Width - g.Padding.Horizontal);
            g.Resize += (o, e) => { t.Width = Math.Max(300, g.ClientSize.Width - g.Padding.Horizontal); FitGrid(t); };
            t.ControlAdded += (o, e) => { if (e.Control is ComboBox co) co.DropDown += (s2, e2) => FitDropDown(co); };
            t.SizeChanged += (o, e) => g.PerformLayout();
            return t;
        }

        // spazio sotto l'ultima riga e altezza card corretta: la card si adatta al TLP
        static void Row(TableLayoutPanel t, string label, Control c, Control extra)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 8) };
            int r = t.RowCount; t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, r);
            c.Margin = new Padding(0, 4, 8, 4);
            if (c is ComboBox) { c.Width = 260; c.Anchor = AnchorStyles.Left; }
            else if (c is NumericUpDown) { c.Width = 120; c.Anchor = AnchorStyles.Left; }
            else if (c is TextBox) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            else if (c is TrackBar) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            else c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c, 1, r);
            t.RowCount = r + 1;
            if (extra is Label hint)
            {
                // testo di aiuto sotto il campo: niente terza colonna che allarga la finestra
                hint.Margin = new Padding(0, 0, 0, 6); hint.Anchor = AnchorStyles.Left;
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.Controls.Add(hint, 1, r + 1); t.SetColumnSpan(hint, 2);
                t.RowCount = r + 2;
            }
            else if (extra != null) { extra.Anchor = AnchorStyles.Left; extra.Margin = new Padding(0, 4, 0, 4); t.Controls.Add(extra, 2, r); }
        }

        static void Full(TableLayoutPanel t, Control c)
        {
            int r = t.RowCount; t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            c.Margin = new Padding(0, 4, 0, 4);
            c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c, 1, r); t.SetColumnSpan(c, 2);
            t.RowCount = r + 1;
        }

        static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0), Tag = "panel" };
            foreach (var b in buttons) { b.Margin = new Padding(0, 0, 8, 0); f.Controls.Add(b); }
            return f;
        }

        static FlowLayoutPanel Pair(string l1, Control c1, string l2, Control c2)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Tag = "panel" };
            f.Controls.Add(new Label { Text = l1, AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            c1.Margin = new Padding(0, 3, 18, 3); f.Controls.Add(c1);
            f.Controls.Add(new Label { Text = l2, AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
            c2.Margin = new Padding(0, 3, 0, 3); f.Controls.Add(c2);
            return f;
        }

        static (TrackBar, Label) Slider(TableLayoutPanel t, string label, int min, int max, int val)
        {
            var tb = new SafeTrackBar { Minimum = min, Maximum = max, Value = val, TickStyle = TickStyle.None, AutoSize = false, Height = 30, Width = 320 };
            var lv = new Label { AutoSize = false, Width = 64, TextAlign = ContentAlignment.MiddleRight };
            Row(t, label, tb, lv);
            return (tb, lv);
        }

        static ComboBox Combo() => new SafeCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        static NumericUpDown Num(int min, int max, int step) => new SafeNumeric { Minimum = min, Maximum = max, Increment = step };
        /// <summary>Testi a capo entro la larghezza della colonna dei campi; riepilogo standard e menu non escono dalla finestra.</summary>
        static void FitGrid(TableLayoutPanel t)
        {
            int avail = Math.Max(220, t.Width - 170 - 16);
            t.SuspendLayout();
            foreach (Control c in t.Controls)
            {
                if (t.GetColumn(c) < 1) continue;
                if (c is Label lb && lb.AutoSize && (Equals(lb.Tag, "muted") || Equals(lb.Tag, "keep"))) lb.MaximumSize = new Size(avail, 0);
                else if (c is ComboBox co && co.Width > avail) co.Width = avail;
            }
            t.ResumeLayout(true);
        }

        static void FitDropDown(ComboBox co)
        {
            int w = co.Width;
            foreach (var it in co.Items) w = Math.Max(w, TextRenderer.MeasureText(it?.ToString() ?? "", co.Font).Width + 30);
            co.DropDownWidth = Math.Min(w, 900);
        }

        static Label Muted(string t, int maxW = 0) => new Label { Text = t, AutoSize = true, Tag = "muted", Margin = new Padding(0, 8, 0, 0), MaximumSize = new Size(maxW, 0) };
        static void Sel(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i].ToString(), v, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
        }

        // ================= anteprima =================
        void UpdatePreview()
        {
            if (lblInfo != null)
            {
                string info = getInputInfo?.Invoke(work.Id);
                lblInfo.Text = "Ingresso effettivo: " + (info ?? "—");
            }
            Bitmap b = null;
            try { b = getPreview?.Invoke(work.Id); } catch { }
            if (b == null)
            {
                // un attimo senza frame (riavvio dopo una modifica) è normale: tengo l'ultima immagine
                if ((DateTime.Now - lastPreviewOk).TotalSeconds < 2 && pv.Image != null) return;
                var o = pv.Image; pv.Image = null; o?.Dispose();
                pvMsg.Visible = true;
                pvMsg.Text = structTimer.Enabled ? "Applico le modifiche…" : "Nessuna anteprima (sorgente fuori dal canvas, nascosta o dispositivo non partito — vedi Log)";
                return;
            }
            lastPreviewOk = DateTime.Now;
            pvMsg.Visible = false;
            var old = pv.Image; pv.Image = b; old?.Dispose();
        }

        // ================= valori =================
        void LoadValues()
        {
            txtName.Text = work.Name;
            if (work.Type == SourceType.Capture)
            {
                RefreshDevices(false);
                Sel(cbVideo, work.VideoDevice);
                if (cbVideo.SelectedIndex < 0 && cbVideo.Items.Count > 0) cbVideo.SelectedIndex = 0;
                Sel(cbAudio, string.IsNullOrEmpty(work.AudioDevice) ? "(nessuno)" : work.AudioDevice);
                if (cbAudio.SelectedIndex < 0) cbAudio.SelectedIndex = 0;
                cbSize.Text = work.InputSize;
                RefreshSizes();
                cbFps.Text = work.InputFps;
                nRtBuf.Value = Math.Clamp(work.RtBufMB, 64, 4096);
                cbDeint.SelectedIndex = work.DeinterlaceMode switch { "yadif" => 1, "yadif2x" => 2, "bwdif" => 3, "bwdif2x" => 4, _ => 0 };
                cbFormat.SelectedIndex = work.VideoFormat switch { "mjpeg" => 1, "yuyv422" => 2, "nv12" => 3, _ => 0 };
                nCropL.Value = Math.Clamp(work.CropL, 0, 4000); nCropT.Value = Math.Clamp(work.CropT, 0, 4000);
                nCropR.Value = Math.Clamp(work.CropR, 0, 4000); nCropB.Value = Math.Clamp(work.CropB, 0, 4000);
                nAudioOff.Value = Math.Clamp(work.AudioOffsetMs, -2000, 2000);
                tVol.Value = Math.Clamp((int)Math.Round(work.VolumeDb), -60, 12); lVol.Text = tVol.Value + " dB";
                chkMute.Checked = work.Muted;
            }
            else if (work.Type == SourceType.Image) txtImage.Text = work.ImagePath;
            else { try { colorSwatch.BackColor = ColorTranslator.FromHtml(work.Color); } catch { } }

            cbScale.SelectedIndex = work.ScaleFilter switch { "bilinear" => 0, "lanczos" => 2, "area" => 3, _ => 1 };
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
            var ci = CultureInfo.InvariantCulture;
            lBri.Text = (tBri.Value / 100.0).ToString("0.00", ci);
            lCon.Text = (tCon.Value / 100.0).ToString("0.00", ci);
            lSat.Text = (tSat.Value / 100.0).ToString("0.00", ci);
            lGam.Text = (tGam.Value / 100.0).ToString("0.00", ci);
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
        void PullCrop()
        {
            if (nCropL == null) return;
            work.CropL = (int)nCropL.Value; work.CropT = (int)nCropT.Value; work.CropR = (int)nCropR.Value; work.CropB = (int)nCropB.Value;
        }

        void PullAudio() { if (tVol != null) { work.VolumeDb = tVol.Value; work.Muted = chkMute.Checked; } }

        void PullStructural()
        {
            work.ScaleFilter = cbScale.SelectedIndex switch { 0 => "bilinear", 2 => "lanczos", 3 => "area", _ => "bicubic" };
            if (work.Type == SourceType.Capture)
            {
                work.VideoDevice = cbVideo.Text.StartsWith("(") ? "" : cbVideo.Text;
                work.AudioDevice = cbAudio.Text.StartsWith("(") ? "" : cbAudio.Text;
                work.InputSize = string.IsNullOrWhiteSpace(cbSize.Text) ? "auto" : cbSize.Text.Trim();
                work.InputFps = string.IsNullOrWhiteSpace(cbFps.Text) ? "auto" : cbFps.Text.Trim();
                work.RtBufMB = (int)nRtBuf.Value;
                work.DeinterlaceMode = cbDeint.SelectedIndex switch { 1 => "yadif", 2 => "yadif2x", 3 => "bwdif", 4 => "bwdif2x", _ => "off" };
                work.VideoFormat = cbFormat.SelectedIndex switch { 1 => "mjpeg", 2 => "yuyv422", 3 => "nv12", _ => "auto" };
                work.AudioOffsetMs = (int)nAudioOff.Value;
            }
            else if (work.Type == SourceType.Image) { if (File.Exists(txtImage.Text)) work.ImagePath = txtImage.Text; }
            else work.Color = ColorTranslator.ToHtml(Color.FromArgb(colorSwatch.BackColor.R, colorSwatch.BackColor.G, colorSwatch.BackColor.B));
        }

        void StructChanged()
        {
            if (loading || structuralLocked) return;
            structTimer.Stop(); structTimer.Start();   // debounce: riavvia l'anteprima quando smetti di toccare
        }

        bool Commit()
        {
            work.Name = string.IsNullOrWhiteSpace(txtName.Text) ? work.Name : txtName.Text.Trim();
            if (work.Type == SourceType.Capture && (cbVideo.SelectedItem == null || cbVideo.Text.StartsWith("(")))
            { MessageBox.Show(this, "Seleziona un dispositivo video.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            if (work.Type == SourceType.Image && !File.Exists(txtImage.Text))
            { MessageBox.Show(this, "File immagine non trovato.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            PullStructural(); PullTransform(); PullColor(); PullAudio(); PullCrop();
            structTimer.Stop();
            Result = work;
            return true;
        }

        // ================= driver =================
        void OpenDriverPage(string kind)
        {
            bool audio = kind == "audio";
            string dev = audio ? (cbAudio.Text.StartsWith("(") ? "" : cbAudio.Text) : (cbVideo.Text.StartsWith("(") ? "" : cbVideo.Text);
            if (string.IsNullOrEmpty(dev)) { MessageBox.Show(this, "Seleziona prima il dispositivo.", "VHSCapture"); return; }
            if (kind != "crossbar" && DShowProps.ShowNative(Handle, dev, !audio, log)) return;
            if (structuralLocked) { MessageBox.Show(this, "Ferma la registrazione per aprire questa pagina.", "VHSCapture"); return; }
            if (withDeviceFree != null) withDeviceFree(() => DShowProps.ShowViaFFmpeg(dev, kind, log));
            else DShowProps.ShowViaFFmpeg(dev, kind, log);
        }

        void RefreshDevices(bool keep)
        {
            bool was = loading; loading = true;
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
            loading = was;
        }

        void RefreshSizes()
        {
            bool was = loading; loading = true;
            string cur = cbSize.Text;
            cbSize.Items.Clear(); cbSize.Items.Add("auto");
            var devSizes = new List<string>();
            var modes = new List<FFmpeg.DeviceMode>();
            if (cbVideo.SelectedItem != null && !cbVideo.Text.StartsWith("(")) modes = FFmpeg.ListModes(cbVideo.Text);
            foreach (var m in modes) if (!devSizes.Contains(m.Size)) devSizes.Add(m.Size);
            if (lblModes != null)
            {
                // TUTTE le risoluzioni, dalla più grande (prima ne mostravo 8 ordinate per fps e le 720×576 a 25 fps restavano fuori)
                int Px(string sz) { var q = sz.Split('x'); return q.Length == 2 && int.TryParse(q[0], out int a) && int.TryParse(q[1], out int b) ? a * b : 0; }
                var sizes = modes.GroupBy(m => m.Size).OrderByDescending(g => Px(g.Key))
                                 .Select(g => g.Key.Replace("x", "×") + (g.Any(m => m.Format == "mjpeg") ? " (MJPEG)" : ""));
                var fmts = string.Join("/", modes.Select(m => m.Format == "mjpeg" ? "MJPEG" : m.Format.ToUpperInvariant()).Distinct());
                lblModes.Text = modes.Count > 0 ? $"Il dispositivo supporta ({fmts}): " + string.Join(" · ", sizes) : "Modalità del dispositivo non lette (in uso o non dichiarate).";
            }
            var found = new List<string>(devSizes);
            bool deviceKnown = devSizes.Count > 0;
            foreach (var d in new[] { "720x576", "720x480", "704x576", "704x480", "768x576", "640x480", "352x288", "352x240", "1280x720", "1920x1080", "2560x1440", "3840x2160" }) if (!found.Contains(d)) found.Add(d);
            foreach (var d in found) cbSize.Items.Add(d);
            // se il dispositivo dichiara le sue risoluzioni e quella corrente non c'è, metti auto (evita che ffmpeg non parta)
            if (deviceKnown && cur != "auto" && !string.IsNullOrEmpty(cur) && !devSizes.Contains(cur)) cur = "auto";
            cbSize.Text = string.IsNullOrEmpty(cur) ? "auto" : cur;
            loading = was;
        }
    }
}
