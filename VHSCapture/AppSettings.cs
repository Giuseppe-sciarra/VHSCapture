using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VHSCapture
{
    public enum SourceType { Capture, Image, Color }

    /// <summary>Una sorgente sul canvas (come in OBS).</summary>
    public class Source
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "Sorgente";
        public SourceType Type { get; set; } = SourceType.Capture;
        public bool Visible { get; set; } = true;
        /// <summary>Bloccata (come il lucchetto di OBS): non si sposta/ridimensiona dall'anteprima e non si seleziona cliccandoci sopra.</summary>
        public bool Locked { get; set; } = false;

        // Capture (dshow)
        public string VideoDevice { get; set; } = "";
        public string AudioDevice { get; set; } = "";
        public string InputSize { get; set; } = "720x576";
        public string InputFps { get; set; } = "25";
        /// <summary>auto, mjpeg, yuyv422, nv12 (come "Formato video" di OBS). Per 1080p60 dai grabber HDMI serve quasi sempre MJPEG.</summary>
        public string VideoFormat { get; set; } = "auto";
        /// <summary>off, yadif, yadif2x, bwdif, bwdif2x (come il deinterlacciamento di OBS). 2x = 50p da VHS PAL.
        /// fields = campi separati e raddoppiati in altezza, senza supporre la parità (ricostruzione NTSC su VCR PAL).</summary>
        public string DeinterlaceMode { get; set; } = "off";
        public bool? Deinterlace { get; set; } = null;          // solo migrazione dalle versioni precedenti
        public int RtBufMB { get; set; } = 512;
        /// <summary>Standard del grabber (scheda "Decoder video" del driver) che VHSCapture imposta prima di avviare la cattura:
        /// PAL_B, NTSC_M, PAL_60, NTSC_433… "" = non toccarlo (lo imposti a mano da Driver video…).</summary>
        public string TvStandard { get; set; } = "";
        /// <summary>bilinear, bicubic, lanczos, area (come "Filtro di ridimensionamento" di OBS).</summary>
        public string ScaleFilter { get; set; } = "bicubic";
        /// <summary>Ritardo audio in ms (come "Ritardo di sincronizzazione" di OBS). Positivo = audio più tardi.</summary>
        public int AudioOffsetMs { get; set; } = 0;

        // Ritaglio in pixel della sorgente (come Alt+trascina / filtro Ritaglia di OBS)
        public int CropL { get; set; } = 0;
        public int CropT { get; set; } = 0;
        public int CropR { get; set; } = 0;
        public int CropB { get; set; } = 0;

        // Image
        public string ImagePath { get; set; } = "";

        // Color
        public string Color { get; set; } = "#202020";

        // Trasformazione (coordinate canvas)
        public int X { get; set; } = 0;
        public int Y { get; set; } = 0;
        public int W { get; set; } = 720;
        public int H { get; set; } = 576;

        // Correzione colore
        public double Brightness { get; set; } = 0;   // -1..1
        public double Contrast { get; set; } = 1;     // 0..3
        public double Saturation { get; set; } = 1;   // 0..3
        public double Gamma { get; set; } = 1;        // 0.1..3
        public double Hue { get; set; } = 0;          // -180..180

        // Audio
        public double VolumeDb { get; set; } = 0;     // -60..+12
        public bool Muted { get; set; } = false;

        [JsonIgnore] public bool HasAudio => Type == SourceType.Capture && !string.IsNullOrWhiteSpace(AudioDevice);
        [JsonIgnore] public double VolumeLinear => Muted ? 0 : Math.Pow(10, VolumeDb / 20.0);
        [JsonIgnore] public double VolumeGain => Math.Pow(10, VolumeDb / 20.0);
        /// <summary>Cassetta NTSC da VCR PAL con grabber senza PAL-60: grabber in PAL B/G a 720×480 (colori giusti, quadro impaginato a 50 Hz).
        /// Il motore scarta i fotogrammi vuoti che il grabber manda in questo modo.</summary>
        [JsonIgnore] public bool IsNtscRebuild => Type == SourceType.Capture && DShowProps.TvSame(TvStandard, "PAL_B") &&
                                                 (DeinterlaceMode == "fields" || InputSize == "720x480");
        [JsonIgnore] public bool ColorIsNeutral => Math.Abs(Brightness) < 1e-6 && Math.Abs(Contrast - 1) < 1e-6 && Math.Abs(Saturation - 1) < 1e-6 && Math.Abs(Gamma - 1) < 1e-6 && Math.Abs(Hue) < 1e-6;

        public Source Clone() => (Source)MemberwiseClone();

        /// <summary>Le proprietà che richiedono un riavvio del grafo ffmpeg.</summary>
        public bool StructurallyEquals(Source o) =>
            Type == o.Type && Visible == o.Visible && VideoDevice == o.VideoDevice && AudioDevice == o.AudioDevice &&
            InputSize == o.InputSize && InputFps == o.InputFps && DeinterlaceMode == o.DeinterlaceMode && RtBufMB == o.RtBufMB &&
            VideoFormat == o.VideoFormat && ScaleFilter == o.ScaleFilter && AudioOffsetMs == o.AudioOffsetMs &&
            (TvStandard ?? "") == (o.TvStandard ?? "") &&
            ImagePath == o.ImagePath && Color == o.Color;

        public void CopyStructuralFrom(Source o)
        {
            Type = o.Type; Visible = o.Visible; VideoDevice = o.VideoDevice; AudioDevice = o.AudioDevice;
            InputSize = o.InputSize; InputFps = o.InputFps; DeinterlaceMode = o.DeinterlaceMode; RtBufMB = o.RtBufMB;
            VideoFormat = o.VideoFormat; ScaleFilter = o.ScaleFilter; AudioOffsetMs = o.AudioOffsetMs;
            TvStandard = o.TvStandard ?? "";
            ImagePath = o.ImagePath; Color = o.Color;
        }

        public void CopyAllFrom(Source o) { CopyStructuralFrom(o); CopyLiveFrom(o); }

        public void CopyLiveFrom(Source o)
        {
            X = o.X; Y = o.Y; W = o.W; H = o.H;
            Brightness = o.Brightness; Contrast = o.Contrast; Saturation = o.Saturation; Gamma = o.Gamma; Hue = o.Hue;
            VolumeDb = o.VolumeDb; Muted = o.Muted; Name = o.Name;
            CropL = o.CropL; CropT = o.CropT; CropR = o.CropR; CropB = o.CropB;
            Locked = o.Locked;
        }

        [JsonIgnore] public bool IsSD
        {
            get
            {
                var p = (InputSize ?? "").Split('x');
                return p.Length == 2 && int.TryParse(p[1], out int h) && h <= 576;
            }
        }

        /// <summary>Dimensioni "naturali" (per Adatta/Ripristina). VHS 720x576 anamorfico → 4:3.</summary>
        /// <summary>Risoluzione REALE che ffmpeg sta ricevendo dalla sorgente (impostata dall'app), se nota.</summary>
        [JsonIgnore] public static Func<Source, (int w, int h)?> ActualSize;

        public (int w, int h) NaturalSize()
        {
            if (Type == SourceType.Capture)
            {
                int w = 0, h = 0;
                var p = (InputSize ?? "").Split('x');
                if (!(p.Length == 2 && int.TryParse(p[0], out w) && int.TryParse(p[1], out h)))
                {
                    // "automatico": uso la risoluzione vera che arriva dal dispositivo, non un 4:3 inventato
                    var real = ActualSize?.Invoke(this);
                    if (real.HasValue) { w = real.Value.w; h = real.Value.h; }
                    else return (768, 576);
                }
                w -= CropL + CropR; h -= CropT + CropB;
                if (w <= 0 || h <= 0) return (768, 576);
                // SD analogico (PAL/NTSC, 720/704 punti) = 4:3, anche se il ritaglio toglie qualche riga
                if ((w + CropL + CropR == 720 || w + CropL + CropR == 704) && (h + CropT + CropB == 576 || h + CropT + CropB == 480))
                    return (h * 4 / 3, h);
                return (w, h);
            }
            if (Type == SourceType.Image && File.Exists(ImagePath))
            {
                try { using var im = System.Drawing.Image.FromFile(ImagePath); return (im.Width, im.Height); } catch { }
            }
            return (W > 0 ? W : 640, H > 0 ? H : 480);
        }

        public void FitTo(int cw, int ch)
        {
            var (nw, nh) = NaturalSize();
            double s = Math.Min((double)cw / nw, (double)ch / nh);
            W = Math.Max(16, (int)Math.Round(nw * s)); H = Math.Max(16, (int)Math.Round(nh * s));
            X = (cw - W) / 2; Y = (ch - H) / 2;
        }
        public void FillTo(int cw, int ch) { X = 0; Y = 0; W = cw; H = ch; }
        public void Center(int cw, int ch) { X = (cw - W) / 2; Y = (ch - H) / 2; }
    }

    /// <summary>
    /// Standard video analogici: tutto ciò che passa dal grabber (VHS, S-VHS, Hi8, Video8, MiniDV) è PAL o NTSC.
    /// Ogni standard porta con sé anche lo standard del grabber (Tv), così risoluzione, fps e decoder vanno sempre insieme.
    /// Le cassette NTSC lette da un videoregistratore PAL escono a 525 righe / 60 Hz ma col colore a 4,43 MHz
    /// (PAL-60 o NTSC 4.43): col grabber su NTSC_M vengono in bianco e nero, su PAL_B tagliate o che sfarfallano.
    /// </summary>
    public class VideoStandard
    {
        public string Name, Size, InFps, CanvasFps, Deint, Tv; public int CropB;
        public static readonly VideoStandard PAL = new VideoStandard { Name = "PAL", Size = "720x576", InFps = "25", CanvasFps = "50", Deint = "yadif2x", CropB = 8, Tv = "PAL_B" };
        public static readonly VideoStandard NTSC = new VideoStandard { Name = "NTSC", Size = "720x480", InFps = "29.97", CanvasFps = "59.94", Deint = "yadif2x", CropB = 6, Tv = "NTSC_M" };
        /// <summary>Cassetta NTSC su videoregistratore PAL che esce in PAL-60 (il caso più comune).</summary>
        public static readonly VideoStandard NTSC_PAL60 = new VideoStandard { Name = "NTSC su VCR PAL (PAL-60)", Size = "720x480", InFps = "29.97", CanvasFps = "59.94", Deint = "yadif2x", CropB = 6, Tv = "PAL_60" };
        /// <summary>Cassetta NTSC su videoregistratore PAL che esce in NTSC 4.43 (alcuni VCR, soprattutto vecchi).</summary>
        public static readonly VideoStandard NTSC_443 = new VideoStandard { Name = "NTSC su VCR PAL (NTSC 4.43)", Size = "720x480", InFps = "29.97", CanvasFps = "59.94", Deint = "yadif2x", CropB = 6, Tv = "NTSC_433" };

        /// <summary>
        /// Cassetta NTSC su videoregistratore PAL con un grabber che non tiene PAL-60 né NTSC 4.43 (es. USB 2828x: resta su NTSC_M).
        /// Il grabber va in PAL B/G: il colore è giusto, ma impagina a 50 Hz un segnale a 60 Hz. A 720×576 ogni campo da 288 righe
        /// contiene il quadro (righe 0–231), la banda nera (da ~234) e un pezzo del quadro dopo; i due campi di un fotogramma sono
        /// istanti diversi (prima il superiore) ma con allineamento verticale variabile, quindi niente Yadif (era lo sfarfallio):
        /// campi separati e raddoppiati, ritaglio di 112 righe in basso. Misurato sui campioni dell'USB 2828x del laboratorio:
        /// 720×576 = circa 21 immagini al secondo, 720×480 = 16,5, 352×288 = 12,5, 352×240 = 10, 640×480 = 7.
        /// </summary>
        public static readonly VideoStandard NTSC_PALB = new VideoStandard { Name = "NTSC su VCR PAL (PAL B/G ricostruito)", Size = "720x576", InFps = "25", CanvasFps = "59.94", Deint = "fields", CropB = 112, Tv = "PAL_B" };

        /// <summary>Nell'ordine del menu «Standard video» delle Proprietà (dopo c'è «Personalizzato»).</summary>
        public static readonly VideoStandard[] All = { PAL, NTSC, NTSC_PAL60, NTSC_443, NTSC_PALB };

        static bool SameValues(Source s, VideoStandard v) =>
            s.InputSize == v.Size && s.InputFps == v.InFps && s.DeinterlaceMode == v.Deint &&
            s.CropL == 0 && s.CropT == 0 && s.CropR == 0 && s.CropB == v.CropB;

        /// <summary>Lo standard a cui corrispondono i valori della sorgente (grabber compreso), o null se sono stati personalizzati.</summary>
        public static VideoStandard Detect(Source s)
        {
            foreach (var v in All)
                if (SameValues(s, v) && (s.TvStandard ?? "") == v.Tv) return v;
            return null;
        }

        /// <summary>Solo per la migrazione: PAL o NTSC guardando risoluzione/fps, senza lo standard del grabber.</summary>
        public static VideoStandard DetectValues(Source s)
        {
            foreach (var v in new[] { PAL, NTSC }) if (SameValues(s, v)) return v;
            return null;
        }
    }

    public class AppSettings
    {
        // Canvas / output
        public int CanvasW { get; set; } = 1920;
        public int CanvasH { get; set; } = 1080;
        public string Fps { get; set; } = "25";
        public List<Source> Sources { get; set; } = new List<Source>();

        // Video
        public string Encoder { get; set; } = "h264_qsv";       // libx264, h264_nvenc, h264_qsv, h264_amf
        public bool EncoderUserSet { get; set; } = false;      // false = l'app sceglie da sola l'encoder hardware migliore
        public string RateControl { get; set; } = "CBR";       // CBR, VBR, CRF
        public int VideoBitrate { get; set; } = 12000;         // kbps
        public int Crf { get; set; } = 18;
        public string Preset { get; set; } = "veryfast";

        // Audio
        public int AudioBitrate { get; set; } = 192;
        public bool AudioMono { get; set; } = false;

        // Output
        public string OutputFolder { get; set; } = @"Y:\5.0.Ripping-video";
        public string FilePrefix { get; set; } = "VHS";
        public bool SafeRecording { get; set; } = false;       // opzionale: MKV + remux MP4 a fine
        public int MaxMinutes { get; set; } = 0;

        public bool FragmentedMp4 { get; set; } = false;       // false = MP4 normale come OBS (indice unico: i lettori lo aprono subito)
        public int FormatVersion { get; set; } = 0;            // migrazioni una tantum delle impostazioni
        public int SplitMinutes { get; set; } = 0;             // come "Divisione automatica dei file" di OBS (0 = off)
        public bool HighPriority { get; set; } = true;         // come "Priorità del processo" di OBS
        public bool AudioMonitor { get; set; } = false;        // come "Monitoraggio audio" di OBS: senti l'audio dalle casse
        public int MonitorDevice { get; set; } = -1;           // uscita audio per l'ascolto (-1 = predefinita di Windows)
        public int KeyframeSec { get; set; } = 2;              // intervallo keyframe (OBS: 2 s)

        // Fine cassetta
        public bool AutoStopOnBlank { get; set; } = true;      // arresto prudente dopo conferma di assenza di dettagli, movimento e audio
        public int AutoStopSeconds { get; set; } = 120;        // secondi di fila di solo sfondo (blu/nero/neve) prima di fermare
        public bool RecordLocalFirst { get; set; } = false;    // SPENTO = come OBS: si scrive direttamente nella cartella scelta, anche di rete
        public bool TrimBlankTail { get; set; } = false;       // taglia la coda uniforme dal file (spento: allo stop si salva e basta)
        public bool AskNameAtEnd { get; set; } = true;         // a fine registrazione chiede il nome della cassetta e rinomina
        public string Profile { get; set; } = "";              // ultimo profilo applicato (solo per l'etichetta)
        public bool DiagLog { get; set; } = false;             // riga di diagnostica anteprima nel Log ogni 5 s
        public bool IntelGpu { get; set; } = true;            // filtri QSV con encoder Intel e sorgente compatibile
        public string QsvBackend { get; set; } = "";           // "dxva2" = su questo PC D3D11 non va (GPU Intel vecchie): si parte subito con DXVA2
        public List<string> EncodersWorking { get; set; }      // esito della verifica encoder su questo PC (si rifà solo se cambia ffmpeg o la chiedi)
        public string EncodersStamp { get; set; } = "";        // ffmpeg.exe a cui si riferisce la verifica salvata
        public string IntelFieldOrder { get; set; } = "tff"; // i grabber raw spesso non dichiarano l'ordine dei campi

        // Collegamento al CRM (Controllo PC → 🎬 VHSCapture). Le opzioni restano salvate anche quando sono spente.
        public bool CrmAttivo { get; set; } = true;              // interruttore generale: spento = VHSCapture come prima (token e opzioni restano salvati)
        public string CrmUrl { get; set; } = "https://crm.tastieredigitali.it";
        public string CrmToken { get; set; } = "";
        public bool CrmChiediCliente { get; set; } = true;       // al Registra, solo se non c'è un cliente in corso (prima volta / videocassette del cliente finite)
        public bool CrmCartellaCliente { get; set; } = true;     // salva in «Nome Cognome» dentro la cartella del PC
        public bool CrmChiediFine { get; set; } = true;          // a fine cassetta: ✅ Tieni / 🗑 Scarta (spento = conta sempre)
        public bool CrmRipartenzaAttiva { get; set; } = true;    // partenza sbagliata: fermata entro N secondi = non si conta, file cancellato
        public int CrmRipartenzaSec { get; set; } = 60;
        public string CrmConfigAt { get; set; } = "";
        public int CrmUltimoCliente { get; set; } = 0;           // ultimo cliente (scheda) in corso: alla riapertura si propone «Continua con…»            // ultima modifica della configurazione (sincronizzata col CRM: vince la più recente)

        // Controllo live (zmq)
        public bool LiveControl { get; set; } = true;

        // UI
        public bool DarkTheme { get; set; } = false;
        public bool ShowLog { get; set; } = false;
        public int WindowW { get; set; } = 1280;
        public int WindowH { get; set; } = 800;
        public bool WindowMax { get; set; } = false;
        public int RightPanelW { get; set; } = 340;
        public bool RightPanelHidden { get; set; } = false;
        public int MixerH { get; set; } = 260;
        public int LogH { get; set; } = 140;

        // --- campi della v1 (solo per migrazione) ---
        public string VideoDevice { get; set; } = "";
        public string AudioDevice { get; set; } = "";
        public string InputSize { get; set; } = "";
        public string InputFps { get; set; } = "";

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VHSCapture");
        static readonly string File = Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (System.IO.File.Exists(File))
                    s = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File));
            }
            catch { }
            s ??= new AppSettings();
            if (s.Sources == null) s.Sources = new List<Source>();

            // fine cassetta: sotto il minuto si rischia di fermare su un nero del filmato; il nuovo rilevatore conta tutto il tempo
            if (s.AutoStopSeconds < 60) s.AutoStopSeconds = 120;
            // 1.2.3: MP4 frammentato predefinito (niente riscritture allo stop, taglio coda istantaneo). Chi usa MKV resta su MKV.
            if (s.FormatVersion < 2) { if (!s.SafeRecording) s.FragmentedMp4 = true; s.FormatVersion = 2; }
            // 1.2.4: "registra sul PC e sposta" non è più attivo di default: si scrive dritto nella cartella scelta, come OBS
            if (s.FormatVersion < 3) { s.RecordLocalFirst = false; s.FormatVersion = 3; }
            // 1.2.5: allo stop si salva e basta: niente taglio della coda di default
            if (s.FormatVersion < 4) { s.TrimBlankTail = false; s.FormatVersion = 4; }
            // 1.2.6: MP4 normale come OBS (il frammentato si apre lentamente nei lettori). Chi usa MKV resta su MKV.
            if (s.FormatVersion < 5) { s.FragmentedMp4 = false; s.FormatVersion = 5; }
            if (!string.IsNullOrEmpty(s.Profile) && s.Profile != "PAL" && s.Profile != "NTSC")
                s.Profile = s.Profile.Contains("NTSC") ? "NTSC" : (s.Profile.Contains("PAL") ? "PAL" : "");
            foreach (var src in s.Sources)
            {
                if (src.Deinterlace.HasValue) src.DeinterlaceMode = src.Deinterlace.Value ? "yadif" : "off";
                if (string.IsNullOrEmpty(src.DeinterlaceMode)) src.DeinterlaceMode = "off";
                src.Deinterlace = null;
                if (string.IsNullOrEmpty(src.VideoFormat)) src.VideoFormat = "auto";
                if (string.IsNullOrEmpty(src.ScaleFilter)) src.ScaleFilter = "bicubic";
            }

            // migrazione dalla v1 (singolo dispositivo)
            if (s.Sources.Count == 0 && !string.IsNullOrEmpty(s.VideoDevice))
            {
                var src = new Source
                {
                    Name = "Grabber USB", Type = SourceType.Capture,
                    VideoDevice = s.VideoDevice, AudioDevice = s.AudioDevice ?? "",
                    InputSize = string.IsNullOrEmpty(s.InputSize) ? "720x576" : s.InputSize,
                    InputFps = string.IsNullOrEmpty(s.InputFps) ? "25" : s.InputFps,
                    DeinterlaceMode = "yadif",
                };
                src.FitTo(s.CanvasW, s.CanvasH);
                s.Sources.Add(src);
                s.VideoDevice = ""; s.AudioDevice = ""; s.InputSize = ""; s.InputFps = "";
                s.Save();
            }
            // 1.3.1: lo standard PAL/NTSC imposta anche il grabber (prima andava cambiato a mano nel driver e i due si mescolavano)
            if (s.FormatVersion < 6)
            {
                foreach (var src in s.Sources)
                {
                    if (src.Type != SourceType.Capture || !string.IsNullOrEmpty(src.TvStandard)) continue;
                    var v = VideoStandard.DetectValues(src);
                    if (v != null) src.TvStandard = v.Tv;
                }
                s.FormatVersion = 6;
            }
            foreach (var src in s.Sources) src.TvStandard ??= "";
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public string ResolvedOutputFolder()
        {
            if (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder)) return OutputFolder;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
    }
}
