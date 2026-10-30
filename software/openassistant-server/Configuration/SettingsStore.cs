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
            s.HomeAssistantUrl, hasDeviceToken = !string.IsNullOrWhiteSpace(s.DeviceToken), hasHomeAssistantToken = !string.IsNullOrWhiteSpace(s.HomeAssistantToken),
            s.LlmTemperature, s.LlmTopP, s.LlmTopK, s.LlmRepeatPenalty, s.LlmMaxTokens,
            s.InputSampleRate, s.OutputSampleRate, s.RadioStations
        };
    }

    /// <summary>обновляет только параметры Home Assistant; прочие настройки сохраняет.</summary>
    public Task UpdateHomeAssistantAsync(string url, string token, CancellationToken ct)
    {
        var s = options.CurrentValue;
        return SaveAsync(new SettingsUpdate
        {
            LlmUrl = s.LlmUrl,
            LlmModel = s.LlmModel,
            SystemPrompt = s.SystemPrompt,
            SttUrl = s.SttUrl,
            SttModel = s.SttModel,
            TtsUrl = s.TtsUrl,
            TtsModel = s.TtsModel,
            TtsVoice = s.TtsVoice,
            TtsLanguage = s.TtsLanguage,
            HomeAssistantUrl = url,
            HomeAssistantToken = token,
            DeviceToken = s.DeviceToken,
            LlmTemperature = s.LlmTemperature,
            LlmTopP = s.LlmTopP,
            LlmTopK = s.LlmTopK,
            LlmRepeatPenalty = s.LlmRepeatPenalty,
            LlmMaxTokens = s.LlmMaxTokens,
            InputSampleRate = s.InputSampleRate,
            OutputSampleRate = s.OutputSampleRate,
            RadioStations = new Dictionary<string, string>(s.RadioStations)
        }, ct);
    }

    /// <summary>Сохраняет точечные изменения, не сбрасывая остальные параметры ассистента.</summary>
    public Task UpdateLlmUrlAsync(string url, CancellationToken ct) => UpdatePartialAsync(url, null, null, null, ct);
    public Task UpdateSttUrlAsync(string url, CancellationToken ct) => UpdatePartialAsync(null, url, null, null, ct);
    public Task UpdateTtsUrlAsync(string url, CancellationToken ct) => UpdatePartialAsync(null, null, url, null, ct);
    public Task UpdateDeviceTokenAsync(string token, CancellationToken ct) => UpdatePartialAsync(null, null, null, token, ct);
    public Task UpdateLlmTemperatureAsync(double temperature, CancellationToken ct)
    {
        if (temperature is < 0 or > 2) throw new ArgumentException("Температура должна быть от 0 до 2");
        return UpdatePartialAsync(null, null, null, null, ct, temperature);
    }
    private Task UpdatePartialAsync(string? llmUrl, string? sttUrl, string? ttsUrl, string? deviceToken, CancellationToken ct, double? temperature = null)
    {
        var s = options.CurrentValue;
        return SaveAsync(new SettingsUpdate {
            LlmUrl = llmUrl ?? s.LlmUrl, LlmModel = s.LlmModel, SystemPrompt = s.SystemPrompt,
            SttUrl = sttUrl ?? s.SttUrl, SttModel = s.SttModel,
            TtsUrl = ttsUrl ?? s.TtsUrl, TtsModel = s.TtsModel, TtsVoice = s.TtsVoice,
            TtsLanguage = s.TtsLanguage, HomeAssistantUrl = s.HomeAssistantUrl,
            HomeAssistantToken = s.HomeAssistantToken, DeviceToken = deviceToken ?? s.DeviceToken,
            LlmTemperature = temperature ?? s.LlmTemperature, LlmTopP = s.LlmTopP,
            LlmTopK = s.LlmTopK, LlmRepeatPenalty = s.LlmRepeatPenalty,
            LlmMaxTokens = s.LlmMaxTokens,
            InputSampleRate = s.InputSampleRate, OutputSampleRate = s.OutputSampleRate,
            RadioStations = new Dictionary<string, string>(s.RadioStations)
        }, ct);
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
        if (!double.IsFinite(update.LlmTemperature) || update.LlmTemperature is < 0 or > 2 ||
            !double.IsFinite(update.LlmTopP) || update.LlmTopP is <= 0 or > 1 ||
            update.LlmTopK is < 0 or > 200 ||
            !double.IsFinite(update.LlmRepeatPenalty) || update.LlmRepeatPenalty is < 0.5 or > 2 ||
            update.LlmMaxTokens is < 16 or > 8192)
            throw new ArgumentException("Некорректные параметры генерации LLM");
        if (update.InputSampleRate is < 8000 or > 48000 || update.OutputSampleRate is < 8000 or > 48000)
            throw new ArgumentException("частота дискретизации вне допустимого диапазона");
        foreach (string value in new[] { update.LlmModel, update.SttModel, update.TtsModel, update.TtsVoice, update.TtsLanguage })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 150) throw new ArgumentException("некорректное название модели или голоса");
        if (update.DeviceToken is { Length: > 256 } || update.HomeAssistantToken is { Length: > 4096 }) throw new ArgumentException("слишком длинный токен");
        if (update.SystemPrompt is null || update.SystemPrompt.Length > 16000) throw new ArgumentException("системная инструкция слишком длинная");

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
                    DeviceToken = update.DeviceToken is null ? current.DeviceToken : update.DeviceToken,
                    update.LlmTemperature, update.LlmTopP, update.LlmTopK, update.LlmRepeatPenalty, update.LlmMaxTokens,
                    update.InputSampleRate, update.OutputSampleRate,
                    RadioStations = update.RadioStations ?? current.RadioStations
                }
            };
            string destination = Path.Combine(env.ContentRootPath, "settings.local.json");
            string tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), ct);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
    public string? DeviceToken { get; set; }
    public double LlmTemperature { get; set; } = 0.35;
    public double LlmTopP { get; set; } = 0.9;
    public int LlmTopK { get; set; } = 40;
    public double LlmRepeatPenalty { get; set; } = 1.1;
    public int LlmMaxTokens { get; set; } = 768;
    public int InputSampleRate { get; set; } = 16000;
    public int OutputSampleRate { get; set; } = 24000;
    public Dictionary<string, string>? RadioStations { get; set; }
}
