# Livelli audio per i test del rilevatore di fine cassetta

Ogni file è la sequenza dei livelli RMS (dB, il canale più alto) che VHSCapture misura sull'audio della sorgente:
stessa catena dell'app, `asetnsamples=n=1600:p=0,astats=metadata=1:reset=1:measure_perchannel=RMS_level+Peak_level`,
cioè una misura ogni 33 ms a 48 kHz. 40 secondi per file.

- `vivo_*` = audio che deve far considerare la ripresa **filmato** anche se l'immagine è nera (riprese al buio):
  parlato vicino, parlato lontano (−45 dB), sussurri (−58 dB), musica compressa forte, musica bassa sotto il
  fruscio, brusio di una festa (tre voci + musica).
- `fermo_*` = audio del **nastro vuoto**: silenzio, fruscio della neve, fruscio rosa + ronzio di massa a 50/150 Hz,
  fruscio con dropout, ronzio forte con ticchettio a 25 Hz, rumore di fondo del grabber (−78 dB).

Come sono stati fatti: parlato italiano con `espeak-ng -v it` (tre voci), musica da `brano_pop.py` (MIDI) suonata
con `fluidsynth` e il soundfont GM, rumori con i generatori di ffmpeg (`anoisesrc`, `sine`, `aevalsrc`), mescolati
con `amix`/`volume`/`acompressor` e misurati con la catena qui sopra.
