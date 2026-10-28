"""Guided end-user workflow for WakeForge.

This layer deliberately leaves the existing synthesis/training pipeline alone.
It persists completed steps, presents the next action, and only installs
packages or starts expensive stages after explicit user approval.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import time
import shutil
from pathlib import Path

from .core import load_json, project_config, save_json

STEPS = (
    ('download', 'Загрузка моделей и акустики',
     'Скачаем Qwen3-TTS, SLR26/28 и русский корпус Golos (~18 ГБ, с продолжением загрузки).'),
    ('generate', 'Создание голосов и отрицательных записей',
     'Сгенерируем выбранное слово, похожие негативы и обычные фразы разными дикторами.'),
    ('audit', 'Проверка произношения',
     'Whisper проверит, действительно ли позитивные записи содержат нужное слово.'),
    ('augment', 'Шумы и аугментации',
     'Добавим фоновую речь, реверберацию и шумы; исходные записи сохраним.'),
    ('challenge', 'Сложные независимые проверки',
     'Создадим challenge-выборки для validation/test, не для обучения.'),
    ('features', 'Подготовка спектральных признаков',
     'Преобразуем аудио в 40-канальные признаки MicroFrontend.'),
    ('train', 'Обучение одной модели',
     'Обучим одну MicroWakeWord-модель, используя выбранную GPU или CPU.'),
    ('evaluate', 'Проверка готовой модели',
     'Проверим распознавание и ложные активации на независимых примерах.'),
    ('export', 'Экспорт TFLite',
     'Сохраним INT8 TFLite-файл и параметры для использования в устройстве.'),
)

# Imports that should be present before starting the expensive stages.
# pip setup installs additional dependencies pulled in by these packages.
MODULES = ('numpy', 'scipy', 'soundfile', 'sounddevice', 'torch', 'qwen_tts',
           'huggingface_hub', 'faster_whisper', 'pymicro_features',
           'mmap_ninja', 'microwakeword', 'tensorflow', 'yaml')


def signature(paths, config):
    """Changing input configuration/voice references invalidates wizard checkmarks."""
    from .voices import list_voices
    voices = [dict(id=v.get('id'), sha256=v.get('sha256')) for v in list_voices(paths)]
    source = json.dumps({'config': config, 'voices': voices}, ensure_ascii=False,
                        sort_keys=True, separators=(',', ':')).encode('utf-8')
    return hashlib.sha256(source).hexdigest()


def state_file(paths):
    return paths['root'] / '.wakeforge_steps.json'


def get_state(paths, cfg):
    path = state_file(paths)
    sig = signature(paths, cfg)
    if path.is_file():
        try:
            state = load_json(path)
        except (ValueError, OSError):
            print('Не удалось прочитать сохранённый прогресс. Этапы будут перепроверены.')
            state = {}
    else:
        state = {}
    if state.get('signature') != sig:
        if state.get('completed'):
            print('\nНастройки проекта или эталоны голосов изменились.')
            print('Подтверждения завершённых этапов сброшены; готовые файлы не удалены.')
        state = {'version': 1, 'signature': sig, 'completed': []}
        save_json(path, state)
    return state


def mark_done(paths, state, stage):
    completed = list(dict.fromkeys([*state['completed'], stage]))
    state['completed'] = completed
    state['last_completed_at'] = time.strftime('%Y-%m-%d %H:%M:%S')
    save_json(state_file(paths), state)


def mark_not_done(paths, state, stage):
    idx = [s[0] for s in STEPS].index(stage)
    state['completed'] = [s for s in state['completed'] if s not in {v[0] for v in STEPS[idx:]}]
    save_json(state_file(paths), state)


def outputs_exist(paths, stage):
    """Evidence that a successful stage produced outputs; not an attempt to guess from partial runs."""
    root = paths['root']
    if stage == 'download':
        marker=paths['cache']/'downloads.json'
        if not marker.is_file():return False
        try:
            record=load_json(marker)
            cfg=project_config(root)[1]
            from .voices import list_voices
            if record.get('tts_model')!=cfg['tts_model'] or not Path(record['tts_cache']).is_dir():return False
            if list_voices(paths):
                if record.get('clone_model')!=cfg.get('clone_model') or not Path(record.get('clone_cache') or '').is_dir():return False
            if not record.get('acoustics'):return False
            if not all((paths['acoustics']/label/'.wakeforge_extracted').is_file() for label in ('slr26','slr28')):return False
            from .golos import assert_ready
            assert_ready(paths,cfg)
            return True
        except (OSError,ValueError,KeyError,TypeError,RuntimeError):return False
    if stage == 'generate':
        if not (root / 'tts_plan.json').is_file():return False
        from .tts import jobs_for,real_jobs_for,source_path,valid_labeled
        _, config = project_config(root)
        from .golos import assert_ready
        try:assert_ready(paths,config)
        except (RuntimeError,OSError,ValueError):return False
        return all(valid_labeled(source_path(paths, r),r)
                   for r in [*jobs_for(config),*real_jobs_for(config,paths)])
    if stage == 'audit':
        try:
            report=load_json(paths['positive']/'quality_report.json')
            if not isinstance(report,dict) or report.get('missing',0)>0 or report.get('accepted',0)<1:
                return False
            return all((paths['positive']/f'positive_{s}.jsonl').is_file()
                       and (paths['positive']/f'positive_{s}.jsonl').stat().st_size>0
                       for s in ('train','val','test'))
        except (OSError,ValueError,TypeError):return False
    if stage == 'augment':
        cfg=project_config(root)[1]
        for split in ('train','val','test'):
            clean=paths['positive']/f'positive_{split}.jsonl'
            if not clean.is_file():return False
            with clean.open(encoding='utf-8') as f:
                expected=sum(bool(line.strip()) for line in f)
            if not expected:return False
            for kind,multiplier in (('augclean',cfg['augclean_multiplier']),
                                    ('hardmix',cfg['hardmix_multiplier'])):
                manifest=paths['augmented']/f'positive_{kind}_{split}.jsonl'
                if not manifest.is_file():return False
                with manifest.open(encoding='utf-8') as f:
                    actual=sum(bool(line.strip()) for line in f)
                if actual!=expected*multiplier:return False
        return True
    if stage == 'challenge':
        if project_config(root)[1]['challenge_count']==0:return True
        count=project_config(root)[1]['challenge_count']
        for split in ('val','test'):
            manifest=paths['positive']/f'challenge_{split}.jsonl'
            if not manifest.is_file():return False
            with manifest.open(encoding='utf-8') as f:
                actual=sum(bool(line.strip()) for line in f)
            if actual!=count:return False
        return True
    if stage == 'features':
        plan=paths['training']/'feature_plan.json'
        marker=paths['training']/'.wakeforge_features.done'
        if not plan.is_file() or not marker.is_file():return False
        from .legacy.fit import sha256_file
        return marker.read_text(encoding='utf-8').strip()==sha256_file(plan)
    if stage == 'train':
        plan=paths['training']/'training_plan.json'
        marker=paths['training']/'.wakeforge_training.done'
        if not plan.is_file() or not marker.is_file():return False
        from .legacy.fit import sha256_file
        if marker.read_text(encoding='utf-8').strip()!=sha256_file(plan):return False
        return any(f.stat().st_size>0 for f in (paths['training']/'runs').glob(
            'gpu*_wakeword/model/tflite_stream_state_internal_quant/stream_state_internal_quant.tflite'))
    if stage == 'evaluate':
        return all((paths['training'] / 'evaluation' / f'report_{s}.json').is_file()
                   for s in ('val', 'test'))
    if stage == 'export':
        return any(paths['models'].glob('*.tflite'))
    return False


def missing_modules():
    return [name for name in MODULES if importlib.util.find_spec(name) is None]


def _header(index, total, name, done):
    print('\n' + '=' * 68)
    cells = round(26 * done / total)
    print(f'WAKEFORGE  [{"#" * cells}{"." * (26 - cells)}] {done}/{total} выполнено')
    print(f'ЭТАП {index}/{total}: {name}')
    print('=' * 68)


def _preflight(paths, cfg, yesno, cmd_runner):
    from .resources import dataset_count,storage_estimate_gib
    count=dataset_count(cfg)
    low,high=storage_estimate_gib(count)
    probe=paths['root']
    while not probe.exists() and probe!=probe.parent:
        probe=probe.parent
    free=shutil.disk_usage(probe).free/1024**3
    print(f'Оценка дискового пространства: ~{low:.0f}–{high:.0f} ГиБ; доступно {free:.1f} ГиБ.')
    print('В оценку включён большой архив Golos (~18 ГБ), SLR26/28 и кэши Qwen. Это приблизительные значения.')
    if free<low and not yesno('Места меньше нижней оценки. Продолжить на свой риск?',False):
        print('Обработка отменена до загрузки больших файлов.')
        return False
    if cfg.get('train_gpu',-1) < 0:
        print('Предупреждение: обучение модели на CPU может занять очень много времени.')
    from .menu import hardware_info
    from .core import detect
    print('\nПРОВЕРКА ПЕРЕД ЗАПУСКОМ')
    hardware_info(detect())
    print(f'Целевое слово: {cfg["keyword"]}')
    missing = missing_modules()
    if not missing:
        print('Основные Python-зависимости обнаружены.')
        return True
    print('Не хватает библиотек:', ', '.join(missing))
    print('WakeForge может установить зависимости в текущий Python, без venv.')
    print('Установка изменяет окружение и может скачивать большие пакеты.')
    if not yesno('Установить сейчас?', False):
        print('Мастер приостановлен. Настройки и данные сохранены.')
        return False
    print('Устанавливаем зависимости...')
    return bool(cmd_runner('setup'))


def guided_pipeline(project, *, confirm=None, ask=None, run_stage=None):
    """Interactive, resumable workflow. Injectable IO keeps tests fully offline."""
    from .menu import ask as ask_default, confirm as confirm_default, _run_stage, show_plan
    ask = ask or ask_default
    confirm = confirm or confirm_default
    run_stage = run_stage or (lambda stage: _run_stage(project, stage, resume=True))

    paths, cfg = project_config(project)
    state = get_state(paths, cfg)
    descriptions = {key: (name, description) for key, name, description in STEPS}
    print('\n' + '-' * 68)
    print('ПОШАГОВЫЙ МАСТЕР WAKEFORGE  |  автор: iatethelogs')
    print('-' * 68)
    show_plan(cfg, paths=paths)
    print('\nПосле каждого шага можно продолжить или остановиться.')
    print('Готовые этапы сохраняются, при повторном запуске они пропускаются.')
    if not confirm('Начать или продолжить создание модели?', True):
        return False

    def setup_runner(stage):
        import subprocess, sys
        code = subprocess.call([sys.executable, '-m', 'wakeforge', 'setup', '--yes'])
        if code:
            print(f'Не удалось установить зависимости (код {code}).')
        return code == 0

    # Avoid triggering package installation for a project that is already finished.
    pending = [key for key, _, _ in STEPS
               if key not in state['completed'] or not outputs_exist(paths, key)]
    if pending and not _preflight(paths, cfg, confirm, setup_runner):
        return False

    mode = str(ask('После каждого этапа: [1] подтверждать / [2] продолжать автоматически', '1')).strip()
    automatic = mode == '2'
    total = len(STEPS)
    for index, (stage, title, detail) in enumerate(STEPS, 1):
        done = sum(k in state['completed'] and outputs_exist(paths, k)
                   for k, _, _ in STEPS)
        _header(index, total, title, done)
        print(detail)
        if stage in state['completed'] and outputs_exist(paths, stage):
            print('Уже выполнено. Повторная обработка не нужна.')
            continue
        if stage in state['completed']:
            print('В сохранённом результате не хватает файлов; перепроверим этап.')
            mark_not_done(paths, state, stage)
        if not automatic:
            decision = str(ask('Enter = запустить | п = пауза | м = главное меню', '') or '').strip().lower()
            if decision in ('п', 'p', 'pause', 'м', 'm', 'menu'):
                print('Пауза. В следующий раз мастер продолжит с этого шага.')
                return False
        while True:
            result = run_stage(stage)
            if result and outputs_exist(paths, stage):
                mark_done(paths, state, stage)
                print(f'\n[OK] {title}: завершено. Результаты сохранены.')
                break
            if result:
                print('Команда завершилась, но ожидаемые файлы не появились.')
                print('Этап не помечен завершённым.')
            decision = str(ask('Enter = повторить этап | п = остановиться', 'п') or '').strip().lower()
            if decision not in ('', 'r', 'р', 'repeat', 'повтор'):
                print('Пауза. Можно повторить этот этап после исправления ошибки.')
                return False
        if not automatic and index < total:
            decision = str(ask('Enter = следующий этап | п = пауза', '') or '').strip().lower()
            if decision in ('п', 'p', 'pause'):
                print('Прогресс сохранён. Продолжение начнётся со следующего этапа.')
                return False

    _header(total, total, 'МОДЕЛЬ ГОТОВА', total)
    models = sorted(paths['models'].glob('*.tflite'))
    for file in models:
        print(f'  {file.name}  ({file.stat().st_size/1024:.1f} КиБ)')
    print('Итоговая модель:', paths['models'])
    print('Готово. WakeForge не запускает модель на микрофоне автоматически.')
    return True
