using AcDream.App.Audio;
using AcDream.App.Combat;
using AcDream.App.Composition;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Sky;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Settings;
using AcDream.App.Spells;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Audio;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Input;
using DatReaderWriter;
using Silk.NET.Input;

namespace AcDream.App.Rendering;

internal enum GameWindowLifetimeStatus
{
    Active,
    RetryableIncomplete,
    Complete,
    CompleteWithCleanupFailures,
    CompleteWithDeferredNativeRelease,
    AbandonedIncomplete,
}

internal sealed record GameWindowLifetimeReport(
    GameWindowLifetimeStatus Status,
    string? BlockedStage,
    IReadOnlyList<ResourceShutdownCleanupFailure> CleanupFailures,
    Exception? Error)
{
    public bool IsTerminal => Status is
        GameWindowLifetimeStatus.Complete
        or GameWindowLifetimeStatus.CompleteWithCleanupFailures
        or GameWindowLifetimeStatus.CompleteWithDeferredNativeRelease
        or GameWindowLifetimeStatus.AbandonedIncomplete;
}

internal sealed record IngressShutdownRoots(
    HostQuiescenceGate HostQuiescence,
    LiveCombatModeCommandSlot CombatCommands,
    RuntimeDiagnosticCommandSlot DiagnosticCommands,
    RetainedUiGameplayBinding? RetainedGameplay,
    GameplayInputActionRouter? GameplayActions,
    CameraPointerInputController? CameraPointer,
    InputDispatcher? Dispatcher,
    SilkMouseSource? MouseSource,
    SilkKeyboardSource? KeyboardSource,
    RetailUiRuntimeLease RetailUi,
    // Keeps failed physical UI bindings alive through native-window release.
    UiHost? RetainedUiHost,
    IDisposable? Plugins,
    GameRuntime Runtime,
    DispatcherMovementInputSource MovementInput,
    DispatcherCameraInputSource CameraInput,
    SilkWindowCallbackBinding? WindowCallbacks);

internal sealed record FrameShutdownRoots(
    IDisposable? FrameGraphPublication,
    FrameRootRuntimeBindings? FrameBindings,
    SessionPlayerRuntimeBindings? SessionBindings,
    InteractionUiLateBindings? InteractionBindings);

internal sealed record LiveShutdownRoots(
    CameraPointerInputController? CameraPointer,
    RetailUiRuntimeLease RetailUi,
    MagicRuntime? Magic,
    ItemInteractionController? ItemInteraction,
    ExternalContainerLifecycleController? ExternalContainers,
    LandblockStreamer? Streamer,
    EquippedChildRenderController? EquippedChildren,
    LiveEntityRuntime? LiveEntities,
    GameRuntime Runtime,
    IDisposable RuntimeHostLease,
    RenderSceneShadowRuntime? RenderSceneShadow,
    LivePresentationRuntimeBindings? PresentationBindings,
    DeferredEntityEffectAdvanceSource EffectAdvance,
    EntityEffectController? EntityEffects,
    AnimationHookRegistrationSet? HookRegistrations,
    LiveEntityLightController? LiveLights,
    LiveEntityPresentationController? LivePresentation,
    AnimationHookFrameQueue? AnimationHookFrames,
    EntityEffectPoseRegistry EffectPoses,
    OpenAlAudioEngine? Audio);

