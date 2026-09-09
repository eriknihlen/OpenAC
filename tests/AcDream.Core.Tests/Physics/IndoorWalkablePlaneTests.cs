using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class IndoorWalkablePlaneTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static PhysicsBSPTree BuildLeafBsp(IEnumerable<ushort> polyIds,
        Vector3 center, float radius)
    {
        var node = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = center, Radius = radius },
        };
        foreach (var id in polyIds)
            node.Polygons.Add(id);
        return new PhysicsBSPTree { Root = node };
    }

    private static CellPhysics BuildCellWithFloor(float floorZ = 0f)
    {
        var verts = new[]
        {
            new Vector3(-5f, -5f, floorZ),
            new Vector3( 5f, -5f, floorZ),
            new Vector3( 5f,  5f, floorZ),
            new Vector3(-5f,  5f, floorZ),
        };
        var normal = new Vector3(0f, 0f, 1f);   // straight up
        float D = -Vector3.Dot(normal, verts[0]);  // = -floorZ

        var floorPoly = new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = new Plane(normal, D),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var resolved = new Dictionary<ushort, ResolvedPolygon> { [0] = floorPoly };
        var bsp = BuildLeafBsp(new ushort[] { 0 }, new Vector3(0f, 0f, floorZ), 10f);

        return new CellPhysics
        {
            BSP                   = bsp,
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = resolved,
        };
    }

    // -----------------------------------------------------------------------
    // TryFindIndoorWalkablePlane
    // -----------------------------------------------------------------------

    [Fact]
    public void TryFindIndoorWalkablePlane_PlayerDirectlyOverFloor_ReturnsTrue()
    {
        var cell      = BuildCellWithFloor(floorZ: 0f);
        var transition = new Transition();
        var localFoot = new Vector3(0f, 0f, 0.4f);

        bool found = transition.TryFindIndoorWalkablePlane(
            cell, localFoot, sphereRadius: 0.48f,
            out _, out _, out _);

        Assert.True(found);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_PlayerDirectlyOverFloor_PlaneNormalIsUp()
    {
        var cell      = BuildCellWithFloor(floorZ: 0f);
        var transition = new Transition();
        var localFoot = new Vector3(0f, 0f, 0.4f);

        transition.TryFindIndoorWalkablePlane(
            cell, localFoot, sphereRadius: 0.48f,
            out var plane, out _, out _);

        Assert.True(plane.Normal.Z > 0.99f,
            $"Expected plane.Normal.Z > 0.99, got {plane.Normal.Z}");
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_PlayerDirectlyOverFloor_PlaneAtFloorZ()
    {
        const float floorZ = 2.5f;
        var cell      = BuildCellWithFloor(floorZ);
        var transition = new Transition();
        var localFoot = new Vector3(0f, 0f, floorZ + 0.4f);

        transition.TryFindIndoorWalkablePlane(
            cell, localFoot, sphereRadius: 0.48f,
            out var plane, out _, out _);

        // With identity transform and an upward normal, plane.D = -floorZ.
        // The plane equation: normal·p + D = 0  → p.Z = floorZ when normal=(0,0,1).
        Assert.True(MathF.Abs(plane.D - (-floorZ)) < 1e-4f,
            $"Expected plane.D ≈ {-floorZ}, got {plane.D}");
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_PlayerOutsidePolygonXY_ReturnsFalse()
    {
        var cell      = BuildCellWithFloor();
        var transition = new Transition();
        // XY = (20, 20) is far outside the 10×10 square (-5..5 in both axes).
        var localFoot = new Vector3(20f, 20f, 0.4f);

        bool found = transition.TryFindIndoorWalkablePlane(
            cell, localFoot, sphereRadius: 0.48f,
            out _, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_NoBsp_ReturnsFalse()
    {
        var cell = new CellPhysics
        {
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
        };
        var transition = new Transition();

        bool found = transition.TryFindIndoorWalkablePlane(
            cell, new Vector3(0f, 0f, 0.4f), sphereRadius: 0.48f,
            out _, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_WallPolyInBsp_ReturnsFalse()
    {
        Vector3[] wallVerts =
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 0f, 1f),
            new Vector3(0f, 0f, 1f),
        };
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices  = wallVerts,
                Plane     = new Plane(new Vector3(0f, 1f, 0f), 0f),  // wall facing +Y
                NumPoints = 4,
                SidesType = CullMode.None,
            },
        };

        var center = new Vector3(0.5f, 0f, 0.5f);
        var bsp = BuildLeafBsp(new ushort[] { 0 }, center, 2f);

        var cell = new CellPhysics
        {
            BSP                   = bsp,
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = resolved,
        };

        var transition = new Transition();
        transition.SpherePath.WalkInterp = 1.0f;

        // Foot sphere positioned to overlap the wall's plane (|Y - 0| = 0 < radius 0.48).
        bool found = transition.TryFindIndoorWalkablePlane(
            cell,
            localFootCenter: new Vector3(0.5f, 0f, 0.5f),
            sphereRadius: 0.48f,
            out _,
            out _,
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_EmptyResolved_ReturnsFalse()
    {
        // BSP leaf exists but references no polygons → FindWalkableSphere returns false.
        var bsp = BuildLeafBsp(System.Array.Empty<ushort>(), Vector3.Zero, 10f);
        var cell = new CellPhysics
        {
            BSP                   = bsp,
            WorldTransform        = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved              = new Dictionary<ushort, ResolvedPolygon>(),
        };
        var transition = new Transition();

        bool found = transition.TryFindIndoorWalkablePlane(
            cell, new Vector3(0f, 0f, 0.4f), sphereRadius: 0.48f,
            out _, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_WithWorldTranslation_PlaneInWorldSpace()
    {
        var translation = Matrix4x4.CreateTranslation(100f, 200f, 94f);
        Matrix4x4.Invert(translation, out var inv);

        var localVerts = new[]
        {
            new Vector3(-5f, -5f, 0f),
            new Vector3( 5f, -5f, 0f),
            new Vector3( 5f,  5f, 0f),
            new Vector3(-5f,  5f, 0f),
        };
        var floorPoly = new ResolvedPolygon
        {
            Vertices  = localVerts,
            Plane     = new Plane(new Vector3(0f, 0f, 1f), 0f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };
        var resolved = new Dictionary<ushort, ResolvedPolygon> { [0] = floorPoly };
        var bsp = BuildLeafBsp(new ushort[] { 0 }, Vector3.Zero, 10f);

        var cell = new CellPhysics
        {
            BSP                   = bsp,
            WorldTransform        = translation,
            InverseWorldTransform = inv,
            Resolved              = resolved,
        };

        var localFoot = new Vector3(0f, 0f, 0.4f);
        var transition = new Transition();

        bool found = transition.TryFindIndoorWalkablePlane(
            cell, localFoot, sphereRadius: 0.48f,
            out var plane, out var worldVerts, out _);

        Assert.True(found);
        // World normal should still be (0,0,1).
        Assert.True(plane.Normal.Z > 0.99f);
        Assert.True(MathF.Abs(worldVerts[0].X - 95f) < 1e-3f);
        Assert.True(MathF.Abs(worldVerts[0].Y - 195f) < 1e-3f);
        Assert.True(MathF.Abs(worldVerts[0].Z - 94f) < 1e-3f,
            $"Expected worldVerts[0].Z ≈ 94, got {worldVerts[0].Z}");
    }
}
