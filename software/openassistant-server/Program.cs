using OpenAssistant.Server.Audio;
using OpenAssistant.Server.ConsoleUi;
using System.Net;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;
using OpenAssistant.Server.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("settings.local.json", optional: true, reloadOnChange: true);
builder.Services.AddSingleton<SettingsStore>();
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
