using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Physics;

public sealed class LiveEntityCollisionBuilderTests
{
    private const uint Guid = 0x70000100u;
    private const uint Cell = 0x01010001u;

    [Fact]
    public void EffectivePartResolution_IsSetupAndVisualOptionIndependent()
    {
        uint[] resolved = LiveEntityCollisionBuilder.ResolveEffectivePartIdentities(
            [0x01001000u, 0x01002000u],
            id => id == 0x01001000u ? 0x01001001u : id);

        Assert.Equal([0x01001001u, 0x01002000u], resolved);
    }

    [Fact]
    public void Build_PropagatesExactStateFlagsScaleAndFullSeedCell()
    {
        var setup = new Setup();
        setup.CylSpheres.Add(new CylSphere
        {
            Origin = Vector3.Zero,
            Radius = 0.4f,
            Height = 1.2f,
        });
        WorldSession.EntitySpawn spawn = Spawn(
            scale: 2f,
            itemType: (uint)ItemType.Creature,
            descriptionFlags: 0x28u);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        record.FinalPhysicsState =
            PhysicsStateFlags.Hidden | PhysicsStateFlags.Static;
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = Builder();

        LiveEntityCollisionRegistration registration = Assert.IsType<LiveEntityCollisionRegistration>(
            builder.Build(
                entity,
                setup,
                Array.Empty<uint>(),
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                new Vector3(192f, -192f, 0f)));

        ShadowShape shape = Assert.Single(registration.Shapes);
        Assert.Equal(ShadowCollisionType.Cylinder, shape.CollisionType);
        Assert.Equal(0.8f, shape.Radius);
        Assert.Equal(2.4f, shape.CylHeight);
        Assert.Equal((uint)record.FinalPhysicsState, registration.State);
        Assert.True(registration.Flags.HasFlag(EntityCollisionFlags.HasWeenie));
        Assert.True(registration.Flags.HasFlag(EntityCollisionFlags.IsCreature));
        Assert.True(registration.Flags.HasFlag(EntityCollisionFlags.IsPlayer));
        Assert.True(registration.Flags.HasFlag(EntityCollisionFlags.IsPK));
        Assert.Equal(Cell, registration.LandblockId);
        Assert.Equal(Cell, registration.SeedCellId);
        Assert.Equal((192f, -192f), (registration.WorldOffsetX, registration.WorldOffsetY));
    }

    [Fact]
    public void ShapelessSetupWithRadius_ProducesNoRegistration()
    {
        var setup = new Setup { Radius = 0.75f, Height = 2.5f };
        WorldSession.EntitySpawn spawn = Spawn(scale: 2f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;

        Assert.Null(Builder().Build(
            entity,
            setup,
            Array.Empty<uint>(),
            spawn,
            record.ServerGuid,
            record.Generation,
            record.WorldEntity!,
            record.FinalPhysicsState,
            new Vector3(192f, -192f, 0f)));
    }

    [Fact]
    public void BspOnlyPart_UsesRealScaledPhysicsBoundingSphere()
    {
        const uint part = 0x0100ABCDu;
        var setup = new Setup();
        setup.Parts.Add(part);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1.5f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == part ? Bsp(3f, centerZ: 2f) : null,
            PoseResolver());

        LiveEntityCollisionRegistration registration = Assert.IsType<LiveEntityCollisionRegistration>(
            builder.Build(
                entity,
                setup,
                [part],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));

        ShadowShape shape = Assert.Single(registration.Shapes);
        Assert.Equal(ShadowCollisionType.BSP, shape.CollisionType);
        Assert.Equal(4.5f, shape.Radius);                       // 3 m * 1.5
        Assert.Equal(new Vector3(0f, 0f, 3f), shape.BoundsCenter); // 2 m * 1.5
        Assert.Equal(part, shape.GfxObjId);
    }

    [Fact]
    public void Build_PartWithNoCollisionButResolvableVisualBounds_RegistersRenderOnly()
    {
        const uint part = 0x0100BEEFu;
        var setup = new Setup();
        setup.Parts.Add(part);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            physicsBspBounds: _ => null,
            PoseResolver(),
            getGfxObj: _ => null,
            getVisualBounds: id => id == part
                ? new GfxObjVisualBounds
                {
                    Min = new Vector3(-1f, -1f, -1f),
                    Max = new Vector3(1f, 1f, 1f),
                    Center = Vector3.Zero,
                    Radius = 1.5f,
                    HalfExtents = new Vector3(1f, 1f, 1f),
                }
                : null);

        LiveEntityCollisionRegistration registration = Assert.IsType<LiveEntityCollisionRegistration>(
            builder.Build(
                entity,
                setup,
                [part],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));

        Assert.Empty(registration.Shapes);
        ShadowShape renderPart = Assert.Single(registration.RenderParts);
        Assert.Equal(part, renderPart.GfxObjId);

