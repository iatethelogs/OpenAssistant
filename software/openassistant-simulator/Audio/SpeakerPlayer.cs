using NAudio.Wave;

namespace OpenAssistant.Simulator.Audio;

/// <summary>Separate audio outputs for streamed TTS and music (the latter stays stereo).</summary>
public sealed class SpeakerPlayer : IDisposable
{
    private readonly object _musicLock = new();
    private static readonly HttpClient MusicClient = new() { Timeout = TimeSpan.FromSeconds(40) };
    private string? _musicTempPath;
    private WaveOut? _speechOutput;
    private BufferedWaveProvider? _speechBuffer;
    private int _speechRate;
    private long _speechVersion;
    private WaveOut? _musicOutput;
    private MediaFoundationReader? _mediaReader;
    private long _musicVersion;
    private bool _duckedByAssistant;
    private bool _pausedByUser;
    public event Action? MusicEnded;
    public event Action<Exception>? MusicFailed;
    public bool SpeechActive => _speechOutput is not null;
    public bool MusicActive
    {
        get { lock (_musicLock) return _musicOutput?.PlaybackState == PlaybackState.Playing; }
    }
    public bool MusicPresent
    {
        get { lock (_musicLock) return _musicOutput is not null; }
    }

    public void BeginSpeech(int rate)
    {
        if (rate < 8000 || rate > 48000) throw new ArgumentOutOfRangeException(nameof(rate));
        if (_speechOutput is not null && _speechRate == rate) return;
        EndSpeech();
        _speechRate = rate;
        _speechBuffer = new BufferedWaveProvider(
            new WaveFormat(rate, 16, 1), TimeSpan.FromMinutes(10))
        { DiscardOnBufferOverflow = false };
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
        // The server can deliver a long answer much faster than real-time.
        // Never time out after twelve seconds and truncate its buffered tail.
        while (version == Volatile.Read(ref _speechVersion))
        {
            if (buffer.BufferedBytes == 0)
            {
                await Task.Delay(220);
                if (version == Volatile.Read(ref _speechVersion) && buffer.BufferedBytes == 0)
                    EndSpeech();
                return;
            }
            await Task.Delay(100);
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

    /// <summary>Duck background music without losing playback position or pausing its decoder.</summary>
    public void DuckMusic()
    {
        lock (_musicLock)
        {
            if (_musicOutput is null) return;
            _musicOutput.Volume = 0.18f;
            _duckedByAssistant = true;
        }
    }

    public void ResumeMusic()
    {
        lock (_musicLock)
        {
            if (!_duckedByAssistant) return;
            _duckedByAssistant = false;
            if (_musicOutput is not null) _musicOutput.Volume = 1.0f;
        }
    }

    public void PauseMusic()
    {
        lock (_musicLock)
        {
            if (_musicOutput?.PlaybackState != PlaybackState.Playing) return;
            _pausedByUser = true;
            _musicOutput.Pause();
        }
    }

    public void ResumeMusicPlayback()
    {
        lock (_musicLock)
        {
            if (_musicOutput is null || !_pausedByUser) return;
            _pausedByUser = false;
            _musicOutput.Play();
        }
    }

    private static async Task<(MediaFoundationReader Reader, string? TempPath)> OpenMusicAsync(
        string url, string? codec, CancellationToken ct)
    {
        try
        {
            // Fast path: stream directly through Windows Media Foundation.
            return (await Task.Run(() => new MediaFoundationReader(url), ct), null);
        }
        catch (Exception directError) when (!ct.IsCancellationRequested &&
            directError is not OperationCanceledException)
        {
            // Media Foundation does not always infer codecs from Yandex/CDN
            // URLs without a filename extension. Its local file source is
            // substantially more reliable for these streams.
            string extension = codec?.ToLowerInvariant() switch {
                "aac" => ".aac",
                "aac-mp4" => ".m4a",
                _ => ".mp3"
            };
            string path = Path.Combine(Path.GetTempPath(),
                "openassistant-audio-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                using var response = await MusicClient.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                const long maxBytes = 40L * 1024 * 1024;
                if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                    throw new InvalidDataException("Аудиофайл превышает 40 МБ");
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(path, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536];
                    long bytes = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        bytes += count;
                        if (bytes > maxBytes)
                            throw new InvalidDataException("Аудиопоток превысил 40 МБ");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (bytes < 1024) throw new InvalidDataException("Пустой аудиопоток");
                    await output.FlushAsync(ct);
                }
                return (await Task.Run(() => new MediaFoundationReader(path), ct), path);
            }
            catch (Exception fallbackError)
            {
                try { File.Delete(path); } catch (IOException) { }
                throw new IOException(
                    "Не удалось открыть поток через Media Foundation и резервное скачивание. " +
                    "Прямой поток: " + directError.Message + ". Локальный декодер: " +
                    fallbackError.Message, fallbackError);
            }
        }
    }

    public async Task<MusicStreamInfo?> PlayUrlAsync(
        string url, string? codec = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) ||
            address.Scheme is not ("http" or "https"))
            throw new ArgumentException("Music URL must use HTTP or HTTPS");
        long version = Interlocked.Increment(ref _musicVersion);
        var (reader, temporaryFile) = await OpenMusicAsync(url, codec, ct);
        lock (_musicLock)
        {
            if (version != Volatile.Read(ref _musicVersion) || ct.IsCancellationRequested)
            {
                reader.Dispose();
                if (temporaryFile is not null) TryDelete(temporaryFile);
                return null;
            }
            StopMusicCore();
            try
            {
                var output = new WaveOut { BufferMilliseconds = 120 };
                output.Init(reader);
                // Completion of a manually stopped/replaced track is not a
                // real media_ended event and must not advance the server queue.
                output.PlaybackStopped += (_, e) =>
                {
                    if (version != Volatile.Read(ref _musicVersion)) return;
                    if (e.Exception is not null) MusicFailed?.Invoke(e.Exception);
                    else MusicEnded?.Invoke();
                };
                _mediaReader = reader;
                _musicTempPath = temporaryFile;
                _musicOutput = output;
                output.Play();
                return new MusicStreamInfo(reader.WaveFormat.SampleRate,
                    reader.WaveFormat.Channels, reader.WaveFormat.BitsPerSample);
            }
            catch
            {
                reader.Dispose();
                if (temporaryFile is not null) TryDelete(temporaryFile);
                _musicTempPath = null;
                _mediaReader = null;
                _musicOutput?.Dispose();
                _musicOutput = null;
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void StopMusic()
    {
        Interlocked.Increment(ref _musicVersion);
        lock (_musicLock) StopMusicCore();
    }
    private void StopMusicCore()
    {
        _duckedByAssistant = false;
        _pausedByUser = false;
        _musicOutput?.Stop();
        _musicOutput?.Dispose();
        _musicOutput = null;
        _mediaReader?.Dispose();
        _mediaReader = null;
        if (_musicTempPath is not null)
        {
            TryDelete(_musicTempPath);
            _musicTempPath = null;
        }
    }

    public void Dispose()
    {
        EndSpeech();
        StopMusic();
    }
}
public sealed record MusicStreamInfo(int SampleRate, int Channels, int BitsPerSample);
