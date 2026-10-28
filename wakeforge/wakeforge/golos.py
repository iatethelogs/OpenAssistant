"""Automatic, transcribed Golos (SLR114) training-speech negatives.

The official compressed source archive is large (~18 GiB). It is downloaded
once, never bulk-unpacked, and only the chosen training examples are decoded.
Unknown transcripts are NOT treated as safe negatives. Golos is train-only:
the corpus does not expose trustworthy speaker identities in every variant,
so unseen val/test negatives remain from the independent TTS voice splits.
"""
from __future__ import annotations
import io
import json
import os
import shutil
import subprocess
import tarfile
import time
from pathlib import Path

from .core import includes_keyword, norm, save_json, token

GOLOS_URLS = (
    'https://openslr.elda.org/resources/114/golos_opus.tar.gz',
    'https://www.openslr.org/resources/114/golos_opus.tar.gz',
)
AUDIO_SUFFIXES = ('.opus', '.ogg', '.wav', '.flac')
META_SUFFIXES = ('.json', '.txt')


def target_count(cfg):
    """Real speech count independently of VRAM, in addition to TTS negatives."""
    return max(100, min(20000, int(cfg['voice_profiles']) * 3))


def transcription(raw, name):
    try:
        text = raw.decode('utf-8-sig').strip()
        if name.endswith('.json'):
            data = json.loads(text)
            if isinstance(data, dict):
                for field in ('text', 'transcription', 'transcript', 'sentence', 'normalized_text'):
                    if isinstance(data.get(field), str):
                        return data[field]
                for field in ('metadata', 'annotation'):
                    if isinstance(data.get(field), dict):
                        for key in ('text', 'transcription'):
                            if isinstance(data[field].get(key), str):
                                return data[field][key]
                return ''
            return data if isinstance(data, str) else ''
        return text
    except (ValueError, UnicodeError):
        return ''


def training_member(filename):
    """Never source negatives from official held-out Golos test recordings."""
    parts = filename.lower().replace('\\', '/').split('/')
    return 'test' not in parts and 'dev' not in parts and 'val' not in parts


def decode_audio(payload, suffix):
    """Decode to 16-kHz mono float PCM in memory, without shell interpolation."""
    if not shutil.which('ffmpeg'):
        raise RuntimeError('Нужен ffmpeg для декодирования Golos Opus: sudo apt install ffmpeg')
    args = ['ffmpeg', '-nostdin', '-hide_banner', '-loglevel', 'error', '-i', 'pipe:0',
            '-ac', '1', '-ar', '16000', '-t', '4', '-f', 'f32le', 'pipe:1']
    proc = subprocess.run(args, input=payload, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=35, check=False)
    if proc.returncode:
        raise ValueError('Не удалось декодировать Golos: ' + proc.stderr.decode(errors='replace')[-220:])
    import numpy as np
    arr = np.frombuffer(proc.stdout, dtype='<f4').copy()
    if len(arr) < 8000 or not np.isfinite(arr).all():
        raise ValueError('Golos: пустой, короткий или повреждённый звук')
    if arr.size < 64000:
        arr = np.pad(arr, (0, 64000 - arr.size))
    return np.clip(arr[:64000], -.99, .99)


def _iter_members(archive):
    """Stream top-level tar; supports nested tar shards without extracting them."""
    with tarfile.open(archive, 'r|*') as tar:
        for member in tar:
            if not member.isfile():
                continue
            src = tar.extractfile(member)
            if src is None:
                continue
            name = member.name.lower()
            if name.endswith(('.tar', '.tar.gz', '.tgz')) and member.size < (4 * 1024**3):
                # Nested webdataset shard. Stream the member, not an extractall().
                with tarfile.open(fileobj=src, mode='r|*') as inner:
                    for item in inner:
                        if item.isfile() and item.name.lower().endswith(AUDIO_SUFFIXES + META_SUFFIXES):
                            data = inner.extractfile(item)
                            if data:
                                yield f'{member.name}/{item.name}', data.read()
            elif name.endswith(AUDIO_SUFFIXES + META_SUFFIXES):
                yield member.name, src.read()


def _stem(filename):
    p = Path(filename)
    return str(p.with_suffix(''))


