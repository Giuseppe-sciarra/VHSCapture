using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

namespace VHSCapture
{
    // =====================================================================================
    //  Utility ffmpeg: dispositivi, encoder, remux
    // =====================================================================================
    public static class FFmpeg
    {
        public static string ExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        public static bool Exists => File.Exists(ExePath);

        static bool? hasZmq;
        public static bool HasZmq
        {
            get
            {
                if (hasZmq == null) hasZmq = Regex.IsMatch(RunCapture("-hide_banner -filters", stdout: true), @"\szmq\s");
                return hasZmq.Value;
            }
        }

        internal static ProcessStartInfo Psi(string args) => new ProcessStartInfo
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

        /// <summary>Modalità dichiarate dal dispositivo: formato → risoluzioni e fps massimi.</summary>
        public class DeviceMode { public string Format; public string Size; public double MaxFps; public override string ToString() => $"{Format} {Size} @ {MaxFps:0.##}"; }

        static readonly ConcurrentDictionary<string, List<DeviceMode>> modeCache = new ConcurrentDictionary<string, List<DeviceMode>>();

        /// <summary>Modalità del dispositivo. Se il dispositivo è occupato dall'anteprima usa l'ultima lettura riuscita.</summary>
        public static List<DeviceMode> ListModes(string device)
        {
            if (string.IsNullOrEmpty(device)) return new List<DeviceMode>();
            var r = ListModesRaw(device);
            if (r.Count > 0) { modeCache[device] = r; return r; }
            return modeCache.TryGetValue(device, out var c) ? c : r;
        }

        static List<DeviceMode> ListModesRaw(string device)
        {
            var res = new List<DeviceMode>();
            if (!Exists || string.IsNullOrEmpty(device)) return res;
            string err = RunCapture($"-hide_banner -list_options true -f dshow -i video=\"{device}\"");
            // es: vcodec=mjpeg  min s=1920x1080 fps=5 max s=1920x1080 fps=60
            //     pixel_format=yuyv422  min s=720x576 fps=25 max s=720x576 fps=25
            var rx = new Regex(@"(vcodec|pixel_format)=(\S+)\s+min s=(\d+x\d+) fps=([\d.]+)\s+max s=(\d+x\d+) fps=([\d.]+)");
            foreach (Match m in rx.Matches(err))
            {
                string fmt = m.Groups[2].Value;
                double.TryParse(m.Groups[6].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps);
                string size = m.Groups[5].Value;
                var ex = res.FirstOrDefault(x => x.Format == fmt && x.Size == size);
                if (ex == null) res.Add(new DeviceMode { Format = fmt, Size = size, MaxFps = fps });
                else ex.MaxFps = Math.Max(ex.MaxFps, fps);
            }
            return res;
        }

        public static List<string> ListVideoSizes(string device)
        {
            var sizes = new List<string>();
            foreach (var m in ListModes(device)) if (!sizes.Contains(m.Size)) sizes.Add(m.Size);
            return sizes;
        }

        static List<string> workingCache;
        static readonly object encLock = new object();

        /// <summary>Encoder H.264 che si aprono DAVVERO su questo PC (prova di pochi frame). Il risultato resta in cache.</summary>
        public static List<string> ListWorkingH264Encoders()
        {
            lock (encLock)
            {
                if (workingCache != null) return new List<string>(workingCache);
                var compiled = ListH264Encoders();
                var ok = new ConcurrentBag<string>();
                System.Threading.Tasks.Parallel.ForEach(compiled, e => { if (e == "libx264" || TestEncoder(e)) ok.Add(e); });
                // preferenza: hardware prima (come OBS)
                var order = new[] { "h264_nvenc", "h264_amf", "h264_qsv", "libx264" };
                workingCache = order.Where(ok.Contains).ToList();
                if (workingCache.Count == 0) workingCache.Add("libx264");
                return new List<string>(workingCache);
            }
        }

