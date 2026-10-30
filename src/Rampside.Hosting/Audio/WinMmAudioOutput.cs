using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rampside.Hosting.Audio;

/// <summary>Plays a WAV file from memory, blocking until it has finished.</summary>
public interface IAudioOutput
{
    void Play(byte[] wav);

    /// <summary>Ends the clip that is playing (from any thread).</summary>
    void Stop();
}

/// <summary>Windows' own PlaySound: no extra library, one clip at a time, which is all the crew voice needs.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmAudioOutput : IAudioOutput
{
    private const uint SndSync = 0x0000;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    public void Play(byte[] wav) => PlaySound(wav, IntPtr.Zero, SndSync | SndMemory | SndNoDefault);

    public void Stop() => PlaySound(null, IntPtr.Zero, 0);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[]? sound, IntPtr module, uint flags);
}
