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
builder.Configuration.AddJsonFile("settings.local.json", optional: true, reloadOnChange: true);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ModelProcessManager>();
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
app.MapGet("/api/models", async (HttpContext ctx, ModelProcessManager manager, CancellationToken ct) =>
    localOnly(ctx) ? Results.Ok(new { profiles = await manager.ProfilesAsync(ct), status = await manager.StatusAsync(ct) }) : Results.StatusCode(403));
app.MapPut("/api/models/{role}", async (HttpContext ctx, string role, ModelLaunchProfile profile, ModelProcessManager manager, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { await manager.SaveAsync(role, profile, ct); return Results.Ok(new { saved = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
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
    await pipeline.RunAsync(socket, ctx.RequestAborted);
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
