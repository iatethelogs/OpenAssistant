#!/usr/bin/env python3
"""WakeForge model training from prepared positives and negatives.

Stages are intentionally separate:
  check    inspect data sources (read-only)
  features materialize 4-s PyMicroFrontend ragged-mmap features (CPU)
  train    train one microWakeWord candidate network (selected GPU or CPU)

NO TTS, ASR, acoustic augmentation or dataset regeneration occurs here.
No venv, no Docker, no edits to source audio.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import multiprocessing as mp
import os
import re
import shutil
import subprocess
import sys
import time
from collections import Counter
from pathlib import Path

SR = 16000
N = SR * 4
ANCHOR_END = round(3.4 * SR)
SPLITS = {"train": "training", "val": "validation", "test": "testing"}
KINDS = ("positive", "augclean", "hardmix", "speech", "hard", "noise")
VERSION = "martin_v6_fit_features_v1"
# Single WakeForge MixedNet configuration and class sampling weights.
MODELS = (dict(gpu=0, name="wakeword", w=(30,22,13,20,12,3), pos=1.0,
               filters="48,64,64,64", kernels="[5], [9,13], [13,19], [27]"),)



def sha256_file(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def read_jsonl(p: Path):
    with p.open(encoding="utf-8") as f:
        for line_no, line in enumerate(f, 1):
            if line.strip():
                try:
                    yield json.loads(line)
                except json.JSONDecodeError as ex:
                    raise ValueError(f"{p}:{line_no}: {ex}") from ex


def manifest_path(args, group, split):
    if group == "positive":
        return args.positive / f"positive_{split}.jsonl"
    if group in ("augclean", "hardmix"):
        return args.augmented / f"positive_{group}_{split}.jsonl"
    return None


def source_path(row, base):
    v = row.get("file") or row.get("path")
    if not isinstance(v, str) or not v:
        raise ValueError(f"Manifest row missing file/path: {str(row)[:250]}")
    p = Path(v)
    return (p if p.is_absolute() else base / p).resolve()


def positive_records(args, group, split):
    p = manifest_path(args, group, split)
    root = args.positive if group == "positive" else args.augmented
    for i, row in enumerate(read_jsonl(p)):
        if row.get("keyword") != args.keyword or row.get("label") != 1:
            raise ValueError(f"Unexpected label/word in {p}, row {i+1}")
        a = row.get("word_start_sample")
        b = row.get("word_end_sample")
        if a is None or b is None:
            raise ValueError(f"Missing word timing in {p}, row {i+1}")
        rate = int(row.get("sample_rate", SR))
        a = round(int(a) * SR / rate)
        b = round(int(b) * SR / rate)
        if not 0 <= a < b <= N:
            raise ValueError(f"Invalid word timing {a}:{b} in {p}, row {i+1}")
        wav = source_path(row, root)
        yield (group, str(wav), a, b, f"{group}:{split}:{i:09d}")


def negative_paths(args, group, split):
    folder = args.negative / split / "negative" / group
    if not folder.is_dir():
        raise FileNotFoundError(f"Missing original V2 negatives: {folder}")
    paths = sorted(folder.glob("*.flac"))
    if not paths:
        raise RuntimeError(f"Empty original V2 negatives: {folder}")
    return paths


def negative_records(args, group, split):
    paths = negative_paths(args, group, split)
    # Keep all training negatives; sample a deterministic, spread-out eval subset.
    count = len(paths) if split == "train" else min(len(paths), args.negative_eval)
    for i in range(count):
        index = (i * len(paths) // count) if count < len(paths) else i
        yield (group, str(paths[index]), -1, -1, f"{group}:{split}:{i:09d}")


def ambient_records(args, split):
    paths = negative_paths(args, "speech", split)
    n = min(len(paths), args.ambient_val if split == "val" else args.ambient_test)
    for i in range(n):
        index = i * len(paths) // n
        yield ("ambient", str(paths[index]), -1, -1, f"ambient:{split}:{i:09d}")


def get_records(args, group, split):
    if group in ("positive", "augclean", "hardmix"):
        return positive_records(args, group, split)
    return negative_records(args, group, split)


def plan(args):
    result = {"version": VERSION, "positive_root": str(args.positive.resolve()),
              "augmented_root": str(args.augmented.resolve()),
              "negative_root": str(args.negative.resolve()),
              "anchor_end": ANCHOR_END, "sr": SR,
              "shard_size": args.shard_size, "negative_eval": args.negative_eval,
              "ambient_val": args.ambient_val, "ambient_test": args.ambient_test,
              "manifests": {}, "negative_inventory": {}}
    counts = {}
    for split in SPLITS:
        for group in KINDS:
            if group in ("positive", "augclean", "hardmix"):
                p = manifest_path(args, group, split)
                if not p.is_file():
                    raise FileNotFoundError(f"Missing positive manifest {p}")
                n = 0
                # Validate every row's schema and paths. No waveform loading here.
                for kind, filename, a, b, key in positive_records(args, group, split):
                    if not Path(filename).is_file():
                        raise FileNotFoundError(f"Missing audio {key}: {filename}")
                    n += 1
                if n == 0:
                    raise RuntimeError(f"Empty manifest: {p}")
                result["manifests"][f"{split}/{group}"] = {
                    "path": str(p.resolve()), "sha256": sha256_file(p), "count": n}
            else:
                files = negative_paths(args, group, split)
                n = len(files) if split == "train" else min(len(files), args.negative_eval)
                # Protect negative inventory from inadvertent re-layout.
                result["negative_inventory"][f"{split}/{group}"] = {
                    "path": str((args.negative / split / 'negative' / group).resolve()),
                    "available": len(files),
                    "sampled": n,
                    "first": files[0].name, "last": files[-1].name}
            counts[f"{split}/{group}"] = n
    for split, maxn in (("val", args.ambient_val), ("test", args.ambient_test)):
        files = negative_paths(args, "speech", split)
        counts[f"{split}/ambient"] = min(len(files), maxn)
    return result, counts


def load_plan(args, create=False):
    cfg, counts = plan(args)
    p = args.work / "feature_plan.json"
    if p.is_file():
        stored = json.loads(p.read_text(encoding="utf-8"))
        if stored != cfg:
            raise RuntimeError(f"Feature source/config changed; refusing mixed cache: {p}. "
                               "Use a different --work directory.")
    elif create:
        args.work.mkdir(parents=True, exist_ok=True)
        temp = p.with_suffix(".tmp")
        temp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(temp, p)
    else:
        raise RuntimeError(f"No feature plan {p}; run 'features' first")
    return counts


def check(args):
    cfg, counts = plan(args)
    print("INPUTS:", flush=True)
    print("  clean positives:     ", args.positive, flush=True)
    print("  prebuilt augmented:  ", args.augmented, flush=True)
    print("  original V2 negatives:", args.negative, flush=True)
    print("  isolated work:       ", args.work, flush=True)
    print("SPLIT / CLASS COUNTS:", flush=True)
    for k, v in counts.items():
        print(f"  {k:19s} {v:,}", flush=True)
    print("READY: no datasets or models changed", flush=True)
    return cfg, counts


def feature_worker(task):
    import numpy as np
    import soundfile as sf
    from pymicro_features import MicroFrontend
    kind, filename, a, b, key = task
    y, sr = sf.read(filename, dtype="float32")
    if sr != SR or y.ndim != 1 or len(y) != N or not np.isfinite(y).all():
        raise RuntimeError(f"Not four-second 16kHz mono finite PCM: {filename} ({sr}, {y.shape})")
    if a >= 0:
        delta = ANCHOR_END - b
        if not 0 <= a + delta < b + delta <= N:
            raise RuntimeError(f"Bad aligned timing: {filename} {a}:{b} shift={delta}")
        if delta > 0:
            tmp = np.zeros_like(y)
            tmp[delta:] = y[:-delta]
            y = tmp
        elif delta < 0:
            tmp = np.zeros_like(y)
            tmp[:delta] = y[-delta:]
            y = tmp
    y = np.clip(y, -1, 32767. / 32768.)
    raw = (y * 32768.).astype("<i2").tobytes()
    extractor = MicroFrontend()
    offset = 0
    features = []
    while offset + 320 < len(raw):
        packet = extractor.process_samples(raw[offset:offset+320])
        if packet.samples_read <= 0:
            raise RuntimeError(f"MicroFrontend did not advance: {filename}")
        offset += int(packet.samples_read) * 2
        if packet.features:
            features.append(packet.features)
    features = np.asarray(features, dtype=np.float32)
    if features.ndim != 2 or features.shape[1] != 40 or not 385 <= features.shape[0] <= 401:
        raise RuntimeError(f"Unexpected shape {features.shape}: {filename}")
    return features.astype(np.float16), {
        "sample_id": key, "label": 1 if a >= 0 else 0,
        "scenario": kind, "source_file": filename,
        "word_start_sample": (a + ANCHOR_END - b) if a >= 0 else None,
        "word_end_sample": ANCHOR_END if a >= 0 else None,
        "feature_shape": list(features.shape), "sample_rate": SR}


def chunked(items, n):
    it = iter(items)
    while True:
        group = []
        for _ in range(n):
            try:
                group.append(next(it))
            except StopIteration:
                break
        if not group:
            return
        yield group


def elapsed_fmt(seconds):
    if seconds is None:
        return "--:--:--"
    seconds = max(int(seconds), 0)
    return f"{seconds//3600:02d}:{seconds//60%60:02d}:{seconds%60:02d}"


def progress(label, done, total, start, new, cached):
    dt = max(time.monotonic() - start, .001)
    speed = done / dt
    eta = (total - done) / speed if speed else None
    f = done / total if total else 1
    units = min(28, int(f * 28))
    print(f"[{'#'*units}{'.'*(28-units)}] {f:6.1%} {label:19s} "
          f"{done:,}/{total:,} | {speed:.1f}/s ETA {elapsed_fmt(eta)} "
          f"new={new:,} cached={cached:,}", flush=True)


def feature_path(args, kind, split):
    return args.work / "features" / kind / SPLITS.get(split, split)


def generate_shards(args, group, split, iterator, n, pool):
    from mmap_ninja.ragged import RaggedMmap
    folder = feature_path(args, group, split)
    folder.mkdir(parents=True, exist_ok=True)
    started = last = time.monotonic()
    done = new = cached = 0
    actual = 0
    for shard_idx, tasks in enumerate(chunked(iterator, args.shard_size)):
        actual += len(tasks)
        tag = f"v6_{shard_idx:05d}_mmap"
        target = folder / tag
        marker = folder / f"{tag}.complete"
        sidecar = folder / f"{tag}.labels.jsonl"
        if target.is_dir() and marker.is_file() and sidecar.is_file() and marker.read_text().strip() == f"{VERSION}:{len(tasks)}":
            if sum(1 for _ in sidecar.open(encoding="utf-8")) == len(tasks):
                cached += len(tasks)
                done += len(tasks)
                if time.monotonic() - last > args.status_interval:
                    progress(f"{split}/{group}", done, n, started, new, cached)
                    last = time.monotonic()
                continue
        # Clear only an incomplete shard within THIS isolated work root.
        if target.exists():
            shutil.rmtree(target)
        marker.unlink(missing_ok=True)
        sidecar.unlink(missing_ok=True)
        tmp = sidecar.with_name(sidecar.name + ".partial")
        try:
            with tmp.open("w", encoding="utf-8") as out:
                def spectra():
                    for feat, meta in pool.imap(feature_worker, tasks, chunksize=8):
                        out.write(json.dumps(meta, ensure_ascii=False, separators=(",", ":")) + "\n")
                        yield feat
                RaggedMmap.from_generator(out_dir=str(target), sample_generator=spectra(), batch_size=100, verbose=False)
            os.replace(tmp, sidecar)
            marker.write_text(f"{VERSION}:{len(tasks)}", encoding="utf-8")
        except BaseException:
            tmp.unlink(missing_ok=True)
            if target.exists():
                shutil.rmtree(target)
            raise
        done += len(tasks)
        new += len(tasks)
        if time.monotonic() - last > args.status_interval:
            progress(f"{split}/{group}", done, n, started, new, cached)
            last = time.monotonic()
    if actual != n:
        raise RuntimeError(f"Wrong {split}/{group} manifest size: processed {actual}, expected {n}")
    expected = {f"v6_{i:05d}_mmap.complete" for i in range((n+args.shard_size-1)//args.shard_size)}
    excess = {f.name for f in folder.glob("v6_*_mmap.complete")} - expected
    if excess:
        raise RuntimeError(f"Unexpected stale shards in {folder}: {sorted(excess)[:5]}")
    progress(f"{split}/{group}", done, n, started, new, cached)


def run_features(args):
    # Import checks before starting potentially lengthy job.
    import numpy
    import soundfile
    import pymicro_features
    from mmap_ninja.ragged import RaggedMmap
    counts = load_plan(args, create=True)
    free = shutil.disk_usage(args.work).free / (1024**3)
    print(f"AVAILABLE DISK {free:.1f} GiB, workers={args.workers}, shard_size={args.shard_size}", flush=True)
    if free < 8:
        raise RuntimeError("Less than 8 GiB free; insufficient for feature preparation")
    overall = time.monotonic()
    ctx = mp.get_context("fork")
    # CPU-only feature extraction. Each worker imports only audio/DSP modules.
    with ctx.Pool(processes=args.workers, maxtasksperchild=1500) as pool:
        for split in SPLITS:
            for group in KINDS:
                key = f"{split}/{group}"
                generate_shards(args, group, split, get_records(args, group, split), counts[key], pool)
            if split in ("val", "test"):
                name = f"{SPLITS[split]}_ambient"
                iterator = ambient_records(args, split)
                generate_shards(args, "ambient", name, iterator, counts[f"{split}/ambient"], pool)
    # A feature_plan.json is written before the shards exist. Only this sentinel
    # indicates the ENTIRE feature generation succeeded on this run.
    (args.work/'.wakeforge_features.done').write_text(
        sha256_file(args.work/'feature_plan.json'), encoding='utf-8')
    print(f"FEATURES COMPLETED in {elapsed_fmt(time.monotonic()-overall)}: {args.work/'features'}", flush=True)


def confirm_features(args):
    counts = load_plan(args, create=False)
    for split in SPLITS:
        for group in KINDS:
            folder = feature_path(args, group, split)
            n = counts[f"{split}/{group}"]
            required = (n + args.shard_size - 1)//args.shard_size
            markers = list(folder.glob("v6_*_mmap.complete"))
            if len(markers) != required:
                raise RuntimeError(f"Incomplete feature bank: {folder} {len(markers)}/{required} shards")
            for i in range(required):
                tag = f"v6_{i:05d}_mmap"
                if not ((folder / tag).is_dir() and (folder / (tag + '.labels.jsonl')).is_file()):
                    raise RuntimeError(f"Incomplete feature shard {folder/tag}")
        if split in ("val", "test"):
            folder = args.work / "features" / "ambient" / (SPLITS[split] + "_ambient")
            n = counts[f"{split}/ambient"]
            required = (n + args.shard_size - 1)//args.shard_size
            if len(list(folder.glob('v6_*_mmap.complete'))) != required:
                raise RuntimeError(f"Incomplete ambient features: {folder}")
    print("ALL FEATURE GROUPS VERIFIED", flush=True)
    return counts


def feature_entry(dir_path, weight, positive, strategy="truncate_start", penalty=1.0):
    return {"features_dir": str(dir_path.resolve()), "sampling_weight": weight,
            "penalty_weight": penalty, "truth": positive,
            "truncation_strategy": strategy, "type": "mmap"}


def training_config(args, model):
    base = args.work / "features"
    w = model["w"]
    sources = [
        feature_entry(base / "positive", w[0], True),
        feature_entry(base / "augclean", w[1], True),
        feature_entry(base / "hardmix", w[2], True),
        feature_entry(base / "speech", w[3], False, "random"),
        feature_entry(base / "hard", w[4], False, "random"),
        feature_entry(base / "noise", w[5], False, "random"),
        feature_entry(base / "ambient", 0, False)
    ]
    steps = [int(args.steps * .5), int(args.steps * .3)]
    steps.append(args.steps - sum(steps))
    return {
        "window_step_ms": 10, "clip_duration_ms": 3200,
        "train_dir": str((args.work / "runs" / f"gpu{model['gpu']}_{model['name']}" / "model").resolve()),
        "features": sources, "training_steps": steps,
        "positive_class_weight": [model['pos']] * 3,
        "negative_class_weight": [1.0] * 3,
        "learning_rates": [.00065, .00019, .00005],
        "batch_size": args.batch,
        "time_mask_max_size": [6, 8, 9], "time_mask_count": [1, 1, 1],
        "freq_mask_max_size": [2, 3, 4], "freq_mask_count": [1, 1, 1],
        "eval_step_interval": args.eval_interval,
        "target_minimization": 0.0, "minimization_metric": None,
        "maximization_metric": "average_viable_recall"}


def training_command(args, model, cfg):
    return [sys.executable, '-u', '-m', 'microwakeword.model_train_eval',
            '--training_config', str(cfg), '--train', '1',
            '--restore_checkpoint', '1' if args.resume else '0',
            '--test_tf_nonstreaming', '0', '--test_tflite_nonstreaming', '0',
            '--test_tflite_nonstreaming_quantized', '0', '--test_tflite_streaming', '0',
            '--test_tflite_streaming_quantized', '1', '--use_weights', 'best_weights',
            'mixednet', '--pointwise_filters', model['filters'], '--repeat_in_block', '1,1,1,1',
            '--mixconv_kernel_sizes', model['kernels'], '--residual_connection', '0,0,0,0',
            '--first_conv_filters', '32', '--first_conv_kernel_size', '5', '--stride', '3']


def prepare_overlay(args):
    import importlib
    importlib.import_module('microwakeword.model_train_eval')
    import microwakeword.train as tm
    orig_pkg = Path(tm.__file__).resolve().parent
    src = orig_pkg / 'train.py'
    current = src.read_text(encoding='utf-8')
    fingerprint = hashlib.sha256(current.encode('utf-8')).hexdigest()
    overlay = args.work / 'microwakeword_overlay'
    cloned = overlay / 'microwakeword'
    fingerprint_file = overlay / 'source.sha256'
    if cloned.exists():
        if not fingerprint_file.is_file() or fingerprint_file.read_text().strip() != fingerprint:
            raise RuntimeError('Local microWakeWord upstream changed, refusing to reuse overlay')
        return overlay
    overlay.mkdir(parents=True, exist_ok=True)
    shutil.copytree(orig_pkg, cloned, ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    patch = cloned / 'train.py'
    code = patch.read_text(encoding='utf-8')
    if 'train_ground_truth.ravel()' not in code and 'train_ground_truth.reshape(-1)' not in code:
        old = 'class_weights.get)(\n            train_ground_truth\n        )'
        if old not in code:
            shutil.rmtree(cloned)
            raise RuntimeError('Unrecognized microWakeWord class-weight path; refusing blind change')
        code = code.replace(old, 'class_weights.get)(\n            train_ground_truth.ravel()\n        )', 1)
        patch.write_text(code, encoding='utf-8')
        print('MicroWakeWord class-weights patched in isolated overlay', flush=True)
    utils_file = cloned / 'utils.py'
    utils_code = utils_file.read_text(encoding='utf-8')
    original = '            assert spectrogram.shape[0] % stride == 0'
    if original not in utils_code:
        raise RuntimeError('Unknown MicroWakeWord TFLite calibrator version; cannot patch stride')
    utils_file.write_text(utils_code.replace(original,
        '            spectrogram = np.pad(spectrogram, ((0, -spectrogram.shape[0] % stride), (0, 0)), mode="edge")', 1), encoding='utf-8')
    train_file = cloned / 'train.py'
    train_code = train_file.read_text(encoding='utf-8')
    old_loop='    for training_step in range(1, training_steps_max + 1):'
    if old_loop not in train_code:
        raise RuntimeError('Unknown MicroWakeWord resume loop; cannot patch')
    train_file.write_text(train_code.replace(old_loop,
        '    for training_step in range(int(optimizer.iterations.numpy()) + 1, training_steps_max + 1):',1), encoding='utf-8')
    # On checkpoint resume, retain best validation score instead of resetting it.
    train_file = cloned / 'train.py'
    train_code = train_file.read_text(encoding='utf-8')
    point='    best_no_faph_cutoff = 1.0'
    if point not in train_code:
        raise RuntimeError('Unknown MicroWakeWord validation state: cannot patch safe resume')
    insert = """    if int(optimizer.iterations.numpy()) > 0:
        import re
        _log = os.path.join(config["train_dir"], "..", "train.log")
        if not os.path.isfile(_log):
            raise RuntimeError("Missing log to restore best score: " + _log)
        with open(_log, encoding="utf-8", errors="replace") as _handle:
            _history = re.findall(r"So far the best minimization quantity is ([0-9.]+) with best maximization quantity of ([0-9.]+)%", _handle.read())
        if _history:
            best_minimization_quantity = float(_history[-1][0])
            best_maximization_quantity = float(_history[-1][1]) / 100.0
