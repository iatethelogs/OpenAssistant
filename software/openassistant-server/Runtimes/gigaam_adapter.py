"""Простой локальный HTTP-адаптер GigaAM v3. Работает с официальным пакетом gigaam."""
import io
import json
import os
import argparse
import tempfile
import wave
import numpy as np
from email.parser import BytesParser
from email.policy import default
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

p = argparse.ArgumentParser()
p.add_argument("--port", type=int, default=8016)
args = p.parse_args()

from gigaam_torchaudio_fallback import ensure_torchaudio
ensure_torchaudio()
import gigaam
import torch

requested = os.environ.get("OPENASSISTANT_DEVICE", "cpu")
if requested == "cuda" and not torch.cuda.is_available():
    raise RuntimeError("Запрошен GPU, но PyTorch не видит CUDA. Проверьте совместимость драйвера, torch и GPU.")
device = "cuda" if requested == "cuda" else "cpu"
model = gigaam.load_model("v3_e2e_rnnt", device=device, use_flash=False)
print("READY gigaam v3_e2e_rnnt on", device, flush=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *values):
        print("HTTP:", fmt % values, flush=True)
    def reply(self, status, obj):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)
    def do_GET(self):
        if self.path == "/health":
            self.reply(200, {"status": "ok", "model": "gigaam-v3"})
        else:
            self.reply(404, {"error": "not found"})
    def do_POST(self):
        if self.path != "/v1/audio/transcriptions":
            self.reply(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 1 <= length <= 4 * 1024 * 1024:
                self.reply(413, {"error": "audio too large"})
                return
            raw = self.rfile.read(length)
            mime = self.headers.get("Content-Type", "")
            message = BytesParser(policy=default).parsebytes(
                ("Content-Type: " + mime + "\r\nMIME-Version: 1.0\r\n\r\n").encode() + raw
            )
            part = next((x for x in message.walk() if x.get_param("name", header="content-disposition") == "file"), None)
            if part is None:
                self.reply(400, {"error": "file part required"})
                return
            audio = part.get_payload(decode=True)
            with wave.open(io.BytesIO(audio), "rb") as w:
                if w.getnchannels() != 1 or w.getsampwidth() != 2 or w.getframerate() != 16000:
                    self.reply(400, {"error": "expected 16kHz mono PCM16"})
                    return
                samples = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2")
                if len(samples) < 1600:
                    self.reply(400, {"error": "audio too short"})
                    return
                normalized = samples.astype(np.float32) * (1.0 / 32768.0)
                rms = float(np.sqrt(np.mean(normalized ** 2)))
                peak = float(np.max(np.abs(normalized)))
                seconds = len(samples) / 16000.0
                print(f"GigaAM input: {seconds:.2f}s, {len(samples)} samples, rms={rms:.5f}, peak={peak:.5f}", flush=True)
            with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as file:
                path = file.name
                file.write(audio)
            try:
                result = model.transcribe(path)
                text = result.text if hasattr(result, "text") else str(result)
                print("GigaAM recognized:", repr(text[:180]), flush=True)
                if not text.strip() and rms >= 0.006:
                    print("GigaAM warning: empty transcript despite non-silent input", flush=True)
                    # Verify that the same waveform survives FFmpeg/GigaAM's official loader.
                    try:
                        from gigaam.preprocess import load_audio
                        decoded = load_audio(path)
                        decoded_rms = float(torch.sqrt(torch.mean(decoded.float().square())))
                        print(f"GigaAM decoded wav: {decoded.numel()} samples, "
                              f"rms={decoded_rms:.5f}", flush=True)
                        if decoded.numel() < 1600 or decoded_rms < rms * 0.25:
                            print("GigaAM INPUT ERROR: FFmpeg output differs significantly "
                                  "from incoming PCM", flush=True)
                    except Exception as decode_error:
                        print("GigaAM FFmpeg diagnostics failed:", repr(decode_error), flush=True)
            finally:
                os.unlink(path)
            self.reply(200, {"text": text})
        except Exception as exc:
            print("STT error:", repr(exc), flush=True)
            self.reply(500, {"error": str(exc)[:300]})

ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
