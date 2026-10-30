namespace Rampside.Hosting.Audio;

/// <summary>
/// Makes a clean voice clip sound like the headset line between tug and cockpit: telephone band (about
/// 300-3400 Hz), a little saturation and a faint hiss. The clips are stored clean so this can change.
/// </summary>
public static class RadioEffect
{
    private const double LowCutHz = 300;
    private const double HighCutHz = 3400;
    private const double Drive = 1.8;
    private const double HissLevel = 0.004;
    private const double Peak = 0.89;

    /// <param name="volume">0..1, applied after the effect.</param>
    public static WavClip Apply(WavClip clip, double volume, Random random)
    {
        var rate = clip.SampleRate;
        var highPass1 = Biquad.HighPass(LowCutHz, rate);
        var highPass2 = Biquad.HighPass(LowCutHz, rate);
        var lowPass1 = Biquad.LowPass(Math.Min(HighCutHz, rate * 0.45), rate);
        var lowPass2 = Biquad.LowPass(Math.Min(HighCutHz, rate * 0.45), rate);

        var buffer = new double[clip.Samples.Length];
        var peak = 0.0;
        var norm = Math.Tanh(Drive);
        for (var i = 0; i < buffer.Length; i++)
        {
            var x = clip.Samples[i] / 32768.0 + (random.NextDouble() * 2 - 1) * HissLevel;
            x = lowPass2.Process(lowPass1.Process(highPass2.Process(highPass1.Process(x))));
            x = Math.Tanh(x * Drive) / norm;
            buffer[i] = x;
            peak = Math.Max(peak, Math.Abs(x));
        }

        var gain = peak > 0 ? Peak * Math.Clamp(volume, 0, 1) / peak : 0;
        var samples = new short[buffer.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(Math.Round(buffer[i] * gain * 32767), short.MinValue, short.MaxValue);
        }

        return new WavClip(samples, rate);
    }

    /// <summary>Second-order filter (RBJ audio EQ cookbook), Q = 0.707.</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public static Biquad HighPass(double frequency, int rate)
        {
            var (cos, alpha) = Coefficients(frequency, rate);
            return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public static Biquad LowPass(double frequency, int rate)
        {
            var (cos, alpha) = Coefficients(frequency, rate);
            return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
        }

        public double Process(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }

        private static (double Cos, double Alpha) Coefficients(double frequency, int rate)
        {
            var w0 = 2 * Math.PI * frequency / rate;
            return (Math.Cos(w0), Math.Sin(w0) / (2 * 0.707));
        }
    }
}
