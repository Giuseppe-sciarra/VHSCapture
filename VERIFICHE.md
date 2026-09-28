# Verifiche della consegna — 28 settembre 2026

## Compilazione e pacchetto

.NET SDK 8.0.425 su macOS ARM, destinazione net8.0-windows, pubblicazione win-x64 self-contained in file singolo, versione 1.1.0. Compilazione completata. Restano due avvisi già presenti nel sorgente originale: CS0675 nell'elaborazione dei timestamp e CS0067 sull'evento FrameReady non utilizzato.

Il pacchetto Windows contiene l'eseguibile compilato, FFmpeg Gyan 9.0.2 e relativa licenza. L'interfaccia Windows non è stata eseguita nell'ambiente di compilazione. Lo ZIP dei sorgenti esclude bin, obj, cache e SDK.

## Controlli automatici

48 controlli superati: 37 controlli di regressione in tests/PipelineChecks.csproj e 11 campioni generati da FFmpeg e passati al classificatore C# reale.

- Pipeline completa, H.264/AAC e pre-roll mantenuti anche fuori registrazione.
- Nessun dimezzamento dell'anteprima; vecchie preferenze di risparmio ignorate.
- Costruzione QSV: deinterlaccio a frequenza di campo, crop/scala, bande nere, fondo riusato e superfici QSV imposte fino all'encoder.
- Tentativo D3D11, passaggio a DXVA2 una sola volta e ripristino dello stato dei tentativi.
- TFF/BFF, input progressivo, PAL/NTSC, audio con/senza ascolto e assenza di audio.
- Percorsi compatibili per scene multiple, correzioni colore, ritagli dispari e sorgenti fuori canvas.
- Azzeramento di PAT/PMT, GOP e frammenti TS quando la pipeline viene riavviata.
- Rilevamento indipendente dal colore, rigetto di metadati incompleti/non validi e di immagini con variazioni di luminanza o crominanza.

I controlli QSV verificano il comando e la logica dei tentativi, non l'esecuzione su una GPU Intel.

## Prove video effettive

FFmpeg 7.1 macOS ARM, testsrc2 720×576 a 25 fps per 5 secondi e tono audio a 48 kHz. I filtri sono estratti dai comandi generati dal progetto; DirectShow e le named pipe vengono sostituiti con sorgenti sintetiche e file locali.

Due prove, con ascolto acceso e spento:

- file MP4 decodificabile a 1920×1080, base temporale 1/50 e traccia AAC;
- anteprima a 960×540, base temporale 1/50, senza dimezzamento dei fotogrammi;
- 249 frame in entrambi i rami dopo il drenaggio del deinterlaccio sul campione di 5 secondi;
- con ascolto acceso, PCM stereo di 5 secondi.

Per l'auto-stop, FFmpeg ha prodotto statistiche reali dopo crop e ridimensionamento a 64×36. Il classificatore dell'app ha riconosciuto come uniformi nero, grigio, bianco, blu, rosso, verde, giallo, ciano e magenta. Ha escluso testsrc2 e un'immagine a luminanza costante con dettagli cromatici. Il tempo di attesa, i 10 secondi iniziali di contenuto e l'esclusione durante la pausa restano nel codice originale.

## Da verificare sul PC del laboratorio

Intel QuickSync e i driver effettivi, in particolare sui modelli di quarta generazione, DirectShow, disegno WinForms, pausa/ripresa dal dispositivo, auto-stop con il rumore reale del grabber e sincronismo audio/video. Non sono stati misurati consumo CPU o frame persi su quel PC. Non è garantita una percentuale CPU specifica.

Un tentativo locale di allocare superfici hardware VideoToolbox non è riuscito nell'ambiente; non è contato come verifica GPU. Le prove software macOS non certificano l'esecuzione Windows né il percorso QSV.

## Riprodurre le verifiche

Con .NET SDK 8, Python 3 e FFmpeg:

```text
dotnet run --project tests/PipelineChecks.csproj -c Release -- verification
python tests/VerifyVideo.py --ffmpeg PERCORSO_FFMPEG --artifacts verification
dotnet run --project tests/PipelineChecks.csproj -c Release -- verification verification/signals.json
```

## Riferimenti tecnici

- [FFmpeg: dispositivi hardware](https://ffmpeg.org/ffmpeg-doc.html#Advanced-Video-options)
- [Sorgente ufficiale vpp_qsv](https://github.com/FFmpeg/FFmpeg/blob/n8.0/libavfilter/vf_vpp_qsv.c)
- [Sorgente ufficiale overlay_qsv](https://github.com/FFmpeg/FFmpeg/blob/n8.0/libavfilter/vf_overlay_qsv.c)
- [Intel: transizione Media SDK / oneVPL e runtime legacy](https://www.intel.com/content/www/us/en/docs/onevpl/upgrade-from-msdk/2023-1/transition-from-intel-r-media-sdk-to-intel-r.html)
- [Distribuzione Windows FFmpeg Gyan](https://www.gyan.dev/ffmpeg/builds/)
