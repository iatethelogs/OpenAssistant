using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace OpenAssistant.Server.Monitoring;

/// <summary>
/// LAN-only UDP discovery. It never transmits credentials or executes remote
/// commands; discovery is separate from authenticated HTTP/WebSocket sessions.
/// </summary>
public sealed record DiscoveredClient(string Name, string Address, bool Connected);

public sealed class LanDiscoveryService(ILogger<LanDiscoveryService> logger, IConfiguration config) : BackgroundService
{
    public const int ServerPort = 5056;
    public const int SimulatorPort = 5057;
    private const string FindServer = "OA_DISCOVER_SERVER_V1";
    private const string ServerHere = "OA_SERVER_V1";
    private const string FindClient = "OA_DISCOVER_CLIENT_V1";
    private const string ClientHerePrefix = "OA_CLIENT_V1|";
    private static readonly Encoding Wire = Encoding.ASCII;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, ServerPort));
            logger.LogInformation("LAN discovery listening on UDP {Port}", ServerPort);
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult query = await listener.ReceiveAsync(stoppingToken);
                if (!IsPrivate(query.RemoteEndPoint.Address) ||
                    query.Buffer.Length > 128 ||
                    Wire.GetString(query.Buffer) != FindServer)
                    continue;
                string endpoint = config["Kestrel:Endpoints:Http:Url"] ?? "http://0.0.0.0:5055";
                int port = Uri.TryCreate(endpoint, UriKind.Absolute, out var serverUri) ? serverUri.Port : 5055;
                byte[] reply = Wire.GetBytes(ServerHere + "|" + port);
                await listener.SendAsync(reply, query.RemoteEndPoint, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "UDP server discovery unavailable (port {Port})", ServerPort);
        }
    }

    public async Task<DiscoveredClient[]> FindClientsAsync(CancellationToken ct)
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        probe.EnableBroadcast = true;
        byte[] message = Wire.GetBytes(FindClient);
        int sent = 0;
        foreach (var address in BroadcastTargets())
        {
            try
            {
                await probe.SendAsync(message, new IPEndPoint(address, SimulatorPort), ct);
                sent++;
            }
            catch (SocketException ex) { logger.LogDebug(ex, "Discovery UDP send to {Address} failed", address); }
        }
        if (sent == 0) return [];
        var found = new Dictionary<string, DiscoveredClient>(StringComparer.Ordinal);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(2.2));
        while (!window.IsCancellationRequested && found.Count < 32)
        {
            UdpReceiveResult packet;
            try { packet = await probe.ReceiveAsync(window.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            if (!IsPrivate(packet.RemoteEndPoint.Address) || packet.Buffer.Length > 160)
                continue;
            string response = Wire.GetString(packet.Buffer);
            if (!response.StartsWith(ClientHerePrefix, StringComparison.Ordinal))
                continue;
            string[] values = response.Split('|');
            if (values.Length != 3 || values[1] != "SIMULATOR")
                continue;
            string address = packet.RemoteEndPoint.Address.ToString();
            found[address] = new DiscoveredClient("Мартин · Windows", address, values[2] == "connected");
        }
        return found.Values.OrderBy(x => x.Address, StringComparer.Ordinal).ToArray();
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
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivate(unicast.Address) || unicast.IPv4Mask is null)
                        continue;
                    byte[] ip = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.Length != 4) continue;
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { /* fallback to 255.255.255.255 */ }
        return targets;
    }
}
