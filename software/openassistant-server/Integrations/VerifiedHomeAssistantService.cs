using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Integrations;

/// <summary>Получает факты непосредственно из HA и подтверждает результат действий повторным чтением.</summary>
public sealed class VerifiedHomeAssistantService(IHttpClientFactory factory,
    IOptionsMonitor<ServerOptions> options)
{
    public sealed record Entity(string Id, string Name, string State, string? Unit);
    private static readonly HashSet<string> Controllable = [
        "light", "switch", "input_boolean", "fan", "cover", "media_player"
    ];
    private static readonly HashSet<string> OnOff = ["light", "switch", "input_boolean", "fan"];
    private static readonly Regex EntityId = new(@"\b[a-z_]+\.[a-z0-9_]+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HomeTopic = new(
        @"свет|ламп|люстр|розет|выключател|штор|жалюзи|вентилятор|датчик|термостат|температур|градус|home assistant|умн.{0,5}дом|\b(?:light|switch|sensor|fan|cover)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex QueryIntent = new(
        @"включ|выключ|откро|откры|закро|закры|состояни|статус|горит|работает|температур|градус|какие|сколько|покажи|проверь|\b(on|off)\b|включ[её]н|выключ[её]н|управ",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommandOn = new(@"\bвключ\w*|\bзажги\w*|\bоткро\w*|\bоткры\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommandOff = new(@"\bвыключ\w*|\bпогаси\w*|\bзакро\w*|\bзакры\w*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public bool Connected => !string.IsNullOrWhiteSpace(options.CurrentValue.HomeAssistantUrl) &&
                             !string.IsNullOrWhiteSpace(options.CurrentValue.HomeAssistantToken);

    private HttpClient Client()
    {
        var client = factory.CreateClient("homeassistant");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", options.CurrentValue.HomeAssistantToken);
        return client;
    }
    private string Api => options.CurrentValue.HomeAssistantUrl.TrimEnd('/') + "/api";

    public async Task<IReadOnlyList<Entity>> EntitiesAsync(CancellationToken ct)
    {
        if (!Connected) throw new InvalidOperationException("Home Assistant не подключён");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = Client();
        using var response = await client.GetAsync(Api + "/states", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 24 }, timeout.Token);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant вернул неожиданный ответ");
        var entities = new List<Entity>();
        foreach (var item in json.RootElement.EnumerateArray().Take(3000))
        {
            if (!item.TryGetProperty("entity_id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("state", out var stateElement) ||
                stateElement.ValueKind != JsonValueKind.String) continue;
            string id = idElement.GetString() ?? "";
            string state = stateElement.GetString() ?? "";
            if (!EntityId.IsMatch(id) || state.Length > 100) continue;
            string name = id;
            string? unit = null;
            if (item.TryGetProperty("attributes", out var attr) && attr.ValueKind == JsonValueKind.Object)
            {
                if (attr.TryGetProperty("friendly_name", out var friendly) &&
                    friendly.ValueKind == JsonValueKind.String)
                    name = (friendly.GetString() ?? id)[..Math.Min((friendly.GetString() ?? id).Length, 120)];
                if (attr.TryGetProperty("unit_of_measurement", out var unitProp) &&
                    unitProp.ValueKind == JsonValueKind.String)
                    unit = (unitProp.GetString() ?? "")[..Math.Min((unitProp.GetString() ?? "").Length, 20)];
            }
            entities.Add(new Entity(id, name, state, unit));
        }
        return entities;
    }
    public async Task<Entity> GetAsync(string id, CancellationToken ct)
    {
        if (!EntityId.IsMatch(id) || EntityId.Match(id).Value != id)
            throw new ArgumentException("Некорректный ID устройства");
        var entities = await EntitiesAsync(ct);
        return entities.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Устройство отсутствует в Home Assistant");
    }

    /// <summary>Возвращает наблюдаемое состояние, а не «ok» на основании HTTP 200.</summary>
    public async Task<object> ChangeAsync(string id, string domain, string service, CancellationToken ct)
    {
        if (!Controllable.Contains(domain) || !id.StartsWith(domain + ".", StringComparison.Ordinal) ||
            !Regex.IsMatch(id, @"^[a-z_]+\.[a-z0-9_]+$"))
            throw new ArgumentException("Недопустимая команда Home Assistant");
        bool valid = OnOff.Contains(domain) && service is "turn_on" or "turn_off" ||
                     domain == "cover" && service is "open_cover" or "close_cover" or "stop_cover" ||
                     domain == "media_player" && service is "media_play" or "media_pause";
        if (!valid) throw new ArgumentException("Команда не разрешена для устройства");
        var before = await GetAsync(id, ct);
        if (before.State is "unavailable" or "unknown")
            return new { verified = false, entity_id = id, state = before.State, error = "Устройство недоступно" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = Client();
        using var response = await client.PostAsJsonAsync(
            Api + "/services/" + domain + "/" + service,
            new { entity_id = id }, timeout.Token);
        response.EnsureSuccessStatusCode();
        // Получение нового состояния отделено от ответа POST. Подтверждаем только наблюдаемый эффект.
        string? expected = service switch {
            "turn_on" or "open_cover" or "media_play" => service == "open_cover" ? "open" :
                service == "media_play" ? "playing" : "on",
            "turn_off" or "close_cover" or "media_pause" => service == "close_cover" ? "closed" :
                service == "media_pause" ? "paused" : "off",
            _ => null
        };
        Entity after = before;
        for (int n = 0; n < 3; n++)
        {
            if (n > 0) await Task.Delay(450, ct);
            after = await GetAsync(id, ct);
            if (expected is null || after.State.Equals(expected, StringComparison.OrdinalIgnoreCase)) break;
        }
        bool verified = expected is not null && after.State.Equals(expected, StringComparison.OrdinalIgnoreCase);
        return new {
            verified, entity_id = id, friendly_name = after.Name,
            observed_state = after.State, expected_state = expected,
            message = verified ? "Состояние подтверждено повторным чтением" :
                "Команда отправлена, но ожидаемое состояние не подтверждено"
        };
    }

    private static string[] Tokens(string source) => Regex.Matches(source.ToLowerInvariant(), @"[\p{L}\p{N}]{4,}")
        .Select(x => x.Value).Where(x => !new [] {
            "свет", "света", "лампа", "лампы", "включ", "выключ", "состояние",
            "датчик", "какая", "какое", "сейчас", "работает", "температура",
            "turn", "switch", "light", "sensor", "input", "boolean"
        }.Contains(x)).Distinct().ToArray();

    private static List<Entity> FindEntities(string input, IReadOnlyList<Entity> entities)
    {
        var explicitId = EntityId.Match(input.ToLowerInvariant());
        if (explicitId.Success) return entities.Where(e => e.Id == explicitId.Value).ToList();
        string? domain =
            Regex.IsMatch(input, @"свет|ламп|люстр", RegexOptions.IgnoreCase) ? "light" :
            Regex.IsMatch(input, @"розет|выключател", RegexOptions.IgnoreCase) ? "switch" :
            Regex.IsMatch(input, @"штор|жалюзи", RegexOptions.IgnoreCase) ? "cover" :
            Regex.IsMatch(input, @"вентилятор", RegexOptions.IgnoreCase) ? "fan" :
            Regex.IsMatch(input, @"температур|градус", RegexOptions.IgnoreCase) ? "sensor" : null;
        var candidates = entities.Where(e => domain is null ||
            e.Id.StartsWith(domain + ".", StringComparison.Ordinal)).ToList();
        var terms = Tokens(input);
        var ranked = candidates.Select(e => new {
            Entity = e,
            Score = terms.Count(w => Tokens(e.Name.Replace('_',' ')).Concat(Tokens(e.Id.Replace('_',' ')))
                .Any(t => t.StartsWith(w[..Math.Min(4,w.Length)], StringComparison.Ordinal)))
        }).OrderByDescending(x => x.Score).ToArray();
        if (ranked.Length == 0) return [];
        if (ranked[0].Score == 0) return ranked.Length == 1 ? [ranked[0].Entity] : [];
        return ranked.Where(x => x.Score == ranked[0].Score).Select(x => x.Entity).Take(10).ToList();
    }

    /// <summary>Перехватывает вопросы о состоянии и прямые команды; модель не формирует фактический ответ.</summary>
    public async Task<string?> TryHandleAsync(string text, bool canWrite, CancellationToken ct)
    {
        if (!HomeTopic.IsMatch(text) || !QueryIntent.IsMatch(text)) return null;
        if (!Connected) return "Home Assistant не подключён. Добавьте адрес и токен в разделе интеграций.";
        try
        {
            var entities = await EntitiesAsync(ct);
            bool on = CommandOn.IsMatch(text), off = CommandOff.IsMatch(text);
            bool command = on ^ off && !Regex.IsMatch(text,
                @"\bкак\s+(включ|выключ|откро|закро)|\b(если|можно ли|умеешь ли)\b",
                RegexOptions.IgnoreCase);
            if (Regex.IsMatch(text, @"какие\s+(устройств|ламп|светильник|датчик)", RegexOptions.IgnoreCase))
            {
                return entities.Count == 0 ? "Устройств не найдено." :
                    "Устройства Home Assistant: " + string.Join("; ",
                        entities.Take(25).Select(e => $"{e.Name} ({e.State}{e.Unit})")) +
                        (entities.Count > 25 ? ". Показаны первые 25." : ".");
            }
            var matches = FindEntities(text, entities);
            if (matches.Count == 0) return "Не удалось однозначно определить устройство в Home Assistant. Назовите его точнее.";
            if (matches.Count > 1) return "Уточните устройство: " +
                string.Join(", ", matches.Take(6).Select(e => e.Name + " (" + e.Id + ")")) + ".";
            var entity = matches[0];
            if (!command)
                return $"По данным Home Assistant на момент запроса: {entity.Name} — {entity.State}{(string.IsNullOrWhiteSpace(entity.Unit) ? "" : " " + entity.Unit)}.";
            if (!canWrite)
                return "Управление Home Assistant отключено. Разрешите действие в разделе интеграций.";
            string domain = entity.Id.Split('.')[0];
            string service = domain switch {
                "cover" => on ? "open_cover" : "close_cover",
                "media_player" => on ? "media_play" : "media_pause",
                _ => on ? "turn_on" : "turn_off"
            };
            var result = await ChangeAsync(entity.Id, domain, service, ct);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var root = document.RootElement;
            bool verified = root.GetProperty("verified").GetBoolean();
            string state = root.TryGetProperty("observed_state", out var observed) ? observed.GetString() ?? "?" :
                root.TryGetProperty("state", out var fallback) ? fallback.GetString() ?? "?" : "?";
            return verified
                ? $"{entity.Name}: состояние «{state}» подтверждено Home Assistant."
                : $"{entity.Name}: ожидаемое состояние не подтверждено. Последнее наблюдение: «{state}».";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
            JsonException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return "Не удалось получить подтверждённое состояние Home Assistant: " + ex.Message;
        }
    }
}
