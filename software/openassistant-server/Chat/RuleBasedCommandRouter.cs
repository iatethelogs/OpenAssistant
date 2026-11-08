using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

/// <summary>
/// Routes explicit commands by their structure, never by a hard-coded title,
/// artist, device name, or conversation history. Actual entities and music
/// results are looked up in their respective service catalogs.
/// </summary>
public sealed class RuleBasedCommandRouter(
    VerifiedHomeAssistantService home,
    ILogger<RuleBasedCommandRouter> logger)
{
    public sealed record Decision(string Name, JsonElement Arguments, string? Error = null);

    private const RegexOptions Rx = RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(80);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    private static Match Match(string value, string expression) =>
        Regex.Match(value, expression, Rx, RegexTimeout);

    private static Decision Call(string name, object args) =>
        new(name, JsonSerializer.SerializeToElement(args));
    private static Decision Fail(string message) => new("", Empty, message);

    private static string Clean(string input)
    {
        string text = Regex.Replace((input ?? "").Trim(), @"\s+", " ");
        text = Regex.Replace(text,
            @"^(?:(?:мартин|слушай|эй|ну|пожалуйста)\s*[,!.?]*\s+)+",
            "", Rx, RegexTimeout);
        return text.Trim(' ', '\t', '\r', '\n', '"', '«', '»', '.', '?', '!');
    }

    private static readonly HashSet<string> StopWords = [
        "в", "во", "на", "у", "мне", "пожалуйста", "мой", "моя", "мою",
        "моем", "моей", "моем", "этот", "эта", "эту", "тот", "ту",
        "комнате", "комнату", "комнаты", "комнатой", "дома", "дом",
        "здесь", "сейчас"
    ];

    private static string Stem(string word)
    {
        word = word.ToLowerInvariant().Replace('ё', 'е');
        // Only lexical normalization for matching real HA friendly_name values,
        // not a dictionary of particular device names or music.
        foreach (string suffix in new[] {
            "иями", "ями", "ами", "ого", "ему", "ому", "ыми", "ими",
            "ая", "яя", "ую", "юю", "ый", "ий", "ой", "ые", "ые",
            "ом", "ем", "ой", "их", "ах", "ях", "ов", "ам", "ям",
            "ы", "и", "е", "у", "ю", "а", "я", "ь"
        })
        {
            if (word.Length > suffix.Length + 3 &&
                word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        }
        return word;
    }

    private static string[] Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant().Replace('ё', 'е'),
            @"[\p{L}\p{N}]+", Rx, RegexTimeout)
            .Select(m => m.Value)
            .Where(w => !StopWords.Contains(w) && w.Length > 1)
            .Select(Stem).Where(w => w.Length >= 2).Distinct().ToArray();

    private static readonly Dictionary<string, string[]> DeviceTypes = new()
    {
        ["light"] = ["свет", "лампа", "лампу", "лампочку", "люстра",
            "подсветка", "торшер", "светильник"],
        ["switch"] = ["розетка", "выключатель", "реле"],
        ["cover"] = ["штора", "шторы", "жалюзи"],
        ["fan"] = ["вентилятор"],
        ["media_player"] = ["телевизор", "тв", "телик"],
        ["appliance"] = ["кондиционер", "чайник", "обогреватель",
            "увлажнитель", "пылесос", "гирлянда"]
    };

    private static string? DeviceHint(string target)
    {
        var tokens = Tokens(target);
        foreach (var group in DeviceTypes)
        {
            if (tokens.Any(t => group.Value.Any(word => Stem(word) == t)))
                return group.Key;
        }
        return null;
    }

    private static string[] TargetFeatures(string target) =>
        Tokens(target).Where(t => !DeviceTypes.Values.SelectMany(x => x)
            .Any(word => Stem(word) == t)).ToArray();

    private static Decision Music(string query, string kind = "auto")
    {
        query = query.Trim(' ', '"', '«', '»', '.', ',', '!', '?');
        if (query.Length == 0)
            return Fail("Уточните, что включить: исполнителя или композицию.");
        if (query.Length > 120)
            return Fail("Название музыки слишком длинное.");
        return Call("music_play_query", new { query, kind });
    }

    public async Task<Decision?> RouteAsync(string utterance, CancellationToken ct)
    {
        // Intent is decided from the latest utterance ONLY. This prevents a
        // previous chat about music from turning "привет" into playback.
        string text = Clean(utterance);
        if (text.Length is < 2 or > 200) return null;

        // Standalone playback commands.
        if (Match(text, @"^(?:пауза|поставь(?:\s+музыку)?\s+на\s+паузу|приостанови(?:\s+(?:музыку|трек|воспроизведение))?)$").Success)
            return Call("music_control", new { action = "pause" });
        if (Match(text, @"^(?:продолжи|возобнови)(?:\s+(?:музыку|воспроизведение))?$").Success)
            return Call("music_control", new { action = "resume" });
        if (Match(text, @"^(?:(?:следующий|дальше|следующую)(?:\s+(?:трек|песню|композицию))?|переключи\s+(?:на\s+)?следующий(?:\s+трек)?)$").Success)
            return Call("music_control", new { action = "next" });
        if (Match(text, @"^(?:(?:предыдущий|предыдущую)(?:\s+(?:трек|песню|композицию))?|переключи\s+на\s+предыдущий(?:\s+трек)?)$").Success)
            return Call("music_control", new { action = "previous" });
        if (Match(text, @"^(?:стоп|останови|выключи)(?:\s+(?:музыку|песню|трек|воспроизведение|радио))?$").Success)
            return Call("music_control", new { action = "stop" });
        if (Match(text, @"^(?:что|какая\s+песня|какой\s+трек)\s+(?:сейчас\s+)?играет$").Success)
            return Call("music_control", new { action = "status" });

        var verb = Match(text,
            @"^(?<verb>включи(?:те)?|поставь|поставьте|запусти(?:те)?|сыграй|проиграй|воспроизведи(?:те)?|выключи(?:те)?|отключи(?:те)?|погаси(?:те)?|закрой(?:те)?|открой(?:те)?)\s+(?:мне\s+)?(?<target>.+)$");
        if (!verb.Success) return null;

        string action = verb.Groups["verb"].Value.ToLowerInvariant();
        string target = verb.Groups["target"].Value.Trim();
        bool activate = action.StartsWith("включ", StringComparison.Ordinal) ||
            action.StartsWith("постав", StringComparison.Ordinal) ||
            action.StartsWith("запуст", StringComparison.Ordinal) ||
            action.StartsWith("сыгр", StringComparison.Ordinal) ||
            action.StartsWith("проигр", StringComparison.Ordinal) ||
            action.StartsWith("воспроиз", StringComparison.Ordinal);
        string homeService = action.StartsWith("открой", StringComparison.Ordinal) ? "open_cover" :
            action.StartsWith("закрой", StringComparison.Ordinal) ? "close_cover" :
            activate ? "turn_on" : "turn_off";

        // Explicit radio and explicit music requests never go to Home Assistant.
        var radio = Match(target, @"^(?:радио|радиостанцию)(?:\s+(?<query>.+))?$");
        if (radio.Success)
        {
            string station = radio.Groups["query"].Value.Trim();
            if (!activate) return Call("music_control", new { action = "stop" });
            return station.Length == 0
                ? Fail("Назовите радиостанцию, которую нужно включить.")
                : Call("radio_play", new { query = station });
        }

        if (Match(target, @"^(?:музыку|воспроизведение)$").Success)
            return Call("music_control", new { action = activate ? "resume" : "stop" });
        if (activate && Match(target,
            @"^(?:мои|свои)?\s*(?:любимые|понравившиеся)(?:\s+(?:треки|песни|композиции))?$").Success)
            return Call("music_play_liked", new { });

        var artist = Match(target,
            @"^(?:группу|исполнителя|исполнительницу|артиста|певца|певицу|песни\s+(?:группы|исполнителя))\s+(?<query>.+)$");
        if (artist.Success && activate)
            return Music(artist.Groups["query"].Value, "artist");

        var track = Match(target,
            @"^(?:песню|трек|композицию|музыку)\s+(?<query>.+)$");
        if (track.Success && activate)
            return Music(track.Groups["query"].Value,
                target.StartsWith("музыку ", StringComparison.OrdinalIgnoreCase) ? "auto" : "track");

        string? hint = DeviceHint(target);
        if (!activate || hint is not null)
            return await ResolveHomeAsync(target, homeService, hint, explicitHome: true, ct);

        // "Включи <X>" is ambiguous. Check real HA names first, but never
        // invent an entity or deduce a device from the verb alone. An unmatched
        // name is searched in Yandex's music catalog with relevance scoring.
        if (home.Connected)
        {
            var resolved = await ResolveHomeAsync(target, homeService,
                null, explicitHome: false, ct);
            if (resolved is not null) return resolved;
        }
        return Music(target, "auto");
    }

    private async Task<Decision?> ResolveHomeAsync(string target, string service,
        string? hint, bool explicitHome, CancellationToken ct)
    {
        if (!home.Connected)
            return explicitHome ? Fail("Умный дом не подключён. Команда не выполнена.") : null;
        IReadOnlyList<VerifiedHomeAssistantService.Entity> entities;
        try { entities = await home.EntitiesAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or InvalidOperationException or JsonException or
                InvalidDataException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Home Assistant entity lookup failed");
            return explicitHome
                ? Fail("Не удалось получить устройства Home Assistant: " + ex.Message)
                : null;
        }

        var features = TargetFeatures(target);
        var matches = new List<(VerifiedHomeAssistantService.Entity Entity, int Score)>();
        foreach (var e in entities)
        {
            string domain = e.Id.Split('.')[0];
            if (domain is not ("light" or "switch" or "input_boolean" or
                "fan" or "cover" or "media_player")) continue;
            if (hint is not null && hint != "appliance" && domain != hint) continue;
            if ((service is "open_cover" or "close_cover") && domain != "cover") continue;
            string[] name = Tokens(e.Name + " " + e.Id.Replace('_', ' ').Replace('.', ' '));
            if (!features.All(f => name.Any(n => n.StartsWith(f, StringComparison.Ordinal) ||
                                                 f.StartsWith(n, StringComparison.Ordinal)))) continue;
            if (hint is null && features.Length == 0) continue;
            int score = features.Sum(f => name.Contains(f) ? 3 : 1);
            if (hint is null && features.Length == 1 &&
                Tokens(e.Name).Length == 1 && Tokens(e.Name)[0] == features[0]) score += 5;
            matches.Add((e, score));
        }
        if (matches.Count == 0)
            return explicitHome ? Fail("Подходящее устройство Home Assistant не найдено.") : null;

        int top = matches.Max(x => x.Score);
        var best = matches.Where(x => x.Score == top).ToArray();
        if (best.Length > 1)
        {
            string names = string.Join(", ", best.Take(4).Select(x => x.Entity.Name));
            return Fail("Неоднозначная команда для умного дома. Уточните устройство: " + names);
        }

        var entity = best[0].Entity;
        string domainName = entity.Id.Split('.')[0];
        string selectedService = service;
        if (service == "turn_on" && domainName == "cover") selectedService = "open_cover";
        if (service == "turn_off" && domainName == "cover") selectedService = "close_cover";
        if (domainName == "media_player")
            selectedService = service == "turn_on" ? "media_play" :
                service == "turn_off" ? "media_pause" : service;
        logger.LogInformation("Device matched from HA catalog: {EntityId}", entity.Id);
        return Call("ha_call_service", new {
            entity_id = entity.Id, domain = domainName, service = selectedService
        });
    }
}
