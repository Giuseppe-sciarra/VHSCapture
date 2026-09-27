using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>Misuratore orizzontale stereo come nel mixer di OBS: due barre L/R, scala -60..0 dB, RMS pieno, picco tenue, peak-hold.</summary>
    public class HMeter : Control
    {
        // valori mostrati (animati) e valori bersaglio (ultimi dati arrivati)
        readonly double[] rms = { -90, -90 }, peak = { -90, -90 }, hold = { -90, -90 };
        readonly double[] tRms = { -90, -90 }, tPeak = { -90, -90 };
        readonly DateTime[] holdAt = { DateTime.MinValue, DateTime.MinValue };
        DateTime lastData = DateTime.MinValue, lastSignal = DateTime.MinValue;
        public bool Muted { get; set; }
        /// <summary>true se negli ultimi 1,5 secondi è arrivato qualcosa sopra -55 dB.</summary>
        public bool HasSignal => (DateTime.Now - lastSignal).TotalSeconds < 1.5;

        // un solo timer per tutti i misuratori: animazione a ~30 fps indipendente dall'arrivo dei dati (come OBS)
        static readonly System.Collections.Generic.List<WeakReference<HMeter>> all = new System.Collections.Generic.List<WeakReference<HMeter>>();
        static System.Windows.Forms.Timer anim;
        static DateTime lastTick = DateTime.Now;

        public HMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 38;
            lock (all) all.Add(new WeakReference<HMeter>(this));
            if (anim == null)
            {
                anim = new System.Windows.Forms.Timer { Interval = 33 };
                anim.Tick += (o, e) => TickAll();
                anim.Start();
            }
        }

        static void TickAll()
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.2, (now - lastTick).TotalSeconds); lastTick = now;
            lock (all)
            {
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (!all[i].TryGetTarget(out var m) || m.IsDisposed) { all.RemoveAt(i); continue; }
                    if (m.Visible) m.Animate(dt, now);
                }
            }
        }

        void Animate(double dt, DateTime now)
        {
            // se non arrivano dati da un po' (pipeline ferma) il bersaglio torna a silenzio
            if ((now - lastData).TotalMilliseconds > 400) for (int c = 0; c < 2; c++) { tRms[c] = -90; tPeak[c] = -90; }
            const double decay = 23.5;   // dB/s, come il decadimento "veloce" dei misuratori di OBS
            bool changed = false;
            for (int c = 0; c < 2; c++)
            {
                double nr = tRms[c] >= rms[c] ? tRms[c] : Math.Max(tRms[c], rms[c] - decay * dt);
                double np = tPeak[c] >= peak[c] ? tPeak[c] : Math.Max(tPeak[c], peak[c] - decay * dt);
                if (Math.Abs(nr - rms[c]) > 0.05 || Math.Abs(np - peak[c]) > 0.05) changed = true;
                rms[c] = nr; peak[c] = np;
                if ((now - holdAt[c]).TotalSeconds > 1.5 && hold[c] > -90) { hold[c] = Math.Max(-90, hold[c] - decay * dt); changed = true; }
            }
            if (changed) Invalidate();
        }

        public void SetLevels(double rmsL, double peakL, double rmsR, double peakR)
        {
            if (rmsR <= -89 && peakR <= -89) { rmsR = rmsL; peakR = peakL; }   // sorgente mono → stessa barra su L e R
            lastData = DateTime.Now;
            tRms[0] = rmsL; tRms[1] = rmsR; tPeak[0] = peakL; tPeak[1] = peakR;
            for (int c = 0; c < 2; c++)
            {
                double p = tPeak[c];
                if (p >= hold[c]) { hold[c] = p; holdAt[c] = DateTime.Now; }
            }
            if (Math.Max(peakL, peakR) > -55) lastSignal = DateTime.Now;
        }

        public void Reset() { for (int i = 0; i < 2; i++) rms[i] = peak[i] = hold[i] = tRms[i] = tPeak[i] = -90; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            // stile OBS: zone sempre visibili in tenue, il livello le accende; linea bianca = picco trattenuto
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            int w = Width - 1, barH = 7, gap = 3, y0 = 1;
            double frac(double db) => Math.Clamp((db + 60.0) / 60.0, 0, 1);
            int xY = (int)(w * frac(-20)), xR = (int)(w * frac(-9));
            int alpha = Muted ? 110 : 255;

            Color G = Color.FromArgb(76, 204, 96), Y = Color.FromArgb(240, 200, 40), R = Color.FromArgb(235, 70, 60);
            int dimA = Theme.Dark ? 55 : 70;
            using var gDim = new SolidBrush(Color.FromArgb(dimA, G)); using var yDim = new SolidBrush(Color.FromArgb(dimA, Y)); using var rDim = new SolidBrush(Color.FromArgb(dimA, R));
            using var gOn = new SolidBrush(Color.FromArgb(alpha, G)); using var yOn = new SolidBrush(Color.FromArgb(alpha, Y)); using var rOn = new SolidBrush(Color.FromArgb(alpha, R));
            using var pkBr = new SolidBrush(Color.FromArgb(alpha * 55 / 100, Theme.Fore));
            using var holdPen = new Pen(Color.FromArgb(alpha, Theme.Dark ? Color.White : Color.Black), 2);

            for (int c = 0; c < 2; c++)
            {
                int y = y0 + c * (barH + gap);
                Bar(g, 0, y, w, barH, 1.0, xY, xR, gDim, yDim, rDim);          // zone di sfondo
                Bar(g, 0, y, w, barH, frac(rms[c]), xY, xR, gOn, yOn, rOn);     // livello (RMS)
                int px = (int)(w * frac(peak[c]));                              // picco istantaneo: tacca sottile
                if (peak[c] > -60) g.FillRectangle(pkBr, Math.Max(0, px - 1), y, 2, barH);
                if (hold[c] > -60) { int hx = (int)(w * frac(hold[c])); g.DrawLine(holdPen, hx, y - 1, hx, y + barH); }
            }

            // scala: tacche ogni 5 dB, numeri ogni 10 (come OBS, senza sovrapposizioni)
            int ty = y0 + 2 * (barH + gap);
            using var tick = new Pen(Theme.Muted);
            var f = ScaleFont;
            for (int db = -60; db <= 0; db += 5)
            {
                int tx = (int)(w * frac(db));
                g.DrawLine(tick, tx, ty, tx, ty + (db % 10 == 0 ? 4 : 2));
                if (db % 10 != 0 && db != -5) continue;
                string t = db.ToString();
                var sz = TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.NoPadding);
                int lx = Math.Clamp(tx - sz.Width / 2, 0, Width - sz.Width);
                TextRenderer.DrawText(g, t, f, new Point(lx, ty + 5), Theme.Muted, TextFormatFlags.NoPadding);
            }
        }

        static readonly Font ScaleFont = new Font("Segoe UI", 7f);

        static void Bar(Graphics g, int x0, int y, int w, int h, double fr, int xY, int xR, Brush gr, Brush ye, Brush re)
        {
            int xe = x0 + (int)(w * fr);
            if (xe <= x0) return;
            g.FillRectangle(gr, x0, y, Math.Min(xe, xY) - x0, h);
            if (xe > xY) g.FillRectangle(ye, xY, y, Math.Min(xe, xR) - xY, h);
            if (xe > xR) g.FillRectangle(re, xR, y, xe - xR, h);
        }
    }

    /// <summary>Una riga del mixer, come OBS: nome, dispositivo, dB, misuratore, fader, muto.</summary>
    public class MixerRow : Panel
    {
        public Source Src { get; }
        public HMeter Meter { get; }
        readonly Label lblVal, lblState;
        readonly SafeTrackBar fader;
        readonly RoundedButton btnMute;
        public event Action<Source> VolumeChanged, VolumeCommitted, MuteChanged;

        public MixerRow(Source s)
        {
            Src = s;
            Tag = "panel";
            Height = 112;
            Padding = new Padding(0, 0, 0, 8);

            var name = new Label { Text = s.Name, AutoSize = true, Left = 0, Top = 0, Font = new Font("Segoe UI Semibold", 9.5f) };
            lblVal = new Label { AutoSize = false, Width = 70, Height = 18, Top = 0, TextAlign = ContentAlignment.TopRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var dev = new Label { Text = "🎤 " + s.AudioDevice, AutoSize = false, Left = 0, Top = 19, Height = 16, Tag = "muted", Font = new Font("Segoe UI", 8f), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lblState = new Label { AutoSize = false, Width = 110, Height = 16, Top = 19, TextAlign = ContentAlignment.TopRight, Font = new Font("Segoe UI", 8f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Meter = new HMeter { Left = 0, Top = 40, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Muted = s.Muted };
            fader = new SafeTrackBar { Left = -6, Top = 80, Height = 28, Minimum = -60, Maximum = 12, TickStyle = TickStyle.None, AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Value = Math.Clamp((int)Math.Round(s.VolumeDb), -60, 12) };
            btnMute = Ui.IconBtn(s.Muted ? "🔇" : "🔊", "Muto", null);
            btnMute.Top = 78; btnMute.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            if (s.Muted) btnMute.Variant = "danger";

            fader.ValueChanged += (o, e) => { Src.VolumeDb = fader.Value; UpdateVal(); VolumeChanged?.Invoke(Src); };
            fader.MouseUp += (o, e) => VolumeCommitted?.Invoke(Src);
            fader.DoubleClick += (o, e) => { fader.Value = 0; VolumeCommitted?.Invoke(Src); };   // doppio clic = 0 dB
            btnMute.Click += (o, e) => { Src.Muted = !Src.Muted; SyncMute(); MuteChanged?.Invoke(Src); };

            Controls.AddRange(new Control[] { name, lblVal, dev, lblState, Meter, fader, btnMute });
            Resize += (o, e) => DoLayout();
            DoLayout(); UpdateVal();
        }

        void DoLayout()
        {
            int w = ClientSize.Width;
            lblVal.Left = w - lblVal.Width;
            lblState.Left = w - lblState.Width;
            foreach (Control c in Controls) if (c.Tag as string == "muted") c.Width = Math.Max(40, w - lblState.Width - 4);
            Meter.Width = w;
            btnMute.Left = w - btnMute.Width;
            fader.Width = Math.Max(60, w - btnMute.Width - 2);
        }

        void UpdateVal() => lblVal.Text = (Src.VolumeDb > 0 ? "+" : "") + Src.VolumeDb.ToString("0.0") + " dB";

        void SyncMute()
        {
            btnMute.Text = Src.Muted ? "🔇" : "🔊";
            btnMute.Variant = Src.Muted ? "danger" : "ghost";
            btnMute.Invalidate();
            Meter.Muted = Src.Muted; Meter.Invalidate();
        }

        public void SyncFromSource()
        {
            int v = Math.Clamp((int)Math.Round(Src.VolumeDb), -60, 12);
            if (fader.Value != v) fader.Value = v;
            SyncMute(); UpdateVal();
        }

        /// <summary>Stato del segnale sotto al nome: così si capisce subito se il microfono/grabber sta mandando audio.</summary>
        public void RefreshState(bool running)
        {
            if (!running) { lblState.Text = ""; return; }
            if (Src.Muted) { lblState.Text = "muto"; lblState.ForeColor = Theme.Rec; }
            else if (Meter.HasSignal) { lblState.Text = "● segnale"; lblState.ForeColor = Color.FromArgb(60, 190, 90); }
            else { lblState.Text = "○ nessun segnale"; lblState.ForeColor = Theme.Muted; }
        }
    }
}
