using NAudio.Wave;

namespace OpenAssistant.Simulator.Audio;

/// <summary>Separate audio outputs for streamed TTS and music (the latter stays stereo).</summary>
public sealed class SpeakerPlayer : IDisposable
{
    private readonly object _musicLock = new();
    private WaveOut? _speechOutput;
    private BufferedWaveProvider? _speechBuffer;
    private int _speechRate;
    private long _speechVersion;
    private WaveOut? _musicOutput;
    private MediaFoundationReader? _mediaReader;
    private long _musicVersion;
    private bool _pausedByAssistant;
    public bool SpeechActive => _speechOutput is not null;
    public bool MusicActive
    {
        get { lock (_musicLock) return _musicOutput?.PlaybackState == PlaybackState.Playing; }
    }

    public void BeginSpeech(int rate)
    {
        if (rate < 8000 || rate > 48000) throw new ArgumentOutOfRangeException(nameof(rate));
        if (_speechOutput is not null && _speechRate == rate) return;
        EndSpeech();
        _speechRate = rate;
        _speechBuffer = new BufferedWaveProvider(
            new WaveFormat(rate, 16, 1), TimeSpan.FromSeconds(12))
        { DiscardOnBufferOverflow = true };
        _speechOutput = new WaveOut { BufferMilliseconds = 60, NumberOfBuffers = 3 };
        _speechOutput.Init(_speechBuffer);
        _speechOutput.Play();
    }

    public void AddSpeech(byte[] pcm)
    {
        if ((pcm.Length & 1) != 0) throw new InvalidDataException("Нечётный PCM16");
        _speechBuffer?.AddSamples(pcm, 0, pcm.Length);
    }

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
                await Task.Delay(220);
                if (version == Volatile.Read(ref _speechVersion)) EndSpeech();
                return;
            }
            await Task.Delay(50);
        }
    }

    public void EndSpeech()
    {
        Interlocked.Increment(ref _speechVersion);
        _speechOutput?.Stop();
        _speechOutput?.Dispose();
        _speechOutput = null;
        _speechBuffer = null;
    }

    public void DuckMusic()
    {
        lock (_musicLock)
        {
            if (_musicOutput?.PlaybackState == PlaybackState.Playing)
            {
                _musicOutput.Pause();
                _pausedByAssistant = true;
            }
        }
    }
    public void ResumeMusic()
    {
        lock (_musicLock)
        {
            if (!_pausedByAssistant) return;
            _pausedByAssistant = false;
            _musicOutput?.Play();
        }
    }

    public async Task<MusicStreamInfo?> PlayUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https"))
            throw new ArgumentException("Music URL must use HTTP or HTTPS");
        long version = Interlocked.Increment(ref _musicVersion);
        var reader = await Task.Run(() => new MediaFoundationReader(url), ct);
        lock (_musicLock)
        {
            if (version != Volatile.Read(ref _musicVersion) || ct.IsCancellationRequested)
            {
                reader.Dispose();
                return null;
            }
            StopMusicCore();
            try
            {
                var output = new WaveOut { BufferMilliseconds = 120 };
                output.Init(reader);
                _mediaReader = reader;
                _musicOutput = output;
                output.Play();
                return new MusicStreamInfo(reader.WaveFormat.SampleRate,
                    reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample);
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

    public void StopMusic()
    {
        Interlocked.Increment(ref _musicVersion);
        lock (_musicLock) StopMusicCore();
    }
    private void StopMusicCore()
    {
        _pausedByAssistant = false;
        _musicOutput?.Stop();
        _musicOutput?.Dispose();
        _musicOutput = null;
        _mediaReader?.Dispose();
        _mediaReader = null;
    }

    public void Dispose()
    {
        EndSpeech();
        StopMusic();
    }
}
public sealed record MusicStreamInfo(int SampleRate, int Channels, int BitsPerSample);
