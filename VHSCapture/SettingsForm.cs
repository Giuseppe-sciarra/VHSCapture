using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VHSCapture
{
    public class SettingsForm : Form
    {
        readonly AppSettings s;

        ComboBox cbVideo, cbAudio, cbSize, cbFps, cbEncoder, cbRc, cbPreset, cbOutSize;
        NumericUpDown nBitrate, nCrf, nAudioBr, nMaxMin, nRtBuf;
        CheckBox chkDeint, chkMono, chkSafe;
        TextBox txtFolder, txtPrefix;
        Label lblBitrate, lblCrf, lblPreset;

        public SettingsForm(AppSettings settings)
        {
            s = settings;
            Text = "Impostazioni";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 690);
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

            // ---- Dispositivi ----
            var gDev = Group("Dispositivi (adattatore USB)");
            var tDev = Grid(gDev, 3);
            cbVideo = Combo(); cbAudio = Combo();
            var btnRefresh = new Button { Text = "↻ Aggiorna", AutoSize = true };
            btnRefresh.Click += (o, e) => RefreshDevices(true);
            Row(tDev, "Video", cbVideo, btnRefresh);
            Row(tDev, "Audio", cbAudio, null);
            cbSize = Combo(); cbSize.DropDownStyle = ComboBoxStyle.DropDown;
            cbFps = Combo(); cbFps.DropDownStyle = ComboBoxStyle.DropDown;
            cbFps.Items.AddRange(new object[] { "auto", "25", "29.97", "30", "50", "59.94", "60" });
            Row(tDev, "Risoluzione ingresso", cbSize, null);
            Row(tDev, "Frame rate", cbFps, null);
            nRtBuf = Num(64, 4096, 64);
            Row(tDev, "Buffer cattura (MB)", nRtBuf, null);
            cbVideo.SelectedIndexChanged += (o, e) => RefreshSizes();
            root.Controls.Add(gDev);

            // ---- Video ----
            var gVid = Group("Video (come OBS)");
            var tVid = Grid(gVid, 3);
            cbEncoder = Combo();
            Row(tVid, "Encoder", cbEncoder, null);
            cbRc = Combo(); cbRc.Items.AddRange(new object[] { "CBR", "VBR", "CRF" });
            Row(tVid, "Controllo bitrate", cbRc, null);
            nBitrate = Num(500, 60000, 500);
            lblBitrate = Row(tVid, "Bitrate video (kbps)", nBitrate, Muted("VHS PAL: 6000–10000 va benissimo"));
            nCrf = Num(0, 51, 1);
            lblCrf = Row(tVid, "CRF / qualità", nCrf, Muted("più basso = meglio, 18 ottimo"));
            cbPreset = Combo();
            cbPreset.Items.AddRange(new object[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow" });
            lblPreset = Row(tVid, "Preset x264", cbPreset, Muted("veryfast se la CPU arranca"));
            cbOutSize = Combo(); cbOutSize.DropDownStyle = ComboBoxStyle.DropDown;
            cbOutSize.Items.AddRange(new object[] { "source", "720x576", "720x540", "768x576", "640x480", "1024x768", "1440x1080" });
            Row(tVid, "Risoluzione output", cbOutSize, Muted("source = come l'ingresso"));
            chkDeint = new CheckBox { Text = "Deinterlaccia (yadif) — consigliato per VHS", AutoSize = true };
            tVid.Controls.Add(chkDeint, 1, tVid.RowCount); tVid.SetColumnSpan(chkDeint, 2); tVid.RowCount++;
            cbRc.SelectedIndexChanged += (o, e) => UpdateEnabled();
            cbEncoder.SelectedIndexChanged += (o, e) => UpdateEnabled();
            root.Controls.Add(gVid);

            // ---- Audio ----
            var gAud = Group("Audio");
            var tAud = Grid(gAud, 3);
            nAudioBr = Num(64, 320, 32);
            Row(tAud, "Bitrate AAC (kbps)", nAudioBr, null);
            chkMono = new CheckBox { Text = "Mono (le VHS mono hanno lo stesso canale a destra e sinistra)", AutoSize = true };
            tAud.Controls.Add(chkMono, 1, tAud.RowCount); tAud.SetColumnSpan(chkMono, 2); tAud.RowCount++;
            root.Controls.Add(gAud);

            // ---- Output ----
            var gOut = Group("Salvataggio");
            var tOut = Grid(gOut, 3);
            txtFolder = new TextBox { Dock = DockStyle.Fill };
            var btnBrowse = new Button { Text = "Sfoglia…", AutoSize = true };
            btnBrowse.Click += (o, e) =>
            {
                using var d = new FolderBrowserDialog { SelectedPath = txtFolder.Text };
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            };
            Row(tOut, "Cartella (anche di rete)", txtFolder, btnBrowse);
            txtPrefix = new TextBox { Dock = DockStyle.Fill };
            Row(tOut, "Prefisso file", txtPrefix, Muted("→ Prefisso_2026-09-27_14-30-00.mp4"));
            nMaxMin = Num(0, 600, 5);
            Row(tOut, "Stop automatico (minuti)", nMaxMin, Muted("0 = illimitato. Es. 245 per una E-240"));
            chkSafe = new CheckBox { Text = "Modalità sicura (opzionale): registra in MKV e converte in MP4 alla fine. Di default OFF = MP4 diretto", AutoSize = true, MaximumSize = new Size(560, 0) };
            tOut.Controls.Add(chkSafe, 1, tOut.RowCount); tOut.SetColumnSpan(chkSafe, 2); tOut.RowCount++;
            root.Controls.Add(gOut);

            // ---- pulsanti ----
            var pBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
            var btnOk = new Button { Text = "Salva", Width = 100, Height = 30, Tag = "accent" };
            var btnCancel = new Button { Text = "Annulla", Width = 100, Height = 30 };
            btnOk.Click += (o, e) => { if (SaveValues()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += (o, e) => { DialogResult = DialogResult.Cancel; Close(); };
            pBtn.Controls.Add(btnOk); pBtn.Controls.Add(btnCancel);
            Controls.Add(pBtn);
            AcceptButton = btnOk; CancelButton = btnCancel;
        }

        // ---------- helpers UI ----------
        static GroupBox Group(string title) => new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10) };
        static TableLayoutPanel Grid(GroupBox g, int cols)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = cols, AutoSize = true, RowCount = 0 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(t);
            return t;
        }
        static Label Row(TableLayoutPanel t, string label, Control c, Control extra)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(l, 0, r);
            c.Margin = new Padding(0, 3, 6, 3);
            if (c is ComboBox || c is NumericUpDown) c.Width = 220; else c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 1, r);
            if (extra != null) { extra.Anchor = AnchorStyles.Left; extra.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(extra, 2, r); }
            t.RowCount = r + 1;
            return l;
        }
        static ComboBox Combo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        static NumericUpDown Num(int min, int max, int step) => new NumericUpDown { Minimum = min, Maximum = max, Increment = step };
        static Label Muted(string t) => new Label { Text = t, AutoSize = true, Tag = "muted", Margin = new Padding(0, 6, 0, 0) };

        // ---------- valori ----------
        void LoadValues()
        {
            RefreshDevices(false);
            Sel(cbVideo, s.VideoDevice);
            Sel(cbAudio, string.IsNullOrEmpty(s.AudioDevice) ? "(nessuno)" : s.AudioDevice);
            RefreshSizes();
            cbSize.Text = s.InputSize;
            cbFps.Text = s.InputFps;
            nRtBuf.Value = Math.Clamp(s.RtBufMB, 64, 4096);

            cbEncoder.Items.Clear();
            foreach (var e in FFmpeg.ListH264Encoders()) cbEncoder.Items.Add(EncLabel(e));
            Sel(cbEncoder, EncLabel(s.Encoder));
            if (cbEncoder.SelectedIndex < 0 && cbEncoder.Items.Count > 0) cbEncoder.SelectedIndex = 0;
            Sel(cbRc, s.RateControl);
            nBitrate.Value = Math.Clamp(s.VideoBitrate, 500, 60000);
            nCrf.Value = Math.Clamp(s.Crf, 0, 51);
            Sel(cbPreset, s.Preset);
            cbOutSize.Text = s.OutputSize;
            chkDeint.Checked = s.Deinterlace;

            nAudioBr.Value = Math.Clamp(s.AudioBitrate, 64, 320);
            chkMono.Checked = s.AudioMono;

            txtFolder.Text = s.OutputFolder;
            txtPrefix.Text = s.FilePrefix;
            nMaxMin.Value = Math.Clamp(s.MaxMinutes, 0, 600);
            chkSafe.Checked = s.SafeRecording;
            UpdateEnabled();
        }

        bool SaveValues()
        {
            if (cbVideo.SelectedItem == null || cbVideo.Text.StartsWith("("))
            {
                MessageBox.Show(this, "Seleziona un dispositivo video.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            s.VideoDevice = cbVideo.Text;
            s.AudioDevice = cbAudio.Text.StartsWith("(") ? "" : cbAudio.Text;
            s.InputSize = string.IsNullOrWhiteSpace(cbSize.Text) ? "auto" : cbSize.Text.Trim();
            s.InputFps = string.IsNullOrWhiteSpace(cbFps.Text) ? "auto" : cbFps.Text.Trim();
            s.RtBufMB = (int)nRtBuf.Value;

            s.Encoder = EncFromLabel(cbEncoder.Text);
            s.RateControl = cbRc.Text;
            s.VideoBitrate = (int)nBitrate.Value;
            s.Crf = (int)nCrf.Value;
            s.Preset = cbPreset.Text;
            s.OutputSize = string.IsNullOrWhiteSpace(cbOutSize.Text) ? "source" : cbOutSize.Text.Trim();
            s.Deinterlace = chkDeint.Checked;

            s.AudioBitrate = (int)nAudioBr.Value;
            s.AudioMono = chkMono.Checked;

            s.OutputFolder = txtFolder.Text.Trim();
            s.FilePrefix = string.IsNullOrWhiteSpace(txtPrefix.Text) ? "VHS" : txtPrefix.Text.Trim();
            s.MaxMinutes = (int)nMaxMin.Value;
            s.SafeRecording = chkSafe.Checked;
            s.Save();
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
            if (cbAudio.SelectedIndex < 0) cbAudio.SelectedIndex = audio.Count > 0 ? 1 : 0;
        }

        void RefreshSizes()
        {
            string cur = cbSize.Text;
            cbSize.Items.Clear();
            cbSize.Items.Add("auto");
            var found = new List<string>();
            if (cbVideo.SelectedItem != null && !cbVideo.Text.StartsWith("(")) found = FFmpeg.ListVideoSizes(cbVideo.Text);
            foreach (var d in new[] { "720x576", "720x480", "640x480", "1280x720", "1920x1080" }) if (!found.Contains(d)) found.Add(d);
            foreach (var d in found) cbSize.Items.Add(d);
            cbSize.Text = string.IsNullOrEmpty(cur) ? "720x576" : cur;
        }

        void UpdateEnabled()
        {
            string rc = cbRc.Text;
            bool x264 = EncFromLabel(cbEncoder.Text) == "libx264";
            nBitrate.Enabled = lblBitrate.Enabled = rc != "CRF";
            nCrf.Enabled = lblCrf.Enabled = rc == "CRF";
            cbPreset.Enabled = lblPreset.Enabled = x264;
        }

        static void Sel(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i].ToString(), v, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
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
