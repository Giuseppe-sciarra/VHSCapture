import mido, random, subprocess, os
random.seed(5)
# brano pop semplice: batteria, basso, accordi, melodia — 70 s a 110 bpm
mid = mido.MidiFile(ticks_per_beat=480)
def track(ch, prog, notes):
    t = mido.MidiTrack(); mid.tracks.append(t)
    if ch != 9: t.append(mido.Message('program_change', channel=ch, program=prog, time=0))
    ev = []
    for start, dur, note, vel in notes:
        ev.append((start, 'on', note, vel)); ev.append((start + dur, 'off', note, 0))
    ev.sort(key=lambda e: (e[0], e[1] == 'on'))
    now = 0
    for tm, kind, note, vel in ev:
        t.append(mido.Message('note_on' if kind == 'on' else 'note_off', channel=ch, note=note, velocity=vel, time=int(tm - now))); now = tm
B = 480; bars = 32
chords = [[60,64,67],[57,60,64],[53,57,60],[55,59,62]]
drums, bass, pad, mel = [], [], [], []
for bar in range(bars):
    c = chords[bar % 4]; s = bar * 4 * B
    for beat in range(4):
        drums.append((s + beat*B, B//4, 36 if beat % 2 == 0 else 38, 110))
        for h in range(2): drums.append((s + beat*B + h*B//2, B//8, 42, 70))
        bass.append((s + beat*B, B - 20, c[0] - 24, 100))
    for n in c: pad.append((s, 4*B - 10, n, 70))
    for k in range(8):
        if random.random() < .7: mel.append((s + k*B//2, B//2 - 10, random.choice(c) + 12, random.randint(70, 100)))
track(9, 0, drums); track(1, 33, bass); track(2, 89, pad); track(3, 0, mel)
mid.save('pop.mid')
