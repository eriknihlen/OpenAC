using System.Collections.Generic;
using AcDream.App;
using AcDream.App.Rendering.Residency;
using AcDream.App.Streaming;

namespace AcDream.App.Tests;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void ResidencyBudgetsPreserveCurrentProductionDefaults()
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            "D:\\dat",
            _ => null);

        Assert.Equal(ResidencyBudgetOptions.Default, options.ResidencyBudgets);
        Assert.Equal(
            StreamingWorkBudgetOptions.Default,
            options.StreamingWorkBudgets);
    }

    [Fact]
    public void StreamingWorkBudgetOverridesAreOneTypedProfile()
    {
        var values = new Dictionary<string, string?>
        {
            ["ACDREAM_STREAM_WORK_MS"] = "1.75",
            ["ACDREAM_STREAM_WORK_COMPLETIONS"] = "31",
            ["ACDREAM_STREAM_WORK_CPU_MIB"] = "6",
            ["ACDREAM_STREAM_WORK_ENTITY_OPS"] = "144",
            ["ACDREAM_STREAM_WORK_GPU_MIB"] = "5",
            ["ACDREAM_STREAM_WORK_GL_RETIRE_OPS"] = "23",
            ["ACDREAM_STREAM_WORK_DEST_RESERVE_PERCENT"] = "60",
        };

        RuntimeOptions options = RuntimeOptions.Parse(
            "D:\\dat",
            name => values.GetValueOrDefault(name));

        Assert.Equal(1.75, options.StreamingWorkBudgets.MaxUpdateMilliseconds);
        Assert.Equal(31, options.StreamingWorkBudgets.MaxCompletionAdmissions);
        Assert.Equal(
            6 * StreamingWorkBudgetOptions.MiB,
            options.StreamingWorkBudgets.MaxAdoptedCpuBytes);
        Assert.Equal(144, options.StreamingWorkBudgets.MaxEntityOperations);
        Assert.Equal(
            5 * StreamingWorkBudgetOptions.MiB,
            options.StreamingWorkBudgets.MaxGpuUploadBytes);
        Assert.Equal(23, options.StreamingWorkBudgets.MaxGlRetireOperations);
        Assert.Equal(0.60f, options.StreamingWorkBudgets.DestinationReserveFraction);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("bad")]
    public void InvalidStreamingWorkValuesFallBackIndependently(string value)
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            "D:\\dat",
            name => name switch
            {
                "ACDREAM_STREAM_WORK_MS" => value,
                "ACDREAM_STREAM_WORK_DEST_RESERVE_PERCENT" => value,
                _ => null,
            });

        Assert.Equal(
            StreamingWorkBudgetOptions.Default.MaxUpdateMilliseconds,
            options.StreamingWorkBudgets.MaxUpdateMilliseconds);
        Assert.Equal(
            StreamingWorkBudgetOptions.Default.DestinationReserveFraction,
            options.StreamingWorkBudgets.DestinationReserveFraction);
    }

    [Fact]
    public void ResidencyBudgetOverridesAreTypedMebibytesAndCounts()
    {
        var values = new Dictionary<string, string?>
        {
            ["ACDREAM_RESIDENCY_MESH_GPU_MIB"] = "768",
            ["ACDREAM_RESIDENCY_MESH_UNOWNED_ENTRIES"] = "72",
            ["ACDREAM_RESIDENCY_ANIMATION_MIB"] = "48",
            ["ACDREAM_RESIDENCY_ANIMATION_ENTRIES"] = "300",
            ["ACDREAM_RESIDENCY_AUDIO_MIB"] = "24",
        };

        RuntimeOptions options = RuntimeOptions.Parse(
            "D:\\dat",
            name => values.GetValueOrDefault(name));

        Assert.Equal(
            768 * ResidencyBudgetOptions.MiB,
            options.ResidencyBudgets.ObjectMeshGpuBytes);
        Assert.Equal(
            72,
            options.ResidencyBudgets.ObjectMeshUnownedEntries);
        Assert.Equal(
            48 * ResidencyBudgetOptions.MiB,
            options.ResidencyBudgets.AnimationBytes);
        Assert.Equal(300, options.ResidencyBudgets.AnimationEntries);
        Assert.Equal(
            24 * ResidencyBudgetOptions.MiB,
            options.ResidencyBudgets.AudioBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("bad")]
    [InlineData("8796093022208")]
    public void InvalidResidencyBudgetFallsBackToCurrentDefault(string value)
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            "D:\\dat",
            name => name == "ACDREAM_RESIDENCY_MESH_GPU_MIB"
                ? value
                : null);

        Assert.Equal(
            ResidencyBudgetOptions.Default.ObjectMeshGpuBytes,
            options.ResidencyBudgets.ObjectMeshGpuBytes);
    }

    private const string AnyDatDir = "C:/Users/test/dats";

    private static Func<string, string?> Env(Dictionary<string, string?> values)
        => name => values.TryGetValue(name, out var v) ? v : null;

    private static Func<string, string?> EmptyEnv() => _ => null;

    [Fact]
    public void Defaults_AllSafeOff_WhenEnvironmentIsEmpty()
    {
        var opts = RuntimeOptions.Parse(AnyDatDir, EmptyEnv());

        Assert.Equal(AnyDatDir, opts.DatDir);
        Assert.Equal(
            Path.Combine(AnyDatDir, "acdream.pak"),
            opts.PreparedAssetPath);
        Assert.False(opts.LiveMode);
        Assert.Equal("127.0.0.1", opts.LiveHost);
        Assert.Equal(9000, opts.LivePort);
        Assert.Null(opts.LiveUser);
        Assert.Null(opts.LivePass);
        Assert.False(opts.DevTools);
        Assert.False(opts.UncappedRendering);
        Assert.False(opts.DumpMoveTruth);
        Assert.False(opts.DumpWalkTranscript);
        Assert.False(opts.NoAudio);
        Assert.Equal(-1, opts.HidePartIndex);
        Assert.True(opts.RetailCloseDegrades);
        Assert.False(opts.DumpSceneryZ);
        Assert.Null(opts.LegacyStreamRadius);
        Assert.False(opts.UiProbeDump);
        Assert.Null(opts.UiProbeScript);
        Assert.Null(opts.AutomationArtifactDirectory);
        Assert.False(opts.ExactAutomationFramebuffer);
        Assert.False(opts.UiProbeEnabled);
        Assert.False(opts.HasLiveCredentials);
        Assert.Empty(opts.PluginTags);
        Assert.Null(opts.VtankProfileDirectoryOverride);
    }

    [Fact]
    public void PluginPeerTagsAreParsedOnceBoundedAndCaseInsensitive()
    {
        string oversized = new('x', 129);
        RuntimeOptions options = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new()
            {
                ["ACDREAM_PLUGIN_TAGS"] =
                    $" healer,Leader,HEALER,,{oversized}, scout ",
            }));

        Assert.Equal(["healer", "Leader", "scout"], options.PluginTags);
    }

    [Fact]
    public void VtankProfileDirectoryOverrideIsNullUnlessSet()
    {
        RuntimeOptions blank = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new() { ["ACDREAM_VTANK_PROFILE_DIR"] = "" }));
        Assert.Null(blank.VtankProfileDirectoryOverride);

        RuntimeOptions set = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new()
            {
                ["ACDREAM_VTANK_PROFILE_DIR"] =
                    "C:/Games/VirindiPlugins/VirindiTank",
            }));
        Assert.Equal(
            "C:/Games/VirindiPlugins/VirindiTank",
            set.VtankProfileDirectoryOverride);
    }

    [Fact]
    public void PreparedAssetPath_DefaultsBesideDats_AndAllowsOneOverride()
    {
        Assert.Equal(
            Path.Combine(AnyDatDir, "acdream.pak"),
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).PreparedAssetPath);

        var overridden = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new() { ["ACDREAM_PAK_PATH"] = "D:/prepared/acdream.pak" }));
        Assert.Equal("D:/prepared/acdream.pak", overridden.PreparedAssetPath);
    }

    [Fact]
    public void LiveMode_Set_ExactlyByValue1()
    {
        Assert.True(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_LIVE"] = "1" })).LiveMode);
        Assert.False(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_LIVE"] = "0" })).LiveMode);
        Assert.False(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_LIVE"] = "true" })).LiveMode);
        Assert.False(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_LIVE"] = "" })).LiveMode);
    }

    [Fact]
    public void LiveHostAndPort_FallBackToDefaults_WhenUnsetOrInvalid()
    {
        var withDefaults = RuntimeOptions.Parse(AnyDatDir, EmptyEnv());
        Assert.Equal("127.0.0.1", withDefaults.LiveHost);
        Assert.Equal(9000, withDefaults.LivePort);

        var withOverrides = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_TEST_HOST"] = "play.example.com",
            ["ACDREAM_TEST_PORT"] = "9123",
        }));
        Assert.Equal("play.example.com", withOverrides.LiveHost);
        Assert.Equal(9123, withOverrides.LivePort);

        // Non-numeric port falls back to default; we don't throw at parse time.
        var withBadPort = RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_TEST_PORT"] = "abc" }));
        Assert.Equal(9000, withBadPort.LivePort);
    }

    [Fact]
    public void LiveUserPass_NullWhenEmptyOrUnset()
    {
        var emptyValues = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_TEST_USER"] = "",
            ["ACDREAM_TEST_PASS"] = "",
        }));
        Assert.Null(emptyValues.LiveUser);
        Assert.Null(emptyValues.LivePass);

        var realValues = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_TEST_USER"] = "testaccount",
            ["ACDREAM_TEST_PASS"] = "testpassword",
        }));
        Assert.Equal("testaccount", realValues.LiveUser);
        Assert.Equal("testpassword", realValues.LivePass);
    }

    [Fact]
    public void RecordPrintMembersRedactsTheLivePassword()
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new()
            {
                ["ACDREAM_LIVE"] = "1",
                ["ACDREAM_TEST_USER"] = "testaccount",
                ["ACDREAM_TEST_PASS"] = "top-secret-value",
            }));

        string printed = options.ToString();

        Assert.DoesNotContain("top-secret-value", printed, StringComparison.Ordinal);
        Assert.Contains("LivePass = <redacted>", printed, StringComparison.Ordinal);
        Assert.Contains("LiveHost = 127.0.0.1", printed, StringComparison.Ordinal);
        Assert.Contains("HasLiveCredentials = True", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void HasLiveCredentials_RequiresLiveModeAndBothUserAndPass()
    {
        var noLive = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_TEST_USER"] = "u",
            ["ACDREAM_TEST_PASS"] = "p",
        }));
        Assert.False(noLive.HasLiveCredentials);

        var missingUser = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_LIVE"] = "1",
            ["ACDREAM_TEST_PASS"] = "p",
        }));
        Assert.False(missingUser.HasLiveCredentials);

        var missingPass = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_LIVE"] = "1",
            ["ACDREAM_TEST_USER"] = "u",
        }));
        Assert.False(missingPass.HasLiveCredentials);

        var ok = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_LIVE"] = "1",
            ["ACDREAM_TEST_USER"] = "u",
            ["ACDREAM_TEST_PASS"] = "p",
        }));
        Assert.True(ok.HasLiveCredentials);
    }

    [Fact]
    public void HidePartIndex_MinusOneWhenUnset_ParsesIntegers()
    {
        Assert.Equal(-1, RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).HidePartIndex);
        Assert.Equal(7, RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_HIDE_PART"] = "7" })).HidePartIndex);
        // Invalid → fall back to -1 (preserves the int.TryParse failure semantics).
        Assert.Equal(-1, RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_HIDE_PART"] = "abc" })).HidePartIndex);
    }

    [Fact]
    public void RetailCloseDegrades_DefaultOn_ExceptWhenValueIsExactlyZero()
    {
        // Unset → on.
        Assert.True(RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).RetailCloseDegrades);

        // Exactly "0" → off. Matches the pre-refactor semantics:
        //   !string.Equals(env, "0", StringComparison.Ordinal)
        Assert.False(RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_RETAIL_CLOSE_DEGRADES"] = "0",
        })).RetailCloseDegrades);

        Assert.True(RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_RETAIL_CLOSE_DEGRADES"] = "1",
        })).RetailCloseDegrades);
        Assert.True(RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_RETAIL_CLOSE_DEGRADES"] = "false",
        })).RetailCloseDegrades);
    }

    [Fact]
    public void LegacyStreamRadius_NullWhenUnsetOrInvalid_ParsesNonNegativeIntegers()
    {
        Assert.Null(RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).LegacyStreamRadius);
        Assert.Null(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_STREAM_RADIUS"] = "abc" })).LegacyStreamRadius);
        // Negative values are filtered out by the pre-refactor `sr >= 0` guard.
        Assert.Null(RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_STREAM_RADIUS"] = "-3" })).LegacyStreamRadius);

        Assert.Equal(0, RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_STREAM_RADIUS"] = "0" })).LegacyStreamRadius);
        Assert.Equal(5, RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_STREAM_RADIUS"] = "5" })).LegacyStreamRadius);
        Assert.Equal(12, RuntimeOptions.Parse(AnyDatDir, Env(new() { ["ACDREAM_STREAM_RADIUS"] = "12" })).LegacyStreamRadius);
    }

    [Fact]
    public void DayGroupOverride_IsReadOnceIntoTypedOptions()
    {
        Assert.Null(
            RuntimeOptions.Parse(
                AnyDatDir,
                EmptyEnv()).ForcedDayGroupIndex);
        Assert.Null(
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_DAY_GROUP"] = "-1" }))
            .ForcedDayGroupIndex);
        Assert.Equal(
            7,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_DAY_GROUP"] = "7" }))
            .ForcedDayGroupIndex);
    }

    [Fact]
    public void WorldTimeOverride_IsReadOnceIntoTypedOptions()
    {
        Assert.Null(
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).PinnedWorldDayFraction);
        Assert.Equal(
            0.5f,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_WORLD_TIME"] = "0.5" }))
            .PinnedWorldDayFraction);
        Assert.Equal(
            0f,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_WORLD_TIME"] = "0" }))
            .PinnedWorldDayFraction);
        foreach (string rejected in new[] { "1", "1.5", "-0.1", "midnight", "" })
        {
            Assert.Null(
                RuntimeOptions.Parse(
                    AnyDatDir,
                    Env(new() { ["ACDREAM_WORLD_TIME"] = rejected }))
                .PinnedWorldDayFraction);
        }
    }

    [Fact]
    public void ExactAutomationFramebuffer_IsExplicitAndExactOneOnly()
    {
        Assert.True(RuntimeOptions.Parse(
            AnyDatDir,
            Env(new() { ["ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER"] = "1" }))
            .ExactAutomationFramebuffer);

        foreach (string value in new[] { "", "0", "true", "yes" })
        {
            Assert.False(RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER"] = value }))
                .ExactAutomationFramebuffer);
        }
    }

    [Fact]
    public void OrbitDistanceOverride_AcceptsOnlyPositiveFiniteMeters()
    {
        Assert.Null(
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv())
                .InitialOrbitDistanceMeters);
        Assert.Equal(
            120f,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_ORBIT_DISTANCE_METERS"] = "120" }))
            .InitialOrbitDistanceMeters);

        foreach (string rejected in new[] { "0", "-1", "NaN", "Infinity", "near" })
        {
            Assert.Null(
                RuntimeOptions.Parse(
                    AnyDatDir,
                    Env(new() { ["ACDREAM_ORBIT_DISTANCE_METERS"] = rejected }))
                .InitialOrbitDistanceMeters);
        }
    }

    [Fact]
    public void OrbitAngleOverrides_AcceptFiniteYawAndBoundedPitchDegrees()
    {
        RuntimeOptions defaults = RuntimeOptions.Parse(AnyDatDir, EmptyEnv());
        Assert.Null(defaults.InitialOrbitYawDegrees);
        Assert.Null(defaults.InitialOrbitPitchDegrees);

        RuntimeOptions parsed = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new()
            {
                ["ACDREAM_ORBIT_YAW_DEGREES"] = "-135.5",
                ["ACDREAM_ORBIT_PITCH_DEGREES"] = "7.25",
            }));
        Assert.Equal(-135.5f, parsed.InitialOrbitYawDegrees);
        Assert.Equal(7.25f, parsed.InitialOrbitPitchDegrees);

        foreach (string rejected in new[] { "NaN", "Infinity", "angle" })
        {
            Assert.Null(
                RuntimeOptions.Parse(
                    AnyDatDir,
                    Env(new() { ["ACDREAM_ORBIT_YAW_DEGREES"] = rejected }))
                .InitialOrbitYawDegrees);
        }
        foreach (string rejected in new[] { "-90", "90", "NaN", "pitch" })
        {
            Assert.Null(
                RuntimeOptions.Parse(
                    AnyDatDir,
                    Env(new() { ["ACDREAM_ORBIT_PITCH_DEGREES"] = rejected }))
                .InitialOrbitPitchDegrees);
        }
    }

    [Fact]
    public void SkyPhaseOverride_IsReadOnceIntoTypedOptions()
    {
        Assert.Null(
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).SkyAnimationPhaseSeconds);
        Assert.Null(
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_SKY_PHASE_SECONDS"] = "not-a-number" }))
            .SkyAnimationPhaseSeconds);
        Assert.Equal(
            0f,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_SKY_PHASE_SECONDS"] = "0" }))
            .SkyAnimationPhaseSeconds);
        Assert.Equal(
            12.5f,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_SKY_PHASE_SECONDS"] = "12.5" }))
            .SkyAnimationPhaseSeconds);
    }

    [Fact]
    public void DiagnosticFlags_RespectExactValueOne()
    {
        var allOn = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_DEVTOOLS"] = "1",
            ["ACDREAM_UNCAPPED_RENDER"] = "1",
            ["ACDREAM_DUMP_MOVE_TRUTH"] = "1",
            ["ACDREAM_DUMP_SKY"] = "1",
            ["ACDREAM_DUMP_WALK_TRANSCRIPT"] = "1",
            ["ACDREAM_NO_AUDIO"] = "1",
            ["ACDREAM_DUMP_SCENERY_Z"] = "1",
        }));
        Assert.True(allOn.DevTools);
        Assert.True(allOn.UncappedRendering);
        Assert.True(allOn.DumpMoveTruth);
        Assert.True(allOn.DumpSky);
        Assert.True(allOn.DumpWalkTranscript);
        Assert.True(allOn.NoAudio);
        Assert.True(allOn.DumpSceneryZ);

        var anyOther = RuntimeOptions.Parse(AnyDatDir, Env(new()
        {
            ["ACDREAM_DUMP_MOVE_TRUTH"] = "true",
            ["ACDREAM_DEVTOOLS"] = "true",
            ["ACDREAM_UNCAPPED_RENDER"] = "true",
            ["ACDREAM_NO_AUDIO"] = "2",
            ["ACDREAM_DUMP_SCENERY_Z"] = " 1",
            ["ACDREAM_DUMP_WALK_TRANSCRIPT"] = "0",
        }));
        Assert.False(anyOther.DevTools);
        Assert.False(anyOther.UncappedRendering);
        Assert.False(anyOther.DumpMoveTruth);
        Assert.False(anyOther.NoAudio);
        Assert.False(anyOther.DumpSceneryZ);
        Assert.False(anyOther.DumpWalkTranscript);
    }

    [Fact]
    public void Parse_RejectsNullDatDirOrEnv()
    {
        Assert.Throws<ArgumentNullException>(() => RuntimeOptions.Parse(null!, EmptyEnv()));
        Assert.Throws<ArgumentNullException>(() => RuntimeOptions.Parse(AnyDatDir, null!));
    }

    [Fact]
    public void VulkanDeviceOverride_IsNullWhenUnsetOrEmpty()
    {
        Assert.Null(RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).VulkanDeviceOverride);
        Assert.Null(
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_VULKAN_DEVICE"] = "" })).VulkanDeviceOverride);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("Radeon")]
    public void VulkanDeviceOverride_IsCarriedVerbatim(string value)
    {
        Assert.Equal(
            value,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_VULKAN_DEVICE"] = value })).VulkanDeviceOverride);
    }

    [Fact]
    public void VulkanForcedUnsupportedFeature_DrivesTheExitFourGateKnob()
    {
        Assert.Null(
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).VulkanForcedUnsupportedFeature);
        Assert.Equal(
            "timelineSemaphore",
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_VULKAN_FORCE_UNSUPPORTED"] = "timelineSemaphore" }))
                .VulkanForcedUnsupportedFeature);
    }

    [Fact]
    public void VulkanCapabilityProbeFrames_DefaultsToUnbounded()
    {
        Assert.Equal(
            0,
            RuntimeOptions.Parse(AnyDatDir, EmptyEnv()).VulkanCapabilityProbeFrames);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData("0", 0)]
    public void VulkanCapabilityProbeFrames_ParsesANonNegativeBudget(
        string value,
        int expected)
    {
        Assert.Equal(
            expected,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_VULKAN_PROBE_FRAMES"] = value }))
                .VulkanCapabilityProbeFrames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("many")]
    [InlineData("30.5")]
    public void VulkanCapabilityProbeFrames_RejectsMalformedValues(string value)
    {
        Assert.Equal(
            0,
            RuntimeOptions.Parse(
                AnyDatDir,
                Env(new() { ["ACDREAM_VULKAN_PROBE_FRAMES"] = value }))
                .VulkanCapabilityProbeFrames);
    }
}
