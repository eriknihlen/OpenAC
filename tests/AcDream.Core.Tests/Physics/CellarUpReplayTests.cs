using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellarUpReplayTests
{
    private static readonly Vector3 FailingFrameSphereWorld =
        new(141.7164f, 8.3937f, 92.0093f);
    private const float FailingFrameSphereRadius = 0.4800f;
    private const float WalkableAllowance = 0.6642f; // PhysicsGlobals.FloorZ
    private const float StepSearch = 0.6000f;

    private const uint CellarId        = 0xA9B40147u;
    private const uint CottageNeighborA = 0xA9B40143u;
    private const uint CottageNeighborB = 0xA9B40146u;

    private static readonly string FixtureDir =
        Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests",
                     "Fixtures", "cellar-ascent");

    [Fact]
    public void Fixtures_AllThreeCellsLoadAndShareOrigin()
    {
        var cells = LoadAllThreeCells();

        Assert.Equal(3, cells.Length);
        foreach (var (id, cell) in cells)
        {
            Assert.NotEmpty(cell.Resolved);
            var origin = Vector3.Transform(Vector3.Zero, cell.WorldTransform);
            Assert.InRange(origin.X, 130.49f, 130.51f);
            Assert.InRange(origin.Y, 11.49f, 11.51f);
            Assert.InRange(origin.Z, 93.99f, 94.01f);
        }
    }

    [Fact]
    public void Cellar_HasMostPolygons_CottageNeighborBIsSparse()
    {
        var cellar  = LoadCell(CellarId);
        var nbrA    = LoadCell(CottageNeighborA);
        var nbrB    = LoadCell(CottageNeighborB);

        Assert.InRange(cellar.Resolved.Count, 30, 50);
        Assert.InRange(nbrA.Resolved.Count, 10, 20);
        Assert.InRange(nbrB.Resolved.Count, 1, 8);
    }

    [Fact]
    public void FailingFrame_CellarPrimary_HasCellarRampAsNearestWalkable()
    {
        var cell = LoadCell(CellarId);
        var local = ToCellLocal(FailingFrameSphereWorld, cell);
        var nearest = FindNearestWalkable(cell, local, FailingFrameSphereRadius);

        Assert.True(nearest.Found,
            "Cellar should always have at least one walkable candidate " +
            "at the failing frame; the ramp's foot is right under the sphere.");
        Assert.True(nearest.Polygon!.Plane.Normal.Z > WalkableAllowance,
            "Nearest walkable normal must be above WalkableAllowance.");
    }

    [Fact]
    public void FailingFrame_CottageNeighborA_NearestWalkableIsOutsideSphereAndEdges()
    {
        var cell = LoadCell(CottageNeighborA);
        var local = ToCellLocal(FailingFrameSphereWorld, cell);
        var nearest = FindNearestWalkable(cell, local, FailingFrameSphereRadius);

        Assert.True(nearest.Found,
            "0xA9B40143 must have at least one walkable candidate " +
            "(otherwise the nearest-walkable diagnostic itself would be wrong).");

        Assert.False(nearest.OverlapsSphere,
            "Failing frame: sphere center is too far below the cottage floor " +
            "plane in 0xA9B40143 — overlapsSphere is false.");
        Assert.False(nearest.InsideEdges,
            "Failing frame: sphere XY is beyond the cottage floor triangle's " +
            "edge in 0xA9B40143 — insideEdges is false.");
    }

    [Fact]
    public void FailingFrame_CottageNeighborB_HasNoWalkableCandidate()
    {
        var cell = LoadCell(CottageNeighborB);
        var local = ToCellLocal(FailingFrameSphereWorld, cell);
        var nearest = FindNearestWalkable(cell, local, FailingFrameSphereRadius);

        Assert.False(nearest.Found,
            "0xA9B40146 has no walkable polygon close enough to the failing-frame " +
            "sphere for the nearest-walkable diagnostic to select.");
    }

    [Fact]
    public void FailingFrame_NoCottageNeighbourYieldsAcceptedWalkable()
    {
        var nbrA = LoadCell(CottageNeighborA);
        var nbrB = LoadCell(CottageNeighborB);

        var localA = ToCellLocal(FailingFrameSphereWorld, nbrA);
        var localB = ToCellLocal(FailingFrameSphereWorld, nbrB);

        var resultA = FindNearestWalkable(nbrA, localA, FailingFrameSphereRadius);
        var resultB = FindNearestWalkable(nbrB, localB, FailingFrameSphereRadius);

        bool nbrAAccepted = resultA.Found && resultA.InsideEdges && resultA.OverlapsSphere;
        bool nbrBAccepted = resultB.Found && resultB.InsideEdges && resultB.OverlapsSphere;

        Assert.False(nbrAAccepted || nbrBAccepted,
            "Failing frame: no cottage neighbour cell yields a walkable " +
            "that passes both insideEdges and overlapsSphere.");
    }

    [Fact]
    public void FailingFrame_CottageNeighborA_Poly0x0004_HasExpectedShape()
    {
        var cell = LoadCell(CottageNeighborA);

        Assert.True(cell.Resolved.TryGetValue(0x0004, out var poly),
            "Poly 0x0004 must exist in 0xA9B40143 — it's the nearest-walkable " +
            "candidate per the negpoly capture.");
        Assert.NotNull(poly);
        Assert.Equal(3, poly!.NumPoints);
        Assert.Equal(0f, poly.Plane.Normal.X, precision: 4);
        Assert.Equal(0f, poly.Plane.Normal.Y, precision: 4);
        Assert.Equal(1f, poly.Plane.Normal.Z, precision: 4);
        Assert.Equal(0f, poly.Plane.D, precision: 4);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static (uint Id, CellPhysics Cell)[] LoadAllThreeCells() => new[]
    {
        (CellarId,         LoadCell(CellarId)),
        (CottageNeighborA, LoadCell(CottageNeighborA)),
        (CottageNeighborB, LoadCell(CottageNeighborB)),
    };

    private static CellPhysics LoadCell(uint cellId)
    {
        var path = Path.Combine(FixtureDir, $"0x{cellId:X8}.json");
        Assert.True(File.Exists(path),
            $"Fixture missing: {path}. Re-run cell-dump capture (Step 2 of plan).");
        var dump = CellDumpSerializer.Read(path);
        return CellDumpSerializer.Hydrate(dump);
    }

    private static Vector3 ToCellLocal(Vector3 worldPos, CellPhysics cell)
        => Vector3.Transform(worldPos, cell.InverseWorldTransform);

    private static NearestWalkableResult FindNearestWalkable(
        CellPhysics cell, Vector3 localCenter, float sphereRadius)
    {
        ResolvedPolygon? nearest = null;
        ushort nearestId = 0;
        float nearestAbs = float.MaxValue;
        float nearestSigned = 0f;
        bool overlaps = false;
        bool insideEdges = false;

        foreach (var (id, poly) in cell.Resolved)
        {
            float normalDotUp = Vector3.Dot(Vector3.UnitZ, poly.Plane.Normal);
            if (normalDotUp <= WalkableAllowance)
                continue;

            float signed = Vector3.Dot(poly.Plane.Normal, localCenter) + poly.Plane.D;
            float abs = MathF.Abs(signed);
            if (abs >= nearestAbs)
                continue;

            nearest = poly;
            nearestId = id;
            nearestAbs = abs;
            nearestSigned = signed;
            overlaps = abs <= sphereRadius - 1e-4f;
            insideEdges = !BSPQuery.FindCrossedEdge(
                poly.Plane, poly.Vertices, localCenter, Vector3.UnitZ, out _);
        }

        return new NearestWalkableResult
        {
            Found = nearest is not null,
            Polygon = nearest,
            PolygonId = nearestId,
            SignedDistance = nearestSigned,
            AbsDistance = nearestAbs,
            OverlapsSphere = overlaps,
            InsideEdges = insideEdges,
        };
    }

    private sealed class NearestWalkableResult
    {
        public bool Found { get; init; }
        public ResolvedPolygon? Polygon { get; init; }
        public ushort PolygonId { get; init; }
        public float SignedDistance { get; init; }
        public float AbsDistance { get; init; }
        public bool OverlapsSphere { get; init; }
        public bool InsideEdges { get; init; }
    }

    private static string SolutionRoot()
    {
        // Walk up from the test binary until we find AcDream.slnx.
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate AcDream.slnx from " + AppContext.BaseDirectory);
    }
}
