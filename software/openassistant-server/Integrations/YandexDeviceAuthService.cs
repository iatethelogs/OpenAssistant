using System.Text.Json;

namespace OpenAssistant.Server.Integrations;

/// <summary>вход в яндекс музыку через подтверждение кода на oauth.yandex.ru/device.</summary>
public sealed class YandexDeviceAuthService(IHttpClientFactory clients, YandexMusicService music)
{
    // публичные параметры Android-клиента из MarshalX/yandex-music-api.
    // они не являются пользовательскими секретами. доступность зависит от яндекс oauth.
    private const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    private const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    private const string OAuthBase = "https://oauth.yandex.ru";
    private readonly object _gate = new();
    private Pending? _pending;

    private sealed class Pending
    {
        public required string Session { get; init; }
        public required string Code { get; init; }
        public required DateTimeOffset Expires { get; init; }
        public required int Interval { get; set; }
        public DateTimeOffset NextPoll { get; set; }
        public bool Busy { get; set; }
    }

    /// <summary>выдаёт одноразовый код и адрес страницы подтверждения.</summary>
    public async Task<object> StartAsync(CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_id"] = Guid.NewGuid().ToString("N")[..16],
            ["device_name"] = "OpenAssistant"
        });
        using var response = await clients.CreateClient("yandex-oauth")
            .PostAsync(OAuthBase + "/device/code", content, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        string deviceCode = root.GetProperty("device_code").GetString() ?? "";
        string userCode = root.GetProperty("user_code").GetString() ?? "";
        if (deviceCode.Length < 10 || userCode.Length == 0)
            throw new InvalidDataException("яндекс не выдал код авторизации");

        int expires = root.TryGetProperty("expires_in", out var duration) &&
            duration.TryGetInt32(out var e) ? Math.Clamp(e, 60, 600) : 300;
        int interval = root.TryGetProperty("interval", out var frequency) &&
            frequency.TryGetInt32(out var i) ? Math.Clamp(i, 5, 60) : 5;

        var pending = new Pending
        {
            Session = Guid.NewGuid().ToString("N"),
            Code = deviceCode,
            Expires = DateTimeOffset.UtcNow.AddSeconds(expires),
            Interval = interval,
            NextPoll = DateTimeOffset.UtcNow
        };
        lock (_gate) _pending = pending;
        return new
        {
            session = pending.Session, userCode, verificationUrl = OAuthBase + "/device",
            expiresIn = expires, interval
        };
    }

    /// <summary>однократно проверяет подтверждение кода, соблюдая интервал яндекс oauth.</summary>
    public async Task<object> PollAsync(string session, CancellationToken ct)
    {
        Pending pending;
        lock (_gate)
        {
            if (_pending is null || _pending.Session != session)
                return new { state = "expired", interval = 0, message = "Сессия не найдена. Начните подключение заново." };
            if (_pending.Expires <= DateTimeOffset.UtcNow)
            {
                _pending = null;
                return new { state = "expired", interval = 0, message = "Код истёк. Начните подключение заново." };
            }
            pending = _pending;
            if (pending.Busy || pending.NextPoll > DateTimeOffset.UtcNow)
                return new { state = "pending", interval = pending.Interval, message = "Ожидание подтверждения" };
            pending.Busy = true;
            pending.NextPoll = DateTimeOffset.UtcNow.AddSeconds(pending.Interval);
        }

        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "device_code",
                ["code"] = pending.Code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret
            });
            using var response = await clients.CreateClient("yandex-oauth")
                .PostAsync(OAuthBase + "/token", form, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            if (response.IsSuccessStatusCode)
            {
                string token = root.GetProperty("access_token").GetString() ?? "";
                if (token.Length == 0) throw new InvalidDataException("яндекс не вернул oauth-токен");

                // нельзя сохранять результат отменённой или заменённой попытки входа.
                lock (_gate)
                    if (!ReferenceEquals(_pending, pending))
                        return new { state = "expired", interval = 0, message = "Попытка входа отменена" };
                var account = await music.ValidateTokenAsync(token, ct);
                lock (_gate)
                    if (!ReferenceEquals(_pending, pending))
                        return new { state = "expired", interval = 0, message = "Попытка входа отменена" };
                await music.SaveTokenAsync(token, ct);
                lock (_gate)
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                return new { state = "connected", interval = 0, message = account };
            }

            string error = root.TryGetProperty("error", out var errorValue)
                ? errorValue.GetString() ?? "" : "";
            if (error == "authorization_pending")
                return new { state = "pending", interval = pending.Interval, message = "Ожидание подтверждения" };
            if (error == "slow_down")
            {
                lock (_gate)
                {
                    pending.Interval = Math.Min(60, pending.Interval + 5);
                    pending.NextPoll = DateTimeOffset.UtcNow.AddSeconds(pending.Interval);
                }
                return new { state = "pending", interval = pending.Interval, message = "Яндекс просит увеличить интервал проверки" };
            }
            lock (_gate)
                if (ReferenceEquals(_pending, pending)) _pending = null;
            return new { state = "error", interval = 0,
                message = error is "access_denied" ? "Доступ не предоставлен" :
                    error is "expired_token" ? "Срок действия кода истёк" :
                    "Авторизация не завершена: " + error };
        }
        finally
        {
            lock (_gate) pending.Busy = false;
        }
    }

    /// <summary>отменяет активную попытку входа, не раскрывая служебный device code.</summary>
    public void Cancel()
    {
        lock (_gate) _pending = null;
    }
}
