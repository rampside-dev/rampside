using Rampside.Core.Services;
using Rampside.Core.Simulation;
using Rampside.Hosting.Audio;

namespace Rampside.Core.Tests;

public class CrewVoiceTests
{
    [Fact]
    public void Full_pushback_says_each_step_in_order()
    {
        var rig = new TestRig();
        var calls = Record(rig);

        rig.Orchestrator.RequestPushback();
        rig.Run(6);
        rig.SetParkingBrake(false);
        rig.Run(5);
        rig.Orchestrator.SteerPushback(PushbackSteering.TailLeft);
        rig.Orchestrator.SteerPushback(PushbackSteering.TailLeft); // same again: no second call
        rig.Run(2);
        rig.Orchestrator.StopPushback();
        rig.Run(10);
        rig.SetParkingBrake(true);
        rig.Run(6);

        Assert.Equal(
            new[]
            {
                CrewPhrases.TugOnTheWay, CrewPhrases.TugConnected, CrewPhrases.PushStart, CrewPhrases.SteerTailLeft,
                CrewPhrases.PushStopping, CrewPhrases.SetBrake, CrewPhrases.Disconnecting, CrewPhrases.Disconnected,
            },
            calls.Select(c => string.Join("+", c.Phrases)));
        Assert.True(calls[0].NewCrew);
        Assert.All(calls.Skip(1), c => Assert.False(c.NewCrew));
        Assert.All(calls, c => Assert.Equal("pushback", c.ServiceId));
    }

    [Fact]
    public void Planned_pushback_names_the_direction()
    {
        var rig = new TestRig(state: FakeSimBridge.CreateDefaultState() with { Stand = FakeSimBridge.DemoStand });
        var calls = Record(rig);

        Assert.True(rig.Orchestrator.RequestPushback(CompassDirection.North).Accepted);
        rig.Run(6);
        rig.SetParkingBrake(false);
        rig.Run(1);

        Assert.Contains(calls, c => c.Phrases.SequenceEqual(new[] { CrewPhrases.PushStart, "facing_north" }));
    }

    [Fact]
    public void Brake_set_while_pushing_and_cancelling_are_announced()
    {
        var rig = new TestRig();
        var calls = Record(rig);
        rig.Orchestrator.RequestPushback();
        rig.Run(6);
        rig.SetParkingBrake(false);
        rig.Run(3);
        rig.SetParkingBrake(true);
        rig.Run(0.2);
        Assert.Equal(CrewPhrases.BrakeDuringPush, calls[^1].Phrases.Single());

        var cancel = new TestRig();
        var cancelCalls = Record(cancel);
        cancel.Orchestrator.RequestPushback();
        cancel.Run(6);
        cancel.Orchestrator.StopPushback();
        cancel.Run(0.2);
        Assert.Equal(CrewPhrases.Cancelled, cancelCalls[^1].Phrases.Single());
    }

    [Fact]
    public void Every_phrase_has_a_clip_in_every_voice()
    {
        var library = CrewVoiceLibrary.Load(CrewVoiceLibrary.DefaultFolder);

        Assert.True(library.Voices.Count >= 2, $"voices in {CrewVoiceLibrary.DefaultFolder}: {library.Voices.Count}");
        foreach (var voice in library.Voices)
        {
            foreach (var phrase in CrewPhrases.All)
            {
                var variants = library.Variants(voice, phrase);
                Assert.True(variants.Count > 0, $"{voice} has no clip for {phrase}");
                Assert.All(variants, file =>
                {
                    Assert.NotNull(library.Text(file));
                    Assert.InRange(library.Clip(file).Duration.TotalSeconds, 0.3, 10);
                });
            }
        }
    }

    [Fact]
    public void Player_keeps_the_voice_for_one_crew_and_joins_phrases()
    {
        var library = CrewVoiceLibrary.Load(CrewVoiceLibrary.DefaultFolder);
        using var player = new CrewVoicePlayer(library, new SilentOutput(), new Random(1));

        var first = player.Prepare(new CrewCall("pushback", new[] { CrewPhrases.TugOnTheWay }, NewCrew: true))!;
        var second = player.Prepare(new CrewCall("pushback", new[] { CrewPhrases.PushStart, "facing_north" }))!;
        var nextCrew = player.Prepare(new CrewCall("pushback", new[] { CrewPhrases.TugOnTheWay }, NewCrew: true))!;

        Assert.Equal(first.Voice, second.Voice);
        Assert.NotEqual(first.Voice, nextCrew.Voice);
        Assert.Equal(2, second.Files.Count);
        Assert.EndsWith("Facing north.", second.Text);
        Assert.Null(player.Prepare(new CrewCall("pushback", new[] { "no_such_phrase" })));
    }

