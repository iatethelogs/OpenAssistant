"""Self-contained interactive console UI; never installs or downloads without approval."""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

from .resources import preset_groups, dataset_count, storage_estimate_gib, hardware_tier, suggest_workers, recommend_batch
from .core import (DEFAULT_HARD, GENERIC_PHRASES, default_config, detect,
                   load_json, norm, project_config, project_paths,
                   save_json, slug, suggested_hard, validate)

BANNER = r"""
 __        __    _        _____
 \ \      / /_ _| | _____|  ___|__  _ __ __ _  ___
  \ \ /\ / / _` | |/ / _ \ |_ / _ \| '__/ _` |/ _ \
   \ V  V / (_| |   <  __/  _| (_) | | | (_| |  __/
    \_/\_/ \__,_|_|\_\___|_|  \___/|_|  \__, |\___|
                                          |___/
         Wake-word model builder  |  by iatethelogs
"""

STAGE_INFO = [
    ('doctor', 'Проверить окружение и зависимости'),
    ('setup', 'Установить Python-зависимости (изменяет текущую среду)'),
    ('download', 'Скачать Qwen, SLR26, SLR28 и Golos (~18 ГБ)'),
    ('generate', 'Сгенерировать целевое слово и отрицательную речь'),
    ('audit', 'Проверить целевое слово через Whisper'),
    ('augment', 'Создать шумовые и речевые аугментации'),
    ('challenge', 'Создать сложные тестовые примеры'),
    ('features', 'Создать признаки MicroFrontend'),
    ('train', 'Обучить одну модель MicroWakeWord'),
    ('evaluate', 'Проверить модель на val/test'),
    ('export', 'Экспортировать TFLite и параметры'),
    ('import-speech', 'Импортировать свои отрицательные аудиозаписи'),
    ('all', 'Выполнить весь процесс (без установки зависимостей)'),
]

COUNT_FIELDS = [
    ('voice_profiles', 'Число синтетических голосовых профилей'),
    ('real_positive_per_voice', 'Позитивов на каждый записанный реальный голос'),
    ('real_hard_per_voice', 'Похожих негативов на реальный голос'),
    ('real_speech_per_voice', 'Обычных фраз на реальный голос'),
    ('variants_per_voice', 'Произношений целевого слова на диктора'),
    ('negative_per_voice', 'Похожих отрицательных записей на диктора'),
    ('generic_speech_per_voice', 'Обычных отрицательных фраз на диктора'),
    ('augclean_multiplier', 'Аугментаций на принятый позитив'),
    ('hardmix_multiplier', 'Сложных аугментаций на позитив'),
    ('challenge_count', 'Challenge-записей на val и на test'),
    ('steps', 'Шагов обучения одной модели'),
    ('batch', 'Примеров в одном шаге обучения'),
    ('eval_interval', 'Интервал валидации (шагов)'),
    ('shard_size', 'Размер шарда признаков'),
]
# Compatibility name for developer integrations. Dataset presets do not depend on hardware.
SIZES = preset_groups({'cpu_threads':2,'gpus':[],'tts_gpus':[]})
COUNT_MIN = {'voice_profiles':12, 'augclean_multiplier':1,
             'hardmix_multiplier':1, 'challenge_count':0}


def recommended_counts(cfg):
    """Legacy API: complete count estimate, before ASR rejections."""
    data=dataset_count(cfg)
    return {**data, 'total_tts':data['tts']}


