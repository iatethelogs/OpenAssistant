using System.Diagnostics;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Audio;

/// <summary>адаптеры локальных whisper.cpp и piper; движки можно менять отдельно от сервера.</summary>
public sealed class SpeechService(IOptions<ServerOptions> settings)
{
    /// <summary>передаёт накопленные PCM16-сэмплы whisper-cli и возвращает распознанный текст.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        string file = Path.Combine(Path.GetTempPath(), $"openassistant-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(file, WaveWriter.Wrap(pcm, settings.Value.InputSampleRate), ct);
        try
        {
            var output = await ExecuteAsync(settings.Value.SttExecutable,
                ["-m", settings.Value.SttModel, "-f", file, "-l", "ru", "-nt"], null, ct);
            return System.Text.Encoding.UTF8.GetString(output).Trim();
        }
        finally { File.Delete(file); }
    }

    /// <summary>синтезирует фразу в PCM16 mono; piper пишет сырые байты сразу в stdout.</summary>
    public Task<byte[]> SynthesizeAsync(string phrase, CancellationToken ct) =>
        ExecuteAsync(settings.Value.TtsExecutable,
            ["--model", settings.Value.TtsModel, "--output-raw"], phrase + "\n", ct);

    /// <summary>запускает внешнюю утилиту без shell и прибивает процесс при отмене.</summary>
    private static async Task<byte[]> ExecuteAsync(string executable, string[] args,
        string? input, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = input is not null
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"не запустился {executable}");
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        Task<string> errors = process.StandardError.ReadToEndAsync(ct);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();
        }
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        await process.WaitForExitAsync(ct);
        string error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable}: {error}");
        return output.ToArray();
    }
}
