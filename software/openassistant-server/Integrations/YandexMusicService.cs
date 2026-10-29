using System.Net.Http.Headers;
using System.Text.Json;
namespace OpenAssistant.Server.Integrations;

/// <summary>неофициальный клиент каталога яндекс музыки, без пользовательского пароля.</summary>
public sealed class YandexMusicService(IHttpClientFactory factory, IWebHostEnvironment env)
{
    private string TokenFile => Path.Combine(env.ContentRootPath, "yandex-music.local.json");
    /// <summary>сохраняет oauth-токен локально; в публичных ответах токен не возвращаем.</summary>
    public async Task SaveTokenAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 10 or > 4096) throw new ArgumentException("некорректный токен");
        await File.WriteAllTextAsync(TokenFile, JsonSerializer.Serialize(new { token }), ct);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    /// <summary>выполняет поиск треков через api.music.yandex.net по токену аккаунта.</summary>
    public async Task<object> SearchAsync(string query, CancellationToken ct)
    {
        if (query.Length is < 1 or > 120) throw new ArgumentException("неверный поисковый запрос");
        if (!File.Exists(TokenFile)) throw new InvalidOperationException("сначала подключите аккаунт яндекс музыки");
        using var tokenDoc = JsonDocument.Parse(await File.ReadAllTextAsync(TokenFile, ct));
        var token = tokenDoc.RootElement.GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.music.yandex.net/search?type=track&page=0&nocorrect=false&text=" + Uri.EscapeDataString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using var response = await factory.CreateClient("yandex-music").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tracks", out var tracks) ||
            !tracks.TryGetProperty("results", out var entries)) return Array.Empty<object>();
        return entries.EnumerateArray().Take(25).Select(t => new {
            id = t.TryGetProperty("id", out var id) ? id.ToString() : "",
            title = t.TryGetProperty("title", out var title) ? title.GetString() : "",
            artists = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : "").ToArray()
                : Array.Empty<string>()
        }).ToArray();
    }
}
