# VHSCapture — cassette NTSC su videoregistratore PAL

Un videoregistratore PAL che legge una cassetta NTSC manda **525 righe a 60 Hz con il colore a 4,43 MHz** (PAL-60, oppure NTSC 4.43 su alcuni VCR vecchi). Con il grabber su NTSC_M l'immagine è intera ma in bianco e nero; con PAL_B è tagliata o sfarfalla. Prima lo standard del grabber si cambiava a mano da «Driver video…» e restava scollegato da risoluzione e fps: da lì il «miscuglio» NTSC + PAL.

- **Proprietà sorgente → Standard video**, due voci nuove: «NTSC su videoregistratore PAL — PAL-60» e «… — NTSC 4.43». Ingresso 720×480 a 29,97, Yadif 2x, registrazione 1080p a 59,94.
- **Standard nel grabber** (riga nuova): ogni preset imposta da solo anche la scheda «Decoder video» del driver (PAL B/G, NTSC M, PAL-60, NTSC 4.43…), prima di avviare ffmpeg. 4 s dopo la partenza ricontrolla, e se il driver all'apertura ha rimesso il vecchio standard lo corregge. Se il grabber non ha PAL-60 usa NTSC 4.43, e viceversa.
- Sotto la riga si vede cosa c'è adesso nel grabber: standard, **righe rilevate** (525 = NTSC/PAL-60, 625 = PAL; vanno lette col nastro in Play, perché il menu blu del VCR è sempre a 625), segnale agganciato e standard che il driver accetta. Le stesse informazioni finiscono nel Log a ogni avvio.
- Se le righe rilevate non tornano con lo standard scelto (625 con 720×480, o 525 con 720×576) compare un avviso con lo standard da provare.
- Se cambi lo standard dalla pagina del driver, VHSCapture si adegua invece di rimettere il suo al riavvio.
- Al primo avvio le sorgenti esistenti prendono lo standard del grabber che corrisponde alla loro risoluzione (720×576 → PAL B/G, 720×480 → NTSC M). «Non toccare» lascia il driver com'è, come prima.
- Se i colori vengono sbagliati o a strisce orizzontali con PAL-60, il VCR manda NTSC 4.43: scegli l'altra voce.
- **«NTSC su videoregistratore PAL — PAL-60 col colore rifatto»** (nuovo preset al posto di «PAL B/G ricostruito») è per i grabber che non decodificano il PAL a 60 Hz, come l'USB 2828x. Il grabber si apre in NTSC_M e poi riceve PAL_60. Il driver risponde NTSC_M, ma demodula il colore a 4,43 MHz come se fosse NTSC: la componente V esce col segno invertito una riga sì e una no. Misurato sui campioni del laboratorio: correlazione −0,97 tra righe vicine dello stesso campo, 120 fotogrammi su 120 in 4 s, quadro intero.
  - `PalPhase.cs` → `PalSoftware.Filter`: nel grafo di ffmpeg, prima di tutto il resto, V viene rigirato riga per riga con lo schema + − − + che si ripete ogni 2 fotogrammi. Poi si fa la media con la riga precedente dello stesso campo, come la linea di ritardo di un decoder PAL. Il geq lavora solo sulla crominanza, a piani separati. Uno `streamselect@pal<id>` sceglie tra V così com'è e V invertito, cambiabile dal vivo via zmq.
  - `PalPhaseMonitor` guarda le immagini 80×60 del ramo di analisi, che ora parte dopo la correzione, e fa due cose:
    - all'avvio sceglie la fase, perché nelle immagini naturali i colori stanno sull'asse arancio ↔ blu, mentre con la fase sbagliata finiscono su viola ↔ verde. Decide in circa 1,5 s;
    - se il grabber perde un fotogramma, il colore si rovescia di colpo: lo riconosce sull'immagine stessa e lo rigira.

    Provato sul campione vero: fase sbagliata corretta al fotogramma 44, fotogramma tolto a metà riconosciuto all'istante, nessun falso allarme su 5 riprese con colori già giusti.
  - **Correzione del colore 4 volte più leggera.** La prima versione usava `geq`, una formula per pixel: sul PC del laboratorio ffmpeg arrivava a 24 fotogrammi al secondo su 30, il buffer si riempiva («real-time buffer too full, frame dropped»), i fotogrammi persi spostavano la fase del colore (fotogrammi giusti e sbagliati alternati) e l'audio saltava. Ora si usano solo filtri a blocchi: `il` due volte per raggruppare le righe per Y mod 4, `negate` sulla metà centrale, `il` due volte per rimettere a posto, `negate` con `enable` per il rovesciamento a ogni fotogramma, `convolution` verticale come linea di ritardo e `lut` per il guadagno. Misurato su 2 core: 124 fps contro 29, con risultato uguale (differenza media 3,6/255).
  - Saturazione: in PAL_60 il grabber tira fuori la crominanza con ampiezza ridotta (|U| 26 contro 40, |V| 17 contro 23 rispetto a PAL_B). Il filtro applica un guadagno 1,5 (`PalSoftware.ChromaGain`): misurato dopo, |U| 38 e |V| 26, cioè come PAL_B. Il cursore «Saturazione» resta per rifinire.
  - Fase che si girava ogni tanto durante la cattura: il controllo ora 1) riconosce il rovesciamento anche con molto movimento, perché guarda U e V e non la luminanza; 2) lo conferma sul fotogramma dopo, così un disturbo di un solo fotogramma (drop-out) non conta; 3) dopo un cambio di scena, dove la sequenza del VCR può ripartire girata, rivaluta i colori di continuo e se restano viola/verdi per circa 0,4 s corregge.
  - Dal log del laboratorio: la fase veniva decisa prima del passaggio a PAL_60 (su immagini senza colore); un falso «rovesciamento di colpo» su una giunta restava 6 s; e alle 18:12:50 il controllo lento è scattato su una scena con viola/verde veri. Ora: il controllo riparte dopo il cambio a PAL_60; il rovesciamento di colpo vale solo se la scena è la stessa (luminanza e U correlate) e l'immagine nuova è davvero sull'asse viola/verde; il controllo lento scatta solo con indice > +0,25 per 1 s (la fase girata misura circa +0,45, le scene normali tra −0,1 e −0,5) e poi sta fermo 10 s. Limite: invertendo V un viola vero diventa blu, quindi a posteriori l'indice non distingue i due casi; se una scena viene corretta a torto, il tasto «Inverti colore» la rimette e ferma l'automatico per 60 s.
  - Bug corretto: dopo una nostra inversione, l'arrivo del nostro cambio veniva riconosciuto solo se l'immagine era viola (mai, visto che dopo è giusta); il controllo restava in attesa e si mangiava il primo rovesciamento vero. Ora il riconoscimento del nostro cambio non guarda l'indice; il rovesciamento vero, dopo, sì.
  - Tinta: sullo sfondo blu del lettore il colore esce ruotato di circa 15° verso il magenta (misurato dallo screenshot). Si corregge col cursore «Tonalità», dal vivo.
  - Fotogrammi persi dal grabber (log del laboratorio: «More than 1000 frames duplicated» in 5 minuti su un nastro brutto, con 100 inversioni in un'ora): ora davanti alla correzione c'è `fps=30000/1001`, che riempie i buchi di tempo con un duplicato. Il conteggio da cui dipende il segno di V resta allineato al tempo reale e la fase non si gira più a ogni fotogramma perso (verificato: indice −0,44 prima e dopo un fotogramma tolto, contro +0,45 senza). Dopo ogni inversione il controllo sta fermo 2 s.
  - Tasto **«⇄ Inverti colore»** nelle Proprietà, che vale anche in registrazione. Nel Log compaiono le righe «Colore PAL: …».
  - Deinterlaccio Yadif 2x (campi nell'ordine normale, prima il superiore), registrazione a 59,94 con 60 immagini diverse al secondo. Il grafo esatto generato dall'app è stato provato con ffmpeg sul campione, compreso il cambio dal vivo via zmq.
  - Serve «Modifiche delle sorgenti al volo (zmq)» attivo nelle Impostazioni, che è il predefinito; l'ffmpeg «essentials» ha zmq. Il geq costa CPU: su 2 core lenti va a circa 29 fotogrammi al secondo, sui PC del laboratorio ci sono più core.
- «NTSC M → PAL B/G dal vivo» (`NTSC_M>PAL_B`) resta selezionabile a mano in «Standard nel grabber». Dà colori giusti ma il grabber perde un fotogramma su tre, misurato.
- Correzione: scegliendo uno standard dal menu, ora si imposta anche il deinterlacciamento giusto per quello standard (prima veniva messo sempre Yadif 2x).
- Lo standard del grabber viene verificato dopo averlo impostato: se il driver non lo tiene si prova l'alternativa (PAL-60 ↔ NTSC 4.43). Nel Log e sotto «Standard nel grabber» compare «⚠ Il grabber non ha tenuto …» con lo standard da usare al suo posto.
- Fine cliente CRM: prima si dà il **nome della cassetta**, poi arrivano riconteggio e domande.

# VHSCapture 1.2.8 — avvio veloce e anteprima fluida

- **GPU Intel vecchie (es. HD Graphics 4600)**: se QuickSync via D3D11 fallisce ("Error creating a MFX session: -9"), l'app usa DXVA2 e **se lo ricorda** per quel PC. Prima riprovava D3D11 a ogni avvio e a ogni chiusura delle Impostazioni, perdendo circa 6 s ogni volta. "Verifica encoder" nelle Impostazioni fa riprovare D3D11 al prossimo avvio (utile dopo un aggiornamento dei driver).
- **Verifica encoder una volta per PC**: l'esito viene salvato e rifatto solo se cambia ffmpeg.exe o se la chiedi. Nel Log, gli encoder mancanti occupano una riga corta con il motivo (es. "driver NVIDIA troppo vecchio", "nessuna scheda AMD").
- **Anteprima**: disegno sempre col metodo rapido. Quando l'anteprima era più grande del riquadro, il metodo "bello" di Windows costava circa 27 ms a fotogramma: dal Log, 93 fotogrammi scartati in 5 s. Ora costa circa 3 ms. Il file registrato non cambia.

# VHSCapture 1.2.7 — testi che stanno nella finestra

- **Impostazioni** e **Proprietà sorgente**: note, suggerimenti e caselle di spunta vanno a capo secondo la larghezza reale della finestra, e si riadattano quando la ridimensioni. I suggerimenti stanno sotto il campo invece che in una terza colonna, i menu a tendina si aprono larghi quanto la voce più lunga, e le voci del Formato sono tornate corte.

# VHSCapture 1.2.6 — MP4 normale come OBS

- Formato predefinito tornato a **MP4 normale**, come OBS con "MPEG-4 (.mp4)": un indice unico scritto alla chiusura, quindi MPC-HC e gli altri lettori lo aprono subito. Al primo avvio viene impostato anche dove c'era il frammentato. Chi usa MKV resta su MKV. Il frammentato resta selezionabile in Impostazioni.

# VHSCapture 1.2.5 — allo stop si salva e basta

- **Il controllo audio non rilegge più il file**: la media e il picco vengono calcolati dal VU durante la registrazione. Prima, su un MP4 frammentato in rete, ffmpeg faceva circa un salto per ogni keyframe: 319 salti per 10 minuti, circa 5.700 per una cassetta di 3 ore, cioè minuti di attesa sul NAS.
- **Taglio della coda spento di default**, e spento una volta anche a chi l'aveva attivo: lo stop automatico dopo 120 s di sfondo resta, ma il file non viene toccato.
- Nel Log compare **"File chiuso in X s"** a ogni stop.

# VHSCapture 1.2.4 — si scrive dritto nella cartella scelta, come OBS

- "Cartella di rete: passa prima dal disco del PC" ora è **spenta di default**, e al primo avvio viene spenta anche a chi ce l'aveva. Il file si scrive direttamente nella cartella scelta e niente passa dal PC. A ogni registrazione il Log mostra il percorso completo.

# VHSCapture 1.2.3 — chiusura istantanea come OBS

- **MP4 frammentato predefinito** (come l'"MP4 ibrido" di OBS): il file si scrive a pezzi mentre registri, quindi allo stop non c'è niente da convertire e il file resta leggibile anche se salta la corrente. Chi aveva scelto "MKV sicuro" resta su MKV. Se un TV molto vecchio non legge il frammentato, in Impostazioni si sceglie "MP4 normale".
- **Taglio della coda istantaneo**: nel frammentato si accorcia il file all'inizio del pezzo giusto (`Mp4Tools.cs`, `Mp4Frag.TruncateAt`) invece di riscriverlo. Provato con ffmpeg: 60 s → 30,04 s in 15 ms, 1500/1500 fotogrammi, decodifica pulita. Con l'MP4 normale resta la riscrittura, ma senza `+faststart` (una passata invece di due).
- **Copia in rete a pezzi** (`NetworkMirror`): con "registra sul PC e sposta" il file sul NAS cresce insieme a quello locale, ogni 2 s. Allo stop si copiano solo l'ultimo pezzo e l'intestazione. Provato su file normale, accorciato e rifatto da capo: identici byte per byte, chiusura in 46–112 ms. Se la rete non risponde, la registrazione non ne risente e alla fine si ripiega sulla copia completa.
- **Registrazione immediata**: niente più "Preparazione" al clic su Registra. Il controllo che il file riceva video gira in background per 20 s.

# VHSCapture 1.2.2 — fine cassetta e registrazione su rete

## Fine cassetta: rilevatore nuovo, sui pixel
Il ramo di analisi manda all'app l'immagine rimpicciolita a 80×60 (yuv444p, 14 KB a fotogramma, prima del deinterlaccio) al posto delle statistiche `signalstats`. `NoSignalDetector.cs` la guarda così:
- **sfondo dominante**: almeno l'85% del quadro (bordi esclusi) dello stesso colore, con tolleranza per il rumore analogico. Vale per blu, nero, grigio, neve e schermate "nessun segnale", anche con scritte OSD;
- **movimento vero**: conta solo cambi netti e compatti (pixel vicini che cambiano insieme), rispetto al fotogramma precedente e a 1, 2 e 3 secondi prima. Il rumore e la neve non contano, una scritta che lampeggia nemmeno;
- **oggetti colorati**, anche piccoli (almeno 6 pixel su 80×60) = filmato. Le scritte OSD sono bianche o grigie;
- **audio vivo** (volume che sale e scende di almeno 12 dB, sopra −42 dB) = filmato. Fruscio costante e silenzio non bloccano lo stop.

Un disturbo fino a 1 s non azzera il conteggio. Lo stop scatta dopo **120 s di fila** di solo sfondo (impostabile da 60 a 900; i valori salvati sotto 60 diventano 120). Si arma dopo 10 s di filmato, e la pausa riparte da zero.

Provato su 14 clip generate con FFmpeg 7 attraverso la stessa catena di analisi dell'app, con rumore analogico, bordi scuri e righe sporche: 14 esiti corretti. Riconosce come fine cassetta blu con OSD, blu con OSD lampeggiante, nero con contatore, grigio "no signal", neve e neve a strisce con fruscio, dissolvenza al nero. Riconosce come filmato una scena in movimento con voce, una scena buia, un'inquadratura ferma, un oggetto colorato o bianco che si muove nel buio, e il nero con voci. 15 nuovi controlli in `tests/Program.cs`: 49 superati.

**Limite:** un tratto di filmato quasi tutto di un colore, fermo e muto per 120 s di fila resta indistinguibile dallo sfondo del lettore.

## Registrazione che non si interrompe più per la rete
- Tolto lo stop dopo 20 s di file che non cresce: ora c'è solo un avviso nel Log. Il video resta in memoria (buffer da circa 10 minuti invece di 2,5) e viene scritto appena la destinazione riparte. La registrazione si ferma solo per un errore vero (processo terminato o buffer davvero pieno).
- **Cartella di rete** (`\\server\...` o unità mappata): con l'opzione predefinita "registra sul PC e sposta alla fine" si scrive in `%LOCALAPPDATA%\VHSCapture\Da spostare`. Dopo taglio, rinomina e controllo audio il file viene copiato in rete, verificato e cancellato dal PC, con 3 tentativi. Se la rete non risponde il file resta sul PC, l'app lo dice, e al prossimo avvio propone di spostarlo.

## Nota sul workflow
Il `.github/workflows/build.yml` in questo pacchetto scarica l'ultima build "essentials" di gyan.dev e **non** esegue i test. Le sezioni qui sotto parlano di FFmpeg 8.0.1 fissato e di test nel workflow: nel file attuale quella parte non c'è.

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
