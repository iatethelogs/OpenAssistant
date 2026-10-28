"""ASR screen, manifest assembly, supplemental speech and noise negatives."""
from __future__ import annotations
import json, os, random, re, time
from pathlib import Path
from .core import SPLITS,bar,norm,save_json,token
from .tts import jobs_for,source_path,valid


def audit_and_assemble(paths,config,hw,skip_asr=False):
    from .tts import real_jobs_for
    records=[r for r in jobs_for(config)+real_jobs_for(config,paths) if r['kind']=='positive']
    cache=paths['positive']/'asr_cache.jsonl';old={}
    if cache.exists():
        for line in cache.read_text(encoding='utf-8').splitlines():
            if line.strip():
                row=json.loads(line);old[row['file']]=row
    pending=[r for r in records if valid(source_path(paths,r))]
    model=None
    if not skip_asr:
        from faster_whisper import WhisperModel
        requested=config.get('asr_device','auto')
        if requested=='auto':requested='cuda' if hw['tts_gpus'] else 'cpu'
        for device in ([requested,'cpu'] if requested=='cuda' else [requested]):
            try:
                model=WhisperModel(config['asr_model'],device=device,
                     compute_type='float16' if device=='cuda' else 'int8',cpu_threads=max(1,min(8,hw['cpu_threads'])))
                print('Whisper:',device,flush=True);break
            except Exception as exc:
                print(f'Whisper {device} unavailable: {exc}',flush=True)
        if model is None:raise RuntimeError('Whisper failed on GPU and CPU')
    out={k:[] for k in SPLITS};stats={'accepted':0,'rejected':0,'missing':len(records)-len(pending),'cached':0}
    start=time.monotonic()
    fp=cache.open('a',encoding='utf-8')
    try:
        for i,r in enumerate(pending,1):
            p=source_path(paths,r);st=p.stat();key=str(p.resolve())
            cached=old.get(key)
            if cached and cached.get('size')==st.st_size and cached.get('mtime_ns')==st.st_mtime_ns and cached.get('model')==config['asr_model'] and cached.get('transcript')!='NOT_AUDITED':
                accepted=bool(cached['accepted']);stats['cached']+=1
            else:
                if skip_asr:accepted=True;transcript='NOT_AUDITED'
                else:
                    seg,_=model.transcribe(str(p),language='ru' if config['language']=='Russian' else None,
                                           beam_size=4,condition_on_previous_text=False,vad_filter=False)
                    transcript=' '.join(x.text.strip() for x in seg)
                    accepted=norm(transcript)==norm(config['keyword'])
                fp.write(json.dumps(dict(file=key,size=st.st_size,mtime_ns=st.st_mtime_ns,
                    model='UNVERIFIED' if skip_asr else config['asr_model'],transcript=transcript,accepted=accepted),ensure_ascii=False)+'\n')
                if i%30==0:fp.flush()
            if accepted:
                meta=json.loads(p.with_suffix('.json').read_text(encoding='utf-8'))
                out[r['split']].append(meta);stats['accepted']+=1
            else:stats['rejected']+=1
            if i%50==0 or i==len(pending):bar('ASR audit',i,len(pending),start)
    finally:fp.close()
    for split,items in out.items():
        manifest=paths['positive']/f'positive_{split}.jsonl'
        manifest.parent.mkdir(parents=True,exist_ok=True)
        # atomic file replacement; other source files remain immutable.
        temp=manifest.with_suffix('.tmp')
        with temp.open('w',encoding='utf-8') as f:
            for item in items:f.write(json.dumps(item,ensure_ascii=False)+'\n')
        os.replace(temp,manifest)
        if not items:raise RuntimeError(f'No positive examples in {split}; increase voices or inspect ASR')
    save_json(paths['positive']/'quality_report.json',stats)
    if stats['missing']:
        raise RuntimeError(f'Отсутствуют {stats["missing"]} положительных TTS-записей. Повторите генерацию.')
    print('POSITIVE AUDIT:',stats,flush=True)