def configure_counts(cfg, hw=None):
    """Ask dataset quantities independently from hardware; batch is separate."""
    hw = hw or detect()
    presets = preset_groups(hw)
    print('\n=== СКОЛЬКО ПРИМЕРОВ СОЗДАТЬ ===')
    print('Размер датасета НЕ зависит от числа GPU и видеопамяти.')
    print('GPU влияет на скорость, число TTS-процессов и размер batch.')
    print('Количество записей не гарантирует точность: важны разные дикторы и негативы.')
    for key,(title,values) in presets.items():
        c=recommended_counts({**cfg,**values})
        print(f'{key}. {title}: {c["tts"]:,} TTS-записей + '
              f'{c["augclean"]+c["hardmix"]:,} аугментаций '
              f'= ~{c["total"]:,} файлов с проверками и шумами')
    print('4. Оставить текущие параметры и настроить вручную')
    selected=choice('Режим [2]: ',('1','2','3','4'),default='2')
    values=presets.get(selected)
    if values:
        cfg.update(values[1])
    print('\nEnter оставляет показанное значение. Можно ввести свои числа.')
    primary={'voice_profiles','variants_per_voice','negative_per_voice',
             'generic_speech_per_voice','augclean_multiplier','hardmix_multiplier',
             'challenge_count','steps'}
    for field,desc in COUNT_FIELDS:
        if field not in primary:continue
        suggestion=presets['2'][1].get(field,cfg[field])
        cfg[field]=number(f'{desc} | ориентир {suggestion}',cfg[field],COUNT_MIN.get(field,1))
    print('\nДополнительно: число клонированных примеров, batch и шарды.')
    print(f'Ориентир batch для выбранной GPU: {recommend_batch(hw, cfg.get("train_gpu",-1))}.')
    if confirm('Изменить дополнительные параметры?',False):
        for field,desc in COUNT_FIELDS:
            if field in primary:continue
            suggested=presets['2'][1].get(field,default_recommendation(field,cfg,hw))
            cfg[field]=number(f'{desc} | ориентир {suggested}',cfg[field],COUNT_MIN.get(field,1))
    c=recommended_counts(cfg)
    low,high=storage_estimate_gib(c)
    print('\nПЛАН (до проверки Whisper и включая синтетический шум):')
    print(f'  TTS (самая дорогая часть): {c["tts"]:,} генераций')
    print(f'  Чистых позитивов: {c["positive"]:,}; похожих негативов: {c["hard"]:,}; обычных фраз: {c["speech"]:,}')
    print(f'  Аугментаций: {c["augclean"]:,} обычных + {c["hardmix"]:,} сложных')
    print(f'  Шумовых негативов: ~{c["noise"]:,}; challenge: {c["challenge"]:,}')
    print(f'  Всего аудиофайлов: ~{c["total"]:,} (плюс записанные реальные голоса)')
    print(f'  Планируемый запас диска: ~{low:.0f}–{high:.0f} ГиБ с модельными кэшами (приблизительно).')
    if c['tts'] > 5000:
        print('  Внимание: большая часть времени уйдёт на последовательный синтез TTS. Прогресс сохраняется.')
    return cfg


def default_recommendation(field,cfg,hw=None):
    # The current project configuration already contains user-preferred hardware.
    # Reference recommendations are fixed defaults for the current architecture.
    return {'real_positive_per_voice':24,'real_hard_per_voice':12,
            'real_speech_per_voice':12,'batch':recommend_batch(hw or detect(), cfg.get('train_gpu',-1)),'eval_interval':2000,
            'shard_size':500}.get(field,cfg[field])


TTS_FIELDS = {'keyword','voice_profiles','variants_per_voice','negative_per_voice',
              'generic_speech_per_voice','negative_words','generic_phrases',
              'real_positive_per_voice','real_hard_per_voice','real_speech_per_voice'}
AUG_FIELDS = {'augclean_multiplier','hardmix_multiplier'}
TRAIN_FIELDS = {'steps','batch','eval_interval','shard_size'}


def ask(prompt, default=None):
    suffix = f' [{default}]' if default is not None else ''
    answer = input(f'{prompt}{suffix}: ').strip()
    return answer if answer else default


def number(prompt, default, min_value=0, max_value=10000000):
    while True:
        value = ask(prompt, default)
        try:
            n = int(value)
            if min_value <= n <= max_value:
                return n
        except (TypeError, ValueError):
            pass
        print(f'Введите целое число от {min_value} до {max_value}.')


def choice(prompt, options, default=None):
    options = {str(x) for x in options}
    while True:
        value = input(prompt).strip().lower()
        if not value and default is not None:
            value = str(default)
        if value in options:
            return value
        print('Выберите вариант из меню.')


def confirm(prompt, default=False):
    tip='Д/н' if default else 'д/Н'
    while True:
        answer = input(f'{prompt} [{tip}]: ').strip().lower()
        if not answer:
            return default
        if answer in ('д','да','y','yes','1'):
            return True
        if answer in ('н','нет','n','no','0'):
            return False
        print('Ответьте да или нет.')


