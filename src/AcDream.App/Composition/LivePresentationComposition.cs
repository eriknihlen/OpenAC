using System.Numerics;
using AcDream.Content;
using AcDream.App.Diagnostics;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Selection;
using AcDream.App.Rendering.Sky;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.Settings;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.App.World;
using AcDream.Core.Audio;
using AcDream.Core.Items;
using AcDream.Core.Lighting;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.Rendering;
using AcDream.Core.Selection;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.World;
using AcDream.UI.Abstractions.Panels.SpewBox;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using Silk.NET.Windowing;

namespace AcDream.App.Composition;

internal sealed record LivePresentationDependencies(
    RuntimeOptions Options,
    GameWindowGraphics Graphics,
    IWindow Window,
    object DatLock,
    RuntimeSettingsController Settings,
    HostQuiescenceGate HostQuiescence,
    PhysicsEngine PhysicsEngine,
    PhysicsDataCache PhysicsDataCache,
    WorldGameState WorldGameState,
    WorldEvents WorldEvents,
    GameRuntime Runtime,
    LiveEntityRuntimeSlot RuntimeSlot,
    DeferredLiveEntityMotionRuntimeBindings MotionBindings,
    DeferredEntityEffectAdvanceSource EffectAdvance,
    EntityEffectPoseRegistry EffectPoses,
    RemotePhysicsUpdater RemotePhysicsUpdater,
    LocalPlayerShadowState LocalPlayerShadow,
    LiveEntityAnimationRuntimeView<LiveEntityAnimationState> AnimatedEntities,
    AnimationPresentationDiagnostics AnimationDiagnostics,
    EntityClassificationCache ClassificationCache,
    TranslucencyFadeManager TranslucencyFades,
    RetailAlphaQueue RetailAlphaQueue,
    CellVisibility CellVisibility,
    LiveWorldOriginState WorldOrigin,
    LocalPlayerIdentityState PlayerIdentity,
    ChaseCameraInputState ChaseCameraInput,
    PointerPositionState PointerPosition,
    PlayerApproachCompletionState PlayerApproachCompletions,
    GameRenderResourceLifetime RenderResourceLifetime,
    TransferableResourceSlot<PortalTunnelPresentation> PortalTunnelFallback,
    AnimationHookRouter HookRouter,
    IRenderFrameDiagnosticLog RenderDiagnosticLog,
    WorldTimeService WorldTime,
    DeferredCanonicalWorldEntityCountSource? DevWorldEntities,
    DeferredRenderFrameDiagnosticsSource? DevFrameDiagnostics,
    DeferredRenderFrameDiagnosticsSource UiFrameDiagnostics,
    Action<string> Log,
    Action<string>? Toast,
    AcDream.App.Rendering.Packs.IRenderPackDiagnosticsSnapshotSource?
        RenderPackDiagnostics = null)
{
    public SelectionState Selection => Runtime.ActionOwner.Selection;

    public RuntimeEntityObjectLifetime EntityObjects =>
        Runtime.EntityObjects;

    public RuntimeWorldTransitState WorldTransit => Runtime.TransitOwner;

    public RuntimeLocalPlayerMovementState PlayerController =>
        Runtime.MovementOwner;

    public RuntimeCharacterState Character => Runtime.CharacterOwner;
}

internal sealed record LivePresentationResult(
    DeferredLiveEntityRuntimeComponentLifecycle ComponentLifecycle,
    LiveEntityMotionRuntimeController MotionRuntime,
    DeferredLiveEntityParentAcceptance ParentAcceptance,
    EntitySpawnAdapter EntitySpawnAdapter,
    EntityScriptActivator EntityScriptActivator,
    RetailStaticAnimatingObjectScheduler StaticAnimationScheduler,
    RuntimeWorldTransitState WorldTransit,
    WorldGenerationAvailabilityState WorldAvailability,
    GpuWorldState WorldState,
    RenderSceneShadowRuntime? RenderSceneShadow,
    LiveEntityRuntime LiveEntities,
    RuntimePlacementPresentationSink PlacementProjection,
    LocalPlayerShadowSynchronizer LocalPlayerShadowSynchronizer,
    ProjectileController ProjectileController,
    LiveEntityProjectionWithdrawalController ProjectionWithdrawal,
    LiveEntityLightController Lights,
    LiveEntityAnimationScheduler AnimationScheduler,
    LiveEntityAnimationPresenter AnimationPresenter,
    EquippedChildRenderController EquippedChildren,
    EntityEffectController EntityEffects,
    LiveEntityPresentationController Presentation,
    WbDrawDispatcher? DrawDispatcher,
    RetailSelectionScene SelectionScene,
    WorldSelectionQuery SelectionQuery,
    SelectionInteractionController SelectionInteractions,
    RetainedUiGameplayBinding? RetainedGameplay,
    PaperdollViewportRenderer? PaperdollRenderer,
    PaperdollFramePresenter? PaperdollPresenter,
    CreatureAppraisalViewportRenderer? CreatureAppraisalRenderer,
    CreatureAppraisalFramePresenter? CreatureAppraisalPresenter,
    ChargenPreviewRenderer? ChargenPreviewRenderer,
    ChargenPreviewController? ChargenPreviewController,
    ChargenPreviewRenderer? SummaryPreviewRenderer,
    ChargenPreviewController? SummaryPreviewController,
    WbFrustum EnvCellFrustum,
    EnvCellRenderer? EnvCellRenderer,
    LandblockPresentationPipeline LandblockPipeline,
    ClipFrame ClipFrame,
    PortalDepthMaskRenderer? PortalDepthMask,
    SkyRenderer? SkyRenderer,
    ParticleRenderer? ParticleRenderer,
    RenderFrameDiagnosticsController FrameDiagnostics,
    LivePresentationRuntimeBindings RuntimeBindings,
    DeferredLiveEntityLandblockLoadedSink LandblockLoaded);

internal interface IGameWindowLivePresentationPublication
{
    void PublishLivePresentation(LivePresentationResult result);
}

internal enum LivePresentationCompositionPoint
{
    CanonicalRuntimeCreated,
    CanonicalRuntimeBound,
    MotionRuntimeBound,
    ProjectionVisibilityBound,
    CorePresentationCreated,
    EffectRoutingBound,
    SelectionAndRadarBound,
    RetainedGameplayBound,
    PrivateCreatureViewportsCreated,
    EnvironmentCellsCreated,
    LandblockPipelineCreated,
    PortalResourcesCreated,
    SkyAndParticlesCreated,
    DiagnosticsBound,
    ResultPublished,
}

