using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class BSPQueryTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static VertexArray UnitSquareVertexArray()
    {
        var va = new VertexArray();
        var positions = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f),
        };
        for (ushort i = 0; i < positions.Length; i++)
        {
            var sv = new SWVertex { Origin = positions[i], Normal = Vector3.UnitZ };
            va.Vertices[i] = sv;
        }
        return va;
    }

    private static Polygon UnitSquarePolygon() => new Polygon
    {
        SidesType = DatReaderWriter.Enums.CullMode.None,
        VertexIds = new List<short> { 0, 1, 2, 3 },
    };

    private static PhysicsBSPNode LeafNode(Sphere bounds)
    {
        var node = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = bounds,
        };
        node.Polygons.Add(0);
        return node;
    }

    // -----------------------------------------------------------------------
    // Test 1: null node returns false without throwing
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsPoly_NullNode_ReturnsFalse()
    {
        var polygons = new Dictionary<ushort, Polygon>();
        var vertices = new VertexArray();

        bool hit = BSPQuery.SphereIntersectsPoly(
            null, polygons, vertices,
            new Vector3(0.5f, 0.5f, 0.1f), 0.2f,
            out _, out _);

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // Test 2: sphere far outside the bounding sphere is fast-rejected
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsPoly_MissesBoundingSphere_ReturnsFalse()
    {
        var bounds = new Sphere { Origin = Vector3.Zero, Radius = 1f };
        var node = LeafNode(bounds);
        var polygons = new Dictionary<ushort, Polygon> { [0] = UnitSquarePolygon() };
        var vertices = UnitSquareVertexArray();

        bool hit = BSPQuery.SphereIntersectsPoly(
            node, polygons, vertices,
            new Vector3(100f, 100f, 100f), 0.5f,
            out _, out _);

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // Test 3: sphere resting just above the unit-square floor polygon hits
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsPoly_HitsLeafPolygon()
    {
        var bounds = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0f), Radius = 2f };
        var node = LeafNode(bounds);
        var polygons = new Dictionary<ushort, Polygon> { [0] = UnitSquarePolygon() };
        var vertices = UnitSquareVertexArray();

        bool hit = BSPQuery.SphereIntersectsPoly(
            node, polygons, vertices,
            new Vector3(0.5f, 0.5f, 0.3f), 0.5f,
            out ushort polyId, out Vector3 normal);

        Assert.True(hit);
        Assert.Equal(0, polyId);
        // Normal should point roughly upward (+Z).
        Assert.True(normal.Z > 0.9f, $"Expected Z-up normal, got {normal}");
    }


    [Fact]
    public void SphereIntersectsPoly_SphereTooHigh_ReturnsFalse()
    {
        var bounds = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0f), Radius = 2f };
        var node = LeafNode(bounds);
        var polygons = new Dictionary<ushort, Polygon> { [0] = UnitSquarePolygon() };
        var vertices = UnitSquareVertexArray();

        bool hit = BSPQuery.SphereIntersectsPoly(
            node, polygons, vertices,
            new Vector3(0.5f, 0.5f, 5f), 0.3f,
            out _, out _);

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // Test 5: internal node — sphere on positive side recurses pos subtree
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsPoly_InternalNode_PosSubtreeHit()
    {
        var leafBounds = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0f), Radius = 2f };
        var leafNode = LeafNode(leafBounds);

        var polygons = new Dictionary<ushort, Polygon> { [0] = UnitSquarePolygon() };
        var vertices = UnitSquareVertexArray();

        // Splitting plane: Z = 0, normal = +Z, D = 0.
        // Sphere at Z = 0.3 is on the positive side.
        var internalBounds = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0f), Radius = 5f };
        var internalNode = new PhysicsBSPNode
        {
            Type = BSPNodeType.BPnn,   // has PosNode only (BPnn = pos + null-neg)
            SplittingPlane = new Plane(Vector3.UnitZ, 0f),
            BoundingSphere = internalBounds,
            PosNode = leafNode,
            NegNode = null,
        };

        bool hit = BSPQuery.SphereIntersectsPoly(
            internalNode, polygons, vertices,
            new Vector3(0.5f, 0.5f, 0.3f), 0.5f,
            out ushort polyId, out _);

        Assert.True(hit);
        Assert.Equal(0, polyId);
    }


    [Fact]
    public void PhysicsDataCache_CachesGfxObjWithPhysics()
    {
        var cache = new PhysicsDataCache();
        Assert.Equal(0, cache.GfxObjCount);
        Assert.Equal(0, cache.SetupCount);

        // GetGfxObj for an unknown id should return null safely.
        Assert.Null(cache.GetGfxObj(0x01000001u));
        Assert.Null(cache.GetSetup(0x02000001u));
    }


    private static (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved)
        BuildTwoFloorsBsp(float lowerZ, float upperZ)
    {
        var center = new Vector3(0.5f, 0.5f, (lowerZ + upperZ) * 0.5f);
        float halfHeight = MathF.Abs(upperZ - lowerZ) * 0.5f + 1.0f;
        float radius = MathF.Sqrt(0.5f * 0.5f + 0.5f * 0.5f + halfHeight * halfHeight);

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = center, Radius = radius },
        };
        root.Polygons.Add(0);
        root.Polygons.Add(1);

        Vector3[] lowerVerts =
        {
            new Vector3(0f, 0f, lowerZ),
            new Vector3(1f, 0f, lowerZ),
            new Vector3(1f, 1f, lowerZ),
            new Vector3(0f, 1f, lowerZ),
        };
        Vector3[] upperVerts =
        {
            new Vector3(0f, 0f, upperZ),
            new Vector3(1f, 0f, upperZ),
            new Vector3(1f, 1f, upperZ),
            new Vector3(0f, 1f, upperZ),
        };

        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices = lowerVerts,
                Plane = new Plane(Vector3.UnitZ, -lowerZ),
                NumPoints = 4,
                SidesType = CullMode.None,
            },
            [1] = new ResolvedPolygon
            {
                Vertices = upperVerts,
                Plane = new Plane(Vector3.UnitZ, -upperZ),
                NumPoints = 4,
                SidesType = CullMode.None,
            },
        };

        return (root, resolved);
    }

    /// <summary>
    /// Build a Transition with WalkableAllowance set to FloorZ — what the
    /// indoor walkable-plane synthesis uses.
    /// </summary>
    private static Transition BuildFloorZTransition()
    {
        var transition = new Transition();
        transition.SpherePath.WalkableAllowance = PhysicsGlobals.FloorZ;
        transition.SpherePath.WalkInterp = 1.0f;
        return transition;
    }

    [Fact]
    public void FindWalkableSphere_TwoFloors_FootBetween_PicksLowerFloor()
    {
        var (root, resolved) = BuildTwoFloorsBsp(lowerZ: 0f, upperZ: 3f);
        var transition = BuildFloorZTransition();

        var sphere = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0.4f), Radius = 0.48f };

        bool found = BSPQuery.FindWalkableSphere(
            root, resolved, transition,
            sphere,
            probeDistance: 0.5f,
            up: Vector3.UnitZ,
            out var hitPoly,
            out var hitPolyId,
            out var adjustedCenter);

        Assert.True(found);
        Assert.Equal((ushort)0, hitPolyId);
        Assert.NotNull(hitPoly);
        Assert.Equal(1f, hitPoly!.Plane.Normal.Z, precision: 3);  // horizontal floor: normal.Z = 1
        Assert.Equal(0.48f, adjustedCenter.Z, precision: 2);
    }

    [Fact]
    public void FindWalkableSphere_OnlyUpperFloor_FootAbove_PicksUpperFloor()
    {
        var (root, resolved) = BuildTwoFloorsBsp(lowerZ: 0f, upperZ: 3f);
        var transition = BuildFloorZTransition();

        var sphere = new Sphere { Origin = new Vector3(0.5f, 0.5f, 3.4f), Radius = 0.48f };

        bool found = BSPQuery.FindWalkableSphere(
            root, resolved, transition,
            sphere,
            probeDistance: 0.5f,
            up: Vector3.UnitZ,
            out var hitPoly,
            out var hitPolyId,
            out var adjustedCenter);

        Assert.True(found);
        Assert.Equal((ushort)1, hitPolyId);
        Assert.NotNull(hitPoly);
        Assert.Equal(1f, hitPoly!.Plane.Normal.Z, precision: 3);  // horizontal upper floor
        // Same math as Test 1 but offset by 3: adjustedCenter.Z = 3.0 + 0.48 = 3.48.
        Assert.Equal(3.48f, adjustedCenter.Z, precision: 2);
    }

    [Fact]
    public void FindWalkableSphere_NoWalkableInProbeRange_ReturnsFalse()
    {
        // Two floors at Z=0 and Z=3. Foot at Z=10 with radius 0.48 — out of
        // sphere-overlap range for both polygons (|10-0|=10 >> 0.48, |10-3|=7 >> 0.48).
        // find_walkable requires the sphere to overlap the polygon plane; neither
        // floor is within overlap range, so no hit is found.
        var (root, resolved) = BuildTwoFloorsBsp(lowerZ: 0f, upperZ: 3f);
        var transition = BuildFloorZTransition();

        var sphere = new Sphere { Origin = new Vector3(0.5f, 0.5f, 10f), Radius = 0.48f };

        bool found = BSPQuery.FindWalkableSphere(
            root, resolved, transition,
            sphere,
            probeDistance: 0.5f,
            up: Vector3.UnitZ,
            out var hitPoly,
            out var hitPolyId,
            out var adjustedCenter);

        Assert.False(found);
        Assert.Null(hitPoly);
        Assert.Equal((ushort)0, hitPolyId);
        Assert.Equal(sphere.Origin, adjustedCenter);
    }

    [Fact]
    public void FindWalkableSphere_SteepPoly_RejectedByWalkableAllowance()
    {
        var center = new Vector3(0.5f, 0.5f, 0f);
        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = center, Radius = 2f },
        };
        root.Polygons.Add(0);

        // Plane tilted: normal has Z = 0.5 (60° slope). Build from two orthogonal verts.
        var steepNormal = Vector3.Normalize(new Vector3(0f, MathF.Sqrt(0.75f), 0.5f));
        // Vertices lying on the plane through the origin.
        float rise = MathF.Sqrt(0.75f) / 0.5f;   // how much Y-displacement equals 1 unit Z-rise
        Vector3[] verts =
        {
            new Vector3(0f, 0f,    0f),
            new Vector3(1f, 0f,    0f),
            new Vector3(1f, 1f,   rise),
            new Vector3(0f, 1f,   rise),
        };
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices = verts,
                Plane = new Plane(steepNormal, -Vector3.Dot(steepNormal, verts[0])),
                NumPoints = 4,
                SidesType = CullMode.None,
            },
        };

        var transition = BuildFloorZTransition();
        // Sphere overlapping the tilted plane at the origin side.
        var sphere = new Sphere { Origin = new Vector3(0.5f, 0.5f, 0.3f), Radius = 0.48f };

        bool found = BSPQuery.FindWalkableSphere(
            root, resolved, transition,
            sphere,
            probeDistance: 0.5f,
            up: Vector3.UnitZ,
            out _,
            out _,
            out _);

        Assert.False(found);
    }


    private static (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved)
        BuildSingleFloorBsp(float floorZ)
    {
        var verts = new[]
        {
            new Vector3(-2f, -2f, floorZ),
            new Vector3( 2f, -2f, floorZ),
            new Vector3( 2f,  2f, floorZ),
            new Vector3(-2f,  2f, floorZ),
        };

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0f, 0f, floorZ),
                Radius = 3f,
            },
        };
        root.Polygons.Add(0);

        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices  = verts,
                Plane     = new Plane(Vector3.UnitZ, -floorZ),
                NumPoints = 4,
                SidesType = CullMode.None,
            },
        };

        return (root, resolved);
    }

    [Fact]
    public void FindCollisions_StepDown_TranslatedWorldOrigin_WritesWorldSpacePlane()
    {
        var (root, resolved) = BuildSingleFloorBsp(floorZ: 0f);

        var transition = new Transition();
        transition.SpherePath.WalkableAllowance = PhysicsGlobals.FloorZ;
        transition.SpherePath.WalkInterp        = 1.0f;
        transition.SpherePath.StepDown          = true;
        transition.SpherePath.StepDownAmt       = 0.5f;

        var localSphere = new Sphere
        {
            Origin = new Vector3(0f, 0f, 0.4f),
            Radius = 0.48f,
        };

        var state = BSPQuery.FindCollisions(
            root,
            resolved,
            transition,
            localSphere,
            localSphere1: null,
            localCurrCenter: localSphere.Origin,
            localSpaceZ: Vector3.UnitZ,
            scale: 1.0f,
            localToWorld: Quaternion.Identity,
            engine: null,
            worldOrigin: new Vector3(0f, 0f, 94f));

        // Path 3 step-down should fire and adjust the sphere onto the floor.
        Assert.Equal(TransitionState.Adjusted, state);

        Assert.True(transition.CollisionInfo.ContactPlaneValid);
        Assert.Equal(1.0f, transition.CollisionInfo.ContactPlane.Normal.Z, precision: 3);
        Assert.Equal(-94.0f, transition.CollisionInfo.ContactPlane.D, precision: 2);
    }


    private static (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved)
        BuildSingleWallBsp()
    {
        var verts = new[]
        {
            new Vector3(-2f, 0f, -2f),
            new Vector3(-2f, 0f,  2f),
            new Vector3( 2f, 0f,  2f),
            new Vector3( 2f, 0f, -2f),
        };

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0f, 0f, 0f),
                Radius = 4f,
            },
        };
        root.Polygons.Add(0);

        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices  = verts,
                // +Y-facing plane at Y=0 → normal=(0,+1,0), d=0
                Plane     = new Plane(Vector3.UnitY, 0f),
                NumPoints = 4,
                SidesType = CullMode.None,
                Id        = 0,
            },
        };

        return (root, resolved);
    }

    [Fact]
    public void FindCollisions_Path5_SphereOverlapsWallButMovesParallel_SetsNegPolyHit()
    {
        var (root, resolved) = BuildSingleWallBsp();

        var transition = new Transition();
        transition.SpherePath.WalkInterp = 1.0f;
        transition.ObjectInfo.State = ObjectInfoState.Contact;

        var localSphere = new Sphere
        {
            Origin = new Vector3(0f, 0.3f, 0f),
            Radius = 0.48f,
        };
        // Head sphere — also statically overlaps the wall (Z within poly range).
        var localSphere1 = new Sphere
        {
            Origin = new Vector3(0f, 0.3f, 1.0f),
            Radius = 0.48f,
        };

        var localCurrCenter = localSphere.Origin - new Vector3(0.05f, 0f, 0f);

        var state = BSPQuery.FindCollisions(
            root,
            resolved,
            transition,
            localSphere,
            localSphere1: localSphere1,
            localCurrCenter: localCurrCenter,
            localSpaceZ: Vector3.UnitZ,
            scale: 1.0f,
            localToWorld: Quaternion.Identity,
            engine: null,
            worldOrigin: Vector3.Zero);

        Assert.Equal(TransitionState.OK, state);
        Assert.True(transition.SpherePath.NegPolyHit,
            "Sphere statically overlaps wall but moves parallel → near-miss " +
            "polygon should be recorded, NegPolyHit should fire. The original " +
            "client's CPolygon::pos_hits_sphere writes the polygon out-pointer " +
            "BEFORE the front-face cull.");
    }

    [Fact]
    public void FindCollisions_Path5_SphereOverlapsWallAndMovesAway_SetsNegPolyHit()
    {
        var (root, resolved) = BuildSingleWallBsp();

        var transition = new Transition();
        transition.SpherePath.WalkInterp = 1.0f;
        transition.ObjectInfo.State = ObjectInfoState.Contact;

        var localSphere = new Sphere
        {
            Origin = new Vector3(0f, 0.3f, 0f),
            Radius = 0.48f,
        };
        var localSphere1 = new Sphere
        {
            Origin = new Vector3(0f, 0.3f, 1.0f),
            Radius = 0.48f,
        };
        var localCurrCenter = localSphere.Origin - new Vector3(0f, 0.05f, 0f);

        var state = BSPQuery.FindCollisions(
            root,
            resolved,
            transition,
            localSphere,
            localSphere1: localSphere1,
            localCurrCenter: localCurrCenter,
            localSpaceZ: Vector3.UnitZ,
            scale: 1.0f,
            localToWorld: Quaternion.Identity,
            engine: null,
            worldOrigin: Vector3.Zero);

        Assert.Equal(TransitionState.OK, state);
        Assert.True(transition.SpherePath.NegPolyHit,
            "Sphere overlaps wall and moves away → near-miss polygon should " +
            "still be recorded (front-face cull rejects motion but retail " +
            "records the polygon BEFORE that check).");
    }

    [Fact]
    public void FindCollisions_Path5_SphereOverlapsWallAndMovesInto_DispatchesStepSphereUp()
    {
        var (root, resolved) = BuildSingleWallBsp();

        var transition = new Transition();
        transition.SpherePath.WalkInterp = 1.0f;
        transition.ObjectInfo.State = ObjectInfoState.Contact;

        var localSphere = new Sphere
        {
            Origin = new Vector3(0f, 0.3f, 0f),
            Radius = 0.48f,
        };
        // Movement -Y (into the wall from the +Y side).
        var localCurrCenter = localSphere.Origin - new Vector3(0f, -0.05f, 0f);

        var state = BSPQuery.FindCollisions(
            root,
            resolved,
            transition,
            localSphere,
            localSphere1: null,
            localCurrCenter: localCurrCenter,
            localSpaceZ: Vector3.UnitZ,
            scale: 1.0f,
            localToWorld: Quaternion.Identity,
            engine: null,
            worldOrigin: Vector3.Zero);

        Assert.Equal(TransitionState.Slid, state);
        Assert.True(transition.CollisionInfo.CollisionNormalValid,
            "Full hit should set the collision normal (slide fallback).");
        Assert.False(transition.CollisionInfo.SlidingNormalValid,
            "find_collisions must not write the sliding normal — retail's " +
            "only in-transition writer is validate_transition.");
        Assert.False(transition.SpherePath.NegPolyHit,
            "Full hit should NOT also fire NegPolyHit — that's the near-miss " +
            "path only. The original client returns early on a full hit, " +
            "before the near-miss dispatch.");
    }
}
