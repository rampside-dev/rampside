namespace Rampside.Hosting.Audio;

/// <summary>
/// Makes a clean voice clip sound like the headset line between tug and cockpit: telephone band (about
/// 300-3400 Hz), a little saturation and a faint hiss. The clips are stored clean so this can change.
/// With push-to-talk, the line hiss fades in shortly before the voice; after it the key is released with a
/// click and the squelch opens for a moment before it snaps shut.
/// </summary>
public static class RadioEffect
{
    private const double LowCutHz = 300;
    private const double HighCutHz = 3400;
    private const double Drive = 1.8;
    private const double HissLevel = 0.004;
    private const double Peak = 0.89;

    // Levels relative to the loudest point of the voice after the effect.
    private const double ReleaseLevel = 0.75;
    private const double SquelchLevel = 0.22;

    /// <summary>Silence at the start: sound devices often fade in the first milliseconds.</summary>
    public static readonly TimeSpan LeadSilence = TimeSpan.FromMilliseconds(80);

    /// <summary>Line hiss before the first word.</summary>
    public static readonly TimeSpan KeyLead = TimeSpan.FromMilliseconds(80);

    /// <summary>From the last word to the release click.</summary>
    public static readonly TimeSpan KeyTail = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan ClickLength = TimeSpan.FromMilliseconds(40);

    /// <summary>From the release click to the squelch opening.</summary>
    public static readonly TimeSpan SquelchDelay = TimeSpan.FromMilliseconds(6);

    public static readonly TimeSpan SquelchLength = TimeSpan.FromMilliseconds(68);

    /// <summary>Silence after the release click: players may cut the last milliseconds.</summary>
    public static readonly TimeSpan TrailSilence = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan HissFadeIn = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan SquelchEdge = TimeSpan.FromMilliseconds(3);

    /// <param name="volume">0..1, applied after the effect.</param>
    /// <param name="pushToTalk">Adds the lead-in, the release click and the squelch tail; the clip gets longer.</param>
    public static WavClip Apply(WavClip clip, double volume, Random random, bool pushToTalk = false)
    {
        var rate = clip.SampleRate;
        var lineOpens = pushToTalk ? Samples(LeadSilence, rate) : 0;
        var lead = pushToTalk ? lineOpens + Samples(KeyLead, rate) : 0;
        var voiceEnd = lead + clip.Samples.Length;
        var release = voiceEnd + Samples(KeyTail, rate);
        var length = pushToTalk ? release + Samples(ClickLength, rate) + Samples(TrailSilence, rate) : voiceEnd;

        var input = new double[length];
        for (var i = 0; i < clip.Samples.Length; i++)
        {
            input[lead + i] = clip.Samples[i] / 32768.0;
        }

        // The line hisses while the key is down; without push-to-talk, all the time.
        var hissTo = pushToTalk ? release + Samples(TimeSpan.FromMilliseconds(4), rate) : length;
        var fadeIn = pushToTalk ? Samples(HissFadeIn, rate) : 0;
        for (var i = lineOpens; i < hissTo; i++)
        {
            var fade = i - lineOpens < fadeIn ? (double)(i - lineOpens) / fadeIn : 1;
            input[i] += (random.NextDouble() * 2 - 1) * HissLevel * fade;
        }

        var buffer = Telephone(input, rate, saturate: true);
        var peak = 0.0;
        for (var i = lead; i < voiceEnd; i++)
        {
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        }

        if (pushToTalk)
        {
            // Leveled on their own after the filter, so the telephone band cannot swallow them.
            Mix(buffer, ReleaseClick(rate, random), release, ReleaseLevel * peak);
            Mix(buffer, Squelch(rate, random), release + Samples(SquelchDelay, rate), SquelchLevel * peak);
        }

        // Level by the voice alone, so click and squelch never push it down nor get louder than it.
        var gain = peak > 0 ? Peak * Math.Clamp(volume, 0, 1) / peak : 0;
        var samples = new short[buffer.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = Math.Clamp(buffer[i], -peak, peak) * gain;
            samples[i] = (short)Math.Clamp(Math.Round(x * 32767), short.MinValue, short.MaxValue);
        }

        return new WavClip(samples, rate);
    }

