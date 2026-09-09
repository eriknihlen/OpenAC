using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CollisionPrimitivesTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (Plane Plane, Vector3[] Verts) UnitSquareXY()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);           // Z = 0 plane
        var verts = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f),
        };
        return (plane, verts);
    }

    // -----------------------------------------------------------------------
    // 1. SphereIntersectsRay
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsRay_DirectHit_ReturnsTrue()
    {
        // Ray starts 5 units in front of a unit sphere at origin, shoots −Z.
        var hit = CollisionPrimitives.SphereIntersectsRay(
            sphereCenter: Vector3.Zero, sphereRadius: 1f,
            rayOrigin: new Vector3(0f, 0f, 5f),
            rayDir: new Vector3(0f, 0f, -1f),
            out double t);

        Assert.True(hit);
        // t ≈ 4.0 (ray travels from z=5 to z=1, where sphere surface is)
        Assert.True(t >= 3.9 && t <= 4.1, $"Expected t≈4, got {t}");
    }

    [Fact]
    public void SphereIntersectsRay_Miss_ReturnsFalse()
    {
        var hit = CollisionPrimitives.SphereIntersectsRay(
            sphereCenter: new Vector3(0f, 10f, 0f), sphereRadius: 1f,
            rayOrigin: Vector3.Zero,
            rayDir: Vector3.UnitX,
            out double _);

        Assert.False(hit);
    }

    [Fact]
    public void SphereIntersectsRay_OriginInsideSphere_ReturnsFalse()
    {
        var hit = CollisionPrimitives.SphereIntersectsRay(
            sphereCenter: Vector3.Zero, sphereRadius: 5f,
            rayOrigin: new Vector3(1f, 0f, 0f),
            rayDir: Vector3.UnitX,
            out double _);

        Assert.False(hit);
    }

    [Fact]
    public void SphereIntersectsRay_GrazingHit_ReturnsTrue()
    {
        // Ray passes tangentially: origin at (1, 5, 0) dir −Y, sphere at origin radius 1.
        // Minimum approach distance = 1 (exactly tangent).
        var hit = CollisionPrimitives.SphereIntersectsRay(
            sphereCenter: Vector3.Zero, sphereRadius: 1f,
            rayOrigin: new Vector3(1f, 5f, 0f),
            rayDir: new Vector3(0f, -1f, 0f),
            out double _);

        Assert.True(hit);
    }

    // -----------------------------------------------------------------------
    // 2. RayPlaneIntersect
    // -----------------------------------------------------------------------

    [Fact]
    public void RayPlaneIntersect_PerpendicularRay_ReturnsCorrectT()
    {
        // Z=0 plane, ray from (0,0,5) shooting −Z.  t should be 5.
        var plane = new Plane(Vector3.UnitZ, 0f);
        var hit = CollisionPrimitives.RayPlaneIntersect(
            plane,
            rayOrigin: new Vector3(0f, 0f, 5f),
            rayDir: new Vector3(0f, 0f, -1f),
            out double t);

        Assert.True(hit);
        Assert.Equal(5.0, t, precision: 5);
    }

    [Fact]
    public void RayPlaneIntersect_ParallelRay_ReturnsFalse()
    {
        // Ray in XY plane, plane is Z=0 — parallel, no intersection.
        var plane = new Plane(Vector3.UnitZ, 0f);
        var hit = CollisionPrimitives.RayPlaneIntersect(
            plane,
            rayOrigin: Vector3.Zero,
            rayDir: Vector3.UnitX,
            out double _);

        Assert.False(hit);
    }

    [Fact]
    public void RayPlaneIntersect_BehindRay_ReturnsFalse()
    {
        // Plane is at Z=10, ray starts at Z=0 and shoots +Z away from plane… wait:
        // actually the plane at z=−5 (D=5) is behind a ray shooting from z=0 in +Z.
        // Plane equation: z + 5 = 0 → z = −5.
        var plane = new Plane(Vector3.UnitZ, 5f);   // dot(N,p)+D=0 → z=−5
        var hit = CollisionPrimitives.RayPlaneIntersect(
            plane,
            rayOrigin: new Vector3(0f, 0f, 0f),
            rayDir: new Vector3(0f, 0f, 1f),
            out double t);

        // t = −5/1 = −5 < 0 → should return false
        Assert.False(hit);
    }


    [Fact]
    public void CalcNormal_SquareInXYPlane_NormalIsUnitZ()
    {
        var verts = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(0f, 1f, 0f),
        };
        CollisionPrimitives.CalcNormal(verts, out var normal, out float d);

        Assert.Equal(0f, normal.X, precision: 5);
        Assert.Equal(0f, normal.Y, precision: 5);
        Assert.True(MathF.Abs(MathF.Abs(normal.Z) - 1f) < 0.001f,
            $"Expected |Z|≈1, got {normal.Z}");

        // The polygon lies on Z=0, so plane.D should be ≈ 0
        Assert.Equal(0f, d, precision: 5);
    }

    [Fact]
    public void CalcNormal_Triangle_NormalIsNormalised()
    {
        var verts = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 2f, 0f),
        };
        CollisionPrimitives.CalcNormal(verts, out var normal, out _);

        Assert.True(MathF.Abs(normal.Length() - 1f) < 0.001f,
            $"Normal should be unit-length, got {normal.Length()}");
    }

    [Fact]
    public void CalcNormal_DegeneratePolygon_ReturnsZero()
    {
        var verts = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(2f, 0f, 0f),
        };
        CollisionPrimitives.CalcNormal(verts, out var normal, out float d);

        Assert.Equal(Vector3.Zero, normal);
    }

    // -----------------------------------------------------------------------
    // 4. SphereIntersectsPoly
    // -----------------------------------------------------------------------

    [Fact]
    public void SphereIntersectsPoly_SphereAboveCentre_ReturnsTrue()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.SphereIntersectsPoly(
            plane, verts,
            new Vector3(0.5f, 0.5f, 0.5f), 0.6f,
            out var contact);

        Assert.True(hit);
        Assert.Equal(0f, contact.Z, precision: 4);
    }

    [Fact]
    public void SphereIntersectsPoly_SphereFarAbove_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.SphereIntersectsPoly(
            plane, verts,
            new Vector3(0.5f, 0.5f, 5f), 0.5f,
            out _);

        Assert.False(hit);
    }

    [Fact]
    public void SphereIntersectsPoly_SphereOutsideEdge_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        // Sphere entirely outside the polygon (to the right in X), too far to graze an edge.
        var hit = CollisionPrimitives.SphereIntersectsPoly(
            plane, verts,
            new Vector3(5f, 0.5f, 0f), 0.3f,
            out _);

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // 5. FindTimeOfCollision
    // -----------------------------------------------------------------------

    [Fact]
    public void FindTimeOfCollision_SphereApproachingPlane_ReturnsTrueAndPlaneT()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.FindTimeOfCollision(
            plane, verts,
            sphereOrigin: new Vector3(0.5f, 0.5f, 3f),
            sphereRadius: 1f,
            rayDir: new Vector3(0f, 0f, -1f),
            out float t);

        Assert.True(hit);
        Assert.Equal(-3f, t, precision: 4);
    }

    [Fact]
    public void FindTimeOfCollision_ParallelRay_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.FindTimeOfCollision(
            plane, verts,
            sphereOrigin: new Vector3(0.5f, 0.5f, 0.5f),
            sphereRadius: 0.2f,
            rayDir: Vector3.UnitX,
            out float _);

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // 6. HitsWalkable
    // -----------------------------------------------------------------------

    [Fact]
    public void HitsWalkable_SphereApproachingFromAbove_ReturnsTrue()
    {
        var (plane, verts) = UnitSquareXY();
        var hitFromBelow = CollisionPrimitives.HitsWalkable(
            plane, verts,
            new Vector3(0.5f, 0.5f, -0.3f), 0.5f,
            movementDir: new Vector3(0f, 0f, 1f));   // moving UP toward the underside

        Assert.True(hitFromBelow);  // sphere is within range and direction is positive
    }

    [Fact]
    public void HitsWalkable_SphereMovingAwayFromNormal_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        // Moving −Z (away from floor normal +Z) → HitsWalkable returns false.
        var hit = CollisionPrimitives.HitsWalkable(
            plane, verts,
            new Vector3(0.5f, 0.5f, 0.3f), 0.5f,
            movementDir: new Vector3(0f, 0f, -1f));

        Assert.False(hit);
    }

    // -----------------------------------------------------------------------
    // 7. FindWalkableCollision
    // -----------------------------------------------------------------------

    [Fact]
    public void FindWalkableCollision_InsidePolygon_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.FindWalkableCollision(
            plane, verts,
            sphereOrigin: new Vector3(0.5f, 0.5f, 1f),
            movementDir: new Vector3(0f, 0f, -1f),
            out var edgeNormal);

        Assert.False(hit);
    }

    [Fact]
    public void FindWalkableCollision_ParallelMovement_ReturnsFalse()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.FindWalkableCollision(
            plane, verts,
            sphereOrigin: new Vector3(0.5f, 0.5f, 0f),
            movementDir: Vector3.UnitX,
            out _);

        Assert.False(hit);
    }

    [Fact]
    public void FindWalkableCollision_SphereCrossesEdge_ReturnsTrueWithNormal()
    {
        var (plane, verts) = UnitSquareXY();
        var hit = CollisionPrimitives.FindWalkableCollision(
            plane, verts,
            sphereOrigin: new Vector3(2.5f, 0.5f, 1f),
            movementDir: Vector3.Normalize(new Vector3(-1f, 0f, -1f)),
            out var edgeNormal);

        Assert.True(hit);
        Assert.True(edgeNormal.LengthSquared() > 0.5f, "Edge normal should be non-zero");
        Assert.True(MathF.Abs(edgeNormal.Length() - 1f) < 0.01f, "Edge normal should be unit length");
    }

    // -----------------------------------------------------------------------
    // 8. SlideSphere
    // -----------------------------------------------------------------------

    [Fact]
    public void SlideSphere_SphereAbovePlane_ReturnsPositiveT()
    {
        // Plane at Z=0, sphere at Z=3 (above), radius=0.5, moving −Z.
        var plane = new Plane(Vector3.UnitZ, 0f);
        float t = CollisionPrimitives.SlideSphere(
            plane, sphereRadius: 0.5f,
            sphereCenter: new Vector3(0f, 0f, 3f),
            movementDir: new Vector3(0f, 0f, -1f));

        // Should reach the plane when t ≈ 2.5 (z=3 − 0.5 radius = z=0.5 …
        // offset = −0.5 (radius on neg side), dist=3, t=(−0.5−3)/−1=3.5)
        Assert.True(t > 0f, $"Expected positive t, got {t}");
    }

    [Fact]
    public void SlideSphere_AlreadyTouching_ReturnsMaxValue()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);
        float t = CollisionPrimitives.SlideSphere(
            plane, sphereRadius: 0.5f,
            sphereCenter: new Vector3(0f, 0f, 0.3f),
            movementDir: new Vector3(0f, 0f, -1f));

        Assert.Equal(float.MaxValue, t);
    }

    [Fact]
    public void SlideSphere_ParallelMovement_ReturnsZero()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);
        float t = CollisionPrimitives.SlideSphere(
            plane, sphereRadius: 0.5f,
            sphereCenter: new Vector3(0f, 0f, 3f),
            movementDir: Vector3.UnitX);   // parallel to plane

        Assert.Equal(0f, t);
    }

    // -----------------------------------------------------------------------
    // 9. LandOnSphere
    // -----------------------------------------------------------------------

    [Fact]
    public void LandOnSphere_SphereJustInsideRadius_LandsSuccessfully()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);
        var center = new Vector3(0f, 0f, 0.3f);
        var dir = new Vector3(0f, 0f, -1f);
        float wi = 1f;

        bool landed = CollisionPrimitives.LandOnSphere(
            plane, sphereRadius: 0.5f,
            ref center, ref dir, ref wi);

        Assert.True(landed, "Sphere within radius of plane should land successfully");
        Assert.True(MathF.Abs(center.Z - 0.5f) < 0.01f,
            $"After landing centre.Z should be at radius (0.5), got {center.Z}");
    }

    [Fact]
    public void LandOnSphere_ParallelMovement_ReturnsFalse()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);
        var center = new Vector3(0f, 0f, 2f);
        var dir = Vector3.UnitX;
        float wi = 1f;

        bool landed = CollisionPrimitives.LandOnSphere(
            plane, sphereRadius: 0.5f,
            ref center, ref dir, ref wi);

        Assert.False(landed);
    }

    [Fact]
    public void LandOnSphere_InvalidWalkInterp_ReturnsFalse()
    {
        var plane = new Plane(Vector3.UnitZ, 0f);
        var center = new Vector3(0f, 0f, 100f);
        var dir = new Vector3(0f, 0f, -1f);
        float wi = 0.0001f;

        bool landed = CollisionPrimitives.LandOnSphere(
            plane, sphereRadius: 0.5f,
            ref center, ref dir, ref wi);

        Assert.False(landed);
    }
}
