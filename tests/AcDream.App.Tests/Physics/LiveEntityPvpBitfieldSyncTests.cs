using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityPvpBitfieldSyncTests
{
    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private static WorldEntity Entity(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
    };

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell)
    {
        var position = new CreateObject.ServerPosition(cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
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
            (uint)ItemType.Creature,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    /// <summary>Registers and materializes one live entity, mirroring
    /// <c>LiveEntityRuntimeTests.RegisterRebucketWithdrawAndRestore_UsesOneLogicalCreate</c>'s
    /// proven Register+Materialize sequence.</summary>
    private static (LiveEntityRuntime Runtime, WorldEntity Entity) MaterializedEntity(uint guid)
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu, new LandBlock(), Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        WorldSession.EntitySpawn spawn = Spawn(guid, cell: 0x01010001u);

        runtime.RegisterLiveEntity(spawn);
        WorldEntity? entity = runtime.MaterializeLiveEntity(
            spawn.Guid,
            spawn.Position!.Value.LandblockId,
            id => Entity(id, spawn.Guid));
        return (runtime, entity!);
    }

    [Fact]
    public void ObjectUpdated_WithLiveBitfield_RefreshesRegisteredTargetFlags()
    {
        (LiveEntityRuntime runtime, WorldEntity entity) = MaterializedEntity(0x70000010u);

        var shadows = new ShadowObjectRegistry();
        shadows.Register(
            entity.Id,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B40000u,
            flags: EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsPlayer,
            seedCellId: 0xA9B40001u);

        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000010u,
            PublicWeenieBitfield = 0x8u, // BF_PLAYER only — spawn-time snapshot
        });

        using var sync = new LiveEntityPvpBitfieldSync(objects, runtime, shadows);

        // Live PropertyInt(PlayerKillerStatus) = PKLite arrives.
        objects.UpdateIntProperty(
            0x70000010u,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        ShadowEntry after = Assert.Single(shadows.GetObjectsInCell(0xA9B40001u));
        Assert.Equal(
            EntityCollisionFlags.HasWeenie
                | EntityCollisionFlags.IsPlayer
                | EntityCollisionFlags.IsPKLite,
            after.Flags);
    }

    [Fact]
    public void ObjectUpdated_UnrelatedProperty_NoBitfield_DoesNotThrowOrRegisterUnknownEntity()
    {
        var spatial = new GpuWorldState();
        var runtime = LiveEntityRuntimeFixture.Create(spatial, new RecordingResources());
        var shadows = new ShadowObjectRegistry();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = 0x70000011u });

        using var sync = new LiveEntityPvpBitfieldSync(objects, runtime, shadows);

        // No PublicWeenieBitfield yet, and no matching live entity — must
        // no-op harmlessly rather than throw.
        objects.UpdateIntProperty(0x70000011u, propertyId: 18u, value: 1);

        Assert.Equal(0, shadows.TotalRegistered);
    }

    [Fact]
    public void Dispose_UnsubscribesFromObjectUpdated()
    {
        (LiveEntityRuntime runtime, WorldEntity entity) = MaterializedEntity(0x70000012u);

        var shadows = new ShadowObjectRegistry();
        shadows.Register(
            entity.Id,
            0x01000005u,
            new Vector3(12f, 12f, 50f),
            Quaternion.Identity,
            1f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: 0xA9B40000u,
            flags: EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsPlayer,
            seedCellId: 0xA9B40001u);

        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000012u,
            PublicWeenieBitfield = 0x8u,
        });

        var sync = new LiveEntityPvpBitfieldSync(objects, runtime, shadows);
        sync.Dispose();

        objects.UpdateIntProperty(
            0x70000012u,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        ShadowEntry after = Assert.Single(shadows.GetObjectsInCell(0xA9B40001u));
        Assert.Equal(
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsPlayer,
            after.Flags);
    }
}
