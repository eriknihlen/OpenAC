using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class SphereIntersectsSphereConformanceTests
{
    // -----------------------------------------------------------------------
    // Geometry anchors — verified by hand before the implementation existed
    // -----------------------------------------------------------------------

    [Fact]
    public void HeadOn_HitsAtExpectedTime()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(5f, 0f, 0f),
            targetCenter: new Vector3(5f, 0f, 0f),
            targetRadius: 1f,
            out float t);

        Assert.True(hit, "Head-on sweep should hit");
        Assert.True(MathF.Abs(t - 0.6f) < 1e-4f,
            $"Expected t≈0.6, got {t:G6}");
    }

    [Fact]
    public void PerpendicularSweep_TooFar_Misses()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(0f, 10f, 0f),
            targetCenter: new Vector3(3f, 0f, 0f),
            targetRadius: 1f,
            out float _);

        Assert.False(hit, "Perpendicular sweep at distance 3 > combinedR 2 should miss");
    }

    [Fact]
    public void TangentSweep_Hits()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(0f, 6f, 0f),
            targetCenter: new Vector3(2f, 3f, 0f),
            targetRadius: 1f,
            out float t);

        Assert.True(hit, "Tangent sweep (distance = combinedR) should register as a hit");
        Assert.True(t > 0f && t <= 1f, $"t={t:G6} should be in (0,1]");
    }

    [Fact]
    public void OffAxisSweep_JustOutside_Misses()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(0f, 6f, 0f),
            targetCenter: new Vector3(2.1f, 3f, 0f),
            targetRadius: 1f,
            out float _);

        Assert.False(hit, "Lateral offset 2.1 > combinedR 2 — should miss");
    }

    [Fact]
    public void SweepAwayFromTarget_Misses()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(-5f, 0f, 0f),
            targetCenter: new Vector3(5f, 0f, 0f),
            targetRadius: 1f,
            out float _);

        Assert.False(hit, "Sweep directly away from target should not hit");
    }

    /// <summary>
    /// Sweep within the step but the target is too far for the step to reach.
    /// Target is 10 units away, sweep is only 3 units — t would be >1.
    /// </summary>
    [Fact]
    public void TargetBeyondStep_Misses()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(3f, 0f, 0f),
            targetCenter: new Vector3(10f, 0f, 0f),
            targetRadius: 1f,
            out float _);

        Assert.False(hit, "Target 10 away with only a 3-unit sweep should miss (t>1)");
    }

    /// <summary>
    /// Zero-length sweep is degenerate — should not hit regardless of position.
    /// </summary>
    [Fact]
    public void DegenerateSweep_Misses()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   Vector3.Zero,
            targetCenter: new Vector3(0.5f, 0f, 0f),
            targetRadius: 0.1f,
            out float _);

        Assert.False(hit, "Zero-length sweep should return false (degenerate)");
    }

    [Fact]
    public void AlreadyOverlapping_ReturnsFalse()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  Vector3.Zero,
            moverRadius:  1f,
            sweepDelta:   new Vector3(1f, 0f, 0f),
            targetCenter: new Vector3(0.5f, 0f, 0f),
            targetRadius: 1f,
            out float _);

        Assert.False(hit,
            "Already-overlapping spheres: retail FindTimeOfCollision returns -1 (no forward t); SweptSphereHitsSphere should return false");
    }

    // -----------------------------------------------------------------------
    // 3-D geometry — sphere primitive must use full 3-D distance
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pure Z-axis sweep: verifies the primitive uses 3-D distance (not XY-only).
    /// A purely vertical sweep toward a sphere directly below should hit.
    /// </summary>
    [Fact]
    public void VerticalSweep_HitsTargetBelow()
    {
        bool hit = CollisionPrimitives.SweptSphereHitsSphere(
            moverCenter:  new Vector3(0f, 0f, 5f),
            moverRadius:  1f,
            sweepDelta:   new Vector3(0f, 0f, -5f),
            targetCenter: Vector3.Zero,
            targetRadius: 1f,
            out float t);

        Assert.True(hit, "Vertical sweep toward sphere below should hit");
        Assert.True(MathF.Abs(t - 0.6f) < 1e-4f, $"Expected t≈0.6, got {t:G6}");
    }
}
