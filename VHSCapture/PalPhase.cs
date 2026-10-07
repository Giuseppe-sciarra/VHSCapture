using System;

namespace VHSCapture
{
    /// <summary>
    /// PAL-60 con il colore rifatto da VHSCapture (cassette NTSC lette da un videoregistratore PAL, grabber come l'USB 2828x
    /// che non decodifica il PAL a 60 Hz). Il grabber, impostato su PAL_60, demodula il colore a 4,43 MHz ma come se fosse
    /// NTSC: la componente V esce col segno invertito una riga sì e una no (misurato: correlazione −0,97 tra righe vicine
    /// dello stesso campo). Il filtro di ffmpeg (<see cref="Filter"/>) rigira V riga per riga, con lo schema + − − + che si ripete
    /// ogni 2 fotogrammi, e fa la media con la riga precedente dello stesso campo (la "linea di ritardo" di un decoder PAL).
    ///
    /// Resta da sapere da quale fotogramma della sequenza è partita la cattura: se è quello sbagliato, tutto il colore
    /// ha V rovesciato (viola/verde invece di blu/arancio). Lo decide <see cref="PalPhaseMonitor"/> e lo corregge
    /// dal vivo scambiando l'uscita di uno streamselect (V normale / V invertito) via zmq.
    /// </summary>
    public static class PalSoftware
    {
        /// <summary>Chiave dello standard del grabber: si scrive PAL_60 senza pretendere che il driver lo confermi (l'USB 2828x
        /// risponde NTSC_M ma il colore a 4,43 MHz resta attivo) e senza ripiegare su NTSC 4.43.</summary>
        public const string TvKey = "PAL_60_SW";

        /// <summary>Guadagno della crominanza: in PAL_60 il grabber tira fuori U e V con ampiezza ridotta (misurato sui campioni:
        /// |U| 26 contro 40 e |V| 17 contro 23 rispetto a PAL_B). 1,5 riporta la saturazione a quella di PAL_B; si rifinisce con «Saturazione».</summary>
        public const double ChromaGain = 1.5;
        static readonly string G = ChromaGain.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
        // U: media con la riga precedente dello stesso campo (Y-2). V: segno + − − + per righe del fotogramma, alternato a ogni
        // fotogramma (N), poi la stessa media: s(Y)·(V(Y) − V(Y−2))/2 perché s(Y−2) = −s(Y). Le prime 2 righe restano com'erano.
        static readonly string UExpr = "if(lt(Y\\,2)\\,p(X\\,Y)\\,clip(128+" + G + "*((p(X\\,Y)+p(X\\,Y-2))/2-128)\\,16\\,240))";
        static readonly string VExpr = "if(lt(Y\\,2)\\,p(X\\,Y)\\,clip(128+" + G + "*(1-2*mod(floor(Y/2)+mod(Y\\,2)+N\\,2))*(p(X\\,Y)-p(X\\,Y-2))/2\\,16\\,240))";

        /// <summary>
        /// Pezzo di grafo: dall'ingresso <paramref name="input"/> all'etichetta <paramref name="output"/>, con lo streamselect
        /// che si chiama streamselect@pal{id} (map 0 = come calcolato, map 1 = V invertito).
        /// I piani si lavorano separati: il geq calcola solo la crominanza (metà dei punti), ~2× più veloce.
        /// </summary>
        public static string Filter(string input, string output, string id, string tag)
        {
            string t = "pal" + tag;
            return $"{input}format=yuv422p,split=3[{t}y0][{t}u0][{t}v0];" +
                   $"[{t}y0]extractplanes=y[{t}y];" +
                   $"[{t}u0]extractplanes=u,geq=lum='{UExpr}'[{t}u];" +
                   $"[{t}v0]extractplanes=v,geq=lum='{VExpr}'[{t}v];" +
                   $"[{t}y][{t}u][{t}v]mergeplanes=0x001020:yuv422p,split=2[{t}a][{t}b];" +
                   $"[{t}b]lutyuv=v=negval[{t}c];" +
                   $"[{t}a][{t}c]streamselect@pal{id}=inputs=2:map=0{output};";
        }

