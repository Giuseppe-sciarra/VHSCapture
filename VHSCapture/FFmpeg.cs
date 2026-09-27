using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VHSCapture
{
    public static class FFmpeg
    {
        public static string ExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        public static bool Exists => File.Exists(ExePath);

        static ProcessStartInfo Psi(string args) => new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        /// <summary>Elenca i dispositivi DirectShow (video, audio).</summary>
        public static (List<string> video, List<string> audio) ListDevices()
        {
            var video = new List<string>();
            var audio = new List<string>();
            if (!Exists) return (video, audio);

            string err = RunCapture("-hide_banner -list_devices true -f dshow -i dummy");

            // Formato nuovo: [dshow @ ...] "Nome" (video)   /   (audio)   /   (audio, video)
            var rx = new Regex("\"([^\"]+)\"\\s+\\(([a-z, ]+)\\)");
            bool matched = false;
            foreach (var line in err.Split('\n'))
            {
                if (line.Contains("Alternative name")) continue;
                var m = rx.Match(line);
                if (!m.Success) continue;
                matched = true;
                string name = m.Groups[1].Value;
                string kind = m.Groups[2].Value;
                if (kind.Contains("video") && !video.Contains(name)) video.Add(name);
                if (kind.Contains("audio") && !audio.Contains(name)) audio.Add(name);
            }

            if (!matched)
            {
                // Formato vecchio: sezioni "DirectShow video devices" / "DirectShow audio devices"
                bool inVideo = false, inAudio = false;
                var rx2 = new Regex("\"([^\"]+)\"");
                foreach (var line in err.Split('\n'))
                {
                    if (line.Contains("DirectShow video devices")) { inVideo = true; inAudio = false; continue; }
                    if (line.Contains("DirectShow audio devices")) { inVideo = false; inAudio = true; continue; }
                    if (line.Contains("Alternative name")) continue;
                    var m = rx2.Match(line);
                    if (!m.Success) continue;
                    if (inVideo) video.Add(m.Groups[1].Value);
                    else if (inAudio) audio.Add(m.Groups[1].Value);
                }
            }
            return (video, audio);
        }

        /// <summary>Restituisce le risoluzioni supportate dal dispositivo video (WxH).</summary>
        public static List<string> ListVideoSizes(string device)
        {
            var sizes = new List<string>();
            if (!Exists || string.IsNullOrEmpty(device)) return sizes;
            string err = RunCapture($"-hide_banner -list_options true -f dshow -i video=\"{device}\"");
            var rx = new Regex(@"s=(\d+x\d+)");
            foreach (Match m in rx.Matches(err))
                if (!sizes.Contains(m.Groups[1].Value)) sizes.Add(m.Groups[1].Value);
            return sizes;
        }

        /// <summary>Elenco encoder H.264 realmente disponibili in questa build (libx264, nvenc, qsv, amf).</summary>
        public static List<string> ListH264Encoders()
        {
            var res = new List<string>();
            if (!Exists) return res;
            string outp = RunCapture("-hide_banner -encoders", stdout: true);
            foreach (var e in new[] { "libx264", "h264_nvenc", "h264_qsv", "h264_amf" })
                if (Regex.IsMatch(outp, @"\s" + e + @"\s")) res.Add(e);
            if (res.Count == 0) res.Add("libx264");
            return res;
        }

        static string RunCapture(string args, bool stdout = false)
        {
            try
            {
                using var p = Process.Start(Psi(args));
                string s = stdout ? p.StandardOutput.ReadToEnd() : p.StandardError.ReadToEnd();
                if (stdout) p.StandardError.ReadToEnd(); else p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                return s;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Remux MKV -> MP4 senza ricodifica. Ritorna true se ok.</summary>
        public static bool RemuxToMp4(string mkv, string mp4, Action<string> log)
        {
            try
            {
                string args = $"-hide_banner -loglevel error -y -i \"{mkv}\" -c copy -movflags +faststart \"{mp4}\"";
                log?.Invoke("ffmpeg " + args);
                using var p = Process.Start(Psi(args));
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(err)) log?.Invoke(err.Trim());
                return p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0;
            }
            catch (Exception ex) { log?.Invoke("Remux error: " + ex.Message); return false; }
        }
    }

    /// <summary>
    /// Un processo ffmpeg: input dshow, anteprima raw su stdout, livello audio su stderr,
    /// opzionalmente registrazione su file.
    /// </summary>
    public class CaptureEngine : IDisposable
    {
        public const int PW = 640, PH = 480;

        public event Action<Bitmap> FrameReady;
        public event Action<double> AudioLevel;   // dB (RMS), -inf..0
        public event Action<string> Log;
        public event Action<int> Exited;          // exit code

        public bool IsRunning => proc != null && !proc.HasExited;
        public bool IsRecording { get; private set; }
        public string OutputFile { get; private set; }
        public string LastCommand { get; private set; }

        Process proc;
        Thread readThread;
        volatile bool stopping;

        public void Start(AppSettings s, string outputFile)
        {
            Stop();
            stopping = false;
            IsRecording = outputFile != null;
            OutputFile = outputFile;

            string args = BuildArgs(s, outputFile);
            LastCommand = "ffmpeg " + args;
            Log?.Invoke(LastCommand);

            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg.ExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += OnErr;
            proc.Exited += (o, e) => { try { Exited?.Invoke(((Process)o).ExitCode); } catch { } };
            proc.Start();
            proc.BeginErrorReadLine();

            readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-preview" };
            readThread.Start();
        }

        public void Stop()
        {
            if (proc == null) return;
            stopping = true;
            var p = proc;
            proc = null;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                    if (!p.WaitForExit(IsRecording ? 15000 : 3000))
                    {
                        Log?.Invoke("ffmpeg non risponde, kill forzato");
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch { }
            try { readThread?.Join(2000); } catch { }
            try { p.Dispose(); } catch { }
            IsRecording = false;
        }

        public void Dispose() => Stop();

        void OnErr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
            if (line.StartsWith("lavfi.astats.Overall.RMS_level="))
            {
                string v = line.Substring("lavfi.astats.Overall.RMS_level=".Length).Trim();
                if (v == "-inf") AudioLevel?.Invoke(-90);
                else if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double db))
                    AudioLevel?.Invoke(db);
                return;
            }
            if (line.StartsWith("frame:")) return; // header di ametadata
            if (line.Trim().Length == 0) return;
            Log?.Invoke(line);
        }

        void ReadLoop()
        {
            var p = proc;
            if (p == null) return;
            Stream st;
            try { st = p.StandardOutput.BaseStream; } catch { return; }
            int size = PW * PH * 3;
            byte[] buf = new byte[size];
            while (!stopping)
            {
                int got = 0;
                try
                {
                    while (got < size)
                    {
                        int n = st.Read(buf, got, size - got);
                        if (n <= 0) return;
                        got += n;
                    }
                }
                catch { return; }

                var bmp = new Bitmap(PW, PH, PixelFormat.Format24bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, PW, PH), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                if (bd.Stride == PW * 3) Marshal.Copy(buf, 0, bd.Scan0, size);
                else for (int y = 0; y < PH; y++) Marshal.Copy(buf, y * PW * 3, bd.Scan0 + y * bd.Stride, PW * 3);
                bmp.UnlockBits(bd);
                try { FrameReady?.Invoke(bmp); } catch { bmp.Dispose(); }
            }
        }

        // ---------- costruzione argomenti ----------

        public static string BuildArgs(AppSettings s, string outputFile)
        {
            var sb = new StringBuilder();
            bool audio = !string.IsNullOrWhiteSpace(s.AudioDevice);

            sb.Append("-hide_banner -loglevel warning -nostats ");
            sb.Append($"-f dshow -rtbufsize {Math.Max(64, s.RtBufMB)}M ");
            if (!string.IsNullOrWhiteSpace(s.InputSize) && s.InputSize != "auto") sb.Append($"-video_size {s.InputSize} ");
            if (!string.IsNullOrWhiteSpace(s.InputFps) && s.InputFps != "auto") sb.Append($"-framerate {s.InputFps} ");
            sb.Append("-i ");
            sb.Append(audio ? $"video=\"{s.VideoDevice}\":audio=\"{s.AudioDevice}\" " : $"video=\"{s.VideoDevice}\" ");

            string deint = s.Deinterlace ? "yadif=mode=send_frame" : "";

            // ---- output registrazione ----
            if (outputFile != null)
            {
                sb.Append("-map 0:v ");
                if (audio) sb.Append("-map 0:a ");

                var vf = new List<string>();
                if (deint != "") vf.Add(deint);
                if (!string.IsNullOrWhiteSpace(s.OutputSize) && s.OutputSize != "source")
                {
                    var wh = s.OutputSize.Split('x');
                    vf.Add($"scale={wh[0]}:{wh[1]}:flags=lanczos");
                }
                if (vf.Count > 0) sb.Append($"-vf \"{string.Join(",", vf)}\" ");

                sb.Append(EncoderArgs(s));
                sb.Append("-pix_fmt yuv420p ");
                if (audio)
                {
                    sb.Append($"-c:a aac -b:a {s.AudioBitrate}k ");
                    if (s.AudioMono) sb.Append("-ac 1 ");
                    sb.Append("-af aresample=async=1 ");
                }
                // niente faststart in diretta: riscriverebbe tutto il file a fine registrazione
                sb.Append($"-y \"{outputFile}\" ");
            }

            // ---- anteprima raw su stdout ----
            string pvf = (deint != "" ? deint + "," : "") + $"scale={PW}:{PH}";
            sb.Append($"-map 0:v -vf \"{pvf}\" -f rawvideo -pix_fmt bgr24 pipe:1 ");

            // ---- livello audio su stderr ----
            if (audio)
                sb.Append("-map 0:a -af \"astats=metadata=1:reset=4,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=pipe\\:2\" -f null NUL");

            return sb.ToString();
        }

        static string EncoderArgs(AppSettings s)
        {
            int b = Math.Max(500, s.VideoBitrate);
            int crf = Math.Clamp(s.Crf, 0, 51);
            string rc = (s.RateControl ?? "CBR").ToUpperInvariant();
            switch (s.Encoder)
            {
                case "h264_nvenc":
                    if (rc == "CRF") return $"-c:v h264_nvenc -preset p5 -rc vbr -cq {crf} -b:v 0 ";
                    if (rc == "VBR") return $"-c:v h264_nvenc -preset p5 -rc vbr -b:v {b}k -maxrate {b * 3 / 2}k -bufsize {b * 2}k ";
                    return $"-c:v h264_nvenc -preset p5 -rc cbr -b:v {b}k -maxrate {b}k -bufsize {b * 2}k ";
                case "h264_qsv":
                    if (rc == "CRF") return $"-c:v h264_qsv -preset medium -global_quality {crf} -look_ahead 0 ";
                    if (rc == "VBR") return $"-c:v h264_qsv -preset medium -b:v {b}k -maxrate {b * 3 / 2}k ";
                    return $"-c:v h264_qsv -preset medium -b:v {b}k -maxrate {b}k ";
                case "h264_amf":
                    if (rc == "CRF") return $"-c:v h264_amf -quality balanced -rc cqp -qp_i {crf} -qp_p {crf} ";
                    if (rc == "VBR") return $"-c:v h264_amf -quality balanced -rc vbr_peak -b:v {b}k -maxrate {b * 3 / 2}k ";
                    return $"-c:v h264_amf -quality balanced -rc cbr -b:v {b}k ";
                default:
                    string pre = string.IsNullOrWhiteSpace(s.Preset) ? "veryfast" : s.Preset;
                    if (rc == "CRF") return $"-c:v libx264 -preset {pre} -crf {crf} ";
                    if (rc == "VBR") return $"-c:v libx264 -preset {pre} -b:v {b}k -maxrate {b * 3 / 2}k -bufsize {b * 2}k ";
                    return $"-c:v libx264 -preset {pre} -b:v {b}k -minrate {b}k -maxrate {b}k -bufsize {b * 2}k -x264-params nal-hrd=cbr ";
            }
        }
    }
}
