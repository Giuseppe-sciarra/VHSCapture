using System;
using System.Collections.Generic;

namespace VHSCapture
{
    /// <summary>
    /// Riconosce lo "sfondo" del lettore o della videocamera (schermo blu, nero, grigio, neve senza colore, schermata
    /// "nessun segnale" del grabber, anche con scritte OSD sopra) distinguendolo dal filmato vero.
    ///
    /// Lavora sui PIXEL di un'immagine rimpicciolita a 80×60 (yuv444p planare), non su statistiche riassuntive:
    ///  1. sfondo dominante: quanta parte del quadro ha lo stesso colore (lo sfondo ≥ 85% anche con OSD e bordi sporchi);
    ///  2. movimento vero: cambi netti e COMPATTI (pixel vicini che cambiano insieme), sia tra fotogrammi consecutivi sia
    ///     rispetto a 1 s prima (movimenti lenti). Il rumore e la neve cambiano pixel sparsi e non contano;
    ///  3. oggetti colorati: anche piccoli sono filmato (le scritte OSD di lettori e videocamere sono bianche/grigie);
    ///  4. audio "vivo": voci e musica salgono e scendono di volume; fruscio e silenzio no.
    /// Un'interruzione breve (riga di tracking, scritta che lampeggia) non azzera il conteggio: serve più di 1 s di contenuto.
    /// </summary>
    public sealed class NoSignalDetector
    {
        public const int W = 80, H = 60;
        public const int FrameBytes = W * H * 3;

        // area analizzata: si escludono i bordi, spesso scuri o sporchi nel segnale analogico
        const int X0 = 4, X1 = W - 4, Y0 = 3, Y1 = H - 3;
        const int Inner = (X1 - X0) * (Y1 - Y0);

        // soglie
        public const double UniformFraction = 0.85;     // ≥ 85% del quadro dello stesso colore
        public const double MaxMotionFraction = 0.01;   // ≤ 1% dei pixel con un cambio netto e compatto
        const int MinColoredPixels = 6;                 // oggetto colorato ≥ 6 pixel su 80×60 (≈ 0,15% del quadro) = filmato
        const int ColorDist = 24;                       // distanza di crominanza da sfondo e da grigio per dire "colorato"
        const int TolY = 12, TolC = 10;                 // tolleranza "stesso colore" (rumore analogico)
        const int MotionStep = 16;                      // cambio di luminanza considerato movimento vero
        const double NonUniformGrace = 1.0;             // secondi di contenuto necessari per azzerare
        const double DriftY = 14, DriftC = 12;          // lo sfondo cambia colore → si riparte (es. dissolvenza)

        readonly int[] hist = new int[32 * 16 * 16];
        readonly List<int> touched = new List<int>(512);
        readonly byte[] prevY = new byte[W * H];
        readonly bool[] mask = new bool[W * H];
        readonly sbyte[] sign = new sbyte[W * H];
        bool havePrev;
        // istantanee di ~1, 2 e 3 secondi fa: il movimento lento si vede, una scritta che lampeggia no (torna identica)
        readonly Queue<byte[]> snaps = new Queue<byte[]>();
        double lagT = double.NaN, lagMotion;

        double since = double.NaN, last = double.NaN, nonUniformSince = double.NaN;
        double refY, refU, refV;

        // audio: livelli RMS (dB) degli ultimi 10 s
        readonly Queue<(double t, double db)> audio = new Queue<(double, double)>();

        /// <summary>Ultima misura, per il Log di diagnostica.</summary>
        public double LastFraction { get; private set; }
        public double LastMotion { get; private set; }
        public bool LastAudioAlive { get; private set; }
        public int LastColored { get; private set; }
        public (int y, int u, int v) LastBackground { get; private set; }

        /// <summary>Da quanti secondi di fila si vede solo lo sfondo (0 = contenuto).</summary>
        public double BlankSeconds(double now) => double.IsNaN(since) ? 0 : Math.Max(0, now - since);

