using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VHSCapture
{
    /// <summary>
    /// Impostazioni generali (come la finestra Impostazioni di OBS): sezioni a sinistra, tutto globale,
    /// niente legato al singolo dispositivo di ingresso (quello sta nelle Proprietà della sorgente).
    /// </summary>
    public class SettingsForm : Form
    {
        readonly AppSettings s;
        public bool CanvasChanged { get; private set; }

        // Generale
        ComboBox cbTheme; TextBox txtFolder, txtPrefix; NumericUpDown nMaxMin;
        // Registrazione
        ComboBox cbFormat, cbEncoder, cbRc, cbPreset; NumericUpDown nBitrate, nCrf, nSplit, nKey;
        Label lblBitrate, lblCrf, lblPreset;
        TextBox encoderStatus;
        RoundedButton btnProbe, btnSave;
        bool probingEncoders, encoderChosen;
        readonly Dictionary<string, FFmpeg.EncoderProbeResult> encoderResults = new Dictionary<string, FFmpeg.EncoderProbeResult>();
        // Video
        ComboBox cbCanvas, cbFps;
        // Audio
        NumericUpDown nAudioBr; CheckBox chkMono; ComboBox cbMonDev;
        // Avanzate
        CheckBox chkLive, chkPrio, chkDiag, chkIntel;
        ComboBox cbIntelField;
        // Fine cassetta
        CheckBox chkAutoStop, chkTrim, chkAskName, chkLocalFirst; NumericUpDown nAutoSec;
        // CRM
        TextBox txtCrmUrl, txtCrmToken; Label lblCrmProva;
        CheckBox chkCrmAttivo, chkCrmCliente, chkCrmCartella, chkCrmFine, chkCrmRip; NumericUpDown nCrmRip;
        readonly List<Control> crmControlli = new List<Control>();

        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        ListBox nav; Panel host;

        const int LabelW = 190, FieldW = 260, RowH = 36;
        string cartellaPrimaSalvataggio;

        public SettingsForm(AppSettings settings)
        {
            s = settings;
            Text = "Impostazioni";
            FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 600); MinimumSize = new Size(620, 420);
            Font = new Font("Segoe UI", 9.5f);
            Build();
            LoadValues();
            Theme.Apply(this, s.DarkTheme);
            StyleNav();
            Shown += async (o, e) => { FitText(); await RefreshEncoders(false); };
            Resize += (o, e) => FitText();
        }

        // ================= struttura =================
        void Build()
        {
            var pBtn = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 58, Padding = new Padding(12, 10, 12, 10) };
            var btnOk = btnSave = Ui.Btn("Salva", "accent", null, 110);
            var btnCancel = Ui.Btn("Annulla", "normal", null, 110);
            btnOk.Click += (o, e) => { if (SaveValues()) { DialogResult = DialogResult.OK; Close(); } };
            btnCancel.Click += (o, e) => { DialogResult = DialogResult.Cancel; Close(); };
            pBtn.Controls.Add(btnOk); pBtn.Controls.Add(btnCancel);
            AcceptButton = btnOk; CancelButton = btnCancel;

            nav = new ListBox { Dock = DockStyle.Left, Width = 180, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, IntegralHeight = false, Font = new Font("Segoe UI", 10f) };
            nav.Items.AddRange(new object[] { "Generale", "Registrazione", "Video", "Audio", "Avanzate", "CRM" });
            nav.DrawItem += DrawNav;
            nav.SelectedIndexChanged += (o, e) => ShowPage(nav.SelectedItem as string);
            var navWrap = new Panel { Dock = DockStyle.Left, Width = 196, Padding = new Padding(12, 12, 4, 12) };
            navWrap.Controls.Add(nav);

            host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 12, 12, 4) };

            BuildGenerale(); BuildRegistrazione(); BuildVideo(); BuildAudio(); BuildAvanzate(); BuildCrm();
            foreach (var p in pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; host.Controls.Add(p); }

            Controls.Add(host);
            Controls.Add(navWrap);
            Controls.Add(pBtn);
            nav.SelectedIndex = 0;
        }

        void StyleNav() { nav.BackColor = Theme.Back; nav.ForeColor = Theme.Fore; nav.Invalidate(); }

        void DrawNav(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Back)) g.FillRectangle(bg, e.Bounds);
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var r = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
            if (sel)
            {
                using var path = Ui.Rounded(r, 8);
                using var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 50, Theme.Accent));
                g.FillPath(b, path);
            }
            string icon = e.Index switch { 0 => "⚙", 1 => "⏺", 2 => "🖥", 3 => "🔊", 5 => "🔗", _ => "🔧" };
            TextRenderer.DrawText(g, icon, new Font("Segoe UI Symbol", 10.5f), new Rectangle(r.X + 8, r.Y, 26, r.Height), Theme.Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, nav.Items[e.Index].ToString(), nav.Font, new Rectangle(r.X + 38, r.Y, r.Width - 40, r.Height), Theme.Fore, TextFormatFlags.VerticalCenter);
        }

        void ShowPage(string name)
        {
            if (name == null) return;
            foreach (var kv in pages) kv.Value.Visible = kv.Key == name;
        }

        // ---- helper di impaginazione: righe a griglia fissa, tutte alte uguali ----
        Panel Page(string name)
        {
            var p = new Panel { AutoScroll = true, Padding = new Padding(4) };
            pages[name] = p;
            return p;
        }

        Card Section(Panel page, string title)
        {
            var c = new Card { HeaderText = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 34, 16, 12), Margin = new Padding(0, 0, 0, 10) };
            var t = new TableLayoutPanel { ColumnCount = 3, RowCount = 0, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(c.Padding.Left, c.Padding.Top), Tag = "panel" };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelW));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, FieldW + 8));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            c.Controls.Add(t);
            c.Tag = t;
            // le sezioni si impilano dall'alto: aggiunte in testa → servono in ordine inverso
            page.Controls.Add(c); page.Controls.SetChildIndex(c, 0);
            // spaziatore tra le card
            var sp = new Panel { Dock = DockStyle.Top, Height = 10 };
            page.Controls.Add(sp); page.Controls.SetChildIndex(sp, 0);
            return c;
        }

        static TableLayoutPanel T(Card c) => (TableLayoutPanel)c.Tag;

        /// <summary>
        /// Adatta i testi alla larghezza REALE della finestra: note, suggerimenti e caselle di spunta vanno a capo
        /// invece di uscire; i menu a tendina si aprono larghi quanto la voce più lunga.
        /// </summary>
        void FitText()
        {
            foreach (var page in pages.Values)
            {
                int avail = Math.Max(300, page.ClientSize.Width - 32 - 24 - SystemInformation.VerticalScrollBarWidth);
                void Walk(Control parent)
                {
                    foreach (Control c in parent.Controls)
                    {
                        if (c is Label lb && lb.Name == "note") lb.MaximumSize = new Size(avail, 0);
                        else if (c is Label hb && hb.Name == "hint") hb.MaximumSize = new Size(Math.Max(200, avail - LabelW), 0);
                        else if (c is CheckBox cb && cb.Name == "chk") cb.MaximumSize = new Size(avail, 0);
                        else if (c is ComboBox co) FitDropDown(co);
                        if (c.HasChildren) Walk(c);
                    }
                }
                page.SuspendLayout(); Walk(page); page.ResumeLayout(true);
            }
        }

        static void FitDropDown(ComboBox co)
        {
            int w = co.Width;
            foreach (var it in co.Items) w = Math.Max(w, TextRenderer.MeasureText(it?.ToString() ?? "", co.Font).Width + 30);
            co.DropDownWidth = Math.Min(w, 900);
        }

        static Label Row(Card c, string label, Control field, string hint = null)
        {
            var t = T(c);
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, RowH));
            var l = new Label { Text = label, AutoSize = false, Width = LabelW - 8, Height = RowH, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            t.Controls.Add(l, 0, r);
            field.Margin = new Padding(0, (RowH - field.Height) / 2, 8, 0);
            if (field is ComboBox || field is NumericUpDown || field is TextBox) field.Width = FieldW;
            t.Controls.Add(field, 1, r);
            t.RowCount = r + 1;
            if (hint != null)
            {
                // sotto il campo, a capo secondo la larghezza della finestra (vedi FitText)
                var h = new Label { Text = hint, AutoSize = true, Tag = "muted", Name = "hint", Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(FieldW + 200, 0) };
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                t.Controls.Add(h, 1, r + 1); t.SetColumnSpan(h, 2);
                t.RowCount = r + 2;
            }
            return l;
        }

        static void Note(Card c, string text)
        {
            var t = T(c);
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = text, AutoSize = true, Tag = "muted", Name = "note", MaximumSize = new Size(LabelW + FieldW + 100, 0), Margin = new Padding(0, 2, 0, 8) };
            t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 3);
            t.RowCount = r + 1;
        }

        static CheckBox Check(Card c, string text, string description = null)
        {
            var t = T(c);
            int r = t.RowCount;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var ch = new CheckBox { Text = text, AutoSize = true, Name = "chk", Margin = new Padding(0, 6, 0, 4), MaximumSize = new Size(LabelW + FieldW + 100, 0) };
            t.Controls.Add(ch, 0, r); t.SetColumnSpan(ch, 3);
            t.RowCount = r + 1;
            if (description != null) Note(c, "      " + description);
            return ch;
        }

        static ComboBox Combo(params object[] items) { var c = new SafeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Height = 26 }; c.Items.AddRange(items); return c; }
        static ComboBox EditCombo(params object[] items) { var c = new SafeCombo { DropDownStyle = ComboBoxStyle.DropDown, Height = 26 }; c.Items.AddRange(items); return c; }
        static NumericUpDown Num(int min, int max, int step) => new SafeNumeric { Minimum = min, Maximum = max, Increment = step, Height = 26 };

        // ================= pagine =================
        void BuildGenerale()
        {
            var p = Page("Generale");
            var aspetto = Section(p, "Aspetto");
            cbTheme = Combo("Chiaro", "Scuro");
            Row(aspetto, "Tema", cbTheme);

            var sal = Section(p, "Dove salvare le registrazioni");
            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "panel" };
            txtFolder = new TextBox { Width = FieldW - 96, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 6, 0) };
            var browse = Ui.Btn("Sfoglia…", "ghost"); browse.MinimumSize = new Size(88, 28); browse.Height = 28; browse.Margin = new Padding(0);
            browse.Click += (o, e) =>
            {
                using var d = new FolderBrowserDialog { SelectedPath = txtFolder.Text };
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            };
            folderRow.Controls.Add(txtFolder); folderRow.Controls.Add(browse);
            folderRow.Height = 30;
            Row(sal, "Cartella (anche di rete)", folderRow);
            chkLocalFirst = Check(sal, "Cartella di rete: passa prima dal disco del PC (facoltativo)",
                "Spenta (predefinito, come OBS): il file si scrive direttamente nella cartella scelta, niente sul PC. Accesa: si scrive sul PC e il file viene copiato in rete a pezzi mentre registri, utile solo se la rete è instabile.");
            txtPrefix = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            Row(sal, "Prefisso nome file", txtPrefix, "→ Prefisso_Nome_2026-09-27_14-30-00.mp4");
            nMaxMin = Num(0, 600, 5);
            Row(sal, "Stop automatico (minuti)", nMaxMin, "0 = mai. Es. 245 per una E-240");
        }

        void BuildRegistrazione()
        {
            var p = Page("Registrazione");
            var file = Section(p, "File");
            cbFormat = Combo("MP4 normale (come OBS)", "MP4 frammentato", "MKV → MP4 alla fine");
            Row(file, "Formato", cbFormat);
            Note(file, "MP4 normale: come OBS con \"MPEG-4 (.mp4)\", un indice unico scritto alla chiusura, i lettori lo aprono subito. Se salta la corrente durante la registrazione il file non è recuperabile. MP4 frammentato: resta leggibile anche dopo un crash, ma lettori come MPC-HC ci mettono di più ad aprirlo.");
            nSplit = Num(0, 600, 5);
            Row(file, "Dividi file ogni (minuti)", nSplit, "0 = no. Utile per chiavette FAT32 (4 GB)");

            var fine = Section(p, "Fine cassetta");
            chkAutoStop = Check(fine, "Ferma da sola quando la cassetta finisce", "Riconosce lo sfondo del lettore o della videocamera (blu, nero, grigio, neve, anche con le scritte OSD): quasi tutto il quadro dello stesso colore, niente movimento, nessuna voce o musica. Un disturbo di un attimo non azzera il conteggio. Si arma dopo 10 s di filmato.");
            nAutoSec = Num(60, 900, 10);
            Row(fine, "Secondi di solo sfondo", nAutoSec, "Consigliato 120: un nero lungo dentro il filmato non ferma la registrazione");
            chkTrim = Check(fine, "Taglia la parte uniforme finale dal file", "Senza ricodifica, pochi secondi anche per file lunghi.");
            chkAskName = Check(fine, "Chiedi il nome della cassetta alla fine", "Il file viene rinominato \"Nome cassetta.mp4\". Se annulli resta il nome automatico.");
            chkAutoStop.CheckedChanged += (o, e) => { nAutoSec.Enabled = chkTrim.Enabled = chkAutoStop.Checked; };

            var enc = Section(p, "Encoder video");
            cbEncoder = Combo();
            Row(enc, "Encoder", cbEncoder);
            btnProbe = Ui.Btn("Verifica encoder", "normal", null, 180);
            btnProbe.Click += async (o, e) => await RefreshEncoders(true);
            Row(enc, "Rilevamento", btnProbe);
            encoderStatus = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Width = LabelW + FieldW, Height = 92, Margin = new Padding(0, 2, 0, 8) };
            var et = T(enc); int er = et.RowCount++;
            et.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            et.Controls.Add(encoderStatus, 0, er); et.SetColumnSpan(encoderStatus, 2);
            cbEncoder.SelectionChangeCommitted += (o, e) => encoderChosen = true;
            cbRc = Combo("CBR", "VBR", "CRF");
            Row(enc, "Controllo bitrate", cbRc);
            nBitrate = Num(500, 100000, 500);
            lblBitrate = Row(enc, "Bitrate video (kbps)", nBitrate, "1080p: 10000–15000 · 1080p60: 15000–20000");
            nCrf = Num(0, 51, 1);
            lblCrf = Row(enc, "Qualità CRF", nCrf, "più basso = meglio, 18 ottimo");
            cbPreset = Combo("ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow");
            lblPreset = Row(enc, "Preset x264", cbPreset, "veryfast se la CPU arranca");
            nKey = Num(1, 10, 1);
            Row(enc, "Keyframe ogni (secondi)", nKey, "OBS usa 2");
            Note(enc, "La pipeline e l'encoder hardware restano attivi: Registra avvia la scrittura senza riaprire il grabber.");
            cbRc.SelectedIndexChanged += (o, e) => UpdateEnabled();
            cbEncoder.SelectedIndexChanged += (o, e) => { UpdateEnabled(); ShowEncoderStatus(); };
        }

        void BuildVideo()
        {
            var p = Page("Video");
            var v = Section(p, "Canvas (= risoluzione del file)");
            cbCanvas = EditCombo("3840x2160", "2560x1440", "1920x1080", "1600x900", "1280x720", "1024x576", "854x480",
                                 "2048x1536", "1600x1200", "1440x1080", "1280x960", "1024x768", "960x720", "800x600", "768x576", "640x480",
                                 "720x576", "720x480", "704x576", "704x480", "1920x1200", "1680x1050", "1280x800", "2560x1080", "3440x1440");
            Row(v, "Risoluzione", cbCanvas, "scegli o scrivi LxA");
            cbFps = EditCombo("23.976", "24", "25", "29.97", "30", "48", "50", "59.94", "60", "75", "90", "100", "120", "144");
            Row(v, "Frame rate", cbFps);
            Note(v, "Il frame rate del canvas deve combaciare con la sorgente, se no il movimento scatta (come in OBS):\nVHS PAL con Yadif 2x → 50 · webcam/grabber a 30 → 30 · grabber HDMI a 60 → 60.");
        }

        void BuildAudio()
        {
            var p = Page("Audio");
            var a = Section(p, "Registrazione");
            nAudioBr = Num(64, 320, 32);
            Row(a, "Bitrate AAC (kbps)", nAudioBr);
            chkMono = Check(a, "Registra in mono", "Le VHS mono hanno lo stesso segnale su L e R.");
            var m = Section(p, "Ascolto (🎧 nel mixer)");
            cbMonDev = Combo();
            Row(m, "Uscita audio", cbMonDev);
        }

        void BuildAvanzate()
        {
            var p = Page("Avanzate");
            var a = Section(p, "Prestazioni");
            chkIntel = Check(a, "Accelerazione GPU (Intel)", "Con encoder Intel QuickSync: deinterlacciamento, scala e bande nere su GPU per un solo grabber. Scene o driver non compatibili usano i filtri CPU. Anteprima e registrazione mantengono il frame rate completo.");
            cbIntelField = Combo("Superiore prima (TFF)", "Inferiore prima (BFF)");
            Row(a, "Ordine campi Intel", cbIntelField, "Se il movimento va avanti e indietro, prova l'altro ordine.");
            chkPrio = Check(a, "Priorità alta a ffmpeg", "Come la priorità del processo di OBS: meno frame persi se il PC fa altro.");
            chkDiag = Check(a, "Diagnostica dell'anteprima nel Log ogni 5 s", "Serve solo per capire eventuali scatti dell'anteprima.");
            chkLive = Check(a, "Modifiche delle sorgenti al volo (zmq)", FFmpeg.HasZmq ? "Sposta, ritaglia e regola i colori senza riavviare l'anteprima." : "ffmpeg senza zmq: ogni modifica riavvia l'anteprima.");
        }

        // ================= valori =================
        static void Sel(ComboBox cb, string v)
        {
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals(cb.Items[i].ToString(), v, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
        }

        void BuildCrm()
        {
            var p = Page("CRM");
            var gen = Section(p, "Collegamento");
            chkCrmAttivo = Check(gen, "Usa il collegamento al CRM",
                "Spento: VHSCapture registra come prima, senza clienti né conteggio. Indirizzo, token e opzioni qui sotto restano salvati per quando lo riaccendi.");
            chkCrmAttivo.Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
            chkCrmAttivo.CheckedChanged += (o, e) => AggiornaCrmAbilitati();
            var col = Section(p, "Indirizzo e token");
            txtCrmUrl = new TextBox { BorderStyle = BorderStyle.FixedSingle };
            Row(col, "Indirizzo del CRM", txtCrmUrl, "es. https://crm.tastieredigitali.it");
            txtCrmToken = new TextBox { BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true };
            Row(col, "Token di questo PC", txtCrmToken, "Nel CRM: Controllo PC → la postazione → 🎬 VHSCapture → Genera il token");
            var provaRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "panel" };
            var bProva = Ui.Btn("Prova collegamento", "ghost"); bProva.MinimumSize = new Size(150, 28); bProva.Height = 28; bProva.Margin = new Padding(0);
            bProva.Click += async (o, e) =>
            {
                lblCrmProva.Text = "Provo…"; bProva.Enabled = false;
                using var c = new CrmClient(s);
                var (ok, msg) = await c.Prova(txtCrmUrl.Text, txtCrmToken.Text);
                lblCrmProva.Text = (ok ? "✅ " : "⚠ ") + msg; lblCrmProva.ForeColor = ok ? Theme.Fore : Theme.Rec; bProva.Enabled = true;
            };
            provaRow.Controls.Add(bProva); provaRow.Height = 30;
            Row(col, "", provaRow);
            lblCrmProva = new Label { AutoSize = true, Name = "note", Margin = new Padding(0, 2, 0, 8) };
            var tc = T(col); int r = tc.RowCount; tc.RowStyles.Add(new RowStyle(SizeType.AutoSize)); tc.Controls.Add(lblCrmProva, 0, r); tc.SetColumnSpan(lblCrmProva, 3); tc.RowCount = r + 1;

            var opz = Section(p, "Questo PC");
            Note(opz, "Valgono solo per questo PC e si vedono anche nel CRM (Controllo PC → 🎬 VHSCapture): vince l'ultima modifica. Le opzioni spente restano salvate.");
            chkCrmCliente = Check(opz, "Chiedi il cliente quando parte la registrazione",
                "La registrazione parte SUBITO; la domanda arriva mentre registra, solo se non c'è un cliente in corso (la prima volta e quando hai finito le sue videocassette). Scelto il cliente, a fine registrazione il file viene spostato nella sua cartella. Senza scelta entro 60 secondi la finestra si chiude e si resta nella cartella predefinita.");
            chkCrmCartella = Check(opz, "Salva nella cartella del cliente", "«Nome Cognome» dentro la cartella di salvataggio del PC (Generale), creata se non c'è.");
            chkCrmRip = Check(opz, "Partenza sbagliata: se fermi entro pochi secondi non chiedere niente",
                "La cassetta non si conta, il file breve si cancella e la prossima registrazione riparte dalla stessa cassetta. Zero clic.");
            nCrmRip = Num(10, 600, 10);
            Row(opz, "Entro (secondi)", nCrmRip);
            chkCrmRip.CheckedChanged += (o, e) => nCrmRip.Enabled = chkCrmAttivo.Checked && chkCrmRip.Checked;
            chkCrmFine = Check(opz, "A fine cassetta chiedi ✅ Tieni / 🗑 Scarta / 🔄 Ricomincia",
                "Tieni (Invio) = la conta e il file resta, qualunque durata. Scarta = cassetta vuota: non si conta, il totale del cliente scende, il file si cancella. Ricomincia = video da rifare (es. test testine): file cancellato, stessa cassetta, la registrazione riparte subito. Spento = conta sempre senza chiedere.");
            crmControlli.AddRange(new Control[] { txtCrmUrl, txtCrmToken, chkCrmCliente, chkCrmCartella, chkCrmFine, chkCrmRip, nCrmRip });
        }

        /// <summary>CRM spento: le voci si vedono disattivate ma restano compilate e salvate.</summary>
        void AggiornaCrmAbilitati()
        {
            foreach (var c in crmControlli) c.Enabled = chkCrmAttivo.Checked;
            nCrmRip.Enabled = chkCrmAttivo.Checked && chkCrmRip.Checked;
        }

        void LoadValues()
        {
            cbTheme.SelectedIndex = s.DarkTheme ? 1 : 0;
            txtFolder.Text = s.OutputFolder;
            chkLocalFirst.Checked = s.RecordLocalFirst;
            txtPrefix.Text = s.FilePrefix;
            nMaxMin.Value = Math.Clamp(s.MaxMinutes, 0, 600);

            cbFormat.SelectedIndex = s.SafeRecording ? 2 : (s.FragmentedMp4 ? 1 : 0);
            nSplit.Value = Math.Clamp(s.SplitMinutes, 0, 600);
            foreach (var e in new[] { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" }) cbEncoder.Items.Add(EncLabel(e));
            Sel(cbEncoder, EncLabel(s.Encoder));
            if (cbEncoder.SelectedIndex < 0 && cbEncoder.Items.Count > 0) cbEncoder.SelectedIndex = 0;
            Sel(cbRc, s.RateControl);
            if (cbRc.SelectedIndex < 0) cbRc.SelectedIndex = 0;
            nBitrate.Value = Math.Clamp(s.VideoBitrate, 500, 100000);
            nCrf.Value = Math.Clamp(s.Crf, 0, 51);
            Sel(cbPreset, s.Preset);
            nKey.Value = Math.Clamp(s.KeyframeSec, 1, 10);

            cbCanvas.Text = $"{s.CanvasW}x{s.CanvasH}";
            cbFps.Text = s.Fps;

            nAudioBr.Value = Math.Clamp(s.AudioBitrate, 64, 320);
            chkMono.Checked = s.AudioMono;
            cbMonDev.Items.Add("Predefinita di Windows");
            foreach (var d in AudioMonitor.ListOutputs()) cbMonDev.Items.Add(d);
            cbMonDev.SelectedIndex = Math.Clamp(s.MonitorDevice + 1, 0, cbMonDev.Items.Count - 1);

            chkPrio.Checked = s.HighPriority;
            chkAutoStop.Checked = s.AutoStopOnBlank;
            nAutoSec.Value = Math.Clamp(s.AutoStopSeconds, 60, 900);
            chkTrim.Checked = s.TrimBlankTail;
            chkAskName.Checked = s.AskNameAtEnd;
            nAutoSec.Enabled = chkTrim.Enabled = chkAutoStop.Checked;
            chkLive.Checked = s.LiveControl;
            chkDiag.Checked = s.DiagLog;
            chkIntel.Checked = s.IntelGpu;
            cbIntelField.SelectedIndex = s.IntelFieldOrder == "bff" ? 1 : 0;
            chkCrmAttivo.Checked = s.CrmAttivo;
            txtCrmUrl.Text = s.CrmUrl ?? "";
            txtCrmToken.Text = s.CrmToken ?? "";
            chkCrmCliente.Checked = s.CrmChiediCliente;
            chkCrmCartella.Checked = s.CrmCartellaCliente;
            chkCrmFine.Checked = s.CrmChiediFine;
            chkCrmRip.Checked = s.CrmRipartenzaAttiva;
            nCrmRip.Value = Math.Clamp(s.CrmRipartenzaSec, 10, 600);
            AggiornaCrmAbilitati();
            UpdateEnabled();
        }

        bool SaveValues()
        {
            if (probingEncoders) return false;
            cartellaPrimaSalvataggio = s.OutputFolder;
            string selected = EncFromLabel(cbEncoder.Text);
            if ((encoderChosen || selected != s.Encoder) && (!encoderResults.TryGetValue(selected, out var probe) || !probe.Works))
            {
                MessageBox.Show(this, "Questo encoder non ha superato la verifica. Il motivo è nel riquadro Encoder video. Premi Verifica encoder per riprovare.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                nav.SelectedItem = "Registrazione";
                return false;
            }

            var p = cbCanvas.Text.Trim().ToLowerInvariant().Split('x');
            if (p.Length != 2 || !int.TryParse(p[0], out int cw) || !int.TryParse(p[1], out int ch) || cw < 64 || ch < 64)
            {
                nav.SelectedItem = "Video";
                MessageBox.Show(this, "Risoluzione del canvas non valida (es. 1920x1080).", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            cw -= cw % 2; ch -= ch % 2;
            CanvasChanged = cw != s.CanvasW || ch != s.CanvasH || cbFps.Text.Trim() != s.Fps;
            s.CanvasW = cw; s.CanvasH = ch;
            s.Fps = string.IsNullOrWhiteSpace(cbFps.Text) ? "25" : cbFps.Text.Trim();

            s.DarkTheme = cbTheme.SelectedIndex == 1;
            s.OutputFolder = txtFolder.Text.Trim();
            s.RecordLocalFirst = chkLocalFirst.Checked;
            s.FilePrefix = string.IsNullOrWhiteSpace(txtPrefix.Text) ? "VHS" : txtPrefix.Text.Trim();
            s.MaxMinutes = (int)nMaxMin.Value;

            s.SafeRecording = cbFormat.SelectedIndex == 2;
            s.FragmentedMp4 = cbFormat.SelectedIndex == 1;
            s.SplitMinutes = (int)nSplit.Value;
            var enc = EncFromLabel(cbEncoder.Text);
            if (encoderChosen || enc != s.Encoder) s.EncoderUserSet = true;   // da qui in poi l'encoder lo decide l'utente
            s.Encoder = enc;
            s.RateControl = string.IsNullOrEmpty(cbRc.Text) ? "CBR" : cbRc.Text;
            s.VideoBitrate = (int)nBitrate.Value;
            s.Crf = (int)nCrf.Value;
            if (!string.IsNullOrEmpty(cbPreset.Text)) s.Preset = cbPreset.Text;
            s.KeyframeSec = (int)nKey.Value;

            s.AudioBitrate = (int)nAudioBr.Value;
            s.AudioMono = chkMono.Checked;
            s.MonitorDevice = cbMonDev.SelectedIndex - 1;

            s.HighPriority = chkPrio.Checked;
            s.AutoStopOnBlank = chkAutoStop.Checked;
            s.AutoStopSeconds = (int)nAutoSec.Value;
            s.TrimBlankTail = chkTrim.Checked;
            s.AskNameAtEnd = chkAskName.Checked;
            s.LiveControl = chkLive.Checked;
            s.DiagLog = chkDiag.Checked;
            s.IntelGpu = chkIntel.Checked;
            s.IntelFieldOrder = cbIntelField.SelectedIndex == 1 ? "bff" : "tff";

            // CRM: se cambia qualcosa della configurazione di questo PC la segno «modificata adesso» (vince sulla copia nel CRM)
            bool cambiata = s.CrmChiediCliente != chkCrmCliente.Checked || s.CrmCartellaCliente != chkCrmCartella.Checked
                         || s.CrmChiediFine != chkCrmFine.Checked || s.CrmRipartenzaAttiva != chkCrmRip.Checked
                         || s.CrmRipartenzaSec != (int)nCrmRip.Value || !string.Equals(cartellaPrimaSalvataggio, s.OutputFolder, StringComparison.OrdinalIgnoreCase);
            s.CrmAttivo = chkCrmAttivo.Checked;
            s.CrmUrl = txtCrmUrl.Text.Trim();
            s.CrmToken = txtCrmToken.Text.Trim();
            s.CrmChiediCliente = chkCrmCliente.Checked;
            s.CrmCartellaCliente = chkCrmCartella.Checked;
            s.CrmChiediFine = chkCrmFine.Checked;
            s.CrmRipartenzaAttiva = chkCrmRip.Checked;
            s.CrmRipartenzaSec = (int)nCrmRip.Value;
            if (cambiata) s.CrmConfigAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
            s.Save();
            return true;
        }

        async Task RefreshEncoders(bool refresh)
        {
            if (probingEncoders) return;
            probingEncoders = true; btnProbe.Enabled = btnSave.Enabled = false;
            encoderStatus.Text = "Verifica in corso…";
            try
            {
                var results = await Task.Run(() => FFmpeg.ProbeH264Encoders(refresh));
                if (IsDisposed) return;
                if (refresh)
                {
                    // verifica chiesta a mano: si aggiorna quella salvata e al prossimo avvio si riprova anche la GPU Intel via D3D11
                    var ok = results.Where(x => x.Works).Select(x => x.Encoder).ToList();
                    if (ok.Count > 0) { s.EncodersWorking = ok; s.EncodersStamp = FFmpeg.BinaryStamp(); }
                    s.QsvBackend = "";
                }
                string selected = EncFromLabel(cbEncoder.Text);
                encoderResults.Clear(); cbEncoder.Items.Clear();
                foreach (var result in results)
                {
                    encoderResults[result.Encoder] = result;
                    cbEncoder.Items.Add(EncLabel(result.Encoder) + (result.Works ? " — pronto" : " — non disponibile"));
                }
                for (int i = 0; i < cbEncoder.Items.Count; i++)
                    if (EncFromLabel(cbEncoder.Items[i].ToString()) == selected) { cbEncoder.SelectedIndex = i; break; }
            }
            catch (Exception ex) { if (!IsDisposed) encoderStatus.Text = "Verifica non completata: " + ex.Message; }
            finally
            {
                probingEncoders = false;
                if (!IsDisposed) { btnProbe.Enabled = btnSave.Enabled = true; ShowEncoderStatus(); }
            }
        }

        void ShowEncoderStatus()
        {
            if (encoderStatus == null || probingEncoders) return;
            encoderStatus.Text = encoderResults.TryGetValue(EncFromLabel(cbEncoder.Text), out var result)
                ? result.Detail : "Verifica non ancora completata. La presenza in elenco non certifica una scheda installata: controlla l'esito della prova.";
        }

        void UpdateEnabled()
        {
            string rc = cbRc.Text;
            bool x264 = EncFromLabel(cbEncoder.Text) == "libx264";
            nBitrate.Enabled = lblBitrate.Enabled = rc != "CRF";
            nCrf.Enabled = lblCrf.Enabled = rc == "CRF";
            cbPreset.Enabled = lblPreset.Enabled = x264;
        }

        static string EncLabel(string e) => e switch
        {
            "h264_nvenc" => "NVIDIA NVENC (h264_nvenc)",
            "h264_qsv" => "Intel QuickSync (h264_qsv)",
            "h264_amf" => "AMD AMF (h264_amf)",
            _ => "Software x264 (libx264)",
        };
        static string EncFromLabel(string l)
        {
            if (l.Contains("nvenc")) return "h264_nvenc";
            if (l.Contains("qsv")) return "h264_qsv";
            if (l.Contains("amf")) return "h264_amf";
            return "libx264";
        }
    }
}
