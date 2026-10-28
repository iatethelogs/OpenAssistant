#!/usr/bin/env python3
"""Martin V6: standalone, resumable positive audio augmentation (no training).

Sources: assembled positive_{train,val,test}.jsonl, SLR26/SLR28 RIRs/noises,
          optional MUSAN/DNS recordings, V2/Golos negative speech.
Outputs: FLAC 16 kHz mono, exact four seconds, JSONL manifests and provenance.

Requires: numpy, scipy, soundfile. Does not import martin_v6_train or change source data.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import random
import shutil
import sys
import time
from collections import Counter
from concurrent.futures import ProcessPoolExecutor, wait, FIRST_COMPLETED
from functools import lru_cache
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import fftconvolve, butter, sosfilt, resample_poly

SR = 16000
DURATION = 4
N = SR * DURATION
AUDIO_EXT = {'.wav', '.flac', '.ogg', '.mp3', '.m4a'}
CFG = None
BANK = None


def seed_int(*parts):
    payload = '|'.join(map(str, parts)).encode('utf-8')
    return int.from_bytes(hashlib.sha256(payload).digest()[:8], 'big')


def stream_jsonl(path):
    with path.open(encoding='utf-8') as fp:
        for line_no, line in enumerate(fp, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ValueError(f'{path}:{line_no}: {exc}') from exc


def resolve_audio(record, base):
    for field in ('file', 'path', 'audio_path', 'wav', 'audio_file'):
        x = record.get(field)
        if isinstance(x, dict):
            x = x.get('path') or x.get('file')
        if not isinstance(x, str) or not x:
            continue
        p = Path(x)
        options = [p] if p.is_absolute() else [base/p, base/'data'/p, base.parent/p]
        for candidate in options:
            if candidate.is_file():
                return candidate.resolve()
        return options[0]
    raise ValueError(f'Manifest record lacks a readable file/path/audio_path field: {str(record)[:400]}')


def read_positives(root, split):
    path = root/f'positive_{split}.jsonl'
    if not path.is_file():
        raise FileNotFoundError(f'Missing assembled manifest: {path}')
    result = []
    for i, rec in enumerate(stream_jsonl(path)):
        source = resolve_audio(rec, root)
        if not source.is_file():
            raise FileNotFoundError(f'{path} record {i}: missing audio {source}')
        start = rec.get('word_start_sample', rec.get('start_sample'))
        end = rec.get('word_end_sample', rec.get('end_sample'))
        if (start is None or end is None) and isinstance(rec.get('word_timing'), dict):
            timing = rec['word_timing']
            start = timing.get('start_sample') if start is None else start
            end = timing.get('end_sample') if end is None else end
        if start is None or end is None:
            raise ValueError(f'{path} record {i} missing word_start_sample/word_end_sample')
        result.append(dict(source=str(source), start=int(start), end=int(end),
                           sample_rate=int(rec.get('sample_rate', SR)),
                           origin_id=str(rec.get('profile', rec.get('base', source.name)))))
    if not result:
        raise ValueError(f'Empty positive manifest {path}')
    return result


def audio_paths(root, limit=0):
    if not root.is_dir():
        return []
    found = []
    for directory, subdirs, names in os.walk(root):
        subdirs.sort()
        for name in sorted(names):
            p = Path(directory)/name
            if p.suffix.lower() in AUDIO_EXT:
                found.append(str(p))
                if limit and len(found) >= limit:
                    return found
    return found


def which_acoustic(path, root_name):
    s = path.replace('\\','/').lower()
    if root_name == 'slr26':
        return 'rir'
    parts = Path(s).parts
    tail = '/'.join(parts[-4:])
    if 'simulated_rirs' in s or '/real_rirs/' in s:
        return 'rir'
    if any(t in tail for t in ('pointsource_nois', 'isotropic_nois', '/noise/', 'noises/', 'babble', 'music')):
        return 'noise'
    if '/rir/' in s or 'impulse' in tail or 'rir_' in tail:
        return 'rir'
    if root_name in ('musan', 'dns', 'noise'):
        return 'noise'
    return 'unknown'


def split_bucket(path, category):
    # RIR/noise partition by physical file so val/test don't share exact recordings.
    key = '/'.join(Path(path).parts[-5:])
    h = seed_int('acoustic-v6-v1', category, key) % 100
    return 'train' if h < 80 else 'val' if h < 90 else 'test'


def discover_acoustics(root):
    banks = {split: {'rir':[], 'noise':[]} for split in ('train','val','test')}
    sources = {'slr26': root/'slr26', 'slr28': root/'slr28',
               'musan': root/'musan', 'dns': root/'dns', 'noise': root/'noise'}
    skipped = Counter()
    for label, folder in sources.items():
        for path in audio_paths(folder):
            kind = which_acoustic(path, label)
            if kind == 'unknown':
                skipped[label] += 1
                continue
            banks[split_bucket(path,kind)][kind].append(path)
    return banks, skipped


def find_speech(negative, split, limit):
    # Prefer actual Golos V2 negatives, rather than hard near-wake-word negatives.
    candidates = [negative/split/'negative', negative/'data'/split/'negative',
                  negative/split, negative/'data'/split]
    base = next((p for p in candidates if p.is_dir()), None)
    if base is None:
        return []
    paths = audio_paths(base, limit=0)
    speech = [p for p in paths if any(t in p.lower() for t in ('golos', '/speech/', '/voice/', '/crowd/'))]
    if not speech:
        # Do not silently use unknown negatives: they may include other wake words.
        return []
    if limit and len(speech)>limit:
        rng=random.Random(seed_int('speech-bank-v1',split))
        speech=rng.sample(speech,limit)
    return sorted(speech)


def rms(x):
    return math.sqrt(float(np.mean(np.square(np.asarray(x, dtype=np.float64)))) + 1e-14)


def read_audio(path):
    try:
        x,sr = sf.read(path,dtype='float32',always_2d=True)
        x = x.mean(axis=1)
    except Exception:
        if not shutil.which('ffmpeg'):
            raise
        import subprocess
        p=subprocess.run(['ffmpeg','-v','error','-i',str(path),'-ac','1','-ar',str(SR),
                          '-f','f32le','-'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
        x=np.frombuffer(p.stdout,dtype='<f4').copy()
        sr=SR
    if sr != SR:
        g=math.gcd(int(sr), SR)
        x=resample_poly(x,SR//g,int(sr)//g).astype(np.float32)
    if not np.isfinite(x).all() or x.size==0:
        raise ValueError(f'Empty or invalid sound: {path}')
    return x.astype(np.float32, copy=False),int(sr)


@lru_cache(maxsize=32)
def cached_audio(path):
    return read_audio(path)[0]


def crop_random(x, count, rng):
    if len(x)>=count:
        a=rng.randrange(len(x)-count+1)
        return np.array(x[a:a+count],dtype=np.float32,copy=True)
    result=np.zeros(count,dtype=np.float32)
    a=rng.randrange(count-len(x)+1)
    result[a:a+len(x)]=x
    return result


def select_file(files, rng):
    if not files:
        return None
    return files[rng.randrange(len(files))]


def apply_rir(x, rir_path, rng):
    ir=cached_audio(rir_path)
    if len(ir)<16:
        raise ValueError(f'Invalid RIR length: {rir_path}')
    # Align first meaningful arrival to time zero, retain reverberation tail.
    peak=float(np.max(np.abs(ir)))
    if peak < 1e-8:
        raise ValueError(f'Silent RIR: {rir_path}')
    meaningful=np.flatnonzero(np.abs(ir)>=peak*.12)
    start=int(meaningful[0]) if len(meaningful) else int(np.argmax(abs(ir)))
    ir=ir[start:start+min(24000,len(ir)-start)].copy()
    ir=ir/(np.sqrt(np.sum(ir.astype(np.float64)**2))+1e-12)
    if len(ir) < 16:
        return x
    old=rms(x)
    y=fftconvolve(x,ir,mode='full')[:len(x)].astype(np.float32)
    y*=min(4.0,max(0.25,old/max(rms(y),1e-7)))
    # Blend retains speech clarity and the direct path.
    wet=rng.uniform(.30,.68)
    return (x*(1-wet)+y*wet).astype(np.float32)


def speech_overlay(path, word_start, word_end, rng):
    x=cached_audio(path)
    if len(x)>N:
        x=crop_random(x, min(len(x),N), rng)
    x=np.asarray(x,dtype=np.float32)
    if len(x)>N:
        x=x[:N]
    out=np.zeros(N,dtype=np.float32)
    # Ensure a significant part of background utterance overlaps target word.
    midpoint=(word_start+word_end)//2
    if len(x)>=N:
        return x.copy()
    offset=int(midpoint - len(x)*rng.uniform(.25,.70))
    offset=max(0,min(N-len(x),offset))
    out[offset:offset+len(x)]=x
    return out


def add_snr(target, background, word_start, word_end, snr_db):
    active=target[max(0,word_start):min(N,word_end)]
    signal_rms=max(rms(active),1e-5)
    background_rms=max(rms(background),1e-8)
    gain=signal_rms/(background_rms*(10**(snr_db/20)))
    return target+(background*gain),float(gain)


def channel_process(x, rng, kind):
    mode=rng.choice(('neutral','neutral','phone','lowpass','highpass','softclip'))
    if mode == 'phone':
        sos=butter(2,[rng.uniform(130,270),rng.uniform(3000,4200)],btype='bandpass',fs=SR,output='sos')
        return sosfilt(sos,x).astype(np.float32),mode
    if mode == 'lowpass':
        sos=butter(2,rng.uniform(3800,7000),btype='lowpass',fs=SR,output='sos')
        return sosfilt(sos,x).astype(np.float32),mode
    if mode == 'highpass':
        sos=butter(2,rng.uniform(70,210),btype='highpass',fs=SR,output='sos')
        return sosfilt(sos,x).astype(np.float32),mode
    if mode == 'softclip':
        drive=rng.uniform(1.05,1.40)
        return (np.tanh(x*drive)/np.tanh(drive)).astype(np.float32),mode
    return x,mode


def loud_microphone(x, rng, kind):
    """Occasional hot microphone on POSITIVES, so high level alone isn't negative.

    The last-resort peak limiter below does not undo clipping distortion.
    These are not clean examples: only ~8% of hardmix and ~2% augclean.
    """
    probability = .08 if kind=='hardmix' else .02
    if rng.random() >= probability:
        return x, None
    peak=max(float(np.max(np.abs(x))),1e-6)
    # Raise audio above the virtual ADC full scale before limiting it.
    drive=rng.uniform(1.3,2.8)
    input_signal=x*(.90/peak)*drive
    clipped=np.clip(input_signal,-.96,.96).astype(np.float32)
    return clipped, dict(type='hot_microphone',drive=round(drive,3))


def procedural_noise(rng):
    x=np.random.default_rng(rng.randrange(2**63)).standard_normal(N).astype(np.float32)
    mode=rng.choice(('white','pink','brown','lowpass'))
    if mode in ('pink','brown'):
        X=np.fft.rfft(x)
        frequencies=np.fft.rfftfreq(N,1/SR)
        factor=np.maximum(frequencies,5.)**(-.5 if mode=='pink' else -1.)
        x=np.fft.irfft(X*factor,n=N).astype(np.float32)
    elif mode == 'lowpass':
        sos=butter(2,rng.uniform(600,3200),btype='lowpass',fs=SR,output='sos')
        x=sosfilt(sos,x).astype(np.float32)
    return x,mode


def init_worker(cfg,banks):
    global CFG,BANK
    CFG=cfg
    BANK=banks


def render_one(split,kind,i,positive,output):
    cfg=CFG; bank=BANK[split]
    rng=random.Random(seed_int(cfg['seed'],split,kind,i))
    row=positive[i%len(positive)] if i<len(positive) else positive[rng.randrange(len(positive))]
    audio=str(row['source'])
    x,actual_sr=read_audio(audio)
    # Stored timestamps refer to original sample rate; read_audio resamples to 16 kHz.
    orig_sr=int(row['sample_rate'])
    a=int(round(row['start']*SR/orig_sr))
    b=int(round(row['end']*SR/orig_sr))
    if len(x)<N:
        x=np.pad(x,(0,N-len(x)))
    if len(x)>N:
        # If base is longer than 4 s, choose crop containing the word plus a tail.
        crop_start=max(0,min(len(x)-N,b-int(3.70*SR)))
        x=x[crop_start:crop_start+N]
        a-=crop_start;b-=crop_start
    x=x[:N].copy()
    if a<0 or b<=a or b>N or b>int(3.8*SR):
        raise ValueError(f'Invalid word bounds: {audio}, {a}:{b} / {len(x)}')
    if rms(x[a:b])<1e-5:
        raise ValueError(f'Silent word interval: {audio}')

    # Mild positional jitter, preserving an intact target and 260 ms after it.
    left=max(-int(.18*SR),int(.12*SR)-a)
    right=min(int(.18*SR),int(3.72*SR)-b)
    shift=rng.randint(left,right) if right>=left else 0
    if shift:
        shifted=np.zeros(N,dtype=np.float32)
        if shift>0:
            shifted[shift:]=x[:N-shift]
        else:
            shifted[:N+shift]=x[-shift:]
        x=shifted
        a+=shift;b+=shift

    applied=[]
    if bank['rir'] and rng.random() < (.36 if kind=='augclean' else .55):
        path=select_file(bank['rir'],rng)
        x=apply_rir(x,path,rng)
        applied.append(dict(type='rir', source=path))

    x,eq=channel_process(x,rng,kind)
    if eq!='neutral':
        applied.append(dict(type='channel',mode=eq))

    # Keep most backgrounds well below the foreground wake word.
    if kind=='augclean':
        speech_prob=.33
        noise_prob=.76
        snr_noise=(18.,35.)
        snr_speech=(6.,12.)
    else:
        speech_prob=.82
        noise_prob=.93
        snr_noise=(11.,24.)
        snr_speech=(5.,10.)
    speech_used=False
    if bank['speech'] and rng.random()<speech_prob:
        path=select_file(bank['speech'],rng)
        other=speech_overlay(path,a,b,rng)
        desired=rng.uniform(*snr_speech)
        x,gain=add_snr(x,other,a,b,desired)
        applied.append(dict(type='background_speech',source=path,snr_db=round(desired,2),gain=round(gain,5)))
        speech_used=True
    elif kind=='hardmix' and not bank['speech']:
        raise RuntimeError('No V2/Golos speech for hardmix. Check --negative path.')

    if rng.random()<noise_prob:
        path=select_file(bank['noise'],rng)
        if path:
            noise=crop_random(cached_audio(path),N,rng)
            label=dict(type='environmental_noise',source=path)
        else:
            noise,noise_kind=procedural_noise(rng)
            label=dict(type='procedural_noise',mode=noise_kind)
        desired=rng.uniform(*snr_noise)
        x,gain=add_snr(x,noise,a,b,desired)
        label.update(snr_db=round(desired,2),gain=round(gain,5))
        applied.append(label)

    # Global gain avoids hard clipping (keeps target-to-background ratio).
    gain_db=rng.uniform(-3.5,2.0)
    x*=10**(gain_db/20)
    x,hot=loud_microphone(x,rng,kind)
    if hot:applied.append(hot)
    peak=float(np.max(abs(x)))
    if peak>.965:
        x*=.965/peak
    if not np.isfinite(x).all() or x.shape!=(N,):
        raise RuntimeError('Invalid rendered audio')
    if rms(x[a:b])<1e-5:
        raise RuntimeError('Word nearly silent after mixing')
    outrel=Path('data')/split/kind/f'{i:09d}.flac'
    dest=output/outrel
    dest.parent.mkdir(parents=True,exist_ok=True)
    if dest.is_file():
        try:
            info=sf.info(str(dest))
            if (info.frames,info.samplerate,info.channels)!=(N,SR,1):
                dest.unlink()
        except Exception:
            dest.unlink()
    if not dest.exists():
        temp=dest.with_suffix('.flac.part')
        try:
            with temp.open('wb') as fp:
                sf.write(fp,x,SR,format='FLAC',subtype='PCM_16')
            os.replace(temp,dest)
        finally:
            temp.unlink(missing_ok=True)
    return dict(file=outrel.as_posix(),label=1,keyword=cfg['word'],
                split=split,scenario=kind,sample_rate=SR,samples=N,
                word_start_sample=int(a),word_end_sample=int(b),
                word_start_ms=round(a*1000/SR,2),word_end_ms=round(b*1000/SR,2),
                trigger_sample=min(N-1,b+int(.07*SR)),
                source_positive=audio,source_profile=row['origin_id'],
                backgrounds=applied,global_gain_db=round(gain_db,2),
                augmentation_seed=seed_int(cfg['seed'],split,kind,i),
                provenance_version='martin_v6_augment_v1'),speech_used


def make_shard(task):
    split,kind,first,last,positives,outstr=task
    out=Path(outstr)
    manifest=out/'shards'/split/kind/f'{first:09d}.jsonl'
    if manifest.is_file():
        try:
            recs=list(stream_jsonl(manifest))
            if (len(recs)==last-first and all((out/r['file']).is_file() for r in recs)):
                return (first,last-first,0,True,0)
        except Exception:
            pass
    results=[]
    speech=0
    for i in range(first,last):
        record,used=render_one(split,kind,i,positives,out)
        results.append(record)
        speech+=int(used)
    manifest.parent.mkdir(parents=True,exist_ok=True)
    temp=manifest.with_suffix('.jsonl.part')
    with temp.open('w',encoding='utf-8') as fp:
        for r in results:
            fp.write(json.dumps(r,ensure_ascii=False,separators=(',',':'))+'\n')
    os.replace(temp,manifest)
    return first,len(results),len(results),False,speech


def duration(seconds):
    if seconds is None:
        return '--:--:--'
    sec=max(0,int(seconds))
    return f'{sec//3600:02}:{(sec//60)%60:02}:{sec%60:02}'


def bar(label,done,total,started,new,skipped,speech):
    elapsed=max(time.monotonic()-started,.001)
    rate=done/elapsed
    eta=(total-done)/rate if rate>0 else None
    frac=done/max(total,1)
    cells=min(24,int(frac*24))
    print(f"[{'#'*cells}{'.'*(24-cells)}] {frac*100:5.1f}% "
          f'{label}: {done:,}/{total:,} {rate:.1f}/s ETA {duration(eta)} '
          f'new={new:,} cached={skipped:,} background_speech={speech:,}',flush=True)


def counts(args):
    return {('train','augclean'):args.augclean_train,
            ('train','hardmix'):args.hardmix_train,
            ('val','augclean'):args.augclean_val,
            ('val','hardmix'):args.hardmix_val,
            ('test','augclean'):args.augclean_test,
            ('test','hardmix'):args.hardmix_test}


def prepare(args):
    positive={s:read_positives(args.positive,s) for s in ('train','val','test')}
    acoustics,unclassified=discover_acoustics(args.acoustics)
    speech={s:find_speech(args.negative,s,args.max_speech_files) for s in ('train','val','test')}
    print('SOURCE POSITIVES:',{s:len(x) for s,x in positive.items()},flush=True)
    print('SLR26/28 + OTHER ACOUSTICS:',{s:{k:len(v) for k,v in acoustics[s].items()} for s in acoustics},flush=True)
    print('V2/GOLOS BACKGROUND SPEECH:',{s:len(x) for s,x in speech.items()},flush=True)
    print('UNCLASSIFIED ACOUSTIC FILES:',dict(unclassified),flush=True)
    if sum(len(x['rir']) for x in acoustics.values())==0:
        raise RuntimeError(f'No RIR wav files found under {args.acoustics}. Check extracted slr26/slr28.')
    for s in ('train','val','test'):
        if not acoustics[s]['rir']:
            raise RuntimeError(f'No acoustic RIRs in split={s}; check dataset folder content')
        if not speech[s] and any(counts(args)[s,k]>0 for k in ('augclean','hardmix')):
            raise RuntimeError(f'No Golos background speech in split={s} under {args.negative}. '
                               f'Check V2/Golos paths or pass --negative to the V2 dataset.')
    for s in ('train','val','test'):
        first=positive[s][0]
        print(f'FIRST POSITIVE {s}: {first}',flush=True)
        x,sr=read_audio(first['source'])
        if not 0 <= round(first['start']*SR/first['sample_rate']) < round(first['end']*SR/first['sample_rate']) <=len(x):
            raise RuntimeError(f'Bad word boundaries in {s} manifest')
    return positive,{s:{**acoustics[s],'speech':speech[s]} for s in acoustics}


def run(args):
    positive,banks=prepare(args)
    if args.check:
        print('CHECK PASSED, no output modified')
        return
    args.out.mkdir(parents=True,exist_ok=True)
    cfg={'seed':args.seed, 'word':args.word}
    output_counts=counts(args)
    if args.smoke:
        output_counts={k:min(args.smoke,v) for k,v in output_counts.items()}
    started=time.monotonic()
    grand_total=sum(output_counts.values())
    cumulative=0
    print(f'OUTPUT: {args.out}; workers={args.workers}; clips={grand_total:,}',flush=True)
    for (split,kind),n in output_counts.items():
        if not n:
            continue
        chunks=[(split,kind,i,min(n,i+args.shard_size),positive[split],str(args.out))
                for i in range(0,n,args.shard_size)]
        outstanding={}
        next_job=iter(chunks)
        done=new=cached=speech=0
        t0=time.monotonic(); last=t0
        with ProcessPoolExecutor(max_workers=args.workers,initializer=init_worker,initargs=(cfg,banks)) as pool:
            for _ in range(min(2*args.workers,len(chunks))):
                try: task=next(next_job)
                except StopIteration: break
                outstanding[pool.submit(make_shard,task)]=task
            while outstanding:
                completed,_=wait(outstanding,return_when=FIRST_COMPLETED)
                for future in completed:
                    task=outstanding.pop(future)
                    try:
                        _,processed,created,was_cached,used=future.result()
                    except Exception as e:
                        raise RuntimeError(f'Failed shard {task[0]}/{task[1]} {task[2]}-{task[3]}: {e}') from e
                    done+=processed
                    new+=created
                    cached+=processed if was_cached else 0
                    speech+=used
                    try: task=next(next_job)
                    except StopIteration: pass
                    else: outstanding[pool.submit(make_shard,task)]=task
                if time.monotonic()-last>=args.status_interval or done>=n:
                    bar(f'{split}/{kind}',done,n,t0,new,cached,speech)
                    last=time.monotonic()
        dest=args.out/f'positive_{kind}_{split}.jsonl'
        tmp=dest.with_suffix('.jsonl.part')
        written=0
        with tmp.open('w',encoding='utf-8') as target:
            for i in range(0,n,args.shard_size):
                shard=args.out/'shards'/split/kind/f'{i:09d}.jsonl'
                if not shard.is_file():
                    raise RuntimeError(f'Missing shard manifest: {shard}')
                for row in stream_jsonl(shard):
                    target.write(json.dumps(row,ensure_ascii=False,separators=(',',':'))+'\n')
                    written+=1
        if written!=n:
            tmp.unlink(missing_ok=True)
            raise RuntimeError(f'Manifest record count mismatch: {written} vs {n}')
        os.replace(tmp,dest)
        cumulative+=n
        print(f'READY {split}/{kind}: {written:,} FLAC, {dest}',flush=True)
        bar('TOTAL',cumulative,grand_total,started,0,0,0)
    report={'created_at_utc':__import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat(),
            'positive':str(args.positive),'acoustics':str(args.acoustics),
            'negative':str(args.negative),'out':str(args.out),
            'counts':{f'{s}/{k}':v for (s,k),v in output_counts.items()},
            'seed':args.seed, 'sample_rate':SR, 'duration_s':DURATION,
            'foreground_speech_snr_db':{'augclean':[6,12],'hardmix':[5,10]},
            'noise_snr_db':{'augclean':[18,35],'hardmix':[11,24]},
            'hot_positive_microphone_probability':{'augclean':.02,'hardmix':.08},
            'data_format':'FLAC mono 16k PCM16, JSONL label=1',
            'note':'Code and manifests only for open source; respect corpus data redistribution licenses.'}
    (args.out/'augmentation_report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print('AUGMENTATION COMPLETED',flush=True)


def parse():
    p=argparse.ArgumentParser(description=__doc__,formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    p.add_argument('--word',required=True)
    p.add_argument('--positive',type=Path,default=Path('/root/martin/martin_positives_v6'))
    p.add_argument('--acoustics',type=Path,default=Path('/root/martin/martin_acoustics_v6'))
    p.add_argument('--negative',type=Path,default=Path('/root/martin/martin_dataset_v2'))
    p.add_argument('--out',type=Path,default=Path('/root/martin/martin_augmented_v6'))
    p.add_argument('--workers',type=int,default=12)
    p.add_argument('--shard-size',type=int,default=128)
    p.add_argument('--status-interval',type=float,default=3)
    p.add_argument('--seed',type=int,default=2601026)
    p.add_argument('--max-speech-files',type=int,default=80000)
    p.add_argument('--augclean-train',type=int,default=155000)
    p.add_argument('--hardmix-train',type=int,default=65000)
    p.add_argument('--augclean-val',type=int,default=3000)
    p.add_argument('--hardmix-val',type=int,default=1500)
    p.add_argument('--augclean-test',type=int,default=3000)
    p.add_argument('--hardmix-test',type=int,default=1500)
    p.add_argument('--check',action='store_true',help='Validate input manifests and sources; do not generate')
    p.add_argument('--smoke',type=int,default=0,help='Generate at most N of each category in a separate output subfolder')
    args=p.parse_args()
    if args.workers<1 or args.shard_size<1 or args.status_interval<=0 or args.smoke<0:
        p.error('workers/shard-size/status-interval must be positive, smoke >=0')
    if min(counts(args).values())<0:
        p.error('augmentation counts must be >=0')
    if args.smoke:
        args.out=args.out.parent/(args.out.name+'_smoke')
    return args


if __name__=='__main__':
    run(parse())
