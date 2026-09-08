using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering;

public sealed class LiveAppearanceAnimationTests
{
    [Fact]
    public void Capture_UsesCanonicalRecordEntityAndAnimationOwner()
    {
        const uint guid = 0x70000002u;
        var runtime = LiveEntityRuntimeFixture.Create(
            new AcDream.App.Streaming.GpuWorldState(),
            new AcDream.App.World.DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        var spawn = new AcDream.Core.Net.WorldSession.EntitySpawn(
            guid,
            Position: null,
            SetupTableId: null,
            AnimPartChanges: Array.Empty<AcDream.Core.Net.Messages.CreateObject.AnimPartChange>(),
            TextureChanges: Array.Empty<AcDream.Core.Net.Messages.CreateObject.TextureChange>(),
            SubPalettes: Array.Empty<AcDream.Core.Net.Messages.CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: null,
            Name: "appearance fixture",
            ItemType: null,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: 1).WithConsistentPhysics();
        LiveEntityRecord record = runtime.RegisterAndMaterializeProjection(
            spawn,
            id => Entity(id, 0x01000001u, guid));
        WorldEntity entity = Assert.IsType<WorldEntity>(record.WorldEntity);
        var state = new LiveEntityAnimationState
        {
            Entity = entity,
            Setup = new Setup(),
            Animation = new Animation(),
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            Scale = 1f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
        };
        record.AnimationRuntime = state;

        LiveEntityAppearanceUpdateState captured = Assert.IsType<LiveEntityAppearanceUpdateState>(
            LiveEntityAppearanceBinding.Capture(runtime, guid));

        Assert.Same(entity, captured.Entity);
        Assert.Same(state, captured.Animation);
    }

    [Fact]
    public void RebindAppearance_PreservesEntitySequencerAndPlaybackState()
    {
        var setup = new Setup();
        var animation = new Animation();
        var sequencer = new AnimationSequencer(setup, new MotionTable(), new NullLoader());
        var oldEntity = Entity(0x70000001u, 0x01000001u);
        var state = new LiveEntityAnimationState
        {
            Entity = oldEntity,
            Setup = setup,
            Animation = animation,
            LowFrame = 2,
            HighFrame = 9,
            Framerate = 30f,
            Scale = 1f,
            PartTemplate = [new LiveAnimationPartTemplate(0x01000001u, null, true)],
            PartAvailability = [true],
            CurrFrame = 6.5f,
            Sequencer = sequencer,
        };
        IReadOnlyList<MeshRef> dressedParts =
        [
            new MeshRef(0x01000002u, Matrix4x4.Identity),
            new MeshRef(0x01000003u, Matrix4x4.CreateTranslation(1f, 2f, 3f)),
        ];

        oldEntity.ApplyAppearance(dressedParts, paletteOverride: null, partOverrides: []);
        LiveEntityAppearanceBinding.RebindAnimation(
            state,
            oldEntity,
            setup,
            1.25f,
            [
                new LiveAnimationPartTemplate(0x01000002u, null, true),
                new LiveAnimationPartTemplate(0x01000003u, null, true),
            ],
            [true, true]);

        Assert.Same(oldEntity, state.Entity);
        Assert.Same(dressedParts, state.Entity.MeshRefs);
        Assert.Same(sequencer, state.Sequencer);
        Assert.Equal(6.5f, state.CurrFrame);
        Assert.Equal((2, 9, 30f), (state.LowFrame, state.HighFrame, state.Framerate));
        Assert.Equal(1.25f, state.Scale);
        Assert.Equal(new[] { 0x01000002u, 0x01000003u },
            state.PartTemplate.Select(part => part.GfxObjId));
    }

    [Fact]
    public void CollisionCommit_DelayedOldIncarnationCannotMutateGuidReplacement()
    {
        const uint guid = 0x70000010u;
        const uint cell = 0x01010001u;
        var spatial = new GpuWorldState();
        spatial.AddLandblock(new LoadedLandblock(
            0x0101FFFFu,
            new LandBlock(),
            Array.Empty<WorldEntity>()));
        var runtime = LiveEntityRuntimeFixture.Create(
            spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }));
        WorldSession.EntitySpawn first = Spawn(guid, cell, instance: 1);
        runtime.RegisterLiveEntity(first);
        WorldEntity oldEntity = runtime.MaterializeLiveEntity(
            guid,
            cell,
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = guid,
                SourceGfxObjOrSetupId = 0x02000010u,
                Position = Vector3.One,
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = cell,
            })!;
        var registry = new ShadowObjectRegistry();
        registry.Register(
            oldEntity.Id,
            0x01000010u,
            oldEntity.Position,
            oldEntity.Rotation,
            radius: 0.5f,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: cell,
            seedCellId: cell,
            isStatic: false);
        var builder = new LiveEntityCollisionBuilder(
            _ => null,
            new LiveEntityDefaultPoseResolver(
                _ => null,
                new NullLoader(),
                dumpMotion: false));
        LiveEntityAppearanceCollisionUpdate update =
            Assert.IsType<LiveEntityAppearanceCollisionUpdate>(
                LiveEntityAppearanceBinding.PrepareCollision(
                    runtime,
                    builder,
                    oldEntity,
                    new Setup(),
                    Array.Empty<uint>(),
                    first,
                    Vector3.Zero));

        runtime.RegisterLiveEntity(Spawn(guid, cell, instance: 2));

        Assert.False(LiveEntityAppearanceBinding.CommitCollision(
            runtime,
            registry,
            update));
        Assert.Equal(1, registry.RetainedRegistrationCount);
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, uint cell, ushort instance) => new WorldSession.EntitySpawn(
        guid,
        new CreateObject.ServerPosition(cell, 1f, 1f, 1f, 1f, 0f, 0f, 0f),
        0x02000010u,
        Array.Empty<CreateObject.AnimPartChange>(),
        Array.Empty<CreateObject.TextureChange>(),
        Array.Empty<CreateObject.SubPaletteSwap>(),
        BasePaletteId: null,
        ObjScale: 1f,
        Name: "appearance collision fixture",
        ItemType: null,
        MotionState: null,
        MotionTableId: null,
        InstanceSequence: instance).WithConsistentPhysics();

    private static WorldEntity Entity(
        uint id,
        uint gfxObjId,
        uint serverGuid = 0x50000001u) => new()
    {
        Id = id,
        ServerGuid = serverGuid,
        SourceGfxObjOrSetupId = 0x02000001u,
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        MeshRefs = [new MeshRef(gfxObjId, Matrix4x4.Identity)],
    };

    private sealed class NullLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
