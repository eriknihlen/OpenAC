using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Gameplay;

public static class RuntimeVendorRangeQuery
{
    public static void EnforceRange(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        VendorState vendor = runtime.InventoryOwner.Vendor;
        uint vendorId = vendor.VendorId;
        if (vendorId == 0u)
            return;

        RuntimeWorldTransitState transit = runtime.TransitOwner;
        if (transit.HasPendingTeleportStart || transit.IsTeleportActive)
        {
            vendor.Close();
            return;
        }

        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return;
        }

        if (!runtime.EntityObjects.Entities.TryGetActive(
                vendorId,
                out RuntimeEntityRecord vendorRecord)
            || vendorRecord.Snapshot.Position is not { } vendorPosition)
        {
            vendor.Close();
            return;
        }

        float useRadius = vendorRecord.Snapshot.UseRadius ?? 0f;

        float playerRadius =
            runtime.EntityObjects.Physics.ResolveObjectTableHost(playerGuid)
                ?.Radius ?? 0f;
        float vendorRadius =
            runtime.EntityObjects.Physics.ResolveObjectTableHost(vendorId)
                ?.Radius ?? 0f;
        bool inRange = ObjectRangeMath.ObjectsInRange(
            AbsolutePosition(playerPosition),
            playerRadius,
            0f,
            AbsolutePosition(vendorPosition),
            vendorRadius,
            0f,
            useRadius,
            useRadii: true,
            ignoreZDelta: false);

        if (!inRange)
            vendor.Close();
    }

    private static Vector3 AbsolutePosition(CreateObject.ServerPosition position)
    {
        int landblockX = (int)((position.LandblockId >> 24) & 0xFFu);
        int landblockY = (int)((position.LandblockId >> 16) & 0xFFu);
        return new Vector3(
            position.PositionX + landblockX * 192f,
            position.PositionY + landblockY * 192f,
            position.PositionZ);
    }
}
