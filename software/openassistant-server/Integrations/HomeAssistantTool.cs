using System.Text.Json;
namespace OpenAssistant.Server.Integrations;
public sealed class HomeAssistantTool(VerifiedHomeAssistantService verified) : IAssistantTool
{
    public string Name => "home_assistant";
    public async Task<object> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        string action = args.GetProperty("action").GetString() ?? "";
        if (action == "list_states") return (await verified.EntitiesAsync(ct)).Take(1000).ToArray();
        if (action == "get_state") return await verified.GetAsync(args.GetProperty("entity_id").GetString() ?? "", ct);
        if (action == "call_service") return await verified.ChangeAsync(
            args.GetProperty("data").GetProperty("entity_id").GetString() ?? "",
            args.GetProperty("domain").GetString() ?? "",
            args.GetProperty("service").GetString() ?? "", ct);
        throw new ArgumentException("Неизвестная команда Home Assistant");
    }
}