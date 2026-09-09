using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AcDream.App.Configuration;
using AcDream.App.Rendering.Residency;
using AcDream.App.Streaming;
using AcDream.Runtime.Session;

namespace AcDream.App;

public sealed record RuntimeOptions(
    string DatDir,
    string PreparedAssetPath,
    bool LiveMode,
    string LiveHost,
    int LivePort,
    string? LiveUser,
    string? LivePass,
    bool DevTools,
    bool UncappedRendering,
    bool DumpMoveTruth,
    bool DumpSky,
    bool DumpWalkTranscript,
    bool NoAudio,
    int HidePartIndex,
    bool RetailCloseDegrades,
    bool DumpSceneryZ,
    int? LegacyStreamRadius,
    bool RetailUi,
    bool OpenCharacterCreationOnStart,
    string? AcDir,
    bool UiProbeDump,
    string? UiProbeScript,
    string? AutomationArtifactDirectory,
    bool ExactAutomationFramebuffer,
    int? ForcedDayGroupIndex,
    float? PinnedWorldDayFraction,
    float? SkyAnimationPhaseSeconds,
    float? InitialOrbitDistanceMeters,
    float? InitialOrbitYawDegrees,
    float? InitialOrbitPitchDegrees,
    ResidencyBudgetOptions ResidencyBudgets,
    StreamingWorkBudgetOptions StreamingWorkBudgets,
    string? VulkanDeviceOverride,
    string? VulkanForcedUnsupportedFeature,
    bool VulkanCapabilityProbe,
    int VulkanCapabilityProbeFrames,
    string? SessionConfigPath,
    string? SessionId,
    LiveSessionCharacterSelector? LiveCharacterSelector,
    string? StatusFilePath,
    IReadOnlyList<string>? Plugins,
    IReadOnlyList<string> LoginCommands,
    int LoginCommandDelayMs)
{
    public string? PreparedAssetOverlayPath { get; init; }

    public uint? PreparedAssetBaseRecipeVersion { get; init; }

    public uint? PreparedAssetEffectiveRecipeVersion { get; init; }

    public IReadOnlyList<string> PluginTags { get; init; } = [];

    public string? VtankProfileDirectoryOverride { get; init; }

    /// <summary>
    /// Build options from the process environment. Used by
    /// <c>Program.cs</c> at startup.
    /// </summary>
    public static RuntimeOptions FromEnvironment(string datDir)
        => Parse(datDir, Environment.GetEnvironmentVariable);

    public static RuntimeOptions Parse(string datDir, Func<string, string?> env)
    {
        if (datDir is null) throw new ArgumentNullException(nameof(datDir));
        if (env    is null) throw new ArgumentNullException(nameof(env));

        return new RuntimeOptions(
            DatDir:              datDir,
            PreparedAssetPath:   NullIfEmpty(env("ACDREAM_PAK_PATH"))
                                     ?? Path.Combine(datDir, "acdream.pak"),
            LiveMode:            IsExactlyOne(env("ACDREAM_LIVE")),
            LiveHost:            env("ACDREAM_TEST_HOST") ?? "127.0.0.1",
            LivePort:            TryParseInt(env("ACDREAM_TEST_PORT")) ?? 9000,
            LiveUser:            NullIfEmpty(env("ACDREAM_TEST_USER")),
            LivePass:            NullIfEmpty(env("ACDREAM_TEST_PASS")),
            DevTools:            IsExactlyOne(env("ACDREAM_DEVTOOLS")),
            // Normal presentation is always bounded by VSync or a
            // refresh-rate software pacer. This explicit diagnostic is the
            // sole way to measure truly uncapped renderer throughput.
            UncappedRendering:   IsExactlyOne(env("ACDREAM_UNCAPPED_RENDER")),
            DumpMoveTruth:       IsExactlyOne(env("ACDREAM_DUMP_MOVE_TRUTH")),
            DumpSky:             IsExactlyOne(env("ACDREAM_DUMP_SKY")),
            DumpWalkTranscript:  IsExactlyOne(env("ACDREAM_DUMP_WALK_TRANSCRIPT")),
            NoAudio:             IsExactlyOne(env("ACDREAM_NO_AUDIO")),
            HidePartIndex:       TryParseInt(env("ACDREAM_HIDE_PART")) ?? -1,
            RetailCloseDegrades: !string.Equals(env("ACDREAM_RETAIL_CLOSE_DEGRADES"), "0", StringComparison.Ordinal),
            DumpSceneryZ:        IsExactlyOne(env("ACDREAM_DUMP_SCENERY_Z")),
            LegacyStreamRadius:  TryParseNonNegativeInt(env("ACDREAM_STREAM_RADIUS")),
            RetailUi:            !string.Equals(
                                     env("ACDREAM_RETAIL_UI"),
                                     "0",
                                     StringComparison.Ordinal),
            OpenCharacterCreationOnStart:
                                 IsExactlyOne(env("ACDREAM_OPEN_CHARGEN")),
            AcDir:               NullIfEmpty(env("ACDREAM_AC_DIR")),
            UiProbeDump:         IsExactlyOne(env("ACDREAM_UI_PROBE_DUMP")),
            UiProbeScript:       NullIfEmpty(env("ACDREAM_UI_PROBE_SCRIPT")),
            AutomationArtifactDirectory:
                NullIfEmpty(env("ACDREAM_AUTOMATION_ARTIFACT_DIR")),
            ExactAutomationFramebuffer:
                IsExactlyOne(env("ACDREAM_AUTOMATION_EXACT_FRAMEBUFFER")),
            ForcedDayGroupIndex:
                TryParseNonNegativeInt(env("ACDREAM_DAY_GROUP")),
            PinnedWorldDayFraction:
                TryParseDayFraction(env("ACDREAM_WORLD_TIME")),
            SkyAnimationPhaseSeconds:
                TryParseFloat(env("ACDREAM_SKY_PHASE_SECONDS")),
            InitialOrbitDistanceMeters:
                TryParsePositiveFiniteFloat(
                    env("ACDREAM_ORBIT_DISTANCE_METERS")),
            InitialOrbitYawDegrees:
                TryParseFiniteFloat(env("ACDREAM_ORBIT_YAW_DEGREES")),
            InitialOrbitPitchDegrees:
                TryParseOrbitPitchDegrees(
                    env("ACDREAM_ORBIT_PITCH_DEGREES")),
            ResidencyBudgets:    ResidencyBudgetOptions.Parse(env),
            StreamingWorkBudgets: StreamingWorkBudgetOptions.Parse(env),
            VulkanDeviceOverride:
                NullIfEmpty(env("ACDREAM_VULKAN_DEVICE")),
            VulkanForcedUnsupportedFeature:
                NullIfEmpty(env("ACDREAM_VULKAN_FORCE_UNSUPPORTED")),
            VulkanCapabilityProbe:
                IsExactlyOne(env("ACDREAM_VULKAN_PROBE")),
            VulkanCapabilityProbeFrames:
                TryParseNonNegativeInt(env("ACDREAM_VULKAN_PROBE_FRAMES")) ?? 0,
            SessionConfigPath: null,
            SessionId: null,
            LiveCharacterSelector: null,
            StatusFilePath: null,
            Plugins: null,
            LoginCommands: [],
            LoginCommandDelayMs: 500)
        {
            PluginTags = ParsePluginTags(env("ACDREAM_PLUGIN_TAGS")),
            VtankProfileDirectoryOverride = NullIfEmpty(env("ACDREAM_VTANK_PROFILE_DIR")),
        };
    }

    internal static RuntimeOptions FromSessionConfig(
        string datDir,
        Func<string, string?> env,
        string sessionConfigPath,
        SessionConfiguration config,
        SessionDescriptor session,
        string? resolvedPassword)
    {
        if (config    is null) throw new ArgumentNullException(nameof(config));
        if (session   is null) throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionConfigPath);

        RuntimeOptions baseOptions = Parse(datDir, env);
        SessionContentDescriptor? content = config.Process?.Content;
        return baseOptions with
        {
            PreparedAssetPath = NullIfEmpty(content?.PreparedAssetPath)
                ?? baseOptions.PreparedAssetPath,
            PreparedAssetOverlayPath =
                NullIfEmpty(content?.PreparedAssetOverlayPath),
            PreparedAssetBaseRecipeVersion =
                content?.PreparedAssetBaseRecipeVersion,
            PreparedAssetEffectiveRecipeVersion =
                content?.PreparedAssetEffectiveRecipeVersion,
            LiveMode = true,
            LiveHost = session.Endpoint.Host,
            LivePort = session.Endpoint.Port,
            LiveUser = session.Account,
            LivePass = resolvedPassword,
            SessionConfigPath = sessionConfigPath,
            SessionId = session.Id,
            LiveCharacterSelector = MapCharacterSelector(session.Character),
            StatusFilePath = NullIfEmpty(session.StatusFile),
            Plugins = session.Plugins,
            LoginCommands = (IReadOnlyList<string>?)session.LoginCommands ?? [],
            LoginCommandDelayMs = session.LoginCommandDelayMs,
        };
    }

    private static LiveSessionCharacterSelector? MapCharacterSelector(
        SessionCharacterSelectorDescriptor? selector) =>
        selector is null
            ? null
            : new LiveSessionCharacterSelector(
                selector.Index,
                selector.Id,
                selector.Name);

    private static readonly PropertyInfo[] PrintableProperties =
        typeof(RuntimeOptions)
            .GetProperties(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly)
            .Where(static property =>
                property.GetMethod is not null
                && property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.MetadataToken)
            .ToArray();

    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        for (int index = 0; index < PrintableProperties.Length; index++)
        {
            PropertyInfo property = PrintableProperties[index];
            if (index != 0)
                builder.Append(", ");
            builder.Append(property.Name);
            builder.Append(" = ");
            builder.Append(
                property.Name == nameof(LivePass) && LivePass is not null
                    ? "<redacted>"
                    : property.GetValue(this));
        }
        return PrintableProperties.Length != 0;
    }

    public bool HasLiveCredentials =>
        LiveMode && !string.IsNullOrEmpty(LiveUser) && !string.IsNullOrEmpty(LivePass);

    public bool UiProbeEnabled => UiProbeDump || !string.IsNullOrEmpty(UiProbeScript);

    private static bool IsExactlyOne(string? s)
        => string.Equals(s, "1", StringComparison.Ordinal);

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrEmpty(s) ? null : s;

    private static IReadOnlyList<string> ParsePluginTags(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Where(static tag => tag.Length <= 128)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToArray();

    private static int? TryParseInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? TryParseNonNegativeInt(string? s)
        => TryParseInt(s) is { } v && v >= 0 ? v : null;

    private static float? TryParseDayFraction(string? s)
        => TryParseFloat(s) is { } value && value >= 0f && value < 1f ? value : null;

    private static float? TryParseFloat(string? s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    private static float? TryParsePositiveFiniteFloat(string? s)
        => TryParseFloat(s) is { } value
            && float.IsFinite(value)
            && value > 0f
                ? value
                : null;

    private static float? TryParseFiniteFloat(string? s)
        => TryParseFloat(s) is { } value && float.IsFinite(value)
            ? value
            : null;

    private static float? TryParseOrbitPitchDegrees(string? s)
        => TryParseFiniteFloat(s) is { } value
            && value is >= -89f and <= 89f
                ? value
                : null;
}
