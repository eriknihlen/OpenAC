using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToManagerCompletionSeamTests
{
    /// <summary>Arms a position move 5 m dead ahead (heading 90 = +X,
    /// facing it) and drives the scripted body to arrival via UseTime.</summary>
    private static MoveToManagerHarness DriveToArrival()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(
            new Position(1u, new Vector3(5f, 0f, 0f), Quaternion.Identity),
            new MovementParameters { UseSpheres = false, DistanceToObject = 0.6f });
        h.DrainPendingMotions();

        for (int i = 0; i < 200 && h.Manager.IsMovingTo(); i++)
        {
            h.Tick();
            var cur = h.WorldPosition.Frame.Origin;
            h.WorldPosition = new Position(
                1u, cur + new Vector3(0.2f, 0f, 0f), Quaternion.Identity);
            h.Manager.UseTime();
            h.DrainPendingMotions();
        }

        Assert.False(h.Manager.IsMovingTo(),
            "scripted drive must reach arrival within the tick budget");
        return h;
    }

    [Fact]
    public void NaturalArrival_FiresMoveToComplete_OnceWithNone()
    {
        var h = DriveToArrival();

        Assert.Single(h.MoveToCompleteCalls);
        Assert.Equal(WeenieError.None, h.MoveToCompleteCalls[0]);
    }

    [Fact]
    public void AfterArrival_ExtraUseTimeTicks_DoNotRefire()
    {
        var h = DriveToArrival();

        for (int i = 0; i < 10; i++)
        {
            h.Tick();
            h.Manager.UseTime();
        }

        Assert.Single(h.MoveToCompleteCalls);
    }

    [Fact]
    public void CancelMoveTo_DoesNotFireMoveToComplete()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToPosition(
            new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            new MovementParameters { UseSpheres = false });
        Assert.True(h.Manager.IsMovingTo());

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.False(h.Manager.IsMovingTo());
        Assert.Empty(h.MoveToCompleteCalls);
    }

    [Fact]
    public void CancelMoveTo_FiresCancellationCompanionOnlyForAnActiveMove()
    {
        var h = new MoveToManagerHarness();
        var cancellations = new List<WeenieError>();
        h.Manager.MoveToCancelled = cancellations.Add;

        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);
        Assert.Empty(cancellations);

        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Manager.MoveToPosition(
            new Position(1u, new Vector3(20f, 0f, 0f), Quaternion.Identity),
            new MovementParameters { UseSpheres = false });
        h.Manager.CancelMoveTo(WeenieError.ObjectGone);
        h.Manager.CancelMoveTo(WeenieError.ActionCancelled);

        Assert.Equal(new[] { WeenieError.ObjectGone }, cancellations);
        Assert.Empty(h.MoveToCompleteCalls);
    }

    [Fact]
    public void StickyCompletion_FiresMoveToComplete_AfterStickToHandoff()
    {
        var h = new MoveToManagerHarness();
        h.WorldPosition = new Position(1u, Vector3.Zero, Quaternion.Identity);
        h.Heading = 90f;
        h.Manager.MoveToObject(
            objectId: 0x1000u, topLevelId: 0x1000u, radius: 0.5f, height: 1f,
            new MovementParameters { Sticky = true, DistanceToObject = 5f });
        h.DrainPendingMotions();

        var target = new Position(1u, new Vector3(1f, 0f, 0f), Quaternion.Identity);
        h.Manager.HandleUpdateTarget(
            new TargetInfo(0x1000u, TargetStatus.Ok, target, target));
        h.DrainPendingMotions();

        // Some builds need the turn/settle nodes ticked through first.
        for (int i = 0; i < 50 && h.Manager.IsMovingTo(); i++)
        {
            h.Tick();
            h.Manager.UseTime();
            h.DrainPendingMotions();
        }

        Assert.False(h.Manager.IsMovingTo());
        Assert.Single(h.StickToCalls);
        Assert.Equal((0x1000u, 0.5f, 1f), h.StickToCalls[0]);
        Assert.Single(h.MoveToCompleteCalls);
        Assert.Equal(WeenieError.None, h.MoveToCompleteCalls[0]);
    }
}
