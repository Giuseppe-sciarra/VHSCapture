using System;
using NAudio.Wave;

namespace VHSCapture
{
    /// <summary>Monitoraggio audio (come in OBS): riproduce sulle casse l'audio che arriva dal grabber, con ~150 ms di latenza.</summary>
    public class AudioMonitor : IDisposable
    {
        WaveOutEvent output;
        BufferedWaveProvider buffer;
        public float Volume { get => output?.Volume ?? 1f; set { if (output != null) output.Volume = Math.Clamp(value, 0f, 1f); } }

        public void Start()
        {
            Stop();
            buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(600),
                DiscardOnBufferOverflow = true,   // se le casse sono indietro si butta, mai bloccare ffmpeg
            };
            output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
            output.Init(buffer);
            output.Play();
        }

        public void Add(byte[] data, int count)
        {
            var b = buffer; if (b == null) return;
            // se si accumula troppo ritardo, svuota: meglio un "clic" che l'audio in ritardo di secondi
            if (b.BufferedDuration.TotalMilliseconds > 400) b.ClearBuffer();
            b.AddSamples(data, 0, count);
        }

        public void Stop()
        {
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            output = null; buffer = null;
        }

        public void Dispose() => Stop();
    }
}
