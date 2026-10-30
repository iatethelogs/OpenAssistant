"""Compatibility implementation of torchaudio.transforms.MelSpectrogram for GigaAM.

Only installed when torchaudio cannot be imported.  The submodules and buffers
match torchaudio's checkpoint state_dict layout exactly:
  spectrogram.window
  mel_scale.fb
No PyTorch, driver or CUDA package is replaced.
"""
import sys
import types


def ensure_torchaudio():
    try:
        import torchaudio
        return False
    except (ImportError, OSError) as exc:
        print("GigaAM: torchaudio unavailable; using torch STFT/MelScale:", exc, flush=True)

    import torch
    from torch import nn

    class Spectrogram(nn.Module):
        def __init__(self, n_fft, win_length, hop_length, pad, power,
                     normalized, center, pad_mode, window_fn=torch.hann_window):
            super().__init__()
            self.n_fft, self.win_length, self.hop_length = n_fft, win_length, hop_length
            self.pad, self.power = pad, power
            self.normalized, self.center, self.pad_mode = normalized, center, pad_mode
            self.register_buffer("window", window_fn(win_length))

        def forward(self, waveform):
            if self.pad:
                waveform = torch.nn.functional.pad(waveform, (self.pad, self.pad))
            complex_spec = torch.stft(
                waveform, n_fft=self.n_fft, hop_length=self.hop_length,
                win_length=self.win_length,
                window=self.window.to(waveform.device, waveform.dtype),
                center=self.center, pad_mode=self.pad_mode,
                normalized=self.normalized, return_complex=True)
            magnitude = complex_spec.abs()
            return magnitude if self.power is None else magnitude.pow(self.power)

    class MelScale(nn.Module):
        def __init__(self, n_mels, sample_rate, n_stft, f_min, f_max, norm, mel_scale):
            super().__init__()
            if mel_scale not in ("htk", "slaney"):
                raise ValueError("Unknown mel_scale: " + str(mel_scale))
            if norm not in (None, "slaney"):
                raise ValueError("Unknown mel norm: " + str(norm))
            max_freq = sample_rate / 2 if f_max is None else f_max
            def hz_to_mel(hz):
                if mel_scale == "htk":
                    return 2595.0 * torch.log10(1.0 + torch.as_tensor(hz) / 700.0)
                hz = torch.as_tensor(hz)
                # torchaudio Slaney conversion
                linear = hz / (200.0 / 3.0)
                log = 15.0 + torch.log(torch.clamp(hz / 1000.0, min=1e-12)) / 0.06875177742094912
                return torch.where(hz >= 1000, log, linear)
            def mel_to_hz(mel):
                if mel_scale == "htk":
                    return 700.0 * (torch.pow(10.0, mel / 2595.0) - 1.0)
                linear = mel * (200.0 / 3.0)
                log = 1000.0 * torch.exp(0.06875177742094912 * (mel - 15.0))
                return torch.where(mel >= 15.0, log, linear)
            mel_points = torch.linspace(hz_to_mel(float(f_min)), hz_to_mel(float(max_freq)), n_mels + 2)
            freq_points = mel_to_hz(mel_points)
            all_freqs = torch.linspace(0, sample_rate // 2, n_stft)
            diff = freq_points[1:] - freq_points[:-1]
            slopes = freq_points.unsqueeze(0) - all_freqs.unsqueeze(1)
            lower = -slopes[:, :-2] / diff[:-1]
            upper = slopes[:, 2:] / diff[1:]
            fb = torch.maximum(torch.zeros_like(lower), torch.minimum(lower, upper))
            if norm == "slaney":
                fb = fb * (2.0 / (freq_points[2:] - freq_points[:-2])).unsqueeze(0)
            # torchaudio shape is [n_stft, n_mels], not [n_mels, n_stft]
            self.register_buffer("fb", fb)

        def forward(self, specgram):
            return torch.matmul(specgram.transpose(-1, -2),
                                self.fb.to(specgram.device, specgram.dtype)).transpose(-1, -2)

    class MelSpectrogram(nn.Module):
        def __init__(self, sample_rate=16000, n_fft=400, win_length=None,
                     hop_length=None, f_min=0.0, f_max=None, pad=0, n_mels=128,
                     window_fn=torch.hann_window, power=2.0, normalized=False,
                     center=True, pad_mode="reflect", norm=None, mel_scale="htk", **kwargs):
            super().__init__()
            win_length = n_fft if win_length is None else win_length
            hop_length = win_length // 2 if hop_length is None else hop_length
            self.spectrogram = Spectrogram(n_fft, win_length, hop_length, pad, power,
                                           normalized, center, pad_mode, window_fn)
            self.mel_scale = MelScale(n_mels, sample_rate, n_fft // 2 + 1, f_min,
                                      f_max, norm, mel_scale)

        def forward(self, waveform):
            return self.mel_scale(self.spectrogram(waveform))

    stub = types.ModuleType("torchaudio")
    transforms = types.ModuleType("torchaudio.transforms")
    transforms.MelSpectrogram = MelSpectrogram
    stub.transforms = transforms
    sys.modules["torchaudio"] = stub
    sys.modules["torchaudio.transforms"] = transforms
    return True
