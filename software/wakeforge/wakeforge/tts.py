"""Qwen3-TTS VoiceDesign worker and per-speaker train/val/test dataset generation."""
from __future__ import annotations
import json, math, os, random, subprocess, sys, time
from pathlib import Path
from .core import token, norm, save_json, bar, split_for_profile,GENERIC_PHRASES, includes_keyword

ATTR={
 'sex':('masculine','feminine'),
 'age':('young adult','adult','mature adult','senior'),
 'pitch':('low','medium','high'),
 'timbre':('warm','deep','dry','bright','breathy','husky','nasal','soft','raspy'),
 'pace':('slow','conversational','fast'),
 'accent':('standard native Russian','colloquial native Russian'),
}
STYLES=('natural clear speech','quiet precise speech','loud energetic call',
        'hesitant gentle voice','surprised calling voice','slightly drawn out vowels')


def jobs_for(config):
    rng=random.Random(token(config['seed'],'speaker-index'))
    rows=[]
    n=config['voice_profiles']
    order=sorted(range(n), key=lambda j: token(config['seed'],'speaker',j))
    positions={speaker:i for i,speaker in enumerate(order)}
    for i in range(config['voice_profiles']):
        attrs={key:rng.choice(vals) for key,vals in ATTR.items()}
        desc=(f'A {attrs["sex"]} {attrs["age"]} native Russian speaker, '
              f'{attrs["pitch"]} pitch, {attrs["timbre"]} timbre, {attrs["pace"]} pace, '
              f'{attrs["accent"]}. Speak exactly the provided Russian text, no additional words.')
        rank=positions[i]
        split='train' if rank < int(n*.8) else 'val' if rank<int(n*.9) else 'test'
        for v in range(config['variants_per_voice']):
            rows.append(dict(id=f'p{i:06d}_{v:02d}',speaker=i,split=split,kind='positive',
                             text=config['keyword'],description=desc,style=STYLES[v%len(STYLES)]))
        for v in range(config['negative_per_voice']):
            words=config['negative_words'];w=words[token(config['seed'],i,v,'hard')%len(words)]
            if includes_keyword(w,config['keyword']):
                raise ValueError(f'Похожий негатив содержит слово активации: {w}')
            rows.append(dict(id=f'h{i:06d}_{v:02d}',speaker=i,split=split,kind='hard',
                             text=w,description=desc,style=STYLES[v%len(STYLES)]))
        for v in range(config['generic_speech_per_voice']):
            phrases=config.get('generic_phrases', GENERIC_PHRASES)
            t=phrases[token(config['seed'],i,v,'speech')%len(phrases)]
            if includes_keyword(t,config['keyword']):
                raise ValueError(f'Отрицательная фраза содержит слово активации: {t}')
            rows.append(dict(id=f's{i:06d}_{v:02d}',speaker=i,split=split,kind='speech',
                             text=t,description=desc,style=STYLES[v%len(STYLES)]))
    return rows


def real_jobs_for(cfg, paths):
    """Additional jobs generated from consented local voice references.

    For fewer than 10 real speakers, all stay in train. Test/val remain unseen
    synthetic profiles; splitting clips from one real person would leak identity.
    """
    from .voices import list_voices
    voices=list_voices(paths)
    n=len(voices)
    rows=[]
    for i,sp in enumerate(voices):
        if n < 10:
            split='train'
        else:
            order=sorted(range(n),key=lambda j:token(cfg['seed'],'real-speaker',voices[j]['id']))
            rank=order.index(i)
            split='train' if rank<int(n*.8) else 'val' if rank<int(n*.9) else 'test'
        speaker='real_'+sp['id']
        ident=sp['id']
        ref=str((paths['root']/'voices'/ident/'reference.wav').resolve())
        common=dict(speaker=speaker,split=split,reference=ref,ref_text=sp['ref_text'],
                    real_voice=True,voice_id=ident)
        for j in range(int(cfg.get('real_positive_per_voice',24))):
            rows.append(dict(id=f'rp_{ident}_{j:04d}',kind='positive',text=cfg['keyword'],**common))
        for j in range(int(cfg.get('real_hard_per_voice',12))):
            words=cfg['negative_words']
            word=words[token(cfg['seed'],'real',ident,j,'hard')%len(words)]
            rows.append(dict(id=f'rh_{ident}_{j:04d}',kind='hard',text=word,**common))
        for j in range(int(cfg.get('real_speech_per_voice',12))):
            words=cfg.get('generic_phrases',GENERIC_PHRASES)
            word=words[token(cfg['seed'],'real',ident,j,'speech')%len(words)]
            if includes_keyword(word,cfg['keyword']):
                raise ValueError(f'Отрицательная фраза содержит слово активации: {word}')
            rows.append(dict(id=f'rs_{ident}_{j:04d}',kind='speech',text=word,**common))
    return rows