        public static string SelectCommand(string id, int map) => $"streamselect@pal{id} map {map}";
    }

    /// <summary>
    /// Guarda le immagini 80×60 (yuv444p) del ramo di analisi, prese DOPO la correzione del colore, e dice quando
    /// invertire V:
    ///  1. all'avvio: nelle immagini naturali i colori stanno sull'asse arancio ↔ blu (luce calda / luce fredda, pelle / cielo),
    ///     cioè U e V di segno opposto; con V rovesciato finiscono su viola ↔ verde. Si accumula per circa 1,5–10 s;
    ///  2. durante la cattura: se il grabber perde un fotogramma lo schema + − − + slitta e il colore si rovescia di colpo.
    ///     Si riconosce perché tra due immagini consecutive quasi uguali (luminanza e U correlate) V cambia segno.
    /// Dopo un'inversione chiesta da noi, il primo rovesciamento che arriva è il nostro (latenza dello zmq) e non conta.
    /// </summary>
    public sealed class PalPhaseMonitor
    {
        const int W = NoSignalDetector.W, H = NoSignalDetector.H, N = W * H;
        const int X0 = 4, X1 = W - 4, Y0 = 3, Y1 = H - 3;

        readonly float[] prevY = new float[N], prevU = new float[N], prevV = new float[N];
        bool havePrev;
        // rovesciamento visto ma non ancora confermato dal fotogramma dopo (un disturbo di un solo fotogramma non deve contare)
        bool pendingFlip; float[] pendV = new float[N];

        double priorSum, priorNorm; int priorFrames;
        public bool Decided { get; private set; }
        int expectFlip;               // fotogrammi in cui aspettarsi il rovesciamento chiesto da noi
        public string LastReason { get; private set; } = "";
        public int Toggles { get; private set; }
        /// <summary>Già scritto nel Log che la fase era giusta.</summary>
        public bool Logged { get; set; }

        // stima continua: media mobile dell'indice U·V (negativo = arancio/blu = giusto); se resta positiva a lungo si gira
        double ema; int emaBad;
        public const int EmaConfirm = 30;        // ~1 s di colori "viola/verde" di fila
        public const double EmaStrong = 0.10;

        /// <summary>Soglie (pubbliche per i test).</summary>
        public const int PriorMinFrames = 45, PriorMaxFrames = 300;
        public const double PriorStrong = 0.08;

        public void Reset()
        {
            havePrev = false; priorSum = priorNorm = 0; priorFrames = 0; Decided = false; expectFlip = 0; pendingFlip = false; ema = 0; emaBad = 0;
        }

        /// <summary>Da chiamare quando si inverte V (automaticamente o col tasto): il prossimo rovesciamento è nostro.</summary>
        public void ExpectFlip() { expectFlip = 60; Toggles++; pendingFlip = false; ema = 0; emaBad = 0; }

        /// <summary>Inversione col tasto: la scelta di chi guarda vale più della stima automatica.</summary>
        public void ManualToggle() { ExpectFlip(); Decided = true; LastReason = "invertito a mano"; }

        static double Corr(double sxy, double sxx, double syy) => sxy / Math.Sqrt(Math.Max(1e-9, sxx * syy));

