using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Runtime;
using AcDream.App.Settings;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.Core.Combat;
using AcDream.Core.Lighting;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace AcDream.App.Composition;

internal sealed record FrameRootDependencies(
    RuntimeOptions Options,
    GameRuntime Runtime,
    GameWindowGraphics Graphics,
    IWindow Window,
    IInputContext Input,
    WorldTimeService WorldTime,
    WeatherSystem Weather,
    LightManager Lighting,
    WorldEnvironmentController WorldEnvironment,
    PhysicsEngine PhysicsEngine,
    CellVisibility CellVisibility,
    LocalPlayerModeState PlayerMode,
    LocalPlayerIdentityState PlayerIdentity,
    ChaseCameraInputState ChaseCameraInput,
    LiveWorldOriginState WorldOrigin,
    ParticleVisibilityController ParticleVisibility,
    EntityEffectPoseRegistry EffectPoses,
    WorldRenderRangeState RenderRange,
    RuntimeSettingsController Settings,
    BuildingDegradeController BuildingDegrades,
    DisplayFramePacingController DisplayFramePacing,
    WorldSceneDebugState WorldSceneDebugState,
    RetailAlphaQueue RetailAlphaQueue,
    FrameProfiler FrameProfiler,
    bool FrameDiagnosticsEnabled,
    IRenderFrameDiagnosticLog RenderDiagnosticLog,
    DebugVmRenderFactsPublisher DebugVmRenderFacts,
    IInputCaptureSource InputCapture,
    DispatcherCameraInputSource CameraInput,
    LiveEntityAnimationRuntimeView<LiveEntityAnimationState> Animations,
    UpdateFrameClock UpdateClock,
    GameFrameGraphSlot FrameGraphs,
    Action<string> Log,
    AcDream.App.Rendering.Packs.DeferredRenderPackDiagnosticsSource?
        RenderPackDiagnostics = null)
{
    public RuntimeLocalPlayerMovementState PlayerController =>
        Runtime.MovementOwner;

    public SelectionState Selection => Runtime.ActionOwner.Selection;

    public CombatState Combat => Runtime.ActionOwner.Combat;
}

internal sealed record FrameRootResult(
    UpdateFrameOrchestrator Update,
    RenderFrameOrchestrator Render,
    FrameRootRuntimeBindings RuntimeBindings,
    IDisposable FrameGraphPublication,
    LiveSessionHost SessionHost,
    CurrentGameRuntimeAdapter GameRuntime);

internal interface IGameWindowFrameRootPublication
{
    void PublishFrameRoots(FrameRootResult result);
}

internal enum FrameRootCompositionPoint
{
    RenderResourcesCreated,
    WorldRendererCreated,
    LifecycleAutomationBound,
    RenderRootCreated,
    UpdateRootCreated,
    FrameGraphPublished,
    ResultPublished,
}

internal sealed class FrameRootRuntimeBindings : IDisposable
{
    private readonly List<(string Name, IDisposable Binding)> _bindings = [];
    private bool _deactivationStarted;

    public void Adopt(string name, IDisposable binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(_deactivationStarted, this);
        _bindings.Add((name, binding));
    }

    public void Dispose()
    {
        if (_deactivationStarted && _bindings.Count == 0)
            return;
        _deactivationStarted = true;

        List<Exception>? failures = null;
        for (int i = _bindings.Count - 1; i >= 0; i--)
        {
            (string name, IDisposable binding) = _bindings[i];
            try
            {
                binding.Dispose();
                _bindings.RemoveAt(i);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(new InvalidOperationException(
                    $"Frame-root binding '{name}' did not detach.",
                    failure));
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Frame-root binding cleanup remains incomplete.",
                failures);
        }
    }
}

