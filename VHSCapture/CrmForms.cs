using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>
    /// Una scelta grande e cliccabile: titolo, spiegazione in parole semplici e, in evidenza (colorata),
    /// cosa succede davvero scegliendola. Si clicca ovunque sul riquadro.
    /// </summary>
    public class SceltaCard : Panel
    {
        public event EventHandler Scelta;
        readonly Label lTitolo, lSpieg, lEffetto;
        readonly Color accento;
        bool sopra, attiva = true;

        public SceltaCard(string titolo, string spiegazione, string effetto, Color accento, int larghezza)
        {
            this.accento = accento;
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Width = larghezza; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 0, 10);
            int w = larghezza - 44;
            lTitolo = new Label { Text = titolo, AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Location = new Point(24, 12), Tag = "keep", BackColor = Color.Transparent };
            lSpieg = new Label { Text = spiegazione, AutoSize = true, MaximumSize = new Size(w, 0), Font = new Font("Segoe UI", 10f), Tag = "keep", BackColor = Color.Transparent };
            lEffetto = new Label { Text = effetto, AutoSize = true, MaximumSize = new Size(w, 0), Font = new Font("Segoe UI Semibold", 10f), Tag = "keep", BackColor = Color.Transparent };
            Controls.AddRange(new Control[] { lTitolo, lSpieg, lEffetto });
            int y = 12 + lTitolo.GetPreferredSize(Size.Empty).Height + 4;
            lSpieg.Location = new Point(24, y); y += lSpieg.GetPreferredSize(new Size(w, 0)).Height + 6;
            lEffetto.Location = new Point(24, y); y += lEffetto.GetPreferredSize(new Size(w, 0)).Height + 14;
            Height = y;
            foreach (Control c in new Control[] { this, lTitolo, lSpieg, lEffetto })
            {
                c.Click += (o, e) => { if (attiva) Scelta?.Invoke(this, EventArgs.Empty); };
                c.MouseEnter += (o, e) => { sopra = true; Invalidate(); };
                c.MouseLeave += (o, e) => { sopra = ClientRectangle.Contains(PointToClient(MousePosition)); Invalidate(); };
            }
            Colori();
        }

        public bool Attiva
        {
            get => attiva;
            set { attiva = value; Cursor = value ? Cursors.Hand : Cursors.No; foreach (Control c in Controls) c.Cursor = Cursor; Colori(); Invalidate(); }
        }

        /// <summary>Colori dopo il cambio di tema (chiaro/scuro).</summary>
        public void Colori()
        {
            lTitolo.ForeColor = attiva ? Theme.Fore : Theme.Muted;
            lSpieg.ForeColor = attiva ? Theme.Fore : Theme.Muted;
            lEffetto.ForeColor = attiva ? accento : Theme.Muted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Back)) g.FillRectangle(bg, ClientRectangle);
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = Ui.Rounded(r, 12);
            Color fondo = !attiva ? (Theme.Dark ? Color.FromArgb(38, 38, 40) : Color.FromArgb(236, 236, 239))
                        : sopra ? Color.FromArgb(Theme.Dark ? 60 : 30, accento) : Theme.Panel;
            using (var f = new SolidBrush(fondo)) g.FillPath(f, path);
            using (var pen = new Pen(attiva && sopra ? accento : Theme.Border, attiva && sopra ? 2f : 1f)) g.DrawPath(pen, path);
            // barra colorata a sinistra: si riconosce la scelta anche a colpo d'occhio
            using var barra = new SolidBrush(attiva ? accento : Theme.Border);
            g.FillRectangle(barra, new Rectangle(r.X + 1, r.Y + 10, 6, r.Height - 20));
        }
    }

    /// <summary>Riquadro informativo in cima alle finestre: cosa sta chiedendo il programma, in chiaro.</summary>
    public class InfoBox : Panel
    {
        readonly Label testo;
        readonly Color colore;
        public InfoBox(string icona, string messaggio, int larghezza, Color colore)
        {
            this.colore = colore;
            DoubleBuffered = true; Width = larghezza;
            var ic = new Label { Text = icona, AutoSize = true, Font = new Font("Segoe UI Emoji", 14f), Location = new Point(14, 10), Tag = "keep", BackColor = Color.Transparent };
            testo = new Label { Text = messaggio, AutoSize = true, MaximumSize = new Size(larghezza - 70, 0), Font = new Font("Segoe UI", 10f), Location = new Point(52, 12), Tag = "keep", BackColor = Color.Transparent };
            Controls.Add(ic); Controls.Add(testo);
            Height = Math.Max(48, testo.GetPreferredSize(new Size(larghezza - 70, 0)).Height + 24);
            testo.ForeColor = Theme.Fore;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Back)) g.FillRectangle(bg, ClientRectangle);
            using var path = Ui.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10);
            using (var f = new SolidBrush(Color.FromArgb(Theme.Dark ? 55 : 28, colore))) g.FillPath(f, path);
            using (var p = new Pen(Color.FromArgb(120, colore))) g.DrawPath(p, path);
            testo.ForeColor = Theme.Fore;
        }
    }

    static class CrmColori
    {
        public static readonly Color Verde = Color.FromArgb(34, 150, 70);
        public static readonly Color Rosso = Color.FromArgb(205, 45, 45);
        public static readonly Color Arancio = Color.FromArgb(215, 120, 0);
    }

    /// <summary>
    /// Fascia dell'avanzamento del cliente in corso, ben visibile sotto la barra dei pulsanti:
    /// nome, «Cassetta 4 di 10» in grande e una barra a blocchetti (verdi fatte, rossa lampeggiante in registrazione, grigie da fare).
    /// </summary>
    public class ProgressoCliente : Control
    {
        public string Cliente { get; set; } = "";
        public string Dettaglio { get; set; } = "";
        public int Fatti { get; set; }
        public int Totali { get; set; }
        public int Corrente { get; set; }
        public bool Registrando { get; set; }
        bool lampo;
        // font creati una volta sola: la fascia si ridisegna due volte al secondo durante la registrazione
        static readonly Font FNome = new Font("Segoe UI Semibold", 15f), FGrande = new Font("Segoe UI Semibold", 20f),
                             FPiccolo = new Font("Segoe UI", 9.5f), FSotto = new Font("Segoe UI Semibold", 10f);

        public ProgressoCliente()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 96;
        }

        /// <summary>Chiamato ogni mezzo secondo: fa lampeggiare il blocchetto della cassetta in registrazione.</summary>
        public void Lampeggia() { lampo = !lampo; if (Registrando) Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Back)) g.FillRectangle(bg, ClientRectangle);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, 12))
            {
                using var f = new SolidBrush(Theme.Panel); g.FillPath(f, path);
                using var p = new Pen(Registrando ? CrmColori.Rosso : Theme.Accent, 2f); g.DrawPath(p, path);
            }
            int n = Registrando && Corrente > 0 ? Corrente : Math.Min(Fatti + 1, Math.Max(1, Totali));
            var fNome = FNome; var fGrande = FGrande; var fPiccolo = FPiccolo;
            int destra = 360;
            TextRenderer.DrawText(g, "👤  " + Cliente, fNome, new Rectangle(18, 10, Width - destra - 30, 30), Theme.Fore, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "Supporti: " + (string.IsNullOrEmpty(Dettaglio) ? "—" : Dettaglio), fPiccolo, new Rectangle(20, 42, Width - destra - 30, 20), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, $"Cassetta {n} di {Totali}", fGrande, new Rectangle(Width - destra, 4, destra - 18, 40), Registrando ? CrmColori.Rosso : Theme.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            string sotto = Registrando ? "🔴  IN REGISTRAZIONE" : $"{Fatti} fatte  ·  {Math.Max(0, Totali - Fatti)} da fare";
            TextRenderer.DrawText(g, sotto, FSotto, new Rectangle(Width - destra, 44, destra - 18, 20), Registrando ? CrmColori.Rosso : Theme.Muted, TextFormatFlags.Right);
            // barra: un blocchetto per cassetta (oltre 40 cassette diventa una barra continua)
            var barra = new Rectangle(18, Height - 28, Width - 36, 14);
            if (Totali <= 0) return;
            if (Totali <= 40)
            {
                int gap = Totali > 20 ? 3 : 5;
                float w = (barra.Width - gap * (Totali - 1)) / (float)Totali;
                for (int i = 0; i < Totali; i++)
                {
                    var b = new Rectangle((int)(barra.X + i * (w + gap)), barra.Y, Math.Max(2, (int)w), barra.Height);
                    Color c = i < Fatti ? CrmColori.Verde
                            : (Registrando && i == n - 1) ? (lampo ? CrmColori.Rosso : Color.FromArgb(120, CrmColori.Rosso))
                            : (Theme.Dark ? Color.FromArgb(70, 70, 75) : Color.FromArgb(222, 222, 228));
                    using var pb = Ui.Rounded(b, 4); using var fb = new SolidBrush(c); g.FillPath(fb, pb);
                }
            }
            else
            {
                using (var pb = Ui.Rounded(barra, 6)) using (var fb = new SolidBrush(Theme.Dark ? Color.FromArgb(70, 70, 75) : Color.FromArgb(222, 222, 228))) g.FillPath(fb, pb);
                var pieno = new Rectangle(barra.X, barra.Y, (int)(barra.Width * Math.Min(1.0, Fatti / (double)Totali)), barra.Height);
                if (pieno.Width > 4) { using var pp = Ui.Rounded(pieno, 6); using var fv = new SolidBrush(CrmColori.Verde); g.FillPath(fv, pp); }
            }
        }
    }

    /// <summary>
    /// Un cliente nella lista: si illumina passandoci sopra (sfondo colorato, bordo, «▶ Clicca per iniziare»,
    /// manina), si «schiaccia» al clic. Nome grande, bollino «Cassetta 3 di 4», supporti e mini barra delle videocassette fatte.
    /// </summary>
    public class ClienteCard : Control
    {
        public CrmLavoro Lavoro { get; }
        bool sopra, premuto;
        static readonly Font FNome = new Font("Segoe UI Semibold", 13.5f), FBollino = new Font("Segoe UI Semibold", 11f),
                             FSup = new Font("Segoe UI", 9.5f), FAzione = new Font("Segoe UI Semibold", 10f);

        public ClienteCard(CrmLavoro l)
        {
            Lavoro = l;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 90; Width = 560; Cursor = Cursors.Hand; Margin = new Padding(0, 0, 0, 8);
        }

        protected override void OnMouseEnter(EventArgs e) { sopra = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { sopra = false; premuto = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { premuto = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { premuto = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Ui.ScrollParent(this, e.Delta); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Parent?.BackColor ?? Theme.Back)) g.FillRectangle(bg, ClientRectangle);
            var l = Lavoro;
            bool altroPc = !string.IsNullOrEmpty(l.in_registrazione_su);
            Color acc = altroPc ? CrmColori.Rosso : Theme.Accent;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            if (premuto) r.Inflate(-2, -2);
            using (var path = Ui.Rounded(r, 12))
            {
                Color fondo = premuto ? Color.FromArgb(Theme.Dark ? 110 : 55, acc) : sopra ? Color.FromArgb(Theme.Dark ? 70 : 24, acc) : Theme.Panel;
                using var f = new SolidBrush(fondo); g.FillPath(f, path);
                using var p = new Pen(sopra ? acc : Theme.Border, sopra ? 2f : 1f); g.DrawPath(p, path);
            }
            using (var barra = new SolidBrush(acc)) g.FillRectangle(barra, new Rectangle(r.X + 1, r.Y + 12, sopra ? 7 : 5, r.Height - 24));
            int x = r.X + 22;

            // bollino «Cassetta n di tot» a destra
            int n = Math.Min(l.prossima, Math.Max(1, l.nastri_totali));
            string bol = $"Cassetta {n} di {l.nastri_totali}";
            var sz = TextRenderer.MeasureText(bol, FBollino);
            var rb = new Rectangle(r.Right - sz.Width - 36, r.Y + 12, sz.Width + 22, 30);
            using (var pb = Ui.Rounded(rb, 15)) using (var fb = new SolidBrush(acc)) g.FillPath(fb, pb);
            TextRenderer.DrawText(g, bol, FBollino, rb, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // nome e supporti
            TextRenderer.DrawText(g, l.cliente, FNome, new Rectangle(x, r.Y + 10, Math.Max(40, rb.X - x - 12), 32), Theme.Fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string sup = "Supporti: " + (string.IsNullOrEmpty(l.dettaglio) ? "—" : l.dettaglio) + (string.IsNullOrEmpty(l.stato) ? "" : "   ·   " + l.stato);
            TextRenderer.DrawText(g, sup, FSup, new Rectangle(x, r.Y + 44, Math.Max(40, r.Width - 330), 20), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // mini barra delle videocassette: verdi le fatte
            if (l.nastri_totali > 0)
            {
                var mb = new Rectangle(x, r.Bottom - 20, Math.Min(280, Math.Max(80, r.Width - 360)), 8);
                Color vuoto = Theme.Dark ? Color.FromArgb(70, 70, 75) : Color.FromArgb(222, 222, 228);
                if (l.nastri_totali <= 30)
                {
                    int gap = 3; float w = (mb.Width - gap * (l.nastri_totali - 1)) / (float)l.nastri_totali;
                    for (int i = 0; i < l.nastri_totali; i++)
                    {
                        var b = new Rectangle((int)(mb.X + i * (w + gap)), mb.Y, Math.Max(2, (int)w), mb.Height);
                        using var pb = Ui.Rounded(b, 3); using var fb = new SolidBrush(i < l.nastri_fatti ? CrmColori.Verde : vuoto); g.FillPath(fb, pb);
                    }
                }
                else
                {
                    using (var pb = Ui.Rounded(mb, 4)) using (var fb = new SolidBrush(vuoto)) g.FillPath(fb, pb);
                    int wv = (int)(mb.Width * Math.Min(1.0, l.nastri_fatti / (double)l.nastri_totali));
                    if (wv > 4) { using var pv = Ui.Rounded(new Rectangle(mb.X, mb.Y, wv, mb.Height), 4); using var fv = new SolidBrush(CrmColori.Verde); g.FillPath(fv, pv); }
                }
            }

            // in basso a destra: cosa succede cliccando
            string azione = altroPc ? "🔴 lo sta registrando " + l.in_registrazione_su
                          : sopra ? "▶  Clicca per iniziare"
                          : $"{l.nastri_fatti} fatte  ·  {Math.Max(0, l.nastri_totali - l.nastri_fatti)} da fare";
            TextRenderer.DrawText(g, azione, FAzione, new Rectangle(r.Right - 320, r.Bottom - 32, 298, 22),
                altroPc ? CrmColori.Rosso : (sopra ? acc : Theme.Muted), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>
    /// «Chi stai riversando?»: «👤 Cliente» mostra la coda del CRM (un clic sul nome e si parte),
    /// «Nessun cliente» registra come sempre. Dal pulsante 👤 in alto si apre direttamente la lista.
    /// </summary>
    public class ClienteForm : Form
    {
        readonly Func<Task<List<CrmLavoro>>> carica;
        readonly Func<string> errore;
        readonly Panel passo1, passo2;
        readonly FlowLayoutPanel elenco;
        readonly TextBox cerca;
        readonly List<ClienteCard> schede = new List<ClienteCard>();
        readonly Label lblStato;
        const int W = 640;
        public CrmLavoro Scelto { get; private set; }

        readonly Label lblTempo;
        System.Windows.Forms.Timer tempo;
        int restano;

        /// <param name="chiudiDopo">secondi dopo cui la finestra si chiude da sola senza scelta (0 = mai): usato mentre si registra</param>
        public ClienteForm(bool dark, Func<Task<List<CrmLavoro>>> caricaLavori, Func<string> ultimoErrore, bool soloLista, string titolo = null, int chiudiDopo = 0)
        {
            Theme.Apply(this, dark);   // i colori servono già per costruire i riquadri
            carica = caricaLavori; errore = ultimoErrore;
            Text = "VHSCapture — chi stai riversando?";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);

            // ── passo 1: Cliente / Nessun cliente ──
            passo1 = new Panel { Dock = DockStyle.Fill };
            int y = 20;
            var t1 = new Label { Text = "Chi stai riversando?", AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Location = new Point(24, y) };
            passo1.Controls.Add(t1); y += 44;
            var info1 = new InfoBox("ℹ️", "Prima di registrare dimmi se queste cassette sono di un cliente della Coda Lavorazioni del CRM. Basta un clic.", W - 48, Theme.Accent) { Location = new Point(24, y) };
            passo1.Controls.Add(info1); y += info1.Height + 14;
            var cCliente = new SceltaCard("👤   Cliente",
                "Stai riversando le cassette di un cliente che è nella Coda Lavorazioni del CRM.",
                "→ scegli il nome dalla lista · ogni cassetta si conta da sola (es. 3 di 4) · il file va nella cartella «Nome Cognome»",
                Theme.Accent, W - 48) { Location = new Point(24, y) };
            cCliente.Scelta += (o, e) => MostraLista();
            passo1.Controls.Add(cCliente); y += cCliente.Height + 10;
            var cNessuno = new SceltaCard("🚫   Nessun cliente",
                "Una prova, un lavoro interno o cassette di qualcuno che non è nel CRM.",
                "→ si registra come sempre: niente conteggio, niente cartella del cliente, il CRM non viene toccato",
                CrmColori.Arancio, W - 48) { Location = new Point(24, y) };
            cNessuno.Scelta += (o, e) => { Scelto = null; DialogResult = DialogResult.OK; Close(); };
            passo1.Controls.Add(cNessuno); y += cNessuno.Height + 14;
            int altezzaPasso1 = y;

            // ── passo 2: lista dei clienti in coda ──
            passo2 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 16), Visible = false };
            var t2 = new Label { Text = titolo ?? "Quale cliente?", Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI Semibold", 15f) };
            var info2 = new InfoBox(chiudiDopo > 0 ? "🔴" : "ℹ️",
                (chiudiDopo > 0
                    ? "La registrazione è GIÀ PARTITA, non stai perdendo niente. Clicca sul cliente di questa cassetta: a fine registrazione il file viene spostato nella sua cartella. Le prossime cassette dello stesso cliente non chiedono più niente, finché non hai finito le sue videocassette.\n"
                    : "Clicca sul cliente di questa cassetta. Le prossime cassette dello stesso cliente partono senza chiedere, finché non hai finito le sue videocassette.\n") +
                "«cassetta 3 di 4» = stai per registrare la 3ª delle 4 videocassette della scheda (VHS, S-VHS, VHS-C, 8mm, Hi8, Digital8, MiniDV).\n" +
                "La riga sotto elenca tutti i supporti del cliente (anche DVD, CD, musicassette… che si lavorano a parte).\n" +
                "🔴 = un altro PC sta già registrando questo cliente.",
                W - 48, Theme.Accent) { Dock = DockStyle.Top };
            var spazio = new Panel { Dock = DockStyle.Top, Height = 10 };
            // ricerca: con tanti clienti in coda si trova il nome in un attimo (Invio = il primo della lista)
            var rigaCerca = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 2, 0, 6) };
            cerca = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 11f), PlaceholderText = "🔎  Cerca il cliente per nome…", BorderStyle = BorderStyle.FixedSingle };
            cerca.TextChanged += (o, e) => { Filtra(); restano = Math.Max(restano, 60); };   // stai cercando: il conto alla rovescia riparte
            cerca.KeyDown += (o, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                var primo = schede.FirstOrDefault(x => x.Visible);
                if (primo != null) Scegli(primo.Lavoro);
            };
            rigaCerca.Controls.Add(cerca);
            lblStato = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI Semibold", 10f), Tag = "keep", TextAlign = ContentAlignment.MiddleLeft };
            elenco = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 2, 0, 2) };
            elenco.Resize += (o, e) => Larghezze();
            var giu = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            var bNessuno2 = Ui.Btn("🚫  Nessun cliente (registra senza CRM)", "ghost", (o, e) => { Scelto = null; DialogResult = DialogResult.OK; Close(); }, 250);
            var bAggiorna = Ui.Btn("↻  Aggiorna la lista", "ghost", async (o, e) => await Carica(), 150);
            giu.Controls.Add(bNessuno2); giu.Controls.Add(bAggiorna);
            // conto alla rovescia: senza scelta la finestra si chiude e la registrazione continua nella cartella predefinita
            lblTempo = new Label { Dock = DockStyle.Bottom, Height = 26, Tag = "keep", Font = new Font("Segoe UI Semibold", 9.5f), TextAlign = ContentAlignment.MiddleLeft, Visible = chiudiDopo > 0 };
            if (chiudiDopo > 0)
            {
                restano = chiudiDopo;
                tempo = new System.Windows.Forms.Timer { Interval = 1000 };
                tempo.Tick += (o, e) =>
                {
                    restano--;
                    lblTempo.Text = $"⏱  Si chiude da sola tra {restano} s: la registrazione continua nella cartella predefinita, senza cliente.";
                    lblTempo.ForeColor = restano <= 10 ? Theme.Rec : Theme.Muted;
                    if (restano <= 0) { tempo.Stop(); Close(); }
                };
                lblTempo.Text = $"⏱  Si chiude da sola tra {restano} s: la registrazione continua nella cartella predefinita, senza cliente.";
                Shown += (o, e) => tempo.Start();
                FormClosed += (o, e) => { tempo.Stop(); tempo.Dispose(); };
            }
            passo2.Controls.Add(elenco); passo2.Controls.Add(lblStato); passo2.Controls.Add(rigaCerca); passo2.Controls.Add(spazio); passo2.Controls.Add(info2); passo2.Controls.Add(t2); passo2.Controls.Add(lblTempo); passo2.Controls.Add(giu);

            Controls.Add(passo2); Controls.Add(passo1);
            ClientSize = new Size(W, soloLista ? 640 : altezzaPasso1);
            Theme.Apply(this, dark);
            foreach (Control c in passo1.Controls) if (c is SceltaCard sc) sc.Colori();
            elenco.BackColor = Theme.Back;
            if (soloLista) Shown += (o, e) => MostraLista();
        }

        async void MostraLista()
        {
            passo1.Visible = false; passo2.Visible = true;
            if (ClientSize.Height < 640) { ClientSize = new Size(W, 640); CenterToParent(); }
            await Carica();
        }

        void Scegli(CrmLavoro l) { Scelto = l; DialogResult = DialogResult.OK; Close(); }

        /// <summary>Card larghe quanto la lista, lasciando sempre il posto alla barra di scorrimento (niente scorrimento orizzontale).</summary>
        void Larghezze()
        {
            int w = Math.Max(300, elenco.ClientSize.Width - elenco.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 4);
            elenco.SuspendLayout();
            foreach (var c in schede) c.Width = w;
            elenco.ResumeLayout();
        }

        async Task Carica()
        {
            lblStato.ForeColor = Theme.Muted; lblStato.Text = "⏳  Carico la coda dal CRM…";
            elenco.Controls.Clear();
            foreach (var c in schede) c.Dispose();
            schede.Clear();
            var l = await carica();
            if (IsDisposed) return;
            if (l == null)
            {
                lblStato.ForeColor = Theme.Rec;
                lblStato.Text = "⚠ " + (errore() ?? "CRM non raggiungibile") + " — premi «Aggiorna la lista» o scegli «Nessun cliente».";
                return;
            }
            elenco.SuspendLayout();
            foreach (var x in l)
            {
                var c = new ClienteCard(x);
                c.Click += (o, e) => Scegli(x);
                schede.Add(c); elenco.Controls.Add(c);
            }
            elenco.ResumeLayout();
            Larghezze();
            Filtra();
            cerca.Focus();
        }

        void Filtra()
        {
            string q = (cerca.Text ?? "").Trim().ToLowerInvariant();
            int vis = 0;
            elenco.SuspendLayout();
            foreach (var c in schede) { bool v = q.Length == 0 || (c.Lavoro.cliente ?? "").ToLowerInvariant().Contains(q); c.Visible = v; if (v) vis++; }
            elenco.ResumeLayout();
            if (schede.Count == 0) { lblStato.ForeColor = Theme.Muted; lblStato.Text = "Nessun cliente in coda con videocassette da registrare."; return; }
            if (vis == 0) { lblStato.ForeColor = Theme.Muted; lblStato.Text = "Nessun cliente con questo nome."; return; }
            lblStato.ForeColor = Theme.Accent;
            lblStato.Text = q.Length > 0 && vis == 1 ? "⏎  Premi Invio o clicca per iniziare con questo cliente" : "👇  Passa sopra al cliente e clicca per iniziare";
        }
    }

    /// <summary>
    /// Fine cassetta, ogni scelta spiegata: ✅ Completata (si conta, il file resta) · 🗑 Scarta (cassetta vuota:
    /// non si conta, il totale del cliente scende, il file si cancella) · 🔄 Rifai (partenza sbagliata: non si conta,
    /// il file si cancella). Sotto la durata minima «Completata» non si può scegliere. Non si chiude senza scegliere.
    /// </summary>
    public class FineCassettaForm : Form
    {
        public string Esito { get; private set; } = "rifai";
        const int W = 640;

        public FineCassettaForm(bool dark, string cliente, int cassetta, int totali, TimeSpan durata, int minutiMinimi)
        {
            Theme.Apply(this, dark);
            Text = "VHSCapture — com'è andata la cassetta?";
            FormBorderStyle = FormBorderStyle.FixedDialog; ControlBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 10f);
            bool troppoBreve = minutiMinimi > 0 && durata.TotalMinutes < minutiMinimi;
            string d = durata.TotalHours >= 1 ? $"{(int)durata.TotalHours}:{durata.Minutes:00}:{durata.Seconds:00}" : $"{durata.Minutes}:{durata.Seconds:00}";
            int y = 18;
            var t = new Label { Text = "Com'è andata la cassetta?", AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Location = new Point(24, y) };
            Controls.Add(t); y += 40;
            var sub = new Label { Text = $"{cliente}  ·  cassetta {cassetta} di {totali}  ·  registrata per {d}", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Location = new Point(26, y) };
            Controls.Add(sub); y += 34;
            if (troppoBreve)
            {
                var avviso = new InfoBox("⚠️", $"La registrazione è durata meno di {minutiMinimi} minuti: con questa durata la cassetta NON si può contare. Scegli «Scarta» se era vuota, «Rifai» se è partita male. (La durata minima si cambia in Impostazioni → CRM.)", W - 48, CrmColori.Rosso) { Location = new Point(24, y) };
                Controls.Add(avviso); y += avviso.Height + 12;
            }
            else
            {
                var info = new InfoBox("ℹ️", "Scegli cosa è successo: il CRM aggiorna da solo il conteggio delle cassette del cliente.", W - 48, Theme.Accent) { Location = new Point(24, y) };
                Controls.Add(info); y += info.Height + 12;
            }
            SceltaCard Scelta(string titolo, string spieg, string effetto, Color col, string esito)
            {
                var c = new SceltaCard(titolo, spieg, effetto, col, W - 48) { Location = new Point(24, y) };
                c.Scelta += (o, e) => { Esito = esito; DialogResult = DialogResult.OK; Close(); };
                Controls.Add(c); y += c.Height + 10;
                return c;
            }
            var ok = Scelta("✅   Completata",
                "La cassetta è stata registrata ed è venuta bene.",
                $"→ la conto: {cassetta} di {totali} fatte · il file resta dov'è",
                CrmColori.Verde, "completata");
            ok.Attiva = !troppoBreve;
            Scelta("🗑   Scarta — cassetta vuota",
                "Dentro non c'era niente: solo nero, neve o schermo blu.",
                $"→ NON la conto · il cliente passa da {totali} a {Math.Max(0, totali - 1)} cassette (scende anche il prezzo) · il file appena registrato viene cancellato",
                CrmColori.Rosso, "scartata");
            Scelta("🔄   Rifai — partenza sbagliata",
                "Hai fermato per errore o vuoi ricominciare questa stessa cassetta.",
                $"→ NON la conto · resta da fare la cassetta {cassetta} · il file appena registrato viene cancellato",
                CrmColori.Arancio, "rifai");
            ClientSize = new Size(W, y + 12);
            Theme.Apply(this, dark);
            foreach (Control c in Controls) if (c is SceltaCard sc) sc.Colori();
        }
    }
}
