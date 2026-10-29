using System.Text;

namespace OpenAssistant.Server.Audio;

/// <summary>упаковка сырого микрофонного PCM16 в wav для распознавателя.</summary>
public static class WaveWriter
{
    /// <summary>делает полноценный заголовок wav; без этой штуки whisper не поймёт, что ему скормили.</summary>
    public static byte[] Wrap(byte[] pcm, int rate)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        return stream.ToArray();
    }
}
