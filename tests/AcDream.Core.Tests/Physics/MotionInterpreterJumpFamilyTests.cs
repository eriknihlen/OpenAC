using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Tests.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;


/// <summary>Fake WeenieObject for jump-family test isolation.</summary>
file sealed class FakeWeenie : IWeenieObject
{
    public float RunRate = 1.0f;
    public float JumpVz = 10.0f;
    public bool CanJumpResult = true;
    public bool InqRunRateResult = true;
    public bool InqJumpVelocityResult = true;

    public bool JumpStaminaCostResult = true;
    public int JumpStaminaCostCalls;

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

    public bool JumpStaminaCost(float extent, out int cost)
    {
        JumpStaminaCostCalls++;
        cost = 0;
        return JumpStaminaCostResult;
    }
}

public sealed class MotionInterpreterJumpFamilyTests
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


    [Fact]
    public void JumpChargeIsAllowed_NoWeenie_ReadyForward_ReturnsNone()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_WeenieCanJumpFalse_ReturnsCantJumpLoadedDown()
    {
        var weenie = new FakeWeenie { CanJumpResult = false };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.CantJumpLoadedDown, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_Fallen_ReturnsYouCantJumpFromThisPosition()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Fallen;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
    }

    [Theory]
    [InlineData(MotionCommand.Crouch)]
    [InlineData(MotionCommand.Sitting)]
    [InlineData(MotionCommand.Sleeping)]
    public void JumpChargeIsAllowed_CrouchSitSleepRange_ReturnsYouCantJumpFromThisPosition(uint forward)
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = forward;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_CrouchLowerBoundExact_Passes()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.CrouchLowerBound;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_SleepUpperBoundExact_Blocked()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.CrouchUpperExclusive;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_Falling_Passes()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Falling;

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpChargeIsAllowed_CanJumpCheckedBeforePostureCheck()
    {
        var weenie = new FakeWeenie { CanJumpResult = false };
        var interp = MakeInterp(weenie: weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.Falling; // would pass posture gate

        var result = interp.JumpChargeIsAllowed(0.5f);

        Assert.Equal(WeenieError.CantJumpLoadedDown, result);
    }


    [Fact]
    public void ChargeJump_GroundedIdle_ArmsStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;
        // Default interpreted state: ForwardCommand=Ready, SideStep=0, Turn=0.

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.None, result);
        Assert.True(interp.StandingLongJump, "grounded + idle must arm StandingLongJump");
    }

    [Fact]
    public void ChargeJump_GroundedButMoving_DoesNotArmStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.None, result);
        Assert.False(interp.StandingLongJump, "must not arm while moving forward");
    }

    [Fact]
    public void ChargeJump_GroundedIdleButSidestepping_DoesNotArmStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;
        interp.InterpretedState.SideStepCommand = MotionCommand.SideStepRight;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.None, result);
        Assert.False(interp.StandingLongJump, "must not arm while sidestepping");
    }

    [Fact]
    public void ChargeJump_GroundedIdleButTurning_DoesNotArmStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;
        interp.InterpretedState.TurnCommand = MotionCommand.TurnRight;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.None, result);
        Assert.False(interp.StandingLongJump, "must not arm while turning");
    }

    [Fact]
    public void ChargeJump_Airborne_DoesNotArmStandingLongJump()
    {
        var body = MakeAirborne();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.None, result);
        Assert.False(interp.StandingLongJump, "must not arm while airborne (no Contact+OnWalkable)");
    }

    [Fact]
    public void ChargeJump_WeenieBlocksCanJump_ReturnsCantJumpLoadedDown_NoArm()
    {
        var weenie = new FakeWeenie { CanJumpResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        interp.StandingLongJump = false;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.CantJumpLoadedDown, result);
        Assert.False(interp.StandingLongJump);
    }

    [Fact]
    public void ChargeJump_Fallen_ReturnsYouCantJumpFromThisPosition_NoArm()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = MotionCommand.Fallen;
        interp.StandingLongJump = false;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
        Assert.False(interp.StandingLongJump);
    }

    [Fact]
    public void ChargeJump_CrouchRange_ReturnsYouCantJumpFromThisPosition_NoArm()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = MotionCommand.Sitting;
        interp.StandingLongJump = false;

        var result = interp.ChargeJump();

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
        Assert.False(interp.StandingLongJump);
    }


    [Fact]
    public void ContactAllowsMove_GroundedAndIdle_DoesNotArmStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = false;

        interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.False(interp.StandingLongJump,
            "contact_allows_move must never arm StandingLongJump (J6 — moved to ChargeJump exclusively)");
    }

    [Fact]
    public void ContactAllowsMove_GroundedAndIdle_DoesNotClearPreArmedStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = true;

        interp.contact_allows_move(MotionCommand.WalkForward);

        Assert.True(interp.StandingLongJump,
            "contact_allows_move must not clear an externally-armed StandingLongJump flag");
    }


    [Fact]
    public void GetJumpVZ_EpsilonIsRetailLiteral()
    {
        Assert.Equal(0.000199999995f, MotionInterpreter.JumpVzEpsilon);
    }

    [Fact]
    public void GetJumpVZ_JustBelowEpsilon_ReturnsZero()
    {
        var weenie = new FakeWeenie { JumpVz = 10.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.JumpExtent = 0.0001f; // < 0.000199999995f

        Assert.Equal(0f, interp.GetJumpVZ(), precision: 6);
    }

    [Fact]
    public void GetJumpVZ_JustAboveEpsilon_DelegatesToWeenie()
    {
        var weenie = new FakeWeenie { JumpVz = 10.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.JumpExtent = 0.0003f; // > 0.000199999995f

        float vz = interp.GetJumpVZ();

        // Not the zero-fallback: delegates to InqJumpVelocity(extent, ...).
        Assert.Equal(weenie.JumpVz * 0.0003f, vz, precision: 4);
    }

    [Fact]
    public void GetJumpVZ_ExactlyOldWrongEpsilon_0_001_IsAboveRetailEpsilon_NotZero()
    {
        var weenie = new FakeWeenie { JumpVz = 10.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.JumpExtent = 0.0005f;

        float vz = interp.GetJumpVZ();

        Assert.NotEqual(0f, vz);
        Assert.Equal(weenie.JumpVz * 0.0005f, vz, precision: 4);
    }

    [Fact]
    public void GetJumpVZ_NoWeenie_ReturnsDefault()
    {
        var interp = MakeInterp();
        interp.JumpExtent = 0.5f;

        Assert.Equal(MotionInterpreter.DefaultJumpVz, interp.GetJumpVZ(), precision: 4);
    }

    [Fact]
    public void GetJumpVZ_ExtentClampsAtMax1()
    {
        var weenie = new FakeWeenie { JumpVz = 10.0f };
        var interp = MakeInterp(weenie: weenie);
        interp.JumpExtent = 5.0f;

        float vz = interp.GetJumpVZ();

        Assert.Equal(10.0f, vz, precision: 4);
    }

    [Fact]
    public void GetJumpVZ_WeenieRefusesInqJumpVelocity_ReturnsZero()
    {
        var weenie = new FakeWeenie { InqJumpVelocityResult = false };
        var interp = MakeInterp(weenie: weenie);
        interp.JumpExtent = 0.5f;

        Assert.Equal(0f, interp.GetJumpVZ(), precision: 5);
    }


    [Fact]
    public void GetLeaveGroundVelocity_WalkingForward_HasPositiveYAndZ()
    {
        var weenie = new FakeWeenie { JumpVz = 10.0f };
        var body = MakeGrounded();
        var interp = new MotionInterpreter(body, weenie)
        {
            JumpExtent = 1.0f,
        };
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        var vel = interp.GetLeaveGroundVelocity();

        Assert.True(vel.Y > 0f, "Y velocity should be positive when walking forward");
        Assert.True(vel.Z > 0f, "Z (jump) velocity should be positive");
    }

    [Fact]
    public void GetLeaveGroundVelocity_TrulyZero_FallsBackToWorldVelocityGlobalToLocal()
    {
        var body = MakeGrounded();
        body.Velocity = new Vector3(2.0f, 3.0f, 0f); // nonzero WORLD velocity (momentum)
        body.Orientation = Quaternion.Identity;       // identity: local == global for this pin
        var interp = new MotionInterpreter(body)
        {
            JumpExtent = 0f, // below epsilon -> get_jump_v_z returns 0
        };

        var vel = interp.GetLeaveGroundVelocity();

        Assert.Equal(2.0f, vel.X, precision: 4);
        Assert.Equal(3.0f, vel.Y, precision: 4);
        Assert.Equal(0.0f, vel.Z, precision: 4);
    }

    [Fact]
    public void GetLeaveGroundVelocity_TrulyZero_NoWorldVelocity_StaysZero()
    {
        var body = MakeGrounded();
        body.Velocity = Vector3.Zero;
        var interp = new MotionInterpreter(body)
        {
            JumpExtent = 0f,
        };

        var vel = interp.GetLeaveGroundVelocity();

        Assert.Equal(0f, vel.X, precision: 5);
        Assert.Equal(0f, vel.Y, precision: 5);
        Assert.Equal(0f, vel.Z, precision: 5);
    }

    [Fact]
    public void GetLeaveGroundVelocity_NonzeroXOnly_DoesNotTriggerFallback()
    {
        var body = MakeGrounded();
        body.Velocity = new Vector3(99f, 99f, 99f); // if fallback wrongly fired, this would show
        var interp = new MotionInterpreter(body)
        {
            JumpExtent = 0f, // jump v_z stays 0
        };
        interp.InterpretedState.SideStepCommand = MotionCommand.SideStepRight;
        interp.InterpretedState.SideStepSpeed = 1.0f;

        var vel = interp.GetLeaveGroundVelocity();

        // X must be the sidestep-derived value, not 99 (fallback did not fire).
        Assert.NotEqual(99f, vel.X);
        Assert.True(vel.X > 0f, "sidestep should have produced nonzero X from get_state_velocity");
    }

    [Fact]
    public void GetLeaveGroundVelocity_EpsilonBoundary_JustBelow_TriggersFallback()
    {
        var body = MakeGrounded();
        body.Velocity = new Vector3(5f, 0f, 0f);
        body.Orientation = Quaternion.Identity;
        var interp = new MotionInterpreter(body)
        {
            JumpExtent = 0.0001f, // < 0.000199999995f -> get_jump_v_z() == 0
        };
        // Default interpreted state -> XY == 0 too. All three axes are
        // exactly at the "essentially zero" edge -> fallback fires.

        var vel = interp.GetLeaveGroundVelocity();

        Assert.Equal(5f, vel.X, precision: 4);
    }


    [Fact]
    public void JumpIsAllowed_NullPhysicsObj_ReturnsNotGrounded()
    {
        var interp = new MotionInterpreter();

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.NotGrounded, result);
    }

    [Fact]
    public void JumpIsAllowed_CreatureWeenie_Airborne_GravityState_ReturnsNotGrounded()
    {
        var body = MakeAirborne();
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.NotGrounded, result);
    }

    [Fact]
    public void JumpIsAllowed_CreatureWeenie_Grounded_ReturnsNone()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out int cost);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void JumpIsAllowed_NonCreatureWeenie_Airborne_SkipsGroundGate_ReturnsNone()
    {
        var weenie = new FakeWeenie();
        var body = MakeAirborne();
        var interp = MakeInterp(body, weenie);
        var nonCreature = new NonCreatureFakeWeenie();
        interp.WeenieObj = nonCreature;

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpIsAllowed_NoWeenie_GravityStateOff_SkipsGroundGate_ReturnsNone()
    {
        var body = new PhysicsBody
        {
            State = PhysicsStateFlags.None,
            TransientState = TransientStateFlags.None,
        };
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpIsAllowed_IsFullyConstrained_ReturnsGeneralMovementFailure()
    {
        var body = MakeGrounded();
        body.IsFullyConstrained = true;
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.GeneralMovementFailure, result); // 0x47
    }

    [Fact]
    public void JumpIsAllowed_IsFullyConstrained_BeatsPendingHeadPeek()
    {
        var body = MakeGrounded();
        body.IsFullyConstrained = true;
        var interp = MakeInterp(body);
        interp.AddToQueue(0, MotionCommand.WalkForward, (uint)WeenieError.CantJumpLoadedDown);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.GeneralMovementFailure, result);
    }

    [Fact]
    public void JumpIsAllowed_LeashArmedAndOverstrained_ReturnsGeneralMovementFailure()
    {
        var world = new System.Collections.Generic.Dictionary<uint, PhysicsObjHostStub>();
        var host = new PhysicsObjHostStub(10u, world);
        const uint outdoorCell = 0x12340007u; // low16 < 0x0100 -> outdoor
        host.Position = new Position(outdoorCell, Vector3.Zero, Quaternion.Identity);

        float start = ConstraintDistance.GetStartConstraintDistance(outdoorCell);
        float max = ConstraintDistance.GetMaxConstraintDistance(outdoorCell);
        Assert.Equal(10.0f, start);
        Assert.Equal(50.0f, max);

        host.PositionManager.ConstrainTo(host.Position, start, max);
        Assert.False(host.PositionManager.IsFullyConstrained());

        var frame = new MotionDeltaFrame { Origin = new Vector3(46f, 0f, 0f) };
        host.PositionManager.AdjustOffset(frame, quantum: 1.0 / 30.0);
        Assert.True(host.PositionManager.IsFullyConstrained());

        var body = MakeGrounded();
        // The per-tick pump's push (PlayerMovementController / RuntimeRemotePhysicsUpdater):
        body.IsFullyConstrained = host.PositionManager.IsFullyConstrained();
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.GeneralMovementFailure, result); // 0x47
    }

    [Fact]
    public void JumpIsAllowed_PendingHeadNonzeroError_ShortCircuitsChain()
    {
        var weenie = new FakeWeenie();
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        interp.AddToQueue(0, MotionCommand.WalkForward, (uint)WeenieError.YouCantJumpFromThisPosition);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
        Assert.Equal(0, weenie.JumpStaminaCostCalls);
    }

    [Fact]
    public void JumpIsAllowed_PendingHeadZeroError_FallsThroughToChain()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.AddToQueue(0, MotionCommand.WalkForward, jumpErrorCode: 0);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpIsAllowed_EmptyQueue_FallsThroughToChain()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void JumpIsAllowed_ChargeBlocked_Fallen_ReturnsYouCantJumpFromThisPosition()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = MotionCommand.Fallen;

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
    }

    [Fact]
    public void JumpIsAllowed_ChargeBlocked_WeenieCanJumpFalse_ReturnsCantJumpLoadedDown()
    {
        var weenie = new FakeWeenie { CanJumpResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.CantJumpLoadedDown, result);
    }

    [Fact]
    public void JumpIsAllowed_MotionAllowsJumpBlocks_ReturnsYouCantJumpFromThisPosition()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = 0x40000020u;

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.YouCantJumpFromThisPosition, result);
    }

    [Fact]
    public void JumpIsAllowed_NoWeenie_PassesChain_ReturnsNone()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body); // no weenie

        var result = interp.jump_is_allowed(0.5f, out int cost);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(0, cost);
    }

    [Fact]
    public void JumpIsAllowed_WeenieJumpStaminaCostRefuses_ReturnsGeneralMovementFailure()
    {
        var weenie = new FakeWeenie { JumpStaminaCostResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.GeneralMovementFailure, result); // 0x47
        Assert.Equal(1, weenie.JumpStaminaCostCalls);
    }

    [Fact]
    public void JumpIsAllowed_WeenieJumpStaminaCostAffords_ReturnsNone()
    {
        var weenie = new FakeWeenie { JumpStaminaCostResult = true };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);

        var result = interp.jump_is_allowed(0.5f, out _);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(1, weenie.JumpStaminaCostCalls);
    }

    private sealed class NonCreatureFakeWeenie : IWeenieObject
    {
        public bool InqJumpVelocity(float extent, out float vz) { vz = 10f; return true; }
        public bool InqRunRate(out float rate) { rate = 1f; return true; }
        public bool CanJump(float extent) => true;
        public bool IsCreature() => false;
    }


    [Fact]
    public void Jump_Grounded_SetsJumpExtentAndLeavesWalkable()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);

        var result = interp.jump(0.5f);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(0.5f, interp.JumpExtent, precision: 5);
        Assert.False(body.OnWalkable, "Body should no longer be on walkable after jump");
    }

    [Fact]
    public void Jump_Airborne_ReturnsNotGrounded()
    {
        var body = MakeAirborne();
        var interp = MakeInterp(body);

        var result = interp.jump(0.5f);

        Assert.Equal(WeenieError.NotGrounded, result);
    }

    [Fact]
    public void Jump_WeenieBlocksJump_ClearsStandingLongJump()
    {
        var weenie = new FakeWeenie { CanJumpResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        interp.StandingLongJump = true;

        var result = interp.jump(0.5f);

        Assert.Equal(WeenieError.CantJumpLoadedDown, result);
        Assert.False(interp.StandingLongJump, "a failed jump attempt cancels any pending StandingLongJump");
    }

    [Fact]
    public void Jump_NullPhysicsObj_ReturnsNoPhysicsObject()
    {
        var interp = new MotionInterpreter();

        var result = interp.jump(0.5f);

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }

    [Fact]
    public void Jump_Success_DoesNotClearStandingLongJump()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = true;

        var result = interp.jump(0.5f);

        Assert.Equal(WeenieError.None, result);
        Assert.True(interp.StandingLongJump, "success path must not touch StandingLongJump");
    }

    [Fact]
    public void Jump_CallsInterruptCurrentMovementSeam()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        bool called = false;
        interp.InterruptCurrentMovement = () => called = true;

        interp.jump(0.5f);

        Assert.True(called, "jump() must invoke the InterruptCurrentMovement seam");
    }

    [Fact]
    public void Jump_CallsInterruptCurrentMovementSeam_EvenOnFailure()
    {
        var body = MakeAirborne();
        var interp = MakeInterp(body);
        bool called = false;
        interp.InterruptCurrentMovement = () => called = true;

        interp.jump(0.5f);

        Assert.True(called, "jump() calls interrupt_current_movement before jump_is_allowed, unconditionally");
    }


    [Fact]
    public void IWeenieObject_IsThePlayer_DefaultsFalse()
    {
        IWeenieObject weenie = new FakeWeenie();

        Assert.False(weenie.IsThePlayer());
    }

    [Fact]
    public void PlayerWeenie_IsThePlayer_ReturnsTrue()
    {
        var weenie = new PlayerWeenie();

        Assert.True(weenie.IsThePlayer());
    }
}
