using System.Text.Json;

namespace OpenAssistant.Server.Audio;

/// <summary>Настройки TTS всего сервера. Профили отдельных колонок будут независимыми.</summary>
public sealed class VoiceProfileStore(IWebHostEnvironment env)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(env.ContentRootPath, "voice-profile.local.json");
    public sealed record Profile(string Mode = "custom", string Speaker = "Ryan",
        string Description = "A calm, friendly, clear Russian-speaking adult voice with natural intonation and moderate pace.",
        string Style = "", string GenerationMode = "seeded", int Seed = 42,
        double Temperature = 0.75, double TopP = 0.9, int TopK = 40,
        double RepetitionPenalty = 1.05);
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
        var style = (input.Style ?? "").Trim();
        if (speaker.Length is < 1 or > 80 || description.Length > 1200 || style.Length > 600 ||
            input.Mode == "design" && description.Length < 8)
            throw new ArgumentException("Некорректные параметры голоса");
        if (input.GenerationMode is not ("seeded" or "greedy" or "random") ||
            input.Seed is < 0 or > 2147483647 ||
            !double.IsFinite(input.Temperature) || input.Temperature is < 0.1 or > 2.0 ||
            !double.IsFinite(input.TopP) || input.TopP is <= 0 or > 1 ||
            input.TopK is < 1 or > 200 ||
            !double.IsFinite(input.RepetitionPenalty) || input.RepetitionPenalty is < 1 or > 2)
            throw new ArgumentException("Некорректные параметры генерации TTS");
        var clean = input with { Speaker = speaker, Description = description, Style = style };
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
