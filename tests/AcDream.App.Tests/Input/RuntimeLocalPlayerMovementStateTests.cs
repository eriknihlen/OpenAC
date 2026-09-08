using AcDream.App.Input;
using AcDream.Core.Physics;

namespace AcDream.App.Tests.Input;

public sealed class RuntimeLocalPlayerMovementStateTests
{
    [Fact]
    public void MotionPreparation_RoutesCompletionWithoutPublishingController()
    {
        var slot = new RuntimeLocalPlayerMovementState();
        ILocalPlayerMotionSource source = slot;
        var controller = new PlayerMovementController(new PhysicsEngine());
        controller.Motion.AddToQueue(0, MotionCommand.Ready, 0);

        using (slot.BeginMotionPreparation(controller))
        {
            Assert.Null(slot.Controller);
            MotionInterpreter? owner = source.Motion;
            Assert.Same(controller.Motion, owner);

            owner!.MotionDone(MotionCommand.Ready, success: true);
            Assert.False(controller.Motion.MotionsPending());
        }

        Assert.Null(source.Motion);
    }

    [Fact]
    public void MotionPreparation_HandsOffToCommittedControllerOnDispose()
    {
        var slot = new RuntimeLocalPlayerMovementState();
        ILocalPlayerMotionSource source = slot;
        var controller = new PlayerMovementController(new PhysicsEngine());

        using (slot.BeginMotionPreparation(controller))
        {
            slot.Controller = controller;
            Assert.Same(controller.Motion, source.Motion);
        }

        Assert.Same(controller.Motion, source.Motion);
    }

    [Fact]
    public void MotionPreparation_DrainsPriorAnimationBeforePublishingCandidate()
    {
        var slot = new RuntimeLocalPlayerMovementState();
        ILocalPlayerMotionSource source = slot;
        var prior = new PlayerMovementController(new PhysicsEngine());
        var candidate = new PlayerMovementController(new PhysicsEngine());
        slot.Controller = prior;
        var order = new List<string>();

        using (slot.BeginMotionPreparation(
            candidate,
            () =>
            {
                order.Add("drain");
                Assert.Same(prior.Motion, source.Motion);
            }))
        {
            order.Add("candidate");
            Assert.Same(candidate.Motion, source.Motion);
        }

        Assert.Equal(["drain", "candidate"], order);
        Assert.Same(prior.Motion, source.Motion);
    }

    [Fact]
    public void MotionPreparation_RejectsConcurrentCandidate()
    {
        var slot = new RuntimeLocalPlayerMovementState();
        var first = new PlayerMovementController(new PhysicsEngine());
        var second = new PlayerMovementController(new PhysicsEngine());

        using IDisposable preparation = slot.BeginMotionPreparation(first);

        Assert.Throws<InvalidOperationException>(
            () => slot.BeginMotionPreparation(second));
    }
}
