using System.Diagnostics;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Packs;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.World;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanRenderFrameClearPhase : IRenderFrameClearPhase
{
    private readonly WorldTimeService _worldTime;
    private readonly WeatherSystem _weather;
    private readonly IRenderFramePortalStateSource _portal;
    private readonly ParticleVisibilityController _particleVisibility;
    private readonly VulkanBackbufferClearState _clear;

    public VulkanRenderFrameClearPhase(
        WorldTimeService worldTime,
        WeatherSystem weather,
        IRenderFramePortalStateSource portal,
        ParticleVisibilityController particleVisibility,
        VulkanBackbufferClearState clear)
    {
        _worldTime = worldTime ?? throw new ArgumentNullException(nameof(worldTime));
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));
        _portal = portal ?? throw new ArgumentNullException(nameof(portal));
        _particleVisibility = particleVisibility
            ?? throw new ArgumentNullException(nameof(particleVisibility));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
    }

    public RenderFrameFoundation Clear()
    {
        bool portalViewportVisible = _portal.IsPortalViewportVisible;
        if (portalViewportVisible)
            _particleVisibility.Reset();

        SkyKeyframe sky = _worldTime.CurrentSky;
        AtmosphereSnapshot atmosphere = _weather.Snapshot(in sky);
        Vector4 clear = portalViewportVisible
            ? new Vector4(0f, 0f, 0f, 1f)
            : new Vector4(
                Math.Clamp(atmosphere.FogColor.X, 0f, 1f),
                Math.Clamp(atmosphere.FogColor.Y, 0f, 1f),
                Math.Clamp(atmosphere.FogColor.Z, 0f, 1f),
                1f);

        var foundation = new RenderFrameFoundation(
            portalViewportVisible,
            sky,
            atmosphere);
        _clear.ClearColor = clear;
        _clear.Foundation = foundation;
        return foundation;
    }
}

internal sealed class VulkanWorldScenePhase : IWorldSceneFramePhase
{
    private readonly ICurrentGpuFrameSource _frames;
    private readonly VulkanBackbufferClearState _clear;
    private readonly Func<int> _sampleCount;
    private readonly VulkanWorldPassScope _scope;
    private readonly IWorldSceneFramePhase _world;
    private readonly RenderPackController? _renderPacks;
    private readonly AtmosphericFrameInputState? _atmosphere;
    private readonly Func<RenderPackActivationExtent, RenderPackActivationSnapshot>?
        _applyRenderPackBoundary;
    private readonly RenderSceneShadowRuntime? _renderScene;
    private readonly WbDrawDispatcher? _worldMeshes;
    private readonly TerrainModernRenderer? _terrain;

    public VulkanWorldScenePhase(
        ICurrentGpuFrameSource frames,
        VulkanBackbufferClearState clear,
        Func<int> sampleCount,
        VulkanWorldPassScope scope,
        IWorldSceneFramePhase world,
        RenderPackController? renderPacks = null,
        AtmosphericFrameInputState? atmosphere = null,
        Func<RenderPackActivationExtent, RenderPackActivationSnapshot>?
            applyRenderPackBoundary = null,
        RenderSceneShadowRuntime? renderScene = null,
        WbDrawDispatcher? worldMeshes = null,
        TerrainModernRenderer? terrain = null)
    {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        _sampleCount = sampleCount ?? throw new ArgumentNullException(nameof(sampleCount));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _renderPacks = renderPacks;
        _atmosphere = atmosphere;
        _applyRenderPackBoundary = applyRenderPackBoundary;
        _renderScene = renderScene;
        _worldMeshes = worldMeshes;
        _terrain = terrain;
    }

