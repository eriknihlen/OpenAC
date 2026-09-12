using System;
using System.Collections.Generic;
using AcDream.Core.Audio;

namespace AcDream.App.Audio;

/// <summary>
/// The one pool of playing voices, shared by everything that makes a noise:
/// sounds in the world, the ambient beds, and the interface clicks alike. There
/// is no second pool for the interface.
/// <para>
/// How many voices there are, and what happens when they are all busy, comes
/// from <see cref="AudioMixerOptions"/>. With the shipped settings there are
/// sixteen and a sound that finds them all busy is dropped; with acdream's
/// defaults there are more of them, an important sound may take a voice from a
/// less important one, and no single sound may hold more than its share.
/// </para>
/// </summary>
internal sealed class WorldVoicePool
{
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
        /// Whether this voice is carrying an interface sound rather than a sound
        /// in the world. Interface sounds share the pool but do not belong to the
        /// world, so a world change does not stop them: the cue that plays as you
        /// step into a portal has to outlive the world it is leaving.
        /// </summary>
        public bool IsInterface;

        /// <summary>
        /// How important the sound on this voice is, as the sound data authored
        /// it — or one flat value for every voice when authored priority is
        /// switched off, which is what makes a playing sound untouchable.
        /// </summary>
        public float Priority;

        /// <summary>Which sound this voice was given.</summary>
        public uint WaveId;

        /// <summary>When this voice was given that sound.</summary>
        public long SpokeAtMs;
    }

    private readonly List<Voice> _voices = [];
    private AudioMixerOptions _options;
    private int _cursor;

    public WorldVoicePool(AudioMixerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Normalized();
        for (int i = 0; i < _options.EffectiveVoiceCount; i++)
            _voices.Add(new Voice());
    }

    /// <summary>The settings the pool is running with.</summary>
    public AudioMixerOptions Options => _options;

    public int Count => _voices.Count;

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
    /// <param name="isInterface">
    /// True for an interface sound, which a world change leaves alone.
    /// </param>
    /// <param name="authoredPriority">
    /// How important the new sound is, as its data authors it. Ignored, along
    /// with every recorded priority, when authored priority is switched off.
    /// </param>
    /// <param name="waveId">The sound being played, for the per-sound cap.</param>
    /// <param name="nowMs">Now, so the cap can find the oldest copy.</param>
    /// <param name="tookPlayingVoice">
    /// True when the claimed voice was taken from a sound that was still
    /// playing, so the caller knows a sound was cut short.
    /// </param>
    public Voice? Claim(
        Func<uint, bool> isStillPlaying,
        uint ownerId,
        bool isInterface,
        float authoredPriority,
        uint waveId,
        long nowMs,
        out bool tookPlayingVoice)
    {
        ArgumentNullException.ThrowIfNull(isStillPlaying);

        tookPlayingVoice = false;
        if (_voices.Count == 0)
            return null;

        float recorded = _options.EffectiveUseAuthoredPriority
            ? authoredPriority
            : RetailVoicePool.VoicePriority;

        Span<VoiceSlotState> slots = stackalloc VoiceSlotState[_voices.Count];
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice voice = _voices[i];
            slots[i] = new VoiceSlotState(
                Occupied: voice.InUse,
                StillPlaying: voice.InUse && isStillPlaying(voice.SourceId),
                Priority: voice.Priority,
                WaveId: voice.WaveId,
                StartedAtMs: voice.SpokeAtMs);
        }

        VoiceClaimResult result = VoiceAllocation.Acquire(
            slots,
            _cursor,
            recorded,
            waveId,
            _options.EffectiveMaxVoicesPerWave);
        if (!result.Found)
            return null;

        Voice claimed = _voices[result.Slot];
        claimed.InUse = true;
        claimed.OwnerId = ownerId;
        claimed.IsInterface = isInterface;
        claimed.Priority = recorded;
        claimed.WaveId = waveId;
        claimed.SpokeAtMs = nowMs;
        _cursor = RetailVoicePool.AdvanceCursor(result.Slot, _voices.Count);
        tookPlayingVoice = result.TookPlayingVoice;
        return claimed;
    }

    /// <summary>
    /// Put new settings in force. Voices the pool no longer has room for are
    /// handed to <paramref name="retire"/> (the caller silences and releases
    /// their sources); voices it has gained ask
    /// <paramref name="createSource"/> for one.
    /// </summary>
    public void ApplyOptions(
        AudioMixerOptions options,
        Action<Voice> retire,
        Func<uint> createSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retire);
        ArgumentNullException.ThrowIfNull(createSource);

        _options = options.Normalized();
        int target = _options.EffectiveVoiceCount;

        while (_voices.Count > target)
        {
            Voice leaving = _voices[^1];
            _voices.RemoveAt(_voices.Count - 1);
            retire(leaving);
        }

        while (_voices.Count < target)
            _voices.Add(new Voice { SourceId = createSource() });

        if (_cursor >= _voices.Count)
            _cursor = 0;
    }

    /// <summary>
    /// The voices a world change takes down with it: every one that is not
    /// carrying an interface sound. Interface cues keep playing across the
    /// change — one of them is the sound of making it happen.
    /// </summary>
    public IEnumerable<Voice> SilencedByWorldChange()
    {
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice voice = _voices[i];
            if (!voice.IsInterface)
                yield return voice;
        }
    }

    /// <summary>Give a voice back to the pool. The caller silences the source.</summary>
    public static void Vacate(Voice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        voice.OwnerId = 0;
        voice.IsInterface = false;
        voice.Priority = RetailVoicePool.VoicePriority;
        voice.WaveId = 0;
        voice.SpokeAtMs = 0;
        voice.InUse = false;
    }
}