    private static double[] Telephone(double[] input, int rate, bool saturate)
    {
        var highPass1 = Biquad.HighPass(LowCutHz, rate);
        var highPass2 = Biquad.HighPass(LowCutHz, rate);
        var lowPass1 = Biquad.LowPass(Math.Min(HighCutHz, rate * 0.45), rate);
        var lowPass2 = Biquad.LowPass(Math.Min(HighCutHz, rate * 0.45), rate);
        var norm = Math.Tanh(Drive);
        var output = new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var x = lowPass2.Process(lowPass1.Process(highPass2.Process(highPass1.Process(input[i]))));
            output[i] = saturate ? Math.Tanh(x * Drive) / norm : x;
        }

        return output;
    }

    /// <summary>Adds <paramref name="sound"/> at <paramref name="start"/>, scaled so its peak is <paramref name="level"/>.</summary>
    private static void Mix(double[] buffer, double[] sound, int start, double level)
    {
        var soundPeak = sound.Max(Math.Abs);
        if (soundPeak <= 0)
        {
            return;
        }

        for (var i = 0; i < sound.Length && start + i < buffer.Length; i++)
        {
            buffer[start + i] += sound[i] * level / soundPeak;
        }
    }

    /// <summary>Resonances of the switch housing, relative to its base pitch; not harmonic, so it sounds like plastic.</summary>
    private static readonly (double Ratio, double Gain, double Decay)[] SwitchModes =
    {
        (1.0, 1.0, 0.0065),
        (1.37, 0.6, 0.0045),
        (0.62, 0.5, 0.009),
    };

    /// <summary>
    /// The PTT key coming up, already through the telephone band: the mic bias dropping (a decaying step), then
    /// the contact opening. It bounces once a few milliseconds later; each touch rings the switch housing (a few
    /// resonances) and adds a little contact noise. Slightly different every time.
    /// </summary>
    private static double[] ReleaseClick(int rate, Random random)
    {
        double Vary(double spread) => 1 - spread + 2 * spread * random.NextDouble();

        var baseHz = 1400 * Vary(0.1);
        var modes = SwitchModes.Select(m => (Hz: baseHz * m.Ratio * Vary(0.03), m.Gain, m.Decay)).ToArray();
        var modeGain = SwitchModes.Sum(m => m.Gain);
        var touches = new[] { (At: 0.0, Strength: 1.0), (At: 0.0008 + 0.0025 * random.NextDouble(), Strength: 0.3 + 0.2 * random.NextDouble()) };

        var raw = new double[Samples(ClickLength, rate)];
        for (var i = 0; i < raw.Length; i++)
        {
            var t = (double)i / rate;
            var step = -Math.Exp(-t / 0.004);
            var ring = 0.0;
            var noise = 0.0;
            foreach (var touch in touches)
            {
                var u = t - touch.At;
                if (u < 0)
                {
                    continue;
                }

                foreach (var mode in modes)
                {
                    ring += touch.Strength * mode.Gain * Math.Sin(2 * Math.PI * mode.Hz * u) * Math.Exp(-u / mode.Decay);
                }

                // Squared envelope: a harder onset than a plain exponential.
                var envelope = Math.Exp(-u / 0.005);
                noise += touch.Strength * (random.NextDouble() * 2 - 1) * envelope * envelope;
            }

            raw[i] = 0.5 * step + ring / modeGain + 0.5 * noise;
        }

        return Telephone(raw, rate, saturate: false);
    }

    /// <summary>The squelch opening for a moment: band-limited noise that sags a little, then snaps shut.</summary>
    private static double[] Squelch(int rate, Random random)
    {
        var highPass1 = Biquad.HighPass(500, rate);
        var highPass2 = Biquad.HighPass(500, rate);
        var lowPass1 = Biquad.LowPass(Math.Min(3200, rate * 0.45), rate);
        var lowPass2 = Biquad.LowPass(Math.Min(3200, rate * 0.45), rate);
        var length = Samples(SquelchLength, rate);
        var edge = Math.Max(1, Samples(SquelchEdge, rate));
        var noise = new double[length];
        for (var i = 0; i < length; i++)
        {
            var x = lowPass2.Process(lowPass1.Process(highPass2.Process(highPass1.Process(random.NextDouble() * 2 - 1))));
            var envelope = Math.Min(1, (double)i / edge) * Math.Min(1, (double)(length - 1 - i) / edge) * (1 - 0.35 * i / length);
            noise[i] = x * envelope;
        }

        return noise;
    }

    private static int Samples(TimeSpan time, int rate) => (int)(time.TotalSeconds * rate);

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
