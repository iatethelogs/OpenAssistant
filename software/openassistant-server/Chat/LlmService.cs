using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Chat;

/// <summary>сообщение истории чата.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>потоковый клиент OpenAI-совместимого llama-server.</summary>
public sealed class LlmService(IHttpClientFactory http, IOptionsMonitor<ServerOptions> settings)
{
    /// <summary>читает SSE по токенам. не ждём, пока модель выдаст всю простыню.</summary>
    public async IAsyncEnumerable<string> GenerateAsync(IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var options = settings.CurrentValue;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            options.LlmUrl.TrimEnd('/') + "/v1/chat/completions");
        request.Content = JsonContent.Create(new {
            model = options.LlmModel,
            stream = true, temperature = options.LlmTemperature, top_p = options.LlmTopP,
            top_k = options.LlmTopK, repeat_penalty = options.LlmRepeatPenalty,
            max_tokens = options.LlmMaxTokens,
            messages = history.Select(m => new { role = m.Role, content = m.Content })
        });
        using var response = await http.CreateClient("llm").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string payload = line[6..];
            if (payload == "[DONE]") yield break;
            using var data = JsonDocument.Parse(payload);
            if (!data.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0) continue;
            if (choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                yield return content.GetString() ?? "";
        }
    }
}
