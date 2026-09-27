using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VHSCapture
{
    public static class Theme
    {
        public static bool Dark { get; private set; }

        public static Color Back => Dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 247);
        public static Color Panel => Dark ? Color.FromArgb(45, 45, 48) : Color.White;
        public static Color Fore => Dark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(30, 30, 30);
        public static Color Muted => Dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);
        public static Color Border => Dark ? Color.FromArgb(70, 70, 75) : Color.FromArgb(210, 210, 215);
        public static Color Accent => Color.FromArgb(0, 120, 212);
        public static Color Rec => Color.FromArgb(220, 50, 50);
        public static Color Input => Dark ? Color.FromArgb(37, 37, 40) : Color.White;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void Apply(Form f, bool dark)
        {
            Dark = dark;
            ApplyRec(f);
            try { int v = dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); } catch { }
            f.Invalidate(true);
        }

        /// <summary>Menu contestuali coerenti col tema.</summary>
        public static void StyleMenu(ToolStrip m)
        {
            m.RenderMode = ToolStripRenderMode.System;
            m.BackColor = Panel; m.ForeColor = Fore;
            m.Font = new Font("Segoe UI", 9.5f);
            foreach (ToolStripItem it in m.Items) { it.BackColor = Panel; it.ForeColor = Fore; it.Padding = new Padding(4, 3, 4, 3); }
        }

        static bool InCard(Control c) { for (var p = c.Parent; p != null; p = p.Parent) { if (p is Card) return true; if (p is Form) return false; } return false; }

        static void ApplyRec(Control c)
        {
            switch (c)
            {
                case Form _:
                    c.BackColor = Back; c.ForeColor = Fore; break;
                case RoundedButton rb:
                    rb.Invalidate(); break;
                case Card cd:
                    cd.BackColor = Panel; cd.ForeColor = Fore; cd.Invalidate(); break;
                case Pill pl:
                    pl.ForeColor = Fore; pl.Invalidate(); break;
                case SplitContainer sc:
                    sc.BackColor = Back; sc.Panel1.BackColor = Back; sc.Panel2.BackColor = Back; break;
                case Button b:
                    if (b.Tag as string == "accent") { b.BackColor = Accent; b.ForeColor = Color.White; }
                    else if (b.Tag as string == "rec") { b.BackColor = Rec; b.ForeColor = Color.White; }
                    else { b.BackColor = Panel; b.ForeColor = Fore; }
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(b.BackColor, Dark ? 0.15f : -0.05f);
                    b.UseVisualStyleBackColor = false;
                    break;
                case TextBox t:
                    t.BackColor = Input; t.ForeColor = Fore; t.BorderStyle = BorderStyle.FixedSingle; break;
                case NumericUpDown n:
                    n.BackColor = Input; n.ForeColor = Fore; n.BorderStyle = BorderStyle.FixedSingle; break;
                case ComboBox cb:
                    cb.BackColor = Input; cb.ForeColor = Fore; cb.FlatStyle = FlatStyle.Flat; break;
                case CheckBox ch:
                    ch.BackColor = Color.Transparent; ch.ForeColor = Fore; break;
                case Label l:
                    l.BackColor = Color.Transparent; l.ForeColor = (l.Tag as string == "muted") ? Muted : Fore; break;
                case GroupBox g:
                    g.BackColor = Panel; g.ForeColor = Fore; break;
                case PictureBox _:
                    c.BackColor = Color.Black; break;
                case ListView lv:
                    lv.BackColor = Input; lv.ForeColor = Fore; break; // NON toccare BorderStyle: ricrea l'handle e perde le spunte
                case TrackBar tb:
                    tb.BackColor = (tb.Parent?.Tag as string == "panel" || InCard(tb)) ? Panel : Back; break;
                case VuMeter _:
                case CanvasView _:
                case SourceList _:
                case HMeter _:
                    break;
                default:
                    c.BackColor = (c.Tag as string == "panel" || InCard(c)) ? Panel : Back; c.ForeColor = Fore; break;
            }
            foreach (Control ch in c.Controls) ApplyRec(ch);
        }
    }

    /// <summary>VU meter stereo (L/R) come il mixer di OBS: RMS pieno, picco sottile, peak-hold, decadimento morbido, scala -60..0 dB.</summary>
    public class VuMeter : Control
    {
        readonly double[] rms = { -90, -90 }, peak = { -90, -90 }, hold = { -90, -90 };
        readonly DateTime[] holdAt = { DateTime.MinValue, DateTime.MinValue };
        DateTime lastSet = DateTime.MinValue;

        public VuMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 44;
        }

        public void SetLevels(double rmsL, double peakL, double rmsR, double peakR)
        {
            if (rmsR <= -89 && peakR <= -89) { rmsR = rmsL; peakR = peakL; }   // sorgente mono
            double dt = Math.Min(0.2, (DateTime.Now - lastSet).TotalSeconds); lastSet = DateTime.Now;
            double decay = 30 * dt;   // 30 dB/s come OBS
            Upd(0, rmsL, peakL, decay); Upd(1, rmsR, peakR, decay);
            Invalidate();
        }

        void Upd(int c, double r, double p, double decay)
        {
            rms[c] = Math.Max(r, rms[c] - decay);
            peak[c] = Math.Max(p, peak[c] - decay);
            if (p >= hold[c] || (DateTime.Now - holdAt[c]).TotalSeconds > 1.5) { hold[c] = p; holdAt[c] = DateTime.Now; }
        }

        public void SetLevel(double db) => SetLevels(db, db, db, db);

        public void Reset() { for (int i = 0; i < 2; i++) { rms[i] = peak[i] = hold[i] = -90; } Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            int top = 4, bottom = Height - 18, h = bottom - top;
            if (h < 20) return;
            int bw = Math.Max(6, (Width - 14) / 2);
            double frac(double db) => Math.Clamp((db + 60.0) / 60.0, 0, 1);

            using var bg = new SolidBrush(Theme.Dark ? Color.FromArgb(30, 30, 32) : Color.FromArgb(225, 225, 230));
            using var green = new SolidBrush(Color.FromArgb(60, 190, 90));
            using var yellow = new SolidBrush(Color.FromArgb(240, 200, 40));
            using var red = new SolidBrush(Color.FromArgb(230, 60, 60));
            using var gDim = new SolidBrush(Color.FromArgb(90, 60, 190, 90));
            using var yDim = new SolidBrush(Color.FromArgb(90, 240, 200, 40));
            using var rDim = new SolidBrush(Color.FromArgb(90, 230, 60, 60));
            using var holdPen = new Pen(Theme.Fore, 2);
            int yY = bottom - (int)(h * frac(-20)), yR = bottom - (int)(h * frac(-9));

            for (int c = 0; c < 2; c++)
            {
                int x = 4 + c * (bw + 4);
                g.FillRectangle(bg, x, top, bw, h);
                // picco (tenue) e RMS (pieno), colorati a zone come OBS: verde < -20, giallo < -9, rosso sopra
                Bar(g, x, bw, bottom, h, frac(peak[c]), yY, yR, gDim, yDim, rDim);
                Bar(g, x, bw, bottom, h, frac(rms[c]), yY, yR, green, yellow, red);
                int hy = bottom - (int)(h * frac(hold[c]));
                if (hold[c] > -60) g.DrawLine(holdPen, x, hy, x + bw, hy);
            }
            // scala
            using var f = new Font("Segoe UI", 6.5f);
            using var mb = new SolidBrush(Theme.Muted);
            g.DrawString("L", f, mb, 4 + bw / 2 - 3, bottom + 2);
            g.DrawString("R", f, mb, 8 + bw + bw / 2 - 3, bottom + 2);
        }

        static void Bar(Graphics g, int x, int w, int bottom, int h, double fr, int yY, int yR, Brush gr, Brush ye, Brush re)
        {
            int yTop = bottom - (int)(h * fr);
            if (yTop >= bottom) return;
            g.FillRectangle(gr, x, Math.Max(yTop, yY), w, bottom - Math.Max(yTop, yY));
            if (yTop < yY) g.FillRectangle(ye, x, Math.Max(yTop, yR), w, yY - Math.Max(yTop, yR));
            if (yTop < yR) g.FillRectangle(re, x, yTop, w, yR - yTop);
        }
    }
}