internal sealed class FrameRootCompositionPhase
    : IFrameRootCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, IInputContext>,
        HostInputCameraResult,
        ContentEffectsAudioResult,
        SettingsDevToolsResult,
        WorldRenderResult,
        InteractionRetainedUiResult,
        LivePresentationResult,
        SessionPlayerResult,
        FrameRootResult>
{
    private readonly FrameRootDependencies _dependencies;
    private readonly IGameWindowFrameRootPublication _publication;
    private readonly Action<FrameRootCompositionPoint>? _faultInjection;

    public FrameRootCompositionPhase(
        FrameRootDependencies dependencies,
        IGameWindowFrameRootPublication publication,
        Action<FrameRootCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _faultInjection = faultInjection;
    }

    public FrameRootResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, IInputContext> platform,
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        LivePresentationResult live,
        SessionPlayerResult session)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(_dependencies.Graphics, platform.Graphics)
            || !ReferenceEquals(_dependencies.Input, platform.Input))
        {
            throw new InvalidOperationException(
                "Frame-root dependencies do not match the ordered platform result.");
        }

        var scope = new CompositionAcquisitionScope();
        FrameRootRuntimeBindings? bindings = null;
        bool bindingsOwnedByScope = false;
        try
        {
            FrameRootResult result = ComposeCore(
                host,
                content,
                settings,
                world,
                interaction,
                live,
                session,
                scope,
                ref bindings,
                ref bindingsOwnedByScope);
            scope.Complete();
            return result;
        }
        catch (Exception failure)
        {
            if (bindings is not null && !bindingsOwnedByScope)
            {
                scope.Own(
                    "frame-root runtime bindings",
                    bindings,
                    static value => value.Dispose());
            }

            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private FrameRootResult ComposeCore(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        LivePresentationResult live,
        SessionPlayerResult session,
        CompositionAcquisitionScope scope,
        ref FrameRootRuntimeBindings? bindings,
        ref bool bindingsOwnedByScope)
    {
        FrameRootDependencies d = _dependencies;
        bindings = new FrameRootRuntimeBindings();
        WorldRenderFoundation foundation = world.Foundation;
        var renderLoginState = new RenderLoginStateSource(
            d.Options.LiveMode,
            d.PlayerMode);
        var teleportRenderState =
            new LocalPlayerTeleportRenderStateSource(
                session.LocalTeleport,
                renderLoginState);
        var vulkanClear = new AcDream.App.Rendering.Gpu.Vk.VulkanBackbufferClearState();
        var renderFrameLivePreparation =
            new RuntimeRenderFrameLivePreparation(
                foundation.TextureCache,
                foundation.MeshAdapter,
                session.WorldReveal,
                teleportRenderState,
                renderLoginState,
                new LiveLoginRevealCellSource(
                    live.LiveEntities,
                    d.PlayerIdentity),
                live.ParticleRenderer,
                d.FrameProfiler,
                d.FrameDiagnosticsEnabled);
        IRenderFrameClearPhase clearPhase =
            new AcDream.App.Rendering.Gpu.Vk.VulkanRenderFrameClearPhase(
                d.WorldTime,
                d.Weather,
                teleportRenderState,
                d.ParticleVisibility,
                vulkanClear);
        var renderFrameResources = new RenderFrameResourceController(
            host.FrameSlots,
            new RuntimeRenderFrameBeginResources(
                foundation.TextureCache,
                live.DrawDispatcher,
                live.EnvCellRenderer,
                live.PortalDepthMask,
                live.ClipFrame,
                foundation.Terrain,
                foundation.SceneLighting),
            clearPhase,
            renderFrameLivePreparation);
        Fault(FrameRootCompositionPoint.RenderResourcesCreated);

        AcDream.App.Rendering.Packs.RenderPackController? renderPackController = null;
        AcDream.App.Rendering.Packs.RenderPackSelectionBinding? renderPackSelection = null;
        AcDream.App.Rendering.Packs.AtmosphericFrameInputState? atmosphericInputs = null;
        if (settings.RenderPacks is { } renderPackCatalog)
        {
            renderPackController = new AcDream.App.Rendering.Packs.RenderPackController(
                renderPackCatalog.Snapshot,
                new AcDream.App.Rendering.Packs.AtmosphericRenderPackRuntimeFactory(
                    host.GpuDevice,
                    d.Options.SkyAnimationPhaseSeconds),
                new AcDream.App.Rendering.Packs.RenderPackReceiverPipelineCoordinator(
                    foundation.Terrain!,
                    live.DrawDispatcher!),
                AcDream.App.Rendering.Packs.ThreadPoolRenderPackPreparationScheduler.Instance,
                renderPackCatalog);
            bindings.Adopt("render-pack controller", renderPackController);
            renderPackSelection = new AcDream.App.Rendering.Packs.RenderPackSelectionBinding(
                d.Settings,
                renderPackController,
                d.Log);
            bindings.Adopt("render-pack selection", renderPackSelection);
            if (d.RenderPackDiagnostics is { } renderPackDiagnostics)
            {
                bindings.Adopt(
                    "render-pack diagnostics",
                    renderPackDiagnostics.BindOwned(renderPackController));
            }
            atmosphericInputs = new AcDream.App.Rendering.Packs.AtmosphericFrameInputState();

            if (live.SkyRenderer is { } skyForNightSky)
            {
                var nightSkyController = renderPackController;
                skyForNightSky.EnhancedNightSkyActive = () =>
                    nightSkyController.ActiveRuntime?.Descriptor.Id
                        == AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.Id;
            }
        }

        var renderWeatherFrame = new RenderWeatherFrameController(
            d.WorldTime,
            d.Weather);
        var skyPesFrame = new SkyPesFrameController(
            content.ScriptRunner,
            content.ParticleSink,
            d.EffectPoses,
            live.EntityEffects,
            d.Log,
            content.Audio is { } audio
                ? audio.Engine.StopAllForOwner
                : null);
        IWorldSceneFramePhase? worldSceneRenderer = null;
        CurrentRenderSceneOracle? currentRenderSceneOracle = null;
        RenderSceneShadowComparisonController? renderSceneShadowComparison = null;
        {
            WorldRenderDiagnostics worldRenderDiagnostics =
                new(d.RenderDiagnosticLog);
            IRenderFrameGlState worldFrameGlState = NullRenderFrameGlState.Instance;
            IWorldPassScope? worldPassScope = d.Graphics.WorldPassScope;
            IWorldPassSurface worldPassSurface = new RhiWorldPassSurface(
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope."),
                host.GpuFrameLifetime,
                live.ClipFrame);
            var worldFrameEnvironment =
                new RuntimeWorldFrameEnvironmentPreparation(
                    d.Options,
                    d.WorldTime,
                    d.Lighting,
                    live.DrawDispatcher!,
                    live.EnvCellRenderer!,
                    foundation.SceneLighting!,
                    d.RenderRange,
                    skyPesFrame,
                    persistentDaylight: () =>
                        d.Runtime.CharacterOwner.Options.GetOptionBit(
                            CharacterOptionId.PersistentAtDay));
            var worldFrameCamera = new RuntimeWorldFrameCameraSource(
                host.CameraController,
                session.LocalTeleport.ApplyViewPlane);
            var worldFrameRoots = new RuntimeWorldFrameRootSource(
                d.PhysicsEngine,
                d.CellVisibility,
                d.PlayerMode,
                d.ChaseCameraInput,
                d.PlayerController,
                d.WorldOrigin);
            var skyPesActivationGate = new RuntimeSkyPesActivationGate(
                skyPesFrame,
                worldFrameCamera,
                worldFrameRoots);
            bindings.Adopt(
                "sky presentation effects activation",
                session.LiveObjectFrame.BindSkyPesActivationGateOwned(
                    skyPesActivationGate));
            var worldRenderFrameBuilder = new WorldRenderFrameBuilder(
                worldFrameCamera,
                new RuntimeWorldFrameVisibilityPreparation(
                    live.SelectionScene,
                    d.ParticleVisibility,
                    foundation.Terrain!,
                    session.WorldReveal,
                    live.EnvCellFrustum),
                new RuntimeWorldFrameSettingsPreview(
                    d.Settings,
                    content.Audio?.Engine,
                    host.CameraController,
                    d.DisplayFramePacing),
                worldFrameRoots,
                worldFrameEnvironment,
                new RuntimeWorldFrameAnimatedEntitySource(
                    d.Animations,
                    live.StaticAnimationScheduler,
                    live.EquippedChildren),
                new RuntimeWorldFrameBuildingSource(
                    live.LandblockPipeline,
                    d.CellVisibility),
                new RuntimeDirectionalShadowCellMembership(
                    d.PhysicsEngine.ShadowObjects));
            var terrainDrawDiagnostics = new TerrainDrawDiagnosticsController(
                d.FrameDiagnosticsEnabled,
                worldRenderDiagnostics,
                new RuntimeFramePipelineDiagnosticFactsSource(
                    foundation.Terrain!,
                    live.LandblockPipeline,
                    renderFrameLivePreparation,
                    live.DrawDispatcher!,
                    session.Streaming,
                    live.LiveEntities,
                    live.WorldState),
                d.RenderDiagnosticLog);
            var retailPViewCells = new RetailPViewCellSource(d.CellVisibility);
            var retailPViewPassExecutor = new RetailPViewPassExecutor(
                worldPassSurface,
                worldFrameGlState,
                live.ClipFrame,
                foundation.Terrain,
                live.EnvCellRenderer!,
                live.DrawDispatcher!,
                live.SkyRenderer,
                content.ParticleSystem,
                live.ParticleRenderer,
                live.PortalDepthMask,
                d.RetailAlphaQueue,
                terrainDrawDiagnostics);
            var worldSceneDiagnostics = new WorldSceneDiagnosticsController(
                new RuntimeWorldScenePViewDiagnosticSource(
                    d.PlayerController,
                    d.PhysicsEngine,
                    d.CellVisibility),
                d.WorldSceneDebugState,
                null,
                d.PhysicsEngine,
                d.PlayerMode,
                d.PlayerController,
                d.DebugVmRenderFacts,
                debugVmConsumerActive: false);
            var worldScenePasses = new WorldScenePassExecutor(
                worldPassSurface,
                worldFrameGlState,
                live.ClipFrame,
                live.DrawDispatcher!,
                live.EnvCellRenderer!,
                foundation.Terrain,
                terrainDrawDiagnostics,
                live.SkyRenderer,
                content.ParticleSystem,
                live.ParticleRenderer);
            // The retained scene is the walk's production object source. The
            // old dispatcher/selection observer remains detached.
            live.DrawDispatcher!.SetCurrentRenderSceneObserver(null);
            live.SelectionScene.SetCurrentRenderSceneObserver(null);
            worldSceneRenderer = new WorldSceneRenderer(
                renderFrameResources,
                renderLoginState,
                d.WorldEnvironment,
                worldRenderFrameBuilder,
                new RuntimeWorldSceneEntitySource(live.WorldState),
                live.SelectionScene,
                d.RetailAlphaQueue,
                d.ParticleVisibility,
                new WorldScenePViewRenderer(
                    new RetailPViewRenderer(
                        live.RenderSceneShadow
                            ?? throw new InvalidOperationException(
                                "The retail frame walk requires the retained render scene."),
                        live.LandblockPipeline.RenderPublisher?.WalkBuildings
                            ?? throw new InvalidOperationException(
                                "The retail frame walk requires the building registry."),
                        live.LandblockPipeline.RenderPublisher?.WalkLandscape
                            ?? throw new InvalidOperationException(
                                "The retail frame walk requires the landscape registry."),
                        d.CellVisibility,
                        d.PhysicsEngine.ShadowObjects,
                        d.BuildingDegrades),
                    retailPViewPassExecutor),
                retailPViewCells,
                worldScenePasses,
                d.RenderRange,
                worldSceneDiagnostics,
                live.WorldAvailability,
                atmosphericInputs);
            worldSceneRenderer =
                new AcDream.App.Rendering.Gpu.Vk.VulkanWorldScenePhase(
                    host.GpuFrameLifetime,
                    vulkanClear,
                    () => d.Graphics.Vulkan?.SampleCount ?? 1,
                    (d.Graphics as VulkanGameWindowGraphics)?.WorldPassScopeCore
                        ?? throw new InvalidOperationException(
                            "The Vulkan world phase requires the Vulkan graphics handle."),
                    worldSceneRenderer,
                    renderPackController,
                    atmosphericInputs,
                    renderPackSelection is null
                        ? null
                        : renderPackSelection.ApplyAtFrameBoundary,
                    live.RenderSceneShadow,
                    live.DrawDispatcher,
                    foundation.Terrain);
        }
        Fault(FrameRootCompositionPoint.WorldRendererCreated);
        WorldLifecycleAutomationController? lifecycleAutomation = null;
        if (interaction.RetainedUi?.Screenshots is { } screenshots
            && d.Options.AutomationArtifactDirectory is { } artifactDirectory)
        {
            AcDream.UI.Abstractions.Panels.Settings.RenderPackSelectionSettings?
                automationLastEnhancedSelection = null;

            (bool Succeeded, string Error) SaveAutomationRenderPackSelection(
                AcDream.UI.Abstractions.Panels.Settings.RenderPackSelectionSettings selection)
            {
                d.Settings.SaveDisplay(d.Settings.Display with
                {
                    RenderPack = selection,
                });
                return d.Settings.Display.RenderPack == selection
                    ? (true, string.Empty)
                    : (false, $"render-pack selection '{selection.PresetId}' was not persisted");
            }

            (bool Succeeded, string Error) SelectAutomationRenderPack(string preset)
            {
                var selection = string.Equals(
                    preset,
                    "retail",
                    StringComparison.Ordinal)
                    ? AcDream.UI.Abstractions.Panels.Settings
                        .RenderPackSelectionSettings.Retail
                    : new AcDream.UI.Abstractions.Panels.Settings
                        .RenderPackSelectionSettings(
                            AcDream.App.Rendering.Packs
                                .BuiltInAtmosphericRenderPack.Id,
                            "1.0.0",
                            preset);
                return SaveAutomationRenderPackSelection(selection);
            }

            (bool Succeeded, string Error) DisableAutomationRenderPack()
            {
                var current = d.Settings.Display.RenderPack;
                if (!current.IsRetail)
                    automationLastEnhancedSelection = current;
                return SaveAutomationRenderPackSelection(
                    AcDream.UI.Abstractions.Panels.Settings
                        .RenderPackSelectionSettings.Retail);
            }

            (bool Succeeded, string Error) ReenableAutomationRenderPack()
            {
                return automationLastEnhancedSelection is { } selection
                    ? SaveAutomationRenderPackSelection(selection)
                    : (false, "render-pack re-enable requires a prior enhanced selection");
            }

            var resourceSnapshots =
                new WorldLifecycleResourceSnapshotSource(
                    live.WorldState,
                    d.Animations,
                    live.FrameDiagnostics,
                    live.LiveEntities,
                    session.Streaming,
                    content.ParticleSystem,
                    content.ParticleSink,
                    live.EntityEffects,
                    live.Lights,
                    content.ScriptRunner,
                    foundation.MeshAdapter,
                    foundation.TextureCache,
                    live.DrawDispatcher,
                    d.FrameProfiler,
                    content.Dats,
                    foundation.Residency,
                    d.PhysicsEngine.DataCache
                        ?? throw new InvalidOperationException(
                            "Lifecycle automation requires the canonical physics cache."),
                    currentRenderSceneOracle,
                    renderSceneShadowComparison);
            lifecycleAutomation =
                new WorldLifecycleAutomationController(
                    () => session.WorldReveal.Snapshot,
                    () => d.WorldEnvironment.Runtime.Ownership,
                    () => live.WorldTransit.Ownership,
                    () => session.WorldReveal.PortalMaterializationCount,
                    resourceSnapshots.Capture,
                    screenshots,
                    artifactDirectory,
                    message => d.Log("[UI-PROBE] " + message),
                    () => renderPackController?.MinimumPerformanceSampleCount ?? 0,
                    () =>
                    {
                        if (renderPackController is null)
                        {
                            return (
                                false,
                                "render-pack performance automation is unavailable");
                        }
                        bool reset = renderPackController.TryResetPerformanceEvidence(
                            out string error);
                        return (reset, error);
                    },
                    () => renderPackController?.Snapshot.State ==
                        AcDream.App.Rendering.Packs.RenderPackActivationState.FailedToRetail,
                    getRenderPackStatus: () =>
                    {
                        AcDream.App.Rendering.Packs.RenderPackActivationSnapshot snapshot =
                            renderPackController?.Snapshot
                            ?? new AcDream.App.Rendering.Packs.RenderPackActivationSnapshot(
                                AcDream.App.Rendering.Packs.RenderPackActivationState.Retail,
                                AcDream.UI.Abstractions.Panels.Settings
                                    .RenderPackSelectionSettings.Retail,
                                ActivePackDisplayName: null,
                                Reason: null,
                                ActivationGeneration: 0);
                        var state = snapshot.State switch
                        {
                            AcDream.App.Rendering.Packs.RenderPackActivationState.Retail =>
                                AcDream.App.UI.Testing
                                    .RetailUiAutomationRenderPackState.Retail,
                            AcDream.App.Rendering.Packs.RenderPackActivationState.CandidatePending =>
                                AcDream.App.UI.Testing
                                    .RetailUiAutomationRenderPackState.CandidatePending,
                            AcDream.App.Rendering.Packs.RenderPackActivationState.Active =>
                                AcDream.App.UI.Testing
                                    .RetailUiAutomationRenderPackState.Active,
                            AcDream.App.Rendering.Packs.RenderPackActivationState.FailedToRetail =>
                                AcDream.App.UI.Testing
                                    .RetailUiAutomationRenderPackState.FailedToRetail,
                            _ => throw new ArgumentOutOfRangeException(),
                        };
                        return new AcDream.App.UI.Testing
                            .RetailUiAutomationRenderPackStatus(
                                state,
                                snapshot.Selection.PackId,
                                snapshot.Selection.PresetId,
                                snapshot.ActivationGeneration,
                                snapshot.Reason);
                    },
                    selectRenderPack: SelectAutomationRenderPack,
                    disableRenderPack: DisableAutomationRenderPack,
                    reenableRenderPack: ReenableAutomationRenderPack,
                    getFramebufferSize: () =>
                    {
                        var size = d.Window.FramebufferSize;
                        return (size.X, size.Y);
                    },
                    resizeFramebuffer: (width, height) =>
                    {
                        if (d.Settings.Display.Fullscreen)
                        {
                            return (
                                false,
                                "automation framebuffer resize requires windowed mode");
                        }
                        string resolution = $"{width}x{height}";
                        d.Settings.SaveDisplay(d.Settings.Display with
                        {
                            Resolution = resolution,
                        });
                        return string.Equals(
                            d.Settings.Display.Resolution,
                            resolution,
                            StringComparison.Ordinal)
                            ? (true, string.Empty)
                            : (false, $"framebuffer resize '{resolution}' was not persisted");
                    },
                    requestClientClose: d.Window.Close);
            bindings.Adopt(
                "world lifecycle automation owner",
                lifecycleAutomation);
            bindings.Adopt(
                "world lifecycle automation binding",
                interaction.LateBindings.Automation.Bind(
                    lifecycleAutomation));
        }
        else if (interaction.RetainedUi is not null)
        {
            bindings.Adopt(
                "world reveal facts automation binding",
                interaction.LateBindings.Automation.Bind(
                    new WorldRevealFactsAutomationRuntime(
                        () => session.WorldReveal.Snapshot,
                        () => session.WorldReveal.PortalMaterializationCount)));
        }
        Fault(FrameRootCompositionPoint.LifecycleAutomationBound);

        IRetainedGameplayUiFrame? retainedGameplayUi =
            d.Options.RetailUi && interaction.RetainedUi is { } retained
                ? new RetainedGameplayUiFrame(retained.Runtime, d.Input)
                : null;
        IPrivateFrameScreenshot? privateScreenshot =
            interaction.RetainedUi?.Screenshots is { } frameScreenshots
                ? new PrivateFrameScreenshot(frameScreenshots)
                : null;
        var privatePresentation = new PrivatePresentationRenderer(
            new LocalPlayerPortalViewport(
                session.LocalTeleport,
                host.CameraController),
            renderFrameResources,
            new PrivateEntityViewportFrameGroup(
                live.PaperdollPresenter,
                live.CreatureAppraisalPresenter,
                live.ChargenPreviewController,
                live.SummaryPreviewController),
            retainedGameplayUi,
            devTools: null);
        var framePreparation = new RenderFramePreparationController(
            renderFrameResources,
            devTools: null,
            renderWeatherFrame,
            live.PaperdollPresenter);
        IRenderFramePostDiagnosticsPhase postDiagnostics =
            renderSceneShadowComparison is not null
            && lifecycleAutomation is not null
                ? new SerialRenderFramePostDiagnosticsPhase(
                    renderSceneShadowComparison,
                    lifecycleAutomation)
                : (IRenderFramePostDiagnosticsPhase?)lifecycleAutomation
                    ?? NullRenderFramePostDiagnosticsPhase.Instance;
        if (RenderPresentationDiagnostics.ProbeLoginFrames)
        {
            var loginFrameProbe = new LoginPresentationFrameProbe(
                () => session.LocalTeleport.IsPortalViewportVisible,
                renderLoginState,
                d.Log);
            postDiagnostics =
                postDiagnostics is NullRenderFramePostDiagnosticsPhase
                    ? loginFrameProbe
                    : new SerialRenderFramePostDiagnosticsPhase(
                        postDiagnostics,
                        loginFrameProbe);
        }
        var renderFrame = new RenderFrameOrchestrator(
            host.GpuFrameLifetime,
            d.Graphics.Vulkan is { } vulkanGraphics
                ? new AcDream.App.Rendering.Gpu.Vk.VulkanFrameGpuMeasurement(
                    d.FrameProfiler,
                    vulkanGraphics.Device)
                : AcDream.App.Rendering.Gpu.Vk.NullRenderFrameGpuMeasurement.Instance,
            framePreparation,
            worldSceneRenderer,
            privatePresentation,
            live.FrameDiagnostics,
            postDiagnostics,
            NullRenderFrameFailureRecovery.Instance,
            d.BuildingDegrades,
            privateScreenshot);
        Fault(FrameRootCompositionPoint.RenderRootCreated);

        var liveFrameCoordinator = new RetailLiveFrameCoordinator(
            session.LiveObjectFrame,
            live.WorldState,
            session.SessionHost,
            session.LocalPlayerFrame,
            session.LiveSpatialReconciler,
            live.WorldAvailability,
            live.RenderSceneShadow?.LiveProjections,
            session.PlacementProjectionRetry);
        var cameraFrame = new CameraFrameController(
            host.CameraController,
            d.InputCapture,
            d.CameraInput,
            session.LocalPlayerFrameRuntime,
            d.ChaseCameraInput,
            session.LocalPlayerFrame,
            session.LiveSpatialReconciler,
            new AcDream.App.Combat.CombatCameraTargetSource(
                new AcDream.App.Combat.CharacterOptionCombatSettingsSource(
                    d.Runtime.CharacterOwner.Options),
                d.Combat,
                d.Selection,
                live.SelectionQuery));
        var updateFrame = new UpdateFrameOrchestrator(
            new LiveEntityTeardownFramePhase(live.LiveEntities),
            new ConsoleUpdateFrameFailureSink(),
            d.UpdateClock,
            new PhysicsScriptClockPublisher(content.ScriptRunner),
            session.StreamingFrame,
            session.GameplayInput,
            liveFrameCoordinator,
            new LiveEntityLivenessFramePhase(
                session.Liveness,
                new StopwatchClientMonotonicTimeSource()),
            session.LocalTeleport,
            new PlayerModeAutoEntryFramePhase(session.PlayerModeAutoEntry),
            cameraFrame,
            new RenderSceneUpdateCommitPhase(live.RenderSceneShadow),
            live.WorldAvailability);
        Fault(FrameRootCompositionPoint.UpdateRootCreated);

        var bindingsLease = scope.Own(
            "frame-root runtime bindings",
            bindings,
            static value => value.Dispose());
        bindingsOwnedByScope = true;
        IDisposable frameGraphPublication = d.FrameGraphs.PublishOwned(
            updateFrame,
            renderFrame);
        var graphLease = scope.Own(
            "game frame graph publication",
            frameGraphPublication,
            static value => value.Dispose());
        Fault(FrameRootCompositionPoint.FrameGraphPublished);

        var result = new FrameRootResult(
            updateFrame,
            renderFrame,
            bindings,
            frameGraphPublication,
            session.SessionHost,
            session.GameRuntime);
        _publication.PublishFrameRoots(result);
        graphLease.Transfer();
        bindingsLease.Transfer();
        Fault(FrameRootCompositionPoint.ResultPublished);
        return result;
    }

    private void Fault(FrameRootCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}
