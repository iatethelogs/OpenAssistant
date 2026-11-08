using System.Buffers.Binary;
using NAudio.Wave;

namespace OpenAssistant.Simulator.Audio;

/// <summary>
/// Short in-process audio cues for wake activation and recognized end of speech.
/// Rendered as PCM through NAudio; no external audio files or Windows system sounds.
/// </summary>
public static class ActivationChime
{
    private const int Rate = 24000;

    public static async Task PlayAsync(bool completed = false)
    {
        const double duration = 0.24;
        int count = (int)(Rate * duration);
        var pcm = new byte[count * sizeof(short)];

        for (int i = 0; i < count; i++)
        {
            double t = i / (double)Rate;
            double segmentTime = t < 0.11 ? t : t - 0.12;
            bool active = t < 0.095 || t is >= 0.12 and < 0.22;
            double level = 0;

            if (active)
            {
                // 8 ms attack/release prevents clicks from the PCM edges.
                double segmentLength = t < 0.11 ? 0.095 : 0.10;
                double envelope = Math.Min(1, segmentTime / 0.008) *
                                  Math.Min(1, (segmentLength - segmentTime) / 0.018);
                double frequency = completed
                    ? (t < 0.11 ? 880 : 660)
                    : (t < 0.11 ? 784 : 1046);
                level = Math.Sin(t * 2 * Math.PI * frequency) * Math.Max(0, envelope) * 0.13;
            }

            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(i * sizeof(short), sizeof(short)),
                (short)Math.Clamp(Math.Round(level * short.MaxValue), short.MinValue, short.MaxValue));
        }

        using var output = new WaveOut { BufferMilliseconds = 40, NumberOfBuffers = 3 };
        var buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1))
        { DiscardOnBufferOverflow = true };
        buffer.AddSamples(pcm, 0, pcm.Length);
        output.Init(buffer);
        output.Play();
        await Task.Delay(320);
    }
}