        /// <summary>true = bisogna invertire V adesso (motivo in <see cref="LastReason"/>).</summary>
        public bool Observe(byte[] f)
        {
            if (f == null || f.Length < N * 3) return false;
            double syy = 0, spy = 0, spp = 0, svv = 0, svp = 0, sqp = 0, suu = 0, sup = 0, squ = 0, svd = 0, sdd = 0;
            double my = 0, mpy = 0; int n = 0;
            double ps = 0, pn = 0;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++) { int i = y * W + x; my += f[i]; mpy += prevY[i]; n++; }
            my /= n; mpy /= n;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int i = y * W + x;
                    double yy = f[i] - my, py = prevY[i] - mpy;
                    double u = f[N + i] - 128, v = f[2 * N + i] - 128;
                    syy += yy * yy; spp += py * py; spy += yy * py;
                    svv += v * v; sqp += prevV[i] * prevV[i]; svp += v * prevV[i];
                    suu += u * u; squ += prevU[i] * prevU[i]; sup += u * prevU[i];
                    svd += v * pendV[i]; sdd += pendV[i] * pendV[i];
                    double m = Math.Abs(u) + Math.Abs(v);
                    if (m > 6) { ps += u * v; pn += u * u + v * v; }
                }
            bool toggle = false;
            bool cut = false;

            if (havePrev)
            {
                double cY = Corr(spy, syy, spp), cV = Corr(svp, svv, sqp), cU = Corr(sup, suu, squ);
                double eV = Math.Sqrt(Math.Min(svv, sqp) / n);
                cut = cY < 0.3 && cU < 0.3;   // cambio di scena: luminanza e U non hanno più niente in comune
                // rovesciamento: U resta uguale (stessa scena, anche con movimento) ma V cambia segno in blocco
                bool flipNow = cU > 0.5 && cV < -0.5 && eV > 2.0 && cY > -0.2;
                if (pendingFlip)
                {
                    // confermato solo se anche questo fotogramma sta dalla parte "nuova" (V concorde col fotogramma sospetto)
                    double cPend = Corr(svd, svv, sdd);
                    if (cPend > 0.5)
                    {
                        if (expectFlip > 0) expectFlip = 0;        // è il nostro cambio che è arrivato
                        else { toggle = true; LastReason = "colore rovesciato di colpo (fotogramma perso dal grabber o giunta del nastro)"; }
                    }
                    pendingFlip = false;
                }
                else if (flipNow)
                {
                    pendingFlip = true;
                    for (int i = 0; i < N; i++) pendV[i] = f[2 * N + i] - 128;
                }
            }
            bool waiting = expectFlip > 0;   // durante la latenza del nostro cambio le immagini non contano per la stima
            if (expectFlip > 0) expectFlip--;

            if (cut) { ema = 0; emaBad = 0; }  // dopo una giunta la sequenza del VCR può ripartire girata: si rivaluta da capo

            if (!toggle && !waiting && !pendingFlip)
            {
                double frameIdx = pn > 0 ? ps / pn : 0;
                bool colored = pn / n > 40;     // abbastanza colore in questa immagine per dire qualcosa
                if (!Decided)
                {
                    priorSum += ps; priorNorm += pn; priorFrames++;
                    double score = priorNorm > 0 ? priorSum / priorNorm : 0;
                    bool enough = priorNorm > 20000;
                    if ((priorFrames >= PriorMinFrames && enough && Math.Abs(score) > PriorStrong) || priorFrames >= PriorMaxFrames)
                    {
                        Decided = true;
                        if (score > 0) { toggle = true; LastReason = $"colori sull'asse viola/verde (indice {score:+0.00;-0.00}): fase PAL girata"; }
                        else LastReason = $"fase PAL giusta (indice {score:+0.00;-0.00})";
                    }
                }
                else if (colored)
                {
                    ema = 0.9 * ema + 0.1 * frameIdx;
                    if (ema > EmaStrong) emaBad++; else emaBad = 0;
                    if (emaBad >= EmaConfirm) { toggle = true; emaBad = 0; LastReason = $"colori tornati viola/verdi da 1 s (indice {ema:+0.00;-0.00})"; }
                }
            }

            for (int i = 0; i < N; i++) { prevY[i] = f[i]; prevU[i] = f[N + i] - 128; prevV[i] = f[2 * N + i] - 128; }
            havePrev = true;
            return toggle;
        }
    }
}
