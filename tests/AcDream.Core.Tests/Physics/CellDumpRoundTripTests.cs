using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellDumpRoundTripTests
{
    [Fact]
    public void Capture_Then_Hydrate_PreservesTransformsAndPolygons()
    {
        var original = MakeFixtureCell();

        var dump = CellDumpSerializer.Capture(0xA9B40147u, original);
        var hydrated = CellDumpSerializer.Hydrate(dump);

        Assert.Equal(original.WorldTransform, hydrated.WorldTransform);
        Assert.Equal(original.InverseWorldTransform, hydrated.InverseWorldTransform);
        Assert.Equal(original.Resolved.Count, hydrated.Resolved.Count);

        foreach (var (id, originalPoly) in original.Resolved)
        {
            Assert.True(hydrated.Resolved.TryGetValue(id, out var rehydrated));
            Assert.Equal(originalPoly.NumPoints, rehydrated!.NumPoints);
            Assert.Equal(originalPoly.SidesType, rehydrated.SidesType);
            Assert.Equal(originalPoly.Plane.Normal, rehydrated.Plane.Normal);
            Assert.Equal(originalPoly.Plane.D, rehydrated.Plane.D);
            Assert.Equal(originalPoly.Vertices.Length, rehydrated.Vertices.Length);
            for (int i = 0; i < originalPoly.Vertices.Length; i++)
                Assert.Equal(originalPoly.Vertices[i], rehydrated.Vertices[i]);
        }
    }

    [Fact]
    public void Capture_Then_Hydrate_PreservesPortalsAndVisibleCells()
    {
        var original = MakeFixtureCell();

        var dump = CellDumpSerializer.Capture(0xA9B40147u, original);
        var hydrated = CellDumpSerializer.Hydrate(dump);

        Assert.Equal(original.Portals.Count, hydrated.Portals.Count);
        for (int i = 0; i < original.Portals.Count; i++)
        {
            Assert.Equal(original.Portals[i].OtherCellId, hydrated.Portals[i].OtherCellId);
            Assert.Equal(original.Portals[i].PolygonId, hydrated.Portals[i].PolygonId);
            Assert.Equal(original.Portals[i].Flags, hydrated.Portals[i].Flags);
        }

        Assert.Equal(original.VisibleCellIds.Count, hydrated.VisibleCellIds.Count);
        foreach (var id in original.VisibleCellIds)
            Assert.Contains(id, hydrated.VisibleCellIds);
    }

    [Fact]
    public void WriteRead_OnDisk_PreservesContent()
    {
        var original = MakeFixtureCell();
        var dump = CellDumpSerializer.Capture(0xA9B40147u, original);

        var path = Path.Combine(Path.GetTempPath(),
                                $"acdream-celldump-test-{System.Guid.NewGuid():N}.json");
        try
        {
            CellDumpSerializer.Write(dump, path);
            Assert.True(File.Exists(path));

            var readBack = CellDumpSerializer.Read(path);
            Assert.Equal(dump.CellId, readBack.CellId);
            Assert.Equal(dump.ResolvedPolygons.Count, readBack.ResolvedPolygons.Count);
            Assert.Equal(dump.Portals.Count, readBack.Portals.Count);

            var hydrated = CellDumpSerializer.Hydrate(readBack);
            Assert.Equal(original.WorldTransform, hydrated.WorldTransform);
            Assert.Equal(original.Resolved.Count, hydrated.Resolved.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Hydrate_LeavesBspNullsSoLeafLevelPredicatesAreTheReplayPath()
    {
        var original = MakeFixtureCell();
        var dump = CellDumpSerializer.Capture(0xA9B40147u, original);
        var hydrated = CellDumpSerializer.Hydrate(dump);

        // Replay harness drives leaf-level predicates directly on
        // Resolved; the BSP trees aren't reconstructed in this iteration.
        Assert.Null(hydrated.BSP);
        Assert.Null(hydrated.CellBSP);
        Assert.NotEmpty(hydrated.Resolved);
    }

    private static CellPhysics MakeFixtureCell()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0x0004] = new ResolvedPolygon
            {
                Id        = 0x0004,
                NumPoints = 3,
                SidesType = CullMode.Clockwise,
                Plane     = new Plane(new Vector3(0, 0, 1), 0f),
                Vertices  = new[]
                {
                    new Vector3(-6.2f, 7.6f, 0f),
                    new Vector3(-10.0f, 7.6f, 0f),
                    new Vector3(-10.0f, 2.8f, 0f),
                },
            },
            [0x0008] = new ResolvedPolygon
            {
                Id        = 0x0008,
                NumPoints = 4,
                SidesType = CullMode.Clockwise,
                Plane     = new Plane(new Vector3(0f, -0.7190f, 0.6950f), -0.1007f),
                Vertices  = new[]
                {
                    new Vector3( 0.8f, -1.59f, -1.5f),
                    new Vector3( 0.8f,  1.31f,  1.5f),
                    new Vector3(-0.8f,  1.31f,  1.5f),
                    new Vector3(-0.8f, -1.59f, -1.5f),
                },
            },
        };

        var portalPolys = new Dictionary<ushort, ResolvedPolygon>
        {
            [0x0100] = new ResolvedPolygon
            {
                Id        = 0x0100,
                NumPoints = 4,
                SidesType = CullMode.Clockwise,
                Plane     = new Plane(new Vector3(1, 0, 0), 5f),
                Vertices  = new[]
                {
                    new Vector3(5f, -1f, -1f),
                    new Vector3(5f,  1f, -1f),
                    new Vector3(5f,  1f,  1f),
                    new Vector3(5f, -1f,  1f),
                },
            },
        };

        var portals = new List<PortalInfo>
        {
            new(otherCellId: 0x0146, polygonId: 0x0100, flags: 0x0002),
        };

        var visible = new HashSet<uint> { 0xA9B40143u, 0xA9B40146u };

        var worldTransform =
            Matrix4x4.CreateRotationZ(MathF.PI) *
            Matrix4x4.CreateTranslation(130.5f, 11.5f, 94.0f);
        Matrix4x4.Invert(worldTransform, out var inverse);

        return new CellPhysics
        {
            BSP                   = null,
            PhysicsPolygons       = null,
            Vertices              = null,
            WorldTransform        = worldTransform,
            InverseWorldTransform = inverse,
            Resolved              = resolved,
            CellBSP               = null,
            Portals               = portals,
            PortalPolygons        = portalPolys,
            VisibleCellIds        = visible,
        };
    }
}
