using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Integrations;

/// <summary>локальный API home assistant: чтение состояний и разрешённые вызовы сервисов.</summary>
public sealed class HomeAssistantTool(IHttpClientFactory factory, IOptions<ServerOptions> options) : IAssistantTool
{
    public string Name => "home_assistant";

    /// <summary>исполняет get_state либо call_service; параметры читаем строго из json.</summary>
    public async Task<object> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.HomeAssistantUrl)
            || string.IsNullOrWhiteSpace(options.Value.HomeAssistantToken))
            throw new InvalidOperationException("home assistant не настроен");
        using var client = factory.CreateClient("homeassistant");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.HomeAssistantToken);
        string baseUrl = options.Value.HomeAssistantUrl.TrimEnd('/');
        string action = args.GetProperty("action").GetString() ?? "";
        if (action == "get_state")
        {
            string entity = args.GetProperty("entity_id").GetString() ?? "";
            using var response = await client.GetAsync(baseUrl + "/api/states/" + Uri.EscapeDataString(entity), ct);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        if (action == "call_service")
        {
            string domain = args.GetProperty("domain").GetString() ?? "";
            string service = args.GetProperty("service").GetString() ?? "";
            // никаких URL из запроса: домен и сервис ограничены допустимыми символами.
            if (!System.Text.RegularExpressions.Regex.IsMatch(domain, "^[a-z_]+$")
                || !System.Text.RegularExpressions.Regex.IsMatch(service, "^[a-z_]+$"))
                throw new ArgumentException("недопустимое имя сервиса");
            object payload = args.GetProperty("data");
            using var response = await client.PostAsJsonAsync(baseUrl + "/api/services/" + domain + "/" + service, payload, ct);
            response.EnsureSuccessStatusCode();
            return new { ok = true, result = await response.Content.ReadAsStringAsync(ct) };
        }
        throw new ArgumentException("неизвестная команда home assistant");
    }
}
