using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using OpenAssistant.Server.Audio;
using OpenAssistant.Server.Chat;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Monitoring;

/// <summary>Реальные замеры HTTP-моделей без сохранения аудио и истории тестов.</summary>
public sealed class DiagnosticService(IHttpClientFactory http, SpeechService speech, IOptionsMonitor<ServerOptions> options)
{
    public sealed record LlmTestInput(string Prompt, string? Model);
    public sealed record TtsTestInput(string Text, string? Voice, string? Model);

    public async Task<object> TestLlmAsync(string prompt, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 1500)
            throw new ArgumentException("Длина запроса: 1–1500 символов");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var cfg = options.CurrentValue;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions");
        request.Content = JsonContent.Create(new {
            model = string.IsNullOrWhiteSpace(model) ? cfg.LlmModel : model,
            stream = true,
            stream_options = new { include_usage = true },
            temperature = cfg.LlmTemperature,
            top_p = cfg.LlmTopP, top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
            max_tokens = cfg.LlmMaxTokens,
            messages = new object[] {
                new { role = "system", content = "Отвечай кратко и по-русски. Это тест скорости." },
                new { role = "user", content = prompt }
            }
        });
        var sw = Stopwatch.StartNew();
        using var response = await http.CreateClient("llm").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        long? first = null;
        int fragments = 0;
        int? exactTokens = null;
        var answer = new StringBuilder();
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string payload = line[6..];
            if (payload == "[DONE]") break;
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("completion_tokens", out var count) &&
                count.TryGetInt32(out int value))
                exactTokens = value;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object ||
                !delta.TryGetProperty("content", out var text) || text.ValueKind != JsonValueKind.String)
                continue;
            string part = text.GetString() ?? "";
            if (part.Length == 0) continue;
            first ??= sw.ElapsedMilliseconds;
            fragments++;
            if (answer.Length < 16000) answer.Append(part);
        }
        sw.Stop();
        double seconds = Math.Max(0.001, (sw.ElapsedMilliseconds - (first ?? 0)) / 1000.0);
        double countTokens = exactTokens ?? (answer.Length / 4.0);
        double speed = Math.Round(countTokens / seconds, 1);
        return new {
            text = answer.ToString(), firstTokenMs = first, totalMs = sw.ElapsedMilliseconds,
            chunks = fragments, completionTokens = exactTokens,
            tokensExact = exactTokens is not null,
            tokensPerSecond = speed, estimatedTokensPerSecond = speed,
            note = exactTokens is not null
                ? "Точное число токенов получено из usage.completion_tokens."
                : "Сервер не вернул usage. Скорость приблизительная (оценка по символам)."
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
