# VHSCapture 1.2.1 — sorgenti per GitHub

Questo pacchetto contiene esclusivamente il progetto, le risorse dell'interfaccia, i test e il workflow GitHub Actions. Non contiene eseguibili, FFmpeg, runtime, SDK o cartelle di compilazione. Caricare il contenuto della cartella VHSCapture-main nella radice del repository, compresa `.github`: il workflow compila Windows e prepara la release.

## Correzione del pacchetto FFmpeg per GTX 745

L'errore `Cannot load cuMemAllocAsync` arriva dal caricamento delle funzioni del driver, prima che NVENC possa codificare. Il driver Windows Update **431.07** segnalato dall'utente è troppo vecchio per la funzione richiesta. Su **GTX 745 e Windows 11**, installare il driver ufficiale NVIDIA **582.66**, quindi riavviare.

C'era anche un problema nella distribuzione: il workflow scaricava sempre l'ultimo FFmpeg. La build 9.0.2 verificata richiede per NVENC il driver **610.00**. Il driver 582.66 della GTX 745 non soddisfa quel requisito.

Il workflow ora usa la build fissa **FFmpeg 8.0.1 essentials**, con requisito NVENC **570.0**, e ne verifica il checksum SHA-256 prima di includerla nella release. Il driver 582.66 soddisfa quel requisito di versione. Aggiornare i sorgenti su GitHub e usare la release appena generata: ricompilare solo l'app lasciando accanto il vecchio FFmpeg 9.0.2 non applica questa correzione.

Dopo aver aggiornato driver e programma, aprire **Impostazioni → Registrazione → Verifica encoder** e scegliere NVIDIA NVENC quando l'esito è pronto. Il file `.github/ffmpeg-windows.json` descrive la dipendenza; la release contiene una sua copia come `ffmpeg-build.json`.

La verifica locale ha controllato archivio, checksum, versione e requisiti del binario Windows. La prova hardware sulla GTX 745 resta da eseguire sul PC interessato. Nessuna modifica ai filtri Intel, ai rilevatori del segnale o al registratore in questa correzione.

