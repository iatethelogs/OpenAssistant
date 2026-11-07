using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpenAssistant.Server.Configuration;

/// <summary>
/// Persistent server secrets outside the Git working tree. Device keys are never
/// regenerated on a restart or source update; rotation is always explicit.
/// </summary>
public sealed class ServerCredentialStore
{
    private sealed class Secrets
    {
        public string DeviceToken { get; set; } = "";
        public string PanelPassword { get; set; } = "";
    }

    private readonly object _sync = new();
    private readonly string _path;
    private Secrets _current;

    public ServerCredentialStore(IOptionsMonitor<ServerOptions> options)
    {
        string? stateRoot;
        if (OperatingSystem.IsWindows())
        {
            stateRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            stateRoot = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg)
                ? xdg
                : Path.Combine(home, ".local", "state");
        }
        if (string.IsNullOrWhiteSpace(stateRoot) || !Path.IsPathFullyQualified(stateRoot))
            throw new InvalidOperationException("Не определён домашний каталог для хранения ключей OpenAssistant");

        string directory = Path.Combine(stateRoot, "openassistant");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(directory, "credentials.json");

        if (File.Exists(_path))
        {
            // A damaged file must never silently trigger rotation and lock out devices.
            try
            {
                _current = JsonSerializer.Deserialize<Secrets>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("Пустой файл секретов");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Повреждён файл ключей {_path}", ex);
            }
            if (string.IsNullOrWhiteSpace(_current.DeviceToken) || _current.DeviceToken.Length > 256)
                throw new InvalidDataException($"Некорректный ключ в {_path}; автоматическая замена запрещена");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else
        {
            // Migrate a previously issued key from settings.local.json.
            string existingKey = options.CurrentValue.DeviceToken;
            _current = new Secrets
            {
                DeviceToken = string.IsNullOrWhiteSpace(existingKey)
                    ? NewDeviceToken() : existingKey,
                PanelPassword = ""
            };
            Persist(_current);
        }

        // One-time setup: the operator supplies a password through the process
        // environment. Subsequent restarts use the securely persisted value.
        // An explicitly supplied *different* password changes it on startup.
        string? suppliedPassword = Environment.GetEnvironmentVariable("OPENASSISTANT_PANEL_PASSWORD");
        if (!string.IsNullOrWhiteSpace(suppliedPassword) &&
            !string.Equals(_current.PanelPassword, suppliedPassword, StringComparison.Ordinal))
        {
            var next = new Secrets { DeviceToken = _current.DeviceToken, PanelPassword = suppliedPassword };
            Persist(next);
            _current = next;
        }
    }

    public string DeviceToken { get { lock (_sync) return _current.DeviceToken; } }
    public string PanelPassword { get { lock (_sync) return _current.PanelPassword; } }

    public string RotateDeviceToken()
    {
        lock (_sync)
        {
            var next = new Secrets
            {
                DeviceToken = NewDeviceToken(),
                PanelPassword = _current.PanelPassword
            };
            Persist(next);
            _current = next;
            return next.DeviceToken;
        }
    }

    private static string NewDeviceToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private void Persist(Secrets data)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
