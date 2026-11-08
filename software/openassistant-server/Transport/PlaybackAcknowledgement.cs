namespace OpenAssistant.Server.Transport;

/// <summary>
/// Per-connection confirmation from the actual audio output. A command sent
/// to the socket is not the same thing as an audible stream being started.
/// </summary>
public sealed class PlaybackAcknowledgement
{
    public sealed record Outcome(bool Started, string Title, string Reason);
    private readonly object _sync = new();
    private readonly Dictionary<long, string> _issued = new();
    private TaskCompletionSource<Outcome>? _pending;
    private string _lastFailure = "";
    private long _radioCounter = 1_000_000_000_000;

    public long NextRadioId() => Interlocked.Increment(ref _radioCounter);

    public void Begin()
    {
        lock (_sync)
        {
            _pending?.TrySetResult(new Outcome(false, "", "Предыдущее действие заменено"));
            _pending = new TaskCompletionSource<Outcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _issued.Clear();
            _lastFailure = "";
        }
    }

    public void Issued(long playbackId, string title)
    {
        lock (_sync)
        {
            if (_pending is not null) _issued[playbackId] = title;
        }
    }

    public void Started(long playbackId)
    {
        lock (_sync)
        {
            if (_pending is not null && _issued.TryGetValue(playbackId, out var title))
                _pending.TrySetResult(new Outcome(true, title, ""));
        }
    }

    public void Failed(long playbackId, string? message, bool terminal = false)
    {
        lock (_sync)
        {
            if (_pending is null || !_issued.ContainsKey(playbackId)) return;
            _lastFailure = string.IsNullOrWhiteSpace(message) ?
                "Клиент не смог открыть аудиопоток" : message;
            if (terminal)
                _pending.TrySetResult(new Outcome(false, "", _lastFailure));
        }
    }

    public void Stopped()
    {
        lock (_sync)
        {
            _pending?.TrySetResult(new Outcome(false, "",
                _lastFailure.Length == 0
                    ? "Не удалось запустить доступные треки" : _lastFailure));
        }
    }

    public async Task<Outcome> WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource<Outcome>? pending;
        lock (_sync) pending = _pending;
        if (pending is null) return new Outcome(false, "", "Поток не был отправлен на устройство");

        try { return await pending.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                return new Outcome(false, "",
                    _lastFailure.Length > 0 ? _lastFailure :
                    _issued.Count == 0
                        ? "Не удалось отправить аудиопоток устройству"
                        : "Устройство не подтвердило начало воспроизведения за 15 секунд");
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    _issued.Clear();
                }
            }
        }
    }
}
