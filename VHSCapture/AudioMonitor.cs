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

        public int DeviceNumber { get; set; } = -1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct WAVEOUTCAPS
        {
            public short wMid, wPid; public int vDriverVersion;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public int dwFormats; public short wChannels, wReserved1; public int dwSupport;
        }
        [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS caps, int size);

        /// <summary>Uscite audio disponibili (indice = DeviceNumber di WaveOutEvent).</summary>
        public static System.Collections.Generic.List<string> ListOutputs()
        {
            var l = new System.Collections.Generic.List<string>();
            try
            {
                int n = waveOutGetNumDevs();
                for (int i = 0; i < n; i++)
                {
                    var c = new WAVEOUTCAPS();
                    if (waveOutGetDevCaps((IntPtr)i, ref c, System.Runtime.InteropServices.Marshal.SizeOf<WAVEOUTCAPS>()) == 0) l.Add(c.szPname);
                    else l.Add("Uscita " + (i + 1));
                }
            }
            catch { }
            return l;
        }

        public void Start()
        {
            Stop();
            buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(600),
                DiscardOnBufferOverflow = true,   // se le casse sono indietro si butta, mai bloccare ffmpeg
            };
            output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3, DeviceNumber = DeviceNumber };
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
