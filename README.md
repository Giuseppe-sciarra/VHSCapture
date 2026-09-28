# VHSCapture

Registratore per riversaggio VHS/cassette/camere da grabber USB DirectShow, con canvas, sorgenti posizionabili, mixer e registrazione H.264/MP4.

La versione 1.2.1 mantiene i filtri Intel QuickSync e l'anteprima completa della 1.1. Corregge i falsi allarmi sulle scene buie con controlli di dettagli, movimento e audio, e rende più robusti avvio, scrittura e chiusura delle registrazioni. Nessuna modalità risparmio CPU. NVIDIA NVENC resta visibile con esito e motivo del rilevamento; il pulsante Verifica encoder ripete la prova e la scelta manuale non viene scavalcata dalla preferenza Intel.

Sono conservati registrazione senza riapertura del grabber, pre-roll, pausa/ripresa, mixer, VU, ascolto, profili PAL/NTSC, crop, correzioni colore, opzioni MP4/MKV e divisione dei file. Le scene non compatibili con i filtri Intel usano il percorso software esistente.

- [Uso e prova sul laboratorio](LEGGIMI-AGGIORNAMENTO.md)
- [Verifiche e limiti](VERIFICHE.md)

## Build

.NET SDK 8 su Windows:

```text
dotnet run --project tests/PipelineChecks.csproj -c Release
dotnet publish VHSCapture/VHSCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Mettere ffmpeg.exe accanto all'eseguibile. Il workflow GitHub Actions esegue i controlli, pubblica il programma e include FFmpeg Gyan essentials 8.0.1, fissato e verificato tramite SHA-256 in `.github/ffmpeg-windows.json`. Per GTX 745 su Windows 11 aggiornare il driver Windows Update 431.07 al driver NVIDIA ufficiale 582.66. La dipendenza fissa evita il requisito NVIDIA 610 introdotto dalla build FFmpeg più recente.
