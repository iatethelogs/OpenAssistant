using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAssistant.Server.Integrations;

namespace OpenAssistant.Server.Chat;

/// <summary>
/// Persisted tool contracts. They can be advertised to the LLM, but a tool that
/// has no registered IAssistantTool handler will return an explicit not-implemented
/// error rather than inventing a successful result.
/// </summary>
public sealed record CustomToolDefinition(
    string Name, string Title, string Description, JsonElement Parameters, bool Enabled);

public sealed class CustomToolStore(
    IWebHostEnvironment environment, AgentToolService builtIns, ToolRegistry registry)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ToolName = new(@"^[a-z][a-z0-9_]{2,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private string FileName => Path.Combine(environment.ContentRootPath, "custom-tools.local.json");

    private async Task<List<CustomToolDefinition>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FileName)) return [];
        string json = await File.ReadAllTextAsync(FileName, ct);
        if (json.Length > 900_000) throw new InvalidDataException("Каталог инструментов слишком большой");
        return JsonSerializer.Deserialize<List<CustomToolDefinition>>(json, JsonOptions)
            ?? throw new InvalidDataException("Каталог инструментов повреждён");
    }

    public async Task<CustomToolDefinition[]> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadAsync(ct)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<CustomToolDefinition[]> UpsertAsync(
        string name, CustomToolDefinition definition, CancellationToken ct)
    {
        if (definition is null || name != definition.Name)
            throw new ArgumentException("Имя в URL и описании инструмента должно совпадать");
        Validate(definition);
        if ((await builtIns.ListAsync(ct)).Any(x => x.Name == name) ||
            registry.Names().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Это имя занято встроенным инструментом");
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadAsync(ct);
            int ix = items.FindIndex(x => x.Name == name);
            if (ix < 0)
            {
                if (items.Count >= 64) throw new ArgumentException("Допускается не более 64 инструментов");
                items.Add(definition);
            }
            else items[ix] = definition;
            await WriteAsync(items, ct);
            return items.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<CustomToolDefinition[]> DeleteAsync(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadAsync(ct);
            if (items.RemoveAll(x => x.Name == name) == 0)
                throw new ArgumentException("Инструмент не найден");
            await WriteAsync(items, ct);
            return items.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(List<CustomToolDefinition> items, CancellationToken ct)
    {
        string tmp = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(items, JsonOptions), ct);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, FileName, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void Validate(CustomToolDefinition definition)
    {
        if (!ToolName.IsMatch(definition.Name ?? ""))
            throw new ArgumentException("Имя: 3–64 символа, строчные латинские буквы, цифры, подчёркивание");
        if (string.IsNullOrWhiteSpace(definition.Title) || definition.Title.Length > 100)
            throw new ArgumentException("Название: 1–100 символов");
        if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > 1000)
            throw new ArgumentException("Описание: 1–1000 символов");
        var schema = definition.Parameters;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "object")
            throw new ArgumentException("Параметры должны быть JSON Schema с type: object");
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Нужен объект properties в JSON Schema");
        if (schema.GetRawText().Length > 12000 || properties.EnumerateObject().Count() > 60)
            throw new ArgumentException("Слишком большая JSON Schema");
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("required должен быть массивом");
            foreach (var prop in required.EnumerateArray())
                if (prop.ValueKind != JsonValueKind.String ||
                    !properties.TryGetProperty(prop.GetString()!, out _))
                    throw new ArgumentException("В required указан неизвестный параметр");
        }
    }
}
