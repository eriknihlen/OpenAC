using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Scene.Arch;
using AcDream.App.Rendering.Wb;
using AcDream.App.Streaming;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Rendering;

public sealed class StaticRenderProjectionJournalTests
{
    private const uint LandblockId = 0x1234FFFFu;

    [Fact]
    public void Reconcile_RegistersOnlyStaticEntitiesAndEnvCellShells()
    {
        RenderSceneGeneration generation = Generation(1);
        var journal = new RenderProjectionJournal(generation);
        var statics = new StaticRenderProjectionJournal(journal);
        WorldEntity staticEntity = Entity(7, position: new Vector3(1, 2, 3));
        WorldEntity liveEntity = Entity(8, serverGuid: 0x80000008u);
        LandblockBuild build = Build(
            LandblockId,
            [staticEntity, liveEntity],
            includeShell: true);
        var publication = Publication(build);

        statics.Reconcile(build, publication);

        Assert.Equal(2, statics.ProjectionCount);
        Assert.Equal(2, journal.Count);
        Assert.All(
            journal.Pending.ToArray(),
            delta => Assert.Equal(
                RenderProjectionDeltaKind.Register,
                delta.Kind));

        using var scene = new ArchRenderScene(generation);
        RenderDeltaApplyResult applied = journal.DrainTo(scene);
        Assert.Equal(2, applied.Registered);
        Assert.Equal(0, journal.Count);
        Assert.Equal(2, scene.Counts.Total);
        Assert.Equal(1, scene.Counts.OutdoorStatic);
        Assert.Equal(1, scene.Counts.IndoorCellStatic);

        RenderProjectionId staticId =
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                staticEntity.Id);
        Assert.True(scene.OpenQuery().TryGet(staticId, out var projected));
        CurrentRenderProjectionFingerprint expected =
            CurrentRenderSceneOracle.CreateProjectionFingerprint(
                LandblockId,
                staticEntity);
        Assert.Equal(expected.Transform, projected.Source.TransformFingerprint);
        Assert.Equal(expected.Geometry, projected.Source.GeometryFingerprint);
        Assert.Equal(expected.Appearance, projected.Source.AppearanceFingerprint);
        Assert.Equal(LandblockId, projected.Residency.OwnerLandblockId);
        Assert.Equal(
            RenderCasterIdentityKind.OutdoorStatic,
            projected.EntityPayload.CasterIdentity);
    }

    [Fact]
    public void Reconcile_RetainsAuthoritativeBuildingIdentity()
    {
        var journal = new RenderProjectionJournal(Generation(21));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild build = Build(
            LandblockId,
            [Entity(9, building: true)],
            includeShell: false);

        statics.Reconcile(build, Publication(build));

        Assert.Equal(
            RenderCasterIdentityKind.Building,
            Assert.Single(journal.Pending.ToArray())
                .Record.EntityPayload.CasterIdentity);
    }

    [Fact]
    public void Reconcile_SameLandblockRetainsUpdatesOmitsAndAddsDeterministically()
    {
        RenderSceneGeneration generation = Generation(2);
        var journal = new RenderProjectionJournal(generation);
        var statics = new StaticRenderProjectionJournal(journal);
        WorldEntity retained = Entity(1);
        WorldEntity omitted = Entity(2);
        LandblockBuild first = Build(
            LandblockId,
            [retained, omitted],
            includeShell: true);
        statics.Reconcile(first, Publication(first));
        using var scene = new ArchRenderScene(generation);
        journal.DrainTo(scene);
        RenderProjectionId retainedId =
            StaticRenderProjectionJournal.StaticEntityId(LandblockId, 1);
        Assert.True(scene.OpenQuery().TryGet(retainedId, out var before));

        WorldEntity moved = Entity(1, position: new Vector3(10, 0, 0));
        WorldEntity added = Entity(3);
        LandblockBuild replacement = Build(
            LandblockId,
            [moved, added],
            includeShell: false);
        statics.Reconcile(replacement, Publication(replacement));

        Assert.Equal(4, journal.Count);
        Assert.Equal(
            [
                RenderProjectionDeltaKind.UpdateTransform,
                RenderProjectionDeltaKind.Register,
                RenderProjectionDeltaKind.Unregister,
                RenderProjectionDeltaKind.Unregister,
            ],
            journal.Pending.ToArray().Select(delta => delta.Kind).ToArray());

        journal.DrainTo(scene);
        Assert.Equal(2, scene.Counts.Total);
        Assert.True(scene.OpenQuery().TryGet(retainedId, out var after));
        Assert.Equal(before.OwnerIncarnation, after.OwnerIncarnation);
        Assert.Equal(moved.Position, after.Transform.Position);
        Assert.False(scene.OpenQuery().TryGet(
            StaticRenderProjectionJournal.StaticEntityId(LandblockId, 2),
            out _));
        Assert.False(scene.OpenQuery().TryGet(
            StaticRenderProjectionJournal.EnvCellShellId(
                LandblockId,
                (LandblockId & 0xFFFF0000u) | 0x0100u),
            out _));
    }

    [Fact]
    public void ProjectionIdentity_DistinguishesDuplicateLocalIdsAcrossLandblocks()
    {
        const uint otherLandblock = 0x5678FFFFu;
        var journal = new RenderProjectionJournal(Generation(3));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild first = Build(
            LandblockId,
            [Entity(42)],
            includeShell: false);
        LandblockBuild second = Build(
            otherLandblock,
            [Entity(42)],
            includeShell: false);

        statics.Reconcile(first, Publication(first));
        statics.Reconcile(second, Publication(second));

        Assert.Equal(2, statics.ProjectionCount);
        Assert.NotEqual(
            StaticRenderProjectionJournal.StaticEntityId(LandblockId, 42),
            StaticRenderProjectionJournal.StaticEntityId(otherLandblock, 42));
    }

    [Fact]
    public void Reconcile_PreservesResidentTraversalOrderInSortKeys()
    {
        const uint secondLandblock = 0x5678FFFFu;
        var journal = new RenderProjectionJournal(Generation(10));
        var statics = new StaticRenderProjectionJournal(journal);
        WorldEntity first = Entity(99);
        WorldEntity liveGap = Entity(77, serverGuid: 0x80000077u);
        WorldEntity second = Entity(1);
        LandblockBuild firstBuild = Build(
            LandblockId,
            [first, liveGap, second],
            includeShell: false);
        LandblockBuild secondBuild = Build(
            secondLandblock,
            [Entity(500)],
            includeShell: false);

        statics.Reconcile(firstBuild, Publication(firstBuild));
        statics.Reconcile(
            secondBuild,
            Publication(secondBuild, renderTraversalOrder: 2));

        Assert.True(statics.TryGet(
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                first.Id),
            out RenderProjectionRecord firstRecord));
        Assert.True(statics.TryGet(
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                second.Id),
            out RenderProjectionRecord secondRecord));
        Assert.True(statics.TryGet(
            StaticRenderProjectionJournal.StaticEntityId(
                secondLandblock,
                500),
            out RenderProjectionRecord laterLandblockRecord));
        Assert.True(firstRecord.SortKey.Value < secondRecord.SortKey.Value);
        Assert.True(
            secondRecord.SortKey.Value
            < laterLandblockRecord.SortKey.Value);
        Assert.Equal(
            2u,
            unchecked((uint)secondRecord.SortKey.Value));

        LandblockBuild reordered = Build(
            LandblockId,
            [second, first],
            includeShell: false);
        statics.Reconcile(reordered, Publication(reordered));

        Assert.True(statics.TryGet(
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                first.Id),
            out firstRecord));
        Assert.True(statics.TryGet(
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                second.Id),
            out secondRecord));
        Assert.True(secondRecord.SortKey.Value < firstRecord.SortKey.Value);
        Assert.Equal(
            2uL,
            laterLandblockRecord.SortKey.Value >> 32);
    }

    [Fact]
    public void StaleFarPublication_DoesNotMutateProjectionJournal()
    {
        var journal = new RenderProjectionJournal(Generation(4));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild build = Build(
            LandblockId,
            [Entity(1)],
            includeShell: true);
        var stale = Publication(build) with { RequiresActivation = false };

        statics.Reconcile(build, stale);

        Assert.Equal(0, statics.ProjectionCount);
        Assert.Equal(0, journal.Count);
    }

    [Fact]
    public void Retire_IsExactAndIdempotentForFullAndNearLayerReceipts()
    {
        var journal = new RenderProjectionJournal(Generation(5));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild build = Build(
            LandblockId,
            [Entity(1), Entity(2)],
            includeShell: true);
        statics.Reconcile(build, Publication(build));
        using var scene = new ArchRenderScene(Generation(5));
        journal.DrainTo(scene);

        var retirement = new GpuLandblockRetirement(
            LandblockId,
            LandblockRetirementKind.NearLayer,
            build.Landblock.Entities);
        statics.Retire(retirement);
        statics.Retire(retirement);

        Assert.Equal(3, journal.Count);
        Assert.Equal(0, statics.ProjectionCount);
        Assert.All(
            journal.Pending.ToArray(),
            delta => Assert.Equal(
                RenderProjectionDeltaKind.Unregister,
                delta.Kind));
        RenderDeltaApplyResult result = journal.DrainTo(scene);
        Assert.Equal(3, result.Unregistered);
        Assert.Equal(0, scene.Counts.Total);
    }

    [Fact]
    public void Clear_AdvancesExactGenerationAndDropsRetainedState()
    {
        var journal = new RenderProjectionJournal(Generation(6));
        var statics = new StaticRenderProjectionJournal(journal);
        LandblockBuild build = Build(
            LandblockId,
            [Entity(1)],
            includeShell: true);
        statics.Reconcile(build, Publication(build));

        statics.Clear(Generation(7));

        Assert.Equal(Generation(7), journal.Generation);
        Assert.Equal(0, journal.Count);
        Assert.Equal(0, statics.ProjectionCount);
        Assert.Equal(0, statics.LandblockCount);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => statics.Clear(Generation(7)));
    }

    [Fact]
    public void ActiveAnimatedSynchronization_ReclassifiesAndUpdatesOnlyActiveSource()
    {
        RenderSceneGeneration generation = Generation(8);
        var journal = new RenderProjectionJournal(generation);
        var statics = new StaticRenderProjectionJournal(journal);
        WorldEntity animated = Entity(1);
        WorldEntity ordinary = Entity(2);
        LandblockBuild build = Build(
            LandblockId,
            [animated, ordinary],
            includeShell: false);
        statics.Reconcile(build, Publication(build));
        using var scene = new ArchRenderScene(generation);
        journal.DrainTo(scene);
        RenderProjectionId animatedId =
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                animated.Id);
        Assert.True(scene.OpenQuery().TryGet(animatedId, out var before));

        statics.SynchronizeActiveAnimatedSources([animated]);
        Assert.Single(journal.Pending.ToArray());
        Assert.Equal(
            RenderProjectionDeltaKind.Register,
            journal.Pending[0].Kind);
        Assert.Equal(
            RenderProjectionClass.ActiveAnimatedStatic,
            journal.Pending[0].Record.ProjectionClass);
        Assert.Equal(
            before.OwnerIncarnation,
            journal.Pending[0].Record.OwnerIncarnation);
        journal.DrainTo(scene);

        statics.SynchronizeActiveAnimatedSources([animated]);
        Assert.Equal(0, journal.Count);

        animated.Rotation =
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f);
        statics.SynchronizeActiveAnimatedSources([animated]);
        Assert.Single(journal.Pending.ToArray());
        Assert.Equal(
            RenderProjectionDeltaKind.UpdateTransform,
            journal.Pending[0].Kind);
        Assert.True(statics.TryGet(animatedId, out var updated));
        Assert.Equal(animated.Rotation, updated.Transform.Rotation);
        RenderProjectionId ordinaryId =
            StaticRenderProjectionJournal.StaticEntityId(
                LandblockId,
                ordinary.Id);
        Assert.True(statics.TryGet(ordinaryId, out var unchanged));
        Assert.Equal(
            RenderProjectionClass.OutdoorStatic,
            unchanged.ProjectionClass);
    }

    [Fact]
    public void ActiveAnimatedSynchronization_ReusesRetainedJournalStorage()
    {
        const int count = 1_000;
        RenderSceneGeneration generation = Generation(9);
        var journal = new RenderProjectionJournal(generation);
        var statics = new StaticRenderProjectionJournal(journal);
        var animated = new WorldEntity[count];
        for (int index = 0; index < animated.Length; index++)
            animated[index] = Entity((uint)index + 1);
        LandblockBuild build = Build(
            LandblockId,
            animated,
            includeShell: false);
        statics.Reconcile(build, Publication(build));
        using var scene = new ArchRenderScene(generation);
        journal.DrainTo(scene);
        statics.SynchronizeActiveAnimatedSources(animated);
        journal.DrainTo(scene);
        for (int index = 0; index < animated.Length; index++)
        {
            animated[index].Rotation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                0.25f);
        }
        statics.SynchronizeActiveAnimatedSources(animated);
        journal.DrainTo(scene);
        float angle = 0.5f;
        ZeroAllocationProbe.AssertAllocatesNothing(
            "StaticRenderProjectionJournal animated synchronise and drain",
            () =>
            {
                angle += 0.01f;
                for (int index = 0; index < animated.Length; index++)
                {
                    animated[index].Rotation = Quaternion.CreateFromAxisAngle(
                        Vector3.UnitZ,
                        angle);
                }

                statics.SynchronizeActiveAnimatedSources(animated);
                journal.DrainTo(scene);
            });

        Assert.Equal(0, journal.Count);
    }

    private static GpuLandblockSpatialPublication Publication(
        LandblockBuild build,
        bool requiresActivation = true,
        uint renderTraversalOrder = 1) =>
        new(
            build.LandblockId,
            build.Landblock,
            build.EnvCells?.Shells.Select(shell => shell.GeometryId).ToArray()
                ?? [],
            build.Landblock.Entities.Where(entity => entity.ServerGuid == 0).ToArray(),
            requiresActivation,
            renderTraversalOrder);

    private static LandblockBuild Build(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities,
        bool includeShell)
    {
        EnvCellLandblockBuild? envCells = null;
        if (includeShell)
        {
            uint cellId = (landblockId & 0xFFFF0000u) | 0x0100u;
            envCells = new EnvCellLandblockBuild(
                landblockId,
                Array.Empty<LoadedCell>(),
                [
                    new EnvCellShellPlacement(
                        cellId,
                        0x2_0000_0001UL,
                        0x0D000001u,
                        1,
                        ImmutableArray<ushort>.Empty,
                        new Vector3(4, 5, 6),
                        Quaternion.Identity,
                        Matrix4x4.CreateTranslation(4, 5, 6),
                        new WbBoundingBox(Vector3.Zero, Vector3.One),
                        new WbBoundingBox(
                            new Vector3(4, 5, 6),
                            new Vector3(5, 6, 7))),
                ]);
        }

        return new LandblockBuild(
            new LoadedLandblock(
                landblockId,
                new LandBlock(),
                entities,
                PhysicsDatBundle.Empty),
            envCells);
    }

    private static WorldEntity Entity(
        uint id,
        uint serverGuid = 0,
        Vector3 position = default,
        bool building = false) =>
        new()
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x01000000u + id,
            Position = position,
            Rotation = Quaternion.Identity,
            IsBuildingShell = building,
            MeshRefs =
            [
                new MeshRef(
                    0x01010000u + id,
                    Matrix4x4.CreateTranslation(0, 0, id)),
            ],
        };

    private static RenderSceneGeneration Generation(ulong value) =>
        RenderSceneGeneration.FromRaw(value);
}
