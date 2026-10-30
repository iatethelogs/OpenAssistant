using System.Diagnostics;
using System.Text;
using OpenAssistant.Server.Audio;
using OpenAssistant.Server.Chat;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Monitoring;

/// <summary>Реальные замеры HTTP-моделей без сохранения аудио и истории тестов.</summary>
public sealed class DiagnosticService(LlmService llm, SpeechService speech, IOptionsMonitor<ServerOptions> options)
{
    public sealed record LlmTestInput(string Prompt, string? Model);
    public sealed record TtsTestInput(string Text, string? Voice, string? Model);

    public async Task<object> TestLlmAsync(string prompt, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1500)
            throw new ArgumentException("Длина запроса: 1–1500 символов");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var sw = Stopwatch.StartNew();
        long? first = null;
        int segments = 0;
        var answer = new StringBuilder();
        await foreach (string chunk in llm.GenerateAsync([
            new ChatMessage("system", "Отвечай кратко и по-русски. Это тест скорости."),
            new ChatMessage("user", prompt)
        ], timeout.Token, model))
        {
            if (first is null) first = sw.ElapsedMilliseconds;
            answer.Append(chunk);
            segments++;
            if (answer.Length > 16000) break;
        }
        sw.Stop();
        double seconds = Math.Max(0.001, (sw.ElapsedMilliseconds - (first ?? 0)) / 1000.0);
        double estimatedTokens = answer.Length / 4.0;
        return new {
            text = answer.ToString(), firstTokenMs = first, totalMs = sw.ElapsedMilliseconds,
            chunks = segments, estimatedTokens = Math.Round(estimatedTokens, 1),
            estimatedTokensPerSecond = Math.Round(estimatedTokens / seconds, 1),
            note = "Токены/с оценены приблизительно по длине ответа. Время первого фрагмента измерено точно."
        };
    }

    public async Task<(byte[] Wave, long Milliseconds, double DurationSeconds, double Rtf)> TestTtsAsync(
        string text, string? voice, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300)
            throw new ArgumentException("Текст теста: 1–300 символов");
        var cfg = options.CurrentValue;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var sw = Stopwatch.StartNew();
        byte[] pcm = await speech.SynthesizeAsync(text, voice, cfg.OutputSampleRate, timeout.Token, model);
        sw.Stop();
        double duration = pcm.Length / (cfg.OutputSampleRate * 2.0);
        return (WaveWriter.Wrap(pcm, cfg.OutputSampleRate), sw.ElapsedMilliseconds, duration,
            Math.Round(sw.Elapsed.TotalSeconds / Math.Max(0.01, duration), 3));
    }

    public async Task<object> TestSttAsync(byte[] pcm, string? model, CancellationToken ct)
    {
        if (pcm.Length is < 3200 or > 1024 * 1024 || pcm.Length % 2 != 0)
            throw new ArgumentException("Нужна запись PCM16 mono 16 кГц продолжительностью от 0,1 до 32 секунд");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var sw = Stopwatch.StartNew();
        string recognized = await speech.TranscribeAsync(pcm, timeout.Token, model);
        sw.Stop();
        double seconds = pcm.Length / (options.CurrentValue.InputSampleRate * 2.0);
        return new {
            text = recognized, totalMs = sw.ElapsedMilliseconds, audioSeconds = Math.Round(seconds, 2),
            realtimeFactor = Math.Round(sw.Elapsed.TotalSeconds / Math.Max(0.01, seconds), 3)
        };
    }
}
