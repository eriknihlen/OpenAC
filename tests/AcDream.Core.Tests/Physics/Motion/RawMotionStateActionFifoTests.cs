using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class RawMotionStateActionFifoTests
{
    // ── AddAction / RemoveAction / GetNumActions FIFO discipline ──────────

    [Fact]
    public void AddAction_AppendsInOrder()
    {
        var raw = new RawMotionState();
        raw.AddAction(0x1000004Bu, 1.0f, 1, autonomous: false);
        raw.AddAction(0x10000050u, 1.5f, 2, autonomous: true);

        Assert.Equal(2, raw.Actions.Count);
        Assert.Equal((ushort)0x004Bu, raw.Actions[0].Command); // widened to ushort on wire, verified below
        Assert.Equal((ushort)0x0050u, raw.Actions[1].Command);
    }

    [Fact]
    public void RemoveAction_PopsHeadFirst_FifoOrder()
    {
        var raw = new RawMotionState();
        raw.AddAction(0x1000004Bu, 1.0f, 1, autonomous: false);
        raw.AddAction(0x10000050u, 1.5f, 2, autonomous: true);

        uint first = raw.RemoveAction();
        uint second = raw.RemoveAction();

        Assert.Equal(0x004Bu, first);  // head popped first (FIFO)
        Assert.Equal(0x0050u, second);
        Assert.Empty(raw.Actions);
    }

    [Fact]
    public void RemoveAction_Empty_ReturnsZero()
    {
        var raw = new RawMotionState();
        Assert.Equal(0u, raw.RemoveAction());
    }

    [Fact]
    public void AddAction_StoresSpeedStampAutonomous()
    {
        var raw = new RawMotionState();
        raw.AddAction(0x1000004Bu, speed: 2.5f, actionStamp: 0x7FFFu, autonomous: true);

        var a = raw.Actions[0];
        Assert.Equal(2.5f, a.Speed);
        Assert.Equal((ushort)0x7FFFu, a.Stamp);
        Assert.True(a.Autonomous);
    }


    [Fact]
    public void ApplyMotion_TurnRight_SetsTurnCommandAndSpeed_HonorsSetHoldKeyBit()
    {
        var raw = new RawMotionState();
        var p = new MovementParameters { Speed = 1.5f, SetHoldKey = true };

        raw.ApplyMotion(0x6500000du, p); // TurnRight

        Assert.Equal(0x6500000du, raw.TurnCommand);
        Assert.Equal(1.5f, raw.TurnSpeed);
        Assert.Equal(HoldKey.Invalid, raw.TurnHoldKey); // SetHoldKey bit set -> Invalid
    }

    [Fact]
    public void ApplyMotion_TurnRight_SetHoldKeyClear_UsesHoldKeyToApply()
    {
        var raw = new RawMotionState();
        var p = new MovementParameters { Speed = 1.5f, SetHoldKey = false, HoldKeyToApply = HoldKey.Run };

        raw.ApplyMotion(0x6500000du, p);

        Assert.Equal(HoldKey.Run, raw.TurnHoldKey);
    }

    [Fact]
    public void ApplyMotion_SideStepRight_SetsSidestepCommandAndSpeed()
    {
        var raw = new RawMotionState();
        var p = new MovementParameters { Speed = 1.248f, SetHoldKey = true };

        raw.ApplyMotion(0x6500000fu, p); // SideStepRight

        Assert.Equal(0x6500000fu, raw.SidestepCommand);
        Assert.Equal(1.248f, raw.SidestepSpeed);
        Assert.Equal(HoldKey.Invalid, raw.SidestepHoldKey);
    }

    [Fact]
    public void ApplyMotion_ForwardClassMotion_SetsForwardCommandAndSpeed()
    {
        var raw = new RawMotionState();
        var p = new MovementParameters { Speed = 1.0f, SetHoldKey = true };

        raw.ApplyMotion(0x45000005u, p);

        Assert.Equal(0x45000005u, raw.ForwardCommand);
        Assert.Equal(1.0f, raw.ForwardSpeed);
        Assert.Equal(HoldKey.Invalid, raw.ForwardHoldKey);
    }

    [Fact]
    public void ApplyMotion_RunForwardExactId_ForwardClassButExcluded_NoWrite()
    {
        var raw = new RawMotionState();
        var before = raw.ForwardCommand;
        var p = new MovementParameters { Speed = 3.0f };

        raw.ApplyMotion(0x44000007u, p);

        Assert.Equal(before, raw.ForwardCommand); // untouched
        Assert.Equal(1.0f, raw.ForwardSpeed);
    }

    [Fact]
    public void ApplyMotion_StyleClassMotion_SetsCurrentStyleAndResetsForwardToReady()
    {
        var raw = new RawMotionState { ForwardCommand = 0x45000005u };
        var p = new MovementParameters();

        raw.ApplyMotion(0x80000042u, p);

        Assert.Equal(0x41000003u, raw.ForwardCommand); // reset to Ready
        Assert.Equal(0x80000042u, raw.CurrentStyle);
    }

    [Fact]
    public void ApplyMotion_StyleClassMotion_SameAsCurrentStyle_NoOp()
    {
        var raw = new RawMotionState { CurrentStyle = 0x80000042u, ForwardCommand = 0x45000005u };
        var p = new MovementParameters();

        raw.ApplyMotion(0x80000042u, p);

        Assert.Equal(0x45000005u, raw.ForwardCommand);
        Assert.Equal(0x80000042u, raw.CurrentStyle);
    }

    [Fact]
    public void ApplyMotion_ActionClassMotion_AddsAction()
    {
        var raw = new RawMotionState();
        var p = new MovementParameters { Speed = 1.0f, ActionStamp = 42u, Autonomous = true };

        raw.ApplyMotion(0x1000004Bu, p); // Jumpup action id

        Assert.Single(raw.Actions);
        var a = raw.Actions[0];
        Assert.Equal((ushort)0x004Bu, a.Command);
        Assert.Equal(1.0f, a.Speed);
        Assert.Equal((ushort)42u, a.Stamp);
        Assert.True(a.Autonomous);
    }


    [Fact]
    public void RemoveMotion_TurnRange_ClearsTurnCommand()
    {
        var raw = new RawMotionState { TurnCommand = 0x6500000du };
        raw.RemoveMotion(0x6500000du);
        Assert.Equal(0u, raw.TurnCommand);
    }

    [Fact]
    public void RemoveMotion_TurnLeftRange_ClearsTurnCommand()
    {
        var raw = new RawMotionState { TurnCommand = 0x6500000eu };
        raw.RemoveMotion(0x6500000eu);
        Assert.Equal(0u, raw.TurnCommand);
    }

    [Fact]
    public void RemoveMotion_SidestepRange_ClearsSidestepCommand()
    {
        var raw = new RawMotionState { SidestepCommand = 0x6500000fu };
        raw.RemoveMotion(0x6500000fu);
        Assert.Equal(0u, raw.SidestepCommand);
    }

    [Fact]
    public void RemoveMotion_ForwardClassMotion_MatchingCommand_ResetsToReady()
    {
        var raw = new RawMotionState { ForwardCommand = 0x45000005u, ForwardSpeed = 3.0f };
        raw.RemoveMotion(0x45000005u);

        Assert.Equal(0x41000003u, raw.ForwardCommand);
        Assert.Equal(1f, raw.ForwardSpeed);
    }

    [Fact]
    public void RemoveMotion_ForwardClassMotion_NonMatchingCommand_NoOp()
    {
        var raw = new RawMotionState { ForwardCommand = 0x45000005u, ForwardSpeed = 3.0f };
        raw.RemoveMotion(0x44000007u);

        Assert.Equal(0x45000005u, raw.ForwardCommand); // untouched
        Assert.Equal(3.0f, raw.ForwardSpeed);
    }

    [Fact]
    public void RemoveMotion_StyleClassMotion_MatchingCurrentStyle_ResetsToNonCombat()
    {
        var raw = new RawMotionState { CurrentStyle = 0x80000042u };
        raw.RemoveMotion(0x80000042u);

        Assert.Equal(0x8000003du, raw.CurrentStyle); // reset to NonCombat
    }

    [Fact]
    public void RemoveMotion_StyleClassMotion_NonMatchingCurrentStyle_NoOp()
    {
        var raw = new RawMotionState { CurrentStyle = 0x80000042u };
        raw.RemoveMotion(0x80000099u); // different style id

        Assert.Equal(0x80000042u, raw.CurrentStyle); // untouched
    }
}
