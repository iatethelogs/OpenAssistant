using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Chat;

public sealed record AgentDelta(string? Text = null, string? Tool = null, object? Result = null, object? Media = null);

/// <summary>OpenAI-compatible SSE tool_calls: запрос → вызов навыков → ответ модели.</summary>
public sealed class AgentRunner(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> options,
    AgentToolService skills)
{
    private sealed class ToolParts
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new();
    }

    public async IAsyncEnumerable<AgentDelta> GenerateAsync(IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new List<object>(history.Select(x => (object)new { role = x.Role, content = x.Content }));
        int executed = 0;
        for (int round = 0; round < 5; round++)
        {
            var enabled = (await skills.ListAsync(ct)).Where(x => x.Enabled).ToArray();
            var cfg = options.CurrentValue;
            object payload = enabled.Length > 0
                ? new {
                    model = cfg.LlmModel, stream = true, temperature = cfg.LlmTemperature,
                    top_p = cfg.LlmTopP, top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                    max_tokens = cfg.LlmMaxTokens, messages,
                    tools = enabled.Select(x => new { type = "function", function = new {
                        name = x.Name, description = x.Description, parameters = x.Parameters } }).ToArray(),
                    tool_choice = "auto"
                }
                : new {
                    model = cfg.LlmModel, stream = true, temperature = cfg.LlmTemperature,
                    top_p = cfg.LlmTopP, top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                    max_tokens = cfg.LlmMaxTokens, messages
                };
            using var request = new HttpRequestMessage(HttpMethod.Post, cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions")
            { Content = JsonContent.Create(payload) };
            using var response = await clients.CreateClient("llm").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var pending = new SortedDictionary<int, ToolParts>();
            var answer = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                string content = line[6..];
                if (content == "[DONE]") break;
                using var json = JsonDocument.Parse(content);
                if (!json.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta) ||
                    delta.ValueKind != JsonValueKind.Object) continue;
                if (delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    string part = text.GetString() ?? "";
                    answer.Append(part);
                    yield return new AgentDelta(Text: part);
                }
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out int index) ||
                            index < 0 || index > 16) continue;
                        if (!pending.TryGetValue(index, out var t)) pending[index] = t = new ToolParts();
                        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            t.Id += id.GetString();
                        if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                t.Name += name.GetString();
                            if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                            {
                                if (t.Arguments.Length + args.GetString()!.Length > 8192)
                                    throw new InvalidDataException("Слишком длинные аргументы инструмента");
                                t.Arguments.Append(args.GetString());
                            }
                        }
                    }
            }
            if (pending.Count == 0) yield break;
            if (executed + pending.Count > 8 || round == 4)
                throw new InvalidOperationException("Превышен лимит вызовов инструментов");
            var callsForHistory = pending.Values.Select((t, index) => new {
                id = string.IsNullOrWhiteSpace(t.Id) ? $"call_{round}_{index}" : t.Id,
                type = "function",
                function = new { name = t.Name, arguments = t.Arguments.ToString() }
            }).ToArray();
            messages.Add(new { role = "assistant", content = answer.ToString(), tool_calls = callsForHistory });
            foreach (var call in callsForHistory)
            {
                executed++;
                object result;
                object? media = null;
                try
                {
                    using var args = JsonDocument.Parse(call.function.arguments);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(15));
                    var action = await skills.ExecuteAsync(call.function.name, args.RootElement, limit.Token);
                    result = action.Result;
                    media = action.Media;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                    HttpRequestException or JsonException or OperationCanceledException or InvalidDataException)
                { result = new { error = ex.Message }; }
                yield return new AgentDelta(Tool: call.function.name, Result: result, Media: media);
                messages.Add(new { role = "tool", tool_call_id = call.id, content = JsonSerializer.Serialize(result) });
            }
        }
    }
}
