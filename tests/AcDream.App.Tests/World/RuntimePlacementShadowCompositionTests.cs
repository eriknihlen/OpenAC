using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.App.Net;
using AcDream.App.Physics;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Plugins;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.World;

public sealed class RuntimePlacementShadowCompositionTests
{
    private const uint SourceCell = 0x01010001u;
    private const uint DestinationCell = 0x01020001u;
    private const uint Guid = 0x7000A101u;
    private static readonly Vector3 SourcePosition = new(10f, 10f, 5f);
    private static readonly Vector3 DestinationPosition = new(202f, 10f, 5f);
    private static readonly Vector3 WirePoseDoubleWrite = new(-900f, -900f, -900f);
    private static readonly Quaternion DestinationOrientation =
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f);

    [Fact]
    public void Place_PublishesRealPhysicsShadowAtDestination_NotOnlyTheDedupCache()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);

        fixture.Physics.ShadowObjects.Register(
            entity.Id,
            gfxObjId: entity.SourceGfxObjOrSetupId,
            worldPos: SourcePosition,
            rotation: Quaternion.Identity,
            radius: 0.48f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: SourceCell & 0xFFFF0000u,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 1.835f,
            seedCellId: SourceCell);
        fixture.Synchronizer.Sync(entity, SourceCell, force: true);
        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);

        entity.Position = WirePoseDoubleWrite;
        entity.Rotation = Quaternion.Identity;

        RuntimePortalPlacementAuthority portal = fixture.BeginPortal(
            DestinationCell,
            teleportSequence: 1);
        record.FullCellId = DestinationCell;
        record.CanonicalLandblockId = (DestinationCell & 0xFFFF0000u) | 0xFFFFu;
        record.Canonical.AdvancePlacementCommit();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            portal,
            DestinationPosition,
            DestinationOrientation);

        Assert.True(fixture.Sink.TryApply(in place));

        Assert.Equal(DestinationPosition, entity.Position);
        Assert.Equal(DestinationOrientation, entity.Rotation);
        Assert.Equal(DestinationCell, entity.ParentCellId);

        Assert.Equal(
            new LocalPlayerShadowState.Snapshot(
                DestinationPosition,
                DestinationOrientation,
                DestinationCell),
            fixture.LocalShadow.Current);

        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(DestinationCell),
            e => e.EntityId == entity.Id);

        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);
    }

    [Fact]
    public void Place_ThenOrdinaryTick_DoesNotNeedToSelfHeal_RealShadowAlreadyRight()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        fixture.Physics.ShadowObjects.Register(
            entity.Id,
            gfxObjId: entity.SourceGfxObjOrSetupId,
            worldPos: SourcePosition,
            rotation: Quaternion.Identity,
            radius: 0.48f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: SourceCell & 0xFFFF0000u,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 1.835f,
            seedCellId: SourceCell);
        fixture.Synchronizer.Sync(entity, SourceCell, force: true);

        RuntimePortalPlacementAuthority portal = fixture.BeginPortal(
            DestinationCell,
            teleportSequence: 1);
        record.FullCellId = DestinationCell;
        record.CanonicalLandblockId = (DestinationCell & 0xFFFF0000u) | 0xFFFFu;
        record.Canonical.AdvancePlacementCommit();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            portal,
            DestinationPosition,
            DestinationOrientation);
        Assert.True(fixture.Sink.TryApply(in place));

        fixture.Synchronizer.Sync(entity, DestinationCell);

        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(DestinationCell),
            e => e.EntityId == entity.Id);
        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);
        Assert.Single(
            fixture.Physics.ShadowObjects.AllEntriesForDebug(),
            e => e.EntityId == entity.Id);
    }

    [Fact]
    public void Place_ForNonLocalPlayerEntity_NeverTouchesShadowObjects()
    {
        const uint ChildGuid = 0x7000A102u;
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(ChildGuid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        Assert.NotEqual(Guid, ChildGuid);

        fixture.Physics.ShadowObjects.Register(
            entity.Id,
            gfxObjId: entity.SourceGfxObjOrSetupId,
            worldPos: SourcePosition,
            rotation: Quaternion.Identity,
            radius: 0.48f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: SourceCell & 0xFFFF0000u,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 1.835f,
            seedCellId: SourceCell);
        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);

        RuntimePortalPlacementAuthority portal = fixture.BeginPortal(
            DestinationCell,
            teleportSequence: 1);
        record.FullCellId = DestinationCell;
        record.CanonicalLandblockId = (DestinationCell & 0xFFFF0000u) | 0xFFFFu;
        record.Canonical.AdvancePlacementCommit();
        RuntimePlacementProjectionSnapshot place = Placement(
            fixture,
            record,
            portal,
            DestinationPosition,
            DestinationOrientation);

        Assert.True(fixture.Sink.TryApply(in place));

        // The render entity DID move (Place still works for a non-player
        // entity) — only the shadow-publish branch is player-gated.
        Assert.Equal(DestinationPosition, entity.Position);

        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);
        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(DestinationCell),
            e => e.EntityId == entity.Id);
        Assert.Equal(1, fixture.Physics.ShadowObjects.TotalRegistered);

        Assert.Null(fixture.LocalShadow.Current);
    }

    [Fact]
    public void Withdraw_SuspendsRealPhysicsShadow_NotOnlyTheDedupCache()
    {
        Fixture fixture = Fixture.Create();
        LiveEntityRecord record = fixture.Materialize(Spawn(Guid, 1, SourceCell));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);

        fixture.Physics.ShadowObjects.Register(
            entity.Id,
            gfxObjId: entity.SourceGfxObjOrSetupId,
            worldPos: SourcePosition,
            rotation: Quaternion.Identity,
            radius: 0.48f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: SourceCell & 0xFFFF0000u,
            collisionType: ShadowCollisionType.Sphere,
            cylHeight: 1.835f,
            seedCellId: SourceCell);
        fixture.Synchronizer.Sync(entity, SourceCell, force: true);
        Assert.Contains(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);
        Assert.NotNull(fixture.LocalShadow.Current);

        RuntimePlacementProjectionSnapshot withdraw = Placement(
            fixture,
            record,
            portal: default,
            entity.Position,
            entity.Rotation,
            RuntimePlacementProjectionKind.Withdraw);

        Assert.True(fixture.Sink.TryApply(in withdraw));

        Assert.Null(fixture.LocalShadow.Current);
        Assert.DoesNotContain(
            fixture.Physics.ShadowObjects.GetObjectsInCell(SourceCell),
            e => e.EntityId == entity.Id);
        Assert.Equal(0, fixture.Physics.ShadowObjects.TotalRegistered);
        Assert.Equal(1, fixture.Physics.ShadowObjects.RetainedRegistrationCount);
    }

    private static RuntimePlacementProjectionSnapshot Placement(
        Fixture fixture,
        LiveEntityRecord record,
        RuntimePortalPlacementAuthority portal,
        Vector3 position,
        Quaternion orientation,
        RuntimePlacementProjectionKind kind = RuntimePlacementProjectionKind.Place)
    {
        RuntimeEntityRecord canonical = record.Canonical;
        var token = new RuntimePlacementProjectionToken(
            Sequence: 1,
            Revision: 1,
            Entity: record.ProjectionKey!.Value,
            PositionAuthorityVersion: canonical.PositionAuthorityVersion,
            SpatialAuthorityVersion: canonical.SpatialAuthorityVersion,
            PlacementCommitVersion: canonical.PlacementCommitVersion,
            SessionLifetimeVersion: fixture.Runtime.SessionLifetimeVersion,
            ExactCellId: canonical.FullCellId,
            CollisionGeneration: 1,
            Portal: portal);
        return new RuntimePlacementProjectionSnapshot(
            token,
            kind,
            position,
            orientation,
            CellLocalPosition: position,
            InContact: false,
            OnWalkable: false);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
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
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class Fixture
    {
        private Fixture(
            PhysicsEngine physics,
            GpuWorldState spatial,
            LiveEntityRuntime runtime,
            RuntimeWorldTransitState transit,
            WorldGameState worldState,
            WorldEvents worldEvents,
            EntityEffectPoseRegistry effectPoses,
            LocalPlayerShadowState localShadow,
            LocalPlayerShadowSynchronizer synchronizer)
        {
            Physics = physics;
            Spatial = spatial;
            Runtime = runtime;
            Transit = transit;
            WorldState = worldState;
            WorldEvents = worldEvents;
            EffectPoses = effectPoses;
            LocalShadow = localShadow;
            Synchronizer = synchronizer;
            Sink = new RuntimePlacementPresentationSink(
                runtime,
                transit,
                worldState,
                worldEvents,
                effectPoses,
                synchronizer,
                () => Guid,
                _ => { },
                [(_, _) => { }]);
        }

        internal PhysicsEngine Physics { get; }
        internal GpuWorldState Spatial { get; }
        internal LiveEntityRuntime Runtime { get; }
        internal RuntimeWorldTransitState Transit { get; }
        internal WorldGameState WorldState { get; }
        internal WorldEvents WorldEvents { get; }
        internal EntityEffectPoseRegistry EffectPoses { get; }
        internal LocalPlayerShadowState LocalShadow { get; }
        internal LocalPlayerShadowSynchronizer Synchronizer { get; }
        internal RuntimePlacementPresentationSink Sink { get; }

        internal static Fixture Create()
        {
            var physics = new PhysicsEngine { DataCache = new PhysicsDataCache() };
            physics.AddLandblock(
                SourceCell & 0xFFFF0000u,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 0f,
                worldOffsetY: 0f);
            physics.AddLandblock(
                DestinationCell & 0xFFFF0000u,
                new TerrainSurface(new byte[81], new float[256]),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                worldOffsetX: 192f,
                worldOffsetY: 0f);

            var spatial = new GpuWorldState();
            spatial.AddLandblock(EmptyLandblock(SourceCell | 0xFFFFu));
            spatial.AddLandblock(EmptyLandblock(DestinationCell | 0xFFFFu));
            var resources = new RecordingResources();
            LiveEntityRuntime runtime = LiveEntityRuntimeFixture.Create(
                spatial,
                resources,
                physics);

            var identity = new LocalPlayerIdentityState { ServerGuid = Guid };
            var origin = new LiveWorldOriginState();
            origin.SetPlaceholder(0, 0);
            var localShadow = new LocalPlayerShadowState();
            var synchronizer = new LocalPlayerShadowSynchronizer(
                physics,
                runtime,
                identity,
                origin,
                localShadow);

            return new Fixture(
                physics,
                spatial,
                runtime,
                new RuntimeWorldTransitState(),
                new WorldGameState(),
                new WorldEvents(),
                new EntityEffectPoseRegistry(),
                localShadow,
                synchronizer);
        }

        internal LiveEntityRecord Materialize(WorldSession.EntitySpawn spawn)
        {
            LiveEntityRecord record = Runtime.RegisterAndMaterializeProjection(spawn);
            Assert.False(Runtime.HasActiveInitialCreateResidence(
                record.Canonical));
            Assert.True(record.ResourcesRegistered);
            WorldEntity entity = record.WorldEntity!;
            var snapshot = new AcDream.Plugin.Abstractions.WorldEntitySnapshot(
                entity.Id,
                entity.SourceGfxObjOrSetupId,
                entity.Position,
                entity.Rotation);
            WorldState.Add(snapshot);
            WorldEvents.UpsertCurrent(snapshot);
            EffectPoses.PublishMeshRefs(entity);
            return record;
        }

        internal RuntimePortalPlacementAuthority BeginPortal(
            uint cell,
            ushort teleportSequence)
        {
            Assert.True(Transit.TryQueueTeleportStart(teleportSequence));
            Assert.True(Transit.ActivateQueuedTeleport());
            Assert.True(Transit.OfferTeleportDestination(
                new RuntimeTeleportDestination(
                    Guid,
                    InstanceSequence: 1,
                    PositionSequence: 1,
                    TeleportSequence: teleportSequence,
                    ForcePositionSequence: 1,
                    new Position(
                        cell,
                        new Vector3(1f, 2f, 3f),
                        Quaternion.Identity)),
                teleportTimestampAdvanced: true));
            Assert.True(Transit.TryBeginPortalReveal(
                teleportSequence,
                cell,
                out long generation));
            Assert.True(Transit.TryRegisterHostProjection(
                generation,
                cell,
                out RuntimeWorldHostProjectionToken host));
            return new RuntimePortalPlacementAuthority(
                true,
                generation,
                teleportSequence,
                host);
        }

        private static LoadedLandblock EmptyLandblock(uint canonicalId) =>
            new(canonicalId, new LandBlock(), Array.Empty<WorldEntity>());
    }

    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }
}
