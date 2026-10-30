"""Torch-native mel spectrogram compatibility layer for GigaAM.

Used only when torchaudio is absent or cannot load against the installed PyTorch.
Does not replace PyTorch, CUDA libraries or any existing working torchaudio build.
"""
import sys
import types

def ensure_torchaudio():
    try:
        import torchaudio
        return False
    except (ImportError, OSError) as error:
        print("TORCHAUDIO_FALLBACK: " + str(error), flush=True)

    import torch

    class MelSpectrogram(torch.nn.Module):
        """Equivalent to torchaudio's default power=2, HTK mel spectrogram."""
        def __init__(self, sample_rate=16000, n_fft=400, win_length=None,
                     hop_length=None, n_mels=128, center=True, f_min=0.0,
                     f_max=None, pad=0, pad_mode="reflect", power=2.0,
                     normalized=False, mel_scale="htk", **kwargs):
            super().__init__()
            if mel_scale != "htk":
                raise ValueError("Only HTK mel scale is supported")
            self.n_fft = int(n_fft)
            self.win_length = int(win_length or n_fft)
            self.hop_length = int(hop_length or (self.win_length // 2))
            self.center = bool(center)
            self.pad_mode = pad_mode
            self.pad = int(pad)
            self.power = power
            self.normalized = bool(normalized)
            self.register_buffer("window", torch.hann_window(self.win_length))
            high = float(f_max or sample_rate / 2)
            hz = torch.linspace(0, sample_rate / 2, n_fft // 2 + 1)
            mel_min = 2595.0 * torch.log10(1.0 + torch.tensor(float(f_min)) / 700.0)
            mel_max = 2595.0 * torch.log10(1.0 + torch.tensor(high) / 700.0)
            mel_points = torch.linspace(mel_min, mel_max, n_mels + 2)
            freq_points = 700.0 * (torch.pow(10.0, mel_points / 2595.0) - 1.0)
            low, mid, upper = freq_points[:-2, None], freq_points[1:-1, None], freq_points[2:, None]
            rising = (hz[None, :] - low) / (mid - low).clamp_min(1e-6)
            falling = (upper - hz[None, :]) / (upper - mid).clamp_min(1e-6)
            fb = torch.maximum(torch.zeros_like(rising), torch.minimum(rising, falling))
            self.register_buffer("fb", fb)

        def forward(self, audio):
            if self.pad:
                audio = torch.nn.functional.pad(audio, (self.pad, self.pad))
            spectrum = torch.stft(audio, n_fft=self.n_fft,
                                  hop_length=self.hop_length,
                                  win_length=self.win_length,
                                  window=self.window.to(device=audio.device, dtype=audio.dtype),
                                  center=self.center, pad_mode=self.pad_mode,
                                  normalized=self.normalized, return_complex=True)
            magnitudes = spectrum.abs()
            if self.power is not None:
                magnitudes = magnitudes.pow(self.power)
            return torch.matmul(self.fb.to(device=audio.device, dtype=magnitudes.dtype), magnitudes)

    fake = types.ModuleType("torchaudio")
    transforms = types.ModuleType("torchaudio.transforms")
    transforms.MelSpectrogram = MelSpectrogram
    fake.transforms = transforms
    sys.modules["torchaudio"] = fake
    sys.modules["torchaudio.transforms"] = transforms
    print("TORCHAUDIO_FALLBACK: GigaAM mel extraction uses installed torch", flush=True)
    return True
