using System.Linq;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;


file sealed class FakeWeenie : IWeenieObject
{
    public bool IsThePlayerResult;
    public bool IsCreatureResult = true;

    public bool InqJumpVelocity(float extent, out float vz) { vz = 10f; return true; }
    public bool InqRunRate(out float rate) { rate = 1f; return true; }
    public bool CanJump(float extent) => true;
    public bool IsThePlayer() => IsThePlayerResult;
    public bool IsCreature() => IsCreatureResult;
}

public sealed class MotionInterpreterGroundLifecycleTests
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
    public void HitGround_NoPhysicsObj_NoOp()
    {
        var interp = new MotionInterpreter();
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.HitGround();

        Assert.False(called);
    }

    [Fact]
    public void HitGround_NonCreatureWeenie_NoOp()
    {
        // raw 305720-305724: (weenie_obj == 0 || eax_2 != 0) -- a WEENIE
        // present whose IsCreature() returns false skips the whole body.
        var weenie = new FakeWeenie { IsCreatureResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.HitGround();

        Assert.False(called, "a non-creature weenie must skip HitGround's body entirely");
    }

    [Fact]
    public void HitGround_NoWeenie_Proceeds()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.HitGround();

        Assert.True(called, "no weenie_obj must still proceed (weenie==0 half of the OR)");
    }

    [Fact]
    public void HitGround_GravityFlagClear_NoOp()
    {
        // raw 305726-305730: state bit 0x400 (Gravity) gates the body.
        var body = MakeGrounded();
        body.State &= ~PhysicsStateFlags.Gravity;
        var interp = MakeInterp(body);
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.HitGround();

        Assert.False(called, "HitGround must no-op when the Gravity state flag is clear");
    }

    [Fact]
    public void HitGround_CreatureWithGravity_CallsRemoveLinkAnimationsThenReapplies()
    {
        var weenie = new FakeWeenie { IsCreatureResult = true };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.HitGround();

        Assert.True(called, "HitGround must call the RemoveLinkAnimations seam");
        Assert.True(body.Velocity.Length() > 0f, "HitGround must re-apply current movement");
    }


    [Fact]
    public void LeaveGround_NoPhysicsObj_NoOp()
    {
        var interp = new MotionInterpreter();
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.LeaveGround();

        Assert.False(called);
    }

    [Fact]
    public void LeaveGround_NonCreatureWeenie_NoOp()
    {
        var weenie = new FakeWeenie { IsCreatureResult = false };
        var body = MakeGrounded();
        var interp = MakeInterp(body, weenie);
        interp.StandingLongJump = true;
        interp.JumpExtent = 0.7f;
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.LeaveGround();

        Assert.False(called);
        // Nothing in the body ran -- StandingLongJump/JumpExtent untouched.
        Assert.True(interp.StandingLongJump);
        Assert.Equal(0.7f, interp.JumpExtent);
    }

    [Fact]
    public void LeaveGround_GravityFlagClear_NoOp()
    {
        var body = MakeGrounded();
        body.State &= ~PhysicsStateFlags.Gravity;
        var interp = MakeInterp(body);
        interp.StandingLongJump = true;
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.LeaveGround();

        Assert.False(called);
        Assert.True(interp.StandingLongJump);
    }

    [Fact]
    public void LeaveGround_CreatureWithGravity_SetsVelocityAndResetsJumpState()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = true;
        interp.JumpExtent = 0.5f;

        interp.LeaveGround();

        Assert.False(interp.StandingLongJump, "standing_longjump = 0 on leave-ground");
        Assert.Equal(0f, interp.JumpExtent, precision: 5);
    }

    [Fact]
    public void LeaveGround_CallsRemoveLinkAnimationsThenReapplies()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;
        bool called = false;
        interp.RemoveLinkAnimations = () => called = true;

        interp.LeaveGround();

        Assert.True(called, "LeaveGround must call the RemoveLinkAnimations seam");
    }

    [Fact]
    public void LeaveGround_VelocityCarriesGetLeaveGroundVelocity_WithNonzeroJumpExtent()
    {
        var weenie = new AcDream.Core.Physics.PlayerWeenie();
        var body = MakeGrounded();
        body.set_on_walkable(false);
        var interp = MakeInterp(body, weenie);
        interp.JumpExtent = 0.5f;

        var expected = interp.GetLeaveGroundVelocity();
        interp.LeaveGround();

        // set_local_velocity transforms local->world via Orientation
        // (Identity in these fixtures), so world == local here.
        Assert.Equal(expected.Z, body.Velocity.Z, precision: 3);
    }

    [Fact]
    public void LeaveGround_SetLocalVelocity_UsesAutonomousFlag()
    {
        var body = MakeGrounded();
        body.LastMoveWasAutonomous = false;
        var interp = MakeInterp(body);

        interp.LeaveGround();

        Assert.True(body.LastMoveWasAutonomous, "LeaveGround's set_local_velocity call carries autonomous=1");
    }


    [Fact]
    public void EnterDefaultState_AppendsReadySentinel_WithoutDrainingExistingNodes()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.AddToQueue(1, MotionCommand.RunForward, 0);
        interp.AddToQueue(2, MotionCommand.WalkForward, 0x48);

        interp.EnterDefaultState();

        var nodes = interp.PendingMotions.ToArray();
        Assert.Equal(3, nodes.Length);
        Assert.Equal(new MotionNode(1, MotionCommand.RunForward, 0), nodes[0]);
        Assert.Equal(new MotionNode(2, MotionCommand.WalkForward, 0x48), nodes[1]);
        Assert.Equal(new MotionNode(0, MotionCommand.Ready, 0), nodes[2]);
    }

    [Fact]
    public void EnterDefaultState_SetsInitted()
    {
        var interp = new MotionInterpreter { PhysicsObj = MakeGrounded() };
        interp.Initted = false;

        interp.EnterDefaultState();

        Assert.True(interp.Initted);
    }

    [Fact]
    public void EnterDefaultState_ResetsRawAndInterpretedStateToCtorDefaults()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.ForwardCommand = MotionCommand.RunForward;
        interp.RawState.ForwardSpeed = 2.94f;
        interp.RawState.CurrentHoldKey = HoldKey.Run;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.TurnCommand = MotionCommand.TurnRight;

        interp.EnterDefaultState();

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
        Assert.Equal(1.0f, interp.RawState.ForwardSpeed);
        Assert.Equal(HoldKey.None, interp.RawState.CurrentHoldKey);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
        Assert.Equal(0u, interp.InterpretedState.TurnCommand);
    }

    [Fact]
    public void EnterDefaultState_CallsInitializeMotionTablesSeam()
    {
        var interp = MakeInterp();
        bool called = false;
        interp.InitializeMotionTables = () => called = true;

        interp.EnterDefaultState();

        Assert.True(called);
    }

    [Fact]
    public void EnterDefaultState_TailCallsLeaveGround()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.StandingLongJump = true;
        interp.JumpExtent = 0.9f;

        interp.EnterDefaultState();

        Assert.False(interp.StandingLongJump, "EnterDefaultState's LeaveGround tail must fire");
        Assert.Equal(0f, interp.JumpExtent, precision: 5);
    }

    // =========================================================================
    // Initted gating — apply_current_movement / ReportExhaustion (A3 entry gate)
    // =========================================================================

    [Fact]
    public void ApplyCurrentMovement_NotInitted_NoOp()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.Initted = false;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.Equal(System.Numerics.Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void ApplyCurrentMovement_Initted_Proceeds()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        Assert.True(interp.Initted, "the two-arg constructor defaults Initted=true (see final report)");
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.True(body.Velocity.Length() > 0f);
    }

    [Fact]
    public void ReportExhaustion_NotInitted_NoOp()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.Initted = false;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        interp.ReportExhaustion();

        Assert.Equal(System.Numerics.Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void ReportExhaustion_NoPhysicsObj_NoOp()
    {
        var interp = new MotionInterpreter();
        interp.ReportExhaustion();
    }


    [Theory]
    [InlineData(true, true, /* expectRaw */ true)]   // IsThePlayer && autonomous -> raw
    [InlineData(true, false, /* expectRaw */ false)]  // IsThePlayer but not autonomous -> interpreted
    [InlineData(false, true, /* expectRaw */ false)]
    [InlineData(false, false, /* expectRaw */ false)] // neither -> interpreted
    public void ApplyCurrentMovement_DualDispatch_MatchesA3TruthTable(
        bool isThePlayer, bool autonomous, bool expectRaw)
    {
        var weenie = new FakeWeenie { IsThePlayerResult = isThePlayer, IsCreatureResult = true };
        var body = MakeGrounded();
        body.LastMoveWasAutonomous = autonomous;
        var interp = MakeInterp(body, weenie);

        // RawState drives WalkForward (speed 1 => WalkAnimSpeed); InterpretedState
        // drives RunForward (speed 1 => RunAnimSpeed). WalkAnimSpeed != RunAnimSpeed
        // so the resulting body.Velocity.Y distinguishes which path ran.
        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        interp.RawState.ForwardSpeed = 1.0f;
        interp.RawState.ForwardHoldKey = HoldKey.None;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        float expectedY = expectRaw
            ? MotionInterpreter.WalkAnimSpeed  // apply_raw_movement re-derives from RawState
            : MotionInterpreter.RunAnimSpeed;  // apply_interpreted_movement reads InterpretedState as-is
        Assert.Equal(expectedY, body.Velocity.Y, precision: 2);
    }

    [Fact]
    public void ApplyCurrentMovement_NoWeenie_Autonomous_DispatchesRaw()
    {
        var body = MakeGrounded();
        body.LastMoveWasAutonomous = true;
        var interp = MakeInterp(body); // no weenie

        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        interp.RawState.ForwardSpeed = 1.0f;
        interp.RawState.ForwardHoldKey = HoldKey.None;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.apply_current_movement(cancelMoveTo: false, allowJump: false);

        Assert.Equal(MotionInterpreter.WalkAnimSpeed, body.Velocity.Y, precision: 2);
    }

    [Fact]
    public void ReportExhaustion_DualDispatch_PassesZeroZeroArgs()
    {
        var weenie = new FakeWeenie { IsThePlayerResult = true };
        var body = MakeGrounded();
        body.LastMoveWasAutonomous = true;
        var interp = MakeInterp(body, weenie);
        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        interp.RawState.ForwardSpeed = 1.0f;
        interp.RawState.ForwardHoldKey = HoldKey.None;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.ReportExhaustion();

        Assert.Equal(MotionInterpreter.WalkAnimSpeed, body.Velocity.Y, precision: 2);
    }

    [Fact]
    public void ReportExhaustion_RemotePlayer_DispatchesInterpreted()
    {
        var weenie = new FakeWeenie { IsThePlayerResult = false, IsCreatureResult = true };
        var body = MakeGrounded();
        body.LastMoveWasAutonomous = true; // even with autonomous=true...
        var interp = MakeInterp(body, weenie);
        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        interp.RawState.ForwardSpeed = 1.0f;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.ReportExhaustion();

        // ...IsThePlayer==false forces the interpreted path.
        Assert.Equal(MotionInterpreter.RunAnimSpeed, body.Velocity.Y, precision: 2);
    }


    [Fact]
    public void SetWeenieObject_WhilePhysicsBoundAndInitted_ReappliesMovement()
    {
        var body = MakeGrounded();
        var interp = new MotionInterpreter { PhysicsObj = body, Initted = true };
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.SetWeenieObject(new FakeWeenie { IsThePlayerResult = false });

        Assert.True(body.Velocity.Length() > 0f, "SetWeenieObject must re-apply movement when bound+initted");
    }

    [Fact]
    public void SetWeenieObject_NoPhysicsObj_DoesNotReapply()
    {
        var interp = new MotionInterpreter();
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;

        // Must not throw despite no PhysicsObj.
        interp.SetWeenieObject(new FakeWeenie());

        Assert.Null(interp.PhysicsObj);
    }

    [Fact]
    public void SetWeenieObject_NotInitted_DoesNotReapply()
    {
        var body = MakeGrounded();
        var interp = new MotionInterpreter { PhysicsObj = body, Initted = false };
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.SetWeenieObject(new FakeWeenie());

        Assert.Equal(System.Numerics.Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void SetPhysicsObject_BindsAndReappliesWhenInitted()
    {
        var interp = new MotionInterpreter { Initted = true };
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;
        var body = MakeGrounded();

        interp.SetPhysicsObject(body);

        Assert.Same(body, interp.PhysicsObj);
        Assert.True(body.Velocity.Length() > 0f);
    }

    [Fact]
    public void SetPhysicsObject_NullArg_DoesNotReapply()
    {
        var interp = new MotionInterpreter { Initted = true };

        // arg2 != 0 gates the reapply -- passing null must not throw and
        // must not attempt InterpretedState velocity work (no body to write to).
        interp.SetPhysicsObject(null);

        Assert.Null(interp.PhysicsObj);
    }


    [Fact]
    public void SetHoldRun_TogglesFromNoneToRun_WhenHoldingRunKey()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.None;

        interp.set_hold_run(holdingRun: true, interrupt: false);

        Assert.Equal(HoldKey.Run, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldRun_TogglesFromRunToNone_WhenReleasingRunKey()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.Run;

        interp.set_hold_run(holdingRun: false, interrupt: false);

        Assert.Equal(HoldKey.None, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldRun_NoChange_WhenAlreadyInRequestedState_IsANoOp()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.Run;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;
        body.Velocity = System.Numerics.Vector3.Zero;

        interp.set_hold_run(holdingRun: true, interrupt: false);

        Assert.Equal(HoldKey.Run, interp.RawState.CurrentHoldKey);
        // No re-apply fired (still zero) -- the guard skipped the whole body.
        Assert.Equal(System.Numerics.Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void SetHoldRun_OnChange_CallsApplyCurrentMovementWithInterruptArg()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.None;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.set_hold_run(holdingRun: true, interrupt: true);

        Assert.True(body.Velocity.Length() > 0f);
    }


    [Fact]
    public void SetHoldKey_None_FromRun_TakesEffect()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.Run;

        interp.SetHoldKey(HoldKey.None, cancelMoveTo: false);

        Assert.Equal(HoldKey.None, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldKey_None_FromInvalid_IsIgnored()
    {
        // raw @306072: setting None only takes effect from Run. Any other
        // starting state (Invalid, or already None) is silently ignored.
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.Invalid;

        interp.SetHoldKey(HoldKey.None, cancelMoveTo: false);

        Assert.Equal(HoldKey.Invalid, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldKey_AlreadyNone_IsNoOp()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.None;

        interp.SetHoldKey(HoldKey.None, cancelMoveTo: false);

        Assert.Equal(HoldKey.None, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldKey_Run_FromNone_TakesEffect()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.None;

        interp.SetHoldKey(HoldKey.Run, cancelMoveTo: false);

        Assert.Equal(HoldKey.Run, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void SetHoldKey_Run_AlreadyRun_IsNoOp()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.Run;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;
        body.Velocity = System.Numerics.Vector3.Zero;

        interp.SetHoldKey(HoldKey.Run, cancelMoveTo: false);

        Assert.Equal(HoldKey.Run, interp.RawState.CurrentHoldKey);
        Assert.Equal(System.Numerics.Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void SetHoldKey_EffectiveChange_ReappliesMovement()
    {
        var body = MakeGrounded();
        var interp = MakeInterp(body);
        interp.RawState.CurrentHoldKey = HoldKey.None;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardSpeed = 1.0f;

        interp.SetHoldKey(HoldKey.Run, cancelMoveTo: false);

        Assert.True(body.Velocity.Length() > 0f, "an effective SetHoldKey change must reapply movement");
    }


    [Fact]
    public void AdjustMotion_NonCreatureWeenie_SkipsNormalization()
    {
        var weenie = new FakeWeenie { IsCreatureResult = false };
        var interp = MakeInterp(weenie: weenie);
        uint motion = MotionCommand.WalkBackward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Invalid);

        Assert.Equal(MotionCommand.WalkBackward, motion);
        Assert.Equal(1.0f, speed);
    }

    [Fact]
    public void AdjustMotion_CreatureWeenie_NormalizesAsBefore()
    {
        var weenie = new FakeWeenie { IsCreatureResult = true };
        var interp = MakeInterp(weenie: weenie);
        uint motion = MotionCommand.WalkBackward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Invalid);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.Equal(-MotionInterpreter.BackwardsFactor, speed, precision: 5);
    }

    [Fact]
    public void AdjustMotion_NoWeenie_StillNormalizes()
    {
        var interp = MakeInterp();
        uint motion = MotionCommand.WalkBackward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Invalid);

        Assert.Equal(MotionCommand.WalkForward, motion);
    }
}
