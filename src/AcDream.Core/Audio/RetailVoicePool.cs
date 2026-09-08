using System;

namespace AcDream.Core.Audio;

public readonly record struct VoiceSlotState(bool Occupied, bool StillPlaying, float Priority);

public static class RetailVoicePool
{
    /// <summary>Sentinel for "no slot available — drop the sound".</summary>
    public const int NoSlot = -1;

    public static int Acquire(ReadOnlySpan<VoiceSlotState> slots, int cursor, float priority)
    {
        if (slots.Length == 0) return NoSlot;

        for (int i = 0; i < slots.Length; i++)
        {
            int idx = Ring(cursor, i, slots.Length);
            VoiceSlotState slot = slots[idx];
            if (!slot.Occupied || !slot.StillPlaying) return idx;
        }

        for (int i = 0; i < slots.Length; i++)
        {
            int idx = Ring(cursor, i, slots.Length);
            if (slots[idx].Priority < priority) return idx;
        }

        return NoSlot;
    }

    public static int AdvanceCursor(int claimedSlot, int slotCount) =>
        slotCount <= 0 ? 0 : (claimedSlot + 1) % slotCount;

    private static int Ring(int cursor, int offset, int slotCount)
    {
        int idx = (cursor + offset) % slotCount;
        return idx < 0 ? idx + slotCount : idx;
    }
}