internal sealed record RenderShutdownRoots(
    GpuFrameFlightController? FrameFlights,
    IGpuDevice? GpuDevice,
    LocalPlayerTeleportController? LocalTeleport,
    TransferableResourceSlot<PortalTunnelPresentation> PortalTunnelFallback,
    PaperdollViewportRenderer? Paperdoll,
    CreatureAppraisalViewportRenderer? CreatureAppraisal,
    ChargenPreviewRenderer? ChargenPreview,
    ChargenPreviewController? ChargenPreviewController,
    ChargenPreviewRenderer? SummaryPreview,
    ChargenPreviewController? SummaryPreviewController,
    WbDrawDispatcher? DrawDispatcher,
    EnvCellRenderer? EnvironmentCells,
    PortalDepthMaskRenderer? PortalDepthMask,
    ClipFrame? ClipFrame,
    SkyRenderer? Sky,
    ParticleRenderer? Particles,
    TextureCache? Textures,
    WbMeshAdapter? MeshAdapter,
    TerrainModernRenderer? Terrain,
    SceneLightingUboBinding? SceneLighting,
    DebugLineRenderer? DebugLines,
    TextRenderer? TextRenderer,
    BitmapFont? DebugFont,
    DisplayFramePacingController FramePacing,
    FrameProfiler FrameProfiler,
    GameRenderResourceLifetime DedicatedResources,
    ResourceConstructionCleanupLedger ConstructionCleanup);

internal sealed record PlatformShutdownRoots(
    IDatReaderWriter? Dats,
    IPreparedAssetSource? PreparedAssets,
    IInputContext? Input,
    GameWindowGraphics? Graphics);

internal sealed record GameWindowShutdownRoots(
    IngressShutdownRoots Ingress,
    FrameShutdownRoots Frame,
    LiveShutdownRoots Live,
    RenderShutdownRoots Render,
    PlatformShutdownRoots Platform);

internal sealed class GameWindowLifetime
{
    private readonly Func<ResourceShutdownTransaction>? _injectedTransactionFactory;
    private GameWindowShutdownRoots? _roots;
    private ResourceShutdownTransaction? _transaction;
    private IDisposable? _nativeWindow;
    private Func<bool>? _isNativeRenderLoopArmed;
    private Action? _requestNativeClose;
    private bool _shutdownRootsPublished;
    private bool _nativeReleaseAttempted;
    private bool _completing;
    private GameWindowLifetimeReport _report = new(
        GameWindowLifetimeStatus.Active,
        null,
        [],
        null);

    public GameWindowLifetime()
    {
    }

    internal GameWindowLifetime(Func<ResourceShutdownTransaction> transactionFactory)
    {
        _injectedTransactionFactory = transactionFactory
            ?? throw new ArgumentNullException(nameof(transactionFactory));
    }

    public GameWindowLifetimeReport Report => _report;
    public bool HasShutdownRoots =>
        _shutdownRootsPublished || _injectedTransactionFactory is not null;
    internal bool RetainsShutdownGraph => _roots is not null || _transaction is not null;

    public void PublishNativeWindow(
        IDisposable nativeWindow,
        Func<bool>? isRenderLoopArmed = null,
        Action? requestClose = null)
    {
        ArgumentNullException.ThrowIfNull(nativeWindow);
        if (_nativeReleaseAttempted || _nativeWindow is not null)
            throw new InvalidOperationException("A native window is already lifetime-owned.");
        _nativeWindow = nativeWindow;
        _isNativeRenderLoopArmed = isRenderLoopArmed;
        _requestNativeClose = requestClose;
    }

    public void PublishShutdownRoots(GameWindowShutdownRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (_transaction is not null || _report.Status != GameWindowLifetimeStatus.Active)
            throw new InvalidOperationException("Shutdown roots cannot change after completion starts.");
        if (_roots is not null && !ReferenceEquals(_roots, roots))
            throw new InvalidOperationException("Shutdown roots are already published.");
        _roots = roots;
        _shutdownRootsPublished = true;
    }

