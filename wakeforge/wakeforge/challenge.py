"""Independent, harsher held-out samples; never part of the training features."""
from __future__ import annotations
import json,os,time
from argparse import Namespace
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path
from .core import bar


def challenge(paths,cfg):
    from .legacy import augment as aug
    opts=Namespace(positive=paths['positive'],acoustics=paths['acoustics'],
                   negative=paths['negative'],out=paths['augmented'],
                   max_speech_files=80000,
                   augclean_train=1,hardmix_train=1,augclean_val=1,hardmix_val=1,
                   augclean_test=1,hardmix_test=1)
    positives,banks=aug.prepare(opts)
    cfg_worker={'seed':cfg['seed']+71911,'word':cfg['keyword']}
    count=int(cfg.get('challenge_count',500))
    if count<=0:return
    def job(split,i):
        # Offset deliberately separate from normal augclean/hardmix filenames.
        return split,'hardmix',1_000_000+i,positives[split],str(paths['augmented'])
    for split in ('val','test'):
        target=paths['positive']/f'challenge_{split}.jsonl'
        start=time.monotonic()
        with ProcessPoolExecutor(max_workers=cfg['workers'],initializer=aug.init_worker,
                                 initargs=(cfg_worker,banks)) as pool:
            # aug.render_one needs worker-local CFG and BANK. Bound task unpacked by helper.
            rows=[]
            last=start
            for i,row in enumerate(pool.map(_task, (job(split,j) for j in range(count)), chunksize=16),1):
                rows.append(row)
                now=time.monotonic()
                if now-last>=3 or i==count:
                    bar(f'CHALLENGE {split}',i,count,start)
                    last=now
        for r in rows:
            r['file']=str((paths['augmented']/r['file']).resolve())
            r['scenario']='challenge'
        tmp=target.with_name(target.name+'.partial')
        with tmp.open('w',encoding='utf-8') as f:
            for r in rows:f.write(json.dumps(r,ensure_ascii=False)+'\n')
        os.replace(tmp,target)
        print('CHALLENGE READY',target,flush=True)


def _task(params):
    from .legacy.augment import render_one
    record,used=render_one(*params)
    return record
