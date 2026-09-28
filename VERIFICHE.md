# Verifiche dei sorgenti 1.2.1 — 28 settembre 2026

## Aggiornamento della dipendenza NVIDIA

È cambiato il workflow di confezionamento, non il codice C# verificato nella 1.2.1. La build Windows FFmpeg 8.0.1 essentials è stata scaricata dalla release del fornitore. Controllati: CRC dell'archivio, SHA-256 identico al digest pubblicato da GitHub, presenza di un solo ffmpeg.exe, stringa di versione 8.0.1 e requisito NVIDIA 570.0 nel binario. Il binario contiene i filtri/encoder usati dal progetto: vpp_qsv, overlay_qsv, signalstats, metadata, h264_qsv, h264_nvenc e aac_adtstoasc. La build 9.0.2 precedente contiene invece il requisito 610.00.

SHA-256 del download fissato: `e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673`.

Il workflow controlla hash e versione e poi esegue i test del progetto su Windows. Qui il nuovo eseguibile Windows non è stato avviato: controllare le stringhe e il pacchetto non equivale a una prova NVENC o QSV. Restano da verificare l'esecuzione di GitHub Actions e il test dopo aggiornamento del driver sul PC GTX 745.

## Esito

Compilazione Release `net8.0-windows` con .NET SDK 8.0.425 su macOS ARM: zero errori. Rimangono due avvisi preesistenti, CS0675 sui timestamp e CS0067 sull'evento FrameReady inutilizzato.

**116 controlli C# superati**, incluse 16 sequenze prodotte da FFmpeg e prove effettive del registratore con un processo FFmpeg. Tre prove video aggiuntive hanno verificato registrazione e anteprima contemporanee. La consegna contiene solo sorgenti, risorse, test e workflow; non è stata prodotta una nuova distribuzione Windows per questa consegna.

## Rilevamento encoder (1.2.1)

Dieci controlli aggiuntivi: scelta NVIDIA manuale preservata anche con Intel disponibile; una prova negativa non cancella la scelta esplicita; preferenza automatica Intel e scelta automatica NVIDIA; lista vuota; NVENC visibile anche senza supporto compilato; prova reale x264; diagnostica presente per tutti gli encoder; motivo del fallimento conservato; una rivalutazione esplicita crea nuovi risultati invece di riutilizzare la cache. Ripetute le prove del registratore e del rilevatore di segnale.

La build WinForms compila; l'interfaccia aggiornata non è stata eseguita su Windows. La GTX 745 dell'utente non è collegata all'ambiente: non è verificato che il suo driver apra NVENC. Il nuovo riquadro nelle impostazioni permette di leggere quell'esito sul PC interessato. La visibilità di NVENC nella lista non è una certificazione di hardware rilevato.

## Scene buie e rilevamento

Il filtro del progetto genera statistiche a 192×108 con tre componenti a 8 bit. L'analisi resta alla frequenza della sorgente; il rilevatore esamina ogni fotogramma e aggiorna l'interfaccia due volte al secondo. I valori minimi/massimi proteggono dettagli che sfuggono ai percentili; le differenze tra fotogrammi proteggono il movimento. La conferma richiede 12 secondi continui, prima dell'attesa configurata.

Test del rilevatore e del parser: attesa iniziale, ritorno del contenuto, dissolvenza lenta, valori non validi, metadati incompleti, interruzioni dell'analisi, audio attivo, silenzio misurato e livelli audio mancanti. Una sorgente con audio configurato ma non misurabile non viene considerata silenziosa.

FFmpeg 7.1 macOS ARM ha generato queste 16 sequenze, passate al classificatore C# dell'app:

- Riconosciuti dopo conferma: nero, grigio, bianco, blu, rosso, verde, giallo, ciano e magenta uniformi.
- Esclusi dallo stop: immagine testsrc2, dettagli cromatici a luminanza costante, testsrc2 molto scuro, piccolo dettaglio scuro su nero, piccolo pattern su blu, debolissima variazione temporale e rumore.

