using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenAssistant.Server.Configuration;

public sealed class DisplayEffect
{
    public string Preset { get; set; } = "off";
    public string Color { get; set; } = "#89ABD8";
    public double Speed { get; set; } = 1;
}
public sealed class DisplayConfiguration
{
    public int Width { get; set; } = 32;
    public int Height { get; set; } = 10;
    public int Fps { get; set; } = 25;
    public int Brightness { get; set; } = 65;
    public bool Serpentine { get; set; }
    public Dictionary<string, DisplayEffect> States { get; set; } = CreateDefaults();

    public static readonly string[] StateNames =
        ["offline", "idle", "listening", "thinking", "speaking", "music", "error"];

    public static readonly string[] Presets =
        ["off", "solid", "breath", "wave", "bars", "orbit", "pulse", "sparkles", "eyes", "scan"];

    public static Dictionary<string, DisplayEffect> CreateDefaults() => new()
    {
        ["offline"] = new() { Preset = "off" },
        ["idle"] = new() { Preset = "breath", Color = "#789BC1", Speed = 0.6 },
        ["listening"] = new() { Preset = "bars", Color = "#69ACE5", Speed = 1.2 },
        ["thinking"] = new() { Preset = "orbit", Color = "#99AACE", Speed = 1 },
        ["speaking"] = new() { Preset = "wave", Color = "#B0BFE1", Speed = 1 },
        ["music"] = new() { Preset = "bars", Color = "#82A8DD", Speed = 1.5 },
        ["error"] = new() { Preset = "pulse", Color = "#C78282", Speed = 0.7 }
    };

    public static void Validate(DisplayConfiguration value)
    {
        if (value.Width is < 1 or > 256 || value.Height is < 1 or > 128 ||
            (long)value.Width * value.Height > 8192)
            throw new ArgumentException("Допустимый размер: до 8192 светодиодов, 256×128");
        if (value.Fps is < 1 or > 60 || value.Brightness is < 1 or > 100)
            throw new ArgumentException("FPS: 1–60, яркость: 1–100%");
        if (value.States is null || value.States.Count != StateNames.Length ||
            StateNames.Any(name => !value.States.ContainsKey(name)))
            throw new ArgumentException("Необходимо настроить семь состояний дисплея");
        foreach (var effect in value.States.Values)
        {
            if (effect is null || !Presets.Contains(effect.Preset, StringComparer.Ordinal) ||
                !Regex.IsMatch(effect.Color ?? "", @"^#[0-9A-Fa-f]{6}$") ||
                !double.IsFinite(effect.Speed) || effect.Speed is < 0.1 or > 5)
                throw new ArgumentException("Некорректный пресет, цвет или скорость");
        }
    }
}

/// <summary>
/// Independent persisted configuration. The normal /api/settings save cannot reset display settings.
/// Clients receive a snapshot on WebSocket connect and may request it again after an edit.
/// </summary>
public sealed class DisplaySettingsStore(IWebHostEnvironment environment)
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string FileName => Path.Combine(environment.ContentRootPath, "display.local.json");
    private DisplayConfiguration? _current;

    public DisplayConfiguration Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _current);
            if (snapshot is not null) return snapshot;
            var loaded = Load();
            return Interlocked.CompareExchange(ref _current, loaded, null) ?? loaded;
        }
    }

    private DisplayConfiguration Load()
    {
        try
        {
            if (!File.Exists(FileName)) return new();
            var loaded = JsonSerializer.Deserialize<DisplayConfiguration>(File.ReadAllText(FileName), JsonOptions);
            if (loaded is null) return new();
            DisplayConfiguration.Validate(loaded);
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(); }
    }

    public async Task<DisplayConfiguration> SaveAsync(DisplayConfiguration next, CancellationToken ct)
    {
        DisplayConfiguration.Validate(next);
        await _saveLock.WaitAsync(ct);
        try
        {
            string temp = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, JsonOptions), ct);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, FileName, true);
                Volatile.Write(ref _current, next);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return next;
        }
        finally { _saveLock.Release(); }
    }
}
