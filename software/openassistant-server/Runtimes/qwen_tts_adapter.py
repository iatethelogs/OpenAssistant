"""Qwen3-TTS HTTP adapter: official and accelerated CUDA-graph engines."""
import os
import io
import json
import argparse
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

p = argparse.ArgumentParser()
p.add_argument("--port", type=int, default=8020)
args = p.parse_args()

import numpy as np
import soundfile as sf
import torch
from scipy.signal import resample_poly
from math import gcd
from qwen_tts import Qwen3TTSModel

requested = os.environ.get("OPENASSISTANT_DEVICE", "cpu")
if requested == "cuda" and not torch.cuda.is_available():
    raise RuntimeError("Запрошен GPU, но PyTorch не видит CUDA.")
device = "cuda:0" if requested == "cuda" else "cpu"
name = os.environ.get("OPENASSISTANT_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice")
engine = os.environ.get("OPENASSISTANT_TTS_ENGINE", "official")
if engine == "faster":
    if device != "cuda:0":
        raise RuntimeError("Faster Qwen3-TTS uses CUDA Graphs and requires a CUDA GPU; choose the regular engine for CPU.")
    from faster_qwen3_tts import FasterQwen3TTS
    model = FasterQwen3TTS.from_pretrained(
        name, device=device, dtype=torch.bfloat16, attn_implementation="sdpa")
    model.warmup(prefill_len=100)
else:
    model = Qwen3TTSModel.from_pretrained(
        name, device_map=device, dtype=torch.float16 if requested == "cuda" else torch.float32,
        attn_implementation="sdpa")
model_lock = threading.Lock()
print("READY Qwen3-TTS", engine, "on", device, flush=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *values):
        print("HTTP:", fmt % values, flush=True)
    def reply(self, status, obj):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        if self.path == "/health":
            self.reply(200, {"status": "ok", "model": name})
        else:
            self.reply(404, {"error": "not found"})
    def do_POST(self):
        if self.path != "/synthesize":
            self.reply(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 1 <= length <= 20000:
                self.reply(413, {"error": "request too large"})
                return
            args = json.loads(self.rfile.read(length))
            text = str(args.get("text", ""))
            if not 1 <= len(text) <= 1200:
                self.reply(400, {"error": "invalid text length"})
                return
            voice = str(args.get("voice", "")).strip()
            instruction = str(args.get("instruct") or "").strip()
            language = str(args.get("language") or "Russian")
            sample_rate = int(args.get("sample_rate", 24000))
            if sample_rate < 8000 or sample_rate > 48000:
                self.reply(400, {"error": "invalid sample rate"})
                return
            if "VoiceDesign" in name:
                if len(instruction) < 8 or len(instruction) > 1200:
                    self.reply(400, {"error": "VoiceDesign requires a voice description"})
                    return
                with model_lock:
                    wavs, sr = model.generate_voice_design(
                        text=text, language=language, instruct=instruction)
            else:
                if not voice or voice.lower() == "default":
                    voice = "Ryan"
                speakers = (model.model if engine == "faster" else model).get_supported_speakers()
                speaker = next((x for x in speakers if x.lower() == voice.lower()), None)
                if speaker is None:
                    self.reply(400, {"error": "unknown voice " + voice, "supported": speakers})
                    return
                with model_lock:
                    wavs, sr = model.generate_custom_voice(text=text, language=language, speaker=speaker,
                        instruct=instruction if "1.7B" in name and instruction else None)
            samples = np.asarray(wavs[0], dtype=np.float32).flatten()
            if sr != sample_rate:
                common = gcd(sr, sample_rate)
                samples = resample_poly(samples, sample_rate // common, sr // common)
            if len(samples) > sample_rate * 35:
                raise ValueError("generated audio is too long")
            buffer = io.BytesIO()
            sf.write(buffer, samples, sample_rate, subtype="PCM_16", format="WAV")
            data = buffer.getvalue()
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except Exception as exc:
            print("TTS error:", repr(exc), flush=True)
            self.reply(500, {"error": str(exc)[:300]})

ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
