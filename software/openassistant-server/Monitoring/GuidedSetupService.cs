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
        new("gemma-4b-abl", "llm", "Gemma 3 4B · Abliterated", "Q8_0", "mradermacher/gemma-3-4b-abliterated-GGUF", "gemma-3-4b-abliterated.Q8_0.gguf", 0, "Q8_0", false, false),
        new("phi-mini-abl", "llm", "Phi-4 Mini · Abliterated", "Q8_0", "tensorblock/Phi-4-mini-instruct-abliterated-GGUF", "Phi-4-mini-instruct-abliterated-Q8_0.gguf", 0, "Q8_0", false, true),
        new("llama-3b-heretic", "llm", "Llama 3.2 3B · Heretic", "Q8_0", "tinyopsec/Llama-3.2-3B-Instruct-heretic-ablitered-uncensored-GGUF", "model_q8_0.gguf", 0, "Q8_0", false, true),
        new("llama-3b-abl", "llm", "Llama 3.2 3B · Abliterated", "Q8_0", "QuantFactory/Llama-3.2-3B-Instruct-abliterated-GGUF", "Llama-3.2-3B-Instruct-abliterated.Q8_0.gguf", 0, "Q8_0", false, false),
        new("gemma3n-e4b-abl", "llm", "Gemma 3n E4B · Abliterated", "Q8_0 · усиленная конфигурация", "mradermacher/Huihui-gemma-3n-E4B-it-abliterated-GGUF", "", 0, "Q8_0", false, true),
        new("gemma3n-e2b-abl", "llm", "Gemma 3n E2B · Abliterated", "Компактная мультимодальная модель · Q8_0", "mradermacher/Huihui-gemma-3n-E2B-it-abliterated-GGUF", "", 0, "Q8_0", false, true),
        new("gemma4-e4b-abl", "llm", "Gemma 4 E4B · Abliterated", "Q8_0 · увеличенная модель", "mradermacher/Gemma-4-E4B-Abliterated-GGUF", "", 0, "Q8_0", false, true),
        new("qwen2-7b-abl", "llm", "Qwen 2.5 7B · Abliterated", "Q8_0 · увеличенный объём весов", "tensorblock/Qwen2.5-7B-Instruct-abliterated-v2-GGUF", "Qwen2.5-7B-Instruct-abliterated-v2-Q8_0.gguf", 0, "Q8_0", false, false),
        new("mistral7-heretic", "llm", "Mistral 7B · Heretic", "Mistral · Q8_0", "mradermacher/Mistral-7B-Heretic-GGUF", "", 0, "Q8_0", true, true),
        new("phi4mini-heretic", "llm", "Phi-4 Mini · Heretic", "Microsoft Phi · Q8_0", "mradermacher/Phi-4-mini-instruct-heretic-GGUF", "", 0, "Q8_0", false, true),
        new("llama31-heretic", "llm", "Llama 3.1 8B · Heretic", "Meta Llama · Q8_0 · требовательна к VRAM", "mradermacher/Meta-Llama-3.1-8B-Instruct-heretic-GGUF", "", 0, "Q8_0", false, true),
        new("aya8-heretic", "llm", "Aya Expanse 8B · Heretic", "Cohere · Q8_0 · многоязычная", "mradermacher/aya-expanse-8b-heretic-GGUF", "", 0, "Q8_0", false, true),
        new("gemma3n-heresy", "llm", "Gemma 3n E4B · Absolute Heresy", "Gemma · Heretic · Q8_0", "mradermacher/gemma-3n-E4B-it-absolute-heresy-MPOA-GGUF", "", 0, "Q8_0", false, true),
        new("zephyr7-heresy", "llm", "Zephyr 7B · Absolute Heresy", "Zephyr / Mistral · Q8_0", "mradermacher/zephyr-7b-beta-absolute-heresy-GGUF", "", 0, "Q8_0", false, true),
        new("gigaam-rnnt", "stt", "GigaAM v3 · RNNT", "Распознавание русской речи", "salute-developers/GigaAM", "", 1.2, "FP32 / FP16", true, true),
        new("qwen-tts-fast-06", "tts", "Faster Qwen3-TTS · 0.6B", "Быстрый синтез · выбор готового голоса · CUDA Graphs", "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", "", 2.5, "FP16", true, true),
        new("qwen-tts-fast-17", "tts", "Faster Qwen3-TTS · 1.7B", "Более качественная речь · готовые голоса и управление манерой", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", "", 4.5, "FP16", false, true),
        new("qwen-tts-fast-design", "tts", "Faster Qwen3-TTS · VoiceDesign", "Создание тембра по текстовому описанию · 1.7B", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", "", 5.0, "FP16", false, true),
        new("qwen-tts-06", "tts", "Qwen3-TTS · 0.6B", "Обычный движок · готовые голоса", "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", "", 2.5, "FP16", false, false),
        new("qwen-tts-design", "tts", "Qwen3-TTS · VoiceDesign 1.7B", "Обычный движок · голос по описанию", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", "", 5.0, "FP16", false, false)
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
    public sealed record BeginRequest(string ModelId, string? GpuUuid, int? ContextSize, double? Temperature, string? ModelUrl = null);
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
        public long? VramMiB;
        public DateTimeOffset Started = DateTimeOffset.UtcNow;
        public Queue<string> Log = new();
        public HashSet<string> Completed = new(StringComparer.Ordinal);
        public CancellationTokenSource Stop = new();
    }
    private static object Snapshot(Job j) => new {
        id = j.Id, role = j.Role, modelId = j.ModelId, name = j.ModelName,
        status = j.Status, step = j.Step, description = j.Description,
        bytes = j.Bytes, total = j.Total, vramMiB = j.VramMiB, error = j.Error,
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
                    bytes = j.Bytes, total = j.Total, vramMiB = j.VramMiB, error = j.Error,
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
        var choice = !string.IsNullOrWhiteSpace(input.ModelUrl) && role == "llm"
            ? ParseCustomModel(input.ModelUrl)
            : GuidedModelCatalog.Require(input.ModelId, role);
        if (role == "llm" && (input.ContextSize is < 512 or > 32768 || input.Temperature is < 0 or > 2))
            throw new ArgumentException("Контекст или температура вне допустимого диапазона");
        if (!string.IsNullOrEmpty(input.GpuUuid) && input.GpuUuid != "cpu" &&
            !System.Text.RegularExpressions.Regex.IsMatch(input.GpuUuid, @"^GPU-[A-Za-z0-9-]{8,80}$"))
            throw new ArgumentException("Неверный идентификатор GPU");
        var j = new Job { Role = role, ModelId = choice.Id, ModelName = choice.Name };
        lock (_gate)
        {
            if (_jobs.TryGetValue(role, out var old) && old.Status == "running")
                throw new InvalidOperationException("Подготовка этой модели уже выполняется");
            _jobs[role] = j;
        }
        _ = Task.Run(() => ExecuteAsync(j, choice, input.GpuUuid == "cpu" ? null : input.GpuUuid, input.GpuUuid == "cpu", input.ContextSize ?? 4096, input.Temperature ?? 0.5));
        return Snapshot(j);
    }
    private static GuidedModelCatalog.Choice ParseCustomModel(string supplied)
    {
        // Только Hugging Face: исключаем локальные адреса и произвольные URL (SSRF).
        if (!Uri.TryCreate(supplied.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            url.Port != 443 || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Укажите HTTPS-ссылку на модель Hugging Face");
        string[] chunks = url.AbsolutePath.Trim('/').Split('/');
        if (chunks.Length < 2 || chunks.Any(x => x is "." or "..") ||
            chunks[0].Length is < 1 or > 80 || chunks[1].Length is < 1 or > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(chunks[0] + "/" + chunks[1],
                @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Некорректная ссылка на модель");
        string file = "";
        if (chunks.Length > 2)
        {
            if (chunks.Length < 4 || chunks[2] is not ("blob" or "resolve" or "tree") || chunks[3] != "main")
                throw new ArgumentException("Поддерживаются ссылки на репозиторий или отдельный GGUF из ветки main");
            if (chunks[2] == "tree" && chunks.Length == 4) return new GuidedModelCatalog.Choice("custom", "llm", chunks[1], "Пользовательская модель", chunks[0] + "/" + chunks[1], "", 0, "GGUF", false, false);
            file = string.Join("/", chunks.Skip(4));
            if (!file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                chunks.Skip(4).Any(x => x is "" or "." or ".."))
                throw new ArgumentException("Выберите файл GGUF");
        }
        return new GuidedModelCatalog.Choice("custom", "llm", chunks[1], "Пользовательская модель",
            chunks[0] + "/" + chunks[1], file, 0, "GGUF", false, false);
    }

    private async Task ExecuteAsync(Job j, GuidedModelCatalog.Choice choice, string? gpu, bool forceCpu, int contextSize, double temperature)
    {
        try
        {
            Stage(j, "prepare", "Проверяем сервер и доступные видеокарты");
            if (!forceCpu) gpu ??= await ChooseGpuAsync(j.Stop.Token);
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
        if (await processes.IsRunningAsync("llm", ct))
        {
            Stage(j, "prepare", "Останавливаем предыдущую LLM");
            await processes.StopAsync("llm", ct);
        }
        Stage(j, "download", "Скачиваем " + choice.Name);
        // Каждую модель проверяем по текущему манифесту Hugging Face.
        // Имя из каталога может измениться; никогда не скачиваем Q4 вместо Q8.
        var manifest = await downloads.InspectAsync(choice.Repo, "main", ct);
        var candidates = manifest.Files.Where(f =>
            f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
            f.Path.Contains("Q8_0", StringComparison.OrdinalIgnoreCase) &&
            f.Size <= 10L * 1024 * 1024 * 1024).ToArray();
        string selectedFile = candidates.FirstOrDefault(f =>
            f.Path.Equals(choice.File, StringComparison.OrdinalIgnoreCase))?.Path
            ?? candidates.OrderByDescending(f => f.Size).FirstOrDefault()?.Path
            ?? throw new InvalidOperationException(
                "В репозитории нет подходящего файла Q8_0. Выберите другую модель.");
        var selectedMetadata = manifest.Files.First(f => f.Path == selectedFile);
        lock (_gate) { j.Total = selectedMetadata.Size; j.Bytes = 0; }
        Output(j, "Файл модели: " + selectedFile);
        Output(j, "Размер по Hugging Face LFS: " + selectedMetadata.Size.ToString("N0") + " байт");

        var request = new ModelDownloadManager.DownloadRequest(choice.Repo, "main", [selectedFile]);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(await downloads.StartAsync(request, ct)));
        var id = result.RootElement.GetProperty("id").GetGuid();
        string folder = result.RootElement.GetProperty("path").GetString()!;
        await downloads.WaitAsync(id, (received, total, file) => {
            lock (_gate) { j.Bytes = received; j.Total = total; j.Description = "Загрузка · " + file; }
        }, ct);
        j.ModelPath = Path.Combine(folder, selectedFile);
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
        if (j.Gpu is not null)
        {
            var used = await processes.ProcessGpuMemoryMiBAsync("llm", j.Gpu, ct);
            lock (_gate) j.VramMiB = used;
            Output(j, used is null
                ? "nvidia-smi не сообщил VRAM процесса. Оценку не показываем."
                : $"VRAM llama-server: {used.Value} МиБ (по PID процесса).");
        }
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
        Stage(j, "install", "Проверяем синтаксис адаптера");
        var syntaxFiles = j.Role == "stt"
            ? new[] { adapter, Path.Combine(env.ContentRootPath, "Runtimes", "gigaam_torchaudio_fallback.py") }
            : new[] { adapter };
        await RunCommandAsync(j, python, ["-m", "py_compile", ..syntaxFiles], env.ContentRootPath, ct, 30);
        if (j.Role == "tts")
        {
            await InstallTtsDependenciesAsync(j, python,
                choice.Id.StartsWith("qwen-tts-fast-", StringComparison.Ordinal), ct);
        }
        else
        {
            // Установка STT независима от Qwen: не меняем работающую GigaAM.
            Stage(j, "install", "Проверяем зависимости GigaAM");
            try { await RunCommandAsync(j, python, ["-m", "pip", "--version"], env.ContentRootPath, ct, 30); }
            catch (InvalidOperationException) { await InstallAptAsync(j, ["python3-pip"], ct); }
            string marker = Path.Combine(env.ContentRootPath, "Runtimes", "stt.ready");
            if (!File.Exists(marker))
            {
                await RunCommandAsync(j, python, ["-m", "pip", "install", "--break-system-packages",
                    "--root-user-action=ignore", "--no-input",
                    "git+https://github.com/salute-developers/GigaAM.git", "numpy", "soundfile"],
                    env.ContentRootPath, ct, 1800);
                await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), ct);
            }
            var probe = "from gigaam_torchaudio_fallback import ensure_torchaudio; ensure_torchaudio(); import torch, gigaam, soundfile; print('torch',torch.__version__)";
            var runtimeDir = Path.GetDirectoryName(adapter)!;
            try { await RunCommandAsync(j, python, ["-c", probe], runtimeDir, ct, 90); }
            catch (InvalidOperationException)
            {
                await RunCommandAsync(j, python, ["-m", "pip", "install", "--break-system-packages",
                    "--root-user-action=ignore", "--no-input",
                    "git+https://github.com/salute-developers/GigaAM.git", "numpy", "soundfile"],
                    env.ContentRootPath, ct, 1800);
                await RunCommandAsync(j, python, ["-c", probe], runtimeDir, ct, 120);
            }
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
                ["OPENASSISTANT_MODEL"] = j.Role == "stt" ? "v3_e2e_rnnt" : choice.Repo,
                ["OPENASSISTANT_TTS_ENGINE"] = choice.Id.StartsWith("qwen-tts-fast-", StringComparison.Ordinal) && gpu is not null ? "faster" : "official"
            }
        };
        await processes.SaveAsync(j.Role, profile, ct);
        await processes.RestartAsync(j.Role, ct);
        if (j.Role == "stt") await settings.UpdateSttUrlAsync("http://127.0.0.1:8016", ct);
        else await settings.UpdateTtsUrlAsync("http://127.0.0.1:8020", ct);
        try { await WaitForHealthAsync(j, profile.HealthUrl, ct, 300); }
        catch (Exception ex) when (gpu is not null && (ex is TimeoutException or InvalidOperationException))
        {
            if (j.Role == "tts" && profile.Environment["OPENASSISTANT_TTS_ENGINE"] == "faster")
            {
                Output(j, "Ускоренный CUDA Graphs runtime недоступен: " + ex.Message);
                Stage(j, "fallback", "Пробуем обычный Qwen3-TTS на той же GPU");
                profile.Environment["OPENASSISTANT_TTS_ENGINE"] = "official";
                await processes.SaveAsync(j.Role, profile, ct);
                await processes.RestartAsync(j.Role, ct);
                try { await WaitForHealthAsync(j, profile.HealthUrl, ct, 300); }
                catch (Exception next) when (next is TimeoutException or InvalidOperationException)
                {
                    if (!await IsGpuFailureAsync(j.Role, ct)) throw;
                    Output(j, "Обычный GPU runtime также недоступен: " + next.Message);
                    Stage(j, "fallback", "Переключаем синтез на CPU");
                    profile.GpuUuids = [];
                    profile.Gpus = [];
                    profile.Environment["OPENASSISTANT_DEVICE"] = "cpu";
                    await processes.SaveAsync(j.Role, profile, ct);
                    await processes.RestartAsync(j.Role, ct);
                    lock (_gate) j.Gpu = null;
                    await WaitForHealthAsync(j, profile.HealthUrl, ct, 300);
                }
            }
            else
            {
                if (!await IsGpuFailureAsync(j.Role, ct)) throw;
                Stage(j, "fallback", "GPU-драйвер или PyTorch не поддерживает модель. Пробуем CPU.");
                profile.GpuUuids = [];
                profile.Gpus = [];
                profile.Environment["OPENASSISTANT_DEVICE"] = "cpu";
                await processes.SaveAsync(j.Role, profile, ct);
                await processes.RestartAsync(j.Role, ct);
                lock (_gate) j.Gpu = null;
                await WaitForHealthAsync(j, profile.HealthUrl, ct, 300);
            }
        }
        Stage(j, "test", "Проверяем готовность модели");
        if (j.Role == "tts")
        {
            var prior = voices.Read();
            await voices.SaveAsync(prior with { Mode = choice.Repo.EndsWith("VoiceDesign", StringComparison.Ordinal) ? "design" : "custom" }, ct);
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
    /// <summary>Самовосстанавливаемая установка Qwen3-TTS без ненадёжного tts.ready.</summary>
    private async Task InstallTtsDependenciesAsync(Job j, string python, bool fast, CancellationToken ct)
    {
        Stage(j, "install", "Проверяем окружение Qwen3-TTS");
        string probe = "from importlib.metadata import version, distributions; "
            + "from packaging.version import Version; "
            + "assert version('qwen-tts-hf') == '0.1.1.post1'; "
            + "assert not any(d.metadata['Name'].lower() == 'qwen-tts' for d in distributions()); "
            + "assert version('transformers') == '5.15.1'; "
            + "assert version('tokenizers') == '0.22.2'; "
            + "assert Version(version('torch').split('+')[0]) >= Version('2.5.1'); "
            + "import torch, tokenizers, transformers, qwen_tts, soundfile, scipy; "
            + "print('TTS OK', torch.__version__, transformers.__version__, tokenizers.__version__)";
        string fastProbe = probe + "; from faster_qwen3_tts import FasterQwen3TTS; "
            + "assert version('faster-qwen3-tts') == '0.5.4'";
        try
        {
            await RunCommandAsync(j, python, ["-c", fast ? fastProbe : probe],
                env.ContentRootPath, ct, 120);
            return;
        }
        catch (InvalidOperationException)
        {
            Output(j, "Несовместимое окружение, выполняем восстановление TTS.");
        }

        Stage(j, "install", "Подготавливаем системный pip");
        try { await RunCommandAsync(j, python, ["-m", "pip", "--version"], env.ContentRootPath, ct, 30); }
        catch (InvalidOperationException) { await InstallAptAsync(j, ["python3-pip"], ct); }

        // PEP 668 запрещает не только install, но и uninstall.
        Stage(j, "install", "Убираем конфликт qwen-tts / qwen-tts-hf");
        await RunCommandAsync(j, python, ["-m", "pip", "uninstall", "--break-system-packages",
            "--yes", "qwen-tts"], env.ContentRootPath, ct, 120);

        Stage(j, "install", "Устанавливаем совместимый стек Qwen3-TTS");
        var packages = new List<string> {
            "qwen-tts-hf==0.1.1.post1", "transformers==5.15.1",
            "tokenizers==0.22.2", "numpy", "soundfile", "scipy"
        };
        if (fast) packages.Add("faster-qwen3-tts==0.5.4");
        await RunCommandAsync(j, python, ["-m", "pip", "install", "--break-system-packages",
            "--root-user-action=ignore", "--no-input",
            "--upgrade-strategy", "only-if-needed", ..packages], env.ContentRootPath, ct, 1800);

        // Удаление qwen-tts могло стереть общий каталог qwen_tts.
        Stage(j, "install", "Восстанавливаем модуль qwen_tts");
        await RunCommandAsync(j, python, ["-m", "pip", "install", "--break-system-packages",
            "--root-user-action=ignore", "--no-input", "--force-reinstall", "--no-deps",
            "qwen-tts-hf==0.1.1.post1"], env.ContentRootPath, ct, 900);
        Stage(j, "install", "Проверяем совместимость библиотек TTS");
        await RunCommandAsync(j, python, ["-c", fast ? fastProbe : probe],
            env.ContentRootPath, ct, 120);
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
            {
                var lines = await processes.ProcessLogsAsync(j.Role, ct);
                var errors = lines.Where(x => x.Contains("] ERR:", StringComparison.Ordinal))
                    .TakeLast(10).Select(x => x.Length > 600 ? x[..600] : x);
                throw new InvalidOperationException("Движок завершился при загрузке: " +
                    string.Join("\n", errors));
            }
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
    private async Task<bool> IsGpuFailureAsync(string role, CancellationToken ct)
    {
        string[] logs = await processes.ProcessLogsAsync(role, ct);
        // SYS-строка "CUDA GPU" относится к метаданным и не означает ошибку GPU.
        var tail = string.Join("\n", logs.Where(x => x.Contains("] ERR:", StringComparison.Ordinal))
            .TakeLast(45));
        if (tail.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("ImportError", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("loading state_dict", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("Missing key(s)", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("Unexpected key(s)", StringComparison.OrdinalIgnoreCase))
            return false;
        return tail.Contains("CUDA error", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("torch.OutOfMemoryError", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("no kernel image", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("device-side assert", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("CUDA driver", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("CUBLAS_STATUS", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ReadTorchVersionAsync(string python, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(python) {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-c", "import torch;print(torch.__version__.split('+')[0])" }
            });
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string value = (await p.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
            await p.WaitForExitAsync(timeout.Token);
            return p.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+\.\d+$")
                ? value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException) { return null; }
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
