using OpenAssistant.Server.Audio;
using OpenAssistant.Server.ConsoleUi;
using System.Net;
using OpenAssistant.Server.Models;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;
using OpenAssistant.Server.Transport;
using OpenAssistant.Server.Monitoring;

var builder = WebApplication.CreateBuilder(args);
var webLogs = new DashboardLogProvider();
builder.Logging.ClearProviders();
builder.Logging.AddProvider(webLogs);
builder.Services.AddSingleton(webLogs);
builder.Services.AddSingleton<GuidedSetupService>();
// Models are started only explicitly by the operator or the setup job.
builder.Configuration.AddJsonFile("settings.local.json", optional: true, reloadOnChange: true);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ModelProcessManager>();
builder.Services.AddSingleton<ModelDownloadManager>();
builder.Services.AddSingleton<AgentToolService>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<ConversationMemoryStore>();
builder.Services.AddSingleton<VerifiedHomeAssistantService>();
builder.Services.AddSingleton<DiagnosticService>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddHttpClient("model-health", c => c.Timeout = TimeSpan.FromSeconds(4));
builder.Services.AddHttpClient("hf-metadata", c => c.Timeout = TimeSpan.FromSeconds(65));
builder.Services.AddHttpClient("hf-download", c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<RadioBrowserService>();
builder.Services.AddSingleton<YandexMusicService>();
builder.Services.AddSingleton<YandexDeviceAuthService>();
builder.Services.AddSingleton<HomeAssistantConnectionService>();
builder.Services.AddHttpClient("radio-browser", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAssistant/0.1"));
builder.Services.AddHttpClient("yandex-music");
builder.Services.AddHttpClient("yandex-oauth", c => c.Timeout = TimeSpan.FromSeconds(12));
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("OpenAssistant"));
builder.Services.AddHttpClient("llm");
builder.Services.AddHttpClient("stt");
builder.Services.AddHttpClient("tts");
builder.Services.AddHttpClient("homeassistant");
builder.Services.AddSingleton<VoiceProfileStore>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<IAssistantTool, HomeAssistantTool>();
builder.Services.AddSingleton<IAssistantTool, RadioTool>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<AssistantPipeline>();
builder.Services.AddSingleton<ConversationLog>();
var app = builder.Build();
// блокируем запросы к локальному api из посторонних веб-страниц.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method is "POST" or "PUT" or "DELETE")
    {
        string expected = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        if (ctx.Request.Headers.Origin.Count > 0 &&
            !string.Equals(ctx.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "openassistant-server" }));
// панель доступна только локально; не выдаём конфигурацию и токены по сети.
var localOnly = new Func<HttpContext, bool>(ctx => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip));
app.MapGet("/api/control/assistant-defaults", (HttpContext ctx) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    var defaults = new ServerOptions();
    return Results.Ok(new {
        defaults.SystemPrompt, defaults.LlmTemperature, defaults.LlmMaxTokens,
        defaults.LlmTopP, defaults.LlmTopK, defaults.LlmRepeatPenalty
    });
});
app.MapGet("/api/settings", (HttpContext ctx, SettingsStore store) =>
    localOnly(ctx) ? Results.Ok(store.PublicSettings()) : Results.StatusCode(403));
