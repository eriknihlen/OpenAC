using System;
using System.Collections.Generic;
using System.Globalization;

namespace AcDream.Core.Audio;

/// <summary>
/// The settings the command asks for, whether they differ from the ones it was
/// given, and the lines to show the player.
/// </summary>
public readonly record struct AudioMixerCommandResult(
    AudioMixerOptions Options,
    bool Changed,
    IReadOnlyList<string> Lines);

/// <summary>
/// The <c>/mixer</c> command: reading and changing the mixer settings from the
/// chat line, so they can be tried without restarting the client. Pure text in,
/// settings plus text out — applying them and writing them down is the caller's
/// job.
/// </summary>
public static class AudioMixerCommand
{
    public const string Verb = "mixer";

    public const string Usage =
        "/mixer | /mixer retail on|off | /mixer voices 16-64 | "
        + "/mixer priority on|off | /mixer perwave 0-8";

    public static AudioMixerCommandResult Execute(
        AudioMixerOptions current,
        string? arguments)
    {
        ArgumentNullException.ThrowIfNull(current);

        AudioMixerOptions before = current.Normalized();
        string[] words = (arguments ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return Unchanged(before, Describe(before));

        AudioMixerOptions? asked = words[0].ToLowerInvariant() switch
        {
            "retail" => TryReadSwitch(words, out bool retail)
                ? before with { RetailMixer = retail }
                : null,
            "voices" => TryReadCount(words, out int voices)
                ? before with { VoiceCount = AudioMixerOptions.ClampVoiceCount(voices) }
                : null,
            "priority" => TryReadSwitch(words, out bool priority)
                ? before with { UseAuthoredPriority = priority }
                : null,
            "perwave" => TryReadCount(words, out int perWave)
                ? before with
                {
                    MaxVoicesPerWave =
                        AudioMixerOptions.ClampMaxVoicesPerWave(perWave),
                }
                : null,
            _ => null,
        };

        if (asked is null)
            return Unchanged(before, Usage);

        AudioMixerOptions after = asked.Normalized();
        return new AudioMixerCommandResult(
            after,
            Changed: after != before,
            Lines: [Describe(after)]);
    }

    /// <summary>One line saying what the mixer is doing.</summary>
    public static string Describe(AudioMixerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"mixer: retail mixer {OnOff(options.RetailMixer)}, "
            + $"{options.EffectiveVoiceCount} voices, "
            + $"authored priority {OnOff(options.EffectiveUseAuthoredPriority)}, "
            + $"{DescribeCap(options.EffectiveMaxVoicesPerWave)}");
    }

    private static string DescribeCap(int maxVoicesPerWave) =>
        maxVoicesPerWave <= AudioMixerOptions.NoPerWaveCap
            ? "no cap per sound"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"at most {maxVoicesPerWave} voices per sound");

    private static string OnOff(bool value) => value ? "on" : "off";

    private static AudioMixerCommandResult Unchanged(
        AudioMixerOptions options,
        string line) =>
        new(options, Changed: false, Lines: [line]);

    private static bool TryReadSwitch(string[] words, out bool value)
    {
        value = false;
        if (words.Length != 2)
            return false;

        switch (words[1].ToLowerInvariant())
        {
            case "on":
            case "1":
            case "true":
                value = true;
                return true;
            case "off":
            case "0":
            case "false":
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadCount(string[] words, out int value)
    {
        value = 0;
        return words.Length == 2
            && int.TryParse(
                words[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value);
    }
}
