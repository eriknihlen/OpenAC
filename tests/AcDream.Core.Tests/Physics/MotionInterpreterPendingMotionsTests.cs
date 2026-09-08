using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.DBObjs;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class MotionInterpreterPendingMotionsTests
{
    private static MotionInterpreter GroundedInterp()
    {
        var body = new PhysicsBody();
        body.State |= PhysicsStateFlags.Gravity;
        body.TransientState |= TransientStateFlags.Contact
                             | TransientStateFlags.OnWalkable
                             | TransientStateFlags.Active;
        return new MotionInterpreter(body);
    }


    [Fact]
    public void AddToQueue_AppendsToTail_InOrder()
    {
        var mi = new MotionInterpreter();

        mi.AddToQueue(1, 0x41000003u, 0u);
        mi.AddToQueue(2, 0x44000007u, 0x48u);
        mi.AddToQueue(3, 0x45000005u, 0u);

        Assert.Equal(
            new[]
            {
                new MotionNode(1, 0x41000003u, 0u),
                new MotionNode(2, 0x44000007u, 0x48u),
                new MotionNode(3, 0x45000005u, 0u),
            },
            mi.PendingMotions.ToArray());
    }

    [Fact]
    public void MotionsPending_FalseWhenEmpty_TrueWhenNonEmpty()
    {
        var mi = new MotionInterpreter();
        Assert.False(mi.MotionsPending());

        mi.AddToQueue(0, MotionCommand.Ready, 0);
        Assert.True(mi.MotionsPending());
    }


    [Fact]
    public void MotionDone_PopsHead_RegardlessOfMotionIdOrSuccessFlag()
    {
        var mi = GroundedInterp();
        mi.AddToQueue(1, 0x44000007u /* RunForward */, 0u);
        mi.AddToQueue(2, 0x45000005u /* WalkForward */, 0u);

        mi.MotionDone(0xDEADBEEFu, false);

        Assert.Single(mi.PendingMotions);
        Assert.Equal(0x45000005u, mi.PendingMotions.First().Motion);
    }

    [Fact]
    public void MotionDone_EmptyQueue_NoOp()
    {
        var mi = new MotionInterpreter();
        // Should not throw.
        mi.MotionDone(0, true);
        Assert.False(mi.MotionsPending());
    }

    [Fact]
    public void MotionDone_NoPhysicsObj_NoOp()
    {
        // §1c: `if (physics_obj != 0) { ... }` — the whole body is
        // gated on a non-null physics_obj. A queued node must survive
        // MotionDone when PhysicsObj is null.
        var mi = new MotionInterpreter();
        mi.AddToQueue(0, MotionCommand.Ready, 0);

        mi.MotionDone(MotionCommand.Ready, true);

        Assert.True(mi.MotionsPending());
    }

    [Fact]
    public void MotionDone_ActionClassHead_FiresUnstickSeam_AndPopsBothActionFifos()
    {
        var mi = GroundedInterp();
        mi.InterpretedState.AddAction(0x10000062u, 1.0f, 5, false);
        mi.RawState.AddAction(0x10000062u, 1.0f, 5, false);

        bool unstickFired = false;
        mi.UnstickFromObject = () => unstickFired = true;

        mi.AddToQueue(0, 0x10000062u, 0u);

        mi.MotionDone(0x10000062u, true);

        Assert.True(unstickFired);
        Assert.Equal(0u, mi.InterpretedState.GetNumActions());
        Assert.Empty(mi.RawState.Actions);
        Assert.False(mi.MotionsPending());
    }

    [Fact]
    public void MotionDone_NonActionClassHead_DoesNotFireUnstick_OrPopActionFifos()
    {
        var mi = GroundedInterp();
        mi.InterpretedState.AddAction(0x10000062u, 1.0f, 5, false);
        mi.RawState.AddAction(0x10000062u, 1.0f, 5, false);

        bool unstickFired = false;
        mi.UnstickFromObject = () => unstickFired = true;

        mi.AddToQueue(0, MotionCommand.Ready, 0u);

        mi.MotionDone(MotionCommand.Ready, true);

        Assert.False(unstickFired);
        Assert.Equal(1u, mi.InterpretedState.GetNumActions());
        Assert.Single(mi.RawState.Actions);
    }

    [Fact]
    public void MotionDone_ActionClassHead_UnstickSeamUnset_DoesNotThrow()
    {
        // UnstickFromObject is a no-op seam (register row -> R5 StickyManager)
        // until wired — null must not throw. Needs a grounded interp so
        // MotionDone's physics_obj != null gate actually pops (§1c).
        var mi = GroundedInterp();
        mi.AddToQueue(0, 0x10000062u, 0u);

        mi.MotionDone(0x10000062u, true);

        Assert.False(mi.MotionsPending());
    }

    [Fact]
    public void MotionDone_MultipleCalls_PopInFifoOrder()
    {
        // §1c gates the pop on physics_obj != null.
        var mi = GroundedInterp();
        mi.AddToQueue(1, 0x41000003u, 0u);
        mi.AddToQueue(2, 0x44000007u, 0u);
        mi.AddToQueue(3, 0x45000005u, 0u);

        mi.MotionDone(0, true);
        Assert.Equal(new uint[] { 2, 3 }, mi.PendingMotions.Select(n => n.ContextId));

        mi.MotionDone(0, true);
        Assert.Equal(new uint[] { 3 }, mi.PendingMotions.Select(n => n.ContextId));

        mi.MotionDone(0, true);
        Assert.Empty(mi.PendingMotions);
        Assert.False(mi.MotionsPending());
    }

    // ── HandleExitWorld — §1d, same body looped ────────────────────────────

    [Fact]
    public void HandleExitWorld_DrainsWholeQueue()
    {
        var mi = new MotionInterpreter();
        mi.AddToQueue(1, 0x41000003u, 0u);
        mi.AddToQueue(2, 0x44000007u, 0u);
        mi.AddToQueue(3, 0x10000062u, 0u);

        mi.HandleExitWorld();

        Assert.Empty(mi.PendingMotions);
        Assert.False(mi.MotionsPending());
    }

    [Fact]
    public void HandleExitWorld_ActionClassNodes_PopActionFifosForEach()
    {
        var mi = GroundedInterp();
        mi.InterpretedState.AddAction(0x10000062u, 1f, 1, false);
        mi.InterpretedState.AddAction(0x10000063u, 1f, 2, false);
        mi.RawState.AddAction(0x10000062u, 1f, 1, false);
        mi.RawState.AddAction(0x10000063u, 1f, 2, false);

        mi.AddToQueue(0, 0x10000062u, 0u);
        mi.AddToQueue(0, 0x10000063u, 0u);

        mi.HandleExitWorld();

        Assert.Equal(0u, mi.InterpretedState.GetNumActions());
        Assert.Empty(mi.RawState.Actions);
    }

    [Fact]
    public void HandleExitWorld_EmptyQueue_NoOp()
    {
        var mi = new MotionInterpreter();
        mi.HandleExitWorld();
        Assert.False(mi.MotionsPending());
    }


    [Theory]
    [InlineData(true, true, MotionCommand.Ready, 0u, 0u, true)]     // grounded + idle -> true
    [InlineData(true, true, MotionCommand.Ready, 0x6500000fu, 0u, false)] // sidestep set -> false
    [InlineData(true, true, MotionCommand.Ready, 0u, 0x6500000du, false)] // turn set -> false
    [InlineData(true, true, MotionCommand.RunForward, 0u, 0u, false)] // not Ready -> false
    [InlineData(true, false, MotionCommand.Ready, 0u, 0u, false)]   // OnWalkable false -> false
    [InlineData(false, true, MotionCommand.Ready, 0u, 0u, false)]
    public void IsStandingStill_TruthTable(
        bool contact, bool onWalkable, uint fwd, uint side, uint turn, bool expected)
    {
        var body = new PhysicsBody();
        body.State |= PhysicsStateFlags.Gravity;
        if (contact) body.TransientState |= TransientStateFlags.Contact;
        if (onWalkable) body.TransientState |= TransientStateFlags.OnWalkable;
        var mi = new MotionInterpreter(body)
        {
            InterpretedState =
            {
                ForwardCommand = fwd,
                SideStepCommand = side,
                TurnCommand = turn,
            },
        };

        Assert.Equal(expected, mi.IsStandingStill());
    }

    [Fact]
    public void IsStandingStill_NoPhysicsObj_False()
    {
        var mi = new MotionInterpreter();
        Assert.False(mi.IsStandingStill());
    }

    // ── MotionAllowsJump — A1 pinned blocklist, exact boundaries ───────────

    [Theory]
    [InlineData(0x1000006fu, true)]
    [InlineData(0x10000070u, true)]
    [InlineData(0x10000078u, true)]
    [InlineData(0x1000006eu, false)] // just below lower bound -> pass
    [InlineData(0x10000079u, false)] // just above upper bound -> pass
    [InlineData(0x10000128u, true)]
    [InlineData(0x10000130u, true)]
    [InlineData(0x10000131u, true)]
    [InlineData(0x10000127u, false)]
    [InlineData(0x10000132u, false)]
    [InlineData(0x40000008u, true)]
    [InlineData(0x40000015u, false)]
    [InlineData(0x40000016u, true)]
    [InlineData(0x40000017u, true)]
    [InlineData(0x40000018u, true)]
    [InlineData(0x40000019u, false)]
    [InlineData(0x4000001eu, true)]
    [InlineData(0x40000030u, true)]
    [InlineData(0x40000039u, true)]
    [InlineData(0x4000001du, false)]
    [InlineData(0x4000003au, false)]
    [InlineData(0x41000012u, true)]
    [InlineData(0x41000013u, true)]
    [InlineData(0x41000014u, true)]
    [InlineData(0x41000011u, false)]
    [InlineData(0x41000015u, false)]
    // Everything else -> pass
    [InlineData(MotionCommand.Ready, false)]
    [InlineData(MotionCommand.Dead, false)]
    [InlineData(0x6500000du, false)] // TurnRight
    [InlineData(0x6500000fu, false)] // SideStepRight
    [InlineData(0x44000007u, false)] // RunForward
    public void MotionAllowsJump_BlocklistTable(uint motion, bool blocked)
    {
        var expected = blocked ? WeenieError.YouCantJumpFromThisPosition : WeenieError.None;
        Assert.Equal(expected, MotionInterpreter.MotionAllowsJump(motion));
    }


    private sealed class NullLoader : IAnimationLoader
    {
        public Animation? LoadAnimation(uint id) => null;
    }

    [Fact]
    public void EndToEnd_ManagerAnimationDone_PopsInterpPendingHead_Synchronously()
    {
        var interp = GroundedInterp();
        var state = new MotionState();
        var sequence = new CSequence(new NullLoader());
        var manager = new MotionTableManager(table: null, state, sequence, sink: interp);

        interp.AddToQueue(contextId: 7, motion: 0x44000007u, jumpErrorCode: 0);

        manager.AddToQueue(0x44000007u, ticks: 1);

        Assert.True(interp.MotionsPending());

        manager.AnimationDone(success: true);

        Assert.Empty(manager.PendingAnimations);
        Assert.False(interp.MotionsPending());
    }
}
