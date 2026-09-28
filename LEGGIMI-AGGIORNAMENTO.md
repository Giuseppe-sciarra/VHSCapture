# VHSCapture 1.1 — Intel GPU e auto-stop su colore uniforme

Estrarre il pacchetto Windows in una cartella, chiudere la vecchia versione e avviare VHSCapture.exe. Sono inclusi il runtime .NET e ffmpeg.exe Gyan 9.0.2. Le impostazioni precedenti vengono lette normalmente.

## Video

La pipeline rimane completa anche in anteprima: encoder H.264 sempre attivo, qualità e risoluzione del canvas conservate, tutti i fotogrammi visualizzati. Non è presente una modalità risparmio CPU. Le vecchie preferenze che dimezzavano i fotogrammi dell'anteprima vengono ignorate. Premendo Registra si aggancia il muxer senza riaprire il grabber; restano il pre-roll e la pausa/ripresa esistenti.

Con **Accelerazione GPU (Intel)** attiva, l'app preferisce **Intel QuickSync H.264**. Per un solo grabber visibile, contenuto nel canvas, con colori neutri e ritagli pari:

- `vpp_qsv`: deinterlacciamento, ritaglio e ingrandimento su Intel;
- `overlay_qsv`: composizione delle bande nere su GPU; il fondo viene caricato una volta e riutilizzato;
- il ramo di registrazione resta nella memoria GPU fino all'encoder H.264;
- l'anteprima viene adattata alla dimensione del riquadro sulla GPU e scaricata per il disegno GDI, mantenendo tutti i fotogrammi.

Il primo percorso usa Direct3D 11. Se non parte, prova QuickSync tramite DXVA2 per i driver meno recenti; se fallisce anche questo torna ai filtri CPU, mantenendo la qualità completa. Il percorso Intel richiede un driver con runtime QuickSync utilizzabile; la compatibilità su ogni modello dalla quarta generazione in avanti deve essere verificata sul PC effettivo. Non è garantita dalla sola generazione della CPU.

Scene multiple, correzioni colore, sorgenti fuori canvas e ritagli dispari mantengono la pipeline software esistente. L'encoder può comunque restare QuickSync. Le trasformazioni QSV richiedono un breve riavvio dell'anteprima; in registrazione quelle modifiche vengono rimandate. Audio, VU, controllo segnale e presentazione GDI conservano una quota di lavoro CPU: il motore non è il renderer Direct3D di OBS.

## Auto-stop

Riconosce un'immagine uniforme indipendentemente dal colore: blu, nero, grigio, bianco e gli altri colori pieni. Analizza la variazione spaziale di luminanza e crominanza, senza richiedere una particolare tonalità media. I metadati incompleti o non validi non vengono considerati uniformi.

Sono conservati il tempo di attesa impostato, l'attivazione dopo almeno 10 secondi di contenuto e l'esclusione durante la pausa. Anche il taglio della coda finale e le relative etichette ora si riferiscono alla parte uniforme. Un'immagine del filmato realmente uniforme per tutto il tempo configurato è indistinguibile da una schermata del grabber: scegliere una durata adeguata al materiale.

## Prova sul laboratorio

1. In **Avanzate**, lasciare accesa **Accelerazione GPU (Intel)**; in **Registrazione**, verificare **Intel QuickSync**. Non ci sono opzioni di risparmio da attivare.
2. Nello stato cercare **filtri Intel GPU** e, per VHS PAL con deinterlaccio 2x, circa 50 fps sia in uscita sia in anteprima. Per NTSC, circa 59,94 fps.
3. Registrare una breve sequenza in movimento, provare Pausa/Riprendi e una seconda registrazione. Verificare proporzioni, bande nere, fluidità e sincronismo audio.
4. Se il movimento va avanti/indietro, provare l'altro valore di **Ordine campi Intel**: TFF è il valore iniziale, BFF l'alternativa. Il deinterlaccio Intel non è identico a Yadif/Bwdif: la resa va verificata sul segnale reale.
5. Per l'auto-stop, far passare almeno 10 secondi di immagini, poi lasciare la schermata uniforme del grabber per il tempo impostato. Verificare in particolare il grigio e che i normali filmati continuino a registrare.
6. Per misurare CPU e fotogrammi, abilitare la diagnostica in Avanzate. Il Log indica anche D3D11, DXVA2 o il motivo del ritorno ai filtri CPU.

Compilazione Windows e controlli locali completati; QuickSync, driver e grabber del laboratorio non sono stati provati da questo ambiente macOS ARM. Non è quindi promessa una specifica percentuale CPU né l'equivalenza al 5–6% di OBS. Dettagli in VERIFICHE.md.
