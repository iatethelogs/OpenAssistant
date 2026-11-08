using System.Text;

namespace OpenAssistant.Server.Chat;

/// <summary>Global speaking policy plus a deterministic final output filter.</summary>
public static class NoEmoji
{
    public const string Instruction =
        "Отвечай обычным текстом, без эмодзи, смайликов, пиктограмм, emoji-флагов " +
        "и декоративных символов. Не вставляй их даже по просьбе пользователя.";

    // Exclude pictographs and emoji presentation characters. Cyrillic text, ordinary
    // punctuation, mathematical symbols and supplementary CJK remain unchanged.
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp is >= 0x1F000 and <= 0x1FAFF ||
                cp is >= 0x2600 and <= 0x27BF ||
                cp is >= 0x2300 and <= 0x23FF ||
                cp is 0x200D or 0xFE0F or 0x20E3 or 0x2934 or 0x2935 or
                    0x2B05 or 0x2B06 or 0x2B07 or 0x3030 or 0x303D or
                    0x3297 or 0x3299)
                continue;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}

/// <summary>Supports surrogate pairs split across SSE text fragments.</summary>
public sealed class NoEmojiStream
{
    private char? _pendingHigh;
    public string Push(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return "";
        if (_pendingHigh is char prior)
        {
            chunk = prior + chunk;
            _pendingHigh = null;
        }
        if (char.IsHighSurrogate(chunk[^1]))
        {
            _pendingHigh = chunk[^1];
            chunk = chunk[..^1];
        }
        return NoEmoji.Strip(chunk);
    }
    public string Complete()
    {
        _pendingHigh = null;
        return "";
    }
}
