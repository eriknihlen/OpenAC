using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;


file sealed class FakeWeenie : IWeenieObject
{
    public bool IsThePlayerResult = false;
    public bool IsCreatureResult = true;
    public bool CanJumpResult = true;

    public bool InqJumpVelocity(float extent, out float vz) { vz = 10f; return true; }
    public bool InqRunRate(out float rate) { rate = 1f; return true; }
    public bool CanJump(float extent) => CanJumpResult;
    public bool IsThePlayer() => IsThePlayerResult;
    public bool IsCreature() => IsCreatureResult;
}

public sealed class MotionInterpreterDoMotionFamilyTests
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

    private static MotionInterpreter MakeInterp(PhysicsBody? body = null, IWeenieObject? weenie = null)
    {
        body ??= MakeGrounded();
        return new MotionInterpreter(body, weenie);
    }


    [Fact]
    public void DoMotion_NullPhysicsObj_Returns8()
    {
        var interp = new MotionInterpreter();

        var result = interp.DoMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }

    [Fact]
    public void DoMotion_CancelMoveToBit_FiresInterruptSeam()
    {
        var interp = MakeInterp();
        bool fired = false;
        interp.InterruptCurrentMovement = () => fired = true;
        var p = new MovementParameters { CancelMoveTo = true };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.True(fired, "the sign-bit (CancelMoveTo) must fire InterruptCurrentMovement before adjust_motion");
    }

    [Fact]
    public void DoMotion_CancelMoveToClear_DoesNotFireInterruptSeam()
    {
        var interp = MakeInterp();
        bool fired = false;
        interp.InterruptCurrentMovement = () => fired = true;
        var p = new MovementParameters { CancelMoveTo = false };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.False(fired);
    }

    [Fact]
    public void DoMotion_SetHoldKeyBit_FiresSetHoldKey_WithCancelMoveToBitAsSecondArg()
    {
        var interp = MakeInterp();
        var p = new MovementParameters
        {
            SetHoldKey = true,
            CancelMoveTo = true,
            HoldKeyToApply = HoldKey.Run,
        };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.Equal(HoldKey.Run, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void DoMotion_SetHoldKeyBit_FiresBeforeAdjustMotion()
    {
        var interp = MakeInterp();
        var p = new MovementParameters
        {
            SetHoldKey = true,
            HoldKeyToApply = HoldKey.Run,
        };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.RunForward, interp.InterpretedState.ForwardCommand);
    }

    [Fact]
    public void DoMotion_SetHoldKeyBitClear_DoesNotChangeHoldKey()
    {
        var interp = MakeInterp();
        interp.RawState.CurrentHoldKey = HoldKey.None;
        var p = new MovementParameters
        {
            SetHoldKey = false,
            HoldKeyToApply = HoldKey.Run,
        };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.Equal(HoldKey.None, interp.RawState.CurrentHoldKey);
    }

    [Fact]
    public void DoMotion_ParamsReDefaulted_CallerDistanceHeadingDiscarded()
    {
        var interp1 = MakeInterp();
        var interp2 = MakeInterp();
        var weird = new MovementParameters { DistanceToObject = 999f, DesiredHeading = 12345f, FailDistance = 1f };
        var plain = new MovementParameters();

        var r1 = interp1.DoMotion(MotionCommand.WalkForward, weird);
        var r2 = interp2.DoMotion(MotionCommand.WalkForward, plain);

        Assert.Equal(r2, r1);
        Assert.Equal(interp2.InterpretedState.ForwardCommand, interp1.InterpretedState.ForwardCommand);
        Assert.Equal(interp2.InterpretedState.ForwardSpeed, interp1.InterpretedState.ForwardSpeed);
    }


    [Theory]
    [InlineData(MotionCommand.Crouch, WeenieError.CrouchInCombatStance)]
    [InlineData(MotionCommand.Sitting, WeenieError.SitInCombatStance)]
    [InlineData(MotionCommand.Sleeping, WeenieError.SleepInCombatStance)]
    public void DoMotion_JumpChargeMotion_RejectedInCombatStance(uint motion, WeenieError expected)
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x80000001u; // any non-NonCombat stance

        var result = interp.DoMotion(motion, new MovementParameters());

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(MotionCommand.Crouch)]
    [InlineData(MotionCommand.Sitting)]
    [InlineData(MotionCommand.Sleeping)]
    public void DoMotion_JumpChargeMotion_AllowedInNonCombatStance(uint motion)
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x8000003Du; // NonCombat

        var result = interp.DoMotion(motion, new MovementParameters());

        Assert.NotEqual(WeenieError.CrouchInCombatStance, result);
        Assert.NotEqual(WeenieError.SitInCombatStance, result);
        Assert.NotEqual(WeenieError.SleepInCombatStance, result);
    }

    [Fact]
    public void DoMotion_ChatEmoteBit_RejectedInCombatStance()
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x80000001u;
        uint motion = 0x10000000u | 0x2000000u;

        var result = interp.DoMotion(motion, new MovementParameters());

        Assert.Equal(WeenieError.ChatEmoteOutsideNonCombat, result);
    }

    [Fact]
    public void DoMotion_ChatEmoteBit_AllowedInNonCombatStance()
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x8000003Du; // NonCombat
        uint motion = 0x10000000u | 0x2000000u;

        var result = interp.DoMotion(motion, new MovementParameters());

        Assert.NotEqual(WeenieError.ChatEmoteOutsideNonCombat, result);
    }

    [Fact]
    public void DoMotion_NonJumpChargeMotion_NotGatedByCombatStance()
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x80000001u;

        var result = interp.DoMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
    }

    // ── action-depth gate (A10: 0x45, GetNumActions >= 6) ──────────────────

    [Fact]
    public void DoMotion_ActionClassMotion_DepthBelowSix_Allowed()
    {
        var interp = MakeInterp();
        for (int i = 0; i < 5; i++)
            interp.InterpretedState.AddAction(0x10000050u, 1f, (uint)i, false);

        uint actionMotion = 0x10000060u;
        var result = interp.DoMotion(actionMotion, new MovementParameters());

        Assert.NotEqual(WeenieError.ActionDepthExceeded, result);
    }

    [Fact]
    public void DoMotion_ActionClassMotion_DepthExactlySix_Rejected()
    {
        var interp = MakeInterp();
        for (int i = 0; i < 6; i++)
            interp.InterpretedState.AddAction(0x10000050u, 1f, (uint)i, false);

        uint actionMotion = 0x10000060u;
        var result = interp.DoMotion(actionMotion, new MovementParameters());

        Assert.Equal(WeenieError.ActionDepthExceeded, result);
    }

    [Fact]
    public void DoMotion_ActionClassMotion_DepthFive_NotRejected()
    {
        var interp = MakeInterp();
        for (int i = 0; i < 5; i++)
            interp.InterpretedState.AddAction(0x10000050u, 1f, (uint)i, false);

        uint actionMotion = 0x10000060u;
        var result = interp.DoMotion(actionMotion, new MovementParameters());

        Assert.NotEqual(WeenieError.ActionDepthExceeded, result);
    }

    [Fact]
    public void DoMotion_NonActionClassMotion_IgnoresDepthCap()
    {
        var interp = MakeInterp();
        for (int i = 0; i < 10; i++)
            interp.InterpretedState.AddAction(0x10000050u, 1f, (uint)i, false);

        var result = interp.DoMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.NotEqual(WeenieError.ActionDepthExceeded, result);
    }

    // ── raw mirror discipline: raw gets ORIGINAL id, interpreted gets ADJUSTED ──

    [Fact]
    public void DoMotion_ModifyRawStateBit_MirrorsOriginalId_NotAdjusted()
    {
        var interp = MakeInterp();
        var p = new MovementParameters { ModifyRawState = true, Speed = 1.0f };

        interp.DoMotion(MotionCommand.WalkBackward, p);

        Assert.Equal(MotionCommand.WalkBackward, interp.RawState.ForwardCommand);
        // Interpreted state, meanwhile, adopted the ADJUSTED (WalkForward) id.
        Assert.Equal(MotionCommand.WalkForward, interp.InterpretedState.ForwardCommand);
    }

    [Fact]
    public void DoMotion_ModifyRawStateBitClear_DoesNotMirror()
    {
        var interp = MakeInterp();
        var p = new MovementParameters { ModifyRawState = false };

        interp.DoMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
    }

    [Fact]
    public void DoMotion_FailedDispatch_DoesNotMirrorToRawState()
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x80000001u;
        var p = new MovementParameters { ModifyRawState = true };

        var result = interp.DoMotion(MotionCommand.Crouch, p);

        Assert.Equal(WeenieError.CrouchInCombatStance, result);
        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand); // untouched
    }

    [Fact]
    public void DoMotion_2Arg_AppOverload_StillCompiles_AndDispatches()
    {
        var interp = MakeInterp();

        var result = interp.DoMotion(MotionCommand.WalkForward, 0.8f);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.WalkForward, interp.InterpretedState.ForwardCommand);
    }


    [Fact]
    public void StopMotion_NullPhysicsObj_Returns8()
    {
        var interp = new MotionInterpreter();

        var result = interp.StopMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }

    [Fact]
    public void StopMotion_NoCombatStanceGate_CrouchStopsEvenInCombatStance()
    {
        var interp = MakeInterp();
        interp.InterpretedState.CurrentStyle = 0x80000001u;
        interp.InterpretedState.ForwardCommand = MotionCommand.Crouch;

        var result = interp.StopMotion(MotionCommand.Crouch, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void StopMotion_ModifyRawStateBit_RemovesOriginalId_NotAdjusted()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand = MotionCommand.WalkBackward;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        var p = new MovementParameters { ModifyRawState = true };

        interp.StopMotion(MotionCommand.WalkBackward, p);

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
    }

    [Fact]
    public void StopMotion_ModifyRawStateBitClear_DoesNotMirror()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        var p = new MovementParameters { ModifyRawState = false };

        interp.StopMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.WalkForward, interp.RawState.ForwardCommand); // untouched
    }

    [Fact]
    public void StopMotion_2Arg_AppOverload_StillCompiles()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;

        var result = interp.StopMotion(MotionCommand.WalkForward);

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
    }


    [Fact]
    public void StopCompletely_ZeroesForwardSidestepTurnCommands_OnBothStates()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand = MotionCommand.RunForward;
        interp.RawState.SidestepCommand = MotionCommand.SideStepRight;
        interp.RawState.TurnCommand = MotionCommand.TurnRight;
        interp.InterpretedState.ForwardCommand = MotionCommand.RunForward;
        interp.InterpretedState.SideStepCommand = MotionCommand.SideStepRight;
        interp.InterpretedState.TurnCommand = MotionCommand.TurnRight;

        interp.StopCompletely();

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
        Assert.Equal(1.0f, interp.RawState.ForwardSpeed);
        Assert.Equal(0u, interp.RawState.SidestepCommand);
        Assert.Equal(0u, interp.RawState.TurnCommand);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
        Assert.Equal(1.0f, interp.InterpretedState.ForwardSpeed);
        Assert.Equal(0u, interp.InterpretedState.SideStepCommand);
        Assert.Equal(0u, interp.InterpretedState.TurnCommand);
    }

    [Fact]
    public void StopCompletely_DoesNotTouchSidestepOrTurnSpeeds()
    {
        var interp = MakeInterp();
        interp.RawState.SidestepSpeed = 2.5f;
        interp.RawState.TurnSpeed = 3.5f;
        interp.InterpretedState.SideStepSpeed = 4.5f;
        interp.InterpretedState.TurnSpeed = 5.5f;

        interp.StopCompletely();

        Assert.Equal(2.5f, interp.RawState.SidestepSpeed);
        Assert.Equal(3.5f, interp.RawState.TurnSpeed);
        Assert.Equal(4.5f, interp.InterpretedState.SideStepSpeed);
        Assert.Equal(5.5f, interp.InterpretedState.TurnSpeed);
    }

    [Fact]
    public void StopCompletely_ZerosPhysicsBodyVelocity()
    {
        var body = MakeGrounded();
        body.set_velocity(new Vector3(5f, 3f, 0f));
        var interp = MakeInterp(body);

        interp.StopCompletely();

        Assert.Equal(Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void StopCompletely_InterruptsCurrentMovement()
    {
        var interp = MakeInterp();
        bool fired = false;
        interp.InterruptCurrentMovement = () => fired = true;

        interp.StopCompletely();

        Assert.True(fired);
    }

    [Fact]
    public void StopCompletely_QueuedNodeErrorCode_ReflectsPreStopForwardCommand()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Crouch;

        interp.StopCompletely();

        var node = Assert.Single(interp.PendingMotions);
        Assert.Equal(0u, node.ContextId);
        Assert.Equal(MotionCommand.Ready, node.Motion);
        Assert.Equal((uint)WeenieError.YouCantJumpFromThisPosition, node.JumpErrorCode);
    }

    [Fact]
    public void StopCompletely_QueuedNodeErrorCode_ZeroWhenPreStopCommandAllowsJump()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready; // passes motion_allows_jump

        interp.StopCompletely();

        var node = Assert.Single(interp.PendingMotions);
        Assert.Equal(0u, node.JumpErrorCode);
    }

    [Fact]
    public void StopCompletely_CellNull_FiresRemoveLinkAnimations()
    {
        var interp = MakeInterp();
        bool fired = false;
        interp.RemoveLinkAnimations = () => fired = true;

        interp.StopCompletely();

        Assert.True(fired, "physics_obj->cell == 0 must fire RemoveLinkAnimations");
    }

    [Fact]
    public void StopCompletely_CellNonNull_DoesNotFireRemoveLinkAnimations()
    {
        var body = MakeGrounded();
        body.SnapToCell(0x12340001u, Vector3.Zero, Vector3.Zero);
        var interp = MakeInterp(body);
        bool fired = false;
        interp.RemoveLinkAnimations = () => fired = true;

        interp.StopCompletely();

        Assert.False(fired);
    }

    [Fact]
    public void StopCompletely_AlwaysReturnsNone_OnceCallExecutes()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Crouch; // would fail motion_allows_jump

        var result = interp.StopCompletely();

        Assert.Equal(WeenieError.None, result); // StopCompletely itself always succeeds
    }


    private sealed class RecordingSink : IInterpretedMotionSink
    {
        public readonly List<string> Calls = new();
        public bool ApplyMotion(uint motion, float speed)
        {
            Calls.Add($"APPLY {motion:x8}@{speed:F2}");
            return true;
        }
        public bool StopMotion(uint motion)
        {
            Calls.Add($"STOP {motion:x8}");
            return true;
        }
    }

    [Fact]
    public void DoInterpretedMotion_StandingLongJumpCharging_WalkForward_StateOnly_NoDispatch_NoQueue()
    {
        var interp = MakeInterp();
        interp.StandingLongJump = true;
        interp.DefaultSink = null; // no App-level sink wired directly on DoInterpretedMotion path

        var before = interp.PendingMotions.Count();
        var result = interp.DoInterpretedMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.WalkForward, interp.InterpretedState.ForwardCommand);
        Assert.Equal(before, interp.PendingMotions.Count());
    }

    [Fact]
    public void DoInterpretedMotion_StandingLongJumpCharging_RunForward_StateOnly_NoDispatch_NoQueue()
    {
        var interp = MakeInterp();
        interp.StandingLongJump = true;

        var before = interp.PendingMotions.Count();
        var result = interp.DoInterpretedMotion(MotionCommand.RunForward, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.RunForward, interp.InterpretedState.ForwardCommand);
        Assert.Equal(before, interp.PendingMotions.Count());
    }

    [Fact]
    public void DoInterpretedMotion_StandingLongJumpCharging_SideStepRight_StateOnly_NoDispatch_NoQueue()
    {
        var interp = MakeInterp();
        interp.StandingLongJump = true;

        var before = interp.PendingMotions.Count();
        var result = interp.DoInterpretedMotion(MotionCommand.SideStepRight, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
        Assert.Equal(MotionCommand.SideStepRight, interp.InterpretedState.SideStepCommand);
        Assert.Equal(before, interp.PendingMotions.Count());
    }

    [Fact]
    public void DoInterpretedMotion_StandingLongJumpCharging_OtherMotion_DispatchesNormally()
    {
        var interp = MakeInterp();
        interp.StandingLongJump = true;

        var result = interp.DoInterpretedMotion(MotionCommand.TurnRight, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
        Assert.Single(interp.PendingMotions);
    }

    [Fact]
    public void DoInterpretedMotion_NotCharging_WalkForward_DispatchesNormally_AndQueues()
    {
        var interp = MakeInterp();
        interp.StandingLongJump = false;

        var result = interp.DoInterpretedMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.None, result);
        Assert.Single(interp.PendingMotions);
    }

    // =========================================================================
    // DisableJumpDuringLink forces the queued node's jump_error_code to 0x48
    // =========================================================================

    [Fact]
    public void DoInterpretedMotion_DisableJumpDuringLink_ForcesQueuedNodeTo0x48()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready; // would otherwise PASS motion_allows_jump
        var p = new MovementParameters { DisableJumpDuringLink = true };

        interp.DoInterpretedMotion(MotionCommand.WalkForward, p);

        var node = Assert.Single(interp.PendingMotions);
        Assert.Equal((uint)WeenieError.YouCantJumpFromThisPosition, node.JumpErrorCode);
    }

    [Fact]
    public void DoInterpretedMotion_DisableJumpDuringLinkClear_UsesComputedErrorCode()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.Ready;
        var p = new MovementParameters { DisableJumpDuringLink = false };

        interp.DoInterpretedMotion(MotionCommand.WalkForward, p);

        var node = Assert.Single(interp.PendingMotions);
        Assert.Equal(0u, node.JumpErrorCode); // WalkForward + Ready both pass motion_allows_jump
    }

    // ── Dead -> RemoveLinkAnimations ───────────────────────────────────────

    [Fact]
    public void DoInterpretedMotion_Dead_FiresRemoveLinkAnimations()
    {
        var interp = MakeInterp();
        bool fired = false;
        interp.RemoveLinkAnimations = () => fired = true;

        interp.DoInterpretedMotion(MotionCommand.Dead, new MovementParameters());

        Assert.True(fired);
    }

    // ── ModifyInterpretedState param gates the state write ─────────────────

    [Fact]
    public void DoInterpretedMotion_ModifyInterpretedStateClear_DoesNotWriteState()
    {
        var interp = MakeInterp();
        var p = new MovementParameters { ModifyInterpretedState = false };

        interp.DoInterpretedMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand); // untouched
    }

    [Fact]
    public void DoInterpretedMotion_ModifyInterpretedStateSet_WritesState()
    {
        var interp = MakeInterp();
        var p = new MovementParameters { ModifyInterpretedState = true, Speed = 1.5f };

        interp.DoInterpretedMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.WalkForward, interp.InterpretedState.ForwardCommand);
    }


    [Fact]
    public void DoInterpretedMotion_ContactBlocked_ActionClass_Returns0x24()
    {
        var body = new PhysicsBody { State = PhysicsStateFlags.Gravity };
        var interp = MakeInterp(body);

        var result = interp.DoInterpretedMotion(0x10000060u, new MovementParameters());

        Assert.Equal(WeenieError.NotGrounded, result);
    }


    [Fact]
    public void StopInterpretedMotion_NullPhysicsObj_Returns8()
    {
        var interp = new MotionInterpreter();

        var result = interp.StopInterpretedMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(WeenieError.NoPhysicsObject, result);
    }

    [Fact]
    public void StopInterpretedMotion_Success_EnqueuesReturnToNoneNode()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        var p = new MovementParameters { ContextId = 42 };

        interp.StopInterpretedMotion(MotionCommand.WalkForward, p);

        var node = Assert.Single(interp.PendingMotions);
        Assert.Equal(42u, node.ContextId);
        Assert.Equal(MotionCommand.Ready, node.Motion);
    }

    [Fact]
    public void StopInterpretedMotion_ModifyInterpretedStateClear_DoesNotWriteState()
    {
        var interp = MakeInterp();
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        var p = new MovementParameters { ModifyInterpretedState = false };

        interp.StopInterpretedMotion(MotionCommand.WalkForward, p);

        Assert.Equal(MotionCommand.WalkForward, interp.InterpretedState.ForwardCommand); // untouched
    }


    [Fact]
    public void PerformMovement_RawCommand_FiresCheckForCompletedMotions()
    {
        var interp = MakeInterp();
        int flushCount = 0;
        interp.CheckForCompletedMotions = () => flushCount++;

        interp.PerformMovement(new MovementStruct
        {
            Type = MovementType.RawCommand,
            Motion = MotionCommand.WalkForward,
            Speed = 1.0f,
        });

        Assert.Equal(1, flushCount);
    }

    [Theory]
    [InlineData(MovementType.RawCommand)]
    [InlineData(MovementType.InterpretedCommand)]
    [InlineData(MovementType.StopRawCommand)]
    [InlineData(MovementType.StopInterpretedCommand)]
    [InlineData(MovementType.StopCompletely)]
    public void PerformMovement_EveryDispatchedOp_FiresFlushExactlyOnce(MovementType type)
    {
        var interp = MakeInterp();
        int flushCount = 0;
        interp.CheckForCompletedMotions = () => flushCount++;

        interp.PerformMovement(new MovementStruct
        {
            Type = type,
            Motion = MotionCommand.WalkForward,
            Speed = 1.0f,
        });

        Assert.Equal(1, flushCount);
    }

    [Fact]
    public void PerformMovement_InvalidType_DoesNotFireFlush()
    {
        // The type-dispatch failure (0x47, bad MovementStruct.type) happens
        // BEFORE any op is dispatched — no flush should fire for it.
        var interp = MakeInterp();
        int flushCount = 0;
        interp.CheckForCompletedMotions = () => flushCount++;

        var result = interp.PerformMovement(new MovementStruct { Type = (MovementType)99 });

        Assert.Equal(WeenieError.GeneralMovementFailure, result);
        Assert.Equal(0, flushCount);
    }

    [Fact]
    public void PerformMovement_FlushSeamUnset_DoesNotThrow()
    {
        var interp = MakeInterp();
        interp.CheckForCompletedMotions = null;

        var result = interp.PerformMovement(new MovementStruct
        {
            Type = MovementType.StopCompletely,
        });

        Assert.Equal(WeenieError.None, result);
    }

    [Fact]
    public void PerformMovement_StopCompletely_ResetsToReady_AndFlushes()
    {
        var interp = MakeInterp();
        interp.RawState.ForwardCommand = MotionCommand.WalkForward;
        interp.InterpretedState.ForwardCommand = MotionCommand.WalkForward;
        int flushCount = 0;
        interp.CheckForCompletedMotions = () => flushCount++;

        interp.PerformMovement(new MovementStruct { Type = MovementType.StopCompletely });

        Assert.Equal(MotionCommand.Ready, interp.RawState.ForwardCommand);
        Assert.Equal(MotionCommand.Ready, interp.InterpretedState.ForwardCommand);
        Assert.Equal(1, flushCount);
    }
}
