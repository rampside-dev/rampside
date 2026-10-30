using System.Buffers.Binary;

namespace Rampside.Hosting.Audio;

/// <summary>Mono 16-bit PCM audio, as the ground crew clips are stored.</summary>
public sealed class WavClip
{
    public WavClip(short[] samples, int sampleRate)
    {
        Samples = samples;
        SampleRate = sampleRate;
    }

    public short[] Samples { get; }
    public int SampleRate { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>Reads a mono 16-bit PCM WAV file. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static WavClip Read(byte[] data)
    {
        if (data.Length < 12 || !Tag(data, 0, "RIFF") || !Tag(data, 8, "WAVE"))
        {
            throw new InvalidDataException("not a WAV file");
        }

        int? rate = null;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4));
            var body = offset + 8;
            if (size < 0 || body + size > data.Length)
            {
                // Some encoders write a too large data size; take what is there.
                size = data.Length - body;
            }

            if (Tag(data, offset, "fmt "))
            {
                var format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body));
                var channels = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 2));
                var bits = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 14));
                if (format != 1 || channels != 1 || bits != 16)
                {
                    throw new InvalidDataException($"only mono 16-bit PCM is supported (format {format}, {channels} channel(s), {bits} bit)");
                }

                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
            }
            else if (Tag(data, offset, "data"))
            {
                if (rate is null)
                {
                    throw new InvalidDataException("data before fmt chunk");
                }

                var samples = new short[size / 2];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 2 * i));
                }

                return new WavClip(samples, rate.Value);
            }

            offset = body + size + (size & 1);
        }

        throw new InvalidDataException("no data chunk");
    }

    public static WavClip Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>The clip as a complete WAV file in memory.</summary>
    public byte[] ToWav()
    {
        var dataSize = Samples.Length * 2;
        var wav = new byte[44 + dataSize];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);
        for (var i = 0; i < Samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + 2 * i)..], Samples[i]);
        }

        return wav;
    }

    /// <summary>Clips one after another with a short pause between them. All clips must share one sample rate.</summary>
    public static WavClip Join(IReadOnlyList<WavClip> clips, TimeSpan gap)
    {
        if (clips.Count == 0)
        {
            throw new ArgumentException("no clips", nameof(clips));
        }

        var rate = clips[0].SampleRate;
        if (clips.Any(c => c.SampleRate != rate))
        {
            throw new InvalidDataException("clips have different sample rates");
        }

        var gapSamples = (int)(gap.TotalSeconds * rate);
        var joined = new List<short>(clips.Sum(c => c.Samples.Length) + gapSamples * (clips.Count - 1));
        for (var i = 0; i < clips.Count; i++)
        {
            if (i > 0)
            {
                joined.AddRange(new short[gapSamples]);
            }

            joined.AddRange(clips[i].Samples);
        }

        return new WavClip(joined.ToArray(), rate);
    }

    private static bool Tag(byte[] data, int offset, string tag) =>
        offset + 4 <= data.Length
        && data[offset] == tag[0] && data[offset + 1] == tag[1] && data[offset + 2] == tag[2] && data[offset + 3] == tag[3];
}
