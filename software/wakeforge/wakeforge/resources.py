"""Dataset-quality presets are invariant to hardware.

The GPU controls HOW samples are processed (batch size / concurrent TTS workers),
not HOW MANY clips are required to train a useful detector.
"""
from __future__ import annotations

from typing import Mapping


def memory_mib():
    """Physical system memory (not VRAM); best-effort with no dependencies."""
    try:
        from pathlib import Path
        for line in Path('/proc/meminfo').read_text().splitlines():
            if line.startswith('MemTotal:'):
                return int(line.split()[1]) // 1024
    except (OSError, ValueError, IndexError):
        pass
    try:
        import os
        if hasattr(os, 'sysconf'):
            return (int(os.sysconf('SC_PHYS_PAGES')) *
                    int(os.sysconf('SC_PAGE_SIZE'))) // (1024*1024)
    except (ValueError, OSError):
        pass
    return None


def hardware_tier(hardware: Mapping):
    """Use capacity of *one* eligible GPU, never sum VRAM across devices."""
    by_id = {g['index']: int(g['mb']) for g in hardware.get('gpus', [])}
    eligible = hardware.get('tts_gpus', list(by_id))
    selected = [by_id[i] for i in eligible if i in by_id]
    if not selected:
        return 'cpu'
    largest = max(selected)
    if largest < 8192:
        return 'limited'
    if largest < 12288:
        return '8-11'
    if largest < 20480:
        return '12-19'
    return '20+'


# Dataset sizes are independent of CPU, RAM, GPU count and VRAM.
# Entries: profiles, keyword variants per profile, hard negatives per profile,
# unrelated phrases per profile, augclean multiplier, hardmix multiplier,
# challenge samples per held-out split, and training steps.
# These are engineering starting points, NOT a guaranteed accuracy target.
DATASET_PRESETS = (
    ('Пробный запуск',             (60,  3, 3, 1, 2, 1,  40,  2500)),
    ('Рекомендуемый датасет',     (5000,4, 3, 1, 6, 4, 500, 25000)),
    ('Расширенный датасет',       (10000,4,4, 2, 8, 4,1000, 40000)),
)
DATASET_FIELDS = (
    'voice_profiles', 'variants_per_voice', 'negative_per_voice',
    'generic_speech_per_voice', 'augclean_multiplier', 'hardmix_multiplier',
    'challenge_count', 'steps',
)

def recommend_batch(hardware: Mapping, selected_gpu=None):
    """Conservative INITIAL batch suggestion, not a VRAM admission guarantee.

    No dependency on dataset sizes. Only training device can change batch.
    Users can override this in the console.
    """
    if selected_gpu is None:
        selected_gpu = hardware.get('train_gpu', -1)
    gpu = next((g for g in hardware.get('gpus', [])
                if int(g['index']) == int(selected_gpu)), None)
    if gpu is None:
        return 16
    mb = int(gpu['mb'])
    if mb < 8192: return 16
    if mb < 12288: return 32
    if mb < 20480: return 64
    return 128

def preset_groups(hardware: Mapping = None):
    """Three dataset-size choices, IDENTICAL across all hardware."""
    return {str(index): (title, dict(zip(DATASET_FIELDS, values)))
            for index, (title, values) in enumerate(DATASET_PRESETS, 1)}


def suggest_workers(hardware: Mapping):
    cpu = max(1,int(hardware.get('cpu_threads', 2)))
    ram = hardware.get('ram_mb')
    # Feature extraction and signal processing can use lots of RAM and FDs.
    by_cpu = max(1, min(8, (cpu + 1)//2))
    if isinstance(ram,(float,int)):
        by_ram = max(1, min(8, int(ram)//3072))
        return min(by_cpu, by_ram)
    return by_cpu


def dataset_count(cfg):
    speakers = int(cfg['voice_profiles'])
    positive = speakers * int(cfg['variants_per_voice'])
    hard = speakers * int(cfg['negative_per_voice'])
    speech = speakers * int(cfg['generic_speech_per_voice'])
    augclean = positive * int(cfg['augclean_multiplier'])
    hardmix = positive * int(cfg['hardmix_multiplier'])
    noise = sum(max(1,round(max(48,speech)*rate)) for rate in (.8,.1,.1))
    challenge = 2*int(cfg['challenge_count'])
    return dict(positive=positive,hard=hard,speech=speech,
                tts=positive+hard+speech,augclean=augclean,hardmix=hardmix,
                noise=noise,challenge=challenge,
                total=positive+hard+speech+augclean+hardmix+noise+challenge)


def storage_estimate_gib(count):
    """Planning range, not filesystem quota: 4s audio + features + model caches.

    4s 16-kHz PCM16 is 125 KiB uncompressed. FLAC depends strongly on noise.
    Feature banks can use float32 and add ~65 KiB per clip + overhead.
    Includes an approximate fixed reserve for TTS/ASR weights and acoustic packs.
    """
    items = int(count['total'])
    return round(35 + items * 0.085 / 1024,1), round(50 + items * 0.21 / 1024,1)
