using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VHSCapture
{
    public class MainForm : Form
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly CaptureEngine engine = new CaptureEngine();

        Panel top, right, bottom;
        PictureBox preview;
        Button btnPreview, btnRec, btnStop, btnSettings, btnFolder, btnTheme, btnLog;
        TextBox txtName, txtLog;
        Label lblStatus, lblRec, lblNoSignal;
        VuMeter vu;
        System.Windows.Forms.Timer timer;

        DateTime recStart;
        string recFile;          // file effettivamente scritto da ffmpeg (mkv o mp4)
        string finalFile;        // mp4 finale
        bool finalizing;
        int frames;
        DateTime lastFrameAt = DateTime.MinValue;

        public MainForm()
        {
            Text = "VHSCapture";
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(860, 560);
            ClientSize = new Size(settings.WindowW, settings.WindowH);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Theme.Apply(this, settings.DarkTheme);

            engine.FrameReady += OnFrame;
            engine.AudioLevel += db => { if (IsHandleCreated) BeginInvoke(new Action(() => vu.SetLevel(db))); };
            engine.Log += AppendLog;
            engine.Exited += OnEngineExited;

            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (o, e) => UpdateStatus();
            timer.Start();

            Load += (o, e) =>
            {
                if (!FFmpeg.Exists)
                {
                    MessageBox.Show(this, "ffmpeg.exe non trovato accanto a VHSCapture.exe.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (string.IsNullOrEmpty(settings.VideoDevice)) OpenSettings();
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
            // barra superiore
            top = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 10, 10, 10), Tag = "panel" };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false };
            btnPreview = Btn("▶  Anteprima", null, (o, e) => StartPreview());
            btnRec = Btn("⏺  Registra", "rec", (o, e) => StartRecording());
            btnStop = Btn("⏹  Stop", null, (o, e) => StopRecording(true));
            btnStop.Enabled = false;
            var lblName = new Label { Text = "Nome:", AutoSize = true, Margin = new Padding(18, 8, 4, 0) };
            txtName = new TextBox { Width = 220, Margin = new Padding(0, 4, 0, 0) };
            txtName.PlaceholderText = "es. Rossi_matrimonio_1994";
            btnSettings = Btn("⚙  Impostazioni", null, (o, e) => OpenSettings());
            btnSettings.Margin = new Padding(18, 4, 4, 4);
            btnFolder = Btn("📁  Apri cartella", null, (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Btn("◐", null, (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); });
            btnTheme.Width = 36;
            btnLog = Btn("Log", null, (o, e) => { bottom.Visible = !bottom.Visible; });
            btnLog.Width = 50;
            flow.Controls.AddRange(new Control[] { btnPreview, btnRec, btnStop, lblName, txtName, btnSettings, btnFolder, btnTheme, btnLog });
            top.Controls.Add(flow);

            // log in basso
            bottom = new Panel { Dock = DockStyle.Bottom, Height = 110, Padding = new Padding(10, 0, 10, 8), Visible = settings.ShowLog, Tag = "panel" };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), WordWrap = false };
            bottom.Controls.Add(txtLog);

            // barra di stato
            var status = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(12, 0, 12, 0), Tag = "panel" };
            lblRec = new Label { AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10f), Padding = new Padding(0, 8, 0, 0) };
            lblStatus = new Label { AutoSize = true, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, Tag = "muted", Padding = new Padding(0, 8, 0, 0) };
            status.Controls.Add(lblStatus);
            status.Controls.Add(lblRec);

            // VU a destra
            right = new Panel { Dock = DockStyle.Right, Width = 60, Padding = new Padding(10, 10, 10, 10), Tag = "panel" };
            vu = new VuMeter { Dock = DockStyle.Fill };
            var lblVu = new Label { Text = "AUDIO", Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Height = 22, Tag = "muted", Font = new Font("Segoe UI", 7.5f) };
            right.Controls.Add(vu);
            right.Controls.Add(lblVu);

            // anteprima
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            lblNoSignal = new Label { Text = "Nessuna anteprima\nPremi ▶ Anteprima o controlla le impostazioni", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray, BackColor = Color.Black, Font = new Font("Segoe UI", 12f) };
            preview.Controls.Add(lblNoSignal);

            Controls.Add(preview);
            Controls.Add(right);
            Controls.Add(bottom);
            Controls.Add(status);
            Controls.Add(top);
        }

        static Button Btn(string text, string tag, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 34, Padding = new Padding(8, 0, 8, 0), Tag = tag, Margin = new Padding(0, 4, 4, 4) };
            b.MinimumSize = new Size(0, 34);
            b.Click += click;
            return b;
        }

        // ---------------- azioni ----------------

        void StartPreview()
        {
            if (!FFmpeg.Exists || string.IsNullOrEmpty(settings.VideoDevice)) return;
            if (engine.IsRecording) return;
            frames = 0;
            vu.Reset();
            try { engine.Start(settings, null); }
            catch (Exception ex) { AppendLog("Errore avvio: " + ex.Message); }
            SetButtons();
        }

        void StartRecording()
        {
            if (!FFmpeg.Exists || string.IsNullOrEmpty(settings.VideoDevice)) { OpenSettings(); return; }
            if (engine.IsRecording || finalizing) return;

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

            frames = 0;
            vu.Reset();
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
            lblRec.Text = "Chiusura file…";
            lblRec.ForeColor = Theme.Fore;

            string written = recFile, final = finalFile;
            await Task.Run(() => engine.Stop());

            if (settings.SafeRecording && !string.Equals(written, final, StringComparison.OrdinalIgnoreCase))
            {
                lblRec.Text = "Conversione in MP4 (senza ricodifica)…";
                bool ok = await Task.Run(() => FFmpeg.RemuxToMp4(written, final, AppendLog));
                if (ok)
                {
                    try { File.Delete(written); } catch { }
                    AppendLog("Salvato: " + final);
                }
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
            if (engine.IsRecording) { MessageBox.Show(this, "Ferma la registrazione prima di cambiare le impostazioni.", "VHSCapture"); return; }
            engine.Stop();
            ClearPreview();
            using (var f = new SettingsForm(settings))
                f.ShowDialog(this);
            Theme.Apply(this, settings.DarkTheme);
            if (!string.IsNullOrEmpty(settings.VideoDevice)) StartPreview();
            else SetButtons();
        }

        void SetButtons()
        {
            bool rec = engine.IsRecording;
            btnPreview.Enabled = !rec && !finalizing;
            btnRec.Enabled = !rec && !finalizing;
            btnStop.Enabled = rec && !finalizing;
            btnSettings.Enabled = !rec && !finalizing;
            txtName.Enabled = !rec;
        }

        // ---------------- eventi engine ----------------

        void OnFrame(Bitmap bmp)
        {
            if (!IsHandleCreated || IsDisposed) { bmp.Dispose(); return; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    var old = preview.Image;
                    preview.Image = bmp;
                    old?.Dispose();
                    frames++;
                    lastFrameAt = DateTime.Now;
                    if (lblNoSignal.Visible) lblNoSignal.Visible = false;
                }));
            }
            catch { bmp.Dispose(); }
        }

        void OnEngineExited(int code)
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (code != 0 && code != 255 && !finalizing)
                    AppendLog($"ffmpeg terminato con codice {code}");
                if (!engine.IsRunning && !finalizing)
                {
                    if (engine.IsRecording)
                    {
                        AppendLog("ATTENZIONE: ffmpeg è uscito durante la registrazione — chiudo il file");
                        StopRecording(false);
                    }
                    ClearPreview();
                    SetButtons();
                }
            }));
        }

        void ClearPreview()
        {
            var old = preview.Image; preview.Image = null; old?.Dispose();
            lblNoSignal.Visible = true;
            vu.Reset();
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
            string dev = settings.VideoDevice ?? "";
            string inp = (settings.InputSize == "auto" ? "" : settings.InputSize) + (settings.InputFps == "auto" ? "" : " @" + settings.InputFps + "fps");
            string enc = settings.Encoder + " " + (settings.RateControl == "CRF" ? $"CRF {settings.Crf}" : $"{settings.RateControl} {settings.VideoBitrate} kbps");
            lblStatus.Text = engine.IsRunning ? $"{dev}  ·  {inp}  ·  {enc}  ·  {settings.ResolvedOutputFolder()}" : "Anteprima ferma";

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
