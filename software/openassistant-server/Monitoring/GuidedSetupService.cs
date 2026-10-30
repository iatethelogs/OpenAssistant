using System.Diagnostics;
using System.Text.Json;
using OpenAssistant.Server.Models;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Audio;

namespace OpenAssistant.Server.Monitoring;

/// <summary>Предустановленные варианты для одного GPU с 8 GiB; указаны конкретные файлы.</summary>
public static class GuidedModelCatalog
{
    public sealed record Choice(string Id, string Role, string Name, string Summary,
        string Repo, string File, double DiskGb, string Quality, bool Default, bool Recommended);
    public static readonly Choice[] All = [
        new("gemma-4b", "llm", "Gemma 3 · 4B", "Сбалансированная модель для ассистента", "unsloth/gemma-3-4b-it-GGUF", "gemma-3-4b-it-Q8_0.gguf", 4.13, "Q8_0", true, true),
        new("phi-mini", "llm", "Phi-4 Mini · 3.8B", "Компактная, хороша для технических вопросов", "unsloth/Phi-4-mini-instruct-GGUF", "Phi-4-mini-instruct.Q8_0.gguf", 4.08, "Q8_0", false, true),
        new("llama-3b", "llm", "Llama 3.2 · 3B", "Лёгкая универсальная модель", "bartowski/Llama-3.2-3B-Instruct-GGUF", "Llama-3.2-3B-Instruct-Q8_0.gguf", 3.42, "Q8_0", false, false),
        new("smollm-3b", "llm", "SmolLM3 · 3B", "Для экспериментов и быстрого отклика", "ggml-org/SmolLM3-3B-GGUF", "SmolLM3-Q8_0.gguf", 3.28, "Q8_0", false, false),
        new("gemma-1b", "llm", "Gemma 3 · 1B", "Минимальные требования и быстрый отклик", "unsloth/gemma-3-1b-it-GGUF", "gemma-3-1b-it-Q8_0.gguf", 1.07, "Q8_0", false, false),
        new("gigaam-rnnt", "stt", "GigaAM v3 · RNNT", "Распознавание русской речи с пунктуацией", "salute-developers/GigaAM", "", 1.2, "FP32 / FP16", true, true),
        new("qwen-tts-06", "tts", "Qwen3-TTS · 0.6B", "Выбор готового голоса, экономия VRAM", "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", "", 2.5, "FP16", true, true),
        new("qwen-tts-design", "tts", "Qwen3-TTS · VoiceDesign 1.7B", "Создание голоса по текстовому описанию; требует больше памяти и может работать на CPU", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", "", 5.0, "FP16", false, false)
    ];
    public static Choice Require(string id, string role) =>
        All.FirstOrDefault(x => x.Id == id && x.Role == role)
        ?? throw new ArgumentException("Неизвестный вариант модели");
}

/// <summary>Запускает реальные этапы установки и подготовки движка, без ручных путей в мастере.</summary>
public sealed class GuidedSetupService(
    ModelDownloadManager downloads, ModelProcessManager processes,
    SettingsStore settings, DiagnosticService diagnostics, VoiceProfileStore voices,
    IWebHostEnvironment env, ILogger<GuidedSetupService> log)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private const string LlmDir = "/opt/llama.cpp";
    public sealed record BeginRequest(string ModelId, string? GpuUuid, int? ContextSize, double? Temperature);
    public sealed record TestRequest(string Prompt);
    private sealed class Job
    {
        public required string Role;
        public required string ModelId;
        public required string ModelName;
        public Guid Id = Guid.NewGuid();
        public string Step = "starting";
        public string Status = "running";
        public string Description = "Подготовка…";
        public string? Error;
        public string? ModelPath;
        public string? Runtime;
        public string? Gpu;
        public string? TestResult;
        public long Bytes;
        public long Total;
        public DateTimeOffset Started = DateTimeOffset.UtcNow;
        public Queue<string> Log = new();
        public HashSet<string> Completed = new(StringComparer.Ordinal);
        public CancellationTokenSource Stop = new();
    }
    private static object Snapshot(Job j) => new {
        id = j.Id, role = j.Role, modelId = j.ModelId, name = j.ModelName,
        status = j.Status, step = j.Step, description = j.Description,
        bytes = j.Bytes, total = j.Total, error = j.Error,
        path = j.ModelPath, runtime = j.Runtime, gpu = j.Gpu, testResult = j.TestResult,
        startedAt = j.Started, completedSteps = j.Completed.OrderBy(x => x).ToArray(), logs = j.Log.ToArray()
    };
    public object Status()
    {
        lock (_gate)
            return new { jobs = _jobs.Values.Select(Snapshot).ToArray(),
                catalog = GuidedModelCatalog.All,
                setupComplete = _jobs.TryGetValue("llm", out var l) && l.Status == "ready"
                    && _jobs.TryGetValue("stt", out var st) && st.Status == "ready"
                    && _jobs.TryGetValue("tts", out var t) && t.Status == "ready" };
    }
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        Job[] active;
        lock (_gate) active = _jobs.Values.ToArray();
        var visible = new List<object>();
        foreach (var j in active)
        {
            string[] logs = await processes.ProcessLogsAsync(j.Role, ct);
            lock (_gate)
            {
                visible.Add(new {
                    id = j.Id, role = j.Role, modelId = j.ModelId, name = j.ModelName,
                    status = j.Status, step = j.Step, description = j.Description,
                    bytes = j.Bytes, total = j.Total, error = j.Error,
                    path = j.ModelPath, runtime = j.Runtime, gpu = j.Gpu, testResult = j.TestResult,
                    startedAt = j.Started, completedSteps = j.Completed.OrderBy(x => x).ToArray(), logs = j.Log.Concat(logs).TakeLast(120).ToArray()
                });
            }
        }
        return new { jobs = visible, catalog = GuidedModelCatalog.All };
    }

    private void Stage(Job j, string step, string message)
    {
        lock (_gate)
        {
            // Этап отмечаем успешным только когда установка действительно дошла до следующего.
            if (step != "failed" && step != "cancelled" && step != "fallback" &&
                j.Step != step && j.Step != "starting" && j.Step != "failed" &&
                j.Step != "cancelled")
                j.Completed.Add(j.Step);
            if (step == "ready") j.Completed.Add("test");
            j.Step = step; j.Description = message;
            j.Log.Enqueue($"{DateTimeOffset.Now:HH:mm:ss} · {message}");
            while (j.Log.Count > 160) j.Log.Dequeue();
        }
    }
    private void Output(Job j, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_gate)
        {
            j.Log.Enqueue(message.Length > 350 ? message[..350] : message);
            while (j.Log.Count > 160) j.Log.Dequeue();
        }
    }
    public object Begin(string role, BeginRequest input)
    {
        if (role is not ("llm" or "stt" or "tts")) throw new ArgumentException("Недопустимая роль");
        if (input is null) throw new ArgumentException("Нужно выбрать модель");
        var choice = GuidedModelCatalog.Require(input.ModelId, role);
        if (role == "llm" && (input.ContextSize is < 512 or > 32768 || input.Temperature is < 0 or > 2))
            throw new ArgumentException("Контекст или температура вне допустимого диапазона");
        if (!string.IsNullOrEmpty(input.GpuUuid) &&
            !System.Text.RegularExpressions.Regex.IsMatch(input.GpuUuid, @"^GPU-[A-Za-z0-9-]{8,80}$"))
            throw new ArgumentException("Неверный идентификатор GPU");
        var j = new Job { Role = role, ModelId = choice.Id, ModelName = choice.Name };
        lock (_gate)
        {
            if (_jobs.TryGetValue(role, out var old) && old.Status == "running")
                throw new InvalidOperationException("Подготовка этой модели уже выполняется");
            _jobs[role] = j;
        }
        _ = Task.Run(() => ExecuteAsync(j, choice, input.GpuUuid, input.ContextSize ?? 4096, input.Temperature ?? 0.5));
        return Snapshot(j);
    }
    private async Task ExecuteAsync(Job j, GuidedModelCatalog.Choice choice, string? gpu, int contextSize, double temperature)
    {
        try
        {
            Stage(j, "prepare", "Проверяем сервер и доступные видеокарты");
            gpu ??= await ChooseGpuAsync(j.Stop.Token);
            lock (_gate) j.Gpu = gpu;
            if (j.Role == "llm") await SetupLlmAsync(j, choice, gpu, contextSize, temperature, j.Stop.Token);
            else await SetupSpeechAsync(j, choice, gpu, j.Stop.Token);
            Stage(j, "ready", j.Role == "stt" ? "Сервис распознавания готов. Проверьте его записью микрофона." : "Модель запущена и проверена тестовым запросом");
            lock (_gate) j.Status = "ready";
        }
        catch (OperationCanceledException)
        {
            Stage(j, "cancelled", "Настройка отменена");
            lock (_gate) j.Status = "cancelled";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Guided setup failed for {Role}", j.Role);
            Output(j, "ОШИБКА: " + ex.GetType().Name + ": " + ex.Message);
            Stage(j, "failed", "Не удалось подготовить " + choice.Name);
            lock (_gate) { j.Status = "failed"; j.Error = ex.Message; }
        }
        finally { j.Stop.Dispose(); }
    }
    /// <summary>Автоматически выбирает самую свободную видеокарту, если пользователь её не указывал.</summary>
    private async Task<string?> ChooseGpuAsync(CancellationToken ct)
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "--query-gpu=uuid,memory.free", "--format=csv,noheader,nounits" }
            };
            using var p = Process.Start(start);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            string output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0) return null;
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split(',', StringSplitOptions.TrimEntries))
                .Where(x => x.Length >= 2 && x[0].StartsWith("GPU-", StringComparison.Ordinal))
                .Select(x => new { Id = x[0], Free = int.TryParse(x[1], out var n) ? n : 0 })
                .OrderByDescending(x => x.Free).FirstOrDefault()?.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        { return null; }
    }
    private async Task SetupLlmAsync(Job j, GuidedModelCatalog.Choice choice, string? gpu, int contextSize, double temperature, CancellationToken ct)
    {
        Stage(j, "download", "Скачиваем " + choice.Name);
        var request = new ModelDownloadManager.DownloadRequest(choice.Repo, "main", [choice.File]);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(await downloads.StartAsync(request, ct)));
        var id = result.RootElement.GetProperty("id").GetGuid();
        string folder = result.RootElement.GetProperty("path").GetString()!;
        await downloads.WaitAsync(id, (received, total, file) => {
            lock (_gate) { j.Bytes = received; j.Total = total; j.Description = "Загрузка · " + file; }
        }, ct);
        j.ModelPath = Path.Combine(folder, choice.File);
        if (!File.Exists(j.ModelPath)) throw new FileNotFoundException("Файл модели не появился после скачивания");
        Stage(j, "runtime", "Проверяем llama.cpp и необходимые компоненты");
        var runtime = await FindOrBuildLlamaAsync(j, gpu is not null, ct);
        string executable = runtime.Executable;
        if (!runtime.Cuda && gpu is not null)
        {
            Output(j, "CUDA runtime не удалось подготовить. Модель будет запущена на CPU.");
            gpu = null;
            lock (_gate) j.Gpu = null;
        }
        j.Runtime = executable;
        Stage(j, "launch", "Запускаем llama.cpp с выбранной моделью");
        var profile = new ModelLaunchProfile {
            Executable = executable, WorkingDirectory = Path.GetDirectoryName(executable)!,
            ModelPath = j.ModelPath, GpuUuids = gpu is null ? [] : [gpu], Gpus = [],
            HealthUrl = "http://127.0.0.1:8080/health",
            Arguments = ["--model", "{model}", "--alias", "local",
                "--host", "127.0.0.1", "--port", "8080", "--ctx-size", contextSize.ToString(),
                "--n-gpu-layers", gpu is null ? "0" : "99", "--threads", "16", "--parallel", "1", "--jinja"]
        };
        await processes.SaveAsync("llm", profile, ct);
        await processes.RestartAsync("llm", ct);
        await settings.UpdateLlmUrlAsync("http://127.0.0.1:8080", ct);
        await settings.UpdateLlmTemperatureAsync(temperature, ct);
        try { await WaitForHealthAsync(j, "http://127.0.0.1:8080/health", ct); }
        catch (Exception ex) when (gpu is not null && ex is TimeoutException or InvalidOperationException)
        {
            Output(j, "GPU-запуск завершился неудачно: " + ex.Message);
            Output(j, "Перезапускаем ту же модель на CPU");
            profile.GpuUuids = [];
            profile.Gpus = [];
            profile.Arguments = profile.Arguments.Select(x => x == "99" ? "0" : x).ToArray();
            await processes.SaveAsync("llm", profile, ct);
            await processes.RestartAsync("llm", ct);
            lock (_gate) j.Gpu = null;
            await WaitForHealthAsync(j, "http://127.0.0.1:8080/health", ct);
        }
        Stage(j, "test", "Проверяем ответ модели");
        var response = await diagnostics.TestLlmAsync("Привет! Расскажи, что умеет OpenAssistant, одним предложением.", "local", ct);
        lock (_gate) j.TestResult = JsonSerializer.Serialize(response);
    }
    private async Task SetupSpeechAsync(Job j, GuidedModelCatalog.Choice choice, string? gpu, CancellationToken ct)
    {
        string python = "/usr/bin/python3";
        if (!File.Exists(python))
        {
            Stage(j, "install", "Устанавливаем Python");
            await InstallAptAsync(j, ["python3", "python3-pip"], ct);
            if (!File.Exists(python))
                throw new InvalidOperationException("Не удалось установить системный Python");
        }
        string adapter = Path.Combine(env.ContentRootPath, "Runtimes", j.Role == "stt" ? "gigaam_adapter.py" : "qwen_tts_adapter.py");
        if (!File.Exists(adapter)) throw new FileNotFoundException("Нет адаптера нейросети", adapter);
        Stage(j, "install", "Проверяем Python и устанавливаем зависимости");
        if (j.Role == "stt" && !await CommandExistsAsync("git", "--version", ct))
            await InstallAptAsync(j, ["git"], ct);
        if (j.Role == "stt" && !await CommandExistsAsync("ffmpeg", "-version", ct))
            await InstallAptAsync(j, ["ffmpeg"], ct);
        string packageMarker = Path.Combine(env.ContentRootPath, "Runtimes", j.Role + ".ready");
        if (!File.Exists(packageMarker))
        {
            Stage(j, "install", "Устанавливаем зависимости " + choice.Name + " в системный Python");
            if (!await CommandExistsAsync(python, "--version", ct))
                throw new InvalidOperationException("Python3 недоступен");
            try { await RunCommandAsync(j, python, ["-m", "pip", "--version"], env.ContentRootPath, ct, 30); }
            catch (InvalidOperationException)
            {
                await InstallAptAsync(j, ["python3-pip", "ffmpeg"], ct);
            }
            string[] packages = j.Role == "stt"
                ? ["git+https://github.com/salute-developers/GigaAM.git", "numpy", "soundfile"]
                : ["qwen-tts", "numpy", "soundfile"];
            // Не создаём venv и не устанавливаем библиотеки на диск проекта.
            await RunCommandAsync(j, python,
                ["-m", "pip", "install", "--break-system-packages", "--no-input", ..packages], env.ContentRootPath, ct, 1800);
            await File.WriteAllTextAsync(packageMarker, DateTimeOffset.UtcNow.ToString("O"), ct);
        }
        Stage(j, "launch", "Запускаем сервис; веса будут загружены автоматически");
        string port = j.Role == "stt" ? "8016" : "8020";
        var profile = new ModelLaunchProfile {
            Executable = python, WorkingDirectory = env.ContentRootPath,
            ModelPath = adapter, GpuUuids = gpu is null ? [] : [gpu], Gpus = [],
            HealthUrl = $"http://127.0.0.1:{port}/health",
            Arguments = [adapter, "--port", port],
            Environment = new Dictionary<string,string> {
                ["OPENASSISTANT_DEVICE"] = gpu is null ? "cpu" : "cuda",
                ["OPENASSISTANT_MODEL"] = j.Role == "stt" ? "v3_e2e_rnnt" : choice.Repo
            }
        };
        await processes.SaveAsync(j.Role, profile, ct);
        await processes.RestartAsync(j.Role, ct);
        if (j.Role == "stt") await settings.UpdateSttUrlAsync("http://127.0.0.1:8016", ct);
        else await settings.UpdateTtsUrlAsync("http://127.0.0.1:8020", ct);
        try { await WaitForHealthAsync(j, profile.HealthUrl, ct, 300); }
        catch (Exception ex) when (gpu is not null && (ex is TimeoutException or InvalidOperationException))
        {
            Stage(j, "fallback", "GPU-драйвер или PyTorch не поддерживает модель. Пробуем CPU.");
            profile.GpuUuids = [];
            profile.Gpus = [];
            profile.Environment["OPENASSISTANT_DEVICE"] = "cpu";
            await processes.SaveAsync(j.Role, profile, ct);
            await processes.RestartAsync(j.Role, ct);
            lock (_gate) j.Gpu = null;
            await WaitForHealthAsync(j, profile.HealthUrl, ct, 300);
        }
        Stage(j, "test", "Проверяем готовность модели");
        if (j.Role == "tts")
        {
            var prior = voices.Read();
            await voices.SaveAsync(prior with { Mode = choice.Id == "qwen-tts-design" ? "design" : "custom" }, ct);
        }
        if (j.Role == "tts")
        {
            var test = await diagnostics.TestTtsAsync("Здравствуйте! Голосовой синтез готов.", "Ryan", null, ct);
            lock (_gate) j.TestResult = JsonSerializer.Serialize(new {
                milliseconds = test.Milliseconds, duration = test.DurationSeconds, rtf = test.Rtf });
        }
        else
            lock (_gate) j.TestResult = "Готово к проверке микрофона из браузера";
    }
    private async Task WaitForHealthAsync(Job j, string url, CancellationToken ct, int limitSeconds = 150)
    {
        Stage(j, "loading", "Ждём загрузку весов в память и запуска API");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < limitSeconds / 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode && await processes.IsRunningAsync(j.Role, ct)) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            if (i > 3 && !await processes.IsRunningAsync(j.Role, ct))
                throw new InvalidOperationException("Движок завершился при загрузке. Подробности в журнале процесса.");
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("Модель не стала доступна по HTTP. Проверьте логи процесса в расширенных настройках.");
    }
    private async Task<(string Executable, bool Cuda)> FindOrBuildLlamaAsync(Job j, bool gpu, CancellationToken ct)
    {
        string[] locations = [
            "/opt/llama.cpp/build/bin/llama-server",
            "/opt/llama.cpp/llama-server",
            "/usr/local/bin/llama-server",
            "/usr/bin/llama-server",
            Path.Combine(env.ContentRootPath, "runtimes", "llama.cpp", "build", "bin", "llama-server")
        ];
        foreach (var path in locations)
            if (File.Exists(path))
            {
                Output(j, "Используем установленный движок: " + path);
                return (path, gpu);
            }

        Stage(j, "runtime", "Установка llama.cpp");
        if (!await CommandExistsAsync("git", "--version", ct) ||
            !await CommandExistsAsync("cmake", "--version", ct))
        {
            Stage(j, "runtime", "Устанавливаем инструменты сборки");
            await InstallAptAsync(j, ["git", "cmake", "build-essential", "libcurl4-openssl-dev",
                "pkg-config"], ct);
        }
        string source = Path.Combine(env.ContentRootPath, "runtimes", "llama.cpp");
        if (!Directory.Exists(Path.Combine(source, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Stage(j, "runtime", "Скачиваем исходники llama.cpp");
            await RunCommandAsync(j, "git", ["clone", "--depth", "1",
                "https://github.com/ggml-org/llama.cpp.git", source], env.ContentRootPath, ct);
        }

        string nvcc = File.Exists("/usr/local/cuda/bin/nvcc") ?
            "/usr/local/cuda/bin/nvcc" : "nvcc";
        if (gpu && !await CommandExistsAsync(nvcc, "--version", ct))
        {
            Output(j, "CUDA Toolkit (nvcc) не найден. Сборка GPU потребовала бы вмешательства в установленный драйвер. Выполняем автоматическую сборку CPU.");
            gpu = false;
        }
        string build = Path.Combine(source, "build");
        async Task CompileAsync(bool cuda)
        {
            Stage(j, "runtime", cuda ? "Собираем llama.cpp с CUDA" : "Собираем llama.cpp для CPU");
            await RunCommandAsync(j, "cmake", ["-S", source, "-B", build,
                "-DCMAKE_BUILD_TYPE=Release", "-DGGML_CUDA=" + (cuda ? "ON" : "OFF")],
                source, ct);
            await RunCommandAsync(j, "cmake",
                ["--build", build, "--target", "llama-server", "--parallel", "24"], source, ct, 1800);
        }
        if (gpu)
        {
            try { await CompileAsync(true); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                Output(j, "Не удалось собрать CUDA runtime: " + ex.Message);
                Output(j, "Пробуем CPU-сборку, не изменяя драйвер NVIDIA.");
                gpu = false;
                await CompileAsync(false);
            }
        }
        else await CompileAsync(false);
        string result = Path.Combine(build, "bin", "llama-server");
        if (!File.Exists(result))
            throw new FileNotFoundException("Сборка завершена, но llama-server не найден", result);
        return (result, gpu);
    }
    private async Task InstallAptAsync(Job j, string[] packages, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root")
            throw new InvalidOperationException("Для автоматической установки пакетов Ubuntu запусти сервер с правами администратора");
        Output(j, "Системные зависимости: " + string.Join(", ", packages));
        await RunCommandAsync(j, "apt-get", ["update", "-qq"], env.ContentRootPath, ct, 600);
        await RunCommandAsync(j, "apt-get", ["install", "-y", "-qq", ..packages], env.ContentRootPath, ct, 900);
    }
    private async Task<bool> CommandExistsAsync(string executable, string argument, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(executable) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { argument }
            });
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private async Task RunCommandAsync(Job j, string executable, string[] args,
        string cwd, CancellationToken ct, int timeoutSeconds = 600)
    {
        Output(j, "$ " + Path.GetFileName(executable) + " " + string.Join(" ", args.Select(x => x.Contains("://") ? "[URL]" : x)));
        // Команда относится к текущему этапу; не сбрасываем его на "runtime".
        var psi = new ProcessStartInfo(executable) {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = cwd, CreateNoWindow = true
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить " + executable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        Task stdout = ConsumeAsync(p.StandardOutput, j, timeout.Token);
        Task stderr = ConsumeAsync(p.StandardError, j, timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Превышено время подготовки движка " + executable);
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(executable) + " завершился с кодом " + p.ExitCode +
                ". Подробности в журнале установки");
    }
    private async Task ConsumeAsync(StreamReader reader, Job job, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
            Output(job, line);
    }
}
