using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Vfx;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering;

internal readonly record struct AtmosphericFrameBufferBinding(
    IGpuBuffer? Buffer,
    uint OffsetBytes,
    uint SizeBytes)
{
    internal bool IsBound => Buffer is not null;
}

internal readonly record struct DirectionalSunShadowRenderInput(
    DirectionalShadowEnvironmentInput Environment,
    Matrix4x4 CameraView,
    Matrix4x4 CameraProjection,
    DirectionalShadowCasterFrame Casters,
    float CameraNearMeters = 0.1f,
    float CasterDepthPaddingMeters = 48f,
    float ResidentMaximumReachMeters = float.PositiveInfinity,
    bool MeasureGpuTimers = true,
    bool MeasureCpuStages = false,
    AtmosphericFrameBufferBinding AtmosphericFrame = default,
    RetailLandscapeVisibilityFrame PriorLandscapeVisibility = default,
    bool AllowTopologyRebuild = true);

internal readonly record struct DirectionalSunShadowCpuStageTicks(
    long EnvironmentGateTicks,
    long PreparedDrawsAndTransformsTicks,
    long FitAndUniformTicks,
    long LayeredPassRecordingTicks,
    long BookkeepingTicks);

internal readonly record struct DirectionalShadowTransformChurnDiagnostics(
    int CopiedSceneChanges,
    int UpdateTransformChanges,
    int UpdateAppearanceChanges,
    int DynamicSynchronizationChanges,
    int ActiveAnimatedStaticChanges,
    int LiveDynamicRootChanges,
    int EquippedChildChanges,
    int DedupedCasterSlots,
    bool SceneJournalFullRefresh,
    bool DensityBulkRefresh,
    int BatchedProjectionCopyCalls,
    int ChangedMatrixSlots,
    int FlightCurrentChangedMatrices,
    int FlightPendingReplayMatrices,
    int FlightUploadedMatrices,
    int FlightUploadRanges,
    long FlightBytesWritten,
    bool FlightFullDynamicFallback,
    bool DenseDirectUpload,
    bool DenseFlightReplay,
    DirectionalShadowCasterClassDiagnostics CasterClasses = default,
    int ActiveSelectedCasters = 0);

internal readonly record struct DirectionalSunShadowDiagnostics(
    DirectionalShadowGateReason GateReason,
    float Strength,
    int CascadeCount,
    int DrawCalls,
    int WorldOpaqueCommands,
    int WorldAlphaCutoutCommands,
    int TerrainCommands,
    ulong WorldPreparationSequence,
    ulong TerrainPreparationSequence,
    double CpuMilliseconds,
    double LastResolvedGpuMilliseconds,
    bool HasResolvedGpuMeasurement,
    long ResidentDepthBytes,
    DirectionalSunShadowCpuStageTicks CpuStages = default,
    DirectionalShadowTransformChurnDiagnostics TransformChurn = default,
    AuthoredCelestialShadowSourceKind SourceKind =
        AuthoredCelestialShadowSourceKind.None,
    int SourceObjectIndex = -1,
    uint SourceGfxObjId = 0u,
    Vector3 SurfaceToLightDirection = default,
    float LightElevationSin = 0f,
    int ResidentWorldCasters = 0,
    int ActiveWorldCasters = 0,
    int ResidentWorldInstances = 0,
    int ActiveWorldInstances = 0,
    int ResidentWorldCommands = 0,
    int ActiveWorldCommands = 0,
    int ResidentTerrainCommands = 0,
    int ActiveTerrainCommands = 0);

internal static class DirectionalShadowBatchFlags
{
    internal const uint AlphaCutout = 1u << 0;
    internal static uint Encode(DirectionalShadowCasterMaterial material) =>
        material is DirectionalShadowCasterMaterial.AlphaCutout
            ? AlphaCutout
            : 0u;
}

internal sealed class DirectionalSunShadowRenderer : IDirectionalShadowReceiverSource, IDisposable
{
    internal const string TimerPrefix = "directional-shadow-cascade-";
    internal const string MultiviewTimerName = "directional-shadow-multiview";
    internal const uint LowMultiviewMask = 0b11;
    private const int DrawCommandStride = 20;

    private readonly IGpuDevice _device;
    private readonly DirectionalShadowQuality _quality;
    private readonly DirectionalShadowAtmospherePolicy _atmospherePolicy;
    private readonly DirectionalShadowPipelineShaders _pipelineShaders;
    private readonly bool _multiviewCascades;
    private readonly IGpuDirectionalDepthTarget _target;
    private readonly IGpuSampler _sampler;
    private readonly GpuTextureSlot _textureSlot;
    private readonly IGpuPipeline _terrainPipeline;
    private readonly IGpuPipeline _worldOpaquePipeline;
    private readonly IGpuPipeline _worldCutoutPipeline;
    private readonly IGpuPipeline? _terrainMultiviewPipeline;
    private readonly IGpuPipeline? _worldOpaqueMultiviewPipeline;
    private readonly IGpuPipeline? _worldCutoutMultiviewPipeline;
    private readonly DirectionalShadowTransformBufferSet _transformBuffers;
    private readonly DirectionalShadowCascade[] _cascades = new DirectionalShadowCascade[4];
    private DirectionalShadowBatchGpuData[] _batchScratch = [];
    private IGpuBuffer? _worldBatchBuffer;
    private IGpuBuffer? _worldCommandBuffer;
    private IGpuBuffer? _terrainCommandBuffer;
    private ulong _worldGpuBuildSequence;
    private ulong _terrainGpuBuildSequence;
    private DirectionalShadowFrameBinding _currentFrameBinding;
    private bool _disposed;