def make_noise(paths, config):
    """Deterministic quiet + loud negatives, including wind and clipping.

    Loud sounds should NOT alone define the negative class: the training set
    already contains loud target-word TTS styles and mixed positive clips.
    """
    import numpy as np
    import soundfile as sf
    from scipy.signal import butter, sosfilt

    version='wakeforge-noise-v2'
    count=max(48, int(config['voice_profiles'])*int(config.get('generic_speech_per_voice', 2)))
    spec={'version':version,'seed':config['seed'],'count':count}
    marker=paths['negative']/'noise_plan.json'
    previous=json.loads(marker.read_text(encoding='utf-8')) if marker.exists() else None
    replace_old = previous!=spec
    # Even on reruns, the files are split by deterministic clip IDs.
    for split in SPLITS:
        total=max(1,round(count*{'train':.8,'val':.1,'test':.1}[split]))
        folder=paths['negative']/split/'negative'/'noise'
        folder.mkdir(parents=True,exist_ok=True)
        start=time.monotonic()
        for i in range(total):
            path=folder/f'noise_{i:07d}.flac'
            if not replace_old and valid(path):continue
            rng=np.random.default_rng(token(config['seed'],'noise',split,i))
            waveform=rng.standard_normal(64000).astype(np.float32)
            kind=i%8
            if kind==1:  # Colored noise
                f=np.fft.rfftfreq(len(waveform),1/16000)
                waveform=np.fft.irfft(np.fft.rfft(waveform)/np.maximum(f,5)**.5,
                                      n=len(waveform)).astype('float32')
            elif kind in (2,4,7):  # Wind, including strong low-frequency gusts
                waveform=sosfilt(butter(2,160 if kind==4 else 800,
                            fs=16000,output='sos'),waveform).astype(np.float32)
                t=np.arange(64000,dtype=np.float32)/16000
                envelope=(.20+.8*(.5+.5*np.sin(2*np.pi*(.5+(.1*i)%3)*t)))
                waveform=waveform*envelope
                if kind==7:
                    waveform+=.20*np.sin(2*np.pi*(55+i%90)*t).astype(np.float32)
            elif kind==3:
                t=np.arange(64000)/16000
                waveform=.20*waveform+np.sin(2*np.pi*(80+i%140)*t).astype('float32')
            elif kind==5:  # Short explosive blast near a microphone
                pulse=np.zeros(64000,dtype=np.float32)
                center=int(rng.integers(5000,58000))
                length=round(.18*16000)
                pulse[center:center+length]=rng.standard_normal(length).astype('float32')
                waveform=.07*waveform+pulse
            elif kind==6:  # Saturation with broadband noise
                waveform=np.tanh(waveform*3.0).astype(np.float32)
            # A variety of input levels, rather than a fixed 0.08 peak.
            targets=(.08,.12,.18,.10,.70,.85,.95,.82)
            waveform*=targets[kind]/max(.00001,float(np.max(np.abs(waveform))))
            if kind==6:
                waveform=np.clip(waveform*2.3,-.99,.99)
            if not np.isfinite(waveform).all():
                raise RuntimeError(f'Invalid generated noise {split}/{i}')
            temp=path.with_suffix('.part')
            with temp.open('wb') as fp:
                sf.write(fp,waveform,16000,format='FLAC',subtype='PCM_16')
            os.replace(temp,path)
            if i%100==0 or i==total-1:
                bar(f'NOISE {split}',i+1,total,start)
        # Remove stale files if dataset volume was reduced between experiments.
        for stale in folder.glob('noise_*.flac'):
            try:
                index=int(stale.stem.rsplit('_',1)[-1])
            except ValueError:continue
            if index>=total:stale.unlink()
        print(f'NOISE {split} READY {total}',flush=True)
    save_json(marker,spec)


def import_speech(paths,dirpath,max_files=10000):
    """Additional natural speech without creating duplicate/synthetic Golos labels."""
    import numpy as np,soundfile as sf
    from scipy.signal import resample_poly
    import math
    root=Path(dirpath).expanduser().resolve()
    if not root.is_dir():raise FileNotFoundError(root)
    files=sorted(p for p in root.rglob('*') if p.suffix.lower() in ('.wav','.flac','.ogg'))
    taken=0
    for path in files:
        if taken>=max_files:break
        try:
            arr,sr=sf.read(path,dtype='float32');arr=arr.mean(axis=1) if arr.ndim==2 else arr
            if len(arr)<int(sr*.5):continue
            if sr!=16000:
                d=math.gcd(int(sr),16000);arr=resample_poly(arr,16000//d,int(sr)//d).astype(np.float32)
            if len(arr)<64000:arr=np.pad(arr,(0,64000-len(arr)))
            if len(arr)>64000:
                start=(len(arr)-64000)//2;arr=arr[start:start+64000]
            if not np.isfinite(arr).all():continue
            # Unknown speaker IDs MUST NOT be randomly split by recording file:
            # the same speaker would otherwise leak into train and test.
            # For a nested dataset, the first directory is interpreted as a
            # speaker group. Unstructured flat files stay training-only.
            rel=path.relative_to(root)
            if len(rel.parts)<2:
                split='train'
            else:
                bucket=token('speech-speaker-group',rel.parts[0])%10
                split='train' if bucket<8 else 'val' if bucket==8 else 'test'
            out=paths['negative']/split/'negative'/'speech'/('real_'+str(token(str(path)))+'.flac')
            out.parent.mkdir(parents=True,exist_ok=True)
            if not valid(out):
                temp=out.with_suffix('.part')
                with temp.open('wb') as f:sf.write(f,np.clip(arr,-.99,.99),16000,format='FLAC',subtype='PCM_16')
                os.replace(temp,out)
            taken+=1
        except Exception:continue
    print(f'IMPORTED {taken} real speech recordings from {root} (grouped by first folder; flat files are train-only)',flush=True)