"""
    train_file.write_text(train_code.replace(point, point+'\n'+insert,1), encoding='utf-8')
    fingerprint_file.write_text(fingerprint, encoding='utf-8')
    return overlay


def parse_step(path, maximum, interval):
    if not path.is_file():
        return 0
    with path.open('rb') as f:
        size = f.seek(0, 2)
        f.seek(max(0, size - 800000))
        tail = f.read()
    steps = [int(x) for x in re.findall(rb'Step #(\d+):', tail)]
    steps.extend((int(a) - 1) * interval + int(b)
                 for a, b in re.findall(rb'Validation Batch #(\d+):[^\r\n]*?Mini-Batch #(\d+)', tail))
    return max((x for x in steps if 0 <= x <= maximum), default=0)


def start_train(args):
    import yaml
    confirm_features(args)
    # Probe selected GPU in a subprocess so TensorFlow cannot occupy every card
    # in this parent process and interfere with TTS or training child processes.
    probe_env=os.environ.copy()
    probe_env['CUDA_VISIBLE_DEVICES']=str(args.gpu) if args.gpu>=0 else '-1'
    check=subprocess.run([sys.executable,'-c',
        'import tensorflow as tf; assert tf.config.list_physical_devices("GPU"), "No selected CUDA GPU available to TensorFlow"'],
        env=probe_env, capture_output=True,text=True,timeout=120) if args.gpu>=0 else None
    if check is not None and check.returncode:
        raise RuntimeError(f'GPU {args.gpu} unavailable to TensorFlow: {check.stderr[-1400:]}')
    overlay = prepare_overlay(args)
    # Protect resumed models from silent changes in training hyperparameters.
    training_plan = {
        "version": "martin_v6_fit_training_v1",
        "steps": args.steps, "batch": args.batch,
        "eval_interval": args.eval_interval,
        "feature_plan_sha256": sha256_file(args.work / "feature_plan.json"),
        "model_configs": {str(args.gpu): training_config(args, dict(m, gpu=args.gpu)) for m in MODELS},
    }
    plan_path = args.work / "training_plan.json"
    if plan_path.is_file():
        if json.loads(plan_path.read_text(encoding="utf-8")) != training_plan:
            raise RuntimeError("Training config changed after a previous run; refusing to "
                               "mix model checkpoints. Use another --work.")
    else:
        tmp = plan_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(training_plan, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, plan_path)
    runroot = args.work / 'runs'
    existing = [str(runroot / f"gpu{args.gpu}_{m['name']}" / 'model') for m in MODELS
                if (runroot / f"gpu{args.gpu}_{m['name']}" / 'model').exists()]
    if existing and not args.resume:
        raise RuntimeError('Existing training checkpoints. Use --resume; nothing overwritten: ' + ', '.join(existing))
    selected = [dict(x, gpu=args.gpu) for x in MODELS]
    if not selected:
        raise RuntimeError('No matching model/GPU choices')
    procs = []
    runroot.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    try:
        for model in selected:
            loc = runroot / f"gpu{model['gpu']}_{model['name']}"
            loc.mkdir(parents=True, exist_ok=True)
            cfg = loc / 'training_parameters.yaml'
            cfg.write_text(yaml.safe_dump(training_config(args, model), allow_unicode=True, sort_keys=False), encoding='utf-8')
            logfile = loc / 'train.log'
            fp = logfile.open('a' if args.resume else 'w', encoding='utf-8')
            cmd = training_command(args, model, cfg)
            if args.resume and (loc / "model" / "best_weights.weights.h5").is_file() and parse_step(logfile, args.steps, args.eval_interval) >= args.steps:
                cmd[cmd.index("--train") + 1] = "0"
                print(f"GPU{model['gpu']} {model['name']}: all steps done; EXPORT ONLY", flush=True)
            env = os.environ.copy()
            env['CUDA_VISIBLE_DEVICES'] = str(model['gpu']) if model['gpu'] >= 0 else '-1'
            env['TF_FORCE_GPU_ALLOW_GROWTH'] = 'true'
            env['TF_CPP_MIN_LOG_LEVEL'] = '2'
            env['TF_NUM_INTRAOP_THREADS'] = '2'
            env['TF_NUM_INTEROP_THREADS'] = '1'
            env['OPENBLAS_NUM_THREADS'] = '1'
            env['OMP_NUM_THREADS'] = '2'
            env['PYTHONUNBUFFERED'] = '1'
            env['PYTHONPATH'] = str(overlay) + os.pathsep + env.get('PYTHONPATH', '')
            fp.write('COMMAND: ' + ' '.join(cmd) + '\n'); fp.flush()
            proc = subprocess.Popen(cmd, cwd=str(args.work), env=env, stdout=fp, stderr=subprocess.STDOUT)
            procs.append((model, proc, fp, logfile, time.monotonic()))
            print(f"START GPU{model['gpu']} {model['name']}: PID={proc.pid}, log={logfile}", flush=True)
        last_steps = {m['gpu']: 0 for m, _, _, _, _ in procs}
        while True:
            problems = False
            for m, proc, fp, logfile, launched in procs:
                step = max(last_steps[m['gpu']], parse_step(logfile, args.steps, args.eval_interval))
                last_steps[m['gpu']] = step
                dt = max(.001, time.monotonic()-launched)
                speed = step/dt
                eta = elapsed_fmt((args.steps-step)/speed) if speed>0 and step<args.steps else '--:--:--'
                code = proc.poll()
                state = 'running' if code is None else ('COMPLETE' if code == 0 else f'FAILED exit={code}')
                problems |= code not in (None, 0)
                print(f"GPU{m['gpu']} {m['name']:17s} {step:>6,}/{args.steps:,} "
                      f"{step/max(args.steps,1):6.1%} {speed:.2f}step/s ETA {eta} {state}", flush=True)
            if problems and all(p.poll() is not None for _, p, _, _, _ in procs):  # MARTIN_V6_NO_FAIL_FAST
                for _, proc, _, _, _ in procs:
                    if proc.poll() is None:
                        proc.terminate()
                raise RuntimeError('GPU training failed; inspect runs/gpu*/train.log')
            if all(p.poll() is not None for _, p, _, _, _ in procs):
                if problems: raise RuntimeError('Training/export failed; inspect train.log')
                print('SINGLE MODEL TRAINING FINISHED', flush=True)
                break
            time.sleep(args.status_interval)
    except KeyboardInterrupt:
        for _, proc, _, _, _ in procs:
            if proc.poll() is None:
                proc.terminate()
        raise
    finally:
        for _, proc, fp, _, _ in procs:
            if proc.poll() is None:
                proc.terminate()
            proc.wait()
            fp.close()


def parse_args():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    p.add_argument('action', choices=['check', 'features', 'train'])
    p.add_argument('--keyword',required=True)
    p.add_argument('--gpu',type=int,default=-1)
    p.add_argument('--positive', type=Path, default=Path('/root/martin/martin_positives_v6'))
    p.add_argument('--augmented', type=Path, default=Path('/root/martin/martin_augmented_v6'))
    p.add_argument('--negative', type=Path, default=Path('/root/martin/martin_dataset_v2'))
    p.add_argument('--work', type=Path, default=Path('/root/martin/martin_training_v6_prebuilt'))
    p.add_argument('--workers', type=int, default=28)
    p.add_argument('--shard-size', type=int, default=2000)
    p.add_argument('--negative-eval', type=int, default=2000)
    p.add_argument('--ambient-val', type=int, default=1800)
    p.add_argument('--ambient-test', type=int, default=3000)
    p.add_argument('--steps', type=int, default=25000)
    p.add_argument('--batch', type=int, default=128)
    p.add_argument('--eval-interval', type=int, default=2000)
    p.add_argument('--status-interval', type=float, default=5.)
    p.add_argument('--resume', action='store_true', help='Restore existing microWakeWord checkpoints when training')
    p.add_argument('--gpus', type=str, default='0')
    args = p.parse_args()
    if args.gpu < -1:
        p.error('--gpu must be >= -1')
    args.gpus = (args.gpu,)
    for field in ('workers','shard_size','negative_eval','ambient_val','ambient_test','steps','batch','eval_interval'):
        if getattr(args, field) < 1:
            p.error(f'--{field.replace("_", "-")} must be >=1')
    if args.status_interval <= 0:
        p.error('--status-interval must be positive')
    return args


def main():
    args = parse_args()
    if args.action == 'check':
        check(args)
    elif args.action == 'features':
        run_features(args)
    elif args.action == 'train':
        start_train(args)


if __name__ == '__main__':
    main()
