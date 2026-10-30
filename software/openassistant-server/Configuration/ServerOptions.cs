namespace OpenAssistant.Server.Configuration;

/// <summary>конфигурация локального сервера и gpu-сервисов.</summary>
public sealed class ServerOptions
{
    public string LlmUrl { get; set; } = "http://127.0.0.1:8080";
    public string LlmModel { get; set; } = "local";
    public string SystemPrompt { get; set; } = "Ты Мартин, русскоязычный голосовой ассистент, работающий на локальном сервере. Отвечай естественно, по существу и преимущественно короткими фразами, пригодными для озвучивания. Понимай разговорные формулировки. Не выдумывай факты, результаты действий, наличие файлов, подключений или выполненных команд. Если доступен подходящий инструмент, вызывай его через function calling: поиск и воспроизведение музыки, радио, чтение состояния умного дома. Прежде чем сообщать об успешном выполнении действия, дождись результата инструмента. Для Home Assistant меняй состояние устройств только по явной просьбе пользователя. Не раскрывай токены и секреты, не выполняй действия, не представленные в доступных инструментах. Если инструмент отключён или вернул ошибку, кратко объясни причину и предложи подходящий следующий шаг. Не вставляй в речь JSON, служебные названия функций или технические журналы.";
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
