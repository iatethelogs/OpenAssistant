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

}
