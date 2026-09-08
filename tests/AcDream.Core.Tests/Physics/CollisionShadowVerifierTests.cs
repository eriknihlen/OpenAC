using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class CollisionShadowVerifierTests
{
    [Fact]
    public void MatchingDualView_RefereesEveryTraversalWithoutChangingGraph()
    {
        string directory = NewArtifactDirectory();
        PhysicsDataCache cache = CacheWithShadow(directory);
        CellPhysics cell = MatchingCell(0xA9B4_0100u);
        cache.RegisterCellStructForTest(cell.SourceId, cell);
        var transition = SeedTransition();

        Assert.True(CollisionTraversal.HasCellContainment(cache, cell));
        Assert.True(CollisionTraversal.HasPhysics(cache, cell));
        FlatCollisionSphere root =
            CollisionTraversal.RootBoundingSphere(cache, cell);
        Assert.Equal(10f, root.Radius);
        Assert.True(CollisionTraversal.PointInsideCell(
            cache,
            cell,
            Vector3.Zero));
        Assert.True(CollisionTraversal.SphereIntersectsCell(
            cache,
            cell,
            Vector3.Zero,
            0.48f));
        TransitionState state = CollisionTraversal.FindCollisions(
            cache,
            cell,
            transition,
            Vector3.Zero,
            0.48f,
            false,
            Vector3.Zero,
            0f,
            Vector3.Zero,
            Vector3.UnitZ,
            1f,
            Quaternion.Identity,
            engine: null,
            worldOrigin: Vector3.Zero);

        Assert.Equal(TransitionState.OK, state);
        CollisionShadowStats stats = cache.CollisionShadowStats;
        Assert.Equal(6, stats.Queries);
        Assert.Equal(6, stats.Samples);
        Assert.Equal(6, stats.Matches);
        Assert.Equal(0, stats.Mismatches);
        Assert.Equal(0, stats.Faults);
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public void Mismatch_WritesDeterministicArtifactAndReturnsGraphResult()
    {
        string directory = NewArtifactDirectory();
        PhysicsDataCache cache = CacheWithShadow(directory);
        CellPhysics matching = MatchingCell(0xA9B4_0100u);
        CellPhysics mismatched = WithFlatPhysics(
            matching,
            new FlatPhysicsBsp(
                -1,
                ImmutableArray<FlatPhysicsBspNode>.Empty,
                ImmutableArray<int>.Empty,
                FlatPolygonTable.Empty));

        bool graphResult = CollisionTraversal.HasPhysics(cache, mismatched);

        Assert.True(graphResult);
        CollisionShadowStats stats = cache.CollisionShadowStats;
        Assert.Equal(1, stats.Mismatches);
        Assert.Equal(0, stats.Faults);
        string artifact = Assert.Single(Directory.EnumerateFiles(directory));
        Assert.Equal(
            "collision-shadow-00000001.json",
            Path.GetFileName(artifact));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(artifact));
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("HasCellPhysics", root.GetProperty("Kind").GetString());
        Assert.Equal("0xA9B40100", root.GetProperty("SourceId").GetString());
        Assert.Equal("Result", root.GetProperty("Difference").GetString());
        Assert.Equal("True", root.GetProperty("Graph").GetString());
        Assert.Equal("False", root.GetProperty("Flat").GetString());
    }

    [Fact]
    public void FlatAuthority_MismatchKeepsFlatResultAndUsesGraphOnlyAsReferee()
    {
        string directory = NewArtifactDirectory();
        PhysicsDataCache cache = CacheWithShadow(directory);
        cache.CollisionTraversalMode = CollisionTraversalMode.Flat;
        CellPhysics matching = MatchingCell(0xA9B4_0100u);
        CellPhysics mismatched = WithFlatPhysics(
            matching,
            new FlatPhysicsBsp(
                -1,
                ImmutableArray<FlatPhysicsBspNode>.Empty,
                ImmutableArray<int>.Empty,
                FlatPolygonTable.Empty));

        bool flatResult = CollisionTraversal.HasPhysics(cache, mismatched);

        Assert.False(flatResult);
        CollisionShadowStats stats = cache.CollisionShadowStats;
        Assert.Equal(1, stats.Queries);
        Assert.Equal(1, stats.Samples);
        Assert.Equal(1, stats.Mismatches);
        Assert.Equal(0, stats.Faults);
        string artifact = Assert.Single(Directory.EnumerateFiles(directory));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(artifact));
        JsonElement root = json.RootElement;
        Assert.Equal("True", root.GetProperty("Graph").GetString());
        Assert.Equal("False", root.GetProperty("Flat").GetString());
    }

    [Fact]
    public void MissingFlat_IsRecordedAsFaultAndGraphRemainsAuthoritative()
    {
        string directory = NewArtifactDirectory();
        PhysicsDataCache cache = CacheWithShadow(directory);
        CellPhysics cell = WithoutFlatContainment(
            MatchingCell(0xA9B4_0100u));

        bool graphResult = CollisionTraversal.PointInsideCell(
            cache,
            cell,
            Vector3.Zero);

        Assert.True(graphResult);
        Assert.Equal(1, cache.CollisionShadowStats.Faults);
        string artifact = Assert.Single(Directory.EnumerateFiles(directory));
        Assert.Contains(
            "FlatFault",
            File.ReadAllText(artifact),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionCopyAndComparer_CoverCompleteLogicalState()
    {
        Transition source = SeedTransition();
        source.ObjectInfo.State =
            ObjectInfoState.Contact | ObjectInfoState.IsPlayer;
        source.ObjectInfo.TargetId = 0x5000_0001u;
        source.ObjectInfo.VelocityKilled = true;
        source.SpherePath.NumSphere = 2;
        source.SpherePath.LocalSphere[1].Origin = Vector3.UnitZ;
        source.SpherePath.LocalSphere[1].Radius = 0.48f;
        source.SpherePath.CarriedBlockOrigin = new Vector3(192f, -192f, 0f);
        source.SpherePath.SetWalkable(
            new Plane(Vector3.UnitZ, -1f),
            [
                Vector3.Zero,
                Vector3.UnitX,
                Vector3.UnitY,
            ],
            Vector3.UnitZ);
        source.SpherePath.CellCandidates.Add(0xA9B4_0001u);
        source.SpherePath.CellCandidates.Add(0xA9B4_0100u);
        source.CollisionInfo.SetContactPlane(
            new Plane(Vector3.UnitZ, -1f),
            0xA9B4_0100u);
        source.CollisionInfo.SetSlidingNormal(Vector3.UnitX);
        source.CollisionInfo.CollideObjectGuids.Add(0x8000_0001u);
        source.CollisionInfo.LastCollidedObjectGuid = 0x8000_0001u;

        var clone = new Transition();
        clone.CopyFrom(source);

        Assert.False(TransitionExactComparer.TryFindDifference(
            source,
            clone,
            out _,
            out _,
            out _));
        clone.SpherePath.WalkableVertices![1].X =
            BitConverter.Int32BitsToSingle(
                BitConverter.SingleToInt32Bits(
                    clone.SpherePath.WalkableVertices[1].X) + 1);
        Assert.True(TransitionExactComparer.TryFindDifference(
            source,
            clone,
            out string path,
            out _,
            out _));
        Assert.Equal("SpherePath.WalkableVertices[1]", path);
    }

    [Fact]
    public void DisabledGraphTraversal_AllocatesZeroBytes()
    {
        var cache = new PhysicsDataCache
        {
            CollisionShadow = null,
        };
        CellPhysics cell = MatchingCell(0xA9B4_0100u);

        for (int i = 0; i < 100; i++)
            _ = CollisionTraversal.HasPhysics(cache, cell);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            _ = CollisionTraversal.HasPhysics(cache, cell);
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static PhysicsDataCache CacheWithShadow(string directory)
    {
        var cache = new PhysicsDataCache();
        cache.CollisionShadow = new CollisionShadowVerifier(1, directory);
        return cache;
    }

    private static CellPhysics MatchingCell(uint sourceId)
    {
        var physicsRoot = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = Vector3.Zero,
                Radius = 10f,
            },
        };
        var containmentRoot = new CellBSPNode
        {
            Type = BSPNodeType.Leaf,
            LeafIndex = 1,
        };
        var resolved = new Dictionary<ushort, ResolvedPolygon>();
        return new CellPhysics
        {
            SourceId = sourceId,
            BSP = new PhysicsBSPTree { Root = physicsRoot },
            Resolved = resolved,
            CellBSP = new CellBSPTree { Root = containmentRoot },
            FlatPhysicsBsp =
                FlatCollisionAssetBuilder.FlattenPhysicsBsp(
                    physicsRoot,
                    resolved),
            FlatContainmentBsp =
                FlatCollisionAssetBuilder.FlattenCellContainmentBsp(
                    containmentRoot),
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
        };
    }

    private static Transition SeedTransition()
    {
        var transition = new Transition();
        transition.SpherePath.InitPath(
            Vector3.Zero,
            Vector3.Zero,
            0xA9B4_0100u,
            0.48f);
        return transition;
    }

    private static CellPhysics WithFlatPhysics(
        CellPhysics source,
        FlatPhysicsBsp flat) => new()
    {
        SourceId = source.SourceId,
        BSP = source.BSP,
        PhysicsPolygons = source.PhysicsPolygons,
        Vertices = source.Vertices,
        WorldTransform = source.WorldTransform,
        InverseWorldTransform = source.InverseWorldTransform,
        Resolved = source.Resolved,
        FlatPhysicsBsp = flat,
        CellBSP = source.CellBSP,
        FlatContainmentBsp = source.FlatContainmentBsp,
        FlatPortalPolygons = source.FlatPortalPolygons,
        FlatTopology = source.FlatTopology,
        Portals = source.Portals,
        PortalPolygons = source.PortalPolygons,
        VisibleCellIds = source.VisibleCellIds,
        SeenOutside = source.SeenOutside,
    };

    private static CellPhysics WithoutFlatContainment(
        CellPhysics source) => new()
    {
        SourceId = source.SourceId,
        BSP = source.BSP,
        PhysicsPolygons = source.PhysicsPolygons,
        Vertices = source.Vertices,
        WorldTransform = source.WorldTransform,
        InverseWorldTransform = source.InverseWorldTransform,
        Resolved = source.Resolved,
        FlatPhysicsBsp = source.FlatPhysicsBsp,
        CellBSP = source.CellBSP,
        FlatContainmentBsp = null,
        FlatPortalPolygons = source.FlatPortalPolygons,
        FlatTopology = source.FlatTopology,
        Portals = source.Portals,
        PortalPolygons = source.PortalPolygons,
        VisibleCellIds = source.VisibleCellIds,
        SeenOutside = source.SeenOutside,
    };

    private static string NewArtifactDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "acdream-collision-shadow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
