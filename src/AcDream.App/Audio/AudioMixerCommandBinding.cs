using System;
using AcDream.Core.Audio;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Audio;

/// <summary>
/// Binds the <c>/mixer</c> command to the running mixer: it says what the mixer
/// is doing, changes it where it stands, and writes the change down so the next
/// launch starts the same way. It exists so the settings can be tried by ear
/// without restarting the client.
/// </summary>
internal sealed class AudioMixerCommandBinding : IDisposable
{
    internal const string SaveFailed =
        "mixer: the settings could not be saved, so nothing changed.";

    internal const string NoMixerRunning =
        "mixer: saved, but there is no mixer running to change - it will start "
        + "this way next time.";

    private readonly AudioMixerSettings _mixer;
    private readonly Action<string> _say;
    private IDisposable? _registration;

    private AudioMixerCommandBinding(
        AudioMixerSettings mixer,
        Action<string> say)
    {
        _mixer = mixer;
        _say = say;
    }

    /// <summary>
    /// Register the command, or return null when there is nothing to register
    /// it with (a host without a command line, or without audio).
    /// </summary>
    public static AudioMixerCommandBinding? TryRegister(
        IPluginCommandRegistry? commands,
        AudioMixerSettings? mixer,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (commands is null || mixer is null)
            return null;

        var binding = new AudioMixerCommandBinding(mixer, say);
        binding._registration = commands.Register(
            AudioMixerCommand.Verb,
            binding.Execute);
        return binding;
    }

    internal void Execute(PluginCommand command)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            _mixer.Current,
            command.Arguments);

        bool running = true;
        if (result.Changed)
        {
            // The command and the Options panel share one save-then-apply
            // owner, so both orders are the same order.
            AudioMixerChange change = _mixer.Change(result.Options);
            if (!change.Saved)
            {
                _say(SaveFailed);
                return;
            }

            running = change.MixerRunning;
        }

        foreach (string line in result.Lines)
            _say(line);

        if (!running)
            _say(NoMixerRunning);
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
