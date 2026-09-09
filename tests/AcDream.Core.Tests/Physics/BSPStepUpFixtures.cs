using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public static class BSPStepUpFixtures
{
    public const ushort LowStep_FloorId     = 10;
    public const ushort LowStep_WallId      = 11;
    public const ushort LowStep_UpperFloorId = 12;

    public const ushort TallWall_FloorId    = 20;
    public const ushort TallWall_WallId     = 21;

    public const ushort FlatRoof_FloorId    = 30;
    public const ushort FlatRoof_RoofId     = 31;

    public const ushort SlopedUnwalkable_FloorId = 40;
    public const ushort SlopedUnwalkable_SlopeId = 41;

    // -------------------------------------------------------------------------
    // Sphere radius used in every test.
    // -------------------------------------------------------------------------
    public const float SphereRadius = 0.2f;


    public static (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved)
        LowStep()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>();

        // Lower floor: z=0, x∈[-2,0.5], y∈[-1,1], normal = +Z
        resolved[LowStep_FloorId] = MakeFloor(
            new Vector3(-2f, -1f, 0f), new Vector3(0.5f, -1f, 0f),
            new Vector3(0.5f,  1f, 0f), new Vector3(-2f,  1f, 0f));

        resolved[LowStep_WallId] = MakeQuad(
            new Vector3(0.5f, -1f, 0f),
            new Vector3(0.5f, -1f, 0.25f),
            new Vector3(0.5f,  1f, 0.25f),
            new Vector3(0.5f,  1f, 0f),
            expectedNormal: new Vector3(-1f, 0f, 0f));

        resolved[LowStep_UpperFloorId] = MakeFloor(
            new Vector3(0.2f, -1f, 0.25f), new Vector3(2f, -1f, 0.25f),
            new Vector3(2f,   1f, 0.25f), new Vector3(0.2f, 1f, 0.25f));

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 10f },
        };
        leaf.Polygons.Add(LowStep_FloorId);
        leaf.Polygons.Add(LowStep_WallId);
        leaf.Polygons.Add(LowStep_UpperFloorId);

        return (leaf, resolved);
    }

    // =========================================================================
    // Fixture 2 — Too-tall wall (5 m)
    //
    //  A floor at z=0 and a 5 m wall at x=0.5 with no floor on the other side.
    //  Expected: step-up fails (wall too tall), mover slides along wall.
    // =========================================================================

    public static (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved)
        TallWall()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>();

        // Floor at z=0
        resolved[TallWall_FloorId] = MakeFloor(
            new Vector3(-2f, -1f, 0f), new Vector3(0.5f, -1f, 0f),
            new Vector3(0.5f,  1f, 0f), new Vector3(-2f,  1f, 0f));

        // Tall wall at x=0.5, z∈[0,5], normal = -X
        // Winding for normal=(-1,0,0): (y=-1,z=0)→(y=-1,z=5)→(y=1,z=5)→(y=1,z=0).
        resolved[TallWall_WallId] = MakeQuad(
            new Vector3(0.5f, -1f, 0f),
            new Vector3(0.5f, -1f, 5f),
            new Vector3(0.5f,  1f, 5f),
            new Vector3(0.5f,  1f, 0f),
            expectedNormal: new Vector3(-1f, 0f, 0f));

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = new Vector3(0f, 0f, 2.5f), Radius = 10f },
        };
        leaf.Polygons.Add(TallWall_FloorId);
        leaf.Polygons.Add(TallWall_WallId);

        return (leaf, resolved);
    }


    public static (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved)
        FlatRoof()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>();

        // Ground floor for reference (not involved in landing test)
        resolved[FlatRoof_FloorId] = MakeFloor(
            new Vector3(-2f, -1f, 0f), new Vector3(2f, -1f, 0f),
            new Vector3(2f,   1f, 0f), new Vector3(-2f, 1f, 0f));

        // Roof at z=3.0, x∈[-2,2], y∈[-1,1], normal = +Z
        resolved[FlatRoof_RoofId] = MakeFloor(
            new Vector3(-2f, -1f, 3f), new Vector3(2f, -1f, 3f),
            new Vector3(2f,   1f, 3f), new Vector3(-2f, 1f, 3f));

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = new Vector3(0f, 0f, 1.5f), Radius = 10f },
        };
        leaf.Polygons.Add(FlatRoof_FloorId);
        leaf.Polygons.Add(FlatRoof_RoofId);

        return (leaf, resolved);
    }


    public static (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved)
        SlopedUnwalkable()
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>();

        // Reference floor at z=0
        resolved[SlopedUnwalkable_FloorId] = MakeFloor(
            new Vector3(-2f, -1f, 0f), new Vector3(0f, -1f, 0f),
            new Vector3(0f,   1f, 0f), new Vector3(-2f, 1f, 0f));

        var v0 = new Vector3(0f, -1f, 0f);
        var v1 = new Vector3(1f, -1f, 2f);
        var v2 = new Vector3(1f,  1f, 2f);
        var v3 = new Vector3(0f,  1f, 0f);
        var raw = Vector3.Cross(v1 - v0, v3 - v0);
        var slopeNormal = Vector3.Normalize(raw);
        // Ensure the normal faces away from the approach side (-X direction).
        if (slopeNormal.X > 0) slopeNormal = -slopeNormal;

        var vertices = new[] { v0, v1, v2, v3 };
        float dotSum = 0f;
        foreach (var v in vertices) dotSum += Vector3.Dot(slopeNormal, v);
        float d = -(dotSum / vertices.Length);

        resolved[SlopedUnwalkable_SlopeId] = new ResolvedPolygon
        {
            Vertices  = vertices,
            Plane     = new Plane(slopeNormal, d),
            NumPoints = 4,
            SidesType = CullMode.None,
        };

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = new Vector3(0.5f, 0f, 1f), Radius = 10f },
        };
        leaf.Polygons.Add(SlopedUnwalkable_FloorId);
        leaf.Polygons.Add(SlopedUnwalkable_SlopeId);

        return (leaf, resolved);
    }

    // =========================================================================
    // Transition builder helpers
    // =========================================================================

    public static Transition MakeGroundedTransition(
        Vector3 from,
        Vector3 to,
        float   stepUpHeight = 0.30f,
        uint    cellId       = 0xA9B40001u)
    {
        var t = new Transition();
        t.SpherePath.InitPath(from, to, cellId, SphereRadius);
        t.ObjectInfo.State          = ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
        t.ObjectInfo.StepUpHeight   = stepUpHeight;
        t.ObjectInfo.StepDownHeight = 0.04f;
        t.ObjectInfo.StepDown       = true;
        // Seed LastKnownContactPlane so the mover is "on the floor".
        t.CollisionInfo.LastKnownContactPlane      = new Plane(Vector3.UnitZ, 0f);
        t.CollisionInfo.LastKnownContactPlaneValid = true;
        return t;
    }

    public static Transition MakeAirborneTransition(
        Vector3 from,
        Vector3 to,
        uint    cellId = 0xA9B40001u)
    {
        var t = new Transition();
        t.SpherePath.InitPath(from, to, cellId, SphereRadius);
        t.ObjectInfo.State          = ObjectInfoState.None;
        t.ObjectInfo.StepUpHeight   = 0.04f;
        t.ObjectInfo.StepDownHeight = 0.04f;
        t.ObjectInfo.StepDown       = false;
        return t;
    }

    // =========================================================================
    // Internal polygon builders
    // =========================================================================

    // Build a horizontal floor polygon (normal = +Z) from four CCW vertices
    // (as viewed from above).
    private static ResolvedPolygon MakeFloor(
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        var verts = new[] { v0, v1, v2, v3 };
        var normal = Vector3.UnitZ;
        float dotSum = 0f;
        foreach (var v in verts) dotSum += Vector3.Dot(normal, v);
        float d = -(dotSum / verts.Length);
        return new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = new Plane(normal, d),
            NumPoints = 4,
            SidesType = CullMode.None,
        };
    }

    private static ResolvedPolygon MakeQuad(
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        Vector3 expectedNormal)
    {
        var verts = new[] { v0, v1, v2, v3 };
        float dotSum = 0f;
        foreach (var v in verts) dotSum += Vector3.Dot(expectedNormal, v);
        float d = -(dotSum / verts.Length);
        return new ResolvedPolygon
        {
            Vertices  = verts,
            Plane     = new Plane(expectedNormal, d),
            NumPoints = 4,
            SidesType = CullMode.None,
        };
    }
}
