using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcDream.Core.Audio;
using AcDream.App.Tests.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;

namespace AcDream.App.Tests.Audio;

// OpenAC #42: the authored thunder sequence. Its hooks come round on a fixed
// cadence and are meant to lose most of their rolls; playing all of them every
// cycle is what made thunder sound constant.
//
// This also pins which of the tweaked hook's two floats is the play
// probability. The dat reader's names for them are the wrong way round, so the
// mapping is checked against what the installed data actually contains rather
// than trusted to the property name.
[Trait("Lane", "InstalledDat")]
public sealed class TweakedSoundHookLiveDatTests
{
    private const uint ThunderScript = 0x33000453u;

    private const uint FirstScriptId = 0x33000000u;
    private const uint LastScriptId = 0x33FFFFFFu;

    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");

    [InstalledDatFact]
    public void TheThunderSequence_IsAuthoredToStaySilentMostTimesRound()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        DatPhysicsScript script = Assert.IsType<DatPhysicsScript>(
            dats.Get<DatPhysicsScript>(ThunderScript));

        SoundTweakedHook[] thunder = script.ScriptData
            .Select(static item => item.Hook)
            .OfType<SoundTweakedHook>()
            .ToArray();

        // Eight claps spread over the script, which restarts itself every
        // thirty seconds.
        Assert.Equal(8, thunder.Length);
        Assert.Equal(
            new[] { 0x0A0004D1u, 0x0A0004D0u, 0x0A0004D1u, 0x0A0004D0u, 0x0A0004D2u, 0x0A0004D0u, 0x0A0004D2u, 0x0A0004D0u },
            thunder.Select(static hook => (uint)hook.SoundId));

        foreach (SoundTweakedHook clap in thunder)
        {
            float probability = TweakedSoundHooks.PlayProbability(clap);
            // Every clap is authored to be heard sometimes and skipped more
            // often than not. Reading the other float instead would still land
            // in range, so the point of this row is the exact values.
            Assert.InRange(probability, 0.2f, 0.5f);
        }

        Assert.Equal(
            new[] { 0.2f, 0.5f, 0.4f, 0.3f, 0.2f, 0.2f, 0.2f, 0.2f },
            thunder.Select(TweakedSoundHooks.PlayProbability));
    }

    [InstalledDatFact]
    public void EveryAuthoredTweakedHook_CarriesAUsableProbability()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);

        var probabilities = new List<float>();
        foreach (uint id in dats.Portal.Tree
            .Select(static file => file.Id)
            .Where(static id => id >= FirstScriptId && id <= LastScriptId))
        {
            if (dats.Get<DatPhysicsScript>(id) is not { } script)
                continue;

            probabilities.AddRange(script.ScriptData
                .Select(static item => item.Hook)
                .OfType<SoundTweakedHook>()
                .Select(TweakedSoundHooks.PlayProbability));
        }

        Assert.NotEmpty(probabilities);
        Assert.All(probabilities, probability => Assert.InRange(probability, 0f, 1f));

        // Nothing is authored at zero: every one of these hooks is meant to be
        // heard at least sometimes. The other float does reach zero, which is
        // what a priority looks like and not what a play chance looks like.
        Assert.DoesNotContain(0f, probabilities);

        Console.WriteLine(FormattableString.Invariant(
            $"[#42] tweaked sound hooks: {probabilities.Count}, play probability {probabilities.Min()}..{probabilities.Max()}"));
    }
}
