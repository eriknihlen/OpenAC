using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;


file sealed class FakeWeenie : IWeenieObject
{
    public float RunRate  = 1.0f;
    public float JumpVz   = 10.0f;
    public bool  CanJumpResult = true;
    public bool  InqRunRateResult = true;
    public bool  InqJumpVelocityResult = true;

    public bool InqJumpVelocity(float extent, out float vz)
    {
        vz = JumpVz * extent;
        return InqJumpVelocityResult;
    }

    public bool InqRunRate(out float rate)
    {
        rate = RunRate;
        return InqRunRateResult;
    }

    public bool CanJump(float extent) => CanJumpResult;
}

public sealed class MotionInterpreterTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static PhysicsBody MakeGrounded()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        };
        body.TransientState = TransientStateFlags.Contact
                            | TransientStateFlags.OnWalkable
                            | TransientStateFlags.Active;
        return body;
    }

    private static PhysicsBody MakeAirborne()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        };
        body.TransientState = TransientStateFlags.Active;
        return body;
    }

    private static MotionInterpreter MakeInterp(PhysicsBody? body = null, IWeenieObject? weenie = null)
    {
        body ??= MakeGrounded();
        return new MotionInterpreter(body, weenie);
    }


    [Theory]
    [InlineData(MovementType.RawCommand)]
    [InlineData(MovementType.InterpretedCommand)]
    [InlineData(MovementType.StopRawCommand)]
    [InlineData(MovementType.StopInterpretedCommand)]
    [InlineData(MovementType.StopCompletely)]
    public void PerformMovement_ValidTypes_ReturnNone(MovementType type)
    {
        var interp = MakeInterp();
        var mvs = new MovementStruct
        {
            Type              = type,
            Motion            = MotionCommand.WalkForward,
            Speed             = 1.0f,
            ModifyInterpretedState = true,
            ModifyRawState    = false,
        };

        var result = interp.PerformMovement(mvs);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void PerformMovement_InvalidType_ReturnsGeneralFailure()
    {
        var interp = MakeInterp();
        var mvs = new MovementStruct { Type = (MovementType)99, Motion = MotionCommand.WalkForward };

        var result = interp.PerformMovement(mvs);

        Assert.Equal(WeenieError.GeneralMovementFailure, result);
    }

    [Fact]
    public void PerformMovement_StopCompletely_ResetsToReady()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand         = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        var mvs = new MovementStruct { Type = MovementType.StopCompletely };
        interp.PerformMovement(mvs);

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
    }

    [Fact]
    public void PerformMovement_NullPhysicsObj_ReturnsNoPhysicsObject()
    {
        var interp = new MotionInterpreter(); // no PhysicsObj
        var mvs = new MovementStruct { Type = MovementType.RawCommand, Motion = MotionCommand.WalkForward };

        var result = interp.PerformMovement(mvs);

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }


    [Fact]
    public void DoMotion_WalkForward_SetsRawForwardCommand()
    {
        var interp = MakeInterp();

        interp.DoMotion(MotionCommand.WalkForward, 0.8f);

        Assert.Equal(MotionCommand.WalkForward, interp.RawState.ForwardCommand);
        Assert.Equal(0.8f, interp.RawState.ForwardSpeed, precision: 5);
    }

    [Fact]
    public void DoMotion_NullPhysicsObj_ReturnsNoPhysicsObject()
    {
        var interp = new MotionInterpreter();

        var result = interp.DoMotion(MotionCommand.WalkForward);

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }


    [Fact]
    public void StopCompletely_ResetsRawAndInterpretedToReady()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand         = MotionCommand.WalkForward;
        interp.RawState.SidestepCommand        = MotionCommand.SideStepRight;
        interp.RawState.TurnCommand            = MotionCommand.TurnRight;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.TurnCommand    = MotionCommand.TurnLeft;

        var result = interp.StopCompletely();

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
        Assert.Equal(1.0f, interp.RawState.ForwardSpeed, precision: 5);
        Assert.Equal(0u, interp.RawState.SidestepCommand);
        Assert.Equal(0u, interp.RawState.TurnCommand);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
        Assert.Equal(1.0f, interp.InterpretedState.ForwardSpeed, precision: 5);
        Assert.Equal(0u, interp.InterpretedState.SideStepCommand);
        Assert.Equal(0u, interp.InterpretedState.TurnCommand);
    }

    [Fact]
    public void StopCompletely_ZerosPhysicsBodyVelocity()
    {
        var body   = MakeGrounded();
        body.set_velocity(new Vector3(5f, 3f, 0f));
        var interp = MakeInterp(body);

        interp.StopCompletely();

        Assert.Equal(Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void StopCompletely_NullPhysicsObj_ReturnsNoPhysicsObject()
    {
        var interp = new MotionInterpreter();

        var result = interp.StopCompletely();

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }


    [Fact]
    public void GetStateVelocity_Idle_ReturnsZero()
    {
        var interp = MakeInterp();
        // Default interpreted state = Ready, no sidestep, no turn

        var vel = interp.get_state_velocity();

        Assert.Equal(Vector3.Zero, vel);
    }

    [Fact]
    public void GetStateVelocity_WalkForward_ReturnsWalkSpeed()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;

        var vel = interp.get_state_velocity();

        // Y = WalkAnimSpeed * 1.0 = 3.12
        Assert.Equal(0f, vel.X, precision: 5);
        Assert.Equal(MotionInterpreter.WalkAnimSpeed, vel.Y, precision: 4);
        Assert.Equal(0f, vel.Z, precision: 5);
    }

    [Fact]
    public void GetStateVelocity_RunForward_ReturnsRunSpeed()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;

        var vel = interp.get_state_velocity();

        // Y = RunAnimSpeed * 1.0 = 4.0
        Assert.Equal(MotionInterpreter.RunAnimSpeed, vel.Y, precision: 4);
    }

    [Fact]
    public void GetStateVelocity_SidestepRight_ReturnsPositiveX()
    {
        var interp = MakeInterp();
        interp.InterpretedState.SideStepCommand = MotionCommand.SideStepRight;
        interp.InterpretedState.SideStepSpeed   = 1.0f;

        var vel = interp.get_state_velocity();

        Assert.Equal(MotionInterpreter.SidestepAnimSpeed, vel.X, precision: 4);
        Assert.Equal(0f, vel.Y, precision: 5);
    }

    [Fact]
    public void GetStateVelocity_ClampsToMaxSpeed_WhenExceedRunRate()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed   = 10.0f;

        var vel = interp.get_state_velocity();

        float maxSpeed = MotionInterpreter.RunAnimSpeed * interp.MyRunRate;
        Assert.True(vel.Length() <= maxSpeed + 1e-4f, $"velocity {vel.Length()} exceeds maxSpeed {maxSpeed}");
    }

    [Fact]
    public void GetStateVelocity_UsesWeenieRunRate()
    {
        var weenie = new FakeWeenie { RunRate = 2.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;

        var vel = interp.get_state_velocity();

        // maxSpeed = 4.0 * 2.0 = 8.0; velocity.Y = RunAnimSpeed * 1.0 = 4.0 ≤ 8.0
        Assert.Equal(MotionInterpreter.RunAnimSpeed, vel.Y, precision: 4);
    }


    [Fact]
    public void GetStateVelocity_CycleAccessor_OverridesRunAnimSpeed()
    {
        var weenie = new FakeWeenie { RunRate = 2.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 2.0f;
        interp.GetCycleVelocity = () => new Vector3(0f, 6.0f, 0f);

        var vel = interp.get_state_velocity();

        Assert.Equal(6.0f, vel.Y, precision: 4);
    }

    [Fact]
    public void GetStateVelocity_CycleAccessor_OverridesWalkAnimSpeed()
    {
        // Walk with MotionData.Velocity.Y=2.5 + speedMod=1.0 → sequencer reports 2.5.
        // Without accessor, get_state_velocity would return WalkAnimSpeed (3.12).
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;
        interp.GetCycleVelocity = () => new Vector3(0f, 2.5f, 0f);

        var vel = interp.get_state_velocity();

        Assert.Equal(2.5f, vel.Y, precision: 4);
    }

    [Fact]
    public void GetStateVelocity_CycleAccessor_ZeroY_FallsBackToConstant()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;
        interp.GetCycleVelocity = () => Vector3.Zero;

        var vel = interp.get_state_velocity();

        Assert.Equal(MotionInterpreter.RunAnimSpeed, vel.Y, precision: 4);
    }

    [Fact]
    public void GetStateVelocity_CycleAccessor_ClampStillApplies()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;
        interp.GetCycleVelocity = () => new Vector3(0f, 20.0f, 0f);

        var vel = interp.get_state_velocity();

        float maxSpeed = MotionInterpreter.RunAnimSpeed * interp.MyRunRate;
        Assert.True(vel.Length() <= maxSpeed + 1e-4f,
            $"velocity {vel.Length()} exceeds maxSpeed {maxSpeed}");
    }

    [Fact]
    public void GetStateVelocity_CycleAccessor_OnlyAffectsForwardAxis()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand  = MotionCommand.Ready;
        interp.InterpretedState.SideStepCommand = MotionCommand.SideStepRight;
        interp.InterpretedState.SideStepSpeed   = 1.0f;
        interp.GetCycleVelocity = () => new Vector3(99f, 0f, 0f);

        var vel = interp.get_state_velocity();

        // velocity.X must equal SidestepAnimSpeed (1.25), NOT 99.
        Assert.Equal(MotionInterpreter.SidestepAnimSpeed, vel.X, precision: 4);
    }

    [Fact]
    public void GetStateVelocity_CycleAccessor_NotCalledWhenIdle()
    {
        int invocations = 0;
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;
        interp.GetCycleVelocity = () =>
        {
            invocations++;
            return new Vector3(0f, 5f, 0f);
        };

        var vel = interp.get_state_velocity();

        // Velocity must be zero; accessor must not have supplied the 5.0f.
        Assert.Equal(0f, vel.Y, precision: 4);
    }



    [Fact]
    public void ContactAllowsMove_Grounded_Ready_AllowsMove()
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        // InterpretedState.ForwardCommand defaults to Ready

        bool allowed = interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.True(allowed);
    }

    [Fact]
    public void ContactAllowsMove_Airborne_RejectsMove()
    {
        var body   = MakeAirborne();
        var interp = MakeInterp(body);

        bool allowed = interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.False(allowed);
    }

    [Fact]
    public void ContactAllowsMove_TurnCommand_AlwaysAllowed_EvenAirborne()
    {
        var body   = MakeAirborne();
        var interp = MakeInterp(body);

        bool allowRight = interp.contact_allows_move(MotionCommand.TurnRight);
        bool allowLeft  = interp.contact_allows_move(MotionCommand.TurnLeft);

        Assert.True(allowRight);
        Assert.True(allowLeft);
    }


    [Theory]
    [InlineData(MotionCommand.Fallen)]
    [InlineData(MotionCommand.Dead)]
    [InlineData(MotionCommand.Crouch)]
    [InlineData(MotionCommand.Sitting)]
    [InlineData(MotionCommand.Sleeping)]
    public void ContactAllowsMove_GroundedPosture_StillAllowsMove(uint postureCommand)
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = postureCommand;

        bool allowed = interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.True(allowed);
    }

    [Fact]
    public void ContactAllowsMove_AirborneCreature_AcceptsFallingAndTurns_BlocksWalk()
    {
        var body = new PhysicsBody { State = PhysicsStateFlags.Gravity };
        var interp = MakeInterp(body);

        Assert.True(interp.contact_allows_move(MotionCommand.Falling));
        Assert.True(interp.contact_allows_move(0x40000011u));
        Assert.True(interp.contact_allows_move(MotionCommand.TurnRight));
        Assert.True(interp.contact_allows_move(MotionCommand.TurnLeft));
        Assert.False(interp.contact_allows_move(MotionCommand.WalkForward));
        Assert.False(interp.contact_allows_move(0x8000003Du));
    }

    [Fact]
    public void ContactAllowsMove_NoGravity_AlwaysAllows()
    {
        // E.g. flying or swimming object
        var body = new PhysicsBody
        {
            State         = PhysicsStateFlags.None,
            TransientState = TransientStateFlags.None,
        };
        var interp = MakeInterp(body);

        bool allowed = interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.True(allowed);
    }

    [Fact]
    public void ContactAllowsMove_GroundedAndIdle_DoesNotSetStandingLongJump()
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;

        interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.False(interp.StandingLongJump, "contact_allows_move must never touch StandingLongJump (J6)");
    }


    [Fact]
    public void ApplyCurrentMovement_WalkForward_PushesNonZeroVelocity()
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed   = 1.0f;

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.True(body.Velocity.Length() > 0f, "Velocity should be non-zero when walking");
    }

    [Fact]
    public void ApplyCurrentMovement_Idle_PushesZeroVelocity()
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        // Default state = Ready

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.Equal(Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void ApplyCurrentMovement_Run_UpdatesMyRunRate()
    {
        var body   = MakeGrounded();
        var interp = MakeInterp(body);
        interp.MyRunRate = 1.0f;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed   = 2.0f;  // e.g. speed-buff

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.Equal(2.0f, interp.MyRunRate, precision: 5);
    }

    [Fact]
    public void ApplyCurrentMovement_RunForward_SetsMyRunRate()
    {
        var body = new PhysicsBody();
        var mi = new MotionInterpreter(body);
        mi.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        mi.InterpretedState.ForwardSpeed = 2.375f;

        mi.apply_current_movement(cancelMoveTo: false, allowJump: true);

        Assert.Equal(2.375f, mi.MyRunRate, precision: 3);
        var vel = mi.get_state_velocity();
        Assert.Equal(4.0f * 2.375f, vel.Y, precision: 2);
    }


    [Fact]
    public void GetMaxSpeed_RunForward_ReturnsRunAnimSpeedTimesRunRate()
    {
        var weenie = new FakeWeenie { RunRate = 1.5f };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;

        float speed = interp.GetMaxSpeed();

        Assert.Equal(MotionInterpreter.RunAnimSpeed * 1.5f, speed, precision: 4); // 6.0
    }

    [Theory]
    [InlineData(MotionCommand.WalkForward)]
    [InlineData(MotionCommand.WalkBackward)]
    [InlineData(MotionCommand.Ready)]
    [InlineData(MotionCommand.RunForward)]
    public void GetMaxSpeed_IgnoresForwardCommand_AlwaysReturnsRunRate(uint command)
    {
        var weenie = new FakeWeenie { RunRate = 1.75f };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = command;

        float speed = interp.GetMaxSpeed();

        Assert.Equal(MotionInterpreter.RunAnimSpeed * 1.75f, speed, precision: 4);
    }


    [Theory]
    [InlineData(MotionCommand.WalkForward)]
    [InlineData(MotionCommand.WalkBackward)]
    [InlineData(MotionCommand.Ready)]
    public void GetAdjustedMaxSpeed_NotRunForward_ReturnsBareRunRate(uint command)
    {
        var weenie = new FakeWeenie { RunRate = 2.94f };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = command;

        Assert.Equal(2.94f, interp.GetAdjustedMaxSpeed(), precision: 4);
    }

    [Fact]
    public void GetAdjustedMaxSpeed_RunForward_ReturnsForwardSpeedTimesRunAnimSpeed()
    {
        var weenie = new FakeWeenie { RunRate = 99f }; // must be ignored
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 2.5f;

        Assert.Equal(
            2.5f * MotionInterpreter.RunAnimSpeed,
            interp.GetAdjustedMaxSpeed(),
            precision: 4); // 10.0 — NOT 99×4
    }

    [Fact]
    public void GetAdjustedMaxSpeed_NoWeenie_FallsBackToLiteralOne()
    {
        var interp = MakeInterp(weenie: null);
        interp.MyRunRate = 3.5f;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        Assert.Equal(1.0f, interp.GetAdjustedMaxSpeed(), precision: 4);
    }

    [Fact]
    public void GetAdjustedMaxSpeed_InqRunRateFails_FallsBackToMyRunRate()
    {
        var weenie = new FakeWeenie { RunRate = 5f, InqRunRateResult = false };
        var interp = MakeInterp(weenie: weenie);
        interp.MyRunRate = 2.4f;
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;

        Assert.Equal(2.4f, interp.GetAdjustedMaxSpeed(), precision: 4);
    }

    [Fact]
    public void GetMaxSpeed_NoWeenie_ReturnsLiteralOneTimesRunAnimSpeed()
    {
        var interp = MakeInterp();
        interp.MyRunRate = 1.75f;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;

        float speed = interp.GetMaxSpeed();

        Assert.Equal(MotionInterpreter.RunAnimSpeed * 1.0f, speed, precision: 4);
    }

    [Fact]
    public void GetMaxSpeed_InqRunRateFails_FallsBackToMyRunRate()
    {
        var weenie = new FakeWeenie { RunRate = 9.9f, InqRunRateResult = false };
        var interp = MakeInterp(weenie: weenie);
        interp.MyRunRate = 1.75f;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;

        float speed = interp.GetMaxSpeed();

        Assert.Equal(MotionInterpreter.RunAnimSpeed * 1.75f, speed, precision: 4);
    }
}
