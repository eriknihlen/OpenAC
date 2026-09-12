using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

// OpenAC #42 follow-up: with one flat priority and no cap, a crowd of walking
// creatures fills every voice — each of them fires a footstep about twelve
// times a second — and the sound the player most wants to hear, their own
// spell, is the one that gets dropped. Two things stop that: the priority the
// sound data already authors, and a cap on how many voices one sound may hold.
public sealed class VoiceAllocationTests
{
    private const uint Footstep = 0x0A000101u;
    private const uint Spell = 0x0A000202u;
    private const float FootstepPriority = 0.10f;
    private const float SpellPriority = 0.90f;

    private static VoiceSlotState Free() => new(Occupied: false, StillPlaying: false, Priority: 0f);

    private static VoiceSlotState Playing(
        float priority,
        uint waveId = Footstep,
        long startedAtMs = 0L) =>
        new(Occupied: true, StillPlaying: true, Priority: priority,
            WaveId: waveId, StartedAtMs: startedAtMs);

    private static VoiceSlotState Finished(
        float priority,
        uint waveId = Footstep) =>
        new(Occupied: true, StillPlaying: false, Priority: priority, WaveId: waveId);

    private static VoiceSlotState[] AllPlaying(
        float priority,
        int count = 16,
        uint waveId = Footstep)
    {
        var slots = new VoiceSlotState[count];
        for (int i = 0; i < count; i++)
            slots[i] = Playing(priority, waveId, startedAtMs: (i + 1) * 100L);
        return slots;
    }

    // (a) Every voice is holding a footstep and the player casts a spell. The
    // spell is the more important sound, so it takes one.
    [Fact]
    public void WithEveryVoiceHoldingAFootstep_TheSpellTakesOne()
    {
        VoiceSlotState[] slots = AllPlaying(FootstepPriority);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: SpellPriority,
            waveId: Spell,
            maxVoicesPerWave: AudioMixerOptions.NoPerWaveCap);

