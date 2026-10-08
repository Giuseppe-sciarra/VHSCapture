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

        public sealed class EncoderProbeResult
        {
            public string Encoder { get; }
            public bool Included { get; }
            public bool Works { get; }
            public string Detail { get; }
            public EncoderProbeResult(string encoder, bool included, bool works, string detail)
            { Encoder = encoder; Included = included; Works = works; Detail = detail; }
        }
        static readonly string[] encoderOrder = { "h264_qsv", "h264_nvenc", "h264_amf", "libx264" };
        static List<EncoderProbeResult> encoderCache;
        static long cacheAt;
        static DateTime cacheBinaryDate;
        static readonly object encLock = new object();

        // Esiti e cause rimangono visibili. Cache breve e rivalutazione esplicita dopo un errore.
        public static List<EncoderProbeResult> ProbeH264Encoders(bool refresh = false)
        {
            lock (encLock)
            {
                var binaryDate = File.Exists(ExePath) ? File.GetLastWriteTimeUtc(ExePath) : DateTime.MinValue;
                double age = (Stopwatch.GetTimestamp() - cacheAt) / (double)Stopwatch.Frequency;
                if (!refresh && encoderCache != null && age < 30 && binaryDate == cacheBinaryDate)
                    return new List<EncoderProbeResult>(encoderCache);
                var compiled = ListH264Encoders();
                var results = new List<EncoderProbeResult>();
                // Nessuna apertura simultanea di sessioni hardware per il rilevamento.
                foreach (var encoder in encoderOrder)
                    results.Add(compiled.Contains(encoder) ? ProbeEncoder(encoder) :
                        new EncoderProbeResult(encoder, false, false, Exists ? "Questo FFmpeg non include l'encoder " + encoder + "." : "ffmpeg.exe non trovato accanto al programma."));
                encoderCache = results; cacheAt = Stopwatch.GetTimestamp(); cacheBinaryDate = binaryDate;
                return new List<EncoderProbeResult>(results);
            }
        }

        /// <summary>Identifica ffmpeg.exe (data + dimensione): la verifica encoder salvata vale finché non cambia.</summary>
        public static string BinaryStamp()
        {
            try { var fi = new FileInfo(ExePath); return fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : ""; }
            catch { return ""; }
        }

        /// <summary>Motivo in una riga, invece del muro di errori di ffmpeg.</summary>
        public static string ShortReason(EncoderProbeResult r)
        {
            string d = r.Detail ?? "";
            if (d.Contains("minimum required Nvidia driver") || d.Contains("nvenc API version")) return "driver NVIDIA troppo vecchio (serve 610 o più recente)";
            if (d.Contains("nvcuda") || d.Contains("No capable devices found") || d.Contains("Cannot load")) return "nessuna scheda NVIDIA utilizzabile";
            if (d.Contains("amfrt64.dll")) return "nessuna scheda AMD";
            if (d.Contains("MFX") || d.Contains("mfx")) return "QuickSync non disponibile";
            if (!r.Included) return "non incluso in questo ffmpeg";
            return "non funziona su questo PC";
        }

        public static List<string> ListWorkingH264Encoders()
        {
            var working = ProbeH264Encoders().Where(x => x.Works).Select(x => x.Encoder).ToList();
            if (working.Count == 0) working.Add("libx264");
            return working;
        }

        public static string ChooseEncoder(AppSettings settings, IList<string> working)
        {
            // La scelta esplicita dell'utente precede la preferenza automatica Intel.
            if (settings.EncoderUserSet) return settings.Encoder;
            if (settings.IntelGpu && working.Contains("h264_qsv")) return "h264_qsv";
            if (working.Contains(settings.Encoder) && settings.Encoder != "libx264") return settings.Encoder;
            return working.Count > 0 ? working[0] : settings.Encoder;
        }

        public static EncoderProbeResult ProbeEncoder(string enc)
        {
            if (!encoderOrder.Contains(enc)) return new EncoderProbeResult(enc, false, false, "Encoder non previsto.");
            try
            {
                using var p = Process.Start(Psi($"-hide_banner -nostdin -loglevel error -f lavfi -i color=c=black:s=1280x720:r=25:d=0.5 -frames:v 5 -pix_fmt yuv420p -c:v {enc} -f null -"));
                var errors = p.StandardError.ReadToEndAsync(); var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                    return new EncoderProbeResult(enc, true, false, "La verifica non si è conclusa entro 10 secondi. Premi Verifica encoder per riprovare.");
                }
                bool drained = System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { errors, output }, 2000);
                string detail = drained ? errors.Result.Trim() : "Lettura della risposta FFmpeg non completata.";
                bool ok = p.ExitCode == 0;
                return new EncoderProbeResult(enc, true, ok, ok ? "Pronto: prova di codifica H.264 riuscita." :
                    string.IsNullOrEmpty(detail) ? "FFmpeg ha rifiutato l'encoder (codice " + p.ExitCode + ")." : detail);
            }
            catch (Exception ex) { return new EncoderProbeResult(enc, true, false, ex.Message); }
        }

        public static bool TestEncoder(string enc) => ProbeEncoder(enc).Works;

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
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                    return "FFmpeg non ha risposto entro 10 secondi.";
                }
                if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { so, se }, 2000)) return "Risposta FFmpeg incompleta.";
                return stdout ? so.Result : se.Result;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Controlla l'audio di un file registrato (primi 30 s): null se non c'è la traccia, altrimenti (media dB, picco dB).</summary>
        public static (bool hasAudio, double mean, double max) CheckAudio(string file)
        {
            try
            {
                using var p = Process.Start(Psi($"-hide_banner -nostats -t 30 -i \"{file}\" -map 0:a:0? -vn -af volumedetect -f null NUL"));
                var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
                p.WaitForExit(60000);
                string err = se.Result;
                bool has = Regex.IsMatch(err, @"Stream #0:\d+.*Audio:");
                var mm = Regex.Match(err, @"mean_volume:\s*(-?[\d.]+|-inf) dB");
                var mx = Regex.Match(err, @"max_volume:\s*(-?[\d.]+|-inf) dB");
                double P(Match m) => m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -91;
                return (has, P(mm), P(mx));
            }
            catch { return (false, -91, -91); }
        }

        /// <summary>Taglia il file a 'seconds' secondi senza ricodificare (per togliere la coda uniforme). Rimpiazza il file originale.</summary>
        public static bool TrimFile(string file, double seconds, Action<string> log)
        {
            try
            {
                string tmp = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".trim" + Path.GetExtension(file));
                string args = $"-hide_banner -loglevel error -y -i \"{file}\" -t {seconds.ToString("0.00", CultureInfo.InvariantCulture)} -map 0 -c copy \"{tmp}\"";   // senza +faststart: una passata sola
                log?.Invoke("ffmpeg " + args);
                using var p = Process.Start(Psi(args));
                var se = p.StandardError.ReadToEndAsync(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(se.Result)) log?.Invoke(se.Result.Trim());
                if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 4096) { try { File.Delete(tmp); } catch { } return false; }
                File.Delete(file);
                File.Move(tmp, file);
                return true;
            }
            catch (Exception ex) { log?.Invoke("Taglio non riuscito: " + ex.Message); return false; }
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
    //  Frame di anteprima (BGRA top-down), disegnato con StretchDIBits
    // =====================================================================================
    public class FrameBuf
    {
        public readonly byte[] Data; public readonly int W, H;
        public FrameBuf(int w, int h) { W = w; H = h; Data = new byte[w * h * 4]; }
    }

    /// <summary>Chi fornisce i frame di anteprima (il motore): coda di frame pronti + restituzione dei buffer.</summary>
    public interface IFrameSource
    {
        int ReadyFrames { get; }
        bool TryTakeFrame(out FrameBuf fb);
        void ReturnFrame(FrameBuf fb);
        double PreviewRate { get; }
    }

    public class EngineStats
    {
        public double Fps; public long Frame; public long Drop; public long Dup; public double Speed; public long TotalSize; public double CpuPercent;
    }

    // =====================================================================================
    //  Registratore: riceve l'MPEG-TS già codificato e lo scrive su file con un processo
    //  ffmpeg separato (come obs-ffmpeg-mux). La pipeline di cattura non si ferma MAI.
    // =====================================================================================
    public class TsRecorder
    {
        const int PKT = 188;
        public event Action<string> Log;

        // stato del flusso
        byte[] pat, pmt;
        int pmtPid = -1, videoPid = -1;
        readonly List<byte[]> gop = new List<byte[]>();   // pacchetti dall'ultimo keyframe (pre-roll)
        long gopBytes;
        bool haveKey;
        readonly byte[] carry = new byte[PKT]; int carryLen;

        // registrazione
        Process mux;
        System.Collections.Concurrent.BlockingCollection<byte[]> queue;
        Thread writer;
        volatile bool armed, recording, closing, muxStopping;
        string lastError;
        long outputTimeUs, lastOutputTick;
        public string LastError => Volatile.Read(ref lastError);
        public void ReportFailure(string message) => Fail(message);
        public bool OutputStarted => Interlocked.Read(ref outputTimeUs) > 0;
        public double SecondsSinceOutput => (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastOutputTick)) / (double)Stopwatch.Frequency;
        void Fail(string message)
        {
            if (Interlocked.CompareExchange(ref lastError, message, null) != null) return;
            Log?.Invoke("Registrazione interrotta: " + message);
            try { queue?.CompleteAdding(); } catch { }
            closed.Set();
        }
        readonly ManualResetEventSlim closed = new ManualResetEventSlim(true);
        public bool IsRecording => recording || armed || paused || pausing || resumeArmed;

        // ===== pausa (come OBS): i timestamp vengono riscritti, così nel file non resta nessun buco =====
        volatile bool pausing, paused, resumeArmed;
        public bool IsPaused => paused || pausing || resumeArmed;
        long offset90k;                  // quanto sottrarre a PTS/DTS/PCR dopo le pause
        long lastAdjVideoDts = -1, lastAdjAudioPts = -1;
        long lastRawVideoDts = -1, frameDur90k = 3600;   // durata di un frame misurata dal flusso (default 25 fps)
        readonly Dictionary<int, bool> dropPes = new Dictionary<int, bool>();
        const long Wrap = 1L << 33;

        /// <summary>Mette in pausa all'inizio del prossimo frame video (l'ultimo frame scritto è intero).</summary>
        public void Pause() { lock (this) { if (recording) pausing = true; else if (armed) { armed = false; paused = true; } } }

        /// <summary>Riprende dall'ultimo keyframe disponibile (pre-roll ≤ intervallo keyframe), ricucendo i timestamp.</summary>
        public void Resume()
        {
            lock (this)
            {
                if (!paused && !pausing) return;
                Interlocked.Exchange(ref lastOutputTick, Stopwatch.GetTimestamp());
                if (pausing) { pausing = false; return; }   // pausa chiesta e annullata prima di scattare
                paused = false;
                if (haveKey && gop.Count > 0) { StartSegment(gop[0]); foreach (var p in gop) Write(p); recording = true; }
                else resumeArmed = true;
            }
        }

        /// <summary>Calcola lo spostamento dei timestamp perché il segmento riprenda subito dopo l'ultimo frame scritto.</summary>
        void StartSegment(byte[] keyPkt)
        {
            long dts = ReadDts(keyPkt);
            if (dts >= 0 && lastAdjVideoDts >= 0)
                offset90k = Mod(dts - (lastAdjVideoDts + frameDur90k));
            dropPes.Clear();
        }

        static long Mod(long v) { v %= Wrap; return v < 0 ? v + Wrap : v; }

        static int PayloadStart(byte[] p)
        {
            int afc = (p[3] >> 4) & 3;
            if (afc == 0 || afc == 2) return -1;                 // solo adaptation field
            int o = 4;
            if (afc == 3) o += 1 + p[4];
            return o < 188 ? o : -1;
        }

        static long ReadTs(byte[] p, int o) =>
            ((long)((p[o] >> 1) & 7) << 30) | ((long)p[o + 1] << 22) | ((long)(p[o + 2] >> 1) << 15) | ((long)p[o + 3] << 7) | ((long)p[o + 4] >> 1);

        static void WriteTs(byte[] p, int o, long v)
        {
            p[o] = (byte)((p[o] & 0xF1) | ((v >> 29) & 0x0E));
            p[o + 1] = (byte)(v >> 22);
            p[o + 2] = (byte)(((v >> 14) & 0xFE) | 1);
            p[o + 3] = (byte)(v >> 7);
            p[o + 4] = (byte)(((v << 1) & 0xFE) | 1);
        }

        /// <summary>DTS (o PTS se manca) di un pacchetto che apre un PES, altrimenti -1.</summary>
        static long ReadDts(byte[] p)
        {
            if ((p[1] & 0x40) == 0) return -1;
            int o = PayloadStart(p);
            if (o < 0 || o + 19 > 188 || p[o] != 0 || p[o + 1] != 0 || p[o + 2] != 1) return -1;
            int flags = (p[o + 7] >> 6) & 3;
            if (flags == 3) return ReadTs(p, o + 14);
            if (flags == 2) return ReadTs(p, o + 9);
            return -1;
        }

        /// <summary>Scrive un pacchetto applicando lo spostamento dei timestamp (copia: l'originale può servire al pre-roll).</summary>
        void Write(byte[] pkt)
        {
            int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
            bool pusi = (pkt[1] & 0x40) != 0;
            // PAT/PMT, e tutto finché non c'è stata una pausa: passano come sono (salvo i contatori)
            lastOriginal = pkt;
            if (offset90k == 0 || pid == 0 || pid == pmtPid) { TrackAndEnqueue(pkt, pid, pusi); return; }
            var q = (byte[])pkt.Clone();
            {
                // PCR nell'adaptation field
                int afc = (q[3] >> 4) & 3;
                if ((afc == 2 || afc == 3) && q[4] >= 7 && (q[5] & 0x10) != 0)
                {
                    long b = ((long)q[6] << 25) | ((long)q[7] << 17) | ((long)q[8] << 9) | ((long)q[9] << 1) | ((long)q[10] >> 7);
                    b = Mod(b - offset90k);
                    q[6] = (byte)(b >> 25); q[7] = (byte)(b >> 17); q[8] = (byte)(b >> 9); q[9] = (byte)(b >> 1);
                    q[10] = (byte)((q[10] & 0x7F) | (int)((b & 1) << 7));
                }
                // PTS/DTS nell'intestazione del PES
                if (pusi)
                {
                    int o = PayloadStart(q);
                    if (o >= 0 && o + 19 <= 188 && q[o] == 0 && q[o + 1] == 0 && q[o + 2] == 1)
                    {
                        int flags = (q[o + 7] >> 6) & 3;
                        if (flags >= 2) WriteTs(q, o + 9, Mod(ReadTs(q, o + 9) - offset90k));
                        if (flags == 3) WriteTs(q, o + 14, Mod(ReadTs(q, o + 14) - offset90k));
                    }
                }
            }
            TrackAndEnqueue(q, pid, pusi);
        }

        void TrackAndEnqueue(byte[] q, int pid, bool pusi)
        {
            if (pusi && pid != 0 && pid != pmtPid)
            {
                long ts = ReadDts(q);
                if (pid == videoPid) { if (ts >= 0) lastAdjVideoDts = ts; }
                else if (ts >= 0)
                {
                    // audio del pre-roll che si sovrapporrebbe a quello già scritto: il PES intero viene saltato
                    bool drop = lastAdjAudioPts >= 0 && Mod(ts - lastAdjAudioPts) > Wrap / 2 || ts == lastAdjAudioPts;
                    dropPes[pid] = drop;
                    if (!drop) lastAdjAudioPts = ts;
                }
            }
            if (pid != videoPid && pid != 0 && pid != pmtPid && dropPes.TryGetValue(pid, out bool d) && d) return;
            // contatori di continuità rinumerati: dopo una pausa o l'inizio a metà flusso il demuxer non vede "buchi"
            // (altrimenti scarta come corrotto il primo frame dopo la giunzione — visto nei test)
            int afc = (q[3] >> 4) & 3;
            if (afc == 1 || afc == 3)
            {
                int cc = ccOut.TryGetValue(pid, out int prev) ? (prev + 1) & 0x0F : q[3] & 0x0F;
                if ((q[3] & 0x0F) != cc)
                {
                    if (ReferenceEquals(q, lastOriginal)) q = (byte[])q.Clone();
                    q[3] = (byte)((q[3] & 0xF0) | cc);
                }
                ccOut[pid] = cc;
            }
            Enqueue(q);
        }
        readonly Dictionary<int, int> ccOut = new Dictionary<int, int>();
        byte[] lastOriginal;
        public string LastCommand { get; private set; }

        /// <summary>Chiamato dal thread che legge la pipe TS dell'encoder.</summary>
        public void Feed(byte[] buf, int n)
        {
            int i = 0;
            // riallinea ai pacchetti da 188 byte
            if (carryLen > 0)
            {
                int need = PKT - carryLen, take = Math.Min(need, n);
                Buffer.BlockCopy(buf, 0, carry, carryLen, take); carryLen += take; i = take;
                if (carryLen == PKT) { OnPacket(carry, 0); carryLen = 0; }
            }
            while (i + PKT <= n)
            {
                if (buf[i] != 0x47) { i++; continue; }   // perso l'allineamento: cerca il sync byte
                OnPacket(buf, i); i += PKT;
            }
            if (i < n) { Buffer.BlockCopy(buf, i, carry, 0, n - i); carryLen = n - i; }
        }

        void OnPacket(byte[] b, int o)
        {
            int pid = ((b[o + 1] & 0x1F) << 8) | b[o + 2];
            bool pusi = (b[o + 1] & 0x40) != 0;
            int afc = (b[o + 3] >> 4) & 3;
            var pkt = new byte[PKT]; Buffer.BlockCopy(b, o, pkt, 0, PKT);

            if (pkt[0] != 0x47 || (pkt[1] & 0x80) != 0 || afc == 0) return;
            if ((afc == 2 || afc == 3) && pkt[4] > 183) return;
            if (pid == 0 && pusi && ParsePat(pkt)) pat = pkt;
            else if (pid == pmtPid && pusi && ParsePmt(pkt)) pmt = pkt;

            bool key = false;
            if (pid == videoPid && pusi && (afc == 2 || afc == 3) && pkt[4] > 0 && (pkt[5] & 0x40) != 0) key = true;   // random_access_indicator

            if (pid == videoPid && pusi)
            {
                long rd = ReadDts(pkt);
                if (rd >= 0 && lastRawVideoDts >= 0) { long dd = Mod(rd - lastRawVideoDts); if (dd > 0 && dd < 90000) frameDur90k = dd; }
                if (rd >= 0) lastRawVideoDts = rd;
            }

            if (key)
            {
                // nuovo GOP: il pre-roll riparte da qui
                gop.Clear(); gopBytes = 0; haveKey = true;
                if (armed && pat != null && pmt != null)
                {
                    armed = false; recording = true;
                    Write(pat); Write(pmt);
                }
                if (resumeArmed)
                {
                    resumeArmed = false; recording = true;
                    StartSegment(pkt);
                }
            }
            if (haveKey && pid != 0 && pid != pmtPid)
            {
                if (gopBytes < 64L << 20) { gop.Add(pkt); gopBytes += PKT; }
                else { gop.Clear(); gopBytes = 0; haveKey = false; }
            }
            // chiusura pulita: si smette di scrivere all'inizio del frame video successivo, così l'ultimo frame è intero
            if (recording && closing && pid == videoPid && pusi) { recording = false; closing = false; closed.Set(); }
            // pausa pulita: stesso principio
            if (recording && pausing && pid == videoPid && pusi) { recording = false; pausing = false; paused = true; }
            if (recording) Write(pkt);
        }

        static bool Section(byte[] p, int table, int minLength, out int ptr, out int end)
        {
            ptr = end = 0;
            int payload = PayloadStart(p);
            if (payload < 0) return false;
            ptr = payload + 1 + p[payload];
            if (ptr + 3 > PKT || p[ptr] != table || (p[ptr + 1] & 0x80) == 0) return false;
            int length = ((p[ptr + 1] & 15) << 8) | p[ptr + 2];
            // Le tabelle della nostra uscita FFmpeg stanno in un pacchetto. Non leggere sezioni tronche.
            if (length < minLength || ptr + 3 + length > PKT) return false;
            end = ptr + 3 + length - 4;
            return (p[ptr + 5] & 1) != 0;
        }

        bool ParsePat(byte[] p)
        {
            if (!Section(p, 0, 13, out int ptr, out int end)) return false;
            for (int i = ptr + 8; i + 4 <= end; i += 4)
            {
                if (((p[i] << 8) | p[i + 1]) == 0) continue;
                int pid = ((p[i + 2] & 31) << 8) | p[i + 3];
                if (pid == 0 || pid == 8191) return false;
                if (pmtPid != pid) { pmt = null; videoPid = -1; haveKey = false; gop.Clear(); gopBytes = 0; }
                pmtPid = pid; return true;
            }
            return false;
        }

        bool ParsePmt(byte[] p)
        {
            if (!Section(p, 2, 18, out int ptr, out int end)) return false;
            int info = ((p[ptr + 10] & 15) << 8) | p[ptr + 11];
            for (int i = ptr + 12 + info; i + 5 <= end;)
            {
                int type = p[i], pid = ((p[i + 1] & 31) << 8) | p[i + 2];
                int length = ((p[i + 3] & 15) << 8) | p[i + 4];
                if (i + 5 + length > end) return false;
                if ((type == 0x1B || type == 0x24) && pid > 0 && pid < 8191)
                {
                    if (videoPid != pid) { haveKey = false; gop.Clear(); gopBytes = 0; }
                    videoPid = pid; return true;
                }
                i += 5 + length;
            }
            return false;
        }

        void Enqueue(byte[] pkt)
        {
            var q = queue;
            if (q == null || LastError != null) return;
            // La cattura resta fluida, ma una destinazione bloccata deve dare errore: mai perdere pacchetti in silenzio.
            try { if (!q.TryAdd(pkt)) Fail("La destinazione non riesce a scrivere abbastanza velocemente (buffer pieno)."); }
            catch (InvalidOperationException) { if (!muxStopping) Fail("La scrittura del file si è chiusa inaspettatamente."); }
        }

        /// <summary>Inizia a registrare: scrive subito dall'ultimo keyframe (nessun frame perso, nessuno scatto).</summary>
        public void Start(string muxArgs)
        {
            Stop(false);
            Interlocked.Exchange(ref lastError, null); muxStopping = false;
            Interlocked.Exchange(ref outputTimeUs, 0);
            Interlocked.Exchange(ref lastOutputTick, Stopwatch.GetTimestamp());
            LastCommand = "ffmpeg " + muxArgs;
            Log?.Invoke("mux: " + LastCommand);
            var psi = FFmpeg.Psi(muxArgs);
            mux = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var process = mux;
            string muxDetail = null;
            mux.ErrorDataReceived += (o, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { muxDetail = e.Data; Log?.Invoke("mux: " + e.Data); } };
            mux.OutputDataReceived += (o, e) =>
            {
                const string key = "out_time_us=";
                if (!ReferenceEquals(mux, process) || e.Data == null || !e.Data.StartsWith(key) || !long.TryParse(e.Data.Substring(key.Length), out long t)) return;
                if (t > Interlocked.Read(ref outputTimeUs))
                {
                    Interlocked.Exchange(ref outputTimeUs, t);
                    Interlocked.Exchange(ref lastOutputTick, Stopwatch.GetTimestamp());
                }
            };
            mux.Exited += (o, e) =>
            {
                if (ReferenceEquals(mux, process) && !muxStopping)
                    Fail("Il processo di registrazione si è arrestato. " + muxDetail);
            };
            try { mux.Start(); }
            catch { mux.Dispose(); mux = null; throw; }
            mux.BeginErrorReadLine(); mux.BeginOutputReadLine();
            try { mux.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

            queue = new System.Collections.Concurrent.BlockingCollection<byte[]>(new System.Collections.Concurrent.ConcurrentQueue<byte[]>(), 1_600_000); // ~300 MB: circa 10 minuti a 3,5 Mbit/s
            var q = queue; var stdin = mux.StandardInput.BaseStream;
            writer = new Thread(() =>
            {
                var buf = new byte[PKT * 512]; int used = 0;
                try
                {
                    while (!q.IsCompleted)
                    {
                        if (q.TryTake(out var pkt, 40))
                        {
                            Buffer.BlockCopy(pkt, 0, buf, used, PKT); used += PKT;
                            if (used < buf.Length) continue;
                        }
                        if (used > 0) { stdin.Write(buf, 0, used); used = 0; }
                    }
                    if (used > 0) stdin.Write(buf, 0, used);
                    stdin.Flush();
                }
                catch (Exception ex) { if (ReferenceEquals(mux, process)) Fail("Scrittura del file fallita: " + ex.Message); }
                try { stdin.Close(); } catch { }
            }) { IsBackground = true, Name = "mux-writer" };
            writer.Start();

            lock (this)
            {
                offset90k = 0; lastAdjVideoDts = -1; lastAdjAudioPts = -1; dropPes.Clear(); ccOut.Clear();
                pausing = paused = resumeArmed = false;
                if (haveKey && pat != null && pmt != null)
                {
                    Write(pat); Write(pmt);
                    foreach (var p in gop) Write(p);
                    recording = true; armed = false;
                }
                else { armed = true; recording = false; }   // parte al prossimo keyframe
            }
        }

        /// <summary>Ferma: chiude lo stdin del muxer, che finalizza il file. Ritorna true se il muxer è uscito pulito.</summary>
        public bool Stop(bool wait = true)
        {
            muxStopping = true;
            if (recording && wait && LastError == null)
            {
                closed.Reset(); closing = true;
                closed.Wait(1000);          // al massimo un frame
            }
            System.Collections.Concurrent.BlockingCollection<byte[]> q;
            lock (this)
            {
                recording = armed = closing = pausing = paused = resumeArmed = false; closed.Set();
                q = queue; queue = null;
                try { q?.CompleteAdding(); } catch { }
            }
            var m = mux; var w = writer;
            bool drained = true;
            try { drained = w == null || w.Join(wait && LastError == null ? 30000 : 3000); } catch { drained = false; }
            if (!drained)
            {
                Fail("La destinazione non risponde: file chiuso dopo il timeout.");
                try { m?.Kill(); } catch { }
                try { drained = w.Join(3000); } catch { }
            }
            writer = null;
            bool ok = drained && LastError == null;
            if (m != null)
            {
                try
                {
                    if (!m.WaitForExit(wait ? 60000 : 5000))
                    {
                        Fail("Il processo non ha completato la chiusura del file.");
                        try { m.Kill(); m.WaitForExit(3000); } catch { } ok = false;
                    }
                    else if (m.ExitCode != 0) { Fail("Chiusura del file fallita (codice " + m.ExitCode + ")."); ok = false; }
                }
                catch { ok = false; }
                mux = null;
                try { m.Dispose(); } catch { }
            }
            if (drained) q?.Dispose();
            return ok && LastError == null;
        }

        public bool HasBufferedKeyFrame { get { lock (this) return haveKey && pat != null && pmt != null && gop.Count > 0; } }

        // Il pre-roll appartiene a UNA sola istanza ffmpeg: mai riusare PAT/PMT o GOP dopo un riavvio.
        public void ResetStream()
        {
            lock (this)
            {
                pat = pmt = null; pmtPid = videoPid = -1;
                gop.Clear(); gopBytes = 0; haveKey = false; carryLen = 0;
                lastRawVideoDts = lastAdjVideoDts = lastAdjAudioPts = -1;
                frameDur90k = 3600; offset90k = 0; dropPes.Clear(); ccOut.Clear();
            }
        }

        public bool MuxAlive { get { try { return mux != null && !mux.HasExited; } catch { return false; } } }
    }

    // =====================================================================================
    //  Motore di cattura: UN processo ffmpeg sempre acceso (come la pipeline di OBS).
    //  Canvas completo, filtri QSV quando disponibili, encoder sempre acceso:
    //   - anteprima BGRA  → named pipe
    //   - MPEG-TS codificato → named pipe → TsRecorder (registra senza fermare niente)
    //   - livelli audio   → una named pipe per sorgente
    //   - statistiche     → named pipe (-progress)
    //   - audio di ascolto → named pipe
    //  stderr resta solo per il log: niente più righe mescolate.
    // =====================================================================================
    public class CaptureEngine : IDisposable, IFrameSource
    {
        public int PW { get; private set; } = 960;
        public int PH { get; private set; } = 540;

        public event Action<FrameBuf> FrameReady;
        public event Action<string, double, double, double, double> AudioLevels;
        public event Action<byte[], int> MonitorData;
        public event Action<EngineStats> Stats;
        /// <summary>Stato della sorgente (2 volte al secondo), confermato su ogni frame: contenuto, verifica o assenza probabile.</summary>
        /// <summary>id sorgente, secondi di fila in cui si vede solo lo sfondo (0 = contenuto), dettaglio per il Log.</summary>
        public event Action<string, double, string> SignalState;
        public event Action<string> Log;
        public event Action<int> Exited;

        public bool IsRunning => proc != null && !proc.HasExited;
        public bool GpuActive { get; private set; }
        public bool GpuDisabled { get; set; } // solo sessione; non modifica le preferenze salvate
        public int RunId { get; private set; }
        public string QsvBackend { get; private set; } = "d3d11va";
        /// <summary>Riparte dal backend ricordato per questo PC: se D3D11 era già fallito si va dritti su DXVA2.</summary>
        public void ResetGpuRetry(string remembered = null) { GpuDisabled = false; QsvBackend = remembered == "dxva2" ? "dxva2" : "d3d11va"; }
        public bool TryLegacyGpu()
        {
            if (!GpuActive || GpuDisabled || QsvBackend == "dxva2") return false;
            QsvBackend = "dxva2";
            return true;
        }
        Dictionary<string, Source> runningSources = new Dictionary<string, Source>();
        public bool IsRecording => recorder.IsRecording;
        public bool LiveControl => zmq != null && zmq.Enabled;
        public string LastCommand { get; private set; }
        public TsRecorder Recorder => recorder;

        Process proc; volatile bool stopping;
        ZmqControl zmq;
        readonly TsRecorder recorder = new TsRecorder();
        readonly List<NamedPipeServerStream> pipes = new List<NamedPipeServerStream>();
        readonly List<Thread> threads = new List<Thread>();
        HashSet<string> activeIds = new HashSet<string>(), activeAudioIds = new HashSet<string>();
        bool hasMix;
        Dictionary<int, string> inputMap = new Dictionary<int, string>();
        readonly ConcurrentDictionary<string, string> inputInfo = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, (int w, int h)> inputSize = new ConcurrentDictionary<string, (int, int)>();
        // PAL-60 col colore rifatto: fase scelta dal monitor (0 = come calcolato, 1 = V invertito), cambiata dal vivo via zmq
        readonly ConcurrentDictionary<string, PalPhaseMonitor> palMon = new ConcurrentDictionary<string, PalPhaseMonitor>();
        readonly ConcurrentDictionary<string, int> palMap = new ConcurrentDictionary<string, int>();
        bool palNoZmqLogged;

        /// <summary>Inverte V della sorgente in PAL-60 software (tasto «Inverti colore» o monitor automatico), senza riavviare.</summary>
        public bool TogglePal(string id, bool manual)
        {
            if (!palMon.TryGetValue(id, out var mon)) return false;
            if (!LiveControl)
            {
                if (!palNoZmqLogged) Log?.Invoke("Colore PAL: per girarlo dal vivo serve «Modifiche delle sorgenti al volo (zmq)» nelle Impostazioni");
                palNoZmqLogged = true;
                return false;
            }
            int map = palMap.AddOrUpdate(id, 1, (_, m) => m ^ 1);
            lock (mon) { if (manual) mon.ManualToggle(); else mon.ExpectFlip(); }
            zmq.Queue(id + ":pal", PalSoftware.SelectCommand(id, map));
            zmq.Queue(id + ":palm", PalSoftware.SelectCommandDelayed(id, map));
            Log?.Invoke($"Colore PAL: {(manual ? "invertito a mano" : "invertito da solo — " + mon.LastReason)} (uscita {map}{(manual ? "" : "; " + mon.ScheduleInfo)})");
            return true;
        }
        public bool IsPalSoftware(string id) => palMon.ContainsKey(id);
        static int pipeCounter;
        TimeSpan lastCpu; DateTime lastCpuAt;

        public CaptureEngine() { recorder.Log += l => Log?.Invoke(l); }

        /// <summary>Se true i livelli audio vanno a NUL (paracadute: l'anteprima e la registrazione non dipendono dal VU).</summary>
        public bool MetersDisabled { get; set; }
        public bool AnalysisDisabled { get; set; }

        public void Start(AppSettings s, int previewW, bool monitor)
        {
            Stop();
            stopping = false;
            runningSources = s.Sources.ToDictionary(x => x.Id, x => x.Clone());
            palMon.Clear(); palMap.Clear(); palNoZmqLogged = false;
            foreach (var ps in s.Sources.Where(x => x.Visible && x.IsPal60Software)) { palMon[ps.Id] = new PalPhaseMonitor(); palMap[ps.Id] = 0; }
            GpuActive = !GpuDisabled && CanUseQsv(s);
            inputInfo.Clear(); inputSize.Clear(); sig.Clear(); audioActivity.Clear(); audioObserved.Clear(); inInputSection = false;

            PW = Math.Clamp(previewW / 2 * 2, 480, 1280);
            PreviewRate = double.TryParse(PreviewFps(string.IsNullOrWhiteSpace(s.Fps) ? "25" : s.Fps), NumberStyles.Float, CultureInfo.InvariantCulture, out var pr) && pr > 0 ? pr : 30;
            PH = Math.Max(2, (int)Math.Round((double)PW * s.CanvasH / s.CanvasW / 2) * 2);

            int n = Interlocked.Increment(ref pipeCounter);
            RunId = n;
            string tag = $"{Environment.ProcessId}_{n}";
            bool live = s.LiveControl && FFmpeg.HasZmq;
            // porta libera scelta ora: col 5555 fisso, riavviando in fretta la vecchia pipeline la teneva ancora occupata
            int zmqPort = live ? FreePort() : 0;
            activeIds = new HashSet<string>(s.Sources.Where(x => x.Visible && IsUsable(x)).Select(x => x.Id));
            var audioIds = s.Sources.Where(x => x.Visible && IsUsable(x) && x.HasAudio).Select(x => x.Id).ToList();
            activeAudioIds = new HashSet<string>(audioIds);
            colorIds = new HashSet<string>(s.Sources.Where(x => x.Visible && x.Type == SourceType.Capture && !x.ColorIsNeutral).Select(x => x.Id));
            bool mon = monitor && audioIds.Count > 0;
            hasMix = audioIds.Count > 0;

            // nomi delle pipe (nel filtro si usano con le barre dritte: niente escape da gestire)
            var names = new PipeNames
            {
                Preview = "vhscap_pv_" + tag,
                Ts = "vhscap_ts_" + tag,
                Progress = "vhscap_pr_" + tag,
                Monitor = mon ? "vhscap_mo_" + tag : null,
                Meters = MetersDisabled ? new Dictionary<string, string>() : audioIds.ToDictionary(id => id, id => "vhscap_me_" + id + "_" + tag),
                Analysis = AnalysisDisabled ? new Dictionary<string, string>() :
                    s.Sources.Where(x => x.Visible && x.Type == SourceType.Capture && IsUsable(x)).Take(1).ToDictionary(x => x.Id, x => "vhscap_an_" + x.Id + "_" + tag),
                FrameRate = new Dictionary<string, string>(),
            };

            // server delle pipe PRIMA di avviare ffmpeg
            var pvPipe = NewPipe(names.Preview, PW * PH * 4 * 3);
            var tsPipe = NewPipe(names.Ts, 4 << 20);
            var prPipe = NewPipe(names.Progress, 64 << 10);
            var moPipe = mon ? NewPipe(names.Monitor, 1 << 20) : null;
            // ffmpeg 7 inizializza il grafo DUE volte (una per analizzarlo, poi quella vera) e il filtro dei livelli
            // apre la pipe entrambe le volte: servono più istanze in ascolto, altrimenti la seconda apertura fallisce.
            const int meterInstances = 3;
            var mePipes = names.Meters.ToDictionary(kv => kv.Key,
                kv => Enumerable.Range(0, meterInstances).Select(_ => NewPipe(kv.Value, 256 << 10, meterInstances)).ToList());

            // standard del grabber (PAL_B / NTSC_M / PAL_60…) scritto PRIMA che ffmpeg apra il dispositivo:
            // così risoluzione, fps e decoder sono sempre dello stesso standard (niente più "miscuglio" NTSC + PAL)
            var tvSet = s.Sources.Where(x => x.Visible && x.Type == SourceType.Capture && !string.IsNullOrWhiteSpace(x.VideoDevice) && !string.IsNullOrEmpty(x.TvStandard))
                                 .GroupBy(x => x.VideoDevice).Select(g => (dev: g.Key, std: g.First().TvStandard, id: g.First().Id)).ToList();
            // con lo standard a due fasi ("NTSC_M>PAL_B") qui si scrive solo quello di apertura
            foreach (var (dev, std, _) in tvSet) DShowProps.ApplyTv(dev, DShowProps.TvOpen(std), l => Log?.Invoke(l));

            string args = BuildArgs(s, PW, PH, zmqPort, names, out inputMap, GpuActive, QsvBackend);
            fastIdRunning = FastPathId;
            var fs = s.Sources.FirstOrDefault(x => x.Id == fastIdRunning);
            if (fs != null) { lastFastX = fs.X; lastFastY = fs.Y; lastFastW = fs.W; lastFastH = fs.H; }
            LastCommand = "ffmpeg " + args;
            Log?.Invoke("Pipeline completa: encoder attivo e anteprima a tutti i fotogrammi");
            Log?.Invoke(GpuActive ? "Filtri Intel GPU attivi: VPP + composizione QSV (" + QsvBackend + ")" : "Filtri video CPU" + (s.IntelGpu ? " (GPU disattivata, encoder diverso o scena non compatibile)" : ""));
            Log?.Invoke(LastCommand);

            proc = new Process { StartInfo = FFmpeg.Psi(args), EnableRaisingEvents = true };
            proc.ErrorDataReceived += OnErr;
            proc.OutputDataReceived += (o, e) => { };
            proc.Exited += (o, e) =>
            {
                // Ignora le uscite volontarie e quelle appartenenti a una vecchia pipeline.
                if (stopping || !ReferenceEquals(proc, o)) return;
                try { Exited?.Invoke(((Process)o).ExitCode); } catch { }
            };
            proc.Start();
            proc.BeginErrorReadLine(); proc.BeginOutputReadLine();
            if (s.HighPriority) { try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { } }
            lastCpu = TimeSpan.Zero; lastCpuAt = DateTime.Now;

            Run("preview", () => PreviewLoop(pvPipe));
            Run("ts", () => TsLoop(tsPipe));
            Run("progress", () => TextLoop(prPipe, OnProgressLine));
            foreach (var kv in mePipes) { var id = kv.Key; foreach (var p in kv.Value) { var pp = p; Run("meter", () => TextLoop(pp, l => OnMeterLine(id, l))); } }
            foreach (var kv in names.FrameRate)
            {
                for (int i = 0; i < meterInstances; i++) { var pp = NewPipe(kv.Value, 64 << 10, meterInstances); Run("framerate", () => TextLoop(pp, OnFrameLine)); }
            }
            srcFrames.Clear(); SourceFps = 0;
            foreach (var kv in names.Analysis)
            {
                var id = kv.Key;
                var pp = NewPipe(kv.Value, NoSignalDetector.FrameBytes * 8);
                Run("analysis", () => AnalysisLoop(pp, id));
            }
            if (moPipe != null) Run("monitor", () => MonitorLoop(moPipe));

            if (live)
            {
                zmq = new ZmqControl(zmqPort);
                zmq.Log += l => Log?.Invoke(l);
                zmq.Start();
            }

            // a cattura avviata: cambio dal vivo per gli standard a due fasi, poi ricontrollo
            // (alcuni driver all'apertura rimettono lo standard salvato nel registro)
            if (tvSet.Count > 0)
            {
                int run = RunId;
                var chk = new Thread(() =>
                {
                    try
                    {
                        if (tvSet.Any(t => DShowProps.TvTwoPhase(t.std)))
                        {
                            // aspetto che ffmpeg abbia aperto il grabber e stia ricevendo (riga "Input #… Video:" letta), max 15 s
                            var sw = Stopwatch.StartNew();
                            while (sw.ElapsedMilliseconds < 15000 && !(stopping || RunId != run) &&
                                   tvSet.Where(t => DShowProps.TvTwoPhase(t.std)).Any(t => !inputSize.ContainsKey(t.id))) Thread.Sleep(150);
                            Thread.Sleep(800);
                            if (stopping || RunId != run) return;
                            foreach (var (dev, std, _) in tvSet.Where(t => DShowProps.TvTwoPhase(t.std)))
                            {
                                Log?.Invoke($"Standard grabber: cambio dal vivo {DShowProps.TvOpen(std)} → {DShowProps.TvLive(std)} (cattura avviata)");
                                DShowProps.ApplyTv(dev, DShowProps.TvLive(std), l => Log?.Invoke(l));
                                // il colore arriva solo da adesso: la scelta della fase riparte da zero (prima giudicava immagini senza colore)
                                foreach (var kv in palMon) if (runningSources.TryGetValue(kv.Key, out var rs) && rs.VideoDevice == dev) lock (kv.Value) kv.Value.Reset();
                            }
                        }
                        Thread.Sleep(4000);
                        if (stopping || RunId != run) return;
                        // il PAL-60 software non si riscrive a cattura avviata: il driver risponde sempre NTSC_M e ogni scrittura
                        // può far perdere un fotogramma (che sposterebbe la fase del colore)
                        foreach (var (dev, std, _) in tvSet.Where(t => DShowProps.TvLive(t.std) != PalSoftware.TvKey))
                            DShowProps.ApplyTv(dev, DShowProps.TvLive(std), l => Log?.Invoke(l), recheck: true);
                    }
                    catch { }
                }) { IsBackground = true, Name = "tvcheck" };
                chk.Start();
            }
        }

        static int FreePort()
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                return p;
            }
            catch { return 5555 + new Random().Next(1, 400); }
        }

        NamedPipeServerStream NewPipe(string name, int inBuf, int instances = 1)
        {
            var p = new NamedPipeServerStream(name, PipeDirection.In, instances, PipeTransmissionMode.Byte, PipeOptions.None, inBuf, 0);
            pipes.Add(p);
            return p;
        }

        void Run(string name, Action a)
        {
            var t = new Thread(() => { try { a(); } catch { } }) { IsBackground = true, Name = name };
            threads.Add(t); t.Start();
        }

        public void Stop()
        {
            // se si stava registrando, prima si chiude bene il file
            if (recorder.IsRecording) recorder.Stop();
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
                        if (!p.WaitForExit(5000))
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
            foreach (var pp in pipes) { try { pp.Dispose(); } catch { } }
            pipes.Clear();
            foreach (var t in threads) { try { t.Join(1000); } catch { } }
            threads.Clear();
            while (readyFrames.TryDequeue(out var fb)) ReturnFrame(fb);
            recorder.ResetStream();
        }

        public void Dispose() => Stop();

        // ---------- registrazione (il muxer si aggancia dopo che la pipeline completa è pronta) ----------

        public void StartRecording(AppSettings s, string outputFile)
        {
            if (!IsRunning) throw new InvalidOperationException("Pipeline non avviata");
            // niente attese: se il pre-roll ha già un keyframe si scrive subito da lì, altrimenti si parte al prossimo
            recorder.Start(MuxArgs(s, outputFile));
        }

        public bool StopRecording() => recorder.Stop();
        public bool IsPaused => recorder.IsPaused;
        public void PauseRecording() { lock (recorder) recorder.Pause(); }
        public void ResumeRecording() { lock (recorder) recorder.Resume(); }

        static string MuxArgs(AppSettings s, string outputFile)
        {
            var sb = new StringBuilder("-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.5 -n -fflags +genpts+discardcorrupt -probesize 5M -analyzeduration 2M -f mpegts -i pipe:0 -map 0 -c copy -bsf:a aac_adtstoasc ");
            bool mkv = outputFile.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
            string movflags = s.FragmentedMp4 && !mkv ? "+frag_keyframe+empty_moov+default_base_moof" : null;
            if (s.SplitMinutes > 0 && !mkv)
            {
                sb.Append($"-f segment -segment_time {s.SplitMinutes * 60} -reset_timestamps 1 -segment_format mp4 ");
                if (movflags != null) sb.Append($"-segment_format_options movflags={movflags} ");
            }
            else if (movflags != null) sb.Append($"-movflags {movflags} ");
            sb.Append($"\"{outputFile}\"");
            return sb.ToString();
        }

        public string GetInputInfo(string id) => inputInfo.TryGetValue(id, out var v) ? v : null;
        public (int w, int h)? GetInputSize(string id) => inputSize.TryGetValue(id, out var v) ? v : ((int, int)?)null;

        public double CpuPercent()
        {
            try
            {
                var p = proc; if (p == null || p.HasExited) return 0;
                p.Refresh();
                var cpu = p.TotalProcessorTime; var now = DateTime.Now;
                double el = (now - lastCpuAt).TotalMilliseconds, used = (cpu - lastCpu).TotalMilliseconds;
                lastCpu = cpu; lastCpuAt = now;
                return el <= 0 ? 0 : Math.Clamp(used / el / Environment.ProcessorCount * 100.0, 0, 100);
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
            if (!activeIds.Contains(src.Id)) return;
            // I filtri QSV non espongono i comandi ZMQ per ritaglio e trasformazioni.
            if (GpuActive)
            {
                if (runningSources.TryGetValue(src.Id, out var old) &&
                    (src.X != old.X || src.Y != old.Y || src.W != old.W || src.H != old.H ||
                     src.CropL != old.CropL || src.CropT != old.CropT || src.CropR != old.CropR || src.CropB != old.CropB))
                    NeedsRestart?.Invoke();
                return;
            }
            if (fastIdRunning == src.Id)
            {
                // percorso veloce: posizione e dimensione sono fisse nel grafo → basta un riavvio (il ritaglio invece va al volo)
                if (LiveControl && src.Type == SourceType.Capture)
                {
                    zmq.Queue(src.Id + ":cw", $"crop@s{src.Id} w iw-{src.CropL + src.CropR}");
                    zmq.Queue(src.Id + ":ch", $"crop@s{src.Id} h ih-{src.CropT + src.CropB}");
                    zmq.Queue(src.Id + ":cx", $"crop@s{src.Id} x {src.CropL}");
                    zmq.Queue(src.Id + ":cy", $"crop@s{src.Id} y {src.CropT}");
                }
                if (src.X != lastFastX || src.Y != lastFastY || src.W != lastFastW || src.H != lastFastH) NeedsRestart?.Invoke();
                return;
            }
            if (!LiveControl) return;
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

        /// <summary>Scatta quando un cambio colore richiede di riavviare l'anteprima (i filtri colore non erano attivi).</summary>
        public event Action NeedsRestart;
        /// <summary>Id della sorgente composta col percorso veloce (pad), o null se si usa la composizione completa.</summary>
        [ThreadStatic] static string fastPathIdTls;
        static string FastPathId { get => fastPathIdTls; set => fastPathIdTls = value; }
        string fastIdRunning;
        int lastFastX, lastFastY, lastFastW, lastFastH;
        HashSet<string> colorIds = new HashSet<string>();

        public void ApplyColor(Source src)
        {
            if (GpuActive) { if (!src.ColorIsNeutral) NeedsRestart?.Invoke(); return; }
            if (src.Type == SourceType.Capture && !colorIds.Contains(src.Id))
            {
                if (!src.ColorIsNeutral && activeIds.Contains(src.Id)) NeedsRestart?.Invoke();
                return;
            }
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
            zmq.Queue(src.Id + ":vol", $"volume@a{src.Id} volume {src.VolumeGain.ToString("0.#####", ci)}");
            if (hasMix) zmq.Queue(src.Id + ":mute", $"volume@m{src.Id} volume {(src.Muted ? "0" : "1")}");
        }

        // ---------- lettori delle pipe ----------

        // ===== anteprima: coda di frame (buffer anti-tremolio) =====
        // I frame letti dalla pipe finiscono in una coda; il thread di rendering del canvas li presenta a ritmo fisso.
        // Se arrivano "a coppie" non si perdono: vengono mostrati uno per volta a distanza regolare (come OBS).
        readonly ConcurrentQueue<FrameBuf> readyFrames = new ConcurrentQueue<FrameBuf>();
        readonly ConcurrentQueue<FrameBuf> freeFrames = new ConcurrentQueue<FrameBuf>();
        int poolW, poolH;
        /// <summary>Segnalato quando arriva un nuovo frame (sveglia il thread di rendering).</summary>
        public event Action FrameAvailable;
        /// <summary>Frequenza a cui presentare l'anteprima (= fps del canvas, max 60).</summary>
        public double PreviewRate { get; private set; } = 30;

        public int ReadyFrames => readyFrames.Count;
        public bool TryTakeFrame(out FrameBuf fb) => readyFrames.TryDequeue(out fb);
        /// <summary>Restituisce al motore un frame non più mostrato (verrà riusato).</summary>
        public void ReturnFrame(FrameBuf fb)
        {
            if (fb != null && fb.W == poolW && fb.H == poolH) freeFrames.Enqueue(fb);
        }

        // diagnostica: ritmo di arrivo da ffmpeg
        readonly Stopwatch arrivalClock = Stopwatch.StartNew();
        double lastArrival, maxGap; long arrived, dropped;
        public (double fps, double maxGapMs, long dropped) TakeArrivalStats()
        {
            lock (arrivalClock)
            {
                double now = arrivalClock.Elapsed.TotalSeconds;
                var r = (arrivedWindowStart > 0 && now > arrivedWindowStart ? arrived / (now - arrivedWindowStart) : 0, maxGap * 1000, dropped);
                arrived = 0; dropped = 0; maxGap = 0; arrivedWindowStart = now;
                return r;
            }
        }
        double arrivedWindowStart;
        public void FrameConsumed() { }   // compatibilità: con la coda non serve più

        void PreviewLoop(NamedPipeServerStream pipe)
        {
            int w = PW, h = PH, size = w * h * 4;
            // pool nuovo per queste dimensioni (i buffer vecchi eventualmente in giro vengono ignorati al ritorno)
            while (readyFrames.TryDequeue(out _)) { }
            while (freeFrames.TryDequeue(out _)) { }
            poolW = w; poolH = h;
            for (int i = 0; i < 6; i++) freeFrames.Enqueue(new FrameBuf(w, h));
            byte[] scratch = new byte[size];
            try { pipe.WaitForConnection(); } catch { return; }
            while (true)
            {
                // se la coda è piena (rendering fermo) si scarta il più vecchio: latenza sempre limitata
                if (!freeFrames.TryDequeue(out var fb))
                {
                    if (readyFrames.TryDequeue(out var oldest)) fb = oldest;
                }
                byte[] target = fb != null && !stopping ? fb.Data : scratch;
                int got = 0;
                try { while (got < size) { int r = pipe.Read(target, got, size - got); if (r <= 0) return; got += r; } }
                catch { return; }
                lock (arrivalClock)
                {
                    double t = arrivalClock.Elapsed.TotalSeconds;
                    if (lastArrival > 0) maxGap = Math.Max(maxGap, t - lastArrival);
                    lastArrival = t; arrived++;
                    if (fb == null && !stopping) dropped++;
                }
                if (fb == null || stopping) continue;
                readyFrames.Enqueue(fb);
                try { FrameAvailable?.Invoke(); } catch { }
            }
        }

        void TsLoop(NamedPipeServerStream pipe)
        {
            var buf = new byte[188 * 1024];
            try { pipe.WaitForConnection(); } catch { return; }
            while (true)
            {
                int r;
                try { r = pipe.Read(buf, 0, buf.Length); } catch { return; }
                if (r <= 0) return;
                lock (recorder) recorder.Feed(buf, r);
            }
        }

        void TextLoop(NamedPipeServerStream pipe, Action<string> onLine)
        {
            try { pipe.WaitForConnection(); } catch { return; }
            using var rd = new StreamReader(pipe, Encoding.UTF8, false, 16384);
            string line;
            while (true)
            {
                try { line = rd.ReadLine(); } catch { return; }
                if (line == null) return;
                try { onLine(line); } catch { }
            }
        }

        void MonitorLoop(NamedPipeServerStream pipe)
        {
            byte[] buf = new byte[48000 * 4 / 25];
            try { pipe.WaitForConnection(); } catch { return; }
            while (true)
            {
                int r;
                try { r = pipe.Read(buf, 0, buf.Length); } catch { return; }
                if (r <= 0) return;
                if (!stopping) { try { MonitorData?.Invoke(buf, r); } catch { } }
            }
        }

        // livelli: ogni sorgente ha la sua pipe, quindi niente righe mescolate
        class MeterState { public double rl = -90, pl = -90, rr = -90, pr = -90; public bool any; }
        readonly ConcurrentDictionary<string, MeterState> meters = new ConcurrentDictionary<string, MeterState>();

        void OnMeterLine(string id, string line)
        {
            var m = meters.GetOrAdd(id, _ => new MeterState());
            if (line.StartsWith("frame:"))
            {
                if (m.any)
                {
                    audioObserved[id] = signalClock.Elapsed.TotalSeconds;
                    sig.GetOrAdd(id, _ => new SigState()).detector.AddAudio(Math.Max(m.rl, m.rr), signalClock.Elapsed.TotalSeconds);
                    if (Math.Max(m.pl, m.pr) > -48 && Math.Max(m.rl, m.rr) > -60)
                        audioActivity[id] = signalClock.Elapsed.TotalSeconds;
                    AudioLevels?.Invoke(id, m.rl, m.pl, m.rr, m.pr);
                }
                m.rl = m.pl = m.rr = m.pr = -90; m.any = false;
                return;
            }
            if (!line.StartsWith("lavfi.astats.")) return;
            int eq = line.IndexOf('='); if (eq < 0) return;
            string key = line.Substring(13, eq - 13), v = line.Substring(eq + 1).Trim();
            double db = v == "-inf" || v == "nan" ? -90 : (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : -90);
            switch (key)
            {
                case "1.RMS_level": m.rl = db; m.any = true; break;
                case "2.RMS_level": m.rr = db; break;
                case "1.Peak_level": m.pl = db; m.any = true; break;
                case "2.Peak_level": m.pr = db; break;
            }
        }

        // fps reali della sorgente: conto i frame arrivati negli ultimi 2 secondi
        readonly Queue<DateTime> srcFrames = new Queue<DateTime>();
        /// <summary>Frame al secondo che la sorgente sta DAVVERO mandando (0 = sconosciuto).</summary>
        public double SourceFps { get; private set; }

        void OnFrameLine(string line)
        {
            if (!line.StartsWith("frame:")) return;
            SourceFrameTick();
        }

        void SourceFrameTick()
        {
            var now = DateTime.Now;
            lock (srcFrames)
            {
                srcFrames.Enqueue(now);
                while (srcFrames.Count > 0 && (now - srcFrames.Peek()).TotalSeconds > 2) srcFrames.Dequeue();
                if (srcFrames.Count > 1)
                {
                    double span = (now - srcFrames.Peek()).TotalSeconds;
                    if (span > 0.5) SourceFps = (srcFrames.Count - 1) / span;
                }
            }
        }

        class SigState
        {
            public readonly NoSignalDetector detector = new NoSignalDetector();
            public double lastEmit = -1;
        }
        readonly ConcurrentDictionary<string, SigState> sig = new ConcurrentDictionary<string, SigState>();
        readonly ConcurrentDictionary<string, double> audioActivity = new ConcurrentDictionary<string, double>();
        readonly ConcurrentDictionary<string, double> audioObserved = new ConcurrentDictionary<string, double>();
        readonly Stopwatch signalClock = Stopwatch.StartNew();

        /// <summary>
        /// Legge i fotogrammi 80×60 del ramo di analisi (prima del deinterlaccio, frequenza della sorgente) e li passa al
        /// rilevatore. La pipe va SEMPRE svuotata: se si fermasse, ffmpeg bloccherebbe anche anteprima e registrazione.
        /// </summary>
        void AnalysisLoop(NamedPipeServerStream pipe, string id)
        {
            try { pipe.WaitForConnection(); } catch { return; }
            var g = sig.GetOrAdd(id, _ => new SigState());
            var buf = new byte[NoSignalDetector.FrameBytes];
            while (true)
            {
                int got = 0;
                try { while (got < buf.Length) { int r = pipe.Read(buf, got, buf.Length - got); if (r <= 0) return; got += r; } }
                catch { return; }
                try
                {
                    SourceFrameTick();
                    double now = signalClock.Elapsed.TotalSeconds;
                    string state;
                    lock (g) state = g.detector.Observe(buf, now);
                    if (palMon.TryGetValue(id, out var pm))
                    {
                        bool flip; string why;
                        lock (pm) { flip = pm.Observe(buf); why = pm.LastReason; }
                        if (flip) TogglePal(id, false);
                        else if (pm.Decided && why.StartsWith("fase PAL giusta") && !pm.Logged) { pm.Logged = true; Log?.Invoke("Colore PAL: " + why); }
                    }
                    if (now - g.lastEmit >= .5)
                    {
                        g.lastEmit = now;
                        var d = g.detector;
                        string detail = $"{state} · sfondo {d.LastFraction:P0} (Y{d.LastBackground.y} U{d.LastBackground.u} V{d.LastBackground.v}) · movimento {d.LastMotion:P1} · audio {(d.LastAudioAlive ? "vivo" : "fermo")}";
                        SignalState?.Invoke(id, d.BlankSeconds(now), detail);
                    }
                }
                catch { }
            }
        }

        readonly EngineStats st = new EngineStats();
        void OnProgressLine(string line)
        {
            int ix = line.IndexOf('='); if (ix <= 0) return;
            string k = line.Substring(0, ix).Trim(), v = line.Substring(ix + 1).Trim();
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
                    Stats?.Invoke(new EngineStats { Fps = st.Fps, Frame = st.Frame, Drop = st.Drop, Dup = st.Dup, Speed = st.Speed, TotalSize = st.TotalSize });
                    break;
            }
        }

        // ---------- stderr: solo log + info ingressi ----------

        static readonly Regex rxStream = new Regex(@"Stream #(\d+):\d+.*?: Video: (\w+)");
        static readonly Regex rxSize = new Regex(@"[\s,](\d{2,5})x(\d{2,5})[\s,\[]");
        static readonly Regex rxRawFmt = new Regex(@"rawvideo \([^)]*\), (\w+)");
        static readonly Regex rxFps = new Regex(@"([\d.]+) fps");
        bool inInputSection;

        void OnErr(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string line = e.Data;
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
                    if (codec == "rawvideo") { var rf = rxRawFmt.Match(line); if (rf.Success) codec = rf.Groups[1].Value; }
                    inputSize[sid] = (w, h);
                    inputInfo[sid] = $"{w}×{h} · {(mf.Success ? mf.Groups[1].Value : "?")} fps · {codec}";
                }
            }
            if (line.Trim().Length == 0 || IsNoise(line)) return;
            Log?.Invoke(line);
        }

        static bool IsNoise(string l) =>
            l.StartsWith("  Metadata:") || l.StartsWith("    ") || l.StartsWith("  Side data") || l.Contains("Press [q] to stop") ||
            l.StartsWith("Stream mapping:") || l.StartsWith("  Stream #") && l.Contains("->") || l.StartsWith("  Duration:") ||
            l.Contains("-progress period set") || l.StartsWith("[Parsed_astats") || l.StartsWith("frame=") ||
            (l.Contains("[out#") && l.Contains("muxing overhead")) ||
            (l.StartsWith("[null @") && l.Contains("non monotonically increasing dts"));   // uscita fittizia dell'analisi: innocuo

        // ---------- costruzione grafo ----------

        public class PipeNames
        {
            public string Preview, Ts, Progress, Monitor;
            public Dictionary<string, string> Meters = new Dictionary<string, string>();
            public Dictionary<string, string> Analysis = new Dictionary<string, string>();   // rilevamento fine cassetta
            public Dictionary<string, string> FrameRate = new Dictionary<string, string>();  // fps reali della sorgente
            public static string Win(string n) => @"\\.\pipe\" + n;
            /// <summary>
            /// Percorso della pipe dentro l'opzione di un filtro. I backslash vanno escapati DUE volte
            /// (parser del grafo + parser delle opzioni): ogni \ diventa \\\\ → ffmpeg apre \\.\pipe\nome.
            /// (Le barre dritte //./pipe/ su Windows ffmpeg non le accetta.) Verificato su ffmpeg reale.
            /// </summary>
            public static string InFilter(string n) => Win(n).Replace("\\", "\\\\\\\\");
        }

        static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

        static string DeintFilter(string mode) => mode switch
        {
            "yadif" => "yadif=mode=send_frame",
            "yadif2x" => "yadif=mode=send_field",
            "bwdif" => "bwdif=mode=send_frame",
            "bwdif2x" => "bwdif=mode=send_field",
            // ricostruzione NTSC su VCR PAL: ogni campo diventa un'immagine intera, alta come il fotogramma (il ritaglio resta in righe del fotogramma)
            "fields" => FieldsFilter,
            _ => null,
        };

        /// <summary>
        /// Fotogrammi vuoti (Y=U=V=0, verdi a schermo) che il grabber manda quando impagina a 50 Hz un segnale a 60 Hz:
        /// si scartano e il filtro fps ripete il precedente. Il nero vero di una cassetta ha Y≈16, quindi resta.
        /// </summary>
        public const string DropEmptyFrames = "signalstats,metadata=mode=select:key=lavfi.signalstats.YAVG:value=4:function=greater";
        static string PreFilter(Source src) => src.IsNtscRebuild ? DropEmptyFrames : null;
        /// <summary>Campi separati senza supporre la parità (prima il superiore, misurato) e raddoppiati in altezza.</summary>
        public const string FieldsFilter = "setfield=tff,separatefields,scale=w=iw:h=ih*2";

        static string ScaleFlags(string f) => f switch
        {
            "bilinear" => "bilinear", "lanczos" => "lanczos", "area" => "area", "fast_bilinear" => "fast_bilinear", _ => "bicubic",
        };

        public static bool CanUseQsv(AppSettings s)
        {
            if (!s.IntelGpu || s.Encoder != "h264_qsv") return false;
            var visible = s.Sources.Where(x => x.Visible && IsUsable(x)).ToList();
            if (visible.Count != 1) return false;
            var src = visible[0];
            // Mantiene intatti correzioni colore e ritagli non allineati: per questi usa la pipeline CPU.
            return src.Type == SourceType.Capture && src.ColorIsNeutral &&
                src.W >= 2 && src.H >= 2 && src.X >= 0 && src.Y >= 0 &&
                (long)src.X + src.W <= s.CanvasW && (long)src.Y + src.H <= s.CanvasH &&
                s.CanvasW % 2 == 0 && s.CanvasH % 2 == 0 &&
                new[] { src.CropL, src.CropT, src.CropR, src.CropB }.All(v => v >= 0 && v % 2 == 0);
        }

        public static string BuildArgs(AppSettings s, int pw, int ph, int zmqPort, PipeNames pn,
            out Dictionary<int, string> map, bool useQsv = false, string qsvBackend = "d3d11va")
        {
            useQsv = useQsv && CanUseQsv(s);
            map = new Dictionary<int, string>();
            var sb = new StringBuilder();
            var graph = new StringBuilder();
            string fps = string.IsNullOrWhiteSpace(s.Fps) ? "25" : s.Fps;
            var visible = s.Sources.Where(x => x.Visible && IsUsable(x)).ToList();

            sb.Append($"-hide_banner -y -loglevel info -nostats -progress \"{PipeNames.Win(pn.Progress)}\" -stats_period 1 ");
            sb.Append($"-filter_complex_threads {Math.Clamp(Environment.ProcessorCount, 2, 8)} ");
            if (useQsv) sb.Append(qsvBackend == "dxva2"
                ? "-init_hw_device qsv=hw:hw,child_device_type=dxva2 -filter_hw_device hw "
                : "-init_hw_device d3d11va=igpu:,vendor_id=0x8086 -init_hw_device qsv=hw@igpu -filter_hw_device hw ");
            // PERCORSO VELOCE (caso normale: solo il grabber) → niente canvas nero + overlay a ogni fotogramma,
            // basta aggiungere le bande con pad. Misurato su ffmpeg 7: overlay ≈ 19% di un core, pad ≈ 4%.
            var usable = visible.Where(x => x.Type != SourceType.Capture || !string.IsNullOrWhiteSpace(x.VideoDevice)).ToList();
            var only = usable.Count == 1 && usable[0].Type == SourceType.Capture ? usable[0] : null;
            bool fast = only != null && only.X >= 0 && only.Y >= 0 && only.X + only.W <= s.CanvasW && only.Y + only.H <= s.CanvasH;
            FastPathId = fast ? only.Id : null;
            if (!fast) sb.Append($"-re -f lavfi -i color=c=black:s={s.CanvasW}x{s.CanvasH}:r={fps} ");

            var idx = new Dictionary<Source, int>();
            int n = fast ? 0 : 1;
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
                        sb.Append($"-re -loop 1 -framerate {fps} -i \"{src.ImagePath}\" ");
                        break;
                    case SourceType.Color:
                        sb.Append($"-re -f lavfi -i color=c={src.Color.Replace("#", "0x")}:s={Math.Max(2, src.W)}x{Math.Max(2, src.H)}:r={fps} ");
                        break;
                }
                idx[src] = n; map[n] = src.Id;
                n++;
            }

            string cur = "[0:v]";
            // indirizzo con ':' escapati due volte (grafo + opzione) → tcp://127.0.0.1:porta (verificato su ffmpeg)
            string zmqFilter = zmqPort > 0 ? $"zmq=b=tcp\\\\://127.0.0.1\\\\:{zmqPort}" : null;
            if (zmqFilter != null && !fast) { graph.Append($"[0:v]{zmqFilter}[base];"); cur = "[base]"; }

            int k = 0;
            var analysisLabels = new List<(string label, string pipe)>();
            foreach (var kv in idx)
            {
                var src = kv.Key; int i = kv.Value;
                var chain = new List<string>();
                string srcIn = $"[{i}:v]";
                if (src.Type == SourceType.Capture && src.IsPal60Software)
                {
                    // PAL-60 col colore rifatto: V rigirato riga per riga PRIMA di tutto (anche del ramo di analisi,
                    // che così vede i colori veri e serve a scegliere la fase)
                    // [pq{k}] immediato → analisi; [pqd{k}] ritardato → uscita. Senza ramo di analisi l'immediato va a nullsink.
                    graph.Append(PalSoftware.Filter(srcIn, $"[pq{k}]", src.Id, k.ToString(), $"[pqd{k}]"));
                    if (!pn.Analysis.ContainsKey(src.Id)) graph.Append($"[pq{k}]nullsink;");
                    srcIn = $"[pqd{k}]";
                }
                if (src.Type == SourceType.Capture)
                {
                    if (pn.Analysis.TryGetValue(src.Id, out var anPipe))
                    {
                        // ramo di analisi per la fine cassetta PRIMA del deinterlaccio: 25 fotogrammi al secondo invece di 50
                        // (per capire se lo schermo è uniforme non serve deinterlacciare). Sempre a piena velocità della sorgente:
                        // un'uscita decimata (es. 2 fps) resta indietro e ffmpeg 7 frena tutte le altre → anteprima a raffiche.
                        string pre0 = PreFilter(src);
                        if (src.IsPal60Software)
                        {
                            // l'analisi guarda il ramo immediato, l'uscita quello ritardato: nessuno split
                            graph.Append($"[pqd{k}]null[cs{k}];[pq{k}]null[an{k}];");
                        }
                        else graph.Append($"{srcIn}{(pre0 != null ? pre0 + "," : "")}split=2[cs{k}][an{k}];");
                        // 80×60 pixel yuv444p verso l'app (14 KB a fotogramma): il rilevatore guarda l'immagine, non statistiche riassuntive
                        graph.Append($"[an{k}]crop=w=iw-{src.CropL + src.CropR}:h=ih-{src.CropT + src.CropB}:x={src.CropL}:y={src.CropT}," +
                                     $"scale={NoSignalDetector.W}:{NoSignalDetector.H}:flags=area,format=yuv444p[ano{k}];");
                        analysisLabels.Add(($"[ano{k}]", anPipe));
                        i = -1;   // l'ingresso della catena ora è [cs{k}]
                    }
                    // senza ramo di analisi il filtro dei fotogrammi vuoti va in testa alla catena
                    if (i >= 0 && PreFilter(src) != null && !useQsv) chain.Add(PreFilter(src));
                    var d = DeintFilter(src.DeinterlaceMode);
                    if (d != null && !useQsv) chain.Add(d);
                    if (!useQsv) chain.Add($"crop@s{src.Id}=w=iw-{src.CropL + src.CropR}:h=ih-{src.CropT + src.CropB}:x={src.CropL}:y={src.CropT}:exact=1");
                    // correzione colore solo se serve: con i valori neutri eq e hue lavorano per niente a ogni fotogramma
                    if (!src.ColorIsNeutral)
                    {
                        chain.Add($"eq@s{src.Id}=brightness={F(src.Brightness)}:contrast={F(src.Contrast)}:saturation={F(src.Saturation)}:gamma={F(src.Gamma)}");
                        chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}");
                    }
                }
                else
                {
                    chain.Add("format=yuva420p");
                    chain.Add($"hue@s{src.Id}=h={F(src.Hue, "0.#")}:s={F(src.Saturation)}:b={F(src.Brightness * 10)}");
                }
                chain.Add($"scale@s{src.Id}=w={Math.Max(2, src.W)}:h={Math.Max(2, src.H)}:flags={ScaleFlags(src.ScaleFilter)}:eval=frame");
                string inLabel = i >= 0 ? srcIn : $"[cs{k}]";
                if (useQsv)
                {
                    chain.Clear();
                    if (zmqFilter != null) chain.Add(zmqFilter);
                    if (i >= 0 && PreFilter(src) != null) chain.Add(PreFilter(src));
                    // I grabber analogici spesso marcano progressivi i frame interlacciati.
                    // Come l'automatico Yadif senza metadati, si assume prima il campo superiore.
                    bool campi = src.DeinterlaceMode == "fields";
                    if (campi) chain.Add(FieldsFilter);   // su CPU prima dell'upload: è SD, costa poco; la GPU non deve deinterlacciare
                    else if (DeintFilter(src.DeinterlaceMode) != null) chain.Add("setfield=" + (s.IntelFieldOrder == "bff" ? "bff" : "tff"));
                    chain.Add("format=nv12");
                    chain.Add("hwupload=extra_hw_frames=64");
                    string deint = !campi && DeintFilter(src.DeinterlaceMode) != null
                        ? $"deinterlace=advanced:rate={(src.DeinterlaceMode.EndsWith("2x") ? "field" : "frame")}:" : "";
                    int qw = Math.Max(2, src.W & ~1), qh = Math.Max(2, src.H & ~1);
                    int qx = src.X & ~1, qy = src.Y & ~1;
                    chain.Add($"vpp_qsv={deint}cw=iw-{src.CropL + src.CropR}:ch=ih-{src.CropT + src.CropB}:cx={src.CropL}:cy={src.CropT}:w={qw}:h={qh}:format=nv12");
                    // NV12 è il formato della superficie; QSV forza la permanenza in memoria GPU.
                    chain.Add("format=qsv");
                    chain.Add("setsar=1");
                    graph.Append($"{inLabel}{string.Join(",", chain)},fps={fps}[qsrc];");
                    if (qw == s.CanvasW && qh == s.CanvasH && qx == 0 && qy == 0)
                        graph.Append($"[qsrc]null[t{k}];");
                    else
                    {
                        // Un solo fondo nero caricato in GPU e riutilizzato: niente upload del canvas a ogni frame.
                        graph.Append($"color=c=black:s={s.CanvasW}x{s.CanvasH}:r={fps},format=nv12,hwupload=extra_hw_frames=8,loop=loop=-1:size=1:start=0[qbg];");
                        graph.Append($"[qbg][qsrc]overlay_qsv=x={qx}:y={qy}:w={qw}:h={qh}:shortest=1,format=qsv,setsar=1[t{k}];");
                    }
                }
                else if (fast)
                {
                    // bande nere con pad: dimensioni e posizione pari (yuv420)
                    int w2 = Math.Max(2, src.W) & ~1, h2 = Math.Max(2, src.H) & ~1, x2 = Math.Max(0, src.X) & ~1, y2 = Math.Max(0, src.Y) & ~1;
                    if (x2 + w2 > s.CanvasW) x2 = (s.CanvasW - w2) & ~1;
                    if (y2 + h2 > s.CanvasH) y2 = (s.CanvasH - h2) & ~1;
                    chain[chain.Count - 1] = $"scale@s{src.Id}=w={w2}:h={h2}:flags={ScaleFlags(src.ScaleFilter)}";
                    if (zmqFilter != null) chain.Insert(0, zmqFilter);   // il controllo live (ritaglio, colore) resta
                    chain.Add($"pad=w={s.CanvasW}:h={s.CanvasH}:x={x2}:y={y2}:color=black");
                    chain.Add("format=yuv420p");
                    graph.Append($"{inLabel}{string.Join(",", chain)}[t{k}];");
                }
                else
                {
                    graph.Append($"{inLabel}{string.Join(",", chain)}[v{k}];");
                    graph.Append($"{cur}[v{k}]overlay@s{src.Id}=x={src.X}:y={src.Y}:eof_action=pass:format=yuv420[t{k}];");
                }
                cur = $"[t{k}]";
                k++;
            }

            // La pipeline rimane completa anche senza registrare: nessuna decimazione dell'anteprima.
            string pvFps = PreviewFps(fps);
            if (useQsv)
            {
                graph.Append($"{cur}split=2[venc][pv];");
                // Download SOLO del ramo di anteprima, già adattato al riquadro; encoder su QSV.
                graph.Append($"[pv]vpp_qsv=w={pw}:h={ph}:format=nv12,format=qsv,hwdownload,format=nv12,format=bgra[pvs];");
            }
            else graph.Append($"{cur}fps={fps},format=yuv420p,split=2[venc][pv];[pv]fps={pvFps},scale={pw}:{ph}:flags=fast_bilinear,format=bgra[pvs];");

            // audio: per sorgente → misuratore (pipe propria) + ramo di mix con muto → encoder (+ ascolto)
            var audioSrcs = idx.Keys.Where(x => x.HasAudio).ToList();
            bool hasAudio = audioSrcs.Count > 0;
            bool mon = hasAudio && pn.Monitor != null;
            var meterLabels = new List<string>();
            var mixLabels = new List<string>();
            int a = 0;
            foreach (var src in audioSrcs)
            {
                string off = src.AudioOffsetMs != 0 ? $"asetpts=PTS+{F(src.AudioOffsetMs / 1000.0, "0.000")}/TB," : "";
                if (src.IsPal60Software && PalSoftware.DelayFrames > 0) off = PalSoftware.AudioDelayFilter + "," + off;   // stesso ritardo del video
                string meterPipe = pn.Meters.TryGetValue(src.Id, out var mp) ? PipeNames.InFilter(mp) : "NUL";
                graph.Append($"[{idx[src]}:a]{off}aresample=48000:async=1,volume@a{src.Id}=volume={F(src.VolumeGain, "0.#####")},asplit=2[am{a}][ax{a}];");
                graph.Append($"[am{a}]asetnsamples=n=1600:p=0,astats=metadata=1:reset=1:measure_perchannel=RMS_level+Peak_level:measure_overall=none," +
                             $"ametadata=mode=print:direct=1:file={meterPipe}[amo{a}];");
                graph.Append($"[ax{a}]volume@m{src.Id}=volume={(src.Muted ? "0" : "1")}[amx{a}];");
                meterLabels.Add($"[amo{a}]"); mixLabels.Add($"[amx{a}]");
                a++;
            }
            if (hasAudio)
            {
                string amix;
                if (mixLabels.Count == 1) amix = mixLabels[0];
                else { graph.Append($"{string.Join("", mixLabels)}amix=inputs={mixLabels.Count}:duration=longest:normalize=0[amix];"); amix = "[amix]"; }
                if (mon) graph.Append($"{amix}asplit=2[aenc][amon];[amon]aformat=sample_fmts=s16:channel_layouts=stereo[amons];");
                else graph.Append($"{amix}anull[aenc];");
            }

            sb.Append($"-filter_complex \"{graph.ToString().TrimEnd(';')}\" ");

            // encoder sempre acceso → MPEG-TS sulla pipe (il registratore decide quando scrivere su file)
            sb.Append("-map \"[venc]\" ");
            if (hasAudio) sb.Append("-map \"[aenc]\" ");
            sb.Append(EncoderArgs(s, fps));
            if (useQsv) sb.Append("-pix_fmt qsv ");
            if (hasAudio)
            {
                sb.Append($"-c:a aac -b:a {s.AudioBitrate}k ");
                if (s.AudioMono) sb.Append("-ac 1 ");
            }
            sb.Append($"-f mpegts -muxdelay 0 -muxpreload 0 -flush_packets 1 \"{PipeNames.Win(pn.Ts)}\" ");

            sb.Append($"-map \"[pvs]\" -f rawvideo -flush_packets 1 \"{PipeNames.Win(pn.Preview)}\" ");
            foreach (var ml in meterLabels) sb.Append($"-map \"{ml}\" -f null NUL ");
            foreach (var al in analysisLabels) sb.Append($"-map \"{al.label}\" -f rawvideo \"{PipeNames.Win(al.pipe)}\" ");
            if (mon) sb.Append($"-map \"[amons]\" -f s16le -ar 48000 -ac 2 \"{PipeNames.Win(pn.Monitor)}\"");
            return sb.ToString();
        }

        /// <summary>Anteprima alla frequenza completa del canvas, anche con vecchie impostazioni salvate.</summary>
        static string PreviewFps(string fps) => string.IsNullOrWhiteSpace(fps) ? "25" : fps;

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
