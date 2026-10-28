"""Config, deterministic planning, hardware discovery and safe filesystem helpers."""
from __future__ import annotations
import hashlib, json, os, re, subprocess, unicodedata
from pathlib import Path

SPLITS=('train','val','test')
DEFAULT_HARD=('мама','мамочка','молоко','машина','машенька','маша','марта','марина',
              'март','маршрут','маркер','мартын','марти','мартини','мандарин',
              'магнит','матрас','монитор','мотор','можно','музыка')
GENERIC_PHRASES=('Как дела сегодня', 'Принеси пожалуйста воды', 'Какая погода на улице',
                 'Можно позвонить позже', 'Я сейчас вернусь', 'Где находится магазин',
                 'Мне необходимо немного времени', 'Выключи свет на кухне',
                 'Сегодня очень хорошая погода', 'Скажи пожалуйста ещё раз',
                 'Нам нужно пойти домой', 'Открой пожалуйста окно',
                 'Никого не было в комнате', 'Давай поговорим об этом позже',
                 'Я смотрю интересное кино', 'Это просто невероятно')


def token(*values):
    raw='|'.join(map(str,values)).encode('utf-8')
    return int.from_bytes(hashlib.blake2b(raw,digest_size=8).digest(),'big')


def slug(word):
    ru='абвгдеёжзийклмнопрстуфхцчшщъыьэюя'
    lat=('a','b','v','g','d','e','yo','zh','z','i','y','k','l','m','n','o','p','r',
         's','t','u','f','kh','ts','ch','sh','shch','','y','','e','yu','ya')
    tr=dict(zip(ru,lat))
    result=''.join(tr.get(ch.lower(),ch.lower() if ch.isascii() and ch.isalnum() else '-')
                   for ch in unicodedata.normalize('NFKC',word))
    result=re.sub(r'-+','-',result).strip('-')
    return result if result else hashlib.sha256(word.encode()).hexdigest()[:12]


def save_json(path,obj):
    p=Path(path);p.parent.mkdir(parents=True,exist_ok=True)
    temp=p.with_name(p.name+'.tmp')
    temp.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8');temp.replace(p)


def load_json(path):return json.loads(Path(path).read_text(encoding='utf-8'))


def norm(text):return ' '.join(re.findall(r'[^\W\d_]+',text.casefold(),re.UNICODE))


def detect():
    from .resources import memory_mib, suggest_workers
    gpu=[]
    try:
        r=subprocess.run(['nvidia-smi','--query-gpu=index,memory.total','--format=csv,noheader,nounits'],
                         capture_output=True,text=True,timeout=10)
        if r.returncode==0:
            for line in r.stdout.splitlines():
                x=line.split(',')
                if len(x)==2:
                    try: gpu.append(dict(index=int(x[0].strip()),mb=int(x[1].strip())))
                    except ValueError:pass
    except (FileNotFoundError,subprocess.TimeoutExpired):pass
    cpus=len(os.sched_getaffinity(0)) if hasattr(os,'sched_getaffinity') else (os.cpu_count() or 2)
    available=[x['index'] for x in gpu if x['mb']>=6000]
    ram=memory_mib()
    hw=dict(cpu_threads=cpus,ram_mb=ram,gpus=gpu,tts_gpus=available,
            tts_workers=max(1,len(available)),train_gpu=available[0] if available else -1)
    hw['audio_workers']=suggest_workers(hw)
    return hw


def suggested_hard(word):
    # Approximate phonetic confusables; the wizard lets users edit this list.
    w=norm(word).split()[0]
    if len(w)<3:return list(DEFAULT_HARD)
    prefix=w[:2]
    endings=('а','я','и','о','у','ка','очка','ушка','ина','инка','ик','ый','ить','ник','ский')
    suggestions=[prefix+suffix for suffix in endings]
    for index in range(len(w)):
        for replacement in ('а','о','и','у','р','л','н','м','т','к'):
            altered=w[:index]+replacement+w[index+1:]
            if altered!=w:suggestions.append(altered)
    return list(dict.fromkeys(x for x in suggestions if x!=w and len(x)>=2))[:40]


