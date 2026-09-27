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
        /// <summary>off, yadif, yadif2x, bwdif, bwdif2x (come il deinterlacciamento di OBS). 2x = 50p da VHS PAL.</summary>
        public string DeinterlaceMode { get; set; } = "off";
        public bool? Deinterlace { get; set; } = null;          // solo migrazione dalle versioni precedenti
        public int RtBufMB { get; set; } = 512;
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

        public Source Clone() => (Source)MemberwiseClone();

        /// <summary>Le proprietà che richiedono un riavvio del grafo ffmpeg.</summary>
        public bool StructurallyEquals(Source o) =>
            Type == o.Type && Visible == o.Visible && VideoDevice == o.VideoDevice && AudioDevice == o.AudioDevice &&
            InputSize == o.InputSize && InputFps == o.InputFps && DeinterlaceMode == o.DeinterlaceMode && RtBufMB == o.RtBufMB &&
            VideoFormat == o.VideoFormat && ScaleFilter == o.ScaleFilter && AudioOffsetMs == o.AudioOffsetMs &&
            ImagePath == o.ImagePath && Color == o.Color;

        public void CopyStructuralFrom(Source o)
        {
            Type = o.Type; Visible = o.Visible; VideoDevice = o.VideoDevice; AudioDevice = o.AudioDevice;
            InputSize = o.InputSize; InputFps = o.InputFps; DeinterlaceMode = o.DeinterlaceMode; RtBufMB = o.RtBufMB;
            VideoFormat = o.VideoFormat; ScaleFilter = o.ScaleFilter; AudioOffsetMs = o.AudioOffsetMs;
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
        public (int w, int h) NaturalSize()
        {
            if (Type == SourceType.Capture)
            {
                var p = (InputSize ?? "").Split('x');
                if (p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h))
                {
                    if ((w == 720 || w == 704) && (h == 576 || h == 480)) return (h * 4 / 3, h); // PAL/NTSC SD = 4:3
                    return (w, h);
                }
                return (768, 576);
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

    public class AppSettings
    {
        // Canvas / output
        public int CanvasW { get; set; } = 1920;
        public int CanvasH { get; set; } = 1080;
        public string Fps { get; set; } = "25";
        public List<Source> Sources { get; set; } = new List<Source>();

        // Video
        public string Encoder { get; set; } = "libx264";       // libx264, h264_nvenc, h264_qsv, h264_amf
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

        public bool FragmentedMp4 { get; set; } = false;       // come "MP4 ibrido/frammentato" di OBS: leggibile anche dopo un crash
        public int SplitMinutes { get; set; } = 0;             // come "Divisione automatica dei file" di OBS (0 = off)
        public bool HighPriority { get; set; } = true;         // come "Priorità del processo" di OBS
        public bool AudioMonitor { get; set; } = false;        // come "Monitoraggio audio" di OBS: senti l'audio dalle casse
        public int MonitorDevice { get; set; } = -1;           // uscita audio per l'ascolto (-1 = predefinita di Windows)
        public int KeyframeSec { get; set; } = 2;              // intervallo keyframe (OBS: 2 s)

        // Fine cassetta
        public bool AutoStopOnBlank { get; set; } = true;      // ferma quando il grabber manda schermo blu/nero uniforme
        public int AutoStopSeconds { get; set; } = 30;
        public bool TrimBlankTail { get; set; } = true;        // taglia la coda blu/nera dal file
        public bool AskNameAtEnd { get; set; } = true;         // a fine registrazione chiede il nome della cassetta e rinomina
        public string Profile { get; set; } = "";              // ultimo profilo applicato (solo per l'etichetta)
        public bool DiagLog { get; set; } = false;             // riga di diagnostica anteprima nel Log ogni 5 s

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
