using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenAssistant.Server.Monitoring;

/// <summary>Метрики только процесса и локального диска моделей; без доступа к чужим приложениям.</summary>
public static class SystemMetrics
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    public static object Snapshot(IWebHostEnvironment environment)
    {
        using var process = Process.GetCurrentProcess();
        string root = Path.GetPathRoot(Path.GetFullPath(environment.ContentRootPath)) ?? "/";
        DriveInfo? drive = null;
        try { drive = new DriveInfo(root); if (!drive.IsReady) drive = null; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        return new {
            os = RuntimeInformation.OSDescription,
            dotnet = RuntimeInformation.FrameworkDescription,
            cpuThreads = Environment.ProcessorCount,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            serverMemoryMiB = Math.Round(process.WorkingSet64 / 1048576d, 1),
            diskFreeGiB = drive is null ? (double?)null : Math.Round(drive.AvailableFreeSpace / 1073741824d, 1),
            diskTotalGiB = drive is null ? (double?)null : Math.Round(drive.TotalSize / 1073741824d, 1)
        };
    }
}
