#!/usr/bin/env python3
"""Independent held-out evaluation for five Martin V6 streaming INT8 TFLite models.

Stages: check, val, test. Thresholds are chosen on val and frozen for test.
Original audio, features, training code and TFLite models are read-only.
IMPORTANT: V2 negatives are independent 4-second FLAC clips. Their FAPH is a
clip-based proxy, NOT a measurement on uninterrupted real-world audio.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import math
import os
import shutil
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

BASE = Path('/root/martin')
RATE = 16000
N_SAMPLES = RATE * 4
TF_FILE = 'model/tflite_stream_state_internal_quant/stream_state_internal_quant.tflite'
KINDS = ('clean', 'augclean', 'hardmix', 'challenge', 'speech', 'hard', 'noise')
VERSION = 'wakeforge_evaluator_v1'


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as fp:
        for buf in iter(lambda: fp.read(1024 * 1024), b''):
            h.update(buf)
    return h.hexdigest()


def jsonl(path):
    with path.open('r', encoding='utf-8') as fp:
        for number, line in enumerate(fp, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except ValueError as exc:
                    raise RuntimeError(f'Invalid JSON {path}:{number}: {exc}') from exc


def spread(seq, limit):
    if limit <= 0 or limit >= len(seq):
        return seq
    return [seq[i * len(seq) // limit] for i in range(limit)]


def sample_plan(args, split):
    samples = []
    for kind, root, filename in (
        ('clean', args.positive, f'positive_{split}.jsonl'),
        ('augclean', args.augmented, f'positive_augclean_{split}.jsonl'),
        ('hardmix', args.augmented, f'positive_hardmix_{split}.jsonl'),
        ('challenge', args.positive, f'challenge_{split}.jsonl'),
    ):
        manifest = root / filename
        if not manifest.is_file():
            if kind == 'challenge':
                continue
            raise FileNotFoundError(f'Manifest missing: {manifest}')
        records = list(jsonl(manifest))
        if not records:
            raise RuntimeError(f'Empty positive manifest: {manifest}')
        cap = args.clean if kind == 'clean' else args.challenge if kind == 'challenge' else args.augmented_count
        for row in spread(records, cap):
            if row.get('label') != 1:
                raise RuntimeError(f'Unexpected positive label in {manifest}')
            file = row.get('file') or row.get('path')
            end = row.get('word_end_sample')
            if not file or end is None:
                raise RuntimeError(f'Missing file/word_end_sample in {manifest}')
            p = Path(file)
            p = p if p.is_absolute() else root / p
            rate = int(row.get('sample_rate', RATE))
            end_sec = float(end) / rate
            if not 0 < end_sec < 4:
                raise RuntimeError(f'Invalid word end {end_sec}: {p}')
            samples.append(dict(path=str(p.resolve()), group=kind, end=end_sec, split=split))
    for kind, cap in (('speech', args.speech), ('hard', args.hard), ('noise', args.noise)):
        folder = args.negative / split / 'negative' / kind
        files = sorted(folder.glob('*.flac'))
        if not files:
            raise RuntimeError(f'No negative files: {folder}')
        for p in spread(files, cap):
            samples.append(dict(path=str(p.resolve()), group=kind, end=None, split=split))
    seen = set()
    for item in samples:
        p = Path(item['path'])
        if not p.is_file():
            raise FileNotFoundError(f'Audio missing: {p}')
        info = (item['path'], item['group'])
        if info in seen:
            raise RuntimeError(f'Duplicate evaluation sample: {info}')
        seen.add(info)
    return samples


def models(args):
    discovered = []
    for folder in sorted(args.runs.glob('gpu*_*')):
        model = folder / TF_FILE
        if model.is_file():
            discovered.append((folder.name, str(model.resolve())))
    if len(discovered) != 1:
        raise RuntimeError(f'Expected one exported model, found {discovered}')
    return discovered


def as_frontend(audio):
    """EXACT 4-second MicroFrontend loop from martin_v6_fit.py."""
    import numpy as np
    from pymicro_features import MicroFrontend

    raw = (np.clip(audio, -1, 32767 / 32768) * 32768).astype('<i2').tobytes()
    fe = MicroFrontend()
    offset = 0
    features = []
    while offset + 320 < len(raw):
        packet = fe.process_samples(raw[offset:offset + 320])
        if packet.samples_read <= 0:
            raise RuntimeError('MicroFrontend did not advance')
        offset += int(packet.samples_read) * 2
        if packet.features:
            features.append(packet.features)
    result = np.asarray(features, dtype=np.float32)
    if result.ndim != 2 or result.shape[1] != 40 or not (385 <= len(result) <= 401):
        raise RuntimeError(f'Unexpected features shape: {result.shape}')
    return result


def score_one(model_path, audio_path, word_end):
    import numpy as np
    import soundfile as sf
    from microwakeword.inference import Model

    y, rate = sf.read(audio_path, dtype='float32')
    if rate != RATE or y.ndim != 1 or len(y) != N_SAMPLES or not np.isfinite(y).all():
        raise RuntimeError(f'Expected exactly 4s 16kHz mono: {audio_path}; {rate} {y.shape}')
    spec = as_frontend(y)
    # A new interpreter per independent clip is intentional: reset streaming state.
    model = Model(model_path, stride=3)
    pred = np.asarray(model.predict_spectrogram(spec), dtype=np.float32).reshape(-1)
    if len(pred) < 5 or not np.isfinite(pred).all():
        raise RuntimeError(f'Bad streaming scores for {audio_path}, len={len(pred)}')
    # Five-output trailing average; one output corresponds to 3*10 ms.
    mean = np.convolve(pred, np.ones(5, dtype=np.float32) / 5., mode='valid')
    times = (int(model.input_feature_slices) + (np.arange(len(mean)) + 4) * int(model.stride)) * .010
    if word_end is not None:
        mask = (times >= word_end - .10) & (times <= word_end + .85)
        if not mask.any():
            raise RuntimeError(f'No prediction in target timing window: {audio_path}, word_end={word_end}')
    else:
        mask = np.ones(len(mean), dtype=bool)
    ind = np.flatnonzero(mask)[int(np.argmax(mean[mask]))]
    return float(mean[ind]), float(times[ind])


def cache_key(item):
    return f"{item['group']}:{item['path']}:{item['end']}"


def evaluate_worker(job):
    name, path, split, items, output, args_dict = job
    # CPU inference only; no CUDA/TensorFlow training required.
    os.environ['CUDA_VISIBLE_DEVICES'] = '-1'
    os.environ['OMP_NUM_THREADS'] = '1'
    os.environ['OPENBLAS_NUM_THREADS'] = '1'
    os.environ['TF_CPP_MIN_LOG_LEVEL'] = '3'
    work = Path(output) / split
    work.mkdir(parents=True, exist_ok=True)
    overlay = Path(args_dict['work']) / 'microwakeword_overlay'
    if overlay.is_dir():
        sys.path.insert(0, str(overlay.resolve()))
    # Import upfront, before caching any evaluation result.
    from microwakeword.inference import Model
    from pymicro_features import MicroFrontend
    import soundfile
    import numpy

    record_file = work / f'{name}.scores.jsonl'
    meta_file = work / f'{name}.meta.json'
    signature = dict(version=VERSION, model_sha256=sha256_file(path),
                     samples_sha256=hashlib.sha256(json.dumps(items, ensure_ascii=False, sort_keys=True).encode()).hexdigest(),
                     stride=3, smooth=5, valid_window=[-.10,.85])
    if meta_file.exists():
        if json.loads(meta_file.read_text(encoding='utf-8')) != signature:
            raise RuntimeError(f'Score cache differs from models/samples. Delete ONLY stale {record_file} and {meta_file}')
    else:
        if record_file.exists():
            raise RuntimeError(f'Found scores without metadata: {record_file}')
        temp = meta_file.with_suffix('.json.tmp')
        temp.write_text(json.dumps(signature, indent=2), encoding='utf-8')
        os.replace(temp, meta_file)
    cached = {}
    if record_file.exists():
        for row in jsonl(record_file):
            cached[row['key']] = row
    total = len(items)
    fresh = 0
    began = time.monotonic()
    last = began
    results = []
    with record_file.open('a', encoding='utf-8') as writer:
        for i, item in enumerate(items, 1):
            key = cache_key(item)
            if key in cached:
                row = cached[key]
            else:
                score, second = score_one(path, item['path'], item['end'])
                row = dict(key=key, score=score, time=second, group=item['group'], path=item['path'], end=item['end'])
                writer.write(json.dumps(row, ensure_ascii=False, separators=(',', ':')) + '\n')
                fresh += 1
                if fresh % 100 == 0:
                    writer.flush()
            results.append(row)
            now = time.monotonic()
            if now-last >= args_dict['progress_every'] or i == total:
                speed = i / max(now-began, .001)
                eta = (total-i) / speed if speed else 0
                print(f'[{name} {split}] {i:,}/{total:,} ({i/total:.1%}) {speed:.1f}/s '
                      f'ETA {int(eta//60):02d}:{int(eta%60):02d} new={fresh:,} cached={i-fresh:,}', flush=True)
                writer.flush()
                last = now
    return name, results


def proxy_statistics(rows, threshold):
    groups = defaultdict(list)
    for row in rows:
        groups[row['group']].append(row['score'])
    pgroup = ('clean', 'augclean', 'hardmix', 'challenge')
    positives = [v for group in pgroup for v in groups[group]]
    negatives = [v for group in ('speech', 'hard', 'noise') for v in groups[group]]
    hits = sum(x > threshold for x in negatives)
    hours = len(negatives) * 4 / 3600
    try:
        from scipy.stats import chi2
        upper = float(chi2.ppf(.95, 2 * (hits + 1)) * .5 / hours)
    except ImportError:
        upper = None
    return dict(cutoff=threshold, positive_count=len(positives), recall=sum(x > threshold for x in positives) / len(positives),
                frr=sum(x <= threshold for x in positives) / len(positives),
                negative_clips=len(negatives), negative_hours=hours,
                false_accepts=hits, proxy_faph=hits / hours, proxy_faph_95_upper=upper,
                by_group={g: dict(n=len(v), hits=sum(x > threshold for x in v),
                                  recall=(sum(x > threshold for x in v) / len(v)) if g in pgroup else None)
                          for g, v in sorted(groups.items())})


def curve_select(rows, target_faph, max_hard_fpr):
    import numpy as np
    pos = np.sort(np.asarray([r['score'] for r in rows if r['end'] is not None], dtype=np.float64))
    neg = np.sort(np.asarray([r['score'] for r in rows if r['end'] is None], dtype=np.float64))
    hard = np.sort(np.asarray([r['score'] for r in rows if r['group'] == 'hard'], dtype=np.float64))
    if not len(pos) or not len(neg) or not len(hard):
        raise RuntimeError('Missing val positives/negatives/hard negatives')
    candidates = np.unique(np.concatenate(([0., 1.], np.linspace(.5,.9999,1701),
                                           np.round(np.asarray([r['score'] for r in rows]), 6))))
    candidates = candidates[(candidates >= 0) & (candidates <= 1)]
    recall = (len(pos) - np.searchsorted(pos, candidates, side='right')) / len(pos)
    faph = (len(neg) - np.searchsorted(neg, candidates, side='right')) / (len(neg) * 4 / 3600)
    hfpr = (len(hard) - np.searchsorted(hard, candidates, side='right')) / len(hard)
    ok = np.flatnonzero((faph <= target_faph) & (hfpr <= max_hard_fpr) & (recall > 0))
    # Higher recall, then fewer false positives, then higher threshold.
    if len(ok):
        best = min(ok, key=lambda k: (-recall[k], faph[k], hfpr[k], -candidates[k]))
        cut = float(candidates[best])
        feasible = True
    else:
        # No feasible strict cutoff: find the best achievable threshold.
        pool = np.flatnonzero((recall > 0) & (faph <= target_faph))
        if not len(pool):
            pool = np.flatnonzero((recall > 0) & (faph <= max(target_faph, 2.0)))
        if len(pool):
            best = min(pool, key=lambda k: (-recall[k], faph[k], hfpr[k], -candidates[k]))
        else:
            pool = np.flatnonzero(recall > 0)
            if not len(pool):
                raise RuntimeError("No cutoff yields positive recall; check score files/inference")
            best = min(pool, key=lambda k: (faph[k], -recall[k], hfpr[k], -candidates[k]))
        cut = float(candidates[best])
        print(f"WARNING: requested constraints infeasible; fallback cutoff={cut:.6f}, "
              f"val recall={recall[best]:.2%}, proxy FAPH={faph[best]:.3f}, "
              f"hard-FPR={hfpr[best]:.2%}", flush=True)
        feasible = False
    curve = [dict(cutoff=round(float(c),6), recall=round(float(r),6),
                  proxy_faph=round(float(f),5),hard_fpr=round(float(h),6))
             for c,r,f,h in zip(candidates,recall,faph,hfpr)]
    return cut, feasible, curve


def export_cases(rows, cut, dest, max_files):
    from collections import defaultdict
    files = defaultdict(list)
    for r in rows:
        positive = r['end'] is not None
        if positive and r['score'] <= cut:
            files['missed'].append(r)
        if not positive and r['score'] > cut:
            files['false'].append(r)
    dest.mkdir(parents=True, exist_ok=True)
    for kind in ('false', 'missed'):
        candidates = files[kind]
        candidates.sort(key=lambda r: r['score'], reverse=(kind == 'false'))
        with (dest / f'{kind}.jsonl').open('w', encoding='utf-8') as f:
            for item in candidates:
                f.write(json.dumps(item, ensure_ascii=False) + '\n')
        copies = dest / kind
        copies.mkdir(exist_ok=True)
        for i, r in enumerate(candidates[:max_files]):
            suffix = Path(r['path']).suffix
            name = f'{i:03d}_{r["group"]}_{r["score"]:.4f}_{r["time"]:.2f}s{suffix}'
            shutil.copy2(r['path'], copies / name)


def main():
    parser = argparse.ArgumentParser(description='Martin V6 held-out INT8 streaming evaluator; NO training')
    parser.add_argument('action', choices=['check','val','test'])
    parser.add_argument('--runs', type=Path, default=BASE / 'martin_training_v6_prebuilt' / 'runs')
    parser.add_argument('--work', type=Path, default=BASE / 'martin_training_v6_prebuilt')
    parser.add_argument('--positive', type=Path, default=BASE / 'martin_positives_v6')
    parser.add_argument('--augmented', type=Path, default=BASE / 'martin_augmented_v6')
    parser.add_argument('--negative', type=Path, default=BASE / 'martin_dataset_v2')
    parser.add_argument('--out', type=Path, default=BASE / 'martin_training_v6_prebuilt' / 'evaluation')
    parser.add_argument('--clean', type=int, default=0, help='0 = all clean positives')
    parser.add_argument('--augmented-count', type=int, default=1000, help='max per augmented group, 0=all')
    parser.add_argument('--challenge', type=int, default=0, help='0 = all challenge')
    parser.add_argument('--speech', type=int, default=0, help='0 = all Golos negatives')
    parser.add_argument('--hard', type=int, default=2500, help='0 = all hard negatives')
    parser.add_argument('--noise', type=int, default=2500, help='0 = all noise negatives')
    parser.add_argument('--target-faph', type=float, default=.5, help='MAX proxy FAPH on val')
    parser.add_argument('--max-hard-fpr', type=float, default=.01)
    parser.add_argument('--max-copies', type=int, default=30)
    parser.add_argument('--parallel', type=int, default=5, help='independent CPU model evaluators')
    parser.add_argument('--progress-every', type=float, default=5.)
    args = parser.parse_args()
    if any(getattr(args, k) < 0 for k in ('clean','augmented_count','challenge','speech','hard','noise','max_copies')):
        parser.error('Counts cannot be negative')
    if not 0 <= args.target_faph or not 0 <= args.max_hard_fpr <= 1:
        parser.error('Invalid FAPH / hard-FPR constraint')
    if not 1 <= args.parallel <= 5:
        parser.error('parallel must be 1..5')
    found = models(args)
    print('MODELS:', flush=True)
    for name, model in found:
        print(f'  {name:19s} {Path(model).stat().st_size/1024:.1f} KiB  {model}', flush=True)
    if args.action == 'check':
        splits = ('val','test')
    else:
        splits = (args.action,)
    for split in splits:
        items = sample_plan(args, split)
        print(f'SAMPLES {split}: {dict(Counter(x["group"] for x in items))}; '
              f'{len(items)*4/3600:.2f} hours total (includes positives)', flush=True)
    if args.action == 'check':
        import pymicro_features
        import soundfile
        overlay = args.work / 'microwakeword_overlay'
        if overlay.is_dir():
            sys.path.insert(0, str(overlay.resolve()))
        from microwakeword.inference import Model
        print('CHECK OK: no training and no files modified', flush=True)
        return
    split = args.action
    items = sample_plan(args, split)
    jobs = [(name, path, split, items, str(args.out), dict(work=str(args.work), progress_every=args.progress_every)) for name, path in found]
    print(f'EVALUATION {split}: {len(items):,} files per model; parallel CPU={args.parallel}', flush=True)
    with concurrent.futures.ProcessPoolExecutor(max_workers=args.parallel) as pool:
        results = dict(pool.map(evaluate_worker, jobs))
    args.out.mkdir(parents=True, exist_ok=True)
    if split == 'val':
        selected = {}
        for name, rows in results.items():
            cut, feasible, curve = curve_select(rows, args.target_faph, args.max_hard_fpr)
            selected[name] = dict(cutoff=cut, feasible=feasible, model_sha256=sha256_file(dict(found)[name]))
            (args.out / f'{name}_validation_curve.json').write_text(json.dumps(curve, ensure_ascii=False), encoding='utf-8')
        (args.out / 'validation_thresholds.json').write_text(json.dumps(selected, ensure_ascii=False, indent=2), encoding='utf-8')
        print('VALIDATION THRESHOLDS SAVED; test will use exactly these cutoffs', flush=True)
    else:
        cutoff_file = args.out / 'validation_thresholds.json'
        if not cutoff_file.is_file():
            raise RuntimeError(f'Missing val thresholds: {cutoff_file}; run val first')
        selected = json.loads(cutoff_file.read_text(encoding='utf-8'))
        for name, path in found:
            if name not in selected or selected[name]['model_sha256'] != sha256_file(path):
                raise RuntimeError(f'Model changed or cutoff missing for {name}; run validation again')
    summary = {}
    for name, rows in results.items():
        cut = float(selected[name]['cutoff'])
        stats = proxy_statistics(rows, cut)
        stats['feasible_validation'] = bool(selected[name]['feasible'])
        stats['evaluation_type'] = 'independent_4s_clips_proxy_NOT_CONTINUOUS_AUDIO'
        summary[name] = stats
        export_cases(rows, cut, args.out / split / f'{name}_review', args.max_copies)
        upper = stats['proxy_faph_95_upper']
        print(f'{name}: CUT={cut:.5f} RECALL={stats["recall"]:.2%} FRR={stats["frr"]:.2%} '
              f'FP={stats["false_accepts"]}/{stats["negative_hours"]:.1f}h '
              f'proxy FAPH={stats["proxy_faph"]:.3f} upper95={upper if upper is not None else "N/A"} '
              f'feasible={stats["feasible_validation"]}', flush=True)
    report = args.out / f'report_{split}.json'
    report.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    import csv
    summary_csv = args.out / f'report_{split}.csv'
    with summary_csv.open('w', newline='', encoding='utf-8') as fp:
        writer = csv.writer(fp)
        writer.writerow(['model', 'cutoff', 'recall', 'frr', 'false_accepts', 'negative_hours',
                         'proxy_faph', 'proxy_faph_95_upper', 'threshold_feasible_on_val'])
        for name, d in sorted(summary.items()):
            writer.writerow([name, d['cutoff'], d['recall'], d['frr'], d['false_accepts'],
                             round(d['negative_hours'], 4), d['proxy_faph'],
                             d['proxy_faph_95_upper'], d['feasible_validation']])
    print('REPORT:', report, 'CSV:', summary_csv, flush=True)
    print('FALSE POSITIVES: <out>/' + split + '/gpu*_review/false/', flush=True)
    print('NOTE: FAPH above is a 4-second clip-based proxy; not continuous microphone measurement.', flush=True)


if __name__ == '__main__':
    main()
