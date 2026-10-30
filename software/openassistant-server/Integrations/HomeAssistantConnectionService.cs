using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;

namespace OpenAssistant.Server.Integrations;

/// <summary>проверка и настройка подключения home assistant без хранения пароля.</summary>
public sealed class HomeAssistantConnectionService(
    IHttpClientFactory clients, IOptionsMonitor<ServerOptions> options, SettingsStore settings)
{
    /// <summary>проверяет токен вызовом безопасного метода GET /api/config.</summary>
    public async Task<ServiceConnectionStatus> TestAsync(string url, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("укажите адрес Home Assistant и токен");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("неверный адрес Home Assistant");

        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/api/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await clients.CreateClient("homeassistant").SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            return new ServiceConnectionStatus(true, false, null,
                "Home Assistant отклонил запрос (HTTP " + (int)response.StatusCode + ")");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version))
            return new ServiceConnectionStatus(true, false, null, "Некорректный ответ Home Assistant");
        string name = root.TryGetProperty("location_name", out var location) &&
            location.ValueKind == JsonValueKind.String ? location.GetString() ?? "" : "";
        string message = "Подключение подтверждено. Home Assistant " + version.ToString();
        return new ServiceConnectionStatus(true, true, name, message);
    }

    /// <summary>сохраняет только работающую пару URL + долгоживущий токен.</summary>
    public async Task<ServiceConnectionStatus> ConnectAsync(string url, string token, CancellationToken ct)
    {
        var result = await TestAsync(url, token, ct);
        if (!result.Connected) return result;
        await settings.UpdateHomeAssistantAsync(url.TrimEnd('/'), token, ct);
        return result;
    }

    /// <summary>возвращает состояние подключения, не раскрывая секрет.</summary>
    public async Task<ServiceConnectionStatus> StatusAsync(CancellationToken ct)
    {
        var cfg = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(cfg.HomeAssistantUrl) ||
            string.IsNullOrWhiteSpace(cfg.HomeAssistantToken))
            return new ServiceConnectionStatus(false, false, null, "Не подключён");
        try { return await TestAsync(cfg.HomeAssistantUrl, cfg.HomeAssistantToken, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or
            TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            return new ServiceConnectionStatus(true, false, null, "Не удалось проверить: " + ex.Message);
        }
    }

    /// <summary>забывает авторизацию home assistant, не меняя настройки языковых моделей.</summary>
    public Task DisconnectAsync(CancellationToken ct) =>
        settings.UpdateHomeAssistantAsync("", "", ct);
}

/// <summary>общий безопасный статус внешнего сервиса, без ключей авторизации.</summary>
public sealed record ServiceConnectionStatus(bool Configured, bool Connected, string? Account, string Message);