    [Fact]
    public void Wav_round_trip_and_radio_effect_keep_length_and_limit_the_peak()
    {
        var rate = 24000;
        var tone = Enumerable.Range(0, rate / 2)
            .Select(i => (short)(20000 * Math.Sin(2 * Math.PI * 1000 * i / rate)))
            .ToArray();
        var clip = new WavClip(tone, rate);

        var read = WavClip.Read(clip.ToWav());
        Assert.Equal(rate, read.SampleRate);
        Assert.Equal(tone, read.Samples);

        var radio = RadioEffect.Apply(clip, volume: 0.5, new Random(1));
        Assert.Equal(tone.Length, radio.Samples.Length);
        Assert.InRange(radio.Samples.Max(s => Math.Abs((int)s)), 0.4 * 32767, 0.46 * 32767);
    }

    [Fact]
    public void Push_to_talk_release_click_and_squelch_follow_the_voice_without_drowning_it()
    {
        var rate = 24000;
        var tone = Enumerable.Range(0, rate / 2)
            .Select(i => (short)(20000 * Math.Sin(2 * Math.PI * 1000 * i / rate)))
            .ToArray();
        var clean = RadioEffect.Apply(new WavClip(tone, rate), volume: 0.5, new Random(1));
        var keyed = RadioEffect.Apply(new WavClip(tone, rate), volume: 0.5, new Random(1), pushToTalk: true);

        int At(TimeSpan time) => (int)(time.TotalSeconds * rate);
        var lineOpens = At(RadioEffect.LeadSilence);
        var lead = lineOpens + At(RadioEffect.KeyLead);
        var release = lead + tone.Length + At(RadioEffect.KeyTail);
        var squelch = release + At(RadioEffect.SquelchDelay);
        var squelchEnd = squelch + At(RadioEffect.SquelchLength);
        Assert.Equal(release + At(RadioEffect.ClickLength) + At(RadioEffect.TrailSilence), keyed.Samples.Length);

        // Silence at the start, so a sound device fading in does not swallow anything; then only faint hiss, no click.
        static int Loudest(IEnumerable<short> s) => s.Max(x => Math.Abs((int)x));
        var voice = Loudest(keyed.Samples.Skip(lead).Take(tone.Length));
        Assert.Equal(0, Loudest(keyed.Samples.Take(lineOpens)));
        Assert.InRange(Loudest(keyed.Samples.Skip(lineOpens).Take(lead - lineOpens - At(TimeSpan.FromMilliseconds(10)))), 1, 0.02 * voice);

        // The voice keeps its level and stays the loudest part; the release click is clear, the squelch quiet.
        Assert.InRange(voice, 0.4 * 32767, 0.46 * 32767);
        Assert.Equal(Loudest(clean.Samples), voice, tolerance: 0.02 * voice);
        Assert.InRange(Loudest(keyed.Samples.Skip(release).Take(At(TimeSpan.FromMilliseconds(15)))), 0.6 * voice, voice);
        var squelchMiddle = keyed.Samples.Skip(squelch + At(TimeSpan.FromMilliseconds(30))).Take(At(TimeSpan.FromMilliseconds(30)));
        Assert.InRange(Loudest(squelchMiddle), 0.08 * voice, 0.3 * voice);
        Assert.InRange(Loudest(keyed.Samples.Skip(squelchEnd + At(TimeSpan.FromMilliseconds(20)))), 0, 0.02 * voice);
    }

    private static List<CrewCall> Record(TestRig rig)
    {
        var calls = new List<CrewCall>();
        rig.Orchestrator.CrewCalled += (_, call) => calls.Add(call);
        return calls;
    }

    private sealed class SilentOutput : IAudioOutput
    {
        public void Play(byte[] wav)
        {
        }

        public void Stop()
        {
        }
    }
}
