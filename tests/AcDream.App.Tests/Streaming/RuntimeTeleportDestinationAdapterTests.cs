using AcDream.App.Streaming;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime;

namespace AcDream.App.Tests.Streaming;

public sealed class RuntimeTeleportDestinationAdapterTests
{
    [Fact]
    public void AcceptedPosition_PreservesExactIdentityCellFrameAndSequences()
    {
        var update = new WorldSession.EntityPositionUpdate(
            Guid: 0x50000001u,
            Position: new CreateObject.ServerPosition(
                LandblockId: 0x30310123u,
                PositionX: -12.5f,
                PositionY: 8.25f,
                PositionZ: 91.75f,
                RotationW: 0.5f,
                RotationX: 0.25f,
                RotationY: -0.125f,
                RotationZ: 0.75f),
            Velocity: new System.Numerics.Vector3(1f, 2f, 3f),
            PlacementId: 0x65u,
            IsGrounded: false,
            InstanceSequence: 0x1234,
            PositionSequence: 0x2345,
            TeleportSequence: 0x3456,
            ForcePositionSequence: 0x4567);

        RuntimeTeleportDestination destination =
            RuntimeTeleportDestinationAdapter.FromAcceptedPosition(update);

        Assert.Equal(update.Guid, destination.EntityGuid);
        Assert.Equal(update.InstanceSequence, destination.InstanceSequence);
        Assert.Equal(update.PositionSequence, destination.PositionSequence);
        Assert.Equal(update.TeleportSequence, destination.TeleportSequence);
        Assert.Equal(
            update.ForcePositionSequence,
            destination.ForcePositionSequence);
        Assert.Equal(update.Position.LandblockId, destination.CellId);
        Assert.Equal(
            new System.Numerics.Vector3(-12.5f, 8.25f, 91.75f),
            destination.Position.Frame.Origin);
        Assert.Equal(
            new System.Numerics.Quaternion(0.25f, -0.125f, 0.75f, 0.5f),
            destination.Position.Frame.Orientation);
    }
}
