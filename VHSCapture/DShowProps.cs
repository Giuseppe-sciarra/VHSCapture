using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VHSCapture
{
    /// <summary>Apre la pagina di configurazione nativa del driver DirectShow (la stessa di OBS "Configura video"),
    /// con fallback ai dialoghi mostrati da ffmpeg.</summary>
    public static class DShowProps
    {
        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICreateDevEnum
        {
            [PreserveSig] int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker ppEnumMoniker, [In] int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyBag
        {
            [PreserveSig] int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, Out, MarshalAs(UnmanagedType.Struct)] ref object pVar, [In] IntPtr pErrorLog);
            [PreserveSig] int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName, [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
        }

        [ComImport, Guid("B196B28B-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpecifyPropertyPages
        {
            [PreserveSig] int GetPages(out CAUUID pPages);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CAUUID { public int cElems; public IntPtr pElems; }

        [ComImport, Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86")]
        class SystemDeviceEnum { }

        static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
        static readonly Guid CLSID_AudioInputDeviceCategory = new Guid("33D9A762-90C8-11D0-BD43-00A0C911CE86");
        static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
        static extern int OleCreatePropertyFrame(IntPtr hwndOwner, int x, int y, [MarshalAs(UnmanagedType.LPWStr)] string lpszCaption,
            int cObjects, [In, MarshalAs(UnmanagedType.Interface)] ref object ppUnk, int cPages, IntPtr pPageClsID, int lcid, int dwReserved, IntPtr pvReserved);

        /// <summary>Prova ad aprire la property page nativa del dispositivo. Ritorna false se il driver non ne ha o se fallisce.</summary>
        public static bool ShowNative(IntPtr owner, string friendlyName, bool video, Action<string> log)
        {
            object filter = null;
            try
            {
                var devEnum = (ICreateDevEnum)new SystemDeviceEnum();
                var cat = video ? CLSID_VideoInputDeviceCategory : CLSID_AudioInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref cat, out IEnumMoniker en, 0);
                if (hr != 0 || en == null) { log?.Invoke("dshow: nessun dispositivo nella categoria"); return false; }

                var monikers = new IMoniker[1];
                var fetched = IntPtr.Zero;
                while (en.Next(1, monikers, fetched) == 0)
                {
                    var mon = monikers[0];
                    try
                    {
                        var bagGuid = typeof(IPropertyBag).GUID;
                        mon.BindToStorage(null, null, ref bagGuid, out object bagObj);
                        var bag = (IPropertyBag)bagObj;
                        object name = null;
                        bag.Read("FriendlyName", ref name, IntPtr.Zero);
                        if (!string.Equals(name as string, friendlyName, StringComparison.OrdinalIgnoreCase)) continue;

                        var iid = IID_IUnknown;
                        mon.BindToObject(null, null, ref iid, out filter);
                        break;
                    }
                    finally { Marshal.ReleaseComObject(mon); }
                }
                if (filter == null) { log?.Invoke($"dshow: dispositivo \"{friendlyName}\" non trovato"); return false; }

                if (!(filter is ISpecifyPropertyPages spp)) { log?.Invoke("dshow: il driver non espone pagine di proprietà"); return false; }
                hr = spp.GetPages(out CAUUID pages);
                if (hr != 0 || pages.cElems == 0) { log?.Invoke("dshow: nessuna pagina di proprietà"); return false; }
                try
                {
                    hr = OleCreatePropertyFrame(owner, 0, 0, friendlyName, 1, ref filter, pages.cElems, pages.pElems, 0, 0, IntPtr.Zero);
                    if (hr != 0) log?.Invoke($"dshow: OleCreatePropertyFrame hr=0x{hr:X8}");
                    return hr == 0;
                }
                finally { if (pages.pElems != IntPtr.Zero) Marshal.FreeCoTaskMem(pages.pElems); }
            }
            catch (Exception ex) { log?.Invoke("dshow: " + ex.Message); return false; }
            finally { if (filter != null) Marshal.ReleaseComObject(filter); }
        }

        /// <summary>Fallback: fa mostrare il dialogo a ffmpeg (richiede il dispositivo libero). kind: "video", "crossbar", "audio".</summary>
        public static void ShowViaFFmpeg(string device, string kind, Action<string> log)
        {
            string opt = kind switch
            {
                "crossbar" => "-show_video_crossbar_connection_dialog true",
                "audio" => "-show_audio_device_dialog true",
                _ => "-show_video_device_dialog true",
            };
            string input = kind == "audio" ? $"audio=\"{device}\"" : $"video=\"{device}\"";
            string args = $"-hide_banner -loglevel warning -f dshow {opt} -i {input} -t 1 -f null NUL";
            log?.Invoke("ffmpeg " + args);
            try
            {
                var psi = new ProcessStartInfo(FFmpeg.ExePath, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi);
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(err)) log?.Invoke(err.Trim());
            }
            catch (Exception ex) { log?.Invoke("ffmpeg dialog: " + ex.Message); }
        }
    }
}
