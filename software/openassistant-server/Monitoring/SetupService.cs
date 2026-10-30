using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OpenAssistant.Server.Configuration;
using OpenAssistant.Server.Models;

namespace OpenAssistant.Server.Monitoring;

public sealed record LlamaSetupInput(
    string Executable, string ModelPath, string[] GpuUuids,
    int ContextSize, int GpuLayers, int Threads, int Port, int Parallel);

/// <summary>Настройка сервера и подключаемых устройств без shell и установки стороннего runtime.</summary>
public sealed class SetupService(SettingsStore store, ModelProcessManager manager, IOptionsMonitor<ServerOptions> options)
{
    public object LlamaExecutables()
    {
        string[] locations = [
            "/opt/llama.cpp/build/bin/llama-server",
            "/opt/llama.cpp/llama-server",
            "/usr/local/bin/llama-server",
            "/usr/bin/llama-server"
        ];
        return new { found = locations.Where(File.Exists).ToArray(), examples = locations };
    }

    public async Task<object> ConfigureLlamaAsync(LlamaSetupInput input, CancellationToken ct)
    {
        if (input is null) throw new ArgumentException("Нужны параметры llama.cpp");
        if (!Path.IsPathFullyQualified(input.Executable) || !File.Exists(input.Executable))
            throw new ArgumentException("Укажи существующий llama-server");
        if (!Path.IsPathFullyQualified(input.ModelPath) || !File.Exists(input.ModelPath) ||
            !input.ModelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажи скачанный GGUF-файл");
        if (input.ContextSize is < 256 or > 131072 || input.GpuLayers is < 0 or > 999 ||
            input.Threads is < 1 or > 256 || input.Port is < 1024 or > 65535 ||
            input.Parallel is < 1 or > 32 || input.GpuUuids is null)
            throw new ArgumentException("Некорректные параметры запуска llama.cpp");
        string dir = Path.GetDirectoryName(input.Executable) ?? throw new ArgumentException("Неправильный путь");
        await manager.SaveAsync("llm", new ModelLaunchProfile {
            Executable = input.Executable,
            WorkingDirectory = dir,
            ModelPath = input.ModelPath,
            GpuUuids = input.GpuUuids,
            Gpus = [],
            HealthUrl = $"http://127.0.0.1:{input.Port}/health",
            Arguments = ["-m", "{model}", "--host", "127.0.0.1",
                "--port", input.Port.ToString(), "--ctx-size", input.ContextSize.ToString(),
                "--n-gpu-layers", input.GpuLayers.ToString(), "--threads", input.Threads.ToString(),
                "--parallel", input.Parallel.ToString(), "--jinja"]
        }, ct);
        await manager.RestartAsync("llm", ct);
        await store.UpdateLlmUrlAsync($"http://127.0.0.1:{input.Port}", ct);
        return new { configured = true, url = $"http://127.0.0.1:{input.Port}", started = true };
    }

    public async Task<object> GenerateDeviceKeyAsync(CancellationToken ct)
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await store.UpdateDeviceTokenAsync(key, ct);
        return new { token = key, warning = "Предыдущий ключ перестаёт действовать для новых подключений" };
    }
}