    public GameWindowLifetimeReport TryComplete()
    {
        if (_report.IsTerminal || _completing)
            return _report;
        EnsureTransaction();

        _completing = true;
        try
        {
            try
            {
                _transaction!.CompleteOrThrow();
            }
            catch (Exception error)
            {
                _report = new GameWindowLifetimeReport(
                    GameWindowLifetimeStatus.RetryableIncomplete,
                    _transaction!.CurrentStageName,
                    _transaction.CleanupFailures,
                    error);
                return _report;
            }

            GameWindowLifetimeStatus status = _transaction!.CleanupFailures.Count == 0
                ? GameWindowLifetimeStatus.Complete
                : GameWindowLifetimeStatus.CompleteWithCleanupFailures;
            _report = new GameWindowLifetimeReport(
                status,
                null,
                _transaction.CleanupFailures,
                null);
            return _report;
        }
        finally
        {
            _completing = false;
        }
    }

    public GameWindowLifetimeReport CompleteAndReleaseNativeWindow()
    {
        if (_completing)
            return _report;
        if (!_report.IsTerminal)
            TryComplete();

        if (_report.Status == GameWindowLifetimeStatus.RetryableIncomplete)
        {
            AbandonRetainedRootsAfterTerminalFailure();
            (_, Exception? nativeFailure) = ReleaseNativeWindow();
            Exception terminalError = nativeFailure is null
                ? _report.Error ?? new InvalidOperationException(
                    "Shutdown did not converge before native fallback.")
                : new AggregateException(
                    "Shutdown and native fallback both failed.",
                    _report.Error ?? new InvalidOperationException(
                        "Shutdown did not converge before native fallback."),
                    nativeFailure);
            _report = new GameWindowLifetimeReport(
                GameWindowLifetimeStatus.AbandonedIncomplete,
                _report.BlockedStage,
                _report.CleanupFailures,
                terminalError);
            return _report;
        }

        if (_report.IsTerminal && !_nativeReleaseAttempted)
        {
            (NativeReleaseOutcome outcome, Exception? nativeFailure) = ReleaseNativeWindow();
            if (outcome == NativeReleaseOutcome.Deferred)
            {
                _report = new GameWindowLifetimeReport(
                    GameWindowLifetimeStatus.CompleteWithDeferredNativeRelease,
                    "native window",
                    _report.CleanupFailures,
                    null);
            }
            else if (nativeFailure is not null)
            {
                _report = new GameWindowLifetimeReport(
                    GameWindowLifetimeStatus.AbandonedIncomplete,
                    "native window",
                    _report.CleanupFailures,
                    nativeFailure);
            }
            else
            {
                ReleaseCompletedRoots();
            }
        }

        return _report;
    }

    private void EnsureTransaction()
    {
        if (_transaction is not null)
            return;
        _transaction = _injectedTransactionFactory?.Invoke()
            ?? GameWindowShutdownManifest.Create(
                _roots ?? throw new InvalidOperationException(
                    "Shutdown roots must publish before completion starts."));
    }

    private enum NativeReleaseOutcome
    {
        Released,
        Deferred,
        Failed,
    }

    private (NativeReleaseOutcome Outcome, Exception? Error) ReleaseNativeWindow()
    {
        if (_nativeReleaseAttempted)
            return (NativeReleaseOutcome.Released, null);

        if (_isNativeRenderLoopArmed?.Invoke() == true)
        {
            try
            {
                _requestNativeClose?.Invoke();
            }
            catch
            {
                // Best-effort only — the deferred outcome does not depend on
                // this succeeding, and the native window's own exception
                // must never become the reported failure here.
            }
            _nativeReleaseAttempted = true;
            return (NativeReleaseOutcome.Deferred, null);
        }

        _nativeReleaseAttempted = true;
        try
        {
            _nativeWindow?.Dispose();
            _nativeWindow = null;
            return (NativeReleaseOutcome.Released, null);
        }
        catch (Exception error)
        {
            return (NativeReleaseOutcome.Failed, error);
        }
    }

    private void ReleaseCompletedRoots()
    {
        _transaction = null;
        _roots = null;
    }

    private void AbandonRetainedRootsAfterTerminalFailure()
    {
        RetailUiRuntimeLease? retainedUi = _roots?.Live.RetailUi;
        if (retainedUi?.HasDisposalFailure == true)
            retainedUi.AbandonAfterTerminalFailure();
    }
}

