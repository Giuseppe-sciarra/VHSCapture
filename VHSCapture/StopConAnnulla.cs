using System;

namespace VHSCapture
{
    /// <summary>
    /// «Ferma / Interrompi» con ripensamento: il clic non ferma subito. Per qualche secondo (si sceglie nelle impostazioni) il pulsante
    /// diventa «↶ Annulla» con il conto alla rovescia: se lo premi il lavoro continua come se niente fosse,
    /// se non lo premi allo zero si ferma davvero. Gli stop automatici e la chiusura del programma
    /// non passano di qui: restano immediati come prima. Con 0 secondi si ferma subito, come prima.
    /// </summary>
    public sealed class StopConAnnulla : IDisposable
    {
        /// <summary>Secondi predefiniti per ripensarci (si cambiano nelle impostazioni).</summary>
        public const int Predefiniti = 5;
        /// <summary>Massimo accettato dalle impostazioni.</summary>
        public const int Massimo = 60;

        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        Action fermaDavvero;

        /// <summary>Secondi che mancano allo stop vero (vale solo mentre <see cref="InAttesa"/>).</summary>
        public int Restano { get; private set; }

        /// <summary>True dal clic su «Ferma» allo stop vero (o all'annullamento).</summary>
        public bool InAttesa => fermaDavvero != null;

        /// <summary>A ogni cambio (partito, ogni secondo, annullato, fermato): aggiorna pulsante e stato.</summary>
        public event Action Cambiato;

        public StopConAnnulla()
        {
            timer.Tick += (o, e) => Tick();
        }

        /// <summary>
        /// Primo clic su «Ferma»: parte il conto alla rovescia di <paramref name="secondi"/> secondi,
        /// allo zero esegue <paramref name="ferma"/>. Con 0 secondi la esegue subito (come prima).
        /// </summary>
        public void Avvia(int secondi, Action ferma)
        {
            secondi = Math.Min(secondi, Massimo);
            if (secondi <= 0) { Annulla(); ferma(); return; }
            fermaDavvero = ferma;
            Restano = secondi;
            timer.Stop();
            timer.Start();
            Cambiato?.Invoke();
        }

        /// <summary>Annulla lo stop in attesa. Ritorna true se ce n'era uno (false = niente da annullare).</summary>
        public bool Annulla()
        {
            timer.Stop();
            if (fermaDavvero == null) return false;
            fermaDavvero = null;
            Cambiato?.Invoke();
            return true;
        }

        void Tick()
        {
            if (fermaDavvero == null) { timer.Stop(); return; }
            Restano--;
            if (Restano > 0) { Cambiato?.Invoke(); return; }
            timer.Stop();
            var ferma = fermaDavvero;
            fermaDavvero = null;
            Cambiato?.Invoke();
            ferma();
        }

        public void Dispose()
        {
            fermaDavvero = null;
            timer.Stop();
            timer.Dispose();
        }
    }
}
