using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

namespace VHSCapture
{
    public static class FFmpeg
    {
        public static string ExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        public static bool Exists => File.Exists(ExePath);
        static bool? hasZmq;
        public static bool HasZmq
        {
            get
            {
                if (hasZmq == null)
                {
                    string f = RunCapture("-hide_banner -filters", stdout: true);
                    hasZmq = Regex.IsMatch(f, @"\szmq\s");
                }
                return hasZmq.Value;
            }
        }

        static ProcessStartInfo Psi(string args) => new ProcessStartInfo
        {
            FileName = ExePath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        public static (List<string> video, List<string> audio) ListDevices()
        {
            var video = new List<string>(); var audio = new List<string>();
            if (!Exists) return (video, audio);
            string err = RunCapture("-hide_banner -list_devices true -f dshow -i dummy");
            var rx = new Regex("\"([^\"]+)\"\\s+\\(([a-z, ]+)\\)");
            bool matched = false;
            foreach (var line in err.Split('\n'))
            {
                if (line.Contains("Alternative name")) continue;
                var m = rx.Match(line); if (!m.Success) continue;
                matched = true;
                string name = m.Groups[1].Value, kind = m.Groups[2].Value;
                if (kind.Contains("video") && !video.Contains(name)) video.Add(name);
                if (kind.Contains("audio") && !audio.Contains(name)) audio.Add(name);
            }
            if (!matched)
            {
                bool inVideo = false, inAudio = false; var rx2 = new Regex("\"([^\"]+)\"");
                foreach (var line in err.Split('\n'))
                {
                    if (line.Contains("DirectShow video devices")) { inVideo = true; inAudio = false; continue; }
                    if (line.Contains("DirectShow audio devices")) { inVideo = false; inAudio = true; continue; }
                    if (line.Contains("Alternative name")) continue;
                    var m = rx2.Match(line); if (!m.Success) continue;
                    if (inVideo) video.Add(m.Groups[1].Value); else if (inAudio) audio.Add(m.Groups[1].Value);
                }
            }
            return (video, audio);
        }

        public static List<string> ListVideoSizes(string device)
        {
            var sizes = new List<string>();
            if (!Exists || string.IsNullOrEmpty(device)) return sizes;
            string err = RunCapture($"-hide_banner -list_options true -f dshow -i video=\"{device}\"");
            foreach (Match m in new Regex(@"s=(\d+x\d+)").Matches(err))
                if (!sizes.Contains(m.Groups[1].Value)) sizes.Add(m.Groups[1].Value);
            return sizes;
        }

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

        public static bool RemuxToMp4(string mkv, string mp4, Action<string> log)
        {
            try
            {
                string args = $"-hide_banner -loglevel error -y -i \"{mkv}\" -c copy -movflags +faststart \"{mp4}\"";
                log?.Invoke("ffmpeg " + args);
                using var p = Process.Start(Psi(args));
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(err)) log?.Invoke(err.Trim());
                return p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0;
            }
            catch (Exception ex) { log?.Invoke("Remux error: " + ex.Message); return false; }
        }
    }

    /// <summary>Invio comandi live al filtro zmq di ffmpeg (coalescing: tiene solo l'ultimo comando per chiave).</summary>
    public class ZmqControl : IDisposable
    {
        readonly int port;
        RequestSocket sock;
        readonly ConcurrentDictionary<string, string> pending = new ConcurrentDictionary<string, string>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread worker; volatile bool stop;
        public bool Enabled { get; private set; }
        public event Action<string> Log;

        public ZmqControl(int port) { this.port = port; }

        public void Start()
        {
            stop = false; Enabled = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "zmq" };
            worker.Start();
        }

        public void Queue(string key, string cmd) { if (!Enabled) return; pending[key] = cmd; wake.Set(); }

        void Loop()
        {
            int failures = 0;
            while (!stop)
            {
                wake.WaitOne(500);
                while (!stop && !pending.IsEmpty)
                {
                    var kv = pending.First();
                    pending.TryRemove(kv.Key, out _);
                    if (SendOne(kv.Value)) failures = 0;
                    else if (++failures > 20) { Log?.Invoke("zmq: troppi errori, controllo live disattivato"); Enabled = false; pending.Clear(); return; }
                }
            }
        }

