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
        new("music_play", "Включить Яндекс Музыку", "Включи композицию по ID из music_search и продолжай похожими треками.", true, false, One("track_id", "ID трека из music_search")),
        new("music_play_query", "Включить музыку: исполнитель или композиция",
            "Запускает Яндекс Музыку на колонке. Используй при просьбах включить музыку, исполнителя, группу или песню. "
            + "Примеры: «включи группу Кино» -> query='Кино', kind='artist'; «поставь Queen» -> query='Queen', kind='artist'; "
            + "«включи песню Звезда по имени Солнце» -> query='Звезда по имени Солнце', kind='track'. "
            + "Не вызывай для света, ламп, розеток, телевизоров, Home Assistant или других устройств.", true, false,
            new { type = "object", properties = new {
                query = new { type = "string", description = "Название музыки, исполнителя либо группы, без слов «включи», «группу» и т. п." },
                kind = new { type = "string", @enum = new[] { "artist", "track", "auto" },
                    description = "artist = исполнитель/группа; track = конкретная песня; auto = общий поиск по каталогу." }
            }, required = new[] { "query", "kind" }, additionalProperties = false }),
        new("music_play_liked", "Включить понравившиеся композиции", "Включи из Яндекс Музыки плейлист «Мне нравится» пользователя с автопереходом на следующую песню.", true, false,
            new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        new("music_list_playlists", "Список плейлистов Яндекс Музыки", "Получить названия и идентификаторы плейлистов авторизованного пользователя.", true, false,
            new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        new("music_play_playlist", "Включить плейлист Яндекс Музыки", "Включить плейлист пользователя по ID из music_list_playlists и продолжать воспроизведение.", true, false, One("kind", "Идентификатор плейлиста")),
        new("music_control", "Управление музыкальным плеером", "Пауза, продолжение, следующий, предыдущий, остановка или статус очереди на текущей колонке.", true, false,
            new { type = "object", properties = new {
                action = new { type = "string", @enum = new[] { "pause", "resume", "next", "previous", "stop", "status" },
                    description = "pause: пауза; resume: продолжить; next: следующий; previous: предыдущий; stop: остановить; status: состояние" }
            }, required = new[] { "action" }, additionalProperties = false }),
        new("ha_list_entities", "Устройства Home Assistant", "Найди реальные устройства и их состояния без угадывания идентификаторов.", true, false,
            new { type = "object", properties = new {
                filter = new { type = "string", description = "Часть имени или entity_id, можно оставить пустым" }
            }, required = new[] { "filter" }, additionalProperties = false }),
        new("ha_get_state", "Состояние устройства", "Узнай состояние конкретного entity_id Home Assistant.", true, false, One("entity_id", "Например light.kitchen")),
        new("ha_call_service", "Управление Home Assistant", "Выполняй только явно запрошенные пользователем действия над устройством Home Assistant; для информационных вопросов используй чтение состояния. Управление разрешается отдельно в настройках.", false, true,
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
            case "music_play_query":
            {
                string kind = args.TryGetProperty("kind", out var node) &&
                    node.ValueKind == JsonValueKind.String ? node.GetString() ?? "auto" : "auto";
                var selection = await music.SelectAsync(Arg(args, "query"), kind, ct);
                if (selection.Tracks.Length == 0)
                    return new AgentAction(new { error = "В каталоге Яндекс Музыки ничего не найдено",
                        query = selection.Name, kind = selection.Kind });
                return new AgentAction(new { playing = selection.Name, kind = selection.Kind,
                    firstTrack = selection.Tracks[0].Title, queued = selection.Tracks.Length },
                    new { type = "music_queue", tracks = selection.Tracks });
            }
            case "music_play":
            {
                string id = Arg(args, "track_id");
                if (!Regex.IsMatch(id, @"^\d+(?::\d+)?$")) throw new ArgumentException("Некорректный ID трека");
                var similar = await music.SimilarAsync(id, ct);
                var selected = similar.FirstOrDefault(t => t.Id == id) ??
                    new YandexTrack(id, "Выбранный трек", []);
                var queue = new[] { selected }.Concat(similar.Where(t => t.Id != id))
                    .DistinctBy(t => t.Id).Take(50).ToArray();
                return new AgentAction(new { playing = selected.Title, queued = queue.Length },
                    new { type = "music_queue", tracks = queue });
            }
            case "music_play_liked":
            {
                var liked = await music.LikedTracksAsync(ct);
                if (liked.Length == 0) return new AgentAction(new { error = "Нет понравившихся треков" });
                return new AgentAction(new { playing = "Мне нравится", queued = liked.Length },
                    new { type = "music_queue", tracks = liked });
            }
            case "music_list_playlists":
                return new AgentAction(await music.PlaylistsAsync(ct));
            case "music_play_playlist":
            {
                string kind = Arg(args, "kind");
                if (!Regex.IsMatch(kind, @"^\d{1,12}$")) throw new ArgumentException("Некорректный ID плейлиста");
                var tracks = await music.PlaylistTracksAsync(kind, ct);
                if (tracks.Length == 0) return new AgentAction(new { error = "Плейлист пуст" });
                return new AgentAction(new { playing = "Плейлист", queued = tracks.Length },
                    new { type = "music_queue", tracks });
            }
            case "music_control":
            {
                string action = Arg(args, "action");
                if (action is not ("pause" or "resume" or "next" or "previous" or "stop" or "status"))
                    throw new ArgumentException("Неизвестная команда музыки");
                return new AgentAction(new { requested = action },
                    new { type = "music_control", action });
            }
            case "ha_list_entities":
            {
                var items = (VerifiedHomeAssistantService.Entity[])await ha.ExecuteAsync(
                    JsonSerializer.SerializeToElement(new { action = "list_states" }), ct);
                string filter = args.TryGetProperty("filter", out var value) &&
                    value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
                return new AgentAction(items.Where(e => filter.Length == 0 ||
                    e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    e.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(40).ToArray());
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
                // The LLM chooses the operation; this executor validates the
                // explicit per-tool permission, domain, service and entity ID.
                // No natural-language keyword checks run after a tool call.
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
