using NAudio.Wave;

namespace OpenAssistant.Simulator.Audio;

/// <summary>воспроизведение голоса и прямых музыкальных URL на стороне колонки.</summary>
public sealed class SpeakerPlayer : IDisposable
{
    private readonly object _musicLock = new();
    private WaveOutEvent? _speechOutput;
    private BufferedWaveProvider? _speechBuffer;
    private int _speechRate;
    private long _speechVersion;
    private WaveOutEvent? _musicOutput;
    private MediaFoundationReader? _mediaReader;
    private long _musicVersion;

    /// <summary>создаёт голосовой PCM-буфер либо продолжает воспроизведение следующей фразы.</summary>
    public void BeginSpeech(int rate)
    {
        if (rate < 8000 || rate > 48000) throw new ArgumentOutOfRangeException(nameof(rate));
        if (_speechOutput is not null && _speechRate == rate) return;
        EndSpeech();
        _speechRate = rate;
        _speechBuffer = new BufferedWaveProvider(new WaveFormat(rate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(12),
            DiscardOnBufferOverflow = true
        };
        _speechOutput = new WaveOutEvent { DesiredLatency = 120 };
        _speechOutput.Init(_speechBuffer);
        _speechOutput.Play();
    }

    /// <summary>пополняет буфер голосового ответа сырыми PCM16 отсчётами.</summary>
    public void AddSpeech(byte[] pcm)
    {
        if ((pcm.Length & 1) != 0) throw new InvalidDataException("нечётное число байт PCM16");
        _speechBuffer?.AddSamples(pcm, 0, pcm.Length);
    }

    /// <summary>ждёт окончания буфера после done, не обрезая последние слова.</summary>
    public async Task FinishSpeechAsync()
    {
        var buffer = _speechBuffer;
        long version = Volatile.Read(ref _speechVersion);
        if (buffer is null) return;
        for (int i = 0; i < 240; i++)
        {
            if (version != Volatile.Read(ref _speechVersion)) return;
            if (buffer.BufferedBytes == 0)
            {
                await Task.Delay(180);
                if (version == Volatile.Read(ref _speechVersion)) EndSpeech();
                return;
            }
            await Task.Delay(50);
        }
    }

    /// <summary>сбрасывает голос, в том числе при новом wake-word или команде cancel.</summary>
    public void EndSpeech()
    {
        Interlocked.Increment(ref _speechVersion);
        _speechOutput?.Stop();
        _speechOutput?.Dispose();
        _speechOutput = null;
        _speechBuffer = null;
    }

    /// <summary>открывает музыкальный URL в фоновом потоке, чтобы интерфейс не зависал на сети.</summary>
    public async Task<bool> PlayUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https"))
            throw new ArgumentException("музыкальный URL должен использовать HTTP или HTTPS");
        long version = Interlocked.Increment(ref _musicVersion);
        // MediaFoundationReader может долго ожидать ответа станции: не блокируем WPF.
        var reader = await Task.Run(() => new MediaFoundationReader(url), ct);
        lock (_musicLock)
        {
            if (version != Volatile.Read(ref _musicVersion) || ct.IsCancellationRequested)
            {
                reader.Dispose();
                return false;
            }
            StopMusicCore();
            try
            {
                var output = new WaveOutEvent();
                output.Init(reader);
                _mediaReader = reader;
                _musicOutput = output;
                output.Play();
                return true;
            }
            catch
            {
                reader.Dispose();
                _mediaReader = null;
                _musicOutput?.Dispose();
                _musicOutput = null;
                throw;
            }
        }
    }

    /// <summary>останавливает только музыку, отменяя также незавершённое открытие ссылки.</summary>
    public void StopMusic()
    {
        Interlocked.Increment(ref _musicVersion);
        lock (_musicLock) StopMusicCore();
    }

    /// <summary>освобождает текущий музыкальный декодер под блокировкой.</summary>
    private void StopMusicCore()
    {
        _musicOutput?.Stop();
        _musicOutput?.Dispose();
        _musicOutput = null;
        _mediaReader?.Dispose();
        _mediaReader = null;
    }

    /// <summary>завершает все аудиоканалы.</summary>
    public void Dispose()
    {
        EndSpeech();
        StopMusic();
    }
}
