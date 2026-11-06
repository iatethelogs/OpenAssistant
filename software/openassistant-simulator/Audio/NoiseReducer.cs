namespace OpenAssistant.Simulator.Audio;

/// <summary>
/// Lightweight deterministic high-pass and adaptive noise expander for 16 kHz mono PCM16.
/// This is not acoustic echo cancellation: hardware/Windows communications DSP performs AEC.
/// A soft expander attenuates stationary low-level noise without distorting loud speech.
/// </summary>
public sealed class NoiseReducer
{
    private double _previousInput;
    private double _previousHighPass;
    private double _noiseRms = 0.006;
    private double _gain = 1.0;
    public bool Enabled { get; set; } = true;
    public double LastRms { get; private set; }

    public byte[] Process(byte[] data)
    {
        if ((data.Length & 1) != 0) return data;
        if (data.Length == 0) return data;
        var filtered = new double[data.Length / 2];
        double energy = 0;
        for (int i = 0; i < filtered.Length; i++)
        {
            var x = BitConverter.ToInt16(data, i * 2) / 32768.0;
            var hp = x - _previousInput + 0.972 * _previousHighPass;
            _previousInput = x;
            _previousHighPass = hp;
            filtered[i] = hp;
            energy += hp * hp;
        }
        double rms = Math.Sqrt(energy / filtered.Length);
        LastRms = rms;
        if (!Enabled) return data;
        // Update the floor mostly during silence, slowly when speech is present.
        double speed = rms < _noiseRms * 1.8 ? 0.022 : 0.0005;
        _noiseRms = Math.Clamp(_noiseRms * (1-speed) + rms * speed, 0.0015, 0.05);
        double lower = Math.Max(0.003, _noiseRms * 1.7);
        double upper = lower * 2.7;
        double target = rms <= lower ? 0.18 :
                        rms >= upper ? 1.0 :
                        0.18 + 0.82 * (rms - lower) / (upper - lower);
        // Smooth gains between successive microphone callbacks.
        double nextGain = _gain + (target - _gain) * (target > _gain ? 0.32 : 0.085);
        var output = new byte[data.Length];
        for (int i = 0; i < filtered.Length; i++)
        {
            double progress = (double)(i+1) / filtered.Length;
            double gain = _gain + (nextGain - _gain) * progress;
            short sample = (short)Math.Clamp((int)Math.Round(filtered[i] * gain * 32767), -32768, 32767);
            output[i * 2] = (byte)(sample & 255);
            output[i * 2 + 1] = (byte)((ushort)sample >> 8);
        }
        _gain = nextGain;
        return output;
    }
    public void Reset()
    {
        _previousInput = _previousHighPass = 0;
        _noiseRms = 0.006;
        _gain = 1;
    }
}
