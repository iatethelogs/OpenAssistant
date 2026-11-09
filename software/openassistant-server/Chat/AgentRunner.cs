using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

public sealed record AgentDelta(string? Text = null, string? Tool = null,
    object? Result = null, object? Media = null);

/// <summary>
/// Uses the configured conversational LLM directly with OpenAI function tools.
/// The old rule-based classifier and separate intent LLM are disabled.
/// Tool executions are checked server-side; the model never decides success.
/// </summary>
public sealed class AgentRunner(IHttpClientFactory clients, IOptionsMonitor<ServerOptions> options,
    AgentToolService skills, CustomToolStore customTools, ToolRegistry registry,
    ILogger<AgentRunner> logger)
{
    private sealed record ToolCall(string Id, string Name, JsonElement Arguments);
    private sealed record ModelTurn(string Text, ToolCall[] Calls, bool JsonFallback);
    private sealed record ToolInfo(string Name, string Description, object Parameters);

    private const int MaxCalls = 8;
    private const int MaxRounds = 3;
    private const int MaxObservationChars = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // No individual song, performer, or device is mentioned here. Actions are
    // inferred only from the user's actual request. No affirmative response
    // can be emitted until a server-executed tool reports its real result.
    private const string ToolInstructions = """
        Ты голосовой ассистент Мартин. Отвечай по-русски, коротко, без эмодзи.
        Тебе доступны функции (tools) для реальных внешних действий.
        Если текущая реплика — приветствие, вопрос, шутка или обычный разговор,
        отвечай текстом, НЕ вызывай инструменты. Не выбирай музыку просто
        потому, что музыка обсуждалась в прошлом или есть в списке функций.
        Если пользователь попросил действие — вызови соответствующие функции;
        не изображай выполнение действия обычным текстом.
        Один запрос может содержать несколько самостоятельных действий:
        вызывай необходимые функции для каждого действия, сохраняй порядок.
        Не игнорируй часть запроса. Не выполняй действия, которые не просили.
        Для Яндекс Музыки используй music_play_query с произвольным названием
        исполнителя/трека и kind: artist (явно группа/исполнитель),
        track (явно песня/трек) или auto (неоднозначное название).
        Передавай query только из слов текущего запроса; не подставляй известных
        тебе артистов, прошлые поиски или названия из системного промпта.
        Яндекс Музыка — сервис по умолчанию, не спрашивай название сервиса.
        Для избранного используй music_play_liked; для текущего плеера —
        music_control; для радиостанций — radio_play.
        Для Home Assistant сначала получи реальные entity_id через
        ha_list_entities, затем используй только эти entity_id.
        Не выдумывай entity_id, помещения, состояния и музыкальные track_id.
        Если действия требуют уточнения, задай один короткий вопрос.
        Текст об успешности музыки или устройств генерирует сервер после
        фактического результата инструмента, не генерируй такой текст сам.
        """;

    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal) {
        "music_search", "music_list_playlists", "ha_list_entities",
        "ha_get_state", "radio_search"
    };

    private static readonly HashSet<string> MusicPlaying = new(StringComparer.Ordinal) {
        "music_play_query", "music_play", "music_play_liked",
        "music_play_playlist", "radio_play"
    };

    private static List<object> History(IReadOnlyList<ChatMessage> history)
    {
        string[] system = history.Where(x => x.Role == "system")
            .Select(x => x.Content?.Trim() ?? "").Where(x => x.Length > 0)
            .Take(5).ToArray();
        // The runtime tool policy is appended after the user's general
        // conversation settings, so artist examples or prose style cannot
        // override the tool contract.
        string instruction = string.Join("\n\n",
            system.Append(NoEmoji.Instruction).Append(ToolInstructions));
        var messages = new List<object> { new { role = "system", content = instruction } };
        var ordinary = history.Where(x => x.Role is "user" or "assistant")
            .Where(x => !string.IsNullOrWhiteSpace(x.Content))
            .TakeLast(17).ToArray();
        var normalized = new List<ChatMessage>();
        foreach (var entry in ordinary)
        {
            if (normalized.Count == 0 && entry.Role != "user") continue;
            if (normalized.Count > 0 && normalized[^1].Role == entry.Role)
                normalized[^1] = normalized[^1] with {
                    Content = normalized[^1].Content + "\n" + entry.Content };
            else normalized.Add(entry);
        }
        if (normalized.Count == 0 || normalized[^1].Role != "user")
            throw new ArgumentException("История должна заканчиваться сообщением пользователя");
        messages.AddRange(normalized.Select(x => (object)new { role = x.Role, content = x.Content }));
        return messages;
    }

    private static ToolCall[] ExtractCalls(JsonElement message)
    {
        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(MaxCalls))
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("function", out var fun) ||
                    fun.ValueKind != JsonValueKind.Object ||
                    !fun.TryGetProperty("name", out var n) ||
                    n.ValueKind != JsonValueKind.String) continue;
                string name = n.GetString() ?? "";
                if (name.Length == 0) continue;
                JsonElement args = JsonSerializer.SerializeToElement(new { });
                if (fun.TryGetProperty("arguments", out var a))
                {
                    if (a.ValueKind == JsonValueKind.Object) args = a.Clone();
                    else if (a.ValueKind == JsonValueKind.String)
                    {
                        try { args = JsonDocument.Parse(a.GetString() ?? "{}").RootElement.Clone(); }
                        catch (JsonException) { args = default; }
                    }
                    else args = default;
                }
                string id = item.TryGetProperty("id", out var ident) &&
                    ident.ValueKind == JsonValueKind.String
                    ? ident.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id)) id = "call_" + calls.Count;
                calls.Add(new ToolCall(id, name, args));
            }
        }
        return calls.ToArray();
    }

    private static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("", content.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object &&
                x.TryGetProperty("type", out var type) && type.GetString() == "text")
            .Select(x => x.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""));
    }

    private static ModelTurn ParseTurn(string response, bool fallback)
    {
        using var json = JsonDocument.Parse(response);
        var root = json.RootElement;
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("LLM вернула ответ без choices[0].message");

        var calls = ExtractCalls(message);
        string content = ExtractText(message).Trim();
        if ((fallback || (calls.Length == 0 && content.StartsWith('{'))) &&
            content.StartsWith('{'))
        {
            try
            {
                using var data = JsonDocument.Parse(content);
                var value = data.RootElement;
                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("actions", out var actions) &&
                    actions.ValueKind == JsonValueKind.Array)
                {
                    var plan = new List<ToolCall>();
                    foreach (var item in actions.EnumerateArray().Take(MaxCalls))
                    {
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("name", out var n) ||
                            n.ValueKind != JsonValueKind.String ||
                            !item.TryGetProperty("arguments", out var a)) continue;
                        plan.Add(new ToolCall("plan_" + plan.Count, n.GetString() ?? "", a.Clone()));
                    }
                    string reply = value.TryGetProperty("reply", out var t) &&
                        t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    return new ModelTurn(reply, plan.ToArray(), fallback);
                }
            }
            catch (JsonException) { if (fallback) throw new InvalidDataException("LLM вернула некорректный JSON-план"); }
        }
        if (fallback) throw new InvalidDataException("LLM не вернула actions и reply");
        return new ModelTurn(content, calls, false);
    }

    private static bool ToolSyntaxUnsupported(HttpStatusCode status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or
            HttpStatusCode.NotImplemented;

    private async Task<ModelTurn> CompleteAsync(List<object> history,
        ToolInfo[] available, bool useJson, CancellationToken ct)
    {
        var cfg = options.CurrentValue;
        string endpoint = cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions";
        var http = clients.CreateClient("llm");
        int maxTokens = Math.Clamp(cfg.LlmMaxTokens, 256, 2048);

        var modelTools = available.Select(x => new {
            type = "function",
            function = new { name = x.Name, description = x.Description, parameters = x.Parameters }
        }).ToArray();
        var prompt = new List<object>(history);

        if (useJson)
        {
            string list = string.Join("\n", available.Select(x =>
                x.Name + ": " + x.Description + " | parameters=" +
                JsonSerializer.Serialize(x.Parameters, Json)));
            prompt[0] = new { role = "system", content =
                ToolInstructions + "\nОтветь строго одним JSON-объектом " +
                "{\"actions\":[{\"name\":\"имя функции\",\"arguments\":{}}],\"reply\":\"\"}. " +
                "Если действие не нужно, actions=[], reply=обычный ответ. " +
                "Если нужны несколько действий — помести их в actions в исходном порядке. " +
                "Не дублируй одно действие. Никогда не выдумывай вызовы. " +
                "Доступные функции:\n" + list };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = JsonContent.Create(useJson ? (object)new {
                model = cfg.LlmModel, stream = false, messages = prompt,
                temperature = 0.1, max_tokens = maxTokens,
                response_format = new { type = "json_object" }
            } : new {
                model = cfg.LlmModel, stream = false, messages = prompt,
                temperature = 0.1, max_tokens = maxTokens,
                tools = modelTools, tool_choice = "auto", parallel_tool_calls = true
            })
        };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(ct);
            if (!useJson && ToolSyntaxUnsupported(response.StatusCode))
            {
                logger.LogWarning("llama.cpp does not accept native tool calls (HTTP {Status}); using JSON tool plan",
                    (int)response.StatusCode);
                return await CompleteJsonFallbackAsync(history, available, ct);
            }
            if (useJson && ToolSyntaxUnsupported(response.StatusCode))
            {
                logger.LogWarning("llama.cpp rejected response_format (HTTP {Status}); retrying plain JSON",
                    (int)response.StatusCode);
                return await CompletePlainJsonAsync(history, available, ct);
            }
            throw new InvalidOperationException("LLM HTTP " + (int)response.StatusCode +
                ": " + detail[..Math.Min(detail.Length, 260)]);
        }
        string body = await response.Content.ReadAsStringAsync(ct);
        return ParseTurn(body, useJson);
    }

    private Task<ModelTurn> CompleteJsonFallbackAsync(
        List<object> history, ToolInfo[] tools, CancellationToken ct) =>
        CompleteAsync(history, tools, useJson: true, ct);

    private async Task<ModelTurn> CompletePlainJsonAsync(
        List<object> history, ToolInfo[] available, CancellationToken ct)
    {
        var cfg = options.CurrentValue;
        string list = string.Join("\n", available.Select(x =>
            x.Name + ": " + x.Description + " " + JsonSerializer.Serialize(x.Parameters, Json)));
        var messages = new List<object>(history);
        messages[0] = new { role = "system", content = ToolInstructions +
            "\nОтветь ТОЛЬКО JSON вида {\"actions\":[],\"reply\":\"текст\"} или " +
            "{\"actions\":[{\"name\":\"...\",\"arguments\":{}}],\"reply\":\"\"}. " +
            "Функции:\n" + list };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions") {
            Content = JsonContent.Create(new {
                model = cfg.LlmModel, stream = false, messages,
                temperature = 0.1, max_tokens = Math.Clamp(cfg.LlmMaxTokens, 256, 2048)
            })
        };
        using var response = await clients.CreateClient("llm").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return ParseTurn(await response.Content.ReadAsStringAsync(ct), fallback: true);
    }

    private static string Observation(object result)
    {
        string text = JsonSerializer.Serialize(result, Json);
        return text.Length <= MaxObservationChars ? text :
            JsonSerializer.Serialize(new {
                truncated = true, preview = text[..MaxObservationChars]
            });
    }

    public async IAsyncEnumerable<AgentDelta> GenerateAsync(
        IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
    {
        var enabled = (await skills.ListAsync(ct)).Where(x => x.Enabled).ToArray();
        var available = enabled.Select(x => new ToolInfo(
            x.Name, x.Description[..Math.Min(x.Description.Length, 260)], x.Parameters)).ToList();

        // Custom tools become selectable only when a real handler is registered.
        // Merely saving a schema never grants it execution permission.
        try
        {
            var registered = registry.Names().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var custom = await customTools.ListAsync(ct);
            foreach (var tool in custom.Where(x => x.Enabled && registered.Contains(x.Name))
                .Take(8))
                available.Add(new ToolInfo(tool.Name,
                    tool.Description[..Math.Min(tool.Description.Length, 180)], tool.Parameters));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is IOException or InvalidDataException or JsonException)
        {
            logger.LogWarning(ex, "Custom tools unavailable; built-in tools remain enabled");
        }

        var toolNames = available.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var messages = History(history);
        var used = new HashSet<string>(StringComparer.Ordinal);
        bool performed = false;
        int count = 0;
        bool jsonMode = false;

        for (int round = 0; round < MaxRounds; round++)
        {
            var response = await CompleteAsync(messages, available.ToArray(), jsonMode, ct);
            jsonMode = response.JsonFallback;

            if (response.Calls.Length == 0)
            {
                if (!performed)
                    yield return new AgentDelta(Text: NoEmoji.Strip(
                        string.IsNullOrWhiteSpace(response.Text)
                            ? "Не удалось получить ответ модели." : response.Text));
                yield break;
            }

            // Never stream a model's speculative content alongside tool calls.
            // The assistant may only acknowledge real results later.
            bool needsAnotherRound = false;
            if (!jsonMode)
            {
                messages.Add(new {
                    role = "assistant", content = (string?)null,
                    tool_calls = response.Calls.Select(x => new {
                        id = x.Id, type = "function",
                        function = new {
                            name = x.Name,
                            arguments = x.Arguments.ValueKind == JsonValueKind.Undefined
                                ? "{}" : x.Arguments.GetRawText()
                        }
                    }).ToArray()
                });
            }

            foreach (var tool in response.Calls)
            {
                ct.ThrowIfCancellationRequested();
                object result;
                object? media = null;
                if (++count > MaxCalls)
                    result = new { error = "Превышен лимит вызовов инструментов за запрос" };
                else if (!toolNames.Contains(tool.Name))
                    result = new { error = "Инструмент не разрешён или отключён: " + tool.Name };
                else if (tool.Arguments.ValueKind != JsonValueKind.Object)
                    result = new { error = "Инструменту нужен JSON-объект аргументов" };
                else if (!used.Add(tool.Name + ":" + tool.Arguments.GetRawText()))
                    result = new { error = "Повторный вызов инструмента в одной команде заблокирован" };
                else
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(
                            MusicPlaying.Contains(tool.Name) ? 40 : 15));
                        if (enabled.Any(x => x.Name == tool.Name))
                        {
                            var execution = await skills.ExecuteAsync(tool.Name,
                                tool.Arguments, timeout.Token);
                            result = execution.Result;
                            media = execution.Media;
                        }
                        else result = await registry.InvokeAsync(
                            tool.Name, tool.Arguments, timeout.Token);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested &&
                        ex is InvalidOperationException or ArgumentException or
                            HttpRequestException or JsonException or InvalidDataException or
                            NotSupportedException or TaskCanceledException)
                    {
                        logger.LogWarning(ex, "Tool {Tool} failed", tool.Name);
                        result = new { error = ex.Message };
                    }
                }

                performed = true;
                logger.LogInformation("LLM tool {Name} executed; media={Media}",
                    tool.Name, media is not null);
                yield return new AgentDelta(Tool: tool.Name, Result: result, Media: media);

                if (ReadOnly.Contains(tool.Name)) needsAnotherRound = true;
                string observed = Observation(result);
                if (!jsonMode)
                    messages.Add(new { role = "tool", tool_call_id = tool.Id, content = observed });
                else messages.Add(new { role = "assistant",
                    content = "Результат инструмента " + tool.Name + ": " + observed +
                        "\nПланируй только оставшиеся действия запроса." });
            }
            if (!needsAnotherRound || count >= MaxCalls) yield break;
        }

        if (!performed)
            yield return new AgentDelta(Text: "Не удалось определить запрошенное действие.");
    }
}
