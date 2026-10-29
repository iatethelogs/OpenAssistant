using OpenAssistant.Server.Audio;
using OpenAssistant.Server.Chat;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Integrations;
using OpenAssistant.Server.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("OpenAssistant"));
builder.Services.AddHttpClient("llm");
builder.Services.AddHttpClient("homeassistant");
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<IAssistantTool, HomeAssistantTool>();
builder.Services.AddSingleton<IAssistantTool, RadioTool>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<AssistantPipeline>();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "openassistant-server" }));
app.MapGet("/tools", (ToolRegistry registry) => Results.Ok(registry.Names()));
app.Map("/ws", async (HttpContext ctx, AssistantPipeline pipeline) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await pipeline.RunAsync(socket, ctx.RequestAborted);
});
app.Run();
