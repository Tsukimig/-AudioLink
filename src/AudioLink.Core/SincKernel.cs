using System.Collections.Concurrent;

namespace AudioLink.Core;

/// <summary>64-tap, 1024-phase Blackman-windowed low-pass interpolation.</summary>
public sealed class SincKernel
{
    public const int Taps = 64;
    public const int Half = Taps / 2;
    public const int Phases = 1024;
    private static readonly ConcurrentDictionary<(int, int), SincKernel> Cache = new();
    private readonly float[] _table = new float[(Phases + 1) * Taps];

    public static SincKernel ForRates(int sourceRate, int outputRate) => Cache.GetOrAdd((sourceRate, outputRate), pair => new(pair.Item1, pair.Item2));

    private SincKernel(int sourceRate, int outputRate)
    {
        double cutoff = Math.Min(1, (double)outputRate / sourceRate) * 0.94;
        for (int phase = 0; phase <= Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double x = tap - (Half - 1) - fraction;
                double u = x / Half;
                double window = Math.Abs(u) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);
                double z = Math.PI * x * cutoff;
                double value = cutoff * (Math.Abs(z) < 1e-12 ? 1 : Math.Sin(z) / z) * window;
                _table[phase * Taps + tap] = (float)value;
                sum += value;
            }
            for (int tap = 0; tap < Taps; tap++) _table[phase * Taps + tap] /= (float)sum;
        }
    }

    public float Sample(ReadOnlySpan<float> samples, int channels, int channel, double frame)
    {
        int whole = (int)frame;
        double phasePosition = (frame - whole) * Phases;
        int phase = Math.Min(Phases - 1, (int)phasePosition);
        float fraction = (float)(phasePosition - phase);
        int table = phase * Taps;
        int index = (whole - Half + 1) * channels + channel;
        float a = 0, b = 0;
        for (int tap = 0; tap < Taps; tap++, index += channels)
        {
            float sample = samples[index];
            a += sample * _table[table + tap];
            b += sample * _table[table + Taps + tap];
        }
        return a + (b - a) * fraction;
    }
}