app.MapPost("/api/settings", async (HttpContext ctx, SettingsUpdate settings, SettingsStore store, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    if (ctx.Request.Headers.Origin.Count > 0 &&
        !string.Equals(ctx.Request.Headers.Origin.ToString(), $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(403);
    try { await store.SaveAsync(settings, ct); return Results.Ok(new { saved = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
// Hugging Face: запуск задач скачивания не блокирует веб-запрос, весь прогресс доступен через polling.
// Единая панель: skills, настройка оборудования и прямые benchmark-запросы к runtime.
// Пошаговая настройка: каждый этап выполняется сервером и возвращает наблюдаемый статус.
app.MapGet("/api/setup/status", async (HttpContext ctx, GuidedSetupService guided, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(await guided.StatusAsync(ct)) : Results.StatusCode(403));
app.MapGet("/api/setup/voice", (HttpContext ctx, VoiceProfileStore voices) =>
    localOnly(ctx) ? Results.Ok(voices.Read()) : Results.StatusCode(403));
app.MapPut("/api/setup/voice", async (
    HttpContext ctx, VoiceProfileStore.Profile profile, VoiceProfileStore voices, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await voices.SaveAsync(profile, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/setup/catalog", (HttpContext ctx) =>
    localOnly(ctx) ? Results.Ok(GuidedModelCatalog.All) : Results.StatusCode(403));
app.MapPost("/api/setup/{role}", (HttpContext ctx, string role,
    GuidedSetupService.BeginRequest input, GuidedSetupService guided) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(guided.Begin(role, input)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/setup/logs", (HttpContext ctx, DashboardLogProvider logs, int? take) =>
    localOnly(ctx) ? Results.Ok(logs.Recent(take ?? 300)) : Results.StatusCode(403));
app.MapGet("/api/memory/{profile}", async (
    HttpContext ctx, string profile, ConversationMemoryStore memory, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(new { profile, exchanges = await memory.CountAsync(profile, ct) }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/memory/{profile}", async (
    HttpContext ctx, string profile, ConversationMemoryStore memory, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { await memory.ClearAsync(profile, ct); return Results.Ok(new { cleared = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/control/skills", async (HttpContext ctx, AgentToolService skills, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(await skills.ListAsync(ct)) : Results.StatusCode(403));
app.MapPut("/api/control/skills", async (
    HttpContext ctx, Dictionary<string, bool> enabled, AgentToolService skills, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await skills.SaveAsync(enabled, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/control/catalog", (HttpContext ctx) =>
    localOnly(ctx) ? Results.Ok(new object[] {
        new { id = "gemma3-4b", role = "llm", name = "Gemma 3 4B · GGUF", repo = "unsloth/gemma-3-4b-it-GGUF", hint = "Выберите один GGUF, предпочтительно Q8_0 либо F16 при достаточной VRAM." },
        new { id = "gemma3-12b", role = "llm", name = "Gemma 3 12B · GGUF", repo = "unsloth/gemma-3-12b-it-GGUF", hint = "Для нескольких GPU; скачивайте нужную квантизацию и все части, если файл разделён." },
        new { id = "qwen25-3b", role = "llm", name = "Qwen 2.5 3B · GGUF", repo = "bartowski/Qwen2.5-3B-Instruct-GGUF", hint = "Для теста скорости; доступны Q8_0 и F16." },
        new { id = "gigaam", role = "stt", name = "GigaAM v3 · ASR", repo = "ai-sage/GigaAM-v3", hint = "Нужен полный комплект весов и отдельный HTTP runtime." },
        new { id = "gigaam-onnx", role = "stt", name = "GigaAM v3 · ONNX", repo = "istupakov/gigaam-v3-onnx", hint = "Нужен совместимый ONNX STT HTTP runtime." },
        new { id = "qwen3tts-base", role = "tts", name = "Qwen3-TTS 0.6B · Base", repo = "Qwen/Qwen3-TTS-12Hz-0.6B-Base", hint = "Для клонирования голоса, загрузите весь репозиторий и подключите TTS runtime." },
        new { id = "qwen3tts-voice", role = "tts", name = "Qwen3-TTS 0.6B · CustomVoice", repo = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice", hint = "Встроенные голоса; скачайте весь репозиторий." }
    }) : Results.StatusCode(403));
app.MapGet("/api/control/llama/scan", (HttpContext ctx, SetupService setup) =>
    localOnly(ctx) ? Results.Ok(setup.LlamaExecutables()) : Results.StatusCode(403));
app.MapPost("/api/control/llama/start", async (
    HttpContext ctx, LlamaSetupInput input, SetupService setup, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await setup.ConfigureLlamaAsync(input, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/control/system", (HttpContext ctx, IWebHostEnvironment env) =>
    localOnly(ctx) ? Results.Ok(SystemMetrics.Snapshot(env)) : Results.StatusCode(403));
app.MapGet("/api/control/devices", (HttpContext ctx, DeviceRegistry devices) =>
    localOnly(ctx) ? Results.Ok(devices.Overview()) : Results.StatusCode(403));
app.MapPost("/api/control/device/key", async (HttpContext ctx, SetupService setup, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(await setup.GenerateDeviceKeyAsync(ct)) : Results.StatusCode(403));
app.MapPost("/api/control/test/llm", async (
    HttpContext ctx, DiagnosticService.LlmTestInput input, DiagnosticService diagnostic, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await diagnostic.TestLlmAsync(input.Prompt, input.Model, ct)); }
    catch (Exception ex) when (ex is ArgumentException or HttpRequestException or
        InvalidOperationException or OperationCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/control/test/tts", async (
    HttpContext ctx, DiagnosticService.TtsTestInput input, DiagnosticService diagnostic, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try
    {
        var test = await diagnostic.TestTtsAsync(input.Text, input.Voice, input.Model, ct);
        ctx.Response.Headers["X-Synthesis-Ms"] = test.Milliseconds.ToString();
        ctx.Response.Headers["X-Audio-Seconds"] = test.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-RTF"] = test.Rtf.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.File(test.Wave, "audio/wav");
    }
    catch (Exception ex) when (ex is ArgumentException or HttpRequestException or
        InvalidDataException or InvalidOperationException or OperationCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/control/test/stt", async (
    HttpContext ctx, string? model, DiagnosticService diagnostic, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    if (ctx.Request.ContentLength is null or < 3200 or > 1048576)
        return Results.BadRequest(new { error = "Ожидается 16-bit mono PCM, максимум 1 МБ" });
    try
    {
        await using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length > 1024 * 1024) return Results.BadRequest(new { error = "Запись слишком длинная" });
        return Results.Ok(await diagnostic.TestSttAsync(buffer.ToArray(), model, ct));
    }
    catch (Exception ex) when (ex is ArgumentException or HttpRequestException or
        InvalidDataException or InvalidOperationException or OperationCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/models/downloads", (HttpContext ctx, ModelDownloadManager downloads) =>
    localOnly(ctx) ? Results.Ok(downloads.Status()) : Results.StatusCode(403));
app.MapGet("/api/models/hub", async (
    HttpContext ctx, string repo, string? revision, ModelDownloadManager downloads, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await downloads.InspectAsync(repo, revision ?? "main", ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
        HttpRequestException or System.Text.Json.JsonException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/downloads", async (
    HttpContext ctx, ModelDownloadManager.DownloadRequest request, ModelDownloadManager downloads, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await downloads.StartAsync(request, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
        InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/weights/delete", async (
    HttpContext ctx, ModelWeightDeleteInput input, ModelProcessManager processes,
    ModelDownloadManager downloads, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await processes.DeleteDownloadedWeightAsync(input.Path, downloads, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
        IOException or UnauthorizedAccessException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/downloads/{id:guid}/cancel", (
    HttpContext ctx, Guid id, ModelDownloadManager downloads) =>
    localOnly(ctx) ? Results.Ok(new { cancelled = downloads.Cancel(id) }) : Results.StatusCode(403));
app.MapPost("/api/models/hf-token", async (
    HttpContext ctx, HfTokenInput input, ModelDownloadManager downloads, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { await downloads.SaveTokenAsync(input.Token, ct); return Results.Ok(new { saved = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/models/hf-token", async (
    HttpContext ctx, ModelDownloadManager downloads, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    await downloads.RemoveTokenAsync(ct);
    return Results.Ok(new { disconnected = true });
});
app.MapGet("/api/models", async (HttpContext ctx, ModelProcessManager manager, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(new { profiles = await manager.ProfilesAsync(ct), status = await manager.StatusAsync(ct) }) : Results.StatusCode(403));
app.MapPut("/api/models/{role}", async (HttpContext ctx, string role, ModelLaunchProfile profile, ModelProcessManager manager, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { await manager.SaveAsync(role, profile, ct); return Results.Ok(new { saved = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/{role}/restart", async (
    HttpContext ctx, string role, ModelProcessManager manager, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await manager.RestartAsync(role, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/{role}/start", async (HttpContext ctx, string role, ModelProcessManager manager, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await manager.StartAsync(role, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/models/{role}/stop", async (HttpContext ctx, string role, ModelProcessManager manager, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(await manager.StopAsync(role, ct)) : Results.StatusCode(403));
app.MapGet("/api/radio/search", async (HttpContext ctx, string name, RadioBrowserService radio, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await radio.SearchAsync(name, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
// состояние сервисов без передачи сохранённых токенов в браузер.
app.MapGet("/api/integrations", async (
    HttpContext ctx, YandexMusicService yandex,
    HomeAssistantConnectionService home, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    var yandexStatus = await yandex.ConnectionStatusAsync(ct);
    var homeStatus = await home.StatusAsync(ct);
    return Results.Ok(new {
        yandexMusic = yandexStatus,
        homeAssistant = homeStatus,
        radioBrowser = new { requiresAuthentication = false, provider = "Radio Browser" }
    });
});
app.MapPost("/api/integrations/yandex/token", async (
    HttpContext ctx, YandexTokenInput input, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try {
        string account = await music.ValidateTokenAsync(input.Token, ct);
        await music.SaveTokenAsync(input.Token.Trim(), ct);
        return Results.Ok(new { connected = true, account });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
        InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/integrations/yandex/device/start", async (
    HttpContext ctx, YandexDeviceAuthService oauth, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await oauth.StartAsync(ct)); }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
        System.Text.Json.JsonException or TaskCanceledException)
    { return Results.BadRequest(new { error = "Не удалось получить код Яндекса: " + ex.Message }); }
});
app.MapPost("/api/integrations/yandex/device/poll", async (
    HttpContext ctx, YandexAuthPollRequest input, YandexDeviceAuthService oauth, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await oauth.PollAsync(input.Session, ct)); }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or
        InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
    { return Results.BadRequest(new { error = "Ошибка проверки авторизации: " + ex.Message }); }
});
app.MapPost("/api/integrations/yandex/device/cancel", (
    HttpContext ctx, YandexDeviceAuthService oauth) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    oauth.Cancel();
    return Results.Ok(new { cancelled = true });
});
app.MapDelete("/api/integrations/yandex", async (
    HttpContext ctx, YandexMusicService music, YandexDeviceAuthService oauth, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    oauth.Cancel();
    await music.DisconnectAsync(ct);
    return Results.Ok(new { disconnected = true });
});
app.MapGet("/api/integrations/homeassistant/entities", async (
    HttpContext ctx, VerifiedHomeAssistantService home, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await home.EntitiesAsync(ct)); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or
        System.Text.Json.JsonException or InvalidDataException or TaskCanceledException)
    { return Results.BadRequest(new { error = "Home Assistant: " + ex.Message }); }
});
app.MapPost("/api/integrations/homeassistant", async (
    HttpContext ctx, HomeAssistantConnectRequest input,
    HomeAssistantConnectionService home, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try {
        var result = await home.ConnectAsync(input.Url, input.Token, ct);
        return result.Connected ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
        HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/integrations/homeassistant", async (
    HttpContext ctx, HomeAssistantConnectionService home, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    await home.DisconnectAsync(ct);
    return Results.Ok(new { disconnected = true });
});
app.MapGet("/api/yandex/search", async (HttpContext ctx, string query, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await music.SearchAsync(query, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
// на клиент передаём только короткоживущую ссылку, без oauth-токена и аудиопрокси.
app.MapGet("/api/yandex/tracks/{trackId}/playback", async (
    HttpContext ctx, string trackId, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await music.ResolveAsync(trackId, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
        InvalidDataException or NotSupportedException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/yandex/token", async (HttpContext ctx, YandexTokenInput input, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try {
        string account = await music.ValidateTokenAsync(input.Token, ct);
        await music.SaveTokenAsync(input.Token.Trim(), ct);
        return Results.Ok(new { saved = true, account });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or
        InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/events", (HttpContext ctx, long? after, ConversationLog log) =>
    localOnly(ctx) ? Results.Ok(log.Since(Math.Max(0, after ?? 0))) : Results.StatusCode(403));
app.MapGet("/tools", (HttpContext ctx, ToolRegistry registry) =>
    localOnly(ctx) ? Results.Ok(registry.Names()) : Results.StatusCode(403));
app.Map("/ws", async (HttpContext ctx, AssistantPipeline pipeline, Microsoft.Extensions.Options.IOptionsMonitor<ServerOptions> options) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    // браузерный websocket с чужого сайта не должен управлять колонкой в локальной сети.
    if (ctx.Request.Headers.Origin.Count > 0)
    {
        if (!Uri.TryCreate(ctx.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || !string.Equals(origin.Host, ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            || origin.Port != (ctx.Request.Host.Port ?? (ctx.Request.IsHttps ? 443 : 80)))
        { ctx.Response.StatusCode = 403; return; }
    }
    if (!localOnly(ctx))
    {
        var token = options.CurrentValue.DeviceToken;
        var supplied = ctx.Request.Headers["X-OpenAssistant-Token"].ToString();
        if (string.IsNullOrEmpty(token) || supplied.Length != token.Length ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes(token)))
        { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await pipeline.RunAsync(socket, ctx.RequestAborted, ctx.Connection.RemoteIpAddress?.ToString());
});
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5055";
    StartupBanner.Print(url);
});
app.Run();

public sealed record YandexTokenInput(string Token);

public sealed record YandexAuthPollRequest(string Session);
public sealed record HomeAssistantConnectRequest(string Url, string Token);

public sealed record HfTokenInput(string Token);

public sealed record ModelWeightDeleteInput(string Path);
