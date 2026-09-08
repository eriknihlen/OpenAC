using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Selection;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class CurrentRenderSceneOracleTests
{
    private const uint LandblockA = 0xA9B4FFFF;
    private const uint LandblockB = 0xA9B5FFFF;
    private const uint CellA = 0xA9B40170;
    private const uint CellB = 0xA9B50170;

    [Fact]
    public void DigestIsStableAcrossLandblockEnumerationOrder()
    {
        WorldEntity outdoorA = Entity(id: 7, serverGuid: 0, parentCell: null);
        WorldEntity cellStatic = Entity(id: 8, serverGuid: 0, parentCell: CellA);
        WorldEntity live = Entity(
            id: 1_000_001,
            serverGuid: 0x8000_0001,
            parentCell: CellA);
        // Static IDs are landblock-local. The same id in another landblock is
        // a distinct projection and must survive the referee sort.
        WorldEntity outdoorB = Entity(id: 7, serverGuid: 0, parentCell: null);

        var firstEntries = new[]
        {
            Entry(LandblockA, outdoorA, cellStatic, live),
            Entry(LandblockB, outdoorB),
        };
        var secondEntries = new[]
        {
            Entry(LandblockB, outdoorB),
            Entry(LandblockA, outdoorA, cellStatic, live),
        };
        var visible = new HashSet<uint> { CellA };
        var result = new InteriorEntityPartition.Result();
        var oracle = new CurrentRenderSceneOracle();

        InteriorEntityPartition.Partition(
            result,
            visible,
            firstEntries,
            oracle);
        CurrentRenderSceneOracleSnapshot first = oracle.Snapshot;

        InteriorEntityPartition.Partition(
            result,
            visible,
            secondEntries,
            oracle);
        CurrentRenderSceneOracleSnapshot second = oracle.Snapshot;

        Assert.True(first.Enabled);
        Assert.Equal(4, first.ProjectionCount);
        Assert.Equal(2, first.OutdoorStaticCount);
        Assert.Equal(1, first.CellStaticCount);
        Assert.Equal(1, first.DynamicCount);
        Assert.Equal(1, first.CellBucketCount);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.CompletedFrameSequence + 1, second.CompletedFrameSequence);
        Assert.Equal(2, oracle.Projections.Count(
            projection => projection.EntityId == 7));
        Assert.Contains(
            oracle.Projections,
            projection => projection.EntityId == 7
                && projection.LandblockId == LandblockA);
        Assert.Contains(
            oracle.Projections,
            projection => projection.EntityId == 7
                && projection.LandblockId == LandblockB);
    }

    [Fact]
    public void ObservedPartitionMatchesUninstrumentedProductionPath()
    {
        WorldEntity outdoor = Entity(id: 10, serverGuid: 0, parentCell: null);
        WorldEntity cellStatic = Entity(id: 11, serverGuid: 0, parentCell: CellA);
        WorldEntity dynamic = Entity(
            id: 1_000_011,
            serverGuid: 0x8000_0011,
            parentCell: CellB);
        WorldEntity hiddenStatic = Entity(
            id: 12,
            serverGuid: 0,
            parentCell: CellB);
        var entries = new[]
        {
            Entry(LandblockA, outdoor, cellStatic),
            Entry(LandblockB, dynamic, hiddenStatic),
        };
        var visible = new HashSet<uint> { CellA };
        var production = new InteriorEntityPartition.Result();
        var observed = new InteriorEntityPartition.Result();
        var oracle = new CurrentRenderSceneOracle();

        InteriorEntityPartition.Partition(production, visible, entries);
        InteriorEntityPartition.Partition(observed, visible, entries, oracle);

        Assert.Equal(production.ByCell.Keys, observed.ByCell.Keys);
        foreach (uint cell in production.ByCell.Keys)
            Assert.Equal(production.ByCell[cell], observed.ByCell[cell]);
        Assert.Equal(production.OutdoorStatic, observed.OutdoorStatic);
        Assert.Equal(production.Dynamics, observed.Dynamics);
    }

    [Fact]
    public void FingerprintsNameTransformGeometryAppearanceAndFlagChanges()
    {
        var visible = new HashSet<uint> { CellA, CellB };

        CurrentRenderProjectionFingerprint baseline = CaptureSingle(
            Entity(id: 20, serverGuid: 0, parentCell: CellA),
            LandblockA,
            visible);
        CurrentRenderProjectionFingerprint transform = CaptureSingle(
            Entity(
                id: 20,
                serverGuid: 0,
                parentCell: CellA,
                position: new Vector3(1, 2, 3)),
            LandblockA,
            visible);
        CurrentRenderProjectionFingerprint geometry = CaptureSingle(
            Entity(
                id: 20,
                serverGuid: 0,
                parentCell: CellA,
                meshId: 0x0100_2222),
            LandblockA,
            visible);
        CurrentRenderProjectionFingerprint appearance = CaptureSingle(
            Entity(
                id: 20,
                serverGuid: 0,
                parentCell: CellA,
                palette: new PaletteOverride(
                    0x0400_0001,
                    [new PaletteOverride.SubPaletteRange(0x0400_0002, 3, 4)])),
            LandblockA,
            visible);
        WorldEntity hidden = Entity(id: 20, serverGuid: 0, parentCell: CellA);
        hidden.IsDrawVisible = false;
        CurrentRenderProjectionFingerprint flags = CaptureSingle(
            hidden,
            LandblockA,
            visible);
        CurrentRenderProjectionFingerprint cell = CaptureSingle(
            Entity(id: 20, serverGuid: 0, parentCell: CellB),
            LandblockB,
            visible);

        Assert.NotEqual(baseline.Transform, transform.Transform);
        Assert.Equal(baseline.Geometry, transform.Geometry);
        Assert.Equal(baseline.Appearance, transform.Appearance);

        Assert.NotEqual(baseline.Geometry, geometry.Geometry);
        Assert.Equal(baseline.Transform, geometry.Transform);

        Assert.NotEqual(baseline.Appearance, appearance.Appearance);
        Assert.NotEqual(baseline.Flags, flags.Flags);
        Assert.NotEqual(baseline.ParentCellId, cell.ParentCellId);
        Assert.NotEqual(baseline.LandblockId, cell.LandblockId);
    }

    [Fact]
    public void NonFloodedStaticIsAbsentButNonFloodedDynamicRemains()
    {
        WorldEntity hiddenStatic = Entity(
            id: 30,
            serverGuid: 0,
            parentCell: CellB);
        WorldEntity hiddenDynamic = Entity(
            id: 1_000_030,
            serverGuid: 0x8000_0030,
            parentCell: CellB);
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();

        InteriorEntityPartition.Partition(
            result,
            new HashSet<uint> { CellA },
            new[] { Entry(LandblockB, hiddenStatic, hiddenDynamic) },
            oracle);

        Assert.Equal(1, oracle.Snapshot.ProjectionCount);
        Assert.Equal(0, oracle.Snapshot.CellStaticCount);
        Assert.Equal(1, oracle.Snapshot.DynamicCount);
        CurrentRenderProjectionFingerprint fingerprint =
            Assert.Single(oracle.Projections);
        Assert.Equal(hiddenDynamic.Id, fingerprint.EntityId);
        Assert.Equal(
            InteriorEntityPartition.ProjectionClass.Dynamic,
            fingerprint.ProjectionClass);
    }

    [Fact]
    public void AbortPreservesLastCompleteDigestAndRecordsFailure()
    {
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        WorldEntity entity = Entity(id: 40, serverGuid: 0, parentCell: null);

        InteriorEntityPartition.Partition(
            result,
            [],
            new[] { Entry(LandblockA, entity) },
            oracle);
        CurrentRenderSceneOracleSnapshot complete = oracle.Snapshot;

        oracle.BeginFrame();
        oracle.Observe(
            LandblockA,
            entity,
            InteriorEntityPartition.ProjectionClass.OutdoorStatic);
        oracle.AbortFrame();

        Assert.Equal(complete.Digest, oracle.Snapshot.Digest);
        Assert.Equal(
            complete.CompletedFrameSequence,
            oracle.Snapshot.CompletedFrameSequence);
        Assert.Equal(complete.AbortedFrames + 1, oracle.Snapshot.AbortedFrames);
        Assert.Empty(oracle.Projections);
    }

    [Fact]
    public void PViewDigestNamesRouteOrderAndRejectsObjectsOutsidePartition()
    {
        WorldEntity cellStatic = Entity(
            id: 50,
            serverGuid: 0,
            parentCell: CellA);
        WorldEntity dynamic = Entity(
            id: 1_000_050,
            serverGuid: 0x8000_0050,
            parentCell: CellA);
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        InteriorEntityPartition.Partition(
            result,
            new HashSet<uint> { CellA },
            new[] { Entry(LandblockA, cellStatic, dynamic) },
            oracle);

        oracle.BeginPViewFrame();
        oracle.ObservePViewBucket(
            CurrentRenderPViewRoute.CellStatic,
            routeIndex: 0,
            CellA,
            [cellStatic]);
        oracle.ObservePViewBucket(
            CurrentRenderPViewRoute.DynamicLast,
            routeIndex: 0,
            cellId: 0,
            [dynamic]);
        oracle.CompletePViewFrame();
        CurrentRenderSceneOracleSnapshot first = oracle.Snapshot;

        oracle.BeginPViewFrame();
        oracle.ObservePViewBucket(
            CurrentRenderPViewRoute.DynamicLast,
            routeIndex: 0,
            cellId: 0,
            [dynamic]);
        oracle.ObservePViewBucket(
            CurrentRenderPViewRoute.CellStatic,
            routeIndex: 0,
            CellA,
            [cellStatic]);
        oracle.CompletePViewFrame();
        CurrentRenderSceneOracleSnapshot second = oracle.Snapshot;

        Assert.Equal(2, first.PViewCandidateCount);
        Assert.NotEqual(first.PViewDigest, second.PViewDigest);
        Assert.Equal(
            first.CompletedPViewFrameSequence + 1,
            second.CompletedPViewFrameSequence);

        oracle.BeginPViewFrame();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => oracle.ObservePViewBucket(
                CurrentRenderPViewRoute.CellStatic,
                routeIndex: 0,
                CellA,
                [Entity(51, 0, CellA)]));
        Assert.Contains("absent from the current partition", error.Message);
        oracle.AbortPViewFrame();
    }

    [Fact]
    public void DispatcherDigestResetsPerFrameAndRecordsAcceptedMeshRefs()
    {
        WorldEntity entity = Entity(
            id: 60,
            serverGuid: 0,
            parentCell: CellA);
        var accepted = new List<(
            WorldEntity Entity,
            int MeshRefIndex,
            uint LandblockId)>
        {
            (entity, 0, LandblockB),
        };
        var oracle = new CurrentRenderSceneOracle();

        oracle.BeginDispatcherFrame();
        oracle.ObserveDispatcherDraw(
            WbDrawDispatcher.EntitySet.All,
            entitiesWalked: 1,
            accepted);
        var submission = new CurrentRenderDispatcherSubmission(
            VisibleInstanceCount: 3,
            ImmediateInstanceCount: 2,
            OpaqueGroupCount: 1,
            TransparentGroupCount: 1,
            TransparentDeferred: true,
            OpaqueDigest: new RenderSceneHash128(11, 12),
            TransparentDigest: new RenderSceneHash128(21, 22),
            TransparentSetDigest: new RenderSceneHash128(31, 32),
            Digest: new RenderSceneHash128(101, 202));
        oracle.ObserveDispatcherSubmission(in submission);
        CurrentRenderSceneOracleSnapshot first = oracle.Snapshot;

        Assert.Equal(1uL, first.DispatcherFrameSequence);
        Assert.Equal(1, first.DispatcherDrawCount);
        Assert.Equal(1, first.DispatcherEntityCount);
        Assert.Equal(1, first.DispatcherMeshRefCount);
        Assert.Equal(3, first.DispatcherInstanceCount);
        Assert.Equal(1, first.DispatcherOpaqueGroupCount);
        Assert.Equal(1, first.DispatcherTransparentGroupCount);
        CurrentRenderDispatcherFingerprint fingerprint =
            Assert.Single(oracle.DispatcherCandidates);
        Assert.Equal(LandblockB, fingerprint.TupleLandblockId);
        Assert.Equal(LandblockA, fingerprint.CacheLandblockId);

        oracle.BeginDispatcherFrame();
        CurrentRenderSceneOracleSnapshot reset = oracle.Snapshot;

        Assert.Equal(2uL, reset.DispatcherFrameSequence);
        Assert.Equal(0, reset.DispatcherDrawCount);
        Assert.Equal(0, reset.DispatcherMeshRefCount);
        Assert.Empty(oracle.DispatcherCandidates);
        Assert.NotEqual(first.DispatcherDigest, reset.DispatcherDigest);
    }

    [Fact]
    public void DispatcherTemporaryBucket_DoesNotRewriteProjectionOwner()
    {
        WorldEntity outdoor = Entity(
            id: 61,
            serverGuid: 0,
            parentCell: null);
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        InteriorEntityPartition.Partition(
            result,
            [],
            [Entry(LandblockA, outdoor)],
            oracle);

        oracle.BeginDispatcherFrame();
        oracle.ObserveDispatcherDraw(
            WbDrawDispatcher.EntitySet.All,
            entitiesWalked: 1,
            [(outdoor, 0, LandblockB)]);

        CurrentRenderDispatcherFingerprint candidate =
            Assert.Single(oracle.DispatcherCandidates);
        Assert.Equal(LandblockA, candidate.Projection.LandblockId);
        Assert.Equal(LandblockB, candidate.TupleLandblockId);
        Assert.Equal(LandblockB, candidate.CacheLandblockId);
    }

    [Fact]
    public void DispatcherAbortDiscardsIncompleteCandidates()
    {
        WorldEntity entity = Entity(
            id: 70,
            serverGuid: 0x8000_0070,
            parentCell: CellA);
        var oracle = new CurrentRenderSceneOracle();

        oracle.BeginDispatcherFrame();
        oracle.ObserveDispatcherDraw(
            WbDrawDispatcher.EntitySet.All,
            entitiesWalked: 1,
            [(entity, 0, LandblockA)]);
        oracle.AbortDispatcherFrame();

        Assert.Equal(1, oracle.Snapshot.AbortedDispatcherFrames);
        Assert.Empty(oracle.DispatcherCandidates);
    }

    [Fact]
    public void SelectionGeometryFingerprint_IsCachedAndFrameStorageIsReused()
    {
        const int partCount = 1_000;
        var oracle = new CurrentRenderSceneOracle();
        var mesh = new RetailSelectionMesh(
            Vector3.Zero,
            2f,
            [new RetailSelectionPolygon(
                [
                    new(-1f, -1f, 0f),
                    new(1f, -1f, 0f),
                    new(1f, 1f, 0f),
                    new(-1f, 1f, 0f),
                ],
                SingleSided: false)]);
        ZeroAllocationProbe.AssertAllocatesNothing(
            "CurrentRenderSceneOracle selection frame publish",
            PublishSelectionFrame);

        Assert.Equal(partCount, oracle.Snapshot.SelectionPartCount);

        void PublishSelectionFrame()
        {
            oracle.BeginSelectionFrame();
            for (int index = 0; index < partCount; index++)
            {
                oracle.ObserveSelectionPart(
                    serverGuid: (uint)index + 1,
                    localEntityId: (uint)index + 1,
                    partIndex: index,
                    gfxObjId: 0x0100_0001u,
                    Matrix4x4.CreateTranslation(index, 0, 0),
                    mesh);
            }
            oracle.CompleteSelectionFrame();
        }
    }

    [Fact]
    public void SurfaceOverrideFingerprint_IsOrderIndependentAndContentExact()
    {
        var first = new Dictionary<uint, uint>
        {
            [0x0800_0002] = 0x0500_0012,
            [0x0800_0001] = 0x0500_0011,
        };
        var reordered = new Dictionary<uint, uint>
        {
            [0x0800_0001] = 0x0500_0011,
            [0x0800_0002] = 0x0500_0012,
        };
        var changed = new Dictionary<uint, uint>
        {
            [0x0800_0001] = 0x0500_0011,
            [0x0800_0002] = 0x0500_0013,
        };

        RenderSceneHash128 expected =
            CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(first);

        Assert.Equal(
            expected,
            CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(
                reordered));
        Assert.NotEqual(
            expected,
            CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(
                changed));
        Assert.NotEqual(
            expected,
            CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(null));
    }

    [Fact]
    public void SurfaceOverrideFingerprint_DictionaryHotPathAllocatesNothing()
    {
        var overrides = new Dictionary<uint, uint>
        {
            [0x0800_0001] = 0x0500_0011,
            [0x0800_0002] = 0x0500_0012,
            [0x0800_0003] = 0x0500_0013,
        };
        RenderSceneHash128 expected =
            CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(
                overrides);
        RenderSceneHash128 actual = default;

        ZeroAllocationProbe.AssertAllocatesNothing(
            "CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint",
            () => actual =
                CurrentRenderSceneOracle.CreateSurfaceOverrideFingerprint(
                    overrides));

        Assert.Equal(expected, actual);
    }

    private static CurrentRenderProjectionFingerprint CaptureSingle(
        WorldEntity entity,
        uint landblockId,
        HashSet<uint> visible)
    {
        var oracle = new CurrentRenderSceneOracle();
        var result = new InteriorEntityPartition.Result();
        InteriorEntityPartition.Partition(
            result,
            visible,
            new[] { Entry(landblockId, entity) },
            oracle);
        return Assert.Single(oracle.Projections);
    }

    private static WorldEntity Entity(
        uint id,
        uint serverGuid,
        uint? parentCell,
        Vector3? position = null,
        uint meshId = 0x0100_0001,
        PaletteOverride? palette = null) =>
        new()
        {
            Id = id,
            ServerGuid = serverGuid,
            SourceGfxObjOrSetupId = meshId,
            Position = position ?? Vector3.Zero,
            Rotation = Quaternion.Identity,
            MeshRefs =
            [
                new MeshRef(
                    meshId,
                    Matrix4x4.CreateTranslation(0.25f, 0.5f, 0.75f)),
            ],
            ParentCellId = parentCell,
            PaletteOverride = palette,
        };

    private static (
        uint LandblockId,
        Vector3 AabbMin,
        Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)
        Entry(uint landblockId, params WorldEntity[] entities) =>
        (
            landblockId,
            Vector3.Zero,
            Vector3.One,
            entities,
            null);
}