internal static class GameWindowShutdownManifest
{
    public static ResourceShutdownTransaction Create(GameWindowShutdownRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        IngressShutdownRoots ingress = roots.Ingress;
        FrameShutdownRoots frame = roots.Frame;
        LiveShutdownRoots live = roots.Live;
        RenderShutdownRoots render = roots.Render;
        PlatformShutdownRoots platform = roots.Platform;

        return new ResourceShutdownTransaction(
            new ResourceShutdownStage("host and session barriers",
            [
                Hard("host quiescence", ingress.HostQuiescence.StopAccepting),
                Hard("combat command slot", ingress.CombatCommands.Deactivate),
                Hard("diagnostic command slot", ingress.DiagnosticCommands.Deactivate),
                Hard("retained gameplay", () => ingress.RetainedGameplay?.Deactivate()),
                Hard("gameplay actions", () => ingress.GameplayActions?.Deactivate()),
                Hard("camera pointer", () => ingress.CameraPointer?.Deactivate()),
                Hard("dispatcher", () => ingress.Dispatcher?.Deactivate()),
                Hard("mouse source", () => ingress.MouseSource?.Deactivate()),
                Hard("keyboard source", () => ingress.KeyboardSource?.Deactivate()),
                Hard("retained UI input", ingress.RetailUi.QuiesceInput),
                Hard("game runtime session", ingress.Runtime.StopSession),
            ]),
            new ResourceShutdownStage("physical ingress cleanup",
            [
                Soft("retained gameplay", () => DisposeRetainedGameplay(ingress.RetainedGameplay)),
                Soft("gameplay actions", () => DisposeGameplayActions(ingress.GameplayActions)),
                Soft("retained UI input", ingress.RetailUi.DeactivateInput),
                Soft("camera pointer", () => DisposeCameraPointer(ingress.CameraPointer)),
                Soft("dispatcher", () => DisposeDispatcher(ingress)),
                Soft("mouse source", () => DisposeMouseSource(ingress.MouseSource)),
                Soft("keyboard source", () => DisposeKeyboardSource(ingress.KeyboardSource)),
                Soft("native window callbacks", () => DisposeWindowCallbacks(ingress.WindowCallbacks)),
            ]),
            new ResourceShutdownStage("plugin host",
            [
                Hard("plugins", () => ingress.Plugins?.Dispose()),
            ]),
            new ResourceShutdownStage("frame borrowers",
            [
                Hard("world frame composition", () => frame.FrameGraphPublication?.Dispose()),
                Hard("frame-root bindings", () => frame.FrameBindings?.Dispose()),
                Hard("session/player bindings", () => frame.SessionBindings?.Dispose()),
            ]),
            new ResourceShutdownStage("session dependents",
            [
                Hard("interaction/UI late bindings", () => frame.InteractionBindings?.Dispose()),
                Hard("mouse capture", () => live.CameraPointer?.ReleaseMouseLookAfterSessionRetirement()),
                Hard("retail UI", () => DisposeRetailUi(live.RetailUi)),
                Hard("magic runtime", () => live.Magic?.Dispose()),
                Hard("item interaction", () => live.ItemInteraction?.Dispose()),
                Hard("external containers", () => live.ExternalContainers?.Dispose()),
                Hard("streamer", () => live.Streamer?.Dispose()),
                Hard("equipped children", () => live.EquippedChildren?.Dispose()),
            ]),
            new ResourceShutdownStage("live entities",
            [
                Hard("live entity runtime", () => live.LiveEntities?.Clear()),
                Hard(
                    "shadow render scene",
                    () => live.RenderSceneShadow?.Dispose()),
            ]),
            new ResourceShutdownStage("effect dispatch edges",
            [
                Hard("live-presentation bindings", () => live.PresentationBindings?.Dispose()),
                Hard("entity-effect advance source", () =>
                {
                    if (live.EntityEffects is { } effects)
                        live.EffectAdvance.Unbind(effects);
                    live.EffectAdvance.Deactivate();
                }),
                Hard("animation-hook registrations", () => DisposeHookRegistrations(live.HookRegistrations)),
            ]),
            new ResourceShutdownStage("live entity dependents",
            [
                Hard("live lights", () => live.LiveLights?.Dispose()),
                Hard("live presentation", () => live.LivePresentation?.Dispose()),
                Hard("effect network state", () =>
                {
                    live.EntityEffects?.ClearNetworkState();
                    live.AnimationHookFrames?.Clear();
                    live.EffectPoses.Clear();
                }),
                Hard("audio", () => DisposeAudio(live.Audio)),
            ]),
            new ResourceShutdownStage("submitted GPU work",
            [
                Hard("frame flight drain", () => render.FrameFlights?.WaitForSubmittedWork()),
            ]),
            new ResourceShutdownStage("render frontends",
            [
                Hard("portal tunnel", () =>
                {
                    render.LocalTeleport?.Dispose();
                    render.PortalTunnelFallback.ReleaseFallback();
                }),
                Hard("paperdoll viewport", () => render.Paperdoll?.Dispose()),
                Hard(
                    "creature appraisal viewport",
                    () => render.CreatureAppraisal?.Dispose()),
                Hard("chargen preview control", () => render.ChargenPreviewController?.Dispose()),
                Hard("chargen preview viewport", () => render.ChargenPreview?.Dispose()),
                Hard("summary preview control", () => render.SummaryPreviewController?.Dispose()),
                Hard("summary preview viewport", () => render.SummaryPreview?.Dispose()),
                Hard("mesh draw dispatcher", () => render.DrawDispatcher?.Dispose()),
                Hard("environment cells", () => render.EnvironmentCells?.Dispose()),
                Hard("portal depth mask", () => render.PortalDepthMask?.Dispose()),
                Hard("clip frame", () => render.ClipFrame?.Dispose()),
                Hard("sky", () => render.Sky?.Dispose()),
                Hard("particles", () => render.Particles?.Dispose()),
            ]),
            new ResourceShutdownStage("game runtime root",
            [
                Hard("graphical runtime host lease", live.RuntimeHostLease.Dispose),
                Hard("game runtime", () => DisposeGameRuntime(live.Runtime)),
            ]),
            new ResourceShutdownStage("shared texture owners",
            [
                Hard("texture cache", () => render.Textures?.Dispose()),
            ]),
            new ResourceShutdownStage("mesh adapter",
            [
                Hard("WB mesh adapter", () => render.MeshAdapter?.Dispose()),
            ]),
            new ResourceShutdownStage("remaining render owners",
            [
                Hard("terrain", () => render.Terrain?.Dispose()),
                Hard("scene lighting", () => render.SceneLighting?.Dispose()),
                Hard("debug lines", () => render.DebugLines?.Dispose()),
                Hard("text renderer", () => render.TextRenderer?.Dispose()),
                Hard("debug font", () => render.DebugFont?.Dispose()),
                Hard("frame pacing", render.FramePacing.Dispose),
                Hard("frame profiler", render.FrameProfiler.Dispose),
                Hard("GPU device (RHI)", () => render.GpuDevice?.Dispose()),
            ]),
            new ResourceShutdownStage("dedicated render resources",
            [
                Hard("terrain atlas", render.DedicatedResources.ReleaseTerrainAtlas),
            ]),
            new ResourceShutdownStage("failed render construction cleanup",
            [
                Hard("resource construction ledger", render.ConstructionCleanup.Dispose),
            ]),
            new ResourceShutdownStage("frame flight owner",
            [
                Hard("frame flights", () => render.FrameFlights?.Dispose()),
            ]),
            new ResourceShutdownStage("content mappings",
            [
                Hard("prepared asset source", () => platform.PreparedAssets?.Dispose()),
                Hard("DAT collection", () => platform.Dats?.Dispose()),
            ]),
            new ResourceShutdownStage("input context",
            [
                Hard("input context", () => platform.Input?.Dispose()),
            ]),
            new ResourceShutdownStage("graphics API context",
            [
                Hard("graphics API", () => platform.Graphics?.Dispose()),
            ]));
    }

