using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class RenderSceneShadowRuntimeTests
{
    private const uint Landblock = 0xA9B4FFFF;
    private const uint Cell = 0xA9B40170;

    [Fact]
    public void ExactCurrentProjection_MatchesAndAcknowledgesDirtyWorkset()
    {
        WorldEntity entity = Entity(17, serverGuid: 0, parentCell: null);
        CurrentRenderSceneOracle oracle = Capture(
            entity,
            visibleCells: []);
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        Register(shadow, entity, RenderProjectionClass.OutdoorStatic);
        Assert.Equal(1, shadow.DrainUpdateBoundary().Registered);

        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle);
        RenderSceneShadowComparisonSnapshot snapshot =
            controller.CaptureCheckpointSnapshot();

        Assert.True(snapshot.Enabled);
        Assert.Equal(1uL, snapshot.ComparisonCount);
        Assert.Equal(1uL, snapshot.SuccessfulComparisonCount);
        Assert.Equal(0, snapshot.MismatchCount);
        Assert.Equal(1, snapshot.MatchedProjectionCount);
        Assert.Equal(0, snapshot.PendingDeltaCount);
        Assert.Equal(0, snapshot.SceneIndexCounts.Dirty);
        Assert.Equal(1, snapshot.SceneCounts.Total);
        Assert.True(snapshot.Memory.TotalEstimatedBytes > 0);
    }

    [Fact]
    public void FirstExactMismatch_IsNamedAndLoggedOnlyOnce()
    {
        WorldEntity entity = Entity(18, serverGuid: 0, parentCell: null);
        CurrentRenderSceneOracle oracle = Capture(entity, []);
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        RenderProjectionRecord record = Project(
            entity,
            RenderProjectionClass.OutdoorStatic) with
        {
            Source = Project(
                entity,
                RenderProjectionClass.OutdoorStatic).Source with
            {
                TransformFingerprint = new RenderSceneHash128(91, 92),
            },
        };
        shadow.Journal.Register(in record);
        shadow.DrainUpdateBoundary();
        var logs = new List<string>();
        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle,
            logs.Add);

        controller.CompareNow();
        controller.CompareNow();
        RenderSceneShadowComparisonSnapshot snapshot = controller.Snapshot;

        Assert.Equal(2uL, snapshot.ComparisonCount);
        Assert.Equal(0uL, snapshot.SuccessfulComparisonCount);
        Assert.Equal(2, snapshot.MismatchCount);
        Assert.Contains("sourceChannel=static", snapshot.FirstMismatch);
        Assert.Contains("field=transform", snapshot.FirstMismatch);
        Assert.Single(logs);
        Assert.Equal(1, snapshot.SceneIndexCounts.Dirty);
    }

    [Fact]
    public void PackedProductOwnership_LeavesDirtyAcknowledgementToProduct()
    {
        WorldEntity entity = Entity(23, serverGuid: 0, parentCell: null);
        CurrentRenderSceneOracle oracle = Capture(entity, []);
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        Register(shadow, entity, RenderProjectionClass.OutdoorStatic);
        shadow.DrainUpdateBoundary();
        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle,
            acknowledgeDirty: false);

        controller.CompareNow();

        Assert.Equal(1uL, controller.Snapshot.SuccessfulComparisonCount);
        Assert.Equal(1, controller.Snapshot.SceneIndexCounts.Dirty);
    }

    [Fact]
    public void NonFloodedIndoorStatic_IsAValidRetainedSceneExtra()
    {
        WorldEntity entity = Entity(19, serverGuid: 0, parentCell: Cell);
        CurrentRenderSceneOracle oracle = Capture(entity, []);
        Assert.Empty(oracle.Projections);
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        Register(shadow, entity, RenderProjectionClass.IndoorCellStatic);
        shadow.DrainUpdateBoundary();
        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle);

        controller.CompareNow();

        Assert.Equal(1uL, controller.Snapshot.SuccessfulComparisonCount);
        Assert.Equal(0, controller.Snapshot.MismatchCount);
    }

    [Fact]
    public void DrawnOutdoorExtra_IsReportedAsStaleProjection()
    {
        WorldEntity expected = Entity(20, serverGuid: 0, parentCell: null);
        WorldEntity extra = Entity(21, serverGuid: 0, parentCell: null);
        CurrentRenderSceneOracle oracle = Capture(expected, []);
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        Register(shadow, expected, RenderProjectionClass.OutdoorStatic);
        Register(shadow, extra, RenderProjectionClass.OutdoorStatic);
        shadow.DrainUpdateBoundary();
        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle);

        controller.CompareNow();

        Assert.Equal(1, controller.Snapshot.MismatchCount);
        Assert.Contains("expected=absent actual=drawn", controller.Snapshot.FirstMismatch);
    }

    [Fact]
    public void RejectedDelta_IsAComparisonFailureEvenWhenSceneIsEmpty()
    {
        CurrentRenderSceneOracle oracle = CaptureNoEntities();
        using var shadow = new RenderSceneShadowRuntime(Generation(1));
        WorldEntity missing = Entity(22, serverGuid: 0, parentCell: null);
        RenderProjectionRecord record = Project(
            missing,
            RenderProjectionClass.OutdoorStatic);
        shadow.Journal.Update(
            RenderProjectionDeltaKind.UpdateTransform,
            in record);
        RenderDeltaApplyResult apply = shadow.DrainUpdateBoundary();
        Assert.Equal(1, apply.RejectedMissing);
        var controller = new RenderSceneShadowComparisonController(
            shadow,
            oracle);

        controller.CompareNow();

        Assert.Equal(1, controller.Snapshot.MismatchCount);
        Assert.Contains("rejectedDeltas=1", controller.Snapshot.FirstMismatch);
    }

    private static void Register(
        RenderSceneShadowRuntime shadow,
        WorldEntity entity,
        RenderProjectionClass projectionClass)
    {
        RenderProjectionRecord record = Project(entity, projectionClass);
        shadow.Journal.Register(in record);
    }

    private static RenderProjectionRecord Project(
        WorldEntity entity,
        RenderProjectionClass projectionClass) =>
        RenderProjectionRecordFactory.ProjectEntity(
            StaticRenderProjectionJournal.StaticEntityId(
                Landblock,
                entity.Id),
            projectionClass,
            RenderOwnerIncarnation.FromRaw(entity.Id),
            Landblock,
            entity.ParentCellId ?? Landblock,
            entity,
            spatiallyVisible: true);

    private static CurrentRenderSceneOracle Capture(
        WorldEntity entity,
        HashSet<uint> visibleCells)
    {
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        InteriorEntityPartition.Partition(
            result,
            visibleCells,
            [
                (
                    Landblock,
                    Vector3.Zero,
                    Vector3.One,
                    (IReadOnlyList<WorldEntity>)[entity],
                    (IReadOnlyDictionary<uint, WorldEntity>?)null),
            ],
            oracle);
        return oracle;
    }

    private static CurrentRenderSceneOracle CaptureNoEntities()
    {
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        InteriorEntityPartition.Partition(
            result,
            [],
            Array.Empty<(
                uint LandblockId,
                Vector3 AabbMin,
                Vector3 AabbMax,
                IReadOnlyList<WorldEntity> Entities,
                IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)>(),
            oracle);
        return oracle;
    }

    private static RenderSceneGeneration Generation(ulong value) =>
        RenderSceneGeneration.FromRaw(value);

    private static WorldEntity Entity(
        uint id,
        uint serverGuid,
        uint? parentCell) =>
        new()
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = 0x02000001u,
            Position = new Vector3(1, 2, 3),
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(
                    0x01000001u,
                    Matrix4x4.CreateTranslation(0.25f, 0.5f, 0.75f)),
            ],
            ParentCellId = parentCell,
        };
}
