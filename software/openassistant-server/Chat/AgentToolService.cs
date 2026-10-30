using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

public sealed record SkillDefinition(string Name, string Title, string Description, bool Enabled, bool Sensitive, object Parameters);
public sealed record AgentAction(object Result, object? Media = null);

/// <summary>Разрешённые навыки LLM. Вызовы ограничены по времени, аргументам и списку инструментов.</summary>
public sealed class AgentToolService(IWebHostEnvironment env, RadioBrowserService radio,
    YandexMusicService music, HomeAssistantTool ha)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(env.ContentRootPath, "agent-tools.local.json");
    private static object One(string key, string description) => new {
        type = "object",
        properties = new Dictionary<string, object> { [key] = new { type = "string", description } },
        required = new[] { key }, additionalProperties = false
    };
    private static readonly SkillDefinition[] Catalog = [
        new("radio_search", "Поиск радио", "Найди радиостанцию по названию или жанру, верни список.", true, false, One("query", "Поисковый запрос")),
        new("radio_play", "Включить радио", "Найди станцию и начни воспроизведение на устройстве.", true, false, One("query", "Название станции")),
        new("music_search", "Поиск Яндекс Музыки", "Найди треки и верни их ID для последующего воспроизведения.", true, false, One("query", "Название или исполнитель")),
        new("music_play", "Включить Яндекс Музыку", "Включи композицию по ID из music_search.", true, false, One("track_id", "ID трека из music_search")),
        new("ha_get_state", "Состояние устройства", "Узнай состояние конкретного entity_id Home Assistant.", true, false, One("entity_id", "Например light.kitchen")),
        new("ha_call_service", "Управление Home Assistant", "Выполни явно запрошенное пользователем действие с устройством. Доступно только после включения администратором.", false, true,
            new { type = "object", properties = new {
                domain = new { type = "string", description = "Домен, например light" },
                service = new { type = "string", description = "Например turn_on" },
                entity_id = new { type = "string", description = "Например light.kitchen" }
            }, required = new[] { "domain", "service", "entity_id" }, additionalProperties = false })
    ];

    private async Task<Dictionary<string,bool>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath, ct));
        var output = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    output[property.Name] = property.Value.GetBoolean();
        return output;
    }
    public async Task<SkillDefinition[]> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await ReadAsync(ct);
            return Catalog.Select(x => x with { Enabled = saved.TryGetValue(x.Name, out var enabled) ? enabled : x.Enabled }).ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task<SkillDefinition[]> SaveAsync(Dictionary<string,bool> enabled, CancellationToken ct)
    {
        if (enabled is null || enabled.Keys.Any(x => Catalog.All(c => c.Name != x)))
            throw new ArgumentException("Неизвестный инструмент");
        await _gate.WaitAsync(ct);
        try
        {
            var saved = await ReadAsync(ct);
            foreach (var pair in enabled) saved[pair.Key] = pair.Value;
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }), ct);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, FilePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
        return await ListAsync(ct);
    }

    public async Task<AgentAction> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        var skills = await ListAsync(ct);
        if (!skills.Any(s => s.Name == name && s.Enabled))
            throw new InvalidOperationException("Инструмент отключён: " + name);
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Нужен объект параметров");
        static string Arg(JsonElement args, string key)
        {
            if (!args.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Отсутствует параметр " + key);
            string s = v.GetString()?.Trim() ?? "";
            if (s.Length is < 1 or > 150) throw new ArgumentException("Неверная длина " + key);
            return s;
        }
        switch (name)
        {
            case "radio_search":
            case "radio_play":
            {
                var data = JsonSerializer.SerializeToElement(await radio.SearchAsync(Arg(args, "query"), ct));
                if (name == "radio_search")
                    return new AgentAction(data.EnumerateArray().Take(6).Select(x => new {
                        name = x.GetProperty("name").GetString(),
                        country = x.GetProperty("country").GetString()
                    }).ToArray());
                if (data.GetArrayLength() == 0) return new AgentAction(new { error = "Станции не найдены" });
                var first = data[0];
                string url = first.GetProperty("url").GetString() ?? "";
                string title = first.GetProperty("name").GetString() ?? "Радио";
                return new AgentAction(new { playing = title }, new { type = "play_media", url, title, source = "radio_browser" });
            }
            case "music_search":
            {
                var found = await music.SearchAsync(Arg(args, "query"), ct);
                return new AgentAction(found.Take(8).ToArray());
            }
            case "music_play":
            {
                string id = Arg(args, "track_id");
                if (!Regex.IsMatch(id, @"^\d+(?::\d+)?$")) throw new ArgumentException("Некорректный ID трека");
                var playable = await music.ResolveAsync(id, ct);
                return new AgentAction(new { playing = id, codec = playable.Codec },
                    new { type = "play_media", url = playable.Url, title = "Яндекс Музыка", source = "yandex_music",
                        codec = playable.Codec, bitrateKbps = playable.BitrateKbps });
            }
            case "ha_get_state":
            {
                string entity = Arg(args, "entity_id");
                if (!Regex.IsMatch(entity, @"^[a-z_]+\.[a-z0-9_]+$")) throw new ArgumentException("Некорректный entity_id");
                return new AgentAction(await ha.ExecuteAsync(JsonSerializer.SerializeToElement(new {
                    action = "get_state", entity_id = entity
                }), ct));
            }
            case "ha_call_service":
            {
                string domain = Arg(args, "domain"), service = Arg(args, "service"), entity = Arg(args, "entity_id");
                if (!Regex.IsMatch(entity, @"^[a-z_]+\.[a-z0-9_]+$") ||
                    !Regex.IsMatch(domain, @"^[a-z_]+$") || !Regex.IsMatch(service, @"^[a-z_]+$"))
                    throw new ArgumentException("Некорректные параметры HA");
                if (domain is not ("light" or "switch" or "input_boolean" or "fan" or "cover" or "media_player") ||
                    service is not ("turn_on" or "turn_off" or "toggle" or "open_cover" or "close_cover" or "stop_cover" or "media_play" or "media_pause"))
                    throw new ArgumentException("Этот тип команды Home Assistant запрещён");
                if (!entity.StartsWith(domain + ".", StringComparison.Ordinal))
                    throw new ArgumentException("Домен и entity_id не совпадают");
                return new AgentAction(await ha.ExecuteAsync(JsonSerializer.SerializeToElement(new {
                    action = "call_service", domain, service, data = new { entity_id = entity }
                }), ct));
            }
            default: throw new ArgumentException("Неизвестный инструмент");
        }
    }
}