def prepare_golos(paths, cfg, archive=None, required=None):
    """Convert transcribed non-keyword Golos speech to safe training negatives.

    A state file is committed only after the requested number of clips was
    produced, so resumptions cannot accidentally accept a partial conversion.
    """
    import soundfile as sf
    base = paths['cache'] / 'golos'
    base.mkdir(parents=True, exist_ok=True)
    archive = Path(archive) if archive is not None else base / 'golos_opus.tar.gz'
    required = target_count(cfg) if required is None else int(required)
    if required < 1:
        raise ValueError('Нужно минимум одно Golos-аудио')
    marker = base / 'prepared.json'
    output = paths['negative'] / 'train' / 'negative' / 'speech'
    output.mkdir(parents=True, exist_ok=True)
    profile = {'keyword': norm(cfg['keyword']), 'required': required,
               'archive_size': archive.stat().st_size if archive.is_file() else 0,
               'source': 'Golos SLR114 official training corpus', 'version': 2,
               'seed': int(cfg['seed'])}
    if marker.is_file():
        try:
            old = json.loads(marker.read_text(encoding='utf-8'))
            if all(old.get(k) == v for k, v in profile.items()) and old.get('created') == required:
                wanted = old.get('files', [])
                if len(wanted) == required and len(set(wanted)) == required:
                    if all((output / name).is_file() and (output / name).stat().st_size > 100 for name in wanted):
                        return old
        except (OSError, ValueError):
            pass
    if not archive.is_file():
        raise FileNotFoundError(f'Не скачан Golos: {archive}')
    # Very small fake test archives are permitted only with explicit archive arg
    # used by test fixtures. Normal downloads use fetch_large_file() validation.
    matched = {}
    created = 0
    selected_files = []
    skipped = {'keyword': 0, 'missing_text': 0, 'invalid_audio': 0}
    started = time.monotonic()
    for name, data in _iter_members(archive):
        if not training_member(name):
            continue
        ext = Path(name).suffix.lower()
        key = _stem(name)
        entry = matched.setdefault(key, {})
        if ext in META_SUFFIXES:
            entry['text'] = transcription(data, name.lower())
        else:
            entry['audio'] = data
            entry['format'] = ext
        if 'audio' not in entry or 'text' not in entry:
            if len(matched) > 10000:
                # Fail safely rather than growing unbounded on an unexpected archive format.
                raise RuntimeError('Неизвестная структура Golos: аудио и транскрипции не образуют пары')
            continue
        record = matched.pop(key)
        phrase = record['text']
        if not phrase or len(norm(phrase).split()) < 1:
            skipped['missing_text'] += 1
            continue
        if includes_keyword(phrase, cfg['keyword']):
            skipped['keyword'] += 1
            continue
        # Deterministic subset selection; selected samples are independent
        # from training GPU and CPU count.
        if token(cfg['seed'], key) % 7:
            continue
        ident = f'golos_{token(key):016x}'
        dest = output / (ident + '.flac')
        if not (dest.is_file() and dest.stat().st_size > 100):
            try:
                x = decode_audio(record['audio'], record['format'])
                tmp = dest.with_suffix('.part')
                with tmp.open('wb') as fp:
                    sf.write(fp, x, 16000, format='FLAC', subtype='PCM_16')
                os.replace(tmp, dest)
            except (ValueError, subprocess.TimeoutExpired):
                skipped['invalid_audio'] += 1
                continue
        selected_files.append(dest.name)
        created += 1
        if created % 100 == 0 or created == required:
            elapsed = max(.01, time.monotonic()-started)
            print(f'GOLOS [{created}/{required}] {created/required:.1%} '
                  f'{created/elapsed:.1f} аудио/с', flush=True)
        if created >= required:
            break
    if created < required:
        raise RuntimeError(f'Golos: найдено {created}/{required} подходящих записей. '
                           'Проверьте исходный архив, структуру транскрипций и ffmpeg. '
                           'Обучение без настоящей речи не запускается.')
    # Remove old Golos clips only after producing a complete new plan.
    selected = set(selected_files)
    for old in output.glob('golos_*.flac'):
        if old.name not in selected:
            old.unlink()
    record = {**profile, 'created': created, 'files': selected_files, 'skipped': skipped}
    save_json(marker, record)
    return record


def assert_ready(paths, cfg):
    """Fail closed: never silently use synthetic speech instead of Golos."""
    folder = paths['cache'] / 'golos'
    file = folder / 'prepared.json'
    if not file.is_file():
        raise RuntimeError('Golos не подготовлен. Сначала запустите этап загрузки.')
    status = json.loads(file.read_text(encoding='utf-8'))
    if (status.get('keyword') != norm(cfg['keyword']) or
        status.get('required') != target_count(cfg) or
        status.get('seed') != int(cfg['seed'])):
        raise RuntimeError('Golos подготовлен для другого проекта или объёма. Повторите загрузку.')
    audio = paths['negative'] / 'train' / 'negative' / 'speech'
    selected = status.get('files', [])
    count = sum((audio / name).is_file() and (audio / name).stat().st_size > 100
                for name in selected)
    if (len(selected) != status.get('required', 0) or count < status.get('required', 0) or
        len(list(audio.glob('golos_*.flac'))) != len(selected)):
        raise RuntimeError(f'Golos: осталось {count} корректных из {status["required"]} записей. Повторите этап загрузки.')
    return status
