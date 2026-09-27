using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VHSCapture
{
    public static class Ui
    {
        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static RoundedButton Btn(string text, string variant = "normal", EventHandler click = null, int minWidth = 0)
        {
            var b = new RoundedButton { Text = text, Variant = variant, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            b.MinimumSize = new Size(minWidth, 36);
            b.Padding = new Padding(14, 0, 14, 0);
            if (click != null) b.Click += click;
            return b;
        }

        public static RoundedButton IconBtn(string glyph, string tip, EventHandler click = null)
        {
            var b = new RoundedButton { Text = glyph, Variant = "ghost", Width = 40, Height = 34, AutoSize = false, Margin = new Padding(0, 0, 6, 0), Font = new Font("Segoe UI Symbol", 11f) };
            if (click != null) b.Click += click;
            if (tip != null) new ToolTip().SetToolTip(b, tip);
            return b;
        }

        /// <summary>Scorre il primo contenitore AutoScroll che contiene il controllo.</summary>
        public static void ScrollParent(Control c, int delta)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                if (p is ScrollableControl sc && sc.AutoScroll && sc.VerticalScroll.Visible)
                {
                    int y = -sc.AutoScrollPosition.Y - Math.Sign(delta) * 60;
                    sc.AutoScrollPosition = new Point(0, Math.Max(0, y));
                    return;
                }
            }
        }

        public static Label Title(string t) => new Label { Text = t, AutoSize = true, Tag = "muted", Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(2, 0, 0, 6) };
    }

    /// <summary>Controlli che NON cambiano valore con la rotella: la rotella scorre la pagina.</summary>
    public class SafeTrackBar : TrackBar
    {
        protected override void OnMouseWheel(MouseEventArgs e) { if (e is HandledMouseEventArgs h) h.Handled = true; Ui.ScrollParent(this, e.Delta); }
    }
    public class SafeCombo : ComboBox
    {
        protected override void OnMouseWheel(MouseEventArgs e) { if (e is HandledMouseEventArgs h) h.Handled = true; if (!DroppedDown) Ui.ScrollParent(this, e.Delta); }
    }
    public class SafeNumeric : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e) { if (e is HandledMouseEventArgs h) h.Handled = true; Ui.ScrollParent(this, e.Delta); }
    }

    /// <summary>Pulsante piatto con angoli arrotondati e stati hover/pressed. Variant: normal, accent, rec, ghost, danger.</summary>
    public class RoundedButton : Button
    {
        public int Radius { get; set; } = 8;
        public string Variant { get; set; } = "normal";
        bool hover, down;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 9.5f);
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        (Color bg, Color fg, Color border) Colors()
        {
            bool dark = Theme.Dark;
            switch (Variant)
            {
                case "accent": return (Theme.Accent, Color.White, Theme.Accent);
                case "rec": return (Theme.Rec, Color.White, Theme.Rec);
                // "Ferma registrazione": colore diverso dal rosso di avvio, chiaro su scuro / scuro su chiaro, testo rosso
                case "stop": return (dark ? Color.FromArgb(235, 235, 238) : Color.FromArgb(40, 40, 44), dark ? Color.FromArgb(190, 30, 30) : Color.FromArgb(255, 110, 100), Theme.Rec);
                case "danger": return (Theme.Panel, Theme.Rec, Theme.Rec);
                case "ghost": return (Theme.Panel, Theme.Fore, Theme.Border);
                default: return (dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(232, 232, 236), Theme.Fore, Color.Transparent);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            var (bg, fg, border) = Colors();
            if (!Enabled) { bg = Color.FromArgb(120, bg); fg = Color.FromArgb(110, fg); }
            else if (down) bg = ControlPaint.Dark(bg, 0.08f);
            else if (hover) bg = Theme.Dark ? ControlPaint.Light(bg, 0.18f) : ControlPaint.Dark(bg, 0.05f);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, Radius))
            {
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                if (border.A > 0) using (var p = new Pen(Enabled ? border : Color.FromArgb(120, border))) g.DrawPath(p, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var sz = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            return new Size(Math.Max(MinimumSize.Width, sz.Width + Padding.Horizontal), Math.Max(MinimumSize.Height, sz.Height + Padding.Vertical + 12));
        }
    }

    /// <summary>Pannello "card" con angoli arrotondati e bordo sottile.</summary>
    public class Card : Panel
    {
        public int Radius { get; set; } = 10;
        public string HeaderText { get; set; }

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Tag = "card";
            Padding = new Padding(12);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            if (!AutoSize) return base.GetPreferredSize(proposed);
            int bottom = Padding.Top, right = 0;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                var ps = c.AutoSize ? c.GetPreferredSize(new Size(Math.Max(1, Width - Padding.Horizontal), 0)) : c.Size;
                bottom = Math.Max(bottom, c.Top + Math.Max(c.Height, ps.Height) + c.Margin.Bottom);
                right = Math.Max(right, c.Right);
            }
            return new Size(Math.Max(Width, right + Padding.Right), bottom + Padding.Bottom);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Ui.Rounded(r, Radius);
            using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
            using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            if (!string.IsNullOrEmpty(HeaderText))
                TextRenderer.DrawText(g, HeaderText, new Font("Segoe UI Semibold", 8.5f), new Point(Padding.Left, 8), Theme.Muted);
        }
    }

    /// <summary>Etichetta stato REC a forma di pillola.</summary>
    public class Pill : Label
    {
        public Color Fill { get; set; } = Color.Transparent;
        public Pill() { AutoSize = true; Padding = new Padding(10, 4, 10, 4); Font = new Font("Segoe UI Semibold", 9.5f); SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Back);
            if (Fill.A > 0 && !string.IsNullOrEmpty(Text))
            {
                using var path = Ui.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
                using var b = new SolidBrush(Fill); g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
