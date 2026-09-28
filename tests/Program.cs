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
double[] Flat(double y = 16, double u = 128, double v = 128) => new[] { y,y,u,u,v,v,y,y,u,u,v,v,0d,0d,0d };
string ObserveFor(NoSignalDetector detector, double[] values, double begin, double end, bool audio = false)
{
    string state = "";
    for (int frame = 0; begin + frame / 25d <= end; frame++) state = detector.Observe(values, begin + frame / 25d, audio);
    return state;
}
foreach (var color in new[] { ("nero",16d,128d,128d), ("grigio",128d,128d,128d), ("bianco",235d,128d,128d), ("blu",41d,240d,110d), ("rosso",81d,90d,240d), ("verde",145d,54d,34d), ("giallo",210d,16d,146d) })
{
    var detector = new NoSignalDetector(); var flat = Flat(color.Item2,color.Item3,color.Item4);
    Check(ObserveFor(detector,flat,0,11.96)=="verifica" && detector.Observe(flat,12)=="assenza probabile", "Conferma continua, nessun allarme anticipato: " + color.Item1);
}
var det = new NoSignalDetector();
var dark = Flat(); dark[7] = 22;
Check(ObserveFor(det,dark,0,40)=="contenuto", "Dettaglio piccolo e buio protetto anche se i percentili sono identici");
var chroma = Flat(41,240,110); chroma[8]=230;
Check(ObserveFor(det,chroma,41,80)=="contenuto", "Pattern blu cromatico protetto");
var motion = Flat(); motion[12]=.1;
Check(ObserveFor(det,motion,81,120)=="contenuto", "Movimento debolissimo impedisce lo stop");
det.Reset();
Check(ObserveFor(det,Flat(),0,40,true)=="contenuto", "Audio attivo protegge anche il nero pieno");
Check(det.Observe(Flat(),40.04)=="verifica", "Dopo audio riparte la conferma");
Check(ObserveFor(det,Flat(),40.08,53)=="assenza probabile", "Conferma riparte dopo il contenuto");
Check(det.Observe(dark,53.04)=="contenuto" && det.Observe(Flat(),53.08)=="verifica", "Ritorno di contenuto cancella subito la conferma");
Check(det.Observe(null,53.12)=="" && det.Observe(Flat(),53.16)=="verifica", "Metadati mancanti azzerano la conferma");
ObserveFor(det,Flat(),54,70);
Check(det.Observe(Flat(),73)=="verifica", "Interruzione dell'analisi non conta come segnale assente");
var bad = Flat(); bad[0]=double.NaN;
Check(det.Observe(bad,74)=="", "Valori non finiti non attivano lo stop");
det.Reset(); bool fading = false;
for (int frame=0;frame<1000;frame++) fading |= det.Observe(Flat(16+frame*.01), frame/25d)=="assenza probabile";
Check(!fading,"Dissolvenza lenta non viene scambiata per schermo fermo");
string ParseSignal(IEnumerable<string> values, bool audioSource = false, bool meterFresh = false, bool audioActive = false)
{
    using var engine = new CaptureEngine(); string result=null;
    if(audioSource) typeof(CaptureEngine).GetField("runningSources",flags).SetValue(engine,new Dictionary<string,Source>{["test"]=new Source{AudioDevice="audio"}});
    if(meterFresh) ((System.Collections.Concurrent.ConcurrentDictionary<string,double>)typeof(CaptureEngine).GetField("audioObserved",flags).GetValue(engine))["test"]=0;
    if(audioActive) ((System.Collections.Concurrent.ConcurrentDictionary<string,double>)typeof(CaptureEngine).GetField("audioActivity",flags).GetValue(engine))["test"]=0;
    engine.SignalState += (_,uniform,kind) => result=kind;
    var parse=typeof(CaptureEngine).GetMethod("OnAnalysisLine",flags);
    foreach(var v in values) parse.Invoke(engine,new object[]{"test","lavfi.signalstats."+v});
    parse.Invoke(engine,new object[]{"test","frame:1"});
    return result;
}
Check(ParseSignal(NoSignalDetector.Keys.Select((key,i)=>key+"="+Flat(128)[i].ToString(System.Globalization.CultureInfo.InvariantCulture))) == "verifica", "Parser completo attende conferma prima dello stop");
Check(ParseSignal(new[]{"YLOW=128"}) == "", "Parser rifiuta frame incompleti");
var metadata = NoSignalDetector.Keys.Select((key,i)=>key+"="+Flat(128)[i].ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
Check(ParseSignal(metadata,audioSource:true)=="","Audio previsto ma non misurabile impedisce lo stop");
Check(ParseSignal(metadata,audioSource:true,meterFresh:true)=="verifica","Silenzio realmente misurato permette la conferma");
Check(ParseSignal(metadata,audioSource:true,meterFresh:true,audioActive:true)=="contenuto","Audio misurato impedisce il falso allarme sul nero");
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
if (args.Length > 0)
{
    Directory.CreateDirectory(args[0]); s = Settings(); s.Encoder = "libx264";
    File.WriteAllText(Path.Combine(args[0], "graphs.json"), JsonSerializer.Serialize(new {
        record = Args(s, false), recordAnalysis = Args(s, false, analysis:true), recordSilent = Args(s, false, false), gpu,
        ntsc = Args(new AppSettings { Fps="59.94", Encoder="libx264", Sources=s.Sources },false)
    }));
}
if(args.Length>1)
{
    foreach(var x in JsonDocument.Parse(File.ReadAllText(args[1])).RootElement.EnumerateArray())
    {
        var samples=x.GetProperty("frames").EnumerateArray().Select(frame=>frame.EnumerateArray().Select(t=>t.GetDouble()).ToArray()).ToArray();
        var detector=new NoSignalDetector(); bool detected=false;
        for(int frame=0;frame<400;frame++) detected |= detector.Observe(samples[frame % samples.Length],frame/25d)=="assenza probabile";
        Check(detected==x.GetProperty("expected").GetBoolean(),"FFmpeg signalstats reale: "+x.GetProperty("name").GetString());
    }
}
if(args.Length>2) RecorderChecks.Run(args[0],args[2],Check);
Console.WriteLine($"{checks} controlli superati.");