        Assert.True(result.Found);
        Assert.Equal(0, result.Slot);
        Assert.True(result.TookPlayingVoice);
    }

    // (a) The same pool, and another footstep asks. Equally important is not
    // more important: it waits its turn rather than cutting one short.
    [Fact]
    public void WithEveryVoiceHoldingAFootstep_AnotherFootstepIsDropped()
    {
        VoiceSlotState[] slots = AllPlaying(FootstepPriority);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: FootstepPriority,
            waveId: Footstep,
            maxVoicesPerWave: AudioMixerOptions.NoPerWaveCap);

        Assert.False(result.Found);
        Assert.Equal(RetailVoicePool.NoSlot, result.Slot);
        Assert.False(result.TookPlayingVoice);
    }

    // (b) With authored priority switched off every voice records the same
    // value, so the spell is dropped as well — the mixer the game shipped with.
    [Fact]
    public void WithoutAuthoredPriority_EvenTheSpellIsDropped()
    {
        VoiceSlotState[] slots = AllPlaying(RetailVoicePool.VoicePriority);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: RetailVoicePool.VoicePriority,
            waveId: Spell,
            maxVoicesPerWave: AudioMixerOptions.NoPerWaveCap);

        Assert.False(result.Found);
    }

    // (c) Four footsteps are already playing and the cap is four. The fifth
    // replaces the one that started first, and does not touch anything else.
    [Fact]
    public void AtTheCap_TheNextCopyReplacesTheOldestCopyOfItself()
    {
        var slots = new VoiceSlotState[16];
        slots[0] = Playing(FootstepPriority, Footstep, startedAtMs: 400L);
        slots[1] = Playing(SpellPriority, Spell, startedAtMs: 50L);
        slots[2] = Playing(FootstepPriority, Footstep, startedAtMs: 100L);
        slots[3] = Playing(FootstepPriority, Footstep, startedAtMs: 300L);
        slots[4] = Playing(FootstepPriority, Footstep, startedAtMs: 200L);
        for (int i = 5; i < slots.Length; i++)
            slots[i] = Playing(FootstepPriority, waveId: 0x0A000999u, startedAtMs: 10L);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: FootstepPriority,
            waveId: Footstep,
            maxVoicesPerWave: 4);

        Assert.Equal(2, result.Slot);           // startedAtMs 100 is the oldest
        Assert.True(result.TookPlayingVoice);
    }

    // (c) The cap is per sound, not per pool: a different sound still gets a
    // free voice while one sound is at its limit.
    [Fact]
    public void AtTheCapForOneSound_ADifferentSoundStillTakesAFreeVoice()
    {
        var slots = new VoiceSlotState[16];
        for (int i = 0; i < 4; i++)
            slots[i] = Playing(FootstepPriority, Footstep, startedAtMs: (i + 1) * 100L);
        for (int i = 4; i < slots.Length; i++)
            slots[i] = Free();

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: SpellPriority,
            waveId: Spell,
            maxVoicesPerWave: 4);

        Assert.Equal(4, result.Slot);
        Assert.False(result.TookPlayingVoice);
    }

    // Below the cap nothing is taken: the copy plays alongside the others.
    [Fact]
    public void BelowTheCap_TheCopyTakesAFreeVoice()
    {
        var slots = new VoiceSlotState[16];
        for (int i = 0; i < 3; i++)
            slots[i] = Playing(FootstepPriority, Footstep, startedAtMs: (i + 1) * 100L);
        for (int i = 3; i < slots.Length; i++)
            slots[i] = Free();

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: FootstepPriority,
            waveId: Footstep,
            maxVoicesPerWave: 4);

        Assert.Equal(3, result.Slot);
        Assert.False(result.TookPlayingVoice);
    }

    // A copy whose sound has finished is not one of the copies you can hear,
    // so it does not count towards the cap — and it is free to be reused.
    [Fact]
    public void AFinishedCopy_DoesNotCountTowardsTheCap()
    {
        var slots = new VoiceSlotState[16];
        slots[0] = Playing(FootstepPriority, Footstep, startedAtMs: 100L);
        slots[1] = Playing(FootstepPriority, Footstep, startedAtMs: 200L);
        slots[2] = Playing(FootstepPriority, Footstep, startedAtMs: 300L);
        slots[3] = Finished(FootstepPriority, Footstep);
        for (int i = 4; i < slots.Length; i++)
            slots[i] = Playing(SpellPriority, Spell, startedAtMs: 10L);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: FootstepPriority,
            waveId: Footstep,
            maxVoicesPerWave: 4);

        Assert.Equal(3, result.Slot);
        Assert.False(result.TookPlayingVoice);
    }

    // A sound with no id cannot be counted, so the cap has nothing to say
    // about it and the ordinary rules decide.
    [Fact]
    public void ASoundWithNoId_IsNotCapped()
    {
        VoiceSlotState[] slots = AllPlaying(RetailVoicePool.VoicePriority, waveId: 0u);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: RetailVoicePool.VoicePriority,
            waveId: 0u,
            maxVoicesPerWave: 1);

        Assert.False(result.Found);
    }

    // A free voice is always better than taking one, whatever the cap says.
    [Fact]
    public void AFreeVoice_IsPreferredToTakingOne()
    {
        var slots = new VoiceSlotState[4];
        slots[0] = Playing(SpellPriority, Spell, startedAtMs: 100L);
        slots[1] = Free();
        slots[2] = Playing(FootstepPriority, Footstep, startedAtMs: 200L);
        slots[3] = Finished(FootstepPriority);

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: SpellPriority,
            waveId: Spell,
            maxVoicesPerWave: AudioMixerOptions.NoPerWaveCap);

        Assert.Equal(1, result.Slot);
        Assert.False(result.TookPlayingVoice);
    }

    [Fact]
    public void AnEmptyPool_HasNothingToGive()
    {
        VoiceClaimResult result = VoiceAllocation.Acquire(
            [],
            cursor: 0,
            priority: SpellPriority,
            waveId: Spell,
            maxVoicesPerWave: 4);

        Assert.False(result.Found);
    }

    // (d) Thirty-two voices really are thirty-two: the pool is full only when
    // all of them are.
    [Fact]
    public void WithThirtyTwoVoices_TheThirtyTwoSlotsAreAllUsable()
    {
        VoiceSlotState[] slots = AllPlaying(FootstepPriority, count: 32);
        slots[31] = Free();

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            cursor: 0,
            priority: FootstepPriority,
            waveId: Footstep,
            maxVoicesPerWave: AudioMixerOptions.NoPerWaveCap);

        Assert.Equal(31, result.Slot);
    }
}
