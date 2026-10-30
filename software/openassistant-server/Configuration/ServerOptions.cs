namespace OpenAssistant.Server.Configuration;

/// <summary>конфигурация локального сервера и gpu-сервисов.</summary>
public sealed class ServerOptions
{
    public string LlmUrl { get; set; } = "http://127.0.0.1:8080";
    public string LlmModel { get; set; } = "local";
    public string SystemPrompt { get; set; } = "Ты OpenAssistant, персональный голосовой ассистент. Отвечай по-русски естественно, точно и кратко; для озвучивания используй короткие предложения. Учитывай выбранный пользователем профиль и историю его разговоров, но не делай выводов о личности собеседника по голосу. Сведения из памяти могут устареть. При вопросах о текущем состоянии устройств используй только свежие данные Home Assistant; в случае отсутствия подтверждения сообщи об этом, а не угадывай. Действия выполняй только по явной просьбе и только через доступные инструменты. Результат действия подтверждай лишь после проверки. Для музыки и радио используй соответствующие инструменты. Не раскрывай токены, внутренние вызовы и служебные данные. Не выдавай неподтверждённые предположения за факты.";
    public string SttUrl { get; set; } = "http://127.0.0.1:8016";
    public string SttModel { get; set; } = "gigaam-v3";
    public string TtsUrl { get; set; } = "http://127.0.0.1:8020";
    public string TtsModel { get; set; } = "Qwen3-TTS";
    public string TtsVoice { get; set; } = "default";
    public string TtsLanguage { get; set; } = "Russian";
    public string HomeAssistantUrl { get; set; } = "";
    public string HomeAssistantToken { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public double LlmTemperature { get; set; } = 0.35;
    public double LlmTopP { get; set; } = 0.9;
    public int LlmTopK { get; set; } = 40;
    public double LlmRepeatPenalty { get; set; } = 1.1;
    public int LlmMaxTokens { get; set; } = 768;
    public int InputSampleRate { get; set; } = 16000;
    public int OutputSampleRate { get; set; } = 24000;
    public Dictionary<string, string> RadioStations { get; set; } = new();
}