def items_input(title, initial=None):
    """User can paste comma/semicolon-separated words or type one per line."""
    print(f'\n{title}')
    print('Введите по одному на строке или несколько через запятую/точку с запятой.')
    print('Пустая строка завершает ввод. Для отмены введите /cancel.')
    entries=[]
    while True:
        raw=input('> ').strip()
        if raw=='/cancel':
            return None
        if not raw:
            break
        for item in raw.replace(';',',').split(','):
            item=' '.join(item.split()).strip()
            if item and item.casefold() not in {x.casefold() for x in entries}:
                entries.append(item)
    if not entries and initial is not None:
        return list(initial)
    return entries


def show_plan(cfg, hardware=None, paths=None):
    n=cfg['voice_profiles'];v=cfg['variants_per_voice'];hard=cfg['negative_per_voice'];speech=cfg['generic_speech_per_voice']
    from .voices import list_voices
    real=list_voices(paths) if paths else []
    n_real=len(real)
    real_pos=n_real*int(cfg.get('real_positive_per_voice',24))
    real_neg=n_real*(int(cfg.get('real_hard_per_voice',12))+int(cfg.get('real_speech_per_voice',12)))
    positives=n*v+real_pos
    negatives=n*(hard+speech)+real_neg
    print('\n--- Планируемые объёмы (до проверки Whisper) ---')
    print(f'Целевое слово: {cfg["keyword"]}')
    print(f'Синтетических голосов: {n:,}; произношений на голос: {v}')
    print(f'Записанных реальных голосов: {n_real}')
    print(f'  из них планируем: {real_pos} позитивов и {real_neg} негативов')
    print(f'Чистые позитивы:              {positives:>10,}')
    print(f'Похожие отрицательные:        {n*hard:>10,}')
    print(f'Обычные отрицательные фразы:  {n*speech:>10,}')
    print(f'Всего TTS-записей:            {positives+negatives:>10,}')
    print(f'Ожидаемые augclean:           {positives*cfg["augclean_multiplier"]:>10,} (верхний ориентир)')
    print(f'Ожидаемые hardmix:            {positives*cfg["hardmix_multiplier"]:>10,} (верхний ориентир)')
    print(f'Challenge:                    {cfg["challenge_count"]*2:>10,} (val + test)')
    print(f'Похожих отрицательных текстов: {len(cfg["negative_words"])}')
    print(f'Обычных отрицательных фраз:    {len(cfg.get("generic_phrases", GENERIC_PHRASES))}')
    c=dataset_count(cfg)
    low,high=storage_estimate_gib(c)
    print(f'Всего TTS-генераций: {c["tts"]:,}; всех 4-секундных записей: ~{c["total"]:,}')
    print(f'Шумовых негативов: ~{c["noise"]:,}; примерный запас на диске: {low:.0f}–{high:.0f} ГиБ')
    print(f'Рабочих потоков CPU: {cfg["workers"]}; TTS GPU: {cfg["voice_gpus"] or "CPU"}; обучение GPU: {cfg["train_gpu"]}')
    print(f'Обучение: {cfg["steps"]:,} шагов, batch={cfg["batch"]}, одна модель')
    print('Фактический объём может уменьшиться после Whisper, а при ошибках генерации этап остановится.')


def hardware_info(hw):
    print(f'CPU доступно: {hw["cpu_threads"]} логических потоков; ОЗУ: {hw.get("ram_mb", "неизвестно")} МиБ')
    if hw['gpus']:
        print('Найденные GPU:')
        for g in hw['gpus']:
            print(f'  GPU{g["index"]}: {g["mb"]} MiB VRAM')
    else:
        print('Совместимых NVIDIA GPU не обнаружено. Доступен CPU.')
    if hardware_tier(hw)=='limited':
        print('Внимание: на GPU <8 ГиБ Qwen3-TTS 1.7B может не поместиться. CPU очень медленный.')
    if hardware_tier(hw)=='cpu':
        print('CPU-only: объём датасета тот же, но синтез и обучение могут быть очень медленными.')
    print('Рекомендуемых CPU-воркеров:',suggest_workers(hw))