        public static bool TestEncoder(string enc)
        {
            try
            {
                using var p = Process.Start(Psi($"-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=25:d=0.5 -frames:v 5 -pix_fmt yuv420p -c:v {enc} -f null -"));
                var a = p.StandardError.ReadToEndAsync(); var b = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
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
                var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                p.WaitForExit(10000);
                return stdout ? so.Result : se.Result;
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
                var se = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(se.Result)) log?.Invoke(se.Result.Trim());
                return p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0;
            }
            catch (Exception ex) { log?.Invoke("Remux error: " + ex.Message); return false; }
        }
    }

    // =====================================================================================
    //  Controllo live via zmq
    // =====================================================================================
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
                if (!rep.StartsWith("0")) Log?.Invoke($"zmq [{cmd}] → {rep}");
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

    // =====================================================================================
    //  Frame di anteprima (BGRA top-down), disegnato con StretchDIBits: niente Bitmap, niente GDI+
    // =====================================================================================
    public class FrameBuf
    {
        public readonly byte[] Data; public readonly int W, H;
        public FrameBuf(int w, int h) { W = w; H = h; Data = new byte[w * h * 4]; }
    }

    public class EngineStats
    {
        public double Fps; public long Frame; public long Drop; public long Dup; public double Speed; public long TotalSize; public double CpuPercent;
    }

    // =====================================================================================
    //  Motore di cattura
    // =====================================================================================
    public class CaptureEngine : IDisposable
    {
        public int PW { get; private set; } = 960;
        public int PH { get; private set; } = 540;

        public event Action<FrameBuf> FrameReady;
        public event Action<string, double, double, double, double> AudioLevels;   // id sorgente, rmsL, peakL, rmsR, peakR (dB)
        public event Action<byte[], int> MonitorData;                       // PCM s16le 48k stereo
        public event Action<EngineStats> Stats;
        public event Action<string> Log;
        public event Action<int> Exited;

        public bool IsRunning => proc != null && !proc.HasExited;
        public bool IsRecording { get; private set; }
        public bool LiveControl => zmq != null && zmq.Enabled;
        public string LastCommand { get; private set; }

        Process proc; volatile bool stopping;
        ZmqControl zmq;
        NamedPipeServerStream pvPipe, monPipe;
        Thread pvThread, monThread;
        HashSet<string> activeIds = new HashSet<string>(), activeAudioIds = new HashSet<string>();
        bool hasMix;   // esiste il ramo di mix (registrazione o ascolto): lì c'è il filtro del muto
        Dictionary<int, string> inputMap = new Dictionary<int, string>();   // indice input ffmpeg → id sorgente
        readonly ConcurrentDictionary<string, string> inputInfo = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, (int w, int h)> inputSize = new ConcurrentDictionary<string, (int, int)>();
        static int pipeCounter;

        // cpu
        TimeSpan lastCpu; DateTime lastCpuAt;

        /// <summary>Avvia ffmpeg. previewW = larghezza anteprima desiderata (≈ larghezza del riquadro a schermo).</summary>
        public void Start(AppSettings s, string outputFile, int previewW, bool monitor)
        {
            Stop();
            stopping = false;
            IsRecording = outputFile != null;
            inputInfo.Clear(); inputSize.Clear(); inInputSection = false;

            PW = Math.Clamp(previewW / 2 * 2, 480, 1280);
            PH = Math.Max(2, (int)Math.Round((double)PW * s.CanvasH / s.CanvasW / 2) * 2);

            int n = Interlocked.Increment(ref pipeCounter);
            string pvName = $"vhscapture_pv_{Environment.ProcessId}_{n}";
            string monName = $"vhscapture_mon_{Environment.ProcessId}_{n}";

            // pipe con buffer grande: una lettura per frame invece di centinaia (la pipe di stdout è da 4 KB)
            pvPipe = new NamedPipeServerStream(pvName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, PW * PH * 4 * 3, 0);
            bool hasAudio = s.Sources.Any(x => x.Visible && x.HasAudio && IsUsable(x));
            bool mon = monitor && hasAudio;
            if (mon) monPipe = new NamedPipeServerStream(monName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None, 1 << 20, 0);

            bool live = s.LiveControl && FFmpeg.HasZmq;
            activeIds = new HashSet<string>(s.Sources.Where(x => x.Visible && IsUsable(x)).Select(x => x.Id));
            activeAudioIds = new HashSet<string>(s.Sources.Where(x => x.Visible && IsUsable(x) && x.HasAudio).Select(x => x.Id));

            string args = BuildArgs(s, outputFile, PW, PH, live, @"\\.\pipe\" + pvName, mon ? @"\\.\pipe\" + monName : null, out inputMap);
            hasMix = hasAudio && (outputFile != null || mon);
            LastCommand = "ffmpeg " + args;
            Log?.Invoke(LastCommand);

            var psi = FFmpeg.Psi(args);
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += OnErr;
            proc.OutputDataReceived += (o, e) => { };
            proc.Exited += (o, e) => { try { Exited?.Invoke(((Process)o).ExitCode); } catch { } };
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            if (s.HighPriority) { try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { } }
            lastCpu = TimeSpan.Zero; lastCpuAt = DateTime.Now;

            var pvp = pvPipe;
            pvThread = new Thread(() => PreviewLoop(pvp)) { IsBackground = true, Name = "preview" };
            pvThread.Start();
            if (mon)
            {
                var mp = monPipe;
                monThread = new Thread(() => MonitorLoop(mp)) { IsBackground = true, Name = "monitor" };
                monThread.Start();
            }

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
            var p = proc; proc = null;
            if (p != null)
            {
                stopping = true;
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                        if (!p.WaitForExit(IsRecording ? 20000 : 5000))
                        {
                            Log?.Invoke("ffmpeg non risponde, kill forzato");
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(3000); } catch { }
                        }
                    }
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
            // chiudere le pipe sblocca anche un eventuale WaitForConnection
            try { pvPipe?.Dispose(); } catch { }
            try { monPipe?.Dispose(); } catch { }
            try { pvThread?.Join(1500); } catch { }
            try { monThread?.Join(1500); } catch { }
            pvPipe = null; monPipe = null;
            IsRecording = false;
        }

        public void Dispose() => Stop();

        public string GetInputInfo(string id) => inputInfo.TryGetValue(id, out var v) ? v : null;
        public (int w, int h)? GetInputSize(string id) => inputSize.TryGetValue(id, out var v) ? v : ((int, int)?)null;

        public double CpuPercent()
        {
            try
            {
                var p = proc; if (p == null || p.HasExited) return 0;
                p.Refresh();
                var cpu = p.TotalProcessorTime; var now = DateTime.Now;
                double el = (now - lastCpuAt).TotalMilliseconds;
                double used = (cpu - lastCpu).TotalMilliseconds;
                lastCpu = cpu; lastCpuAt = now;
                if (el <= 0) return 0;
                return Math.Clamp(used / el / Environment.ProcessorCount * 100.0, 0, 100);
            }
            catch { return 0; }
        }

        // ---------- comandi live ----------

        static bool IsUsable(Source x) => x.Type switch
        {
            SourceType.Capture => !string.IsNullOrWhiteSpace(x.VideoDevice),
            SourceType.Image => File.Exists(x.ImagePath),
            _ => true,
        };

        public void ApplyTransform(Source src)
        {
            if (!LiveControl || !activeIds.Contains(src.Id)) return;
            zmq.Queue(src.Id + ":w", $"scale@s{src.Id} w {src.W}");
            zmq.Queue(src.Id + ":h", $"scale@s{src.Id} h {src.H}");
            zmq.Queue(src.Id + ":x", $"overlay@s{src.Id} x {src.X}");
            zmq.Queue(src.Id + ":y", $"overlay@s{src.Id} y {src.Y}");
            if (src.Type == SourceType.Capture)
            {
                zmq.Queue(src.Id + ":cw", $"crop@s{src.Id} w iw-{src.CropL + src.CropR}");
                zmq.Queue(src.Id + ":ch", $"crop@s{src.Id} h ih-{src.CropT + src.CropB}");
                zmq.Queue(src.Id + ":cx", $"crop@s{src.Id} x {src.CropL}");
                zmq.Queue(src.Id + ":cy", $"crop@s{src.Id} y {src.CropT}");
            }
        }

        public void ApplyColor(Source src)
        {
            if (!LiveControl || !activeIds.Contains(src.Id)) return;
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
            if (!LiveControl || !src.HasAudio || !activeAudioIds.Contains(src.Id)) return;
            var ci = CultureInfo.InvariantCulture;
            // guadagno prima del misuratore (il VU segue il fader), muto solo sul ramo di mix (il VU resta visibile, come OBS)
            zmq.Queue(src.Id + ":vol", $"volume@a{src.Id} volume {src.VolumeGain.ToString("0.#####", ci)}");
            if (hasMix) zmq.Queue(src.Id + ":mute", $"volume@m{src.Id} volume {(src.Muted ? "0" : "1")}");
        }

        // ---------- stderr: log, progress, livelli audio, info ingressi ----------

        static readonly Regex rxStream = new Regex(@"Stream #(\d+):\d+.*?: Video: (\w+)");
        static readonly Regex rxSize = new Regex(@"[\s,](\d{2,5})x(\d{2,5})[\s,\[]");
        static readonly Regex rxRawFmt = new Regex(@"rawvideo \([^)]*\), (\w+)");
        static readonly Regex rxFps = new Regex(@"([\d.]+) fps");
        bool inInputSection;
        double rmsL = -90, rmsR = -90, pkL = -90, pkR = -90;
        string meterSrc;
        readonly EngineStats st = new EngineStats();

        void OnErr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;

            // livelli audio (ametadata)
            if (line.StartsWith("lavfi.astats."))
            {
                int eq = line.IndexOf('='); if (eq < 0) return;
                string key = line.Substring(13, eq - 13); string v = line.Substring(eq + 1).Trim();
                double db = v == "-inf" || v == "nan" ? -90 : (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : -90);
                switch (key)
                {
                    case "1.RMS_level": rmsL = db; break;
                    case "2.RMS_level": rmsR = db; break;
                    case "1.Peak_level": pkL = db; break;
                    case "2.Peak_level": pkR = db; break;
                }
                return;
            }
            if (line.StartsWith("vhs.src=")) { meterSrc = line.Substring(8).Trim(); return; }
            if (line.StartsWith("frame:"))
            {
                // inizio di un nuovo blocco: consegno il precedente
                if (meterSrc != null) AudioLevels?.Invoke(meterSrc, rmsL, pkL, rmsR, pkR);
                meterSrc = null; rmsL = pkL = rmsR = pkR = -90;
                return;
            }

            // progress (-progress pipe:2)
            int ix = line.IndexOf('=');
            if (ix > 0 && ix < 24 && !line.Contains(' ') && Regex.IsMatch(line, @"^[a-z_0-9]+=", RegexOptions.None))
            {
                string k = line.Substring(0, ix), v = line.Substring(ix + 1);
                var ci = CultureInfo.InvariantCulture;
                switch (k)
                {
                    case "frame": long.TryParse(v, out st.Frame); break;
                    case "fps": double.TryParse(v, NumberStyles.Float, ci, out st.Fps); break;
                    case "drop_frames": long.TryParse(v, out st.Drop); break;
                    case "dup_frames": long.TryParse(v, out st.Dup); break;
                    case "total_size": long.TryParse(v, out st.TotalSize); break;
                    case "speed": double.TryParse(v.TrimEnd('x'), NumberStyles.Float, ci, out st.Speed); break;
                    case "progress":
                        var copy = new EngineStats { Fps = st.Fps, Frame = st.Frame, Drop = st.Drop, Dup = st.Dup, Speed = st.Speed, TotalSize = st.TotalSize };
                        Stats?.Invoke(copy);
                        break;
                }
                return;
            }

            // info ingressi (formato/risoluzione/fps effettivi)
            if (line.StartsWith("Input #")) inInputSection = true;
            else if (line.StartsWith("Output #") || line.StartsWith("Stream mapping")) inInputSection = false;
            var ms = inInputSection ? rxStream.Match(line) : Match.Empty;
            if (ms.Success && int.TryParse(ms.Groups[1].Value, out int inIdx) && inputMap.TryGetValue(inIdx, out string sid))
            {
                string codec = ms.Groups[2].Value;
                var sz = rxSize.Match(line + " ");
                if (sz.Success)
                {
                    int w = int.Parse(sz.Groups[1].Value), h = int.Parse(sz.Groups[2].Value);
                    var mf = rxFps.Match(line);
                    string fps = mf.Success ? mf.Groups[1].Value : "?";
                    if (codec == "rawvideo") { var rf = rxRawFmt.Match(line); if (rf.Success) codec = rf.Groups[1].Value; }
                    inputSize[sid] = (w, h);
                    inputInfo[sid] = $"{w}×{h} · {fps} fps · {codec}";
                }
            }

            if (line.Trim().Length == 0) return;
            if (IsNoise(line)) return;
            Log?.Invoke(line);
        }

        static bool IsNoise(string l)
        {
            // righe informative di ffmpeg che non servono nel Log
            return l.StartsWith("  Metadata:") || l.StartsWith("      ") || l.StartsWith("    encoder") || l.StartsWith("  Side data") ||
                   l.Contains("Press [q] to stop") || l.StartsWith("Stream mapping:") || l.StartsWith("  Stream #0:0 (") ||
                   l.StartsWith("  Duration:") || l.Contains("[aost#") || l.Contains("[vost#") && !l.Contains("rror");
        }

        // ---------- anteprima ----------

        FrameBuf[] bufs; int back; volatile bool uiBusy;

        /// <summary>La UI lo chiama dopo aver disegnato il frame: il motore può consegnare il prossimo.</summary>
        public void FrameConsumed() => uiBusy = false;

        void PreviewLoop(NamedPipeServerStream pipe)
        {
            int w = PW, h = PH, size = w * h * 4;
            var local = new[] { new FrameBuf(w, h), new FrameBuf(w, h) };
            bufs = local; back = 0; uiBusy = false;
            byte[] scratch = new byte[size];
            try { pipe.WaitForConnection(); } catch { return; }

            while (true)
            {
                // se la UI è indietro leggo in un buffer di scarto: la pipe va SEMPRE svuotata, altrimenti ffmpeg si blocca
                bool drop = stopping || uiBusy;
                byte[] target = drop ? scratch : local[back].Data;
                int got = 0;
                try { while (got < size) { int r = pipe.Read(target, got, size - got); if (r <= 0) return; got += r; } }
                catch { return; }
                if (drop) continue;

                var fb = local[back];
                uiBusy = true;
                back ^= 1;
                try { FrameReady?.Invoke(fb); } catch { uiBusy = false; }
            }
        }

        void MonitorLoop(NamedPipeServerStream pipe)
        {
            byte[] buf = new byte[48000 * 4 / 25];   // 40 ms
            try { pipe.WaitForConnection(); } catch { return; }
            while (true)
            {
                int r;
                try { r = pipe.Read(buf, 0, buf.Length); } catch { return; }
                if (r <= 0) return;
                if (!stopping) { try { MonitorData?.Invoke(buf, r); } catch { } }
            }
        }

        // ---------- costruzione grafo ----------

        static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

        static string DeintFilter(string mode) => mode switch
        {
            "yadif" => "yadif=mode=send_frame",
            "yadif2x" => "yadif=mode=send_field",
            "bwdif" => "bwdif=mode=send_frame",
            "bwdif2x" => "bwdif=mode=send_field",
            _ => null,
        };

        static string ScaleFlags(string f) => f switch
        {
            "bilinear" => "bilinear", "lanczos" => "lanczos", "area" => "area", "fast_bilinear" => "fast_bilinear", _ => "bicubic",
        };

        public static string BuildArgs(AppSettings s, string outputFile, int pw, int ph, bool zmq, string pvPath, string monPath, out Dictionary<int, string> map)
        {
            map = new Dictionary<int, string>();
            var sb = new StringBuilder();
            var graph = new StringBuilder();
            string fps = string.IsNullOrWhiteSpace(s.Fps) ? "25" : s.Fps;
            var visible = s.Sources.Where(x => x.Visible).ToList();

            // -y globale: le pipe "esistono già" e senza -y ffmpeg chiederebbe se sovrascrivere
            sb.Append("-hide_banner -y -loglevel info -nostats -progress pipe:2 -stats_period 1 ");
            sb.Append($"-filter_complex_threads {Math.Clamp(Environment.ProcessorCount, 2, 8)} ");
            // tela (input 0). Niente -re: il ritmo lo impone il dispositivo live.
            sb.Append($"-f lavfi -i color=c=black:s={s.CanvasW}x{s.CanvasH}:r={fps} ");

            var idx = new Dictionary<Source, int>();
            int n = 1;
            foreach (var src in visible)
            {
                switch (src.Type)
                {
                    case SourceType.Capture:
                        if (string.IsNullOrWhiteSpace(src.VideoDevice)) continue;
                        sb.Append($"-f dshow -rtbufsize {Math.Max(64, src.RtBufMB)}M -thread_queue_size 4096 ");
                        switch (src.VideoFormat)
                        {
                            case "mjpeg": sb.Append("-vcodec mjpeg "); break;
                            case "yuyv422": sb.Append("-pixel_format yuyv422 "); break;
                            case "nv12": sb.Append("-pixel_format nv12 "); break;
                        }
                        if (!string.IsNullOrWhiteSpace(src.InputSize) && src.InputSize != "auto") sb.Append($"-video_size {src.InputSize} ");
                        if (!string.IsNullOrWhiteSpace(src.InputFps) && src.InputFps != "auto") sb.Append($"-framerate {src.InputFps} ");
                        if (src.HasAudio) sb.Append("-audio_buffer_size 50 ");
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
                idx[src] = n; map[n] = src.Id;
                n++;
            }

            string cur = "[0:v]";
            if (zmq) { graph.Append("[0:v]zmq[base];"); cur = "[base]"; }

            int k = 0;
            foreach (var kv in idx)
            {
                var src = kv.Key; int i = kv.Value;
                var chain = new List<string>();
                if (src.Type == SourceType.Capture)
                {
                    var d = DeintFilter(src.DeinterlaceMode);
                    if (d != null) chain.Add(d);
                    // crop quasi gratis (sposta solo i puntatori), sempre presente così è regolabile al volo
                    chain.Add($"crop@s{src.Id}=w=iw-{src.CropL + src.CropR}:h=ih-{src.CropT + src.CropB}:x={src.CropL}:y={src.CropT}:exact=1");
                    chain.Add($"eq@s{src.Id}=brightness={F(src.Brightness)}:contrast={F(src.Contrast)}:saturation={F(src.Saturation)}:gamma={F(src.Gamma)}");
                    chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}");
                }
                else
                {
                    chain.Add("format=yuva420p");
                    chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}:s={F(src.Saturation)}:b={F(src.Brightness * 10)}");
                }
                chain.Add($"scale@s{src.Id}=w={Math.Max(2, src.W)}:h={Math.Max(2, src.H)}:flags={ScaleFlags(src.ScaleFilter)}:eval=frame");
                graph.Append($"[{i}:v]{string.Join(",", chain)}[v{k}];");
                graph.Append($"{cur}[v{k}]overlay@s{src.Id}=x={src.X}:y={src.Y}:eof_action=pass:format=yuv420[t{k}];");
                cur = $"[t{k}]";
                k++;
            }

            bool recV = outputFile != null;
            string pvFps = PreviewFps(fps);
            if (recV) graph.Append($"{cur}fps={fps},format=yuv420p,split=2[rec][pv];[pv]fps={pvFps},scale={pw}:{ph}:flags=fast_bilinear,format=bgra[pvs];");
            else graph.Append($"{cur}fps={pvFps},scale={pw}:{ph}:flags=fast_bilinear,format=bgra[pvs];");

            // ---- audio ----
            // per ogni sorgente: offset → 48k → guadagno → [misuratore della sorgente] + [ramo di mix con muto]
            var audioSrcs = idx.Keys.Where(x => x.HasAudio).ToList();
            bool hasAudio = audioSrcs.Count > 0;
            bool rec = outputFile != null;
            bool mon = hasAudio && monPath != null;
            bool mix = hasAudio && (rec || mon);
            string meterOpts = "astats=metadata=1:reset=1:measure_perchannel=RMS_level+Peak_level:measure_overall=none";
            // doppio escape: il parser del grafo toglie un backslash, il parser delle opzioni l'altro → file=pipe:2
            string printOpts = "ametadata=mode=print:file=pipe\\\\:2";
            var meterLabels = new List<string>();
            var mixLabels = new List<string>();
            int a = 0;
            foreach (var src in audioSrcs)
            {
                string off = src.AudioOffsetMs != 0 ? $"asetpts=PTS+{F(src.AudioOffsetMs / 1000.0, "0.000")}/TB," : "";
                string head = $"[{idx[src]}:a]{off}aresample=48000:async=1,volume@a{src.Id}=volume={F(src.VolumeGain, "0.#####")}";
                string meter = $"asetnsamples=n=1600:p=0,ametadata=mode=add:key=vhs.src:value={src.Id},{meterOpts},{printOpts}";
                if (mix)
                {
                    graph.Append($"{head},asplit=2[am{a}][ax{a}];");
                    graph.Append($"[am{a}]{meter}[amo{a}];");
                    graph.Append($"[ax{a}]volume@m{src.Id}=volume={(src.Muted ? "0" : "1")}[amx{a}];");
                    mixLabels.Add($"[amx{a}]");
                }
                else graph.Append($"{head},{meter}[amo{a}];");
                meterLabels.Add($"[amo{a}]");
                a++;
            }
            if (mix)
            {
                string amix;
                if (mixLabels.Count == 1) amix = mixLabels[0];
                else { graph.Append($"{string.Join("", mixLabels)}amix=inputs={mixLabels.Count}:duration=longest:normalize=0[amix];"); amix = "[amix]"; }
                if (rec && mon) graph.Append($"{amix}asplit=2[arec][amon];");
                else if (rec) graph.Append($"{amix}anull[arec];");
                else graph.Append($"{amix}anull[amon];");
                if (mon) graph.Append("[amon]aformat=sample_fmts=s16:channel_layouts=stereo[amons];");
            }

            sb.Append($"-filter_complex \"{graph.ToString().TrimEnd(';')}\" ");

            if (rec)
            {
                sb.Append("-map \"[rec]\" ");
                if (hasAudio) sb.Append("-map \"[arec]\" ");
                sb.Append(EncoderArgs(s, fps));
                if (hasAudio)
                {
                    sb.Append($"-c:a aac -b:a {s.AudioBitrate}k ");
                    if (s.AudioMono) sb.Append("-ac 1 ");
                }
                bool mkv = outputFile.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
                string movflags = s.FragmentedMp4 && !mkv ? "+frag_keyframe+empty_moov+default_base_moof" : null;
                if (s.SplitMinutes > 0 && !mkv)
                {
                    sb.Append($"-f segment -segment_time {s.SplitMinutes * 60} -reset_timestamps 1 -segment_format mp4 ");
                    if (movflags != null) sb.Append($"-segment_format_options movflags={movflags} ");
                }
                else if (movflags != null) sb.Append($"-movflags {movflags} ");
                sb.Append($"\"{outputFile}\" ");
            }
            sb.Append($"-map \"[pvs]\" -f rawvideo \"{pvPath}\" ");
            foreach (var ml in meterLabels) sb.Append($"-map \"{ml}\" -f null NUL ");
            if (mon) sb.Append($"-map \"[amons]\" -f s16le -ar 48000 -ac 2 \"{monPath}\"");
            return sb.ToString();
        }

        /// <summary>Anteprima fino a 60 fps (come OBS, che mostra il canvas alla sua frequenza).</summary>
        static string PreviewFps(string fps)
        {
            if (double.TryParse(fps, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 60) return "60";
            return fps;
        }

        static string EncoderArgs(AppSettings s, string fps)
        {
            int b = Math.Max(500, s.VideoBitrate);
            int crf = Math.Clamp(s.Crf, 0, 51);
            string rc = (s.RateControl ?? "CBR").ToUpperInvariant();
            double.TryParse(fps, NumberStyles.Float, CultureInfo.InvariantCulture, out double f);
            int g = Math.Max(1, (int)Math.Round((f > 0 ? f : 25) * Math.Max(1, s.KeyframeSec)));
            string gop = $"-g {g} ";
            switch (s.Encoder)
            {
                case "h264_nvenc":
                    if (rc == "CRF") return $"-c:v h264_nvenc -preset p5 -rc vbr -cq {crf} -b:v 0 {gop}";
                    if (rc == "VBR") return $"-c:v h264_nvenc -preset p5 -rc vbr -b:v {b}k -maxrate {b * 3 / 2}k -bufsize {b * 2}k {gop}";
                    return $"-c:v h264_nvenc -preset p5 -rc cbr -b:v {b}k -maxrate {b}k -bufsize {b * 2}k {gop}";
                case "h264_qsv":
                    if (rc == "CRF") return $"-c:v h264_qsv -preset medium -global_quality {crf} -look_ahead 0 {gop}";
                    if (rc == "VBR") return $"-c:v h264_qsv -preset medium -b:v {b}k -maxrate {b * 3 / 2}k {gop}";
                    return $"-c:v h264_qsv -preset medium -b:v {b}k -maxrate {b}k {gop}";
                case "h264_amf":
                    if (rc == "CRF") return $"-c:v h264_amf -quality balanced -rc cqp -qp_i {crf} -qp_p {crf} {gop}";
                    if (rc == "VBR") return $"-c:v h264_amf -quality balanced -rc vbr_peak -b:v {b}k -maxrate {b * 3 / 2}k {gop}";
                    return $"-c:v h264_amf -quality balanced -rc cbr -b:v {b}k {gop}";
                default:
                    string pre = string.IsNullOrWhiteSpace(s.Preset) ? "veryfast" : s.Preset;
                    if (rc == "CRF") return $"-c:v libx264 -preset {pre} -crf {crf} {gop}";
                    if (rc == "VBR") return $"-c:v libx264 -preset {pre} -b:v {b}k -maxrate {b * 3 / 2}k -bufsize {b * 2}k {gop}";
                    return $"-c:v libx264 -preset {pre} -b:v {b}k -minrate {b}k -maxrate {b}k -bufsize {b * 2}k -x264-params nal-hrd=cbr {gop}";
            }
        }
    }
}
