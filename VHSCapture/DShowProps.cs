using System;
using System.Collections.Generic;
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

        // ================= standard video del grabber (IAMAnalogVideoDecoder) =================
        // È la stessa impostazione della scheda "Decoder video" del driver: VHSCapture la scrive da solo
        // prima di avviare ffmpeg, così risoluzione/fps e standard del grabber non vanno mai per conto loro.

        [ComImport, Guid("C6E13350-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAMAnalogVideoDecoder
        {
            [PreserveSig] int get_AvailableTVFormats(out int lAnalogVideoStandard);
            [PreserveSig] int put_TVFormat(int lAnalogVideoStandard);
            [PreserveSig] int get_TVFormat(out int plAnalogVideoStandard);
            [PreserveSig] int get_HorizontalLocked(out int plLocked);
            [PreserveSig] int put_VCRHorizontalLocking(int lVCRHorizontalLocking);
            [PreserveSig] int get_VCRHorizontalLocking(out int plVCRHorizontalLocking);
            [PreserveSig] int get_NumberOfLines(out int plNumberOfLines);
            [PreserveSig] int put_OutputEnable(int lOutputEnable);
            [PreserveSig] int get_OutputEnable(out int plOutputEnable);
        }

        /// <summary>Valori di AnalogVideoStandard (strmif.h), con lo stesso nome che mostra il driver.</summary>
        static readonly (string key, int flag)[] TvFlags =
        {
            ("NTSC_M", 0x1), ("NTSC_M_J", 0x2), ("NTSC_433", 0x4),
            ("PAL_B", 0x10), ("PAL_D", 0x20), ("PAL_G", 0x40), ("PAL_H", 0x80), ("PAL_I", 0x100),
            ("PAL_M", 0x200), ("PAL_N", 0x400), ("PAL_60", 0x800),
            ("SECAM_B", 0x1000), ("SECAM_D", 0x2000), ("SECAM_G", 0x4000), ("SECAM_H", 0x8000),
            ("SECAM_K", 0x10000), ("SECAM_K1", 0x20000), ("SECAM_L", 0x40000), ("SECAM_L1", 0x80000),
            ("PAL_N_COMBO", 0x100000),
        };

        public static int TvFlag(string key) { foreach (var t in TvFlags) if (t.key == key) return t.flag; return 0; }
        public static string TvName(int flag) { foreach (var t in TvFlags) if (t.flag == flag) return t.key; return flag == 0 ? "nessuno" : $"0x{flag:X}"; }
        public static List<string> TvNames(int mask) { var l = new List<string>(); foreach (var t in TvFlags) if ((mask & t.flag) != 0) l.Add(t.key); return l; }

        /// <summary>Varianti che su un ingresso composito/S-Video si decodificano uguali (cambiano solo l'audio in antenna).</summary>
        static string TvGroup(string key) => key switch
        {
            "PAL_B" or "PAL_D" or "PAL_G" or "PAL_H" or "PAL_I" => "PAL",
            "NTSC_M" or "NTSC_M_J" => "NTSC",
            "SECAM_B" or "SECAM_D" or "SECAM_G" or "SECAM_H" or "SECAM_K" or "SECAM_K1" => "SECAM",
            "SECAM_L" or "SECAM_L1" => "SECAM_L",
            _ => key ?? "",
        };
        public static bool TvSame(string a, string b) => !string.IsNullOrEmpty(a) && TvGroup(a) == TvGroup(b);

        /// <summary>
        /// Standard a due fasi "APERTURA>DAL_VIVO" (es. "NTSC_M>PAL_B"): il grabber si apre nel primo, così il ponte USB
        /// si imposta a 525 righe / 60 Hz con tutti i semiquadri, e a cattura avviata il decoder passa al secondo
        /// (colore PAL). È il «miscuglio» che con l'USB 2828x dà quadro intero, colori giusti e ~40 immagini al secondo
        /// dalle cassette NTSC lette da un videoregistratore PAL (misurato: aprendo direttamente in PAL_B sono ~20 e il quadro è tagliato).
        /// </summary>
        public static bool TvTwoPhase(string key) => !string.IsNullOrEmpty(key) && key.Contains(">");
        public static string TvOpen(string key) => TvTwoPhase(key) ? key.Substring(0, key.IndexOf('>')) : (key ?? "");
        public static string TvLive(string key) => TvTwoPhase(key) ? key.Substring(key.IndexOf('>') + 1) : (key ?? "");

        /// <summary>Se il driver non ha lo standard chiesto, quello che ci va più vicino (per le cassette NTSC su VCR PAL l'altro dei due).</summary>
        static string[] TvCandidates(string want) => want switch
        {
            "PAL_60" => new[] { "PAL_60", "NTSC_433" },
            "NTSC_433" => new[] { "NTSC_433", "PAL_60" },
            "PAL_B" => new[] { "PAL_B", "PAL_G", "PAL_D", "PAL_I", "PAL_H" },
            "NTSC_M" => new[] { "NTSC_M", "NTSC_M_J" },
            "SECAM_D" => new[] { "SECAM_D", "SECAM_K", "SECAM_B", "SECAM_G" },
            "SECAM_L" => new[] { "SECAM_L", "SECAM_L1" },
            _ => new[] { want },
        };

        public class TvInfo
        {
            public bool Found, Supported;
            public int Available, Current, Lines = -1, Locked = -1;
            public string CurrentKey => TvName(Current);
            public List<string> AvailableKeys => TvNames(Available);
        }

        /// <summary>Apre il filtro DirectShow del dispositivo per nome (null se non c'è).</summary>
        static object BindFilter(string friendlyName, bool video, Action<string> log)
        {
            var devEnum = (ICreateDevEnum)new SystemDeviceEnum();
            var cat = video ? CLSID_VideoInputDeviceCategory : CLSID_AudioInputDeviceCategory;
            int hr = devEnum.CreateClassEnumerator(ref cat, out IEnumMoniker en, 0);
            if (hr != 0 || en == null) { log?.Invoke("dshow: nessun dispositivo nella categoria"); return null; }
            object filter = null;
            try
            {
                var monikers = new IMoniker[1];
                while (filter == null && en.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var mon = monikers[0];
                    try
                    {
                        var bagGuid = typeof(IPropertyBag).GUID;
                        mon.BindToStorage(null, null, ref bagGuid, out object bagObj);
                        var bag = (IPropertyBag)bagObj;
                        object name = null;
                        bag.Read("FriendlyName", ref name, IntPtr.Zero);
                        Marshal.ReleaseComObject(bagObj);
                        if (!string.Equals(name as string, friendlyName, StringComparison.OrdinalIgnoreCase)) continue;
                        var iid = IID_IUnknown;
                        mon.BindToObject(null, null, ref iid, out filter);
                    }
                    finally { Marshal.ReleaseComObject(mon); }
                }
            }
            finally { Marshal.ReleaseComObject(en); }
            return filter;
        }

        static TvInfo Read(IAMAnalogVideoDecoder dec)
        {
            var i = new TvInfo { Found = true, Supported = true };
            if (dec.get_AvailableTVFormats(out int av) == 0) i.Available = av;
            if (dec.get_TVFormat(out int cur) == 0) i.Current = cur;
            if (dec.get_NumberOfLines(out int ln) == 0) i.Lines = ln;
            if (dec.get_HorizontalLocked(out int lk) == 0) i.Locked = lk;
            return i;
        }

        /// <summary>Legge lo standard del grabber (anche mentre l'anteprima gira, come la pagina del driver).</summary>
        public static TvInfo ReadTv(string friendlyName)
        {
            object filter = null;
            try
            {
                if (string.IsNullOrWhiteSpace(friendlyName)) return new TvInfo();
                filter = BindFilter(friendlyName, true, null);
                if (filter == null) return new TvInfo();
                if (!(filter is IAMAnalogVideoDecoder dec)) return new TvInfo { Found = true };
                return Read(dec);
            }
            catch { return new TvInfo { Found = filter != null }; }
            finally { if (filter != null) try { Marshal.ReleaseComObject(filter); } catch { } }
        }

        /// <summary>
        /// Porta il grabber sullo standard voluto (es. PAL_60 per le cassette NTSC su videoregistratore PAL).
        /// Non tocca niente se è già giusto. Ritorna lo standard attivo alla fine, o null se il driver non lo permette.
        /// recheck = controllo a pipeline avviata: scrive nel Log solo se deve correggere.
        /// </summary>
        public static string ApplyTv(string friendlyName, string want, Action<string> log, bool recheck = false)
        {
            if (string.IsNullOrWhiteSpace(friendlyName) || string.IsNullOrEmpty(want)) return null;
            object filter = null;
            try
            {
                filter = BindFilter(friendlyName, true, recheck ? null : log);
                if (filter == null) { if (!recheck) log?.Invoke($"Standard grabber: dispositivo «{friendlyName}» non trovato"); return null; }
                if (!(filter is IAMAnalogVideoDecoder dec))
                {
                    if (!recheck) log?.Invoke($"Standard grabber: «{friendlyName}» non permette di cambiarlo da programma — impostalo da «Driver video…» (scheda Decoder video) su {want}");
                    return null;
                }
                var info = Read(dec);
                if (want == PalSoftware.TvKey)
                {
                    // PAL-60 col colore rifatto: si scrive PAL_60 e basta. L'USB 2828x risponde NTSC_M, ma il colore a 4,43 MHz
                    // resta attivo (senza questa scrittura l'NTSC_M dà bianco e nero): niente verifica e niente ripiego su NTSC 4.43
                    if (info.Available != 0 && (info.Available & TvFlag("PAL_60")) == 0)
                        log?.Invoke($"Standard grabber: il driver non elenca PAL_60 (ha: {string.Join(", ", info.AvailableKeys)}) — provo lo stesso");
                    int hr0 = dec.put_TVFormat(TvFlag("PAL_60"));
                    System.Threading.Thread.Sleep(200);
                    var a0 = Read(dec);
                    log?.Invoke($"Standard grabber: PAL_60 scritto (hr=0x{hr0:X8}), il driver risponde {a0.CurrentKey}{Det(a0)} — il colore PAL lo rifà VHSCapture");
                    return a0.CurrentKey;
                }
                if (TvSame(info.CurrentKey, want))
                {
                    if (!recheck) log?.Invoke($"Standard grabber: {info.CurrentKey} (già impostato){Det(info)}");
                    return info.CurrentKey;
                }
                string start = info.CurrentKey;
                bool tried = false;
                foreach (var c in TvCandidates(want))
                {
                    if (info.Available != 0 && (info.Available & TvFlag(c)) == 0) continue;
                    if (TvSame(info.CurrentKey, c))
                    {
                        if (tried) log?.Invoke($"Standard grabber: resta {info.CurrentKey} — {want} non c'è su questo grabber{Det(info)}");
                        return info.CurrentKey;
                    }
                    tried = true;
                    int hr = dec.put_TVFormat(TvFlag(c));
                    if (hr != 0) { log?.Invoke($"Standard grabber: il driver ha rifiutato {c} (hr=0x{hr:X8})"); continue; }
                    System.Threading.Thread.Sleep(200);   // il decoder ci mette un attimo a riagganciarsi
                    var after = Read(dec);
                    if (TvSame(after.CurrentKey, c))
                    {
                        string why = recheck ? " (il driver l'aveva cambiato all'apertura)" : "";
                        string alt = c != want ? $" — {want} non c'è, uso {c}" : "";
                        log?.Invoke($"Standard grabber: {start} → {after.CurrentKey}{why}{alt}{Det(after)}");
                        return after.CurrentKey;
                    }
                    // il driver lo elenca ma non lo tiene (es. USB 2828x con PAL_60: torna NTSC_M)
                    log?.Invoke($"Standard grabber: il driver accetta {c} ma non lo tiene (torna {after.CurrentKey})");
                    info = after;
                }
                if (!tried)
                    log?.Invoke($"Standard grabber: il driver non ha {want} (ha: {string.Join(", ", info.AvailableKeys)}) — resta {info.CurrentKey}");
                return info.CurrentKey;
            }
            catch (Exception ex) { log?.Invoke("Standard grabber: " + ex.Message); return null; }
            finally { if (filter != null) try { Marshal.ReleaseComObject(filter); } catch { } }
        }

        static string Det(TvInfo i)
        {
            string s = "";
            if (i.Lines > 0) s += $" · righe {i.Lines}";
            if (i.Locked >= 0) s += i.Locked != 0 ? " · segnale agganciato" : " · segnale non agganciato";
            return s;
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
