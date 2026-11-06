using NAudio.Wave;

namespace OpenAssistant.Simulator.Audio;

/// <summary>Windows WASAPI communications capture (AEC/NS/AGC when provided by the endpoint).
/// Falls back to plain WaveIn if communications processing is unavailable.</summary>
public sealed class MicrophoneCapture : IDisposable
{
    private WasapiRecorder? _recorder;
    private WaveIn? _fallback;
    public event Action<byte[]>? Data;
    public string ProcessingMode { get; private set; } = "Остановлен";
    public bool CommunicationsMode { get; private set; }

    public void Start(bool requestWindowsProcessing = true)
    {
        Stop();
        try
        {
            var builder = new WasapiRecorderBuilder()
                .WithFormat(new WaveFormat(16000, 16, 1))
                .WithBufferLength(40);
            if (requestWindowsProcessing) builder.WithCommunicationsMode();
            var recorder = builder.Build();
            recorder.DataAvailable += (samples, _, _, _) =>
            {
                if (!samples.IsEmpty) Data?.Invoke(samples.ToArray());
            };
            try { recorder.StartRecording(); }
            catch { recorder.Dispose(); throw; }
            _recorder = recorder;
            CommunicationsMode = requestWindowsProcessing;
            ProcessingMode = requestWindowsProcessing
                ? "Windows Communications DSP (AEC / NS зависят от драйвера)"
                : "WASAPI без системной обработки";
        }
        catch (Exception)
        {
            // Degrade gracefully instead of disabling the assistant entirely.
            var fallback = new WaveIn {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 40
            };
            fallback.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0) Data?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
            };
            try { fallback.StartRecording(); }
            catch { fallback.Dispose(); throw; }
            _fallback = fallback;
            CommunicationsMode = false;
            ProcessingMode = "WaveIn · программный шумофильтр, без системного AEC";
        }
    }

    public void Stop()
    {
        _recorder?.StopRecording();
        _recorder?.Dispose();
        _recorder = null;
        _fallback?.StopRecording();
        _fallback?.Dispose();
        _fallback = null;
        CommunicationsMode = false;
        ProcessingMode = "Остановлен";
    }
    public void Dispose() => Stop();
}
