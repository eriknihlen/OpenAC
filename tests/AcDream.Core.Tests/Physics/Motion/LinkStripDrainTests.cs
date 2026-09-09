using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics.Motion;

public class LinkStripDrainTests
{
    private readonly ITestOutputHelper _out;
    public LinkStripDrainTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RemoveLinkAnimationsSeam_DrainsBothQueues()
    {
        var h = new RemoteChaseHarness(_out);

        // Drive a motion burst — walk, run, stop — the shape a player's
        // pre-jump input produces. Each successful dispatch pairs an interp
        // node with a manager node.
        var p = new MovementParameters();
        h.Interp.DoMotion(MotionCommand.WalkForward, p);
        h.Interp.set_hold_run(true, interrupt: false);
        h.Interp.StopMotion(MotionCommand.WalkForward, p);

        Assert.True(h.Interp.MotionsPending(),
            "precondition: the burst must leave pending interp nodes");
        Assert.NotEmpty(h.Seq.Manager.PendingAnimations);

        h.Interp.RemoveLinkAnimations!.Invoke();

        Assert.False(h.Interp.MotionsPending(),
            "HandleEnterWorld's drain must pop every pending interp node " +
            "(retail: each AnimationDone(0) relays MotionDone)");
        Assert.Empty(h.Seq.Manager.PendingAnimations);
    }

    [Fact]
    public void AfterSeamDrain_NewMotionsQueueAndComplete()
    {
        var h = new RemoteChaseHarness(_out);
        var p = new MovementParameters();

        // Pre-jump activity, then the jump's LeaveGround strip+drain.
        h.Interp.DoMotion(MotionCommand.WalkForward, p);
        h.Interp.RemoveLinkAnimations!.Invoke();
        Assert.False(h.Interp.MotionsPending());

        // A fresh dispatch (the armed moveto's turn) queues...
        h.Interp.DoMotion(MotionCommand.TurnRight, p);
        Assert.True(h.Interp.MotionsPending());

        while (h.Seq.Manager.PendingAnimations.GetEnumerator() is var e && e.MoveNext())
            h.Seq.Manager.AnimationDone(success: true);

        h.Seq.Manager.CheckForCompletedMotions();
        Assert.False(h.Interp.MotionsPending(),
            "the normal AnimationDone → MotionDone chain must drain the new node");
    }
}
