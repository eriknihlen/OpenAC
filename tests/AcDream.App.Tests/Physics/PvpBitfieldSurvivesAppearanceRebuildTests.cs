using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

public sealed class PvpBitfieldSurvivesAppearanceRebuildTests
{
    private const uint Guid = 0x70000030u;
    private const uint Cell = 0x01010001u;

    private sealed class RecordingResources : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity) { }
        public void Unregister(WorldEntity entity) { }
    }

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    private static WorldEntity EntityFactory(uint id, uint guid) => new()
    {
        Id = id,
        ServerGuid = guid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
        ParentCellId = Cell,
    };

    private static WorldSession.EntitySpawn Spawn(
        uint? objectDescriptionFlags,
        ushort objDescSequence)
    {
        var position = new CreateObject.ServerPosition(Cell, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, objDescSequence, 1);
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
            Guid,
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
            ObjectDescriptionFlags: objectDescriptionFlags,
            Physics: physics);
    }

    [Fact]
    public void ObjDescAfterPkUpdate_RebuildKeepsLivePkLiteFlag()
    {
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu, new LandBlock(), Array.Empty<WorldEntity>()));
        var lifetime = new RuntimeEntityObjectLifetime();
        lifetime.BindEventContext(
            static () => new RuntimeGenerationToken(1UL),
            static () => 1UL);
        var runtime = new LiveEntityRuntime(spatial, new RecordingResources(), lifetime);

        WorldSession.EntitySpawn spawn = Spawn(objectDescriptionFlags: 0x8u, objDescSequence: 1);
        runtime.RegisterLiveEntity(spawn);
        WorldEntity entity = runtime.MaterializeLiveEntity(
            spawn.Guid, Cell, id => EntityFactory(id, spawn.Guid))!;
        Assert.True(runtime.TryGetRecord(Guid, out LiveEntityRecord record));

        lifetime.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Guid,
            PublicWeenieBitfield = 0x8u, // BF_PLAYER only
        });

        // Live PropertyInt(PlayerKillerStatus) = PKLite arrives.
        lifetime.Objects.UpdateIntProperty(
            Guid,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        Assert.Equal(0x2000008u, record.Snapshot.ObjectDescriptionFlags);

        var setup = new Setup();
        setup.Parts.Add(0x0100AB01u);
        var builder = new LiveEntityCollisionBuilder(
            id => id == 0x0100AB01u
                ? ShadowPartGeometry.Create(
                    new FlatCollisionSphere(new Vector3(0f, 0f, 0.5f), 1f),
                    null)
                : (ShadowPartGeometry?)null,
            new LiveEntityDefaultPoseResolver(
                _ => null,
                new NullAnimationLoader(),
                dumpMotion: false));
        var registry = new ShadowObjectRegistry();

        LiveEntityCollisionRegistration initial = Assert.IsType<LiveEntityCollisionRegistration>(
            builder.Build(
                entity,
                setup,
                [],
                record.Snapshot,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));
        LiveEntityCollisionBuilder.Register(registry, initial);
        ShadowEntry beforeObjDesc = Assert.Single(registry.GetObjectsInCell(Cell));
        Assert.True(beforeObjDesc.Flags.HasFlag(EntityCollisionFlags.IsPKLite));

        var update = new ObjDescEvent.Parsed(
            Guid,
            new CreateObject.ModelData(
                0x04000001u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 1,
            ObjDescSequence: 2);
        Assert.True(runtime.TryApplyObjDesc(update, out WorldSession.EntitySpawn accepted));
        Assert.Equal(0x2000008u, accepted.ObjectDescriptionFlags);
        Assert.Equal(0x2000008u, record.Snapshot.ObjectDescriptionFlags);

        LiveEntityCollisionRegistration rebuilt = Assert.IsType<LiveEntityCollisionRegistration>(
            builder.Build(
                entity,
                setup,
                [],
                accepted,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));
        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, rebuilt, suspendIfNew: false);

        ShadowEntry afterObjDesc = Assert.Single(registry.GetObjectsInCell(Cell));
        Assert.True(
            afterObjDesc.Flags.HasFlag(EntityCollisionFlags.IsPKLite),
            "the ObjDesc-triggered appearance rebuild must not revert a live PK-status change");
    }
}
