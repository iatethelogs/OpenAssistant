using System.Net.Http.Json;
using System.Text.Json;
namespace OpenAssistant.Server.Integrations;

/// <summary>поиск интернет-радио через открытый каталог radio browser.</summary>
public sealed class RadioBrowserService(IHttpClientFactory factory)
{
    /// <summary>находит станции по названию и выдаёт ссылки на аудиопотоки.</summary>
    public async Task<object> SearchAsync(string name, CancellationToken ct)
    {
        if (name.Length is < 1 or > 100) throw new ArgumentException("длина запроса 1–100 символов");
        var client = factory.CreateClient("radio-browser");
        var uri = "https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&limit=30&order=clickcount&reverse=true&name=" + Uri.EscapeDataString(name);
        using var response = await client.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.EnumerateArray().Select(s => new {
            name = s.TryGetProperty("name", out var n) ? n.GetString() : null,
            url = s.TryGetProperty("url_resolved", out var u) ? u.GetString() : null,
            codec = s.TryGetProperty("codec", out var c) ? c.GetString() : null,
            country = s.TryGetProperty("country", out var p) ? p.GetString() : null
        }).Where(x => x.url is not null && Uri.TryCreate(x.url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http")).ToArray();
    }
}
