using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VHSCapture
{
    /// <summary>
    /// MP4 frammentato (come l'"MP4 ibrido" di OBS): il file è una sequenza di pezzi indipendenti (moof+mdat), uno per keyframe.
    /// Per togliere la coda basta ACCORCIARE il file all'inizio del pezzo giusto: niente riscrittura, istantaneo anche su GB.
    /// </summary>
    public static class Mp4Frag
    {
        static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        static ulong U64(byte[] b, int o) => (ulong)U32(b, o) << 32 | U32(b, o + 4);

        /// <summary>Scorre i box figli in b[start..end): (tipo, inizio contenuto, fine).</summary>
        static System.Collections.Generic.IEnumerable<(string type, int body, int end)> Boxes(byte[] b, int start, int end)
        {
            int p = start;
            while (p + 8 <= end)
            {
                long size = U32(b, p); int hdr = 8;
                string type = Encoding.ASCII.GetString(b, p + 4, 4);
                if (size == 1 && p + 16 <= end) { size = (long)U64(b, p + 8); hdr = 16; }
                else if (size == 0) size = end - p;
                if (size < hdr || p + size > end) yield break;
                yield return (type, p + hdr, (int)(p + size));
                p += (int)size;
            }
        }

        /// <summary>Traccia video e sua scala temporale, dal moov iniziale (vuoto di campioni con empty_moov).</summary>
        static bool VideoTrack(byte[] moov, out uint trackId, out uint timescale)
        {
            trackId = 0; timescale = 0;
            foreach (var trak in Boxes(moov, 0, moov.Length))
            {
                if (trak.type != "trak") continue;
                uint id = 0, ts = 0; bool video = false;
                foreach (var c in Boxes(moov, trak.body, trak.end))
                {
                    if (c.type == "tkhd") id = moov[c.body] == 1 ? U32(moov, c.body + 4 + 16) : U32(moov, c.body + 4 + 8);
                    if (c.type != "mdia") continue;
                    foreach (var m in Boxes(moov, c.body, c.end))
                    {
                        if (m.type == "mdhd") ts = moov[m.body] == 1 ? U32(moov, m.body + 4 + 16) : U32(moov, m.body + 4 + 8);
                        if (m.type == "hdlr") video = Encoding.ASCII.GetString(moov, m.body + 8, 4) == "vide";
                    }
                }
                if (video && id > 0 && ts > 0) { trackId = id; timescale = ts; return true; }
            }
            return false;
        }

        /// <summary>
        /// Accorcia un MP4 frammentato al primo pezzo il cui video inizia a ≥ <paramref name="seconds"/> dall'inizio.
        /// false = file non frammentato o niente da tagliare (il chiamante può ripiegare sulla riscrittura).
        /// </summary>
        public static bool TruncateAt(string file, double seconds, Action<string> log = null)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                long len = fs.Length, pos = 0;
                uint vid = 0, scale = 0; bool haveFirst = false; ulong first = 0;
                var hdr = new byte[16];
                while (pos + 8 <= len)
                {
                    fs.Position = pos;
                    if (fs.Read(hdr, 0, 16) < 8) break;
                    long size = U32(hdr, 0); int h = 8;
                    string type = Encoding.ASCII.GetString(hdr, 4, 4);
                    if (size == 1) { size = (long)U64(hdr, 8); h = 16; } else if (size == 0) size = len - pos;
                    if (size < h || pos + size > len) break;   // pezzo finale incompleto: si taglia comunque prima
                    if ((type == "moov" || type == "moof") && size <= 64 << 20)
                    {
                        var b = new byte[size - h];
                        fs.Position = pos + h;
                        int got = 0; while (got < b.Length) { int r = fs.Read(b, got, b.Length - got); if (r <= 0) break; got += r; }
                        if (type == "moov" && !VideoTrack(b, out vid, out scale)) return false;
                        if (type == "moof" && vid != 0)
                        {
                            foreach (var traf in Boxes(b, 0, b.Length))
                            {
                                if (traf.type != "traf") continue;
                                uint tid = 0; ulong bmdt = 0; bool has = false;
                                foreach (var c in Boxes(b, traf.body, traf.end))
                                {
                                    if (c.type == "tfhd") tid = U32(b, c.body + 4);
                                    if (c.type == "tfdt") { bmdt = b[c.body] == 1 ? U64(b, c.body + 4) : U32(b, c.body + 4); has = true; }
                                }
                                if (tid != vid || !has) continue;
                                if (!haveFirst) { first = bmdt; haveFirst = true; }
                                double t = (bmdt - first) / (double)scale;
                                if (t >= seconds)
                                {
                                    fs.SetLength(pos);
                                    log?.Invoke($"Coda tagliata accorciando il file a {TimeSpan.FromSeconds(t):hh\\:mm\\:ss} (istantaneo)");
                                    return true;
                                }
                            }
                        }
                    }
                    else if (type == "moov") return false;
                    pos += size;
                }
                return false;
            }
            catch (Exception ex) { log?.Invoke("Taglio rapido non riuscito: " + ex.Message); return false; }
        }
    }

    /// <summary>
    /// Copia in rete A PEZZI mentre si registra sul PC: ogni 2 s aggiunge al file sul NAS i byte nuovi.
    /// Allo stop resta solo l'ultimo pezzetto (più l'intestazione, che l'MP4 normale aggiorna in chiusura).
    /// Se la rete si blocca, la registrazione non ne risente: si riprova al giro dopo.
    /// </summary>
    public sealed class NetworkMirror
    {
        string local;
        readonly string destDir, part;
        long mirrored;
        volatile bool stop;
        Thread thread;
        readonly object gate = new object();
        public string LastError { get; private set; }
        public long MirroredBytes => Interlocked.Read(ref mirrored);

        public NetworkMirror(string localFile, string destFolder)
        {
            local = localFile; destDir = destFolder;
            part = Path.Combine(destFolder, Path.GetFileName(localFile) + ".part");
        }

        public void Start()
        {
            thread = new Thread(() => { while (!stop) { Sync(false); for (int i = 0; i < 20 && !stop; i++) Thread.Sleep(100); } })
            { IsBackground = true, Name = "copia in rete", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        bool Sync(bool final)
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(local)) return !final;
                    if (!Directory.Exists(destDir)) { LastError = "cartella di rete non raggiungibile"; return false; }
                    using var src = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                    using var dst = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 20);
                    long n = src.Length;
                    if (dst.Length > n) dst.SetLength(n);                  // file accorciato (taglio della coda)
                    long from = Math.Min(Interlocked.Read(ref mirrored), Math.Min(n, dst.Length));
                    var buf = new byte[4 << 20];
                    if (final && from > 0) Copy(src, dst, 0, Math.Min(from, 1 << 20), buf);   // intestazione aggiornata in chiusura
                    Copy(src, dst, from, n, buf);
                    dst.Flush();
                    Interlocked.Exchange(ref mirrored, n);
                    LastError = null;
                    return true;
                }
                catch (Exception ex) { LastError = ex.Message; return false; }
            }
        }

        static void Copy(FileStream src, FileStream dst, long from, long to, byte[] buf)
        {
            src.Position = from; dst.Position = from;
            long left = to - from;
            while (left > 0)
            {
                int r = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (r <= 0) break;
                dst.Write(buf, 0, r); left -= r;
            }
        }

        /// <summary>
        /// Chiusura: il file locale definitivo (eventualmente rinominato; <paramref name="rewritten"/> = contenuto rifatto da capo,
        /// es. conversione MKV→MP4, quindi va ricopiato tutto). Ritorna il percorso in rete, o null (il file resta sul PC).
        /// </summary>
        public async Task<string> FinishAsync(string finalLocal, bool rewritten, Action<string> progress)
        {
            stop = true;
            try { thread?.Join(5000); } catch { }
            lock (gate) { local = finalLocal; if (rewritten) Interlocked.Exchange(ref mirrored, 0); }
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                progress?.Invoke(attempt == 1 ? "Completo la copia in rete…" : $"Copia in rete, tentativo {attempt}…");
                bool ok = await Task.Run(() => Sync(true));
                if (ok)
                {
                    try
                    {
                        long a = new FileInfo(finalLocal).Length, b = new FileInfo(part).Length;
                        if (a != b) { LastError = "dimensioni diverse dopo la copia"; Interlocked.Exchange(ref mirrored, 0); continue; }
                        string name = Path.GetFileName(finalLocal);
                        string target = Path.Combine(destDir, name);
                        for (int i = 1; File.Exists(target); i++)
                            target = Path.Combine(destDir, Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name));
                        File.Move(part, target);
                        File.Delete(finalLocal);
                        return target;
                    }
                    catch (Exception ex) { LastError = ex.Message; }
                }
                await Task.Delay(3000);
            }
            return null;
        }

        /// <summary>Rinuncia (es. registrazione divisa in parti): toglie il file parziale dalla rete.</summary>
        public void Abort()
        {
            stop = true;
            try { thread?.Join(3000); } catch { }
            try { lock (gate) if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }
}
