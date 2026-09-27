# VHSCapture

OBS ridotto all'osso per il riversaggio VHS/cassette da grabber USB (DirectShow).
Canvas a risoluzione/fps di uscita, sorgenti posizionabili e configurabili, ⏺ → MP4 sulla cartella di rete.

- **Canvas**: risoluzione e fps di uscita (es. 1920x1080 @ 25/50/60) = risoluzione del file
- **Sorgenti** (＋): dispositivo di cattura video (grabber USB), immagine (logo/sfondo), colore pieno
  - trascina/ridimensiona sull'anteprima (maniglie, snap ai bordi/centro, frecce per spostare, Shift = libero)
  - proprietà (doppio click / ⚙): dispositivo, risoluzione/fps d'ingresso, deinterlaccio, posizione/dimensione,
    Adatta / Riempi / Centra / 4:3 / 16:9, luminosità, contrasto, saturazione, gamma, tonalità, volume/muto
  - le modifiche si applicano al volo senza riavviare l'anteprima (filtro `zmq` di ffmpeg); aggiungere/togliere
    sorgenti o cambiare dispositivo riavvia il grafo
- **Mixer audio** per sorgente + VU meter
- **Uscita** (⚙): encoder x264 / NVENC / QuickSync / AMF, CBR-VBR-CRF, bitrate, AAC, cartella, prefisso,
  stop automatico a N minuti, modalità sicura MKV→MP4 (opzionale, off)
- Tema chiaro/scuro, impostazioni in `%APPDATA%\VHSCapture\settings.json`

Build automatica via GitHub Actions: ogni push su `main` crea la release con lo zip (exe + ffmpeg full di gyan.dev, che include zmq).
