using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class PositionManagerFacadeTests
{
    private static (PhysicsObjHostStub self, PhysicsObjHostStub target, PositionManager pm) Setup()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world);
        var target = new PhysicsObjHostStub(20u, world);
        return (self, target, new PositionManager(self));
    }

    [Fact]
    public void UnStick_BeforeAnyStick_IsNoOp()
    {
        var (_, _, pm) = Setup();
        pm.UnStick(); // no sticky manager yet — must not throw
        Assert.Null(pm.Sticky);
        Assert.Equal(0u, pm.GetStickyObjectId());
    }

    [Fact]
    public void StickTo_LazilyCreatesSticky_AndForwards()
    {
        var (self, target, pm) = Setup();
        pm.StickTo(target.Id, radius: 0.5f, height: 1.0f);

        Assert.NotNull(pm.Sticky);
        Assert.Equal(target.Id, pm.GetStickyObjectId());
    }

    [Fact]
    public void IsFullyConstrained_FalseWhenNoConstraintManager()
    {
        var (_, _, pm) = Setup();
        Assert.False(pm.IsFullyConstrained());
        Assert.Null(pm.Constraint);
    }

    [Fact]
    public void ConstrainTo_LazilyCreatesConstraint()
    {
        var (self, _, pm) = Setup();
        self.SetOrigin(Vector3.Zero);
        pm.ConstrainTo(new Position(1u, new Vector3(5f, 0f, 0f), Quaternion.Identity), 2f, 10f);
        Assert.NotNull(pm.Constraint);
        Assert.True(pm.Constraint!.IsConstrained);
    }

    [Fact]
    public void HandleUpdateTarget_ForwardsToSticky()
    {
        var (self, target, pm) = Setup();
        pm.StickTo(target.Id, 0.5f, 1.0f);

        var tp = new Position(1u, new Vector3(2f, 0f, 0f), Quaternion.Identity);
        pm.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.Ok, tp, tp));

        Assert.True(pm.Sticky!.Initialized);
    }

    [Fact]
    public void AdjustOffset_ChainsStickyThenConstraint()
    {
        var (self, target, pm) = Setup();
        self.Radius = 0.5f;
        self.MinterpMaxSpeed = 1.0f;
        self.InContact = true;

        pm.StickTo(target.Id, 0.5f, 1.0f);
        target.SetOrigin(new Vector3(5f, 0f, 0f));
        var tp = new Position(1u, new Vector3(5f, 0f, 0f), Quaternion.Identity);
        pm.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.Ok, tp, tp));

        self.SetOrigin(Vector3.Zero);
        pm.ConstrainTo(new Position(1u, new Vector3(6f, 0f, 0f), Quaternion.Identity),
            startDistance: 2f, maxDistance: 10f); // offset 6 → taper (10-6)/(10-2)=0.5

        var frame = new MotionDeltaFrame();
        pm.AdjustOffset(frame, quantum: 0.1);

        Assert.Equal(0.25f, frame.Origin.X, 3);
    }

    [Fact]
    public void UseTime_DrivesStickyTimeout()
    {
        var (self, target, pm) = Setup();
        self.CurTime = 100.0;
        pm.StickTo(target.Id, 0.5f, 1.0f); // deadline 101

        self.CurTime = 101.001;
        pm.UseTime();

        Assert.Equal(0u, pm.GetStickyObjectId()); // unstuck
    }
}
