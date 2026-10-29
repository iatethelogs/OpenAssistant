using System.Text;

namespace OpenAssistant.Server.Chat;

/// <summary>выделяет законченные предложения из потока токенов языковой модели.</summary>
public sealed class SentenceSegmenter
{
    private readonly StringBuilder _buffer = new();

    /// <summary>выдаёт готовую фразу, чтобы tts не ждал весь ответ.</summary>
    public string? Push(string token)
    {
        _buffer.Append(token);
        if (_buffer.Length < 2) return null;
        char last = _buffer[^1];
        if (last is '.' or '!' or '?' or '…' or '\n')
        {
            var sentence = _buffer.ToString().Trim();
            _buffer.Clear();
            return sentence.Length > 0 ? sentence : null;
        }
        return null;
    }

    /// <summary>забирает оставшийся хвост ответа после окончания генерации.</summary>
    public string? Flush()
    {
        string tail = _buffer.ToString().Trim();
        _buffer.Clear();
        return tail.Length > 0 ? tail : null;
    }
}
