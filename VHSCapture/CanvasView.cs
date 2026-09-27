using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>Anteprima del canvas con selezione, spostamento e ridimensionamento delle sorgenti (stile OBS).</summary>
    public class CanvasView : Control
    {
        public int CanvasW { get; set; } = 1920;
        public int CanvasH { get; set; } = 1080;
        public List<Source> Sources { get; set; } = new List<Source>();   // ordine: dal basso (0) verso l'alto
        public Source Selected { get; private set; }
        public Bitmap Frame { get; private set; }
        public string Message { get; set; } = "";

        public event Action<Source> SelectionChanged;
        public event Action<Source, bool> TransformChanged;   // bool = definitivo (mouse up)
        public event Action<Source> OpenProperties;
        public event Action<Source> RemoveRequested;

        enum Mode { None, Move, Resize }
        Mode mode = Mode.None;
        int handle = -1;              // 0..7: TL,T,TR,R,BR,B,BL,L
        Point dragStartCanvas; Rectangle dragStartRect;
        const int Snap = 12;

        public CanvasView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Black;
            TabStop = true;
        }

        public void SetFrame(Bitmap bmp)
        {
            var old = Frame; Frame = bmp; old?.Dispose();
            Invalidate();
        }

        public void Select(Source s)
        {
            if (Selected == s) { Invalidate(); return; }
            Selected = s;
            SelectionChanged?.Invoke(s);
            Invalidate();
        }

        // ---------- geometria ----------
        Rectangle DisplayRect()
        {
            if (Width <= 0 || Height <= 0) return Rectangle.Empty;
            double sc = Math.Min((double)Width / CanvasW, (double)Height / CanvasH);
            int w = (int)(CanvasW * sc), h = (int)(CanvasH * sc);
            return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
        }
        double Scale() { var r = DisplayRect(); return r.Width <= 0 ? 1 : (double)r.Width / CanvasW; }
        Point ToCanvas(Point p) { var r = DisplayRect(); double s = Scale(); return new Point((int)Math.Round((p.X - r.X) / s), (int)Math.Round((p.Y - r.Y) / s)); }
        Rectangle ToScreen(Rectangle c) { var r = DisplayRect(); double s = Scale(); return new Rectangle(r.X + (int)Math.Round(c.X * s), r.Y + (int)Math.Round(c.Y * s), (int)Math.Round(c.Width * s), (int)Math.Round(c.Height * s)); }
        static Rectangle RectOf(Source s) => new Rectangle(s.X, s.Y, s.W, s.H);

        Rectangle[] Handles(Rectangle sr)
        {
            int hs = 5;
            int cx = sr.X + sr.Width / 2, cy = sr.Y + sr.Height / 2;
            var pts = new[] { new Point(sr.Left, sr.Top), new Point(cx, sr.Top), new Point(sr.Right, sr.Top), new Point(sr.Right, cy),
                              new Point(sr.Right, sr.Bottom), new Point(cx, sr.Bottom), new Point(sr.Left, sr.Bottom), new Point(sr.Left, cy) };
            return pts.Select(p => new Rectangle(p.X - hs, p.Y - hs, hs * 2, hs * 2)).ToArray();
        }

        // ---------- paint ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Dark ? Color.FromArgb(18, 18, 18) : Color.FromArgb(40, 40, 40));
            var dr = DisplayRect();
            if (dr.Width <= 0) return;

            g.FillRectangle(Brushes.Black, dr);
            if (Frame != null)
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(Frame, dr);
            }
            else
            {
                using var f = new Font("Segoe UI", 12f);
                using var b = new SolidBrush(Color.Gray);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(string.IsNullOrEmpty(Message) ? "Nessuna anteprima" : Message, f, b, dr, sf);
            }
            using (var pen = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(pen, dr.X - 1, dr.Y - 1, dr.Width + 1, dr.Height + 1);

            // contorni sorgenti non selezionate (tenui)
            using (var thin = new Pen(Color.FromArgb(110, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                foreach (var s in Sources.Where(x => x.Visible && x != Selected))
                    g.DrawRectangle(thin, ToScreen(RectOf(s)));

            if (Selected != null && Selected.Visible)
            {
                var sr = ToScreen(RectOf(Selected));
                using var pen = new Pen(Theme.Rec, 2);
                g.DrawRectangle(pen, sr);
                using var hb = new SolidBrush(Theme.Rec);
                foreach (var h in Handles(sr)) g.FillRectangle(hb, h);
                using var f = new Font("Segoe UI", 8.5f);
                string lbl = $"{Selected.Name}  {Selected.W}×{Selected.H} @ {Selected.X},{Selected.Y}";
                var sz = g.MeasureString(lbl, f);
                var lr = new RectangleF(sr.X, sr.Y - sz.Height - 2, sz.Width + 6, sz.Height);
                if (lr.Y < dr.Y) lr.Y = sr.Y + 2;
                using var bb = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                g.FillRectangle(bb, lr);
                g.DrawString(lbl, f, Brushes.White, lr.X + 3, lr.Y);
            }
        }

        // ---------- mouse ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            var cp = ToCanvas(e.Location);

            if (Selected != null && Selected.Visible && e.Button == MouseButtons.Left)
            {
                var hs = Handles(ToScreen(RectOf(Selected)));
                for (int i = 0; i < hs.Length; i++)
                    if (hs[i].Contains(e.Location))
                    {
                        mode = Mode.Resize; handle = i; dragStartCanvas = cp; dragStartRect = RectOf(Selected);
                        return;
                    }
            }
            // hit test dall'alto verso il basso
            var hit = Sources.Where(s => s.Visible).Reverse().FirstOrDefault(s => RectOf(s).Contains(cp));
            Select(hit);
            if (hit != null && e.Button == MouseButtons.Left)
            {
                mode = Mode.Move; dragStartCanvas = cp; dragStartRect = RectOf(hit);
            }
            if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (mode == Mode.None)
            {
                Cursor = Cursors.Default;
                if (Selected != null && Selected.Visible)
                {
                    var hs = Handles(ToScreen(RectOf(Selected)));
                    for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(e.Location)) { Cursor = HandleCursor(i); return; }
                    if (ToScreen(RectOf(Selected)).Contains(e.Location)) Cursor = Cursors.SizeAll;
                }
                return;
            }
            if (Selected == null) return;
            var cp = ToCanvas(e.Location);
            int dx = cp.X - dragStartCanvas.X, dy = cp.Y - dragStartCanvas.Y;
            var r = dragStartRect;

            if (mode == Mode.Move)
            {
                int nx = r.X + dx, ny = r.Y + dy;
                // snap a bordi e centro del canvas
                int snap = (int)(Snap / Scale());
                if (Math.Abs(nx) < snap) nx = 0;
                if (Math.Abs(nx + r.Width - CanvasW) < snap) nx = CanvasW - r.Width;
                if (Math.Abs(nx + r.Width / 2 - CanvasW / 2) < snap) nx = (CanvasW - r.Width) / 2;
                if (Math.Abs(ny) < snap) ny = 0;
                if (Math.Abs(ny + r.Height - CanvasH) < snap) ny = CanvasH - r.Height;
                if (Math.Abs(ny + r.Height / 2 - CanvasH / 2) < snap) ny = (CanvasH - r.Height) / 2;
                Selected.X = nx; Selected.Y = ny;
            }
            else
            {
                int l = r.Left, t = r.Top, rt = r.Right, bt = r.Bottom;
                double aspect = (double)r.Width / Math.Max(1, r.Height);
                switch (handle)
                {
                    case 0: l += dx; t += dy; break;
                    case 1: t += dy; break;
                    case 2: rt += dx; t += dy; break;
                    case 3: rt += dx; break;
                    case 4: rt += dx; bt += dy; break;
                    case 5: bt += dy; break;
                    case 6: l += dx; bt += dy; break;
                    case 7: l += dx; break;
                }
                int nw = Math.Max(16, rt - l), nh = Math.Max(16, bt - t);
                bool corner = handle % 2 == 0;
                if (corner && (ModifierKeys & Keys.Shift) == 0)
                {
                    // proporzionale: usa la dimensione dominante
                    if (Math.Abs(dx) >= Math.Abs(dy)) nh = (int)Math.Round(nw / aspect); else nw = (int)Math.Round(nh * aspect);
                    if (handle == 0 || handle == 6) l = rt - nw;
                    if (handle == 0 || handle == 2) t = bt - nh;
                }
                Selected.X = l; Selected.Y = t; Selected.W = nw; Selected.H = nh;
            }
            TransformChanged?.Invoke(Selected, false);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (mode != Mode.None && Selected != null) TransformChanged?.Invoke(Selected, true);
            mode = Mode.None; handle = -1;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (Selected != null && e.Button == MouseButtons.Left) OpenProperties?.Invoke(Selected);
        }

        protected override bool IsInputKey(Keys keyData) => keyData switch
        {
            Keys.Up or Keys.Down or Keys.Left or Keys.Right => true,
            _ => base.IsInputKey(keyData)
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Selected == null) return;
            int step = e.Shift ? 10 : 1;
            bool moved = true;
            switch (e.KeyCode)
            {
                case Keys.Left: Selected.X -= step; break;
                case Keys.Right: Selected.X += step; break;
                case Keys.Up: Selected.Y -= step; break;
                case Keys.Down: Selected.Y += step; break;
                case Keys.Delete: RemoveRequested?.Invoke(Selected); moved = false; break;
                default: moved = false; break;
            }
            if (moved) { TransformChanged?.Invoke(Selected, true); Invalidate(); e.Handled = true; }
        }

        static Cursor HandleCursor(int i) => i switch
        {
            0 or 4 => Cursors.SizeNWSE,
            2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS,
            _ => Cursors.SizeWE,
        };

        void ShowMenu(Point at)
        {
            if (Selected == null) return;
            var m = new ContextMenuStrip();
            m.Items.Add("Proprietà…", null, (o, e) => OpenProperties?.Invoke(Selected));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Adatta allo schermo (mantieni proporzioni)", null, (o, e) => { Selected.FitTo(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Riempi lo schermo (stira)", null, (o, e) => { Selected.FillTo(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Centra", null, (o, e) => { Selected.Center(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Dimensione originale", null, (o, e) => { var (w, h) = Selected.NaturalSize(); Selected.W = w; Selected.H = h; Selected.Center(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Rimuovi", null, (o, e) => RemoveRequested?.Invoke(Selected));
            m.Show(this, at);
        }
    }
}