    public WorldRenderFrameOutcome Render(RenderFrameInput input)
    {
        IGpuFrame frame = _frames.CurrentFrame
            ?? throw new InvalidOperationException(
                "The Vulkan world phase requires an open IGpuFrame (see GpuDeviceFrameLifetime).");

        int samples = _sampleCount();
        if (_renderPacks is not null)
        {
            var extent = new RenderPackActivationExtent(
                input.ViewportWidth,
                input.ViewportHeight,
                samples);
            _ = _applyRenderPackBoundary is not null
                ? _applyRenderPackBoundary(extent)
                : _renderPacks.ApplyAtFrameBoundary(extent);
        }
        if (_renderPacks?.ActiveRuntime is { } active)
        {
            if (active is IDefaultWorldPathRenderPackRuntime)
                return RenderRetail(frame, input);
            if (active is not IAtmosphericWorldGraphRuntime graph
                || _atmosphere is null)
            {
                _renderPacks.OnRuntimeFailure(
                    "The selected pack has no compatible production world graph.");
                return RenderRetail(frame, input);
            }

            IAtmosphericCpuStageProfileRuntime? cpuStageProfile =
                graph as IAtmosphericCpuStageProfileRuntime;
            bool profileCpuStages = cpuStageProfile?.ShouldProfileCpuFrame(frame.Serial) == true;
            long packCpuTicks = 0;
            long targetPreparationTicks = 0;
            IGpuRenderTarget target;
            long packStarted = Stopwatch.GetTimestamp();
            try
            {
                target = graph.PrepareWorldTarget(
                    input.ViewportWidth,
                    input.ViewportHeight,
                    samples);
            }
            catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
            {
                _renderPacks.OnRuntimeFailure(
                    "Atmospheric target creation failed: "
                    + error.GetBaseException().Message);
                return RenderRetail(frame, input);
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - packStarted;
                packCpuTicks += elapsed;
                if (profileCpuStages)
                    targetPreparationTicks = elapsed;
            }

            _atmosphere.BeginFrame(in input, _clear.Foundation);
            PreparedWorldSceneFrame? prepared = null;
            if (graph is IDirectionalShadowWorldGraphRuntime directional)
            {
                if (_world is not IPreparedWorldSceneFramePhase preparedWorld
                    || _renderScene is null
                    || _worldMeshes is null
                    || _terrain is null)
                {
                    _renderPacks.OnRuntimeFailure(
                        "The selected directional-shadow pack has no compatible world preparation seam.");
                    return RenderRetail(frame, input);
                }

                PreparedWorldSceneFrame value;
                try
                {
                    value = preparedWorld.PrepareEnhanced(input);
                }
                catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
                {
                    _renderPacks.OnRuntimeFailure(
                        "Atmospheric world preparation failed: "
                        + error.GetBaseException().Message);
                    return RenderRetail(frame, input);
                }
                prepared = value;
                if (value.ShouldRender)
                {
                    packStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        RenderSceneQuery scene = _renderScene.Query;
                        RenderFrameFoundation preparedFoundation = value.Foundation;
                        WorldRenderFrame preparedWorldFrame = value.World;
                        directional.RenderDirectionalShadows(
                            frame,
                            in preparedFoundation,
                            in preparedWorldFrame,
                            value.ActiveDayGroup,
                            in scene,
                            _worldMeshes,
                            _terrain);
                    }
                    catch (Exception error) when (VulkanRenderFailurePolicy.IsFatal(error))
                    {
                        preparedWorld.CancelPreparedEnhanced(in value);
                        throw;
                    }
                    catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
                    {
                        preparedWorld.CancelPreparedEnhanced(in value);
                        _worldMeshes.CancelDirectionalShadowTransformFrame(frame);
                        _renderPacks.OnRuntimeFailure(
                            "Directional shadow rendering failed: "
                            + error.GetBaseException().Message);
                        return RenderRetail(frame, input);
                    }
                    finally
                    {
                        packCpuTicks += Stopwatch.GetTimestamp() - packStarted;
                    }
                }
            }
            WorldRenderFrameOutcome outcome;
            long receiverCpuTicks = 0;
            try
            {
                using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
                {
                    Name = "atmospheric-world-hdr",
                    Color = new GpuColorAttachment(
                        target,
                        GpuLoadOp.Clear,
                        samples > 1 ? GpuStoreOp.Resolve : GpuStoreOp.Store,
                        _clear.ClearColor),
                    Depth = new GpuDepthAttachment(
                        GpuLoadOp.Clear,
                        GpuStoreOp.Store,
                        1f,
                        0),
                    SampleCount = samples,
                });
                using IDisposable publication = prepared is { ShouldRender: true }
                    ? _scope.PublishPrepared(encoder)
                    : _scope.Publish(encoder);
                if (prepared is { } value)
                {
                    using IDisposable receiverTimer = encoder.BeginTimerScope(
                        RenderPackPerformanceScopeNames.EnhancedWorldReceiver);
                    long receiverStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        outcome = ((IPreparedWorldSceneFramePhase)_world)
                            .RenderPreparedEnhanced(input, in value);
                    }
                    finally
                    {
                        receiverCpuTicks += Stopwatch.GetTimestamp() - receiverStarted;
                    }
                }
                else
                {
                    outcome = _world.Render(input);
                }
            }
            catch (Exception error) when (VulkanRenderFailurePolicy.IsFatal(error))
            {
                if (prepared is { } value
                    && _world is IPreparedWorldSceneFramePhase preparedWorld)
                {
                    preparedWorld.CancelPreparedEnhanced(in value);
                }
                _worldMeshes?.CancelDirectionalShadowTransformFrame(frame);
                throw;
            }
            catch (Exception error)
            {
                if (prepared is { } value
                    && _world is IPreparedWorldSceneFramePhase preparedWorld)
                {
                    preparedWorld.CancelPreparedEnhanced(in value);
                }
                _worldMeshes?.CancelDirectionalShadowTransformFrame(frame);
                _renderPacks?.OnRuntimeFailure(
                    "Atmospheric world rendering failed: "
                    + error.GetBaseException().Message);
                return default;
            }

            try
            {
                AtmosphericFrameInputs atmospheric = _atmosphere.Snapshot();
                packStarted = Stopwatch.GetTimestamp();
                graph.RenderPostProcess(frame, in atmospheric);
                packCpuTicks += Stopwatch.GetTimestamp() - packStarted;
                var observation = new RenderPackFramePerformanceObservation(
                    PackAddedCpuMilliseconds: packCpuTicks * 1000d / Stopwatch.Frequency,
                    StableFrameBoundary: outcome.NormalWorldDrawn,
                    input.ViewportWidth,
                    input.ViewportHeight,
                    samples,
                    AbsoluteEnhancedWorldReceiverCpuMilliseconds:
                        receiverCpuTicks * 1000d / Stopwatch.Frequency);
                bool observationSucceeded = false;
                long observeStarted = profileCpuStages ? Stopwatch.GetTimestamp() : 0L;
                try
                {
                    _renderPacks.ObserveActiveFrame(in observation);
                    observationSucceeded = true;
                }
                catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
                {
                    _renderPacks.OnRuntimeFailure(
                        "Atmospheric performance observation failed: "
                        + error.GetBaseException().Message);
                }
                long observeBookkeepingTicks = profileCpuStages
                    ? Stopwatch.GetTimestamp() - observeStarted
                    : 0L;
                if (observationSucceeded && profileCpuStages)
                {
                    cpuStageProfile!.CompleteCpuProfile(
                        frame.Serial,
                        targetPreparationTicks,
                        packCpuTicks,
                        observeBookkeepingTicks,
                        outcome.NormalWorldDrawn);
                }
                return outcome;
            }
            catch (Exception error) when (!VulkanRenderFailurePolicy.IsFatal(error))
            {
                _renderPacks.OnRuntimeFailure(
                    "Atmospheric post-processing failed: "
                    + error.GetBaseException().Message);
                return outcome;
            }
        }

        return RenderRetail(frame, input);
    }

    private WorldRenderFrameOutcome RenderRetail(
        IGpuFrame frame,
        RenderFrameInput input)
    {
        int samples = _sampleCount();
        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "vk-world",
            Color = new GpuColorAttachment(
                Target: null,
                Load: GpuLoadOp.Clear,
                Store: samples > 1 ? GpuStoreOp.Resolve : GpuStoreOp.Store,
                ClearColor: _clear.ClearColor),
            Depth = new GpuDepthAttachment(
                Load: GpuLoadOp.Clear,
                Store: GpuStoreOp.DontCare,
                ClearDepth: 1f,
                ClearStencil: 0),
            SampleCount = samples,
        });

        using IDisposable publication = _scope.Publish(encoder);
        return _world.Render(input);
    }
}

internal sealed class VulkanBackbufferClearState
{
    internal System.Numerics.Vector4 ClearColor { get; set; } = new(0f, 0f, 0f, 1f);

    internal RenderFrameFoundation Foundation { get; set; }
}

internal sealed class NullRenderFrameGpuMeasurement : IRenderFrameGpuMeasurement
{
    public static NullRenderFrameGpuMeasurement Instance { get; } = new();

    private NullRenderFrameGpuMeasurement()
    {
    }

    public void BeginFrame()
    {
    }

    public void EndFrame()
    {
    }
}
