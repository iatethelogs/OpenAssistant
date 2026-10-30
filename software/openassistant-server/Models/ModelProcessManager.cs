using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Models;

/// <summary>Отдельное управление моделями, физическими GPU и логами runtime.</summary>
public sealed class ModelProcessManager(
    IWebHostEnvironment env, IOptionsMonitor<ServerOptions> settings,
    IHttpClientFactory clients, ILogger<ModelProcessManager> logger) : IDisposable
{
    private static readonly string[] Roles = ["llm", "stt", "tts"];
    private readonly SemaphoreSlim _guard = new(1, 1);
    private readonly Dictionary<string, Managed> _running = new(StringComparer.OrdinalIgnoreCase);
    private string ConfigPath => Path.Combine(env.ContentRootPath, "models.local.json");

    private sealed class Managed
    {
        public required Process Process { get; init; }
        public required string ModelPath { get; init; }
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Queue<string> Log { get; } = new();
    }

    public async Task<Dictionary<string, ModelLaunchProfile>> ProfilesAsync(CancellationToken ct)
    {
        if (!File.Exists(ConfigPath)) return new(StringComparer.OrdinalIgnoreCase);
        var profiles = JsonSerializer.Deserialize<Dictionary<string, ModelLaunchProfile>>(
            await File.ReadAllTextAsync(ConfigPath, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return profiles is null ? new(StringComparer.OrdinalIgnoreCase) :
            new(profiles, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveAsync(string role, ModelLaunchProfile profile, CancellationToken ct)
    {
        Validate(role, profile, false);
        await _guard.WaitAsync(ct);
        try
        {
            var all = await ProfilesAsync(ct);
            all[role] = profile;
            string temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(all,
                    new JsonSerializerOptions { WriteIndented = true }), ct);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, ConfigPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _guard.Release(); }
    }

    public async Task<object> StartAsync(string role, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        try
        {
            var profiles = await ProfilesAsync(ct);
            if (!profiles.TryGetValue(role, out var profile)) throw new ArgumentException("Профиль модели не сохранён");
            Validate(role, profile, true);
            if (_running.TryGetValue(role, out var old))
            {
                if (!old.Process.HasExited)
                {
                    string expected = string.IsNullOrWhiteSpace(profile.ModelPath) ? "" : Path.GetFullPath(profile.ModelPath);
                    if (string.Equals(expected, old.ModelPath, StringComparison.Ordinal))
                        return new { role, running = true, pid = old.Process.Id };
                    old.Process.Kill(entireProcessTree: true);
                    await old.Process.WaitForExitAsync(ct);
                    Append(old, "SYS", "Previous model stopped before switching weights");
                }
                old.Process.Dispose();
                _running.Remove(role);
            }
            string model = string.IsNullOrWhiteSpace(profile.ModelPath) ? "" : Path.GetFullPath(profile.ModelPath);
            string devices = profile.GpuUuids.Length != 0
                ? string.Join(",", profile.GpuUuids) : string.Join(",", profile.Gpus);
            var start = new ProcessStartInfo(profile.Executable)
            {
                UseShellExecute = false, WorkingDirectory = profile.WorkingDirectory,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in profile.Arguments) start.ArgumentList.Add(Expand(argument, model));
            if (profile.GpuUuids.Length > 0 || profile.Gpus.Length > 0)
                start.Environment["CUDA_VISIBLE_DEVICES"] = devices;
            else start.Environment.Remove("CUDA_VISIBLE_DEVICES");
            foreach (var (key, value) in profile.Environment)
                start.Environment[key] = Expand(value, model);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Движок не запустился");
            var managed = new Managed { Process = process, ModelPath = model };
            _running[role] = managed;
            process.OutputDataReceived += (_, args) => Append(managed, "OUT", args.Data);
            process.ErrorDataReceived += (_, args) => Append(managed, "ERR", args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Append(managed, "SYS", $"PID {process.Id}; CUDA GPU {devices}; model {model}");
            logger.LogInformation("Started {Role} PID={Pid} devices={Devices}", role, process.Id, devices);
            return new { role, running = !process.HasExited, pid = process.Id };
        }
        finally { _guard.Release(); }
    }

    private static string Expand(string value, string model) =>
        value.Replace("{model}", model, StringComparison.Ordinal)
            .Replace("{modelDir}", model.Length == 0 ? "" :
                Directory.Exists(model) ? model : Path.GetDirectoryName(model) ?? "", StringComparison.Ordinal);

    private static void Append(Managed managed, string category, string? line)
    {
        if (line is null) return;
        lock (managed.Log)
        {
            managed.Log.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] {category}: " +
                (line.Length < 3000 ? line : line[..3000]));
            while (managed.Log.Count > 250) managed.Log.Dequeue();
        }
    }

    public async Task<object> StopAsync(string role, CancellationToken ct)
    {
        ValidateRole(role);
        await _guard.WaitAsync(ct);
        try
        {
            if (!_running.TryGetValue(role, out var managed) || managed.Process.HasExited)
                return new { role, running = false };
            var process = managed.Process;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
            Append(managed, "SYS", "Остановлен пользователем");
            return new { role, running = false };
        }
        finally { _guard.Release(); }
    }

    /// <summary>Удаляет завершённый GGUF, если он не используется запущенным процессом.</summary>
    public async Task<object> DeleteDownloadedWeightAsync(
        string file, ModelDownloadManager downloads, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("Выберите файл");
        string full = Path.GetFullPath(file);
        await _guard.WaitAsync(ct);
        try
        {
            if (_running.Values.Any(x => !x.Process.HasExited &&
                    string.Equals(x.ModelPath, full, StringComparison.Ordinal)))
                throw new InvalidOperationException("Остановите модель перед удалением весов");
            var profiles = await ProfilesAsync(ct);
            var result = await downloads.DeleteWeightAsync(full, ct);
            bool changed = false;
            foreach (var profile in profiles.Values)
            {
                if (!string.IsNullOrWhiteSpace(profile.ModelPath) &&
                    string.Equals(Path.GetFullPath(profile.ModelPath), full, StringComparison.Ordinal))
                {
                    profile.ModelPath = "";
                    changed = true;
                }
            }
            if (changed)
            {
                string temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(profiles,
                        new JsonSerializerOptions { WriteIndented = true }), ct);
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(temp, ConfigPath, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            return result;
        }
        finally { _guard.Release(); }
    }

    public async Task<object> RestartAsync(string role, CancellationToken ct)
    {
        await StopAsync(role, ct);
        return await StartAsync(role, ct);
    }

    public async Task<string[]> ProcessLogsAsync(string role, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        try {
            if (!_running.TryGetValue(role, out var process)) return [];
            lock (process.Log) return process.Log.ToArray();
        }
        finally { _guard.Release(); }
    }

    /// <summary>Используется мастером: обнаруживает раннее завершение runtime без ожидания таймаута.</summary>
    public async Task<bool> IsRunningAsync(string role, CancellationToken ct)
    {
        await _guard.WaitAsync(ct);
        try { return _running.TryGetValue(role, out var process) && !process.Process.HasExited; }
        finally { _guard.Release(); }
    }

    /// <summary>Возвращает PID и exit code, последние строки логов, health API и телеметрию каждой карты.</summary>
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var running = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        await _guard.WaitAsync(ct);
        try
        {
            foreach (var (role, managed) in _running)
            {
                var p = managed.Process;
                bool live = !p.HasExited;
                string[] logs;
                lock (managed.Log) logs = managed.Log.ToArray();
                running[role] = new {
                    running = live, pid = p.Id, modelPath = managed.ModelPath, exitCode = live ? (int?)null : p.ExitCode,
                    startedAt = managed.StartedAt, logs
                };
            }
        }
        finally { _guard.Release(); }

        // Телеметрия и health endpoints запрашиваются параллельно: мониторинг не блокируется
        // последовательными таймаутами STT, TTS и llama-server.
        var gpuTask = QueryGpusAsync(ct);
        var profiles = await ProfilesAsync(ct);
        var health = Roles.Select(async role =>
        {
            string url = profiles.TryGetValue(role, out var profile) &&
                !string.IsNullOrWhiteSpace(profile.HealthUrl) ? profile.HealthUrl : role switch
            {
                "llm" => settings.CurrentValue.LlmUrl.TrimEnd('/') + "/health",
                "stt" => settings.CurrentValue.SttUrl.TrimEnd('/') + "/health",
                _ => settings.CurrentValue.TtsUrl.TrimEnd('/') + "/health"
            };
            return (role, status: await CheckHealthAsync(url, ct));
        }).ToArray();
        var statuses = await Task.WhenAll(health);
        var api = statuses.ToDictionary(x => x.role, x => x.status);
        var gpus = await gpuTask;
        return new { running, gpus, api };
    }

    private async Task<object> CheckHealthAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return new { state = "invalid", httpCode = 0 };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await clients.CreateClient("model-health")
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new {
                state = response.IsSuccessStatusCode ? "ready" :
                    response.StatusCode is System.Net.HttpStatusCode.NotFound or
                        System.Net.HttpStatusCode.MethodNotAllowed ? "unknown" : "error",
                httpCode = (int)response.StatusCode
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { return new { state = "unreachable", httpCode = 0 }; }
    }

    private async Task<object[]> QueryGpusAsync(CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = {
                    "--query-gpu=index,uuid,name,temperature.gpu,utilization.gpu,memory.total,memory.used,power.draw,power.limit",
                    "--format=csv,noheader,nounits"
                }
            }) ?? throw new InvalidOperationException("nvidia-smi не найден");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            string output;
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
                output = await stdout; _ = await stderr;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return [];
            }
            if (p.ExitCode != 0) return [];
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Split(',', StringSplitOptions.TrimEntries))
                .Where(a => a.Length >= 9)
                .Select(a => (object)new {
                    index = Number(a[0]), uuid = a[1], name = a[2],
                    temperature = Number(a[3]), utilization = Number(a[4]),
                    totalMiB = Number(a[5]), usedMiB = Number(a[6]),
                    powerWatts = Floating(a[7]), powerLimitWatts = Floating(a[8])
                }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { logger.LogDebug(ex, "nvidia-smi unavailable"); return []; }
    }
    private static int? Number(string value) => int.TryParse(value, out var n) ? n : null;
    private static double? Floating(string value) => double.TryParse(value,
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static void ValidateRole(string role)
    {
        if (!Roles.Contains(role)) throw new ArgumentException("Неизвестный тип модели");
    }
    private static void Validate(string role, ModelLaunchProfile profile, bool requireFiles)
    {
        ValidateRole(role);
        if (profile is null) throw new ArgumentException("Нет профиля");
        if (role == "llm" && requireFiles && string.IsNullOrWhiteSpace(profile.ModelPath))
            throw new ArgumentException("Сначала выберите модель для LLM");
        if (!Path.IsPathFullyQualified(profile.Executable) ||
            (requireFiles && !File.Exists(profile.Executable)))
            throw new ArgumentException("Не найден исполняемый файл Linux");
        if (!Path.IsPathFullyQualified(profile.WorkingDirectory) ||
            (requireFiles && !Directory.Exists(profile.WorkingDirectory)))
            throw new ArgumentException("Не найден рабочий каталог");
        if (!string.IsNullOrEmpty(profile.ModelPath) &&
            (!Path.IsPathFullyQualified(profile.ModelPath) ||
             (requireFiles && !File.Exists(profile.ModelPath) && !Directory.Exists(profile.ModelPath))))
            throw new ArgumentException("Не найден файл или каталог модели");
        if (profile.Gpus is null || profile.Gpus.Length > 16 ||
            profile.Gpus.Any(g => g < 0 || g > 64) ||
            profile.Gpus.Distinct().Count() != profile.Gpus.Length)
            throw new ArgumentException("Некорректные индексы GPU");
        if (profile.GpuUuids is null || profile.GpuUuids.Length > 16 ||
            profile.GpuUuids.Any(x => !Regex.IsMatch(x, @"^GPU-[A-Za-z0-9-]{8,80}$")) ||
            profile.GpuUuids.Distinct(StringComparer.Ordinal).Count() != profile.GpuUuids.Length)
            throw new ArgumentException("Некорректные UUID GPU");
        if (profile.Arguments is null || profile.Arguments.Length > 128 ||
            profile.Arguments.Any(x => x is null || x.Length > 4096))
            throw new ArgumentException("Некорректные аргументы запуска");
        if (profile.Environment is null || profile.Environment.Count > 64 ||
            profile.Environment.Any(x => x.Value is null || x.Value.Length > 4096 ||
                !Regex.IsMatch(x.Key, @"^[A-Za-z_][A-Za-z0-9_]*$")))
            throw new ArgumentException("Некорректные переменные окружения");
        if (!string.IsNullOrEmpty(profile.HealthUrl) &&
            (!Uri.TryCreate(profile.HealthUrl, UriKind.Absolute, out var health) ||
                health.Scheme is not ("http" or "https") || health.UserInfo.Length != 0))
            throw new ArgumentException("Проверка готовности требует HTTP(S) URL");
    }
    public void Dispose()
    {
        foreach (var item in _running.Values)
        {
            try { if (!item.Process.HasExited) item.Process.Kill(entireProcessTree: true); } catch { }
            item.Process.Dispose();
        }
        _guard.Dispose();
    }
}

/// <summary>Настройки конкретного процесса модели.</summary>
public sealed class ModelLaunchProfile
{
    public string Executable { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public int[] Gpus { get; set; } = [0];
    public string[] GpuUuids { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new();
    public string HealthUrl { get; set; } = "";
}
