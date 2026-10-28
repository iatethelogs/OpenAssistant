"""Local, opt-in voice references for Qwen3-TTS Base. No uploads or telemetry.

Recording is optional and can be performed on a separate computer, followed by
export/import of a small voice pack. A speaker's files must never cross splits.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import time
import unicodedata
import wave
import zipfile
from datetime import datetime, timezone
from pathlib import Path

from .core import save_json, slug, token

PROMPTS = (
    'Сегодня утром я вышел на улицу и услышал, как шумит дождь за окном.',
    'Пожалуйста, проверь, что лампа выключена, а дверь в комнату закрыта.',
    'Вчера мы долго обсуждали новую книгу, потом отправились гулять по парку.',
    'Сейчас я говорю спокойным голосом, стараясь произносить каждое слово отчётливо.',
    'На кухне закипел чайник, рядом кто-то разговаривает и играет музыка.',
    'Интересно, какая погода будет завтра и когда закончится этот сильный ветер.',
)

SR = 24000
REC_SECONDS = 7.0

# Original public-project recording passages. These are not excerpts from books.
LITERARY_PROMPTS = (
    'Утром город казался удивительно спокойным. Над пустыми улицами медленно поднимался туман, '
    'и только редкие шаги нарушали тишину. Я остановился у окна, чтобы рассмотреть старую площадь.',
    'На краю стола лежала записка, которую никто не решался открыть. Бумага пожелтела от времени, '
    'но аккуратные буквы оставались отчётливыми. За дверью послышался короткий, осторожный стук.',
    'Поезд двигался через широкую равнину, оставляя позади маленькие станции и тёмные леса. '
    'Пассажиры разговаривали вполголоса, а за окном мелькали огни незнакомых деревень.',
    'Он долго рассматривал карту, пытаясь найти дорогу через холмы. Несколько тропинок '
    'пересекались возле реки, и каждая обещала привести к совершенно разным местам.',
    'В мастерской пахло деревом, металлом и свежим кофе. На полках стояли приборы, '
    'чьё назначение было трудно угадать. Мастер улыбнулся и начал рассказывать о новом устройстве.',
    'Вечером ветер усилился, и в саду зашелестели деревья. Кто-то включил свет на втором этаже, '
    'после чего дом снова погрузился в спокойствие. Мы решили дождаться утра и продолжить путь.',
    'Никто не заметил, когда над горизонтом появилась яркая точка. Сначала её приняли за '
    'обычную звезду, но она медленно двигалась и постепенно меняла цвет.',
    'Она открыла тяжёлую книгу и обнаружила между страницами засушенный лист. '
    'На обороте была короткая надпись, сделанная незнакомым почерком много лет назад.',
)

PHONETIC_PROMPTS = (
    'Мягкий мяч медленно катился мимо маленького маяка. Мама Маши молча смотрела на море.',
    'Шесть шустрых мышей шуршали в шкафу, а четыре чёрных чайки кружили над крышей.',
    'Роман рано разбудил Марину, чтобы проверить мотор, маршрут и старую радиостанцию.',
    'Тихий голос сменился громким, потом снова стал спокойным и немного хриплым.',
    'Завтра в двадцать три часа поезд отправится с третьей платформы, а автобус приедет позже.',
    'Пять больших коробок стояли у окна. За ними лежали семь маленьких папок и два ключа.',
    'Утром я быстро прочитал рассказ, вечером повторил трудные фразы и сделал короткую паузу.',
    'В тёмном коридоре раздался стук. Кто-то негромко произнёс несколько слов и рассмеялся.',
)



def voices_root(paths):
    return paths['root'] / 'voices'


def _record_dir(paths, voice_id):
    return voices_root(paths) / voice_id


def list_voices(paths):
    root=voices_root(paths)
    result=[]
    for file in sorted(root.glob('*/voice.json')):
        try:
            row=json.loads(file.read_text(encoding='utf-8'))
            name=str(row['id'])
            if not re.fullmatch(r'[a-z0-9_-]{1,80}',name):continue
            if file.parent.name != name:continue
            ref=file.parent / 'reference.wav'
            if not ref.is_file():continue
            result.append(row)
        except (ValueError,KeyError,UnicodeError,OSError):continue
    return result


def _consent_banner():
    print('\n' + '='*62)
    print('  WAKEFORGE | СТУДИЯ ЗАПИСИ ГОЛОСА')
    print('  автор: iatethelogs')
    print('='*62)
    print('Будут записаны несколько русских фраз для локального клонирования.')
    print('Используйте только собственный голос или голос человека,')
    print('который прямо разрешил запись и создание синтетических примеров.')
    print('Аудио остаётся в проекте и не загружается в WakeForge.')
    print('При использовании Qwen3-TTS модели загружаются из сети,')
    print('но записи голоса передаются модели только локально.')


def _meter(value, width=28):
    filled=max(0,min(width,round(width*min(1.,value))))
    return '[' + '#'*filled + '.'*(width-filled) + ']'


def _choose_microphone(sd):
    devices=sd.query_devices()
    inputs=[(i,d) for i,d in enumerate(devices) if int(d['max_input_channels'])>0]
    if not inputs:raise RuntimeError('Не обнаружены устройства записи. Проверьте микрофон и права приложения.')
    print('\nДоступные микрофоны:')
    hostapis = sd.query_hostapis() if hasattr(sd, 'query_hostapis') else []
    for idx,d in inputs:
        api_index = int(d.get('hostapi', -1))
        api = hostapis[api_index]['name'] if 0 <= api_index < len(hostapis) else 'Audio'
        print(f'  [{idx}] {d["name"]} | {api} | {int(d["max_input_channels"])} канал. | '
              f'{float(d.get("default_samplerate", SR)):.0f} Гц')
    default=sd.default.device
    default_idx=int(default[0]) if isinstance(default,(tuple,list)) and default[0] is not None else inputs[0][0]
    if default_idx not in {x[0] for x in inputs}:default_idx=inputs[0][0]
    while True:
        raw=input(f'Номер микрофона [{default_idx}]: ').strip()
        if raw=='':return default_idx
        if raw.isdecimal() and int(raw) in {x[0] for x in inputs}:
            return int(raw)
        print('Выберите номер из списка.')


def _choose_int(title, default, minimum, maximum):
    while True:
        entered=input(f'{title} [{default}]: ').strip()
        try:
            value=int(entered) if entered else default
            if minimum<=value<=maximum:return value
        except ValueError:pass
        print(f'Введите число от {minimum} до {maximum}.')


def _ask_recording_plan():
    print('\nВЫБОР МАТЕРИАЛА ДЛЯ ЗАПИСИ')
    print('  1. Художественный рассказ (оригинальный текст WakeForge)')
    print('  2. Фонетические фразы: разные согласные, гласные и числа')
    print('  3. Смешанный набор: литературный текст и фонетические фразы')
    print('  4. Собственный текст, в том числе текст из книги при наличии прав')
    print('  5. Короткие стандартные предложения')
    while True:
        mode=input('Тип текста [3]: ').strip() or '3'
        if mode in ('1','2','3','4','5'):break
        print('Выберите 1–5.')
    if mode=='1':source=list(LITERARY_PROMPTS)
    elif mode=='2':source=list(PHONETIC_PROMPTS)
    elif mode=='3':source=[v for pair in zip(LITERARY_PROMPTS,PHONETIC_PROMPTS) for v in pair]
    elif mode=='5':source=list(PROMPTS)
    else:
        print('Вставляйте по одному абзацу на строку. Пустая строка завершает ввод.')
        source=[]
        while True:
            line=input('Текст > ').strip()
            if not line:break
            if len(line)>2000:
                print('Абзац слишком длинный, максимум 2000 символов.');continue
            source.append(line)
        if len(source)<2:
            print('Для эталона нужно хотя бы два абзаца. Используем оригинальный рассказ.')
            source=list(LITERARY_PROMPTS)
    limit=min(12,len(source))
    pieces=_choose_int('Сколько фрагментов прочитать',min(4,limit),2,limit)
    seconds=_choose_int('Время записи одного дубля, секунд',20 if mode in ('1','3','4') else 10,5,90)
    if 2*seconds+.25>45:
        print('Чтобы объединить хотя бы две записи в эталон, один дубль должен быть не длиннее 22 секунд.')
        seconds=_choose_int('Новая длительность дубля, секунд',20,5,22)
    max_reference=max(2,min(pieces,int((45+.25)/(seconds+.25))))
    reference_count=_choose_int('Сколько принятых фрагментов объединить в эталон',
                               min(2,max_reference),2,max_reference)
    selected=source[:pieces]
    print(f'\nБудет {pieces} фрагментов по {seconds} секунд, '
          f'для клонирования используется {reference_count}.')
    print('Слишком длинные фразы при короткой записи могут обрезаться.')
    return selected,seconds,reference_count


def _microphone_check(sd,device):
    print('\nПроверка микрофона. Произнесите несколько слов обычным голосом.')
    try:
        sample=capture(sd,device,seconds=2.5)
    except Exception as exc:
        print('Микрофон не удалось открыть:',exc)
        return False
    ok,message,_=_audio_check(sample,SR)
    print(('Уровень в норме: ' if ok else 'Проблема с уровнем: ')+message)
    return ok


def _audio_check(samples,sr):
    import numpy as np
    mono=np.asarray(samples,dtype=np.float32).reshape(-1)
    if not len(mono) or not np.isfinite(mono).all():return False,'Запись пуста или повреждена',0
    peak=float(np.max(np.abs(mono)))
    rms=float(np.sqrt(np.mean(mono.astype('float64')**2)))
    clipping=float(np.mean(np.abs(mono)>=.995))
    if clipping>.002:return False,f'Перегрузка микрофона ({clipping:.2%} отсчётов обрезаны)',rms
    if rms<.006:return False,f'Слишком тихо (RMS {20*np.log10(max(rms,1e-9)):.1f} дБFS)',rms
    if rms>.55:return False,'Слишком громко, убавьте усиление микрофона',rms
    # Reject if recording has almost no activity at all.
    step=max(1,int(sr*.2))
    levels=[float(np.sqrt(np.mean(mono[i:i+step].astype('float64')**2))) for i in range(0,len(mono),step)]
    if sum(x>.015 for x in levels)<4:return False,'Мало речи: короткий звук или тишина',rms
    return True,f'Пик {20*np.log10(max(peak,1e-9)):.1f} дБFS | RMS {20*np.log10(max(rms,1e-9)):.1f} дБFS',rms


def capture(sd,device,seconds=REC_SECONDS,sample_rate=SR):
    """Streaming callback + simple terminal live level meter; no data written until approved."""
    import queue
    import numpy as np
    q=queue.Queue(maxsize=1024)
    # Many Windows/WASAPI microphones reject 24 kHz. Record at the hardware's
    # preferred rate, then resample to the Qwen reference format (24 kHz).
    try:
        device_details=sd.query_devices(device,'input')
        native_rate=int(round(float(device_details['default_samplerate'])))
        if native_rate<8000 or native_rate>192000:native_rate=sample_rate
    except Exception:
        native_rate=sample_rate
    chunks=[]
    status_errors=[]
    def callback(indata,frames,time_info,status):
        if status:status_errors.append(str(status))
        try:q.put_nowait(indata[:,0].copy())
        except queue.Full:status_errors.append('Переполнение очереди аудио')
    start=time.monotonic();last=0.0
    received=0
    with sd.InputStream(samplerate=native_rate,device=device,channels=1,dtype='float32',
                        blocksize=max(128,round(native_rate*.04)),callback=callback):
        while received<round(native_rate*seconds):
            try:
                arr=q.get(timeout=.15)
                chunks.append(arr)
                received+=len(arr)
            except queue.Empty:pass
            now=time.monotonic()
            if now-last>.12:
                recent=chunks[-1] if chunks else np.zeros(1,dtype=np.float32)
                amp=float(np.sqrt(np.mean(recent.astype('float64')**2)))
                elapsed=min(seconds,received/native_rate)
                print(f'\r  REC  {_meter(elapsed/seconds,20)} {elapsed:4.1f}/{seconds:.1f} c  '
                      f'Уровень {_meter(min(1.,amp/.14),16)}',end='',flush=True)
                last=now
            if now-start>seconds+7:raise RuntimeError('Микрофон перестал передавать звук')
    print()
    if status_errors:
        raise RuntimeError('Аудио потеряло отсчёты: '+', '.join(status_errors[-3:]))
    if not chunks:
        raise RuntimeError('Микрофон не вернул звук')
    result=np.concatenate(chunks)[:round(native_rate*seconds)].astype('float32')
    if native_rate!=sample_rate:
        import math
        try:
            from scipy.signal import resample_poly
            divisor=math.gcd(native_rate,sample_rate)
            result=resample_poly(result,sample_rate//divisor,native_rate//divisor).astype('float32')
        except ImportError:
            # Fallback for the standalone voice-recording utility without SciPy.
            length=round(len(result)*sample_rate/native_rate)
            result=np.interp(np.arange(length)*native_rate/sample_rate,
                             np.arange(len(result)),result).astype('float32')
    return result[:round(sample_rate*seconds)]


def trim_audio(samples,sr):
    import numpy as np
    # Only remove leading/trailing silence; don't alter phonetic content.
    hop=max(1,int(sr*.02));x=np.asarray(samples,dtype=np.float32)
    length=len(x)//hop
    if length<1:return x
    chunks=x[:length*hop].reshape(-1,hop)
    rms=np.sqrt((chunks.astype('float64')**2).mean(axis=1))
    active=np.flatnonzero(rms>max(.008,float(rms.max())*.08))
    if not len(active):return x
    a=max(0,int(active[0]*hop-.12*sr));b=min(len(x),int((active[-1]+1)*hop+.2*sr))
    return x[a:b]


def _save_wav(path,samples,sr):
    import soundfile as sf
    import numpy as np
    path.parent.mkdir(parents=True,exist_ok=True)
    tmp=path.with_suffix('.part')
    # Limited gain only: avoid hiding clipping and exaggerated amplitude changes.
    y=np.clip(samples,-.98,.98).astype(np.float32)
    sf.write(str(tmp),y,sr,format='WAV',subtype='PCM_16')
    os.replace(tmp,path)


def _select_reference(takes,folder,max_takes=3):
    """Join selected readings; keep each fragment's transcript aligned with its audio."""
    import numpy as np
    import soundfile as sf
    chosen=takes[:max_takes]
    waves=[];words=[]
    for row in chosen:
        x,sr=sf.read(str(folder/row['file']),dtype='float32')
        if sr!=SR or x.ndim!=1:raise ValueError('Invalid recorded take format')
        waves.append(x)
        words.append(row['text'])
    pause=np.zeros(int(.25*SR),dtype=np.float32)
    y=np.concatenate([a for i,w in enumerate(waves) for a in ((pause,w) if i else (w,))])
    if len(y)>45*SR:
        raise ValueError('Эталон длиннее 45 секунд. Выберите меньше фрагментов или запишите более короткие дубли.')
    _save_wav(folder/'reference.wav',y,SR)
    return ' '.join(words),len(y)/SR