Le sequenze sintetiche non equivalgono a prove su tutte le cassette o grabber. Non dimostrano la capacità di distinguere un filmato perfettamente uniforme e silenzioso dal segnale di riposo, che può essere identico.

## Registratore effettivo

Un file MPEG-TS H.264/AAC sintetico di 8 secondi è stato alimentato al `TsRecorder` dell'app in blocchi da 7.913 byte, non allineati ai pacchetti TS. Il registratore ha creato i file tramite un processo FFmpeg reale.

Superati:

- lettura di PAT/PMT e pre-roll H.264, anche con adaptation field;
- 10.000 pacchetti PAT malformati senza eccezioni del lettore;
- avvio confermato dai messaggi di progresso del muxer e chiusura normale;
- pausa/ripresa con file decodificabile;
- MP4 frammentato, MP4 standard, MKV, file senza audio e divisione in più MP4 decodificabili (intervallo di 1 secondo nel test);
- errore reale di destinazione inesistente, chiusura dello stato e del processo, poi nuova registrazione funzionante;
- arresto forzato del processo rilevato e ripulito;
- rifiuto di sovrascrivere un file esistente, verificato confrontando il suo contenuto prima e dopo;
- saturazione del buffer simulata con segnalazione dell'errore, senza perdita silenziosa di pacchetti.

La prima prova sul codice senza la correzione audio ha riprodotto `Malformed AAC bitstream detected` durante il passaggio a MP4. Con `aac_adtstoasc` esplicito, le prove di registrazione e decodifica sono passate. Il filtro cambia il confezionamento AAC, senza ricodificare. [Documentazione FFmpeg](https://ffmpeg.org/ffmpeg-bitstream-filters.html#aac_005fadtstoasc).

## Pipeline video

Tre prove effettive con sorgenti sintetiche: ascolto acceso, ascolto spento, ascolto e nuova analisi accesi. Il grafo viene estratto dal comando del progetto; DirectShow e named pipe sono sostituiti con ingressi sintetici e file locali.

Per ogni prova: input 720×576 a 25 fps per 5 secondi; output MP4 H.264/AAC a 1920×1080 e anteprima 960×540, entrambi con 249 fotogrammi e base temporale 1/50. Con ascolto acceso, PCM stereo di 5 secondi. Nessun dimezzamento dell'anteprima quando si aggiunge l'analisi.

I controlli QSV verificano costruzione dei comandi, superfici hardware, crop/scala/bande, TFF/BFF, fallback D3D11 → DXVA2 e percorsi compatibili. **Non eseguono il grafo su una GPU Intel.**

## Limiti della verifica

Non eseguiti: interfaccia WinForms, DirectShow e named pipe su Windows; QuickSync sui PC Intel del laboratorio; consumo CPU reale; sincronismo A/V e fluidità con i grabber effettivi; prove prolungate su cassette reali o su un disco pieno. Il guasto del processo e la destinazione inesistente sono reali; il buffer pieno è simulato. I test di pausa/ripresa controllano la decodificabilità del file, non certificano il sincronismo percepito sul dispositivo.

Un file parziale viene conservato, ma la sua recuperabilità dipende dal formato e dal punto di interruzione. Nessuna percentuale CPU specifica è garantita.

## Riproduzione

Con .NET SDK 8, Python 3 e un eseguibile FFmpeg compatibile:

```text
dotnet run --project tests/PipelineChecks.csproj -c Release -- verification
python tests/VerifyVideo.py --ffmpeg PERCORSO_FFMPEG --artifacts verification
dotnet run --project tests/PipelineChecks.csproj -c Release -- verification verification/signals.json PERCORSO_FFMPEG
```

Il primo comando genera i grafi. Il secondo produce le sequenze e il flusso MPEG-TS. Il terzo esegue anche le prove con processi reali. I file generati restano nella cartella di verifica; il test copia o collega FFmpeg nella propria cartella `bin`. Il workflow GitHub Actions esegue questi passaggi prima della release Windows.

[Documentazione delle statistiche video FFmpeg](https://ffmpeg.org/ffmpeg-filters.html#signalstats).
