using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
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
        bool valid = (OnOff.Contains(domain) && (service is "turn_on" or "turn_off")) ||
                     (domain == "cover" && (service is "open_cover" or "close_cover" or "stop_cover")) ||
                     (domain == "media_player" && (service is "media_play" or "media_pause"));
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


    private sealed record AreaInfo(string Id, string Name);

    private static string Normalize(string value)
    {
        value = value.ToLowerInvariant().Replace('ё', 'е');
        return string.Join(" ", Regex.Matches(value, @"[\p{L}\p{N}]+")
            .Select(x => x.Value));
    }

    private static bool SimilarName(string spoken, string actual)
    {
        string a = Normalize(spoken), b = Normalize(actual);
        if (a == b) return true;
        if (a.Length < 4 || b.Length < 4) return false;
        // Russian case endings: спальне / спальня, кухне / кухня.
        int prefix = Math.Min(5, Math.Min(a.Length, b.Length) - 1);
        return prefix >= 3 && a[..prefix] == b[..prefix] &&
            Math.Abs(a.Length - b.Length) <= 3;
    }

    /// <summary>Home Assistant area registry through a constant, read-only template.</summary>
    private async Task<AreaInfo[]> AreasAsync(CancellationToken ct)
    {
        const string template = "{% set ns = namespace(items=[]) %}" +
            "{% for id in areas() %}" +
            "{% set ns.items = ns.items + [{'id': id, 'name': area_name(id)}] %}" +
            "{% endfor %}{{ ns.items | to_json }}";
        string payload = await RenderTemplateAsync(template, ct);
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant не вернул список помещений");
        return json.RootElement.EnumerateArray().Take(200).Select(x =>
            new AreaInfo(x.GetProperty("id").GetString() ?? "",
                x.GetProperty("name").GetString() ?? ""))
            .Where(x => x.Id.Length > 0 && x.Name.Length > 0).ToArray();
    }

    private async Task<string> RenderTemplateAsync(string template, CancellationToken ct)
    {
        if (!Connected) throw new InvalidOperationException("Home Assistant не подключён");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = Client();
        using var response = await client.PostAsJsonAsync(Api + "/template",
            new { template }, timeout.Token);
        response.EnsureSuccessStatusCode();
        string result = await response.Content.ReadAsStringAsync(timeout.Token);
        if (result.Length > 120000) throw new InvalidDataException("Слишком большой ответ Home Assistant");
        return result;
    }

    private async Task<string[]> AreaEntityIdsAsync(string areaId, CancellationToken ct)
    {
        if (!Regex.IsMatch(areaId, @"^[a-zA-Z0-9_-]{1,100}$"))
            throw new InvalidDataException("Недопустимый ID помещения");
        string text = await RenderTemplateAsync(
            "{{ area_entities('" + areaId + "') | to_json }}", ct);
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant не вернул список устройств помещения");
        return json.RootElement.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => Regex.IsMatch(x, @"^[a-z_]+\.[a-z0-9_]+$"))
            .Distinct(StringComparer.Ordinal).Take(150).ToArray();
    }

    /// <summary>
    /// Resolve user-spoken names against actual HA areas/entities. Never trust
    /// model-supplied entity_id or area_id. For 'all lights in room', apply
    /// HA's own area targeting, then verify light states independently.
    /// </summary>
    public async Task<object> ControlNamedAsync(
        string target, string area, string scope, string service, CancellationToken ct)
    {
        target = (target ?? "").Trim();
        area = (area ?? "").Trim();
        if (target.Length > 100 || area.Length > 100 ||
            scope is not ("all" or "one") ||
            service is not ("turn_on" or "turn_off" or "open_cover" or "close_cover"))
            throw new ArgumentException("Недопустимые параметры управления умным домом");

        if (scope == "all")
        {
            if (area.Length == 0)
                return new { error = "Для групповой команды требуется название комнаты" };
            if (service is not ("turn_on" or "turn_off") ||
                !Regex.IsMatch(Normalize(target), @"(^| )(свет|освещен|ламп|люстр|светильник|подсвет)",
                    RegexOptions.IgnoreCase))
                return new { error = "Групповое управление разрешено только освещением указанной комнаты" };

            var areas = await AreasAsync(ct);
            var matches = areas.Where(x => SimilarName(area, x.Name)).ToArray();
            if (matches.Length != 1)
                return new { error = matches.Length == 0
                    ? "Помещение не найдено в реестре Home Assistant: " + area
                    : "Несколько помещений подходят под название: " + area };

            string areaId = matches[0].Id;
            var lights = (await AreaEntityIdsAsync(areaId, ct))
                .Where(x => x.StartsWith("light.", StringComparison.Ordinal)).ToArray();
            if (lights.Length == 0)
                return new { error = "В помещении нет светильников Home Assistant: " + matches[0].Name };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = Client();
            using var response = await client.PostAsJsonAsync(
                Api + "/services/light/" + service,
                new { area_id = areaId }, timeout.Token);
            response.EnsureSuccessStatusCode();

            string expected = service == "turn_off" ? "off" : "on";
            var verifiedCount = 0;
            var unknown = Array.Empty<string>();
            for (int n = 0; n < 3; n++)
            {
                if (n > 0) await Task.Delay(450, ct);
                var states = (await EntitiesAsync(ct)).ToDictionary(x => x.Id,
                    StringComparer.OrdinalIgnoreCase);
                var notConfirmed = lights.Where(id =>
                    !states.TryGetValue(id, out var state) ||
                    !state.State.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToArray();
                unknown = notConfirmed;
                verifiedCount = lights.Length - notConfirmed.Length;
                if (unknown.Length == 0) break;
            }
            return new {
                verified = unknown.Length == 0,
                friendly_name = matches[0].Name,
                action = service,
                entity_count = lights.Length,
                verified_count = verifiedCount,
                expected_state = expected,
                message = unknown.Length == 0
                    ? "Состояние всех светильников подтверждено"
                    : "Состояние части светильников не подтверждено: " +
                        string.Join(", ", unknown.Take(5))
            };
        }

        if (target.Length == 0)
            return new { error = "Не указано устройство Home Assistant" };

        var entities = await EntitiesAsync(ct);
        if (area.Length > 0)
        {
            var possible = (await AreasAsync(ct))
                .Where(x => SimilarName(area, x.Name)).ToArray();
            if (possible.Length != 1)
                return new { error = "Не удалось однозначно определить помещение: " + area };
            var ids = (await AreaEntityIdsAsync(possible[0].Id, ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            entities = entities.Where(x => ids.Contains(x.Id)).ToArray();
        }
        string name = Normalize(target);
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x is not ("в" or "на" or "мне" or "этот" or "эту" or "весь" or "все"))
            .ToArray();
        var candidates = entities.Where(x =>
            x.Id.Split('.')[0] is "light" or "switch" or "input_boolean" or "fan" or "cover" &&
            words.Length > 0 && words.All(w =>
                Normalize(x.Name + " " + x.Id.Replace('_', ' ')).Split(' ')
                    .Any(t => t == w || (w.Length >= 4 && t.StartsWith(w[..Math.Min(4,w.Length)], StringComparison.Ordinal)))))
            .ToArray();
        if (candidates.Length != 1)
            return new { error = candidates.Length == 0
                ? "Устройство не найдено в Home Assistant: " + target
                : "Неоднозначное название устройства: " +
                    string.Join(", ", candidates.Take(5).Select(x => x.Name)) };
        var selected = candidates[0];
        string domain = selected.Id.Split('.')[0];
        string operation = domain == "cover"
            ? service == "turn_on" ? "open_cover" :
              service == "turn_off" ? "close_cover" : service
            : service;
        return await ChangeAsync(selected.Id, domain, operation, ct);
    }

}
