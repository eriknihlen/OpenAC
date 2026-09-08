using System.Numerics;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct RenderPackPassDiagnostics(
    string PassId,
    double GpuMilliseconds,
    int DrawCalls,
    int DispatchCalls);

internal sealed record RenderPackRuntimeDiagnostics(
    string EffectiveQuality,
    long RetainedGpuBytes,
    long TransientGpuBytes,
    int ImageCount,
    int BufferCount,
    int DrawCalls,
    int DispatchCalls,
    int ShadowCasterCount,
    int CascadeDrawCount,
    int CpuClassificationCalls,
    double SunElevationDegrees,
    int ActiveDayGroup,
    string Weather,
    double WeatherIntensity,
    bool Outdoor,
    double DirectionalShadowStrength,
    IReadOnlyList<RenderPackPassDiagnostics> Passes)
{
    /// <summary>
    /// Number of matrices addressed through the one shared world-transform
    /// binding after the enhanced world receiver has appended its ordinary
    /// draws to the directional-shadow prefix. Zero means that no shared
    /// directional-shadow frame was active for the sampled frame.
    /// </summary>
    public uint SharedWorldTransformUsedInstances { get; init; }

    public IReadOnlyList<RenderPackCpuStageDiagnostics> CpuStages { get; init; } = [];
    public AuthoredCelestialShadowSourceKind DirectionalShadowSourceKind
    {
        get;
        init;
    }
    public int DirectionalShadowSourceObjectIndex { get; init; } = -1;
    public uint DirectionalShadowSourceGfxObjId { get; init; }
    public Vector3 DirectionalShadowSurfaceToLightDirection { get; init; }
    public float DirectionalShadowLightElevationSin { get; init; }
    public DirectionalShadowTransformChurnDiagnostics ShadowTransformChurn
    {
        get;
        init;
    }

    internal static RenderPackRuntimeDiagnostics Empty(string quality) => new(
        quality,
        RetainedGpuBytes: 0,
        TransientGpuBytes: 0,
        ImageCount: 0,
        BufferCount: 0,
        DrawCalls: 0,
        DispatchCalls: 0,
        ShadowCasterCount: 0,
        CascadeDrawCount: 0,
        CpuClassificationCalls: 0,
        SunElevationDegrees: 0,
        ActiveDayGroup: -1,
        Weather: "unknown",
        WeatherIntensity: 0,
        Outdoor: false,
        DirectionalShadowStrength: 0,
        Passes: []);
}

internal interface IRenderPackRuntimeDiagnosticsSource
{
    RenderPackRuntimeDiagnostics CaptureDiagnostics();
}

internal sealed record RenderPackDiagnosticsSnapshot(
    RenderPackActivationState State,
    string PackId,
    string? PackVersion,
    string PresetId,
    string EffectiveQuality,
    string? FailureReason,
    long ActivationGeneration,
    long RetainedGpuBytes,
    long TransientGpuBytes,
    int ImageCount,
    int BufferCount,
    int DrawCalls,
    int DispatchCalls,
    int ShadowCasterCount,
    int CascadeDrawCount,
    int CpuClassificationCalls,
    double SunElevationDegrees,
    int ActiveDayGroup,
    string Weather,
    double WeatherIntensity,
    bool Outdoor,
    double DirectionalShadowStrength,
    IReadOnlyList<RenderPackPassDiagnostics> Passes,
    RenderPackPerformanceSnapshot Performance = default)
{
    public uint SharedWorldTransformUsedInstances { get; init; }

    public IReadOnlyList<RenderPackCpuStageDiagnostics> CpuStages { get; init; } = [];
    public AuthoredCelestialShadowSourceKind DirectionalShadowSourceKind
    {
        get;
        init;
    }
    public int DirectionalShadowSourceObjectIndex { get; init; } = -1;
    public uint DirectionalShadowSourceGfxObjId { get; init; }
    public Vector3 DirectionalShadowSurfaceToLightDirection { get; init; }
    public float DirectionalShadowLightElevationSin { get; init; }
    public DirectionalShadowTransformChurnDiagnostics ShadowTransformChurn
    {
        get;
        init;
    }

    internal static RenderPackDiagnosticsSnapshot Retail { get; } = new(
        RenderPackActivationState.Retail,
        PackId: "retail",
        PackVersion: null,
        PresetId: "off",
        EffectiveQuality: "off",
        FailureReason: null,
        ActivationGeneration: 0,
        RetainedGpuBytes: 0,
        TransientGpuBytes: 0,
        ImageCount: 0,
        BufferCount: 0,
        DrawCalls: 0,
        DispatchCalls: 0,
        ShadowCasterCount: 0,
        CascadeDrawCount: 0,
        CpuClassificationCalls: 0,
        SunElevationDegrees: 0,
        ActiveDayGroup: -1,
        Weather: "unknown",
        WeatherIntensity: 0,
        Outdoor: false,
        DirectionalShadowStrength: 0,
        Passes: []);

    internal bool IsRetail =>
        string.Equals(PackId, "retail", StringComparison.Ordinal);
}

