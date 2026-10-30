using System.Collections.Concurrent;
using Rampside.Core.Services;

namespace Rampside.Hosting.Audio;

/// <summary>What a crew call sounds like: the voice, the clip files in order and the spoken text.</summary>
public sealed record PreparedCall(string Voice, IReadOnlyList<string> Files, string Text);

/// <summary>
/// Speaks <see cref="CrewCall"/>s on its own thread, one after another, through the radio effect. Each service keeps
/// its voice until a new crew member starts (<see cref="CrewCall.NewCrew"/>), so one pushback is one person.
/// Calls that waited too long are dropped: a late "release parking brake" would only confuse.
/// </summary>
public sealed class CrewVoicePlayer : IDisposable
{
    private static readonly TimeSpan PhraseGap = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

    private readonly CrewVoiceLibrary _library;
    private readonly IAudioOutput _output;
    private readonly Random _random;
    private readonly BlockingCollection<(CrewCall Call, DateTime Queued)> _queue = new();
    private readonly Dictionary<string, string> _voiceByService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastVariant = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _choiceLock = new();
    private readonly Thread _thread;
    private volatile bool _enabled = true;
    private double _volume = 0.8;

    public CrewVoicePlayer(CrewVoiceLibrary library, IAudioOutput output, Random? random = null)
    {
        _library = library;
        _output = output;
        _random = random ?? new Random();
        _thread = new Thread(Run) { IsBackground = true, Name = "Rampside crew voice" };
        _thread.Start();
    }

    /// <summary>Raised on the playback thread with the spoken text, just before it is played.</summary>
    public event EventHandler<string>? Speaking;

    /// <summary>Raised on the playback thread when a clip cannot be played (e.g. a broken file).</summary>
    public event EventHandler<string>? Problem;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                _output.Stop();
            }
        }
    }

    /// <summary>0..1.</summary>
    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 1));
    }

    public bool HasVoices => !_library.IsEmpty;

    public void Enqueue(CrewCall call)
    {
        if (_enabled && HasVoices && !_queue.IsAddingCompleted)
        {
            _queue.TryAdd((call, DateTime.UtcNow));
        }
    }

    /// <summary>A sample line in a random voice, for the settings page.</summary>
    public void PlaySample() => Enqueue(new CrewCall("sample", new[] { CrewPhrases.TugConnected }, NewCrew: true));

    /// <summary>Chooses voice and clip variants for <paramref name="call"/>; null when no clip fits. Does not play.</summary>
    public PreparedCall? Prepare(CrewCall call)
    {
        lock (_choiceLock)
        {
            var voice = VoiceFor(call);
            if (voice is null)
            {
                return null;
            }

            var files = new List<string>();
            var texts = new List<string>();
            foreach (var phrase in call.Phrases)
            {
                var variants = _library.Variants(voice, phrase);
                if (variants.Count == 0)
                {
                    continue;
                }

                var file = PickVariant(phrase, variants);
                files.Add(file);
                texts.Add(_library.Text(file) ?? phrase.Replace('_', ' '));
            }

            return files.Count == 0 ? null : new PreparedCall(voice, files, string.Join(" ", texts));
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _output.Stop();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var (call, queued) in _queue.GetConsumingEnumerable())
        {
            if (!_enabled || DateTime.UtcNow - queued > MaxDelay || Prepare(call) is not { } prepared)
            {
                continue;
            }

            try
            {
                var clip = WavClip.Join(prepared.Files.Select(_library.Clip).ToArray(), PhraseGap);
                var wav = RadioEffect.Apply(clip, Volume, _random, pushToTalk: true).ToWav();
                Speaking?.Invoke(this, prepared.Text);
                _output.Play(wav);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Problem?.Invoke(this, $"crew voice {prepared.Voice}: {ex.Message}");
            }
        }
    }

    private string? VoiceFor(CrewCall call)
    {
        var voices = _library.Voices.ToArray();
        if (voices.Length == 0)
        {
            return null;
        }

        if (!call.NewCrew && _voiceByService.TryGetValue(call.ServiceId, out var kept) && voices.Contains(kept, StringComparer.OrdinalIgnoreCase))
        {
            return kept;
        }

        // A new crew member: another voice than last time, when there is a choice.
        _voiceByService.TryGetValue(call.ServiceId, out var previous);
        var candidates = voices.Length > 1 && previous is not null
            ? voices.Where(v => !v.Equals(previous, StringComparison.OrdinalIgnoreCase)).ToArray()
            : voices;
        var voice = candidates[_random.Next(candidates.Length)];
        _voiceByService[call.ServiceId] = voice;
        return voice;
    }

    private string PickVariant(string phrase, IReadOnlyList<string> variants)
    {
        _lastVariant.TryGetValue(phrase, out var last);
        var candidates = variants.Count > 1 && last is not null ? variants.Where(v => v != last).ToArray() : variants.ToArray();
        var file = candidates[_random.Next(candidates.Length)];
        _lastVariant[phrase] = file;
        return file;
    }
}
