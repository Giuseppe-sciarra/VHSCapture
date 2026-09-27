using System;
using System.Drawing;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>Impostazioni di output: canvas, encoder, audio, salvataggio (come "Uscita" e "Video" di OBS).</summary>
    public class SettingsForm : Form
    {
        readonly AppSettings s;
        public bool CanvasChanged { get; private set; }

        ComboBox cbCanvas, cbFps, cbEncoder, cbRc, cbPreset;
        NumericUpDown nBitrate, nCrf, nAudioBr, nMaxMin;
        CheckBox chkMono, chkSafe, chkLive;
        TextBox txtFolder, txtPrefix;
        Label lblBitrate, lblCrf, lblPreset;

        public SettingsForm(AppSettings settings)
        {
            s = settings;
            Text = "Impostazioni di uscita";
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(680, 700); MinimumSize = new Size(560, 420);
            Font = new Font("Segoe UI", 9.5f);
            Build();
            LoadValues();
            Theme.Apply(this, s.DarkTheme);
        }

        void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---- Canvas ----
            var gCv = Group("Canvas / risoluzione di uscita");
            var tCv = Grid(gCv);
            cbCanvas = Combo(); cbCanvas.DropDownStyle = ComboBoxStyle.DropDown;
            cbCanvas.Items.AddRange(new object[] {
                // 16:9
                "3840x2160", "2560x1440", "1920x1080", "1600x900", "1280x720", "1024x576", "854x480",
                // 4:3
                "2048x1536", "1600x1200", "1440x1080", "1280x960", "1024x768", "960x720", "800x600", "768x576", "640x480",
                // SD nativo (anamorfico)
                "720x576", "720x480", "704x576", "704x480", "352x288", "352x240",
                // 16:10 / 21:9
                "1920x1200", "1680x1050", "1280x800", "2560x1080", "3440x1440" });
            Row(tCv, "Risoluzione canvas", cbCanvas, Muted("= risoluzione del file MP4 (o scrivi LxA)"));
            cbFps = Combo(); cbFps.DropDownStyle = ComboBoxStyle.DropDown;
            cbFps.Items.AddRange(new object[] { "23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "75", "90", "100", "120", "144" });
            Row(tCv, "Frame rate uscita", cbFps, Muted("PAL 25/50, NTSC 29.97/59.94, o scrivi a mano"));
            root.Controls.Add(gCv);

            // ---- Video ----
            var gVid = Group("Encoder video (come OBS)");
            var tVid = Grid(gVid);
            cbEncoder = Combo();
            Row(tVid, "Encoder", cbEncoder, Muted("solo quelli che funzionano su questo PC"));
            cbRc = Combo(); cbRc.Items.AddRange(new object[] { "CBR", "VBR", "CRF" });
            Row(tVid, "Controllo bitrate", cbRc, null);
            nBitrate = Num(500, 60000, 500);
            lblBitrate = Row(tVid, "Bitrate video (kbps)", nBitrate, Muted("1080p da VHS: 10000–15000"));
            nCrf = Num(0, 51, 1);
            lblCrf = Row(tVid, "CRF / qualità", nCrf, Muted("più basso = meglio, 18 ottimo"));
            cbPreset = Combo();
            cbPreset.Items.AddRange(new object[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" });
            lblPreset = Row(tVid, "Preset x264", cbPreset, Muted("veryfast se la CPU arranca"));
            cbRc.SelectedIndexChanged += (o, e) => UpdateEnabled();
            cbEncoder.SelectedIndexChanged += (o, e) => UpdateEnabled();
            root.Controls.Add(gVid);

            // ---- Audio ----
            var gAud = Group("Audio");
            var tAud = Grid(gAud);
            nAudioBr = Num(64, 320, 32);
            Row(tAud, "Bitrate AAC (kbps)", nAudioBr, null);
            chkMono = new CheckBox { Text = "Esporta mono", AutoSize = true };
            tAud.Controls.Add(chkMono, 1, tAud.RowCount); tAud.SetColumnSpan(chkMono, 2); tAud.RowCount++;
            root.Controls.Add(gAud);

            // ---- Output ----
            var gOut = Group("Salvataggio");
            var tOut = Grid(gOut);
            txtFolder = new TextBox { Dock = DockStyle.Fill };
            var btnBrowse = Ui.Btn("Sfoglia…", "ghost");
            btnBrowse.Click += (o, e) =>
            {
                using var d = new FolderBrowserDialog { SelectedPath = txtFolder.Text };
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            };
            Row(tOut, "Cartella (anche di rete)", txtFolder, btnBrowse);
            txtPrefix = new TextBox { Dock = DockStyle.Fill };
            Row(tOut, "Prefisso file", txtPrefix, Muted("→ Prefisso_Nome_2026-09-27_14-30-00.mp4"));
            nMaxMin = Num(0, 600, 5);
            Row(tOut, "Stop automatico (minuti)", nMaxMin, Muted("0 = illimitato. Es. 245 per una E-240"));
            chkSafe = new CheckBox { Text = "Modalità sicura (opzionale): registra in MKV e converte in MP4 alla fine. Di default OFF = MP4 diretto", AutoSize = true, MaximumSize = new Size(560, 0) };
            tOut.Controls.Add(chkSafe, 1, tOut.RowCount); tOut.SetColumnSpan(chkSafe, 2); tOut.RowCount++;
            root.Controls.Add(gOut);

            // ---- Avanzate ----
            var gAdv = Group("Avanzate");
            var tAdv = Grid(gAdv);
            chkLive = new CheckBox { Text = "Controllo live delle sorgenti (zmq): sposta/ridimensiona/colore senza riavviare l'anteprima", AutoSize = true, MaximumSize = new Size(560, 0) };
            tAdv.Controls.Add(chkLive, 1, tAdv.RowCount); tAdv.SetColumnSpan(chkLive, 2); tAdv.RowCount++;
            var lz = Muted(FFmpeg.HasZmq ? "ffmpeg con supporto zmq: OK" : "ffmpeg SENZA zmq: le modifiche riavviano l'anteprima");
            tAdv.Controls.Add(lz, 1, tAdv.RowCount); tAdv.SetColumnSpan(lz, 2); tAdv.RowCount++;
            root.Controls.Add(gAdv);

            var pBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(10) };
            var btnOk = Ui.Btn("Salva", "accent", null, 110);
            var btnCancel = Ui.Btn("Annulla", "normal", null, 110);
            btnOk.Click += (o, e) => { if (SaveValues()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += (o, e) => { DialogResult = DialogResult.Cancel; Close(); };
            pBtn.Controls.Add(btnOk); pBtn.Controls.Add(btnCancel);
            Controls.Add(pBtn);
            AcceptButton = btnOk; CancelButton = btnCancel;
        }

        static Card Group(string title) => new Card { HeaderText = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 30, 12, 10), Margin = new Padding(0, 0, 0, 10) };
        static TableLayoutPanel Grid(Card g)
        {
            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 0, Location = new Point(g.Padding.Left, g.Padding.Top), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            t.Width = Math.Max(200, g.ClientSize.Width - g.Padding.Horizontal);
            g.Resize += (o, e) => t.Width = Math.Max(200, g.ClientSize.Width - g.Padding.Horizontal);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(t); return t;
        }
        static Label Row(TableLayoutPanel t, string label, Control c, Control extra)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };
            int r = t.RowCount; t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, r);
            c.Margin = new Padding(0, 3, 6, 3);
            if (c is ComboBox || c is NumericUpDown) c.Width = 220; else c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 1, r);
            if (extra != null) { extra.Anchor = AnchorStyles.Left; extra.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(extra, 2, r); }
            t.RowCount = r + 1;
            return l;
        }
        static ComboBox Combo() => new SafeCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        static NumericUpDown Num(int min, int max, int step) => new SafeNumeric { Minimum = min, Maximum = max, Increment = step };
        static Label Muted(string t) => new Label { Text = t, AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0) };
        static void Sel(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i].ToString(), v, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
        }

        void LoadValues()
        {
            cbCanvas.Text = $"{s.CanvasW}x{s.CanvasH}";
            cbFps.Text = s.Fps;
            cbEncoder.Items.Clear();
            Cursor = Cursors.WaitCursor;
            foreach (var e in FFmpeg.ListWorkingH264Encoders()) cbEncoder.Items.Add(EncLabel(e));
            Cursor = Cursors.Default;
            Sel(cbEncoder, EncLabel(s.Encoder));
            if (cbEncoder.SelectedIndex < 0 && cbEncoder.Items.Count > 0) cbEncoder.SelectedIndex = 0;
            Sel(cbRc, s.RateControl);
            nBitrate.Value = Math.Clamp(s.VideoBitrate, 500, 60000);
            nCrf.Value = Math.Clamp(s.Crf, 0, 51);
            Sel(cbPreset, s.Preset);
            nAudioBr.Value = Math.Clamp(s.AudioBitrate, 64, 320);
            chkMono.Checked = s.AudioMono;
            txtFolder.Text = s.OutputFolder;
            txtPrefix.Text = s.FilePrefix;
            nMaxMin.Value = Math.Clamp(s.MaxMinutes, 0, 600);
            chkSafe.Checked = s.SafeRecording;
            chkLive.Checked = s.LiveControl;
            UpdateEnabled();
        }

        bool SaveValues()
        {
            var p = cbCanvas.Text.Trim().ToLowerInvariant().Split('x');
            if (p.Length != 2 || !int.TryParse(p[0], out int cw) || !int.TryParse(p[1], out int ch) || cw < 64 || ch < 64)
            { MessageBox.Show(this, "Risoluzione canvas non valida (es. 1920x1080).", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            cw -= cw % 2; ch -= ch % 2;
            CanvasChanged = cw != s.CanvasW || ch != s.CanvasH || cbFps.Text.Trim() != s.Fps;
            s.CanvasW = cw; s.CanvasH = ch;
            s.Fps = string.IsNullOrWhiteSpace(cbFps.Text) ? "25" : cbFps.Text.Trim();

            s.Encoder = EncFromLabel(cbEncoder.Text);
            s.RateControl = cbRc.Text;
            s.VideoBitrate = (int)nBitrate.Value;
            s.Crf = (int)nCrf.Value;
            s.Preset = cbPreset.Text;
            s.AudioBitrate = (int)nAudioBr.Value;
            s.AudioMono = chkMono.Checked;
            s.OutputFolder = txtFolder.Text.Trim();
            s.FilePrefix = string.IsNullOrWhiteSpace(txtPrefix.Text) ? "VHS" : txtPrefix.Text.Trim();
            s.MaxMinutes = (int)nMaxMin.Value;
            s.SafeRecording = chkSafe.Checked;
            s.LiveControl = chkLive.Checked;
            s.Save();
            return true;
        }

        void UpdateEnabled()
        {
            string rc = cbRc.Text;
            bool x264 = EncFromLabel(cbEncoder.Text) == "libx264";
            nBitrate.Enabled = lblBitrate.Enabled = rc != "CRF";
            nCrf.Enabled = lblCrf.Enabled = rc == "CRF";
            cbPreset.Enabled = lblPreset.Enabled = x264;
        }

        static string EncLabel(string e) => e switch
        {
            "h264_nvenc" => "NVIDIA NVENC (h264_nvenc)",
            "h264_qsv" => "Intel QuickSync (h264_qsv)",
            "h264_amf" => "AMD AMF (h264_amf)",
            _ => "Software x264 (libx264)",
        };
        static string EncFromLabel(string l)
        {
            if (l.Contains("nvenc")) return "h264_nvenc";
            if (l.Contains("qsv")) return "h264_qsv";
            if (l.Contains("amf")) return "h264_amf";
            return "libx264";
        }
    }
}