internal interface IRenderPackDiagnosticsSnapshotSource
{
    RenderPackDiagnosticsSnapshot CaptureDiagnostics();
}

internal sealed class DeferredRenderPackDiagnosticsSource
    : IRenderPackDiagnosticsSnapshotSource
{
    private IRenderPackDiagnosticsSnapshotSource? _target;

    public RenderPackDiagnosticsSnapshot CaptureDiagnostics() =>
        _target?.CaptureDiagnostics() ?? RenderPackDiagnosticsSnapshot.Retail;

    internal IDisposable BindOwned(IRenderPackDiagnosticsSnapshotSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_target is not null && !ReferenceEquals(_target, target))
            throw new InvalidOperationException("Render-pack diagnostics are already bound.");
        _target = target;
        return new Binding(this, target);
    }

    private void Unbind(IRenderPackDiagnosticsSnapshotSource target)
    {
        if (ReferenceEquals(_target, target))
            _target = null;
    }

    private sealed class Binding(
        DeferredRenderPackDiagnosticsSource owner,
        IRenderPackDiagnosticsSnapshotSource target) : IDisposable
    {
        private DeferredRenderPackDiagnosticsSource? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(target);
    }
}

internal static class RenderPackDiagnosticsFormatter
{
    internal static string Format(RenderPackDiagnosticsSnapshot value) =>
            $"[render-pack] state={value.State} "
            + $"pack={value.PackId}@{value.PackVersion ?? "(missing)"} "
            + $"preset={value.PresetId} effective={value.EffectiveQuality} "
            + $"generation={value.ActivationGeneration} "
            + $"gpuBytes={value.RetainedGpuBytes}/{value.TransientGpuBytes} "
            + $"resources={value.ImageCount}i/{value.BufferCount}b "
            + $"submit={value.DrawCalls}d/{value.DispatchCalls}c "
            + $"worldTransforms={value.SharedWorldTransformUsedInstances}used "
            + $"shadow={value.ShadowCasterCount}casters/{value.CascadeDrawCount}cascadeDraws/"
            + $"{value.CpuClassificationCalls}classify "
            + $"shadowSource={value.DirectionalShadowSourceKind}/"
            + $"obj{value.DirectionalShadowSourceObjectIndex}/"
            + $"0x{value.DirectionalShadowSourceGfxObjId:X8}/"
            + $"dir({Invariant(value.DirectionalShadowSurfaceToLightDirection.X, "F4")},"
            + $"{Invariant(value.DirectionalShadowSurfaceToLightDirection.Y, "F4")},"
            + $"{Invariant(value.DirectionalShadowSurfaceToLightDirection.Z, "F4")})/"
            + $"elevSin={Invariant(value.DirectionalShadowLightElevationSin, "F4")} "
            + $"atmosphere={Invariant(value.SunElevationDegrees, "F2")}deg/day{value.ActiveDayGroup}/"
            + $"{value.Weather}:{Invariant(value.WeatherIntensity, "F3")}/outdoor={value.Outdoor}/"
            + $"shadowStrength={Invariant(value.DirectionalShadowStrength, "F3")} "
            + $"perf=cpu-added:{Invariant(value.Performance.IncrementalCpuMillisecondsP50, "F3")}/"
            + $"{Invariant(value.Performance.IncrementalCpuMillisecondsP95, "F3")}/"
            + $"{Invariant(value.Performance.IncrementalCpuMillisecondsP99, "F3")}ms,"
            + $"receiver-cpu-absolute:{Invariant(value.Performance.AbsoluteReceiverCpuMillisecondsP50, "F3")}/"
            + $"{Invariant(value.Performance.AbsoluteReceiverCpuMillisecondsP95, "F3")}/"
            + $"{Invariant(value.Performance.AbsoluteReceiverCpuMillisecondsP99, "F3")}ms,"
            + $"gpu-inclusive:{Invariant(value.Performance.InclusiveGpuMillisecondsP50, "F3")}/"
            + $"{Invariant(value.Performance.InclusiveGpuMillisecondsP95, "F3")}/"
            + $"{Invariant(value.Performance.InclusiveGpuMillisecondsP99, "F3")}ms "
            + $"passes={FormatPasses(value.Passes)} "
            + $"cpuStages={FormatCpuStages(value.CpuStages)} "
            + $"shadowTransformChurn={FormatShadowTransformChurn(value.ShadowTransformChurn)} "
            + $"reason={value.FailureReason ?? "none"}";

