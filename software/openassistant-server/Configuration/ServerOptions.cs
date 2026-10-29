namespace OpenAssistant.Server.Configuration;

/// <summary>настройки движков. секреты передавай через переменные окружения, а не коммить в git.</summary>
public sealed class ServerOptions
{
    public string LlmUrl { get; set; } = "http://127.0.0.1:8080";
    public string LlmModel { get; set; } = "local";
    public string SystemPrompt { get; set; } = "ты голосовой ассистент. отвечай по-русски.";
    public string SttExecutable { get; set; } = "whisper-cli";
    public string SttModel { get; set; } = "";
    public string TtsExecutable { get; set; } = "piper";
    public string TtsModel { get; set; } = "";
    public string FfmpegExecutable { get; set; } = "ffmpeg";
    public string HomeAssistantUrl { get; set; } = "";
    public string HomeAssistantToken { get; set; } = "";
    public int InputSampleRate { get; set; } = 16000;
    public int OutputSampleRate { get; set; } = 22050;
    public Dictionary<string, string> RadioStations { get; set; } = new();
}
