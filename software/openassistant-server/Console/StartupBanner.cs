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
        System.Console.WriteLine("  " + url.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        if (!System.Console.IsOutputRedirected) System.Console.ForegroundColor = old;
    }
}