- [Driver NVIDIA 582.66: Windows 11 e GTX 745 inclusi](https://www.nvidia.com/Download/driverResults.aspx/272768/en-us/1000/)
- [Release FFmpeg 8.0.1 usata dal workflow](https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1)

## Rilevamento NVIDIA e scelta dell'encoder

NVIDIA NVENC ora resta nell'elenco con un esito esplicito: pronto oppure non disponibile. Il riquadro mostra la risposta della prova FFmpeg, oppure segnala che questo FFmpeg non include l'encoder. La presenza nella lista da sola non indica che una scheda NVIDIA sia stata rilevata o sia utilizzabile.

In **Impostazioni → Registrazione → Verifica encoder** si può ripetere la verifica senza riavviare il programma. Le prove avvengono in sequenza, fuori dal thread dell'interfaccia; un errore non rimane memorizzato per tutta la sessione. La preferenza automatica Intel non sostituisce più una scelta NVIDIA esplicita.

La modifica rende visibili e ripetibili gli esiti: non rende compatibili un driver e un FFmpeg che non lo sono. Una scelta nuova viene applicata solo dopo una prova riuscita. Il normale ritorno al percorso compatibile in caso di vero errore della pipeline resta presente. I filtri video Intel non sono convertiti in filtri NVIDIA: usare NVENC cambia l'encoder, non porta automaticamente tutti i filtri sulla GPU NVIDIA.

## Fine cassetta più prudente

Il rilevamento non usa la luminosità media per decidere se fermarsi. Controlla la struttura dell'immagine, compresi piccoli dettagli che prima sparivano nelle statistiche, le differenze tra fotogrammi e l'audio della sorgente. Blu, nero, grigio e altri colori vengono trattati nello stesso modo.

Prima occorrono 12 secondi continui di immagine quasi perfettamente piatta e ferma, senza audio rilevato. Solo allora può comparire il conto alla rovescia con l'attesa configurata: con 30 secondi impostati, il totale minimo è 42 secondi. Dettagli, movimento, audio o dati mancanti annullano la conferma. La conferma iniziale non viene mostrata come imminente arresto.

Lo stop si arma solo dopo circa 10 secondi di contenuto durante la registrazione. Attendere il Play su uno schermo vuoto non lo arma. Pausa e preparazione lo sospendono; se l'analisi si interrompe il conto alla rovescia viene annullato. Con più grabber visibili lo stop viene sospeso, perché l'analisi riguarda una sola sorgente. Se la sorgente ha audio ma i suoi livelli non sono disponibili, lo stop resta sospeso per prudenza.

Il ritaglio impostato viene applicato prima dell'analisi. L'eventuale taglio automatico della coda conserva i 12 secondi iniziali di conferma come margine, invece di tagliare all'inizio del primo nero sospetto.

**Limite reale:** questo grabber fornisce immagini, non un'indicazione affidabile di “nastro finito”. Un tratto di filmato completamente uniforme, immobile e silenzioso per tutto il tempo di attesa può essere identico al segnale di riposo. Il nuovo controllo preferisce continuare nei casi dubbi; schermate di riposo con scritte, rumore o animazioni possono quindi non fermare automaticamente la registrazione.

## Registrazione

- Il comando Registra attende un fotogramma completo nel pre-roll; mostra “Preparazione” fino alla conferma che FFmpeg sta scrivendo il file. Il grabber già acceso non viene riaperto.
- Conversione dell'intestazione AAC per il passaggio da MPEG-TS a MP4, senza ricodifica: corregge un errore di apertura del file riprodotto nei test.
- Controllo dei pacchetti PAT/PMT, anche con adaptation field, per evitare che dati malformati interrompano il lettore.
- Scrittura in blocchi e buffer limitato; se la destinazione si blocca o il processo termina, l'app segnala l'interruzione e chiude il file. Non continua a mostrare un REC apparentemente funzionante e non perde pacchetti in silenzio.
- Nomi con millisecondi e suffisso in caso di collisione; il muxer rifiuta di sovrascrivere file esistenti.
- Una sola chiusura per volta, ripristino dei pulsanti anche dopo un errore, file parziali conservati. Un file parziale non è garantito riproducibile, soprattutto con MP4 standard.

## Intel GPU senza modalità risparmio

Resta il percorso Intel della versione 1.1: H.264 QuickSync sempre acceso, anteprima a tutti i fotogrammi, canvas e qualità completi anche fuori registrazione. Nessuna modalità di risparmio e nessun dimezzamento della frequenza dell'anteprima.

Per un solo grabber visibile, dentro il canvas, con correzioni colore neutre e ritagli pari, `vpp_qsv` esegue deinterlaccio, ritaglio e ingrandimento; `overlay_qsv` compone le bande nere. Il ramo di registrazione resta su superfici QSV fino all'encoder. L'anteprima viene ridimensionata sulla GPU e scaricata per il disegno GDI.

Prima viene tentato D3D11, poi DXVA2 per i driver precedenti. Se entrambi falliscono, resta il ritorno automatico ai filtri CPU con qualità completa. Scene multiple, ritagli dispari e correzioni colore usano il percorso compatibile. Audio, analisi e disegno dell'interfaccia conservano lavoro CPU: non è il renderer Direct3D di OBS e non è promessa una percentuale di consumo.

## Prova sul PC del laboratorio

1. Verificare “filtri Intel GPU” nello stato, Intel QuickSync come encoder e circa 50 fps per PAL deinterlacciato 2x; per NTSC circa 59,94 fps. Non attivare modalità risparmio.
2. Registrare scene scure, sfondi blu con dettagli e passaggi in nero con audio: il conto alla rovescia non deve partire quando il controllo rileva contenuto.
3. Dopo almeno 10 secondi di immagini, lasciare il segnale di riposo del grabber: attendere 12 secondi di conferma più l'attesa impostata. Provare anche il grigio.
4. Provare avvio rapido, stop, una seconda registrazione e pausa/ripresa; verificare fluidità e sincronismo audio nel file.
5. Controllare l'ordine dei campi TFF/BFF se il movimento appare invertito.

La compilazione Windows e le prove sintetiche sono eseguite su macOS ARM. QuickSync, DirectShow, driver Intel di quarta generazione e segnale reale del laboratorio richiedono ancora la prova su quei PC. Dettagli e comandi riproducibili in VERIFICHE.md.
