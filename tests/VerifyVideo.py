import json,re,subprocess,time,argparse,os
from pathlib import Path
parser=argparse.ArgumentParser()
parser.add_argument('--ffmpeg', required=True)
parser.add_argument('--artifacts', default='verification')
opts=parser.parse_args()
base=Path(opts.artifacts);base.mkdir(exist_ok=True)
ff=Path(opts.ffmpeg).resolve()
data=json.loads((base/'graphs.json').read_text())
for case in ('record','recordSilent','recordAnalysis'):
    graph=re.search(r'-filter_complex "([^"]+)"',data[case]).group(1)
    graph=graph.replace('[0:a]','[1:a]').replace('file=NUL','file='+os.devnull)
    graph=re.sub(r'file=[^;]+(?=\[ano0\])',lambda _: 'file='+os.devnull,graph)
    args=[str(ff),'-hide_banner','-y','-benchmark','-filter_complex_threads','4',
          '-re','-f','lavfi','-i','testsrc2=s=720x576:r=25:d=5',
          '-f','lavfi','-i','sine=frequency=1000:sample_rate=48000:duration=5',
          '-filter_complex',graph,'-map','[venc]','-map','[aenc]',
          '-c:v','libx264','-preset','veryfast','-b:v','12000k','-g','100','-c:a','aac',str(base/f'{case}.mp4'),
          '-map','[pvs]','-f','framemd5',str(base/f'{case}.md5'),'-map','[amo0]','-f','null',os.devnull]
    if case=='recordAnalysis':args+=['-map','[ano0]','-f','null',os.devnull]
    if case!='recordSilent':args+=['-map','[amons]','-f','s16le',str(base/f'{case}.pcm')]
    r=subprocess.run(args,capture_output=True,text=True,timeout=30)
    (base/f'{case}.log').write_text(r.stderr)
    if r.returncode:raise RuntimeError(case+'\n'+r.stderr[-6000:])
    md5=(base/f'{case}.md5').read_text()
    frames=[x for x in md5.splitlines() if x and not x.startswith('#')]
    assert len(frames) in (248,249,250), (case,len(frames))
    assert '#dimensions 0: 960x540' in md5 and '#tb 0: 1/50' in md5,md5[:500]
    if case!='recordSilent':assert (base/f'{case}.pcm').stat().st_size==5*48000*2*2
    r=subprocess.run([str(ff),'-hide_banner','-i',str(base/f'{case}.mp4'),'-map','0:v','-f','framemd5','-'],capture_output=True,text=True,check=True)
    rows=[x for x in r.stdout.splitlines() if x and not x.startswith('#')]
    assert len(rows)==len(frames), (len(rows),len(frames))
    assert '#dimensions 0: 1920x1080' in r.stdout and '#tb 0: 1/50' in r.stdout
    assert 'Audio: aac' in r.stderr
    print(case,'PASS: encoder e anteprima entrambi a 50 fps,',len(rows),'frame; file 1920x1080 e AAC',flush=True)
# Produce statistiche reali da inoltrare al classificatore C# della stessa applicazione.
signals=[]
keys=('YLOW','YHIGH','ULOW','UHIGH','VLOW','VHIGH','YMIN','YMAX','UMIN','UMAX','VMIN','VMAX','YDIF','UDIF','VDIF')
cases={c:(f'color=c={c}:s=720x576:r=25:d=0.16',True) for c in ('black','gray','white','blue','red','green','yellow','cyan','magenta')}
cases.update({
    'details':('testsrc2=s=720x576:r=25:d=1',False),
    'chroma_details':("nullsrc=s=720x576:r=25:d=0.16,geq=lum=128:cb='if(lt(X,W/2),40,210)':cr=128",False),
    'dark_scene':("testsrc2=s=720x576:r=25:d=1,lutyuv=y='16+(val-16)*0.04':u=128:v=128",False),
    'dark_small_detail':("color=black:s=720x576:r=25:d=0.16,drawbox=x=300:y=230:w=30:h=30:c=0x101010:t=fill",False),
    'blue_pattern':("color=blue:s=720x576:r=25:d=0.16,drawbox=x=300:y=230:w=30:h=30:c=0x0000e0:t=fill",False),
    'faint_motion':("nullsrc=s=720x576:r=25:d=1,geq=lum='16+mod(N,2)':cb=128:cr=128",False),
    'noise':("color=black:s=720x576:r=25:d=1,noise=alls=8:allf=t",False),
})
for name,(source,expected) in cases.items():
    r=subprocess.run([str(ff),'-hide_banner','-f','lavfi','-i',source,'-vf','crop=w=iw:h=ih-8:x=0:y=0,scale=192:108:flags=area,format=yuv444p,signalstats,metadata=mode=print','-f','null',os.devnull],capture_output=True,text=True,check=True)
    (base/f'signal-{name}.log').write_text(r.stderr)
    frames=[]
    for block in re.split(r'frame:\d+',r.stderr)[1:]:
        vals=[float(re.search(r'lavfi.signalstats.'+k+r'=([\d.]+)',block).group(1)) for k in keys]
        frames.append(vals)
    assert frames,name
    signals.append({'name':name,'frames':frames,'expected':expected})
(base/'signals.json').write_text(json.dumps(signals,indent=2))
print(len(signals),'sequenze signalstats pronte per il classificatore C#',flush=True)
# MPEG-TS reale usato dal registratore C# per avvio, pause e gestione errori.
subprocess.run([str(ff),'-hide_banner','-loglevel','error','-y','-f','lavfi','-i','testsrc2=s=320x240:r=25:d=8','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=8','-c:v','libx264','-preset','ultrafast','-g','25','-bf','0','-c:a','aac','-mpegts_flags','+resend_headers','-f','mpegts',str(base/'feed.ts')],check=True)
