using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>
    /// Lista sorgenti come in OBS: una riga per sorgente con icona tipo, nome, 👁 visibilità e 🔒 blocco cliccabili.
    /// In alto la sorgente che sta sopra nella scena.
    /// </summary>
    public class SourceList : Control
    {
        public List<Source> Sources { get; set; } = new List<Source>();   // ordine scena: 0 = sotto
        public Source Selected { get; private set; }
        public bool ReadOnlyStructure { get; set; }                       // in registrazione: niente nascondi

        public event Action<Source> SelectionChanged;
        public event Action<Source> VisibilityToggled;
        public event Action<Source> LockToggled;
        public event Action<Source> OpenProperties;
        public event Action<Source, Point> ContextRequested;

        const int RowH = 48;
        int scroll;
        int hoverRow = -1;

        public SourceList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Font = new Font("Segoe UI", 10f);
        }

        IList<Source> View()
        {
            var v = new List<Source>(Sources); v.Reverse(); return v;   // in alto quella sopra
        }

        public void Select(Source s) { if (Selected == s) { Invalidate(); return; } Selected = s; Invalidate(); }

        Rectangle EyeRect(int row) => new Rectangle(Width - 66, row * RowH - scroll + 9, 28, RowH - 18);
        Rectangle LockRect(int row) => new Rectangle(Width - 34, row * RowH - scroll + 9, 28, RowH - 18);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Input);
            var v = View();
            using var fIcon = new Font("Segoe UI Symbol", 10.5f);
            using var fMuted = new Font("Segoe UI", 8f);
            using var fName = new Font("Segoe UI Semibold", 10f);
            for (int i = 0; i < v.Count; i++)
            {
                var s = v[i];
                var r = new Rectangle(2, i * RowH - scroll + 2, Width - 4, RowH - 4);
                if (r.Bottom < 0 || r.Top > Height) continue;
                bool sel = s == Selected;
                if (sel || i == hoverRow)
                {
                    using var path = Ui.Rounded(r, 6);
                    using var b = new SolidBrush(sel ? Color.FromArgb(Theme.Dark ? 90 : 60, Theme.Accent) : Color.FromArgb(Theme.Dark ? 40 : 25, Theme.Fore));
                    g.FillPath(b, path);
                }
                var fore = s.Visible ? Theme.Fore : Theme.Muted;
                string icon = s.Type switch { SourceType.Capture => "🎥", SourceType.Image => "🖼", _ => "■" };
                TextRenderer.DrawText(g, icon, fIcon, new Rectangle(8, r.Y, 26, r.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                int tw = Width - 36 - 74;
                TextRenderer.DrawText(g, s.Name, fName, new Rectangle(36, r.Y + 5, tw, 20), fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                // seconda riga: da dove arriva (così si capisce subito che dispositivo è e se ha l'audio)
                string sub = s.Type switch
                {
                    SourceType.Capture => (string.IsNullOrEmpty(s.VideoDevice) ? "nessun dispositivo" : s.VideoDevice) + "   ·   " + (s.HasAudio ? "🎤 " + s.AudioDevice : "senza audio"),
                    SourceType.Image => System.IO.Path.GetFileName(s.ImagePath ?? ""),
                    _ => s.Color,
                };
                if (!s.Visible) sub = "nascosta   ·   " + sub;
                TextRenderer.DrawText(g, sub, fMuted, new Rectangle(36, r.Y + 25, tw, 16), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, s.Visible ? "👁" : "◌", fIcon, EyeRect(i), s.Visible ? Theme.Fore : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, s.Locked ? "🔒" : "🔓", fIcon, LockRect(i), s.Locked ? Theme.Accent : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            }
            if (v.Count == 0)
                TextRenderer.DrawText(g, "Nessuna sorgente — premi ＋", fMuted, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int RowAt(Point p) { int r = (p.Y + scroll) / RowH; return r >= 0 && r < Sources.Count ? r : -1; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var v = View();
            int row = RowAt(e.Location);
            if (row < 0) { if (e.Button == MouseButtons.Left) { Selected = null; SelectionChanged?.Invoke(null); Invalidate(); } return; }
            var s = v[row];
            if (e.Button == MouseButtons.Left)
            {
                if (EyeRect(row).Contains(e.Location)) { if (!ReadOnlyStructure) VisibilityToggled?.Invoke(s); Invalidate(); return; }
                if (LockRect(row).Contains(e.Location)) { LockToggled?.Invoke(s); Invalidate(); return; }
            }
            if (Selected != s) { Selected = s; SelectionChanged?.Invoke(s); Invalidate(); }
            if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(s, e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int row = RowAt(e.Location); if (row < 0) return;
            if (EyeRect(row).Contains(e.Location) || LockRect(row).Contains(e.Location)) return;
            OpenProperties?.Invoke(View()[row]);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int r = RowAt(e.Location);
            if (r != hoverRow) { hoverRow = r; Invalidate(); }
            Cursor = r >= 0 && (EyeRect(r).Contains(e.Location) || LockRect(r).Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hoverRow = -1; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, Sources.Count * RowH - Height);
            scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * RowH, 0, max);
            Invalidate();
        }
    }
}
