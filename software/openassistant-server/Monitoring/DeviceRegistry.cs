using System.Collections.Concurrent;

namespace OpenAssistant.Server.Monitoring;

public sealed class DeviceRegistry
{
    private sealed record Session(string Id, string Remote, DateTimeOffset Connected, DateTimeOffset LastPacket);
    private readonly ConcurrentDictionary<string, Session> _connections = new();
    public string Connect(string? address)
    {
        string id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        _connections[id] = new Session(id, address ?? "unknown", now, now);
        return id;
    }
    public void Activity(string id)
    {
        if (_connections.TryGetValue(id, out var current))
            _connections[id] = current with { LastPacket = DateTimeOffset.UtcNow };
    }
    public void Disconnect(string id) => _connections.TryRemove(id, out _);
    public object Overview() => new {
        count = _connections.Count,
        connections = _connections.Values.OrderByDescending(s => s.Connected).Select(s =>
            new { id = s.Id[..8], remote = s.Remote, connectedAt = s.Connected, lastPacketAt = s.LastPacket }).ToArray()
    };
}
