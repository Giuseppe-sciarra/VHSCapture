using System.Reflection;
using System.Text.Json;
using VHSCapture;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
AppSettings Settings() => new() { CanvasW = 1920, CanvasH = 1080, Fps = "50", Encoder = "h264_qsv", Sources = new() {
    new Source { Id = "grabber", VideoDevice = "Test", AudioDevice = "Test audio", InputSize = "720x576", InputFps = "25", DeinterlaceMode = "yadif2x", W = 1440, H = 1080, X = 240, Y = 0, CropB = 8 }
}};
string Args(AppSettings s, bool gpu, bool audio = true, string backend = "d3d11va", bool analysis = false) => CaptureEngine.BuildArgs(s, 960, 540, 0,
    new CaptureEngine.PipeNames { Preview = "preview", Ts = "ts", Progress = "progress", Monitor = audio ? "monitor" : null, Analysis = analysis ? new() { [s.Sources[0].Id]="analysis" } : new() }, out _, gpu, backend);
var s = Settings();
s.Encoder="h264_nvenc"; s.EncoderUserSet=true; s.IntelGpu=true;
Check(FFmpeg.ChooseEncoder(s,new[]{"h264_qsv","h264_nvenc","libx264"})=="h264_nvenc","Scelta NVIDIA esplicita non scavalcata dalla preferenza Intel");
Check(FFmpeg.ChooseEncoder(s,new[]{"h264_qsv","libx264"})=="h264_nvenc","Una prova transitoria fallita non cancella la scelta esplicita");
s.EncoderUserSet=false;
Check(FFmpeg.ChooseEncoder(s,new[]{"h264_qsv","h264_nvenc","libx264"})=="h264_qsv","Scelta automatica conserva preferenza Intel");
s.IntelGpu=false; s.Encoder="libx264";
Check(FFmpeg.ChooseEncoder(s,new[]{"h264_nvenc","libx264"})=="h264_nvenc","Scelta automatica usa NVIDIA disponibile");
Check(FFmpeg.ChooseEncoder(s,Array.Empty<string>())==s.Encoder,"Verifica inconcludente non forza un encoder arbitrario");
s=Settings();
var before = JsonSerializer.Serialize(s);
var cpu = Args(s, false);
Check(cpu.Contains("[venc]") && cpu.Contains("[aenc]") && cpu.Contains("-c:v h264_qsv") && cpu.Contains("-c:a aac") && cpu.Contains("-f mpegts"), "Pipeline completa ed encoder sempre attivo");
Check(cpu.Contains("pad=w=1920:h=1080:x=240:y=0") && cpu.Contains("fps=50") && cpu.Contains("yadif=mode=send_field"), "Canvas 1080p50 e deinterlaccio 2x completi");
Check(!cpu.Contains("fps=25") && !cpu.Contains("send_frame"), "Nessun risparmio o dimezzamento dell'anteprima");
Check(JsonSerializer.Serialize(s) == before, "Impostazioni e sorgenti originali immutate");
var gpu = Args(s, true);
Check(gpu.Contains("vpp_qsv=deinterlace=advanced:rate=field") && gpu.Contains("overlay_qsv=x=240:y=0:w=1440:h=1080"), "QSV deinterlaccio 2x, scala e bande su GPU");
Check(gpu.Contains("hwupload=extra_hw_frames=8,loop=loop=-1:size=1") && gpu.IndexOf("hwdownload") > gpu.IndexOf("split=2[venc][pv]"), "Fondo GPU riusato e download confinato all'anteprima");
Check(gpu.Contains("format=qsv") && gpu.Contains("-pix_fmt qsv"), "Superfici hardware QSV imposte fino all encoder");
Check(!gpu.Contains("pad=") && !gpu.Contains("yadif=") && !gpu.Contains("scale@s") && !gpu.Contains("fps=25"), "Filtri video hardware senza ridurre gli fps");
Check(Args(s,true,backend:"dxva2").Contains("child_device_type=dxva2") && !Args(s,true,backend:"dxva2").Contains("d3d11va"), "Percorso Intel DXVA2 per driver precedenti");
s.IntelFieldOrder = "bff";
Check(Args(s, true).Contains("setfield=bff"), "Ordine BFF configurabile");
s.Sources[0].Brightness = .1;
Check(!CaptureEngine.CanUseQsv(s) && Args(s, true).Contains("eq@sgrabber"), "Correzione colore conservata nel percorso compatibile");
s = Settings(); s.Sources[0].CropT = 1;
Check(!CaptureEngine.CanUseQsv(s), "Ritaglio dispari usa il percorso compatibile");
s = Settings(); s.Sources.Add(new Source { Id = "color", Type = SourceType.Color, W = 100, H = 100 });
Check(!CaptureEngine.CanUseQsv(s) && Args(s, true).Contains("overlay@s"), "Scene multiple mantengono la composizione software");
s = Settings(); s.Sources[0].X = -20;
Check(!CaptureEngine.CanUseQsv(s) && Args(s, false).Contains("x=-20:y=0"), "Sorgente parzialmente fuori canvas preservata");
s = Settings(); s.Fps = "59.94"; s.Sources[0].InputFps = "29.97";
Check(Args(s, false).Contains("[pv]fps=59.94") && !Args(s, false).Contains("[pv]fps=29.97"), "NTSC anteprima a 59.94 fps completi");
s = Settings(); s.Sources[0].DeinterlaceMode = "off";
Check(!Args(s, true).Contains("setfield=") && !Args(s, true).Contains("deinterlace="), "Sorgente progressiva senza deinterlaccio");
s = Settings(); s.Sources[0].AudioDevice = "";
Check(!Args(s, true).Contains("[aenc]") && !Args(s, true).Contains("-c:a"), "Sorgente senza audio");
s = Settings(); s.Encoder = "h264_nvenc";
Check(!CaptureEngine.CanUseQsv(s) && !Args(s, true).Contains("init_hw_device"), "Encoder non Intel conserva la pipeline compatibile");
Check(!Args(Settings(), false, false).Contains("[amons]") && Args(Settings(), false, false).Contains("[aenc]"), "Ascolto spento conserva l'audio registrato");
var recorder = new TsRecorder();
var flags = BindingFlags.NonPublic | BindingFlags.Instance;
void Set(string field, object val) => typeof(TsRecorder).GetField(field, flags).SetValue(recorder, val);
Set("pat", new byte[188]); Set("pmt", new byte[188]); Set("haveKey", true); Set("carryLen", 17);
((List<byte[]>)typeof(TsRecorder).GetField("gop", flags).GetValue(recorder)).Add(new byte[188]);
Check(recorder.HasBufferedKeyFrame, "Pre-roll simulato presente");
recorder.ResetStream();
Check(!recorder.HasBufferedKeyFrame && (int)typeof(TsRecorder).GetField("carryLen", flags).GetValue(recorder) == 0, "Riavvio non riusa GOP o pacchetti precedenti");
var oldSettings = JsonSerializer.Deserialize<AppSettings>("{\"SmoothPreview\":false,\"LowCpuPreview\":true,\"Fps\":\"50\"}");
oldSettings.Sources=Settings().Sources;
Check(Args(oldSettings,false).Contains("[pv]fps=50"), "Vecchie preferenze di risparmio ignorate");
// ---------- fine cassetta: rilevatore sui pixel 80×60 ----------
var rng = new Random(3);
byte[] Frame(int y, int u, int v, int noise = 6, Action<byte[]> draw = null)
{
    var f = new byte[NoSignalDetector.FrameBytes]; int n = NoSignalDetector.W * NoSignalDetector.H;
    for (int i = 0; i < n; i++)
    {
        f[i] = (byte)Math.Clamp(y + rng.Next(-noise, noise + 1), 0, 255);
        f[n + i] = (byte)Math.Clamp(u + rng.Next(-2, 3), 0, 255);
        f[2 * n + i] = (byte)Math.Clamp(v + rng.Next(-2, 3), 0, 255);
    }
    draw?.Invoke(f);
    return f;
}
void Box(byte[] f, int x0, int y0, int w, int h, int y, int u = 128, int v = 128)
{
    int n = NoSignalDetector.W * NoSignalDetector.H;
    for (int yy = y0; yy < y0 + h; yy++) for (int xx = x0; xx < x0 + w; xx++)
    { int p = yy * NoSignalDetector.W + xx; f[p] = (byte)y; f[n + p] = (byte)u; f[2 * n + p] = (byte)v; }
}
byte[] Scene(double t) => Frame(90, 128, 128, 6, f => { for (int k = 0; k < 12; k++) Box(f, (k * 7 + (int)(t * 8)) % 70, (k * 5) % 50, 6, 6, 40 + k * 15, 100 + k * 3, 150 - k * 3); });
double Run(NoSignalDetector d, Func<double, byte[]> frames, double from, double to, Func<double, double> audio = null)
{
    for (double t = from; t <= to; t += 0.04)
    {
        if (audio != null) for (int k = 0; k < 2; k++) d.AddAudio(audio(t + k * 0.02), t + k * 0.02);
        d.Observe(frames(t), t);
    }
    return d.BlankSeconds(to);
}
double Speech(double t) { var r = new Random((int)(t * 1.7) * 31 + 5); return r.NextDouble() < .6 ? -20 - r.NextDouble() * 6 : -58 - r.NextDouble() * 8; }
foreach (var c in new[] { ("blu", 41, 240, 110), ("nero", 16, 128, 128), ("grigio", 128, 128, 128), ("blu scuro", 30, 170, 115) })
{
    var d = new NoSignalDetector();
    Check(Run(d, t => Frame(c.Item2, c.Item3, c.Item4, 8), 0, 30) >= 29, "Sfondo del lettore riconosciuto con rumore analogico: " + c.Item1);
}
var dd = new NoSignalDetector();
Check(Run(dd, t => Frame(41, 240, 110, 8, f => Box(f, 60, 4, 12, 4, 200)), 0, 30) >= 29, "Scritta OSD bianca sullo schermo blu non blocca lo stop");
dd = new NoSignalDetector();
Check(Run(dd, t => Frame(16, 128, 128, 8, f => Box(f, 50, 52, 4 + ((int)t % 5) * 3, 3, 220)), 0, 30) >= 25, "Contatore OSD che cambia ogni secondo non blocca lo stop");
dd = new NoSignalDetector();
Check(Run(dd, t => Frame(16, 128, 128, 8), 0, 30, t => -30 + (t * 7 % 1) * 2) >= 29, "Fruscio costante (neve) non blocca lo stop");
dd = new NoSignalDetector();
Check(Run(dd, Scene, 0, 30) == 0, "Filmato con dettagli e movimento: nessuno stop");
dd = new NoSignalDetector();
Check(Run(dd, t => Frame(16, 128, 128, 8), 0, 30, Speech) < 2, "Nero con voci o musica: nessuno stop");
dd = new NoSignalDetector();
Check(Run(dd, t => Frame(12, 128, 128, 6, f => Box(f, 20 + (int)(15 * Math.Sin(t)), 30, 5, 4, 60, 90, 170)), 0, 30) < 3, "Piccolo oggetto colorato nel buio: filmato");
dd = new NoSignalDetector();
Run(dd, t => Frame(41, 240, 110, 8), 0, 20);
Run(dd, t => t < 20.4 ? Scene(t) : Frame(41, 240, 110, 8), 20.04, 21);
Check(dd.BlankSeconds(21) >= 20, "Un disturbo di mezzo secondo non azzera il conteggio");
Run(dd, Scene, 21.04, 24);
Check(dd.BlankSeconds(24) == 0, "Più di un secondo di filmato azzera il conteggio");
dd = new NoSignalDetector();
Run(dd, t => Frame(41, 240, 110, 8), 0, 10);
dd.Observe(Frame(41, 240, 110, 8), 14);
Check(dd.BlankSeconds(14) == 0, "Analisi interrotta per più di 2 s: il conteggio riparte");
Check(dd.Observe(null, 15) == "contenuto" && dd.BlankSeconds(15) == 0, "Fotogramma mancante non conta come sfondo");
var an = Args(Settings(), false, analysis: true);
Check(an.Contains($"scale={NoSignalDetector.W}:{NoSignalDetector.H}:flags=area,format=yuv444p") && an.Contains("-f rawvideo \"\\\\.\\pipe\\analysis\"") && !an.Contains("signalstats"),
    "Ramo di analisi: pixel 80×60 verso la pipe, niente statistiche testuali");
