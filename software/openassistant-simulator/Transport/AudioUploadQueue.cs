using System.Threading.Channels;

namespace OpenAssistant.Simulator.Transport;

/// <summary>очередь микрофонных пакетов: PCM всегда отправляется раньше конца своей фразы.</summary>
public sealed class AudioUploadQueue : IAsyncDisposable
{
    private readonly DeviceSocket _socket;
    private readonly Channel<Packet> _pending = Channel.CreateUnbounded<Packet>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _queuedBytes;
    private int _overloaded;
    private long _generation;
    private const long MaxQueuedAudio = 1024 * 1024;

    public event Action<string>? Error;

    /// <summary>создаёт один поток отправки для всех команд и аудиофреймов.</summary>
    public AudioUploadQueue(DeviceSocket socket)
    {
        _socket = socket;
        _worker = ProcessAsync();
    }

    /// <summary>помещает PCM16 в очередь без блокировки потока микрофона.</summary>
    public void QueueAudio(byte[] pcm)
    {
        if (!_socket.Connected || Volatile.Read(ref _overloaded) != 0) return;
        if (Interlocked.Add(ref _queuedBytes, pcm.Length) > MaxQueuedAudio)
        {
            Interlocked.Add(ref _queuedBytes, -pcm.Length);
            Interlocked.Exchange(ref _overloaded, 1);
            Error?.Invoke("очередь микрофона переполнена; текущую фразу необходимо повторить");
            _pending.Writer.TryWrite(new Packet(null, "cancel", null, Volatile.Read(ref _generation)));
            return;
        }
        if (!_pending.Writer.TryWrite(new Packet(pcm, null, null, Volatile.Read(ref _generation))))
            Interlocked.Add(ref _queuedBytes, -pcm.Length);
    }

    /// <summary>добавляет маркер окончания фразы строго после ранее помещённых фреймов.</summary>
    public void EndPhrase()
    {
        if (Volatile.Read(ref _overloaded) != 0) return;
        _pending.Writer.TryWrite(new Packet(null, "end", null, Volatile.Read(ref _generation)));
    }

    /// <summary>отправляет отмену текущей операции после уже поставленных сообщений.</summary>
    public void Cancel()
    {
        Reset();
        _pending.Writer.TryWrite(new Packet(null, "cancel", null, Volatile.Read(ref _generation)));
    }

    /// <summary>отправляет текст в том же порядке, что аудио и его завершение.</summary>
    public void QueueText(string text) => _pending.Writer.TryWrite(new Packet(null, "text", text, Volatile.Read(ref _generation)));

    /// <summary>передаёт слово активации перед фреймами текущей голосовой команды.</summary>
    public void QueueWake(string word) => _pending.Writer.TryWrite(new Packet(null, "wake", word, Volatile.Read(ref _generation)));

    /// <summary>очищает очередь при отключении и разрешает новую запись.</summary>
    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        while (_pending.Reader.TryRead(out var packet))
            if (packet.Audio is not null) Interlocked.Add(ref _queuedBytes, -packet.Audio.Length);
        Interlocked.Exchange(ref _overloaded, 0);
    }

    /// <summary>передаёт сообщения строго последовательно на сервер.</summary>
    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var packet in _pending.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    if (packet.Generation != Volatile.Read(ref _generation)) continue;
                    if (packet.Audio is { } pcm)
                    {
                        if (_socket.Connected) await _socket.SendPcmAsync(pcm);
                    }
                    else if (packet.Command is { } command && _socket.Connected)
                    {
                        if (command == "text") await _socket.SendControlAsync(new { type = "text", text = packet.Text });
                        else if (command == "wake") await _socket.SendControlAsync(new { type = "wake", word = packet.Text });
                        else await _socket.SendControlAsync(new { type = command });
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
                {
                    Error?.Invoke(ex.Message);
                }
                finally
                {
                    if (packet.Audio is { } data) Interlocked.Add(ref _queuedBytes, -data.Length);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>останавливает передающую задачу и освобождает память очереди.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _pending.Writer.TryComplete();
        try { await _worker; } catch (OperationCanceledException) { }
        Reset();
        _stop.Dispose();
    }

    private sealed record Packet(byte[]? Audio, string? Command, string? Text, long Generation);
}
