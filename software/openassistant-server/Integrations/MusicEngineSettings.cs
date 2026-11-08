using System.Text.Json;

namespace OpenAssistant.Server.Integrations;

/// <summary>Only one music provider can be selected. Radio is an independent capability.</summary>
public sealed class MusicEngineSettings(IWebHostEnvironment env)
{
    public sealed record Options(
        string Engine = "yandex_music",
        bool Search = true,
        bool Playback = true,
        bool Library = true,
        bool Transport = true,
        bool Recommendations = true);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(env.ContentRootPath, "music-engine.local.json");

    public Options Current
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                var read = JsonSerializer.Deserialize<Options>(File.ReadAllText(FilePath)) ?? new();
                return read.Engine == "yandex_music" ? read : new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { return new(); }
        }
    }

    public static string[] Available => ["yandex_music"];
    public async Task<Options> SaveAsync(Options options, CancellationToken ct)
    {
        if (options is null || options.Engine != "yandex_music")
            throw new ArgumentException("Доступный музыкальный движок: Яндекс Музыка");
        await _gate.WaitAsync(ct);
        try
        {
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(options), ct);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _gate.Release(); }
        return options;
    }
}
