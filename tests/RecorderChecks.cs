using System.Diagnostics;
using System.Reflection;
using VHSCapture;

static class RecorderChecks
{
    public static void Run(string artifacts, string executable, Action<bool,string> check)
    {
        executable = Path.GetFullPath(executable);
        if (!Path.GetFullPath(FFmpeg.ExePath).Equals(executable, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(FFmpeg.ExePath)) File.Delete(FFmpeg.ExePath);
            if (OperatingSystem.IsWindows()) File.Copy(executable, FFmpeg.ExePath);
            else File.CreateSymbolicLink(FFmpeg.ExePath, executable);
        }
        var probes=FFmpeg.ProbeH264Encoders(true);
        check(probes.Any(x=>x.Encoder=="h264_nvenc"),"NVENC rimane visibile anche se il test fallisce o non è incluso");
        check(probes.Any(x=>x.Encoder=="libx264"&&x.Works),"Prova reale x264 disponibile");
        check(probes.All(x=>!string.IsNullOrWhiteSpace(x.Detail)),"Tutti gli encoder hanno un esito diagnostico leggibile");
        var nvenc=probes.Single(x=>x.Encoder=="h264_nvenc");
        check(nvenc.Works||nvenc.Detail.Length>0,"NVENC fallito conserva il motivo originale");
        var refreshed=FFmpeg.ProbeH264Encoders(true);
        check(!ReferenceEquals(probes[0],refreshed[0]),"Nuova verifica ignora gli esiti memorizzati");
        var bytes = File.ReadAllBytes(Path.Combine(artifacts,"feed.ts"));
        int at = bytes.Length / 4 / 188 * 188;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string Mux(string path, AppSettings settings = null) => (string)typeof(CaptureEngine).GetMethod("MuxArgs",BindingFlags.Static|BindingFlags.NonPublic)
            .Invoke(null,new object[]{settings ?? new AppSettings { FragmentedMp4=true },Path.GetFullPath(path)});
        void Feed(TsRecorder r, int begin, int end)
        {
            for(int i=begin;i<end;)
            {
                int n=Math.Min(7913,end-i); var chunk=bytes.AsSpan(i,n).ToArray();
                lock(r) r.Feed(chunk,n); i+=n;
            }
        }
        bool Wait(Func<bool> condition)
        {
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed.TotalSeconds<8) { if(condition())return true; Thread.Sleep(20); }
            return condition();
        }
        void Decode(string file)
        {
            var psi=FFmpeg.Psi("-hide_banner -loglevel error -xerror -i \""+Path.GetFullPath(file)+"\" -f null -");
            using var p=Process.Start(psi);
            var error=p.StandardError.ReadToEndAsync(); var stdout=p.StandardOutput.ReadToEndAsync();
            if(!p.WaitForExit(10000)) { p.Kill(); throw new Exception("Decode timeout"); }
            Task.WaitAll(error,stdout);
            check(p.ExitCode==0 && new FileInfo(file).Length>4096,"File del registratore decodificabile: "+Path.GetFileName(file)+" "+error.Result);
        }
        var r=new TsRecorder();
        r.Log += line => Console.WriteLine("  "+line);
        Feed(r,0,at);
        check(r.HasBufferedKeyFrame,"Pre-roll PAT/PMT/H264 letto da TS reale frammentato");
        var output=Path.Combine(artifacts,"recorder.mp4"); if(File.Exists(output))File.Delete(output);
        r.Start(Mux(output)); Feed(r,at,bytes.Length);
        check(Wait(()=>r.OutputStarted),"Avvio confermato dal progresso del muxer reale");
        check(r.Stop(),"Chiusura del muxer reale senza errori"); Decode(output);

        // Stesso registratore, pause/ripresa: timestamps ricuciti e pre-roll conservato.
        r.ResetStream(); Feed(r,0,at); output=Path.Combine(artifacts,"paused.mp4"); if(File.Exists(output))File.Delete(output);
        r.Start(Mux(output)); Feed(r,at,at*2); r.Pause(); Feed(r,at*2,at*3);
        check(r.IsPaused,"Pausa al confine del frame video"); r.Resume(); Feed(r,at*3,bytes.Length);
        check(r.Stop(),"Chiusura dopo pausa/ripresa"); Decode(output);

        foreach (var format in new[] { "standard", "mkv", "segment", "silent" })
        {
            r.ResetStream(); Feed(r,0,at);
            string pattern = format == "segment" ? "segment_%03d.mp4" : format + (format == "mkv" ? ".mkv" : ".mp4");
            foreach(var old in Directory.GetFiles(artifacts,format+"*."+(format=="mkv"?"mkv":"mp4"))) File.Delete(old);
            var command = Mux(Path.Combine(artifacts,pattern),new AppSettings { FragmentedMp4=false, SplitMinutes=format=="segment"?1:0 });
            if(format=="segment") command=command.Replace("-segment_time 60","-segment_time 1");
            if(format=="silent") command=command.Replace("-map 0 ","-map 0:v ");
            r.Start(command); Feed(r,at,bytes.Length);
            check(Wait(()=>r.OutputStarted)&&r.LastError==null,"Avvio formato "+format);
            check(r.Stop(),"Chiusura formato "+format);
            var files=Directory.GetFiles(artifacts,format+"*."+(format=="mkv"?"mkv":"mp4"));
            check(files.Length>0 && (format!="segment"||files.Length>1),"Uscite create: "+format);
            foreach(var file in files) Decode(file);
        }

        // Mancanza di permessi/destinazione inesistente non deve lasciare un falso REC.
        r.ResetStream(); Feed(r,0,at);
        r.Start(Mux(Path.Combine(artifacts,"directory-inesistente", "fail.mp4"))); Feed(r,at,bytes.Length);
        check(Wait(()=>r.LastError!=null),"Errore reale apertura destinazione rilevato");
        check(!r.Stop()&&!r.MuxAlive&&!r.IsRecording,"Errore chiude processo e stato registrazione");

        r.ResetStream(); Feed(r,0,at); output=Path.Combine(artifacts,"recovered.mp4"); if(File.Exists(output))File.Delete(output);
        r.Start(Mux(output)); Feed(r,at,bytes.Length);
        check(Wait(()=>r.OutputStarted)&&r.LastError==null,"Seconda registrazione parte dopo il fallimento");
        check(r.Stop(),"Chiusura dopo recupero"); Decode(output);
        var previous=File.ReadAllBytes(output);
        r.Start(Mux(output)); Feed(r,0,bytes.Length);
        check(Wait(()=>r.LastError!=null),"File già esistente rifiutato dal muxer"); r.Stop();
        check(previous.SequenceEqual(File.ReadAllBytes(output)),"File precedente mai sovrascritto");

        r.ResetStream(); Feed(r,0,at); output=Path.Combine(artifacts,"killed.mp4"); if(File.Exists(output))File.Delete(output);
        r.Start(Mux(output)); Feed(r,at,bytes.Length);
        check(Wait(()=>r.OutputStarted),"Registrazione attiva prima del guasto simulato");
        ((Process)typeof(TsRecorder).GetField("mux",flags).GetValue(r)).Kill();
        check(Wait(()=>r.LastError!=null),"Arresto inatteso del muxer rilevato");
        check(!r.Stop()&&!r.MuxAlive,"Nessun processo residuo dopo il guasto");
    }
}
