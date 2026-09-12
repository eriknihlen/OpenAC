using System;
using AcDream.Core.Audio;

namespace AcDream.App.Audio;

/// <summary>
/// The one pool of playing voices. Sixteen of them, shared by everything that
/// makes a noise: sounds in the world, the ambient beds, and the interface
/// clicks alike. There is no second pool for the interface, and a sound that
/// finds every voice busy is dropped rather than cutting a playing one short.
/// </summary>
internal sealed class WorldVoicePool
{
    /// <summary>How many sounds can be audible at once.</summary>
    internal const int VoiceCount = 16;

    internal sealed class Voice
    {
        /// <summary>The backend source this voice speaks through.</summary>
        public uint SourceId;

        /// <summary>
        /// The entity whose sound this is, or 0 for a sound that belongs to no
        /// entity (an ambient, an interface click).
        /// </summary>
        public uint OwnerId;

        public bool InUse;

        /// <summary>
        /// The priority recorded when the voice was claimed. It is always
        /// <see cref="RetailVoicePool.VoicePriority"/>; the field exists because
        /// the allocator reads it, not because it ever varies.
        /// </summary>
        public float Priority;
    }

    private readonly Voice[] _voices = CreateVoices();
    private int _cursor;

    public int Count => VoiceCount;

    public Voice this[int index] => _voices[index];

    /// <summary>
    /// Claim a voice for a new sound, or return null when there is none to
    /// claim and the sound is therefore dropped.
    /// </summary>
    /// <param name="isStillPlaying">
    /// Asks the backend whether a source is still making noise; a voice whose
    /// sound has finished is free again even though it was never released.
    /// </param>
    /// <param name="ownerId">The entity the new sound belongs to, or 0.</param>
    public Voice? Claim(Func<uint, bool> isStillPlaying, uint ownerId)
    {
        ArgumentNullException.ThrowIfNull(isStillPlaying);

        Span<VoiceSlotState> slots = stackalloc VoiceSlotState[VoiceCount];
        for (int i = 0; i < VoiceCount; i++)
        {
            Voice voice = _voices[i];
            slots[i] = new VoiceSlotState(
                Occupied: voice.InUse,
                StillPlaying: voice.InUse && isStillPlaying(voice.SourceId),
                Priority: voice.Priority);
        }

        int index = RetailVoicePool.Acquire(
            slots,
            _cursor,
            RetailVoicePool.VoicePriority);
        if (index < 0)
            return null;

        Voice claimed = _voices[index];
        claimed.InUse = true;
        claimed.OwnerId = ownerId;
        claimed.Priority = RetailVoicePool.VoicePriority;
        _cursor = RetailVoicePool.AdvanceCursor(index, VoiceCount);
        return claimed;
    }

    /// <summary>Give a voice back to the pool. The caller silences the source.</summary>
    public static void Vacate(Voice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        voice.OwnerId = 0;
        voice.Priority = RetailVoicePool.VoicePriority;
        voice.InUse = false;
    }

    private static Voice[] CreateVoices()
    {
        var voices = new Voice[VoiceCount];
        for (int i = 0; i < voices.Length; i++)
            voices[i] = new Voice();
        return voices;
    }
}
