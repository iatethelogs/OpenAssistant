using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
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

    private static async Task<JsonElement> WsReadAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var stream = new MemoryStream();
        WebSocketReceiveResult part;
        do
        {
            part = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (part.MessageType == WebSocketMessageType.Close)
                throw new IOException("Home Assistant закрыл WebSocket");
            if (stream.Length + part.Count > 2_000_000)
                throw new InvalidDataException("Слишком большой ответ Home Assistant");
            stream.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }

    private static Task WsWriteAsync(ClientWebSocket ws, object command, CancellationToken ct)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(command);
        return ws.SendAsync(new ArraySegment<byte>(data),
            WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> WsResultAsync(
        ClientWebSocket ws, int id, object command, CancellationToken ct)
    {
        await WsWriteAsync(ws, command, ct);
        for (int attempt = 0; attempt < 15; attempt++)
        {
            var packet = await WsReadAsync(ws, ct);
            if (!packet.TryGetProperty("type", out var type) ||
                type.GetString() != "result" ||
                !packet.TryGetProperty("id", out var found) ||
                !found.TryGetInt32(out int actual) || actual != id) continue;
            if (!packet.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True)
            {
                string detail = packet.TryGetProperty("error", out var error)
                    ? error.ToString() : "Ошибка Home Assistant";
                throw new InvalidOperationException("Home Assistant: " +
                    detail[..Math.Min(detail.Length, 230)]);
            }
            return packet.TryGetProperty("result", out var result)
                ? result.Clone() : JsonSerializer.SerializeToElement(new { });
        }
        throw new TimeoutException("Home Assistant не ответил на WebSocket-команду");
    }

    private async Task<ClientWebSocket> ConnectRegistryAsync(CancellationToken ct)
    {
        if (!Connected) throw new InvalidOperationException("Home Assistant не подключён");
        var baseUri = new Uri(options.CurrentValue.HomeAssistantUrl.TrimEnd('/') + "/api/websocket");
        var builder = new UriBuilder(baseUri) {
            Scheme = baseUri.Scheme == "https" ? "wss" : "ws"
        };
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(builder.Uri, ct);
            var hello = await WsReadAsync(ws, ct);
            if (!hello.TryGetProperty("type", out var type) ||
                type.GetString() != "auth_required")
                throw new InvalidDataException("Home Assistant не запросил авторизацию");
            await WsWriteAsync(ws, new {
                type = "auth",
                access_token = options.CurrentValue.HomeAssistantToken
            }, ct);
            var authenticated = await WsReadAsync(ws, ct);
            if (!authenticated.TryGetProperty("type", out var state) ||
                state.GetString() != "auth_ok")
                throw new InvalidOperationException("Авторизация Home Assistant WebSocket отклонена");
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static string NormalizeAreaName(string area) =>
        string.Join(" ", Regex.Matches(
            area.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+")
            .Select(x => x.Value));

    private static int AreaDistance(string left, string right)
    {
        if (left == right) return 0;
        int[] prev = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] next = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            next[0] = i;
            for (int j = 1; j <= right.Length; j++)
                next[j] = Math.Min(Math.Min(prev[j] + 1, next[j - 1] + 1),
                    prev[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (prev, next) = (next, prev);
        }
        return prev[right.Length];
    }

    /// <summary>
    /// Resolve a human room name using the real HA area registry. Expand the
    /// target to real light entities, call light.turn_on/off on the AREA and
    /// confirm each observed light state. No invented area IDs are accepted.
    /// </summary>
    public async Task<object> ChangeAreaLightsAsync(
        string areaName, string service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(areaName) || areaName.Length > 100)
            throw new ArgumentException("Название комнаты должно быть от 1 до 100 символов");
        if (service is not ("turn_on" or "turn_off"))
            throw new ArgumentException("Допускается только включение/выключение света");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var ws = await ConnectRegistryAsync(timeout.Token);

        var registry = await WsResultAsync(ws, 1,
            new { id = 1, type = "config/area_registry/list" }, timeout.Token);
        if (registry.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant не вернул список помещений");

        string requested = NormalizeAreaName(areaName);
        var candidates = registry.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new {
                Id = x.TryGetProperty("area_id", out var a) ? a.GetString() ?? "" : "",
                Name = x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""
            })
            .Where(x => x.Id.Length > 0 && x.Name.Length > 0)
            .Select(x => new { x.Id, x.Name,
                Distance = AreaDistance(requested, NormalizeAreaName(x.Name)) })
            .OrderBy(x => x.Distance).ToArray();
        if (candidates.Length == 0 ||
            candidates[0].Distance > (requested.Length >= 5 ? 2 : 0))
            throw new InvalidOperationException("Помещение не найдено в Home Assistant: " + areaName);
        if (candidates.Length > 1 && candidates[1].Distance == candidates[0].Distance)
            throw new InvalidOperationException("Найдено несколько похожих помещений; уточните название");
        var selected = candidates[0];

        var target = await WsResultAsync(ws, 2, new {
            id = 2, type = "extract_from_target",
            target = new { area_id = new[] { selected.Id } },
            expand_group = true
        }, timeout.Token);
        if (!target.TryGetProperty("referenced_entities", out var entities) ||
            entities.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant не вернул сущности помещения");
        var lights = entities.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "")
            .Where(x => Regex.IsMatch(x, @"^light\.[a-z0-9_]+$"))
            .Distinct(StringComparer.Ordinal).Take(100).ToArray();
        if (lights.Length == 0)
            throw new InvalidOperationException("В помещении «" + selected.Name + "» не найдено светильников");

        await WsResultAsync(ws, 3, new {
            id = 3, type = "call_service", domain = "light", service,
            target = new { area_id = selected.Id }
        }, timeout.Token);

        string expected = service == "turn_on" ? "on" : "off";
        int confirmed = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(400, timeout.Token);
            var states = await EntitiesAsync(timeout.Token);
            var byId = states.ToDictionary(x => x.Id, x => x.State, StringComparer.Ordinal);
            confirmed = lights.Count(id => byId.TryGetValue(id, out var state) &&
                state.Equals(expected, StringComparison.OrdinalIgnoreCase));
            if (confirmed == lights.Length) break;
        }
        return new {
            verified = confirmed == lights.Length,
            area = selected.Name, area_id = selected.Id,
            expected_state = expected, confirmed,
            total = lights.Length,
            message = confirmed == lights.Length ? "Весь свет подтверждён" :
                "Команда отправлена, но не все светильники подтвердили состояние"
        };
    }


}
