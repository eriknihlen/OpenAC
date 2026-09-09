using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class GfxObjDumpRoundTripTests
{
    [Fact]
    public void Capture_Then_Hydrate_PreservesPolygons()
    {
        var original = MakeFixtureGfx();

        var dump = GfxObjDumpSerializer.Capture(0x01000A2Bu, original);
        var hydrated = GfxObjDumpSerializer.Hydrate(dump);

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
            Assert.Equal(originalPoly.Id, rehydrated.Id);
        }
    }

    [Fact]
    public void Hydrate_ConstructsSyntheticSingleLeafBspWithAllPolygons()
    {
        var original = MakeFixtureGfx();
        var dump = GfxObjDumpSerializer.Capture(0x01000A2Bu, original);
        var hydrated = GfxObjDumpSerializer.Hydrate(dump);

        // Synthetic BSP is single-leaf, references every resolved poly id.
        Assert.NotNull(hydrated.BSP);
        Assert.NotNull(hydrated.BSP!.Root);
        Assert.Equal(BSPNodeType.Leaf, hydrated.BSP.Root.Type);
        Assert.Equal(original.Resolved.Count, hydrated.BSP.Root.Polygons.Count);
        foreach (var id in original.Resolved.Keys)
            Assert.Contains(id, hydrated.BSP.Root.Polygons);
    }

    [Fact]
    public void WriteRead_OnDisk_PreservesContent()
    {
        var original = MakeFixtureGfx();
        var dump = GfxObjDumpSerializer.Capture(0x01000A2Bu, original);

        var path = Path.Combine(Path.GetTempPath(),
                                $"acdream-gfxobjdump-test-{System.Guid.NewGuid():N}.json");
        try
        {
            GfxObjDumpSerializer.Write(dump, path);
            Assert.True(File.Exists(path));

            var readBack = GfxObjDumpSerializer.Read(path);
            Assert.Equal(dump.GfxObjId, readBack.GfxObjId);
            Assert.Equal(dump.ResolvedPolygons.Count, readBack.ResolvedPolygons.Count);

            var hydrated = GfxObjDumpSerializer.Hydrate(readBack);
            Assert.Equal(original.Resolved.Count, hydrated.Resolved.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Hydrate_RecomputesCoveringSphereWhenDumpHasZeroRadius()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0x0001] = new ResolvedPolygon
            {
                Id        = 0x0001,
                NumPoints = 3,
                SidesType = CullMode.Clockwise,
                Plane     = new Plane(new Vector3(0, 0, 1), 0f),
                Vertices  = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(4f, 0f, 0f),
                    new Vector3(0f, 3f, 0f),
                },
            },
        };
        var leaf = new PhysicsBSPNode { Type = BSPNodeType.Leaf };
        leaf.Polygons.Add(0x0001);
        var src = new GfxObjPhysics
        {
            BSP             = new PhysicsBSPTree { Root = leaf },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = resolved,
            BoundingSphere  = null,
        };

        var dump = GfxObjDumpSerializer.Capture(0x42u, src);
        // BoundingSphere=null in source → radius 0 in dump.
        Assert.Equal(0f, dump.BoundingSphereRadius);

        var hydrated = GfxObjDumpSerializer.Hydrate(dump);
        Assert.NotNull(hydrated.BoundingSphere);
        Assert.True(hydrated.BoundingSphere!.Radius > 0f);
        foreach (var v in resolved[0x0001].Vertices)
        {
            float dist = Vector3.Distance(hydrated.BoundingSphere.Origin, v);
            Assert.True(dist <= hydrated.BoundingSphere.Radius + 1e-4f,
                $"Vertex {v} at distance {dist} exceeds covering radius {hydrated.BoundingSphere.Radius}");
        }
    }

    private static GfxObjPhysics MakeFixtureGfx()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0x0004] = new ResolvedPolygon
            {
                Id        = 0x0004,
                NumPoints = 3,
                SidesType = CullMode.Clockwise,
                Plane     = new Plane(new Vector3(0, 0, -1), 1.5f),
                Vertices  = new[]
                {
                    new Vector3(-6.2f,  7.6f, 1.5f),
                    new Vector3(-10f,   7.6f, 1.5f),
                    new Vector3(-10f,   2.8f, 1.5f),
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

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(-5f, 4f, 0f),
                Radius = 14f,
            },
        };
        leaf.Polygons.Add(0x0004);
        leaf.Polygons.Add(0x0008);

        return new GfxObjPhysics
        {
            BSP             = new PhysicsBSPTree { Root = leaf },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices        = new VertexArray(),
            Resolved        = resolved,
            BoundingSphere  = leaf.BoundingSphere,
        };
    }
}
