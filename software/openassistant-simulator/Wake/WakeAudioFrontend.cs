using System.Numerics;

namespace OpenAssistant.Simulator.Wake;

/// <summary>
/// 16 kHz MicroWakeWord spectrogram pipeline: 30 ms windows, 10 ms steps, 40
/// mel channels, spectral subtraction, PCAN, logarithmic quantization.
///
/// Algorithm adapted to C# from the MIT-licensed Go microWakeWord frontend
/// by Serge Zaitsev (https://github.com/zserge/microwakeword).
/// This is a floating-point approximation of TFLite Micro's fixed-point
/// microfrontend; model thresholds must be calibrated against real recordings.
/// </summary>
internal sealed class WakeAudioFrontend
{
    private const int WindowLength = 480, StepLength = 160, FftSize = 512, Channels = 40;
    private readonly double[] _window = new double[WindowLength];
    private readonly double[][] _mel = new double[Channels][];
    private readonly double[] _background = new double[Channels];
    private readonly List<short> _audio = new(640);
    private bool _initial = true;

    public WakeAudioFrontend()
    {
        for (int i = 0; i < WindowLength; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / WindowLength);
        const double sampleRate = 16000;
        double min = Mel(125), max = Mel(7500);
        var points = Enumerable.Range(0, Channels + 2)
            .Select(n => Hz(min + n * (max - min) / (Channels + 1)) * FftSize / sampleRate).ToArray();
        for (int i = 0; i < Channels; i++)
        {
            var bins = new double[FftSize / 2 + 1];
            for (int k = 0; k < bins.Length; k++)
            {
                double left = points[i], center = points[i+1], right = points[i+2];
                if (k >= left && k <= center)
                    bins[k] = (k - left) / (center - left);
                else if (k > center && k <= right)
                    bins[k] = (right - k) / (right - center);
            }
            _mel[i] = bins;
        }
    }

    private static double Mel(double hz) => 1127 * Math.Log(1 + hz / 700);
    private static double Hz(double mel) => 700 * (Math.Exp(mel / 1127) - 1);

    public void Reset()
    {
        _audio.Clear();
        Array.Clear(_background);
        _initial = true;
    }

    public IEnumerable<sbyte[]> Accept(byte[] pcm)
    {
        if (pcm.Length % 2 != 0) yield break;
        for (int i = 0; i < pcm.Length; i += 2)
            _audio.Add(unchecked((short)(pcm[i] | pcm[i + 1] << 8)));
        while (_audio.Count >= WindowLength)
        {
            var chunk = new short[WindowLength];
            _audio.CopyTo(0, chunk, 0, WindowLength);
            yield return Features(chunk);
            _audio.RemoveRange(0, StepLength);
        }
    }

    private sbyte[] Features(short[] audio)
    {
        var spectrum = new Complex[FftSize];
        for (int i = 0; i < WindowLength; i++)
            spectrum[i] = new Complex(audio[i] * _window[i], 0);
        // Iterative radix-2 FFT; no native process, no second microphone.
        int j = 0;
        for (int i = 1; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
            j ^= bit;
            if (i < j) (spectrum[i], spectrum[j]) = (spectrum[j], spectrum[i]);
        }
        for (int len = 2; len <= FftSize; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            Complex delta = new(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < FftSize; i += len)
            {
                Complex factor = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var even = spectrum[i+k], odd = spectrum[i+k+len/2] * factor;
                    spectrum[i+k] = even + odd;
                    spectrum[i+k+len/2] = even - odd;
                    factor *= delta;
                }
            }
        }
        var energy = new double[FftSize/2+1];
        for (int i = 0; i < energy.Length; i++)
            energy[i] = spectrum[i].Magnitude * spectrum[i].Magnitude;

        var result = new sbyte[Channels];
        for (int ch = 0; ch < Channels; ch++)
        {
            double sum = 0;
            for (int k = 0; k < energy.Length; k++) sum += energy[k] * _mel[ch][k];
            var observed = Math.Sqrt(Math.Max(0, sum));
            if (_initial) _background[ch] = observed;
            else
            {
                var alpha = observed > _background[ch] ? 0.06 : 0.025;
                _background[ch] += (observed - _background[ch]) * alpha;
            }
            double remaining = Math.Max(observed - _background[ch], observed * .05);
            remaining = remaining * 80 / Math.Pow(_background[ch] + 80, .95);
            int raw = (int)Math.Clamp(Math.Log(Math.Max(remaining, 1)) * 64, 0, 65535);
            int quantized = (raw * 256 + 333) / 666 - 128;
            result[ch] = (sbyte)Math.Clamp(quantized, -128, 127);
        }
        _initial = false;
        return result;
    }
}
