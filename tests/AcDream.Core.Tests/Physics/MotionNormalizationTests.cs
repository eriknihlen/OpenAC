using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;


/// <summary>
/// Fake WeenieObject for injecting a known run rate into apply_run_to_command /
/// adjust_motion without a real weenie.
/// </summary>
file sealed class FakeRunRateWeenie : IWeenieObject
{
    public float RunRate;
    public bool InqRunRateResult = true;

    public bool InqJumpVelocity(float extent, out float vz)
    {
        vz = 0f;
        return false;
    }

    public bool InqRunRate(out float rate)
    {
        rate = RunRate;
        return InqRunRateResult;
    }

    public bool CanJump(float extent) => true;
}

public sealed class MotionNormalizationTests
{
    private const float SidestepScale = 0.5f * (3.11999989f / MotionInterpreter.SidestepAnimSpeed);

    private static MotionInterpreter MakeInterp(IWeenieObject? weenie = null)
    {
        return new MotionInterpreter { WeenieObj = weenie };
    }


    [Fact]
    public void AdjustMotion_WalkBackward_RemapsToWalkForward_NegatesAndScalesSpeed()
    {
        var interp = MakeInterp();
        uint motion = MotionCommand.WalkBackward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.Equal(-MotionInterpreter.BackwardsFactor, speed, 5);
        Assert.Equal(-0.649999976f, speed, 5);
    }

    [Fact]
    public void AdjustMotion_TurnLeft_RemapsToTurnRight_NegatesSpeed()
    {
        var interp = MakeInterp();
        uint motion = MotionCommand.TurnLeft;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.TurnRight, motion);
        Assert.Equal(-1.0f, speed, 5);
    }

    [Fact]
    public void AdjustMotion_SideStepLeft_RemapsToSideStepRight_NegatesBeforeScale()
    {
        // Negate happens BEFORE the ×1.248 sidestep scale, so the net factor is -1.248.
        var interp = MakeInterp();
        uint motion = MotionCommand.SideStepLeft;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.SideStepRight, motion);
        Assert.Equal(-SidestepScale, speed, 5);
        Assert.Equal(-1.24799995f, speed, 4);
    }

    [Fact]
    public void AdjustMotion_SideStepRight_ScalesSpeed_NoNegate()
    {
        var interp = MakeInterp();
        uint motion = MotionCommand.SideStepRight;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.SideStepRight, motion);
        Assert.Equal(SidestepScale, speed, 5);
        Assert.Equal(1.24799995f, speed, 4);
    }

    [Fact]
    public void AdjustMotion_RunForward_EarlyReturns_NoScaleNoHoldKeyPath()
    {
        // GOTCHA: RunForward returns immediately -- no scale AND no holdkey promotion,
        // even when holdKey == Run (there's nothing left to promote it to, but the
        // early-return also means speed is untouched).
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.RunForward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Run);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(1.0f, speed, 5);
    }

    [Fact]
    public void AdjustMotion_NonSidestepNonTurnNonForward_Unchanged_ExceptHoldKeyPath()
    {
        var interp = MakeInterp();
        uint motion = MotionCommand.Ready;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.Ready, motion);
        Assert.Equal(1.0f, speed, 5);
    }

    // ── adjust_motion: holdKey inheritance + Run promotion ───────────────────

    [Fact]
    public void AdjustMotion_HoldKeyInvalid_FallsBackToCurrentHoldKey_PromotesWalkForwardWhenRun()
    {
        var interp = MakeInterp();
        interp.RawState.CurrentHoldKey = HoldKey.Run;
        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Invalid);

        // apply_run_to_command: WalkForward + speed>0 -> RunForward; speed *= speedMod (1.0, no weenie).
        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(1.0f, speed, 5);
    }

    [Fact]
    public void AdjustMotion_HoldKeyNone_DoesNotPromote()
    {
        var interp = MakeInterp();
        interp.RawState.CurrentHoldKey = HoldKey.Run;
        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.None);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.Equal(1.0f, speed, 5);
    }

    [Fact]
    public void AdjustMotion_HoldKeyRunPassedDirectly_PromotesWalkForward()
    {
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.75f });
        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;

        interp.adjust_motion(ref motion, ref speed, HoldKey.Run);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(2.75f, speed, 5);
    }

    // ── apply_run_to_command ──────────────────────────────────────────────────

    [Fact]
    public void ApplyRunToCommand_WalkForward_PositiveSpeed_PromotesToRunForward_ScalesByRunRate()
    {
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(2.94f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_WalkForward_NegativeSpeed_StaysWalkForward_ScalesUnconditionally()
    {
        // GOTCHA: speed *= speedMod is UNCONDITIONAL -- backward (negative speed)
        // still gets run-scaled even though it is NOT promoted to RunForward.
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.WalkForward;
        float speed = -0.649999976f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.Equal(-0.649999976f * 2.94f, speed, 4);
    }

    [Fact]
    public void ApplyRunToCommand_TurnRight_ScalesByRunTurnFactor_NoRunRateNoClamp()
    {
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.TurnRight;
        float speed = 1.0f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.TurnRight, motion);
        Assert.Equal(1.5f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_SideStepRight_BelowClamp_ScalesByRunRate()
    {
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.0f });
        uint motion = MotionCommand.SideStepRight;
        float speed = 1.0f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.SideStepRight, motion);
        Assert.Equal(2.0f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_SideStepRight_AbovePositiveClamp_ClampsToPositiveMax()
    {
        // runRate 2.94 * speed 1.248 (post adjust_motion sidestep scale) ≈ 3.669 > 3.0
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.SideStepRight;
        float speed = 1.24799995f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.SideStepRight, motion);
        Assert.Equal(3.0f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_SideStepRight_AboveNegativeClamp_ClampsToNegativeMax()
    {
        var interp = MakeInterp(new FakeRunRateWeenie { RunRate = 2.94f });
        uint motion = MotionCommand.SideStepRight;
        float speed = -1.24799995f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.SideStepRight, motion);
        Assert.Equal(-3.0f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_UsesMyRunRate_WhenInqRunRateFails()
    {
        var weenie = new FakeRunRateWeenie { RunRate = 999f, InqRunRateResult = false };
        var interp = MakeInterp(weenie);
        interp.MyRunRate = 1.5f;
        uint motion = MotionCommand.WalkForward;
        float speed = 1.0f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(MotionCommand.RunForward, motion);
        Assert.Equal(1.5f, speed, 5);
    }

    [Fact]
    public void ApplyRunToCommand_NoWeenie_SpeedModDefaultsToOne()
    {
        var interp = MakeInterp(weenie: null);
        uint motion = MotionCommand.SideStepRight;
        float speed = 1.0f;

        interp.apply_run_to_command(ref motion, ref speed);

        Assert.Equal(1.0f, speed, 5);
    }
}
