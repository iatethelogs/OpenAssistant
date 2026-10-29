using OpenAssistant.Server.Audio;
using OpenAssistant.Server.ConsoleUi;
using System.Net;
using OpenAssistant.Server.Models;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;
using OpenAssistant.Server.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("settings.local.json", optional: true, reloadOnChange: true);
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ModelProcessManager>();
builder.Services.AddSingleton<RadioBrowserService>();
builder.Services.AddSingleton<YandexMusicService>();
builder.Services.AddHttpClient("radio-browser", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAssistant/0.1"));
builder.Services.AddHttpClient("yandex-music");
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
app.MapGet("/api/yandex/search", async (HttpContext ctx, string query, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { return Results.Ok(await music.SearchAsync(query, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/yandex/token", async (HttpContext ctx, YandexTokenInput input, YandexMusicService music, CancellationToken ct) =>
{
    if (!localOnly(ctx)) return Results.StatusCode(403);
    try { await music.SaveTokenAsync(input.Token, ct); return Results.Ok(new { saved = true }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/tools", (ToolRegistry registry) => Results.Ok(registry.Names()));
app.Map("/ws", async (HttpContext ctx, AssistantPipeline pipeline) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
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
