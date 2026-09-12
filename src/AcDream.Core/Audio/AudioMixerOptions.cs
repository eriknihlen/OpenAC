using System;

namespace AcDream.Core.Audio;

/// <summary>
/// How many sounds the mixer can hold at once, and what it does when they are
/// all held. These are client settings, not game options: they are persisted
/// with the other acdream-only settings and have no place on the wire.
/// </summary>
/// <remarks>
/// The shipped client mixes through sixteen voices, records no priority against
/// any of them, and therefore drops every new sound while all sixteen are busy.
/// A crowd of creatures walking nearby is enough to fill them — footsteps are
/// the most frequent sound in the game — and the sound the player most wants to
/// hear, their own spell, is the one that goes missing. The defaults here fix
/// that: more voices, the authored priority the sound data already carries, and
/// a cap on how many voices one sound may hold. Turning
/// <see cref="RetailMixer"/> on restores the shipped behaviour exactly.
/// </remarks>
public sealed record AudioMixerOptions
{
    /// <summary>The fewest voices the mixer may be given.</summary>
    public const int MinimumVoiceCount = 16;

    /// <summary>The most voices the mixer may be given.</summary>
    public const int MaximumVoiceCount = 64;

    /// <summary>The voice count <see cref="RetailMixer"/> pins the pool to.</summary>
    public const int RetailMixerVoiceCount = 16;

    /// <summary>The value of <see cref="MaxVoicesPerWave"/> that means "no cap".</summary>
    public const int NoPerWaveCap = 0;

    /// <summary>
    /// The most voices one sound may hold. The cap exists to stop a crowd of
    /// one sound from holding the whole pool, and past a handful of copies it
    /// has stopped doing that, so this is the ceiling everywhere: the command,
    /// <see cref="Normalized"/>, and the settings row all clamp to it, and
    /// none of them can therefore store a value another would not show.
    /// </summary>
    public const int MaximumMaxVoicesPerWave = 8;

    public const int DefaultVoiceCount = 32;

    public const int DefaultMaxVoicesPerWave = 4;

    public static AudioMixerOptions Default { get; } = new();

    /// <summary>
    /// Mix exactly the way the shipped client does: sixteen voices, no authored
    /// priority, no per-sound cap. It overrides the three settings below rather
    /// than rewriting them, so turning it off again restores them.
    /// </summary>
    public bool RetailMixer { get; init; }

    /// <summary>How many sounds may be audible at once.</summary>
    public int VoiceCount { get; init; } = DefaultVoiceCount;

    /// <summary>
    /// Whether the priority the sound data authors is recorded against a voice,
    /// which lets an important sound take a voice from a less important one that
    /// is still playing. With it off every voice records the same priority and
    /// nothing is ever taken.
    /// </summary>
    public bool UseAuthoredPriority { get; init; } = true;

    /// <summary>
    /// How many voices one sound may hold at once, from
    /// <see cref="NoPerWaveCap"/> (no limit) to
    /// <see cref="MaximumMaxVoicesPerWave"/>. This is what stops a crowd of
    /// footsteps from holding the whole pool.
    /// </summary>
    public int MaxVoicesPerWave { get; init; } = DefaultMaxVoicesPerWave;

    /// <summary>The voice count actually in force.</summary>
    public int EffectiveVoiceCount =>
        RetailMixer ? RetailMixerVoiceCount : ClampVoiceCount(VoiceCount);

    /// <summary>Whether authored priority is actually in force.</summary>
    public bool EffectiveUseAuthoredPriority => !RetailMixer && UseAuthoredPriority;

    /// <summary>The per-sound cap actually in force.</summary>
    public int EffectiveMaxVoicesPerWave =>
        RetailMixer ? NoPerWaveCap : ClampMaxVoicesPerWave(MaxVoicesPerWave);

    /// <summary>
    /// The same settings with every value inside its range. A settings file
    /// edited by hand, or a value from an older build, cannot put the mixer in
    /// a state it could not otherwise reach.
    /// </summary>
    public AudioMixerOptions Normalized() =>
        this with
        {
            VoiceCount = ClampVoiceCount(VoiceCount),
            MaxVoicesPerWave = ClampMaxVoicesPerWave(MaxVoicesPerWave),
        };

    public static int ClampVoiceCount(int voiceCount) =>
        Math.Clamp(voiceCount, MinimumVoiceCount, MaximumVoiceCount);

    /// <summary>
    /// The per-sound cap inside its range. The ceiling is below
    /// <see cref="MinimumVoiceCount"/>, so the cap can never exceed the voices
    /// there are to cap.
    /// </summary>
    public static int ClampMaxVoicesPerWave(int maxVoicesPerWave) =>
        Math.Clamp(maxVoicesPerWave, NoPerWaveCap, MaximumMaxVoicesPerWave);
}
