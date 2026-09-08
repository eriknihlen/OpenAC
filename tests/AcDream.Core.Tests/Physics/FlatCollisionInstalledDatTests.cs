using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class FlatCollisionInstalledDatTests
{
    private readonly ITestOutputHelper _output;

    public FlatCollisionInstalledDatTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public void InstalledCells_FlattenVerbatimAndDeterministically()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            _output.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        foreach (uint cellId in new[]
        {
            0x8A02_016Eu,
            0x8A02_017Au,
            0xA9B4_013Fu,
            0xA9B4_0150u,
            0xA9B4_0159u, // Holtburg inn south room
            0xA9B4_015Au,
            0xA9B4_0161u,
            0xA9B4_0162u, // Holtburg inn main room / stairwell
            0xA9B4_0164u, // Holtburg inn exterior vestibule
            0xA9B4_0166u, // Holtburg inn upper floor
        })
        {
            var cache = new PhysicsDataCache();
            ConformanceDats.LoadEnvCell(dats, cache, cellId);
            CellPhysics source = Assert.IsType<CellPhysics>(
                cache.GetCellStruct(cellId));

            FlatCellCollisionAsset first =
                FlatCollisionAssetBuilder.FlattenCell(source);
            FlatCellCollisionAsset second =
                FlatCollisionAssetBuilder.FlattenCell(source);
            EnvCell datCell = Assert.IsType<EnvCell>(
                dats.Get<EnvCell>(cellId));
            var environment =
                Assert.IsType<DatReaderWriter.DBObjs.Environment>(
                    dats.Get<DatReaderWriter.DBObjs.Environment>(
                        0x0D00_0000u | datCell.EnvironmentId));
            Assert.True(
                environment.Cells.TryGetValue(
                    datCell.CellStructure,
                    out CellStruct? cellStruct));
            Assert.NotNull(cellStruct);
            FlatCellStructureCollisionAsset directStructure =
                FlatCollisionAssetBuilder.FlattenCellStructure(cellStruct!);
            FlatEnvCellTopology directTopology =
                FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                    cellId,
                    datCell,
                    directStructure.PortalPolygons);

            AssertPhysicsSourceMatches(source.BSP?.Root, source.Resolved, first.Structure.PhysicsBsp);
            AssertContainmentSourceMatches(
                source.CellBSP?.Root,
                first.Structure.ContainmentBsp);
            FlatCollisionAssetBuilderTests.AssertFlatPhysicsEqual(
                first.Structure.PhysicsBsp,
                second.Structure.PhysicsBsp);
            Assert.Equal(
                first.Structure.ContainmentBsp.Nodes.AsEnumerable(),
                second.Structure.ContainmentBsp.Nodes.AsEnumerable());
            Assert.Equal(
                first.Topology.Portals.AsEnumerable(),
                second.Topology.Portals.AsEnumerable());
            Assert.Equal(
                first.Topology.VisibleCellIds.AsEnumerable(),
                second.Topology.VisibleCellIds.AsEnumerable());
            FlatCollisionAssetBuilderTests.AssertFlatPhysicsEqual(
                first.Structure.PhysicsBsp,
                directStructure.PhysicsBsp);
            Assert.Equal(
                first.Structure.ContainmentBsp.Nodes.AsEnumerable(),
                directStructure.ContainmentBsp.Nodes.AsEnumerable());
            AssertPolygonTableEqual(
                first.Structure.PortalPolygons,
                directStructure.PortalPolygons);
            Assert.Equal(
                first.Topology.Portals.AsEnumerable(),
                directTopology.Portals.AsEnumerable());
            Assert.Equal(
                first.Topology.VisibleCellIds.AsEnumerable(),
                directTopology.VisibleCellIds.AsEnumerable());
            Assert.Equal(first.Topology.SeenOutside, directTopology.SeenOutside);

            _output.WriteLine(
                $"cell 0x{cellId:X8}: physicsNodes={first.Structure.PhysicsBsp.Nodes.Length}, " +
                $"physicsPolygons={first.Structure.PhysicsBsp.PolygonTable.Polygons.Length}, " +
                $"containmentNodes={first.Structure.ContainmentBsp.Nodes.Length}, " +
                $"portals={first.Topology.Portals.Length}");
        }
    }

    [Fact]
    public void InstalledGfxObjsAndSetups_FlattenVerbatim()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            _output.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();
        foreach (uint gfxObjId in new[]
        {
            0x0100_0A2Bu,
            0x0100_0C17u, // Holtburg inn shell
            0x0100_0AC5u, // outdoor stair/ramp fixture
            0x0100_44B5u, // multipart door shell
        })
        {
            GfxObj gfxObj = Assert.IsType<GfxObj>(dats.Get<GfxObj>(gfxObjId));
            cache.CacheGfxObj(gfxObjId, gfxObj);
            GfxObjPhysics source = Assert.IsType<GfxObjPhysics>(
                cache.GetGfxObj(gfxObjId));

            FlatGfxObjCollisionAsset flat =
                FlatCollisionAssetBuilder.FlattenGfxObj(
                    source,
                    cache.GetVisualBounds(gfxObjId));
            FlatGfxObjCollisionAsset direct =
                FlatCollisionAssetBuilder.FlattenGfxObj(gfxObj);

            AssertPhysicsSourceMatches(
                source.BSP?.Root,
                source.Resolved,
                flat.PhysicsBsp);
            AssertNullableSphereBits(source.BoundingSphere, flat.BoundingSphere);
            Assert.NotNull(flat.VisualBounds);
            AssertVisualBoundsBits(
                Assert.IsType<GfxObjVisualBounds>(cache.GetVisualBounds(gfxObjId)),
                flat.VisualBounds!.Value);
            FlatCollisionAssetBuilderTests.AssertFlatPhysicsEqual(
                flat.PhysicsBsp,
                direct.PhysicsBsp);
            AssertFlatNullableSphereBits(
                flat.BoundingSphere,
                direct.BoundingSphere);
            AssertFlatNullableVisualBoundsBits(
                flat.VisualBounds,
                direct.VisualBounds);

            _output.WriteLine(
                $"gfx 0x{gfxObjId:X8}: nodes={flat.PhysicsBsp.Nodes.Length}, " +
                $"polygons={flat.PhysicsBsp.PolygonTable.Polygons.Length}");
        }

        foreach (uint setupId in new[]
        {
            0x0200_0001u, // player
            0x0200_19E3u,
            0x0200_19FFu, // multipart door
        })
        {
            Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(setupId));
            cache.CacheSetup(setupId, setup);
            SetupPhysics source = Assert.IsType<SetupPhysics>(
                cache.GetSetup(setupId));

            FlatSetupCollision flat =
                FlatCollisionAssetBuilder.FlattenSetup(source);
            FlatSetupCollision direct =
                FlatCollisionAssetBuilder.FlattenSetup(setup);
            AssertSetupSourceMatches(source, flat);
            AssertSetupFlatEqual(flat, direct);

            _output.WriteLine(
                $"setup 0x{setupId:X8}: cylinders={flat.Cylinders.Length}, " +
                $"spheres={flat.Spheres.Length}");
        }
    }

    private static void AssertPhysicsSourceMatches(
        PhysicsBSPNode? root,
        IReadOnlyDictionary<ushort, ResolvedPolygon> resolved,
        FlatPhysicsBsp flat)
    {
        if (root is null)
        {
            Assert.Equal(-1, flat.RootIndex);
            Assert.Empty(flat.Nodes);
            return;
        }

        var ordered = new List<PhysicsBSPNode>();
        var indices = new Dictionary<PhysicsBSPNode, int>(
            ReferenceEqualityComparer.Instance);
        var stack = new Stack<PhysicsBSPNode>();
        stack.Push(root);
        while (stack.Count != 0)
        {
            PhysicsBSPNode source = stack.Pop();
            indices.Add(source, ordered.Count);
            ordered.Add(source);
            if (source.NegNode is not null)
                stack.Push(source.NegNode);
            if (source.PosNode is not null)
                stack.Push(source.PosNode);
        }

        Assert.Equal(flat.Nodes.Length, ordered.Count);
        for (int flatIndex = 0; flatIndex < ordered.Count; flatIndex++)
        {
            PhysicsBSPNode source = ordered[flatIndex];
            FlatPhysicsBspNode row = flat.Nodes[flatIndex];
            Assert.Equal(source.Type, row.Type);
            FlatCollisionAssetBuilderTests.AssertPlaneBits(
                source.SplittingPlane,
                row.SplittingPlane);
            Assert.Equal(source.LeafIndex, row.LeafIndex);
            Assert.Equal(source.Solid, row.Solid);
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                source.BoundingSphere.Origin,
                row.BoundingSphere.Origin);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                source.BoundingSphere.Radius,
                row.BoundingSphere.Radius);
            Assert.Equal(
                source.PosNode is null ? -1 : indices[source.PosNode],
                row.PositiveChildIndex);
            Assert.Equal(
                source.NegNode is null ? -1 : indices[source.NegNode],
                row.NegativeChildIndex);
            Assert.Equal(source.Polygons.Count, row.PolygonIndexRange.Count);

            for (int i = 0; i < source.Polygons.Count; i++)
            {
                int streamIndex = row.PolygonIndexRange.Start + i;
                int polygonIndex = flat.PolygonIndexStream[streamIndex];
                Assert.Equal(
                    source.Polygons[i],
                    flat.PolygonTable.Polygons[polygonIndex].Id);
            }
        }

        Assert.Equal(resolved.Count, flat.PolygonTable.Polygons.Length);
        foreach ((ushort id, ResolvedPolygon source) in resolved)
        {
            Assert.True(flat.PolygonTable.TryFindPolygonIndex(id, out int polygonIndex));
            FlatCollisionPolygon row = flat.PolygonTable.Polygons[polygonIndex];
            Assert.Equal(source.Id, row.Id);
            Assert.Equal(source.NumPoints, row.NumPoints);
            Assert.Equal(source.SidesType, row.SidesType);
            FlatCollisionAssetBuilderTests.AssertPlaneBits(source.Plane, row.Plane);
            for (int i = 0; i < source.Vertices.Length; i++)
            {
                FlatCollisionAssetBuilderTests.AssertVectorBits(
                    source.Vertices[i],
                    flat.PolygonTable.Vertices[row.VertexRange.Start + i]);
            }
        }
    }

    private static void AssertContainmentSourceMatches(
        CellBSPNode? root,
        FlatCellContainmentBsp flat)
    {
        if (root is null)
        {
            Assert.Equal(-1, flat.RootIndex);
            Assert.Empty(flat.Nodes);
            return;
        }

        var ordered = new List<CellBSPNode>();
        var indices = new Dictionary<CellBSPNode, int>(
            ReferenceEqualityComparer.Instance);
        var stack = new Stack<CellBSPNode>();
        stack.Push(root);
        while (stack.Count != 0)
        {
            CellBSPNode source = stack.Pop();
            indices.Add(source, ordered.Count);
            ordered.Add(source);
            if (source.NegNode is not null)
                stack.Push(source.NegNode);
            if (source.PosNode is not null)
                stack.Push(source.PosNode);
        }

        Assert.Equal(flat.Nodes.Length, ordered.Count);
        for (int flatIndex = 0; flatIndex < ordered.Count; flatIndex++)
        {
            CellBSPNode source = ordered[flatIndex];
            FlatCellBspNode row = flat.Nodes[flatIndex];
            Assert.Equal(source.Type, row.Type);
            Assert.Equal(source.LeafIndex, row.LeafIndex);
            FlatCollisionAssetBuilderTests.AssertPlaneBits(
                source.SplittingPlane,
                row.SplittingPlane);
            Assert.Equal(
                source.PosNode is null ? -1 : indices[source.PosNode],
                row.PositiveChildIndex);
            Assert.Equal(
                source.NegNode is null ? -1 : indices[source.NegNode],
                row.NegativeChildIndex);
        }
    }

    private static void AssertSetupSourceMatches(
        SetupPhysics source,
        FlatSetupCollision flat)
    {
        Assert.Equal(source.CylSpheres.Count, flat.Cylinders.Length);
        Assert.Equal(source.Spheres.Count, flat.Spheres.Length);
        for (int i = 0; i < source.CylSpheres.Count; i++)
        {
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                source.CylSpheres[i].Origin,
                flat.Cylinders[i].Origin);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                source.CylSpheres[i].Radius,
                flat.Cylinders[i].Radius);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                source.CylSpheres[i].Height,
                flat.Cylinders[i].Height);
        }
        for (int i = 0; i < source.Spheres.Count; i++)
        {
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                source.Spheres[i].Origin,
                flat.Spheres[i].Origin);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                source.Spheres[i].Radius,
                flat.Spheres[i].Radius);
        }
        FlatCollisionAssetBuilderTests.AssertFloatBits(source.Height, flat.Height);
        FlatCollisionAssetBuilderTests.AssertFloatBits(source.Radius, flat.Radius);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            source.StepUpHeight,
            flat.StepUpHeight);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            source.StepDownHeight,
            flat.StepDownHeight);
    }

    private static void AssertNullableSphereBits(
        Sphere? source,
        FlatCollisionSphere? flat)
    {
        Assert.Equal(source is null, flat is null);
        if (source is null || flat is null)
            return;

        FlatCollisionAssetBuilderTests.AssertVectorBits(
            source.Origin,
            flat.Value.Origin);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            source.Radius,
            flat.Value.Radius);
    }

    private static void AssertVisualBoundsBits(
        GfxObjVisualBounds source,
        FlatGfxObjVisualBounds flat)
    {
        FlatCollisionAssetBuilderTests.AssertVectorBits(source.Min, flat.Min);
        FlatCollisionAssetBuilderTests.AssertVectorBits(source.Max, flat.Max);
        FlatCollisionAssetBuilderTests.AssertVectorBits(source.Center, flat.Center);
        FlatCollisionAssetBuilderTests.AssertFloatBits(source.Radius, flat.Radius);
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            source.HalfExtents,
            flat.HalfExtents);
    }

    private static void AssertFlatNullableSphereBits(
        FlatCollisionSphere? expected,
        FlatCollisionSphere? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (!expected.HasValue || !actual.HasValue)
            return;
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            expected.Value.Origin,
            actual.Value.Origin);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.Value.Radius,
            actual.Value.Radius);
    }

    private static void AssertFlatNullableVisualBoundsBits(
        FlatGfxObjVisualBounds? expected,
        FlatGfxObjVisualBounds? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (!expected.HasValue || !actual.HasValue)
            return;
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            expected.Value.Min,
            actual.Value.Min);
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            expected.Value.Max,
            actual.Value.Max);
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            expected.Value.Center,
            actual.Value.Center);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.Value.Radius,
            actual.Value.Radius);
        FlatCollisionAssetBuilderTests.AssertVectorBits(
            expected.Value.HalfExtents,
            actual.Value.HalfExtents);
    }

    private static void AssertSetupFlatEqual(
        FlatSetupCollision expected,
        FlatSetupCollision actual)
    {
        Assert.Equal(expected.Cylinders.Length, actual.Cylinders.Length);
        Assert.Equal(expected.Spheres.Length, actual.Spheres.Length);
        for (int i = 0; i < expected.Cylinders.Length; i++)
        {
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                expected.Cylinders[i].Origin,
                actual.Cylinders[i].Origin);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                expected.Cylinders[i].Radius,
                actual.Cylinders[i].Radius);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                expected.Cylinders[i].Height,
                actual.Cylinders[i].Height);
        }
        for (int i = 0; i < expected.Spheres.Length; i++)
        {
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                expected.Spheres[i].Origin,
                actual.Spheres[i].Origin);
            FlatCollisionAssetBuilderTests.AssertFloatBits(
                expected.Spheres[i].Radius,
                actual.Spheres[i].Radius);
        }
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.Height,
            actual.Height);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.Radius,
            actual.Radius);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.StepUpHeight,
            actual.StepUpHeight);
        FlatCollisionAssetBuilderTests.AssertFloatBits(
            expected.StepDownHeight,
            actual.StepDownHeight);
    }

    private static void AssertPolygonTableEqual(
        FlatPolygonTable expected,
        FlatPolygonTable actual)
    {
        Assert.Equal(expected.Polygons.Length, actual.Polygons.Length);
        Assert.Equal(expected.Vertices.Length, actual.Vertices.Length);
        for (int i = 0; i < expected.Polygons.Length; i++)
        {
            Assert.Equal(expected.Polygons[i].Id, actual.Polygons[i].Id);
            Assert.Equal(
                expected.Polygons[i].SidesType,
                actual.Polygons[i].SidesType);
            Assert.Equal(
                expected.Polygons[i].NumPoints,
                actual.Polygons[i].NumPoints);
            Assert.Equal(
                expected.Polygons[i].VertexRange,
                actual.Polygons[i].VertexRange);
            FlatCollisionAssetBuilderTests.AssertPlaneBits(
                expected.Polygons[i].Plane,
                actual.Polygons[i].Plane);
        }
        for (int i = 0; i < expected.Vertices.Length; i++)
        {
            FlatCollisionAssetBuilderTests.AssertVectorBits(
                expected.Vertices[i],
                actual.Vertices[i]);
        }
    }
}
