using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.Audio;
using AcDream.Core.Audio;

namespace AcDream.App.Tests.Audio;

// OpenAC #42: many sounds at once collapsed to two or three, because a new
// sound could take a voice away from one that was still playing. Its follow-up
// is the other half of the same complaint: with one flat priority and no cap,
// a crowd of walking creatures holds every voice and the player's own spell is
// the sound that goes missing. What a pool does when it is full is now a
// setting, and the shipped behaviour is one of its values.
public sealed class WorldVoicePoolTests
{
    private const uint Footstep = 0x0A000101u;
    private const uint Spell = 0x0A000202u;
    private const float FootstepPriority = 0.10f;
    private const float SpellPriority = 0.90f;

    private static readonly AudioMixerOptions ShippedMixer =
        new() { RetailMixer = true };

    private static readonly AudioMixerOptions SixteenWithPriority = new()
    {
        VoiceCount = 16,
        UseAuthoredPriority = true,
        MaxVoicesPerWave = AudioMixerOptions.NoPerWaveCap,
    };

    private static WorldVoicePool FilledPool(out WorldVoicePool.Voice[] claimed) =>
        FilledPool(ShippedMixer, out claimed);

    private static WorldVoicePool FilledPool(
        AudioMixerOptions mixer,
        out WorldVoicePool.Voice[] claimed)
    {
        var pool = new WorldVoicePool(mixer);
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        claimed = new WorldVoicePool.Voice[pool.Count];
        for (int i = 0; i < pool.Count; i++)
        {
            // A realistic mix: interface clicks land among world sounds.
            WorldVoicePool.Voice? voice = ClaimPlain(
                pool,
                Playing,
                ownerId: (uint)(i + 1),
                isInterface: i % 4 == 0);
            claimed[i] = Assert.IsType<WorldVoicePool.Voice>(voice);
        }

        return pool;
    }

    private static WorldVoicePool FilledWithFootsteps(AudioMixerOptions mixer)
    {
        var pool = new WorldVoicePool(mixer);
        for (int i = 0; i < pool.Count; i++)
        {
            pool[i].SourceId = (uint)(i + 1);
            Assert.NotNull(pool.Claim(
                Playing,
                ownerId: (uint)(i + 1),
                isInterface: false,
                authoredPriority: FootstepPriority,
                waveId: Footstep,
                nowMs: (i + 1) * 100L,
                out _));
        }

        return pool;
    }

    private static bool Playing(uint sourceId) => true;

    private static bool Finished(uint sourceId) => false;

    // The shorthand the tests that predate the settings use: one sound with no
    // id, so the per-sound cap has nothing to count, and no authored priority.
    private static WorldVoicePool.Voice? ClaimPlain(
        WorldVoicePool pool,
        Func<uint, bool> isStillPlaying,
        uint ownerId,
        bool isInterface) =>
        pool.Claim(
            isStillPlaying,
            ownerId,
            isInterface,
            authoredPriority: 0f,
            waveId: 0u,
            nowMs: 0L,
            out _);

    [Fact]
    public void TheShippedMixer_HasSixteenVoices_AndTheDefaultHasThirtyTwo()
    {
        Assert.Equal(16, new WorldVoicePool(ShippedMixer).Count);
        Assert.Equal(32, new WorldVoicePool(AudioMixerOptions.Default).Count);
    }

