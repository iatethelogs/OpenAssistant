using System.Text.Json;

namespace OpenAssistant.Server.Audio;

/// <summary>Настройки TTS всего сервера. Профили отдельных колонок будут независимыми.</summary>
public sealed class VoiceProfileStore(IWebHostEnvironment env)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(env.ContentRootPath, "voice-profile.local.json");
    public sealed record Profile(string Mode = "custom", string Speaker = "Ryan",
        string Description = "A calm, friendly, clear Russian-speaking adult voice with natural intonation and moderate pace.");
    public Profile Read()
    {
        if (!File.Exists(FilePath)) return new Profile();
        try { return JsonSerializer.Deserialize<Profile>(File.ReadAllText(FilePath)) ?? new Profile(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new Profile(); }
    }
    public async Task<Profile> SaveAsync(Profile input, CancellationToken ct)
    {
        if (input is null || input.Mode is not ("custom" or "design"))
            throw new ArgumentException("Выберите готовый голос или проектирование по описанию");
        var speaker = (input.Speaker ?? "").Trim();
        var description = (input.Description ?? "").Trim();
        if (speaker.Length is < 1 or > 80 || description.Length > 1200 ||
            input.Mode == "design" && description.Length < 8)
            throw new ArgumentException("Некорректные параметры голоса");
        var clean = new Profile(input.Mode, speaker, description);
        await _gate.WaitAsync(ct);
        try
        {
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(clean), ct);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, FilePath, true);
        }
        finally { _gate.Release(); }
        return clean;
    }
}
