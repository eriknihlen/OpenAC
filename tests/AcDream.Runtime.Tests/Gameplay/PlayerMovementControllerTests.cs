using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Chat;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Tests.Gameplay;

public class PlayerMovementControllerTests
{
    private static float ObjectTick => PhysicsBody.MinQuantum + 0.001f;

    private static PhysicsEngine MakeFlatEngine()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;
        var terrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(0xA9B4FFFFu, terrain, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);
        return engine;
    }

    [Fact]
    public void PerformMovement_ReactivatesCanonicalClock_ExceptForStaticObject()
    {
        var clock = new RetailObjectQuantumClock();
        var controller = new PlayerMovementController(MakeFlatEngine(), clock);
        clock.Deactivate();
        controller.ApplyPhysicsState(PhysicsStateFlags.None);

        controller.Movement.PerformMovement(new MovementStruct
        {
            Type = (MovementType)99,
        });
        Assert.True(clock.IsActive);

        clock.Deactivate();
        controller.ApplyPhysicsState(PhysicsStateFlags.Static);
        controller.Movement.PerformMovement(new MovementStruct
        {
            Type = (MovementType)99,
        });
        Assert.False(clock.IsActive);
    }

    [Fact]
    public void Update_NoInput_PositionUnchanged()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        var result = controller.Update(0.016f, new MovementInput());

        Assert.Equal(96f, result.Position.X, precision: 1);
        Assert.Equal(96f, result.Position.Y, precision: 1);
    }

    [Fact]
    public void Update_AtRestNoInput_RenderPositionBitStableAcrossManyFrames()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        var rest = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(rest, 0x0001, rest);

        var settled = controller.Update(1f / 60f, new MovementInput());
        Vector3 baselineRender = settled.RenderPosition;
        Vector3 baselinePhysics = settled.Position;

        float maxRenderDev = 0f;
        float maxPhysicsDev = 0f;
        for (int i = 0; i < 600; i++)
        {
            var r = controller.Update(1f / 60f, new MovementInput());
            maxRenderDev = MathF.Max(maxRenderDev, (r.RenderPosition - baselineRender).Length());
            maxPhysicsDev = MathF.Max(maxPhysicsDev, (r.Position - baselinePhysics).Length());
        }

        Assert.True(
            maxRenderDev == 0f && maxPhysicsDev == 0f,
            $"resting body drifted: render={maxRenderDev * 1e6f:F3} µm, " +
            $"physics={maxPhysicsDev * 1e6f:F3} µm; expected byte-identical rest");
    }

    [Fact]
    public void Update_WalkThenStop_SettlesToBitStableRest()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;

        // Walk forward ~0.5 s, then release.
        for (int i = 0; i < 30; i++)
            controller.Update(1f / 60f, new MovementInput(Forward: true));
        // Let velocity decay / state settle.
        for (int i = 0; i < 30; i++)
            controller.Update(1f / 60f, new MovementInput());

        var settled = controller.Update(1f / 60f, new MovementInput());
        Vector3 basePos = settled.Position;
        Vector3 baseRender = settled.RenderPosition;

        float maxPos = 0f, maxRender = 0f;
        for (int i = 0; i < 600; i++)
        {
            var r = controller.Update(1f / 60f, new MovementInput());
            maxPos = MathF.Max(maxPos, (r.Position - basePos).Length());
            maxRender = MathF.Max(maxRender, (r.RenderPosition - baseRender).Length());
        }

        Assert.True(maxPos == 0f && maxRender == 0f,
            $"post-walk rest drifted: pos={maxPos * 1e6f:F3} µm, render={maxRender * 1e6f:F3} µm");
    }

    [Fact]
    public void Update_ForwardInput_MovesInFacingDirection()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;  // facing +X

        var input = new MovementInput { Forward = true };
        MovementResult result = default;
        int ticks = (int)MathF.Ceiling(1.0f / PhysicsBody.MaxQuantum) + 1;  // ~11 ticks
        for (int i = 0; i < ticks; i++)
            result = controller.Update(PhysicsBody.MaxQuantum, input);

        // Should have moved >2 units in +X (walk speed over ~1s).
        Assert.True(result.Position.X > 96f + 2f, $"X={result.Position.X} should have moved forward");
    }

    [Fact]
    public void Update_AttachedAnimationWithZeroRootDelta_DoesNotGlideOnForwardEdge()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;
        controller.AttachAnimationRootMotionSource((_, _) => { });

        MovementResult result = controller.Update(
            ObjectTick,
            new MovementInput(Forward: true));

        Assert.Equal(start, result.Position);
        Assert.Equal(0f, controller.BodyVelocity.X);
        Assert.Equal(0f, controller.BodyVelocity.Y);
    }

    [Fact]
    public void Update_AttachedAnimationRootDelta_DrivesGroundedBodyAtObjectScale()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;
        controller.ObjectScale = 2f;
        controller.AttachAnimationRootMotionSource((_, frame) =>
            frame.Origin = new Vector3(0f, 0.1f, 0f));

        MovementResult result = controller.Update(
            ObjectTick,
            new MovementInput(Forward: true));

        // Local +Y is forward. Yaw 0 maps it to world +X; m_scale doubles
        // the animation-authored 0.1 m displacement to 0.2 m.
        Assert.Equal(start.X + 0.2f, result.Position.X, precision: 3);
        Assert.Equal(start.Y, result.Position.Y, precision: 3);
    }

    [Fact]
    public void Update_SubQuantumFrames_AdvanceAnimationOnceAtObjectThreshold()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;
        int advances = 0;
        controller.AttachAnimationRootMotionSource((_, frame) =>
        {
            advances++;
            frame.Origin = new Vector3(0f, 0.1f, 0f);
        });

        MovementResult first = controller.Update(
            ObjectTick * 0.5f,
            new MovementInput(Forward: true));
        MovementResult second = controller.Update(
            ObjectTick * 0.5f,
            new MovementInput(Forward: true));

        Assert.Equal(start, first.Position);
        Assert.Equal(start.X + 0.1f, second.Position.X, precision: 3);
        Assert.Equal(start.Y, second.Position.Y, precision: 3);
        Assert.Equal(1, advances);
    }

    [Fact]
    public void Update_AttachedAnimationFrame_ComposesTranslationBeforeTurn()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f; // body local +Y faces world +X
        controller.AttachAnimationRootMotionSource((_, frame) =>
        {
            frame.Origin = new Vector3(0f, 0.1f, 0f);
            frame.Orientation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                MathF.PI / 2f);
        });

        MovementResult result = controller.Update(
            ObjectTick,
            new MovementInput());

        Assert.Equal(start.X + 0.1f, result.Position.X, precision: 3);
        Assert.Equal(start.Y, result.Position.Y, precision: 3);
        Assert.Equal(MathF.PI / 2f, controller.Yaw, precision: 3);
    }

    [Fact]
    public void Update_AttachedAnimationFrame_PreservesCompleteNonCommutingOrientation()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        Quaternion initial = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.1f);
        Quaternion delta = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.9f);
        controller.SetBodyOrientation(initial);
        controller.AttachAnimationRootMotionSource((_, frame) =>
            frame.Orientation = delta);

        controller.Update(ObjectTick, new MovementInput());

        Quaternion expected = Quaternion.Normalize(initial * delta);
        float alignment = MathF.Abs(Quaternion.Dot(
            expected,
            Quaternion.Normalize(controller.BodyOrientation)));
        Assert.InRange(alignment, 0.99999f, 1.00001f);
        Assert.True(MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(delta * initial),
            Quaternion.Normalize(controller.BodyOrientation))) < 0.999f);
    }

    [Fact]
    public void Update_MultiQuantumAnimationFrames_ComposeInTemporalOrder()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;
        int sample = 0;
        controller.AttachAnimationRootMotionSource((_, frame) =>
        {
            if (sample++ == 0)
            {
                frame.Orientation = Quaternion.CreateFromAxisAngle(
                    Vector3.UnitZ,
                    MathF.PI / 2f);
            }
            else
            {
                frame.Origin = new Vector3(0f, 0.1f, 0f);
            }
        });

        MovementResult result = controller.Update(
            PhysicsBody.MaxQuantum * 2f,
            new MovementInput());

        // The second local-forward displacement occurs after the first
        // quarter-turn, so it moves north. Adding Origins as bare vectors
        // would incorrectly move east.
        Assert.Equal(start.X, result.Position.X, precision: 3);
        Assert.Equal(start.Y + 0.1f, result.Position.Y, precision: 3);
        Assert.Equal(MathF.PI / 2f, controller.Yaw, precision: 3);
    }

    [Fact]
    public void Update_ExactMinQuantum_RetainsTimeWithoutAdvancingObject()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        int advances = 0;
        controller.AttachAnimationRootMotionSource((_, frame) =>
        {
            advances++;
            frame.Origin = new Vector3(0f, 1f, 0f);
        });

        MovementResult atThreshold = controller.Update(
            PhysicsBody.MinQuantum,
            new MovementInput());

        Assert.Equal(start, atThreshold.Position);
        Assert.Equal(0, advances);

        controller.Update(0.001f, new MovementInput());
        Assert.Equal(1, advances);
    }

    [Fact]
    public void Update_LargeFrame_MatchesSeparateRetailObjectQuanta()
    {
        var combined = new PlayerMovementController(MakeFlatEngine());
        var split = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        combined.SeedPlacementForTest(start, 0x0001, start);
        split.SeedPlacementForTest(start, 0x0001, start);
        int combinedHooks = 0;
        int splitHooks = 0;

        static void Advance(float dt, AcDream.Core.Physics.Motion.MotionDeltaFrame frame)
        {
            frame.Origin = new Vector3(0f, dt * 2f, 0f);
            frame.Orientation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                dt * 0.7f);
        }

        combined.AttachAnimationRootMotionSource(Advance, () => combinedHooks++);
        split.AttachAnimationRootMotionSource(Advance, () => splitHooks++);

        combined.Update(PhysicsBody.MaxQuantum * 2f, new MovementInput());
        split.Update(PhysicsBody.MaxQuantum, new MovementInput());
        split.Update(PhysicsBody.MaxQuantum, new MovementInput());

        Assert.Equal(split.Position.X, combined.Position.X, precision: 5);
        Assert.Equal(split.Position.Y, combined.Position.Y, precision: 5);
        Assert.Equal(split.Position.Z, combined.Position.Z, precision: 5);
        float alignment = MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(split.BodyOrientation),
            Quaternion.Normalize(combined.BodyOrientation)));
        Assert.InRange(alignment, 0.99999f, 1.00001f);
        Assert.Equal(2, combinedHooks);
        Assert.Equal(2, splitHooks);
    }

    [Fact]
    public void TickHidden_DoesNotAdvancePartArrayButStillProcessesHooks()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        int advances = 0;
        int hookPasses = 0;
        controller.AttachAnimationRootMotionSource(
            (_, frame) =>
            {
                advances++;
                frame.Origin = new Vector3(0f, 1f, 0f);
            },
            () => hookPasses++);

        controller.TickHidden(ObjectTick);

        Assert.Equal(0, advances);
        Assert.Equal(1, hookPasses);
    }

    [Fact]
    public void InvalidElapsed_VisibleFrameIsPurePresentationRead()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        float initialTime = controller.SimTimeSeconds;
        float initialYaw = controller.Yaw;
        Vector3 initialPosition = controller.Position;
        RawMotionState initialMotion = controller.Motion.RawState;
        var hostileInput = new MovementInput(
            Forward: true,
            TurnLeft: true,
            Jump: true,
            Run: true);

        foreach (float elapsed in new[]
                 {
                     float.NaN,
                     float.PositiveInfinity,
                     float.NegativeInfinity,
                     -0.1f,
                     0f,
                 })
        {
            MovementResult result = controller.Update(elapsed, hostileInput);
            Assert.False(result.ShouldSendMovementEvent);
        }

        Assert.Equal(initialTime, controller.SimTimeSeconds);
        Assert.Equal(initialYaw, controller.Yaw);
        Assert.Equal(initialPosition, controller.Position);
        Assert.Equal(initialMotion, controller.Motion.RawState);

        controller.Update(ObjectTick, new MovementInput());
        controller.Update(ObjectTick, new MovementInput());
        Assert.True(controller.AdvancedObjectQuantumLastTick);
        controller.Update(float.NaN, hostileInput);
        Assert.False(controller.AdvancedObjectQuantumLastTick);
    }

    [Fact]
    public void InvalidElapsed_HiddenFrameDoesNotAdvanceClockOrManagerTail()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        float initialTime = controller.SimTimeSeconds;
        int targetPasses = 0;

        controller.TickHidden(float.NaN, () => targetPasses++);
        controller.TickHidden(float.PositiveInfinity, () => targetPasses++);
        controller.TickHidden(-0.1f, () => targetPasses++);

        Assert.Equal(initialTime, controller.SimTimeSeconds);
        Assert.Equal(0, targetPasses);
        Assert.False(controller.AdvancedObjectQuantumLastTick);

        controller.TickHidden(ObjectTick);
        controller.TickHidden(ObjectTick);
        Assert.True(controller.AdvancedObjectQuantumLastTick);
        controller.TickHidden(float.PositiveInfinity);
        Assert.False(controller.AdvancedObjectQuantumLastTick);
    }

    [Fact]
    public void Update_AirbornePartArrayFrame_SuppressesOriginButPreservesOrientation()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Update(1f, new MovementInput(Jump: true));

        Vector3 beforeRelease = controller.Position;
        Quaternion beforeOrientation = controller.BodyOrientation;
        Quaternion delta = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        int advances = 0;
        controller.AttachAnimationRootMotionSource((_, frame) =>
        {
            advances++;
            frame.Origin = new Vector3(0f, 10f, 0f);
            frame.Orientation = delta;
        });

        controller.Update(ObjectTick, new MovementInput(Jump: false));

        Assert.True(controller.IsAirborne);
        Assert.Equal(beforeRelease.X, controller.Position.X, precision: 5);
        Assert.Equal(beforeRelease.Y, controller.Position.Y, precision: 5);
        Quaternion expected = Quaternion.Normalize(beforeOrientation * delta);
        float alignment = MathF.Abs(Quaternion.Dot(
            expected,
            Quaternion.Normalize(controller.BodyOrientation)));
        Assert.InRange(alignment, 0.99999f, 1.00001f);
        Assert.Equal(1, advances);
    }

    [Fact]
    public void Update_AttachedAnimationTurn_IsNotAppliedByASecondYawIntegrator()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;
        controller.AttachAnimationRootMotionSource((dt, frame) =>
        {
            frame.Orientation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitZ,
                -(MathF.PI / 2f) * dt);
        });

        controller.Update(
            ObjectTick,
            new MovementInput(TurnRight: true));

        Assert.Equal(
            -(MathF.PI / 2f) * ObjectTick,
            controller.Yaw,
            precision: 4);
    }

    [Fact]
    public void Update_SubQuantumFrame_InterpolatesRenderPositionWithoutAdvancingPhysicsPosition()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;

        var firstTick = controller.Update(ObjectTick, new MovementInput(Forward: true));
        Assert.True(firstTick.Position.X > start.X, "Physics tick should advance the authoritative body position");
        Assert.Equal(start.X, firstTick.RenderPosition.X, precision: 4);

        var halfFrame = controller.Update(PhysicsBody.MinQuantum * 0.5f, new MovementInput(Forward: true));

        Assert.Equal(firstTick.Position.X, halfFrame.Position.X, precision: 4);
        Assert.True(halfFrame.RenderPosition.X > start.X, "Render position should move between physics ticks");
        Assert.True(halfFrame.RenderPosition.X < firstTick.Position.X,
            $"Render X={halfFrame.RenderPosition.X} should stay between {start.X} and {firstTick.Position.X}");

        float alpha = (PhysicsBody.MinQuantum * 0.5f) / ObjectTick;
        float expected = start.X + ((firstTick.Position.X - start.X) * alpha);
        Assert.Equal(expected, halfFrame.RenderPosition.X, precision: 3);
    }

    [Fact]
    public void SetPosition_ResnapsRenderInterpolationEndpoints()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;

        controller.Update(ObjectTick, new MovementInput(Forward: true));
        controller.Update(PhysicsBody.MinQuantum * 0.5f, new MovementInput(Forward: true));

        var snapped = new Vector3(120f, 80f, 50f);
        controller.SeedPlacementForTest(snapped, 0x0001, snapped);
        var result = controller.Update(PhysicsBody.MinQuantum * 0.5f, new MovementInput());

        Assert.Equal(snapped, result.Position);
        Assert.Equal(snapped, result.RenderPosition);
    }

    [Fact]
    public void CommitCanonicalForcePositionFrame_ReconcilesPoseWithoutStoppingActiveMotion()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;
        controller.Update(ObjectTick, new MovementInput(Forward: true));
        Vector3 velocity = controller.BodyVelocity;
        Assert.True(velocity.LengthSquared() > 0f);

        var corrected = new Vector3(100f, 98f, 50f);
        controller.PhysicsBody.SnapToCell(0x0001, corrected, corrected);
        controller.CommitCanonicalForcePositionFrame();

        Assert.Equal(corrected, controller.Position);
        Assert.Equal(corrected, controller.RenderPosition);
        Assert.Equal(velocity, controller.BodyVelocity);
    }

    [Fact]
    public void CommitCanonicalForcePositionFrame_PublishesCanonicalOutdoorCellAndLocalFrame()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var world = new Vector3(150f, 193f, 50f);
        var wireLocal = new Vector3(150f, 193f, 50f);

        controller.PhysicsBody.SnapToCell(0xA9B30038u, world, wireLocal);
        controller.CommitCanonicalForcePositionFrame();

        Assert.Equal(0xA9B40031u, controller.CellId);
        Assert.Equal(controller.CellId, controller.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(150f, 1f, 50f), controller.CellPosition.Frame.Origin);
        Assert.Equal(world, controller.Position);
    }

    [Fact]
    public void TeleportPosition_PublishesCanonicalOutdoorCellAndLocalFrame()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var world = new Vector3(12f, 12f, 50f);
        var wireLocal = new Vector3(12f, 12f, 50f);

        controller.SeedPlacementForTest(world, 0xA9B40031u, wireLocal);

        Assert.Equal(0xA9B40001u, controller.CellId);
        Assert.Equal(controller.CellId, controller.CellPosition.ObjCellId);
        Assert.Equal(wireLocal, controller.CellPosition.Frame.Origin);
    }

    [Fact]
    public void Update_HugeQuantumDiscard_ResnapsRenderInterpolationEndpoints()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;
        int animationAdvances = 0;
        int hookPasses = 0;
        controller.AttachAnimationRootMotionSource(
            (_, frame) =>
            {
                animationAdvances++;
                frame.Origin = new Vector3(0f, 0.1f, 0f);
            },
            () => hookPasses++);

        var moved = controller.Update(ObjectTick, new MovementInput(Forward: true));
        int advancesBeforeDiscard = animationAdvances;
        int hooksBeforeDiscard = hookPasses;
        var stale = controller.Update(PhysicsBody.HugeQuantum + 0.1f, new MovementInput(Forward: true));

        Assert.Equal(moved.Position.X, stale.Position.X, precision: 4);
        Assert.Equal(stale.Position, stale.RenderPosition);
        Assert.Equal(advancesBeforeDiscard, animationAdvances);
        Assert.Equal(hooksBeforeDiscard, hookPasses);
    }

    [Fact]
    public void Update_LeftoverAboveMinQuantum_InterpolatesAcrossTheActualQuantumInterval()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;

        var result = controller.Update(
            PhysicsBody.MaxQuantum + PhysicsBody.MinQuantum,
            new MovementInput(Forward: true));

        float alpha = PhysicsBody.MinQuantum / PhysicsBody.MaxQuantum;
        Vector3 expected = Vector3.Lerp(start, result.Position, alpha);
        Assert.Equal(expected.X, result.RenderPosition.X, tolerance: 1e-3f);
        Assert.Equal(expected.Y, result.RenderPosition.Y, tolerance: 1e-3f);
        Assert.Equal(expected.Z, result.RenderPosition.Z, tolerance: 1e-3f);
    }

    [Fact]
    public void Update_RunForward_MoveFasterThanWalk()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;

        var walkInput = new MovementInput { Forward = true };
        var walkResult = controller.Update(1.0f, walkInput);
        float walkDist = walkResult.Position.X - 96f;

        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        var runInput = new MovementInput { Forward = true, Run = true };
        var runResult = controller.Update(1.0f, runInput);
        float runDist = runResult.Position.X - 96f;

        Assert.True(runDist > walkDist, $"Run ({runDist}) should be faster than walk ({walkDist})");
    }

    [Fact]
    public void Update_TurnInput_ChangesYaw()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        float initialYaw = controller.Yaw;

        var input = new MovementInput { TurnRight = true };
        controller.Update(0.5f, input);

        Assert.NotEqual(initialYaw, controller.Yaw);
    }

    [Fact]
    public void MotionStateChanged_WhenStartingToWalk()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        // First frame: idle (no input).
        controller.Update(0.016f, new MovementInput());

        // Second frame: start walking.
        var input = new MovementInput { Forward = true };
        var result = controller.Update(0.016f, input);

        Assert.True(result.MotionStateChanged);
    }

    [Fact]
    public void Update_JumpOnFlatTerrain_BecomesAirborne()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false)); // release → jump fires

        Assert.True(controller.IsAirborne);
        Assert.True(controller.VerticalVelocity > 0f);
    }

    [Fact]
    public void PositionEventGateRequiresContactAndWalkable()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        Assert.True(controller.CanSendPositionEvent);

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false));

        Assert.True(controller.IsAirborne);
        Assert.False(controller.CanSendPositionEvent);
    }

    [Fact]
    public void JumpChargeSnapshot_TracksHeldChargeAndResetsOnRelease()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        Assert.Equal(default, controller.JumpCharge);

        controller.Update(0.25f, new MovementInput(Jump: true));
        Assert.True(controller.JumpCharge.IsCharging);
        Assert.Equal(0.25f, controller.JumpCharge.Power, precision: 3);

        controller.Update(0.016f, new MovementInput(Jump: false));
        Assert.False(controller.JumpCharge.IsCharging);
        Assert.Equal(0f, controller.JumpCharge.Power);
    }

    [Fact]
    public void Update_AirborneFrames_ZRiseThenFalls()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false)); // release → jump fires
        float z1 = controller.Position.Z;

        // A few frames of rising
        controller.Update(0.1f, new MovementInput());
        float z2 = controller.Position.Z;
        Assert.True(z2 > z1, "Should be rising");

        for (int i = 0; i < 50; i++)
            controller.Update(0.05f, new MovementInput());

        Assert.False(controller.IsAirborne, "Should have landed");
        Assert.Equal(50f, controller.Position.Z, precision: 1);
    }

    [Fact]
    public void Update_WalkOffLedge_BecomesFalling()
    {
        var heights = new byte[81];
        for (int x = 0; x < 9; x++)
            for (int y = 0; y < 9; y++)
                heights[x * 9 + y] = (byte)(x < 5 ? 50 : 20);

        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = i * 1f;

        var engine = new PhysicsEngine();
        var terrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(0xA9B4FFFFu, terrain, Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(), worldOffsetX: 0f, worldOffsetY: 0f);

        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(118f, 96f, 50f), 0x0001, new Vector3(118f, 96f, 50f));
        controller.Yaw = 0f; // facing +X

        // Single step — should trigger airborne state because terrain drops sharply.
        controller.Update(0.05f, new MovementInput(Forward: true));

        Assert.True(controller.IsAirborne, "Player should be airborne after stepping off the cliff");

        // Simulate enough frames to fall and land on the Z=20 floor.
        for (int i = 0; i < 60; i++)
            controller.Update(0.05f, new MovementInput(Forward: true));

        Assert.False(controller.IsAirborne, "Player should have landed");
        Assert.Equal(20f, controller.Position.Z, precision: 1);
    }


    [Fact]
    public void SetCharacterBurden_PropagatesToTheWeenieAndGatesCanJump()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        IWeenieObject weenie = controller.Motion.WeenieObj!;

        Assert.True(weenie.CanJump(1.0f));

        controller.SetCharacterBurden(2.5f);
        Assert.False(weenie.CanJump(1.0f));

        controller.SetCharacterBurden(0.5f);
        Assert.True(weenie.CanJump(1.0f));
    }

    [Fact]
    public void SetCharacterStamina_ZeroesEffectiveSkillOnTheWeenie()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        controller.SetCharacterSkills(runSkill: 200, jumpSkill: 100);
        IWeenieObject weenie = controller.Motion.WeenieObj!;

        Assert.True(weenie.InqRunRate(out float baseline));

        controller.SetCharacterStamina(0);
        Assert.True(weenie.InqRunRate(out float exhausted));
        Assert.True(exhausted < baseline);

        controller.SetCharacterStamina(-1);
        Assert.True(weenie.InqRunRate(out float restored));
        Assert.Equal(baseline, restored, precision: 4);
    }


    [Fact]
    public void ChargeJump_RefusedByOverBurden_ReportsCantJumpLoadedDown()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.SetCharacterBurden(2.5f);

        var reported = new List<(string Text, AcDream.Core.Chat.RetailLogTextType Type)>();
        controller.OnInterfaceText = (text, type) => reported.Add((text, type));

        controller.Update(0.016f, new MovementInput(Jump: true));

        var report = Assert.Single(reported);
        Assert.Equal(ClientTextRefusals.CantJumpLoad, report.Text);
        Assert.Equal(AcDream.Core.Chat.RetailLogTextType.ClientLocal, report.Type);
        Assert.False(controller.IsAirborne, "a refused charge must not launch the player");
    }

    [Fact]
    public void ChargeJump_Succeeds_ReportsNothing()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        var reported = new List<string>();
        controller.OnInterfaceText = (text, _) => reported.Add(text);

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false)); // release -> jump fires

        Assert.Empty(reported);
        Assert.True(controller.IsAirborne);
    }

    [Fact]
    public void ChargeJump_RefusalWithNoObserverWired_DoesNotThrow()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.SetCharacterBurden(2.5f);

        var exception = Record.Exception(() =>
            controller.Update(0.016f, new MovementInput(Jump: true)));

        Assert.Null(exception);
    }


    [Fact]
    public void JumpPressWhileAirborne_ChargesSilently_AirborneReleaseReportsCantJumpInAir()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false)); // release -> jump fires
        Assert.True(controller.IsAirborne);
        controller.Update(0.05f, new MovementInput());

        var reported = new List<string>();
        controller.OnInterfaceText = (text, _) => reported.Add(text);

        controller.Update(0.016f, new MovementInput(Jump: true));
        Assert.Empty(reported);
        Assert.True(controller.JumpCharge.IsCharging);

        controller.Update(0.016f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: true));
        Assert.Empty(reported);

        // RELEASING while still airborne: exactly one 0x24 report, and no
        // second launch.
        Assert.True(controller.IsAirborne);
        controller.Update(0.016f, new MovementInput(Jump: false));
        var report = Assert.Single(reported);
        Assert.Equal(ClientTextRefusals.CantJumpInAir, report);
        Assert.False(controller.JumpCharge.IsCharging);
    }

    [Fact]
    public void JumpChargedInAir_HeldThroughLanding_GroundedReleaseJumpsSilently()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));

        controller.Update(1.0f, new MovementInput(Jump: true));
        controller.Update(0.016f, new MovementInput(Jump: false));
        Assert.True(controller.IsAirborne);

        var reported = new List<string>();
        controller.OnInterfaceText = (text, _) => reported.Add(text);

        // Hold jump through the landing.
        for (int i = 0; i < 120 && controller.IsAirborne; i++)
            controller.Update(0.05f, new MovementInput(Jump: true));
        Assert.False(controller.IsAirborne, "should have landed while holding");
        Assert.True(controller.JumpCharge.IsCharging, "charge survives the landing");
        Assert.Empty(reported);

        controller.Update(0.016f, new MovementInput(Jump: false));
        Assert.Empty(reported);
        Assert.True(controller.IsAirborne, "the held charge fires on grounded release");
    }


    private static (PlayerMovementController Controller, EntityPhysicsHost Host)
        MakeControllerWithHost()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var hosts = new Dictionary<uint, IPhysicsObjHost>();
        const uint selfGuid = 0x5000000Au;
        var host = new EntityPhysicsHost(
            selfGuid,
            getPosition: () => controller.CellPosition,
            getVelocity: () => controller.BodyVelocity,
            getRadius: () => 0.5f,
            inContact: () => controller.BodyInContact,
            minterpMaxSpeed: () => controller.Motion.GetMaxSpeed(),
            curTime: () => 0.0,
            physicsTimerTime: () => 0.0,
            getObjectA: id => hosts.TryGetValue(id, out var h) ? h : null,
            handleUpdateTarget: _ => { },
            interruptCurrentMovement: () => { });
        hosts[selfGuid] = host;
        controller.PositionManager = host.PositionManager;
        return (controller, host);
    }

    [Fact]
    public void SetPosition_Teleport_ArmsConstraintAnchoredToReceivedPositionOutdoor()
    {
        var (controller, _) = MakeControllerWithHost();

        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f)); // low16 < 0x0100 -> outdoor

        ConstraintManager cm = controller.PositionManager!.Constraint!;
        Assert.True(cm.IsConstrained);
        Assert.Equal(10.0f, cm.ConstraintDistanceStart);
        Assert.Equal(50.0f, cm.ConstraintDistanceMax);
        Assert.Equal(controller.Position, cm.ConstraintPos.Frame.Origin);
        // Anchored to self (the just-received position) -> zero offset at arm time.
        Assert.Equal(0f, cm.ConstraintPosOffset, 3);
        Assert.Equal(Vector3.Zero, controller.BodyVelocity);
    }

    [Fact]
    public void SetPosition_Teleport_IndoorCellUsesTheTighterBand()
    {
        var (controller, _) = MakeControllerWithHost();

        controller.SeedPlacementForTest(
            new Vector3(10f, 10f, 5f),
            0x01000105u, new Vector3(10f, 10f, 5f));

        ConstraintManager cm = controller.PositionManager!.Constraint!;
        Assert.Equal(5.0f, cm.ConstraintDistanceStart);
        Assert.Equal(20.0f, cm.ConstraintDistanceMax);
    }

    [Fact]
    public void SetPosition_Teleport_TearsDownAndRearmsAPreviouslyFullyConstrainedLeash()
    {
        var (controller, _) = MakeControllerWithHost();
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        ConstraintManager cm = controller.PositionManager!.Constraint!;

        cm.ConstrainTo(controller.CellPosition, startDistance: 0.1f, maxDistance: 0.2f);
        cm.AdjustOffset(new MotionDeltaFrame { Origin = new Vector3(5f, 0f, 0f) }, quantum: 0.1);
        Assert.True(controller.PositionManager.IsFullyConstrained());

        controller.SeedPlacementForTest(new Vector3(150f, 150f, 50f), 0x0001, new Vector3(150f, 150f, 50f));

        Assert.False(controller.PositionManager.IsFullyConstrained());
        Assert.Equal(0f, cm.ConstraintPosOffset, 3);
    }

    [Fact]
    public void CommitCanonicalForcePositionFrame_DoesNotRearmConstraintLeashOrTouchVelocity()
    {
        var (controller, _) = MakeControllerWithHost();
        var initial = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(initial, 0x0001, initial);
        controller.Update(ObjectTick, new MovementInput(Forward: true));
        Vector3 velocityBeforeCommit = controller.BodyVelocity;
        Assert.NotEqual(Vector3.Zero, velocityBeforeCommit); // sanity: actually moving
        ConstraintManager cm = controller.PositionManager!.Constraint!;
        Vector3 leashAnchorBeforeCommit = cm.ConstraintPos.Frame.Origin;

        var corrected = new Vector3(150f, 150f, 50f);
        controller.PhysicsBody.SnapToCell(0x0001, corrected, corrected);
        controller.CommitCanonicalForcePositionFrame();

        Assert.Equal(velocityBeforeCommit, controller.BodyVelocity);
        Assert.Equal(leashAnchorBeforeCommit, cm.ConstraintPos.Frame.Origin);
        Assert.NotEqual(corrected, cm.ConstraintPos.Frame.Origin);
    }

    [Fact]
    public void Update_ConstraintArmedInBand_TapersALargeRootMotionOffsetOnTheSecondTick()
    {
        var (controller, _) = MakeControllerWithHost();
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        ConstraintManager cm = controller.PositionManager!.Constraint!;
        cm.ConstrainTo(controller.CellPosition, startDistance: 1f, maxDistance: 10f);

        controller.AttachAnimationRootMotionSource((dt, frame) =>
        {
            frame.Origin = new Vector3(5f, 0f, 0f); // wildly large per-tick root motion, on purpose
        });

        Vector3 beforeTick1 = controller.Position;
        controller.Update(ObjectTick, new MovementInput());
        float displacement1 = (controller.Position - beforeTick1).Length();
        Assert.True(displacement1 > 4.0f,
            $"first tick should pass through near-unscaled, got {displacement1}");

        Vector3 beforeTick2 = controller.Position;
        controller.Update(ObjectTick, new MovementInput());
        float displacement2 = (controller.Position - beforeTick2).Length();
        Assert.True(displacement2 > 0f);
        Assert.True(displacement2 < 4.0f,
            $"second tick should be tapered well below the raw 5 m input, got {displacement2}");
    }

    [Fact]
    public void Update_ConstraintOverstrained_PushesIsFullyConstrainedOntoBodyAndBlocksJump()
    {
        var (controller, _) = MakeControllerWithHost();
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        ConstraintManager cm = controller.PositionManager!.Constraint!;
        cm.ConstrainTo(controller.CellPosition, startDistance: 1f, maxDistance: 2f);

        controller.AttachAnimationRootMotionSource((dt, frame) =>
        {
            frame.Origin = new Vector3(10f, 0f, 0f); // one huge tick, past max
        });

        controller.Update(ObjectTick, new MovementInput());

        Assert.True(controller.PositionManager!.IsFullyConstrained());
        WeenieError result = controller.Motion.jump_is_allowed(1.0f, out _);
        Assert.Equal(WeenieError.GeneralMovementFailure, result); // 0x47
    }


    [Fact]
    public void Update_AnimationRootMotion_WalkSpeedUnaffectedByResidualVelocityFix()
    {
        var controller = new PlayerMovementController(MakeFlatEngine());
        var start = new Vector3(96f, 96f, 50f);
        controller.SeedPlacementForTest(start, 0x0001, start);
        controller.Yaw = 0f;
        // Fixed per-tick local-forward delta, matching
        // Update_AttachedAnimationRootDelta_DrivesGroundedBodyAtObjectScale's
        // established pattern: root motion alone drives displacement.
        controller.AttachAnimationRootMotionSource((_, frame) =>
            frame.Origin = new Vector3(0f, 0.1f, 0f));

        Vector3 prevPos = controller.Position;
        for (int i = 0; i < 30; i++)
        {
            var result = controller.Update(ObjectTick, new MovementInput(Forward: true));
            float advance = Vector3.Distance(result.Position, prevPos);
            Assert.Equal(0.1f, advance, precision: 4);
            prevPos = result.Position;
        }

        Assert.Equal(0f, controller.BodyVelocity.X, precision: 5);
        Assert.Equal(0f, controller.BodyVelocity.Y, precision: 5);
    }

    private sealed class FakeAnimationDispatchSink : IInterpretedMotionSink
    {
        public bool ApplyMotion(uint motion, float speed) => true;
        public bool StopMotion(uint motion) => true;
    }

    [Fact]
    public void PublicationLifecycleRequiresExplicitActivationAfterOwnershipCommit()
    {
        var candidate = PlayerMovementController.CreatePublicationCandidate(
            new PhysicsEngine(),
            PlayerMovementConstructionOptions.Fallback);
        candidate.LocalEntityId = 0x70004001u;
        candidate.StepUpHeight = 0.4f;
        candidate.StepDownHeight = 0.4f;
        candidate.ObjectScale = 1f;
        candidate.PreparePositionForCommit(
            new Vector3(1f, 2f, 3f),
            0xA9B40021u,
            new Vector3(1f, 2f, 3f));
        candidate.SetBodyOrientation(Quaternion.Identity);
        candidate.ApplyPhysicsState(PhysicsStateFlags.Gravity);
        PhysicsBody body = candidate.PhysicsBody;
        body.InWorld = false;
        body.TransientState &= ~TransientStateFlags.Active;
        Assert.False(body.InWorld);

        candidate.SealPublicationCandidate();

        Assert.True(candidate.IsSealedPublicationCandidate);
        Assert.Throws<InvalidOperationException>(() => candidate.Update(
            1f / 60f,
            default));
        Assert.Throws<InvalidOperationException>(() => candidate.TickHidden(
            1f / 60f));
        Assert.Throws<InvalidOperationException>(() => candidate.SeedPlacementForTest(
            Vector3.One,
            0xA9B40021u,
            Vector3.One));
        Assert.Throws<InvalidOperationException>(() =>
            candidate.CommitCanonicalForcePositionFrame());
        Assert.Throws<InvalidOperationException>(() =>
            candidate.CaptureMovementResult(mouseLookEvent: false));
        Assert.Throws<InvalidOperationException>(() =>
            candidate.NoteMovementSent(1f));
        Assert.Throws<InvalidOperationException>(
            candidate.ArmConstraintLeashAtCommittedPlacement);
        Assert.Throws<InvalidOperationException>(() =>
            candidate.ApplyPhysicsState(PhysicsStateFlags.Frozen));
        Assert.Throws<InvalidOperationException>(() => candidate.LocalEntityId = 2u);
        Assert.Throws<InvalidOperationException>(() => _ = candidate.Movement);

        var canonicalClock = new RetailObjectQuantumClock();
        candidate.CommitRuntimeOwnership(canonicalClock);
        Assert.Throws<InvalidOperationException>(() => candidate.Update(
            1f / 60f,
            default));
        Assert.Throws<InvalidOperationException>(() =>
            candidate.CaptureMovementResult(mouseLookEvent: false));
        Assert.Throws<InvalidOperationException>(() => _ = candidate.PhysicsBody);

        candidate.BeginDormantSetPositionGroundPhase();
        Assert.Throws<InvalidOperationException>(
            candidate.CommitRuntimeActivationFrame);
        Assert.Throws<InvalidOperationException>(
            candidate.ActivateRuntimePublication);
        candidate.EndDormantSetPositionGroundPhase();

        candidate.ActivateRuntimePublication();
        Assert.Same(body, candidate.PhysicsBody);
        _ = candidate.CaptureMovementResult(mouseLookEvent: false);
        _ = candidate.Update(0f, default);
        candidate.ApplyPhysicsState(PhysicsStateFlags.Gravity);

        candidate.RetireRuntimePublication();
        Assert.Throws<InvalidOperationException>(() => candidate.Update(
            1f / 60f,
            default));
    }

    [Fact]
    public void Update_RunningJumpLandsOnFlatGround_ResidualVelocitySurvivesAndDecays_NotFrozen()
    {
        var engine = MakeFlatEngine();
        var controller = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(new Vector3(96f, 96f, 50f), 0x0001, new Vector3(96f, 96f, 50f));
        controller.Yaw = 0f;
        controller.Motion.DefaultSink = new FakeAnimationDispatchSink();
        controller.AttachAnimationRootMotionSource((_, _) => { });

        controller.Update(1.0f, new MovementInput(Forward: true, Jump: true));
        controller.Update(0.016f, new MovementInput(Forward: true, Jump: false));

        controller.Update(0.05f, new MovementInput(Forward: true));

        Assert.True(controller.IsAirborne);
        float horizSpeedAtLaunch =
            new Vector2(controller.BodyVelocity.X, controller.BodyVelocity.Y).Length();
        Assert.True(horizSpeedAtLaunch > 0.5f,
            $"Expected a running jump to carry forward horizontal velocity, got {horizSpeedAtLaunch}");

        for (int i = 0; i < 50 && controller.IsAirborne; i++)
            controller.Update(0.05f, new MovementInput());

        Assert.False(controller.IsAirborne, "Should have landed");

        controller.Update(ObjectTick, new MovementInput());
        float horizSpeedAfterLanding =
            new Vector2(controller.BodyVelocity.X, controller.BodyVelocity.Y).Length();
        Assert.True(horizSpeedAfterLanding > 0.01f,
            $"Expected residual horizontal speed to survive the first post-landing tick; " +
            $"got {horizSpeedAfterLanding} (launch speed was {horizSpeedAtLaunch})");

        for (int i = 0; i < 12; i++)
            controller.Update(ObjectTick, new MovementInput());
        float horizSpeedSettled =
            new Vector2(controller.BodyVelocity.X, controller.BodyVelocity.Y).Length();
        Assert.True(horizSpeedSettled < horizSpeedAtLaunch,
            $"Expected friction to decay the residual speed once the bounce chain settles; " +
            $"got {horizSpeedSettled} vs launch {horizSpeedAtLaunch}");
    }
}