    private static string FormatShadowTransformChurn(
        DirectionalShadowTransformChurnDiagnostics value) =>
        $"scene={value.CopiedSceneChanges}[transform={value.UpdateTransformChanges},"
        + $"appearance={value.UpdateAppearanceChanges},sync={value.DynamicSynchronizationChanges};"
        + $"animated={value.ActiveAnimatedStaticChanges},live={value.LiveDynamicRootChanges},"
        + $"equipped={value.EquippedChildChanges}]/"
        + $"casters={value.DedupedCasterSlots}/sceneFallback={value.SceneJournalFullRefresh}/"
        + $"densityBulk={value.DensityBulkRefresh}/batchCopies={value.BatchedProjectionCopyCalls}/"
        + $"matrices={value.ChangedMatrixSlots}/flightCurrent={value.FlightCurrentChangedMatrices}/"
        + $"flightReplay={value.FlightPendingReplayMatrices}/uploaded={value.FlightUploadedMatrices}/"
        + $"ranges={value.FlightUploadRanges}/bytes={value.FlightBytesWritten}/"
        + $"flightFallback={value.FlightFullDynamicFallback}/denseDirect={value.DenseDirectUpload}/"
        + $"denseReplay={value.DenseFlightReplay}/"
        + $"classes=[terrain={value.CasterClasses.TerrainCommands},"
        + $"outdoorStatic={value.CasterClasses.OutdoorStatics},"
        + $"building={value.CasterClasses.Buildings},"
        + $"animated={value.CasterClasses.AnimatedStatics},"
        + $"localPlayer={value.CasterClasses.LocalPlayers},"
        + $"remotePlayer={value.CasterClasses.RemotePlayers},"
        + $"nonPlayerCreature={value.CasterClasses.NonPlayerCreatures},"
        + $"otherLive={value.CasterClasses.OtherLiveDynamics},"
        + $"equipped={value.CasterClasses.EquippedChildren}]";

    private static string FormatPasses(IReadOnlyList<RenderPackPassDiagnostics> passes) =>
        passes.Count == 0
            ? "none"
            : string.Join(
                ',',
                passes.Select(static pass =>
                    $"{pass.PassId}:{Invariant(pass.GpuMilliseconds, "F3")}ms/"
                    + $"{pass.DrawCalls}d/{pass.DispatchCalls}c"));

    private static string FormatCpuStages(
        IReadOnlyList<RenderPackCpuStageDiagnostics> stages) =>
        stages.Count == 0
            ? "none"
            : string.Join(
                ',',
                stages.Select(static stage =>
                    $"{stage.Stage}:{stage.SampleCount}n/"
                    + $"{Invariant(stage.CpuMillisecondsP50, "F3")}/"
                    + $"{Invariant(stage.CpuMillisecondsP95, "F3")}/"
                    + $"{Invariant(stage.CpuMillisecondsP99, "F3")}ms"));

    private static string Invariant(double value, string format) =>
        value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
}
