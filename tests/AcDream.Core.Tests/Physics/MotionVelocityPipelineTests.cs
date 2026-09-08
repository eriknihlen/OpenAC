using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;


file sealed class FakeRunRateWeenie : IWeenieObject
{
    public float RunRate;
    public bool InqRunRateResult = true;
    public bool InqJumpVelocity(float extent, out float vz) { vz = 0f; return false; }
    public bool InqRunRate(out float rate) { rate = RunRate; return InqRunRateResult; }
    public bool CanJump(float extent) => true;
}

public sealed class MotionVelocityPipelineTests
{
    private const float RunRate = 2.75f;

    private const float WalkAnim  = MotionInterpreter.WalkAnimSpeed;      // 3.11999989
    private const float RunAnim   = MotionInterpreter.RunAnimSpeed;       // 4.0
    private const float SideAnim  = MotionInterpreter.SidestepAnimSpeed;  // 1.25
    private const float BackFac   = MotionInterpreter.BackwardsFactor;    // 0.649999976
    private const float MaxSide   = MotionInterpreter.MaxSidestepAnimRate;// 3.0
    // adjust_motion sidestep scale: 0.5 * (WalkAnim / SideAnim) ≈ 1.24799995
    private const float SideScale = MotionInterpreter.SidestepFactor * (WalkAnim / SideAnim);

    private static MotionInterpreter MakeInterp(float runRate)
        => new() { WeenieObj = new FakeRunRateWeenie { RunRate = runRate } };

    private static RawMotionState Raw(
        uint forward = MotionCommand.Ready, uint sidestep = 0u, uint turn = 0u,
        HoldKey hold = HoldKey.None)
        => new()
        {
            CurrentHoldKey  = hold,
            ForwardCommand  = forward,
            ForwardHoldKey  = forward != MotionCommand.Ready ? hold : HoldKey.Invalid,
            ForwardSpeed    = 1.0f,
            SidestepCommand = sidestep,
            SidestepHoldKey = sidestep != 0u ? hold : HoldKey.Invalid,
            SidestepSpeed   = 1.0f,
            TurnCommand     = turn,
            TurnHoldKey     = turn != 0u ? hold : HoldKey.Invalid,
            TurnSpeed       = 1.0f,
        };

    // ── forward ──────────────────────────────────────────────────────────────

    [Fact]
    public void RunForward_VelocityIsRunAnimSpeedTimesRunRate()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(forward: MotionCommand.WalkForward, hold: HoldKey.Run));

        var v = interp.get_state_velocity();
        Assert.Equal(RunAnim * RunRate, v.Y, 3);
        Assert.Equal(0f, v.X, 5);
    }

    [Fact]
    public void WalkForward_NoRun_VelocityIsWalkAnimSpeed()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(forward: MotionCommand.WalkForward, hold: HoldKey.None));

        var v = interp.get_state_velocity();
        Assert.Equal(WalkAnim * 1.0f, v.Y, 3); // no run scaling; WalkForward @ 1.0
    }


    [Fact]
    public void RunBackward_VelocityIsNegativeWalkTimesBackwardFactorTimesRunRate()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(forward: MotionCommand.WalkBackward, hold: HoldKey.Run));

        // WalkBackward → WalkForward, speed *= -0.65; Run → *= runRate (unconditional,
        // promotion sign-gated so it stays WalkForward). v.Y = WalkAnim * (-0.65 * runRate).
        var v = interp.get_state_velocity();
        Assert.Equal(-(WalkAnim * BackFac * RunRate), v.Y, 3);
        Assert.True(v.Y < 0f, "backward velocity must be negative");
        // Matches the OLD hand-mirror (WalkAnim * 0.65 * runMul) — backward is unchanged.
    }


    [Fact]
    public void RunStrafeRight_ClampsSideStepSpeedToThree_VelocityIs3p75()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(sidestep: MotionCommand.SideStepRight, hold: HoldKey.Run));

        var v = interp.get_state_velocity();
        Assert.Equal(SideAnim * MaxSide, v.X, 3); // 1.25 * 3.0 = 3.75
        Assert.Equal(3.75f, v.X, 3);
        Assert.Equal(0f, v.Y, 5);
    }

    [Fact]
    public void RunStrafeRight_BelowClamp_VelocityIsScaledSidestep()
    {
        const float lowRun = 2.0f;
        var interp = MakeInterp(lowRun);
        interp.apply_raw_movement(Raw(sidestep: MotionCommand.SideStepRight, hold: HoldKey.Run));

        var v = interp.get_state_velocity();
        // v.X = SideAnim * (SideScale * lowRun) = 1.56 * lowRun ≈ 3.12
        Assert.Equal(SideAnim * SideScale * lowRun, v.X, 3);
    }

    [Fact]
    public void RunStrafeLeft_NegatedAndClamped_VelocityIsNeg3p75()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(sidestep: MotionCommand.SideStepLeft, hold: HoldKey.Run));

        var v = interp.get_state_velocity();
        Assert.Equal(-(SideAnim * MaxSide), v.X, 3); // -3.75
        Assert.True(v.X < 0f, "strafe-left velocity must be negative");
    }

    // ── turn normalization (the DAT MotionData supplies production omega) ────

    [Fact]
    public void RunTurnRight_InterpretedTurnSpeedIsPositiveRunTurnFactor()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(turn: MotionCommand.TurnRight, hold: HoldKey.Run));

        Assert.Equal(MotionCommand.TurnRight, interp.InterpretedState.TurnCommand);
        Assert.Equal(MotionInterpreter.RunTurnFactor, interp.InterpretedState.TurnSpeed, 5); // +1.5
    }

    [Fact]
    public void RunTurnLeft_RemapsToTurnRightWithNegativeRunTurnFactor()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(turn: MotionCommand.TurnLeft, hold: HoldKey.Run));

        // TurnLeft → TurnRight, speed *= -1; Run → *= 1.5 → -1.5.
        Assert.Equal(MotionCommand.TurnRight, interp.InterpretedState.TurnCommand);
        Assert.Equal(-MotionInterpreter.RunTurnFactor, interp.InterpretedState.TurnSpeed, 5); // -1.5
    }

    [Fact]
    public void WalkTurn_NoRun_TurnSpeedIsUnity()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw(turn: MotionCommand.TurnRight, hold: HoldKey.None));

        Assert.Equal(MotionCommand.TurnRight, interp.InterpretedState.TurnCommand);
        Assert.Equal(1.0f, interp.InterpretedState.TurnSpeed, 5); // no run factor
    }

    // ── idle ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_ReadyState_ZeroVelocity()
    {
        var interp = MakeInterp(RunRate);
        interp.apply_raw_movement(Raw()); // Ready, no sidestep/turn

        var v = interp.get_state_velocity();
        Assert.Equal(0f, v.X, 5);
        Assert.Equal(0f, v.Y, 5);
    }


    [Theory]
    [InlineData(0x10000062u)] // AttackHigh1
    [InlineData(0x10000063u)] // AttackMed1
    [InlineData(0x10000064u)] // AttackLow1
    [InlineData(0x10000186u)] // AttackHigh4 (shifted late block)
    public void AttackForwardCommand_ZeroVelocity(uint attackCommand)
    {
        var interp = MakeInterp(RunRate);
        interp.InterpretedState.ForwardCommand = attackCommand;
        interp.InterpretedState.ForwardSpeed = 0.97f;

        var v = interp.get_state_velocity();
        Assert.Equal(0f, v.X, 5);
        Assert.Equal(0f, v.Y, 5);
        Assert.Equal(0f, v.Z, 5);
    }
}