def record_voice(paths,*,name=None, prompts=None,device=None,seconds=None,reference_count=None):
    """Interactive consent-based recording flow. Optional, independent of ML stack."""
    try:
        import sounddevice as sd
        import soundfile
        import numpy
    except ImportError:
        import subprocess,sys
        print('Для работы микрофона нужны три небольшие библиотеки: sounddevice, soundfile, numpy.')
        print('Скачивание и установка выполняются в текущий системный Python.')
        if input('Установить сейчас? [да/нет]: ').strip().lower() not in ('да','д','yes','y'):
            print('Запись отменена.');return None
        subprocess.run([sys.executable,'-m','pip','install','sounddevice>=0.5.2',
                        'soundfile>=0.12','numpy>=1.26'],check=True)
        import sounddevice as sd
    _consent_banner()
    if (paths['root']/'tts_plan.json').exists():
        raise RuntimeError('Генерация уже начата. Для добавления нового голоса создайте новый проект до синтеза.')
    reply=input('У вас есть согласие говорящего на запись и клонирование? [да/нет]: ').strip().lower()
    if reply not in ('да','д','yes','y'):
        print('Запись отменена.');return None
    display=(name or input('Имя диктора (например, Алексей): ').strip()).strip()
    if not display:raise ValueError('Имя диктора не задано')
    base=slug(display)
    voice_id=base
    existing={v['id'] for v in list_voices(paths)}
    if voice_id in existing:
        print('Диктор с таким именем уже записан.')
        if input('Перезаписать его полностью? [да/нет]: ').strip().lower() not in ('да','д','yes','y'):
            return None
    # Staging protects previous successful voice references if recording is
    # interrupted or fails during the rerecording session.
    root=_record_dir(paths,'.recording_'+voice_id)
    if root.exists():
        print('Найдены незавершённые записи прошлой попытки. Они будут заменены.')
        shutil.rmtree(root)
    root.mkdir(parents=True,exist_ok=True)
    if device is None:
        while True:
            device=_choose_microphone(sd)
            if _microphone_check(sd,device):
                break
            answer=input('Повторить проверку [Enter], выбрать другой [2], продолжить [3], выйти [0]: ').strip()
            if answer=='0':return None
            if answer=='3':break
            if answer=='2':continue
    if prompts is None:
        selected_prompts,default_seconds,default_reference_count=_ask_recording_plan()
    else:
        selected_prompts=list(prompts)
        default_seconds=REC_SECONDS
        default_reference_count=min(3,len(selected_prompts))
    seconds=float(seconds or default_seconds)
    reference_count=int(reference_count or default_reference_count)
    if not 2<=reference_count<=len(selected_prompts):
        raise ValueError('Нужно 2 или более эталонных фрагмента, не больше количества текстов')
    if reference_count*seconds+(reference_count-1)*.25>45:
        raise ValueError('Выбранные дубль/число эталонов длиннее 45 секунд. Уменьшите один из параметров.')
    print('\nГоворите естественно, без крика, с микрофона на обычном расстоянии.')
    print('Перед каждым дублем виден текст. При плохом сигнале можно повторить.')
    print('После записи вы сможете прослушать каждый фрагмент.')
    takes=[]
    for idx,phrase in enumerate(selected_prompts,1):
        print('\n'+'-'*62)
        print(f'ФРАГМЕНТ {idx}/{len(selected_prompts)}')
        print('Прочитайте:\n')
        print('  '+phrase+'\n')
        while True:
            answer=input('Enter = начать через 2 секунды, /skip = пропустить, /quit = выход: ').strip().lower()
            if answer=='/quit':
                print('Запись прервана. Непринятый эталон не будет подключён.')
                shutil.rmtree(root,ignore_errors=True)
                return None
            if answer=='/skip':break
            time.sleep(2)
            try:
                samples=capture(sd,device,seconds=seconds)
            except Exception as exc:
                print('Ошибка записи:',exc)
                answer=input('Повторить? [Д/н]: ').strip().lower()
                if answer in ('н','нет','n','no'):break
                continue
            ok,report,rms=_audio_check(samples,SR)
            # The voice-clone prompt requires a verbatim transcript. Quality
            # checks alone cannot tell whether the user finished reading.
            if ok:
                completed=input('Весь текст прочитан без пропусков? [Д/н]: ').strip().lower()
                if completed in ('н','нет','no','n'):
                    ok=False
                    report='Текст прочитан не полностью; нужен новый дубль'
            print(('КАЧЕСТВО OK: ' if ok else 'НЕ ПРИНЯТО: ')+report)
            if not ok:
                if input('Повторить дубль? [Д/н]: ').strip().lower() in ('н','нет','n','no'):break
                continue
            clip=trim_audio(samples,SR)
            while True:
                action=input('[1] Сохранить  [2] Прослушать  [3] Перезаписать: ').strip()
                if action in ('','1'):
                    file=f'take_{idx:02d}.wav';_save_wav(root/file,clip,SR)
                    takes.append(dict(file=file,text=phrase,seconds=round(len(clip)/SR,2),rms=round(rms,5)))
                    break
                if action=='2':
                    try:sd.play(clip,SR);sd.wait()
                    except Exception as exc:print('Воспроизведение недоступно:',exc)
                if action=='3':break
            if action in ('','1'):break
    if len(takes)<reference_count:
        print(f'Требуется минимум {reference_count} хороших фрагментов; голос пока не подключён.')
        shutil.rmtree(root,ignore_errors=True)
        return None
    transcript,ref_seconds=_select_reference(takes,root,reference_count)
    fingerprint=hashlib.sha256((root/'reference.wav').read_bytes()).hexdigest()
    result=dict(id=voice_id,name=display,ref='reference.wav',ref_text=transcript,
                seconds=round(ref_seconds,2),takes=takes,sha256=fingerprint,
                created=datetime.now(timezone.utc).isoformat(),consent_confirmed=True)
    save_json(root/'voice.json',result)
    final=_record_dir(paths,voice_id)
    if final.exists():
        backup=_record_dir(paths,voice_id+'_previous_'+datetime.now().strftime('%Y%m%d_%H%M%S'))
        os.replace(final,backup)
        try:os.replace(root,final)
        except Exception:
            os.replace(backup,final)
            raise
        print('Предыдущий эталон сохранён:',backup)
    else:
        os.replace(root,final)
    print('\nГОЛОС ДОБАВЛЕН:',display)
    print('Эталон:',final/'reference.wav')
    print('Длительность эталона:',round(ref_seconds,1),'секунд')
    print('Он будет использован для синтеза слова активации и отрицательных фраз.')
    return result


