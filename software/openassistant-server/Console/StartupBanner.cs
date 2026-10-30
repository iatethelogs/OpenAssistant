namespace OpenAssistant.Server.ConsoleUi;

/// <summary>текстовая заставка и параметры запущенного сервера.</summary>
public static class StartupBanner
{
    /// <summary>печатает ascii-логотип без зелёных точек и прочей новогодней херни.</summary>
    public static void Print(string url)
    {
        if (System.Console.IsOutputRedirected)
        {
            System.Console.WriteLine($"OPENASSISTANT SERVER | {url}");
            return;
        }
        var previous = System.Console.ForegroundColor;
        try
        {
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine("  +---------------------------------------+");
            System.Console.WriteLine("  |                                       |");
            System.Console.WriteLine("  |       O P E N   A S S I S T A N T     |");
            System.Console.WriteLine("  |                                       |");
            System.Console.WriteLine("  +---------------------------------------+");
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("  SERVER  /  LOCAL VOICE PLATFORM");
            System.Console.WriteLine("  ─────────────────────────────────────────────────────────────");
            System.Console.ForegroundColor = ConsoleColor.White;
            string displayUrl = url.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
            System.Console.WriteLine($"  Control panel  {displayUrl}/");
            System.Console.WriteLine($"  Settings       {displayUrl}/settings.html");
            System.Console.WriteLine($"  Connections    {displayUrl}/connections.html");
            System.Console.WriteLine($"  Models / GPU   {displayUrl}/models.html");
            System.Console.WriteLine($"  Voice chat     {displayUrl}/chat.html");
            System.Console.WriteLine($"  WebSocket      {displayUrl.Replace("http", "ws", StringComparison.Ordinal)}/ws");
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine("  GigaAM  /  Qwen3-TTS  /  OpenAI-compatible LLM");
            System.Console.WriteLine("  Ctrl+C to stop");
            System.Console.WriteLine();
        }
        finally { System.Console.ForegroundColor = previous; }
    }
}