// Una PSI corrotta non deve uccidere il thread che alimenta la registrazione.
var random = new Random(42);
for(int i=0;i<10000;i++)
{
    var packet = new byte[188]; random.NextBytes(packet); packet[0]=0x47; packet[1]=0x40; packet[2]=0;
    lock(recorder) recorder.Feed(packet,packet.Length);
}
Check(!recorder.HasBufferedKeyFrame,"10.000 PAT malformate non causano eccezioni né pre-roll spurio");
byte[] PsiPacket(int pid, byte[] section)
{
    var packet = Enumerable.Repeat((byte)255,188).ToArray();
    packet[0]=0x47; packet[1]=(byte)(0x40|(pid>>8)); packet[2]=(byte)pid; packet[3]=0x30;
    packet[4]=1; packet[5]=0; packet[6]=0; section.CopyTo(packet,7); return packet;
}
var pat = PsiPacket(0,new byte[]{0,0xB0,13,0,1,0xC1,0,0,0,1,0xF0,0,0,0,0,0});
recorder.Feed(pat,188);
Check((int)typeof(TsRecorder).GetField("pmtPid",flags).GetValue(recorder)==4096,"PAT con adaptation field e pointer validi");
var pmt = PsiPacket(4096,new byte[]{2,0xB0,18,0,1,0xC1,0,0,0xE1,0,0xF0,0,0x1B,0xE1,0,0xF0,0,0,0,0,0});
recorder.Feed(pmt,188);
Check((int)typeof(TsRecorder).GetField("videoPid",flags).GetValue(recorder)==256,"PMT con adaptation field e traccia H264");
recorder.ResetStream();
var full = new System.Collections.Concurrent.BlockingCollection<byte[]>(1);
full.Add(new byte[188]); Set("queue",full); Set("muxStopping",false);
typeof(TsRecorder).GetMethod("Enqueue",flags).Invoke(recorder,new object[]{new byte[188]});
Check(recorder.LastError?.Contains("buffer pieno")==true,"Destinazione lenta segnalata senza perdita silenziosa di pacchetti");
recorder.Stop(false);
using (var retryEngine = new CaptureEngine())
{
    typeof(CaptureEngine).GetProperty("GpuActive").SetValue(retryEngine, true);
    Check(retryEngine.TryLegacyGpu() && retryEngine.QsvBackend == "dxva2", "Fallback da D3D11 a DXVA2");
    Check(!retryEngine.TryLegacyGpu(), "Il tentativo legacy non si ripete all'infinito");
    retryEngine.GpuDisabled = true;
    retryEngine.ResetGpuRetry();
    Check(!retryEngine.GpuDisabled && retryEngine.QsvBackend == "d3d11va", "Ripristino dei tentativi GPU dalla configurazione");
}
// standard del grabber: i preset portano con sé il decoder giusto (cassette NTSC su VCR PAL = PAL-60 / NTSC 4.43)
Check(DShowProps.TvFlag("PAL_60") == 0x800 && DShowProps.TvFlag("NTSC_433") == 0x4 && DShowProps.TvFlag("PAL_B") == 0x10 && DShowProps.TvFlag("NTSC_M") == 0x1, "Valori AnalogVideoStandard di PAL-60, NTSC 4.43, PAL B, NTSC M");
Check(DShowProps.TvSame("PAL_G", "PAL_B") && DShowProps.TvSame("NTSC_M_J", "NTSC_M") && !DShowProps.TvSame("PAL_60", "PAL_B") && !DShowProps.TvSame("NTSC_433", "NTSC_M") && !DShowProps.TvSame("", ""), "PAL-60 e NTSC 4.43 non confusi con PAL/NTSC normali");
Check(string.Join(",", DShowProps.TvNames(0x1 | 0x4 | 0x10 | 0x800)) == "NTSC_M,NTSC_433,PAL_B,PAL_60", "Elenco standard accettati dal driver");
var tvSrc = new Source { InputSize = "720x480", InputFps = "29.97", DeinterlaceMode = "yadif2x", CropB = 6, TvStandard = "PAL_60" };
Check(VideoStandard.Detect(tvSrc) == VideoStandard.NTSC_PAL60, "Preset NTSC su VCR PAL (PAL-60) riconosciuto");
tvSrc.TvStandard = "NTSC_433";
Check(VideoStandard.Detect(tvSrc) == VideoStandard.NTSC_443, "Preset NTSC su VCR PAL (NTSC 4.43) riconosciuto");
tvSrc.TvStandard = "NTSC_M";
Check(VideoStandard.Detect(tvSrc) == VideoStandard.NTSC, "Preset NTSC con lettore NTSC riconosciuto");
tvSrc.TvStandard = "PAL_B";
Check(VideoStandard.Detect(tvSrc) == null, "720×480 col grabber in PAL = personalizzato (il miscuglio non passa per standard)");
tvSrc.TvStandard = "";
Check(VideoStandard.Detect(tvSrc) == null && VideoStandard.DetectValues(tvSrc) == VideoStandard.NTSC, "Sorgenti vecchie senza standard grabber: migrazione da risoluzione e fps");
var tvA = new Source { TvStandard = "PAL_B" }; var tvB = tvA.Clone(); tvB.TvStandard = "PAL_60";
Check(!tvA.StructurallyEquals(tvB), "Cambiare lo standard del grabber riavvia la cattura");
tvA.CopyStructuralFrom(tvB);
Check(tvA.TvStandard == "PAL_60", "Lo standard del grabber segue la sorgente nelle copie");
// cassette NTSC su VCR PAL con grabber senza PAL-60 (USB 2828x): apertura NTSC_M, PAL_B dal vivo, 720×480, campi separati, ritaglio 10
Check(DShowProps.TvOpen("NTSC_M>PAL_B") == "NTSC_M" && DShowProps.TvLive("NTSC_M>PAL_B") == "PAL_B" && DShowProps.TvOpen("PAL_B") == "PAL_B" && DShowProps.TvLive("PAL_60") == "PAL_60" && !DShowProps.TvTwoPhase("PAL_B") && DShowProps.TvOpen(null) == "", "Standard a due fasi: apertura e dal vivo");
var rb = new Source { Type = SourceType.Capture, InputSize = "720x480", InputFps = "29.97", DeinterlaceMode = "fields", CropB = 10, TvStandard = "NTSC_M>PAL_B" };
Check(rb.IsNtscRebuild && Array.IndexOf(VideoStandard.All, VideoStandard.NTSC_PALB) < 0, "NTSC M → PAL B/G dal vivo resta disponibile a mano (fuori dal menu dei preset)");
Check(!new Source { Type = SourceType.Capture, InputSize = "720x576", DeinterlaceMode = "yadif2x", TvStandard = "PAL_B" }.IsNtscRebuild &&
      !new Source { Type = SourceType.Capture, InputSize = "720x480", TvStandard = "NTSC_M" }.IsNtscRebuild &&
      new Source { Type = SourceType.Capture, InputSize = "720x480", DeinterlaceMode = "bwdif2x", TvStandard = "NTSC_M>PAL_B" }.IsNtscRebuild, "Ricostruzione riconosciuta anche cambiando il deinterlaccio");
