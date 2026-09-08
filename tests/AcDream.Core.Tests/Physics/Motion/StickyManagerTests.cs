using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class StickyManagerTests
{
    private static (PhysicsObjHostStub self, PhysicsObjHostStub target, StickyManager sticky) Setup(
        uint targetId = 20u, float targetRadius = 0.5f)
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world);
        var target = new PhysicsObjHostStub(targetId, world);
        var sticky = new StickyManager(self);
        return (self, target, sticky);
    }

    private static StickyManager StuckAndInitialized(
        PhysicsObjHostStub self, PhysicsObjHostStub target, Vector3 targetOrigin, float targetRadius = 0.5f)
    {
        var sticky = new StickyManager(self);
        sticky.StickTo(target.Id, targetRadius, targetHeight: 1.0f);
        target.SetOrigin(targetOrigin);
        var tp = new Position(1u, targetOrigin, Quaternion.Identity);
        sticky.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.Ok, tp, tp));
        return sticky;
    }

    [Fact]
    public void StickTo_SetsTargetAndTimeout_AndSubscribesVoyeurOnTarget()
    {
        var (self, target, sticky) = Setup();
        self.CurTime = 100.0;

        sticky.StickTo(target.Id, targetRadius: 0.5f, targetHeight: 2.0f);

        Assert.Equal(target.Id, sticky.TargetId);
        Assert.Equal(0.5f, sticky.TargetRadius);
        Assert.False(sticky.Initialized);
        Assert.Equal(101.0, sticky.StickyTimeoutTime); // now + StickyTime(1.0)
        // set_target(0, target, 0.5, 0.5) → watcher subscribes ON the target.
        Assert.NotNull(target.TargetManagerOrNull);
        Assert.True(target.TargetManager.VoyeurTable!.ContainsKey(self.Id));
    }

    [Fact]
    public void HandleUpdateTarget_Ok_MarksInitializedAndCachesPosition()
    {
        var (self, target, sticky) = Setup();
        sticky.StickTo(target.Id, 0.5f, 1.0f);

        var tp = new Position(1u, new Vector3(3f, 0f, 0f), Quaternion.Identity);
        sticky.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.Ok, tp, tp));

        Assert.True(sticky.Initialized);
        Assert.Equal(new Vector3(3f, 0f, 0f), sticky.TargetPosition.Frame.Origin);
    }

    [Fact]
    public void HandleUpdateTarget_ForeignObject_Ignored()
    {
        var (self, target, sticky) = Setup();
        sticky.StickTo(target.Id, 0.5f, 1.0f);

        var tp = new Position(1u, Vector3.Zero, Quaternion.Identity);
        sticky.HandleUpdateTarget(new TargetInfo(999u, TargetStatus.Ok, tp, tp));

        Assert.False(sticky.Initialized);
        Assert.Equal(target.Id, sticky.TargetId); // still stuck to the real target
    }

    [Fact]
    public void HandleUpdateTarget_NonOkStatus_TearsDown()
    {
        var (self, target, sticky) = Setup();
        sticky.StickTo(target.Id, 0.5f, 1.0f);
        int interruptsBefore = self.InterruptCurrentMovementCalls;

        var tp = new Position(1u, Vector3.Zero, Quaternion.Identity);
        sticky.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.ExitWorld, tp, tp));

        Assert.Equal(0u, sticky.TargetId);
        Assert.False(sticky.Initialized);
        Assert.Equal(interruptsBefore + 1, self.InterruptCurrentMovementCalls);
    }

    [Fact]
    public void UseTime_BeforeDeadline_KeepsStick()
    {
        var (self, target, sticky) = Setup();
        self.CurTime = 100.0;
        sticky.StickTo(target.Id, 0.5f, 1.0f); // deadline 101.0

        self.CurTime = 100.9;
        sticky.UseTime();

        Assert.Equal(target.Id, sticky.TargetId);
    }

    [Fact]
    public void UseTime_PastDeadline_UnsticksAndInterrupts()
    {
        var (self, target, sticky) = Setup();
        self.CurTime = 100.0;
        sticky.StickTo(target.Id, 0.5f, 1.0f); // deadline 101.0

        self.CurTime = 101.0;
        sticky.UseTime();
        Assert.Equal(target.Id, sticky.TargetId);

        int interruptsBefore = self.InterruptCurrentMovementCalls;
        self.CurTime = 101.001;
        sticky.UseTime();

        Assert.Equal(0u, sticky.TargetId);
        Assert.Equal(interruptsBefore + 1, self.InterruptCurrentMovementCalls);
    }

    [Fact]
    public void ReStick_TearsDownPreviousBeforeSettingNew()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world);
        var a = new PhysicsObjHostStub(20u, world);
        var b = new PhysicsObjHostStub(21u, world);
        var sticky = new StickyManager(self);

        sticky.StickTo(a.Id, 0.5f, 1.0f);
        int interruptsBefore = self.InterruptCurrentMovementCalls;
        sticky.StickTo(b.Id, 0.5f, 1.0f);

        Assert.Equal(b.Id, sticky.TargetId);
        Assert.Equal(interruptsBefore + 1, self.InterruptCurrentMovementCalls); // old stick torn down
    }

    [Fact]
    public void AdjustOffset_NotInitialized_IsNoOp()
    {
        var (self, target, sticky) = Setup();
        sticky.StickTo(target.Id, 0.5f, 1.0f); // no HandleUpdateTarget → not initialized

        var frame = new MotionDeltaFrame { Origin = new Vector3(9f, 9f, 9f) };
        sticky.AdjustOffset(frame, 0.1);

        Assert.Equal(new Vector3(9f, 9f, 9f), frame.Origin); // untouched
    }

    [Fact]
    public void AdjustOffset_SteersTowardTarget_ClampedToStep()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world) { Radius = 0.5f, MinterpMaxSpeed = 1.0f };
        var target = new PhysicsObjHostStub(20u, world);
        var sticky = StuckAndInitialized(self, target, new Vector3(5f, 0f, 0f));

        var frame = new MotionDeltaFrame();
        sticky.AdjustOffset(frame, quantum: 0.1);

        // dir = +X (east); speed = 1.0 * 5 = 5; delta = 5 * 0.1 = 0.5 (< dist 3.7).
        Assert.Equal(0.5f, frame.Origin.X, 3);
        Assert.Equal(0f, frame.Origin.Y, 3);
        Assert.Equal(0f, frame.Origin.Z, 3); // horizontal-only
        Assert.Equal(90f, frame.GetHeading(), 1);
    }

    [Fact]
    public void AdjustOffset_TooClose_BacksOff_SignedDistance()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world) { Radius = 0.5f, MinterpMaxSpeed = 1.0f };
        var target = new PhysicsObjHostStub(20u, world);
        var sticky = StuckAndInitialized(self, target, new Vector3(0.9f, 0f, 0f));

        var frame = new MotionDeltaFrame();
        sticky.AdjustOffset(frame, quantum: 0.1);

        // delta = 5*0.1 = 0.5 >= |dist|=0.4 → delta = dist = -0.4; dir +X * -0.4 → back off (-X).
        Assert.Equal(-0.4f, frame.Origin.X, 3);
    }

    [Fact]
    public void AdjustOffset_DeepOverlap_BacksOff_RateLimited()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world) { Radius = 0.5f, MinterpMaxSpeed = 1.0f };
        var target = new PhysicsObjHostStub(20u, world);
        var sticky = StuckAndInitialized(self, target, new Vector3(0.1f, 0f, 0f));

        var frame = new MotionDeltaFrame();
        sticky.AdjustOffset(frame, quantum: 0.1);

        Assert.Equal(-0.5f, frame.Origin.X, 3);
    }

    [Fact]
    public void AdjustOffset_UsesCachedPosition_WhenTargetUnresolvable()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world) { Radius = 0.5f, MinterpMaxSpeed = 1.0f };
        var target = new PhysicsObjHostStub(20u, world);
        var sticky = new StickyManager(self);
        target.Resolvable = false;
        sticky.StickTo(target.Id, 0.5f, 1.0f);
        var tp = new Position(1u, new Vector3(4f, 0f, 0f), Quaternion.Identity);
        sticky.HandleUpdateTarget(new TargetInfo(target.Id, TargetStatus.Ok, tp, tp));

        var frame = new MotionDeltaFrame();
        sticky.AdjustOffset(frame, quantum: 0.1);

        Assert.Equal(0.5f, frame.Origin.X, 3);
    }

    [Fact]
    public void AdjustOffset_NoMinterp_UsesFallbackSpeed()
    {
        var world = new Dictionary<uint, PhysicsObjHostStub>();
        var self = new PhysicsObjHostStub(10u, world) { Radius = 0.5f, MinterpMaxSpeed = null };
        var target = new PhysicsObjHostStub(20u, world);
        var sticky = StuckAndInitialized(self, target, new Vector3(50f, 0f, 0f));

        var frame = new MotionDeltaFrame();
        sticky.AdjustOffset(frame, quantum: 0.1);

        Assert.Equal(1.5f, frame.Origin.X, 3);
    }
}
