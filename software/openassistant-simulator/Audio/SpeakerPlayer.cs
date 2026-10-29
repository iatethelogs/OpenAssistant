using NAudio.Wave;
namespace OpenAssistant.Simulator.Audio;
/// <summary>воспроизведение голосовых pcm-фреймов и независимого музыкального url.</summary>
public sealed class SpeakerPlayer : IDisposable
{
    private WaveOutEvent? _speechOut;
    private BufferedWaveProvider? _speechBuffer;
    private int _speechRate;
    private WaveOutEvent? _musicOut;
    private MediaFoundationReader? _media;
    /// <summary>создаёт буфер голоса с частотой, объявленной сервером.</summary>
    public void BeginSpeech(int rate)
    {
        if (_speechOut is not null && _speechRate == rate) return;
        EndSpeech();
        _speechRate = rate;
        _speechBuffer = new BufferedWaveProvider(new WaveFormat(rate, 16, 1)) { BufferDuration = TimeSpan.FromSeconds(15), DiscardOnBufferOverflow = true };
        _speechOut = new WaveOutEvent { DesiredLatency = 120 };
        _speechOut.Init(_speechBuffer);
        _speechOut.Play();
    }
    /// <summary>добавляет полученный PCM непосредственно в аудиобуфер.</summary>
    public void AddSpeech(byte[] bytes) => _speechBuffer?.AddSamples(bytes, 0, bytes.Length);
    /// <summary>закрывает голосовой выход и сбрасывает несыгранные фреймы.</summary>
    public void EndSpeech() { _speechOut?.Stop(); _speechOut?.Dispose(); _speechOut = null; _speechBuffer = null; }
    /// <summary>самостоятельно открывает ссылку на радио или музыку, без проксирования сервером.</summary>
    public void PlayUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("неверный адрес медиа");
        StopMusic();
        _media = new MediaFoundationReader(url);
        _musicOut = new WaveOutEvent();
        _musicOut.Init(_media); _musicOut.Play();
    }
    /// <summary>останавливает музыку, освобождает декодер и динамик.</summary>
    public void StopMusic() { _musicOut?.Stop();_musicOut?.Dispose();_musicOut=null;_media?.Dispose();_media=null; }
    /// <summary>освобождает все аудиоресурсы.</summary>
    public void Dispose(){EndSpeech();StopMusic();}
}