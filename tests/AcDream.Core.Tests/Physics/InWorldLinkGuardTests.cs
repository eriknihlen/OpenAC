using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class InWorldLinkGuardTests
{
    [Fact]
    public void InWorldBody_DispatchKeepsTransitionLinks()
    {
        var body = new PhysicsBody
        {
            InWorld = true,
            TransientState = TransientStateFlags.Contact
                           | TransientStateFlags.OnWalkable
                           | TransientStateFlags.Active,
        };
        var interp = new MotionInterpreter(body);
        int strips = 0;
        interp.RemoveLinkAnimations = () => strips++;

        interp.DoInterpretedMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(0, strips);
    }

    [Fact]
    public void DetachedBody_DispatchStripsLinks_RetailGuard()
    {
        var body = new PhysicsBody
        {
            TransientState = TransientStateFlags.Contact
                           | TransientStateFlags.OnWalkable
                           | TransientStateFlags.Active,
        };
        var interp = new MotionInterpreter(body);
        int strips = 0;
        interp.RemoveLinkAnimations = () => strips++;

        interp.DoInterpretedMotion(MotionCommand.WalkForward, new MovementParameters());

        Assert.Equal(1, strips);
    }

    [Fact]
    public void RemoteShapedBody_StopCompletely_KeepsLinksToo()
    {
        // The other two guard sites (StopCompletely / StopInterpretedMotion)
        // share the same InWorld polarity.
        var body = new PhysicsBody
        {
            InWorld = true,
            TransientState = TransientStateFlags.Contact
                           | TransientStateFlags.OnWalkable
                           | TransientStateFlags.Active,
        };
        var interp = new MotionInterpreter(body);
        int strips = 0;
        interp.RemoveLinkAnimations = () => strips++;

        interp.StopCompletely();

        Assert.Equal(0, strips);
    }
}
