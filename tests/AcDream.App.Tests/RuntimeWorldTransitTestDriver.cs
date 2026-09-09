using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.App.Tests;

internal static class RuntimeWorldTransitTestDriver
{
    public static long BeginPortal(
        RuntimeWorldTransitState transit,
        uint destinationCell,
        ushort sequence = 1)
    {
        AcceptPortalDestination(transit, destinationCell, sequence);
        Assert.True(transit.TryBeginPortalReveal(
            sequence,
            destinationCell,
            out long generation));
        return generation;
    }

    public static void AcceptPortalDestination(
        RuntimeWorldTransitState transit,
        uint destinationCell,
        ushort sequence = 1)
    {
        Assert.True(transit.TryQueueTeleportStart(sequence));
        Assert.True(transit.ActivateQueuedTeleport());
        Assert.True(transit.OfferTeleportDestination(
            new RuntimeTeleportDestination(
                EntityGuid: 0x50000001u,
                InstanceSequence: 1,
                PositionSequence: 1,
                TeleportSequence: sequence,
                ForcePositionSequence: 1,
                Position: new Position(
                    destinationCell,
                    Vector3.Zero,
                    Quaternion.Identity)),
            teleportTimestampAdvanced: true));
    }

    public static bool MaterializePortal(
        RuntimeWorldTransitState transit,
        long generation,
        uint destinationCell,
        ushort sequence = 1) =>
        transit.AcknowledgePortalMaterialized(
            generation,
            sequence,
            destinationCell);
}
