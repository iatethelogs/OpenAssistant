using System.Text;

namespace OpenAssistant.Server.Audio;

/// <summary>формирует корректный WAV PCM16 mono для GigaAM.</summary>
public static class WaveWriter
{
    /// <summary>упаковывает PCM16 в RIFF/WAVE и проверяет длину и частоту.</summary>
    public static byte[] Wrap(byte[] pcm, int rate)
    {
        if (rate < 8000 || rate > 48000) throw new ArgumentOutOfRangeException(nameof(rate));
        if ((pcm.Length & 1) != 0) throw new ArgumentException("нечётное число байт PCM16");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(checked(36 + pcm.Length));
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(checked(rate * 2));
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        return stream.ToArray();
    }
}