def choose_devices(cfg,hw):
    print('\n=== Ресурсы ===')
    hardware_info(hw)
    print('Для синтеза можно использовать несколько GPU, для обучения только одну.')
    print('Объём датасета от выбора GPU не изменится.')
    available={x['index'] for x in hw['gpus']}
    while True:
        raw=str(ask('GPU для TTS (например 0,1,2 или cpu)',','.join(map(str,cfg['voice_gpus'])) if cfg['voice_gpus'] else 'cpu'))
        if raw.lower() in ('cpu','-','none'):
            cfg['voice_gpus']=[];break
        try:
            indices=list(dict.fromkeys(int(x.strip()) for x in raw.split(',')))
            if indices and all(x in available for x in indices):
                cfg['voice_gpus']=indices;break
        except ValueError:
            pass
        print('Укажите индексы обнаруженных GPU или cpu.')
    while True:
        selected=number('GPU для обучения (-1 = CPU)',cfg['train_gpu'],min_value=-1,max_value=1000)
        if selected == -1 or selected in available:
            cfg['train_gpu']=selected;break
        print('Индекс GPU отсутствует в списке.')
    cfg['workers']=number(f'Число воркеров CPU (ориентир {suggest_workers(hw)})',cfg['workers'],1,max(1,hw['cpu_threads']*4))
    old_batch=cfg.get('batch')
    recommended_batch=recommend_batch(hw, cfg['train_gpu'])
    if old_batch!=recommended_batch:
        print(f'Рекомендуемый batch для выбранной GPU: {recommended_batch} (сейчас {old_batch}).')
        if confirm('Применить рекомендованный batch?',True): cfg['batch']=recommended_batch
    if cfg['workers'] > max(8,suggest_workers(hw)*2):
        print('Внимание: слишком много воркеров может вызвать нехватку RAM и дескрипторов файлов.')
    return cfg


def ensure_editable(paths,field):
    if field in TTS_FIELDS and (paths['root']/'tts_plan.json').exists():
        raise RuntimeError('Генерация уже началась. Чтобы не смешать разные записи, параметры слова/примеров/голосов заблокированы. Создайте новый проект.')
    if field in AUG_FIELDS and any(paths['augmented'].glob('positive_*.jsonl')):
        raise RuntimeError('Аугментации уже созданы. Изменение кратности требует нового проекта.')
    if field=='challenge_count' and any(paths['positive'].glob('challenge_*.jsonl')):
        raise RuntimeError('Challenge уже создан. Измените параметры в новом проекте.')
    if field in TRAIN_FIELDS:
        if field=='shard_size' and (paths['training']/'feature_plan.json').exists():
            raise RuntimeError('Признаки уже созданы. Размер шарда менять нельзя.')
        if field!='shard_size' and any((paths['training']/'runs').glob('gpu*')):
            raise RuntimeError('Обучение уже началось. Для других параметров нужен новый проект.')


def save_config(paths,cfg,changes):
    for f in changes:
        ensure_editable(paths,f)
    validate(cfg)
    save_json(paths['config'],cfg)
    print('Настройки сохранены.')


def create_wizard(hw):
    print('\n=== Новый проект WakeForge ===')
    keyword=str(ask('Желаемое слово или короткая фраза')).strip()
    if not keyword:
        print('Не указано слово.');return None
    folder=str(ask('Каталог проекта',f'projects/{slug(keyword)}'))
    paths=project_paths(folder)
    if paths['config'].exists() or (paths['root'].exists() and list(paths['root'].iterdir())):
        print('Каталог уже содержит данные, ничего не изменено:',paths['root']);return None
    print('\nПохожие слова или фразы, которые НЕ должны активировать ассистента.')
    print('Например: Маша, Машенька, молоко, Мартина, март.')
    suggested=suggested_hard(keyword)
    print('Автоматические варианты (пример):',', '.join(suggested[:12]))
    hard=items_input('Свои отрицательные примеры. Пусто = использовать автоматический список.',suggested)
    if hard is None:
        return None
    print('\nОбычные отрицательные фразы. Они тоже не должны содержать слово активации.')
    print('По умолчанию используется встроенный набор русских фраз.')
    phrases=items_input('Свои фразы. Пусто = использовать встроенный набор.',GENERIC_PHRASES)
    if phrases is None:
        return None
    cfg=default_config(keyword,hard=hard,hardware=hw)
    cfg['generic_phrases']=phrases
    configure_counts(cfg,hw)
    print('\nПрограмма обнаружит оборудование. Вы можете оставить предложенные значения.')
    choose_devices(cfg,hw)
    try:
        validate(cfg)
    except ValueError as exc:
        print('Ошибка конфигурации:',exc);return None
    show_plan(cfg)
    if not confirm('Создать проект с этими настройками?'):
        print('Создание отменено.');return None
    paths['root'].mkdir(parents=True,exist_ok=True)
    save_json(paths['config'],cfg)
    print('Проект создан:',paths['root'])
    print('Проект настроен. Далее мастер сам проведёт вас по этапам.')
    if confirm('Записать настоящий голос для клонирования прямо сейчас?',False):
        try:
            from .voices import record_voice
            record_voice(paths)
        except (RuntimeError,ImportError,OSError,ValueError) as e:
            print('Запись не удалась:',e)
            print('Проект сохранён; голос можно добавить позже через меню «Реальные голоса».')
    return paths['root']


