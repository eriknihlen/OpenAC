using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class CellTransitFindTransitCellsBoxTests
{
    private static CellPhysics MakeCellWithPortalAtRightWall(
        Matrix4x4 worldTransform,
        uint otherCellId,
        ushort flags)
    {
        var portalPolyA = new ResolvedPolygon
        {
            Id = 10,
            Vertices = new[]
            {
                new Vector3(2.5f, -2.5f, 0f),
                new Vector3(2.5f, 2.5f, 0f),
                new Vector3(2.5f, 2.5f, 5f),
                new Vector3(2.5f, -2.5f, 5f),
            },
            Plane = new Plane(new Vector3(1, 0, 0), -2.5f),  // x = 2.5
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        Matrix4x4.Invert(worldTransform, out var inv);
        return new CellPhysics
        {
            WorldTransform = worldTransform,
            InverseWorldTransform = inv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon> { [10] = portalPolyA },
            Portals = new[]
            {
                new PortalInfo(otherCellId: (ushort)otherCellId, polygonId: 10, flags: flags),
            },
        };
    }

    private static CellBSPTree SinglePlaneCellBsp()
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf };
        return new CellBSPTree
        {
            Root = new CellBSPNode
            {
                Type = BSPNodeType.BPIn,
                SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),
                PosNode = leaf,
            },
        };
    }

    // ── D3.1: pre-fix admits, post-fix does not, with in-session sabotage ──

    [Fact]
    public void SphereReachesPortal_BoxDoesNot_PreFixAdmitsPostFixRejects()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var partWorldPos = new Vector3(2.0f, 0f, 2.5f);
        var sphere = new Sphere { Origin = partWorldPos, Radius = 0.5f };
        var box = new ShadowPartBox[]
        {
            MakeBox(new Vector3(-0.1f), new Vector3(0.1f), partWorldPos, Quaternion.Identity),
        };

        // Pre-fix: the sphere-only traversal still exists, unmodified.
        var preFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            partWorldPos, sphereRadius: 0.5f, preFixCandidates, out bool preFixExitOutside);
        Assert.Contains(0xA9B40101u, preFixCandidates);
        Assert.False(preFixExitOutside);

        // Post-fix: the box-admitting traversal the D2 rewire installs.
        var postFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsBox(
            cache, cellA, currentCellId: 0xA9B40100u,
            box, new[] { sphere }, postFixCandidates, out bool postFixExitOutside);
        Assert.DoesNotContain(0xA9B40101u, postFixCandidates);
        Assert.False(postFixExitOutside);

        IReadOnlyList<uint> endToEnd = CellTransit.BuildShadowCellSetFromParts(
            cache, seedCellId: 0xA9B40100u, box, new[] { sphere }, isStatic: false);
        Assert.DoesNotContain(0xA9B40101u, endToEnd);
    }

    [Fact]
    public void StaticPartArrayCrossesExteriorPortal_KeepsOutdoorCells()
    {
        var cache = new PhysicsDataCache();
        const uint seedCell = 0xF4180112u;
        cache.RegisterCellStructForTest(
            seedCell,
            MakeCellWithPortalAtRightWall(
                Matrix4x4.Identity,
                otherCellId: 0xFFFF,
                flags: 0));

        var partWorldPos = new Vector3(2.0f, 0f, 2.5f);
        var sphere = new Sphere { Origin = partWorldPos, Radius = 0.7f };
        var box = new[]
        {
            MakeBox(
                new Vector3(-0.7f),
                new Vector3(0.7f),
                partWorldPos,
                Quaternion.Identity),
        };

        IReadOnlyList<uint> cells = CellTransit.BuildShadowCellSetFromParts(
            cache,
            seedCell,
            box,
            new[] { sphere },
            isStatic: true);

        Assert.Contains(seedCell, cells);
        Assert.Contains(cells, id => (id & 0xFFFFu) is >= 1u and <= 64u);
    }


    [Fact]
    public void BoxCrossesPortal_AdmittedBeforeAndAfter_UnloadedNeighbour()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cellBT = Matrix4x4.CreateTranslation(new Vector3(5f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var partWorldPos = new Vector3(2.0f, 0f, 2.5f);
        var sphere = new Sphere { Origin = partWorldPos, Radius = 0.5f };
        var box = new ShadowPartBox[]
        {
            MakeBox(new Vector3(-0.7f), new Vector3(0.7f), partWorldPos, Quaternion.Identity),
        };

        var preFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            partWorldPos, sphereRadius: 0.5f, preFixCandidates, out bool preFixExitOutside);
        Assert.Contains(0xA9B40101u, preFixCandidates);
        Assert.False(preFixExitOutside);

        var postFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsBox(
            cache, cellA, currentCellId: 0xA9B40100u,
            box, new[] { sphere }, postFixCandidates, out bool postFixExitOutside);
        Assert.Contains(0xA9B40101u, postFixCandidates);
        Assert.False(postFixExitOutside);
    }

    [Fact]
    public void BoxCrossesPortal_AdmittedBeforeAndAfter_LoadedNeighbourGate()
    {
        var cellA = MakeCellWithPortalAtRightWall(Matrix4x4.Identity, otherCellId: 0x0101, flags: 0);
        var cellBT = Matrix4x4.CreateTranslation(new Vector3(3f, 0f, 0f));
        Matrix4x4.Invert(cellBT, out var cellBInv);
        var cellB = new CellPhysics
        {
            WorldTransform = cellBT,
            InverseWorldTransform = cellBInv,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = SinglePlaneCellBsp(),
        };

        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xA9B40100u, cellA);
        cache.RegisterCellStructForTest(0xA9B40101u, cellB);

        var partWorldPos = new Vector3(2.9f, 0f, 2.5f);
        var sphere = new Sphere { Origin = partWorldPos, Radius = 0.5f };
        var box = new ShadowPartBox[]
        {
            MakeBox(new Vector3(-0.3f), new Vector3(0.3f), partWorldPos, Quaternion.Identity),
        };

        var preFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellA, currentCellId: 0xA9B40100u,
            partWorldPos, sphereRadius: 0.5f, preFixCandidates, out bool preFixExitOutside);
        Assert.Contains(0xA9B40101u, preFixCandidates);
        Assert.False(preFixExitOutside);

        var postFixCandidates = new HashSet<uint>();
        CellTransit.FindTransitCellsBox(
            cache, cellA, currentCellId: 0xA9B40100u,
            box, new[] { sphere }, postFixCandidates, out bool postFixExitOutside);
        Assert.Contains(0xA9B40101u, postFixCandidates);
        Assert.False(postFixExitOutside);
    }

    // ── D3.3: direction assertion over an installed-DAT sweep ──────────────

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_RandomizedPartSweepNearRealPortals_PostFixMembershipIsSubsetOfPreFix()
    {
        string? datDirectory = ConformanceDats.ResolveDatDir();
        if (datDirectory is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        var cache = new PhysicsDataCache();
        uint[] cellIds =
        [
            0x8A02_016Eu,
            0x8A02_017Au,
            0xA9B4_013Fu,
            0xA9B4_0150u,
            0xA9B4_0159u,
            0xA9B4_015Au,
            0xA9B4_0161u,
            0xA9B4_0162u,
            0xA9B4_0164u,
            0xA9B4_0166u,
        ];
        foreach (uint cellId in cellIds)
            ConformanceDats.LoadEnvCell(dats, cache, cellId);

        var random = new Random(0x3335_5330);
        int objectsSwept = 0;
        int cellsRemovedTotal = 0;
        int cellsAddedTotal = 0;
        int objectsWithChangedMembership = 0;
        var worstExamples = new List<(string Object, int Before, int After, int Removed)>();

        foreach (uint cellId in cellIds)
        {
            CellPhysics cell = Assert.IsType<CellPhysics>(cache.GetCellStruct(cellId));
            if (cell.Portals.Count == 0 || cell.PortalPolygons is null)
                continue;

            foreach (PortalInfo portal in cell.Portals)
            {
                if (!cell.PortalPolygons.TryGetValue(portal.PolygonId, out ResolvedPolygon? portalPoly) ||
                    portalPoly.Vertices.Length == 0)
                {
                    continue;
                }

                Vector3 localAnchor = Vector3.Zero;
                foreach (Vector3 v in portalPoly.Vertices)
                    localAnchor += v;
                localAnchor /= portalPoly.Vertices.Length;

                for (int iteration = 0; iteration < 40; iteration++)
                {
                    // Jitter the anchor along the portal's own local frame so
                    // some placements land squarely on one side, some
                    // straddle, and some sit right at the plane -- the
                    // population a real object population would produce.
                    Vector3 jitter = new(
                        NextFloat(random, -0.6f, 0.6f),
                        NextFloat(random, -0.6f, 0.6f),
                        NextFloat(random, -0.6f, 0.6f));
                    Vector3 localPartPos = localAnchor + jitter;
                    Vector3 worldPartPos = Vector3.Transform(
                        localPartPos, cell.WorldTransform);

                    float sphereRadius = NextFloat(random, 0.3f, 1.2f);
                    float boxHalfExtent = NextFloat(random, 0.02f, 0.25f);

                    var sphere = new Sphere
                    {
                        Origin = worldPartPos,
                        Radius = sphereRadius,
                    };
                    var boxes = new ShadowPartBox[]
                    {
                        MakeBox(
                            new Vector3(-boxHalfExtent),
                            new Vector3(boxHalfExtent),
                            worldPartPos,
                            Quaternion.Identity),
                    };

                    var preFix = new HashSet<uint>();
                    CellTransit.FindTransitCellsSphere(
                        cache, cell, cellId, worldPartPos, sphereRadius,
                        preFix, out _);

                    var postFix = new HashSet<uint>();
                    CellTransit.FindTransitCellsBox(
                        cache, cell, cellId, boxes, new[] { sphere },
                        postFix, out _);

                    objectsSwept++;

                    var removed = new List<uint>();
                    var added = new List<uint>();
                    foreach (uint id in preFix)
                        if (!postFix.Contains(id)) removed.Add(id);
                    foreach (uint id in postFix)
                        if (!preFix.Contains(id)) added.Add(id);

                    cellsRemovedTotal += removed.Count;
                    cellsAddedTotal += added.Count;

                    Assert.True(
                        added.Count == 0,
                        $"cell 0x{cellId:X8} portal->0x{portal.OtherCellId:X4} " +
                        $"iteration {iteration}: post-fix ADDED cell(s) " +
                        $"{string.Join(",", added.Select(id => $"0x{id:X8}"))} " +
                        "under a box RIGGED smaller than the sphere -- for this " +
                        "population the admit must strictly shrink.");

                    if (removed.Count > 0)
                    {
                        objectsWithChangedMembership++;
                        string label =
                            $"cell=0x{cellId:X8},portal->0x{portal.OtherCellId:X4}," +
                            $"iter={iteration},pos=({worldPartPos.X:F2}," +
                            $"{worldPartPos.Y:F2},{worldPartPos.Z:F2})," +
                            $"r={sphereRadius:F3},box=+-{boxHalfExtent:F3}";
                        worstExamples.Add((label, preFix.Count, postFix.Count, removed.Count));
                    }
                }
            }
        }

        worstExamples.Sort((a, b) => b.Removed.CompareTo(a.Removed));

        Assert.Equal(0, cellsAddedTotal);
        Assert.True(objectsSwept > 0, "installed-DAT sweep found no portals to test.");

        Assert.True(
            cellsRemovedTotal > 0,
            $"the box admit removed no cells across {objectsSwept} rigged " +
            "placements — indistinguishable from an unwired no-op.");
        Console.WriteLine(
            $"direction sweep (rigged population): objectsSwept={objectsSwept} " +
            $"changed={objectsWithChangedMembership} removed={cellsRemovedTotal} " +
            $"added={cellsAddedTotal}");

        int prodSwept = 0, prodAdded = 0, prodRemoved = 0, loadedBranchHits = 0;
        var prodRandom = new Random(0x3335_5331);
        foreach (uint cellId in cellIds)
        {
            CellPhysics? cell = cache.GetCellStruct(cellId) as CellPhysics;
            if (cell is null || cell.Portals.Count == 0 || cell.PortalPolygons is null)
                continue;
            foreach (PortalInfo portal in cell.Portals)
            {
                if (!cell.PortalPolygons.TryGetValue(portal.PolygonId, out ResolvedPolygon? prodPoly) ||
                    prodPoly.Vertices.Length == 0)
                {
                    continue;
                }
                Vector3 localAnchor = Vector3.Zero;
                foreach (Vector3 v in prodPoly.Vertices)
                    localAnchor += v;
                localAnchor /= prodPoly.Vertices.Length;
                for (int iteration = 0; iteration < 25; iteration++)
                {
                    Vector3 jitter = new(
                        NextFloat(prodRandom, -0.6f, 0.6f),
                        NextFloat(prodRandom, -0.6f, 0.6f),
                        NextFloat(prodRandom, -0.6f, 0.6f));
                    Vector3 worldPartPos = Vector3.Transform(
                        localAnchor + jitter, cell.WorldTransform);
                    float sphereRadius = NextFloat(prodRandom, 0.2f, 0.6f);
                    float boxHalfExtent = NextFloat(prodRandom, sphereRadius, sphereRadius * 2.5f);
                    var sphere = new Sphere { Origin = worldPartPos, Radius = sphereRadius };
                    var boxes = new ShadowPartBox[]
                    {
                        MakeBox(new Vector3(-boxHalfExtent), new Vector3(boxHalfExtent),
                                worldPartPos, Quaternion.Identity),
                    };
                    var preFix = new HashSet<uint>();
                    CellTransit.FindTransitCellsSphere(
                        cache, cell, cellId, worldPartPos, sphereRadius, preFix, out _);
                    var postFix = new HashSet<uint>();
                    CellTransit.FindTransitCellsBox(
                        cache, cell, cellId, boxes, new[] { sphere }, postFix, out _);
                    prodSwept++;
                    foreach (uint id in postFix)
                        if (!preFix.Contains(id))
                        {
                            prodAdded++;
                            if (cache.GetCellStruct(id) is not null) loadedBranchHits++;
                        }
                    foreach (uint id in preFix)
                        if (!postFix.Contains(id)) prodRemoved++;
                }
            }
        }
        Console.WriteLine(
            $"direction sweep (production-ratio population): swept={prodSwept} " +
            $"added={prodAdded} removed={prodRemoved} loadedBranchAdds={loadedBranchHits}");
        Assert.True(prodSwept > 0);
    }

    private static ShadowPartBox MakeBox(
        Vector3 localMin,
        Vector3 localMax,
        Vector3 worldPosition,
        Quaternion worldRotation)
    {
        var shape = ShadowShape.Bsp(
            gfxObjId: 0x010046D8u,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale: 1f,
            localGeometry: ShadowPartGeometry.Create(
                new FlatCollisionSphere(Vector3.Zero, 0.01f),
                new FlatGfxObjVisualBounds(
                    localMin,
                    localMax,
                    (localMin + localMax) * 0.5f,
                    ((localMax - localMin) * 0.5f).Length(),
                    (localMax - localMin) * 0.5f)));
        return ShadowPartBox.FromShape(shape, worldPosition, worldRotation);
    }

    private static float NextFloat(Random random, float minimum, float maximum)
        => minimum + (float)random.NextDouble() * (maximum - minimum);
}
