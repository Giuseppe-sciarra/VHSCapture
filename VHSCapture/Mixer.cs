using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>Misuratore orizzontale stereo come nel mixer di OBS: due barre L/R, scala -60..0 dB, RMS pieno, picco tenue, peak-hold.</summary>
    public class HMeter : Control
    {
        readonly double[] rms = { -90, -90 }, peak = { -90, -90 }, hold = { -90, -90 };
        readonly DateTime[] holdAt = { DateTime.MinValue, DateTime.MinValue };
        DateTime lastSet = DateTime.MinValue, lastSignal = DateTime.MinValue;
        public bool Muted { get; set; }
        /// <summary>true se negli ultimi 2 secondi è arrivato qualcosa sopra -55 dB.</summary>
        public bool HasSignal => (DateTime.Now - lastSignal).TotalSeconds < 2;

        public HMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 30;
        }

        public void SetLevels(double rmsL, double peakL, double rmsR, double peakR)
        {
            if (rmsR <= -89 && peakR <= -89) { rmsR = rmsL; peakR = peakL; }   // sorgente mono → stessa barra su L e R
            double dt = Math.Min(0.25, (DateTime.Now - lastSet).TotalSeconds); lastSet = DateTime.Now;
            double decay = 25 * dt;
            Upd(0, rmsL, peakL, decay); Upd(1, rmsR, peakR, decay);
            if (Math.Max(peakL, peakR) > -55) lastSignal = DateTime.Now;
            Invalidate();
        }

        void Upd(int c, double r, double p, double decay)
        {
            rms[c] = Math.Max(r, rms[c] - decay);
            peak[c] = Math.Max(p, peak[c] - decay);
            if (p >= hold[c] || (DateTime.Now - holdAt[c]).TotalSeconds > 1.5) { hold[c] = p; holdAt[c] = DateTime.Now; }
        }

        public void Reset() { for (int i = 0; i < 2; i++) rms[i] = peak[i] = hold[i] = -90; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Panel);
            int x0 = 0, w = Width - 1;
            int barH = 6, gap = 2, y0 = 1;
            double frac(double db) => Math.Clamp((db + 60.0) / 60.0, 0, 1);
            int xY = x0 + (int)(w * frac(-20)), xR = x0 + (int)(w * frac(-9));
            int a = Muted ? 90 : 255;

            using var bg = new SolidBrush(Theme.Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(222, 222, 228));
            using var green = new SolidBrush(Color.FromArgb(a, 60, 190, 90));
            using var yellow = new SolidBrush(Color.FromArgb(a, 240, 200, 40));
            using var red = new SolidBrush(Color.FromArgb(a, 230, 60, 60));
            using var gD = new SolidBrush(Color.FromArgb(a * 35 / 100, 60, 190, 90));
            using var yD = new SolidBrush(Color.FromArgb(a * 35 / 100, 240, 200, 40));
            using var rD = new SolidBrush(Color.FromArgb(a * 35 / 100, 230, 60, 60));
            using var holdPen = new Pen(Color.FromArgb(a, Theme.Fore), 2);

            for (int c = 0; c < 2; c++)
            {
                int y = y0 + c * (barH + gap);
                g.FillRectangle(bg, x0, y, w, barH);
                Bar(g, x0, y, w, barH, frac(peak[c]), xY, xR, gD, yD, rD);
                Bar(g, x0, y, w, barH, frac(rms[c]), xY, xR, green, yellow, red);
                if (hold[c] > -60) { int hx = x0 + (int)(w * frac(hold[c])); g.DrawLine(holdPen, hx, y, hx, y + barH); }
            }

            // scala in dB come OBS
            int ty = y0 + 2 * (barH + gap) + 1;
            using var tick = new Pen(Theme.Border);
            using var f = new Font("Segoe UI", 6.5f);
            foreach (int db in new[] { -60, -50, -40, -30, -20, -10, -5, 0 })
            {
                int tx = x0 + (int)(w * frac(db));
                g.DrawLine(tick, tx, ty, tx, ty + 3);
                string t = db.ToString();
                var sz = TextRenderer.MeasureText(t, f);
                int lx = Math.Clamp(tx - sz.Width / 2, 0, Width - sz.Width);
                TextRenderer.DrawText(g, t, f, new Point(lx, ty + 3), Theme.Muted);
            }
        }

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
            Height = 104;
            Padding = new Padding(0, 0, 0, 8);

            var name = new Label { Text = s.Name, AutoSize = true, Left = 0, Top = 0, Font = new Font("Segoe UI Semibold", 9.5f) };
            lblVal = new Label { AutoSize = false, Width = 70, Height = 18, Top = 0, TextAlign = ContentAlignment.TopRight, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var dev = new Label { Text = "🎤 " + s.AudioDevice, AutoSize = false, Left = 0, Top = 19, Height = 16, Tag = "muted", Font = new Font("Segoe UI", 8f), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lblState = new Label { AutoSize = false, Width = 110, Height = 16, Top = 19, TextAlign = ContentAlignment.TopRight, Font = new Font("Segoe UI", 8f), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Meter = new HMeter { Left = 0, Top = 38, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Muted = s.Muted };
            fader = new SafeTrackBar { Left = -6, Top = 70, Height = 28, Minimum = -60, Maximum = 12, TickStyle = TickStyle.None, AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Value = Math.Clamp((int)Math.Round(s.VolumeDb), -60, 12) };
            btnMute = Ui.IconBtn(s.Muted ? "🔇" : "🔊", "Muto", null);
            btnMute.Top = 68; btnMute.Anchor = AnchorStyles.Top | AnchorStyles.Right;
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