def project_select():
    base=Path('projects')
    found=sorted(p for p in base.glob('*/project.json') if p.is_file()) if base.is_dir() else []
    print('\n=== Открыть проект ===')
    for i,p in enumerate(found,1):
        try:
            cfg=load_json(p)
            print(f'{i}. {p.parent} ({cfg["keyword"]})')
        except Exception:
            print(f'{i}. {p.parent} (нечитаемая конфигурация)')
    print('p. Ввести путь вручную')
    print('0. Назад')
    permitted=[str(i) for i in range(len(found)+1)]+['p']
    selected=choice('Выбор: ',permitted)
    if selected=='0':return None
    if selected=='p':
        raw=ask('Путь к проекту')
        if not raw:return None
        project=Path(raw).expanduser().resolve()
    else:
        project=found[int(selected)-1].parent.resolve()
    try:
        project_config(project)
    except Exception as exc:
        print('Не удалось открыть:',exc);return None
    return project


def edit_counts(paths,cfg,hw=None):
    presets=preset_groups(hw or detect())
    while True:
        print('\n=== Количество примеров и параметры ===')
        for i,(field,desc) in enumerate(COUNT_FIELDS,1):
            print(f' {i:2d}. {desc}: {cfg[field]} (ориентир: {default_recommendation(field,cfg,hw) if field not in presets["2"][1] else presets["2"][1][field]})')
        print('  p. Показать полный расчёт; 0. Назад')
        answer=choice('Выбор: ',[str(i) for i in range(len(COUNT_FIELDS)+1)]+['p'])
        if answer=='0':return
        if answer=='p':show_plan(cfg);continue
        field,desc=COUNT_FIELDS[int(answer)-1]
        try:
            ensure_editable(paths,field)
            min_n=COUNT_MIN.get(field,1)
            new=number(desc,cfg[field],min_n)
            updated=dict(cfg);updated[field]=new
            save_config(paths,updated,{field});cfg.update(updated)
        except (ValueError,RuntimeError) as exc:
            print('Изменение отклонено:',exc)


def edit_examples(paths,cfg):
    while True:
        print('\n=== Пользовательские примеры ===')
        print('1. Целевое слово (положительный класс):',cfg['keyword'])
        print(f'2. Похожие отрицательные слова ({len(cfg["negative_words"])})')
        print(f'3. Обычные отрицательные фразы ({len(cfg.get("generic_phrases",GENERIC_PHRASES))})')
        print('4. Показать все введённые примеры')
        print('0. Назад')
        selected=choice('Выбор: ',('0','1','2','3','4'))
        if selected=='0':return
        if selected=='4':
            print('\nЦЕЛЕВОЕ СЛОВО:',cfg['keyword'])
            print('\nПОХОЖИЕ ОТРИЦАТЕЛЬНЫЕ:\n', '\n '.join(cfg['negative_words']))
            print('\nОБЫЧНЫЕ ФРАЗЫ:\n','\n '.join(cfg.get('generic_phrases',GENERIC_PHRASES)))
            continue
        key={ '1':'keyword','2':'negative_words','3':'generic_phrases'}[selected]
        try:
            ensure_editable(paths,key)
            updated=dict(cfg)
            if key=='keyword':
                value=ask('Новое целевое слово',cfg['keyword'])
                if not value:continue
                updated[key]=value
            else:
                originals=cfg['negative_words'] if key=='negative_words' else cfg.get('generic_phrases',GENERIC_PHRASES)
                print('1. Полностью заменить список')
                print('2. Добавить к существующим примерам')
                print('0. Отмена')
                how=choice('Выбор: ',('0','1','2'))
                if how=='0':continue
                new=items_input('Введите свои примеры')
                if new is None or not new:
                    print('Ничего не изменено.');continue
                updated[key]=list(dict.fromkeys(([ *originals, *new ] if how=='2' else new)))
            save_config(paths,updated,{key});cfg.update(updated)
        except (ValueError,RuntimeError) as exc:
            print('Изменение отклонено:',exc)


