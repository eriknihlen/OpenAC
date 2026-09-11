using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Diagnostics;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Runtime;
using AcDream.App.Settings;
using AcDream.App.Audio;
using AcDream.App.Streaming;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Content;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Chat;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.Selection;
using AcDream.Core.Player;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using Silk.NET.Windowing;

namespace AcDream.App.Composition;

internal sealed record SessionPlayerDependencies(
    RuntimeOptions Options,
    GameRuntime Runtime,
    IWindow Window,
    object DatLock,
    RuntimeSettingsController Settings,
    SettingsDevToolsResult SettingsDevTools,
    WorldEnvironmentController WorldEnvironment,
    WorldSceneDebugState WorldSceneDebugState,
    RuntimeDiagnosticCommandSlot RuntimeDiagnosticCommands,
    LiveCombatModeCommandSlot CombatModeCommands,
    RuntimeCombatModeOperationsSlot CombatModeOperations,
    HostQuiescenceGate HostQuiescence,
    PhysicsEngine PhysicsEngine,
    PhysicsDataCache PhysicsDataCache,
    WorldGameState WorldGameState,
    WorldEvents WorldEvents,
    EntityClassificationCache ClassificationCache,
    LiveEntityRuntimeSlot RuntimeSlot,
    LiveEntityAnimationRuntimeView<LiveEntityAnimationState> AnimatedEntities,
    RemoteMovementObservationTracker RemoteMovementObservations,
    RemotePhysicsUpdater RemotePhysicsUpdater,
    RemoteInboundMotionDispatcher RemoteInboundMotion,
    RetailInboundEventDispatcher InboundEntityEvents,
    DeferredLiveEntityMotionRuntimeBindings MotionBindings,
    LocalPlayerIdentityState PlayerIdentity,
    LocalPlayerPhysicsHostSlot PlayerHost,
    LocalPlayerModeState PlayerMode,
    ChaseCameraInputState ChaseCameraInput,
    LocalPlayerOutboundController PlayerOutbound,
    DeferredLocalPlayerTeleportNetworkSink TeleportSink,
    LiveWorldOriginState WorldOrigin,
    WorldRenderRangeState RenderRange,
    LocalPlayerShadowState PlayerShadow,
    ViewportAspectState ViewportAspect,
    PlayerApproachCompletionState PlayerApproachCompletions,
    PointerPositionState PointerPosition,
    DispatcherMovementInputSource MovementInput,
    IInputCaptureSource InputCapture,
    AcDream.App.Rendering.Vfx.ParticleVisibilityController ParticleVisibility,
    TranslucencyFadeManager TranslucencyFades,
    EntityEffectPoseRegistry EffectPoses,
    UpdateFrameClock UpdateClock,
    MovementTruthDiagnosticController MovementDiagnostics,
    CombatAttackOperationsSlot CombatAttackOperations,
    CombatFeedbackSlot CombatFeedback,
    TransferableResourceSlot<PortalTunnelPresentation> PortalTunnelFallback,
    Action<string> Log,
    Func<string, bool>? TryHandlePluginCommand,
    SessionStatusWriter StatusWriter)
{
    public RuntimeActionState Actions => Runtime.ActionOwner;

    public RuntimeEntityObjectLifetime EntityObjects =>
        Runtime.EntityObjects;

    public RuntimeLocalPlayerMovementState PlayerController =>
        Runtime.MovementOwner;

    public RuntimeInventoryState Inventory => Runtime.InventoryOwner;

    public RuntimeCommunicationState Communication =>
        Runtime.CommunicationOwner;

    public RuntimeCharacterState Character => Runtime.CharacterOwner;
}

internal sealed record SessionPlayerResult(
    LandblockStreamer Streamer,
    StreamingController Streaming,
    StreamingOriginRecenterCoordinator StreamingOriginRecenter,
    WorldRevealCoordinator WorldReveal,
    DatSpawnClaimHydrationClassifier SpawnClaimHydration,
    LiveSessionController LiveSession,
    LiveEntityHydrationController Hydration,
    LiveEntityDeletionController Deletion,
    LiveEntityNetworkUpdateController NetworkUpdates,
    LiveEntityLivenessController Liveness,
    LiveEntitySessionController SessionEvents,
    GameplayInputFrameController GameplayInput,
    StreamingFrameController StreamingFrame,
    LocalPlayerAnimationController LocalPlayerAnimation,
    LocalPlayerShadowSynchronizer LocalPlayerShadow,
    LiveLocalPlayerFrameRuntime LocalPlayerFrameRuntime,
    RetailLocalPlayerFrameController LocalPlayerFrame,
    LiveSpatialPresentationReconciler LiveSpatialReconciler,
    LiveObjectFrameController LiveObjectFrame,
    PlayerModeController PlayerMode,
    PlayerModeAutoEntry PlayerModeAutoEntry,
    LocalPlayerTeleportController LocalTeleport,
    LiveSessionHost SessionHost,
    RuntimePlacementProjectionRetrySlot PlacementProjectionRetry,
    CurrentGameRuntimeAdapter GameRuntime,
    GameplayInputActionRouter? GameplayActions,
    SessionPlayerRuntimeBindings RuntimeBindings);

internal interface IGameWindowSessionPlayerPublication
{
    void PublishSessionPlayer(SessionPlayerResult result);
}

