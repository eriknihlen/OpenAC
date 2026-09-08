using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class InterpretedMotionStateActionFifoTests
{
    [Fact]
    public void Default_HasEmptyActionsAndZeroCount()
    {
        var ims = InterpretedMotionState.Default();
        Assert.Empty(ims.Actions);
        Assert.Equal(0u, ims.GetNumActions());
    }

    // ── AddAction / RemoveAction / GetNumActions FIFO discipline ──────────

    [Fact]
    public void AddAction_AppendsInOrder_GetNumActionsCounts()
    {
        var ims = InterpretedMotionState.Default();
        ims.AddAction(0x1000004Bu, 1.0f, 1, autonomous: false);
        ims.AddAction(0x10000050u, 1.5f, 2, autonomous: true);

        Assert.Equal(2u, ims.GetNumActions());
        Assert.Equal((ushort)0x004Bu, ims.Actions[0].Command);
        Assert.Equal((ushort)0x0050u, ims.Actions[1].Command);
    }

    [Fact]
    public void RemoveAction_PopsHeadFirst_FifoOrder()
    {
        var ims = InterpretedMotionState.Default();
        ims.AddAction(0x1000004Bu, 1.0f, 1, autonomous: false);
        ims.AddAction(0x10000050u, 1.5f, 2, autonomous: true);

        uint first = ims.RemoveAction();
        uint second = ims.RemoveAction();

        Assert.Equal(0x004Bu, first);
        Assert.Equal(0x0050u, second);
        Assert.Equal(0u, ims.GetNumActions());
    }

    [Fact]
    public void RemoveAction_Empty_ReturnsZero()
    {
        var ims = InterpretedMotionState.Default();
        Assert.Equal(0u, ims.RemoveAction());
    }

    [Fact]
    public void GetNumActions_DefaultStruct_NoNullRef()
    {
        // Defensive: a bare default(InterpretedMotionState) (bypassing
        // .Default()) must not NRE on GetNumActions/Actions/RemoveAction —
        // the lazy-list field starts null.
        InterpretedMotionState bare = default;
        Assert.Equal(0u, bare.GetNumActions());
        Assert.Empty(bare.Actions);
        Assert.Equal(0u, bare.RemoveAction());
    }


    [Fact]
    public void GetNumActions_SixQueued_MeetsDoMotionDepthCapThreshold()
    {
        var ims = InterpretedMotionState.Default();
        for (uint i = 0; i < 6; i++)
            ims.AddAction(0x10000000u + i, 1.0f, i, false);

        Assert.Equal(6u, ims.GetNumActions());
        Assert.True(ims.GetNumActions() >= 6);
    }


    [Fact]
    public void ApplyMotion_TurnRight_SetsTurnCommandAndSpeed()
    {
        var ims = InterpretedMotionState.Default();
        var p = new MovementParameters { Speed = 1.5f };

        ims.ApplyMotion(0x6500000du, p);

        Assert.Equal(0x6500000du, ims.TurnCommand);
        Assert.Equal(1.5f, ims.TurnSpeed);
    }

    [Fact]
    public void ApplyMotion_SideStepRight_SetsSideStepCommandAndSpeed()
    {
        var ims = InterpretedMotionState.Default();
        var p = new MovementParameters { Speed = 1.248f };

        ims.ApplyMotion(0x6500000fu, p);

        Assert.Equal(0x6500000fu, ims.SideStepCommand);
        Assert.Equal(1.248f, ims.SideStepSpeed);
    }

    [Fact]
    public void ApplyMotion_ForwardClassMotion_SetsForwardCommandAndSpeed()
    {
        var ims = InterpretedMotionState.Default();
        var p = new MovementParameters { Speed = 2.94f };

        ims.ApplyMotion(0x44000007u, p); // RunForward

        Assert.Equal(0x44000007u, ims.ForwardCommand);
        Assert.Equal(2.94f, ims.ForwardSpeed);
    }

    [Fact]
    public void ApplyMotion_StyleClassMotion_ResetsForwardToReady_SetsCurrentStyle()
    {
        var ims = InterpretedMotionState.Default();
        ims.ForwardCommand = 0x45000005u;
        var p = new MovementParameters();

        ims.ApplyMotion(0x80000042u, p);

        Assert.Equal(0x41000003u, ims.ForwardCommand);
        Assert.Equal(0x80000042u, ims.CurrentStyle);
    }

    [Fact]
    public void ApplyMotion_ActionClassMotion_AddsAction()
    {
        var ims = InterpretedMotionState.Default();
        var p = new MovementParameters { Speed = 1.0f, ActionStamp = 7u, Autonomous = true };

        ims.ApplyMotion(0x1000004Bu, p);

        Assert.Equal(1u, ims.GetNumActions());
        var a = ims.Actions[0];
        Assert.Equal((ushort)0x004Bu, a.Command);
        Assert.Equal(1.0f, a.Speed);
        Assert.Equal((ushort)7u, a.Stamp);
        Assert.True(a.Autonomous);
    }


    [Fact]
    public void RemoveMotion_TurnRightExact_ClearsTurnCommand()
    {
        var ims = InterpretedMotionState.Default();
        ims.TurnCommand = 0x6500000du;
        ims.RemoveMotion(0x6500000du);
        Assert.Equal(0u, ims.TurnCommand);
    }

    [Fact]
    public void RemoveMotion_TurnLeftExact_DoesNotMatchTurnBranch_FallsThroughToStyleCheck()
    {
        var ims = InterpretedMotionState.Default();
        ims.TurnCommand = 0x6500000eu;
        ims.RemoveMotion(0x6500000eu);
        Assert.Equal(0x6500000eu, ims.TurnCommand); // untouched
    }

    [Fact]
    public void RemoveMotion_SideStepRightExact_ClearsSideStepCommand()
    {
        var ims = InterpretedMotionState.Default();
        ims.SideStepCommand = 0x6500000fu;
        ims.RemoveMotion(0x6500000fu);
        Assert.Equal(0u, ims.SideStepCommand);
    }

    [Fact]
    public void RemoveMotion_ForwardClassMotion_MatchingCommand_ResetsToReady()
    {
        var ims = InterpretedMotionState.Default();
        ims.ForwardCommand = 0x45000005u;
        ims.ForwardSpeed = 3.0f;

        ims.RemoveMotion(0x45000005u);

        Assert.Equal(0x41000003u, ims.ForwardCommand);
        Assert.Equal(1f, ims.ForwardSpeed);
    }

    [Fact]
    public void RemoveMotion_StyleClassMotion_MatchingCurrentStyle_ResetsToNonCombat()
    {
        var ims = InterpretedMotionState.Default();
        ims.CurrentStyle = 0x80000042u;

        ims.RemoveMotion(0x80000042u);

        Assert.Equal(0x8000003du, ims.CurrentStyle);
    }
}
