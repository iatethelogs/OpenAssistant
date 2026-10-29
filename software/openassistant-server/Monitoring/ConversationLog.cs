namespace OpenAssistant.Server.Monitoring;

/// <summary>кольцевой журнал распознанной речи и ответов llm для локальной веб-панели.</summary>
public sealed class ConversationLog
{
    private readonly Queue<ConversationEntry> _events = new();
    private readonly object _gate = new();
    private long _lastId;
    /// <summary>добавляет событие, не сохраняя бесконечную историю на диске.</summary>
    public void Add(string kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            _events.Enqueue(new ConversationEntry(++_lastId, DateTimeOffset.UtcNow, kind, text));
            while (_events.Count > 250) _events.Dequeue();
        }
    }
    /// <summary>возвращает новые записи после последнего полученного идентификатора.</summary>
    public ConversationEntry[] Since(long id)
    {
        lock (_gate) return _events.Where(x => x.Id > id).ToArray();
    }
}
/// <summary>запись журнала разговора.</summary>
public sealed record ConversationEntry(long Id, DateTimeOffset Time, string Kind, string Text);
