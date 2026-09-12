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
    private readonly OpenAlAudioEngine _engine;
    private readonly Func<AudioMixerOptions> _read;
    private readonly Action<AudioMixerOptions> _persist;
    private readonly Action<string> _say;
    private IDisposable? _registration;

    private AudioMixerCommandBinding(
        OpenAlAudioEngine engine,
        Func<AudioMixerOptions> read,
        Action<AudioMixerOptions> persist,
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
        Action<AudioMixerOptions> persist,
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

        // Written down first: a setting the player can see reported but that a
        // failed save would lose is worse than one that never changed.
        if (result.Changed)
        {
            _persist(result.Options);
            _engine.ApplyMixerOptions(result.Options);
        }

        foreach (string line in result.Lines)
            _say(line);
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _registration = null;
    }
}
