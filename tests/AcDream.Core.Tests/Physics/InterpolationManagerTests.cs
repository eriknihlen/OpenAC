using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;


public sealed class InterpolationManagerTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Origin used as the "body is here" position in most tests.</summary>
    private static readonly Vector3 BodyOrigin = Vector3.Zero;

    private static readonly Vector3 FarTarget = new Vector3(10f, 0f, 0f);

    private static InterpolationManager Make() => new InterpolationManager();

    [Fact]
    public void AdjustOffset_CompleteFrame_ReplacesOriginAndNonCommutingOrientation()
    {
        var manager = Make();
        Quaternion current = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
        Quaternion target = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.9f);
        manager.Enqueue(
            new Vector3(10f, 0f, 0f),
            target,
            isMovingTo: false,
            currentBodyPosition: Vector3.Zero);
        var offset = new MotionDeltaFrame
        {
            Origin = new Vector3(7f, 8f, 9f),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f),
        };

        bool overwritten = manager.AdjustOffset(
            dt: 0.1,
            currentBodyPosition: Vector3.Zero,
            currentBodyOrientation: current,
            maxSpeedFromMinterp: 4f,
            offset);

        Assert.True(overwritten);
        Vector3 expectedLocalOrigin = MoveToMath.GlobalToLocalVec(
            current,
            new Vector3(0.8f, 0f, 0f));
        Assert.Equal(expectedLocalOrigin.X, offset.Origin.X, 5);
        Assert.Equal(expectedLocalOrigin.Y, offset.Origin.Y, 5);
        Assert.Equal(expectedLocalOrigin.Z, offset.Origin.Z, 5);
        Quaternion expectedOrientation = Quaternion.Normalize(
            Quaternion.Inverse(current) * target);
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                expectedOrientation,
                Quaternion.Normalize(offset.Orientation))),
            0.99999f,
            1.00001f);
    }

    [Fact]
    public void AdjustOffset_MoveToNode_KeepsCurrentHeading()
    {
        var manager = Make();
        manager.Enqueue(
            new Vector3(10f, 0f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.9f),
            isMovingTo: true,
            currentBodyPosition: Vector3.Zero);
        var offset = new MotionDeltaFrame
        {
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f),
        };

        Assert.True(manager.AdjustOffset(
            0.1,
            Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f),
            4f,
            offset));

        Assert.Equal(Quaternion.Identity, offset.Orientation);
    }

    [Fact]
    public void AdjustOffset_UsesManagerWideLatestKeepHeadingFlag()
    {
        var manager = Make();
        Quaternion current = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f);
        manager.Enqueue(
            new Vector3(10f, 0f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f),
            isMovingTo: false,
            currentBodyPosition: Vector3.Zero,
            currentBodyOrientation: current);
        manager.Enqueue(
            new Vector3(20f, 0f, 0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f),
            isMovingTo: true,
            currentBodyPosition: Vector3.Zero,
            currentBodyOrientation: current);
        var offset = new MotionDeltaFrame();

        Assert.True(manager.AdjustOffset(
            0.1,
            Vector3.Zero,
            current,
            4f,
            offset));

        Assert.Equal(Quaternion.Identity, offset.Orientation);
    }

    [Fact]
    public void AdjustOffset_WithoutContact_LeavesFrameAndQueueUntouched()
    {
        var manager = Make();
        manager.Enqueue(
            FarTarget,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f),
            isMovingTo: false,
            currentBodyPosition: BodyOrigin);
        var offset = new MotionDeltaFrame
        {
            Origin = new Vector3(1f, 2f, 3f),
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f),
        };
        Vector3 originalOrigin = offset.Origin;
        Quaternion originalOrientation = offset.Orientation;

        bool overwritten = manager.AdjustOffset(
            0.1,
            BodyOrigin,
            Quaternion.Identity,
            4f,
            offset,
            inContact: false);

        Assert.False(overwritten);
        Assert.Equal(originalOrigin, offset.Origin);
        Assert.Equal(originalOrientation, offset.Orientation);
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Enqueue_AlreadyClose_InstallsOnlyTargetHeading()
    {
        var manager = Make();
        Quaternion target = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.6f));

        Quaternion? immediate = manager.Enqueue(
            new Vector3(0.01f, 0f, 0f),
            target,
            isMovingTo: false,
            currentBodyPosition: Vector3.Zero);

        Assert.NotNull(immediate);
        Quaternion expected = MoveToMath.SetHeading(
            target,
            MoveToMath.GetHeading(target));
        Assert.InRange(
            MathF.Abs(Quaternion.Dot(
                Quaternion.Normalize(expected),
                Quaternion.Normalize(immediate!.Value))),
            0.99999f,
            1.00001f);
        Assert.True(MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(target),
            Quaternion.Normalize(immediate.Value))) < 0.999f);
        Assert.False(manager.IsActive);
    }

    // =========================================================================
    // Queue mechanics
    // =========================================================================

    [Fact]
    public void Enqueue_AddsNode_QueueBecomesActive()
    {
        var mgr = Make();
        Assert.False(mgr.IsActive);

        mgr.Enqueue(FarTarget, heading: 0f, isMovingTo: true);

        Assert.True(mgr.IsActive);
    }

    [Fact]
    public void Enqueue_DropsOldestWhenAtCap20()
    {
        var mgr = Make();

        // Fill the queue to cap with distinct positions spaced far enough
        // apart to avoid the duplicate-prune threshold (DesiredDistance = 0.05).
        for (int i = 0; i < InterpolationManager.QueueCap; i++)
        {
            mgr.Enqueue(new Vector3(i * 1f, 0f, 0f), heading: 0f, isMovingTo: true);
        }

        // Sanity: queue is at cap before the 21st enqueue.
        Assert.Equal(InterpolationManager.QueueCap, mgr.Count);

        mgr.Enqueue(new Vector3(100f, 0f, 0f), heading: 0f, isMovingTo: true);

        Assert.Equal(InterpolationManager.QueueCap, mgr.Count);

        var bodyAtSecondOriginal = new Vector3(1f, 0f, 0f);
        var result = mgr.AdjustOffset(
            dt: 0.016,
            currentBodyPosition: bodyAtSecondOriginal,
            maxSpeedFromMinterp: 10f);

        // Reached head (dist ≈ 0) → zero delta + node popped.
        Assert.Equal(Vector3.Zero, result);
        Assert.Equal(InterpolationManager.QueueCap - 1, mgr.Count);
    }

    [Fact]
    public void Enqueue_AtCap20_HeadIsSecondOriginal()
    {
        var mgr = Make();
        for (int i = 0; i < InterpolationManager.QueueCap; i++)
        {
            mgr.Enqueue(new Vector3(i * 1f, 0f, 0f), heading: 0f, isMovingTo: true);
        }
        mgr.Enqueue(new Vector3(100f, 0f, 0f), heading: 0f, isMovingTo: true);

        // Place the body far away from x=0 but RIGHT on x=1.  If x=0 were the
        // head the result would be non-zero (body is 1 m away from x=0).
        // If x=1 is the head the distance is 0 → pop → zero return.
        var bodyAtX1 = new Vector3(1f, 0f, 0f);
        var delta = mgr.AdjustOffset(dt: 0.016, currentBodyPosition: bodyAtX1, maxSpeedFromMinterp: 10f);

        Assert.Equal(Vector3.Zero, delta);
    }

    [Fact]
    public void Enqueue_PrunesDuplicateWithinDesiredDistance()
    {
        var mgr = Make();
        var basePos = new Vector3(5f, 0f, 0f);

        mgr.Enqueue(basePos, heading: 0f, isMovingTo: true);

        // Within DesiredDistance (0.05) — must be ignored.
        var nearDuplicate = basePos + new Vector3(0.01f, 0f, 0f);
        mgr.Enqueue(nearDuplicate, heading: 0f, isMovingTo: true);

        var result = mgr.AdjustOffset(dt: 0.016, currentBodyPosition: basePos, maxSpeedFromMinterp: 10f);

        Assert.Equal(Vector3.Zero, result);         // reached → pop
        Assert.False(mgr.IsActive);                 // only one node existed
    }

    [Fact]
    public void Clear_EmptiesQueueAndResetsCounters()
    {
        var mgr = Make();
        mgr.Enqueue(FarTarget, heading: 0f, isMovingTo: true);
        Assert.True(mgr.IsActive);

        mgr.Clear();

        Assert.False(mgr.IsActive);

        var delta = mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        Assert.Equal(Vector3.Zero, delta);
    }

    // =========================================================================
    // AdjustOffset math
    // =========================================================================

    [Fact]
    public void AdjustOffset_EmptyQueue_ReturnsZero()
    {
        var mgr   = Make();
        var delta = mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);

        Assert.Equal(Vector3.Zero, delta);
    }

    [Fact]
    public void AdjustOffset_ReachesNodeWithinDesiredDistance_PopsHead()
    {
        var mgr    = Make();
        var target = new Vector3(0.02f, 0f, 0f); // within DesiredDistance (0.05)

        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        // Body is at origin; distance = 0.02 < 0.05 → should pop and return zero.
        var delta = mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);

        Assert.Equal(Vector3.Zero, delta);
        Assert.False(mgr.IsActive, "Head node should have been popped after being reached");
    }

    [Fact]
    public void AdjustOffset_ClampedToCatchUpSpeed_2xMotionMax()
    {
        var mgr       = Make();
        float maxSpeed = 4.0f;          // motion-table max speed
        double dt      = 0.5;
        var target = new Vector3(100f, 0f, 0f);
        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        var delta = mgr.AdjustOffset(dt, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: maxSpeed);

        float expectedStep = maxSpeed * InterpolationManager.MaxInterpolatedVelocityMod * (float)dt;
        Assert.Equal(expectedStep, delta.Length(), precision: 4);
    }

    [Fact]
    public void AdjustOffset_FallbackSpeed_WhenMinterpZero()
    {
        var mgr   = Make();
        double dt = 0.5;
        var target = new Vector3(100f, 0f, 0f);
        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        // maxSpeedFromMinterp = 0 → fallback to MaxInterpolatedVelocity (7.5)
        var delta = mgr.AdjustOffset(dt, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 0f);

        float expectedStep = InterpolationManager.MaxInterpolatedVelocity * (float)dt;
        Assert.Equal(expectedStep, delta.Length(), precision: 4);
    }

    [Fact]
    public void AdjustOffset_OvershootProtection_StepClampedToDistance()
    {
        var mgr   = Make();
        float maxSpeed = 10f;
        double dt      = 1.0;       // step = 2*10*1.0 = 20 >> actual distance

        // Place target just 0.5 m away — inside the step distance.
        var target = new Vector3(0.5f, 0f, 0f);
        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        var delta = mgr.AdjustOffset(dt, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: maxSpeed);

        Assert.Equal(0.5f, delta.Length(), precision: 4);
    }

    // =========================================================================
    // Stall detection
    // =========================================================================

    [Fact]
    public void AdjustOffset_StallCounterIncrementsEachFrame()
    {
        // Run 4 frames (< StallCheckFrameInterval = 5) with a body that does
        // not move — the queue should still be active (no blip yet).
        var mgr    = Make();
        var target = new Vector3(10f, 0f, 0f);
        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        // Body does NOT move — we pass the same fixed position each frame.
        for (int i = 0; i < 4; i++)
        {
            mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        }

        Assert.True(mgr.IsActive);
    }

    [Fact]
    public void AdjustOffset_FirstWindowInitializesProgressBaseline()
    {
        var mgr    = Make();
        var target = new Vector3(50f, 0f, 0f);
        mgr.Enqueue(target, heading: 0f, isMovingTo: true);

        for (int i = 0; i < InterpolationManager.StallCheckFrameInterval; i++)
        {
            mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        }

        // The sentinel baseline keeps the first window from recording a failure.
        Assert.True(mgr.IsActive);
    }

    // =========================================================================
    // New tests: I-1 first-window false-positive guard, I-3 dt guard, I-5 cap
    // =========================================================================

    [Fact]
    public void AdjustOffset_FirstWindow_DoesNotFalseFail()
    {
        var mgr = Make();
        mgr.Enqueue(new Vector3(50f, 0f, 0f), heading: 0f, isMovingTo: true);

        for (int i = 0; i < InterpolationManager.StallCheckFrameInterval; i++)
        {
            mgr.AdjustOffset(dt: 0.016, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        }

        // The first window establishes the baseline without recording a failure.
        // Queue must still be active; no spurious blip on first window.
        Assert.True(mgr.IsActive,
            "First window should establish the progress baseline.");
    }


    [Fact]
    public void AdjustOffset_DtZeroOrNegative_ReturnsZero()
    {
        var mgr = Make();
        mgr.Enqueue(FarTarget, heading: 0f, isMovingTo: true);

        // dt == 0 → guard fires, return zero, no side-effects.
        var deltaZero = mgr.AdjustOffset(dt: 0.0, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        Assert.Equal(Vector3.Zero, deltaZero);

        // dt < 0 → guard fires, return zero.
        var deltaNeg = mgr.AdjustOffset(dt: -1.0, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        Assert.Equal(Vector3.Zero, deltaNeg);

        // dt = NaN → guard fires, return zero.
        var deltaNaN = mgr.AdjustOffset(dt: double.NaN, currentBodyPosition: BodyOrigin, maxSpeedFromMinterp: 4f);
        Assert.Equal(Vector3.Zero, deltaNaN);

        Assert.True(mgr.IsActive);
    }
}
