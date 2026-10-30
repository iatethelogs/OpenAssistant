using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenAssistant.Server.Integrations;

/// <summary>поиск и получение прямых аудиоссылок через неофициальный api яндекс музыки.</summary>
public sealed class YandexMusicService(IHttpClientFactory factory, IWebHostEnvironment env)
{
    private const string Api = "https://api.music.yandex.net";
    private const string SignatureSalt = "XGRlBW9FXlekgbPrRHuSiA";
    private const string DesktopSignKey = "kzqU4XhfCaY6B6JTHODeq5";
    private const string DesktopClient = "YandexMusicDesktopAppWindows/5.95.0";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string TokenFile => Path.Combine(env.ContentRootPath, "yandex-music.local.json");

    /// <summary>проверяет действительность токена по account/status до его сохранения.</summary>
    public async Task<string> ValidateTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 10 or > 4096)
            throw new ArgumentException("некорректный oauth-токен");

        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/account/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await factory.CreateClient("yandex-music").SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Яндекс Музыка отклонила токен (HTTP " +
                (int)response.StatusCode + ")");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Яндекс Музыка не подтвердила аккаунт");
        string login = PropertyString(account, "login");
        return login.Length > 0 ? login : "Аккаунт подтверждён";
    }

    /// <summary>возвращает статус авторизации без раскрытия OAuth-токена.</summary>
    public async Task<ServiceConnectionStatus> ConnectionStatusAsync(CancellationToken ct)
    {
        if (!File.Exists(TokenFile))
            return new ServiceConnectionStatus(false, false, null, "Не подключён");
        try
        {
            string token = await ReadTokenAsync(ct);
            string account = await ValidateTokenAsync(token, ct);
            return new ServiceConnectionStatus(true, true, account, "Авторизация подтверждена");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or
            InvalidDataException or InvalidOperationException or ArgumentException or TaskCanceledException)
        {
            return new ServiceConnectionStatus(true, false, null, "Проверка не удалась: " + ex.Message);
        }
    }

    /// <summary>удаляет локальный токен и отключает учётную запись.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try { if (File.Exists(TokenFile)) File.Delete(TokenFile); }
        finally { _tokenLock.Release(); }
    }

    /// <summary>сохраняет пользовательский oauth-токен без пароля; файл недоступен другим пользователям linux.</summary>
    public async Task SaveTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 10 or > 4096)
            throw new ArgumentException("некорректный oauth-токен");
        await _tokenLock.WaitAsync(ct);
        try
        {
            string tmp = TokenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new { token }), ct);
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(tmp, TokenFile, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>создаёт запрос яндекс музыки с oauth-авторизацией, аналогично MarshalX/yandex-music-api.</summary>
    private async Task<HttpRequestMessage> AuthorizedRequestAsync(string relativeUrl, CancellationToken ct)
    {
        string token = await ReadTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Get, Api + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        request.Headers.AcceptLanguage.ParseAdd("ru");
        return request;
    }

    /// <summary>получает сохранённый токен; доступ к файлу синхронизирован с записью.</summary>
    private async Task<string> ReadTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(TokenFile)) throw new InvalidOperationException("сначала подключите аккаунт яндекс музыки");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TokenFile, ct));
            return doc.RootElement.GetProperty("token").GetString() ??
                throw new InvalidDataException("пустой oauth-токен яндекс музыки");
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>ищет треки через GET /search: набор параметров повторяет SearchMixin.search библиотеки MarshalX.</summary>
    public async Task<YandexTrack[]> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 120)
            throw new ArgumentException("неверный поисковый запрос");
        string path = "/search?text=" + Uri.EscapeDataString(query.Trim()) +
            "&nocorrect=False&type=track&page=0&playlist-in-best=True";
        using var request = await AuthorizedRequestAsync(path, ct);
        using var response = await factory.CreateClient("yandex-music").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("tracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Object ||
            !tracks.TryGetProperty("results", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return [];

        return entries.EnumerateArray().Take(25).Select(t =>
        {
            string id = PropertyString(t, "id");
            if (id.Length == 0 && t.TryGetProperty("trackId", out var tid))
                id = tid.ToString();
            string title = PropertyString(t, "title");
            string[] artists = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => PropertyString(x, "name"))
                    .Where(n => n.Length != 0).ToArray() : [];
            return new YandexTrack(id, title, artists);
        }).Where(t => t.Id.Length > 0).ToArray();
    }

    /// <summary>получает короткоживущую ссылку mp3/aac: трек загружает сам клиент, а не сервер.</summary>
    public async Task<YandexPlayback> ResolveAsync(string trackId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackId) ||
            !Regex.IsMatch(trackId, @"^\d+(?::\d+)?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("некорректный идентификатор трека");

        // современный путь из MarshalX v3.2: /get-file-info с подписанным запросом.
        // выбираем только raw mp3/aac, которые может декодировать Windows без DRM.
        YandexPlayback? modern = await TryResolveModernAsync(trackId.Split(':')[0], ct);
        if (modern is not null) return modern;

        // резервный путь библиотеки: устаревший /tracks/{id}/download-info.
        using var request = await AuthorizedRequestAsync(
            "/tracks/" + Uri.EscapeDataString(trackId) + "/download-info", ct);
        using var response = await factory.CreateClient("yandex-music").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var options) ||
            options.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("яндекс не вернул варианты воспроизведения");

        // зашифрованные/lossless варианты без поддерживаемого декодера сознательно не выдаём.
        var variants = options.EnumerateArray()
            .Select(o => new
            {
                Codec = PropertyString(o, "codec").ToLowerInvariant(),
                Bitrate = o.TryGetProperty("bitrateInKbps", out var b) && b.TryGetInt32(out var value) ? value : 0,
                InfoUrl = PropertyString(o, "downloadInfoUrl"),
                Preview = o.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True
            })
            .Where(x => (x.Codec is "mp3" or "aac") && !x.Preview && x.InfoUrl.Length > 0)
            .OrderByDescending(x => x.Codec == "mp3")
            .ThenByDescending(x => x.Bitrate).ToArray();
        if (variants.Length == 0)
            throw new NotSupportedException("доступного mp3/aac-потока нет; возможны подписка или другой формат трека");
        var selected = variants[0];

        if (!Uri.TryCreate(selected.InfoUrl, UriKind.Absolute, out var xmlUrl) ||
            xmlUrl.Scheme != Uri.UriSchemeHttps || IsLocal(xmlUrl.Host))
            throw new InvalidDataException("некорректный адрес информации об аудио");
        string xml = await factory.CreateClient("yandex-music").GetStringAsync(xmlUrl, ct);
        if (xml.Length > 65536) throw new InvalidDataException("слишком большой ответ аудио-CDN");

        // маршрут и md5-подпись повторяют DownloadInfo.get_direct_link библиотеки MarshalX.
        using var textReader = new StringReader(xml);
        using var reader = XmlReader.Create(textReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
        });
        var root = XDocument.Load(reader);
        string host = root.Descendants("host").FirstOrDefault()?.Value ?? "";
        string path = root.Descendants("path").FirstOrDefault()?.Value ?? "";
        string ts = root.Descendants("ts").FirstOrDefault()?.Value ?? "";
        string salt = root.Descendants("s").FirstOrDefault()?.Value ?? "";
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || IsLocal(host) ||
            !path.StartsWith('/') || path.Contains('?') || path.Contains('#') ||
            !Regex.IsMatch(ts, @"^\d+$") || salt.Length is < 1 or > 256 ||
            path.Length > 2048)
            throw new InvalidDataException("неверные реквизиты аудиоссылки");

        string hashSource = SignatureSalt + path[1..] + salt;
        string signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(hashSource))).ToLowerInvariant();
        string url = $"https://{host}/get-mp3/{signature}/{ts}{path}";
        return new YandexPlayback(url, selected.Codec, selected.Bitrate,
            DateTimeOffset.UtcNow.AddSeconds(50));
    }

    /// <summary>читает новый подписанный /get-file-info в режиме raw без DRM и без проксирования медиа.</summary>
    private async Task<YandexPlayback?> TryResolveModernAsync(string trackId, CancellationToken ct)
    {
        const string quality = "hq";
        const string codecs = "mp3,aac";
        const string transport = "raw";
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signedData = ts.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            trackId + quality + codecs.Replace(",", "", StringComparison.Ordinal) + transport;
        byte[] digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(DesktopSignKey),
            Encoding.UTF8.GetBytes(signedData));
        string signature = Convert.ToBase64String(digest)[..^1];
        string query = "/get-file-info?ts=" + ts + "&trackId=" + Uri.EscapeDataString(trackId) +
            "&quality=" + quality + "&codecs=" + Uri.EscapeDataString(codecs) +
            "&transports=" + transport + "&sign=" + Uri.EscapeDataString(signature);

        using var request = await AuthorizedRequestAsync(query, ct);
        request.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", DesktopClient);
        using var response = await factory.CreateClient("yandex-music").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        JsonElement data = document.RootElement;
        if (data.TryGetProperty("result", out var result)) data = result;
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("downloadInfo", out var info) ||
            info.ValueKind != JsonValueKind.Object) return null;

        string codec = PropertyString(info, "codec").ToLowerInvariant();
        string transfer = PropertyString(info, "transport").ToLowerInvariant();
        if (codec is not ("mp3" or "aac" or "aac-mp4") || transfer != "raw") return null;

        string url = PropertyString(info, "url");
        if (url.Length == 0 && info.TryGetProperty("urls", out var urls) &&
            urls.ValueKind == JsonValueKind.Array)
            url = urls.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "").FirstOrDefault() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || IsLocal(uri.Host)) return null;

        int bitrate = info.TryGetProperty("bitrate", out var rate) &&
            rate.ValueKind == JsonValueKind.Number && rate.TryGetInt32(out var b) ? b : 0;
        return new YandexPlayback(url, codec, bitrate, DateTimeOffset.UtcNow.AddSeconds(50));
    }

    /// <summary>не разрешает ссылки CDN на локальную машину.</summary>
    private static bool IsLocal(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var ip) &&
        (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal ||
         ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any));

    /// <summary>безопасно достаёт строковое поле json даже при нестандартном типе.</summary>
    private static string PropertyString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var p) &&
        p.ValueKind is JsonValueKind.String or JsonValueKind.Number ? p.ToString() : "";
}

public sealed record YandexTrack(string Id, string Title, string[] Artists);
public sealed record YandexPlayback(string Url, string Codec, int BitrateKbps, DateTimeOffset ExpiresAt);
