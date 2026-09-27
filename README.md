# VHSCapture

Registratore minimale per riversaggio VHS/cassette da adattatore USB (DirectShow).
Anteprima → Registra → MP4 sulla cartella di rete. Niente altro.

- Dispositivo video/audio USB, risoluzione e framerate d'ingresso
- Encoder x264 / NVENC / QuickSync / AMF, CBR-VBR-CRF come OBS
- Deinterlaccio yadif, bitrate audio AAC, mono
- Registrazione sicura: MKV durante la cattura, remux in MP4 a fine (file mai corrotto)
- Stop automatico a N minuti, tema chiaro/scuro, impostazioni salvate in `%APPDATA%\VHSCapture\settings.json`

Build automatica via GitHub Actions: ogni push su `main` crea la release con lo zip (exe + ffmpeg).
