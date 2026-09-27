using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        volatile FrameBuf frame;
        public FrameBuf Frame => frame;
        /// <summary>Chiamato dal thread di rendering dopo aver mostrato un frame (per restituirlo al motore e per le statistiche).</summary>
        public event Action FrameShown;
        /// <summary>Dimensione reale in pixel dell'ingresso (per Alt+trascina = ritaglio).</summary>
        public Func<Source, (int w, int h)?> InputSizeOf { get; set; }
        public string Message { get; set; } = "";
        /// <summary>Testo del badge REC (null = non in registrazione).</summary>
        public string RecText { get; set; }

        public event Action<Source> SelectionChanged;
        public event Action<Source, bool> TransformChanged;   // bool = definitivo (mouse up)
        public event Action<Source> OpenProperties;
        public event Action<Source> RemoveRequested;
        public event Action<Source> LockChanged;
        static readonly Font LabelFont = new Font("Segoe UI", 8.5f), RecFont = new Font("Segoe UI Semibold", 11f), MsgFont = new Font("Segoe UI", 12f);

        enum Mode { None, Move, Resize, Crop }
        int startCropL, startCropT, startCropR, startCropB;
        Mode mode = Mode.None;
        int handle = -1;              // 0..7: TL,T,TR,R,BR,B,BL,L
        Point dragStartCanvas; Rectangle dragStartRect;
        const int Snap = 12;

        public CanvasView()
        {
            // niente doppio buffer di WinForms e niente cancellazione dello sfondo: disegna tutto il thread di rendering
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            BackColor = Color.Black;
            TabStop = true;
        }

        // =====================================================================================
        //  Thread di rendering dedicato (come OBS): i frame arrivano qui direttamente dal motore,
        //  senza passare dal thread dell'interfaccia. L'interfaccia fornisce solo un'istantanea
        //  di cosa disegnare sopra (selezione, etichette, badge REC).
        // =====================================================================================
        Thread renderThread;
        readonly AutoResetEvent renderSignal = new AutoResetEvent(false);
        volatile bool renderStop;
        volatile bool newFrame;
        volatile Snapshot snap;
        volatile int clientW, clientH;
        IntPtr hwnd;

        /// <summary>Dal thread del motore: nuovo frame da mostrare. Il frame NON va liberato (è un buffer del motore).</summary>
        public void SubmitFrame(FrameBuf fb)
        {
            frame = fb; newFrame = true;
            renderSignal.Set();
        }

        /// <summary>Dal thread UI: toglie il frame (pipeline ferma) e ridisegna col messaggio.</summary>
        public void SetFrame(FrameBuf fb)
        {
            frame = fb; newFrame = false;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hwnd = Handle;
            clientW = ClientSize.Width; clientH = ClientSize.Height;
            snap = BuildSnapshot();
            if (renderThread == null)
            {
                renderStop = false;
                renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "canvas-render", Priority = ThreadPriority.AboveNormal };
                renderThread.Start();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            renderStop = true; renderSignal.Set();
            try { renderThread?.Join(500); } catch { }
            renderThread = null;
            hwnd = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            clientW = ClientSize.Width; clientH = ClientSize.Height;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* disegna il thread di rendering */ }

        /// <summary>Il thread UI qui NON disegna: aggiorna l'istantanea e sveglia il thread di rendering.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            snap = BuildSnapshot();
            renderSignal.Set();
        }

        public new void Update() { snap = BuildSnapshot(); renderSignal.Set(); }

        class Snapshot
        {
            public int W, H; public Rectangle Dr; public bool Dark;
            public string Message, RecText;
            public Rectangle[] Thin; public Rectangle Sel; public bool HasSel, SelLocked; public Rectangle[] SelHandles; public string SelLabel;
        }

        Snapshot BuildSnapshot()
        {
            var dr = DisplayRect();
            var sn = new Snapshot
            {
                W = ClientSize.Width, H = ClientSize.Height, Dr = dr, Dark = Theme.Dark,
                Message = string.IsNullOrEmpty(Message) ? "Nessuna anteprima" : Message, RecText = RecText,
                Thin = Sources.Where(x => x.Visible && x != Selected).Select(x => ToScreen(RectOf(x))).ToArray(),
            };
            if (Selected != null && Selected.Visible)
            {
                sn.HasSel = true; sn.SelLocked = Selected.Locked;
                sn.Sel = ToScreen(RectOf(Selected));
                sn.SelHandles = Handles(sn.Sel);
                string lbl = (Selected.Locked ? "(bloccata)  " : "") + $"{Selected.Name}   {Selected.W}×{Selected.H}   pos {Selected.X},{Selected.Y}";
                if (Selected.CropL + Selected.CropT + Selected.CropR + Selected.CropB > 0) lbl += $"   ritaglio {Selected.CropL},{Selected.CropT},{Selected.CropR},{Selected.CropB}";
                sn.SelLabel = lbl;
            }
            return sn;
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        void RenderLoop()
        {
            IntPtr memDC = IntPtr.Zero, memBmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
            int bw = 0, bh = 0;
            try
            {
                while (!renderStop)
                {
                    renderSignal.WaitOne(250);
                    if (renderStop) break;
                    var h = hwnd; var sn = snap;
                    int w = clientW, ht = clientH;
                    if (h == IntPtr.Zero || sn == null || w <= 0 || ht <= 0) continue;
                    bool shownNew = newFrame; newFrame = false;
                    var fb = frame;

                    IntPtr wdc = GetDC(h);
                    if (wdc == IntPtr.Zero) continue;
                    try
                    {
                        if (memDC == IntPtr.Zero || bw != w || bh != ht)
                        {
                            if (memDC != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(memBmp); DeleteDC(memDC); }
                            memDC = CreateCompatibleDC(wdc);
                            memBmp = CreateCompatibleBitmap(wdc, w, ht);
                            oldBmp = SelectObject(memDC, memBmp);
                            bw = w; bh = ht;
                        }
                        // composizione nel buffer: sfondo, video, sovrapposizioni → poi un'unica copia sulla finestra (niente sfarfallio)
                        using (var g = Graphics.FromHdc(memDC)) DrawScene(g, sn, fb);
                        BitBlt(wdc, 0, 0, w, ht, memDC, 0, 0, 0x00CC0020);
                    }
                    catch { }
                    finally { ReleaseDC(h, wdc); }

                    if (shownNew) { try { FrameShown?.Invoke(); } catch { } }
                }
            }
            finally
            {
                if (memDC != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(memBmp); DeleteDC(memDC); }
            }
        }

        // ---- disegno veloce del frame con GDI (StretchDIBits): molto più rapido di GDI+ DrawImage ----
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);

        static void DrawFrame(Graphics g, FrameBuf fb, Rectangle dr)
        {
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = fb.W, biHeight = -fb.H, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            IntPtr hdc = g.GetHdc();
            try
            {
                // quasi 1:1 (l'anteprima è già grande come il riquadro): COLORONCOLOR, velocissimo.
                // Solo se si rimpicciolisce molto uso HALFTONE (più bello ma molto più lento).
                double ratio = (double)dr.Width / fb.W;
                SetStretchBltMode(hdc, ratio < 0.8 ? 4 : 3);
                SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
                StretchDIBits(hdc, dr.X, dr.Y, dr.Width, dr.Height, 0, 0, fb.W, fb.H, fb.Data, ref bmi, 0, 0x00CC0020);
            }
            finally { g.ReleaseHdc(hdc); }
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
        static void DrawScene(Graphics g, Snapshot sn, FrameBuf fb)
        {
            g.Clear(sn.Dark ? Color.FromArgb(22, 22, 24) : Color.FromArgb(52, 52, 56));
            var dr = sn.Dr;
            if (dr.Width <= 0) return;

            if (fb != null)
            {
                try { DrawFrame(g, fb, dr); } catch { }
            }
            else
            {
                g.FillRectangle(Brushes.Black, dr);
                using var b = new SolidBrush(Color.Gray);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(sn.Message, MsgFont, b, dr, sf);
            }
            if (sn.RecText != null)
            {
                // bordo rosso + badge: si vede subito che stai registrando, e il video continua a scorrere sotto
                using (var pen = new Pen(Theme.Rec, 3)) g.DrawRectangle(pen, dr.X - 2, dr.Y - 2, dr.Width + 3, dr.Height + 3);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var sz = g.MeasureString(sn.RecText, RecFont);
                var br = new Rectangle(dr.X + 12, dr.Y + 12, (int)sz.Width + 20, (int)sz.Height + 8);
                using (var path = Ui.Rounded(br, br.Height / 2))
                using (var b = new SolidBrush(Color.FromArgb(220, Theme.Rec))) g.FillPath(b, path);
                g.DrawString(sn.RecText, RecFont, Brushes.White, br.X + 10, br.Y + 4);
                g.SmoothingMode = SmoothingMode.None;
            }
            else
                using (var pen = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(pen, dr.X - 1, dr.Y - 1, dr.Width + 1, dr.Height + 1);

            // contorni sorgenti non selezionate (tenui)
            using (var thin = new Pen(Color.FromArgb(110, 255, 255, 255)) { DashStyle = DashStyle.Dot })
                foreach (var r in sn.Thin) g.DrawRectangle(thin, r);

            if (sn.HasSel)
            {
                var sr = sn.Sel;
                if (sn.SelLocked)
                {
                    using var lp = new Pen(Theme.Accent, 2) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(lp, sr);
                }
                else
                {
                    using var pen = new Pen(Theme.Rec, 2);
                    g.DrawRectangle(pen, sr);
                    using var hb = new SolidBrush(Theme.Rec);
                    foreach (var h in sn.SelHandles) g.FillRectangle(hb, h);
                }
                var sz = g.MeasureString(sn.SelLabel, LabelFont);
                var lr = new RectangleF(sr.X, sr.Y - sz.Height - 2, sz.Width + 6, sz.Height);
                if (lr.Y < dr.Y) lr.Y = sr.Y + 2;
                using var bb = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                g.FillRectangle(bb, lr);
                g.DrawString(sn.SelLabel, LabelFont, Brushes.White, lr.X + 3, lr.Y);
            }
        }

        // ---------- mouse ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            var cp = ToCanvas(e.Location);

            if (Selected != null && Selected.Visible && !Selected.Locked && e.Button == MouseButtons.Left)
            {
                var hs = Handles(ToScreen(RectOf(Selected)));
                for (int i = 0; i < hs.Length; i++)
                    if (hs[i].Contains(e.Location))
                    {
                        bool alt = (ModifierKeys & Keys.Alt) != 0 && Selected.Type == SourceType.Capture;
                        mode = alt ? Mode.Crop : Mode.Resize; handle = i; dragStartCanvas = cp; dragStartRect = RectOf(Selected);
                        startCropL = Selected.CropL; startCropT = Selected.CropT; startCropR = Selected.CropR; startCropB = Selected.CropB;
                        return;
                    }
            }
            // hit test dall'alto verso il basso
            // le sorgenti bloccate non si prendono cliccando (come OBS): lo sfondo resta fermo
            var hit = Sources.Where(s => s.Visible && !s.Locked).Reverse().FirstOrDefault(s => RectOf(s).Contains(cp));
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
                if (Selected != null && Selected.Visible && !Selected.Locked)
                {
                    var hs = Handles(ToScreen(RectOf(Selected)));
                    for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(e.Location)) { Cursor = (ModifierKeys & Keys.Alt) != 0 ? Cursors.Cross : HandleCursor(i); return; }
                    if (ToScreen(RectOf(Selected)).Contains(e.Location)) Cursor = Cursors.SizeAll;
                }
                return;
            }
            if (Selected == null) return;
            var cp = ToCanvas(e.Location);
            int dx = cp.X - dragStartCanvas.X, dy = cp.Y - dragStartCanvas.Y;
            var r = dragStartRect;

            if (mode == Mode.Crop)
            {
                DoCrop(dx, dy);
                TransformChanged?.Invoke(Selected, false);
                Invalidate();
                return;
            }

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

        /// <summary>
        /// Alt+trascina una maniglia: ritaglia la sorgente invece di stirarla (come OBS). Il contenuto visibile resta dov'è,
        /// cambia solo quanto se ne vede. Il delta in pixel del canvas viene convertito in pixel della sorgente.
        /// </summary>
        void DoCrop(int dx, int dy)
        {
            var r = dragStartRect;
            var size = InputSizeOf?.Invoke(Selected);
            int srcW, srcH;
            if (size.HasValue) { srcW = size.Value.w; srcH = size.Value.h; }
            else { var p = (Selected.InputSize ?? "").Split('x'); if (p.Length != 2 || !int.TryParse(p[0], out srcW) || !int.TryParse(p[1], out srcH)) { srcW = 720; srcH = 576; } }
            int visW0 = Math.Max(16, srcW - startCropL - startCropR), visH0 = Math.Max(16, srcH - startCropT - startCropB);
            double fx = (double)visW0 / Math.Max(1, r.Width), fy = (double)visH0 / Math.Max(1, r.Height);

            int l = startCropL, t = startCropT, rr = startCropR, b = startCropB;
            int x = r.X, y = r.Y, w = r.Width, h = r.Height;
            bool left = handle == 0 || handle == 6 || handle == 7, right = handle == 2 || handle == 3 || handle == 4;
            bool topH = handle == 0 || handle == 1 || handle == 2, bottomH = handle == 4 || handle == 5 || handle == 6;
            if (left) { int c = (int)Math.Round(dx * fx); c = Math.Clamp(c, -startCropL, visW0 - 16); l = startCropL + c; int dd = (int)Math.Round(c / fx); x = r.X + dd; w = r.Width - dd; }
            if (right) { int c = (int)Math.Round(-dx * fx); c = Math.Clamp(c, -startCropR, visW0 - 16); rr = startCropR + c; w = r.Width - (int)Math.Round(c / fx); }
            if (topH) { int c = (int)Math.Round(dy * fy); c = Math.Clamp(c, -startCropT, visH0 - 16); t = startCropT + c; int dd = (int)Math.Round(c / fy); y = r.Y + dd; h = r.Height - dd; }
            if (bottomH) { int c = (int)Math.Round(-dy * fy); c = Math.Clamp(c, -startCropB, visH0 - 16); b = startCropB + c; h = r.Height - (int)Math.Round(c / fy); }
            Selected.CropL = l; Selected.CropT = t; Selected.CropR = rr; Selected.CropB = b;
            Selected.X = x; Selected.Y = y; Selected.W = Math.Max(16, w); Selected.H = Math.Max(16, h);
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
            if (Selected.Locked && e.KeyCode != Keys.Delete) return;
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
            m.Items.Add(Selected.Locked ? "Sblocca" : "Blocca", null, (o, e) => { Selected.Locked = !Selected.Locked; LockChanged?.Invoke(Selected); Invalidate(); });
            m.Items.Add(new ToolStripSeparator());
            if (Selected.Locked) { m.Items.Add("Rimuovi", null, (o, e) => RemoveRequested?.Invoke(Selected)); Theme.StyleMenu(m); m.Show(this, at); return; }
            m.Items.Add("Adatta allo schermo (mantieni proporzioni)", null, (o, e) => { Selected.FitTo(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Riempi lo schermo (stira)", null, (o, e) => { Selected.FillTo(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Centra", null, (o, e) => { Selected.Center(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add("Dimensione originale", null, (o, e) => { var (w, h) = Selected.NaturalSize(); Selected.W = w; Selected.H = h; Selected.Center(CanvasW, CanvasH); TransformChanged?.Invoke(Selected, true); Invalidate(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Azzera ritaglio", null, (o, e) =>
            {
                var sz = InputSizeOf?.Invoke(Selected);
                Selected.CropL = Selected.CropT = Selected.CropR = Selected.CropB = 0;
                TransformChanged?.Invoke(Selected, true); Invalidate();
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Rimuovi", null, (o, e) => RemoveRequested?.Invoke(Selected));
            Theme.StyleMenu(m);
            m.Show(this, at);
        }
    }
}
