using System;
using DatReaderWriter.Types;

namespace AcDream.Core.Audio;

/// <summary>
/// The authored "tweaked" sound hook — a sound plus its own dice roll and its
/// own volume.
/// </summary>
public static class TweakedSoundHooks
{
    /// <summary>
    /// The hook's play probability: the chance, in [0,1], that this hook makes
    /// a sound at all.
    /// <para>
    /// It is the first float of the authored record. The dat reader surfaces
    /// that float under the name <c>Priority</c> and the second one under
    /// <c>Probability</c>, which is the wrong way round for what the two fields
    /// mean — the game reads the first as the probability and the second as a
    /// priority it then never looks at. Everything that needs the probability
    /// goes through here so the swap is stated once instead of being rediscovered
    /// at every call site.
    /// </para>
    /// </summary>
    public static float PlayProbability(SoundTweakedHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return hook.Priority;
    }

    /// <summary>
    /// The hook's authored priority: how important this sound is, in [0,1],
    /// when the mixer has to choose which voice a new sound takes.
    /// <para>
    /// It is the <em>second</em> float of the authored record, which the dat
    /// reader surfaces under the name <c>Probability</c> — the mirror image of
    /// <see cref="PlayProbability"/> and the same swap stated once here rather
    /// than at every call site.
    /// </para>
    /// </summary>
    public static float AuthoredPriority(SoundTweakedHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return hook.Probability;
    }

    /// <summary>
    /// Roll the hook's probability and, when it wins, report the sound to play
    /// and the volume to play it at.
    /// </summary>
    /// <remarks>
    /// A hook that loses the roll is silent — it is not played quietly and it is
    /// not retried. The authored volume is passed through exactly as written,
    /// including zero: a hook authored at zero volume is an inaudible hook, not
    /// a full-volume one.
    /// </remarks>
    public static bool TryRoll(
        SoundTweakedHook hook,
        ISoundRandom rng,
        out uint waveId,
        out float volume)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(rng);

        waveId = (uint)hook.SoundId;
        volume = hook.Volume;
        return SoundCookbook.PlayProbability(PlayProbability(hook), rng);
    }
}