def edit_hardware(paths,cfg,hw):
    updated=dict(cfg)
    choose_devices(updated,hw)
    try:
        save_config(paths,updated,{'voice_gpus','train_gpu','workers','batch'});cfg.update(updated)
    except (RuntimeError,ValueError) as exc:
        print('Изменение отклонено:',exc)


def print_status(paths,cfg):
    print('\n=== Состояние проекта ===')
    show_plan(cfg,paths=paths)
    checks=[
        ('Генерация TTS', paths['root']/'tts_plan.json'),
        ('ASR-проверка', paths['positive']/'quality_report.json'),
        ('Аугментация', paths['augmented']/'positive_augclean_train.jsonl'),
        ('Challenge', paths['positive']/'challenge_val.jsonl'),
        ('Признаки', paths['training']/'feature_plan.json'),
        ('Validation', paths['training']/'evaluation'/'report_val.csv'),
        ('Test', paths['training']/'evaluation'/'report_test.csv'),
    ]
    for label,path in checks:
        print(f'{label:<24} {"есть данные" if path.exists() else "не запускалось/нет отчёта"}')
    for split in ('train','val','test'):
        for label,folder,filename in (
            ('clean',paths['positive'],f'positive_{split}.jsonl'),
            ('augclean',paths['augmented'],f'positive_augclean_{split}.jsonl'),
            ('hardmix',paths['augmented'],f'positive_hardmix_{split}.jsonl')):
            path=folder/filename
            if path.is_file():
                with path.open(encoding='utf-8') as fp:total=sum(bool(x.strip()) for x in fp)
                print(f'{split}/{label}: {total:,}')
    from .voices import list_voices
    print('Голоса реальных дикторов:',', '.join(x['name'] for x in list_voices(paths)) or 'нет')
    print('Готовые TFLite:',[p.name for p in paths['models'].glob('*.tflite')])
    print('Логи:',paths['logs'])


def _stage_state(paths, stage):
    """Fast human-readable progress status from actual output files."""
    checkpoints = {
        'download': paths['cache']/'golos'/'prepared.json',
        'generate': paths['root']/'tts_plan.json',
        'audit': paths['positive']/'quality_report.json',
        'augment': paths['augmented']/'positive_augclean_train.jsonl',
        'challenge': paths['positive']/'challenge_val.jsonl',
        'features': paths['training']/'feature_plan.json',
        'train': paths['training']/'training_plan.json',
        'evaluate': paths['training']/'evaluation'/'report_test.csv',
        'export': paths['models'],
    }
    if stage == 'export':
        return 'ГОТОВО' if paths['models'].exists() and any(paths['models'].glob('*.tflite')) else 'не готово'
    item=checkpoints.get(stage)
    if item and item.exists():
        return 'есть результат' if stage in ('audit','augment','challenge','evaluate') else 'есть данные/запускалось'
    return 'не выполнено'


def _run_stage(project, stage, *, resume=True):
    args=[stage,'--project',str(project)]
    if stage=='train' and resume:
        root=project_config(project)[0]['training']/'runs'
        # A new project has nothing to restore; use normal initial training.
        if root.is_dir() and any((item/'model').is_dir() for item in root.glob('gpu*_*')):
            args.append('--resume')
    print(f'\n>>> {stage.upper()} | начало этапа',flush=True)
    started=time.monotonic()
    try:
        code=subprocess.call([sys.executable,'-u','-m','wakeforge',*args])
    except KeyboardInterrupt:
        print('Прервано пользователем. Готовые данные остаются на диске.')
        return False
    dt=int(time.monotonic()-started)
    if code:
        print(f'ОШИБКА на этапе {stage} (код {code}), прошло {dt//60} мин {dt%60} сек.')
        print('Исправьте причину и повторите этот этап через меню. Уже готовые данные не удаляются.')
        return False
    print(f'ГОТОВО: {stage}, время {dt//60} мин {dt%60} сек.',flush=True)
    return True