    private static ResourceShutdownOperation Hard(string name, Action action) =>
        new(name, action);

    private static ResourceShutdownOperation Soft(string name, Action action) =>
        new(name, action, ResourceShutdownOperationPolicy.ReportAndContinue);

    private static void DisposeGameRuntime(GameRuntime runtime)
    {
        runtime.Dispose();
        GameRuntimeOwnershipSnapshot ownership = runtime.CaptureOwnership();
        if (!ownership.IsConverged)
        {
            throw new InvalidOperationException(
                "The canonical game runtime did not converge after disposal.");
        }
    }

    private static void DisposeRetainedGameplay(RetainedUiGameplayBinding? binding)
    {
        if (binding is null)
            return;
        binding.Dispose();
        if (!binding.IsDisposalComplete)
            throw new InvalidOperationException("Retained gameplay callback removal remains pending.");
    }

    private static void DisposeGameplayActions(GameplayInputActionRouter? actions)
    {
        if (actions is null)
            return;
        actions.Dispose();
        if (!actions.IsDisposalComplete)
            throw new InvalidOperationException("Gameplay action callback removal remains pending.");
    }

    private static void DisposeCameraPointer(CameraPointerInputController? pointer)
    {
        if (pointer is null)
            return;
        pointer.Dispose();
        if (!pointer.IsDisposalComplete)
            throw new InvalidOperationException("Camera pointer callback removal remains pending.");
    }

