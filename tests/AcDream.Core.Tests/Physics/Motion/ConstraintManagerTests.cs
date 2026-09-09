using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class ConstraintManagerTests
{
    private static (PhysicsObjHostStub host, ConstraintManager cm) Setup()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var host = new PhysicsObjHostStub(10u, world);
        return (host, new ConstraintManager(host));
    }

    private static Position Anchor(float x) => new(1u, new Vector3(x, 0f, 0f), Quaternion.Identity);

    [Fact]
    public void ConstrainTo_InitializesOffsetToCurrentDistanceFromAnchor()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);

        cm.ConstrainTo(Anchor(5f), startDistance: 2f, maxDistance: 10f);

        Assert.True(cm.IsConstrained);
        Assert.Equal(5f, cm.ConstraintPosOffset, 3); // distance(anchor(5,0,0), self(0,0,0))
    }

    [Fact]
    public void IsFullyConstrained_TrueOnlyBeyond90PercentOfMax()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);

        cm.ConstrainTo(Anchor(8f), 2f, 10f); // offset 8, 90% of 10 = 9 → 8 < 9
        Assert.False(cm.IsFullyConstrained());

        cm.ConstrainTo(Anchor(9.5f), 2f, 10f); // offset 9.5 > 9
        Assert.True(cm.IsFullyConstrained());
    }

    [Fact]
    public void IsFullyConstrained_FalseWhenNotConstrainedYet()
    {
        var (_, cm) = Setup();
        Assert.False(cm.IsFullyConstrained()); // max 0 → 0 < 0 is false
    }

    [Fact]
    public void AdjustOffset_InBand_AppliesLinearTaper()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);
        host.InContact = true;
        cm.ConstrainTo(Anchor(5f), startDistance: 2f, maxDistance: 10f); // offset 5 in (2,10)

        var frame = new MotionDeltaFrame { Origin = new Vector3(1f, 0f, 0f) };
        cm.AdjustOffset(frame, 0.1);

        // taper = (10-5)/(10-2) = 5/8 = 0.625.
        Assert.Equal(0.625f, frame.Origin.X, 3);
        Assert.Equal(0.625f, cm.ConstraintPosOffset, 3); // recomputed = |offset|
    }

    [Fact]
    public void AdjustOffset_PastMax_HardClampsToZero()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);
        host.InContact = true;
        cm.ConstrainTo(Anchor(20f), 2f, 10f); // offset 20 >= max 10

        var frame = new MotionDeltaFrame { Origin = new Vector3(1f, 0f, 0f) };
        cm.AdjustOffset(frame, 0.1);

        Assert.Equal(Vector3.Zero, frame.Origin);
    }

    [Fact]
    public void AdjustOffset_BelowStart_PassesThrough()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);
        host.InContact = true;
        cm.ConstrainTo(Anchor(1f), startDistance: 2f, maxDistance: 10f); // offset 1 < start 2

        var frame = new MotionDeltaFrame { Origin = new Vector3(1f, 0f, 0f) };
        cm.AdjustOffset(frame, 0.1);

        Assert.Equal(1f, frame.Origin.X, 3); // unscaled
        Assert.Equal(1f, cm.ConstraintPosOffset, 3);
    }

    [Fact]
    public void AdjustOffset_Airborne_SkipsClampButStillTracksLength()
    {
        var (host, cm) = Setup();
        host.SetOrigin(Vector3.Zero);
        host.InContact = false; // airborne
        cm.ConstrainTo(Anchor(20f), 2f, 10f);

        var frame = new MotionDeltaFrame { Origin = new Vector3(3f, 4f, 0f) };
        cm.AdjustOffset(frame, 0.1);

        Assert.Equal(new Vector3(3f, 4f, 0f), frame.Origin); // untouched while airborne
        Assert.Equal(5f, cm.ConstraintPosOffset, 3); // |(3,4,0)| = 5, still updated
    }

    [Fact]
    public void AdjustOffset_NotConstrained_IsNoOp()
    {
        var (host, cm) = Setup();
        var frame = new MotionDeltaFrame { Origin = new Vector3(7f, 0f, 0f) };
        cm.AdjustOffset(frame, 0.1);
        Assert.Equal(new Vector3(7f, 0f, 0f), frame.Origin);
    }

    [Fact]
    public void UnConstrain_ClearsFlag()
    {
        var (host, cm) = Setup();
        cm.ConstrainTo(Anchor(5f), 2f, 10f);
        cm.UnConstrain();
        Assert.False(cm.IsConstrained);
    }
}
