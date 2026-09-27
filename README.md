# VHSCapture

Registratore stile OBS ridotto all'osso per riversaggio VHS/cassette/camere da grabber USB (DirectShow).
Canvas, sorgenti posizionabili, anteprima fluida, ⏺ → MP4 sulla cartella di rete.

## Dall'OBS
- **Canvas** a risoluzione/fps di uscita (fino a 4K, 23.976–144 fps)
- **Sorgenti** (＋): grabber USB, immagine, colore. Trascina/ridimensiona, snap, frecce, **Alt+trascina = ritaglio**
- **Proprietà sorgente** con anteprima live: dispositivo, risoluzione/fps, **formato video (MJPEG/YUY2/NV12)**,
  modalità dichiarate dal dispositivo, **ingresso effettivo** letto da ffmpeg, **deinterlacciamento Yadif/Yadif 2x/Bwdif/Bwdif 2x**,
  **filtro di scala**, ritaglio, luminosità/contrasto/saturazione/gamma/tonalità, volume, **ritardo audio**
- Pagine di configurazione del **driver** (standard video, ingresso composito/S-Video)
- **Mixer** con VU **stereo L/R** (RMS + picco + peak hold) e **monitoraggio audio** (🎧 Ascolta)
- **Statistiche**: fps reali, frame persi/duplicati, CPU di ffmpeg, spazio libero e ore di registrazione residue
- Encoder **NVENC / AMF / QuickSync / x264** (solo quelli che funzionano su quel PC), CBR/VBR/CRF, keyframe ogni 2 s
- **MP4 frammentato** anti-crash (opzionale), **divisione automatica** dei file, priorità alta, **F9** registra/stop

## Motore
Un solo processo ffmpeg compone il canvas, scrive il file e manda anteprima (BGRA) e audio di ascolto
su named pipe con buffer grande; l'app disegna con StretchDIBits (GDI). Modifiche di posizione/ritaglio/colore/volume
al volo via filtro `zmq`. Build automatica via GitHub Actions (ffmpeg full di gyan.dev).