        bool SendOne(string cmd)
        {
            try
            {
                if (sock == null)
                {
                    sock = new RequestSocket();
                    sock.Options.Linger = TimeSpan.Zero;
                    sock.Connect("tcp://127.0.0.1:" + port);
                }
                if (!sock.TrySendFrame(TimeSpan.FromMilliseconds(300), cmd)) { Reset(); return false; }
                if (!sock.TryReceiveFrameString(TimeSpan.FromMilliseconds(1500), out string rep)) { Reset(); return false; }
                if (!rep.StartsWith("0")) { Log?.Invoke($"zmq [{cmd}] → {rep}"); }
                return true;
            }
            catch (Exception ex) { Log?.Invoke("zmq: " + ex.Message); Reset(); return false; }
        }

        void Reset() { try { sock?.Dispose(); } catch { } sock = null; }

        public void Dispose()
        {
            stop = true; wake.Set();
            try { worker?.Join(500); } catch { }
            Reset();
        }
    }

    /// <summary>Motore: un processo ffmpeg che compone il canvas, manda l'anteprima raw su stdout, il livello audio su stderr, e opzionalmente registra.</summary>
    public class CaptureEngine : IDisposable
    {
        public int PW { get; private set; } = 960;
        public int PH { get; private set; } = 540;

        public event Action<Bitmap> FrameReady;
        public event Action<double> AudioLevel;
        public event Action<string> Log;
        public event Action<int> Exited;

        public bool IsRunning => proc != null && !proc.HasExited;
        public bool IsRecording { get; private set; }
        public bool LiveControl => zmq != null && zmq.Enabled;
        public string OutputFile { get; private set; }
        public string LastCommand { get; private set; }

        Process proc; Thread readThread; volatile bool stopping;
        ZmqControl zmq;
        AppSettings cfg;

        public void Start(AppSettings s, string outputFile)
        {
            Stop();
            cfg = s;
            stopping = false;
            IsRecording = outputFile != null;
            OutputFile = outputFile;

            PW = 960; PH = Math.Max(2, (int)Math.Round(960.0 * s.CanvasH / s.CanvasW / 2) * 2);

            bool live = s.LiveControl && FFmpeg.HasZmq;
            string args = BuildArgs(s, outputFile, PW, PH, live);
            LastCommand = "ffmpeg " + args;
            Log?.Invoke(LastCommand);

            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg.ExePath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += OnErr;
            proc.Exited += (o, e) => { try { Exited?.Invoke(((Process)o).ExitCode); } catch { } };
            proc.Start();
            proc.BeginErrorReadLine();

            readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-preview" };
            readThread.Start();

            if (live)
            {
                zmq = new ZmqControl(5555); // porta di default del filtro zmq di ffmpeg
                zmq.Log += l => Log?.Invoke(l);
                zmq.Start();
            }
        }

        public void Stop()
        {
            try { zmq?.Dispose(); } catch { }
            zmq = null;
            if (proc == null) return;
            stopping = true;
            var p = proc; proc = null;
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

        // ---------- comandi live ----------

        public void ApplyTransform(Source src)
        {
            if (!LiveControl) return;
            zmq.Queue(src.Id + ":w", $"scale@s{src.Id} w {src.W}");
            zmq.Queue(src.Id + ":h", $"scale@s{src.Id} h {src.H}");
            zmq.Queue(src.Id + ":x", $"overlay@s{src.Id} x {src.X}");
            zmq.Queue(src.Id + ":y", $"overlay@s{src.Id} y {src.Y}");
        }

        public void ApplyColor(Source src)
        {
            if (!LiveControl) return;
            var ci = CultureInfo.InvariantCulture;
            if (src.Type == SourceType.Capture)
            {
                zmq.Queue(src.Id + ":b", $"eq@s{src.Id} brightness {src.Brightness.ToString("0.###", ci)}");
                zmq.Queue(src.Id + ":c", $"eq@s{src.Id} contrast {src.Contrast.ToString("0.###", ci)}");
                zmq.Queue(src.Id + ":s", $"eq@s{src.Id} saturation {src.Saturation.ToString("0.###", ci)}");
                zmq.Queue(src.Id + ":g", $"eq@s{src.Id} gamma {src.Gamma.ToString("0.###", ci)}");
                zmq.Queue(src.Id + ":hue", $"hue@s{src.Id} h {src.Hue.ToString("0.#", ci)}");
            }
            else
            {
                zmq.Queue(src.Id + ":hue", $"hue@s{src.Id} h {src.Hue.ToString("0.#", ci)}");
                zmq.Queue(src.Id + ":s", $"hue@s{src.Id} s {src.Saturation.ToString("0.###", ci)}");
                zmq.Queue(src.Id + ":b", $"hue@s{src.Id} b {(src.Brightness * 10).ToString("0.###", ci)}");
            }
        }

        public void ApplyVolume(Source src)
        {
            if (!LiveControl || !src.HasAudio) return;
            zmq.Queue(src.Id + ":vol", $"volume@a{src.Id} volume {src.VolumeLinear.ToString("0.#####", CultureInfo.InvariantCulture)}");
        }

        // ---------- stderr / stdout ----------

        void OnErr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
            const string key = "lavfi.astats.Overall.RMS_level=";
            if (line.StartsWith(key))
            {
                string v = line.Substring(key.Length).Trim();
                if (v == "-inf") AudioLevel?.Invoke(-90);
                else if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)) AudioLevel?.Invoke(db);
                return;
            }
            if (line.StartsWith("frame:")) return;
            if (line.Trim().Length == 0) return;
            Log?.Invoke(line);
        }

        void ReadLoop()
        {
            var p = proc; if (p == null) return;
            Stream st; try { st = p.StandardOutput.BaseStream; } catch { return; }
            int w = PW, h = PH, size = w * h * 3;
            byte[] buf = new byte[size];
            while (!stopping)
            {
                int got = 0;
                try { while (got < size) { int n = st.Read(buf, got, size - got); if (n <= 0) return; got += n; } }
                catch { return; }
                var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                if (bd.Stride == w * 3) Marshal.Copy(buf, 0, bd.Scan0, size);
                else for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 3, bd.Scan0 + y * bd.Stride, w * 3);
                bmp.UnlockBits(bd);
                try { FrameReady?.Invoke(bmp); } catch { bmp.Dispose(); }
            }
        }

        // ---------- costruzione grafo ----------

        static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

        public static string BuildArgs(AppSettings s, string outputFile, int pw, int ph, bool zmq)
        {
            var sb = new StringBuilder();
            var graph = new StringBuilder();
            string fps = string.IsNullOrWhiteSpace(s.Fps) ? "25" : s.Fps;
            var visible = s.Sources.Where(x => x.Visible).ToList();

            sb.Append("-hide_banner -loglevel warning -nostats ");
            // input 0: tela nera, -re per andare a tempo reale
            sb.Append($"-re -f lavfi -i color=c=black:s={s.CanvasW}x{s.CanvasH}:r={fps} ");

            var idx = new Dictionary<Source, int>();
            int n = 1;
            foreach (var src in visible)
            {
                switch (src.Type)
                {
                    case SourceType.Capture:
                        if (string.IsNullOrWhiteSpace(src.VideoDevice)) continue;
                        sb.Append($"-f dshow -rtbufsize {Math.Max(64, src.RtBufMB)}M -thread_queue_size 1024 ");
                        if (!string.IsNullOrWhiteSpace(src.InputSize) && src.InputSize != "auto") sb.Append($"-video_size {src.InputSize} ");
                        if (!string.IsNullOrWhiteSpace(src.InputFps) && src.InputFps != "auto") sb.Append($"-framerate {src.InputFps} ");
                        sb.Append("-i ");
                        sb.Append(src.HasAudio ? $"video=\"{src.VideoDevice}\":audio=\"{src.AudioDevice}\" " : $"video=\"{src.VideoDevice}\" ");
                        break;
                    case SourceType.Image:
                        if (!File.Exists(src.ImagePath)) continue;
                        sb.Append($"-loop 1 -framerate {fps} -i \"{src.ImagePath}\" ");
                        break;
                    case SourceType.Color:
                        sb.Append($"-f lavfi -i color=c={src.Color.Replace("#", "0x")}:s={Math.Max(2, src.W)}x{Math.Max(2, src.H)}:r={fps} ");
                        break;
                }
                idx[src] = n++;
            }

            // base
            string cur = "[0:v]";
            if (zmq) { graph.Append("[0:v]zmq[base];"); cur = "[base]"; }

            // catene per sorgente
            int k = 0;
            foreach (var kv in idx)
            {
                var src = kv.Key; int i = kv.Value;
                var chain = new List<string>();
                if (src.Type == SourceType.Capture)
                {
                    if (src.Deinterlace) chain.Add("yadif=mode=send_frame");
                    chain.Add($"eq@s{src.Id}=brightness={F(src.Brightness)}:contrast={F(src.Contrast)}:saturation={F(src.Saturation)}:gamma={F(src.Gamma)}");
                    chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}");
                }
                else
                {
                    chain.Add($"format=yuva420p");
                    chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}:s={F(src.Saturation)}:b={F(src.Brightness * 10)}");
                }
                chain.Add($"scale@s{src.Id}=w={Math.Max(2, src.W)}:h={Math.Max(2, src.H)}:flags=bicubic:eval=frame");
                graph.Append($"[{i}:v]{string.Join(",", chain)}[v{k}];");
                graph.Append($"{cur}[v{k}]overlay@s{src.Id}=x={src.X}:y={src.Y}:eof_action=pass[t{k}];");
                cur = $"[t{k}]";
                k++;
            }

            // uscita video
            bool rec = outputFile != null;
            if (rec) graph.Append($"{cur}fps={fps},format=yuv420p,split=2[rec][pv];[pv]scale={pw}:{ph}[pvs];");
            else graph.Append($"{cur}fps={fps},scale={pw}:{ph}[pvs];");

            // audio
            var audioSrcs = idx.Keys.Where(x => x.HasAudio).ToList();
            bool hasAudio = audioSrcs.Count > 0;
            if (hasAudio)
            {
                var labels = new List<string>();
                int a = 0;
                foreach (var src in audioSrcs)
                {
                    graph.Append($"[{idx[src]}:a]aresample=async=1,volume@a{src.Id}=volume={F(src.VolumeLinear, "0.#####")}[a{a}];");
                    labels.Add($"[a{a}]"); a++;
                }
                string amix;
                if (labels.Count == 1) amix = labels[0];
                else { graph.Append($"{string.Join("", labels)}amix=inputs={labels.Count}:duration=longest:normalize=0[amix];"); amix = "[amix]"; }
                // doppio escape: il parser del grafo toglie un backslash, il parser delle opzioni l'altro → file=pipe:2
                string meter = "astats=metadata=1:reset=4,ametadata=mode=print:key=lavfi.astats.Overall.RMS_level:file=pipe\\\\:2";
                if (rec) graph.Append($"{amix}asplit=2[arec][apv];[apv]{meter}[apvs];");
                else graph.Append($"{amix}{meter}[apvs];");
            }

            string g = graph.ToString().TrimEnd(';');
            sb.Append($"-filter_complex \"{g}\" ");

            if (rec)
            {
                sb.Append("-map \"[rec]\" ");
                if (hasAudio) sb.Append("-map \"[arec]\" ");
                sb.Append(EncoderArgs(s));
                if (hasAudio)
                {
                    sb.Append($"-c:a aac -b:a {s.AudioBitrate}k ");
                    if (s.AudioMono) sb.Append("-ac 1 ");
                }
                sb.Append($"-y \"{outputFile}\" ");
            }
            sb.Append("-map \"[pvs]\" -f rawvideo -pix_fmt bgr24 pipe:1 ");
            if (hasAudio) sb.Append("-map \"[apvs]\" -f null NUL");
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
