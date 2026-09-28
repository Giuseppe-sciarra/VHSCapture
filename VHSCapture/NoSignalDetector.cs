using System;

namespace VHSCapture
{
    // Non esiste un bit "nastro finito" nel video grezzo del grabber: nel dubbio si continua.
    // Valori signalstats a 8 bit, nell'ordine Keys. Si osserva OGNI frame, non solo i campioni UI.
    public sealed class NoSignalDetector
    {
        public static readonly string[] Keys = { "YLOW", "YHIGH", "ULOW", "UHIGH", "VLOW", "VHIGH",
            "YMIN", "YMAX", "UMIN", "UMAX", "VMIN", "VMAX", "YDIF", "UDIF", "VDIF" };
        public const double ConfirmationSeconds = 12;
        double since = double.NaN, last = double.NaN, anchorY, anchorU, anchorV;

        public string Observe(double[] v, double now, bool audioActive = false)
        {
            if (!double.IsFinite(now)) { Reset(); return ""; }
            if (!double.IsNaN(last) && (now <= last || now - last > 1.5)) since = double.NaN;
            last = now;
            if (v == null || v.Length != Keys.Length) { since = double.NaN; return ""; }
            foreach (double x in v)
                if (!double.IsFinite(x) || x < 0 || x > 255) { since = double.NaN; return ""; }
            for (int i = 0; i < 12; i += 2)
                if (v[i + 1] < v[i]) { since = double.NaN; return ""; }

            // Anche dettagli che occupano meno del 10% del quadro devono impedire lo stop.
            // Non c'è una soglia di luminosità: una scena scura con struttura è contenuto.
            bool flat = v[1] - v[0] <= 2 && v[3] - v[2] <= 2 && v[5] - v[4] <= 2 &&
                v[7] - v[6] <= 3 && v[9] - v[8] <= 3 && v[11] - v[10] <= 3;
            bool still = v[12] <= .05 && v[13] <= .05 && v[14] <= .05;
            if (!flat || !still || audioActive) { since = double.NaN; return "contenuto"; }
            if (double.IsNaN(since) || Math.Abs(v[0] - anchorY) > 1 ||
                Math.Abs(v[2] - anchorU) > 1 || Math.Abs(v[4] - anchorV) > 1)
            {
                since = now; anchorY = v[0]; anchorU = v[2]; anchorV = v[4];
            }
            return now - since >= ConfirmationSeconds ? "assenza probabile" : "verifica";
        }

        public void Reset() { since = last = double.NaN; }
    }
}
