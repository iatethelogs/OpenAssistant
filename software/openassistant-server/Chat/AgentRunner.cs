using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

public sealed record AgentDelta(string? Text = null, string? Tool = null,
    object? Result = null, object? Media = null);

/// <summary>
/// Explicit commands are routed through fixed rules and actual service
/// catalogs. Unmatched speech is ordinary conversation; no separate LLM
/// classifier or native tool_choice is used.
/// </summary>
public sealed class AgentRunner(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> options,
    AgentToolService skills, RuleBasedCommandRouter router,
    ILogger<AgentRunner> logger)
{
    private static List<object> NormalizeHistory(IReadOnlyList<ChatMessage> history)
    {
        var instructions = history.Where(x => x.Role == "system" &&
            !string.IsNullOrWhiteSpace(x.Content)).Select(x => x.Content.Trim()).ToArray();
        var normalized = new List<ChatMessage>();
        foreach (var entry in history)
        {
            if (entry.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(entry.Content))
                continue;
            if (normalized.Count == 0 && entry.Role != "user") continue;
            if (normalized.Count > 0 && normalized[^1].Role == entry.Role)
                normalized[^1] = normalized[^1] with {
                    Content = normalized[^1].Content + "\n\n" + entry.Content };
            else normalized.Add(entry);
        }
        if (normalized.Count == 0 || normalized[^1].Role != "user")
            throw new ArgumentException("История должна заканчиваться запросом пользователя");
        string instruction = string.Join("\n\n", instructions.Append(NoEmoji.Instruction).Append(
            "Ты ведёшь обычный разговор. В этом вызове никакие внешние действия " +
            "не выполняются. Не утверждай, что включил музыку, радио или изменил " +
            "состояние устройства. Если просят сделать это, а навык недоступен, " +
            "сообщи, что действие не выполнено. Не спрашивай музыкальный сервис по " +
            "умолчанию: при подключении используется Яндекс Музыка."));
        var result = new List<object> { new { role = "system", content = instruction } };
        result.AddRange(normalized.Select(x => (object)new { role = x.Role, content = x.Content }));
        return result;
    }

    public async IAsyncEnumerable<AgentDelta> GenerateAsync(
        IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
    {
        // Tool routing is deterministic and reads only the LAST user
        // utterance, never previous assistant messages, example prompts, or
        // generated JSON. "Привет" must go directly to conversation.
        string utterance = history.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        RuleBasedCommandRouter.Decision? decision = null;
        string? routeError = null;
        try { decision = await router.RouteAsync(utterance, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or InvalidOperationException or
                ArgumentException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Voice command routing failed");
            routeError = "Не удалось обработать команду: " + ex.Message;
        }

        if (routeError is not null)
        {
            yield return new AgentDelta(Text: routeError);
            yield break;
        }
        if (decision is not null)
        {
            if (decision.Error is { Length: > 0 } failure)
            {
                yield return new AgentDelta(Text: failure);
                yield break;
            }

            object result;
            object? media = null;
            string name = decision.Name;
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(
                    name.StartsWith("music_", StringComparison.Ordinal) ? 40 : 15));
                var executed = await skills.ExecuteAsync(name, decision.Arguments, limit.Token);
                result = executed.Result;
                media = executed.Media;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is ArgumentException or InvalidOperationException or
                    HttpRequestException or JsonException or InvalidDataException or
                    NotSupportedException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Command {Name} failed", name);
                result = new { error = ex.Message };
            }

            yield return new AgentDelta(Tool: name, Result: result, Media: media);
            yield break;
        }

        // Conversation is independent of tools. This second model call cannot
        // generate function calls or claim their results as real events.
        var messages = NormalizeHistory(history);
        var cfg = options.CurrentValue;
        var client = clients.CreateClient("llm");
        string endpoint = cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new {
                model = cfg.LlmModel, stream = true, messages,
                temperature = cfg.LlmTemperature, top_p = cfg.LlmTopP,
                top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                max_tokens = cfg.LlmMaxTokens })
        };
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            string httpDetail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"LLM HTTP {(int)response.StatusCode}: {httpDetail[..Math.Min(httpDetail.Length, 300)]}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var strip = new NoEmojiStream();
        bool emitted = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string content = line[6..];
            if (content == "[DONE]") break;
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() < 1 ||
                !choices[0].TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object ||
                !delta.TryGetProperty("content", out var part) ||
                part.ValueKind != JsonValueKind.String) continue;
            string text = strip.Push(part.GetString() ?? "");
            if (text.Length > 0) { emitted = true; yield return new AgentDelta(Text: text); }
        }
        strip.Complete();

        if (emitted) yield break;
        // A few backends send empty SSE; non-streaming retry is for dialogue
        // only, never for tool operations.
        using var retry = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = JsonContent.Create(new {
                model = cfg.LlmModel, stream = false, messages,
                temperature = cfg.LlmTemperature, top_p = cfg.LlmTopP,
                top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                max_tokens = cfg.LlmMaxTokens })
        };
        using var complete = await client.SendAsync(retry, ct);
        complete.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await complete.Content.ReadAsStringAsync(ct));
        if (json.RootElement.TryGetProperty("choices", out var retryChoices) &&
            retryChoices.ValueKind == JsonValueKind.Array && retryChoices.GetArrayLength() > 0 &&
            retryChoices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var body) && body.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(body.GetString()))
            yield return new AgentDelta(Text: NoEmoji.Strip(body.GetString()!));
        else throw new InvalidOperationException("LLM вернула пустой ответ");
    }
}
