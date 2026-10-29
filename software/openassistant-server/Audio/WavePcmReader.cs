using System.Buffers.Binary;
using System.Text;

namespace OpenAssistant.Server.Audio;

/// <summary>проверка формата wav, который вернул gpu-синтезатор.</summary>
public static class WavePcmReader
{
    /// <summary>достаёт pcm16 mono и проверяет частоту, чтобы колонка не играла кашу.</summary>
    public static byte[] ReadPcm16(byte[] wav, int expectedSampleRate)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("tts должен вернуть wav");
        int offset = 12;
        bool validFmt = false;
        while (offset + 8 <= wav.Length)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            if (length < 0 || (long)offset + 8 + length > wav.Length)
                throw new InvalidDataException("повреждён wav");
            string chunk = Encoding.ASCII.GetString(wav, offset, 4);
            int dataOffset = offset + 8;
            if (chunk == "fmt " && length >= 16)
            {
                var format = wav.AsSpan(dataOffset, length);
                validFmt = BinaryPrimitives.ReadInt16LittleEndian(format) == 1
                    && BinaryPrimitives.ReadInt16LittleEndian(format[2..]) == 1
                    && BinaryPrimitives.ReadInt32LittleEndian(format[4..]) == expectedSampleRate
                    && BinaryPrimitives.ReadInt16LittleEndian(format[14..]) == 16;
            }
            if (chunk == "data")
            {
                if (!validFmt) throw new InvalidDataException("tts: ожидается wav pcm16 mono с правильной частотой");
                return wav.AsSpan(dataOffset, length).ToArray();
            }
            offset = checked(dataOffset + length + (length & 1));
        }
        throw new InvalidDataException("tts: wav не содержит pcm-данных");
    }
}
