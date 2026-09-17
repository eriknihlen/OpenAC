using System.Numerics;
using AcDream.App.Streaming;
using AcDream.Core.Physics;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using Xunit;

namespace AcDream.App.Tests.Streaming;

public sealed partial class LocalPlayerTeleportControllerTests
{
    // A recall whose destination landblock is already resident (the one the
    // character stands in, or the neighbour it just walked out of) has no
    // landblock load to wait for; the arrival must land on the first ready
    // tick rather than wait for a wake that cannot come.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecondTeleportIntoAResidentLandblock_LandsWithoutALoad(bool sameLandblock)
    {
        var order = new List<string>();
        var harness = new Harness(order: order);
        harness.CommitLandblockCollision(0x20220000u);

        CompleteTransit(harness, 9, 0x20220001u, new Vector3(4f, 5f, 5f));
        harness.Placement.Called = false;
        order.Clear();

        uint destination = sameLandblock ? 0x20220001u : 0x20210001u;
        harness.Controller.OnTeleportStarted(10);
        harness.OfferDestination(
            Position(destination, 10, 30f, 31f, 5f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(0.016f);

        Assert.True(harness.Placement.Called, string.Join("\n", order));
        Assert.Equal(new Vector3(30f, 31f, 5f), harness.Movement.Controller!.Position);
        Assert.Equal(2, harness.Reveal.PortalMaterializationCount);
        Assert.DoesNotContain(order, line => line.Contains("invariant-failure"));
    }

    // eriknihlen/OpenAC#127: the server's frame is inside something the world
    // grew after it was measured. The validated sweep refuses it on every
    // tick; the client used to hold portal space forever. Retail ignores the
    // refusal, so the arrival is forced into the cell and the transit runs on.
    [Fact]
    public void RefusedPlacement_LandsAtTheServerFrameAndTheTransitCompletes()
    {
        var order = new List<string>();
        var harness = new Harness(order: order);
        harness.Lifetime.Physics.Engine.TransitionCellCollisionTestHook =
            static (_, phase, _, observed) => phase
                is TransitionCellCollisionPhase.Objects
                    ? TransitionState.Collided
                    : observed;
        harness.Controller.OnTeleportStarted(50);
        harness.OfferDestination(
            Position(0x20210001u, 50, 11f, 12f, 5f),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;

        harness.Controller.Tick(0.016f);

        Assert.True(harness.Placement.Called, string.Join("\n", order));
        Assert.True(harness.AcceptedPositionDrive.LastPortalArrivalWasForced);
        Assert.Equal(new Vector3(11f, 12f, 5f), harness.Movement.Controller!.Position);
        Assert.Equal(1, harness.Reveal.PortalMaterializationCount);
        Assert.Equal(0, harness.AcceptedPositionDrive.PendingCount);

        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);

        Assert.False(harness.Controller.IsActive);
        Assert.True(harness.Reveal.Snapshot.Completed);
        Assert.Equal(1, harness.Session.LoginCompleteCount);
    }

    private static void CompleteTransit(
        Harness harness,
        ushort sequence,
        uint destinationCell,
        Vector3 position)
    {
        harness.Controller.OnTeleportStarted(sequence);
        harness.OfferDestination(
            Position(destinationCell, sequence, position.X, position.Y, position.Z),
            teleportTimestampAdvanced: true);
        harness.Presentation.EmitPlaceWhenReady = true;
        harness.Controller.Tick(0.016f);
        Assert.True(harness.Placement.Called);
        harness.Presentation.Enqueue(TeleportAnimEvent.PlayExitSound);
        harness.Controller.Tick(0.016f);
        harness.Presentation.Enqueue(TeleportAnimEvent.FireLoginComplete);
        harness.Controller.Tick(0.016f);
        Assert.False(harness.Controller.IsActive);
        harness.DrainPlacementFifo();
        harness.AcceptedPositionDrive.Advance();
    }
}
