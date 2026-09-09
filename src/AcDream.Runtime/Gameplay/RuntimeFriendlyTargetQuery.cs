using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

public static class RuntimeFriendlyTargetQuery
{
    public static uint? FindClosestOtherPlayer(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return null;
        }

        Vector3 playerWorld = AbsolutePosition(playerPosition);
        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            if (record.ServerGuid == playerGuid
                || record.Snapshot.Position is not { } position
                || (record.FinalPhysicsState
                    & (PhysicsStateFlags.Hidden
                        | PhysicsStateFlags.NoDraw)) != 0)
            {
                continue;
            }
            if (!IsPlayer(record))
                continue;

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                AbsolutePosition(position));
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static uint? FindPlayerByName(GameRuntime runtime, string name)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrEmpty(name);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return null;
        }

        Vector3 playerWorld = AbsolutePosition(playerPosition);
        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            if (record.ServerGuid == playerGuid
                || record.Snapshot.Position is not { } position
                || (record.FinalPhysicsState
                    & (PhysicsStateFlags.Hidden
                        | PhysicsStateFlags.NoDraw)) != 0
                || !IsPlayer(record)
                || !string.Equals(
                    record.Snapshot.Name,
                    name,
                    StringComparison.Ordinal))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                AbsolutePosition(position));
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static string? TryGetName(GameRuntime runtime, uint guid)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord record)
            ? record.Snapshot.Name
            : null;
    }

    /// <summary>Horizontal live-world distance from the local player.</summary>
    public static bool TryGetDistance(
        GameRuntime runtime,
        uint guid,
        out float distance)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord player)
            || player.Snapshot.Position is not { } playerPosition
            || !runtime.EntityObjects.Entities.TryGetActive(
                guid,
                out RuntimeEntityRecord target)
            || target.Snapshot.Position is not { } targetPosition
            || (target.FinalPhysicsState
                & (PhysicsStateFlags.Hidden | PhysicsStateFlags.NoDraw)) != 0)
        {
            distance = float.PositiveInfinity;
            return false;
        }

        Vector3 from = AbsolutePosition(playerPosition);
        Vector3 to = AbsolutePosition(targetPosition);
        distance = Vector2.Distance(
            new Vector2(from.X, from.Y),
            new Vector2(to.X, to.Y));
        return true;
    }

    private static bool IsPlayer(RuntimeEntityRecord record) =>
        EntityCollisionFlagsExt
            .FromPwdBitfield(record.Snapshot.ObjectDescriptionFlags ?? 0u)
            .HasFlag(EntityCollisionFlags.IsPlayer);

    private static Vector3 AbsolutePosition(
        CreateObject.ServerPosition position)
    {
        int landblockX =
            (int)((position.LandblockId >> 24) & 0xFFu);
        int landblockY =
            (int)((position.LandblockId >> 16) & 0xFFu);
        return new Vector3(
            position.PositionX + landblockX * 192f,
            position.PositionY + landblockY * 192f,
            position.PositionZ);
    }
}