    private static void DisposeDispatcher(IngressShutdownRoots roots)
    {
        InputDispatcher? dispatcher = roots.Dispatcher;
        if (dispatcher is null)
            return;
        roots.MovementInput.Unbind(dispatcher);
        roots.CameraInput.Unbind(dispatcher);
        dispatcher.Dispose();
        if (!dispatcher.IsDisposalComplete)
            throw new InvalidOperationException("Input dispatcher source removal remains pending.");
    }

    private static void DisposeMouseSource(SilkMouseSource? source)
    {
        if (source is null)
            return;
        source.Dispose();
        if (!source.IsDisposalComplete)
            throw new InvalidOperationException("Mouse source callback removal remains pending.");
    }

    private static void DisposeKeyboardSource(SilkKeyboardSource? source)
    {
        if (source is null)
            return;
        source.Dispose();
        if (!source.IsDisposalComplete)
            throw new InvalidOperationException("Keyboard source callback removal remains pending.");
    }

    private static void DisposeWindowCallbacks(SilkWindowCallbackBinding? binding)
    {
        if (binding is null)
            return;
        binding.Dispose();
        if (!binding.IsDisposalComplete)
            throw new NativeWindowCallbackCleanupDeferredException();
    }

    private static void DisposeRetailUi(RetailUiRuntimeLease lease)
    {
        lease.Dispose();
        if (!lease.IsDisposalComplete)
            throw new InvalidOperationException("The retained UI ownership lease did not complete disposal.");
    }

    private static void DisposeHookRegistrations(AnimationHookRegistrationSet? registrations)
    {
        if (registrations is null)
            return;
        registrations.Dispose();
        if (!registrations.IsCleanupComplete)
            throw new InvalidOperationException("Animation-hook registration cleanup remains pending.");
    }

    private static void DisposeAudio(OpenAlAudioEngine? engine)
    {
        if (engine is null)
            return;
        engine.Dispose();
        if (!engine.IsDisposalComplete)
            throw new InvalidOperationException("OpenAL native-resource cleanup remains pending.");
    }

}
