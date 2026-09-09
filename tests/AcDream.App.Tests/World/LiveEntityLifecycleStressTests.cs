using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using DatPhysicsScript = DatReaderWriter.DBObjs.PhysicsScript;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityLifecycleStressTests
{
    private const int OwnerCount = 96;
    private const int ReusedOwnerCount = 32;
    private const uint GuidBase = 0x72000000u;
    private const uint CellA = 0x01010001u;
    private const uint CellB = 0x01020001u;
    private const uint ScriptDid = 0x3300AA01u;
    private const uint EmitterDid = 0x3200AA01u;
    private const uint SetupDid = 0x02000124u;
    private const uint RecallMotion = 0x10000153u;

    private static readonly PhysicsStateFlags MissileState =
        PhysicsStateFlags.ReportCollisions
        | PhysicsStateFlags.Lighting
        | PhysicsStateFlags.Missile
        | PhysicsStateFlags.AlignPath
        | PhysicsStateFlags.PathClipped;

    [Fact]
    public void NinetySixOwners_ChurnDeleteResetAndGuidReuse_LeaveNoRuntimeResources()
    {
        using var fixture = new Fixture();
        var oldLocalIds = new List<uint>(OwnerCount + ReusedOwnerCount);
        var lifetimeRecords = new List<LiveEntityRecord>(OwnerCount + ReusedOwnerCount);

        for (int i = 0; i < OwnerCount; i++)
        {
            uint guid = GuidBase + (uint)i;
            fixture.Effects.HandleDirect(new PlayPhysicsScript(guid, ScriptDid));
            LiveEntityRecord record = fixture.Spawn(guid, generation: 1, i);
            lifetimeRecords.Add(record);
            oldLocalIds.Add(record.LocalEntityId!.Value);

            if (i % 12 == 0)
            {
                fixture.Effects.HandleDirect(new PlayPhysicsScript(guid, ScriptDid));
                fixture.Effects.HandleDirect(new PlayPhysicsScript(guid, ScriptDid));
            }
        }

        fixture.Runner.Tick(0.0);
        fixture.Particles.Tick(0.01f);
        fixture.Projectiles.Tick(
            currentTime: 0.05,
            liveCenterX: 1,
            liveCenterY: 1,
            playerWorldPosition: null);

        Assert.Equal(OwnerCount, fixture.Runtime.Count);
        Assert.Equal(OwnerCount, fixture.Runtime.MaterializedCount);
        Assert.Equal(OwnerCount, fixture.RenderOwners.Count);
        Assert.Equal(OwnerCount, fixture.Projectiles.Count);
        Assert.Equal(OwnerCount, fixture.Effects.ReadyOwnerCount);
        Assert.Equal(0, fixture.Effects.PendingPacketCount);
        Assert.Equal(OwnerCount, fixture.Runner.ActiveOwnerCount);
        Assert.True(fixture.Runner.ActiveScriptCount > OwnerCount);
        Assert.Equal(OwnerCount, fixture.Particles.ActiveEmitterCount);
        Assert.Equal(OwnerCount, fixture.ParticleSink.ActiveBindingCount);
        Assert.Equal(OwnerCount, fixture.ParticleSink.LogicalEmitterCount);
        Assert.Equal(OwnerCount, fixture.ParticleSink.TrackedOwnerCount);
        Assert.Equal(OwnerCount, fixture.ParticleSink.RenderPassOwnerCount);
        Assert.Equal(OwnerCount, fixture.Poses.Count);
        Assert.Equal(OwnerCount, fixture.Lights.RegisteredCount);
        Assert.Equal(OwnerCount, fixture.LiveLights.TrackedOwnerCount);
        Assert.Equal(OwnerCount, fixture.LiveLights.PresentedOwnerCount);
        Assert.Equal(OwnerCount, fixture.Lighting.OwnedLightOwnerCount);
        Assert.Equal(OwnerCount, fixture.Lighting.PoseTrackedOwnerCount);
        Assert.Equal(OwnerCount, fixture.Lighting.RetainedOwnerStateCount);
        Assert.Equal(OwnerCount, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(OwnerCount, fixture.Engine.ShadowObjects.RetainedRegistrationCount);

        for (int i = 0; i < OwnerCount; i += 2)
            Assert.True(fixture.Runtime.RebucketLiveEntity(GuidBase + (uint)i, CellB));
        Assert.Equal(OwnerCount / 2, fixture.Spatial.PendingLiveEntityCount);
        Assert.Equal(OwnerCount, fixture.RenderOwners.Count);
        Assert.Equal(OwnerCount / 2, fixture.ParticleSink.HiddenPresentationOwnerCount);
        Assert.Equal(OwnerCount / 2, fixture.Lights.RegisteredCount);
        Assert.Equal(OwnerCount / 2, fixture.LiveLights.PresentedOwnerCount);

        fixture.Spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        Assert.Equal(0, fixture.Spatial.PendingLiveEntityCount);
        Assert.Equal(0, fixture.ParticleSink.HiddenPresentationOwnerCount);
        Assert.Equal(OwnerCount, fixture.Lights.RegisteredCount);
        fixture.Spatial.RemoveLandblock(0x0102FFFFu);
        Assert.Equal(OwnerCount / 2, fixture.Spatial.PendingLiveEntityCount);
        fixture.Spatial.AddLandblock(EmptyLandblock(0x0102FFFFu));
        Assert.Equal(0, fixture.Spatial.PendingLiveEntityCount);
        Assert.Equal(OwnerCount, fixture.RenderOwners.Count);
        Assert.Equal(OwnerCount, fixture.Projectiles.Count);
        Assert.Equal(OwnerCount, fixture.Lights.RegisteredCount);
        Assert.Equal(OwnerCount, fixture.LiveLights.PresentedOwnerCount);

        // End one third rapidly, then reuse those exact server GUIDs with a
        // newer instance timestamp. No queue, body, emitter, light, or shadow
        // from generation 1 may attach to generation 2.
        for (int i = 0; i < ReusedOwnerCount; i++)
        {
            uint guid = GuidBase + (uint)i;
            Assert.True(fixture.Runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, InstanceSequence: 1),
                isLocalPlayer: false));
        }
        Assert.Equal(OwnerCount - ReusedOwnerCount, fixture.Runtime.Count);
        Assert.Equal(OwnerCount - ReusedOwnerCount, fixture.RenderOwners.Count);
        Assert.Equal(OwnerCount - ReusedOwnerCount, fixture.Particles.ActiveEmitterCount);
        Assert.Equal(OwnerCount - ReusedOwnerCount, fixture.Lights.RegisteredCount);
        Assert.Equal(
            OwnerCount - ReusedOwnerCount,
            fixture.Engine.ShadowObjects.RetainedRegistrationCount);

        for (int i = 0; i < ReusedOwnerCount; i++)
        {
            uint guid = GuidBase + (uint)i;
            fixture.Effects.HandleDirect(new PlayPhysicsScript(guid, ScriptDid));
            LiveEntityRecord replacement = fixture.Spawn(guid, generation: 2, i);
            lifetimeRecords.Add(replacement);
            oldLocalIds.Add(replacement.LocalEntityId!.Value);
        }
        fixture.Runner.Tick(1.0);
        fixture.Particles.Tick(0.01f);

        Assert.Equal(OwnerCount, fixture.Runtime.Count);
        Assert.Equal(OwnerCount, fixture.RenderOwners.Count);
        Assert.Equal(OwnerCount, fixture.Projectiles.Count);
        Assert.Equal(OwnerCount, fixture.Particles.ActiveEmitterCount);
        Assert.Equal(OwnerCount, fixture.Lights.RegisteredCount);
        Assert.Equal(OwnerCount, fixture.Engine.ShadowObjects.RetainedRegistrationCount);

        for (int i = 0; i < OwnerCount; i += 2)
        {
            uint guid = GuidBase + (uint)i;
            ushort generation = i < ReusedOwnerCount ? (ushort)2 : (ushort)1;
            Assert.True(fixture.Runtime.UnregisterLiveEntity(
                new DeleteObject.Parsed(guid, generation),
                isLocalPlayer: false));
        }
        for (int i = 0; i < 8; i++)
        {
            fixture.Effects.HandleDirect(new PlayPhysicsScript(
                0x73000000u + (uint)i,
                ScriptDid));
        }
        Assert.Equal(8, fixture.Effects.PendingPacketCount);

        fixture.Runtime.Clear();
        fixture.Effects.ClearNetworkState();
        fixture.Particles.Tick(0.01f);

        Assert.Equal(0, fixture.Runtime.Count);
        Assert.Equal(0, fixture.Runtime.MaterializedCount);
        Assert.Empty(fixture.RenderOwners);
        Assert.Equal(fixture.RenderRegisterCount, fixture.RenderUnregisterCount);
        Assert.Equal(0, fixture.Projectiles.Count);
        Assert.Equal(0, fixture.Effects.ReadyOwnerCount);
        Assert.Equal(0, fixture.Effects.PendingPacketCount);
        Assert.Equal(0, fixture.Runner.ActiveOwnerCount);
        Assert.Equal(0, fixture.Runner.ActiveScriptCount);
        Assert.Equal(0, fixture.Runner.ScheduledCallPesCount);
        Assert.Equal(0, fixture.Runner.OwnerAnchorCount);
        Assert.Equal(0, fixture.Particles.ActiveEmitterCount);
        Assert.Equal(0, fixture.Particles.ActiveParticleCount);
        Assert.Equal(0, fixture.ParticleSink.ActiveBindingCount);
        Assert.Equal(0, fixture.ParticleSink.LogicalEmitterCount);
        Assert.Equal(0, fixture.ParticleSink.TrackedOwnerCount);
        Assert.Equal(0, fixture.ParticleSink.RenderPassOwnerCount);
        Assert.Equal(0, fixture.ParticleSink.HiddenPresentationOwnerCount);
        Assert.Equal(0, fixture.Poses.Count);
        Assert.Equal(0, fixture.Lights.RegisteredCount);
        Assert.Equal(0, fixture.Lighting.OwnedLightOwnerCount);
        Assert.Equal(0, fixture.Lighting.PoseTrackedOwnerCount);
        Assert.Equal(0, fixture.Lighting.RetainedOwnerStateCount);
        Assert.Equal(0, fixture.LiveLights.TrackedOwnerCount);
        Assert.Equal(0, fixture.LiveLights.PresentedOwnerCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.SuspendedRegistrationCount);
        Assert.Equal(0, fixture.Spatial.PendingLiveEntityCount);
        Assert.Equal(0, fixture.Spatial.PendingBucketCount);
        Assert.Equal(0, fixture.Spatial.PendingRescueCount);
        Assert.Equal(0, fixture.Spatial.PersistentGuidCount);
        Assert.Equal(0, fixture.Spatial.PendingVisibilityTransitionCount);
        Assert.DoesNotContain(fixture.Spatial.Entities, entity => entity.ServerGuid != 0);
        Assert.All(lifetimeRecords, record =>
        {
            Assert.Null(record.PhysicsBody);
            Assert.Null(record.ProjectileRuntime);
            Assert.Null(record.EffectProfile);
            Assert.Null(record.WorldEntity);
        });

        foreach (uint localId in oldLocalIds)
        {
            fixture.Engine.ShadowObjects.UpdatePosition(
                localId,
                new Vector3(20f, 20f, 10f),
                Quaternion.Identity,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0x01010000u,
                seedCellId: CellA);
        }
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(0, fixture.Engine.ShadowObjects.TotalRegistered);

        fixture.Runtime.Clear();
        fixture.Effects.ClearNetworkState();
        Assert.Equal(0, fixture.Runtime.Count);
        Assert.Equal(0, fixture.Effects.PendingPacketCount);
        AssertOwnershipLedgerConverged(fixture);
    }

    [Fact]
    public void RepeatedRetailRecallMotion_HiddenTeleportUnhide_DrainsEveryPresentationOwner()
    {
        using var fixture = new RecallPortalFixture();

        for (int cycle = 0; cycle < 12; cycle++)
        {
            fixture.PlayRecallMotion(cycle + 1.0);
            Assert.Equal(1, fixture.Particles.ActiveEmitterCount);
            Assert.Equal(1, fixture.ParticleSink.ActiveBindingCount);

            fixture.ApplyHidden();
            Assert.Equal(1, fixture.Presentation.DeferredShadowRestoreCount);

            fixture.BeginDeferredTeleport();

            fixture.LoadDestination();
            Assert.Equal(1, fixture.Presentation.DeferredShadowRestoreCount);

            fixture.ApplyVisible();
            Assert.Equal(0, fixture.Presentation.DeferredShadowRestoreCount);
            Assert.Equal(1, fixture.Engine.ShadowObjects.TotalRegistered);
            fixture.UnloadPriorLandblock();
        }

        Assert.Equal(12, fixture.TypedScripts.Count(type =>
            type == LiveEntityPresentationController.HiddenScriptType));
        Assert.Equal(12, fixture.TypedScripts.Count(type =>
            type == LiveEntityPresentationController.UnHideScriptType));
        Assert.Equal(12, fixture.RecallHookCount);

        fixture.Clear();

        Assert.Equal(0, fixture.Runtime.Count);
        Assert.Equal(0, fixture.Effects.ReadyOwnerCount);
        Assert.Equal(0, fixture.Runner.ActiveOwnerCount);
        Assert.Equal(0, fixture.Runner.OwnerAnchorCount);
        Assert.Equal(0, fixture.Particles.ActiveEmitterCount);
        Assert.Equal(0, fixture.ParticleSink.ActiveBindingCount);
        Assert.Equal(0, fixture.Poses.Count);
        Assert.Equal(0, fixture.Engine.ShadowObjects.RetainedRegistrationCount);
        Assert.Equal(0, fixture.Presentation.ReadyOwnerCount);
        Assert.Equal(0, fixture.Presentation.DeferredShadowRestoreCount);
        Assert.Equal(0, fixture.Spatial.PendingLiveEntityCount);
        Assert.Equal(0, fixture.Spatial.PendingBucketCount);
    }

    private static void AssertOwnershipLedgerConverged(Fixture fixture)
    {
        RuntimeEntityObjectOwnershipSnapshot canonical =
            fixture.EntityObjects.CaptureOwnership();
        var ledger = new J3OwnershipLedger(
            canonical,
            fixture.Runtime.MaterializedCount,
            fixture.Runtime.VisibleRecords.Count,
            fixture.Runtime.AnimationRuntimeCount,
            fixture.Runtime.SpatialAnimationRuntimeCount,
            fixture.Runtime.SpatialRemoteMotionRuntimeCount,
            fixture.Runtime.SpatialProjectileRuntimeCount,
            fixture.Runtime.SpatialRootObjectCount,
            fixture.RenderOwners.Count,
            fixture.RenderRegisterCount - fixture.RenderUnregisterCount,
            fixture.Projectiles.Count,
            fixture.Effects.ReadyOwnerCount,
            fixture.Effects.PendingPacketCount,
            fixture.Runner.ActiveOwnerCount,
            fixture.Runner.ActiveScriptCount,
            fixture.Runner.ScheduledCallPesCount,
            fixture.Runner.OwnerAnchorCount,
            fixture.Particles.ActiveEmitterCount,
            fixture.Particles.ActiveParticleCount,
            fixture.ParticleSink.ActiveBindingCount,
            fixture.ParticleSink.LogicalEmitterCount,
            fixture.ParticleSink.TrackedOwnerCount,
            fixture.ParticleSink.RenderPassOwnerCount,
            fixture.ParticleSink.HiddenPresentationOwnerCount,
            fixture.Poses.Count,
            fixture.Lights.RegisteredCount,
            fixture.LiveLights.TrackedOwnerCount,
            fixture.LiveLights.PresentedOwnerCount,
            fixture.Lighting.OwnedLightOwnerCount,
            fixture.Lighting.PoseTrackedOwnerCount,
            fixture.Lighting.RetainedOwnerStateCount,
            fixture.Engine.ShadowObjects.TotalRegistered,
            fixture.Engine.ShadowObjects.RetainedRegistrationCount,
            fixture.Engine.ShadowObjects.SuspendedRegistrationCount,
            fixture.Spatial.PendingLiveEntityCount,
            fixture.Spatial.PendingBucketCount,
            fixture.Spatial.PendingRescueCount,
            fixture.Spatial.PersistentGuidCount,
            fixture.Spatial.PendingVisibilityTransitionCount,
            fixture.Spatial.Entities.Count(entity =>
                entity.ServerGuid != 0u));

        Assert.Equal(default(J3OwnershipLedger), ledger);
    }

    private readonly record struct J3OwnershipLedger(
        RuntimeEntityObjectOwnershipSnapshot Canonical,
        int AppMaterializedCount,
        int AppVisibleCount,
        int AnimationRuntimeCount,
        int SpatialAnimationRuntimeCount,
        int SpatialRemoteMotionRuntimeCount,
        int SpatialProjectileRuntimeCount,
        int SpatialRootObjectCount,
        int RenderOwnerCount,
        int RenderRegistrationDelta,
        int ProjectileCount,
        int EffectReadyOwnerCount,
        int EffectPendingPacketCount,
        int ScriptOwnerCount,
        int ActiveScriptCount,
        int ScheduledCallPesCount,
        int ScriptAnchorCount,
        int EmitterCount,
        int ParticleCount,
        int ParticleBindingCount,
        int LogicalEmitterCount,
        int ParticleTrackedOwnerCount,
        int ParticleRenderPassOwnerCount,
        int HiddenParticlePresentationCount,
        int PoseCount,
        int RegisteredLightCount,
        int TrackedLiveLightCount,
        int PresentedLiveLightCount,
        int OwnedLightCount,
        int LightPoseCount,
        int RetainedLightStateCount,
        int ShadowCount,
        int RetainedShadowCount,
        int SuspendedShadowCount,
        int PendingSpatialEntityCount,
        int PendingBucketCount,
        int PendingRescueCount,
        int PersistentGuidCount,
        int PendingVisibilityCount,
        int MaterializedWorldEntityCount);

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<uint, DatPhysicsScript> _scripts = new();
        private EntityEffectController? _effects;
        private LiveEntityLightController? _liveLights;

        internal Fixture()
        {
            Spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
            AddEmptyPhysicsLandblock(Engine, 0x0101FFFFu, 0f, 0f);
            AddEmptyPhysicsLandblock(Engine, 0x0102FFFFu, 0f, 192f);

            var emitterRegistry = new EmitterDescRegistry();
            emitterRegistry.Register(PersistentEmitter());
            Particles = new ParticleSystem(emitterRegistry, new Random(42));
            ParticleSink = new ParticleHookSink(Particles, Poses);
            Lighting = new LightingHookSink(Lights, Poses);
            Router.Register(ParticleSink);
            Router.Register(Lighting);

            _scripts.Add(ScriptDid, PersistentScript());
            Runner = new PhysicsScriptRunner(
                did => _scripts.TryGetValue(did, out DatPhysicsScript? script)
                    ? script
                    : null,
                Router,
                randomUnit: () => 0.5,
                canAdvanceOwner: ownerId => _effects?.CanAdvanceOwner(ownerId) ?? true);

            EntityObjects = new RuntimeEntityObjectLifetime(Engine);
            EntityObjects.BindEventContext(
                static () => new AcDream.Runtime.RuntimeGenerationToken(1UL),
                static () => 1UL);
            Runtime = new LiveEntityRuntime(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(
                    entity =>
                    {
                        Assert.True(RenderOwners.Add(entity.Id));
                        RenderRegisterCount++;
                        Poses.PublishMeshRefs(entity);
                        ParticleSink.SetEntityRenderPass(entity.Id, ParticleRenderPass.Scene);
                    },
                    entity =>
                    {
                        Runner.StopAllForEntity(entity.Id);
                        ParticleSink.StopAllForEntity(entity.Id, fadeOut: false);
                        Poses.Remove(entity.Id);
                        Assert.True(RenderOwners.Remove(entity.Id));
                        RenderUnregisterCount++;
                    }),
                record =>
                {
                    var cleanups = new List<Action>
                    {
                        () => _effects?.OnLiveEntityUnregistered(record),
                    };
                    if (record.WorldEntity is { } entity)
                    {
                        cleanups.Add(() => Engine.ShadowObjects.Deregister(entity.Id));
                        cleanups.Add(() => _liveLights?.Forget(record));
                    }
                    LiveEntityTeardown.Run(cleanups);
                },
                EntityObjects);

            Effects = new EntityEffectController(
                Runtime,
                Runner,
                new PhysicsScriptTableResolver(_ => null),
                Poses);
            _effects = Effects;
            Router.Register(Effects);
            Projectiles = new ProjectileController(Runtime);
            LiveLights = new LiveEntityLightController(
                Runtime,
                Poses,
                Lighting,
                setupId => setupId == SetupDid ? Setup : null);
            _liveLights = LiveLights;
            Runtime.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
        }

        internal GpuWorldState Spatial { get; } = new();
        internal PhysicsEngine Engine { get; } = new();
        internal EntityEffectPoseRegistry Poses { get; } = new();
        internal AnimationHookRouter Router { get; } = new();
        internal ParticleSystem Particles { get; }
        internal ParticleHookSink ParticleSink { get; }
        internal LightManager Lights { get; } = new();
        internal LightingHookSink Lighting { get; }
        internal PhysicsScriptRunner Runner { get; }
        internal RuntimeEntityObjectLifetime EntityObjects { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal EntityEffectController Effects { get; }
        internal ProjectileController Projectiles { get; }
        internal LiveEntityLightController LiveLights { get; }
        internal Setup Setup { get; } = ProjectileSetup();
        internal HashSet<uint> RenderOwners { get; } = new();
        internal int RenderRegisterCount { get; private set; }
        internal int RenderUnregisterCount { get; private set; }

        internal LiveEntityRecord Spawn(uint guid, ushort generation, int ordinal)
        {
            Vector3 position = new(
                8f + (ordinal % 12) * 6f,
                8f + (ordinal / 12) * 6f,
                10f);
            WorldSession.EntitySpawn spawn = MissileSpawn(guid, generation, position);
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(
                spawn,
                localId => new WorldEntity
                {
                    Id = localId,
                    ServerGuid = guid,
                    SourceGfxObjOrSetupId = SetupDid,
                    Position = position,
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = CellA,
                },
                initializeProjection: exact =>
                    exact.EffectProfile = EntityEffectProfile.CreateLive(
                        new Setup(),
                        spawn.Physics!.Value));
            WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            Assert.True(Effects.OnLiveEntityReady(guid));
            Assert.True(Projectiles.TryBind(record, Setup, 0.0, 1, 1));

            Engine.ShadowObjects.Register(
                entity.Id,
                SetupDid,
                entity.Position,
                entity.Rotation,
                radius: 0.1f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0x01010000u,
                state: (uint)MissileState,
                seedCellId: CellA,
                isStatic: false);
            Assert.True(LiveLights.Register(guid));
            return record;
        }

        private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
        {
            if (record.WorldEntity is { } entity)
                ParticleSink.SetEntityPresentationVisible(entity.Id, visible);
        }

        public void Dispose()
        {
            Runtime.Clear();
            Effects.ClearNetworkState();
            Runtime.ProjectionVisibilityChanged -= OnProjectionVisibilityChanged;
            LiveLights.Dispose();
        }
    }

    private sealed class RecallPortalFixture : IDisposable
    {
        private const uint Guid = 0x75000001u;
        private const uint CellOne = 0x01010001u;
        private const uint CellTwo = 0x01020001u;
        private const uint RecallScriptDid = 0x3300BB01u;
        private const uint IdleAnimationDid = 0x0300BB01u;
        private const uint RecallAnimationDid = 0x0300BB02u;
        private const uint Style = 0x8000003Du;
        private const uint ReadyMotion = 0x41000003u;

        private readonly AnimationHookFrameQueue _hookQueue;
        private readonly TestRemoteMotion _remote = new();
        private uint _currentCell = CellOne;
        private uint _destinationCell;
        private ushort _stateSequence = 1;
        private bool _cleared;

        internal RecallPortalFixture()
        {
            Spatial.AddLandblock(EmptyLandblock(0x0101FFFFu));
            var emitterRegistry = new EmitterDescRegistry();
            emitterRegistry.Register(PersistentEmitter());
            Particles = new ParticleSystem(emitterRegistry, new Random(73));
            ParticleSink = new ParticleHookSink(Particles, Poses);
            Router.Register(ParticleSink);

            DatPhysicsScript recallScript = RecallScript();
            Runner = new PhysicsScriptRunner(
                did => did == RecallScriptDid ? recallScript : null,
                Router,
                randomUnit: () => 0.5);

            EntityEffectController? effects = null;
            LiveEntityPresentationController? presentation = null;
            Runtime = LiveEntityRuntimeFixture.Create(
                Spatial,
                new DelegateLiveEntityResourceLifecycle(
                    entity =>
                    {
                        Poses.PublishMeshRefs(entity);
                        ParticleSink.SetEntityRenderPass(entity.Id, ParticleRenderPass.Scene);
                    },
                    entity =>
                    {
                        Runner.StopAllForEntity(entity.Id);
                        ParticleSink.StopAllForEntity(entity.Id, fadeOut: false);
                        Poses.Remove(entity.Id);
                    }),
                record =>
                {
                    effects?.OnLiveEntityUnregistered(record);
                    presentation?.Forget(record);
                    if (record.WorldEntity is { } entity)
                        Engine.ShadowObjects.Deregister(entity.Id);
                });
            Effects = effects = new EntityEffectController(
                Runtime,
                Runner,
                new PhysicsScriptTableResolver(_ => null),
                Poses);
            Router.Register(Effects);

            WorldSession.EntitySpawn spawn = RecallSpawn(Guid, CellOne);
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(
                spawn,
                localId => new WorldEntity
                {
                    Id = localId,
                    ServerGuid = Guid,
                    SourceGfxObjOrSetupId = SetupDid,
                    Position = new Vector3(10f, 10f, 10f),
                    Rotation = Quaternion.Identity,
                    MeshRefs = Array.Empty<MeshRef>(),
                    ParentCellId = CellOne,
                },
                initializeProjection: exact =>
                    exact.EffectProfile = EntityEffectProfile.CreateLive(
                        new Setup(),
                        spawn.Physics!.Value));
            Entity = Assert.IsType<WorldEntity>(record.WorldEntity);
            Assert.True(Effects.OnLiveEntityReady(Guid));

            _remote.Body.SnapToCell(CellOne, Entity.Position, Entity.Position);
            _remote.CellId = CellOne;
            Runtime.SetRemoteMotionRuntime(Guid, _remote);
            Engine.ShadowObjects.Register(
                Entity.Id,
                SetupDid,
                Entity.Position,
                Entity.Rotation,
                radius: 0.48f,
                worldOffsetX: 0f,
                worldOffsetY: 0f,
                landblockId: 0x0101FFFFu,
                collisionType: ShadowCollisionType.Cylinder,
                cylHeight: 1.835f,
                seedCellId: CellOne,
                isStatic: false);

            Presentation = presentation = new LiveEntityPresentationController(
                Runtime,
                Engine.ShadowObjects,
                (_, type, _) =>
                {
                    TypedScripts.Add(type);
                    return true;
                },
                new LiveEntityPartArrayEnterWorldPort(_ => { }),
                liveCenter: () => (1, 1));
            Assert.True(Presentation.OnLiveEntityReady(Guid));

            Runtime.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
            _hookQueue = new AnimationHookFrameQueue(Router, Poses);
        }

        internal GpuWorldState Spatial { get; } = new();
        internal PhysicsEngine Engine { get; } = new();
        internal EntityEffectPoseRegistry Poses { get; } = new();
        internal AnimationHookRouter Router { get; } = new();
        internal ParticleSystem Particles { get; }
        internal ParticleHookSink ParticleSink { get; }
        internal PhysicsScriptRunner Runner { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal EntityEffectController Effects { get; }
        internal WorldEntity Entity { get; }
        internal LiveEntityPresentationController Presentation { get; }
        internal List<uint> TypedScripts { get; } = [];
        internal int RecallHookCount { get; private set; }

        internal void PlayRecallMotion(double gameTime)
        {
            AnimationSequencer recallSequencer = BuildRecallSequencer();
            recallSequencer.SetCycle(Style, ReadyMotion);
            recallSequencer.PlayAction(RecallMotion);
            recallSequencer.Advance(0.15f);
            IReadOnlyList<AnimationHook> hooks = recallSequencer.ConsumePendingHooks();
            RecallHookCount += hooks.Count(hook => hook is CallPESHook call
                && (uint)call.PES == RecallScriptDid);
            _hookQueue.Capture(Entity.Id, recallSequencer, hooks);
            _hookQueue.Drain();
            Runner.Tick(gameTime);
            Particles.Tick(0.01f);
        }

        internal void ApplyHidden()
        {
            Assert.True(Runtime.TryApplyState(
                new SetState.Parsed(
                    Guid,
                    (uint)(PhysicsStateFlags.Hidden | PhysicsStateFlags.ReportCollisions),
                    InstanceSequence: 1,
                    StateSequence: ++_stateSequence),
                out _,
                out _));
            Assert.True(Presentation.OnStateAccepted(Guid));
        }

        internal void BeginDeferredTeleport()
        {
            _destinationCell = _currentCell == CellOne ? CellTwo : CellOne;
            Assert.False(Spatial.IsLoaded((_destinationCell & 0xFFFF0000u) | 0xFFFFu));
            Assert.True(Runtime.RebucketLiveEntity(Guid, _destinationCell));
            var destination = new Vector3(
                Entity.Position.X + 3f, Entity.Position.Y, Entity.Position.Z);
            _remote.Body.Position = destination;
            _remote.CellId = _destinationCell;
            Entity.SetPosition(destination);
            Entity.ParentCellId = _destinationCell;
        }

        internal void LoadDestination()
        {
            Spatial.AddLandblock(EmptyLandblock(
                (_destinationCell & 0xFFFF0000u) | 0xFFFFu));
        }

        internal void ApplyVisible()
        {
            Assert.True(Runtime.TryApplyState(
                new SetState.Parsed(
                    Guid,
                    (uint)PhysicsStateFlags.ReportCollisions,
                    InstanceSequence: 1,
                    StateSequence: ++_stateSequence),
                out _,
                out _));
            Assert.True(Presentation.OnStateAccepted(Guid));
        }

        internal void UnloadPriorLandblock()
        {
            uint priorLandblock = (_currentCell & 0xFFFF0000u) | 0xFFFFu;
            Spatial.RemoveLandblock(priorLandblock);
            _currentCell = _destinationCell;
        }

        internal void Clear()
        {
            if (_cleared)
                return;
            _cleared = true;
            Runtime.Clear();
            Effects.ClearNetworkState();
            _hookQueue.Clear();
            Particles.Tick(0.01f);
        }

        public void Dispose()
        {
            Clear();
            Runtime.ProjectionVisibilityChanged -= OnProjectionVisibilityChanged;
            Presentation.Dispose();
        }

        private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
        {
            if (record.WorldEntity is { } entity)
                ParticleSink.SetEntityPresentationVisible(entity.Id, visible);
        }

        private static AnimationSequencer BuildRecallSequencer()
        {
            var setup = new Setup();
            setup.Parts.Add(0x01000001u);
            setup.DefaultScale.Add(Vector3.One);

            var table = new MotionTable
            {
                DefaultStyle = (DRWMotionCommand)Style,
            };
            table.StyleDefaults[(DRWMotionCommand)Style] =
                (DRWMotionCommand)ReadyMotion;
            int readyKey = unchecked((int)((Style << 16) | (ReadyMotion & 0xFFFFFFu)));
            table.Cycles[readyKey] = MotionData(IdleAnimationDid);
            var links = new MotionCommandData();
            links.MotionData[unchecked((int)RecallMotion)] = MotionData(RecallAnimationDid);
            table.Links[readyKey] = links;

            Animation idle = Animation(frames: 2);
            Animation recall = Animation(frames: 2);
            recall.PartFrames[0].Hooks.Add(new CallPESHook
            {
                Direction = AnimationHookDir.Forward,
                PES = RecallScriptDid,
                Pause = 0f,
            });
            var loader = new RecallAnimationLoader();
            loader.Add(IdleAnimationDid, idle);
            loader.Add(RecallAnimationDid, recall);
            return new AnimationSequencer(setup, table, loader);
        }

        private static MotionData MotionData(uint animationDid)
        {
            var data = new MotionData();
            QualifiedDataId<Animation> id = animationDid;
            data.Anims.Add(new AnimData
            {
                AnimId = id,
                LowFrame = 0,
                HighFrame = -1,
                Framerate = 10f,
            });
            return data;
        }

        private static Animation Animation(int frames)
        {
            var animation = new Animation();
            for (int i = 0; i < frames; i++)
            {
                var frame = new AnimationFrame(1u);
                frame.Frames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                animation.PartFrames.Add(frame);
            }
            return animation;
        }

        private sealed class RecallAnimationLoader : IAnimationLoader
        {
            private readonly Dictionary<uint, Animation> _animations = new();

            internal void Add(uint id, Animation animation) => _animations[id] = animation;

            public Animation? LoadAnimation(uint id) =>
                _animations.TryGetValue(id, out Animation? animation) ? animation : null;
        }

        private sealed class TestRemoteMotion : IRuntimeRemotePlacement
        {
            private uint _cellId;
            private Func<uint>? _readCell;
            private Action<uint>? _writeCell;

            public PhysicsBody Body { get; } = new();
            public uint CellId
            {
                get => _readCell?.Invoke() ?? _cellId;
                set
                {
                    _cellId = value;
                    _writeCell?.Invoke(value);
                }
            }
            public bool Airborne { get; set; }
            public Vector3 LastServerPosition { get; set; }
            public double LastServerPositionTime { get; set; }
            public Vector3 LastShadowSyncPosition { get; set; }
            public Quaternion LastShadowSyncOrientation { get; set; }
            public void BindCanonicalCell(Func<uint> read, Action<uint> write)
            {
                _readCell = read;
                _writeCell = write;
            }
            public void HitGround() { }
            public void LeaveGround() { }
        }
    }

    private static DatPhysicsScript PersistentScript()
    {
        var script = new DatPhysicsScript();
        script.ScriptData.Add(new PhysicsScriptData
        {
            StartTime = 0.0,
            Hook = new CreateParticleHook
            {
                EmitterInfoId = EmitterDid,
                PartIndex = uint.MaxValue,
                EmitterId = 1u,
                Offset = new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                },
            },
        });
        script.ScriptData.Add(new PhysicsScriptData
        {
            StartTime = 60.0,
            Hook = new DestroyParticleHook { EmitterId = 1u },
        });
        return script;
    }

    private static DatPhysicsScript RecallScript()
    {
        var script = new DatPhysicsScript();
        script.ScriptData.Add(new PhysicsScriptData
        {
            StartTime = 0.0,
            Hook = new CreateParticleHook
            {
                EmitterInfoId = EmitterDid,
                PartIndex = uint.MaxValue,
                EmitterId = 7u,
                Offset = new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                },
            },
        });
        return script;
    }

    private static EmitterDesc PersistentEmitter() => new()
    {
        DatId = EmitterDid,
        Type = AcDream.Core.Vfx.ParticleType.Still,
        EmitterKind = ParticleEmitterKind.BirthratePerSec,
        MaxParticles = 2,
        InitialParticles = 1,
        TotalParticles = 0,
        TotalDuration = 0f,
        Lifespan = 120f,
        LifetimeMin = 120f,
        LifetimeMax = 120f,
        Birthrate = 0f,
        StartSize = 0.25f,
        EndSize = 0.25f,
        StartAlpha = 1f,
        EndAlpha = 1f,
    };

    private static Setup ProjectileSetup() => new()
    {
        Spheres =
        {
            new Sphere
            {
                Origin = new Vector3(0f, -0.165f, 0.1045f),
                Radius = 0.102f,
            },
        },
        Lights =
        {
            [0] = new LightInfo
            {
                ViewSpaceLocation = new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                },
                Color = new ColorARGB
                {
                    Red = 255,
                    Green = 255,
                    Blue = 255,
                    Alpha = 255,
                },
                Intensity = 1f,
                Falloff = 8f,
            },
        },
    };

    private static WorldSession.EntitySpawn MissileSpawn(
        uint guid,
        ushort generation,
        Vector3 position)
    {
        var wirePosition = new CreateObject.ServerPosition(
            CellA,
            position.X,
            position.Y,
            position.Z,
            1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: generation);
        var physics = new PhysicsSpawnData(
            RawState: (uint)MissileState,
            Position: wirePosition,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: SetupDid,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: 0f,
            Elasticity: 0f,
            Translucency: null,
            Velocity: new Vector3(2f, 0f, 0f),
            Acceleration: null,
            AngularVelocity: Vector3.Zero,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            wirePosition,
            SetupDid,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "Stress missile",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: (uint)MissileState,
            Friction: 0f,
            Elasticity: 0f,
            InstanceSequence: generation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static WorldSession.EntitySpawn RecallSpawn(uint guid, uint cell)
    {
        var wirePosition = new CreateObject.ServerPosition(
            cell, 10f, 10f, 10f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 1,
            ServerControlledMove: 1,
            ForcePosition: 1,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: wirePosition,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: SetupDid,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: 1f,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            wirePosition,
            SetupDid,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: 1f,
            Name: "Recall stress owner",
            ItemType: null,
            MotionState: null,
            MotionTableId: 0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private static void AddEmptyPhysicsLandblock(
        PhysicsEngine engine,
        uint landblockId,
        float offsetX,
        float offsetY)
    {
        var heights = new byte[81];
        var table = new float[256];
        Array.Fill(table, -1000f);
        engine.AddLandblock(
            landblockId,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            offsetX,
            offsetY);
    }

    private static LoadedLandblock EmptyLandblock(uint canonicalId) =>
        new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());
}
