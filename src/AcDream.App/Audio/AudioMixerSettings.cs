using System;
using AcDream.Core.Audio;

namespace AcDream.App.Audio;

/// <summary>What happened to a mixer setting that was asked to change.</summary>
/// <param name="Saved">
/// Whether the setting was written down. When it was not, nothing changed at
/// all: the running mixer is left alone and the remembered settings still hold
/// the old value.
/// </param>
/// <param name="MixerRunning">
/// Whether there was a mixer to change. False means the setting is remembered
/// but takes effect on the next launch — there is no audio device, or audio was
/// turned off at startup.
/// </param>
internal readonly record struct AudioMixerChange(bool Saved, bool MixerRunning);

/// <summary>
/// The one place a mixer setting is changed: written down first, and only then
/// applied to the mixer that is running. Both ways of changing it — the
/// <c>/mixer</c> command and the Sound block of the Options panel's Config tab
/// — go through this, so neither can end up with a mixer running settings
/// nothing remembers.
/// </summary>
internal sealed class AudioMixerSettings
{
    /// <summary>Said when the settings could not be written down.</summary>
    internal const string SaveFailed =
        "mixer: the settings could not be saved, so nothing changed.";

    /// <summary>
    /// Said when the settings were written down but there was no mixer to
    /// change. Reporting them as in force would be a lie.
    /// </summary>
    internal const string NoMixerRunning =
        "mixer: saved, but there is no mixer running to change - it will start "
        + "this way next time.";

    private readonly Func<AudioMixerOptions> _read;
    private readonly Func<AudioMixerOptions, bool> _persist;
    private readonly Func<AudioMixerOptions, bool> _applyToRunningMixer;

    /// <param name="read">The settings as they are remembered right now.</param>
    /// <param name="persist">
    /// Writes the settings down; false when the write failed.
    /// </param>
    /// <param name="applyToRunningMixer">
    /// Changes the mixer where it stands; false when there is no mixer running.
    /// </param>
    public AudioMixerSettings(
        Func<AudioMixerOptions> read,
        Func<AudioMixerOptions, bool> persist,
        Func<AudioMixerOptions, bool> applyToRunningMixer)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(applyToRunningMixer);
        _read = read;
        _persist = persist;
        _applyToRunningMixer = applyToRunningMixer;
    }

    /// <summary>The settings as they are remembered right now.</summary>
    public AudioMixerOptions Current => _read();

    /// <summary>
    /// Write the settings down and then change the running mixer to match. The
    /// order matters: a setting reported as changed that a failed save would
    /// lose is worse than one that never changed, and a running mixer nothing
    /// remembers is worse still.
    /// </summary>
    public AudioMixerChange Change(AudioMixerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!_persist(options))
            return new AudioMixerChange(Saved: false, MixerRunning: false);
        return new AudioMixerChange(
            Saved: true,
            MixerRunning: _applyToRunningMixer(options));
    }

    /// <summary>
    /// Change the settings and say where the change took effect, for a caller
    /// that has no reply of its own to put the answer in (the settings row).
    /// </summary>
    /// <returns>Whether the settings were written down.</returns>
    public bool ChangeAndReport(AudioMixerOptions options, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        AudioMixerChange change = Change(options);
        if (!change.Saved)
        {
            say(SaveFailed);
            return false;
        }
        if (!change.MixerRunning)
            say(NoMixerRunning);
        return true;
    }
}
