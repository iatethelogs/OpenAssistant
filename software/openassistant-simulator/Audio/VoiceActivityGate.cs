namespace OpenAssistant.Simulator.Audio;

/// <summary>простой детектор окончания фразы: предварительный буфер и измерение уровня речи.</summary>
public sealed class VoiceActivityGate
{
    private readonly Queue<byte[]> _preRoll = new();
    private int _preRollBytes;
    private readonly int _maximumPreRollBytes = 16000 * 2; // одна секунда до слова активации
    private int _quietMs;
    private int _totalMs;
    private bool _heardSpeech;
    public bool Capturing { get; private set; }
    public event Action<byte[]>? SendAudio;
    public event Action? Finished;

    /// <summary>сохраняет кусочек до активации и следит за паузой после произнесённой команды.</summary>
    public void Push(byte[] pcm)
    {
        if (!Capturing)
        {
            _preRoll.Enqueue(pcm);
            _preRollBytes += pcm.Length;
            while (_preRollBytes > _maximumPreRollBytes && _preRoll.TryDequeue(out var old)) _preRollBytes -= old.Length;
            return;
        }
        SendAudio?.Invoke(pcm);
        double rms = CalculateRms(pcm);
        int durationMs = pcm.Length * 1000 / (16000 * 2);
        _totalMs += durationMs;
        if (rms > 0.018) { _heardSpeech = true; _quietMs = 0; }
        else if (_heardSpeech) _quietMs += durationMs;
        // пауза 1,1 секунды после речи; 12 секунд ограничивают запись, даже если рядом шумно.
        if ((_heardSpeech && _totalMs >= 700 && _quietMs >= 1100) || _totalMs >= 12000)
        {
            Capturing = false;
            _heardSpeech = false;
            _quietMs = 0;
            Finished?.Invoke();
        }
    }
    /// <summary>после wake-word отправляет аудио из буфера вместе с последующей речью.</summary>
    public void Activate()
    {
        if (Capturing) return;
        Capturing = true; _totalMs = 0; _quietMs = 0; _heardSpeech = false;
        while (_preRoll.TryDequeue(out var chunk)) SendAudio?.Invoke(chunk);
        _preRollBytes = 0;
    }
    /// <summary>ручная запись использует ту же схему определения конца фразы.</summary>
    public void Stop() { Capturing = false; _heardSpeech = false; _quietMs = 0; _totalMs = 0; }
    /// <summary>вычисляет rms-нормировку уровня pcm16 без дополнительных библиотек.</summary>
    private static double CalculateRms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0; int count = pcm.Length / 2;
        for (int i = 0; i < count; i++) { short s = (short)(pcm[i*2] | (pcm[i*2+1] << 8)); double v=s/32768.0; sum+=v*v; }
        return Math.Sqrt(sum/count);
    }
}
