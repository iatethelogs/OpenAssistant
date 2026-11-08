using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

public sealed record AgentDelta(string? Text = null, string? Tool = null, object? Result = null, object? Media = null);

/// <summary>OpenAI-compatible SSE tool_calls: запрос → вызов навыков → ответ модели.</summary>
public sealed class AgentRunner(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> options,
    AgentToolService skills, CustomToolStore customTools, ToolRegistry toolRegistry,
    ToolIntentPlanner planner, ILogger<AgentRunner> logger)
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
            {
                normalized[^1] = normalized[^1] with
                {
                    Content = normalized[^1].Content + "\n\n" + entry.Content
                };
            }
            else normalized.Add(entry);
        }
        if (normalized.Count == 0 || normalized[^1].Role != "user")
            throw new ArgumentException("История должна заканчиваться запросом пользователя");
        var result = new List<object>();
        // The model decides intent, not a server-side chain of keyword checks.
        // Keep external actions grounded in explicit tool calls and their results.
        const string toolUsage = """
            Если пользователь просит выполнить внешнее действие, выбирай подходящий доступный инструмент
            и вызывай его. Не утверждай, что действие выполнено, пока не получишь результат вызова.
            Различай музыку и управление устройствами по смыслу, а не по одному слову «включи».
            Настроенный музыкальный провайдер — Яндекс Музыка. Пользователь не обязан
            называть его при музыкальных запросах. Никогда не спрашивай, какую
            платформу использовать, когда есть разрешённый музыкальный навык.
            После успешного результата инструмента кратко сообщи, что воспроизводится.
            «Включи группу Кино» означает музыку исполнителя Кино: вызови music_play_query
            с query="Кино", kind="artist". «Включи песню Звезда по имени Солнце» —
            music_play_query с kind="track". «Включи свет» относится к Home Assistant,
            а не к Яндекс Музыке. Не выдумывай ID треков, исполнителей и устройств.
            Если вызов функции недоступен или завершился ошибкой, сообщи об этом прямо.
            """;
        result.Add(new { role = "system", content = string.Join("\n\n",
            instructions.Append(NoEmoji.Instruction).Append(toolUsage)) });
        result.AddRange(normalized.Select(entry => (object)new {
            role = entry.Role, content = entry.Content
        }));
        return result;
    }

    private sealed class ToolParts
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments = new();
    }

    public async IAsyncEnumerable<AgentDelta> GenerateAsync(IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // llama.cpp strict chat templates reject consecutive system or user messages.
        // Combine all system context (including recalled snippets) and normalize
        // prior turns into a true user/assistant alternation before serialization.
        var messages = NormalizeHistory(history);
        int executed = 0;
        bool toolsSupported = true;
        bool planFurther = true;
        var completedTools = new List<object>();
        var plannedSignatures = new HashSet<string>(StringComparer.Ordinal);
        for (int round = 0; round < 5; round++)
        {
            SkillDefinition[] enabled = toolsSupported
                ? (await skills.ListAsync(ct)).Where(x => x.Enabled).ToArray()
                : Array.Empty<SkillDefinition>();
            CustomToolDefinition[] custom = toolsSupported
                ? (await customTools.ListAsync(ct)).Where(x => x.Enabled).ToArray()
                : Array.Empty<CustomToolDefinition>();
            var offeredTools = enabled.Select(x => new {
                type = "function", function = new {
                    name = x.Name, description = x.Description, parameters = (object)x.Parameters }
            }).Concat(custom.Select(x => new {
                type = "function", function = new {
                    name = x.Name, description = x.Description, parameters = (object)x.Parameters }
            })).ToArray();
            // First, ask the LLM to decide intent in a short structured step.
            // Native tool_choice=auto is unreliable with some local models:
            // they can simply answer "which music service?" despite having
            // received the tool list. This planner chooses an action or "none".
            if (planFurther && offeredTools.Length > 0)
            {
                ToolIntentPlanner.Plan? decision = null;
                try
                {
                    decision = await planner.DecideAsync(history, enabled, custom,
                        completedTools, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or
                    InvalidOperationException or JsonException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    logger.LogWarning(ex, "Tool intent planning unavailable; using native tool calls");
                }

                if (decision is not null)
                {
                    var signature = decision.Name + ":" + decision.Arguments.GetRawText();
                    if (!plannedSignatures.Add(signature))
                    {
                        logger.LogWarning("Planner attempted repeated identical tool {Tool}; falling back to final response", decision.Name);
                        planFurther = false;
                        toolsSupported = false;
                        continue;
                    }
                    if (++executed > 8)
                        throw new InvalidOperationException("Превышен лимит вызовов инструментов");
                    var toolResult = await InvokeToolAsync(decision.Name,
                        decision.Arguments, custom, ct);
                    string callId = "planned_" + round;
                    messages.Add(new
                    {
                        role = "assistant", content = "",
                        tool_calls = new[] { new {
                            id = callId, type = "function",
                            function = new { name = decision.Name,
                                arguments = decision.Arguments.GetRawText() }
                        } }
                    });
                    messages.Add(new { role = "tool", tool_call_id = callId,
                        content = JsonSerializer.Serialize(toolResult.Result) });
                    completedTools.Add(new { tool = decision.Name, result = toolResult.Result });
                    yield return new AgentDelta(Tool: decision.Name,
                        Result: toolResult.Result, Media: toolResult.Media);
                    // A playback/control command already produces its side
                    // effect; avoid repeating it when composing the final reply.
                    if (toolResult.Media is not null)
                    {
                        planFurther = false;
                        toolsSupported = false;
                    }
                    continue;
                }
                planFurther = false;
            }

            var cfg = options.CurrentValue;
            object payload = offeredTools.Length > 0
                ? new {
                    model = cfg.LlmModel, stream = true, temperature = cfg.LlmTemperature,
                    top_p = cfg.LlmTopP, top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                    max_tokens = cfg.LlmMaxTokens, messages,
                    tools = offeredTools,
                    tool_choice = "auto"
                }
                : new {
                    model = cfg.LlmModel, stream = true, temperature = cfg.LlmTemperature,
                    top_p = cfg.LlmTopP, top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                    max_tokens = cfg.LlmMaxTokens, messages
                };
            var client = clients.CreateClient("llm");
            var endpoint = cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions";
            HttpResponseMessage candidate;
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                { Content = JsonContent.Create(payload) })
            {
                candidate = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            // Some OpenAI-compatible local engines cannot parse tool_calls. Keep
            // ordinary chat usable, but never claim actions were executed.
            if (offeredTools.Length > 0 &&
                (candidate.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                 candidate.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity ||
                 candidate.StatusCode == System.Net.HttpStatusCode.InternalServerError))
            {
                candidate.Dispose();
                toolsSupported = false;
                var simple = new {
                    model = cfg.LlmModel, stream = true, temperature = cfg.LlmTemperature,
                    top_p = cfg.LlmTopP, top_k = cfg.LlmTopK,
                    repeat_penalty = cfg.LlmRepeatPenalty, max_tokens = cfg.LlmMaxTokens,
                    messages
                };
                using var retry = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    { Content = JsonContent.Create(simple) };
                candidate = await client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            using var response = candidate;
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct);
                string detail = body.Length > 600 ? body[..600] : body;
                throw new InvalidOperationException(
                    $"LLM HTTP {(int)response.StatusCode}: {detail}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var noEmoji = new NoEmojiStream();
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
                    string part = noEmoji.Push(text.GetString() ?? "");
                    if (part.Length > 0) { answer.Append(part); yield return new AgentDelta(Text: part); }
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
            if (pending.Count == 0)
            {
                if (answer.Length > 0) yield break;

                // A few OpenAI-compatible runtimes acknowledge an SSE request
                // without emitting content. Retry once as a non-streaming chat,
                // with no tool calls, instead of silently returning an empty answer.
                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new {
                        model = cfg.LlmModel, stream = false,
                        temperature = cfg.LlmTemperature, top_p = cfg.LlmTopP,
                        top_k = cfg.LlmTopK, repeat_penalty = cfg.LlmRepeatPenalty,
                        max_tokens = cfg.LlmMaxTokens, messages
                    })
                };
                using var retryResponse = await client.SendAsync(retryRequest,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if (!retryResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"LLM не ответила: HTTP {(int)retryResponse.StatusCode}");
                using var retryJson = JsonDocument.Parse(
                    await retryResponse.Content.ReadAsStringAsync(ct));
                string? fallbackText = null;
                if (retryJson.RootElement.TryGetProperty("choices", out var resultChoices) &&
                    resultChoices.ValueKind == JsonValueKind.Array && resultChoices.GetArrayLength() > 0 &&
                    resultChoices[0].TryGetProperty("message", out var reply) &&
                    reply.TryGetProperty("content", out var bodyText) &&
                    bodyText.ValueKind == JsonValueKind.String)
                    fallbackText = bodyText.GetString();
                if (string.IsNullOrWhiteSpace(fallbackText))
                    throw new InvalidOperationException(
                        "LLM вернула пустой ответ. Проверьте модель, контекст и шаблон чата.");
                yield return new AgentDelta(Text: NoEmoji.Strip(fallbackText));
                yield break;
            }
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
                object? media;
                try
                {
                    using var parsedArgs = JsonDocument.Parse(call.function.arguments);
                    var execution = await InvokeToolAsync(call.function.name,
                        parsedArgs.RootElement, custom, ct);
                    result = execution.Result;
                    media = execution.Media;
                }
                catch (JsonException ex)
                {
                    result = new { error = "Неверные параметры вызова: " + ex.Message };
                    media = null;
                }
                yield return new AgentDelta(Tool: call.function.name, Result: result, Media: media);
                messages.Add(new { role = "tool", tool_call_id = call.id,
                    content = JsonSerializer.Serialize(result) });
            }
        }
    }
    private async Task<(object Result, object? Media)> InvokeToolAsync(
        string name, JsonElement args,
        IReadOnlyList<CustomToolDefinition> allowedCustom, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(name.StartsWith("music_", StringComparison.Ordinal)
                ? 40 : 15));
            if (allowedCustom.Any(x => x.Name == name))
            {
                if (toolRegistry.Names().Contains(name, StringComparer.OrdinalIgnoreCase))
                    return (await toolRegistry.InvokeAsync(name, args, limit.Token), null);
                return (new { error = "Обработчик этого инструмента ещё не подключён",
                    implemented = false }, null);
            }
            var action = await skills.ExecuteAsync(name, args, limit.Token);
            return (action.Result, action.Media);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            HttpRequestException or JsonException or OperationCanceledException or InvalidDataException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Tool invocation {Tool} failed", name);
            return (new { error = ex.Message }, null);
        }
    }

}
