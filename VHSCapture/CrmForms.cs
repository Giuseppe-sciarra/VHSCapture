using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    /// «Chi stai riversando?»: «👤 Cliente» mostra la coda del CRM (un clic sul nome e si parte),
    /// «Nessun cliente» registra come sempre. Dal pulsante 👤 in alto si apre direttamente la lista.
    /// </summary>
    public class ClienteForm : Form
    {
        readonly Func<Task<List<CrmLavoro>>> carica;
        readonly Func<string> errore;
        readonly Panel passo1, passo2;
        readonly ListBox lista;
        readonly Label lblStato;
        const int W = 640;
        public CrmLavoro Scelto { get; private set; }

        public ClienteForm(bool dark, Func<Task<List<CrmLavoro>>> caricaLavori, Func<string> ultimoErrore, bool soloLista)
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
            var t2 = new Label { Text = "Quale cliente?", Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI Semibold", 15f) };
            var info2 = new InfoBox("ℹ️",
                "Clicca sul cliente che stai riversando.\n" +
                "«cassetta 3 di 4» = stai per registrare la 3ª delle 4 videocassette della scheda (VHS, S-VHS, VHS-C, 8mm, Hi8, Digital8, MiniDV).\n" +
                "La riga sotto elenca tutti i supporti del cliente (anche DVD, CD, musicassette… che si lavorano a parte).\n" +
                "🔴 = un altro PC sta già registrando questo cliente.",
                W - 48, Theme.Accent) { Dock = DockStyle.Top };
            var spazio = new Panel { Dock = DockStyle.Top, Height = 10 };
            lblStato = new Label { Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI Semibold", 10f), Tag = "keep", TextAlign = ContentAlignment.MiddleLeft };
            lista = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 64, BorderStyle = BorderStyle.None, IntegralHeight = false };
            lista.DrawItem += DisegnaCliente;
            lista.MouseMove += (o, e) => { int i = lista.IndexFromPoint(e.Location); lista.Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; };
            lista.MouseClick += (o, e) =>
            {
                int i = lista.IndexFromPoint(e.Location);
                if (i >= 0 && lista.Items[i] is CrmLavoro l) { Scelto = l; DialogResult = DialogResult.OK; Close(); }
            };
            var giu = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            var bNessuno2 = Ui.Btn("🚫  Nessun cliente (registra come sempre)", "ghost", (o, e) => { Scelto = null; DialogResult = DialogResult.OK; Close(); }, 250);
            var bAggiorna = Ui.Btn("↻  Aggiorna la lista", "ghost", async (o, e) => await Carica(), 150);
            giu.Controls.Add(bNessuno2); giu.Controls.Add(bAggiorna);
            passo2.Controls.Add(lista); passo2.Controls.Add(lblStato); passo2.Controls.Add(spazio); passo2.Controls.Add(info2); passo2.Controls.Add(t2); passo2.Controls.Add(giu);

            Controls.Add(passo2); Controls.Add(passo1);
            ClientSize = new Size(W, soloLista ? 600 : altezzaPasso1);
            Theme.Apply(this, dark);
            foreach (Control c in passo1.Controls) if (c is SceltaCard sc) sc.Colori();
            lista.BackColor = Theme.Back;
            if (soloLista) Shown += (o, e) => MostraLista();
        }

        async void MostraLista()
        {
            passo1.Visible = false; passo2.Visible = true;
            if (ClientSize.Height < 600) { ClientSize = new Size(W, 600); CenterToParent(); }
            await Carica();
        }

        async Task Carica()
        {
            lblStato.ForeColor = Theme.Muted; lblStato.Text = "Carico la coda dal CRM…"; lista.Items.Clear();
            var l = await carica();
            if (IsDisposed) return;
            if (l == null)
            {
                lblStato.ForeColor = Theme.Rec;
                lblStato.Text = "⚠ " + (errore() ?? "CRM non raggiungibile") + " — premi «Aggiorna la lista» o scegli «Nessun cliente».";
                return;
            }
            foreach (var x in l) lista.Items.Add(x);
            lblStato.ForeColor = l.Count == 0 ? Theme.Muted : Theme.Accent;
            lblStato.Text = l.Count == 0 ? "Nessun cliente in coda con cassette da riversare col grabber." : "👇  Clicca sul cliente per iniziare";
        }

        void DisegnaCliente(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || !(lista.Items[e.Index] is CrmLavoro l)) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Back)) g.FillRectangle(bg, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
            var sopraPt = lista.PointToClient(Control.MousePosition);
            bool sopra = r.Contains(sopraPt);
            using (var path = Ui.Rounded(r, 10))
            {
                using var f = new SolidBrush(sopra ? Color.FromArgb(Theme.Dark ? 70 : 30, Theme.Accent) : Theme.Panel);
                g.FillPath(f, path);
                using var pen = new Pen(sopra ? Theme.Accent : Theme.Border, sopra ? 2f : 1f); g.DrawPath(pen, path);
            }
            using (var barra = new SolidBrush(string.IsNullOrEmpty(l.in_registrazione_su) ? Theme.Accent : Theme.Rec))
                g.FillRectangle(barra, new Rectangle(r.X + 1, r.Y + 8, 5, r.Height - 16));
            var fNome = new Font("Segoe UI Semibold", 12f);
            TextRenderer.DrawText(g, l.cliente, fNome, new Rectangle(r.X + 16, r.Y + 7, r.Width - 230, 26), Theme.Fore, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            string conto = $"cassetta {Math.Min(l.prossima, Math.Max(1, l.nastri_totali))} di {l.nastri_totali}";
            TextRenderer.DrawText(g, conto, fNome, new Rectangle(r.Right - 214, r.Y + 7, 200, 26), Theme.Accent, TextFormatFlags.Right);
            string sotto = "Supporti: " + (string.IsNullOrEmpty(l.dettaglio) ? "—" : l.dettaglio) + (string.IsNullOrEmpty(l.stato) ? "" : "   ·   " + l.stato);
            TextRenderer.DrawText(g, sotto, Font, new Rectangle(r.X + 16, r.Y + 35, r.Width - 290, 22), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(l.in_registrazione_su))
                TextRenderer.DrawText(g, "🔴 lo sta registrando " + l.in_registrazione_su, new Font("Segoe UI Semibold", 9.5f), new Rectangle(r.Right - 290, r.Y + 35, 276, 22), Theme.Rec, TextFormatFlags.Right);
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