    internal DirectionalSunShadowRenderer(
        IGpuDevice device,
        DirectionalShadowPreset preset,
        DirectionalShadowAtmospherePolicy? atmospherePolicy = null,
        DirectionalShadowPipelineShaders? pipelineShaders = null,
        bool multiviewCascades = false)
        : this(
            device,
            DirectionalShadowQuality.For(preset),
            atmospherePolicy,
            pipelineShaders,
            multiviewCascades)
    {
    }

    internal DirectionalSunShadowRenderer(
        IGpuDevice device,
        DirectionalShadowQuality quality,
        DirectionalShadowAtmospherePolicy? atmospherePolicy = null,
        DirectionalShadowPipelineShaders? pipelineShaders = null,
        bool multiviewCascades = false)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (quality.CascadeCount is < 1 or > 4
            || quality.MapResolution <= 0
            || !float.IsFinite(quality.MaximumReachMeters)
            || quality.MaximumReachMeters <= 0f
            || quality.PcfRadiusTexels is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quality),
                "Directional-shadow quality must declare 1..4 cascades, a positive "
                + "resolution/reach, and a 0..2 PCF radius.");
        }
        _quality = quality;
        _atmospherePolicy = atmospherePolicy ?? DirectionalShadowAtmospherePolicy.BuiltIn;
        _pipelineShaders = pipelineShaders ?? DirectionalShadowPipelineShaders.Local;
        _multiviewCascades = multiviewCascades;
        if (multiviewCascades && quality.CascadeCount != 2)
            throw new NotSupportedException("The multiview shadow hint requires exactly two Low cascades.");
        if (multiviewCascades && !device.Capabilities.SupportsMultiview)
            throw new NotSupportedException("The selected device does not support multiview shadow cascades.");
        if (multiviewCascades && _pipelineShaders.MultiviewCasters is null)
            throw new NotSupportedException("The pack did not declare all multiview shadow caster variants.");

        IGpuDirectionalDepthTarget? target = null;
        IGpuSampler? sampler = null;
        GpuTextureSlot textureSlot = GpuTextureSlot.Unassigned;
        IGpuPipeline? terrain = null;
        IGpuPipeline? opaque = null;
        IGpuPipeline? cutout = null;
        IGpuPipeline? terrainMultiview = null;
        IGpuPipeline? opaqueMultiview = null;
        IGpuPipeline? cutoutMultiview = null;
        DirectionalShadowTransformBufferSet? transformBuffers = null;
        try
        {
            target = device.CreateDirectionalDepthTarget(
                new GpuDirectionalDepthTargetDescription(
                    $"directional-shadow-{quality.Preset.ToString().ToLowerInvariant()}",
                    _quality.MapResolution,
                    _quality.CascadeCount));
            sampler = device.CreateSampler(GpuSamplerDescription.ShadowNearestClamp);
            textureSlot = device.RegisterTexture(target.DepthTexture, sampler);
            terrain = CreatePipeline(
                device,
                "directional-shadow-terrain",
                _pipelineShaders.TerrainCaster,
                TerrainModernRenderer.TerrainVertexLayout,
                GpuFrontFace.CounterClockwise);
            opaque = CreatePipeline(
                device,
                "directional-shadow-world-opaque",
                _pipelineShaders.WorldOpaqueCaster,
                GpuVertexLayout.WorldMesh,
                GpuFrontFace.Clockwise);
            cutout = CreatePipeline(
                device,
                "directional-shadow-world-cutout",
                _pipelineShaders.WorldAlphaCutoutCaster,
                GpuVertexLayout.WorldMesh,
                GpuFrontFace.Clockwise);
            if (multiviewCascades)
            {
                DirectionalShadowMultiviewPipelineShaders shaders =
                    _pipelineShaders.MultiviewCasters!.Value;
                terrainMultiview = CreatePipeline(device, "directional-shadow-terrain-multiview",
                    shaders.TerrainCaster, TerrainModernRenderer.TerrainVertexLayout,
                    GpuFrontFace.CounterClockwise, LowMultiviewMask);
                opaqueMultiview = CreatePipeline(device, "directional-shadow-world-opaque-multiview",
                    shaders.WorldOpaqueCaster, GpuVertexLayout.WorldMesh,
                    GpuFrontFace.Clockwise, LowMultiviewMask);
                cutoutMultiview = CreatePipeline(device, "directional-shadow-world-cutout-multiview",
                    shaders.WorldAlphaCutoutCaster, GpuVertexLayout.WorldMesh,
                    GpuFrontFace.Clockwise, LowMultiviewMask);
            }
            transformBuffers = new DirectionalShadowTransformBufferSet(device);
        }
        catch
        {
            transformBuffers?.Dispose();
            cutoutMultiview?.Dispose();
            opaqueMultiview?.Dispose();
            terrainMultiview?.Dispose();
            cutout?.Dispose();
            opaque?.Dispose();
            terrain?.Dispose();
            if (textureSlot.IsAssigned)
                device.ReleaseTextureSlot(textureSlot);
            sampler?.Dispose();
            target?.Dispose();
            throw;
        }

        _target = target;
        _sampler = sampler;
        _textureSlot = textureSlot;
        _terrainPipeline = terrain;
        _worldOpaquePipeline = opaque;
        _worldCutoutPipeline = cutout;
        _terrainMultiviewPipeline = terrainMultiview;
        _worldOpaqueMultiviewPipeline = opaqueMultiview;
        _worldCutoutMultiviewPipeline = cutoutMultiview;
        _transformBuffers = transformBuffers;
    }

    internal DirectionalShadowQuality Quality => _quality;

    internal bool MultiviewCascadesEnabled => _multiviewCascades;

    internal static string TimerName(int cascadeIndex) => cascadeIndex switch
    {
        0 => "directional-shadow-cascade-0",
        1 => "directional-shadow-cascade-1",
        2 => "directional-shadow-cascade-2",
        3 => "directional-shadow-cascade-3",
        _ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex)),
    };

    internal IGpuTexture DepthTexture => _target.DepthTexture;

    internal GpuTextureSlot TextureSlot => _textureSlot;

    public DirectionalShadowPipelineShaders PipelineShaders => _pipelineShaders;

    internal DirectionalShadowFrameBinding CurrentFrameBinding => _currentFrameBinding;

    internal long RetainedCommandBufferBytes => checked(
        (_worldBatchBuffer?.SizeBytes ?? 0L)
        + (_worldCommandBuffer?.SizeBytes ?? 0L)
        + (_terrainCommandBuffer?.SizeBytes ?? 0L));

    internal int RetainedCommandBufferCount =>
        (_worldBatchBuffer is null ? 0 : 1)
        + (_worldCommandBuffer is null ? 0 : 1)
        + (_terrainCommandBuffer is null ? 0 : 1);

    internal long RetainedGpuBufferBytes => checked(
        RetainedCommandBufferBytes + _transformBuffers.RetainedGpuBytes);

    internal int RetainedGpuBufferCount => checked(
        RetainedCommandBufferCount + _transformBuffers.BufferCount);

    public bool TryGetCurrentFrameBinding(
        IGpuFrame frame,
        out DirectionalShadowFrameBinding binding)
    {
        ArgumentNullException.ThrowIfNull(frame);
        binding = _currentFrameBinding;
        return !_disposed && binding.IsBindableFor(frame);
    }

    internal DirectionalSunShadowDiagnostics? EvaluateGateAndPublishDisabledBinding(
        IGpuFrame frame,
        in DirectionalSunShadowRenderInput input,
        out DirectionalShadowEnvironmentState environment,
        out long environmentGateTicks)
    {
        _currentFrameBinding = DirectionalShadowFrameBinding.Disabled;
        long cpuStageStarted = input.MeasureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        environment = DirectionalShadowEnvironmentGate.Evaluate(
            input.Environment,
            _atmospherePolicy);
        environmentGateTicks = input.MeasureCpuStages
            ? Stopwatch.GetTimestamp() - cpuStageStarted
            : 0L;
        if (!environment.ShouldRender)
        {
            PublishDisabledReceiverBinding(frame, input.AtmosphericFrame);
            return Disabled(
                in environment,
                new DirectionalSunShadowCpuStageTicks(
                    environmentGateTicks, 0L, 0L, 0L, 0L));
        }
        if (input.ResidentMaximumReachMeters <= input.CameraNearMeters)
        {
            environment = environment with
            {
                Reason = DirectionalShadowGateReason.ResidentWindowUnavailable,
            };
            PublishDisabledReceiverBinding(frame, input.AtmosphericFrame);
            return Disabled(
                in environment,
                new DirectionalSunShadowCpuStageTicks(
                    environmentGateTicks, 0L, 0L, 0L, 0L));
        }
        return null;
    }

    internal DirectionalSunShadowDiagnostics Render(
        IGpuFrame frame,
        in DirectionalSunShadowRenderInput input,
        WbDrawDispatcher world,
        TerrainModernRenderer terrain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        _currentFrameBinding = DirectionalShadowFrameBinding.Disabled;
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(terrain);
        DirectionalSunShadowDiagnostics? gated = EvaluateGateAndPublishDisabledBinding(
            frame,
            in input,
            out DirectionalShadowEnvironmentState environment,
            out long environmentGateTicks);
        if (gated is not null)
            return gated.Value;

        long cpuStageStarted = input.MeasureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        DirectionalShadowPreparedDraws worldDraws =
            world.PrepareDirectionalShadowDraws(
                input.Casters,
                input.AllowTopologyRebuild);
        RetailLandscapeVisibilityFrame priorLandscapeVisibility =
            input.PriorLandscapeVisibility;
        DirectionalShadowTerrainPreparedDraws terrainDraws =
            terrain.PrepareDirectionalShadowDraws(
                in priorLandscapeVisibility);
        DirectionalShadowMeshGeometry? worldGeometry =
            worldDraws.ActiveCommands.IsEmpty
                ? null
                : world.GetDirectionalShadowGeometry();
        DirectionalShadowTerrainGeometry? terrainGeometry =
            terrainDraws.Commands.IsEmpty ? null : terrain.GetDirectionalShadowGeometry();
        uint transformBindingSizeBytes =
            world.ResolveDirectionalShadowTransformBindingSize(
                worldDraws.Transforms.Length,
                worldDraws.Stats.SourceBatches);
        WorldTransformFrameSlice retainedTransforms = _transformBuffers.Publish(
            frame,
            worldDraws.BuildSequence,
            worldDraws.Transforms,
            worldDraws.DynamicTransformSlots,
            worldDraws.AllDynamicTransformSlots,
            worldDraws.LastDynamicTransformRefreshWasDense,
            transformBindingSizeBytes);
        WorldTransformFrameSlice transforms =
            world.BeginDirectionalShadowTransformFrame(
                frame,
                in retainedTransforms);
        DirectionalShadowCasterBuildStats casterStats = input.Casters.Stats;
        DirectionalShadowCasterClassDiagnostics casterClasses =
            CompleteCasterClassDiagnostics(
                in casterStats,
                terrainDraws.ResidentRanges.Length);
        DirectionalShadowTransformPublishStats publishStats =
            _transformBuffers.LastStats;
        var transformChurn = new DirectionalShadowTransformChurnDiagnostics(
            casterStats.CopiedTransformChanges,
            casterStats.UpdateTransformChanges,
            casterStats.UpdateAppearanceChanges,
            casterStats.DynamicSynchronizationChanges,
            casterStats.ActiveAnimatedStaticChanges,
            casterStats.LiveDynamicRootChanges,
            casterStats.EquippedChildChanges,
            casterStats.DedupedChangedCasterSlots,
            casterStats.TransformJournalFullRefresh,
            casterStats.DensityBulkRefresh,
            casterStats.BatchedProjectionCopyCalls,
            worldDraws.LastDynamicTransformRefreshCount,
            publishStats.CurrentChangedMatrices,
            publishStats.PendingReplayMatrices,
            publishStats.DynamicMatricesUpdated,
            publishStats.DynamicRangesUpdated,
            publishStats.BytesWritten,
            publishStats.UsedFullDynamicFallback,
            publishStats.DenseDirectUpload,
            publishStats.DenseFlightReplay,
            casterClasses,
            casterStats.ActiveSelected);
        long preparedDrawsAndTransformsTicks = input.MeasureCpuStages
            ? Stopwatch.GetTimestamp() - cpuStageStarted
            : 0L;
        try
        {
            return RenderPrepared(
                frame,
                environment,
                input.CameraView,
                input.CameraProjection,
                input.CameraNearMeters,
                input.CasterDepthPaddingMeters,
                worldDraws,
                terrainDraws,
                worldGeometry,
                terrainGeometry,
                transforms,
                input.ResidentMaximumReachMeters,
                input.MeasureGpuTimers,
                input.MeasureCpuStages,
                new DirectionalSunShadowCpuStageTicks(
                    environmentGateTicks,
                    preparedDrawsAndTransformsTicks,
                    0L,
                    0L,
                    0L),
                transformChurn,
                input.AtmosphericFrame);
        }
        catch
        {
            world.CancelDirectionalShadowTransformFrame(frame);
            throw;
        }
    }

    internal void PublishDisabledReceiverBinding(
        IGpuFrame frame,
        AtmosphericFrameBufferBinding atmosphericFrame)
    {
        if (!atmosphericFrame.IsBound)
            return;

        var disabledUniforms = new DirectionalShadowUniforms(
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            Vector4.Zero,
            Vector4.Zero,
            Vector4.Zero,
            new UInt4(0u, 0u, 0u, 0u),
            new Vector4(0f, 0f, 1f, 0f));
        GpuRingAllocation allocation = frame.AllocateRing(
            DirectionalShadowUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        MemoryMarshal.Write(allocation.Data, in disabledUniforms);

        _currentFrameBinding = new DirectionalShadowFrameBinding(
            frame.Serial,
            Enabled: false,
            allocation.Buffer,
            allocation.OffsetBytes,
            DirectionalShadowUniforms.SizeInBytes,
            GpuTextureSlot.Unassigned,
            CascadeCount: 0,
            AtmosphericFrame: atmosphericFrame);
    }

    internal static DirectionalShadowCasterClassDiagnostics
        CompleteCasterClassDiagnostics(
            in DirectionalShadowCasterBuildStats casterStats,
            int terrainCommandCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(terrainCommandCount);
        return casterStats.CasterClasses with
        {
            TerrainCommands = terrainCommandCount,
        };
    }

    internal static float ResolveCasterDepthPaddingMeters(
        float configuredPaddingMeters,
        float qualityReachMeters,
        float residentMaximumReachMeters)
    {
        if (!float.IsFinite(configuredPaddingMeters)
            || configuredPaddingMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredPaddingMeters));
        }
        if (!float.IsFinite(qualityReachMeters) || qualityReachMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(qualityReachMeters));
        if (float.IsNaN(residentMaximumReachMeters)
            || residentMaximumReachMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(residentMaximumReachMeters));
        }

        float effectiveReceiverReach = MathF.Min(
            qualityReachMeters,
            residentMaximumReachMeters);
        return MathF.Max(configuredPaddingMeters, effectiveReceiverReach);
    }

    internal DirectionalSunShadowDiagnostics RenderPrepared(
        IGpuFrame frame,
        in DirectionalShadowEnvironmentState environment,
        Matrix4x4 cameraView,
        Matrix4x4 cameraProjection,
        float cameraNearMeters,
        float casterDepthPaddingMeters,
        DirectionalShadowPreparedDraws worldDraws,
        DirectionalShadowTerrainPreparedDraws terrainDraws,
        DirectionalShadowMeshGeometry? worldGeometry,
        DirectionalShadowTerrainGeometry? terrainGeometry,
        WorldTransformFrameSlice worldTransforms,
        float residentMaximumReachMeters = float.PositiveInfinity,
        bool measureGpuTimers = true,
        bool measureCpuStages = false,
        DirectionalSunShadowCpuStageTicks cpuStages = default,
        DirectionalShadowTransformChurnDiagnostics transformChurn = default,
        AtmosphericFrameBufferBinding atmosphericFrame = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        _currentFrameBinding = DirectionalShadowFrameBinding.Disabled;
        ArgumentNullException.ThrowIfNull(worldDraws);
        ArgumentNullException.ThrowIfNull(terrainDraws);
        if (!environment.ShouldRender)
        {
            PublishDisabledReceiverBinding(frame, atmosphericFrame);
            return Disabled(in environment, cpuStages);
        }
        if (!worldDraws.ActiveCommands.IsEmpty && worldGeometry is null)
            throw new ArgumentNullException(nameof(worldGeometry));
        if (!terrainDraws.Commands.IsEmpty && terrainGeometry is null)
            throw new ArgumentNullException(nameof(terrainGeometry));
        if (!worldTransforms.IsValidFor(frame))
            throw new ArgumentException(
                "Shadow transforms must use this frame's shared N.5 allocation.",
                nameof(worldTransforms));

        long started = Stopwatch.GetTimestamp();
        float effectiveCasterDepthPaddingMeters =
            ResolveCasterDepthPaddingMeters(
                casterDepthPaddingMeters,
                _quality.MaximumReachMeters,
                residentMaximumReachMeters);
        var fit = new DirectionalShadowCascadeFitInput(
            cameraView,
            cameraProjection,
            environment.SurfaceToLightDirection,
            _quality,
            cameraNearMeters,
            PracticalSplitLambda: 0.65f,
            effectiveCasterDepthPaddingMeters,
            residentMaximumReachMeters);
        int cascadeCount = DirectionalShadowCascadeFitter.Fit(
            fit,
            _cascades);
        if (cascadeCount == 0)
        {
            DirectionalShadowEnvironmentState unavailable = environment with
            {
                Reason = DirectionalShadowGateReason.ResidentWindowUnavailable,
            };
            PublishDisabledReceiverBinding(frame, atmosphericFrame);
            return Disabled(in unavailable, cpuStages);
        }

        ReadOnlySpan<DirectionalShadowCascade> cascades =
            _cascades.AsSpan(0, cascadeCount);
        DirectionalShadowUniforms uniforms = DirectionalShadowUniforms.Create(
            cascades,
            environment,
            _quality,
            _textureSlot);
        GpuRingAllocation uniformAllocation = frame.AllocateRing(
            DirectionalShadowUniforms.SizeInBytes,
            GpuRingUsage.Uniform);
        MemoryMarshal.Write(uniformAllocation.Data, in uniforms);

        long fitAndUniformFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;
        PreparedGpuUploads uploads = PrepareGpuData(
            worldTransforms,
            worldDraws,
            terrainDraws);
        if (MultiviewCascadesEnabled)
        {
            using IGpuPassEncoder encoder = frame.BeginPass(
                GpuPassDescription.DirectionalDepthMultiview(
                    "directional-shadow-multiview",
                    _target,
                    LowMultiviewMask));
            using IDisposable? timer = measureGpuTimers
                ? encoder.BeginTimerScope(MultiviewTimerName)
                : null;
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformDirectionalShadow,
                uniformAllocation.Buffer,
                uniformAllocation.OffsetBytes,
                DirectionalShadowUniforms.SizeInBytes);
            if (atmosphericFrame.IsBound)
            {
                encoder.BindUniformBuffer(
                    GpuBindingModel.UniformAtmosphericFrame,
                    atmosphericFrame.Buffer!,
                    atmosphericFrame.OffsetBytes,
                    atmosphericFrame.SizeBytes);
            }
            DrawTerrain(encoder, uploads, terrainDraws, terrainGeometry, 0,
                _terrainMultiviewPipeline);
            DrawWorld(encoder, uploads, worldDraws, worldGeometry, 0,
                _worldOpaqueMultiviewPipeline, _worldCutoutMultiviewPipeline);
        }
        else for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
        {
            using IGpuPassEncoder encoder = frame.BeginPass(
                GpuPassDescription.DirectionalDepth(
                    $"directional-shadow-{cascadeIndex}",
                    _target,
                    cascadeIndex));
            using IDisposable? timer = measureGpuTimers
                ? encoder.BeginTimerScope(TimerName(cascadeIndex))
                : null;
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformDirectionalShadow,
                uniformAllocation.Buffer,
                uniformAllocation.OffsetBytes,
                DirectionalShadowUniforms.SizeInBytes);
            if (atmosphericFrame.IsBound)
            {
                encoder.BindUniformBuffer(
                    GpuBindingModel.UniformAtmosphericFrame,
                    atmosphericFrame.Buffer!,
                    atmosphericFrame.OffsetBytes,
                    atmosphericFrame.SizeBytes);
            }

            DrawTerrain(encoder, uploads, terrainDraws, terrainGeometry, cascadeIndex);
            DrawWorld(encoder, uploads, worldDraws, worldGeometry, cascadeIndex);
        }

        long passRecordingFinished = measureCpuStages ? Stopwatch.GetTimestamp() : 0L;

        _currentFrameBinding = new DirectionalShadowFrameBinding(
            frame.Serial,
            Enabled: true,
            uniformAllocation.Buffer,
            uniformAllocation.OffsetBytes,
            DirectionalShadowUniforms.SizeInBytes,
            _textureSlot,
            cascadeCount,
            AtmosphericFrame: atmosphericFrame);

        (bool hasGpu, double gpuMilliseconds) = ResolveGpu(cascadeCount);
        int drawsPerCascade = terrainDraws.Commands.IsEmpty ? 0 : 1;
        drawsPerCascade = checked(
            drawsPerCascade
            + (worldDraws.ActiveCommands.IsEmpty
                ? 0
                : worldDraws.ActiveOpaqueRuns.Length
                    + worldDraws.ActiveAlphaCutoutRuns.Length));
        long finished = Stopwatch.GetTimestamp();
        cpuStages = cpuStages with
        {
            FitAndUniformTicks = measureCpuStages
                ? fitAndUniformFinished - started
                : 0L,
            LayeredPassRecordingTicks = measureCpuStages
                ? passRecordingFinished - fitAndUniformFinished
                : 0L,
            BookkeepingTicks = measureCpuStages
                ? finished - passRecordingFinished
                : 0L,
        };
        return new DirectionalSunShadowDiagnostics(
            DirectionalShadowGateReason.Enabled,
            environment.Strength,
            cascadeCount,
            checked((MultiviewCascadesEnabled ? 1 : cascadeCount) * drawsPerCascade),
            worldDraws.ActiveOpaqueCommandCount,
            worldDraws.ActiveAlphaCutoutCommandCount,
            terrainDraws.Commands.Length,
            worldDraws.BuildSequence,
            terrainDraws.BuildSequence,
            (finished - started) * 1000d / Stopwatch.Frequency,
            gpuMilliseconds,
            hasGpu,
            _quality.ApproximateDepthMapBytes,
            cpuStages,
            transformChurn,
            environment.SourceKind,
            environment.SourceObjectIndex,
            environment.SourceGfxObjId,
            environment.SurfaceToLightDirection,
            environment.LightElevationSin,
            ResidentWorldCasters: inputCasterClassesCount(transformChurn),
            ActiveWorldCasters: transformChurn.ActiveSelectedCasters,
            ResidentWorldInstances: worldDraws.Stats.PreparedInstances,
            ActiveWorldInstances: worldDraws.Stats.ActiveInstances,
            ResidentWorldCommands: worldDraws.Commands.Length,
            ActiveWorldCommands: worldDraws.ActiveCommands.Length,
            ResidentTerrainCommands: terrainDraws.ResidentRanges.Length,
            ActiveTerrainCommands: terrainDraws.Commands.Length);

        static int inputCasterClassesCount(
            DirectionalShadowTransformChurnDiagnostics churn)
        {
            DirectionalShadowCasterClassDiagnostics classes = churn.CasterClasses;
            return checked(
                classes.OutdoorStatics
                + classes.Buildings
                + classes.AnimatedStatics
                + classes.LocalPlayers
                + classes.RemotePlayers
                + classes.NonPlayerCreatures
                + classes.OtherLiveDynamics
                + classes.EquippedChildren);
        }
    }

    private PreparedGpuUploads PrepareGpuData(
        in WorldTransformFrameSlice transforms,
        DirectionalShadowPreparedDraws world,
        DirectionalShadowTerrainPreparedDraws terrain)
    {
        if (_worldGpuBuildSequence != world.ActiveSelectionSequence)
            RebuildWorldGpuData(world);
        if (_terrainGpuBuildSequence != terrain.ActiveSelectionSequence)
            RebuildTerrainGpuData(terrain);

        return new PreparedGpuUploads(
            transforms,
            Slice(_worldBatchBuffer),
            Slice(_worldCommandBuffer),
            Slice(_terrainCommandBuffer));
    }

    private void RebuildWorldGpuData(DirectionalShadowPreparedDraws world)
    {
        IGpuBuffer? batches = null;
        IGpuBuffer? commands = null;
        try
        {
            if (!world.ActiveCommands.IsEmpty)
            {
                EnsureBatchCapacity(world.ActiveBatches.Length);
                for (int i = 0; i < world.ActiveBatches.Length; i++)
                {
                    DirectionalShadowPreparedBatch batch = world.ActiveBatches[i];
                    _batchScratch[i] = new DirectionalShadowBatchGpuData(
                        batch.TextureSlot.Index,
                        0u,
                        batch.TextureLayer,
                        DirectionalShadowBatchFlags.Encode(batch.Material)
                            | batch.FoliageFlags);
                }

                ReadOnlySpan<byte> batchBytes = MemoryMarshal.AsBytes(
                    _batchScratch.AsSpan(0, world.ActiveBatches.Length));
                ReadOnlySpan<byte> commandBytes = MemoryMarshal.AsBytes(
                    world.ActiveCommands);
                batches = CreateRetainedBuffer(
                    $"directional-shadow-world-batches-{world.ActiveSelectionSequence}",
                    batchBytes,
                    GpuBufferUsage.Storage);
                commands = CreateRetainedBuffer(
                    $"directional-shadow-world-commands-{world.ActiveSelectionSequence}",
                    commandBytes,
                    GpuBufferUsage.Indirect);
            }
        }
        catch
        {
            commands?.Dispose();
            batches?.Dispose();
            throw;
        }

        IGpuBuffer? previousBatches = _worldBatchBuffer;
        IGpuBuffer? previousCommands = _worldCommandBuffer;
        _worldBatchBuffer = batches;
        _worldCommandBuffer = commands;
        _worldGpuBuildSequence = world.ActiveSelectionSequence;
        previousCommands?.Dispose();
        previousBatches?.Dispose();
    }

    private void RebuildTerrainGpuData(DirectionalShadowTerrainPreparedDraws terrain)
    {
        IGpuBuffer? commands = null;
        if (!terrain.Commands.IsEmpty)
        {
            commands = CreateRetainedBuffer(
                $"directional-shadow-terrain-commands-{terrain.ActiveSelectionSequence}",
                MemoryMarshal.AsBytes(terrain.Commands),
                GpuBufferUsage.Indirect);
        }

        IGpuBuffer? previous = _terrainCommandBuffer;
        _terrainCommandBuffer = commands;
        _terrainGpuBuildSequence = terrain.ActiveSelectionSequence;
        previous?.Dispose();
    }

    private IGpuBuffer CreateRetainedBuffer(
        string name,
        ReadOnlySpan<byte> contents,
        GpuBufferUsage usage)
    {
        if (contents.IsEmpty)
            throw new ArgumentException("Retained shadow buffers cannot be empty.", nameof(contents));
        IGpuBuffer buffer = _device.CreateBuffer(new GpuBufferDescription(
            name,
            contents.Length,
            usage | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        try
        {
            buffer.Upload(0, contents);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static RetainedGpuBufferSlice Slice(IGpuBuffer? buffer) =>
        new(buffer, 0u, checked((uint)(buffer?.SizeBytes ?? 0L)));

    private void DrawTerrain(
        IGpuPassEncoder encoder,
        in PreparedGpuUploads uploads,
        DirectionalShadowTerrainPreparedDraws draws,
        DirectionalShadowTerrainGeometry? geometry,
        int cascadeIndex,
        IGpuPipeline? pipeline = null)
    {
        if (draws.Commands.IsEmpty)
            return;
        DirectionalShadowTerrainGeometry actual = geometry!.Value;
        encoder.BindPipeline(pipeline ?? _terrainPipeline);
        encoder.BindVertexBuffer(0, actual.VertexBuffer, 0);
        encoder.BindIndexBuffer(actual.IndexBuffer, 0, GpuIndexType.UInt32);
        GpuPushConstants push = PushForCascade(cascadeIndex, 0);
        encoder.SetPushConstants(in push);
        encoder.MultiDrawIndexedIndirect(
            uploads.TerrainCommands.RequireBuffer(),
            uploads.TerrainCommands.OffsetBytes,
            checked((uint)draws.Commands.Length),
            DrawCommandStride);
    }

    private void DrawWorld(
        IGpuPassEncoder encoder,
        in PreparedGpuUploads uploads,
        DirectionalShadowPreparedDraws draws,
        DirectionalShadowMeshGeometry? geometry,
        int cascadeIndex,
        IGpuPipeline? opaquePipeline = null,
        IGpuPipeline? cutoutPipeline = null)
    {
        if (draws.ActiveCommands.IsEmpty)
            return;
        DirectionalShadowMeshGeometry actual = geometry!.Value;
        encoder.BindStorageBuffer(
            GpuBindingModel.StorageInstances,
            uploads.Transforms.Buffer,
            uploads.Transforms.BaseOffsetBytes,
            uploads.Transforms.BindingSizeBytes);
        encoder.BindStorageBuffer(
            GpuBindingModel.StorageBatches,
            uploads.Batches.RequireBuffer(),
            uploads.Batches.OffsetBytes,
            uploads.Batches.SizeBytes);
        DrawWorldRange(
            encoder,
            uploads.WorldCommands,
            draws.ActiveOpaqueRuns,
            cascadeIndex,
            opaquePipeline ?? _worldOpaquePipeline,
            actual);
        DrawWorldRange(
            encoder,
            uploads.WorldCommands,
            draws.ActiveAlphaCutoutRuns,
            cascadeIndex,
            cutoutPipeline ?? _worldCutoutPipeline,
            actual);
    }

    private static void DrawWorldRange(
        IGpuPassEncoder encoder,
        in RetainedGpuBufferSlice commands,
        ReadOnlySpan<DirectionalShadowPreparedRun> runs,
        int cascadeIndex,
        IGpuPipeline pipeline,
        in DirectionalShadowMeshGeometry geometry)
    {
        if (runs.IsEmpty)
            return;
        encoder.BindPipeline(pipeline);
        encoder.BindVertexBuffer(0, geometry.VertexBuffer, 0);
        encoder.BindIndexBuffer(geometry.IndexBuffer, 0, GpuIndexType.UInt16);

        for (int runIndex = 0; runIndex < runs.Length; runIndex++)
        {
            DirectionalShadowPreparedRun run = runs[runIndex];
            ApplyCull(encoder, run.CullMode);
            GpuPushConstants push = PushForCascade(cascadeIndex, run.StartCommand);
            encoder.SetPushConstants(in push);
            encoder.MultiDrawIndexedIndirect(
                commands.RequireBuffer(),
                commands.OffsetBytes + checked((uint)(run.StartCommand * DrawCommandStride)),
                checked((uint)run.CommandCount),
                DrawCommandStride);
        }
    }

    private static GpuPushConstants PushForCascade(int cascadeIndex, int drawIdOffset)
    {
        GpuPushConstants push = GpuPushConstants.Default;
        push.RenderPass = cascadeIndex;
        push.DrawIdOffset = drawIdOffset;
        return push;
    }

    private static void ApplyCull(IGpuPassEncoder encoder, CullMode mode)
    {
        encoder.SetFrontFace(GpuFrontFace.Clockwise);
        encoder.SetCullMode(mode switch
        {
            CullMode.None => GpuCullMode.None,
            CullMode.Clockwise => GpuCullMode.Front,
            _ => GpuCullMode.Back,
        });
    }

    private static IGpuPipeline CreatePipeline(
        IGpuDevice device,
        string name,
        GpuShaderSet shaders,
        GpuVertexLayout layout,
        GpuFrontFace frontFace,
        uint viewMask = 0) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = shaders,
            VertexLayout = layout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = new GpuDepthState(true, true, WorldDepthContract.WorldCompare),
            Cull = GpuCullMode.Back,
            FrontFace = frontFace,
            AlphaToCoverage = false,
            ColorWrite = false,
            HasColorAttachment = false,
            AllowColorFormatVariants = false,
            SampleCount = 1,
            UsesRenderPackShaderAbi = true,
            ViewMask = viewMask,
        });

    private (bool HasMeasurement, double Milliseconds) ResolveGpu(int cascadeCount)
    {
        if (MultiviewCascadesEnabled)
            return _device.Timers.TryResolve(MultiviewTimerName, out double measured)
                ? (true, measured)
                : (false, 0d);
        double total = 0d;
        for (int i = 0; i < cascadeCount; i++)
        {
            if (!_device.Timers.TryResolve(TimerName(i), out double milliseconds))
                return (false, 0d);
            total += milliseconds;
        }
        return (true, total);
    }

    private DirectionalSunShadowDiagnostics Disabled(
        in DirectionalShadowEnvironmentState environment,
        DirectionalSunShadowCpuStageTicks cpuStages = default) =>
        new(
            environment.Reason,
            0f,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0d,
            0d,
            false,
            _quality.ApproximateDepthMapBytes,
            cpuStages,
            SourceKind: environment.SourceKind,
            SourceObjectIndex: environment.SourceObjectIndex,
            SourceGfxObjId: environment.SourceGfxObjId,
            SurfaceToLightDirection: environment.SurfaceToLightDirection,
            LightElevationSin: environment.LightElevationSin);

    private void EnsureBatchCapacity(int required)
    {
        if (_batchScratch.Length >= required)
            return;
        int capacity = _batchScratch.Length == 0 ? 16 : _batchScratch.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref _batchScratch, capacity);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _currentFrameBinding = DirectionalShadowFrameBinding.Disabled;
        _worldCutoutPipeline.Dispose();
        _worldCutoutMultiviewPipeline?.Dispose();
        _worldOpaqueMultiviewPipeline?.Dispose();
        _terrainMultiviewPipeline?.Dispose();
        _worldOpaquePipeline.Dispose();
        _terrainPipeline.Dispose();
        _terrainCommandBuffer?.Dispose();
        _worldCommandBuffer?.Dispose();
        _worldBatchBuffer?.Dispose();
        _transformBuffers.Dispose();
        _device.ReleaseTextureSlot(_textureSlot);
        _sampler.Dispose();
        _target.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct DirectionalShadowBatchGpuData(
        uint TextureIndex,
        uint Reserved,
        uint TextureLayer,
        uint Flags);

    private readonly record struct RetainedGpuBufferSlice(
        IGpuBuffer? Buffer,
        uint OffsetBytes,
        uint SizeBytes)
    {
        internal IGpuBuffer RequireBuffer() => Buffer
            ?? throw new InvalidOperationException(
                "A non-empty directional-shadow draw has no retained GPU buffer.");
    }

    private readonly record struct PreparedGpuUploads(
        WorldTransformFrameSlice transforms,
        RetainedGpuBufferSlice batches,
        RetainedGpuBufferSlice worldCommands,
        RetainedGpuBufferSlice terrainCommands)
    {
        internal WorldTransformFrameSlice Transforms { get; } = transforms;
        internal RetainedGpuBufferSlice Batches { get; } = batches;
        internal RetainedGpuBufferSlice WorldCommands { get; } = worldCommands;
        internal RetainedGpuBufferSlice TerrainCommands { get; } = terrainCommands;
    }
}
