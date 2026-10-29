namespace OpenAssistant.Simulator.Audio;

/// <summary>передаёт фразу после активации, сохраняя звук до кодового слова.</summary>
public sealed class VoiceActivityGate
{
    private readonly object _sync = new();
    private readonly Queue<byte[]> _preRoll = new();
    private const int SampleRate = 16000;
    private const int MaxPreRollBytes = SampleRate * 2;
    private int _preRollBytes;
    private int _quietMs;
    private int _totalMs;
    private int _speechMs;
    private double _noiseFloor = 0.003;
    private bool _capturing;

    public event Action<byte[]>? SendAudio;
    public event Action? Finished;
    public event Action? TimedOut;

    public bool Capturing { get { lock (_sync) return _capturing; } }

    /// <summary>принимает pcm16, оценивает речевую активность и границу фразы.</summary>
    public void Push(byte[] pcm)
    {
        if (pcm.Length == 0 || (pcm.Length & 1) != 0) return;
        lock (_sync)
        {
            int duration = pcm.Length * 1000 / (SampleRate * 2);
            if (!_capturing)
            {
                _preRoll.Enqueue(pcm);
                _preRollBytes += pcm.Length;
                while (_preRollBytes > MaxPreRollBytes && _preRoll.TryDequeue(out var old))
                    _preRollBytes -= old.Length;
                return;
            }

            SendAudio?.Invoke(pcm);
            _totalMs += duration;
            double rms = CalculateRms(pcm);
            bool isSpeech = rms > Math.Max(0.012, _noiseFloor * 3);
            if (isSpeech)
            {
                _speechMs += duration;
                _quietMs = 0;
            }
            else
            {
                _noiseFloor = Math.Clamp(_noiseFloor * 0.98 + rms * 0.02, 0.001, 0.018);
                if (_speechMs > 0) _quietMs += duration;
            }

            if ((_speechMs >= 160 && _totalMs >= 650 && _quietMs >= 1100) || _totalMs >= 12000)
            {
                StopCore();
                Finished?.Invoke();
            }
            else if (_totalMs >= 5000 && _speechMs == 0)
            {
                StopCore();
                TimedOut?.Invoke();
            }
        }
    }

    /// <summary>активирует запись и немедленно передаёт накопленную секунду звука.</summary>
    public void Activate()
    {
        lock (_sync)
        {
            if (_capturing) return;
            _capturing = true;
            _quietMs = _totalMs = _speechMs = 0;
            while (_preRoll.TryDequeue(out var chunk)) SendAudio?.Invoke(chunk);
            _preRollBytes = 0;
        }
    }

    /// <summary>останавливает захват и удаляет старые данные перед новой сессией.</summary>
    public void Stop() { lock (_sync) StopCore(); }

    /// <summary>сбрасывает внутренние счётчики; вызывается только под блокировкой.</summary>
    private void StopCore()
    {
        _capturing = false;
        _quietMs = _totalMs = _speechMs = 0;
        _preRoll.Clear();
        _preRollBytes = 0;
    }

    /// <summary>вычисляет среднеквадратичный уровень отсчётов pcm16.</summary>
    private static double CalculateRms(byte[] data)
    {
        double sum = 0;
        int count = data.Length / 2;
        for (int i = 0; i < count; i++)
        {
            short sample = (short)(data[i * 2] | data[i * 2 + 1] << 8);
            double normalized = sample / 32768.0;
            sum += normalized * normalized;
        }
        return Math.Sqrt(sum / count);
    }
}
