using OpenAssistant.Server.Models;

namespace OpenAssistant.Server.Monitoring;

/// <summary>После перезапуска сервера поднимает сохранённые профили нейросетей.</summary>
public sealed class ModelAutoStartService(ModelProcessManager manager, ILogger<ModelAutoStartService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(1500, stoppingToken);
            var profiles = await manager.ProfilesAsync(stoppingToken);
            foreach (var role in new[] { "llm", "stt", "tts" })
            {
                if (!profiles.ContainsKey(role)) continue;
                try
                {
                    await manager.StartAsync(role, stoppingToken);
                    logger.LogInformation("Восстановлен процесс модели {Role}", role);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception or IOException)
                {
                    logger.LogWarning(ex, "Не удалось автоматически запустить {Role}", role);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Ошибка восстановления моделей"); }
    }
}
