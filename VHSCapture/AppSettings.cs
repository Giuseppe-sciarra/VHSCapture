using System;
using System.IO;
using System.Text.Json;

namespace VHSCapture
{
    public class AppSettings
    {
        // Dispositivi
        public string VideoDevice { get; set; } = "";
        public string AudioDevice { get; set; } = "";          // "" = nessun audio
        public string InputSize { get; set; } = "720x576";     // "auto" = lascia decidere al driver
        public string InputFps { get; set; } = "25";           // "auto"
        public int RtBufMB { get; set; } = 512;

        // Video
        public bool Deinterlace { get; set; } = true;
        public string Encoder { get; set; } = "libx264";       // libx264, h264_nvenc, h264_qsv, h264_amf
        public string RateControl { get; set; } = "CBR";       // CBR, VBR, CRF
        public int VideoBitrate { get; set; } = 8000;          // kbps
        public int Crf { get; set; } = 18;
        public string Preset { get; set; } = "veryfast";       // solo libx264
        public string OutputSize { get; set; } = "source";     // "source" o WxH

        // Audio
        public int AudioBitrate { get; set; } = 192;           // kbps
        public bool AudioMono { get; set; } = false;

        // Output
        public string OutputFolder { get; set; } = @"Y:\5.0.Ripping-video";
        public string FilePrefix { get; set; } = "VHS";
        public bool SafeRecording { get; set; } = false;       // opzionale: MKV + remux MP4 a fine (di default MP4 diretto)
        public int MaxMinutes { get; set; } = 0;               // 0 = illimitato

        // UI
        public bool DarkTheme { get; set; } = false;
        public bool ShowLog { get; set; } = false;
        public int WindowW { get; set; } = 1000;
        public int WindowH { get; set; } = 720;

        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VHSCapture");
        static readonly string File = Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (System.IO.File.Exists(File))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File));
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
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
