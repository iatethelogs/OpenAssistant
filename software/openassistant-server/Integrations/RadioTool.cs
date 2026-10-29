using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Integrations;

/// <summary>реестр интернет-радиостанций; поток читает отдельный аудиосервис.</summary>
public sealed class RadioTool(IOptions<ServerOptions> options) : IAssistantTool
{
    public string Name => "radio";

    /// <summary>перечисляет станции или выдаёт адрес станции из доверенной конфигурации.</summary>
    public Task<object> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        string action = args.GetProperty("action").GetString() ?? "";
        if (action == "list") return Task.FromResult<object>(options.Value.RadioStations.Keys.Order().ToArray());
        if (action == "resolve")
        {
            string station = args.GetProperty("station").GetString() ?? "";
            if (!options.Value.RadioStations.TryGetValue(station, out string? url))
                throw new ArgumentException("станция не найдена");
            return Task.FromResult<object>(new { station, url });
        }
        throw new ArgumentException("неизвестная команда радио");
    }
}