    [Fact]
    public void SixteenVoices_ClaimInRingOrder()
    {
        WorldVoicePool pool = FilledPool(out WorldVoicePool.Voice[] claimed);

        for (int i = 0; i < pool.Count; i++)
        {
            Assert.Same(pool[i], claimed[i]);
            Assert.True(pool[i].InUse);
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);
        }
    }

    [Fact]
    public void ASeventeenthSound_IsDropped_WithoutTakingAVoiceFromAPlayingOne()
    {
        WorldVoicePool pool = FilledPool(out _);

        Assert.Null(ClaimPlain(pool, Playing, ownerId: 0xDEADu, isInterface: false));

        for (int i = 0; i < pool.Count; i++)
        {
            Assert.True(pool[i].InUse);
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);
        }
    }

    // (d) Thirty-two voices means thirty-two sounds at once; the thirty-third
    // is the first one dropped.
    [Fact]
    public void WithThirtyTwoVoices_ThirtyTwoSoundsPlayBeforeOneIsDropped()
    {
        var pool = new WorldVoicePool(new AudioMixerOptions { VoiceCount = 32 });
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        int played = 0;
        for (int i = 0; i < 64; i++)
        {
            WorldVoicePool.Voice? voice = pool.Claim(
                Playing,
                ownerId: (uint)(i + 1),
                isInterface: false,
                authoredPriority: FootstepPriority,
                waveId: (uint)(0x0A001000u + i),   // all different sounds
                nowMs: (i + 1) * 100L,
                out _);
            if (voice is null)
                break;
            played++;
        }

        Assert.Equal(32, played);
    }

    // (a) Every voice holds a footstep and the player casts a spell: the
    // spell is more important, so it takes one. Another footstep is not, so it
    // is dropped rather than cutting a playing one short.
    [Fact]
    public void AnImportantSound_TakesAVoiceFromALessImportantOne()
    {
        WorldVoicePool pool = FilledWithFootsteps(SixteenWithPriority);

        WorldVoicePool.Voice spell = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(
                Playing,
                ownerId: 0x50000001u,
                isInterface: false,
                authoredPriority: SpellPriority,
                waveId: Spell,
                nowMs: 9_000L,
                out bool tookPlayingVoice));

        Assert.True(tookPlayingVoice);
        Assert.Same(pool[0], spell);
        Assert.Equal(SpellPriority, spell.Priority);
        Assert.Equal(Spell, spell.WaveId);

        Assert.Null(pool.Claim(
            Playing,
            ownerId: 0x50000002u,
            isInterface: false,
            authoredPriority: FootstepPriority,
            waveId: Footstep,
            nowMs: 9_100L,
            out bool tookForFootstep));
        Assert.False(tookForFootstep);
    }

    // (b) With the shipped mixer the same spell is dropped: every voice
    // records the same priority, and nothing is strictly lower than itself.
    [Fact]
    public void WithTheShippedMixer_EvenTheSpellIsDropped()
    {
        WorldVoicePool pool = FilledWithFootsteps(ShippedMixer);

        Assert.Null(pool.Claim(
            Playing,
            ownerId: 0x50000001u,
            isInterface: false,
            authoredPriority: SpellPriority,
            waveId: Spell,
            nowMs: 9_000L,
            out bool tookPlayingVoice));
        Assert.False(tookPlayingVoice);

        for (int i = 0; i < pool.Count; i++)
            Assert.Equal(RetailVoicePool.VoicePriority, pool[i].Priority);
    }

    // (c) One sound may hold only its share. The fifth footstep replaces the
    // footstep that started first, and a different sound still gets a free
    // voice.
    [Fact]
    public void AtItsCap_ASoundReplacesItsOwnOldestCopy()
    {
        var pool = new WorldVoicePool(new AudioMixerOptions
        {
            VoiceCount = 16,
            MaxVoicesPerWave = 4,
        });
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        for (int i = 0; i < 4; i++)
        {
            Assert.NotNull(pool.Claim(
                Playing,
                ownerId: (uint)(i + 1),
                isInterface: false,
                authoredPriority: FootstepPriority,
                waveId: Footstep,
                nowMs: (i + 1) * 100L,
                out _));
        }

        WorldVoicePool.Voice fifth = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(
                Playing,
                ownerId: 0x777u,
                isInterface: false,
                authoredPriority: FootstepPriority,
                waveId: Footstep,
                nowMs: 500L,
                out bool tookPlayingVoice));

        Assert.True(tookPlayingVoice);
        Assert.Same(pool[0], fifth);            // the one that started first
        Assert.Equal(0x777u, fifth.OwnerId);
        Assert.Equal(500L, fifth.SpokeAtMs);

        WorldVoicePool.Voice other = Assert.IsType<WorldVoicePool.Voice>(
            pool.Claim(
                Playing,
                ownerId: 0x888u,
                isInterface: false,
                authoredPriority: SpellPriority,
                waveId: Spell,
                nowMs: 600L,
                out bool tookForOther));

        Assert.False(tookForOther);
        Assert.Same(pool[4], other);            // a free voice, nothing taken
    }

    // There is no reserve of voices kept back for the interface, and no
    // last-resort voice it may take when the pool is full.
    [Fact]
    public void AnInterfaceSound_IsRefusedWhenAllSixteenAreBusy_AndTakesAFreeOneWhenThereIsOne()
    {
        WorldVoicePool pool = FilledPool(out _);

        Assert.Null(ClaimPlain(pool, Playing, ownerId: 0u, isInterface: true));
        for (int i = 0; i < pool.Count; i++)
            Assert.Equal((uint)(i + 1), pool[i].OwnerId);   // nothing was taken

        WorldVoicePool.Vacate(pool[3]);

        WorldVoicePool.Voice taken = Assert.IsType<WorldVoicePool.Voice>(
            ClaimPlain(pool, Playing, ownerId: 0u, isInterface: true));
        Assert.Same(pool[3], taken);
        Assert.True(taken.IsInterface);
    }

    // The cue that plays as you step into a portal is an interface sound, and
    // it starts at the very moment the world it is leaving is taken down.
    [Fact]
    public void AWorldChange_SilencesTheWorldVoicesOnly()
    {
        var pool = new WorldVoicePool(ShippedMixer);
        for (int i = 0; i < pool.Count; i++)
            pool[i].SourceId = (uint)(i + 1);

        WorldVoicePool.Voice world = Assert.IsType<WorldVoicePool.Voice>(
            ClaimPlain(pool, Playing, ownerId: 0x50000001u, isInterface: false));
        WorldVoicePool.Voice cue = Assert.IsType<WorldVoicePool.Voice>(
            ClaimPlain(pool, Playing, ownerId: 0u, isInterface: true));

        WorldVoicePool.Voice[] silenced = pool.SilencedByWorldChange().ToArray();

        Assert.Equal(pool.Count - 1, silenced.Length);
        Assert.Contains(world, silenced);
        Assert.DoesNotContain(cue, silenced);
    }

    [Fact]
    public void WithoutAuthoredPriority_EveryVoiceRecordsTheOnePriority()
    {
        WorldVoicePool pool = FilledPool(out _);

        for (int i = 0; i < pool.Count; i++)
            Assert.Equal(RetailVoicePool.VoicePriority, pool[i].Priority);
    }

    [Fact]
    public void AVoiceWhoseSoundHasFinished_IsClaimedAgain()
    {
        WorldVoicePool pool = FilledPool(out _);

        WorldVoicePool.Voice reclaimed = Assert.IsType<WorldVoicePool.Voice>(
            ClaimPlain(pool, Finished, ownerId: 0xBEEFu, isInterface: false));

        Assert.Equal(0xBEEFu, reclaimed.OwnerId);
        Assert.True(reclaimed.InUse);
        Assert.False(reclaimed.IsInterface);
    }

    [Fact]
    public void AVacatedVoice_IsFreeAgain()
    {
        WorldVoicePool pool = FilledPool(out WorldVoicePool.Voice[] claimed);

        WorldVoicePool.Vacate(claimed[5]);

        Assert.False(claimed[5].InUse);
        Assert.False(claimed[5].IsInterface);
        Assert.Equal(0u, claimed[5].OwnerId);
        Assert.Equal(0u, claimed[5].WaveId);
        Assert.Same(
            claimed[5],
            ClaimPlain(pool, Playing, ownerId: 0x1234u, isInterface: false));
    }

    // Changing the setting while the client runs: the voices the pool no
    // longer has room for are handed back, and the ones it gains ask for a
    // source of their own.
    [Fact]
    public void ShrinkingThePool_HandsBackEveryVoiceBeyondTheNewCount()
    {
        WorldVoicePool pool = FilledPool(
            new AudioMixerOptions { VoiceCount = 32 },
            out WorldVoicePool.Voice[] claimed);
        var retired = new List<uint>();

        pool.ApplyOptions(
            ShippedMixer,
            voice => retired.Add(voice.SourceId),
            () => throw new InvalidOperationException("nothing to create"));

        Assert.Equal(16, pool.Count);
        Assert.Equal(
            Enumerable.Range(17, 16).Reverse().Select(value => (uint)value),
            retired);
        Assert.Same(claimed[0], pool[0]);
    }

    [Fact]
    public void GrowingThePool_AsksForOneSourcePerVoiceItGains()
    {
        var pool = new WorldVoicePool(ShippedMixer);
        uint next = 100u;

        pool.ApplyOptions(
            new AudioMixerOptions { VoiceCount = 24 },
            voice => throw new InvalidOperationException("nothing to retire"),
            () => next++);

        Assert.Equal(24, pool.Count);
        Assert.Equal(108u, next);
        for (int i = 16; i < pool.Count; i++)
            Assert.Equal((uint)(100 + i - 16), pool[i].SourceId);
    }

    [Fact]
    public void NewSettings_TakeEffectOnTheNextClaim()
    {
        WorldVoicePool pool = FilledWithFootsteps(SixteenWithPriority);

        pool.ApplyOptions(ShippedMixer, _ => { }, () => 0u);

        Assert.Null(pool.Claim(
            Playing,
            ownerId: 0x50000001u,
            isInterface: false,
            authoredPriority: SpellPriority,
            waveId: Spell,
            nowMs: 9_000L,
            out _));
    }
}
