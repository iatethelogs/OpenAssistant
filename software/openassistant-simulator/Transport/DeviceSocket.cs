using System.Net.WebSockets;
using System.Text.Json;

namespace OpenAssistant.Simulator.Transport;

/// <summary>двустороннее соединение с сервером с корректной сменой сессий.</summary>
public sealed class DeviceSocket : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _stop;
    private Task? _reader;

    public event Action<JsonElement>? ControlReceived;
    public event Action<byte[]>? AudioReceived;
    public event Action<string>? State;
    public bool Connected => _socket?.State == WebSocketState.Open;

    /// <summary>подключает устройство; старую сессию закрывает перед созданием новой.</summary>
    public async Task ConnectAsync(string address, string? deviceToken = null)
    {
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
            var uri = new Uri(address, UriKind.Absolute);
            if (uri.Scheme is not ("ws" or "wss"))
                throw new ArgumentException("используйте ws:// или wss://");
            var socket = new ClientWebSocket();
            var stop = new CancellationTokenSource();
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceToken))
                    socket.Options.SetRequestHeader("X-OpenAssistant-Token", deviceToken);
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri, connectTimeout.Token);
                _socket = socket;
                _stop = stop;
                _reader = ReadAsync(socket, stop.Token);
                State?.Invoke("Подключено");
            }
            catch { socket.Dispose(); stop.Dispose(); throw; }
        }
        finally { _connectionLock.Release(); }
    }

    /// <summary>передаёт сырой PCM16 в одном бинарном сообщении.</summary>
    public Task SendPcmAsync(byte[] pcm) => SendAsync(pcm, WebSocketMessageType.Binary);

    /// <summary>передаёт управляющую команду в json.</summary>
    public Task SendControlAsync(object payload) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text);

    /// <summary>сериализует все отправки и привязывает их к конкретному соединению.</summary>
    private async Task SendAsync(byte[] data, WebSocketMessageType type)
    {
        await _sendLock.WaitAsync();
        try
        {
            var socket = _socket;
            var stop = _stop;
            if (socket?.State != WebSocketState.Open || stop is null)
                throw new InvalidOperationException("соединение с сервером отсутствует");
            await socket.SendAsync(data.AsMemory(), type, true, stop.Token);
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>принимает фрагментированные сообщения и проверяет их максимальный размер.</summary>
    private async Task ReadAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                ValueWebSocketReceiveResult message;
                do
                {
                    message = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (message.MessageType == WebSocketMessageType.Close) return;
                    if (frame.Length + message.Count > 1024 * 1024)
                        throw new InvalidDataException("входной websocket пакет превышает 1 МБ");
                    frame.Write(buffer, 0, message.Count);
                } while (!message.EndOfMessage);

                if (message.MessageType == WebSocketMessageType.Binary)
                    AudioReceived?.Invoke(frame.ToArray());
                else
                {
                    try
                    {
                        using var document = JsonDocument.Parse(frame.ToArray());
                        ControlReceived?.Invoke(document.RootElement.Clone());
                    }
                    catch (JsonException ex) { State?.Invoke("Ошибка протокола: " + ex.Message); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException ex) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) State?.Invoke("Ошибка соединения: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_socket, socket)) State?.Invoke("Соединение закрыто");
        }
    }

    /// <summary>завершает соединение и ожидает остановки приёмника.</summary>
    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { _connectionLock.Release(); }
    }

    /// <summary>останавливает текущую сессию без захвата внешнего замка.</summary>
    private async Task DisconnectCoreAsync()
    {
        var socket = _socket;
        var stop = _stop;
        var reader = _reader;
        _socket = null;
        _stop = null;
        _reader = null;
        if (stop is not null) stop.Cancel();
        socket?.Abort();
        if (reader is not null)
            try { await reader; } catch (OperationCanceledException) { }
        socket?.Dispose();
        stop?.Dispose();
    }

    /// <summary>освобождает сокет и внутренние блокировки.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
        _connectionLock.Dispose();
    }
}
