namespace OpenAssistant.Server.Audio;

/// <summary>разбивает готовые PCM-данные на небольшие фреймы для отправки по сети.</summary>
public static class AudioChunker
{
    /// <summary>возвращает куски PCM16 mono не длиннее заданного размера.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Split(byte[] pcm, int chunkBytes = 4096)
    {
        for (int pos = 0; pos < pcm.Length; pos += chunkBytes)
            yield return pcm.AsMemory(pos, Math.Min(chunkBytes, pcm.Length - pos));
    }
}