Check(rb.NaturalSize() == (626, 470), "470 righe utili restano 4:3");
var rbS = Settings(); rbS.Sources[0].InputSize = "720x480"; rbS.Sources[0].InputFps = "29.97"; rbS.Sources[0].DeinterlaceMode = "fields"; rbS.Sources[0].CropB = 10; rbS.Sources[0].TvStandard = "NTSC_M>PAL_B";
var rbCpu = Args(rbS, false); var rbCpuAn = Args(rbS, false, analysis: true); var rbGpu = Args(rbS, true); var rbGpuAn = Args(rbS, true, analysis: true);
Check(rbCpu.Contains(CaptureEngine.DropEmptyFrames) && rbCpuAn.Contains(CaptureEngine.DropEmptyFrames + ",split=2") && rbGpu.Contains(CaptureEngine.DropEmptyFrames) && rbGpuAn.Contains(CaptureEngine.DropEmptyFrames + ",split=2"), "Fotogrammi vuoti scartati in CPU, GPU e con l'analisi fine cassetta");
Check(rbCpu.Contains(CaptureEngine.FieldsFilter) && rbGpu.Contains(CaptureEngine.FieldsFilter + ",format=nv12,hwupload") && rbGpuAn.Contains(CaptureEngine.FieldsFilter), "Campi separati su CPU e prima dell'upload GPU");
Check(!rbCpu.Contains("yadif") && !rbGpu.Contains("deinterlace=") && !rbGpu.Contains("setfield=bff") && rbCpu.Contains("h=ih-10:") && rbGpu.Contains("ch=ih-10:"), "Niente Yadif né deinterlaccio GPU, ritaglio in righe del fotogramma");
Check(rbCpu.IndexOf(CaptureEngine.FieldsFilter) < rbCpu.IndexOf("crop@s"), "Campi separati prima del ritaglio");
int Conta(string a) => (a.Length - a.Replace(CaptureEngine.DropEmptyFrames, "").Length) / CaptureEngine.DropEmptyFrames.Length;
Check(Conta(rbCpu) == 1 && Conta(rbCpuAn) == 1 && Conta(rbGpu) == 1 && Conta(rbGpuAn) == 1, "Filtro dei fotogrammi vuoti una sola volta");
Check(!cpu.Contains("signalstats") && !gpu.Contains("signalstats") && !cpu.Contains("separatefields"), "Nessun filtro in più per PAL e NTSC normali");
var rbB = Settings(); rbB.Sources[0].InputSize = "720x480"; rbB.Sources[0].DeinterlaceMode = "bwdif2x"; rbB.Sources[0].CropB = 10; rbB.Sources[0].TvStandard = "NTSC_M>PAL_B";
Check(Args(rbB, false).Contains("bwdif=mode=send_field") && Args(rbB, true).Contains("deinterlace=advanced:rate=field"), "Con Bwdif 2x scelto a mano si usa il deinterlaccio normale");
// PAL-60 col colore rifatto (USB 2828x): apertura NTSC_M, PAL_60 scritto senza verifica, V rigirato riga per riga nel grafo
var sw = new Source { Type = SourceType.Capture, InputSize = "720x480", InputFps = "29.97", DeinterlaceMode = "yadif2x", CropB = 6, TvStandard = "NTSC_M>" + PalSoftware.TvKey };
Check(VideoStandard.Detect(sw) == VideoStandard.NTSC_PAL60SW && sw.IsPal60Software && !sw.IsNtscRebuild && Array.IndexOf(VideoStandard.All, VideoStandard.NTSC_PAL60SW) == 4, "Preset NTSC su VCR PAL (PAL-60 col colore rifatto) riconosciuto");
var swS = Settings(); swS.Sources[0].InputSize = "720x480"; swS.Sources[0].InputFps = "29.97"; swS.Sources[0].CropB = 6; swS.Sources[0].TvStandard = "NTSC_M>" + PalSoftware.TvKey;
var swCpu = Args(swS, false); var swAn = Args(swS, false, analysis: true); var swGpu = Args(swS, true, analysis: true);
Check(swCpu.Contains("streamselect@palgrabber=inputs=2:map=0[pq0]") && swCpu.Contains("[pq0]") && swCpu.IndexOf("streamselect@palgrabber") < swCpu.IndexOf("yadif"), "Colore PAL rifatto prima del deinterlaccio");
Check(swAn.Contains("[pq0]split=2[cs0][an0]") && swGpu.Contains("[pq0]split=2[cs0][an0]") && swGpu.Contains("[cs0]setfield=tff,format=nv12,hwupload"), "Ramo di analisi e GPU dopo la correzione del colore");
Check(!cpu.Contains("streamselect") && !swCpu.Contains("signalstats"), "Correzione solo per il PAL-60 software");
Check(PalSoftware.SelectCommand("grabber", 1) == "streamselect@palgrabber map 1", "Comando zmq per girare il colore");
// monitor della fase su immagini 80×60 sintetiche: scena blu (U+, V−) = fase giusta, viola (U+, V+) = da girare
byte[] Img(int u, int v, int shift) { var f = new byte[NoSignalDetector.FrameBytes]; int w = NoSignalDetector.W, h = NoSignalDetector.H, n = w * h;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { int i = y * w + x; double t = Math.Sin((x + shift) * 0.21) * Math.Cos(y * 0.17);
        f[i] = (byte)(110 + 70 * t); f[n + i] = (byte)Math.Clamp(128 + u * (0.6 + 0.4 * t), 0, 255); f[2 * n + i] = (byte)Math.Clamp(128 + v * (0.6 + 0.4 * t), 0, 255); } return f; }
