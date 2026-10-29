using NAudio.Wave;
namespace OpenAssistant.Simulator.Audio;
/// <summary>микрофон windows: дискретизация 16 кгц, моно, pcm16.</summary>
public sealed class MicrophoneCapture : IDisposable
{
    private WaveInEvent? _mic;
    public event Action<byte[]>? Data;
    /// <summary>включает захват звука пачками по 40 мс.</summary>
    public void Start()
    {
        if (_mic is not null) return;
        _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 40 };
        _mic.DataAvailable += (_, e) => Data?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
        _mic.StartRecording();
    }
    /// <summary>останавливает микрофон, освобождая аудиоустройство.</summary>
    public void Stop() { if (_mic is null) return; _mic.StopRecording(); _mic.Dispose(); _mic = null; }
    /// <summary>закрывает захват при завершении приложения.</summary>
    public void Dispose() => Stop();
}