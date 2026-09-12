using System;

namespace AcDream.Core.Audio;

/// <summary>
/// Which voice a new sound was given, and whether it was taken from a voice
/// that was still making noise.
/// </summary>
public readonly record struct VoiceClaimResult(int Slot, bool TookPlayingVoice)
{
    public static VoiceClaimResult None { get; } = new(RetailVoicePool.NoSlot, false);

    public bool Found => Slot >= 0;
}

/// <summary>
/// Picks the voice a new sound gets. Three questions in order, and the first
/// one that answers wins:
/// <list type="number">
/// <item>Is this same sound already holding as many voices as it may? Then it
/// takes the oldest of its own rather than a voice belonging to anything else.
/// </item>
/// <item>Is a voice free, or has its sound finished? Take that one.</item>
/// <item>Is a voice carrying something less important than this? Take that one.
/// </item>
/// </list>
/// With the per-sound cap off and every voice recording the same priority, only
/// question two can answer and a sound that finds every voice busy is dropped —
/// which is the mixer the game shipped with.
/// </summary>
public static class VoiceAllocation
{
    public static VoiceClaimResult Acquire(
        ReadOnlySpan<VoiceSlotState> slots,
        int cursor,
        float priority,
        uint waveId,
        int maxVoicesPerWave)
    {
        if (slots.Length == 0)
            return VoiceClaimResult.None;

        int cappedSlot = TryTakeOldestOfOneSound(
            slots, cursor, waveId, maxVoicesPerWave);
        if (cappedSlot >= 0)
            return new VoiceClaimResult(cappedSlot, true);

        int slot = RetailVoicePool.Acquire(slots, cursor, priority);
        if (slot < 0)
            return VoiceClaimResult.None;

        VoiceSlotState claimed = slots[slot];
        return new VoiceClaimResult(
            slot,
            claimed.Occupied && claimed.StillPlaying);
    }

    /// <summary>
    /// When one sound already holds its whole allowance, the next copy of it
    /// replaces the copy that has been playing longest. That keeps a crowd of
    /// footsteps audible — you hear the same number of them, just not twenty at
    /// once — and leaves the rest of the pool for everything else.
    /// </summary>
    private static int TryTakeOldestOfOneSound(
        ReadOnlySpan<VoiceSlotState> slots,
        int cursor,
        uint waveId,
        int maxVoicesPerWave)
    {
        if (maxVoicesPerWave <= 0 || waveId == 0)
            return -1;

        int holding = 0;
        int oldest = -1;
        long oldestStartedAtMs = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            int index = Ring(cursor, i, slots.Length);
            VoiceSlotState slot = slots[index];
            if (!slot.Occupied || !slot.StillPlaying || slot.WaveId != waveId)
                continue;

            holding++;
            if (oldest < 0 || slot.StartedAtMs < oldestStartedAtMs)
            {
                oldest = index;
                oldestStartedAtMs = slot.StartedAtMs;
            }
        }

        return holding >= maxVoicesPerWave ? oldest : -1;
    }

    private static int Ring(int cursor, int offset, int slotCount)
    {
        int index = (cursor + offset) % slotCount;
        return index < 0 ? index + slotCount : index;
    }
}
