using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace OpenAssistant.Server.Models;

/// <summary>Скачивает публичные и доступные по токену модели Hugging Face без запуска команд.</summary>
public sealed class ModelDownloadManager(IHttpClientFactory clients, IWebHostEnvironment env, ILogger<ModelDownloadManager> logger) : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Dictionary<Guid, Job> _jobs = new();
    private string Root => Path.Combine(env.ContentRootPath, "models");
    private string TokenPath => Path.Combine(env.ContentRootPath, "hf-token.local.json");

    public sealed record HubFile(string Path, long Size, string? Sha256);
    public sealed record Repository(string Repo, string Revision, string Commit, HubFile[] Files);
    public sealed record DownloadRequest(string Repo, string Revision, string[] Files);
    private sealed class Job
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required string Repo { get; init; }
        public required string Commit { get; init; }
        public required string Folder { get; init; }
        public CancellationTokenSource Stop { get; } = new();
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public long Received;
        public long Total;
        public int FilesDone;
        public int FileCount;
        public string Current = "";
        public string State = "queued";
        public string? Error;
    }
    private static string RepoId(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || repo.Length > 160 ||
            !Regex.IsMatch(repo, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}/[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") ||
            repo.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Нужен Hugging Face ID вида автор/модель");
        return repo;
    }
    private static string Revision(string rev)
    {
        rev = string.IsNullOrWhiteSpace(rev) ? "main" : rev;
        if (rev.Length > 100 || !Regex.IsMatch(rev, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$") ||
            rev.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Недопустимая ревизия модели");
        return rev;
    }
    private static string FilePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 2048 ||
            name.Contains('\\') || name.StartsWith('/') || name.Contains(':') ||
            name.Contains('?') || name.Contains('#') ||
            name.Split('/').Any(x => x is "" or "." or ".."))
            throw new ArgumentException("Некорректное имя файла модели");
        return name;
    }
    private static Uri Hub(string path) => new("https://huggingface.co/" + path);

    public async Task SaveTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.Trim().StartsWith("hf_", StringComparison.Ordinal) || token.Length > 1000)
            throw new ArgumentException("Некорректный токен Hugging Face");
        await _tokenLock.WaitAsync(ct);
        try
        {
            var temp = TokenPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, token.Trim(), ct);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, TokenPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _tokenLock.Release(); }
    }
    public async Task RemoveTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); }
        finally { _tokenLock.Release(); }
    }
    private async Task AddAuthAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (File.Exists(TokenPath))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    (await File.ReadAllTextAsync(TokenPath, ct)).Trim());
        }
        finally { _tokenLock.Release(); }
    }
    private async Task<HttpResponseMessage> RequestAsync(HttpClient client, Uri uri, CancellationToken ct,
        long offset = 0)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        await AddAuthAsync(request, ct);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            try { response.EnsureSuccessStatusCode(); }
            finally { response.Dispose(); }
        }
        return response;
    }

    /// <summary>Список файлов конкретной зафиксированной ревизии, с поддержкой pagination.</summary>
    public async Task<Repository> InspectAsync(string repo, string revision, CancellationToken ct)
    {
        repo = RepoId(repo);
        revision = Revision(revision);
        var http = clients.CreateClient("hf-metadata");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var meta = await RequestAsync(http,
            Hub("api/models/" + repo + "/revision/" + Uri.EscapeDataString(revision)), timeout.Token);
        using var info = await JsonDocument.ParseAsync(await meta.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);
        string sha = info.RootElement.GetProperty("sha").GetString() ?? "";
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("Не удалось определить ревизию Hugging Face");
        Uri? next = Hub("api/models/" + repo + "/tree/" + sha + "?recursive=true");
        var files = new List<HubFile>();
        for (int page = 0; next is not null && page < 50; page++)
        {
            if (next.Host != "huggingface.co" || next.Scheme != "https" ||
                !next.AbsolutePath.StartsWith("/api/models/" + repo + "/tree/" + sha, StringComparison.Ordinal))
                throw new InvalidDataException("Некорректная пагинация Hugging Face");
            using var response = await RequestAsync(http, next, timeout.Token);
            using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            if (data.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Некорректный список файлов репозитория");
            foreach (var entry in data.RootElement.EnumerateArray())
            {
                if (entry.GetProperty("type").GetString() != "file") continue;
                string path = FilePath(entry.GetProperty("path").GetString() ?? "");
                long size = entry.TryGetProperty("size", out var n) && n.TryGetInt64(out var number) ? number : 0;
                string? checksum = null;
                if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    if (lfs.TryGetProperty("size", out var b) && b.TryGetInt64(out var large)) size = large;
                    if (lfs.TryGetProperty("oid", out var hash)) checksum = hash.GetString();
                }
                files.Add(new HubFile(path, Math.Max(0, size), checksum));
                if (files.Count > 3000) throw new InvalidDataException("Слишком много файлов в репозитории");
            }
            next = null;
            if (response.Headers.TryGetValues("Link", out var links))
                foreach (var part in links.SelectMany(x => x.Split(',')))
                {
                    var m = Regex.Match(part, "<([^>]+)>;\\s*rel=\"next\"");
                    if (m.Success && Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var url))
                    { next = url; break; }
                }
            if (next is not null && page == 49)
                throw new InvalidDataException("Слишком много страниц в каталоге");
        }
        return new Repository(repo, revision, sha, files.ToArray());
    }

    public async Task<object> StartAsync(DownloadRequest request, CancellationToken ct)
    {
        if (request.Files is null || request.Files.Length is < 1 or > 800)
            throw new ArgumentException("Выберите от одного до 800 файлов");
        var repo = await InspectAsync(request.Repo, request.Revision, ct);
        var available = repo.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var selected = new List<HubFile>();
        foreach (var path in request.Files.Select(FilePath).Distinct(StringComparer.Ordinal))
        {
            if (!available.TryGetValue(path, out var file))
                throw new ArgumentException("Нет такого файла в репозитории: " + path);
            selected.Add(file);
        }
        string folder = Path.Combine(Root, repo.Repo.Replace('/', Path.DirectorySeparatorChar), repo.Commit);
        lock (_sync)
        {
            if (_jobs.Values.Any(j => j.Folder == folder && j.State is "queued" or "downloading"))
                throw new InvalidOperationException("Эта модель уже загружается");
            var job = new Job { Repo = repo.Repo, Commit = repo.Commit, Folder = folder,
                Total = selected.Sum(x => x.Size), FileCount = selected.Count };
            _jobs[job.Id] = job;
            _ = Task.Run(() => DownloadAsync(job, selected));
            return new { id = job.Id, path = folder, files = selected.Count };
        }
    }

    private async Task DownloadAsync(Job job, List<HubFile> selected)
    {
        try
        {
            lock (_sync) job.State = "downloading";
            Directory.CreateDirectory(job.Folder);
            foreach (var file in selected)
            {
                job.Stop.Token.ThrowIfCancellationRequested();
                lock (_sync) job.Current = file.Path;
                var path = Path.GetFullPath(Path.Combine(job.Folder, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(Path.GetFullPath(job.Folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("Недопустимый путь загрузки");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && file.Size > 0 &&
                    new FileInfo(path).Length == file.Size && await HashMatches(path, file.Sha256, job.Stop.Token))
                {
                    lock (_sync) { job.Received += file.Size; job.FilesDone++; }
                    continue;
                }
                string partial = path + ".partial";
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (file.Size > 0 && offset == file.Size &&
                    await HashMatches(partial, file.Sha256, job.Stop.Token))
                {
                    File.Move(partial, path, true);
                    lock (_sync) { job.Received += file.Size; job.FilesDone++; }
                    continue;
                }
                if (file.Size > 0 && offset > file.Size) { File.Delete(partial); offset = 0; }
                string encoded = string.Join("/", file.Path.Split('/').Select(Uri.EscapeDataString));
                var uri = Hub(job.Repo + "/resolve/" + job.Commit + "/" + encoded);
                using var response = await RequestAsync(clients.CreateClient("hf-download"), uri, job.Stop.Token, offset);
                bool resume = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                    response.Content.Headers.ContentRange?.From == offset;
                if (!resume) offset = 0;
                await using (var destination = new FileStream(partial, resume ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.Read, 262144, FileOptions.Asynchronous))
                {
                    await using var source = await response.Content.ReadAsStreamAsync(job.Stop.Token);
                    var buffer = new byte[262144];
                    lock (_sync) job.Received += offset;
                    while (true)
                    {
                        int n = await source.ReadAsync(buffer, job.Stop.Token);
                        if (n == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, n), job.Stop.Token);
                        lock (_sync) job.Received += n;
                    }
                    await destination.FlushAsync(job.Stop.Token);
                }
                if (file.Size > 0 && new FileInfo(partial).Length != file.Size)
                    throw new InvalidDataException("Файл получен не полностью: " + file.Path);
                if (!await HashMatches(partial, file.Sha256, job.Stop.Token))
                {
                    File.Delete(partial);
                    throw new InvalidDataException("Неверный SHA256: " + file.Path);
                }
                File.Move(partial, path, true);
                lock (_sync) job.FilesDone++;
            }
            lock (_sync) { job.State = "completed"; job.Current = ""; }
        }
        catch (OperationCanceledException) { lock (_sync) job.State = "cancelled"; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model download failed: {Repo}", job.Repo);
            lock (_sync) { job.State = "failed"; job.Error = ex.Message; }
        }
        finally { job.Stop.Dispose(); }
    }

    private static async Task<bool> HashMatches(string path, string? hash, CancellationToken ct)
    {
        if (hash is null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) return true;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct))
            .Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>Ожидает завершения задания, не блокируя HTTP-запрос мастера.</summary>
    public async Task<string> WaitAsync(Guid id, Action<long,long,string> progress, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string state, file;
            long received, total;
            lock (_sync)
            {
                if (!_jobs.TryGetValue(id, out var job))
                    throw new InvalidOperationException("Задание загрузки не найдено");
                state = job.State;
                file = job.Error ?? job.Current;
                received = job.Received;
                total = job.Total;
            }
            progress(received, total, file);
            if (state == "completed") return state;
            if (state is "cancelled" or "failed")
                throw new InvalidOperationException("Скачивание модели: " + state + ": " + file);
            await Task.Delay(900, ct);
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State is not ("queued" or "downloading")) return false;
            job.Stop.Cancel();
            return true;
        }
    }
    public object Status()
    {
        object[] jobs;
        lock (_sync) jobs = _jobs.Values.OrderByDescending(x => x.Started).Take(30)
            .Select(j => (object)new {
                id = j.Id, repo = j.Repo, revision = j.Commit, path = j.Folder, state = j.State,
                file = j.Current, filesDone = j.FilesDone, filesTotal = j.FileCount,
                bytes = j.Received, total = j.Total, startedAt = j.Started, error = j.Error
            }).ToArray();
        string[] files;
        try { files = Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(x => !x.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).Take(2000).ToArray()
            : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
        return new { jobs, files, root = Root, hasToken = File.Exists(TokenPath) };
    }
    /// <summary>Ограниченное удаление конкретного GGUF из управляемого хранилища.</summary>
    public Task<object> DeleteWeightAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Файл не выбран");
        string root = Path.GetFullPath(Root);
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Можно удалить только загруженный файл GGUF");
        // Защита от симлинков, в том числе промежуточных каталогов.
        for (string? parent = full; parent is not null &&
             parent.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
             parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Символические ссылки недопустимы");
        }
        lock (_sync)
        {
            if (_jobs.Values.Any(job => (job.State is "queued" or "downloading") &&
                full.StartsWith(Path.GetFullPath(job.Folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidOperationException("Сначала дождитесь завершения загрузки");
            long bytes = new FileInfo(full).Length;
            File.Delete(full);
            string? folder = Path.GetDirectoryName(full);
            while (folder is not null &&
                folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                if (Directory.EnumerateFileSystemEntries(folder).Any()) break;
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder);
            }
            logger.LogInformation("Deleted downloaded GGUF {Name}", Path.GetFileName(full));
            return Task.FromResult<object>(new { deleted = true, path = full, bytes });
        }
    }

    public void Dispose()
    {
        lock (_sync) foreach (var job in _jobs.Values)
            if (job.State is "queued" or "downloading") job.Stop.Cancel();
        // Token file is owned by the process; active downloads terminate with the host.
    }
}
