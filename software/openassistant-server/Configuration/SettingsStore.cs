using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenAssistant.Server.Configuration;

/// <summary>постоянные настройки без базы данных, через отдельный локальный json.</summary>
public sealed class SettingsStore(IWebHostEnvironment env, IOptionsMonitor<ServerOptions> options)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>возвращает настройки для панели, не раскрывая секреты.</summary>
    public object PublicSettings()
    {
        var s = options.CurrentValue;
        return new {
            s.LlmUrl, s.LlmModel, s.SystemPrompt,
            s.SttUrl, s.SttModel, s.TtsUrl, s.TtsModel, s.TtsVoice, s.TtsLanguage,
            s.HomeAssistantUrl, hasHomeAssistantToken = !string.IsNullOrWhiteSpace(s.HomeAssistantToken),
            s.InputSampleRate, s.OutputSampleRate, s.RadioStations
        };
    }

    /// <summary>валидирует настройки и атомарно сохраняет их в settings.local.json.</summary>
    public async Task SaveAsync(SettingsUpdate update, CancellationToken ct)
    {
        static void RequireHttp(string input, string name, bool optional = false)
        {
            if (optional && string.IsNullOrWhiteSpace(input)) return;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var url) ||
                !(url.Scheme == "http" || url.Scheme == "https") || url.UserInfo.Length != 0)
                throw new ArgumentException($"{name}: требуется http(s) адрес");
        }
        RequireHttp(update.LlmUrl, nameof(update.LlmUrl));
        RequireHttp(update.SttUrl, nameof(update.SttUrl));
        RequireHttp(update.TtsUrl, nameof(update.TtsUrl));
        RequireHttp(update.HomeAssistantUrl, nameof(update.HomeAssistantUrl), true);
        if (update.InputSampleRate is < 8000 or > 48000 || update.OutputSampleRate is < 8000 or > 48000)
            throw new ArgumentException("частота дискретизации вне допустимого диапазона");
        foreach (string value in new[] { update.LlmModel, update.SttModel, update.TtsModel, update.TtsVoice, update.TtsLanguage })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 150) throw new ArgumentException("некорректное название модели или голоса");
        if (update.SystemPrompt.Length > 16000) throw new ArgumentException("системная инструкция слишком длинная");

        await _writeLock.WaitAsync(ct);
        try
        {
            var current = options.CurrentValue;
            var data = new {
                OpenAssistant = new {
                    update.LlmUrl, update.LlmModel, update.SystemPrompt,
                    update.SttUrl, update.SttModel, update.TtsUrl, update.TtsModel,
                    update.TtsVoice, update.TtsLanguage, update.HomeAssistantUrl,
                    HomeAssistantToken = update.HomeAssistantToken is null ? current.HomeAssistantToken : update.HomeAssistantToken,
                    update.InputSampleRate, update.OutputSampleRate,
                    RadioStations = update.RadioStations ?? current.RadioStations
                }
            };
            string destination = Path.Combine(env.ContentRootPath, "settings.local.json");
            string tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), ct);
                File.Move(tmp, destination, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        finally { _writeLock.Release(); }
    }
}

/// <summary>настраиваемые параметры, null для токена означает «оставить прежний».</summary>
public sealed class SettingsUpdate
{
    public string LlmUrl { get; set; } = "";
    public string LlmModel { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string SttUrl { get; set; } = "";
    public string SttModel { get; set; } = "";
    public string TtsUrl { get; set; } = "";
    public string TtsModel { get; set; } = "";
    public string TtsVoice { get; set; } = "";
    public string TtsLanguage { get; set; } = "Russian";
    public string HomeAssistantUrl { get; set; } = "";
    public string? HomeAssistantToken { get; set; }
    public int InputSampleRate { get; set; } = 16000;
    public int OutputSampleRate { get; set; } = 24000;
    public Dictionary<string, string>? RadioStations { get; set; }
}
