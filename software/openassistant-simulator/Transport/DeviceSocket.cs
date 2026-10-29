using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace OpenAssistant.Simulator.Transport;
/// <summary>двусторонний websocket-клиент колонки с последовательной отправкой сообщений.</summary>
public sealed class DeviceSocket : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1,1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _stop;
    public event Action<JsonElement>? ControlReceived;
    public event Action<byte[]>? AudioReceived;
    public event Action<string>? State;
    public bool Connected => _socket?.State == WebSocketState.Open;
    /// <summary>устанавливает соединение и запускает получение бинарных и текстовых пакетов.</summary>
    public async Task ConnectAsync(string url)
    {
        await DisconnectAsync();
        _stop = new CancellationTokenSource();
        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri(url), _stop.Token);
        State?.Invoke("Подключено");
        _ = ReceiveLoopAsync(_socket, _stop.Token);
    }
    /// <summary>передаёт кусок микрофонного PCM как бинарный websocket-фрейм.</summary>
    public Task SendPcmAsync(byte[] bytes) => SendAsync(bytes, WebSocketMessageType.Binary);
    /// <summary>передаёт управляющую команду в json.</summary>
    public Task SendControlAsync(object payload) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text);
    /// <summary>сериализует запись в сокет, чтобы фреймы не перемешались.</summary>
    private async Task SendAsync(byte[] bytes, WebSocketMessageType kind)
    {
        if (_socket?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync();
        try { if (_socket.State == WebSocketState.Open) await _socket.SendAsync(bytes.AsMemory(),kind,true,_stop?.Token ?? CancellationToken.None); }
        finally { _sendLock.Release(); }
    }
    /// <summary>собирает фрагментированные сообщения и публикует события интерфейсу.</summary>
    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[32768];
        try {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
                using var data = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do {
                    r=await ws.ReceiveAsync(buf.AsMemory(),ct);
                    if(r.MessageType==WebSocketMessageType.Close)return;
                    data.Write(buf,0,r.Count);
                    if(data.Length>8*1024*1024) throw new InvalidDataException("слишком большой пакет");
                } while(!r.EndOfMessage);
                if(r.MessageType==WebSocketMessageType.Binary) AudioReceived?.Invoke(data.ToArray());
                else {using var json=JsonDocument.Parse(data.ToArray());ControlReceived?.Invoke(json.RootElement.Clone());}
            }
        } catch(OperationCanceledException) {} catch(Exception e) { State?.Invoke("Ошибка: "+e.Message); }
        finally { State?.Invoke("Соединение закрыто"); }
    }
    /// <summary>отключается и отменяет чтение с сервера.</summary>
    public async Task DisconnectAsync()
    {
        _stop?.Cancel();
        if(_socket is not null){_socket.Dispose();_socket=null;}
        _stop?.Dispose();_stop=null;
        await Task.CompletedTask;
    }
    /// <summary>освобождает ресурсы соединения.</summary>
    public async ValueTask DisposeAsync(){await DisconnectAsync();_sendLock.Dispose();}
}