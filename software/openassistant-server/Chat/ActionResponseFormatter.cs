using System.Text.Json;
using OpenAssistant.Server.Transport;

namespace OpenAssistant.Server.Chat;

/// <summary>
/// Only verified results can generate success confirmations.
/// This avoids hallucinated "playing now" announcements from a second LLM.
/// </summary>
public static class ActionResponseFormatter
{
    private static string Field(JsonElement item, params string[] keys)
    {
        if (item.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in keys)
        {
            if (!item.TryGetProperty(key, out var field)) continue;
            return field.ValueKind switch {
                JsonValueKind.String => field.GetString() ?? "",
                JsonValueKind.Number => field.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
        }
        return "";
    }

    public static string Format(string name, object? result,
        PlaybackAcknowledgement.Outcome? playback)
    {
        if (playback is { Started: true })
            return "Включаю: " + playback.Title + ".";
        if (playback is { Started: false })
            return "Не удалось начать воспроизведение: " + playback.Reason + ".";

        if (result is null) return "Действие не выполнено: нет результата.";
        var node = JsonSerializer.SerializeToElement(result);
        string error = Field(node, "error", "Error");
        if (error.Length > 0) return "Не удалось выполнить действие: " + error + ".";

        switch (name)
        {
            case "music_play_query":
            case "music_play":
            case "music_play_liked":
            case "music_play_playlist":
            case "radio_play":
                return "Не удалось подтвердить воспроизведение на устройстве.";
            case "music_control":
                string action = Field(node, "requested");
                return action switch {
                    "pause" => "Отправил команду паузы.",
                    "resume" => "Отправил команду продолжения.",
                    "stop" => "Музыка остановлена.",
                    "next" or "previous" => "Команда переключения трека отправлена.",
                    "status" => "Запросил состояние музыкального плеера.",
                    _ => "Команда плееру отправлена."
                };
            case "ha_call_service":
                string nameOfDevice = Field(node, "friendly_name", "FriendlyName",
                    "entity_id", "EntityId");
                string observed = Field(node, "observed_state", "ObservedState");
                if (Field(node, "verified", "Verified") == "true")
                    return (nameOfDevice.Length > 0 ? nameOfDevice + ": " : "") +
                        "состояние «" + observed + "» подтверждено Home Assistant.";
                return "Команда отправлена, но Home Assistant не подтвердил изменение состояния" +
                    (observed.Length > 0 ? ". Наблюдаемое состояние: «" + observed + "»." : ".");
            case "ha_get_state":
                return Field(node, "Name", "name", "Id", "id") + ": " +
                    Field(node, "State", "state") +
                    (Field(node, "Unit", "unit").Length > 0
                        ? " " + Field(node, "Unit", "unit") : "") + ".";
            case "ha_list_entities":
                if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
                    return "В Home Assistant не нашёл подходящих устройств.";
                return "Устройства: " + string.Join("; ",
                    node.EnumerateArray().Take(8).Select(x =>
                        Field(x, "Name", "name", "Id", "id") + " (" +
                        Field(x, "State", "state") + ")")) + ".";
            case "music_list_playlists":
                if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
                    return "Плейлисты не найдены.";
                return "Плейлисты: " + string.Join(", ",
                    node.EnumerateArray().Take(10).Select(x => Field(x, "Title", "title"))) + ".";
            case "music_search":
                if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
                    return "Композиций не найдено.";
                return "Нашёл композиции: " + string.Join("; ",
                    node.EnumerateArray().Take(5).Select(x => Field(x, "Title", "title"))) + ".";
            case "radio_search":
                if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
                    return "Радиостанции не найдены.";
                return "Радиостанции: " + string.Join("; ",
                    node.EnumerateArray().Take(5).Select(x => Field(x, "name", "Name"))) + ".";
            default:
                return "Запрос к навыку выполнен.";
        }
    }
}
