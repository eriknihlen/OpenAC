using System;
using System.Linq;
using AcDream.Core.Audio;
using Xunit;

namespace AcDream.Core.Tests.Audio;

public sealed class RetailVoicePoolTests
{
    private static VoiceSlotState Free() => new(Occupied: false, StillPlaying: false, Priority: 0f);

    private static VoiceSlotState Finished(float priority) =>
        new(Occupied: true, StillPlaying: false, Priority: priority);

    private static VoiceSlotState Busy(float priority) =>
        new(Occupied: true, StillPlaying: true, Priority: priority);

    private static VoiceSlotState[] AllBusy(float priority, int count = 16)
    {
        var slots = new VoiceSlotState[count];
        Array.Fill(slots, Busy(priority));
        return slots;
    }

    [Fact]
    public void EmptyPool_DropsTheSound()
    {
        Assert.Equal(RetailVoicePool.NoSlot, RetailVoicePool.Acquire(Array.Empty<VoiceSlotState>(), 0, 1f));
    }

    [Fact]
    public void FirstPass_PrefersAFreeSlot_ScanningFromTheCursor()
    {
        var slots = AllBusy(1f);
        slots[9] = Free();
        Assert.Equal(9, RetailVoicePool.Acquire(slots, cursor: 0, priority: 0f));
    }

    [Fact]
    public void FirstPass_ReclaimsAFinishedVoice_EvenAtHigherPriority()
    {
        var slots = AllBusy(1f);
        slots[4] = Finished(1f);
        Assert.Equal(4, RetailVoicePool.Acquire(slots, cursor: 0, priority: 0.1f));
    }

    [Fact]
    public void FirstPass_WrapsAroundTheRing()
    {
        var slots = AllBusy(1f);
        slots[2] = Free();
        // Starting at 5, the scan must wrap past 15 to reach slot 2.
        Assert.Equal(2, RetailVoicePool.Acquire(slots, cursor: 5, priority: 0f));
    }

    [Fact]
    public void FirstPass_TakesTheNearestFreeSlotInRingOrder()
    {
        var slots = AllBusy(1f);
        slots[1] = Free();
        slots[12] = Free();
        Assert.Equal(12, RetailVoicePool.Acquire(slots, cursor: 10, priority: 0f));
    }

    [Fact]
    public void SecondPass_EvictsStrictlyLowerPriority()
    {
        var slots = AllBusy(0.5f);
        slots[7] = Busy(0.2f);
        Assert.Equal(7, RetailVoicePool.Acquire(slots, cursor: 0, priority: 0.3f));
    }

    [Fact]
    public void SecondPass_EqualPriorityNeverEvicts()
    {
        var slots = AllBusy(0.5f);
        Assert.Equal(RetailVoicePool.NoSlot, RetailVoicePool.Acquire(slots, cursor: 0, priority: 0.5f));
    }

    [Fact]
    public void SecondPass_HigherPriorityPoolDropsTheNewSound()
    {
        var slots = AllBusy(0.9f);
        Assert.Equal(RetailVoicePool.NoSlot, RetailVoicePool.Acquire(slots, cursor: 0, priority: 0.4f));
    }

    [Fact]
    public void SecondPass_TakesTheFirstLowerSlotInRingOrder_NotTheLowest()
    {
        var slots = AllBusy(0.9f);
        slots[3] = Busy(0.1f);
        slots[6] = Busy(0.5f);
        Assert.Equal(6, RetailVoicePool.Acquire(slots, cursor: 6, priority: 0.6f));
    }

    [Fact]
    public void Eviction_IgnoresGain_ByConstruction()
    {
        var slots = AllBusy(0.8f);
        Assert.Equal(
            RetailVoicePool.NoSlot,
            RetailVoicePool.Acquire(slots, cursor: 0, priority: 0.8f));
        Assert.DoesNotContain(
            "Gain",
            string.Join(",", typeof(VoiceSlotState).GetProperties().Select(p => p.Name)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(15, 0)]
    [InlineData(9, 10)]
    public void Cursor_AdvancesPastTheClaimedSlot_AndWraps(int claimed, int expected)
    {
        Assert.Equal(expected, RetailVoicePool.AdvanceCursor(claimed, 16));
    }

    [Fact]
    public void RingOrder_IsStableAcrossRepeatedClaims()
    {
        var slots = new VoiceSlotState[4];
        Array.Fill(slots, Free());

        int cursor = 0;
        var claimed = new int[4];
        for (int i = 0; i < 4; i++)
        {
            claimed[i] = RetailVoicePool.Acquire(slots, cursor, 1f);
            cursor = RetailVoicePool.AdvanceCursor(claimed[i], slots.Length);
        }

        Assert.Equal(new[] { 0, 1, 2, 3 }, claimed);
    }
}
