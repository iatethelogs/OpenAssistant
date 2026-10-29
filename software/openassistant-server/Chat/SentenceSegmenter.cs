using System.Text;

namespace OpenAssistant.Server.Chat;

/// <summary>отделяет готовые предложения из потокового ответа llm для синтезатора.</summary>
public sealed class SentenceSegmenter
{
    private readonly StringBuilder _buffer = new();
    private const int MaxPhraseLength = 240;

    /// <summary>находит границу предложения даже при нескольких знаках в одном токене.</summary>
    public string? Push(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        _buffer.Append(token);
        for (int i = 1; i < _buffer.Length; i++)
        {
            char current = _buffer[i];
            if (current is not ('.' or '!' or '?' or '…' or '\n')) continue;
            if (current == '.' && char.IsDigit(_buffer[i - 1]) && i + 1 < _buffer.Length && char.IsDigit(_buffer[i + 1])) continue;
            if (i + 1 < _buffer.Length && !char.IsWhiteSpace(_buffer[i + 1]) && _buffer[i + 1] is not ('"' or '»')) continue;
            string phrase = _buffer.ToString(0, i + 1).Trim();
            _buffer.Remove(0, i + 1);
            if (phrase.Length > 0) return phrase;
        }
        if (_buffer.Length >= MaxPhraseLength)
        {
            int pos = _buffer.ToString().LastIndexOf(' ', Math.Min(MaxPhraseLength, _buffer.Length - 1));
            if (pos <= 0) pos = MaxPhraseLength;
            string phrase = _buffer.ToString(0, pos).Trim();
            _buffer.Remove(0, pos);
            return phrase.Length > 0 ? phrase : null;
        }
        return null;
    }

    /// <summary>возвращает остаток ответа после завершения генерации.</summary>
    public string? Flush()
    {
        string phrase = _buffer.ToString().Trim();
        _buffer.Clear();
        return phrase.Length > 0 ? phrase : null;
    }
}
