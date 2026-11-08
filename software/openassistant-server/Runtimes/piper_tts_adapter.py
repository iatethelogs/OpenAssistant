"""CPU Piper TTS adapter, compatible with OpenAssistant's /synthesize WAV protocol.

Russian voice weights are downloaded on first installation and cached locally.
"""
import argparse
import io
import json
import os
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.request import urlopen

import numpy as np
from scipy.signal import resample_poly
from math import gcd
from piper import PiperVoice, SynthesisConfig

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=8020)
args = parser.parse_args()

VOICES = {"ru_RU-denis-medium": "denis",
          "ru_RU-dmitri-medium": "dmitri",
          "ru_RU-irina-medium": "irina",
          "ru_RU-ruslan-medium": "ruslan"}
name = os.environ.get("OPENASSISTANT_MODEL", "ru_RU-denis-medium")
if name not in VOICES:
    raise ValueError("Unsupported Russian Piper voice: " + name)

directory = Path(os.environ.get("OPENASSISTANT_VOICE_DIR",
                               str(Path(__file__).resolve().parent / "piper-voices")))
directory.mkdir(parents=True, exist_ok=True)
model_path = directory / (name + ".onnx")
config_path = directory / (name + ".onnx.json")
source = ("https://huggingface.co/rhasspy/piper-voices/resolve/main/"
          + "ru/ru_RU/" + VOICES[name] + "/medium/")

def ensure_file(path, minimum):
    if path.exists() and path.stat().st_size >= minimum:
        return
    tmp = path.with_suffix(path.suffix + ".download")
    try:
        with urlopen(source + path.name, timeout=60) as response, open(tmp, "wb") as output:
            while block := response.read(1024 * 1024):
                output.write(block)
        if tmp.stat().st_size < minimum:
            raise RuntimeError("Downloaded Piper voice is incomplete: " + path.name)
        os.replace(tmp, path)
    finally:
        if tmp.exists():
            tmp.unlink()

ensure_file(model_path, 1024 * 1024)
ensure_file(config_path, 256)
voice = PiperVoice.load(str(model_path))
lock = threading.Lock()
print("READY Piper", name, "CPU", flush=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *values):
        print("HTTP:", fmt % values, flush=True)

    def reply(self, status, body):
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path == "/health":
            self.reply(200, {"status": "ok", "model": name, "engine": "piper"})
        else:
            self.reply(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/synthesize":
            self.reply(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > 20000:
                self.reply(413, {"error": "invalid request length"})
                return
            request = json.loads(self.rfile.read(length))
            text = str(request.get("text", "")).strip()
            rate = int(request.get("sample_rate", 24000))
            if not 1 <= len(text) <= 1200 or not 8000 <= rate <= 48000:
                self.reply(400, {"error": "invalid text or sample rate"})
                return
            try:
                length_scale = float(request.get("length_scale", 1.0))
                noise_scale = float(request.get("noise_scale", 0.667))
                noise_w_scale = float(request.get("noise_w_scale", 0.8))
                volume = float(request.get("volume", 1.0))
                normalize_audio = request.get("normalize_audio", True)
            except (ValueError, TypeError):
                self.reply(400, {"error": "invalid Piper parameters"})
                return
            import math
            if (not all(map(math.isfinite, (length_scale, noise_scale, noise_w_scale, volume))) or
                    not 0.5 <= length_scale <= 2.5 or
                    not 0 <= noise_scale <= 2 or
                    not 0 <= noise_w_scale <= 2 or
                    not 0 <= volume <= 2 or type(normalize_audio) is not bool):
                self.reply(400, {"error": "Piper parameters out of range"})
                return
            config = SynthesisConfig(
                length_scale=length_scale, noise_scale=noise_scale,
                noise_w_scale=noise_w_scale, volume=volume,
                normalize_audio=normalize_audio)
            with lock:
                original = io.BytesIO()
                with wave.open(original, "wb") as wav:
                    voice.synthesize_wav(text, wav, syn_config=config)
            original.seek(0)
            with wave.open(original, "rb") as wav:
                if wav.getnchannels() != 1 or wav.getsampwidth() != 2:
                    raise ValueError("Piper voice did not return mono PCM16")
                sample_rate = wav.getframerate()
                samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2")
            if sample_rate != rate:
                factor = gcd(sample_rate, rate)
                resampled = resample_poly(samples.astype(np.float32),
                                          rate // factor, sample_rate // factor)
                samples = np.clip(np.rint(resampled), -32768, 32767).astype("<i2")
            if len(samples) > rate * 300:
                raise ValueError("Audio exceeds five minutes")
            output = io.BytesIO()
            with wave.open(output, "wb") as wav:
                wav.setnchannels(1)
                wav.setsampwidth(2)
                wav.setframerate(rate)
                wav.writeframes(samples.tobytes())
            data = output.getvalue()
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        except Exception as exc:
            print("Piper error:", repr(exc), flush=True)
            self.reply(500, {"error": str(exc)[:300]})

ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
