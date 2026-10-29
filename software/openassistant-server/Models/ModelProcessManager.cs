using System.Diagnostics;
using System.Text.Json;
namespace OpenAssistant.Server.Models;

/// <summary>управление локальными linux-процессами выбранных пользователем моделей.</summary>
public sealed class ModelProcessManager(IWebHostEnvironment env, ILogger<ModelProcessManager> logger) : IDisposable
{
    private readonly Dictionary<string, Process> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _guard = new(1, 1);
    private static readonly string[] Roles = ["llm", "stt", "tts"];
    private string ConfigPath => Path.Combine(env.ContentRootPath, "models.local.json");
    /// <summary>перечисляет сохранённые профили запуска; исполняемые файлы выбирает владелец сервера.</summary>
    public async Task<Dictionary<string, ModelLaunchProfile>> ProfilesAsync(CancellationToken ct)
    {
        if (!File.Exists(ConfigPath)) return new();
        return JsonSerializer.Deserialize<Dictionary<string, ModelLaunchProfile>>(await File.ReadAllTextAsync(ConfigPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
    /// <summary>сохраняет модель, путь к исполняемому файлу, параметры и номера видеокарт.</summary>
    public async Task SaveAsync(string role, ModelLaunchProfile profile, CancellationToken ct)
    {
        Validate(role, profile);
        await _guard.WaitAsync(ct);
        try {
            var all = await ProfilesAsync(ct);
            all[role] = profile;
            string tmp = ConfigPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(tmp, ConfigPath, true);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } finally { _guard.Release(); }
    }
    /// <summary>запускает только явно настроенную модель, без shell и без скрытой подмены gpu.</summary>
    public async Task<object> StartAsync(string role, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        try {
            var profiles = await ProfilesAsync(ct);
            if (!profiles.TryGetValue(role, out var profile)) throw new ArgumentException("профиль не задан");
            Validate(role, profile);
            if (_running.TryGetValue(role, out var existing))
            {
                if (!existing.HasExited) return new { role, running = true, pid = existing.Id };
                existing.Dispose();
                _running.Remove(role);
            }
            var psi = new ProcessStartInfo(profile.Executable) {
                UseShellExecute = false, RedirectStandardOutput = false, RedirectStandardError = false,
                WorkingDirectory = profile.WorkingDirectory
            };
            foreach (var arg in profile.Arguments) psi.ArgumentList.Add(arg);
            psi.Environment["CUDA_VISIBLE_DEVICES"] = string.Join(",", profile.Gpus);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился");
            _running[role] = process;
            logger.LogInformation("started {Role} PID={Pid} GPU={Gpu}", role, process.Id, psi.Environment["CUDA_VISIBLE_DEVICES"]);
            return new { role, running = true, pid = process.Id };
        } finally { _guard.Release(); }
    }
    /// <summary>останавливает только процесс, который запущен нашим менеджером.</summary>
    public async Task<object> StopAsync(string role, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        try {
            if (!_running.Remove(role, out var process)) return new { role, running = false };
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct); } }
            finally { process.Dispose(); }
            return new { role, running = false };
        } finally { _guard.Release(); }
    }
    /// <summary>показывает состояние процессов и список видеокарт из nvidia-smi.</summary>
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        Dictionary<string, object> running;
        try { running = _running.ToDictionary(p => p.Key, p => (object)new { running = !p.Value.HasExited, pid = p.Value.Id }); }
        finally { _guard.Release(); }
        string gpu = "";
        try {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi") {
                UseShellExecute = false, RedirectStandardOutput = true,
                ArgumentList = { "--query-gpu=index,name,memory.total,memory.used", "--format=csv,noheader,nounits" }
            })!;
            gpu = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
        } catch (Exception ex) { logger.LogDebug(ex, "nvidia-smi unavailable"); }
        return new { running, gpus = gpu.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
    }
    /// <summary>отсекает недопустимые роли, пути и gpu-индексы перед запуском.</summary>
    private static void Validate(string role, ModelLaunchProfile profile)
    {
        if (!Roles.Contains(role)) throw new ArgumentException("неизвестный тип модели");
        if (!Path.IsPathFullyQualified(profile.Executable) || !File.Exists(profile.Executable))
            throw new ArgumentException("требуется абсолютный путь к существующему исполняемому файлу");
        if (!Path.IsPathFullyQualified(profile.WorkingDirectory) || !Directory.Exists(profile.WorkingDirectory))
            throw new ArgumentException("рабочая папка не найдена");
        if (profile.Gpus.Length == 0 || profile.Gpus.Length > 16 || profile.Gpus.Any(g => g < 0 || g > 31) || profile.Gpus.Distinct().Count() != profile.Gpus.Length)
            throw new ArgumentException("неверный список gpu");
        if (profile.Arguments.Length > 64 || profile.Arguments.Any(a => a.Length > 2048)) throw new ArgumentException("слишком длинные аргументы");
    }
    /// <summary>при завершении приложения освобождает дочерние процессы.</summary>
    public void Dispose()
    {
        foreach (var p in _running.Values) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
        _guard.Dispose();
    }
}
/// <summary>один профиль запуска процесса gpu-модели.</summary>
public sealed class ModelLaunchProfile
{
    public string Executable { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public int[] Gpus { get; set; } = [0];
}
