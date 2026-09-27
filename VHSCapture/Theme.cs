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
                    break;
                default:
                    c.BackColor = (c.Tag as string == "panel" || InCard(c)) ? Panel : Back; c.ForeColor = Fore; break;
            }
            foreach (Control ch in c.Controls) ApplyRec(ch);
        }
    }

    /// <summary>Barra VU verticale in dB (-60..0) con peak hold.</summary>
    public class VuMeter : Control
    {
        double level = -90, peak = -90;
        DateTime peakAt = DateTime.MinValue;

        public VuMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 26;
        }

        public void SetLevel(double db)
        {
            level = db;
            if (db > peak || (DateTime.Now - peakAt).TotalMilliseconds > 1200) { peak = db; peakAt = DateTime.Now; }
            Invalidate();
        }

        public void Reset() { level = peak = -90; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            var r = new Rectangle(4, 4, Width - 8, Height - 8);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, r);

            double frac(double db) => Math.Clamp((db + 60.0) / 60.0, 0, 1);
            int h = (int)(r.Height * frac(level));
            if (h > 0)
            {
                // verde fino a -18, giallo fino a -6, rosso sopra
                int yTop = r.Bottom - h;
                int yYellow = r.Bottom - (int)(r.Height * frac(-18));
                int yRed = r.Bottom - (int)(r.Height * frac(-6));
                using var green = new SolidBrush(Color.FromArgb(60, 190, 90));
                using var yellow = new SolidBrush(Color.FromArgb(240, 200, 40));
                using var red = new SolidBrush(Color.FromArgb(230, 60, 60));
                int x = r.X + 1, w = r.Width - 1;
                g.FillRectangle(green, x, Math.Max(yTop, yYellow), w, r.Bottom - Math.Max(yTop, yYellow));
                if (yTop < yYellow) g.FillRectangle(yellow, x, Math.Max(yTop, yRed), w, yYellow - Math.Max(yTop, yRed));
                if (yTop < yRed) g.FillRectangle(red, x, yTop, w, yRed - yTop);
            }
            int py = r.Bottom - (int)(r.Height * frac(peak));
            using (var pp = new Pen(Theme.Fore, 2)) g.DrawLine(pp, r.X + 1, py, r.Right - 1, py);
        }
    }
}
