using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Chat;

public sealed record AgentDelta(string? Text = null, string? Tool = null,
    object? Result = null, object? Media = null);

/// <summary>
/// The SAME conversational LLM can return ordinary text or call one structured
/// submit_action_plan tool. Hard-coded intent matching is intentionally
/// disabled. The server remains the only executor and enforces tool permissions.
/// </summary>
public sealed class AgentRunner(IHttpClientFactory clients,
    IOptionsMonitor<ServerOptions> options, AgentToolService skills,
    ILogger<AgentRunner> logger)
{
    // Rollback option: the former RuleBasedCommandRouter implementation is kept
    // in the project but is NOT invoked by this agent or registered in DI.

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) {
        "music_play_query", "music_play_liked", "music_control",
        "radio_play", "ha_list_entities", "ha_control_named"
    };
    private const int MaxActions = 5;

    // The tools contract is deliberately compact: a single tool for a whole
    // plan, not dozens of verbose examples that can leak into answers.
    private static readonly object[] PlanTools = [
        new {
            type = "function",
            function = new {
                name = "submit_action_plan",
                description = "Выполнить один или несколько явно запрошенных пользователем действий. Не вызывай для беседы, приветствий, вопросов и цитат. Каждый элемент actions — отдельное действие в порядке пользователя.",
                parameters = new {
                    type = "object",
                    properties = new {
                        actions = new {
                            type = "array",
                            minItems = 1,
                            maxItems = MaxActions,
                            description = "Упорядоченный список независимых действий (не больше пяти).",
                            items = new {
                                type = "object",
                                properties = new {
                                    name = new { type = "string", @enum = new[] {
                                        "music_play_query", "music_play_liked", "music_control",
                                        "radio_play", "ha_list_entities", "ha_control_named" } },
                                    query = new { type = "string", description = "Свободное название трека, исполнителя или станции. Не заменяй его примерами, не придумывай названия." },
                                    kind = new { type = "string", @enum = new[] { "artist", "track", "auto" },
                                        description = "artist для исполнителя/группы; track для песни; auto если неясно." },
                                    action = new { type = "string", @enum = new[] {
                                        "pause", "resume", "next", "previous", "stop", "status",
                                        "turn_on", "turn_off", "open_cover", "close_cover" } },
                                    target = new { type = "string", description = "Имя устройства/предмета из фразы пользователя, если нужен Home Assistant." },
                                    area = new { type = "string", description = "Название комнаты из фразы пользователя. Никогда не угадывай." },
                                    scope = new { type = "string", @enum = new[] { "one", "all" },
                                        description = "all только если пользователь явно просит все устройства категории в комнате." },
                                    filter = new { type = "string", description = "Поисковая строка для ha_list_entities." }
                                },
                                required = new[] { "name" },
                                additionalProperties = false
                            }
                        }
                    },
                    required = new[] { "actions" },
                    additionalProperties = false
                }
            }
        }
    ];

    private sealed record PlannedAction(string Name, JsonElement Args);

    private static string? StringField(JsonElement node, string key) =>
        node.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()?.Trim() : null;

    private static List<PlannedAction> ParsePlan(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array ||
            actions.GetArrayLength() is < 1 or > MaxActions)
            throw new InvalidDataException("LLM вернула неверный список действий.");

        var output = new List<PlannedAction>();
        foreach (var item in actions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Некорректный элемент плана LLM.");
            string name = StringField(item, "name") ?? "";
            if (!Supported.Contains(name))
                throw new InvalidOperationException("Неизвестный инструмент: " + name);
            // A disabled tool is still validated as a plan item. Its executor
            // rejects it independently, allowing other requested actions to
            // finish and preventing silent omission from the final response.

            static string Required(JsonElement x, string key, int max = 120)
            {
                string s = StringField(x, key) ?? "";
                if (s.Length is < 1 || s.Length > max)
                    throw new InvalidDataException("Не указан допустимый параметр: " + key);
                return s;
            }
            object args = name switch {
                "music_play_query" => new {
                    query = Required(item, "query"),
                    kind = ValidateKind(Required(item, "kind", 8))
                },
                "music_play_liked" => new { },
                "music_control" => new {
                    action = ValidatePlayer(Required(item, "action", 12))
                },
                "radio_play" => new { query = Required(item, "query") },
                "ha_list_entities" => new { filter = StringField(item, "filter") ?? "" },
                "ha_control_named" => new {
                    target = StringField(item, "target") ?? "",
                    area = StringField(item, "area") ?? "",
                    scope = ValidateScope(StringField(item, "scope") ?? "one"),
                    action = ValidateHome(Required(item, "action", 16))
                },
                _ => throw new InvalidOperationException("Неизвестный инструмент")
            };
            output.Add(new PlannedAction(name, JsonSerializer.SerializeToElement(args)));
        }
        return output;
    }

    private static string ValidateKind(string kind) =>
        kind is "artist" or "track" or "auto" ? kind :
        throw new InvalidDataException("Неверный kind музыки");
    private static string ValidatePlayer(string action) =>
        action is "pause" or "resume" or "next" or "previous" or "stop" or "status" ? action :
        throw new InvalidDataException("Неверная команда плеера");
    private static string ValidateHome(string action) =>
        action is "turn_on" or "turn_off" or "open_cover" or "close_cover" ? action :
        throw new InvalidDataException("Неверная команда умного дома");
    private static string ValidateScope(string scope) =>
        scope is "one" or "all" ? scope :
        throw new InvalidDataException("Неверный scope умного дома");

    private static object[] Messages(IReadOnlyList<ChatMessage> history,
        bool structuredFallback = false)
    {
        var systems = history.Where(x => x.Role == "system" &&
            !string.IsNullOrWhiteSpace(x.Content)).Select(x => x.Content.Trim()).ToArray();
        var dialogue = history.Where(x => (x.Role is "user" or "assistant") &&
            !string.IsNullOrWhiteSpace(x.Content)).TakeLast(12).ToArray();

        const string guidance = """
            Ты Мартин, голосовой помощник. Отвечай на русском языке кратко и по существу, без эмодзи.

            У тебя есть инструмент submit_action_plan. Вызови его ТОЛЬКО когда пользователь
            действительно просит выполнить действие: музыку, радио или управление умным домом.
            При приветствиях, разговоре, вопросах, отрицаниях, цитировании и гипотетических
            примерах отвечай текстом БЕЗ инструментов. Не считай названия из истории новой командой.
            Если просьба содержит несколько действий, включи все действия в один вызов,
            сохраняя порядок, не пропуская и не добавляя действий.
            Для Яндекс Музыки: "группу/исполнителя" -> music_play_query kind=artist;
            "песню/трек" -> kind=track; неопределённый тип -> kind=auto.
            Передавай query ровно как название из текущего запроса, не подставляй
            название какой-либо известной песни или артиста по умолчанию.
            music_play_liked — только для просьбы о понравившихся/любимых треках.
            Для HA по человеческим именам и помещениям используй ha_control_named.
            scope=all только при явном "весь/все"; иначе scope=one.
            Для группового света передавай target=свет и реальную комнату area.
            Никогда не выдумывай имена, области и идентификаторы устройств.
            Не утверждай, что действие уже выполнено: его подтверждает сервер после исполнения.
            """;
        string instructions = string.Join("\n\n", systems.Append(NoEmoji.Instruction).Append(guidance));
        if (structuredFallback)
            instructions += """

                РЕЖИМ СОВМЕСТИМОСТИ: JSON вместо native tool_calls.
                Верни только один JSON-объект без оформления:
                Для разговора: {"reply":"твой ответ"}.
                Для реального действия: {"actions":[{"name":"music_play_query","query":"название из запроса","kind":"auto"}]}.
                Для нескольких действий — один массив actions в порядке пользователя.
                Допустимые name: music_play_query, music_play_liked, music_control,
                radio_play, ha_list_entities, ha_control_named.
                Не выполняй действия для приветствия и обычных вопросов.
                Не выдумывай исполнителей, песни, устройства и успешные результаты.
                """;

        var messages = new List<object> { new { role = "system", content = instructions } };
        foreach (var msg in dialogue)
            messages.Add(new { role = msg.Role, content = msg.Content });
        return messages.ToArray();
    }

    public async IAsyncEnumerable<AgentDelta> GenerateAsync(
        IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = options.CurrentValue;
        var enabled = (await skills.ListAsync(ct)).Where(x => x.Enabled)
            .Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        // Never advertise actions the user has disabled. If none are
        // available, the same model simply answers in conversational mode.
        bool canAct = Supported.Overlaps(enabled);
        var messages = Messages(history);
        string endpoint = cfg.LlmUrl.TrimEnd('/') + "/v1/chat/completions";
        var client = clients.CreateClient("llm");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
            Content = JsonContent.Create(new {
                model = cfg.LlmModel, stream = false, messages,
                tools = canAct ? PlanTools : null,
                tool_choice = canAct ? "auto" : "none",
                parallel_tool_calls = false,
                temperature = Math.Min(cfg.LlmTemperature, 0.4),
                top_p = cfg.LlmTopP, max_tokens = Math.Max(512, Math.Min(2048, cfg.LlmMaxTokens))
            })
        };
        string jsonResponse;
        bool structuredFallback = false;
        using (var response = await client.SendAsync(request, ct))
        {
            jsonResponse = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                bool toolUnsupported = canAct &&
                    (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                     response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity) &&
                    (jsonResponse.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                     jsonResponse.Contains("jinja", StringComparison.OrdinalIgnoreCase) ||
                     jsonResponse.Contains("template", StringComparison.OrdinalIgnoreCase));
                if (!toolUnsupported)
                    throw new InvalidOperationException(
                        "LLM HTTP " + (int)response.StatusCode + ": " +
                        jsonResponse[..Math.Min(jsonResponse.Length, 300)]);
                structuredFallback = true;
                logger.LogWarning("llama.cpp rejected native tools; retrying with structured JSON on the same LLM");
            }
        }
        if (structuredFallback)
        {
            using var backup = new HttpRequestMessage(HttpMethod.Post, endpoint) {
                Content = JsonContent.Create(new {
                    model = cfg.LlmModel,
                    stream = false,
                    messages = Messages(history, structuredFallback: true),
                    temperature = 0.1,
                    top_p = cfg.LlmTopP,
                    max_tokens = Math.Max(512, Math.Min(2048, cfg.LlmMaxTokens))
                })
            };
            using var response = await client.SendAsync(backup, ct);
            jsonResponse = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "LLM JSON fallback HTTP " + (int)response.StatusCode + ": " +
                    jsonResponse[..Math.Min(jsonResponse.Length, 300)]);
        }
        using var document = JsonDocument.Parse(jsonResponse);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("LLM вернула некорректный ответ.");

        List<PlannedAction>? actions = null;
        string? reply = null;
        if (!structuredFallback &&
            message.TryGetProperty("tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array &&
            toolCalls.GetArrayLength() > 0)
        {
            if (toolCalls.GetArrayLength() != 1 ||
                toolCalls[0].ValueKind != JsonValueKind.Object ||
                !toolCalls[0].TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                StringField(function, "name") != "submit_action_plan")
                throw new InvalidDataException("LLM попыталась вызвать неизвестный инструмент.");
            string raw = StringField(function, "arguments") ?? "";
            if (raw.Length is < 2 or > 10000)
                throw new InvalidDataException("Некорректный JSON аргументов инструмента.");
            using var planDocument = JsonDocument.Parse(raw,
                new JsonDocumentOptions { MaxDepth = 12 });
            actions = ParsePlan(planDocument.RootElement);
        }
        else if (message.TryGetProperty("content", out var content) &&
                 content.ValueKind == JsonValueKind.String)
        {
            string body = content.GetString()?.Trim() ?? "";
            if (structuredFallback)
            {
                // Accept only the entire JSON object, optionally wrapped
                // in one exact markdown code fence. Do not extract JSON
                // from surrounding natural-language assertions.
                var fenced = System.Text.RegularExpressions.Regex.Match(body,
                    @"^```(?:json)?\s*([\s\S]*?)\s*```$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (fenced.Success) body = fenced.Groups[1].Value.Trim();
                using var structured = JsonDocument.Parse(body,
                    new JsonDocumentOptions { MaxDepth = 12 });
                var root = structured.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Некорректный JSON ответа модели.");
                if (root.TryGetProperty("actions", out _))
                    actions = ParsePlan(root);
                else if (root.TryGetProperty("reply", out var answer) &&
                    answer.ValueKind == JsonValueKind.String)
                    reply = answer.GetString();
                else throw new InvalidDataException(
                    "LLM не вернула reply либо actions в JSON.");
            }
            else
            {
                // Some Gemma templates encode valid function arguments as
                // ordinary JSON content without the tool_calls wrapper.
                // Accept ONLY a complete, valid object with known actions.
                if (body.Length is > 2 and < 10000 &&
                    body.StartsWith("{", StringComparison.Ordinal) &&
                    body.EndsWith("}", StringComparison.Ordinal))
                {
                    try
                    {
                        using var rawJson = JsonDocument.Parse(body,
                            new JsonDocumentOptions { MaxDepth = 12 });
                        if (rawJson.RootElement.ValueKind == JsonValueKind.Object &&
                            rawJson.RootElement.TryGetProperty("actions", out _))
                            actions = ParsePlan(rawJson.RootElement);
                        else if (rawJson.RootElement.ValueKind == JsonValueKind.Object &&
                                 rawJson.RootElement.TryGetProperty("reply", out var rawReply) &&
                                 rawReply.ValueKind == JsonValueKind.String)
                            reply = rawReply.GetString();
                        else reply = body;
                    }
                    catch (JsonException) { reply = body; }
                }
                else reply = body;
            }
        }
        if (actions is not null)
        {
            // Last-resort safety guard for obviously non-action greetings.
            if (System.Text.RegularExpressions.Regex.IsMatch(
                history.LastOrDefault(x => x.Role == "user")?.Content?.Trim() ?? "",
                @"^(?:мартин[,!. ]*)?(?:привет|здравствуй|здравствуйте|добрый день|доброе утро|добрый вечер)[!. ]*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                yield return new AgentDelta(Text: "Привет!");
                yield break;
            }

            logger.LogInformation("LLM selected {Count} actions: {Actions}",
                actions.Count, string.Join(", ", actions.Select(a => a.Name)));
            var executed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in actions)
            {
                if (action.Name == "ha_control_named" &&
                    action.Args.TryGetProperty("scope", out var scope) &&
                    scope.GetString() == "all" &&
                    !System.Text.RegularExpressions.Regex.IsMatch(
                        history.LastOrDefault(x => x.Role == "user")?.Content ?? "",
                        @"\b(?:весь|все|всё|всю|всех|кажд\w*|полностью)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    yield return new AgentDelta(Tool: action.Name,
                        Result: new { error = "Групповая команда требует явного указания «все/весь»" });
                    continue;
                }
                string signature = action.Name + ":" + action.Args.GetRawText();
                if (!executed.Add(signature))
                {
                    yield return new AgentDelta(Tool: action.Name,
                        Result: new { error = "Повторное действие заблокировано" });
                    continue;
                }
                object result;
                object? media = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(
                        action.Name.StartsWith("music_", StringComparison.Ordinal) ? 45 : 20));
                    var output = await skills.ExecuteAsync(action.Name, action.Args, timeout.Token);
                    result = output.Result;
                    media = output.Media;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is InvalidOperationException or InvalidDataException or JsonException or
                        HttpRequestException or ArgumentException or TaskCanceledException or
                        NotSupportedException)
                {
                    logger.LogWarning(ex, "Action {Action} failed", action.Name);
                    result = new { error = ex.Message };
                }
                yield return new AgentDelta(Tool: action.Name, Result: result, Media: media);
            }
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(reply))
        {
            // An ordinary chat response never invokes services. Explicit
            // unsupported action claims are not treated as actual success.
            string last = history.LastOrDefault(x => x.Role == "user")?.Content ?? "";
            if (System.Text.RegularExpressions.Regex.IsMatch(last,
                    @"\b(?:включи|выключи|запусти|поставь|открой|закрой)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(reply,
                    @"\b(?:включил|включаю|выключил|выключаю|запустил|запускаю)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                yield return new AgentDelta(Text:
                    "Модель не вызвала инструмент. Действие не выполнено.");
                yield break;
            }
            yield return new AgentDelta(Text: NoEmoji.Strip(reply));
            yield break;
        }
        throw new InvalidDataException(
            "LLM не вернула текст или вызов инструмента. Проверьте --jinja и chat template модели.");
    }
}
