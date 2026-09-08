using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityOrdinaryPhysicsUpdaterTests
{
    private const uint Guid = 0x50000071u;
    private const uint SourceCell = 0xA9B40039u;
    private const uint DestinationCell = 0xAAB40001u;

    [Fact]
    public void CompleteRootFrame_CrossesTransitionAndCommitsCanonicalCell()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        spatial.AddLandblock(EmptyLandblock(0xAAB4FFFFu));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        PopulateBoundaryEngine(live.Physics.Engine);
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            Spawn(),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = Guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = new Vector3(191f, 10f, 50f),
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = SourceCell,
            },
            isLocalPlayer: true);
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);

        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = SourceCell,
        };
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        remote.Body.SnapToCell(
            SourceCell,
            entity.Position,
            entity.Position);
        live.SetRemoteMotionRuntime(Guid, remote);
        Assert.True(live.ClearRemoteMotionRuntime(Guid));
        ulong epoch = record.ObjectClockEpoch;

        var updater = new LiveEntityOrdinaryPhysicsUpdater(
            live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f));
        var rootFrame = new Frame
        {
            Origin = new Vector3(2f, 0f, 0f),
            Orientation = Quaternion.Identity,
        };

        Assert.True(updater.Tick(
            live,
            record,
            entity,
            rootFrame,
            objectScale: 1f,
            quantum: 0.1f,
            liveCenterX: 0xA9,
            liveCenterY: 0xB4,
            objectClockEpoch: epoch,
            sequencer: null,
            captureAnimationHooks: (_, _) => { }));

        Assert.Equal(DestinationCell, record.FullCellId);
        Assert.Equal(DestinationCell, entity.ParentCellId);
        Assert.Equal(DestinationCell, record.PhysicsBody!.CellPosition.ObjCellId);
        Assert.True(entity.Position.X > 192f);
        Assert.Equal(entity.Position, record.PhysicsBody.Position);
        Assert.Equal(epoch, record.ObjectClockEpoch);

        live.Physics.SetPosition.ParkCollisionResidents(
            SourceCell,
            includeOutdoorCells: true);
        Assert.Equal(DestinationCell, record.FullCellId);
        Assert.True(live.Physics.IsSpatialRoot(record.Canonical));
    }

    [Fact]
    public void HookDeletion_PreventsTransitionAndStaleEntityCommit()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(EmptyLandblock(0xA9B4FFFFu));
        var live = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        LiveEntityRecord record = live.RegisterAndMaterializeProjection(
            Spawn(),
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = Guid,
                SourceGfxObjOrSetupId = 0x02000001u,
                Position = new Vector3(191f, 10f, 50f),
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = SourceCell,
            });
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        var remote = new AcDream.Runtime.Physics.RemoteMotion
        {
            CellId = SourceCell,
        };
        remote.Body.TransientState = TransientStateFlags.Active
            | TransientStateFlags.Contact
            | TransientStateFlags.OnWalkable;
        remote.Body.SnapToCell(SourceCell, entity.Position, entity.Position);
        live.SetRemoteMotionRuntime(Guid, remote);
        live.ClearRemoteMotionRuntime(Guid);
        var setup = new DatReaderWriter.DBObjs.Setup();
        var sequencer = new AnimationSequencer(
            setup,
            new DatReaderWriter.DBObjs.MotionTable(),
            new NullAnimationLoader());
        Vector3 retainedEntityPosition = entity.Position;
        var updater = new LiveEntityOrdinaryPhysicsUpdater(
            live.Physics,
            (_, _) => (0.48f, 1.835f),
            (_, _) => (System.Collections.Immutable.ImmutableArray<FlatCollisionSphere>.Empty, 1f, 0.4f, 0.4f));

        Assert.False(updater.Tick(
            live,
            record,
            entity,
            new Frame
            {
                Origin = Vector3.UnitX,
                Orientation = Quaternion.Identity,
            },
            1f,
            0.1f,
            0xA9,
            0xB4,
            record.ObjectClockEpoch,
            sequencer,
            (_, _) => live.UnregisterLiveEntity(
                new DeleteObject.Parsed(Guid, record.Generation),
                isLocalPlayer: false)));

        Assert.False(live.TryGetRecord(Guid, out _));
        Assert.Equal(retainedEntityPosition, entity.Position);
    }

    private static void PopulateBoundaryEngine(PhysicsEngine engine)
    {
        static TerrainSurface FlatTerrain()
        {
            var heights = new byte[81];
            var table = new float[256];
            Array.Fill(table, 50f);
            return new TerrainSurface(heights, table);
        }

        engine.AddLandblock(
            0xA9B4FFFFu,
            FlatTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        engine.AddLandblock(
            0xAAB4FFFFu,
            FlatTerrain(),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            192f,
            0f);
    }

    private static LoadedLandblock EmptyLandblock(uint landblockId) =>
        new(
            landblockId,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>());

    private static WorldSession.EntitySpawn Spawn()
    {
        var position = new CreateObject.ServerPosition(
            SourceCell,
            191f,
            10f,
            50f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
        var physics = new PhysicsSpawnData(
            RawState: 0,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
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
            Guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "ordinary physics fixture",
            null,
            null,
            null,
            PhysicsState: 0,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public DatReaderWriter.DBObjs.Animation? LoadAnimation(uint id) => null;
    }
}
