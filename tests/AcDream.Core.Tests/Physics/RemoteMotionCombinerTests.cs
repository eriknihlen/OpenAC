using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;


public sealed class RemoteMotionCombinerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static RemoteMotionCombiner Make() => new();

    private static InterpolationManager EmptyInterp() => new();

    // =========================================================================
    // Test 1: stationary remote — both sources zero, no motion
    // =========================================================================

    [Fact]
    public void ComputeOffset_StationaryRemote_BothSourcesZero_NoMotion()
    {
        var pm    = Make();
        var interp = EmptyInterp();

        Vector3 offset = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: Vector3.Zero,
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 4f);

        Assert.Equal(Vector3.Zero, offset);
    }

    // =========================================================================
    // Test 2: animation only, identity orientation, forward velocity
    // =========================================================================

    [Fact]
    public void ComputeOffset_AnimationOnly_Forward_BodyAdvances()
    {
        var pm     = Make();
        var interp = EmptyInterp();

        Vector3 offset = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: new Vector3(0f, 0.4f, 0f),
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 0f);

        Assert.Equal(0f,   offset.X, precision: 4);
        Assert.Equal(0.4f, offset.Y, precision: 4);
        Assert.Equal(0f,   offset.Z, precision: 4);
    }

    // =========================================================================
    // Test 3: animation only, 180° yaw around Z — body moves south (-Y)
    // =========================================================================

    [Fact]
    public void ComputeOffset_AnimationOnly_OrientedSouth_BodyMovesSouth()
    {
        var pm     = Make();
        var interp = EmptyInterp();

        // 180° around Z flips +Y → -Y
        Quaternion ori = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        Vector3 offset = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: new Vector3(0f, 0.4f, 0f),
            ori: ori,
            interp: interp,
            maxSpeed: 0f);

        Assert.Equal(0f,    offset.X, precision: 4);
        Assert.Equal(-0.4f, offset.Y, precision: 4);
    }


    [Fact]
    public void ComputeOffset_InterpOnly_NoAnimation_BodyChasesQueue()
    {
        var pm     = Make();
        var interp = new InterpolationManager();

        // Enqueue target 1m ahead on +X; body starts at origin
        interp.Enqueue(new Vector3(1f, 0f, 0f), heading: 0f, isMovingTo: false);

        Vector3 offset = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: Vector3.Zero,
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 4f);

        Assert.Equal(0.8f, offset.X, precision: 3);
        Assert.Equal(0f,   offset.Y, precision: 3);
        Assert.Equal(0f,   offset.Z, precision: 3);
    }


    [Fact]
    public void ComputeOffset_BothActive_CorrectionReplacesRootMotion()
    {
        var pm     = Make();
        var interp = new InterpolationManager();

        // Enqueue target 1m ahead on +X
        interp.Enqueue(new Vector3(1f, 0f, 0f), heading: 0f, isMovingTo: false);

        Vector3 offset = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: new Vector3(0f, 0.4f, 0f),
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 4f);

        Assert.Equal(0.8f, offset.X, precision: 3);
        Assert.Equal(0f,   offset.Y, precision: 3);
        Assert.Equal(0f,   offset.Z, precision: 3);
    }

    // =========================================================================
    // Test 6: local-to-world rotation — +90° yaw around Z
    // =========================================================================

    [Fact]
    public void ComputeOffset_LocalToWorldRotation_Yaw90()
    {
        var pm     = Make();
        var interp = EmptyInterp();

        Quaternion ori = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        Vector3 offset = pm.ComputeOffset(
            dt: 1.0,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: new Vector3(0f, 1f, 0f),
            ori: ori,
            interp: interp,
            maxSpeed: 0f);

        Assert.Equal(-1f, offset.X, precision: 4);
        Assert.Equal(0f,  offset.Y, precision: 4);
        Assert.Equal(0f,  offset.Z, precision: 4);
    }


    [Fact]
    public void ComputeOffset_QueueHeadReached_WithLiteralZeroRootMotion_DoesNotOvershoot()
    {
        var pm = Make();
        var interp = new InterpolationManager();
        var target = new Vector3(0.4f, 0f, 0f);
        interp.Enqueue(target, heading: 0f, isMovingTo: false);

        Vector3 catchUp = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            rootMotionLocalDelta: Vector3.Zero,
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 4f);
        Vector3 reachedPosition = catchUp;

        Vector3 afterReach = pm.ComputeOffset(
            dt: 0.1,
            currentBodyPosition: reachedPosition,
            rootMotionLocalDelta: Vector3.Zero,
            ori: Quaternion.Identity,
            interp: interp,
            maxSpeed: 4f);

        Assert.Equal(target, reachedPosition);
        Assert.Equal(Vector3.Zero, afterReach);
    }
}
