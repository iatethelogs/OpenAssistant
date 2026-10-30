namespace OpenAssistant.Server.Configuration;

/// <summary>конфигурация локального сервера и gpu-сервисов.</summary>
public sealed class ServerOptions
{
    public string LlmUrl { get; set; } = "http://127.0.0.1:8080";
    public string LlmModel { get; set; } = "local";
    public string SystemPrompt { get; set; } = "Ты голосовой ассистент платформы OpenAssistant. Общайся с пользователем на русском языке, естественно и короткими фразами, пригодными для озвучивания. OpenAssistant — название проекта и сервера; не называй себя «Мартин» без явного указания имени подключённого устройства или пользователя. Отвечай по существу, не выдумывай сведения и не утверждай, что выполнил действие, до успешного ответа инструмента. Для музыки, радио и умного дома используй доступные функции через function calling. Для изменения состояния устройства Home Assistant требуется явная просьба пользователя. Не раскрывай секреты, ключи, внутренние имена функций и служебные журналы. Если инструмент недоступен или вернул ошибку, кратко объясни проблему.";
    public string SttUrl { get; set; } = "http://127.0.0.1:8016";
    public string SttModel { get; set; } = "gigaam-v3";
    public string TtsUrl { get; set; } = "http://127.0.0.1:8020";
    public string TtsModel { get; set; } = "Qwen3-TTS";
    public string TtsVoice { get; set; } = "default";
    public string TtsLanguage { get; set; } = "Russian";
    public string HomeAssistantUrl { get; set; } = "";
    public string HomeAssistantToken { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public double LlmTemperature { get; set; } = 0.5;
    public double LlmTopP { get; set; } = 0.9;
    public int LlmTopK { get; set; } = 40;
    public double LlmRepeatPenalty { get; set; } = 1.1;
    public int LlmMaxTokens { get; set; } = 512;
    public int InputSampleRate { get; set; } = 16000;
    public int OutputSampleRate { get; set; } = 24000;
    public Dictionary<string, string> RadioStations { get; set; } = new();
}
