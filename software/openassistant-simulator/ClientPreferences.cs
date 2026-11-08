using System.Text.Json;
namespace OpenAssistant.Simulator;

/// <summary>Non-secret UI preferences only. Device tokens are never persisted.</summary>
public sealed class ClientPreferences
{
    public string Server { get; set; } = "ws://127.0.0.1:5055/ws";
    public string Profile { get; set; } = "Общий";
    public string WakeModel { get; set; } = "";
    public string WakeWord { get; set; } = "Мартин";
    public double WakeThreshold { get; set; } = 0.87;
    public bool WakeEnabled { get; set; } = true;
    public bool CommunicationsDsp { get; set; } = true;
    public bool NoiseReduction { get; set; } = true;
    public bool BargeInOnMusic { get; set; } = true;
    // Null means older preferences: default volume ducking to enabled.
    public bool? DuckMusicOnWake { get; set; }
    private static string Location => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAssistant","simulator.json");
    public static ClientPreferences Load()
    {
        try
        {
            return File.Exists(Location)
                ? JsonSerializer.Deserialize<ClientPreferences>(File.ReadAllText(Location)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
        string temp=Location+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(temp,Location,true);
    }
}