def full_workflow(project):
    paths,cfg=project_config(project)
    print('\n=== ВЕСЬ ПРОЦЕСС, ПОШАГОВО ===')
    print('Имя:',cfg['keyword'])
    show_plan(cfg,paths=paths)
    print('Программа последовательно скачает данные, сгенерирует примеры,')
    print('проверит их, подготовит признаки, обучит ОДНУ модель и экспортирует TFLite.')
    print('Готовые записи и этапы сохраняются, при ошибке можно продолжить.')
    if not confirm('Запустить обработку?'):
        return
    print('\nПодготовка Python-среды')
    print('Установка зависимостей изменяет системный Python, без venv.')
    if confirm('Установить или обновить зависимости сейчас?',False):
        argv=['setup','--yes']
        started=time.monotonic()
        try:
            code=subprocess.call([sys.executable,'-m','wakeforge',*argv])
        except KeyboardInterrupt:
            print('Установка отменена.');return
        if code:
            print('Ошибка установки. Вернитесь после устранения причины.');return
        print(f'Зависимости: выполнено за {int(time.monotonic()-started)} с.')
    steps=('download','generate','audit','augment','challenge','features','train','evaluate','export')
    print('\nЭтапы:', ' -> '.join(steps))
    for idx,stage in enumerate(steps,1):
        print('\n'+'='*64)
        print(f'ЭТАП {idx}/{len(steps)}: {dict(STAGE_INFO)[stage]}')
        print('='*64)
        if not _run_stage(project,stage,resume=True):
            print('Работа остановлена на этом этапе. Выполненные этапы сохранены.')
            return
    print('\n'+'='*64)
    print('МОДЕЛЬ ГОТОВА!')
    for file in sorted(paths['models'].glob('*.tflite')):
        print(f'  {file.name} ({file.stat().st_size/1024:.1f} КиБ)')
    print('Каталог результатов:',paths['models'])
    print('='*64)


def stage_menu(project):
    while True:
        paths,cfg=project_config(project)
        print('\n=== ПОШАГОВАЯ ОБРАБОТКА ===')
        for i,(stage,descr) in enumerate(STAGE_INFO,1):
            print(f'{i:2d}. {descr} ({_stage_state(paths,stage)})')
        print('0. Назад')
        selected=choice('Этап: ',[str(i) for i in range(len(STAGE_INFO)+1)])
        if selected=='0':return
        stage=STAGE_INFO[int(selected)-1][0]
        if stage=='setup':
            print('Установка изменит системный Python. Виртуальное окружение не создаётся.')
            if not confirm('Установить зависимости?'):continue
            cmd=[sys.executable,'-m','wakeforge','setup','--yes']
            code=subprocess.call(cmd)
            print('Зависимости установлены.' if code==0 else f'Не удалось установить зависимости: код {code}')
        elif stage=='import-speech':
            directory=ask('Папка с вашими WAV/FLAC/OGG (отрицательная речь)')
            if not directory:continue
            print('Все импортированные записи считаются отрицательными. В них не должно быть целевого слова.')
            if not confirm('Импортировать эти файлы?'):continue
            from .dataset import import_speech
            try:import_speech(paths,directory)
            except Exception as exc:print('ОШИБКА:',exc)
        elif stage=='all':
            from .guide import guided_pipeline
            guided_pipeline(project)
        else:
            if not confirm('Запустить: '+dict(STAGE_INFO)[stage]+'?'):
                continue
            _run_stage(project,stage,resume=True)



