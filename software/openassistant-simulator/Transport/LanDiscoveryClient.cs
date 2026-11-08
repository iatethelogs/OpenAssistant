using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace OpenAssistant.Simulator.Transport;

public sealed record DiscoveredServer(string Address, string Url);

/// <summary>
/// UDP broadcasts are used only to find local peers, not to authenticate or
/// control them. The WebSocket remains the actual connection to the server.
/// </summary>
public sealed class LanDiscoveryClient : IAsyncDisposable
{
    private const int ServerPort = 5056;
    private const int SimulatorPort = 5057;
    private const string FindServer = "OA_DISCOVER_SERVER_V1";
    private const string ServerHere = "OA_SERVER_V1|";
    private const string FindClient = "OA_DISCOVER_CLIENT_V1";
    private static readonly Encoding Wire = Encoding.ASCII;

    private readonly Func<bool> _isConnected;
    private readonly UdpClient _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receiver;

    public LanDiscoveryClient(Func<bool> isConnected)
    {
        _isConnected = isConnected;
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, SimulatorPort));
        _receiver = ListenAsync(_stop.Token);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await _listener.ReceiveAsync(ct);
                if (!IsPrivate(request.RemoteEndPoint.Address) ||
                    request.Buffer.Length > 128 ||
                    Wire.GetString(request.Buffer) != FindClient)
                    continue;
                string reply = "OA_CLIENT_V1|SIMULATOR|" +
                    (_isConnected() ? "connected" : "waiting");
                await _listener.SendAsync(Wire.GetBytes(reply), request.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    public static async Task<DiscoveredServer[]> FindServersAsync(CancellationToken ct = default)
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        probe.EnableBroadcast = true;
        byte[] request = Wire.GetBytes(FindServer);
        int sent = 0;
        foreach (var target in BroadcastTargets())
        {
            try
            {
                await probe.SendAsync(request, new IPEndPoint(target, ServerPort), ct);
                sent++;
            }
            catch (SocketException) { /* try other interfaces */ }
        }
        if (sent == 0) return [];

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(2.2));
        var servers = new Dictionary<string, DiscoveredServer>(StringComparer.Ordinal);
        while (!window.IsCancellationRequested && servers.Count < 32)
        {
            UdpReceiveResult response;
            try { response = await probe.ReceiveAsync(window.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            if (!IsPrivate(response.RemoteEndPoint.Address) || response.Buffer.Length > 100)
                continue;
            string data = Wire.GetString(response.Buffer);
            if (!data.StartsWith(ServerHere, StringComparison.Ordinal) ||
                !int.TryParse(data[ServerHere.Length..], out var port) ||
                port is < 1024 or > 65535)
                continue;
            string ip = response.RemoteEndPoint.Address.ToString();
            servers[ip] = new DiscoveredServer(ip, $"ws://{ip}:{port}/ws");
        }
        return servers.Values.OrderBy(x => x.Address, StringComparer.Ordinal).ToArray();
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && (
            b[0] == 10 ||
            b[0] == 172 && b[1] >= 16 && b[1] <= 31 ||
            b[0] == 192 && b[1] == 168);
    }

    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var info in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivate(info.Address) || info.IPv4Mask is null)
                        continue;
                    var address = info.Address.GetAddressBytes();
                    var mask = info.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(address[i] | ~mask[i]);
                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { /* fallback to global broadcast */ }
        return targets;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { await _receiver; }
        catch (ObjectDisposedException) { }
        _stop.Dispose();
    }
}
