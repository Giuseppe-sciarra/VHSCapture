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

## Collegamento al CRM (1.3.0)

VHSCapture può lavorare con la Coda Lavorazioni del CRM Tastiere Digitali:

- **Collegamento**: nel CRM, *Controllo PC → la postazione → 🎬 VHSCapture → Genera il token*; in VHSCapture, *Impostazioni → CRM*: indirizzo del CRM, token, *Prova collegamento*.
- **Registra parte SUBITO**, nella cartella predefinita del PC. Se non c'è un cliente in corso (la prima volta, o quando hai finito le sue videocassette) **mentre registra** compare «Di chi è questa cassetta?», senza bloccare niente (F9/F10 funzionano): un clic sul nome e, a fine registrazione, i file vengono **spostati** (non copiati) nella cartella «Nome Cognome» dentro la cartella predefinita. Senza scelta entro 60 secondi la finestra si chiude e si resta nella cartella predefinita. Le cassette successive dello stesso cliente non chiedono niente e scrivono direttamente nella sua cartella. «Nessun cliente» vale finché non scegli un cliente dal pulsante 👤 in alto.
- **Riapertura**: VHSCapture si ricorda l'ultimo cliente anche se lo chiudi o spegni il PC. Al primo Registra, nella scelta del cliente compare in cima «▶ Continua con Mario Rossi — 3 di 10 fatte» (clic o Invio), ma solo se nel CRM ha ancora videocassette da fare (non finito, non consegnato, non tutte già in registrazione su altri PC).
- **Avanzamento** ben visibile sotto la barra dei pulsanti: nome del cliente, «Cassetta 4 di 10» in grande e una barra a blocchetti (verdi le fatte, rossa lampeggiante quella in registrazione).
- **Interruttore generale** in *Impostazioni → CRM → Usa il collegamento al CRM*: spento, VHSCapture registra come prima; indirizzo, token e opzioni restano salvati.
- **Cartella**: le registrazioni vanno in «Nome Cognome» dentro la cartella del PC (creata se manca). Il nome del file si chiede a fine registrazione come sempre.
- **Fine cassetta**: quattro scelte grandi — ✅ **Tieni** (Invio: si conta e il file resta, qualunque durata) · 🎁 **Tieni, ma non farla pagare** (il file resta, il totale del cliente scende: per le cassette di pochi minuti) · 🗑 **Scarta** (cassetta vuota: non si conta, il totale del cliente scende, il file si cancella) · 🔄 **Ricomincia la cassetta** (video da rifare, es. il videoregistratore ha fatto il test testine: file cancellato, non si conta, stessa cassetta, la registrazione riparte subito). **Partenza sbagliata**: se fermi entro 60 secondi (modificabile) non chiede niente — non si conta, il file breve si cancella e si riparte dalla stessa cassetta.
- **Conteggio unico**: le videocassette fatte sono lo stesso numero dell'Avanzamento del CRM; la fascia del cliente si riallinea ogni 20 secondi, quindi tutti i PC vedono lo stesso conteggio.
- **Si contano le videocassette della scheda** (campo «N. Videocassette»: VHS, S-VHS, VHS-C, 8mm, Hi8, Digital8, MiniDV, tutte dal grabber); DVD, CD, musicassette e gli altri supporti di «Riversaggio / Backup» si lavorano a parte ma compaiono nel totale del cliente.
- **Barra in basso**: cliente in corso e a che punto sono gli altri PC; nel CRM la Coda mostra «🔴 PC2 · Mario Rossi · cassetta 4/10 · 43:13».
- Ogni opzione si accende e si spegne da *Impostazioni → CRM* o dal CRM (vince l'ultima modifica) e resta salvata anche da spenta.
- **Senza rete la registrazione non si ferma mai**: gli aggiornamenti restano in coda (`%APPDATA%\VHSCapture\crm-coda.json`) e partono appena il CRM risponde; ogni evento ha un codice univoco, quindi non si conta mai due volte.