def voice_menu(paths):
    from .voices import record_voice,export_voices,import_voices,list_voices,remove_voice
    while True:
        voices=list_voices(paths)
        print('\n'+'='*62)
        print('WAKEFORGE | РЕАЛЬНЫЕ ГОЛОСА | iatethelogs')
        print('Здесь записываются эталоны голоса для локального клонирования.')
        print('После начала TTS новые голоса в этот проект добавить нельзя.')
        if voices:
            for i,v in enumerate(voices,1):
                print(f'  {i:2d}. {v["name"]:<22} {v["seconds"]:5.1f} сек  ({v["id"]})')
        else:
            print('  Пока нет записанных голосов.')
        print(' 1. Записать голос через микрофон (пошаговый мастер)')
        print(' 2. Импортировать голоса из ZIP (с другого компьютера)')
        print(' 3. Экспортировать записанные голоса в ZIP')
        print(' 4. Удалить запись диктора')
        print(' 0. Назад')
        selection=choice('Выбор: ',('0','1','2','3','4'))
        if selection=='0':return
        try:
            if selection=='1':
                record_voice(paths)
            elif selection=='2':
                src=ask('Путь к ZIP-пакету голоса')
                if src and confirm('Импортировать локальные записи?',True):
                    import_voices(paths,src)
            elif selection=='3':
                if not voices:print('Пока нечего экспортировать.');continue
                dest=ask('Имя ZIP для передачи на сервер',str(paths['root']/'wakeforge_voices.zip'))
                if dest:export_voices(paths,dest)
            elif selection=='4':
                if not voices:continue
                for i,v in enumerate(voices,1):print(f'{i}. {v["name"]}')
                num=number('Номер диктора',1,1,len(voices))
                v=voices[num-1]
                if confirm(f'Навсегда удалить локальные записи «{v["name"]}»?'):
                    remove_voice(paths,v['id'])
                    print('Запись удалена.')
        except (RuntimeError,ImportError,ValueError,OSError) as e:
            print('ОШИБКА:',e)


def main_menu():
    if not sys.stdin.isatty():
        print('Для интерактивного режима откройте терминал и запустите wakeforge.')
        print('Для автоматизации: wakeforge --help')
        return 2
    hw=detect()
    current=None
    print(BANNER)
    print('Создание модели слова активации | один проект, одна модель TFLite')
    while True:
        print('\n'+'='*62)
        print('WAKEFORGE | автор: iatethelogs')
        if current:
            try:
                _,c=project_config(current)
                print(f'Проект: {c["keyword"]} | {current}')
            except Exception as exc:
                print('Проект недоступен:',exc);current=None
        else:
            print('Проект не выбран')
        print(' 1. НОВЫЙ ПРОЕКТ: настройка и пошаговое создание модели')
        print(' 2. ПРОДОЛЖИТЬ существующий проект по шагам')
        print(' 3. ПОШАГОВЫЙ МАСТЕР для выбранного проекта')
        print(' 4. Дополнительно: запуск отдельного этапа')
        print(' 5. Настроить количество примеров и обучение')
        print(' 6. Ввести или изменить слова и отрицательные примеры')
        print(' 7. Настроить CPU, GPU и число воркеров')
        print(' 8. Посмотреть прогресс и результаты')
        print(' 9. Посмотреть оборудование')
        print('10. Записать / импортировать реальные голоса')
        print(' 0. Выход')
        selected=choice('Выберите пункт [Enter = начать/продолжить]: ',
                        [str(i) for i in range(11)],default='3' if current else '1')
        if selected=='0':
            print('Выход из WakeForge.');return 0
        try:
            if selected=='1':
                created=create_wizard(hw)
                if created:
                    current=created
                    from .guide import guided_pipeline
                    guided_pipeline(current)
                continue
            if selected=='2':
                p=project_select()
                if p:
                    current=p
                    from .guide import guided_pipeline
                    guided_pipeline(current)
                continue
            if selected=='9':
                hw=detect()
                hardware_info(hw)
                continue
            if not current:
                print('Сначала создайте или откройте проект.');continue
            paths,cfg=project_config(current)
            if selected=='10':voice_menu(paths)
            elif selected=='3':
                from .guide import guided_pipeline
                guided_pipeline(current)
            elif selected=='4':stage_menu(current)
            elif selected=='5':edit_counts(paths,cfg,hw)
            elif selected=='6':edit_examples(paths,cfg)
            elif selected=='7':edit_hardware(paths,cfg,hw)
            elif selected=='8':print_status(paths,cfg)
        except KeyboardInterrupt:
            print('\nВозврат в главное меню.')
        except (ValueError,RuntimeError,OSError,ImportError) as exc:
            print('ОШИБКА:',exc)


if __name__=='__main__':
    raise SystemExit(main_menu())