internal sealed class LivePresentationCompositionPhase
    : ILivePresentationCompositionPhase<
        GameWindowPlatformResult<GameWindowGraphics, Silk.NET.Input.IInputContext>,
        HostInputCameraResult,
        ContentEffectsAudioResult,
        SettingsDevToolsResult,
        WorldRenderResult,
        InteractionRetainedUiResult,
        LivePresentationResult>
{
    private readonly LivePresentationDependencies _dependencies;
    private readonly IGameWindowLivePresentationPublication _publication;
    private readonly Action<LivePresentationCompositionPoint>? _faultInjection;

    public LivePresentationCompositionPhase(
        LivePresentationDependencies dependencies,
        IGameWindowLivePresentationPublication publication,
        Action<LivePresentationCompositionPoint>? faultInjection = null)
    {
        _dependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
        _publication = publication
            ?? throw new ArgumentNullException(nameof(publication));
        _faultInjection = faultInjection;
    }

    public LivePresentationResult Compose(
        GameWindowPlatformResult<GameWindowGraphics, Silk.NET.Input.IInputContext> platform,
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        SettingsDevToolsResult settings,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interaction);
        if (!ReferenceEquals(_dependencies.Graphics, platform.Graphics))
        {
            throw new InvalidOperationException(
                "Live-presentation dependencies do not match the ordered platform result.");
        }
        return ComposeCore(host, content, world, interaction);
    }

    private LivePresentationResult ComposeCore(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction)
    {
        LivePresentationDependencies d = _dependencies;
        WorldRenderFoundation foundation = world.Foundation;
        var scope = new CompositionAcquisitionScope();
        LivePresentationRuntimeBindings? bindings = null;
        bool bindingsOwnedByScope = false;

        try
        {
            CompositionAcquisitionScope.CompositionAcquisitionLease<
                RenderSceneShadowRuntime>? renderSceneShadowLease = null;
            renderSceneShadowLease = scope.Acquire(
                "render scene",
                () => new RenderSceneShadowRuntime(
                    RenderSceneGeneration.FromRaw(1)),
                static value => value.Dispose());
            RenderSceneShadowRuntime? renderSceneShadow =
                renderSceneShadowLease?.Resource;

            var componentLifecycle =
                new DeferredLiveEntityRuntimeComponentLifecycle();
            var wbSpawnAdapter = new LandblockSpawnAdapter(
                foundation.MeshAdapter
                ?? throw new InvalidOperationException(
                    "The landblock spawn ledger requires the mesh pipeline, which "
                    + "must be available before the ledger is composed."));
            Setup? LoadPreparedSetup(uint sourceId)
            {
                if (!content.Dats.TryResolvePreferred(
                        sourceId,
                        out IDatDatabase? database,
                        out DatReaderWriter.Enums.DBObjType type)
                    || type != DatReaderWriter.Enums.DBObjType.Setup)
                {
                    return null;
                }

                return database.TryGet<Setup>(
                    sourceId,
                    out Setup? setup)
                    ? setup
                    : null;
            }

            var setupResolver = new PreparedSetupResolver(
                content.PreparedAssets,
                LoadPreparedSetup,
                message => Console.Error.WriteLine(
                    $"setup activation: {message}"));

            AnimationSequencer SequencerFactory(WorldEntity entity)
            {
                if (setupResolver.TryResolve(
                        entity.SourceGfxObjOrSetupId,
                        out Setup? setup))
                {
                    uint motionTableId = (uint)setup.DefaultMotionTable;
                    if (motionTableId != 0
                        && content.Dats.Get<MotionTable>(motionTableId) is { } motionTable)
                    {
                        return new AnimationSequencer(
                            setup,
                            motionTable,
                            content.AnimationLoader);
                    }

                    return new AnimationSequencer(
                        setup,
                        new MotionTable(),
                        content.AnimationLoader);
                }

                return new AnimationSequencer(
                    new Setup(),
                    new MotionTable(),
                    NullAnimationLoader.Instance);
            }

            var entitySpawnAdapter = new EntitySpawnAdapter(
                foundation.TextureCache,
                SequencerFactory,
                foundation.MeshAdapter);
            EntityEffectController? entityEffects = null;
            LiveEntityRuntime? liveEntities = null;
            var staticRootCommitter = new StaticLiveRootCommitter(
                d.RuntimeSlot,
                d.PhysicsEngine.ShadowObjects,
                d.WorldOrigin,
                d.EffectPoses);
            var staticResidency = new LiveStaticAnimationResidency(d.RuntimeSlot);
            (LiveEntityAnimationState Animation, PhysicsBody Body)?
                ResolveLiveStaticOwner(WorldEntity entity)
            {
                if (entity.ServerGuid == 0
                    || liveEntities?.TryGetRecord(
                        entity.ServerGuid,
                        out LiveEntityRecord record) != true
                    || !ReferenceEquals(record.WorldEntity, entity)
                    || !record.IsSpatiallyProjected
                    || !record.IsSpatiallyVisible
                    || record.AnimationRuntime
                        is not LiveEntityAnimationState animation
                    || record.PhysicsBody is not { } body)
                {
                    return null;
                }

                return (animation, body);
            }
            var staticAnimationScheduler =
                new RetailStaticAnimatingObjectScheduler(
                    content.AnimationLoader,
                    content.AnimationHookFrames.Capture,
                    d.EffectPoses.Publish,
                    staticResidency.IsResident,
                    (entity, body) => _ = staticRootCommitter.Commit(entity, body),
                    staticResidency.ProjectionVersion,
                    ResolveLiveStaticOwner);

            ScriptActivationInfo? ResolveActivation(WorldEntity entity)
            {
                if (!setupResolver.TryResolve(
                        entity.SourceGfxObjOrSetupId,
                        out Setup? setup))
                {
                    return null;
                }
                uint scriptId = setup.DefaultScript.DataId;
                if (entity.IndexedPartTransforms.Count == 0)
                {
                    var indexed = IndexedSetupPartPoseBuilder.Build(setup, entity);
                    entity.SetIndexedPartPoses(indexed.Poses, indexed.Available);
                }

                bool usesStaticAnimationWorkset = entity.ServerGuid == 0
                    || (liveEntities?.TryGetRecord(
                            entity.ServerGuid,
                            out LiveEntityRecord liveRecord) == true
                        && (liveRecord.FinalPhysicsState
                            & PhysicsStateFlags.Static) != 0);
                return new ScriptActivationInfo(
                    scriptId,
                    entity.IndexedPartTransforms,
                    EntityEffectProfile.CreateDatStatic(setup),
                    entity.IndexedPartAvailable,
                    setup,
                    (uint)setup.DefaultAnimation,
                    usesStaticAnimationWorkset);
            }

            var entityScriptActivator = new EntityScriptActivator(
                content.ScriptRunner,
                content.ParticleSink,
                d.EffectPoses,
                ResolveActivation,
                (ownerId, entity, profile) =>
                    entityEffects?.OnDatStaticEntityReady(ownerId, entity, profile),
                ownerId =>
                {
                    entityEffects?.OnDatStaticEntityRemoved(ownerId);
                    content.LightingSink.UnregisterOwner(ownerId);
                    d.TranslucencyFades.ClearEntity(ownerId);
                },
                (entity, info) => staticAnimationScheduler.Register(entity, info),
                staticAnimationScheduler.Unregister,
                (entity, info) => staticAnimationScheduler.Rebind(entity, info));

            RuntimeWorldTransitState worldTransit = d.WorldTransit;
            var worldAvailability =
                new WorldGenerationAvailabilityState(worldTransit);
            var worldState = new GpuWorldState(
                wbSpawnAdapter,
                d.ClassificationCache.InvalidateLandblock,
                entityScriptActivator,
                worldAvailability);
            bindings = new LivePresentationRuntimeBindings();
            if (d.DevWorldEntities is { } devWorldEntities)
            {
                bindings.Adopt(
                    "developer world-entity source",
                    devWorldEntities.BindOwned(worldState));
            }
            var resourceOwners =
                new List<CompositeLiveEntityResourceLifecycle.Owner>
                {
                    new(
                        entity =>
                        {
                            if (liveEntities is null
                                || !liveEntities.TryGetRecordByLocalEntityId(
                                    entity.Id,
                                    out LiveEntityRecord record)
                                || record.ProjectionKey is not { } key)
                            {
                                throw new InvalidOperationException(
                                    "Live presentation registration requires an exact Runtime projection key.");
                            }
                            _ = entitySpawnAdapter.OnCreate(key, entity);
                        },
                        entity => _ = entitySpawnAdapter.OnRemove(entity)),
                    new(
                        entityScriptActivator.OnCreate,
                        entityScriptActivator.OnRemove),
                };
            if (renderSceneShadow is not null)
            {
                resourceOwners.Add(new(
                    renderSceneShadow.OnLiveResourceRegistered,
                    renderSceneShadow.OnLiveResourceUnregistered));
            }
            liveEntities = new LiveEntityRuntime(
                worldState,
                new CompositeLiveEntityResourceLifecycle(
                    [.. resourceOwners]),
                componentLifecycle,
                d.EntityObjects);
            var liveRuntimeLease = scope.Own(
                "canonical live-entity runtime",
                liveEntities,
                static runtime => runtime.Clear());
            LiveRenderProjectionJournal? liveRenderProjections =
                renderSceneShadow?.BindLiveRuntime(
                    liveEntities,
                    new GpuWorldRenderTraversalOrderSource(worldState),
                    d.PlayerIdentity);
            Fault(LivePresentationCompositionPoint.CanonicalRuntimeCreated);

            bindings.Adopt(
                "canonical live-runtime slot",
                d.RuntimeSlot.BindOwned(liveEntities));
            Fault(LivePresentationCompositionPoint.CanonicalRuntimeBound);

            var selectionInteractionSource =
                new DeferredSelectionInteractionSource();
            var motionRuntime = new LiveEntityMotionRuntimeController(
                liveEntities,
                d.PhysicsDataCache,
                () => selectionInteractionSource.Current,
                d.Selection,
                d.WorldOrigin);
            bindings.Adopt(
                "live motion runtime",
                d.MotionBindings.BindOwned(motionRuntime));
            Fault(LivePresentationCompositionPoint.MotionRuntimeBound);

            Action<LiveEntityRecord, bool> wbVisibility = (record, visible) =>
            {
                if (record.WorldEntity is { } entity)
                    entitySpawnAdapter.SetPresentationResident(entity, visible);
            };
            bindings.BindProjectionVisibility(
                liveEntities,
                wbVisibility,
                "WB projection visibility");
            if (liveRenderProjections is not null)
            {
                bindings.BindProjectionVisibility(
                    liveEntities,
                    liveRenderProjections.OnProjectionVisibilityChanged,
                    "shadow render projection visibility");
            }
            Action<LiveEntityRecord, bool> particleVisibility = (record, visible) =>
            {
                if (record.WorldEntity is { } entity)
                    content.ParticleSink.SetEntityPresentationVisible(entity.Id, visible);
            };
            bindings.BindProjectionVisibility(
                liveEntities,
                particleVisibility,
                "particle projection visibility");
            var placementVisibilitySinks = new List<
                Action<LiveEntityRecord, bool>>(4)
            {
                wbVisibility,
            };
            if (liveRenderProjections is not null)
            {
                placementVisibilitySinks.Add(
                    liveRenderProjections.OnProjectionVisibilityChanged);
            }
            placementVisibilitySinks.Add(particleVisibility);
            placementVisibilitySinks.Add((record, visible) =>
            {
                if (visible)
                    entityEffects?.OnPresentationBound(record);
            });
            var localPlayerShadowSynchronizer = new LocalPlayerShadowSynchronizer(
                d.PhysicsEngine,
                liveEntities,
                d.PlayerIdentity,
                d.WorldOrigin,
                d.LocalPlayerShadow);
            var placementProjection = new RuntimePlacementPresentationSink(
                liveEntities,
                worldTransit,
                d.WorldGameState,
                d.WorldEvents,
                d.EffectPoses,
                localPlayerShadowSynchronizer,
                () => d.PlayerIdentity.ServerGuid,
                guid =>
                {
                    if (d.Selection.SelectedObjectId == guid)
                    {
                        d.Selection.Clear(
                            SelectionChangeSource.System,
                            SelectionChangeReason.SelectedObjectRemoved);
                    }
                },
                placementVisibilitySinks);
            Fault(LivePresentationCompositionPoint.ProjectionVisibilityBound);

            var projectileController = new ProjectileController(
                liveEntities,
                new DatProjectileSetupResolver(content.Dats, d.DatLock),
                new EntityRootPosePublisher(d.EffectPoses),
                d.WorldOrigin)
            {
                DiagnosticSink = message =>
                    Console.Error.WriteLine($"projectile: {message}"),
            };
            var projectionWithdrawal =
                new LiveEntityProjectionWithdrawalController(
                    liveEntities,
                    projectileController,
                    d.WorldGameState,
                    d.WorldEvents,
                    d.PhysicsEngine.ShadowObjects,
                    d.EffectPoses,
                    d.LocalPlayerShadow);
            var lightsLease = scope.Acquire(
                "live-entity lights",
                () => new LiveEntityLightController(
                    liveEntities,
                    d.EffectPoses,
                    content.LightingSink,
                    setupId => content.Dats.Get<Setup>(setupId)),
                static value => value.Dispose());
            var ordinaryPhysicsUpdater = new LiveEntityOrdinaryPhysicsUpdater(
                d.EntityObjects.Physics,
                d.MotionBindings.GetSetupCylinder,
                d.MotionBindings.GetSetupMoverShape,
                guid => AcDream.Core.Physics.EntityCollisionFlagsExt.ResolveMoverPvpState(
                    d.EntityObjects.Objects,
                    guid));
            var animationScheduler = new LiveEntityAnimationScheduler(
                liveEntities,
                d.PlayerIdentity,
                d.RemotePhysicsUpdater,
                ordinaryPhysicsUpdater,
                projectileController,
                new EntityRootPosePublisher(d.EffectPoses),
                new AnimationHookCaptureSink(content.AnimationHookFrames));
            var animationPresenter = new LiveEntityAnimationPresenter(
                liveEntities,
                staticAnimationScheduler,
                d.EffectPoses,
                new LiveAnimationPresentationContext(
                    liveEntities,
                    d.PlayerIdentity,
                    d.PlayerController),
                d.AnimationDiagnostics,
                d.Options.HidePartIndex);
            var parentAcceptance = new DeferredLiveEntityParentAcceptance();
            var equippedLease = scope.Acquire(
                "equipped-child renderer",
                () => new EquippedChildRenderController(
                    content.Dats,
                    d.DatLock,
                    d.EntityObjects.Objects,
                    liveEntities,
                    d.EffectPoses,
                    parentAcceptance.TryAccept,
                    (childRecord, positionVersion, projectionVersion) =>
                        projectionWithdrawal.WithdrawExact(
                            childRecord,
                            positionVersion,
                            projectionVersion,
                            d.PlayerIdentity.ServerGuid),
                    d.PhysicsEngine.ShadowObjects,
                    d.PhysicsDataCache,
                    content.CollisionAssets.CacheGfxObj),
                static value => value.Dispose());
            Fault(LivePresentationCompositionPoint.CorePresentationCreated);

            var tableResolver = new PhysicsScriptTableResolver(
                id => content.Dats.Get<PhysicsScriptTable>(id));
            entityEffects = new EntityEffectController(
                liveEntities,
                content.ScriptRunner,
                tableResolver,
                d.EffectPoses,
                (parentLocalId, partIndex) =>
                    equippedLease.Resource.FindChildLocalIdAtPart(
                        parentLocalId,
                        partIndex),
                equippedLease.Resource.FindParentLocalId,
                ownerId => content.Audio?.EntitySoundTables.Remove(ownerId),
                (ownerId, soundTableDid) =>
                {
                    content.Audio?.EntitySoundTables.Remove(ownerId);
                    if (soundTableDid is { } did)
                        content.Audio?.EntitySoundTables.Set(ownerId, did);
                },
                (ownerId, worldPosition, soundType, wireVolume) =>
                    content.Audio?.HookSink?.PlayServerSound(
                        ownerId,
                        worldPosition,
                        soundType,
                        wireVolume));
            bindings.Adopt(
                "entity-effect advance",
                d.EffectAdvance.BindOwned(entityEffects));
            entityEffects.DiagnosticSink = message =>
                Console.Error.WriteLine($"vfx: {message}");
            var partArrayLifecycle = new LiveEntityPartArrayLifecycle(
                d.AnimatedEntities);
            var presentationLease = scope.Acquire(
                "live-entity presentation",
                () => new LiveEntityPresentationController(
                    liveEntities,
                    d.PhysicsEngine.ShadowObjects,
                    entityEffects.PlayTypedFromHiddenTransition,
                    new LiveEntityPartArrayEnterWorldPort(
                        partArrayLifecycle.HandleEnterWorld),
                    equippedLease.Resource.SetDirectChildrenNoDraw,
                    d.MotionBindings.ClearTargetForHiddenEntity,
                    d.WorldOrigin.GetCenter),
                static value => value.Dispose());
            bindings.Adopt(
                "live-entity pvp bitfield sync",
                new LiveEntityPvpBitfieldSync(
                    d.EntityObjects.Objects,
                    liveEntities,
                    d.PhysicsEngine.ShadowObjects));
            bindings.BindProjectionPoseReady(
                equippedLease.Resource,
                lightsLease.Resource.OnAttachedPoseReady);
            if (liveRenderProjections is not null)
            {
                bindings.BindProjectionPoseReady(
                    equippedLease.Resource,
                    liveRenderProjections.OnProjectionPoseReady);
                bindings.BindProjectionRemoved(
                    equippedLease.Resource,
                    liveRenderProjections.OnProjectionRemoved);
            }
            bindings.Adopt(
                "entity-effect animation hooks",
                content.HookRegistrations.RegisterOwned(entityEffects));
            Fault(LivePresentationCompositionPoint.EffectRoutingBound);

            return CompletePresentation(
                host,
                content,
                world,
                interaction,
                componentLifecycle,
                motionRuntime,
                parentAcceptance,
                entitySpawnAdapter,
                entityScriptActivator,
                staticAnimationScheduler,
                worldTransit,
                worldAvailability,
                worldState,
                renderSceneShadow,
                renderSceneShadowLease,
                liveEntities,
                placementProjection,
                localPlayerShadowSynchronizer,
                projectileController,
                projectionWithdrawal,
                lightsLease,
                animationScheduler,
                animationPresenter,
                equippedLease,
                entityEffects,
                presentationLease,
                selectionInteractionSource,
                bindings,
                scope,
                ref bindingsOwnedByScope,
                liveRuntimeLease);
        }
        catch (Exception failure)
        {
            if (bindings is not null && !bindingsOwnedByScope)
            {
                scope.Own(
                    "live-presentation runtime bindings",
                    bindings,
                    static value => value.Dispose());
                bindingsOwnedByScope = true;
            }

            scope.RollbackAndThrow(failure);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private LivePresentationResult CompletePresentation(
        HostInputCameraResult host,
        ContentEffectsAudioResult content,
        WorldRenderResult world,
        InteractionRetainedUiResult interaction,
        DeferredLiveEntityRuntimeComponentLifecycle componentLifecycle,
        LiveEntityMotionRuntimeController motionRuntime,
        DeferredLiveEntityParentAcceptance parentAcceptance,
        EntitySpawnAdapter entitySpawnAdapter,
        EntityScriptActivator entityScriptActivator,
        RetailStaticAnimatingObjectScheduler staticAnimationScheduler,
        RuntimeWorldTransitState worldTransit,
        WorldGenerationAvailabilityState worldAvailability,
        GpuWorldState worldState,
        RenderSceneShadowRuntime? renderSceneShadow,
        CompositionAcquisitionScope.CompositionAcquisitionLease<
            RenderSceneShadowRuntime>? renderSceneShadowLease,
        LiveEntityRuntime liveEntities,
        RuntimePlacementPresentationSink placementProjection,
        LocalPlayerShadowSynchronizer localPlayerShadowSynchronizer,
        ProjectileController projectileController,
        LiveEntityProjectionWithdrawalController projectionWithdrawal,
        CompositionAcquisitionScope.CompositionAcquisitionLease<LiveEntityLightController> lightsLease,
        LiveEntityAnimationScheduler animationScheduler,
        LiveEntityAnimationPresenter animationPresenter,
        CompositionAcquisitionScope.CompositionAcquisitionLease<EquippedChildRenderController> equippedLease,
        EntityEffectController entityEffects,
        CompositionAcquisitionScope.CompositionAcquisitionLease<LiveEntityPresentationController> presentationLease,
        DeferredSelectionInteractionSource selectionInteractionSource,
        LivePresentationRuntimeBindings bindings,
        CompositionAcquisitionScope scope,
        ref bool bindingsOwnedByScope,
        CompositionAcquisitionScope.CompositionAcquisitionLease<LiveEntityRuntime> liveRuntimeLease)
    {
        LivePresentationDependencies d = _dependencies;
        WorldRenderFoundation foundation = world.Foundation;
        AlphaScratchBudgetProfile alphaScratchBudgets =
            AlphaScratchBudgetProfile.Create(
                d.Options.ResidencyBudgets.AlphaScratchBytes);

        var selectionScene = new RetailSelectionScene(
            new RetailSelectionGeometryCache(content.Dats, d.DatLock));
        IWorldPassScope? worldPassScope = d.Graphics.WorldPassScope;
        var dispatcherLease = scope.Acquire(
            "WB draw dispatcher",
            () => new WbDrawDispatcher(
                host.GpuDevice,
                host.GpuFrameLifetime,
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope."),
                foundation.TextureCache,
                foundation.MeshAdapter!,
                entitySpawnAdapter,
                d.ClassificationCache,
                d.TranslucencyFades,
                selectionScene,
                d.RetailAlphaQueue,
                alphaScratchBudgets.DispatcherBytes,
                foundation.TerrainAtlas?.BuildingDetailTexture ?? default,
                () => d.Settings.DisplayPreview.BuildingDetailTextures,
                serverGuid => serverGuid != 0u
                    && serverGuid == d.PlayerIdentity.ServerGuid
                        ? d.ChaseCameraInput.Retail?.PlayerTranslucency
                            ?? (d.ChaseCameraInput.Legacy?.IsInHead == true ? 1f : 0f)
                        : 0f),
            static value => value.Dispose());
        var selectionQuery = new WorldSelectionQuery(
            liveEntities,
            d.EntityObjects.Objects,
            selectionScene,
            () => d.PlayerIdentity.ServerGuid,
            interaction.LateBindings.SelectionCamera.Snapshot,
            () => new Vector2(
                d.PointerPosition.X,
                d.PointerPosition.Y),
            () => d.PlayerController.Controller is { } player
                ? new PlayerInteractionPose(player.CellId, player.Position)
                : null,
            d.MotionBindings.GetSetupCylinder,
            setupId =>
            {
                lock (d.DatLock)
                {
                    if (!content.Dats.TryGet<Setup>(setupId, out Setup? setup)
                        || setup.SelectionSphere is not { } sphere)
                    {
                        return null;
                    }
                    return (sphere.Origin, sphere.Radius);
                }
            },
            localEntityId =>
                d.EffectPoses.TryGetRootPose(localEntityId, out Matrix4x4 childRoot)
                    ? childRoot
                    : null,
            hasOpenedCorpse:
                d.Runtime.InventoryOwner.ExternalContainers.HasCorpseBeenOpened,
            combatMode: () => d.Runtime.ActionOwner.Combat.CurrentMode,
            isFellow: guid => d.Runtime.Fellowship.TryGetMember(guid, out _));
        var radarSnapshotProvider = new RadarSnapshotProvider(
            d.EntityObjects.Objects,
            liveEntities,
            () => liveEntities.Snapshots,
            playerGuid: () => d.PlayerIdentity.ServerGuid,
            playerYawRadians: () => d.PlayerController.Controller?.Yaw ?? 0f,
            playerCellId: () => d.PlayerController.Controller?.CellId ?? 0u,
            selectedGuid: () => d.Selection.SelectedObjectId,
            coordinatesOnRadar: () => d.Character.Options.GetOptionBit(
                CharacterOptionId.CoordinatesOnRadar),
            uiLocked: () => d.Character.Options.GetOptionBit(
                CharacterOptionId.LockUI),
            // Fellows and the fellowship leader take their own blip colours;
            // without this the radar never learned who was in the fellowship.
            relationshipFor: guid => new AcDream.Core.Ui.RadarRelationshipTraits(
                IsFellowshipMember: d.Runtime.Fellowship.TryGetMember(guid, out _),
                IsFellowshipLeader: d.Runtime.Fellowship.Snapshot.LeaderGuid == guid),
            spatialQuery: () => worldState);
        bindings.Adopt(
            "radar snapshot",
            interaction.LateBindings.Radar.Bind(radarSnapshotProvider));
        var selectionInteractions = new SelectionInteractionController(
            d.Selection,
            selectionQuery,
            interaction.ItemInteraction,
            new WorldSessionSelectionInteractionTransport(
                () => interaction.LateBindings.Session.CurrentSession),
            new PlayerInteractionMovementSink(
                () => d.PlayerController.Controller,
                d.PlayerApproachCompletions),
            d.Toast,
            d.PlayerApproachCompletions,
            splitStack: guid =>
                interaction.RetainedUi?.Runtime.SelectedObjectController?
                    .FocusSplitStackEntry(guid) ?? false,
            fellowshipMembers: () =>
                d.Runtime.Fellowship.GetMembers().Select(static member => member.Guid),
            combatTarget: d.Runtime.ActionOwner.CombatTarget);
        selectionInteractionSource.Bind(selectionInteractions);
        bindings.Adopt(
            "world selection",
            interaction.LateBindings.Selection.Bind(
                selectionQuery,
                selectionInteractions));
        Fault(LivePresentationCompositionPoint.SelectionAndRadarBound);

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            RetainedUiGameplayBinding>? retainedGameplayLease = null;
        if (interaction.RetainedUi is { } retainedUi)
        {
            retainedGameplayLease = scope.Acquire(
                "retained gameplay binding",
                () => RetainedUiGameplayBinding.Create(
                    retainedUi.Host.Root,
                    (item, x, y) =>
                        selectionInteractions.PlaceDraggedItem(item, x, y),
                    d.HostQuiescence),
                static value => value.Dispose());
            retainedGameplayLease.Resource.Attach();
        }
        Fault(LivePresentationCompositionPoint.RetainedGameplayBound);
        if (dispatcherLease.Resource is { } alphaDispatcher)
        {
            alphaDispatcher.AlphaToCoverage =
                d.Settings.ResolvedQuality.AlphaToCoverage;
        }

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            PaperdollViewportRenderer>? paperdollLease = null;
        PaperdollFramePresenter? paperdollPresenter = null;
        if (dispatcherLease.Resource is { } paperdollDispatcher
            && interaction.RetainedUi?.Runtime.PaperdollViewportWidget is { } viewport
            && interaction.RetainedUi.Runtime.InventoryFrame is { } inventoryFrame)
        {
            paperdollLease = scope.Acquire(
                "paperdoll viewport",
                () => new PaperdollViewportRenderer(
                    worldPassScope
                        ?? throw new InvalidOperationException(
                            "The graphics backend must publish a world pass scope."),
                    host.GpuDevice,
                    host.GpuFrameLifetime,
                    paperdollDispatcher,
                    foundation.SceneLighting!,
                    foundation.TextureCache,
                    foundation.MeshAdapter!),
                static value => value.Dispose());
            IUiViewportRenderer? previousRenderer = viewport.Renderer;
            viewport.Renderer = paperdollLease.Resource;
            bindings.AdoptRelease(
                "paperdoll viewport target",
                () =>
                {
                    if (ReferenceEquals(viewport.Renderer, paperdollLease.Resource))
                        viewport.Renderer = previousRenderer;
                });
            paperdollPresenter = new PaperdollFramePresenter(
                paperdollLease.Resource,
                new RetailPaperdollFrameView(
                    viewport,
                    new PaperdollInventoryVisibility(inventoryFrame)),
                new RetailPaperdollDollFactory(
                    new LivePaperdollEntityLookup(liveEntities),
                    d.PlayerIdentity,
                    new RetailPaperdollPoseApplicator(
                        content.Dats,
                        content.AnimationLoader,
                        d.DatLock)));
        }

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            CreatureAppraisalViewportRenderer>? creatureAppraisalLease = null;
        CreatureAppraisalFramePresenter? creatureAppraisalPresenter = null;
        if (dispatcherLease.Resource is { } appraisalDispatcher
            && interaction.RetainedUi?.Runtime.CreatureAppraisalViewportWidget
                is { } creatureViewport
            && interaction.RetainedUi.Runtime.ExaminationFrame
                is { } examinationFrame
            && interaction.RetainedUi.Runtime.AppraisalController
                is { } appraisalController)
        {
            creatureAppraisalLease = scope.Acquire(
                "creature appraisal viewport",
                () => new CreatureAppraisalViewportRenderer(
                    worldPassScope
                        ?? throw new InvalidOperationException(
                            "The graphics backend must publish a world pass scope."),
                    host.GpuDevice,
                    host.GpuFrameLifetime,
                    appraisalDispatcher,
                    foundation.SceneLighting!,
                    foundation.TextureCache,
                    foundation.MeshAdapter!),
                static value => value.Dispose());
            IUiViewportRenderer? previousRenderer = creatureViewport.Renderer;
            creatureViewport.Renderer = creatureAppraisalLease.Resource;
            bindings.AdoptRelease(
                "creature appraisal viewport target",
                () =>
                {
                    if (ReferenceEquals(
                            creatureViewport.Renderer,
                            creatureAppraisalLease.Resource))
                    {
                        creatureViewport.Renderer = previousRenderer;
                    }
                });
            creatureAppraisalPresenter = new CreatureAppraisalFramePresenter(
                creatureAppraisalLease.Resource,
                new RetailCreatureAppraisalFrameView(
                    creatureViewport,
                    examinationFrame,
                    appraisalController),
                new RetailCreatureAppraisalCloneFactory(
                    new LiveCreatureAppraisalEntityLookup(liveEntities)));
        }

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            ChargenPreviewRenderer>? chargenPreviewLease = null;
        ChargenPreviewController? chargenPreviewController = null;
        if (dispatcherLease.Resource is { } chargenDispatcher
            && interaction.RetainedUi?.Runtime.ChargenPreviewViewportWidget is { } chargenViewport)
        {
            var chargenCamera = new ChargenPreviewCamera();
            chargenPreviewLease = scope.Acquire(
                "chargen preview viewport",
                () => new ChargenPreviewRenderer(
                    worldPassScope
                        ?? throw new InvalidOperationException(
                            "The graphics backend must publish a world pass scope."),
                    host.GpuDevice,
                    host.GpuFrameLifetime,
                    chargenDispatcher,
                    foundation.SceneLighting!,
                    foundation.TextureCache,
                    foundation.MeshAdapter!,
                    camera: chargenCamera),
                static value => value.Dispose());
            IUiViewportRenderer? previousChargenRenderer = chargenViewport.Renderer;
            chargenViewport.Renderer = chargenPreviewLease.Resource;
            bindings.AdoptRelease(
                "chargen preview viewport target",
                () =>
                {
                    if (ReferenceEquals(chargenViewport.Renderer, chargenPreviewLease.Resource))
                        chargenViewport.Renderer = previousChargenRenderer;
                });

            var chargenCatalog = new AcDream.Content.CharGen.ChargenAppearanceCatalog(content.Dats);
            chargenPreviewController = new ChargenPreviewController(
                chargenPreviewLease.Resource,
                chargenCamera,
                new RetailChargenPreviewFrameView(
                    chargenViewport,
                    new RetailChargenPreviewPageVisibility(interaction.RetainedUi.Runtime)),
                content.Dats,
                content.AnimationLoader,
                chargenCatalog,
                chargenCatalog,
                d.DatLock);
            interaction.RetainedUi.Runtime.ChargenPreviewControl = chargenPreviewController;
            interaction.RetainedUi.Runtime.ChargenPalSetSource = chargenCatalog;
            interaction.RetainedUi.Runtime.ChargenClothingTableSource = chargenCatalog;
            interaction.RetainedUi.Runtime.ChargenPaletteColorSource = chargenCatalog;
            var chargenSwatchTextures = new AcDream.App.UI.Layout.ChargenColorSpotComposer(
                content.Dats, foundation.TextureCache);
            interaction.RetainedUi.Runtime.ChargenSwatchTextureSource = chargenSwatchTextures;
            bindings.AdoptRelease(
                "chargen preview control",
                () =>
                {
                    if (ReferenceEquals(
                            interaction.RetainedUi.Runtime.ChargenPreviewControl,
                            chargenPreviewController))
                    {
                        interaction.RetainedUi.Runtime.ChargenPreviewControl = null;
                    }
                });
        }
        else if (dispatcherLease.Resource is not null && interaction.RetainedUi is not null)
        {
            Console.WriteLine(
                "[UI] chargen preview viewport unavailable at composition "
                + "time — the Appearance page's zoom/rotate controls and "
                + "3D preview will not function this session.");
        }

        CompositionAcquisitionScope.CompositionAcquisitionLease<
            ChargenPreviewRenderer>? summaryPreviewLease = null;
        ChargenPreviewController? summaryPreviewController = null;
        if (dispatcherLease.Resource is { } summaryDispatcher
            && interaction.RetainedUi?.Runtime.SummaryPreviewViewportWidget is { } summaryViewport)
        {
            var summaryCamera = new ChargenPreviewCamera();
            summaryPreviewLease = scope.Acquire(
                "summary preview viewport",
                () => new ChargenPreviewRenderer(
                    worldPassScope
                        ?? throw new InvalidOperationException(
                            "The graphics backend must publish a world pass scope."),
                    host.GpuDevice,
                    host.GpuFrameLifetime,
                    summaryDispatcher,
                    foundation.SceneLighting!,
                    foundation.TextureCache,
                    foundation.MeshAdapter!,
                    camera: summaryCamera,
                    renderId: AcDream.App.Rendering.ChargenPreviewEntityBuilder.SummaryPreviewRenderId,
                    backdropRenderId: AcDream.App.Rendering.ChargenPreviewEntityBuilder.SummaryPreviewBackdropRenderId),
                static value => value.Dispose());
            IUiViewportRenderer? previousSummaryRenderer = summaryViewport.Renderer;
            summaryViewport.Renderer = summaryPreviewLease.Resource;
            bindings.AdoptRelease(
                "summary preview viewport target",
                () =>
                {
                    if (ReferenceEquals(summaryViewport.Renderer, summaryPreviewLease.Resource))
                        summaryViewport.Renderer = previousSummaryRenderer;
                });

            var summaryCatalog = new AcDream.Content.CharGen.ChargenAppearanceCatalog(content.Dats);
            summaryPreviewController = new ChargenPreviewController(
                summaryPreviewLease.Resource,
                summaryCamera,
                new RetailChargenPreviewFrameView(
                    summaryViewport,
                    new RetailSummaryPreviewPageVisibility(interaction.RetainedUi.Runtime)),
                content.Dats,
                content.AnimationLoader,
                summaryCatalog,
                summaryCatalog,
                d.DatLock,
                useZoomedOutEye: true,
                renderId: AcDream.App.Rendering.ChargenPreviewEntityBuilder.SummaryPreviewRenderId,
                backdropRenderId: AcDream.App.Rendering.ChargenPreviewEntityBuilder.SummaryPreviewBackdropRenderId);
            interaction.RetainedUi.Runtime.SummaryPreviewControl = summaryPreviewController;
            bindings.AdoptRelease(
                "summary preview control",
                () =>
                {
                    if (ReferenceEquals(
                            interaction.RetainedUi.Runtime.SummaryPreviewControl,
                            summaryPreviewController))
                    {
                        interaction.RetainedUi.Runtime.SummaryPreviewControl = null;
                    }
                });
        }
        else if (dispatcherLease.Resource is not null && interaction.RetainedUi is not null)
        {
            Console.WriteLine(
                "[UI] summary preview viewport unavailable at composition "
                + "time — the Summary page's 3D preview will not function "
                + "this session.");
        }
        Fault(LivePresentationCompositionPoint.PrivateCreatureViewportsCreated);

        var envCellFrustum = new WbFrustum();
        var envCellLease = scope.Acquire(
            "environment-cell renderer",
            () => new EnvCellRenderer(
                host.GpuDevice,
                host.GpuFrameLifetime,
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope."),
                foundation.MeshAdapter!.MeshManager!,
                envCellFrustum,
                foundation.TerrainAtlas?.EnvironmentDetailTexture ?? default,
                () => d.Settings.DisplayPreview.BuildingDetailTextures),
            static value => value.Dispose());
        Fault(LivePresentationCompositionPoint.EnvironmentCellsCreated);

        TerrainModernRenderer? terrainRenderer = foundation.Terrain;
        EnvCellRenderer? envCells = envCellLease.Resource;
        var landblockRenderPublisher = new LandblockRenderPublisher(
            (landblockId, meshData, origin) =>
                terrainRenderer?.AddLandblockWithMesh(
                    landblockId,
                    meshData,
                    origin),
            landblockId => terrainRenderer?.RemoveLandblock(landblockId),
            d.CellVisibility,
            worldState,
            prepareEnvCells: build =>
            {
                if (foundation.MeshAdapter?.MeshManager is { } envCellMeshes)
                    EnvCellMeshPreparationScheduler.Schedule(build, envCellMeshes);
            },
            removeEnvCells: landblockId => envCells?.RemoveLandblock(landblockId),
            envCellPublisher: envCells);
        var landblockPhysicsPublisher = new LandblockPhysicsPublisher(
            d.EntityObjects.Physics,
            world.TerrainBuild.HeightTable);
        var landblockStaticPublisher =
            new LandblockStaticPresentationPublisher(
                content.LightingSink,
                d.TranslucencyFades,
                d.WorldGameState,
                d.WorldEvents);
        var landblockRetirementOwner =
            new LandblockPresentationRetirementOwner(
                landblockRenderPublisher,
                landblockPhysicsPublisher,
                landblockStaticPublisher,
                content.LightingSink,
                d.TranslucencyFades);
        var landblockLoaded = new DeferredLiveEntityLandblockLoadedSink();
        var landblockPipeline = new LandblockPresentationPipeline(
            landblockRenderPublisher,
            landblockPhysicsPublisher,
            landblockStaticPublisher,
            worldState,
            landblockRetirementOwner,
            landblockLoaded.OnLandblockLoaded,
            landblockRenderPublisher.PrepareAfterRenderPins,
            renderSceneShadow?.StaticProjections);
        Fault(LivePresentationCompositionPoint.LandblockPipelineCreated);

        var clipFrameLease = scope.Acquire(
            "portal clip frame",
            ClipFrame.NoClip,
            static value => value.Dispose());
        var portalDepthLease = scope.Acquire(
            "portal depth mask",
            () => new PortalDepthMaskRenderer(
                host.GpuDevice,
                host.GpuFrameLifetime,
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope.")),
            static value => value.Dispose());
        Action<string> displayPortalWaitNotice = text =>
            d.Runtime.CommunicationOwner.AddText(
                text,
                AcDream.Core.Chat.RetailLogTextType.ClientLocal);
        CompositionAcquisitionScope.CompositionAcquisitionLease<
            SpewBoxController>? spewBoxLease = null;
        if (interaction.RetainedUi is { } spewBoxRetainedUi)
        {
            RetailUiAssets spewBoxAssets = spewBoxRetainedUi.Runtime.Assets;
            UiDatFont? spewBoxFont =
                spewBoxAssets.ResolveFont(SpewBoxController.RetailFontId);
            spewBoxLease = scope.Acquire(
                "spew box",
                () => new SpewBoxController(
                    spewBoxRetainedUi.Host.Root,
                    new SpewBoxVM(d.Runtime.CommunicationOwner.SpewBox),
                    spewBoxFont,
                    spewBoxAssets.DebugFont,
                    isGameplayActive: () => d.Settings.IsGameplayDisplay),
                static value => value.Dispose());
        }
        CompositionAcquisitionScope.CompositionAcquisitionLease<
            PortalTunnelPresentation>? portalTunnelLease = null;
        if (dispatcherLease.Resource is { } portalDispatcher)
        {
            PortalTunnelPresentation portalTunnel;
            try
            {
                portalTunnel = d.PortalTunnelFallback.AcquirePrepared(
                    () => PortalTunnelPresentation.CreateRequired(
                        worldPassScope
                            ?? throw new InvalidOperationException(
                                "The graphics backend must publish a world pass scope."),
                        host.GpuFrameLifetime,
                        content.Dats,
                        content.AnimationLoader,
                        new AcDream.App.Audio.UiPresentationHookSink(
                            d.HookRouter,
                            content.Audio?.HookSink),
                        portalDispatcher,
                        foundation.SceneLighting!,
                        foundation.MeshAdapter!,
                        displayPortalWaitNotice),
                    static tunnel => tunnel.PrepareResources());
            }
            catch (Exception acquisitionFailure)
            {
                try
                {
                    d.PortalTunnelFallback.ReleaseFallback();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Portal-tunnel construction and fallback rollback both failed.",
                        acquisitionFailure,
                        cleanupFailure);
                }

                throw;
            }
            portalTunnelLease = scope.Own(
                "portal tunnel fallback",
                portalTunnel,
                _ => d.PortalTunnelFallback.ReleaseFallback());
        }
        Fault(LivePresentationCompositionPoint.PortalResourcesCreated);

        var skyLease = scope.Acquire(
            "sky renderer",
            () => new SkyRenderer(
                host.GpuDevice,
                host.GpuFrameLifetime,
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope."),
                content.Dats,
                foundation.TextureCache)
            {
                AnimationPhaseSecondsOverride = d.Options.SkyAnimationPhaseSeconds,
            },
            static value => value.Dispose());
        var particleLease = scope.AcquireOptional(
            "particle renderer",
            () => new ParticleRenderer(
                host.GpuDevice,
                host.GpuFrameLifetime,
                worldPassScope
                    ?? throw new InvalidOperationException(
                        "The graphics backend must publish a world pass scope."),
                content.ParticleSystem,
                foundation.TextureCache,
                content.Dats,
                foundation.MeshAdapter!,
                d.RetailAlphaQueue,
                alphaScratchBudgets.ParticleBytes),
            static value => value.Dispose());
        Fault(LivePresentationCompositionPoint.SkyAndParticlesCreated);

        IRenderFrameResourceDiagnosticsSource? resourceDiagnostics =
            d.Options.UiProbeDump
            && dispatcherLease.Resource is { } diagnosticDispatcher
            && envCellLease.Resource is { } diagnosticEnvCells
            && particleLease.Resource is { } diagnosticParticles
            && portalDepthLease.Resource is { } diagnosticPortalDepth
                ? new RuntimeRenderFrameResourceDiagnosticsSource(
                    content.ParticleSystem,
                    content.ParticleSink,
                    diagnosticDispatcher,
                    diagnosticEnvCells,
                    diagnosticParticles,
                    interaction.RetainedUi?.Host.TextRenderer,
                    diagnosticPortalDepth,
                    clipFrameLease.Resource,
                    foundation.Terrain!,
                    foundation.SceneLighting!,
                    foundation.MeshAdapter!,
                    foundation.TextureCache,
                    content.PreparedAssets)
                : null;
        var frameDiagnostics = new RenderFrameDiagnosticsController(
            new RuntimeRenderFrameTitleFactsSource(
                worldState,
                d.AnimatedEntities,
                d.WorldTime),
            new SilkRenderFrameTitleSink(d.Window),
            d.RenderDiagnosticLog,
            d.Options.UiProbeDump,
            resourceDiagnostics,
            d.RenderPackDiagnostics);
        if (d.DevFrameDiagnostics is { } devFrameDiagnostics)
        {
            bindings.Adopt(
                "developer frame diagnostics",
                devFrameDiagnostics.BindOwned(frameDiagnostics));
        }
        bindings.Adopt(
            "retained-UI frame diagnostics",
            d.UiFrameDiagnostics.BindOwned(frameDiagnostics));
        Fault(LivePresentationCompositionPoint.DiagnosticsBound);

        var bindingsLease = scope.Own(
            "live-presentation runtime bindings",
            bindings,
            static value => value.Dispose());
        bindingsOwnedByScope = true;
        var result = new LivePresentationResult(
            componentLifecycle,
            motionRuntime,
            parentAcceptance,
            entitySpawnAdapter,
            entityScriptActivator,
            staticAnimationScheduler,
            worldTransit,
            worldAvailability,
            worldState,
            renderSceneShadow,
            liveEntities,
            placementProjection,
            localPlayerShadowSynchronizer,
            projectileController,
            projectionWithdrawal,
            lightsLease.Resource,
            animationScheduler,
            animationPresenter,
            equippedLease.Resource,
            entityEffects,
            presentationLease.Resource,
            dispatcherLease.Resource,
            selectionScene,
            selectionQuery,
            selectionInteractions,
            retainedGameplayLease?.Resource,
            paperdollLease?.Resource,
            paperdollPresenter,
            creatureAppraisalLease?.Resource,
            creatureAppraisalPresenter,
            chargenPreviewLease?.Resource,
            chargenPreviewController,
            summaryPreviewLease?.Resource,
            summaryPreviewController,
            envCellFrustum,
            envCellLease.Resource,
            landblockPipeline,
            clipFrameLease.Resource,
            portalDepthLease.Resource,
            skyLease.Resource,
            particleLease.Resource,
            frameDiagnostics,
            bindings,
            landblockLoaded);
        foundation.Residency.RegisterDomainSource(
            new DelegateResidencyDomainSource(
                ResidencyDomain.AlphaScratch,
                () => new ResidencyDomainSnapshot(
                    ResidencyDomain.AlphaScratch,
                    EntryCount: 3,
                    OwnerCount: 3,
                    Charges: new ResidencyCharges(
                        ScratchBytes: checked(
                            d.RetailAlphaQueue.RetainedScratchBytes
                            + (dispatcherLease.Resource?.RetainedAlphaScratchBytes ?? 0)
                            + (particleLease.Resource?.RetainedAlphaScratchBytes ?? 0))),
                    BudgetBytes: alphaScratchBudgets.TotalBytes)));
        _publication.PublishLivePresentation(result);

        liveRuntimeLease.Transfer();
        renderSceneShadowLease?.Transfer();
        lightsLease.Transfer();
        equippedLease.Transfer();
        presentationLease.Transfer();
        dispatcherLease.Transfer();
        retainedGameplayLease?.Transfer();
        paperdollLease?.Transfer();
        creatureAppraisalLease?.Transfer();
        chargenPreviewLease?.Transfer();
        summaryPreviewLease?.Transfer();
        envCellLease.Transfer();
        clipFrameLease.Transfer();
        portalDepthLease.Transfer();
        portalTunnelLease?.Transfer();
        spewBoxLease?.Transfer();
        skyLease.Transfer();
        particleLease.Transfer();
        bindingsLease.Transfer();
        Fault(LivePresentationCompositionPoint.ResultPublished);
        scope.Complete();
        return result;
    }

    private void Fault(LivePresentationCompositionPoint point) =>
        _faultInjection?.Invoke(point);

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public static NullAnimationLoader Instance { get; } = new();
        public Animation? LoadAnimation(uint id) => null;
    }

    private sealed class DeferredSelectionInteractionSource
    {
        public SelectionInteractionController? Current { get; private set; }

        public void Bind(SelectionInteractionController value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Current is not null)
            {
                throw new InvalidOperationException(
                    "Live motion selection interactions are already bound.");
            }
            Current = value;
        }
    }
}