def export_voices(paths,destination):
    speakers=list_voices(paths)
    if not speakers:raise RuntimeError('Нет записанных голосов для экспорта')
    destination=Path(destination).expanduser().resolve()
    destination.parent.mkdir(parents=True,exist_ok=True)
    with zipfile.ZipFile(destination,'w',compression=zipfile.ZIP_DEFLATED) as z:
        for voice in speakers:
            root=_record_dir(paths,voice['id'])
            for file in sorted(root.rglob('*')):
                if file.is_file():z.write(file,arcname=(Path('voices')/voice['id']/file.relative_to(root)).as_posix())
    print('Пакет голосов:',destination)
    print('Содержит личные записи, передавайте только доверенным получателям.')
    return destination


def import_voices(paths,source):
    if (paths['root']/'tts_plan.json').exists():
        raise RuntimeError('Нельзя добавлять дикторов после начала TTS-генерации.')
    source=Path(source).expanduser().resolve()
    if not zipfile.is_zipfile(source):raise RuntimeError('Ожидается ZIP-пакет голосов')
    root=voices_root(paths)
    if source.stat().st_size>200*1024*1024:raise RuntimeError('Архив превышает лимит 200 МБ')
    staged=root/'.import_staged'
    if staged.exists():shutil.rmtree(staged)
    staged.mkdir(parents=True,exist_ok=True)
    try:
        with zipfile.ZipFile(source) as z:
            items=z.infolist()
            if len(items)>200:raise RuntimeError('Слишком много файлов в пакете')
            if sum(i.file_size for i in items)>500*1024*1024:raise RuntimeError('Слишком большой распакованный пакет')
            for member in items:
                if member.is_dir():continue
                rel=Path(member.filename)
                if len(rel.parts)<3 or rel.parts[0]!='voices' or not re.fullmatch('[a-z0-9_-]{1,80}',rel.parts[1]) or '..' in rel.parts:
                    raise RuntimeError(f'Небезопасный путь в ZIP: {member.filename}')
                if not member.filename.lower().endswith(('.wav','.json')):raise RuntimeError('Недопустимое расширение в архиве')
                dest=staged.joinpath(*rel.parts[1:]);dest.parent.mkdir(parents=True,exist_ok=True)
                with z.open(member) as fd,dest.open('wb') as out:shutil.copyfileobj(fd,out)
        found=[]
        for jsonfile in sorted(staged.glob('*/voice.json')):
            meta=json.loads(jsonfile.read_text(encoding='utf-8'))
            if meta.get('id')!=jsonfile.parent.name or not meta.get('consent_confirmed'):
                raise RuntimeError('Некорректная разметка согласия в пакете')
            wav=jsonfile.parent/'reference.wav'
            if not wav.is_file():raise RuntimeError('Нет эталонного WAV')
            if hashlib.sha256(wav.read_bytes()).hexdigest()!=meta.get('sha256'):
                raise RuntimeError('Контрольная сумма эталона не совпала')
            found.append((jsonfile.parent,meta))
        if not found:raise RuntimeError('В пакете нет профилей дикторов')
        conflicts=[meta['id'] for _,meta in found if (root/meta['id']).exists()]
        if conflicts:raise RuntimeError('Профили уже существуют: '+', '.join(conflicts))
        for folder,meta in found:
            os.replace(folder,root/meta['id'])
        print(f'Импортировано голосов: {len(found)}')
        return len(found)
    finally:shutil.rmtree(staged,ignore_errors=True)


def remove_voice(paths,voice_id):
    if (paths['root']/'tts_plan.json').exists():raise RuntimeError('Нельзя удалять голос после начала генерации TTS')
    found={v['id']:v for v in list_voices(paths)}
    if voice_id not in found:raise ValueError('Диктор не найден')
    shutil.rmtree(_record_dir(paths,voice_id))
    return True
