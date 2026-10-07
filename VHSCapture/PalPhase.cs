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

        // U: media con la riga precedente dello stesso campo (Y-2). V: segno + − − + per righe del fotogramma, alternato a ogni
        // fotogramma (N), poi la stessa media: s(Y)·(V(Y) − V(Y−2))/2 perché s(Y−2) = −s(Y). Le prime 2 righe restano com'erano.
        const string UExpr = "if(lt(Y\\,2)\\,p(X\\,Y)\\,(p(X\\,Y)+p(X\\,Y-2))/2)";
        const string VExpr = "if(lt(Y\\,2)\\,p(X\\,Y)\\,128+(1-2*mod(floor(Y/2)+mod(Y\\,2)+N\\,2))*(p(X\\,Y)-p(X\\,Y-2))/2)";

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

        double priorSum, priorNorm; int priorFrames;
        public bool Decided { get; private set; }
        int expectFlip;               // fotogrammi in cui aspettarsi il rovesciamento chiesto da noi
        public string LastReason { get; private set; } = "";
        public int Toggles { get; private set; }
        /// <summary>Già scritto nel Log che la fase era giusta.</summary>
        public bool Logged { get; set; }

        /// <summary>Soglie (pubbliche per i test).</summary>
        public const int PriorMinFrames = 45, PriorMaxFrames = 300;
        public const double PriorStrong = 0.08;

        public void Reset()
        {
            havePrev = false; priorSum = priorNorm = 0; priorFrames = 0; Decided = false; expectFlip = 0;
        }

        /// <summary>Da chiamare quando si inverte V (automaticamente o col tasto): il prossimo rovesciamento è nostro.</summary>
        public void ExpectFlip() { expectFlip = 60; Toggles++; }

        /// <summary>Inversione col tasto: la scelta di chi guarda vale più della stima automatica.</summary>
        public void ManualToggle() { ExpectFlip(); Decided = true; LastReason = "invertito a mano"; }

        /// <summary>true = bisogna invertire V adesso (motivo in <see cref="LastReason"/>).</summary>
        public bool Observe(byte[] f)
        {
            if (f == null || f.Length < N * 3) return false;
            double syy = 0, spy = 0, spp = 0, svv = 0, svp = 0, sqp = 0, suu = 0, sup = 0, squ = 0;
            double my = 0, mpy = 0; int n = 0;
            double ps = 0, pn = 0;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int i = y * W + x;
                    my += f[i]; mpy += prevY[i]; n++;
                }
            my /= n; mpy /= n;
            for (int y = Y0; y < Y1; y++)
                for (int x = X0; x < X1; x++)
                {
                    int i = y * W + x;
                    double yy = f[i] - my, py = prevY[i] - mpy;
                    double u = f[N + i] - 128, v = f[2 * N + i] - 128;
                    syy += yy * yy; spp += py * py; spy += yy * py;
                    // correlazioni NON centrate per la crominanza: il rovesciamento cambia segno anche alla media
                    svv += v * v; sqp += prevV[i] * prevV[i]; svp += v * prevV[i];
                    suu += u * u; squ += prevU[i] * prevU[i]; sup += u * prevU[i];
                    double m = Math.Abs(u) + Math.Abs(v);
                    if (m > 6) { ps += u * v; pn += u * u + v * v; }
                }
            bool toggle = false;

            if (havePrev)
            {
                double cY = spy / Math.Sqrt(Math.Max(1e-9, syy * spp));
                double cV = svp / Math.Sqrt(Math.Max(1e-9, svv * sqp));
                double cU = sup / Math.Sqrt(Math.Max(1e-9, suu * squ));
                double eV = Math.Sqrt(Math.Min(svv, sqp) / n);
                if (cY > 0.8 && cU > 0.5 && cV < -0.5 && eV > 2.0)
                {
                    if (expectFlip > 0) expectFlip = 0;        // è il nostro cambio che è arrivato
                    else
                    {
                        toggle = true;
                        LastReason = $"colore rovesciato di colpo (fotogramma perso dal grabber, V {cV:+0.00;-0.00})";
                    }
                }
            }
            bool waiting = expectFlip > 0;   // durante la latenza del nostro cambio le immagini non contano per la stima
            if (expectFlip > 0) expectFlip--;

            if (!Decided && !toggle && !waiting)
            {
                priorSum += ps; priorNorm += pn; priorFrames++;
                double score = priorNorm > 0 ? priorSum / priorNorm : 0;
                bool enough = priorNorm > 20000;   // abbastanza colore visto
                if ((priorFrames >= PriorMinFrames && enough && Math.Abs(score) > PriorStrong) || priorFrames >= PriorMaxFrames)
                {
                    Decided = true;
                    if (score > 0)
                    {
                        toggle = true;
                        LastReason = $"colori sull'asse viola/verde (indice {score:+0.00;-0.00}): fase PAL girata";
                    }
                    else LastReason = $"fase PAL giusta (indice {score:+0.00;-0.00})";
                }
            }

            for (int i = 0; i < N; i++) { prevY[i] = f[i]; prevU[i] = f[N + i] - 128; prevV[i] = f[2 * N + i] - 128; }
            havePrev = true;
            return toggle;
        }
    }
}