def source_path(paths,rec):
    if rec['kind']=='positive':return paths['positive']/'data'/rec['split']/'clean'/f"{rec['id']}.flac"
    return paths['negative']/rec['split']/'negative'/rec['kind']/f"{rec['id']}.flac"


def valid_labeled(path, record):
    """Verify audio AND metadata before deciding a resumed task is complete."""
    if not valid(path):return False
    try:
        data=json.loads(path.with_suffix('.json').read_text(encoding='utf-8'))
        return (data.get('source_text')==record['text'] and
                data.get('kind')==record['kind'] and data.get('split')==record['split'] and
                data.get('sample_rate')==16000 and data.get('label')==int(record['kind']=='positive') and
                0 <= int(data['word_start_sample']) < int(data['word_end_sample']) <= 64000 and
                data.get('voice_id')==record.get('voice_id'))
    except (OSError,ValueError,KeyError,TypeError):
        return False


def valid(path):
    try:
        import soundfile as sf
        i=sf.info(path);return i.samplerate==16000 and i.frames==64000 and i.channels==1
    except Exception:return False


def render_tts(y,sr,record):
    import numpy as np,scipy.signal as ss
    wav=np.asarray(y,dtype=np.float32).reshape(-1)
    if int(sr)!=16000:
        gcd=math.gcd(int(sr),16000);wav=ss.resample_poly(wav,16000//gcd,int(sr)//gcd)
    if len(wav)<3200 or not np.isfinite(wav).all():raise ValueError('Bad TTS waveform')
    # Time-bounds of the TTS utterance from energy, not forced phoneme alignment.
    hop=160
    chunk=np.pad(wav,(0,(-len(wav))%hop)).reshape(-1,hop)
    en=np.sqrt((chunk.astype(np.float64)**2).mean(axis=1))
    active=np.flatnonzero(en>max(.0015,float(en.max())*.035))
    if len(active)<12:raise ValueError('No speech detected')
    a=max(0,active[0]*hop-480);b=min(len(wav),(active[-1]+1)*hop+640)
    word=wav[a:b]
    maximum=2.6 if record['kind']=='positive' else 3.25
    if len(word)<int(.24*16000) or len(word)>int(maximum*16000):raise ValueError(f'Invalid word duration: {len(word)/16000:.2f}s')
    rng=random.Random(token(record['id'],'position'))
    target_end=round((3.35+rng.uniform(-.05,.05))*16000)
    result=np.zeros(64000,dtype=np.float32)
    start=target_end-len(word)
    if start<0 or target_end>64000:raise ValueError('Cannot place generated speech')
    result[start:target_end]=word
    peak=max(.001,float(np.max(np.abs(result))))
    result=np.clip(result*min(2.0,.86/peak),-.99,.99)
    return result,start,target_end


def worker(project,device,part,total_parts,mode="design"):
    import numpy as np,torch,soundfile as sf
    from qwen_tts import Qwen3TTSModel
    from .core import project_config
    paths,config=project_config(project)
    if device!='cpu':torch.cuda.set_device(0) # CUDA_VISIBLE_DEVICES is set by parent.
    torch.set_num_threads(max(1,min(4,(os.cpu_count() or 4)//max(1,total_parts))))
    model=Qwen3TTSModel.from_pretrained(config['tts_model'] if mode=='design' else config.get('clone_model','Qwen/Qwen3-TTS-12Hz-1.7B-Base'),
         device_map=('cpu' if device=='cpu' else 'cuda:0'),
         dtype=(torch.float32 if device=='cpu' else torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16),
         attn_implementation='sdpa')
    records=jobs_for(config) if mode=='design' else real_jobs_for(config,paths)
    if mode=='clone':
        # Keep each real speaker on one worker; one reference prompt built per voice.
        ids=sorted({r['voice_id'] for r in records})
        assignment={voice_id:i%total_parts for i,voice_id in enumerate(ids)}
        subset=[r for r in records if assignment[r['voice_id']]==part]
    else:
        subset=[r for i,r in enumerate(records) if i%total_parts==part]
    # One cached ICL prompt per human speaker: the reference audio is not
    # re-encoded for every generated training example.
    clone_prompts={}
    if mode=='clone':
        for r in subset:
            ident=r['voice_id']
            if ident not in clone_prompts:
                clone_prompts[ident]=model.create_voice_clone_prompt(
                    ref_audio=r['reference'],ref_text=r['ref_text'],x_vector_only_mode=False)

    log=paths['logs']/f'tts_{mode}_{part}.jsonl';log.parent.mkdir(parents=True,exist_ok=True)
    start=time.monotonic();done=skipped=bad=0
    for r in subset:
        target=source_path(paths,r)
        if valid_labeled(target,r):skipped+=1;continue
        target.parent.mkdir(parents=True,exist_ok=True)
        for attempt in range(3):
            try:
                torch.manual_seed(token(config['seed'],r['id'],attempt)%(2**31))
                with torch.inference_mode():
                    if mode=='clone':
                        # Text-conditioned ICL cloning, not x-vector-only cloning.
                        waves,rate=model.generate_voice_clone(
                            text=r['text'],language=config['language'],
                            voice_clone_prompt=clone_prompts[r['voice_id']],
                            do_sample=True,temperature=.7,top_p=.92)
                    else:
                        waves,rate=model.generate_voice_design(text=r['text'],language=config['language'],
                            instruct=r['description']+' Delivery: '+r['style'],do_sample=True,
                            temperature=.7,top_p=.92)
                wave,a,b=render_tts(waves[0],rate,r)
                tmp=target.with_name(target.name+'.part')
                with tmp.open('wb') as f:sf.write(f,wave,16000,format='FLAC',subtype='PCM_16')
                os.replace(tmp,target)
                meta=dict(file=(target.relative_to(paths['positive']).as_posix() if r['kind']=='positive' else target.relative_to(paths['negative']).as_posix()),
                          word_start_sample=a,word_end_sample=b,sample_rate=16000,label=int(r['kind']=='positive'),
                          keyword=config['keyword'] if r['kind']=='positive' else None,
                          profile=r['speaker'],source_text=r['text'],kind=r['kind'],split=r['split'],
                          real_voice=bool(r.get('real_voice',False)),voice_id=r.get('voice_id'))
                save_json(target.with_suffix('.json'),meta)
                done+=1;break
            except Exception as exc:
                if attempt==2:
                    with log.open('a',encoding='utf-8') as fp:fp.write(json.dumps(dict(id=r['id'],error=str(exc)),ensure_ascii=False)+'\n')
                    bad+=1
        if (done+skipped+bad)%25==0 or (done+skipped+bad)==len(subset):
            bar(f'TTS {mode} {part} [{device}]',done+skipped+bad,len(subset),start)
    print(f'worker {part}: written={done}, cached={skipped}, failed={bad}',flush=True)
    return int(bad>0)  # Any missing task is retried on rerun; never report incomplete as success.


def generate(project,paths,config,hw):
    from .voices import list_voices
    records=jobs_for(config)+real_jobs_for(config,paths)
    speaker_signatures=[dict(id=v['id'],sha256=v['sha256'],ref_text=v['ref_text']) for v in list_voices(paths)]
    plan=paths['root']/'tts_plan.json'
    sig=dict(keyword=config['keyword'],voices=config['voice_profiles'],variants=config['variants_per_voice'],
             hard=config['negative_words'],hard_per_voice=config['negative_per_voice'],
             speech_per_voice=config['generic_speech_per_voice'],generic_phrases=config.get('generic_phrases',GENERIC_PHRASES),
             seed=config['seed'],tts_model=config['tts_model'],
             real_voices=speaker_signatures,
             real_counts=(config.get('real_positive_per_voice',24),config.get('real_hard_per_voice',12),
                          config.get('real_speech_per_voice',12)),
             clone_model=config.get('clone_model','Qwen/Qwen3-TTS-12Hz-1.7B-Base'))
    if plan.exists():
        if json.loads(plan.read_text(encoding='utf-8'))!=sig:raise RuntimeError('Генерация ранее начата с другой конфигурацией. Создайте новый проект.')
    else:save_json(plan,sig)
    gpus=config['voice_gpus'];devices=[str(x) for x in gpus] if gpus else ['cpu']
    # Loading VoiceDesign and Base simultaneously is too expensive on small GPUs.
    # Synthesize all designed voices first, unload workers, then load Base to clone.
    for mode in ('design','clone'):
        if mode=='clone' and not speaker_signatures:
            continue
        selected=devices if mode=='design' else devices[:min(len(speaker_signatures),len(devices))]
        print(f'\nTTS PHASE: {mode.upper()} | GPU workers: {len(selected)}',flush=True)
        _run_tts_workers(paths,selected,mode)
    print('TTS COMPLETE',flush=True)


def _run_tts_workers(paths,devices,mode):
    running=[]
    for i,gpu in enumerate(devices):
        env=os.environ.copy();env['CUDA_VISIBLE_DEVICES']='' if gpu=='cpu' else gpu
        env['OMP_NUM_THREADS']='2';env['OPENBLAS_NUM_THREADS']='1'
        env['HF_HUB_CACHE']=str(paths['cache']/'hf')
        log=paths['logs']/f'generator_{mode}_{i}.log';log.parent.mkdir(parents=True,exist_ok=True)
        stream=log.open('a',encoding='utf-8')
        cmd=[sys.executable,'-u','-m','wakeforge','_tts_worker','--project',str(paths['root']),
             '--device','cpu' if gpu=='cpu' else 'cuda','--part',str(i),'--total',str(len(devices)), '--mode',mode]
        proc=subprocess.Popen(cmd,env=env,stdout=stream,stderr=subprocess.STDOUT)
        running.append((proc,stream,log))
        print(f'TTS {mode} worker {i} on {gpu}: PID={proc.pid} log={log}',flush=True)
    try:
        while any(proc.poll() is None for proc,_,_ in running):
            for index,(proc,stream,log) in enumerate(running):
                if log.is_file():
                    with log.open('rb') as f:
                        f.seek(max(0,f.seek(0,2)-1600))
                        lines=f.read().decode('utf-8',errors='replace').splitlines()
                    status=next((line for line in reversed(lines) if line.startswith('TTS ') or line.startswith('worker ')),None)
                    if status:print(f'GPU worker {index}: {status}',flush=True)
            time.sleep(8)
        codes=[]
        for proc,stream,log in running:
            code=proc.wait();codes.append(code);stream.close()
            print(f'TTS worker exit={code}: {log}',flush=True)
    except KeyboardInterrupt:
        for proc,_,_ in running:
            if proc.poll() is None:proc.terminate()
        raise
    if any(codes):raise RuntimeError('Некоторые TTS работники завершились с ошибкой. См. logs/generator_*.log')