        /// <summary>Livello RMS dell'audio della sorgente (dB), chiamato a ogni misura del VU.</summary>
        public void AddAudio(double rmsDb, double now)
        {
            lock (audio)
            {
                audio.Enqueue((now, Math.Max(-90, Math.Min(0, rmsDb))));
                while (audio.Count > 0 && now - audio.Peek().t > 10) audio.Dequeue();
            }
        }

        /// <summary>
        /// Audio "vivo": nell'ultima finestra il volume sale e scende (P90−P10 ≥ 12 dB) e non è un filo (P90 > −42 dB).
        /// Il fruscio della neve è forte ma costante, lo schermo blu è muto: nessuno dei due blocca lo stop.
        /// </summary>
        public bool AudioAlive(double now)
        {
            lock (audio)
            {
                while (audio.Count > 0 && now - audio.Peek().t > 10) audio.Dequeue();
                if (audio.Count < 40) return false;
                var v = new double[audio.Count]; int i = 0;
                foreach (var a in audio) v[i++] = a.db;
                Array.Sort(v);
                double p10 = v[(int)(v.Length * 0.10)], p90 = v[(int)(v.Length * 0.90)];
                return p90 > -42 && p90 - p10 >= 12;
            }
        }

        /// <summary>Analizza un fotogramma 80×60 yuv444p. Ritorna "sfondo" o "contenuto".</summary>
        public string Observe(byte[] f, double now)
        {
            if (f == null || f.Length < FrameBytes || !double.IsFinite(now)) { Reset(); return "contenuto"; }
            // analisi interrotta (pipeline riavviata, grabber fermo): non conta come sfondo
            if (!double.IsNaN(last) && (now < last || now - last > 2)) { since = double.NaN; havePrev = false; snaps.Clear(); lagT = double.NaN; lagMotion = 0; }
            last = now;

            const int U0 = W * H, V0 = 2 * W * H;

            // 1) colore dominante: istogramma grossolano, poi media del gruppo più numeroso
            foreach (int b in touched) hist[b] = 0;
            touched.Clear();
            int best = -1, bestN = 0;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int p = y * W + x;
                    int b = ((f[p] >> 3) << 8) | ((f[U0 + p] >> 4) << 4) | (f[V0 + p] >> 4);
                    if (hist[b]++ == 0) touched.Add(b);
                    if (hist[b] > bestN) { bestN = hist[b]; best = b; }
                }
            long sy = 0, su = 0, sv = 0; int n = 0;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int p = y * W + x;
                    int b = ((f[p] >> 3) << 8) | ((f[U0 + p] >> 4) << 4) | (f[V0 + p] >> 4);
                    if (b != best) continue;
                    sy += f[p]; su += f[U0 + p]; sv += f[V0 + p]; n++;
                }
            int by = (int)(sy / Math.Max(1, n)), bu = (int)(su / Math.Max(1, n)), bv = (int)(sv / Math.Max(1, n));
            int same = 0;
            // pixel "colorati": crominanza lontana sia dallo sfondo sia dal grigio (le scritte OSD sono bianche/grigie)
            Array.Clear(mask, 0, mask.Length);
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int p = y * W + x;
                    int u = f[U0 + p], v = f[V0 + p];
                    if (Math.Abs(f[p] - by) <= TolY && Math.Abs(u - bu) <= TolC && Math.Abs(v - bv) <= TolC) same++;
                    else if (Math.Abs(u - bu) + Math.Abs(v - bv) > ColorDist && Math.Abs(u - 128) + Math.Abs(v - 128) > ColorDist) mask[p] = true;
                }
            int colored = Compact(mask, null);

            // movimento compatto rispetto al fotogramma precedente
            double motion = 0;
            bool motionKnown = havePrev;
            if (havePrev) motion = (double)ChangedCompact(f, prevY) / Inner;
            Buffer.BlockCopy(f, 0, prevY, 0, W * H);
            havePrev = true;
            // ... e rispetto a 1, 2 e 3 secondi prima: i movimenti lenti (meno di un pixel a fotogramma) si vedono solo così.
            // Si tiene la differenza MINORE: una scritta OSD che lampeggia torna identica almeno a uno dei tre istanti.
            if (double.IsNaN(lagT) || now - lagT >= 1.0)
            {
                if (snaps.Count > 0)
                {
                    int minChanged = int.MaxValue;
                    foreach (var old in snaps) minChanged = Math.Min(minChanged, ChangedCompact(f, old));
                    lagMotion = (double)minChanged / Inner;
                }
                var snap = snaps.Count >= 3 ? snaps.Dequeue() : new byte[W * H];
                Buffer.BlockCopy(f, 0, snap, 0, W * H);
                snaps.Enqueue(snap);
                lagT = now;
            }
            motion = Math.Max(motion, lagMotion);

            double frac = (double)same / Inner;
            if (!motionKnown) motion = 1;
            bool alive = AudioAlive(now);
            LastFraction = frac; LastMotion = motionKnown ? motion : 0; LastAudioAlive = alive; LastBackground = (by, bu, bv);
            LastColored = colored;

            bool uniform = frac >= UniformFraction && motionKnown && motion <= MaxMotionFraction && colored < MinColoredPixels && !alive;
            if (!uniform)
            {
                // audio vivo = contenuto subito; per l'immagine serve più di 1 s (righe di tracking, OSD che lampeggia)
                if (alive) { since = double.NaN; nonUniformSince = double.NaN; return "contenuto"; }
                if (double.IsNaN(nonUniformSince)) nonUniformSince = now;
                if (now - nonUniformSince > NonUniformGrace) since = double.NaN;
                return double.IsNaN(since) ? "contenuto" : "sfondo";
            }
            nonUniformSince = double.NaN;

            // lo sfondo deve restare lo stesso: se cambia colore (dissolvenza, cambio scena scura) si riparte
            if (double.IsNaN(since) || Math.Abs(by - refY) > DriftY || Math.Abs(bu - refU) > DriftC || Math.Abs(bv - refV) > DriftC)
            {
                since = now; refY = by; refU = bu; refV = bv;
            }
            return "sfondo";
        }

        /// <summary>
        /// Pixel con un cambio netto di luminanza che hanno almeno 2 vicini (su 4) cambiati nello stesso verso.
        /// Il rumore del nastro e la neve cambiano pixel isolati; un oggetto che si muove cambia zone compatte.
        /// </summary>
        int ChangedCompact(byte[] cur, byte[] reference)
        {
            Array.Clear(sign, 0, sign.Length);
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int p = y * W + x, dlt = cur[p] - reference[p];
                    sign[p] = (sbyte)(dlt > MotionStep ? 1 : dlt < -MotionStep ? -1 : 0);
                }
            int n = 0;
            for (int y = Y0 + 1; y < Y1 - 1; y++)
                for (int x = X0 + 1; x < X1 - 1; x++)
                {
                    int p = y * W + x; int sg = sign[p];
                    if (sg == 0) continue;
                    int nb = (sign[p - 1] == sg ? 1 : 0) + (sign[p + 1] == sg ? 1 : 0) + (sign[p - W] == sg ? 1 : 0) + (sign[p + W] == sg ? 1 : 0);
                    if (nb >= 2) n++;
                }
            return n;
        }

        /// <summary>Pixel della maschera con almeno 2 vicini (su 4) anch'essi nella maschera: niente puntini isolati.</summary>
        static int Compact(bool[] m, object _)
        {
            int n = 0;
            for (int y = Y0 + 1; y < Y1 - 1; y++)
                for (int x = X0 + 1; x < X1 - 1; x++)
                {
                    int p = y * W + x;
                    if (!m[p]) continue;
                    int nb = (m[p - 1] ? 1 : 0) + (m[p + 1] ? 1 : 0) + (m[p - W] ? 1 : 0) + (m[p + W] ? 1 : 0);
                    if (nb >= 2) n++;
                }
            return n;
        }

        public void Reset()
        {
            since = last = nonUniformSince = lagT = double.NaN;
            havePrev = false; snaps.Clear(); lagMotion = 0;
            lock (audio) audio.Clear();
        }
    }
}
