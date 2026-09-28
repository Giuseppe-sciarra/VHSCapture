# VHSCapture

Registratore per riversaggio VHS/cassette/camere da grabber USB DirectShow, con canvas, sorgenti posizionabili, mixer e registrazione H.264/MP4.

La versione 1.1 aggiunge i filtri Intel QuickSync per deinterlacciamento, ingrandimento e bande nere, mantenendo la pipeline completa sempre attiva e l'anteprima a tutti i fotogrammi. L'auto-stop riconosce uno schermo uniforme di qualsiasi colore, compreso il grigio.

Sono conservati registrazione senza riapertura del grabber, pre-roll, pausa/ripresa, mixer, VU, ascolto, profili PAL/NTSC, crop, correzioni colore, opzioni MP4/MKV e divisione dei file. Le scene non compatibili con i filtri Intel usano il percorso software esistente.

- [Uso e prova sul laboratorio](LEGGIMI-AGGIORNAMENTO.md)
- [Verifiche e limiti](VERIFICHE.md)

## Build

.NET SDK 8 su Windows:

```text
dotnet run --project tests/PipelineChecks.csproj -c Release
dotnet publish VHSCapture/VHSCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Mettere ffmpeg.exe accanto all'eseguibile. Il workflow GitHub Actions esegue i controlli, pubblica il programma e include FFmpeg Gyan essentials.