internal enum SessionPlayerCompositionPoint
{
    StreamingRadiiResolved,
    StreamerCreated,
    StreamerStarted,
    StreamingCreated,
    RuntimeSettingsBound,
    WorldRevealCreated,
    LiveSessionCreated,
    HydrationCreated,
    LiveEntityGraphBound,
    CombatOperationsBound,
    GameplayInputBound,
    UpdateLeavesCreated,
    PlayerModeBound,
    PortalTransferred,
    TeleportBound,
    SessionHostCreated,
    CommandsBound,
    GameplayActionsAttached,
    ResultPublished,
}

internal sealed class SessionPlayerCompositionPhase
    : ISessionPlayerCompositionPhase<
        HostInputCameraResult,
        ContentEffectsAudioResult,
        SettingsDevToolsResult,
        WorldRenderResult,
        InteractionRetainedUiResult,
        LivePresentationResult,
        SessionPlayerResult>
{
    private readonly SessionPlayerDependencies _dependencies;
    private readonly IGameWindowSessionPlayerPublication _publication;
    private readonly Action<SessionPlayerCompositionPoint>? _faultInjection;

    public SessionPlayerCompositionPhase(
        SessionPlayerDependencies dependencies,
        IGameWindowSessionPlayerPublication publication,
        Action<SessionPlayerCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _faultInjection = faultInjection;
    }

    public SessionPlayerResult Compose(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        LivePresentationResult live)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(live);
        if (!ReferenceEquals(_dependencies.SettingsDevTools, settings))
        {
            throw new InvalidOperationException(
                "Session/player dependencies do not match the ordered settings result.");
        }

        var scope = new CompositionAcquisitionScope();
        SessionPlayerRuntimeBindings? bindings = null;
        bool bindingsOwnedByScope = false;
        try
        {
            SessionPlayerResult result = ComposeCore(
                host,
                content,
                world,
                interaction,
                live,
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
                    "session/player runtime bindings",
                    bindings,
                    static value => value.Dispose());
                bindingsOwnedByScope = true;
            }

            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private SessionPlayerResult ComposeCore(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        LivePresentationResult live,
        CompositionAcquisitionScope scope,
        ref SessionPlayerRuntimeBindings? bindings,
        ref bool bindingsOwnedByScope)
    {
        SessionPlayerDependencies d = _dependencies;
        WorldRenderFoundation foundation = world.Foundation;
        int nearRadius = d.Settings.ResolvedQuality.NearRadius;
        int farRadius = d.Settings.ResolvedQuality.FarRadius;
        if (d.Options.LegacyStreamRadius is { } legacyRadius)
        {
            nearRadius = legacyRadius;
            farRadius = Math.Max(legacyRadius, farRadius);
        }
        d.RenderRange.NearRadius = nearRadius;
        d.RenderRange.FarRadius = farRadius;
        d.Log(
            $"streaming: nearRadius={nearRadius} " +
            $"(window={2 * nearRadius + 1}x{2 * nearRadius + 1})  " +
            $"farRadius={farRadius} " +
            $"(window={2 * farRadius + 1}x{2 * farRadius + 1})");
        Fault(SessionPlayerCompositionPoint.StreamingRadiiResolved);

        IPreparedCollisionSource preparedCollisions =
            content.PreparedAssets as IPreparedCollisionSource ??
            throw new NotSupportedException(
                "Production prepared assets must expose the matching " +
                "prepared-collision catalog.");
        var landblockBuildFactory = new LandblockBuildFactory(
            content.Dats,
            preparedCollisions,
            d.DatLock,
            world.TerrainBuild.HeightTable,
            d.Options.DumpSceneryZ);
        AcDream.Core.Quests.ContractCatalog? pluginContractCatalog = null;
        d.WorldGameState.ContractsSource = () =>
        {
            if (pluginContractCatalog is null)
            {
                lock (d.DatLock)
                    pluginContractCatalog =
                        AcDream.Content.ContractTableReader.Load(content.Dats);
            }

            return AcDream.Runtime.Gameplay.ContractPluginProjection.Project(
                d.Runtime.ContractsOwner.View,
                pluginContractCatalog,
                DateTime.UtcNow);
        };

        var streamerLease = scope.Acquire(
            "landblock streamer",
            () => LandblockStreamer.CreateForRequests(
                landblockBuildFactory.Build,
                (id, landblock) =>
                {
                    if (landblock is null)
                        return null;
                    uint x = (id >> 24) & 0xFFu;
                    uint y = (id >> 16) & 0xFFu;
                    return AcDream.Core.Terrain.LandblockMesh.Build(
                        landblock.Heightmap,
                        x,
                        y,
                        world.TerrainBuild.HeightTable,
                        world.TerrainBuild.Blending,
                        world.TerrainBuild.SurfaceCache);
                }),
            static value => value.Dispose());
        Fault(SessionPlayerCompositionPoint.StreamerCreated);
        streamerLease.Resource.Start();
        Fault(SessionPlayerCompositionPoint.StreamerStarted);

        var streaming = new StreamingController(
            enqueueLoad: (id, kind, generation) => streamerLease.Resource.EnqueueLoad(
                new LandblockBuildRequest(
                    id,
                    kind,
                    generation,
                    new LandblockBuildOrigin(
                        d.WorldOrigin.CenterX,
                        d.WorldOrigin.CenterY))),
            enqueueUnload: streamerLease.Resource.EnqueueUnload,
            completionSource: streamerLease.Resource,
            state: live.WorldState,
            nearRadius: nearRadius,
            farRadius: farRadius,
            presentationPipeline: live.LandblockPipeline,
            clearPendingLoads: streamerLease.Resource.ClearPendingLoads,
            workBudgetOptions: d.Options.StreamingWorkBudgets);
        var streamingOriginRecenter = new StreamingOriginRecenterCoordinator(
            streaming,
            d.WorldOrigin);
        streaming.MaxCompletionsPerFrame =
            d.Settings.ResolvedQuality.MaxCompletionsPerFrame;
        Fault(SessionPlayerCompositionPoint.StreamingCreated);

        bindings = new SessionPlayerRuntimeBindings();
        var liveSessionCommands = new LiveSessionCommandSurface(
            d.TryHandlePluginCommand);
        var settingsTargets = new RuntimeSettingsTargets(
            d.Settings.DisplayWindowTarget ?? new SilkRuntimeDisplayWindowTarget(d.Window),
            live.DrawDispatcher,
            foundation.TerrainAtlas,
            streaming,
            d.RenderRange,
            interaction.RetainedUi?.Host.Root,
            liveSessionCommands,
            chatOpacity: interaction.RetainedUi?.Runtime.WindowOpacity,
            cameras: host.CameraController,
            log: d.Log,
            audio: content.Audio?.Engine);
        bindings.Adopt(
            "runtime settings targets",
            d.Settings.BindRuntimeTargetsOwned(settingsTargets));
        Fault(SessionPlayerCompositionPoint.RuntimeSettingsBound);

        var spawnClaimClassifier = new DatSpawnClaimHydrationClassifier(
            content.Dats,
            d.DatLock);
        var worldQuiescence = new WorldGenerationQuiescence(
            d.Actions.Selection,
            live.WorldState,
            guid => live.LiveEntities.TryGetProjectionKey(
                guid,
                out AcDream.Runtime.Entities.RuntimeEntityKey key)
                    ? key
                    : null,
            content.Audio?.Engine);
        var compositeWarmupSource =
            new CompositeWarmupEntitySource(live.WorldState);
        WbDrawDispatcher? revealDispatcher = live.DrawDispatcher;
        d.PhysicsEngine.DiagnosticLog =
            static line => System.Console.WriteLine(line);
        var revealRenderResources = new WorldRevealRenderResourceScheduler(
            foundation.MeshAdapter is { } revealMeshes
                ? revealMeshes.SetDestinationRevealUploadPriority
                : static _ => { },
            foundation.TextureCache.SetDestinationRevealUploadPriority);
        IRenderFrameResourceDiagnosticsSource? revealResourceDiagnostics =
            StreamingDiagnostics.ProbeRevealTiming
                ? new RuntimeRenderFrameResourceDiagnosticsSource(
                    particles: null,
                    particleBindings: null,
                    worldDispatcher: revealDispatcher,
                    environmentCells: null,
                    particleRenderer: null,
                    uiTextRenderer: null,
                    portalDepthMask: null,
                    clipFrame: null,
                    terrain: null,
                    lighting: null,
                    meshes: foundation.MeshAdapter,
                    textures: foundation.TextureCache,
                    preparedAssets: content.PreparedAssets)
                : null;
        var worldReveal = new WorldRevealCoordinator(
            live.WorldTransit,
            () => StreamingDiagnostics.ApplyRevealRadiusOverride(
                new StreamingRevealWindow(
                    streaming.NearRadius,
                    streaming.FarRadius)),
            streaming.IsRenderNeighborhoodResident,
            d.PhysicsEngine.IsSpawnCellReady,
            d.PhysicsEngine.IsNeighborhoodTerrainResident,
            () => revealDispatcher?.CompositeTexturesReady ?? true,
            (destinationCell, radius) =>
            {
                if (revealDispatcher is null)
                    return;
                compositeWarmupSource.Refresh(destinationCell, radius);
                revealDispatcher.PrepareCompositeTextures(
                    compositeWarmupSource.Entities,
                    compositeWarmupSource.Generation,
                    destinationCell,
                    radius);
            },
            () =>
            {
                compositeWarmupSource.Reset();
                revealDispatcher?.InvalidateCompositeWarmupReadiness();
            },
            spawnClaimClassifier.IsUnhydratable,
            worldQuiescence,
            streaming,
            revealRenderResources,
            () => live.WorldState.LoadedLandblockCount,
            revealResourceDiagnostics);
        Fault(SessionPlayerCompositionPoint.WorldRevealCreated);

        return CompleteSessionPlayer(
            host,
            content,
            world,
            interaction,
            live,
            scope,
            streamerLease,
            streaming,
            streamingOriginRecenter,
            worldReveal,
            spawnClaimClassifier,
            bindings,
            liveSessionCommands,
            ref bindingsOwnedByScope);
    }

    private SessionPlayerResult CompleteSessionPlayer(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        LivePresentationResult live,
        CompositionAcquisitionScope scope,
        CompositionAcquisitionScope.CompositionAcquisitionLease<LandblockStreamer>
            streamerLease,
        StreamingController streaming,
        StreamingOriginRecenterCoordinator streamingOriginRecenter,
        WorldRevealCoordinator worldReveal,
        DatSpawnClaimHydrationClassifier spawnClaimClassifier,
        SessionPlayerRuntimeBindings bindings,
        LiveSessionCommandSurface liveSessionCommands,
        ref bool bindingsOwnedByScope)
    {
        SessionPlayerDependencies d = _dependencies;
        var networkUpdateBridge = new DeferredLiveEntityNetworkUpdateSink();
        var sealedDungeonCells = new DatSealedDungeonCellClassifier(
            content.Dats,
            d.DatLock);
        var projectionMaterializer = new DatLiveEntityProjectionMaterializer(
            d.Options,
            content.Dats,
            live.LiveEntities,
            content.CollisionAssets,
            content.AnimationLoader,
            live.EntitySpawnAdapter,
            world.Foundation.TextureCache,
            d.ClassificationCache,
            d.EffectPoses,
            live.EquippedChildren,
            d.WorldGameState,
            d.WorldEvents,
            d.PhysicsEngine.ShadowObjects,
            content.CollisionBuilder,
            live.ProjectileController,
            live.AnimationPresenter,
            live.StaticAnimationScheduler,
            d.WorldOrigin,
            d.UpdateClock,
            d.Runtime.TransitOwner);
        var originCoordinator = new LiveEntityWorldOriginCoordinator(
            d.WorldOrigin,
            streaming,
            live.WorldState,
            worldReveal,
            d.PlayerIdentity,
            sealedDungeonCells,
            d.Log);

        LiveSessionController liveSession = d.Runtime.Session;
        var liveSessionSource = new LiveSessionAppSource(
            liveSession,
            liveSessionCommands);
        bindings.Adopt(
            "retained-UI live session",
            interaction.LateBindings.Session.Bind(liveSessionSource));
        var localPhysicsTimestamps =
            new LiveSessionLocalPhysicsTimestampPublisher(
                d.PlayerIdentity,
                liveSessionSource);
        Fault(SessionPlayerCompositionPoint.LiveSessionCreated);

        var teardown = new LiveEntityRuntimeTeardownController(
            live.LiveEntities,
            live.Presentation,
            live.EntityEffects,
            live.SelectionInteractions,
            d.Actions.Selection,
            d.AnimatedEntities,
            d.RemoteMovementObservations,
            d.TranslucencyFades,
            live.ProjectionWithdrawal,
            live.EquippedChildren,
            d.PhysicsEngine.ShadowObjects,
            live.Lights,
            d.ClassificationCache,
            d.PlayerIdentity);
        var deletion = new LiveEntityDeletionController(
            live.LiveEntities,
            d.EntityObjects,
            teardown,
            d.PlayerIdentity);
        live.LiveEntities.Physics.BindObjectTableHostResolver(
            guid => d.MotionBindings.ResolvePhysicsHost(guid));
        IPreparedCollisionSource firstEntryCollision =
            content.PreparedAssets as IPreparedCollisionSource
            ?? throw new NotSupportedException(
                "Production prepared assets must expose the matching "
                + "prepared-collision catalog.");
        uint firstEntryCylinderLocalId = 0u;
        float firstEntryCylinderRadius = 0f;
        float firstEntryCylinderHeight = 0f;
        var firstEntryDrive = new RuntimeFirstEntryDriveController(
            d.EntityObjects,
            d.Runtime.Clock,
            firstEntryCollision,
            () => PlayerMovementConstructionOptions.From(
                d.Runtime.CharacterOwner.MovementSkills.Snapshot),
            record =>
            {
                float radius = 0.48f;
                float height = 1.835f;
                uint localId = record.Key?.LocalEntityId ?? 0u;
                if (localId != 0u && localId == firstEntryCylinderLocalId)
                {
                    radius = firstEntryCylinderRadius;
                    height = firstEntryCylinderHeight;
                }
                else if (live.LiveEntities.TryGetWorldEntity(
                        record.ServerGuid,
                        out WorldEntity? playerEntity)
                    && playerEntity is not null)
                {
                    (float setupRadius, float setupHeight) =
                        d.MotionBindings.GetSetupCylinder(
                            record.ServerGuid,
                            playerEntity);
                    if (setupRadius >= 0.05f)
                    {
                        radius = setupRadius;
                        height = setupHeight;
                        if (localId != 0u)
                        {
                            firstEntryCylinderLocalId = localId;
                            firstEntryCylinderRadius = radius;
                            firstEntryCylinderHeight = height;
                        }
                    }
                }
                bool hasAuthoredShadow = record.Key is { } key
                    && d.PhysicsEngine.ShadowObjects.HasLogicalOwner(
                        key.LocalEntityId);
                return new RuntimeLocalPlayerPhysicsActivationPreparation(
                    radius,
                    height,
                    hasAuthoredShadow
                        ? RuntimeLocalPlayerShadowDisposition
                            .RegisteredAuthoredPayload
                        : RuntimeLocalPlayerShadowDisposition.ProvenShapeless);
            });
        var acceptedPositionDrive = new RuntimeAcceptedPositionDriveController(
            d.EntityObjects,
            d.Runtime.Clock,
            firstEntryCollision,
            d.PlayerOutbound,
            () => d.Runtime.Generation,
            () => d.PlayerIdentity.ServerGuid,
            () => d.PlayerController.Controller,
            () => d.Character.UsePositionFromServer,
            () => liveSessionSource.CurrentSession,
            () => d.PlayerController,
            isPortalAuthorityCurrent: portal => live.WorldTransit
                .CanPlacePortalDestination(
                    portal.RevealGeneration,
                    portal.TeleportSequence,
                    portal.Projection.DestinationCell));
        var remotePlacementDrive = new RuntimeRemotePlacementDriveController(
            d.EntityObjects,
            d.Runtime.Clock,
            firstEntryCollision,
            new GraphicalRemotePlacementServiceWindow(
                live.WorldState,
                streaming.IsLandblockPresentationReady));
        var hydration = new LiveEntityHydrationController(
            live.LiveEntities,
            d.EntityObjects,
            d.DatLock,
            projectionMaterializer,
            new LiveEntityRelationshipProjection(live.EquippedChildren),
            new LiveEntityReadyPublisher(
                live.LiveEntities,
                live.EntityEffects,
                live.Presentation,
                live.RenderSceneShadow?.LiveProjections),
            originCoordinator,
            networkUpdateBridge,
            localPhysicsTimestamps,
            d.PlayerIdentity,
            deletion,
            firstEntryDrive,
            acceptedPositionDrive);
        bindings.Adopt(
            "landblock-loaded hydration",
            live.LandblockLoaded.Bind(hydration));
        Fault(SessionPlayerCompositionPoint.HydrationCreated);

        var worldDropProjection =
            new InventoryWorldDropProjectionController(
                interaction.ItemInteraction,
                d.EntityObjects.Objects,
                live.LiveEntities,
                hydration,
                d.Actions.Selection,
                () => d.UpdateClock.SimulationTimeSeconds);
        bindings.Adopt(
            "inventory world-drop projection",
            worldDropProjection);

        var networkUpdates = new LiveEntityNetworkUpdateController(
            live.LiveEntities,
            d.EntityObjects.Objects,
            hydration,
            live.EntityEffects,
            live.Presentation,
            live.Lights,
            live.EquippedChildren,
            live.ProjectileController,
            d.AnimatedEntities,
            d.RemoteMovementObservations,
            d.RemotePhysicsUpdater,
            d.RemoteInboundMotion,
            live.MotionRuntime,
            d.PhysicsEngine,
            content.Dats,
            content.AnimationLoader,
            d.Actions.CombatTarget,
            d.WorldOrigin,
            d.TeleportSink,
            d.PlayerController,
            d.PlayerOutbound,
            d.PlayerHost,
            d.PlayerIdentity,
            d.UpdateClock,
            liveSessionSource,
            localPhysicsTimestamps.Publish,
            d.MovementDiagnostics,
            acceptedPositionDrive,
            remotePlacementDrive,
            worldDropProjection);
        var liveness = new LiveEntityLivenessController(
            live.LiveEntities,
            d.PlayerIdentity,
            deletion);
        var sessionEvents = new LiveEntitySessionController(
            d.InboundEntityEvents,
            hydration,
            networkUpdates,
            d.TeleportSink,
            live.EntityEffects);
        bindings.Adopt(
            "live parent acceptance",
            live.ParentAcceptance.BindOwned(
                hydration.TryAcceptParentForProjection));
        bindings.Adopt(
            "same-generation network updates",
            networkUpdateBridge.BindOwned(networkUpdates));
        bindings.BindEntityReady(
            live.EquippedChildren,
            candidate => _ = hydration.OnEntityReady(candidate));
        bindings.BindAppearanceApplied(
            hydration,
            guid =>
            {
                if (guid == d.PlayerIdentity.ServerGuid)
                    live.PaperdollPresenter?.MarkDirty();
            });
        Fault(SessionPlayerCompositionPoint.LiveEntityGraphBound);

        d.WorldOrigin.SetPlaceholder(
            world.TerrainBuild.InitialCenterX,
            world.TerrainBuild.InitialCenterY);
        bindings.Adopt(
            "combat attack operations",
            d.CombatAttackOperations.BindOwned(
                new LiveCombatAttackOperations(
                    d.Actions.Combat,
                    new CombatAttackTargetSource(
                        d.Actions.Selection,
                        live.LiveEntities,
                        d.EntityObjects.Objects,
                        d.PlayerIdentity),
                    new CharacterOptionCombatSettingsSource(d.Character.Options),
                    d.PlayerController,
                    d.PlayerOutbound,
                    liveSessionSource,
                    liveSessionSource,
                    d.CombatFeedback)));
        bindings.Adopt(
            "combat feedback",
            d.CombatFeedback.BindOwned(
                text => d.Communication.AddText(
                    text, RetailLogTextType.ClientLocal)));
        Fault(SessionPlayerCompositionPoint.CombatOperationsBound);

        MouseLookController? mouseLook =
            host.MouseSource is not null && host.MouseLookCursor is not null
                ? new MouseLookController(
                    host.MouseSource,
                    d.PointerPosition,
                    d.PlayerMode,
                    d.PlayerController,
                    host.CameraController,
                    d.ChaseCameraInput,
                    d.MovementInput,
                    d.PlayerOutbound,
                    liveSessionSource,
                    host.MouseLookCursor,
                    new EnvironmentInputMonotonicClock())
                : null;
        var gameplayInput = new GameplayInputFrameController(
            host.InputDispatcher,
            d.MovementInput,
            mouseLook,
            new CombatAttackInputFrameAdapter(interaction.CombatAttack));
        if (host.CameraPointerInput is { } cameraPointer)
        {
            bindings.Adopt(
                "camera pointer gameplay frame",
                cameraPointer.BindGameplayFrameOwned(gameplayInput));
        }
        Fault(SessionPlayerCompositionPoint.GameplayInputBound);

        var streamingFrame = new StreamingFrameController(
            d.Options.LiveMode,
            d.PlayerMode,
            d.PlayerController,
            liveSessionSource,
            d.WorldOrigin,
            networkUpdates,
            new FlyCameraStreamingObserverSource(host.CameraController),
            new PhysicsStreamingDungeonCellSource(d.PhysicsEngine),
            streamingOriginRecenter,
            streaming,
            new LiveProjectionRescueRebucketter(
                live.WorldState,
                live.LiveEntities));
        IAmbientFramePhase? BuildAmbientFrame()
        {
            if (content.Audio?.Ambient is not { } ambient)
                return null;

            DatReaderWriter.DBObjs.Region? region =
                content.Dats.Get<DatReaderWriter.DBObjs.Region>(0x13000000u);
            if (region is null)
                return null;

            ambient.InstallRegion(region, LoadTerrainWords);
            return new AmbientFramePhase(
                ambient,
                new LocalPlayerAmbientListenerSource(
                    d.PlayerController,
                    indoorLandblockLocal: (cellId, cellLocal) =>
                    {
                        var cellStruct = d.PhysicsDataCache.GetCellStruct(cellId);
                        if (cellStruct is null || !cellStruct.SeenOutside)
                            return null;
                        return System.Numerics.Vector3.Transform(
                            cellLocal,
                            cellStruct.WorldTransform);
                    }));

            ushort[]? LoadTerrainWords(uint landblockId)
            {
                if (!live.WorldState.TryGetLandblock(landblockId, out LoadedLandblock? loaded)
                    || loaded?.Heightmap is not { } heightmap)
                {
                    return null;
                }

                var words = new ushort[heightmap.Terrain.Length];
                for (int i = 0; i < words.Length; i++)
                    words[i] = (ushort)heightmap.Terrain[i];
                return words;
            }
        }

        var liveEffectFrame = new LiveEffectFrameController(
            d.TranslucencyFades,
            content.AnimationHookFrames,
            live.EntityEffects,
            content.ParticleSink,
            live.Lights,
            d.ParticleVisibility,
            content.ParticleSystem,
            content.ScriptRunner,
            d.UpdateClock,
            new SettingsParticleRangeSource(d.Settings),
            BuildAmbientFrame());
        var liveSpatialReconciler = new LiveSpatialPresentationReconciler(
            live.EntityEffects,
            live.EquippedChildren,
            content.ParticleSink,
            live.Lights,
            live.RenderSceneShadow?.LiveProjections);
        var localPlayerAnimation = new LocalPlayerAnimationController(
            live.LiveEntities,
            d.PlayerIdentity,
            d.AnimatedEntities,
            live.AnimationPresenter,
            content.AnimationHookFrames);
        var localPlayerShadow = live.LocalPlayerShadowSynchronizer;
        var localPlayerProjection = new LocalPlayerProjectionController(
            new LiveLocalPlayerProjectionRuntime(
                live.LiveEntities,
                d.PlayerIdentity,
                d.WorldOrigin,
                localPlayerShadow));
        var localPlayerFrameRuntime = new LiveLocalPlayerFrameRuntime(
            host.CameraController,
            d.PlayerMode,
            d.PlayerController,
            d.ChaseCameraInput,
            d.MovementInput,
            d.InputCapture,
            live.LiveEntities,
            d.PlayerIdentity,
            d.PlayerHost,
            localPlayerProjection,
            d.PlayerOutbound,
            liveSessionSource);
        var localPlayerFrame = new RetailLocalPlayerFrameController(
            d.Runtime,
            localPlayerFrameRuntime,
            d.MovementInput);
        var liveObjectFrame = new LiveObjectFrameController(
            d.InboundEntityEvents,
            localPlayerFrame,
            live.SelectionInteractions,
            live.LiveEntities,
            d.PlayerIdentity,
            d.WorldOrigin,
            live.AnimationScheduler,
            live.StaticAnimationScheduler,
            live.AnimationPresenter,
            d.AnimatedEntities,
            live.EquippedChildren,
            liveEffectFrame,
            live.RenderSceneShadow?.LiveProjections,
            live.RenderSceneShadow?.StaticProjections);
        Fault(SessionPlayerCompositionPoint.UpdateLeavesCreated);

        var playerMode = new PlayerModeController(
            d.PlayerMode,
            d.PlayerController,
            d.PlayerHost,
            d.ChaseCameraInput,
            host.CameraController,
            d.PhysicsEngine,
            live.LiveEntities,
            d.PlayerIdentity,
            d.WorldOrigin,
            d.MotionBindings,
            content.Dats,
            d.DatLock,
            content.CollisionAssets,
            d.AnimatedEntities,
            localPlayerAnimation,
            localPlayerShadow,
            d.PlayerApproachCompletions,
            gameplayInput,
            liveSessionSource,
            d.MovementDiagnostics,
            d.Character.MovementSkills,
            d.ViewportAspect);
        var playerModeAutoEntry = new PlayerModeAutoEntry(
            new LivePlayerModeAutoEntryContext(
                liveSessionSource,
                live.LiveEntities,
                d.PlayerIdentity,
                worldReveal,
                d.PlayerMode,
                playerMode));
        playerMode.BindAutoEntry(playerModeAutoEntry);
        Fault(SessionPlayerCompositionPoint.PlayerModeBound);

        LocalPlayerTeleportController localTeleport =
            d.PortalTunnelFallback.Transfer(CreateLocalTeleportWithTunnel);

        LocalPlayerTeleportController CreateLocalTeleport(
            ILocalPlayerTeleportPresentation presentation) =>
            new LocalPlayerTeleportController(
                new LiveLocalPlayerTeleportAuthority(
                    live.LiveEntities,
                    d.PlayerIdentity),
                gameplayInput,
                playerMode,
                new LocalPlayerTeleportStreamingOperations(
                    d.WorldOrigin,
                    streamingOriginRecenter,
                    streaming,
                    sealedDungeonCells),
                live.WorldTransit,
                worldReveal,
                new LocalPlayerTeleportPlacement(
                    live.LiveEntities,
                    d.PlayerIdentity,
                    d.PlayerController,
                    d.PlayerHost,
                    d.ChaseCameraInput,
                    liveSpatialReconciler),
                new LocalPlayerTeleportSession(liveSessionSource),
                presentation,
                acceptedPositionDrive,
                new RuntimeLoginLifecycleSource(d.Runtime),
                new RuntimeLocalPlayerLogoutOperations(
                    d.Runtime,
                    d.PlayerController,
                    liveSessionSource,
                    d.Inventory.Objects,
                    d.PlayerIdentity));

        LocalPlayerTeleportController CreateLocalTeleportWithTunnel(
            PortalTunnelPresentation portalTunnel)
        {
            var tunnelPresentation = new LocalPlayerTeleportPresentation(portalTunnel);
            if (content.Audio?.UiSounds is { } portalUiSounds)
                tunnelPresentation.UiSoundSink = sound => portalUiSounds.Play(sound);
            return CreateLocalTeleport(tunnelPresentation);
        }
        if (content.Audio?.UiSounds is { } environUiSounds)
        {
            d.WorldEnvironment.EnvironSoundSink =
                changeType => environUiSounds.PlayEnvironCue(changeType);
        }
        var teleportLease = scope.Own(
            "local-player teleport",
            localTeleport,
            static value => value.Dispose());
        Fault(SessionPlayerCompositionPoint.PortalTransferred);
        bindings.Adopt(
            "local teleport network sink",
            d.TeleportSink.BindOwned(localTeleport));
        bindings.Adopt(
            "selection view plane",
            interaction.LateBindings.SelectionViewPlane.Bind(localTeleport));
        Fault(SessionPlayerCompositionPoint.TeleportBound);

        IDisposable componentLifecycleBinding =
            live.ComponentLifecycle.BindOwned(teardown);
        IDisposable componentLifecycleAdoption;
        try
        {
            componentLifecycleAdoption = live.RuntimeBindings.AdoptOwned(
                "live runtime component teardown",
                componentLifecycleBinding);
        }
        catch
        {
            componentLifecycleBinding.Dispose();
            throw;
        }
        var componentLifecycleLease = scope.Own(
            "live runtime component teardown adoption",
            componentLifecycleAdoption,
            static value => value.Dispose());

        AcDream.UI.Abstractions.Panels.Vitals.VitalsVM? vitals =
            interaction.RetainedUi?.Vitals;
        var placementProjectionRetry =
            new RuntimePlacementProjectionRetrySlot(
                () => d.Runtime.Generation);
        var sessionRuntimeFactory = new LiveSessionRuntimeFactory(
            new LiveSessionPlayerRuntime(
                d.PlayerIdentity,
                d.PlayerController,
                d.WorldOrigin),
            new LiveSessionDomainRuntime(
                d.Runtime,
                d.EntityObjects,
                d.Character,
                d.Actions,
                d.Inventory,
                d.Communication),
            new LiveSessionUiRuntime(
                interaction.RetainedUi?.Runtime,
                vitals,
                interaction.RetainedUi?.CharacterSheet,
                interaction.Magic,
                live.PaperdollPresenter),
            new LiveSessionInteractionRuntime(
                d.Settings,
                gameplayInput,
                playerMode,
                playerModeAutoEntry,
                interaction.ItemInteraction,
                interaction.CombatAttack,
                live.SelectionInteractions),
            new LiveSessionWorldRuntime(
                content.Dats,
                d.DatLock,
                content.Audio?.Engine is { } sessionAudioEngine
                    ? new AcDream.App.Audio.WorldAudioSessionGate(
                        sessionAudioEngine,
                        content.Audio.Ambient)
                    : null,
                live.WorldState,
                live.LiveEntities,
                sessionEvents,
                d.WorldEnvironment,
                d.TeleportSink,
                spawnClaimClassifier,
                live.EquippedChildren,
                live.SelectionScene,
                d.ParticleVisibility,
                d.InboundEntityEvents,
                liveness,
                networkUpdates,
                hydration,
                live.EntityEffects,
                content.AnimationHookFrames,
                live.Presentation,
                d.RemoteMovementObservations,
                live.RenderSceneShadow,
                live.PlacementProjection,
                placementProjectionRetry,
                firstEntryDrive,
                acceptedPositionDrive,
                remotePlacementDrive),
            liveSessionCommands,
            d.Log,
            d.StatusWriter,
            d.Options.SessionId ?? "app",
            d.Options.LoginCommands,
            d.Options.LoginCommandDelayMs);
        LiveSessionHost sessionHost = sessionRuntimeFactory.Create(
            liveSession,
            new LiveSessionConnectOptions(
                d.Options.LiveMode,
                d.Options.LiveHost,
                d.Options.LivePort,
                d.Options.LiveUser ?? string.Empty,
                d.Options.LivePass ?? string.Empty,
                d.Options.LiveCharacterSelector,
                AwaitCharacterSelection:
                    d.Options.LiveCharacterSelector is null));
        Fault(SessionPlayerCompositionPoint.SessionHostCreated);

        Action<string>? debugToast = null;
        var combatModeOperations = new LiveCombatModeOperations(
            new LiveSessionCombatModeAuthority(sessionHost),
            new LocalPlayerCombatEquipmentSource(
                d.EntityObjects.Objects,
                d.PlayerIdentity),
            new ItemInteractionCombatModeIntentSink(
                interaction.ItemInteraction));
        bindings.Adopt(
            "runtime combat-mode operations",
            d.CombatModeOperations.BindOwned(combatModeOperations));
        var combatCommand = new RuntimeCombatModeCommandAdapter(
            d.Actions.CombatMode,
            d.Log,
            debugToast,
            text => d.Communication.AddText(text, RetailLogTextType.ClientLocal));
        bindings.Adopt(
            "live combat-mode commands",
            d.CombatModeCommands.BindOwned(combatCommand));
        var gameRuntime = new CurrentGameRuntimeAdapter(
            d.Runtime,
            sessionHost,
            liveSessionCommands,
            live.SelectionInteractions);
        bindings.Adopt("current game runtime adapter", gameRuntime);
        bindings.Adopt(
            "retained-UI game runtime commands",
            interaction.LateBindings.GameRuntime.Bind(gameRuntime, gameRuntime));

        var nearbyDiagnostics = new NearbyWorldDiagnosticDumper(
            new RuntimeNearbyWorldDiagnosticSource(
                d.PlayerMode,
                d.PlayerController,
                host.CameraController,
                d.WorldOrigin,
                live.WorldState,
                d.PhysicsEngine),
            d.Log);
        var runtimeDiagnostics = new RuntimeDiagnosticCommandController(
            d.WorldEnvironment,
            d.WorldSceneDebugState,
            host.CameraPointerInput,
            nearbyDiagnostics,
            debugToast);
        bindings.Adopt(
            "runtime diagnostic commands",
            d.RuntimeDiagnosticCommands.BindOwned(runtimeDiagnostics));
        Fault(SessionPlayerCompositionPoint.CommandsBound);

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            GameplayInputActionRouter>? gameplayActionsLease = null;
        if (host.InputDispatcher is { } dispatcher)
        {
            CameraPointerInputController pointer = host.CameraPointerInput
                ?? throw new InvalidOperationException(
                    "Gameplay action routing requires the composed pointer owner.");
            var commands = new GameplayInputCommandController(
                new RetainedGameplayWindowCommands(
                    interaction.RetainedUi?.Runtime),
                runtimeDiagnostics,
                new PlayerModeGameplayCommands(playerMode),
                new ItemTargetModeCommands(interaction.ItemInteraction),
                gameRuntime,
                gameRuntime.Combat,
                toggleAudioMute: content.Audio?.Engine is { } audioEngine
                    ? () =>
                    {
                        audioEngine.Muted = !audioEngine.Muted;
                        Console.WriteLine(
                            audioEngine.Muted
                                ? "audio: muted (Ctrl+M to unmute)"
                                : "audio: unmuted");
                    }
                    : null);
            var targets = new RuntimeGameplayInputPriorityTargets(
                gameplayInput,
                pointer,
                interaction.RetainedUi?.Runtime,
                live.SelectionInteractions,
                gameRuntime,
                gameRuntime.Selection,
                gameRuntime.MovementCommands,
                gameRuntime.CharacterCommands,
                commands);
            GameplayInputActionRouter gameplayActions =
                GameplayInputActionRouter.Create(
                    dispatcher,
                    d.Actions.Combat,
                    targets,
                    d.HostQuiescence,
                    d.Log);
            gameplayActionsLease = scope.Own(
                "gameplay input actions",
                gameplayActions,
                static value => value.Dispose());
            gameplayActions.Attach();
        }
        Fault(SessionPlayerCompositionPoint.GameplayActionsAttached);

        var bindingsLease = scope.Own(
            "session/player runtime bindings",
            bindings,
            static value => value.Dispose());
        bindingsOwnedByScope = true;
        var result = new SessionPlayerResult(
            streamerLease.Resource,
            streaming,
            streamingOriginRecenter,
            worldReveal,
            spawnClaimClassifier,
            liveSession,
            hydration,
            deletion,
            networkUpdates,
            liveness,
            sessionEvents,
            gameplayInput,
            streamingFrame,
            localPlayerAnimation,
            localPlayerShadow,
            localPlayerFrameRuntime,
            localPlayerFrame,
            liveSpatialReconciler,
            liveObjectFrame,
            playerMode,
            playerModeAutoEntry,
            teleportLease.Resource,
            sessionHost,
            placementProjectionRetry,
            gameRuntime,
            gameplayActionsLease?.Resource,
            bindings);
        _publication.PublishSessionPlayer(result);

        streamerLease.Transfer();
        teleportLease.Transfer();
        gameplayActionsLease?.Transfer();
        componentLifecycleLease.Transfer();
        bindingsLease.Transfer();
        Fault(SessionPlayerCompositionPoint.ResultPublished);
        return result;
    }

    private void Fault(SessionPlayerCompositionPoint point) =>
        _faultInjection?.Invoke(point);
}
