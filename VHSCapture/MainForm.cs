using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VHSCapture
{
    public class MainForm : Form
    {
        readonly AppSettings settings = AppSettings.Load();
        readonly CaptureEngine engine = new CaptureEngine();

        SplitContainer splitLog, splitMain, splitRight;
        Card top, cardSources, cardMixer, cardLog, status;
        CanvasView canvas;
        RoundedButton btnRec, btnPause, btnSettings, btnFolder, btnTheme, btnLog;
        DateTime? pausedSince; TimeSpan pausedTotal;
        RoundedButton btnAdd, btnRemove, btnProps, btnUp, btnDown;
        TextBox txtName, txtLog;
        Label lblStatus; Pill lblRec;
        SourceList srcList;
        RoundedButton btnPanels; Label lblName;

        // ── collegamento col CRM (Crm.cs, CrmForms.cs) ──
        CrmClient crm;
        CrmLavoro lavoro;                         // cliente scelto (null = nessun cliente)
        int cassettaInCorso;                      // numero della cassetta che si sta registrando
        TimeSpan durataStop;                      // durata misurata al clic su Stop (al netto delle pause)
        RoundedButton btnCliente;
        Label lblCrm; Panel crmWrap;
        System.Windows.Forms.Timer crmTimer;
        bool crmOccupato;
        bool nessunCliente;                       // scelto «Nessun cliente»: non si chiede più finché non scegli un cliente dal pulsante 👤
        ClienteForm sceltaAperta;                 // «Di chi è questa cassetta?» aperta mentre si registra (non blocca niente)
        bool daSpostare;                          // cliente scelto a registrazione già partita: a fine registrazione i file si SPOSTANO nella sua cartella
        bool ricomincia;                          // «Ricomincia la cassetta»: finita la chiusura, la registrazione riparte da sola
        ProgressoCliente prog; Panel progWrap;    // fascia dell'avanzamento del cliente in corso
        List<CrmPostazione> postazioni = new List<CrmPostazione>();
        bool CrmAttivo => crm != null && crm.Configurato;
        Panel mixer;
        readonly Dictionary<string, MixerRow> mixerRows = new Dictionary<string, MixerRow>();
        System.Windows.Forms.Timer timer, restartTimer;

        DateTime recStart;
        string recFile, finalFile, recBase, recFolder;
        readonly AudioMonitor monitor = new AudioMonitor();
        EngineStats lastStats; double lastCpu;
        RoundedButton btnMonitor;
        bool finalizing, startingRecording, closingApp, syncingList;
        bool CaptureBusy => engine.IsRecording || startingRecording || finalizing;
        int frames; DateTime lastFrameAt = DateTime.MinValue, previewStartedAt;
        readonly System.Text.StringBuilder runLog = new System.Text.StringBuilder();
        bool autoRetried, devicesResolved;
        // diagnostica anteprima
        readonly System.Diagnostics.Stopwatch paintClock = System.Diagnostics.Stopwatch.StartNew();
        double lastPaint, paintMaxGap, paintWindowStart; long painted;
        string previewDiag = "";
        readonly List<(double af, double ag, double pf, double pg, long dr, double src, double outf)> diagAcc = new List<(double, double, double, double, long, double, double)>();
        // fine cassetta
        DateTime lastSignalAt = DateTime.MinValue;
        DateTime? blankSince; string blankKind = ""; double blankStartRecSec = -1; int contentSamples; bool autoStopped;
        DateTime blankFloor = DateTime.MinValue, lastSignalDiag = DateTime.MinValue;   // lo sfondo conta solo da inizio registrazione / ripresa
        bool stallWarned;
        string moveTo;   // cartella di rete di destinazione quando si registra prima sul PC
        NetworkMirror mirror;   // copia in rete a pezzi durante la registrazione

        public MainForm()
        {
            // versione nel titolo: così si vede subito quale build sta girando
            var ver = typeof(MainForm).Assembly.GetName().Version;
            Text = $"VHSCapture  v{ver.Major}.{ver.Minor}.{ver.Build}";
            Font = new Font("Segoe UI", 9.5f);
            MinimumSize = new Size(640, 420);
            ClientSize = new Size(settings.WindowW, settings.WindowH);
            StartPosition = FormStartPosition.CenterScreen;
            if (settings.WindowMax) WindowState = FormWindowState.Maximized;
            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.F5) { e.Handled = true; if (!CaptureBusy) StartPreview(); return; }
                if (e.KeyCode == Keys.F10) { e.Handled = true; TogglePause(); return; }
                if (e.KeyCode != Keys.F9) return;
                e.Handled = true;
                ToggleRecording();
            };
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Theme.Apply(this, settings.DarkTheme);
            RefreshSourceList();
            RebuildMixer();

            engine.FrameAvailable += () => canvas?.NotifyFrame();
            engine.AudioLevels += (id, rl, pl, rr, pr) =>
            {
                RecordAudioStats(Math.Max(rl, rr), Math.Max(pl, pr));
                if (IsHandleCreated) try { BeginInvoke(new Action(() => { if (mixerRows.TryGetValue(id, out var row)) row.Meter.SetLevels(rl, pl, rr, pr); })); } catch { }
            };
            engine.Stats += st => lastStats = st;
            // spostamenti/colore che il grafo attuale non può applicare al volo: riavvio breve dell'anteprima
            engine.NeedsRestart += () => { if (IsHandleCreated) try { BeginInvoke(new Action(() => { if (!CaptureBusy) ScheduleRestart(); else AppendLog("La modifica si applica alla fine della registrazione"); })); } catch { } };
            engine.SignalState += (id, sec, detail) => { if (IsHandleCreated) try { BeginInvoke(new Action(() => OnSignal(sec, detail))); } catch { } };
            engine.MonitorData += (d, n) => monitor.Add(d, n);
            engine.Log += l => { lock (runLog) { if (runLog.Length < 20000) runLog.AppendLine(l); } AppendLog(l); };
            engine.Exited += OnEngineExited;

            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (o, e) =>
            {
                UpdateStatus();
                if (prog != null && progWrap.Visible)
                {
                    bool reg = engine.IsRecording && !finalizing;
                    if (prog.Registrando != reg) { prog.Registrando = reg; prog.Corrente = reg ? cassettaInCorso : 0; prog.Invalidate(); }
                    prog.Lampeggia();
                }
            };
            timer.Start();
            crm = new CrmClient(settings) { Versione = $"{ver.Major}.{ver.Minor}.{ver.Build}" };
            crmTimer = new System.Windows.Forms.Timer { Interval = 20000 };
            crmTimer.Tick += (o, e) => CrmBattito();
            restartTimer = new System.Windows.Forms.Timer { Interval = 450 };
            restartTimer.Tick += (o, e) => { restartTimer.Stop(); if (!CaptureBusy) StartPreview(); };

            Shown += (o, e) =>
            {
                ApplySplitters();          // qui la finestra ha già la dimensione finale (anche se massimizzata)
                splittersReady = true;
                BeginInvoke(new Action(CrmAvvio));   // «Chi stai riversando?» a finestra già visibile
            };
            Load += (o, e) =>
            {
                ApplyCompact();
                if (!FFmpeg.Exists)
                {
                    MessageBox.Show(this, "ffmpeg.exe non trovato accanto a VHSCapture.exe.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                engine.ResetGpuRetry(settings.QsvBackend);
                if (settings.Sources.Count == 0) AutoAddGrabber();
                else StartPreview();
                CheckEncoderAsync();
                OfferPendingMoves();
            };
            FormClosing += (o, e) =>
            {
                if (finalizing) { e.Cancel = true; return; }
                crmTimer?.Stop();
                if (engine.IsRecording)
                {
                    if (MessageBox.Show(this, "Stai registrando. Fermare e uscire?", "VHSCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    { e.Cancel = true; return; }
                    engine.StopRecording();   // chiude bene il file prima di uscire
                }
                closingApp = true;
                engine.Stop();
                monitor.Stop();
                settings.WindowMax = WindowState == FormWindowState.Maximized;
                if (WindowState == FormWindowState.Normal) { settings.WindowW = ClientSize.Width; settings.WindowH = ClientSize.Height; }
                settings.ShowLog = !splitLog.Panel2Collapsed;
                settings.RightPanelHidden = splitMain.Panel2Collapsed;
                if (splittersReady)
                {
                    if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
                    settings.MixerH = splitRight.Height - splitRight.SplitterDistance - splitRight.SplitterWidth;
                    if (!splitLog.Panel2Collapsed) settings.LogH = splitLog.Height - splitLog.SplitterDistance - splitLog.SplitterWidth;
                }
                settings.Save();
            };
        }

        // ---------------- UI ----------------

        void BuildUi()
        {
            const int gap = 10;

            // ---- barra superiore ----
            top = new Card { Dock = DockStyle.Top, Height = 62, Padding = new Padding(12, 12, 12, 12), Margin = new Padding(0), Radius = 10 };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Tag = "panel" };
            // come OBS: un solo pulsante. Rosso = avvia, chiaro = ferma. L'anteprima parte da sola (F5 per riavviarla).
            btnRec = Ui.Btn("⏺   Avvia registrazione", "rec", (o, e) => ToggleRecording(), 230);
            tips.SetToolTip(btnRec, "Avvia / ferma registrazione (F9)");
            btnPause = Ui.Btn("⏸   Pausa", "ghost", (o, e) => TogglePause(), 120);
            btnPause.Visible = false;
            tips.SetToolTip(btnPause, "Pausa / riprendi (F10): salti un pezzo senza fare due file");

            lblName = new Label { Text = "Nome file", AutoSize = true, Tag = "muted", Margin = new Padding(20, 10, 6, 0) };
            txtName = new TextBox { Width = 240, Margin = new Padding(0, 6, 0, 0), PlaceholderText = "es. Rossi_matrimonio_1994", Font = new Font("Segoe UI", 10f) };
            btnSettings = Ui.Btn("⚙   Impostazioni", "ghost", (o, e) => OpenSettings()); btnSettings.Margin = new Padding(20, 0, 8, 0);
            btnFolder = Ui.Btn("📁   Apri cartella", "ghost", (o, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", settings.ResolvedOutputFolder())); } catch { } });
            btnTheme = Ui.IconBtn("◐", "Tema chiaro/scuro", (o, e) => { settings.DarkTheme = !settings.DarkTheme; settings.Save(); Theme.Apply(this, settings.DarkTheme); RefreshSourceList(); });
            btnLog = Ui.Btn("Log", "ghost", (o, e) => ToggleLog());
            btnPanels = Ui.IconBtn("◧", "Mostra/nascondi pannello Sorgenti e Mixer", (o, e) => ToggleRightPanel());
            btnCliente = Ui.Btn("👤   Nessun cliente", "ghost", (o, e) => ScegliCliente(true));
            btnCliente.Margin = new Padding(12, 0, 0, 0); btnCliente.Visible = false;
            flow.Controls.AddRange(new Control[] { btnRec, btnPause, btnCliente, lblName, txtName, btnSettings, btnFolder, btnTheme, btnPanels, btnLog });
            Resize += (o, e) => ApplyCompact();
            top.Controls.Add(flow);

            // ---- barra di stato ----
            status = new Card { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 6, 12, 6), Radius = 10 };
            lblRec = new Pill { Dock = DockStyle.Left };
            lblStatus = new Label { AutoSize = true, Dock = DockStyle.Right, Tag = "muted", Padding = new Padding(0, 8, 0, 0) };
            status.Controls.Add(lblStatus); status.Controls.Add(lblRec);

            // ---- canvas ----
            canvas = new CanvasView { Dock = DockStyle.Fill, CanvasW = settings.CanvasW, CanvasH = settings.CanvasH, Sources = settings.Sources, Message = "Premi ▶ Anteprima" };
            canvas.SelectionChanged += s2 => { SyncListSelection(s2); UpdateSourceButtons(); };
            canvas.TransformChanged += (s2, final) =>
            {
                engine.ApplyTransform(s2);
                if (final) { settings.Save(); if (!engine.LiveControl) RestartIfRunning(); }
            };
            canvas.OpenProperties += s2 => EditSource(s2);
            canvas.InputSizeOf = s2 => engine.GetInputSize(s2.Id);
            canvas.FrameShown += OnFrameShown;
            canvas.FrameSource = engine;
            Source.ActualSize = src0 => engine.GetInputSize(src0.Id);
            canvas.RemoveRequested += s2 => RemoveSource(s2);
            canvas.LockChanged += s2 => { settings.Save(); srcList.Invalidate(); canvas.Select(null); };
            var canvasCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(8), Radius = 10 };
            canvasCard.Controls.Add(canvas);

            // ---- sorgenti ----
            cardSources = new Card { Dock = DockStyle.Fill, HeaderText = "SORGENTI", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            srcList = new SourceList { Dock = DockStyle.Fill, Sources = settings.Sources };
            srcList.SelectionChanged += sx => { if (syncingList) return; canvas.Select(sx); UpdateSourceButtons(); };
            srcList.OpenProperties += sx => EditSource(sx);
            srcList.VisibilityToggled += sx =>
            {
                if (CaptureBusy) return;
                sx.Visible = !sx.Visible; settings.Save(); RefreshSourceList(); RebuildMixer(); RestartIfRunning(); canvas.Invalidate();
            };
            srcList.LockToggled += sx => { sx.Locked = !sx.Locked; settings.Save(); canvas.Invalidate(); srcList.Invalidate(); };
            srcList.ContextRequested += (sx, pt) => ShowSourceMenu(sx, srcList, pt);
            var srcBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Tag = "panel" };
            btnAdd = Ui.IconBtn("＋", "Aggiungi sorgente", (o, e) => ShowAddMenu());
            btnRemove = Ui.IconBtn("－", "Rimuovi sorgente", (o, e) => { if (canvas.Selected != null) RemoveSource(canvas.Selected); });
            btnProps = Ui.IconBtn("⚙", "Proprietà", (o, e) => { if (canvas.Selected != null) EditSource(canvas.Selected); });
            btnUp = Ui.IconBtn("▲", "Porta sopra", (o, e) => MoveSource(+1));
            btnDown = Ui.IconBtn("▼", "Porta sotto", (o, e) => MoveSource(-1));
            srcBtns.Controls.AddRange(new Control[] { btnAdd, btnRemove, btnProps, btnUp, btnDown });
            cardSources.Controls.Add(srcList);
            cardSources.Controls.Add(srcBtns);

            // ---- mixer ----
            cardMixer = new Card { Dock = DockStyle.Fill, HeaderText = "MIXER AUDIO", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            mixer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "panel" };
            mixer.Resize += (o, e) => LayoutMixer();
            var monBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, WrapContents = false, Padding = new Padding(0, 6, 0, 0), Tag = "panel" };
            btnMonitor = Ui.Btn(settings.AudioMonitor ? "🎧  Ascolto attivo" : "🎧  Ascolta", settings.AudioMonitor ? "accent" : "ghost", (o, e) => ToggleMonitor());
            tips.SetToolTip(btnMonitor, "Monitoraggio audio: senti l'audio del grabber dalle casse (come OBS)");
            monBar.Controls.Add(btnMonitor);
            cardMixer.Controls.Add(mixer); cardMixer.Controls.Add(monBar);

            // ---- log ----
            cardLog = new Card { Dock = DockStyle.Fill, HeaderText = "LOG FFMPEG", Padding = new Padding(12, 30, 12, 12), Radius = 10 };
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9f), WordWrap = false, BorderStyle = BorderStyle.None };
            cardLog.Controls.Add(txtLog);

            // ---- split: destra (sorgenti | mixer) ----
            splitRight = new SplitContainer { Size = new Size(400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 80, Panel2MinSize = 80, FixedPanel = FixedPanel.Panel2 };
            splitRight.Panel1.Controls.Add(cardSources);
            splitRight.Panel2.Controls.Add(cardMixer);

            // ---- split: canvas | destra ----
            splitMain = new SplitContainer { Size = new Size(1400, 800), Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = gap, Panel1MinSize = 240, Panel2MinSize = 200, Panel2Collapsed = settings.RightPanelHidden, FixedPanel = FixedPanel.Panel2 };
            splitMain.Panel1.Controls.Add(canvasCard);
            splitMain.Panel2.Controls.Add(splitRight);

            // ---- split: sopra | log ----
            splitLog = new SplitContainer { Size = new Size(1400, 900), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = gap, Panel1MinSize = 160, Panel2MinSize = 60, Panel2Collapsed = !settings.ShowLog, FixedPanel = FixedPanel.Panel2 };
            splitMain.SplitterMoved += (o, e) => SaveSplitters();
            splitRight.SplitterMoved += (o, e) => SaveSplitters();
            splitLog.SplitterMoved += (o, e) => SaveSplitters();
            splitLog.Panel1.Controls.Add(splitMain);
            splitLog.Panel2.Controls.Add(cardLog);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(gap, gap, gap, gap) };
            body.Controls.Add(splitLog);

            var topWrap = new Panel { Dock = DockStyle.Top, Height = 62 + gap, Padding = new Padding(gap, gap, gap, 0) };
            topWrap.Controls.Add(top);
            var statusWrap = new Panel { Dock = DockStyle.Bottom, Height = 44 + gap, Padding = new Padding(gap, 0, gap, gap) };
            statusWrap.Controls.Add(status);

            // ---- barra del CRM: cliente in corso e a che punto sono gli altri PC ----
            var crmCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(12, 2, 12, 2), Radius = 10 };
            lblCrm = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            crmCard.Controls.Add(lblCrm);
            crmWrap = new Panel { Dock = DockStyle.Bottom, Height = 34 + gap, Padding = new Padding(gap, 0, gap, gap), Visible = false };
            crmWrap.Controls.Add(crmCard);

            // ---- fascia dell'avanzamento del cliente (sotto la barra dei pulsanti) ----
            prog = new ProgressoCliente { Dock = DockStyle.Fill, Cursor = Cursors.Hand };
            var menuProg = new ContextMenuStrip { Font = new Font("Segoe UI", 10.5f), ShowImageMargin = false };
            menuProg.Items.Add("👤   Cambia cliente", null, (o, e) => ScegliCliente(true));
            menuProg.Items.Add("🔢   Correggi il conteggio", null, async (o, e) => await Riconta(false));
            prog.MouseUp += (o, e) => menuProg.Show(prog, e.Location);
            tips.SetToolTip(prog, "Clic: cambia cliente o correggi il conteggio");
            progWrap = new Panel { Dock = DockStyle.Top, Height = 96 + gap, Padding = new Padding(gap, gap, gap, 0), Visible = false };
            progWrap.Controls.Add(prog);

            Controls.Add(body);
            Controls.Add(crmWrap);
            Controls.Add(statusWrap);
            Controls.Add(progWrap);
            Controls.Add(topWrap);
        }

        bool splittersReady;

        /// <summary>Salva le misure dei pannelli appena rilasci lo splitter (non solo alla chiusura).</summary>
        void SaveSplitters()
        {
            if (!splittersReady) return;
            if (!splitMain.Panel2Collapsed) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
            settings.MixerH = splitRight.Height - splitRight.SplitterDistance - splitRight.SplitterWidth;
            if (!splitLog.Panel2Collapsed) settings.LogH = splitLog.Height - splitLog.SplitterDistance - splitLog.SplitterWidth;
            settings.Save();
        }

        void ApplySplitters()
        {
            bool was = splittersReady; splittersReady = false;   // le mie impostazioni non devono scatenare il salvataggio
            try
            {
                if (!splitMain.Panel2Collapsed) splitMain.SplitterDistance = Math.Max(splitMain.Panel1MinSize, splitMain.Width - Math.Max(200, settings.RightPanelW) - splitMain.SplitterWidth);
                splitRight.SplitterDistance = Math.Max(splitRight.Panel1MinSize, splitRight.Height - Math.Max(80, settings.MixerH) - splitRight.SplitterWidth);
                if (!splitLog.Panel2Collapsed) splitLog.SplitterDistance = Math.Max(splitLog.Panel1MinSize, splitLog.Height - Math.Max(60, settings.LogH) - splitLog.SplitterWidth);
            }
            catch { }
            splittersReady = was;
        }

        void ToggleLog()
        {
            splitLog.Panel2Collapsed = !splitLog.Panel2Collapsed;
            if (!splitLog.Panel2Collapsed) ApplySplitters();
        }

        // ---------------- sorgenti ----------------

        void RefreshSourceList()
        {
            srcList.Sources = settings.Sources;
            srcList.Select(canvas?.Selected);
            srcList.ReadOnlyStructure = CaptureBusy;
            srcList.Invalidate();
            UpdateSourceButtons();
        }

        void SyncListSelection(Source s)
        {
            syncingList = true;
            srcList.Select(s);
            syncingList = false;
        }

        void ShowSourceMenu(Source s, Control owner, Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Proprietà…", null, (o, e) => EditSource(s));
            m.Items.Add("Rinomina…", null, (o, e) =>
            {
                string n = Prompt("Rinomina sorgente", "Nome", s.Name);
                if (!string.IsNullOrWhiteSpace(n)) { s.Name = n.Trim(); settings.Save(); RefreshSourceList(); RebuildMixer(); canvas.Invalidate(); }
            });
            m.Items.Add(new ToolStripSeparator());
            var vis = m.Items.Add(s.Visible ? "Nascondi" : "Mostra", null, (o, e) => { if (CaptureBusy) return; s.Visible = !s.Visible; settings.Save(); RefreshSourceList(); RebuildMixer(); RestartIfRunning(); canvas.Invalidate(); });
            vis.Enabled = !CaptureBusy;
            m.Items.Add(s.Locked ? "Sblocca" : "Blocca", null, (o, e) => { s.Locked = !s.Locked; settings.Save(); srcList.Invalidate(); canvas.Invalidate(); });
            m.Items.Add(new ToolStripSeparator());
            var up = m.Items.Add("Porta sopra", null, (o, e) => { canvas.Select(s); MoveSource(+1); });
            var dn = m.Items.Add("Porta sotto", null, (o, e) => { canvas.Select(s); MoveSource(-1); });
            up.Enabled = dn.Enabled = !CaptureBusy;
            m.Items.Add(new ToolStripSeparator());
            var rm = m.Items.Add("Rimuovi", null, (o, e) => RemoveSource(s));
            rm.Enabled = !CaptureBusy;
            Theme.StyleMenu(m);
            m.Show(owner, at);
        }

        // ================= CRM =================

        /// <summary>All'avvio: configurazione di questo PC dal CRM, battito, e «Chi stai riversando?».</summary>
        async void CrmAvvio()
        {
            AggiornaCliente();
            if (!crm.Configurato) return;
            crmTimer.Start();
            await CrmSincronizzaConfig();
            CrmBattito();
        }

        void CrmDopoImpostazioni()
        {
            if (CrmAttivo) { crmTimer.Start(); _ = CrmSincronizzaConfig(); CrmBattito(); }
            else { crmTimer.Stop(); lavoro = null; nessunCliente = false; }   // CRM spento: VHSCapture come prima
            AggiornaCliente();
        }

        async void ScegliCliente(bool soloLista)
        {
            if (CaptureBusy) { MessageBox.Show(this, "Ferma la registrazione prima di cambiare cliente.", "VHSCapture"); return; }
            if (!CrmAttivo) { MessageBox.Show(this, "Il collegamento al CRM è spento o non configurato: Impostazioni → CRM.", "VHSCapture"); return; }
            var riprendi = lavoro == null ? await ClienteDaRiprendere() : null;
            using var f = new ClienteForm(settings.DarkTheme, () => crm.Lavori(), () => crm.UltimoErrore, soloLista, null, 0, riprendi);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            lavoro = f.Scelto;
            nessunCliente = lavoro == null;
            AppendLog(lavoro == null ? "Nessun cliente: si registra come sempre" : $"Cliente: {lavoro.cliente} — {lavoro.dettaglio}");
            AggiornaCliente();
        }

        /// <summary>
        /// Riconteggio: mostra fatte/totali del CRM e i video del cliente su questo PC; se correggi, aggiorna il CRM.
        /// Restituisce true se il conteggio è stato cambiato.
        /// </summary>
        async Task<bool> Riconta(bool fineCliente)
        {
            var l = lavoro; if (l == null || !CrmAttivo) return false;
            string cartella = string.IsNullOrWhiteSpace(l.cartella) ? null : Path.Combine(settings.ResolvedOutputFolder(), l.cartella);
            int f0 = l.nastri_fatti, t0 = l.nastri_totali;
            int fatti, totali;
            using (var f = new RiconteggioForm(settings.DarkTheme, l.cliente, f0, t0, cartella, fineCliente))
            {
                if (f.ShowDialog(this) != DialogResult.OK || !f.Corretto) return false;
                fatti = f.Fatti; totali = f.Totali;
            }
            string risp = await crm.Manda("conteggio", new Dictionary<string, object> { ["vhs_id"] = l.id, ["fatti"] = fatti, ["totali"] = totali });
            var agg = CrmClient.Leggi<CrmLavoro>(risp);
            if (agg != null) { l.nastri_fatti = agg.nastri_fatti; l.nastri_totali = agg.nastri_totali; l.prossima = agg.prossima; l.dettaglio = agg.dettaglio; l.stato = agg.stato; }
            else { l.nastri_fatti = fatti; l.nastri_totali = totali; l.prossima = fatti + 1; }   // CRM giù: si allinea quando torna
            AppendLog($"Conteggio corretto per {l.cliente}: fatte {f0} → {l.nastri_fatti}, totali {t0} → {l.nastri_totali}");
            AggiornaCliente();
            CrmBattito();
            return true;
        }

        /// <summary>Ricorda il cliente in corso anche se si chiude VHSCapture (0 = nessuno da ricordare).</summary>
        void RicordaCliente(int id)
        {
            if (settings.CrmUltimoCliente == id) return;
            settings.CrmUltimoCliente = id;
            try { settings.Save(); } catch { }
        }

        /// <summary>
        /// Il cliente da proporre con «▶ Continua con…»: l'ultimo che si stava facendo, SOLO se nel CRM ha ancora
        /// videocassette libere (non finito, non consegnato, non tutte in registrazione su altri PC).
        /// </summary>
        async Task<CrmLavoro> ClienteDaRiprendere()
        {
            int id = settings.CrmUltimoCliente;
            if (id <= 0 || !CrmAttivo) return null;
            var l = await crm.Lavoro(id);
            if (l == null) return crm.Raggiungibile ? Dimentica() : null;          // scheda non più esistente (o CRM giù: non si decide)
            string st = (l.stato ?? "").Trim().ToLowerInvariant();
            bool aperta = st == "in attesa" || st == "in lavorazione";
            if (!aperta || l.nastri_totali <= 0 || l.nastri_fatti >= l.nastri_totali || l.prossima > l.nastri_totali) return Dimentica();
            return l;
            CrmLavoro Dimentica() { RicordaCliente(0); return null; }
        }

        void AggiornaCliente()
        {
            if (btnCliente == null || crm == null) return;
            bool compact = compactState == true;
            btnCliente.Visible = CrmAttivo;
            if (lavoro != null) RicordaCliente(lavoro.id);
            if (progWrap != null)
            {
                progWrap.Visible = CrmAttivo && lavoro != null;
                if (lavoro != null)
                {
                    prog.Cliente = lavoro.cliente; prog.Dettaglio = lavoro.dettaglio;
                    prog.Fatti = lavoro.nastri_fatti; prog.Totali = lavoro.nastri_totali;
                    prog.Registrando = engine.IsRecording && !finalizing; prog.Corrente = prog.Registrando ? cassettaInCorso : 0;
                    prog.Invalidate();
                }
            }
            btnCliente.Text = lavoro == null ? (compact ? "👤" : "👤   Nessun cliente")
                            : (compact ? $"👤 {lavoro.nastri_fatti}/{lavoro.nastri_totali}" : $"👤   {lavoro.cliente}  ·  {lavoro.nastri_fatti} di {lavoro.nastri_totali} fatte");
            btnCliente.Variant = lavoro == null ? "ghost" : "accent";
            btnCliente.Invalidate();
            tips.SetToolTip(btnCliente, lavoro == null ? "Scegli il cliente dalla coda del CRM" : $"{lavoro.cliente}: {lavoro.dettaglio}. Clic per cambiare cliente");
            AggiornaBarraCrm();
        }

        void AggiornaBarraCrm()
        {
            if (crmWrap == null || crm == null) return;
            crmWrap.Visible = CrmAttivo;
            if (!CrmAttivo) return;
            var parti = new List<string>();
            if (lavoro != null)
                parti.Add($"👤 {lavoro.cliente} · {lavoro.nastri_fatti} di {lavoro.nastri_totali} fatte · {lavoro.dettaglio}");
            foreach (var p in postazioni.Where(p => !p.questa && p.configurata))
                parti.Add(p.registrando ? $"🔴 {p.nome}: {p.cliente}, registra la {p.cassetta_n}ª di {p.nastri_totali} · {DurataTesto(p.secondi)}" + (p.in_pausa ? " (pausa)" : "")
                                        : (p.online ? $"🟢 {p.nome}: libera" : $"⚪ {p.nome}: non collegata"));
            string st = crm.Raggiungibile ? "CRM ✓" + (string.IsNullOrEmpty(crm.NomePostazione) ? "" : " · " + crm.NomePostazione)
                                          : "⚠ " + (string.IsNullOrEmpty(crm.UltimoErrore) ? "CRM non raggiungibile" : crm.UltimoErrore);
            if (crm.InCoda > 0) st += $" · {crm.InCoda} aggiornamenti in attesa di invio";
            parti.Add(st);
            lblCrm.Text = string.Join("      ·      ", parti);
            lblCrm.ForeColor = crm.Raggiungibile ? Theme.Fore : Theme.Rec;
        }

        static string DurataTesto(int secondi)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, secondi));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        TimeSpan DurataRegistrazione()
        {
            var d = DateTime.Now - recStart - pausedTotal;
            if (pausedSince != null) d -= DateTime.Now - pausedSince.Value;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }

        /// <summary>Ogni 20 s: eventi rimasti indietro, battito, configurazione cambiata nel CRM, stato degli altri PC.</summary>
        async void CrmBattito()
        {
            if (!CrmAttivo || crmOccupato) return;
            crmOccupato = true;
            try
            {
                await crm.Svuota();
                bool reg = engine.IsRecording && lavoro != null && !finalizing;
                string cfgAt = await crm.Battito(reg, lavoro?.id ?? 0, cassettaInCorso, reg ? (int)DurataRegistrazione().TotalSeconds : 0, pausedSince != null);
                if (cfgAt != null && cfgAt != (settings.CrmConfigAt ?? "")) await CrmSincronizzaConfig();
                // cliente in corso: il conteggio si rilegge dal CRM (altri PC, Avanzamento fatto a mano) → uguale su tutti i PC
                if (lavoro != null && !finalizing)
                {
                    var agg = await crm.Lavoro(lavoro.id);
                    if (agg != null && lavoro != null && agg.id == lavoro.id)
                    {
                        lavoro.nastri_fatti = agg.nastri_fatti; lavoro.nastri_totali = agg.nastri_totali; lavoro.prossima = agg.prossima;
                        lavoro.dettaglio = agg.dettaglio; lavoro.stato = agg.stato; lavoro.in_registrazione_su = agg.in_registrazione_su;
                        if (!engine.IsRecording && agg.nastri_totali > 0 && agg.nastri_fatti >= agg.nastri_totali)
                        {
                            AppendLog("Videocassette di " + lavoro.cliente + " finite (aggiornato dal CRM): alla prossima registrazione ti chiedo il cliente");
                            lavoro = null; nessunCliente = false; RicordaCliente(0);
                        }
                        AggiornaCliente();
                    }
                }
                var p = await crm.Postazioni();
                if (p != null)
                {
                    postazioni = p;
                    var q = p.FirstOrDefault(x => x.questa);
                    if (q != null) crm.NomePostazione = q.nome;
                }
            }
            catch (Exception ex) { AppendLog("CRM: " + ex.Message); }
            finally { crmOccupato = false; AggiornaBarraCrm(); }
        }

        CrmConfig ConfigLocale() => new CrmConfig
        {
            chiedi_cliente = settings.CrmChiediCliente, cartella_cliente = settings.CrmCartellaCliente, chiedi_fine = settings.CrmChiediFine,
            ripartenza_attiva = settings.CrmRipartenzaAttiva, ripartenza_sec = settings.CrmRipartenzaSec, cartella_base = settings.OutputFolder ?? "",
        };

        /// <summary>Configurazione di questo PC: vince la modifica più recente (qui nelle Impostazioni o nel CRM).</summary>
        async Task CrmSincronizzaConfig()
        {
            var r = await crm.LeggiConfig();
            if (r == null) return;
            var (cfg, quando) = r.Value;
            string locale = settings.CrmConfigAt ?? "";
            int cmp = string.Compare(quando ?? "", locale, StringComparison.Ordinal);
            if (cmp > 0)
            {
                settings.CrmChiediCliente = cfg.chiedi_cliente; settings.CrmCartellaCliente = cfg.cartella_cliente;
                settings.CrmChiediFine = cfg.chiedi_fine; settings.CrmRipartenzaAttiva = cfg.ripartenza_attiva;
                settings.CrmRipartenzaSec = Math.Clamp(cfg.ripartenza_sec, 10, 600);
                if (!string.IsNullOrWhiteSpace(cfg.cartella_base)) settings.OutputFolder = cfg.cartella_base;
                settings.CrmConfigAt = quando; settings.Save();
                AppendLog("Impostazioni di questo PC aggiornate dal CRM");
            }
            else if (cmp < 0) await crm.SalvaConfig(ConfigLocale(), locale);
        }

        /// <summary>Fine cassetta: la manda al CRM, aggiorna il conteggio e, finiti i nastri, propone «pronto».</summary>
        async Task CrmFineCassetta(string esito, string file)
        {
            var l = lavoro; if (l == null) return;
            string risp = await crm.Manda("fine", new Dictionary<string, object>
            {
                ["vhs_id"] = l.id, ["cassetta_n"] = cassettaInCorso, ["esito"] = esito, ["secondi"] = (int)durataStop.TotalSeconds, ["file"] = file,
            });
            var f = CrmClient.Leggi<CrmFine>(risp);
            if (f != null) { l.nastri_fatti = f.nastri_fatti; l.nastri_totali = f.nastri_totali; l.prossima = f.prossima; if (!string.IsNullOrEmpty(f.dettaglio)) l.dettaglio = f.dettaglio; }
            else
            {
                // CRM non raggiungibile: conto qui, il CRM si allinea quando l'evento parte dalla coda
                if (esito == "completata") { l.nastri_fatti++; l.prossima = l.nastri_fatti + 1; }
                else if (esito == "scartata" || esito == "omaggio") l.nastri_totali = Math.Max(0, l.nastri_totali - 1);
            }
            AppendLog(esito == "completata" ? $"Cassetta {cassettaInCorso} contata: {l.nastri_fatti} di {l.nastri_totali}"
                    : esito == "omaggio" ? $"Cassetta tenuta ma non fatta pagare: il file resta, il totale scende a {l.nastri_totali}"
                    : esito == "scartata" ? $"Cassetta scartata (vuota): il totale scende a {l.nastri_totali}"
                    : "Rifai: la cassetta non è stata contata");
            AggiornaCliente();
            CrmBattito();
            bool finiti = f != null ? f.nastri_finiti : (l.nastri_totali > 0 && l.nastri_fatti >= l.nastri_totali);
            if (!finiti) return;
            // 🔢 riconteggio prima di chiudere il cliente: se correggi e mancano ancora cassette, il cliente resta aperto
            if (await Riconta(true) && lavoro != null && lavoro.nastri_fatti < lavoro.nastri_totali)
            {
                AppendLog($"{lavoro.cliente} resta aperto: {lavoro.nastri_fatti} di {lavoro.nastri_totali} fatte");
                return;
            }
            if (f != null && f.tutto_finito)
                MessageBox.Show(this, $"Hai finito tutte le cassette di {l.cliente} ({f.nastri_fatti} di {f.nastri_totali}) e non restano altri supporti.\n\n" +
                    "Nel CRM il lavoro è stato segnato PRONTO in automatico: esce dalla coda ed è pronto per la consegna.",
                    "VHSCapture — lavoro finito: segnato PRONTO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (f != null)
                MessageBox.Show(this, $"Hai finito le cassette di {l.cliente} da passare col grabber ({f.nastri_fatti} di {f.nastri_totali}).\n\n" +
                    $"Restano {f.restano_altri} supporti da lavorare a parte, che non passano dal grabber (supporti del cliente: {f.dettaglio}).\n\n" +
                    "Nel CRM il lavoro resta «in lavorazione»: diventa PRONTO da solo quando segni fatti anche quelli.",
                    "VHSCapture — cassette finite, restano altri supporti", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this, $"Hai finito le cassette di {l.cliente} ({l.nastri_fatti} di {l.nastri_totali}).\n\n" +
                    "Il CRM adesso non risponde: conteggio e stato si aggiornano appena torna la connessione.",
                    "VHSCapture — cassette finite (CRM non raggiungibile)", MessageBoxButtons.OK, MessageBoxIcon.Information);
            lavoro = null;                              // videocassette del cliente finite: al prossimo Registra si chiede il cliente
            nessunCliente = false;
            RicordaCliente(0);                          // finito: alla riapertura non si propone più
            AppendLog("Videocassette di " + l.cliente + " finite: alla prossima registrazione ti chiedo il cliente");
            AggiornaCliente();
        }

        /// <summary>
        /// Avvisa il CRM che la cassetta è partita e prende il numero che assegna lui: se un altro PC sta già
        /// registrando la 4ª di questo cliente, qui arriva la 5ª. Senza CRM resta il numero calcolato qui.
        /// </summary>
        async Task CrmInizio(string file)
        {
            try { await CrmInizioInterno(file); }
            catch (Exception ex) { AppendLog("CRM (inizio cassetta): " + ex.Message); }
        }

        async Task CrmInizioInterno(string file)
        {
            var l = lavoro; if (l == null) return;
            string risp = await crm.Manda("inizio", new Dictionary<string, object> { ["vhs_id"] = l.id, ["cassetta_n"] = cassettaInCorso, ["file"] = file, ["versione"] = crm.Versione });
            var r = CrmClient.Leggi<CrmInizio>(risp);
            if (r == null || lavoro != l) return;
            l.nastri_fatti = r.nastri_fatti; l.nastri_totali = r.nastri_totali;
            if (r.cassetta_n > 0 && r.cassetta_n != cassettaInCorso)
            {
                var altri = string.Join(", ", (r.altri_pc ?? new List<CrmAltroPc>()).Select(x => $"{x.pc} la {x.cassetta}ª"));
                AppendLog($"Il CRM assegna la cassetta {r.cassetta_n} di {r.nastri_totali}" + (altri.Length > 0 ? $" (stanno registrando: {altri})" : ""));
                cassettaInCorso = r.cassetta_n;
            }
            AggiornaCliente();
            CrmBattito();
        }

        /// <summary>
        /// «Di chi è questa cassetta?» aperta A REGISTRAZIONE GIÀ PARTITA, senza bloccare niente (si può anche fermare).
        /// Se entro 60 s non si sceglie, si chiude da sola e la registrazione continua nella cartella predefinita.
        /// </summary>
        async void ChiediClienteDurante()
        {
            if (sceltaAperta != null) return;
            var riprendi = await ClienteDaRiprendere();
            if (sceltaAperta != null || !engine.IsRecording || finalizing) return;   // nel frattempo fermata o già aperta
            var f = new ClienteForm(settings.DarkTheme, () => crm.Lavori(), () => crm.UltimoErrore, true, "Di chi è questa cassetta?", 60, riprendi);
            f.FormClosed += async (o, e) =>
            {
                if (sceltaAperta == f) sceltaAperta = null;
                bool scelta = f.DialogResult == DialogResult.OK;
                var scelto = f.Scelto;
                BeginInvoke(new Action(() => f.Dispose()));
                if (!scelta) { AppendLog("Cliente non scelto: la registrazione continua nella cartella predefinita"); return; }
                if (scelto == null)
                {
                    nessunCliente = true;
                    AppendLog("Nessun cliente: si registra senza CRM finché non scegli un cliente dal pulsante 👤");
                    AggiornaCliente();
                    return;
                }
                lavoro = scelto;
                if (!engine.IsRecording || finalizing) { AggiornaCliente(); return; }   // fermata nel frattempo: vale dalla prossima cassetta
                daSpostare = true;
                cassettaInCorso = Math.Min(lavoro.prossima, Math.Max(1, lavoro.nastri_totali));
                AppendLog($"Cliente {lavoro.cliente}: cassetta {cassettaInCorso} di {lavoro.nastri_totali} — a fine registrazione il file va nella cartella «{lavoro.cartella}»");
                AggiornaCliente();
                await CrmInizio(Path.GetFileName((recFile ?? "").Replace("%03d", "000")));   // numero assegnato dal CRM
            };
            // F9 (ferma) e F10 (pausa) funzionano anche mentre questa finestra ha la tastiera
            f.KeyPreview = true;
            f.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.F9) { e.Handled = true; ToggleRecording(); }
                else if (e.KeyCode == Keys.F10) { e.Handled = true; TogglePause(); }
            };
            sceltaAperta = f;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(Left + Math.Max(0, (Width - f.Width) / 2), Top + Math.Max(0, (Height - f.Height) / 2));
            f.Show(this);
        }

        /// <summary>Sposta (taglia e incolla, non copia) i file della registrazione nella cartella del cliente dentro la cartella predefinita.</summary>
        async Task SpostaNellaCartellaCliente(string final, string cartellaRete, string cartellaCliente)
        {
            string dir = cartellaRete ?? Path.GetDirectoryName(final);
            string dest = Path.Combine(dir, cartellaCliente);
            try { Directory.CreateDirectory(dest); }
            catch (Exception ex) { AppendLog("⚠ Cartella del cliente non creata (" + ex.Message + "): il file resta nella cartella predefinita"); return; }
            foreach (var f in RecordedFiles(Path.Combine(dir, Path.GetFileName(final))).ToList())
            {
                string target = Path.Combine(dest, Path.GetFileName(f));
                for (int k = 1; File.Exists(target); k++)
                    target = Path.Combine(dest, Path.GetFileNameWithoutExtension(f) + "_" + k + Path.GetExtension(f));
                try
                {
                    lblRec.Text = "Sposto nella cartella del cliente…";
                    await Task.Run(() => File.Move(f, target));   // stessa unità = istantaneo; unità diversa = copia e cancella l'originale
                    AppendLog("Spostato in «" + cartellaCliente + "»: " + Path.GetFileName(target));
                }
                catch (Exception ex) { AppendLog("⚠ Non spostato (" + ex.Message + "): resta in " + f); }
            }
        }

        string Prompt(string title, string label, string value)
        {
            using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(480, 150), MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = true, TopMost = true, Font = Font };
            var l = new Label { Text = label, Left = 14, Top = 14, Width = 452, Height = 36 };
            var t = new TextBox { Left = 14, Top = 56, Width = 452, Text = value, Font = new Font("Segoe UI", 11f), BorderStyle = BorderStyle.FixedSingle };
            var ok = Ui.Btn("OK", "accent", null, 100); ok.Left = 258; ok.Top = 100; ok.DialogResult = DialogResult.OK;
            var ca = Ui.Btn("Annulla", "normal", null, 100); ca.Left = 366; ca.Top = 100; ca.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { l, t, ok, ca });
            f.AcceptButton = ok; f.CancelButton = ca;
            Theme.Apply(f, settings.DarkTheme);
            return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
        }

        void ToggleRightPanel()
        {
            if (!splitMain.Panel2Collapsed && splittersReady) settings.RightPanelW = splitMain.Width - splitMain.SplitterDistance - splitMain.SplitterWidth;
            splitMain.Panel2Collapsed = !splitMain.Panel2Collapsed;
            if (!splitMain.Panel2Collapsed) ApplySplitters();
            settings.RightPanelHidden = splitMain.Panel2Collapsed;
            settings.Save();
        }

        bool? compactState;
        readonly ToolTip tips = new ToolTip { InitialDelay = 600 };
        /// <summary>Finestra stretta: pulsanti solo icona, così la barra ci sta anche su schermi piccoli o a metà schermo.</summary>
        void ApplyCompact()
        {
            if (btnRec == null) return;
            bool compact = ClientSize.Width < 1180;
            if (compactState == compact) return;
            compactState = compact;
            UpdateRecButton();
            btnSettings.Text = compact ? "⚙" : "⚙   Impostazioni";
            AggiornaCliente();
            btnFolder.Text = compact ? "📁" : "📁   Apri cartella";
            lblName.Visible = !compact;
            txtName.Width = compact ? 150 : 240;
            btnSettings.Margin = new Padding(compact ? 8 : 20, 0, 8, 0);
            tips.SetToolTip(btnSettings, compact ? "Impostazioni" : null);
            tips.SetToolTip(btnFolder, compact ? "Apri cartella" : null);
            top.PerformLayout();
        }

        void UpdateSourceButtons()
        {
            bool sel = canvas.Selected != null, rec = CaptureBusy;
            btnRemove.Enabled = sel && !rec; btnProps.Enabled = sel && !startingRecording && !finalizing;
            int i = sel ? settings.Sources.IndexOf(canvas.Selected) : -1;
            btnUp.Enabled = sel && !rec && i < settings.Sources.Count - 1;
            btnDown.Enabled = sel && !rec && i > 0;
            btnAdd.Enabled = !rec;
            if (srcList != null) srcList.ReadOnlyStructure = rec;
        }

        void ShowAddMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Dispositivo di cattura video (grabber USB)", null, (o, e) => AddSource(SourceType.Capture));
            m.Items.Add("Immagine (logo, sfondo…)", null, (o, e) => AddSource(SourceType.Image));
            m.Items.Add("Colore pieno", null, (o, e) => AddSource(SourceType.Color));
            Theme.StyleMenu(m);
            m.Show(btnAdd, new Point(0, btnAdd.Height));
        }

        void AddSource(SourceType type)
        {
            if (CaptureBusy) return;
            var s = new Source { Type = type };
            s.Name = UniqueName(type switch { SourceType.Capture => "Grabber USB", SourceType.Image => "Immagine", _ => "Colore" });
            if (type == SourceType.Capture)
            {
                var (video, audio) = FFmpeg.ListDevices();
                // proponi il primo dispositivo non ancora usato
                s.VideoDevice = video.FirstOrDefault(v => !settings.Sources.Any(x => x.VideoDevice == v)) ?? video.FirstOrDefault() ?? "";
                s.AudioDevice = "";
                s.DeinterlaceMode = s.IsSD ? "yadif2x" : "off";
                s.FitTo(settings.CanvasW, settings.CanvasH);
            }
            else if (type == SourceType.Color) s.FillTo(settings.CanvasW, settings.CanvasH);
            else { s.W = 400; s.H = 300; s.Center(settings.CanvasW, settings.CanvasH); }

            // come OBS: la sorgente entra subito in scena, così la configuri vedendo l'anteprima
            bool canPreview = type != SourceType.Image;
            if (canPreview)
            {
                settings.Sources.Add(s);
                RefreshSourceList(); canvas.Select(s);
                StartPreview();
            }

            using var f = new SourceForm(s, settings, false, LiveApply, StructuralApply, CropPreview, WithDeviceFree, AppendLog, id => engine.GetInputInfo(id));
            var r = f.ShowDialog(this);
            if (r != DialogResult.OK)
            {
                if (canPreview) { settings.Sources.Remove(s); canvas.Select(null); RefreshSourceList(); RebuildMixer(); StartPreview(); }
                return;
            }
            var res = f.Result;
            if (canPreview)
            {
                s.CopyAllFrom(res);
            }
            else
            {
                settings.Sources.Add(res); s = res;
            }
            ApplyCanvasFps(f.CanvasFpsRequest);
            settings.Save();
            RefreshSourceList(); RebuildMixer();
            canvas.Select(s);
            StartPreview();
        }

        string UniqueName(string baseName)
        {
            if (!settings.Sources.Any(x => x.Name == baseName)) return baseName;
            for (int i = 2; ; i++) if (!settings.Sources.Any(x => x.Name == $"{baseName} {i}")) return $"{baseName} {i}";
        }

        void EditSource(Source s)
        {
            if (startingRecording || finalizing) return;
            using var f = new SourceForm(s, settings, engine.IsRecording, LiveApply, StructuralApply, CropPreview, WithDeviceFree, AppendLog, id => engine.GetInputInfo(id));
            if (f.ShowDialog(this) != DialogResult.OK) { canvas.Invalidate(); RefreshMixerValues(); return; }
            var res = f.Result;
            bool structural = !res.StructurallyEquals(s);
            s.CopyAllFrom(res);
            if (ApplyCanvasFps(f.CanvasFpsRequest)) structural = true;
            settings.Save();
            RefreshSourceList(); RebuildMixer();
            canvas.Select(s);
            if (structural) RestartIfRunning();
            else { engine.ApplyTransform(s); engine.ApplyColor(s); engine.ApplyVolume(s); if (!engine.LiveControl) RestartIfRunning(); }
        }

        /// <summary>Dal dialogo: dispositivo/risoluzione/fps/deinterlaccio cambiati → aggiorna la sorgente in scena e riavvia l'anteprima.</summary>
        void StructuralApply(Source s)
        {
            var target = settings.Sources.FirstOrDefault(x => x.Id == s.Id);
            if (target == null || CaptureBusy) return;
            if (target.StructurallyEquals(s)) return;
            target.CopyStructuralFrom(s);
            RebuildMixer();
            StartPreview();
        }

        /// <summary>Ritaglia dall'anteprima corrente la zona occupata dalla sorgente (per l'anteprima nel dialogo proprietà).</summary>
        Bitmap CropPreview(string id)
        {
            var src = settings.Sources.FirstOrDefault(x => x.Id == id);
            if (src == null || !src.Visible || !engine.IsRunning) return null;
            return canvas.WithFrame(fb => CropFrom(fb, src));
        }

        Bitmap CropFrom(FrameBuf fb, Source src)
        {
            if (fb == null) return null;
            if ((DateTime.Now - lastFrameAt).TotalSeconds > 2) return null;
            double sx = (double)fb.W / settings.CanvasW, sy = (double)fb.H / settings.CanvasH;
            var r = Rectangle.Intersect(new Rectangle((int)(src.X * sx), (int)(src.Y * sy), (int)Math.Ceiling(src.W * sx), (int)Math.Ceiling(src.H * sy)),
                                        new Rectangle(0, 0, fb.W, fb.H));
            if (r.Width < 4 || r.Height < 4) return null;
            try
            {
                var bmp = new Bitmap(r.Width, r.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                for (int y = 0; y < r.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(fb.Data, ((r.Y + y) * fb.W + r.X) * 4, bd.Scan0 + y * bd.Stride, r.Width * 4);
                bmp.UnlockBits(bd);
                return bmp;
            }
            catch { return null; }
        }

        void RemoveSource(Source s)
        {
            if (CaptureBusy) return;
            if (MessageBox.Show(this, $"Rimuovere \"{s.Name}\"?", "VHSCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            settings.Sources.Remove(s);
            settings.Save();
            canvas.Select(null);
            RefreshSourceList(); RebuildMixer();
            RestartIfRunning();
        }

        void MoveSource(int dir)
        {
            var s = canvas.Selected; if (s == null || CaptureBusy) return;
            int i = settings.Sources.IndexOf(s), j = i + dir;
            if (j < 0 || j >= settings.Sources.Count) return;
            settings.Sources[i] = settings.Sources[j]; settings.Sources[j] = s;
            settings.Save();
            RefreshSourceList();
            RestartIfRunning();
        }

        /// <summary>Applica al volo (dal dialogo proprietà) trasformazione/colore/volume.</summary>
        void LiveApply(Source s)
        {
            var target = settings.Sources.FirstOrDefault(x => x.Id == s.Id);
            if (target != null && !ReferenceEquals(target, s)) target.CopyLiveFrom(s);
            var t = target ?? s;
            if (target != null && engine.IsRunning)
            {
                engine.ApplyTransform(t); engine.ApplyColor(t); engine.ApplyVolume(t);
                if (!engine.LiveControl) ScheduleRestart();
            }
            canvas.Invalidate();
            RefreshMixerValues();
        }

        // ---------------- mixer ----------------

        void RebuildMixer()
        {
            mixer.SuspendLayout();
            mixer.Controls.Clear();
            mixerRows.Clear();
            foreach (var sx in settings.Sources.Where(x => x.HasAudio))
            {
                var row = new MixerRow(sx);
                row.VolumeChanged += q => engine.ApplyVolume(q);
                row.VolumeCommitted += q => { settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                row.MuteChanged += q => { engine.ApplyVolume(q); settings.Save(); if (!engine.LiveControl) ScheduleRestart(); };
                row.OpenProperties += q => EditSource(q);
                if (!sx.Visible) row.Enabled = false;
                mixer.Controls.Add(row);
                mixerRows[sx.Id] = row;
            }
            if (mixerRows.Count == 0)
                mixer.Controls.Add(new Label { Text = "Nessuna sorgente audio.\nNelle proprietà del grabber scegli il dispositivo audio.", Left = 0, Top = 4, AutoSize = true, Tag = "muted" });
            mixer.ResumeLayout();
            LayoutMixer();
            Theme.Apply(this, settings.DarkTheme);
        }

        void LayoutMixer()
        {
            int y = 0, w = Math.Max(160, mixer.ClientSize.Width - 2);
            foreach (var row in mixerRows.Values) { row.SetBounds(0, y, w, row.Height); y += row.Height + 6; }
        }

        void ResetMeters() { foreach (var r in mixerRows.Values) r.Meter.Reset(); }

        void RefreshMixerValues()
        {
            foreach (var row in mixerRows.Values) row.SyncFromSource();
        }

        // ---------------- motore ----------------

        void StartPreview()
        {
            if (!FFmpeg.Exists || engine.IsRecording) return;
            restartTimer.Stop();
            frames = 0; ResetMeters(); previewStartedAt = DateTime.UtcNow; lastStats = null;
            lock (runLog) runLog.Clear();
            canvas.Message = settings.Sources.Any(x => x.Visible) ? "Avvio anteprima…" : "Nessuna sorgente: premi ＋ per aggiungere il grabber";
            canvas.SetFrame(null);
            if (!settings.Sources.Any(x => x.Visible && (x.Type == SourceType.Capture ? !string.IsNullOrWhiteSpace(x.VideoDevice) : x.Type != SourceType.Image || File.Exists(x.ImagePath))))
            {
                engine.Stop(); monitor.Stop(); SetButtons(); return;
            }
            try { engine.Start(settings, PreviewWidth(), settings.AudioMonitor); StartMonitorIfNeeded(); }
            catch (Exception ex) { AppendLog("Errore avvio: " + ex.Message); }
            SetButtons();
        }

        /// <summary>Ferma l'anteprima, esegue l'azione (es. dialogo del driver via ffmpeg), riavvia l'anteprima.</summary>
        void WithDeviceFree(Action a)
        {
            if (CaptureBusy) return;
            bool was = engine.IsRunning;
            engine.Stop(); canvas.SetFrame(null); canvas.Message = "Dispositivo in uso dal dialogo del driver…"; canvas.Refresh();
            try { a(); } finally { if (was) StartPreview(); }
        }

        void RestartIfRunning() { if (CaptureBusy) return; if (engine.IsRunning) StartPreview(); }
        void ScheduleRestart() { if (CaptureBusy) return; restartTimer.Stop(); restartTimer.Start(); }

        async void StartRecording()
        {
            if (!FFmpeg.Exists) return;
            if (CaptureBusy) return;
            if (!settings.Sources.Any(x => x.Visible)) { MessageBox.Show(this, "Aggiungi almeno una sorgente.", "VHSCapture"); return; }

            daSpostare = false;
            string destFolder = settings.ResolvedOutputFolder();
            if (lavoro != null && settings.CrmCartellaCliente && !string.IsNullOrWhiteSpace(lavoro.cartella))
            {
                // cartella «Nome Cognome» dentro la cartella del PC: creata se non c'è
                string cli = Path.Combine(destFolder, lavoro.cartella);
                try { Directory.CreateDirectory(cli); destFolder = cli; }
                catch (Exception ex) { AppendLog("⚠ Cartella del cliente non creata (" + ex.Message + "): salvo nella cartella del PC"); }
            }
            string folder = destFolder;
            moveTo = null;
            if (settings.RecordLocalFirst && IsNetworkFolder(destFolder))
            {
                // una pausa della rete non può più interrompere: si scrive sul disco del PC e si sposta alla fine
                folder = LocalStagingFolder();
                moveTo = destFolder;
                if (!Directory.Exists(destFolder)) AppendLog("⚠ Cartella di rete non raggiungibile adesso: registro sul PC e la sposto a fine registrazione.");
            }
            else if (!Directory.Exists(folder))
            {
                MessageBox.Show(this, "Cartella di destinazione non raggiungibile:\n" + folder, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string name = txtName.Text.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            string baseName = string.IsNullOrEmpty(name) ? $"{settings.FilePrefix}_{stamp}" : $"{settings.FilePrefix}_{name}_{stamp}";
            foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            string rootName = baseName; int suffix = 1;
            bool Taken(string dir, string b) => dir != null && (File.Exists(Path.Combine(dir, b + ".mp4")) || File.Exists(Path.Combine(dir, b + ".mkv")) || File.Exists(Path.Combine(dir, b + "_000.mp4")));
            while (Taken(folder, baseName) || Taken(moveTo, baseName))
                baseName = rootName + "_" + suffix++;
            recFolder = folder; recBase = baseName;
            finalFile = Path.Combine(folder, baseName + ".mp4");
            if (settings.SplitMinutes > 0 && !settings.SafeRecording)
                recFile = finalFile = Path.Combine(folder, baseName + "_%03d.mp4");   // divisione automatica: _000, _001, …
            else
                recFile = settings.SafeRecording ? Path.Combine(folder, baseName + ".mkv") : finalFile;

            restartTimer.Stop();
            bool muxStarted = false;
            try
            {
                // cartella scrivibile: controllo istantaneo, senza toccare file esistenti
                string probe = Path.Combine(folder, ".vhscapture-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var writable = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) writable.WriteByte(0);
                if (!engine.IsRunning) StartPreview();
                // PARTE SUBITO (come prima): il registratore scrive dal keyframe già in memoria, o dal prossimo se non c'è ancora.
                engine.StartRecording(settings, recFile); muxStarted = true;
                // cartella di rete: il file sul NAS cresce insieme a quello sul PC (allo stop resta solo l'ultimo pezzo)
                mirror = null;
                if (moveTo != null && !recFile.Contains("%03d")) { mirror = new NetworkMirror(recFile, moveTo); mirror.Start(); }
                recStart = DateTime.Now;
                pausedSince = null; pausedTotal = TimeSpan.Zero;
                blankSince = null; blankStartRecSec = -1; contentSamples = 0; autoStopped = false;
                lastSignalAt = DateTime.MinValue; blankFloor = DateTime.UtcNow; stallWarned = false;
                ResetAudioStats();
                AppendLog("Registrazione avviata: " + recFile.Replace("%03d", "000") + (moveTo != null ? $"  (passa dal PC, copia in rete a pezzi in {moveTo})" : ""));
                SetButtons();
                VerifyRecordingStarted(recFile, ++recSession);   // controllo dietro le quinte, non blocca
                if (lavoro != null)
                {
                    cassettaInCorso = Math.Min(lavoro.prossima, Math.Max(1, lavoro.nastri_totali));
                    AppendLog($"Cliente {lavoro.cliente}: cassetta {cassettaInCorso} di {lavoro.nastri_totali}");
                    AggiornaCliente();
                    _ = CrmInizio(Path.GetFileName(recFile.Replace("%03d", "000")));   // numero assegnato dal CRM (a parte: non può mai fermare la registrazione)
                }
                else if (CrmAttivo && settings.CrmChiediCliente && !nessunCliente)
                    ChiediClienteDurante();   // la registrazione è già partita: la domanda arriva adesso, senza bloccare
            }
            catch (Exception ex)
            {
                mirror?.Abort(); mirror = null;
                if (muxStarted) await Task.Run(() => engine.StopRecording());
                string partial = RecordedBytes(recFile) > 0 ? "\nIl file parziale è conservato: " + recFile : "";
                AppendLog("Registrazione non avviata: " + ex.Message + partial);
                if (!closingApp && !IsDisposed) MessageBox.Show(this, "Registrazione non avviata.\n" + ex.Message + partial, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetButtons();
            }
        }

        int recSession;

        // audio della registrazione misurato dal VU mentre si registra: allo stop non si rilegge il file
        // (su un MP4 frammentato in rete la rilettura voleva migliaia di accessi al NAS = minuti)
        readonly object audioStatsLock = new object();
        double audioEnergy; long audioSamples; double audioPeak = -91;
        void RecordAudioStats(double rmsDb, double peakDb)
        {
            if (!engine.IsRecording || pausedSince != null || finalizing) return;
            lock (audioStatsLock)
            {
                if (rmsDb > -90) { audioEnergy += Math.Pow(10, rmsDb / 10); }
                audioSamples++;
                if (peakDb > audioPeak) audioPeak = peakDb;
            }
        }
        void ResetAudioStats() { lock (audioStatsLock) { audioEnergy = 0; audioSamples = 0; audioPeak = -91; } }

        /// <summary>
        /// Dopo il clic su Registra: se entro 20 s il file non riceve video (grabber fermo, destinazione non scrivibile, muxer
        /// terminato) ferma e avvisa. Gira in background: la registrazione è già partita e l'interfaccia è già in REC.
        /// </summary>
        async void VerifyRecordingStarted(string file, int session)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 20)
            {
                await Task.Delay(250);
                if (session != recSession || closingApp || IsDisposed || !engine.IsRecording || finalizing) return;
                if (engine.Recorder.OutputStarted && RecordedBytes(file) > 0) return;          // tutto ok
                if (engine.Recorder.LastError != null || !engine.Recorder.MuxAlive) break;     // errore vero
            }
            if (session != recSession || closingApp || IsDisposed || !engine.IsRecording || finalizing) return;
            string why = engine.Recorder.LastError ?? (!engine.Recorder.MuxAlive
                ? "Il processo di registrazione si è fermato."
                : "Il file non ha ricevuto video entro 20 secondi: controlla l'anteprima e la cartella di destinazione.");
            AppendLog("Registrazione interrotta all'avvio: " + why);
            StopRecording(true);
            MessageBox.Show(this, "La registrazione non è partita.\n" + why, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        async void StopRecording(bool restartPreview)
        {
            if (!engine.IsRecording || finalizing || startingRecording) return;
            durataStop = DurataRegistrazione();
            if (sceltaAperta != null) { var fa = sceltaAperta; sceltaAperta = null; fa.Close(); }   // fermata prima di scegliere: resta senza cliente
            finalizing = true;
            SetButtons();
            lblRec.Text = "Chiusura file…"; lblRec.Fill = Color.Transparent; lblRec.ForeColor = Theme.Fore;
            canvas.RecText = null; canvas.Invalidate();

            try
            {
            string written = recFile, final = finalFile;
            bool rewritten = false;   // contenuto rifatto da capo (MKV→MP4, taglio con riscrittura): la copia in rete riparte da zero
            var closeClock = Stopwatch.StartNew();
            bool muxOk = await Task.Run(() => engine.StopRecording());
            AppendLog($"File chiuso in {closeClock.Elapsed.TotalSeconds:0.0} s");
            if (!muxOk) AppendLog("Registrazione interrotta: " + (engine.Recorder.LastError ?? "Chiusura non completata. Il file parziale è conservato."));

            bool HasData(string f) => RecordedBytes(f) > 0;

            if (!HasData(written))
            {
                AppendLog("Registrazione NON salvata: ffmpeg non ha scritto niente (vedi errori sopra)");
                MessageBox.Show(this, "La registrazione non è partita e non è stato salvato nulla.\nControlla il Log.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (!muxOk)
            {
                MessageBox.Show(this, "La registrazione si è interrotta. Il file parziale è conservato in:\n" + written + "\n\n" + engine.Recorder.LastError, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (settings.SafeRecording && !string.Equals(written, final, StringComparison.OrdinalIgnoreCase))
            {
                rewritten = true;
                lblRec.Text = "Conversione in MP4 (senza ricodifica)…";
                bool ok = await Task.Run(() => FFmpeg.RemuxToMp4(written, final, AppendLog));
                if (ok) { try { File.Delete(written); } catch { } AppendLog("Salvato: " + final); }
                else
                {
                    AppendLog("Remux fallito: rimane il file MKV " + written);
                    MessageBox.Show(this, "Conversione MP4 fallita, il video è comunque salvo in:\n" + written, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (final.Contains("%03d")) AppendLog("Salvato in più parti: " + string.Join(", ", RecordedFiles(final).Select(Path.GetFileName)));
            else AppendLog("Salvato: " + final);

            // coda uniforme: se lo stop è automatico la taglio (senza ricodifica)
            string mainFile = RecordedFiles(final).LastOrDefault();
            if (muxOk && autoStopped && settings.TrimBlankTail && blankStartRecSec > 0 && mainFile != null && !final.Contains("%03d"))
            {
                lblRec.Text = "Taglio la coda uniforme…";
                // il file parte dal keyframe precedente al clic: margine = intervallo keyframe + 1 s
                double cut = blankStartRecSec + Math.Max(1, settings.KeyframeSec) + 1;
                // MP4 frammentato: si accorcia il file al pezzo giusto, istantaneo. Altrimenti riscrittura (una passata).
                bool ok = await Task.Run(() => Mp4Frag.TruncateAt(mainFile, cut, AppendLog));
                if (!ok) { ok = await Task.Run(() => FFmpeg.TrimFile(mainFile, cut, AppendLog)); if (ok) rewritten = true; }
                AppendLog(ok ? $"Coda uniforme tagliata: file lungo {TimeSpan.FromSeconds(cut):hh\\:mm\\:ss}" : "Coda uniforme non tagliata (il file è comunque salvo)");
            }

            // cliente scelto a registrazione già partita: a fine chiusura i file si SPOSTANO nella sua cartella
            string cartellaDaSpostare = (daSpostare && lavoro != null && settings.CrmCartellaCliente && !string.IsNullOrWhiteSpace(lavoro.cartella)) ? lavoro.cartella : null;

            // cliente del CRM: com'è andata la cassetta. Scarta / Rifai cancellano il file appena registrato.
            if (lavoro != null)
            {
                bool haFile = muxOk && RecordedFiles(final).Any();
                bool cancella = false;
                string esito;
                if (!haFile) esito = "rifai";
                else if (settings.CrmRipartenzaAttiva && durataStop.TotalSeconds < Math.Max(10, settings.CrmRipartenzaSec))
                {
                    // partenza sbagliata: nessuna domanda, non si conta, file breve cancellato, si riparte dalla stessa cassetta
                    esito = "rifai"; cancella = true;
                    AppendLog($"Partenza sbagliata (fermata dopo {DurataTesto((int)durataStop.TotalSeconds)}): cassetta non contata, file cancellato. La prossima registrazione riparte dalla cassetta {cassettaInCorso}.");
                }
                else if (settings.CrmChiediFine)
                {
                    using var ff = new FineCassettaForm(settings.DarkTheme, lavoro.cliente, cassettaInCorso, lavoro.nastri_totali, lavoro.nastri_fatti, durataStop);
                    ff.ShowDialog(this);
                    esito = ff.Esito;
                    cancella = esito == "scartata" || esito == "ricomincia";
                    if (esito == "ricomincia")
                    {
                        esito = "rifai";          // per il CRM: non contata, totale invariato, stessa cassetta
                        ricomincia = true;
                        AppendLog($"Ricomincia la cassetta {cassettaInCorso}: video cancellato, la registrazione riparte subito");
                    }
                }
                else esito = "completata";   // senza domanda: si conta sempre
                if (cancella)
                {
                    cartellaDaSpostare = null;   // file cancellato: niente da spostare
                    if (moveTo != null) { mirror?.Abort(); mirror = null; }
                    foreach (var fdel in RecordedFiles(final).Concat(RecordedFiles(written)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                    {
                        try { File.Delete(fdel); AppendLog("Cancellato: " + Path.GetFileName(fdel)); } catch (Exception ex) { AppendLog("File non cancellato (" + ex.Message + "): " + fdel); }
                        if (moveTo != null) try { File.Delete(Path.Combine(moveTo, Path.GetFileName(fdel))); } catch { }
                    }
                }
                await CrmFineCassetta(esito, Path.GetFileName(final.Replace("%03d", "000")));
            }

            // nome della cassetta: rinomina il file (niente più rinomina a mano in Esplora file)
            if (muxOk && settings.AskNameAtEnd && RecordedFiles(final).Any())
            {
                string suggested = txtName.Text.Trim();
                string n = Prompt("Nome della cassetta", "Come si chiama questa cassetta? (Invio per confermare, Annulla per lasciare il nome automatico)", suggested);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    var renamed = RenameRecording(final, n.Trim());
                    if (renamed != null) { final = renamed; written = renamed; AppendLog("Rinominato: " + Path.GetFileName(renamed.Replace("%03d", "000"))); }
                }
                txtName.Text = "";
            }

            // controllo automatico dell'audio nel file: così non si resta col dubbio
            if (RecordedFiles(final).Any() && settings.Sources.Any(x => x.Visible && x.HasAudio))
            {
                double mean, max; long n;
                lock (audioStatsLock) { n = audioSamples; max = audioPeak; mean = n > 0 && audioEnergy > 0 ? 10 * Math.Log10(audioEnergy / n) : -91; }
                if (n == 0) AppendLog("Audio: nessuna misura durante la registrazione (VU non disponibile)");
                else if (max <= -60) AppendLog($"⚠ ATTENZIONE: l'audio registrato è SILENZIO (picco {max:0.0} dB) — controlla il dispositivo audio della sorgente");
                else AppendLog($"Audio registrato OK: medio {mean:0.0} dB, picco {max:0.0} dB" + (max >= -0.5 ? " — satura, abbassa il volume nel mixer" : ""));
            }

            // registrato sul PC: il grosso è già in rete (copia a pezzi), qui si completa l'ultimo pezzo
            if (moveTo != null)
            {
                var files = RecordedFiles(final).Concat(RecordedFiles(written)).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
                var m = mirror; mirror = null;
                if (m != null && files.Count == 1)
                {
                    string target = await m.FinishAsync(files[0], rewritten, t => { if (!IsDisposed) lblRec.Text = t; });
                    if (target != null) { AppendLog("In rete: " + target); files.Clear(); }
                    else { AppendLog("Copia in rete a pezzi non completata (" + m.LastError + "): copio il file intero"); m.Abort(); }
                }
                else m?.Abort();
                if (files.Count > 0) await MoveToNetwork(files, moveTo);
            }

            // cliente scelto durante la registrazione: sposta (taglia e incolla) i file nella cartella del cliente
            if (cartellaDaSpostare != null && muxOk) await SpostaNellaCartellaCliente(final, moveTo, cartellaDaSpostare);
            daSpostare = false;

            }
            catch (Exception ex)
            {
                AppendLog("Errore nella chiusura: " + ex.Message + ". I file esistenti sono conservati.");
                try { await Task.Run(() => engine.StopRecording()); } catch { }
                if (!IsDisposed) MessageBox.Show(this, "Non è stato possibile completare la chiusura. I file esistenti sono conservati.\n" + ex.Message, "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                finalizing = false; blankSince = null;
                pausedSince = null; pausedTotal = TimeSpan.Zero;
                if (!IsDisposed && !closingApp)
                {
                    lblRec.Text = "";
                    if (restartPreview && !engine.IsRunning) StartPreview();
                    SetButtons();
                    if (ricomincia)
                    {
                        // «Ricomincia la cassetta»: si riparte come se si premesse Registra (stesso cliente, stessa cassetta)
                        ricomincia = false;
                        lblRec.Text = "Riparto a registrare…";
                        BeginInvoke(new Action(async () =>
                        {
                            await Task.Delay(1500);   // l'anteprima deve essere di nuovo in piedi
                            if (IsDisposed || closingApp || engine.IsRecording || finalizing) return;
                            StartRecording();
                        }));
                    }
                }
                else ricomincia = false;
            }
        }

        /// <summary>Cartella di rete: percorso \\server\... oppure lettera di un'unità di rete mappata.</summary>
        static bool IsNetworkFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder)) return false;
                if (folder.StartsWith(@"\\")) return true;
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { return false; }
        }

        static string LocalStagingFolder()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VHSCapture", "Da spostare");
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Copia in rete con verifica della dimensione, poi cancella dal PC. Se la rete non risponde il file resta sul PC.</summary>
        async Task<bool> MoveToNetwork(List<string> files, string dest)
        {
            bool allOk = true;
            foreach (var src in files)
            {
                string name = Path.GetFileName(src);
                string target = Path.Combine(dest, name);
                for (int i = 1; File.Exists(target); i++)
                    target = Path.Combine(dest, Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name));
                long total = 0; try { total = new FileInfo(src).Length; } catch { }
                string err = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    if (!IsDisposed) lblRec.Text = $"Copio in rete {name}…";
                    err = await Task.Run(() =>
                    {
                        string part = target + ".part";
                        try
                        {
                            if (!Directory.Exists(dest)) return "cartella di rete non raggiungibile";
                            using (var fin = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                            using (var fout = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                            {
                                var buf = new byte[4 << 20]; int r; long done = 0; var tick = Stopwatch.StartNew();
                                while ((r = fin.Read(buf, 0, buf.Length)) > 0)
                                {
                                    fout.Write(buf, 0, r); done += r;
                                    if (tick.ElapsedMilliseconds > 500 && total > 0)
                                    {
                                        tick.Restart();
                                        int pc = (int)(done * 100 / total);
                                        try { BeginInvoke(new Action(() => { if (!IsDisposed) lblRec.Text = $"Copio in rete {name}… {pc}%"; })); } catch { }
                                    }
                                }
                            }
                            if (new FileInfo(part).Length != new FileInfo(src).Length) { try { File.Delete(part); } catch { } return "copia incompleta"; }
                            File.Move(part, target);
                            File.Delete(src);
                            return null;
                        }
                        catch (Exception ex) { try { if (File.Exists(part)) File.Delete(part); } catch { } return ex.Message; }
                    });
                    if (err == null) break;
                    await Task.Delay(5000);
                }
                if (err == null) AppendLog("Spostato in rete: " + target);
                else
                {
                    allOk = false;
                    AppendLog($"⚠ Non riesco a copiare in rete ({err}): il file resta sul PC in {src}");
                    if (!IsDisposed) MessageBox.Show(this, $"Il video è salvo, ma non sono riuscito a copiarlo in rete ({err}).\nÈ sul PC in:\n{src}\n\nAl prossimo avvio ti chiedo se spostarlo.", "VHSCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            return allOk;
        }

        /// <summary>All'avvio: registrazioni rimaste sul PC (rete giù, chiusura improvvisa) → propongo di spostarle.</summary>
        async void OfferPendingMoves()
        {
            try
            {
                string dest = settings.ResolvedOutputFolder();
                string stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VHSCapture", "Da spostare");
                if (!Directory.Exists(stage)) return;
                var files = Directory.GetFiles(stage).Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
                if (files.Count == 0) return;
                AppendLog($"Sul PC ci sono {files.Count} registrazioni non ancora spostate in rete ({stage})");
                if (!Directory.Exists(dest)) return;
                var r = MessageBox.Show(this, $"Ci sono {files.Count} registrazioni rimaste sul PC:\n" + string.Join("\n", files.Take(8).Select(Path.GetFileName)) +
                    $"\n\nLe sposto adesso in:\n{dest}?", "VHSCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                await MoveToNetwork(files, dest);
                if (!IsDisposed && !CaptureBusy) lblRec.Text = "";
            }
            catch { }
        }

        void OpenSettings()
        {
            if (CaptureBusy) { MessageBox.Show(this, "Ferma la registrazione prima di cambiare le impostazioni.", "VHSCapture"); return; }
            using var f = new SettingsForm(settings);
            var r = f.ShowDialog(this);
            Theme.Apply(this, settings.DarkTheme);
            if (r != DialogResult.OK) return;
            engine.ResetGpuRetry(settings.QsvBackend);
            CrmDopoImpostazioni();
            RefreshSourceList(); RebuildMixer();
            canvas.CanvasW = settings.CanvasW; canvas.CanvasH = settings.CanvasH;
            canvas.Invalidate();
            StartPreview();
        }

        string pendingFitId;

        /// <summary>Lo standard scelto nelle Proprietà (PAL/NTSC) porta con sé il frame rate della registrazione.</summary>
        bool ApplyCanvasFps(string fps)
        {
            if (string.IsNullOrEmpty(fps) || (fps == settings.Fps && settings.CanvasW == 1920 && settings.CanvasH == 1080)) return false;
            settings.Fps = fps; settings.CanvasW = 1920; settings.CanvasH = 1080;
            canvas.CanvasW = 1920; canvas.CanvasH = 1080;
            settings.Save();
            AppendLog($"Registrazione impostata a 1920×1080 @ {fps} fps (dallo standard della sorgente)");
            return true;
        }

        /// <summary>Dopo un profilo "automatico": appena ffmpeg dice la risoluzione vera, adatto la sorgente al canvas con le proporzioni giuste.</summary>
        void FitPendingSource()
        {
            if (pendingFitId == null) return;
            var src = settings.Sources.FirstOrDefault(x => x.Id == pendingFitId);
            if (src == null) { pendingFitId = null; return; }
            if (engine.GetInputSize(src.Id) == null) return;
            pendingFitId = null;
            src.FitTo(settings.CanvasW, settings.CanvasH);
            settings.Save();
            engine.ApplyTransform(src);
            if (!engine.LiveControl) RestartIfRunning();
            canvas.Invalidate();
            var (w, h) = src.NaturalSize();
            AppendLog($"Proporzioni adattate alla sorgente reale ({w}×{h}): {src.W}×{src.H} sul canvas");
        }

        // ===================== dispositivi rinominati da Windows =====================
        /// <summary>
        /// Dopo un aggiornamento Windows reinstalla spesso i driver USB e rinomina il dispositivo
        /// (es. "USB Audio" → "2- USB Audio", "Microfono (USB Audio)" → "Microfono (3- USB Audio)").
        /// Qui lo ritrovo lo stesso confrontando il nome "pulito", e aggiorno le impostazioni.
        /// </summary>
        bool TryResolveDevices()
        {
            var (videos, audios) = FFmpeg.ListDevices();
            bool changed = false;
            foreach (var src in settings.Sources.Where(x => x.Type == SourceType.Capture))
            {
                var v = Resolve(src.VideoDevice, videos);
                if (v != null && v != src.VideoDevice) { AppendLog($"Windows ha rinominato il video \"{src.VideoDevice}\" in \"{v}\": ritrovato e aggiornato"); src.VideoDevice = v; changed = true; }
                else if (v == null && !string.IsNullOrEmpty(src.VideoDevice) && !videos.Contains(src.VideoDevice))
                    AppendLog($"⚠ Dispositivo video \"{src.VideoDevice}\" non trovato: è collegato? Scegline un altro nelle Proprietà della sorgente");
                if (!string.IsNullOrEmpty(src.AudioDevice))
                {
                    var a = Resolve(src.AudioDevice, audios);
                    if (a != null && a != src.AudioDevice) { AppendLog($"Windows ha rinominato l'audio \"{src.AudioDevice}\" in \"{a}\": ritrovato e aggiornato"); src.AudioDevice = a; changed = true; }
                    else if (a == null && !audios.Contains(src.AudioDevice))
                        AppendLog($"⚠ Dispositivo audio \"{src.AudioDevice}\" non trovato: è collegato? Scegline un altro nelle Proprietà della sorgente");
                }
            }
            if (changed) { settings.Save(); RefreshSourceList(); RebuildMixer(); }
            return changed;
        }

        static string NormDevice(string n)
        {
            if (string.IsNullOrEmpty(n)) return "";
            n = System.Text.RegularExpressions.Regex.Replace(n, @"^\s*\d+\s*-\s*", "");          // "2- USB Audio"
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\(\s*\d+\s*-\s*", "(");         // "Microfono (2- USB Audio)"
            return n.Trim().ToLowerInvariant();
        }

        /// <summary>Il nome esatto se c'è; altrimenti l'unico dispositivo con lo stesso nome "pulito"; altrimenti null.</summary>
        static string Resolve(string saved, List<string> available)
        {
            if (string.IsNullOrEmpty(saved) || available.Contains(saved)) return saved;
            string ns = NormDevice(saved);
            var same = available.Where(d => NormDevice(d) == ns).ToList();
            if (same.Count >= 1) return same[0];
            var close = available.Where(d => NormDevice(d).Contains(ns) || ns.Contains(NormDevice(d))).ToList();
            return close.Count == 1 ? close[0] : null;
        }

        void ToggleRecording()
        {
            if (finalizing || startingRecording) return;
            if (engine.IsRecording) StopRecording(true);
            else StartRecording();
        }

        /// <summary>Pulsante unico: rosso "Avvia registrazione" / chiaro "Ferma registrazione" (colori diversi, come chiesto).</summary>
        void UpdateRecButton()
        {
            if (btnRec == null) return;
            bool compact = compactState == true, rec = engine.IsRecording;
            if (finalizing || startingRecording)
            {
                btnRec.Text = compact ? "…" : startingRecording ? "Preparazione…" : "Chiusura file…";
                btnRec.Variant = "stop"; btnRec.Enabled = false;
            }
            else if (rec)
            {
                btnRec.Text = compact ? "⏹  STOP" : "⏹   Ferma registrazione";
                btnRec.Variant = "stop"; btnRec.Enabled = true;
            }
            else
            {
                btnRec.Text = compact ? "⏺  REC" : "⏺   Avvia registrazione";
                btnRec.Variant = "rec"; btnRec.Enabled = true;
            }
            btnRec.MinimumSize = new Size(compact ? 96 : 230, 36);
            btnRec.Invalidate();
            if (btnPause != null)
            {
                bool p = pausedSince != null;
                btnPause.Visible = rec && !finalizing && !startingRecording;
                btnPause.Text = compact ? (p ? "▶" : "⏸") : (p ? "▶   Riprendi" : "⏸   Pausa");
                btnPause.Variant = p ? "accent" : "ghost";
                btnPause.MinimumSize = new Size(compact ? 44 : 120, 36);
                btnPause.Invalidate();
            }

        }

        /// <summary>Tempo registrato davvero (senza le pause): coincide con la durata del file.</summary>
        TimeSpan RecElapsed()
        {
            var t = DateTime.Now - recStart - pausedTotal;
            if (pausedSince != null) t -= DateTime.Now - pausedSince.Value;
            return t < TimeSpan.Zero ? TimeSpan.Zero : t;
        }

        void TogglePause()
        {
            if (!engine.IsRecording || finalizing || startingRecording) return;
            if (pausedSince == null)
            {
                engine.PauseRecording();
                pausedSince = DateTime.Now;
                AppendLog("Registrazione in pausa");
            }
            else
            {
                engine.ResumeRecording();
                pausedTotal += DateTime.Now - pausedSince.Value;
                pausedSince = null;
                blankSince = null; blankFloor = DateTime.UtcNow;
                AppendLog($"Registrazione ripresa (riparte dall'ultimo keyframe, al massimo {settings.KeyframeSec} s prima)");
            }
            UpdateRecButton();
        }

        void SetButtons()
        {
            bool rec = CaptureBusy;
            UpdateRecButton();
            btnSettings.Enabled = !rec && !finalizing;
            txtName.Enabled = !rec;
            UpdateSourceButtons();
        }

        // ---------------- eventi engine ----------------

        /// <summary>Dal thread di rendering: frame mostrato → restituito al motore + statistiche.</summary>
        void OnFrameShown()
        {
            System.Threading.Interlocked.Increment(ref frames);
            lastFrameAt = DateTime.Now; autoRetried = false; devicesResolved = false;
            double t = paintClock.Elapsed.TotalSeconds;
            if (lastPaint > 0) paintMaxGap = Math.Max(paintMaxGap, t - lastPaint);
            lastPaint = t; System.Threading.Interlocked.Increment(ref painted);
        }

        void OnEngineExited(int code)
        {
            if (!IsHandleCreated || IsDisposed) return;
            int runId = engine.RunId;
            BeginInvoke(new Action(() =>
            {
                if (runId != engine.RunId || IsDisposed) return;
                if (code != 0 && code != 255 && !finalizing) AppendLog($"ffmpeg terminato con codice {code}");
                if (!engine.IsRunning && !finalizing)
                {
                    if (engine.IsRecording)
                    {
                        engine.Recorder.ReportFailure("Il dispositivo di cattura si è fermato durante la registrazione.");
                        AppendLog("ATTENZIONE: la pipeline si è fermata durante la registrazione — chiudo il file");
                        StopRecording(true);
                        return;
                    }
                    if (engine.GpuActive && !engine.GpuDisabled)
                    {
                        RetryGpuPreview();
                        return;
                    }
                    // Se non si apre nemmeno l'encoder, conserva il fallback software esistente.
                    string lg0; lock (runLog) lg0 = runLog.ToString();
                    // paracadute VU: se la pipe dei livelli non si apre, riparto senza misuratori (anteprima e registrazione prima di tutto)
                    if (lg0.Contains("Could not find") && lg0.Contains("device with name") && !devicesResolved)
                    {
                        devicesResolved = true;
                        if (TryResolveDevices()) { StartPreview(); return; }
                    }
                    if (lg0.Contains("Could not bind ZMQ") && !autoRetried)
                    {
                        AppendLog("Porta del controllo live occupata: riparto");
                        autoRetried = true;
                        restartTimer.Stop(); restartTimer.Start();
                        return;
                    }
                    if (lg0.Contains("Could not open") && (lg0.Contains("vhscap_an_") || lg0.Contains("vhscap_fr_")) && !engine.AnalysisDisabled)
                    {
                        AppendLog("Rilevamento fine cassetta non disponibile su questo PC: riparto senza");
                        engine.AnalysisDisabled = true;
                        StartPreview();
                        return;
                    }
                    if (lg0.Contains("Could not open") && lg0.Contains("vhscap_me_") && !engine.MetersDisabled)
                    {
                        AppendLog("Misuratori audio non disponibili su questo PC: riparto senza VU");
                        engine.MetersDisabled = true;
                        StartPreview();
                        return;
                    }
                    // solo un vero errore di apertura dell'encoder (non gli errori a cascata dopo un filtro fallito)
                    bool graphFail = lg0.Contains("Error initializing filters") || lg0.Contains("Error reinitializing filters");
                    bool encFail = !graphFail && (lg0.Contains("Error while opening encoder") || lg0.Contains("Error creating a MFX session") || lg0.Contains("Could not open encoder") && lg0.Contains("[enc:h264_"));
                    if (encFail && settings.Encoder != "libx264" && !autoRetried)
                    {
                        AppendLog($"L'encoder {settings.Encoder} non si apre su questo PC: passo a x264 software");
                        settings.Encoder = "libx264"; settings.Save(); autoRetried = true;
                        StartPreview();
                        return;
                    }
                    if (frames == 0 && code != 0 && !autoRetried && TryAutoFallback()) return;
                    if (frames == 0 && code != 0) { canvas.Message = "ffmpeg non è partito — vedi il Log qui sotto (F5 per riprovare)"; if (splitLog.Panel2Collapsed) ToggleLog(); }
                    else canvas.Message = "Anteprima ferma — premi F5 per riavviarla";
                    canvas.SetFrame(null);
                    ResetMeters();
                    SetButtons();
                }
            }));
        }

        /// <summary>Se il dispositivo ha rifiutato risoluzione/fps, riprova una volta lasciando decidere al driver.</summary>
        bool TryAutoFallback()
        {
            string log; lock (runLog) log = runLog.ToString();
            if (!log.Contains("Could not set video options")) return false;
            bool changed = false;
            foreach (var src in settings.Sources.Where(x => x.Visible && x.Type == SourceType.Capture))
            {
                if (src.InputSize != "auto" || src.InputFps != "auto" || src.VideoFormat != "auto")
                {
                    string fmt = src.VideoFormat == "auto" ? "" : " in " + src.VideoFormat.ToUpperInvariant();
                    AppendLog($"\"{src.VideoDevice}\" non supporta {src.InputSize} @ {src.InputFps}{fmt}: passo a formato, risoluzione e fps automatici");
                    src.InputSize = "auto"; src.InputFps = "auto"; src.VideoFormat = "auto"; changed = true;
                    pendingFitId = src.Id;   // proporzioni ricalcolate sulla risoluzione vera appena arriva
                }
            }
            if (!changed) return false;
            autoRetried = true;
            settings.Save();
            StartPreview();
            return true;
        }

        async void CheckEncoderAsync()
        {
            // verifica salvata e ffmpeg.exe uguale: niente prove all'avvio (le prove aprono sessioni GPU e rallentano la partenza)
            string stamp = FFmpeg.BinaryStamp();
            List<string> list;
            if (settings.EncodersWorking != null && settings.EncodersWorking.Count > 0 && stamp != "" && settings.EncodersStamp == stamp)
            {
                list = new List<string>(settings.EncodersWorking);
                AppendLog("Encoder su questo PC: " + string.Join(", ", list) + " (verifica salvata; per rifarla: Impostazioni → Verifica encoder)");
            }
            else
            {
                var results = await Task.Run(() => FFmpeg.ProbeH264Encoders());
                list = results.Where(x => x.Works).Select(x => x.Encoder).ToList();
                if (IsDisposed || closingApp) return;
                AppendLog("Encoder funzionanti su questo PC: " + string.Join(", ", list));
                var missing = results.Where(x => !x.Works).Select(x => $"{x.Encoder} ({FFmpeg.ShortReason(x)})").ToList();
                if (missing.Count > 0) AppendLog("Non disponibili: " + string.Join(", ", missing));
                if (list.Count > 0) { settings.EncodersWorking = list; settings.EncodersStamp = stamp; settings.Save(); }
            }
            if (IsDisposed || closingApp) return;
            if (CaptureBusy) return;
            string chosen = FFmpeg.ChooseEncoder(settings, list);
            string pick = chosen != settings.Encoder ? chosen : null;
            if (settings.EncoderUserSet && !list.Contains(settings.Encoder))
                AppendLog("L'encoder scelto non supera la prova iniziale: mantengo la tua scelta. Controlla Impostazioni → Registrazione → Verifica encoder.");
            if (pick != null)
            {
                settings.Encoder = pick;
                settings.Save();
                if (engine.IsRunning && !CaptureBusy) StartPreview();
            }
        }

        void RetryGpuPreview()
        {
            if (engine.TryLegacyGpu())
            {
                AppendLog("Intel D3D11 non disponibile: uso QuickSync con DXVA2 e me lo ricordo per questo PC (niente più attesa all'avvio)");
                settings.QsvBackend = "dxva2";
                settings.Save();
            }
            else
            {
                engine.GpuDisabled = true;
                AppendLog("Filtri Intel GPU non disponibili: ritorno automatico ai filtri CPU");
            }
            StartPreview();
        }

        static string EncName(string e) => e switch { "h264_nvenc" => "NVIDIA NVENC", "h264_qsv" => "Intel QuickSync", "h264_amf" => "AMD AMF", _ => "x264 (CPU)" };

        /// <summary>
        /// Primo avvio (nessuna sorgente): aggiungo da solo il grabber con lo standard PAL, senza aprire finestre.
        /// Video: il dispositivo che non sembra una webcam; audio: quello che ha lo stesso nome (es. "USB 2828x").
        /// </summary>
        void AutoAddGrabber()
        {
            var (videos, audios) = FFmpeg.ListDevices();
            if (videos.Count == 0)
            {
                canvas.Message = "Nessun grabber trovato: collegalo e premi ＋ in Sorgenti";
                canvas.Invalidate();
                AppendLog("Nessun dispositivo video trovato");
                return;
            }
            bool LooksLikeCam(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, "camera|webcam|integrated|facetime|ir |obs virtual", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string video = videos.FirstOrDefault(v => !LooksLikeCam(v)) ?? videos[0];

            // audio: la parola più "caratteristica" del nome video (es. "2828x") presente anche nel nome audio
            string audio = "";
            var tokens = System.Text.RegularExpressions.Regex.Split(video, @"[^A-Za-z0-9]+")
                           .Where(t => t.Length >= 3 && !new[] { "usb", "device", "video", "capture" }.Contains(t.ToLowerInvariant()))
                           .OrderByDescending(t => t.Any(char.IsDigit)).ToList();
            foreach (var t in tokens)
            {
                var m = audios.FirstOrDefault(a => a.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) { audio = m; break; }
            }

            var src = new Source { Type = SourceType.Capture, Name = "Grabber USB", VideoDevice = video, AudioDevice = audio };
            var v = VideoStandard.PAL;
            src.InputSize = v.Size; src.InputFps = v.InFps; src.DeinterlaceMode = v.Deint; src.CropB = v.CropB;
            src.Locked = true;
            settings.CanvasW = 1920; settings.CanvasH = 1080; settings.Fps = v.CanvasFps;
            canvas.CanvasW = 1920; canvas.CanvasH = 1080;
            src.FitTo(settings.CanvasW, settings.CanvasH);
            settings.Sources.Add(src);
            settings.Save();
            RefreshSourceList(); RebuildMixer();
            AppendLog($"Sorgente aggiunta da sola: video \"{video}\", audio \"{(audio == "" ? "nessuno" : audio)}\", standard PAL. Doppio clic sulla sorgente per cambiare.");
            StartPreview();
        }

        /// <summary>Rinomina la registrazione in "Nome cassetta.mp4" (o Nome_000.mp4… se divisa). Mai sovrascrivere: aggiunge (2), (3)…</summary>
        string RenameRecording(string pattern, string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var files = RecordedFiles(pattern).ToList();
            if (files.Count == 0) return null;
            string dir = Path.GetDirectoryName(files[0]), ext = Path.GetExtension(files[0]);
            string baseName = name;
            for (int n = 2; ; n++)
            {
                bool clash = files.Count == 1 ? File.Exists(Path.Combine(dir, baseName + ext))
                                              : Enumerable.Range(0, files.Count).Any(i => File.Exists(Path.Combine(dir, $"{baseName}_{i:000}{ext}")));
                if (!clash) break;
                baseName = $"{name} ({n})";
            }
            try
            {
                if (files.Count == 1)
                {
                    string dest = Path.Combine(dir, baseName + ext);
                    File.Move(files[0], dest);
                    return dest;
                }
                for (int i = 0; i < files.Count; i++) File.Move(files[i], Path.Combine(dir, $"{baseName}_{i:000}{ext}"));
                return Path.Combine(dir, baseName + "_%03d" + ext);
            }
            catch (Exception ex) { AppendLog("Rinomina non riuscita: " + ex.Message); return null; }
        }

        int PreviewWidth()
        {
            // anteprima ~ grande quanto il riquadro a schermo: disegno quasi 1:1, niente ridimensionamenti costosi
            double sc = Math.Min((double)Math.Max(1, canvas.ClientSize.Width) / settings.CanvasW, (double)Math.Max(1, canvas.ClientSize.Height) / settings.CanvasH);
            return (int)Math.Clamp(settings.CanvasW * sc, 480, 1280);
        }

        void ToggleMonitor()
        {
            settings.AudioMonitor = !settings.AudioMonitor;
            settings.Save();
            btnMonitor.Text = settings.AudioMonitor ? "🎧  Ascolto attivo" : "🎧  Ascolta";
            btnMonitor.Variant = settings.AudioMonitor ? "accent" : "ghost";
            btnMonitor.Invalidate();
            if (!settings.AudioMonitor) monitor.Stop();
            if (engine.IsRecording) { if (settings.AudioMonitor) AppendLog("L'ascolto partirà alla prossima anteprima (non interrompo la registrazione)"); return; }
            RestartIfRunning();
        }

        void StartMonitorIfNeeded()
        {
            if (settings.AudioMonitor && settings.Sources.Any(x => x.Visible && x.HasAudio))
            {
                try { monitor.DeviceNumber = settings.MonitorDevice; monitor.Start(); } catch (Exception ex) { AppendLog("Ascolto audio non disponibile: " + ex.Message); }
            }
            else monitor.Stop();
        }

        IEnumerable<string> RecordedFiles(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return Array.Empty<string>();
            if (!pattern.Contains("%03d")) return File.Exists(pattern) ? new[] { pattern } : Array.Empty<string>();
            try { return Directory.GetFiles(Path.GetDirectoryName(pattern), Path.GetFileName(pattern).Replace("%03d", "???")).OrderBy(x => x).ToArray(); }
            catch { return Array.Empty<string>(); }
        }

        long RecordedBytes(string pattern)
        {
            long t = 0;
            foreach (var f in RecordedFiles(pattern)) { try { t += new FileInfo(f).Length; } catch { } }
            return t;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

        static long FreeBytes(string folder)
        {
            try { if (GetDiskFreeSpaceEx(folder.EndsWith("\\") ? folder : folder + "\\", out ulong f, out _, out _)) return (long)f; } catch { }
            return -1;
        }

        DateTime lastDiagAt = DateTime.MinValue;
        DateTime lastDiskCheck = DateTime.MinValue, lastCpuSample = DateTime.MinValue; long lastFree = -1;

        /// <summary>
        /// Fine cassetta: il rilevatore dice da quanti secondi di fila si vede solo lo sfondo del lettore/videocamera.
        /// Si ferma dopo il tempo impostato (default 120 s). Si arma solo dopo 10 s di filmato vero; pausa e avvio lo sospendono.
        /// </summary>
        void OnSignal(double blankSec, string detail)
        {
            var now = DateTime.UtcNow;
            if ((now - lastSignalAt).TotalSeconds > 2) blankSince = null;
            lastSignalAt = now;
            if (settings.DiagLog && engine.IsRecording && (now - lastSignalDiag).TotalSeconds >= 10)
            {
                lastSignalDiag = now;
                AppendLog($"[fine cassetta] {detail} · solo sfondo da {blankSec:0} s");
            }
            // Una seconda cattura non analizzata potrebbe ancora contenere video valido.
            if (pausedSince != null || startingRecording || finalizing || !engine.IsRecording ||
                settings.Sources.Count(x => x.Visible && x.Type == SourceType.Capture) != 1)
            { blankSince = null; return; }
            // lo sfondo visto durante una pausa o prima di premere Registra non conta
            blankSec = Math.Min(blankSec, Math.Max(0, (now - blankFloor).TotalSeconds));
            if (blankSec < 3)
            {
                blankSince = null; blankStartRecSec = -1;
                contentSamples++;           // 2 al secondo: 20 = 10 s di filmato vero
                return;
            }
            if (!settings.AutoStopOnBlank || contentSamples < 20) { blankSince = null; return; }
            blankStartRecSec = Math.Max(0, RecElapsed().TotalSeconds - blankSec);   // dove inizia lo sfondo nel file (taglio coda)
            blankSince = blankSec >= 10 ? now - TimeSpan.FromSeconds(blankSec) : (DateTime?)null;
            if (blankSec >= settings.AutoStopSeconds)
            {
                AppendLog($"Fine cassetta: da {blankSec:0} s si vede solo lo sfondo del lettore ({detail}). Stop.");
                autoStopped = true;
                StopRecording(true);
            }
        }

        void AppendLog(string line)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (txtLog.TextLength > 200000) txtLog.Clear();
                    txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
                }));
            }
            catch { }
        }

        // ---------------- stato ----------------

        void UpdateStatus()
        {
            if (!finalizing && !engine.IsRecording && engine.IsRunning && engine.GpuActive &&
                frames == 0 && (DateTime.UtcNow - previewStartedAt).TotalSeconds > 15)
            {
                RetryGpuPreview();
                return;
            }
            FitPendingSource();
            foreach (var r in mixerRows.Values) r.RefreshState(engine.IsRunning);
            string enc = settings.Encoder + " " + (settings.RateControl == "CRF" ? $"CRF {settings.Crf}" : $"{settings.RateControl} {settings.VideoBitrate} kbps");
            var parts = new List<string> { $"{settings.CanvasW}×{settings.CanvasH} @{settings.Fps}" };
            if (engine.IsRunning)
            {
                if ((DateTime.Now - lastCpuSample).TotalSeconds >= 1) { lastCpu = engine.CpuPercent(); lastCpuSample = DateTime.Now; }
                var st = lastStats;
                parts.Add(engine.GpuActive ? "filtri Intel GPU" : "filtri CPU");
                if (st != null) parts.Add($"uscita {st.Fps:0.0} fps");
                // ogni secondo: ritmo di arrivo da ffmpeg e ritmo di disegno a schermo, con la pausa più lunga
                if ((DateTime.Now - lastDiagAt).TotalSeconds >= 1)
                {
                    lastDiagAt = DateTime.Now;
                    var (afps, agap, adrop) = engine.TakeArrivalStats();
                    adrop += canvas.TakeLatencyDrops();
                    double now = paintClock.Elapsed.TotalSeconds;
                    double pfps = paintWindowStart > 0 ? painted / (now - paintWindowStart) : 0;
                    previewDiag = $"anteprima: arrivo {afps:0} fps (pausa max {agap:0} ms) · a schermo {pfps:0} fps (pausa max {paintMaxGap * 1000:0} ms)" + (adrop > 0 ? $" · saltati {adrop}" : "");
                    // ogni 5 s anche nel Log, così basta incollare il Log per la diagnosi
                    diagAcc.Add((afps, agap, pfps, paintMaxGap * 1000, adrop, engine.SourceFps, lastStats?.Fps ?? 0));
                    if (diagAcc.Count >= 5 && !settings.DiagLog) diagAcc.Clear();
                    if (diagAcc.Count >= 5)
                    {
                        var (rAvg, rMax) = canvas.TakeRenderStats();
                        AppendLog($"[diagnostica 5 s] sorgente {diagAcc.Average(d => d.src):0.0} fps · uscita {diagAcc.Average(d => d.outf):0.0} fps · " +
                                  $"arrivo anteprima {diagAcc.Average(d => d.af):0.0} fps (pausa max {diagAcc.Max(d => d.ag):0} ms) · " +
                                  $"a schermo {diagAcc.Average(d => d.pf):0.0} fps (pausa max {diagAcc.Max(d => d.pg):0} ms) · saltati {diagAcc.Sum(d => d.dr)} · " +
                                  $"disegno frame {rAvg:0.0} ms (max {rMax:0}) · CPU ffmpeg {lastCpu:0}%");
                        diagAcc.Clear();
                    }
                    painted = 0; paintMaxGap = 0; paintWindowStart = now;
                }
                if (previewDiag != "" && settings.DiagLog) parts.Add(previewDiag);
                double sf = engine.SourceFps;
                if (sf > 0)
                {
                    var capSrc = settings.Sources.FirstOrDefault(x => x.Visible && x.Type == SourceType.Capture);
                    double exp = 0;
                    var inf = capSrc != null ? engine.GetInputInfo(capSrc.Id) : null;   // "1280×720 · 30 fps · nv12"
                    if (inf != null) { var m = System.Text.RegularExpressions.Regex.Match(inf, @"([\d.]+) fps"); if (m.Success) double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out exp); }
                    string warn = exp > 0 && sf < exp * 0.85 ? $" ⚠ (dovrebbe mandarne {exp:0}: la sorgente rallenta)" : "";
                    parts.Add($"sorgente {sf:0.0} fps{warn}");
                    // sorgente (dopo il deinterlaccio) e registrazione devono essere una multipla dell'altra,
                    // se no ffmpeg ripete/butta fotogrammi a caso e il movimento scatta (es. 60 → 50)
                    if (double.TryParse(settings.Fps, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cf) && cf > 0 && sf > 5)
                    {
                        static bool Whole(double x) { double n = Math.Round(x); return n >= 1 && Math.Abs(x - n) < 0.06 * n; }
                        if (!Whole(cf / sf) && !Whole(sf / cf))
                            parts.Add($"⚠ sorgente {sf:0} fps e registrazione {cf:0.##} fps non combaciano: il movimento può scattare (usa lo standard PAL/NTSC)");
                    }
                }
                if (st != null && (st.Drop > 0 || st.Dup > 0)) parts.Add($"persi {st.Drop} · duplicati {st.Dup}");
                parts.Add($"CPU ffmpeg {lastCpu:0}%");
                parts.Add(EncName(settings.Encoder));
            }
            // spazio libero e tempo di registrazione residuo (come le Statistiche di OBS)
            if ((DateTime.Now - lastDiskCheck).TotalSeconds > 5) { lastFree = FreeBytes(settings.RecordLocalFirst && IsNetworkFolder(settings.ResolvedOutputFolder()) ? LocalStagingFolder() : settings.ResolvedOutputFolder()); lastDiskCheck = DateTime.Now; }
            if (lastFree >= 0)
            {
                double bps = (settings.RateControl == "CRF" ? 8000 : settings.VideoBitrate) * 1000.0 / 8 + settings.AudioBitrate * 1000.0 / 8;
                var left = TimeSpan.FromSeconds(lastFree / Math.Max(1, bps));
                parts.Add($"liberi {Fmt(lastFree)} (~{(int)left.TotalHours} h {left.Minutes:00} m)");
            }
            lblStatus.Text = string.Join("   ·   ", parts);

            if ((DateTime.UtcNow - lastSignalAt).TotalSeconds > 2) blankSince = null;
            if (engine.IsRecording && !finalizing && !startingRecording)
            {
                // Scrittura lenta (NAS che si risveglia, rete, antivirus): NON si ferma più. Il video resta nel buffer
                // (~10 minuti) e viene scritto appena la destinazione riparte. Si ferma solo per un errore vero.
                if (pausedSince == null && engine.Recorder.OutputStarted)
                {
                    double stall = engine.Recorder.SecondsSinceOutput;
                    if (stall > 20 && !stallWarned) { stallWarned = true; AppendLog("⚠ La destinazione non scrive da 20 s: continuo a registrare in memoria e scrivo appena riparte."); }
                    else if (stall < 3 && stallWarned) { stallWarned = false; AppendLog("Scrittura ripresa: nessun fotogramma perso."); }
                }
                if (engine.Recorder.LastError != null || !engine.Recorder.MuxAlive)
                {
                    engine.Recorder.ReportFailure(engine.Recorder.LastError ?? "Il file non sta più ricevendo video.");
                    AppendLog(engine.Recorder.LastError);
                    StopRecording(true); return;
                }
                var el = RecElapsed();
                bool isPaused = pausedSince != null;
                long size = 0;
                size = RecordedBytes(recFile);
                bool blink = (DateTime.Now.Millisecond / 500) % 2 == 0;
                lblRec.Fill = isPaused ? Color.FromArgb(215, 150, 20) : Theme.Rec; lblRec.ForeColor = Color.White;
                string tag = isPaused ? "⏸ IN PAUSA" : $"{(blink ? "●" : "○")} REC";
                lblRec.Text = $"{tag}  {el:hh\\:mm\\:ss}   {Fmt(size)}   {Path.GetFileName(finalFile).Replace("_%03d", "")}";
                canvas.RecText = isPaused ? $"⏸  IN PAUSA  {el:hh\\:mm\\:ss}" : $"{(blink ? "●" : "○")}  REC  {el:hh\\:mm\\:ss}";
                if (blankSince != null && settings.AutoStopOnBlank && !isPaused)
                {
                    int left = Math.Max(0, settings.AutoStopSeconds - (int)(DateTime.UtcNow - blankSince.Value).TotalSeconds);
                    canvas.RecText += $"    solo sfondo: stop tra {left} s";
                }
                canvas.Invalidate();
                if (settings.MaxMinutes > 0 && el.TotalMinutes >= settings.MaxMinutes)
                {
                    AppendLog($"Stop automatico dopo {settings.MaxMinutes} minuti");
                    StopRecording(true);
                }
                if ((DateTime.Now - lastFrameAt).TotalSeconds > 5 && frames > 0)
                    lblRec.Text += "   ⚠ nessun frame da " + (int)(DateTime.Now - lastFrameAt).TotalSeconds + "s";
            }
            else if (!finalizing && !startingRecording)
            {
                if (canvas.RecText != null) { canvas.RecText = null; canvas.Invalidate(); }
                lblRec.Fill = engine.IsRunning ? Theme.Accent : Color.Transparent;
                lblRec.ForeColor = engine.IsRunning ? Color.White : Theme.Fore;
                lblRec.Text = engine.IsRunning ? "●  Anteprima" : "";
            }
        }

        static string Fmt(long b)
        {
            if (b > 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b > 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            return (b / 1024.0).ToString("0") + " KB";
        }
    }
}
