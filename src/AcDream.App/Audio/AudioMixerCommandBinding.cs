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

    private readonly OpenAlAudioEngine _engine;
    private readonly Func<AudioMixerOptions> _read;
    private readonly Func<AudioMixerOptions, bool> _persist;
    private readonly Action<string> _say;
    private IDisposable? _registration;

    private AudioMixerCommandBinding(
        OpenAlAudioEngine engine,
        Func<AudioMixerOptions> read,
        Func<AudioMixerOptions, bool> persist,
        Action<string> say)
    {
        _engine = engine;
        _read = read;
        _persist = persist;
        _say = say;
    }

    /// <summary>
    /// Register the command, or return null when there is nothing to register
    /// it with (a host without a command line, or without audio).
    /// </summary>
    public static AudioMixerCommandBinding? TryRegister(
        IPluginCommandRegistry? commands,
        OpenAlAudioEngine? engine,
        Func<AudioMixerOptions> read,
        Func<AudioMixerOptions, bool> persist,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(say);
        if (commands is null || engine is null)
            return null;

        var binding = new AudioMixerCommandBinding(engine, read, persist, say);
        binding._registration = commands.Register(
            AudioMixerCommand.Verb,
            binding.Execute);
        return binding;
    }

    internal void Execute(PluginCommand command)
    {
        AudioMixerCommandResult result = AudioMixerCommand.Execute(
            _read(),
            command.Arguments);

        bool running = true;
        if (result.Changed)
        {
            // Written down first, and only then applied: a setting reported as
            // changed that a failed save would lose is worse than one that
            // never changed, and a running mixer nothing remembers is worse
            // still.
            if (!_persist(result.Options))
            {
                _say(SaveFailed);
                return;
            }

            running = _engine.ApplyMixerOptions(result.Options);
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