var mBlu = new PalPhaseMonitor(); bool tBlu = false; for (int i = 0; i < 80; i++) tBlu |= mBlu.Observe(Img(30, -20, i));
Check(!tBlu && mBlu.Decided, "Monitor: scena blu = fase giusta, nessuna inversione");
var mViola = new PalPhaseMonitor(); int tV = -1; for (int i = 0; i < 80 && tV < 0; i++) if (mViola.Observe(Img(30, 20, i))) tV = i;
Check(tV >= PalPhaseMonitor.PriorMinFrames - 1 && tV < 80, "Monitor: scena viola = fase da girare dopo ~1,5 s");
var mDrop = new PalPhaseMonitor(); int tD = -1; for (int i = 0; i < 70; i++) if (mDrop.Observe(Img(30, i < 60 ? -20 : 20, i)) && tD < 0) tD = i;
Check(tD == 61, "Monitor: rovesciamento di colpo (fotogramma perso) riconosciuto e confermato al fotogramma dopo");
var mGl = new PalPhaseMonitor(); bool tG = false; for (int i = 0; i < 80; i++) tG |= mGl.Observe(Img(30, i == 60 ? 20 : -20, i));
Check(!tG, "Monitor: disturbo di un solo fotogramma ignorato");
// contenuto davvero viola (indice moderato +0.15) con fase giusta: nessuna inversione
var mPurple = new PalPhaseMonitor(); bool tP = false; for (int i = 0; i < 200; i++) tP |= mPurple.Observe(Img(30, i < 60 ? -20 : 6, i < 60 ? i : i * 7 + 500));   // dopo lo stacco la scena è un'altra
Check(!tP, "Monitor: scena con un po' di viola vero non fa scattare l'inversione");
// scena fortemente viola per 10 s: al massimo UNA inversione del controllo lento, poi fermo
var mPP = new PalPhaseMonitor(); var ev = new System.Collections.Generic.List<int>();
for (int i = 0; i < 400; i++) { if (mPP.Observe(Img(30, i < 60 ? -20 : 25, i < 60 ? i : i * 7 + 500))) { ev.Add(i); mPP.ExpectFlip(); } }
Check(ev.Count == 1 && ev[0] > 60 + PalPhaseMonitor.EmaConfirm && mPP.LastReason.StartsWith("colori viola"), "Monitor: controllo lento prudente, una sola inversione poi fermo");
var mLate = new PalPhaseMonitor(); int tL = -1; for (int i = 0; i < 200; i++) if (mLate.Observe(Img(30, i < 100 ? -20 : 20, i * (i < 100 ? 1 : 9) + (i < 100 ? 0 : 500))) && tL < 0) tL = i;
Check(tL > 100 && tL <= 100 + PalPhaseMonitor.EmaConfirm + 12, "Monitor: dopo una giunta con fase girata si corregge entro ~1,5 s");
Check(PalSoftware.Filter("[0:v]", "[o]", "s", "0").Contains("1.5*"), "Guadagno crominanza 1,5 nel filtro");
var mOwn = new PalPhaseMonitor(); int tO1 = -1; for (int i = 0; i < 50; i++) if (mOwn.Observe(Img(30, 20, i)) && tO1 < 0) { tO1 = i; mOwn.ExpectFlip(); }
bool tO = false; for (int i = 50; i < 110; i++) tO |= mOwn.Observe(Img(30, i < 53 ? 20 : -20, i));   // 3 fotogrammi di latenza, poi colori giusti
Check(tO1 > 0 && !tO, "Monitor: il rovesciamento chiesto da noi non conta e dopo non si tocca più");
var mCut = new PalPhaseMonitor(); bool tC = false; for (int i = 0; i < 70; i++) { var f = Img(30, -20, i); if (i >= 60) { f = Img(-25, 20, i * 7 + 40); for (int q = 0; q < NoSignalDetector.W * NoSignalDetector.H; q++) f[q] = (byte)(255 - f[q]); } tC |= mCut.Observe(f); }
Check(!tC, "Monitor: cambio di scena (luce calda dopo luce fredda) non scambiato per un rovesciamento");
var tvS = Settings(); var tvArgs1 = Args(tvS, false); tvS.Sources[0].TvStandard = "PAL_60"; var tvArgs2 = Args(tvS, false);
Check(tvArgs1 == tvArgs2 && !tvArgs2.Contains("PAL_60"), "Il comando ffmpeg non cambia con lo standard del grabber");
if (args.Length > 0)
{
    Directory.CreateDirectory(args[0]); s = Settings(); s.Encoder = "libx264";
    File.WriteAllText(Path.Combine(args[0], "graphs.json"), JsonSerializer.Serialize(new {
        record = Args(s, false), recordAnalysis = Args(s, false, analysis:true), recordSilent = Args(s, false, false), gpu,
        ntsc = Args(new AppSettings { Fps="59.94", Encoder="libx264", Sources=s.Sources },false)
    }));
}
if(args.Length>2) RecorderChecks.Run(args[0],args[2],Check);
Console.WriteLine($"{checks} controlli superati.");