def default_config(word,hard=None,voices=None,variants=None,hardware=None):
    from .resources import preset_groups, recommend_batch
    hw=hardware or detect()
    preset=preset_groups(hw)['2'][1]
    if voices is None: voices=preset['voice_profiles']
    if variants is None: variants=preset['variants_per_voice']
    custom=list(dict.fromkeys(norm(x) for x in (hard or suggested_hard(word)) if norm(x) and norm(x)!=norm(word)))
    if not custom:raise ValueError('Нужен хотя бы один отрицательный пример')
    return dict(schema=1,keyword=word.strip(),language='Russian',seed=42,
                voice_profiles=voices,variants_per_voice=variants,
                negative_words=custom,negative_per_voice=preset['negative_per_voice'],
                generic_speech_per_voice=preset['generic_speech_per_voice'],
                augclean_multiplier=preset['augclean_multiplier'],
                hardmix_multiplier=preset['hardmix_multiplier'],challenge_count=preset['challenge_count'],
                asr_model='small',asr_device='auto',
                download_acoustics=True, # legacy toggle: SLR are always required
                tts_model='Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign',
                clone_model='Qwen/Qwen3-TTS-12Hz-1.7B-Base',
                real_positive_per_voice=24,real_hard_per_voice=12,real_speech_per_voice=12,
                voice_gpus=hw['tts_gpus'],train_gpu=hw['train_gpu'],
                workers=hw.get('audio_workers',2),shard_size=500,
                steps=preset['steps'],batch=recommend_batch(hw),eval_interval=2000)


def project_paths(project):
    p=Path(project).expanduser().resolve()
    return dict(root=p,config=p/'project.json',positive=p/'positives',negative=p/'negatives',
                augmented=p/'augmented',acoustics=p/'acoustics',training=p/'training',
                models=p/'models',logs=p/'logs',cache=p/'cache')


def includes_keyword(text, keyword):
    """Match complete lexical sequence, including two-word wake phrases."""
    words = norm(text).split()
    target = norm(keyword).split()
    return bool(target) and any(words[i:i+len(target)] == target
                                for i in range(len(words)-len(target)+1))


def validate(cfg):
    if not 1<=len(cfg['keyword'].split())<=2:raise ValueError('Имя активации должно состоять из 1–2 слов')
    if cfg['voice_profiles']<12:raise ValueError('Нужно минимум 12 голосов для раздельного train/val/test')
    if cfg['variants_per_voice']<1:raise ValueError('variants_per_voice >=1')
    if cfg['negative_per_voice']<1 or cfg['generic_speech_per_voice']<1:raise ValueError('Negative counts must be >=1')
    if not cfg['negative_words']:raise ValueError('Не заполнены похожие отрицательные слова')
    if any(includes_keyword(x,cfg['keyword']) for x in cfg['negative_words']):
        raise ValueError('Похожие отрицательные примеры содержат точное слово активации')
    if not cfg.get('generic_phrases',GENERIC_PHRASES):
        raise ValueError('Нужна хотя бы одна обычная отрицательная фраза')
    if any(includes_keyword(x,cfg['keyword']) for x in cfg.get('generic_phrases',GENERIC_PHRASES)):
        raise ValueError('Обычные отрицательные фразы содержат слово активации')
    for field in ('workers','batch','steps','eval_interval','shard_size'):
        if int(cfg[field]) < 1: raise ValueError(f'{field} должно быть положительным')
    for field in ('augclean_multiplier','hardmix_multiplier'):
        if int(cfg[field]) < 1: raise ValueError(f'{field} должен быть >=1 (требуется для обучения)')
    if int(cfg['challenge_count']) < 0: raise ValueError('challenge_count не может быть отрицательным')
    for k in ('real_positive_per_voice','real_hard_per_voice','real_speech_per_voice'):
        if not (1<=int(cfg.get(k,24))<=10000):
            raise ValueError(f'{k}: необходимо значение от 1 до 10000')


def project_config(root):
    p=project_paths(root)
    if not p['config'].is_file():raise FileNotFoundError(f'Нет {p["config"]}. Сначала wakeforge init')
    cfg=load_json(p['config']);validate(cfg);return p,cfg


def split_for_profile(i,n,seed=42):
    # Entire profile remains in a single split. No sharing between train/validation/test.
    order=sorted(range(n),key=lambda j:token(seed,'speaker',j))
    boundaries=(int(n*.8),int(n*.9))
    rank=order.index(i)
    return 'train' if rank<boundaries[0] else 'val' if rank<boundaries[1] else 'test'


def bar(label,done,total,started):
    """Consistent stage progress: percent, count, samples/s, ETA."""
    import time
    dt=max(0.001,time.monotonic()-started)
    total=max(0,int(total))
    done=min(max(0,int(done)),total) if total else max(0,int(done))
    frac=done/total if total else 1.0
    n=int(frac*24)
    speed=done/dt
    eta=(total-done)/speed if speed and total else 0
    seconds=int(max(0,eta))
    print(f'[{"#"*n}{"."*(24-n)}] {frac:6.1%} {label}: '
          f'{done:,}/{total:,} {speed:.1f}/s ETA '
          f'{seconds//3600:02d}:{seconds//60%60:02d}:{seconds%60:02d}',flush=True)
