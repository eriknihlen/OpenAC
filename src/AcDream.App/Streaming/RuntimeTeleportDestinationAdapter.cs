using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Runtime;

namespace AcDream.App.Streaming;

internal static class RuntimeTeleportDestinationAdapter
{
    public static RuntimeTeleportDestination FromAcceptedPosition(
        in WorldSession.EntityPositionUpdate update)
    {
        var position = update.Position;
        return new RuntimeTeleportDestination(
            update.Guid,
            update.InstanceSequence,
            update.PositionSequence,
            update.TeleportSequence,
            update.ForcePositionSequence,
            new Position(
                position.LandblockId,
                new Vector3(
                    position.PositionX,
                    position.PositionY,
                    position.PositionZ),
                new Quaternion(
                    position.RotationX,
                    position.RotationY,
                    position.RotationZ,
                    position.RotationW)));
    }
}
