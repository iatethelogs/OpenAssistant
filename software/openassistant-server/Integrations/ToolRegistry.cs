using System.Text.Json;

namespace OpenAssistant.Server.Integrations;

/// <summary>унифицированная точка регистрации инструментов; сюда цепляем новые API.</summary>
public sealed class ToolRegistry(IEnumerable<IAssistantTool> tools)
{
    private readonly Dictionary<string, IAssistantTool> _tools =
        tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>возвращает названия доступных команд.</summary>
    public string[] Names() => _tools.Keys.Order().ToArray();

    /// <summary>исполняет инструмент после проверки имени; незнакомые команды нахрен не запускаем.</summary>
    public async Task<object> InvokeAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new ArgumentException("неизвестный инструмент: " + name);
        return await tool.ExecuteAsync(args, ct);
    }
}

/// <summary>контракт подключения API, музыкального провайдера или действия.</summary>
public interface IAssistantTool
{
    string Name { get; }
    Task<object> ExecuteAsync(JsonElement args, CancellationToken ct);
}
