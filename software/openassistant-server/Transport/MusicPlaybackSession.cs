using System.Text.RegularExpressions;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Transport;

/// <summary>
/// Per-WebSocket Yandex Music queue. Only short-lived stream URLs, never OAuth
/// tokens, go to the playback device. Commands are serialized so a stale
/// "ended" event cannot unexpectedly skip the new track.
/// </summary>
public sealed class MusicPlaybackSession(
    YandexMusicService music, Func<object, CancellationToken, Task> send)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<YandexTrack> _queue = [];
    private int _index = -1;
    private long _playbackId;
    private bool _paused;
    private int _consecutiveErrors;
    private const int MaxHistory = 500;

    private object Snapshot() => new
    {
        type = "media_status", source = "yandex_music",
        active = _index >= 0 && _index < _queue.Count,
        paused = _paused,
        index = _index, total = _queue.Count,
        track = _index >= 0 && _index < _queue.Count ? _queue[_index] : null,
        queue = _queue.Skip(Math.Max(0, _index)).Take(20).ToArray()
    };

    public async Task PlayQueueAsync(IEnumerable<YandexTrack> tracks, CancellationToken ct)
    {
        var valid = tracks.Where(x => Regex.IsMatch(x.Id, @"^\d+(?::\d+)?$"))
            .DistinctBy(x => x.Id).Take(200).ToArray();
        if (valid.Length == 0) throw new ArgumentException("В очереди нет воспроизводимых треков");
        await _gate.WaitAsync(ct);
        try
        {
            _queue.Clear();
            _queue.AddRange(valid);
            _index = 0;
            _paused = false;
            _consecutiveErrors = 0;
            await TryPlayFromAsync(0, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task PlayCurrentAsync(CancellationToken ct)
    {
        var track = _queue[_index];
        var playback = await music.ResolveAsync(track.Id, ct);
        long id = ++_playbackId;
        _paused = false;
        string title = track.Artists.Length == 0 ? track.Title :
            string.Join(", ", track.Artists) + " — " + track.Title;
        await send(new
        {
            type = "play_media", source = "yandex_music",
            url = playback.Url, title, trackId = track.Id, playbackId = id,
            index = _index, total = _queue.Count, bitrateKbps = playback.BitrateKbps,
            codec = playback.Codec
        }, ct);
        await send(Snapshot(), ct);
    }

    // Unavailable tracks may have expired rights or only DRM media. Skip a few
    // safely rather than terminating the entire WebSocket on a CDN/API error.
    private async Task TryPlayFromAsync(int start, CancellationToken ct)
    {
        for (int candidate = start; candidate < _queue.Count && candidate < start + 5; candidate++)
        {
            _index = candidate;
            try
            {
                await PlayCurrentAsync(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is HttpRequestException or InvalidDataException or
                    InvalidOperationException or NotSupportedException)
            {
                await send(new { type = "media_error",
                    message = "Недоступен трек: " + _queue[candidate].Title + " (" + ex.Message + ")" }, ct);
            }
        }
        _index = -1;
        _playbackId++;
        _paused = false;
        await send(new { type = "stop_media" }, ct);
        await send(Snapshot(), ct);
    }

    public async Task<string?> CurrentTrackAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_index < 0 || _index >= _queue.Count) return null;
            var track = _queue[_index];
            return track.Artists.Length == 0 ? track.Title :
                string.Join(", ", track.Artists) + " — " + track.Title;
        }
        finally { _gate.Release(); }
    }

    public async Task StartedAsync(long playbackId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (_index >= 0 && playbackId == _playbackId) _consecutiveErrors = 0; }
        finally { _gate.Release(); }
    }

    public async Task FailedAsync(long playbackId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_index < 0 || playbackId != _playbackId) return;
            if (++_consecutiveErrors >= 5)
            {
                _queue.Clear();
                _index = -1;
                _playbackId++;
                _paused = false;
                await send(new { type = "media_error", message = "Несколько треков подряд не удалось воспроизвести." }, ct);
                await send(new { type = "stop_media" }, ct);
                await send(Snapshot(), ct);
                return;
            }
            await AdvanceAsync(1, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task AdvanceAsync(int step, CancellationToken ct)
    {
        if (_index < 0 || _queue.Count == 0) return;
        int next = _index + step;
        if (next < 0) next = 0;
        if (step > 0 && next >= _queue.Count)
        {
            // Resolve new recommendations only when the existing queue ends.
            // Never reuse expiring audio URLs between tracks.
            try
            {
                var similar = await music.SimilarAsync(_queue[^1].Id, ct);
                var known = _queue.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                _queue.AddRange(similar.Where(x => known.Add(x.Id))
                    .Take(Math.Max(0, MaxHistory - _queue.Count)));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // An API failure ends the queue normally instead of crashing
                // the client when the last song finishes.
            }
        }
        if (next >= _queue.Count)
        {
            _index = -1;
            _playbackId++;
            _paused = false;
            await send(Snapshot(), ct);
            return;
        }
        await TryPlayFromAsync(next, ct);
    }

    public async Task EndedAsync(long playbackId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_index < 0 || _paused || playbackId != _playbackId) return;
            _consecutiveErrors = 0;
            await AdvanceAsync(1, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ControlAsync(string action, CancellationToken ct)
    {
        if (action is not ("pause" or "resume" or "next" or "previous" or "stop" or "status"))
            throw new ArgumentException("Неизвестная команда музыки");
        await _gate.WaitAsync(ct);
        try
        {
            switch (action)
            {
                case "pause" when _index >= 0:
                    _paused = true;
                    await send(new { type = "pause_media", source = "yandex_music" }, ct);
                    break;
                case "resume" when _index >= 0:
                    _paused = false;
                    await send(new { type = "resume_media", source = "yandex_music" }, ct);
                    break;
                case "next":
                    await AdvanceAsync(1, ct);
                    return;
                case "previous":
                    await AdvanceAsync(-1, ct);
                    return;
                case "stop":
                    _queue.Clear();
                    _index = -1;
                    _paused = false;
                    _playbackId++;
                    await send(new { type = "stop_media" }, ct);
                    break;
            }
            await send(Snapshot(), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ForgetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _queue.Clear();
            _index = -1;
            _playbackId++;
            _paused = false;
        }
        finally { _gate.Release(); }
    }
}
