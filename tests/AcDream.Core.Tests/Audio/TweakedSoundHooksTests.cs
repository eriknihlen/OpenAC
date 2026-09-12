using System;
using System.Collections.Generic;
using AcDream.Core.Audio;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Audio;

// OpenAC #42: a tweaked sound hook is authored with its own odds of making a
// sound at all. Playing one every time it comes round is why thunder sounded
// constant.
public sealed class TweakedSoundHooksTests
{
    private sealed class ScriptedRandom(params float[] probability) : ISoundRandom
    {
        private readonly Queue<float> _probability = new(probability);

        public float NextVariantRoll() => throw new InvalidOperationException(
            "A tweaked hook names its own sound; there is no variant to pick.");

        public float NextProbabilityRoll() => _probability.Dequeue();
    }

    // The two floats after the sound id, in the order they are authored.
    private static SoundTweakedHook Hook(
        uint waveId,
        float firstFloat,
        float secondFloat,
        float volume) =>
        new()
        {
            SoundId = waveId,
            Priority = firstFloat,
            Probability = secondFloat,
            Volume = volume,
        };

    [Fact]
    public void AHookThatLosesItsRoll_MakesNoSound()
    {
        SoundTweakedHook hook = Hook(0x0A00038Bu, 0.1f, 0.9f, volume: 1f);

        Assert.False(TweakedSoundHooks.TryRoll(
            hook, new ScriptedRandom(0.5f), out _, out _));
    }

    [Fact]
    public void AHookThatWinsItsRoll_PlaysItsOwnSound()
    {
        SoundTweakedHook hook = Hook(0x0A00038Bu, 0.9f, 0.1f, volume: 0.75f);

        Assert.True(TweakedSoundHooks.TryRoll(
            hook, new ScriptedRandom(0.5f), out uint waveId, out float volume));
        Assert.Equal(0x0A00038Bu, waveId);
        Assert.Equal(0.75f, volume);
    }

    // The dat reader's two float names are the wrong way round for what the
    // fields mean: the first authored float — the one it calls Priority — is
    // the play probability, and the one it calls Probability is never read.
    // Swapping the two would make every one of these hooks play on the wrong
    // odds, so the mapping is pinned here rather than trusted.
    [Fact]
    public void ThePlayProbability_IsTheFirstAuthoredFloat()
    {
        SoundTweakedHook likely = Hook(0x0A000001u, 0.9f, 0.1f, volume: 1f);
        SoundTweakedHook unlikely = Hook(0x0A000001u, 0.1f, 0.9f, volume: 1f);

        Assert.Equal(0.9f, TweakedSoundHooks.PlayProbability(likely));
        Assert.Equal(0.1f, TweakedSoundHooks.PlayProbability(unlikely));

        Assert.True(TweakedSoundHooks.TryRoll(likely, new ScriptedRandom(0.5f), out _, out _));
        Assert.False(TweakedSoundHooks.TryRoll(unlikely, new ScriptedRandom(0.5f), out _, out _));
    }

    [Fact]
    public void AlwaysAndNever_AreTheEndsOfTheRange()
    {
        Assert.True(TweakedSoundHooks.TryRoll(
            Hook(0x0A000001u, 1f, 0f, volume: 1f), new ScriptedRandom(0.999f), out _, out _));
        Assert.False(TweakedSoundHooks.TryRoll(
            Hook(0x0A000001u, 0f, 1f, volume: 1f), new ScriptedRandom(0f), out _, out _));
    }

    // A hook authored at zero volume is an inaudible hook. It used to be played
    // at full volume instead, because a zero was read as "unset".
    [Fact]
    public void AZeroVolumeHook_PlaysAtZero_NotAtFull()
    {
        SoundTweakedHook hook = Hook(0x0A000001u, 1f, 0f, volume: 0f);

        Assert.True(TweakedSoundHooks.TryRoll(
            hook, new ScriptedRandom(0.5f), out _, out float volume));
        Assert.Equal(0f, volume);
    }

    [Fact]
    public void TheAuthoredVolume_IsPassedThroughUntouched()
    {
        SoundTweakedHook loud = Hook(0x0A000001u, 1f, 0f, volume: 4.5f);

        Assert.True(TweakedSoundHooks.TryRoll(
            loud, new ScriptedRandom(0.5f), out _, out float volume));
        Assert.Equal(4.5f, volume);
    }
}
