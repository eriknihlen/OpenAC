using System;

namespace AcDream.Core.Audio;

/// <summary>
/// What one voice is holding when a new sound asks for a place to play. The
/// wave id and the start time are what the per-sound cap needs: which sound
/// this is, and which copy of it has been playing longest.
/// </summary>
public readonly record struct VoiceSlotState(
    bool Occupied,
    bool StillPlaying,
    float Priority,
    uint WaveId = 0,
    long StartedAtMs = 0);

public static class RetailVoicePool
{
    /// <summary>Sentinel for "no slot available — drop the sound".</summary>
    public const int NoSlot = -1;

    /// <summary>
    /// The priority a voice records when no authored priority is in force: one
    /// value for the whole pool, which is what the game itself does. It is also
    /// what makes the second pass below unreachable: a voice is taken from a
    /// still-playing sound only when its recorded priority is
    /// <em>strictly</em> lower than the new one, and nothing is strictly lower
    /// than itself. So a sound that has started always finishes, and the next
    /// one is dropped instead of cutting a playing one short.
    /// </summary>
    public const float VoicePriority = 0f;

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
