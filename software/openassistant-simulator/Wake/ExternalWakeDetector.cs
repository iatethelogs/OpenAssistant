using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenAssistant.Simulator.Wake;

/// <summary>локальный tflite-детектор получает pcm16 и выдаёт jsonl-события активации.</summary>
public sealed class ExternalWakeDetector : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private CancellationTokenSource? _stop;
    private Channel<byte[]>? _audio;
    private Task[]? _tasks;
    private int _drops;

    public event Action<string, double>? Detected;
    public event Action<string>? Diagnostic;

    /// <summary>запускает детектор со стандартным вводом для звука и jsonl на stdout.</summary>
    public void Start(string executable, string arguments)
    {
        Stop();
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new ArgumentException("укажите полный путь к существующему exe детектора");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in SplitArguments(arguments)) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("детектор не запустился");
        var stop = new CancellationTokenSource();
        var audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        lock (_sync)
        {
            _process = process;
            _stop = stop;
            _audio = audio;
            _drops = 0;
        }
        var tasks = new[]
        {
            WriteAudioAsync(process, audio.Reader, stop.Token),
            ReadEventsAsync(process, stop.Token),
            ReadErrorsAsync(process, stop.Token)
        };
        lock (_sync) _tasks = tasks;
    }

    /// <summary>помещает PCM в ограниченную очередь; никогда не блокирует микрофон.</summary>
    public void Feed(byte[] pcm)
    {
        Channel<byte[]>? audio;
        lock (_sync) audio = _audio;
        if (audio is null) return;
        if (!audio.Writer.TryWrite(pcm) && Interlocked.Increment(ref _drops) % 20 == 1)
            Diagnostic?.Invoke("детектор не успевает обрабатывать звук; часть кадров пропущена");
    }

    /// <summary>подаёт аудио в stdin одним писателем, сохраняя хронологический порядок.</summary>
    private async Task WriteAudioAsync(Process process, ChannelReader<byte[]> audio, CancellationToken ct)
    {
        try
        {
            await foreach (var pcm in audio.ReadAllAsync(ct))
                await process.StandardInput.BaseStream.WriteAsync(pcm, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested) Diagnostic?.Invoke("запись в детектор: " + ex.Message);
        }
    }

    /// <summary>читает события детектора, проверяет уверенность и параметры сообщения.</summary>
    private async Task ReadEventsAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var kind) && kind.GetString() == "wake")
                    {
                        string word = root.TryGetProperty("word", out var w) ? w.GetString() ?? "" : "";
                        double confidence = root.TryGetProperty("score", out var s) ? s.GetDouble() : 0;
                        if (!string.IsNullOrWhiteSpace(word) && double.IsFinite(confidence) && confidence is >= 0 and <= 1)
                            Detected?.Invoke(word, confidence);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                { Diagnostic?.Invoke("некорректное событие детектора: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) Diagnostic?.Invoke("чтение детектора: " + ex.Message); }
    }

    /// <summary>считывает диагностические сообщения процесса без блокировки его stderr.</summary>
    private async Task ReadErrorsAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
                Diagnostic?.Invoke(line);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) Diagnostic?.Invoke(ex.Message); }
    }

    /// <summary>закрывает старый процесс и очередь без воздействия на следующий запуск.</summary>
    public void Stop()
    {
        Process? process;
        CancellationTokenSource? stop;
        Channel<byte[]>? channel;
        Task[]? tasks;
        lock (_sync)
        {
            process = _process; _process = null;
            stop = _stop; _stop = null;
            channel = _audio; _audio = null;
            tasks = _tasks; _tasks = null;
        }
        channel?.Writer.TryComplete();
        stop?.Cancel();
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
        // освобождаем источник отмены, когда фоновые читатели действительно закончили.
        if (stop is not null)
        {
            if (tasks is null) stop.Dispose();
            else _ = Task.WhenAll(tasks).ContinueWith(t =>
            {
                _ = t.Exception;
                stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>разбирает пути и аргументы в кавычках без запуска командной оболочки.</summary>
    private static IEnumerable<string> SplitArguments(string input)
    {
        var part = new StringBuilder();
        bool quoted = false;
        foreach (char ch in input)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (part.Length != 0) { yield return part.ToString(); part.Clear(); }
            }
            else part.Append(ch);
        }
        if (quoted) throw new ArgumentException("незакрытая кавычка в параметрах детектора");
        if (part.Length > 0) yield return part.ToString();
    }

    /// <summary>освобождает ресурсы детектора.</summary>
    public void Dispose() => Stop();
}
