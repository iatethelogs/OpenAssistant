using System.Net;
using System.Net.NetworkInformation;

namespace OpenAssistant.Server.ConsoleUi;

public static class StartupBanner
{
    public static void Print(string url)
    {
        var old = System.Console.ForegroundColor;
        if (!System.Console.IsOutputRedirected) System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine(@" 
  ___  ____  _____ _   _ 
 / _ \|  _ \| ____| \ | |
| | | | |_) |  _| |  \| |
| |_| |  __/| |___| |\  |
 \___/|_|   |_____|_| \_|

    _    ____ ____ ___ ____ _____  _    _   _ _____
   / \  / ___/ ___|_ _/ ___|_   _|/ \  | \ | |_   _|
  / _ \ \___ \___ \| |\___ \ | | / _ \ |  \| | | |
 / ___ \ ___) |__) | | ___) || |/ ___ \| |\  | | |
/_/   \_\____/____/___|____/ |_/_/   \_\_| \_| |_|
");
        if (!System.Console.IsOutputRedirected) System.Console.ForegroundColor = ConsoleColor.White;
        try
        {
            ShowAddresses(url);
        }
        finally
        {
            if (!System.Console.IsOutputRedirected)
                System.Console.ForegroundColor = old;
        }
    }

    private static void ShowAddresses(string listeningUrl)
    {
        if (!Uri.TryCreate(listeningUrl, UriKind.Absolute, out var binding) ||
            binding.Scheme is not ("http" or "https"))
        {
            System.Console.WriteLine("  Адрес веб-сервера: " + listeningUrl);
            return;
        }

        string scheme = binding.Scheme;
        string websocketScheme = scheme == "https" ? "wss" : "ws";
        int port = binding.Port;
        bool wildcard = binding.Host is "0.0.0.0" or "::" or "+" or "*";
        var hosts = wildcard
            ? LanAddresses()
            : new List<string> { binding.Host };

        if (hosts.Count == 0)
        {
            // A wildcard is a listening address, not a routable host address.
            System.Console.WriteLine("  LAN-адрес не обнаружен. Проверьте IP сетевого интерфейса.");
            hosts.Add("127.0.0.1");
        }

        foreach (string host in hosts)
        {
            string authority = host.Contains(':') ? $"[{host}]" : host;
            string portPart = $":{port}";
            System.Console.WriteLine($"  Панель:      {scheme}://{authority}{portPart}/");
            System.Console.WriteLine($"  Устройства:  {websocketScheme}://{authority}{portPart}/ws");
            System.Console.WriteLine();
        }

        if (wildcard && hosts.All(x => x != "127.0.0.1"))
            System.Console.WriteLine($"  На сервере:  {scheme}://127.0.0.1:{port}/");
    }

    private static List<string> LanAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                .OrderBy(n => n.NetworkInterfaceType is
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet or
                    System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(a))
                .Select(a => a.GetAddressBytes())
                .Where(a => a[0] == 10 ||
                           (a[0] == 172 && a[1] >= 16 && a[1] <= 31) ||
                           (a[0] == 192 && a[1] == 168))
                .Select(bytes => new System.Net.IPAddress(bytes).ToString())
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }
}