        var registry = new ShadowObjectRegistry();
        LiveEntityCollisionBuilder.Register(registry, registration);

        Assert.True(
            registry.TryGetRetailCellArray(entity.Id, out IReadOnlyList<uint> cells));
        Assert.NotEmpty(cells);
        Assert.Empty(registry.GetOwnerCells(entity.Id));
        var entries = registry.GetRetailPartEntriesInCell(cells[0])
            .Where(e => e.EntityId == entity.Id)
            .ToList();
        Assert.Single(entries);
        Assert.Equal(part, entries[0].GfxObjId);
    }

    [Fact]
    public void CylSphereAndPhysicsBspPart_EmitsOnlyTheScaledBspShape()
    {
        const uint part = 0x0100AC01u;
        var setup = new Setup();
        setup.Parts.Add(part);
        setup.CylSpheres.Add(new CylSphere
        {
            Origin = Vector3.Zero,
            Radius = 0.4f,
            Height = 1.2f,
        });
        WorldSession.EntitySpawn spawn = Spawn(scale: 1.5f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == part ? Bsp(3f, centerZ: 2f) : null,
            PoseResolver());

        LiveEntityCollisionRegistration registration =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [part],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));

        ShadowShape shape = Assert.Single(registration.Shapes);
        Assert.Equal(ShadowCollisionType.BSP, shape.CollisionType);
        Assert.Equal(4.5f, shape.Radius);   // 3 m physics-BSP radius * 1.5 scale
        Assert.Equal(new Vector3(0f, 0f, 3f), shape.BoundsCenter);
        Assert.Equal(part, shape.GfxObjId);
    }

    [Fact]
    public void EffectiveReplacementWithoutPhysicsBsp_RemovesBasePartCollision()
    {
        const uint basePart = 0x0100AB01u;
        const uint replacement = 0x0100AB02u;
        var setup = new Setup();
        setup.Parts.Add(basePart);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == basePart ? Bsp(1f) : null,
            PoseResolver());

        Assert.Null(builder.Build(
            entity,
            setup,
            [replacement],
            spawn,
            record.ServerGuid,
            record.Generation,
            record.WorldEntity!,
            record.FinalPhysicsState,
            Vector3.Zero));
    }

    [Fact]
    public void EffectiveReplacementWithPhysicsBsp_AddsReplacementCollision()
    {
        const uint basePart = 0x0100AB11u;
        const uint replacement = 0x0100AB12u;
        var setup = new Setup();
        setup.Parts.Add(basePart);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == replacement ? Bsp(2.25f) : null,
            PoseResolver());

        LiveEntityCollisionRegistration registration =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [replacement],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));

        ShadowShape shape = Assert.Single(registration.Shapes);
        Assert.Equal(replacement, shape.GfxObjId);
        Assert.Equal(2.25f, shape.Radius);
    }

    [Fact]
    public void ReconcileAppearance_VisibleReplacementWithoutBspRemovesOldPayload()
    {
        const uint basePart = 0x0100AC01u;
        const uint replacement = 0x0100AC02u;
        var setup = new Setup();
        setup.Parts.Add(basePart);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == basePart ? Bsp(1f) : null,
            PoseResolver());
        LiveEntityCollisionRegistration initial =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [basePart],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));
        var registry = new ShadowObjectRegistry();
        LiveEntityCollisionBuilder.Register(registry, initial);

        LiveEntityCollisionRegistration? changed = builder.Build(
            entity,
            setup,
            [replacement],
            spawn,
            record.ServerGuid,
            record.Generation,
            record.WorldEntity!,
            record.FinalPhysicsState,
            Vector3.Zero,
            retainEmptyPayload: true);
        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, changed, suspendIfNew: false);

        Assert.Equal(1, registry.RetainedRegistrationCount);
        Assert.DoesNotContain(
            registry.GetObjectsInCell(Cell),
            entry => entry.EntityId == entity.Id);
    }

    [Fact]
    public void ReconcileAppearance_SuspendedReplacementRetainsNewPayloadUntilRestore()
    {
        const uint basePart = 0x0100AC11u;
        const uint replacement = 0x0100AC12u;
        var setup = new Setup();
        setup.Parts.Add(basePart);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == basePart || id == replacement ? Bsp(1f) : null,
            PoseResolver());
        LiveEntityCollisionRegistration initial =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [basePart],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));
        var registry = new ShadowObjectRegistry();
        LiveEntityCollisionBuilder.Register(registry, initial);
        Assert.True(registry.Suspend(entity.Id));
        LiveEntityCollisionRegistration changed =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [replacement],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));

        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, changed, suspendIfNew: true);

        Assert.Equal(1, registry.RetainedRegistrationCount);
        Assert.Equal(1, registry.SuspendedRegistrationCount);
        Assert.DoesNotContain(
            registry.GetObjectsInCell(Cell),
            entry => entry.EntityId == entity.Id);

        LiveEntityCollisionRegistration empty =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [0x0100AC13u],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero,
                retainEmptyPayload: true));
        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, empty, suspendIfNew: true);
        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, changed, suspendIfNew: true);
        Assert.Equal(1, registry.RetainedRegistrationCount);
        Assert.Equal(1, registry.SuspendedRegistrationCount);

        registry.UpdatePosition(
            entity.Id,
            entity.Position,
            entity.Rotation,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: Cell,
            seedCellId: Cell);
        ShadowEntry restored = Assert.Single(
            registry.GetObjectsInCell(Cell),
            entry => entry.EntityId == entity.Id);
        Assert.Equal(replacement, restored.GfxObjId);
        Assert.Equal(0, registry.SuspendedRegistrationCount);
    }

    [Fact]
    public void ReconcileAppearance_ExistingMembershipDoesNotDependOnNewFlood()
    {
        const uint basePart = 0x0100AC21u;
        const uint replacement = 0x0100AC22u;
        var setup = new Setup();
        setup.Parts.Add(basePart);
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;
        var builder = new LiveEntityCollisionBuilder(
            id => id == basePart || id == replacement ? Bsp(1f) : null,
            PoseResolver());
        LiveEntityCollisionRegistration initial =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [basePart],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero));
        var registry = new ShadowObjectRegistry();
        LiveEntityCollisionBuilder.Register(registry, initial);
        LiveEntityCollisionRegistration changed =
            Assert.IsType<LiveEntityCollisionRegistration>(builder.Build(
                entity,
                setup,
                [replacement],
                spawn,
                record.ServerGuid,
                record.Generation,
                record.WorldEntity!,
                record.FinalPhysicsState,
                Vector3.Zero))
            with
            {
                LandblockId = 0u,
                SeedCellId = 0u,
            };

        LiveEntityCollisionBuilder.ReconcileAppearance(
            registry, entity.Id, changed, suspendIfNew: false);

        ShadowEntry entry = Assert.Single(
            registry.GetObjectsInCell(Cell),
            candidate => candidate.EntityId == entity.Id);
        Assert.Equal(replacement, entry.GfxObjId);
    }

    [Fact]
    public void ShapelessSetup_ProducesNoRegistration()
    {
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var record = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        WorldEntity entity = Entity();
        record.WorldEntity = entity;

        Assert.Null(Builder().Build(
            entity,
            new Setup(),
            Array.Empty<uint>(),
            spawn,
            record.ServerGuid,
            record.Generation,
            record.WorldEntity!,
            record.FinalPhysicsState,
            Vector3.Zero));
    }

    [Fact]
    public void Build_RejectsDifferentRecordIncarnation()
    {
        WorldSession.EntitySpawn spawn = Spawn(scale: 1f);
        var expected = LiveEntityTestFixture.CreateExactProjectionRecord(spawn);
        var other = LiveEntityTestFixture.CreateExactProjectionRecord(
            spawn with { InstanceSequence = 2 });
        WorldEntity entity = Entity();
        expected.WorldEntity = entity;
        other.WorldEntity = entity;

        Assert.Throws<InvalidOperationException>(() => Builder().Build(
            entity,
            new Setup { Radius = 1f },
            Array.Empty<uint>(),
            spawn,
            other.ServerGuid,
            other.Generation,
            other.WorldEntity!,
            other.FinalPhysicsState,
            Vector3.Zero));
    }

    private static LiveEntityCollisionBuilder Builder() => new(
        _ => null,
        PoseResolver());

    private static ShadowPartGeometry? Bsp(float radius, float centerZ = 1.25f)
        => ShadowPartGeometry.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, centerZ), radius),
            null);

    private static LiveEntityDefaultPoseResolver PoseResolver() => new(
        _ => null,
        new NullAnimationLoader(),
        dumpMotion: false);

    private static WorldEntity Entity() => new()
    {
        Id = 101u,
        ServerGuid = Guid,
        SourceGfxObjOrSetupId = 0x02000100u,
        Position = new Vector3(4f, 5f, 6f),
        Rotation = Quaternion.Identity,
        MeshRefs = Array.Empty<MeshRef>(),
        ParentCellId = Cell,
    };

    private static WorldSession.EntitySpawn Spawn(
        float scale,
        uint? itemType = null,
        uint? descriptionFlags = null) =>
        new(
            Guid,
            new CreateObject.ServerPosition(Cell, 4f, 5f, 6f, 1f, 0f, 0f, 0f),
            0x02000100u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            BasePaletteId: null,
            ObjScale: scale,
            Name: "collision fixture",
            ItemType: itemType,
            MotionState: null,
            MotionTableId: null,
            PhysicsState: 0u,
            ObjectDescriptionFlags: descriptionFlags,
            InstanceSequence: 1);

    private sealed class NullAnimationLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }
}
