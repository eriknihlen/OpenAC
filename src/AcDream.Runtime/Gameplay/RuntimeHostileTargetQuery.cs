using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Properties;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeHostileTargetSnapshot(
    uint ObjectId,
    string Name,
    uint WeenieClassId,
    float Distance,
    float RelativeAngleDegrees,
    bool IsHealthKnown,
    float HealthFraction)
{
    public int SpeciesId { get; init; }
    public int MaximumHealth { get; init; }
    public bool HasShield { get; init; }
    public ushort Incarnation { get; init; }
    public long HealthRevision { get; init; }
    public double SecondsSinceHealthUpdate { get; init; } =
        double.PositiveInfinity;
}

public static class RuntimeHostileTargetQuery
{
    public static IReadOnlyList<RuntimeHostileTargetSnapshot> Capture(
        GameRuntime runtime,
        float maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (float.IsNaN(maximumDistance) || maximumDistance <= 0f)
            return Array.Empty<RuntimeHostileTargetSnapshot>();

        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || playerRecord.Snapshot.Position is not { } playerPosition)
        {
            return Array.Empty<RuntimeHostileTargetSnapshot>();
        }

        Vector3 playerWorld = AbsolutePosition(playerPosition);
        float playerHeading = MoveToMath.GetHeading(new Quaternion(
            playerPosition.RotationX,
            playerPosition.RotationY,
            playerPosition.RotationZ,
            playerPosition.RotationW));
        float maximumDistanceSquared = maximumDistance * maximumDistance;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
        var targets = new List<RuntimeHostileTargetSnapshot>();

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

            ClientObject? candidate = objects.Get(record.ServerGuid);
            if (!CombatTargetPolicy.IsHostileMonster(
                    playerGuid,
                    player,
                    candidate))
            {
                continue;
            }

            bool hasHealth = runtime.ActionOwner.Combat.HasHealth(
                record.ServerGuid);
            float health = hasHealth
                ? runtime.ActionOwner.Combat.GetHealthPercent(record.ServerGuid)
                : 1f;
            if (hasHealth && health <= 0f)
                continue;

            Vector3 targetWorld = AbsolutePosition(position);
            Vector2 delta = new(
                targetWorld.X - playerWorld.X,
                targetWorld.Y - playerWorld.Y);
            float distanceSquared = delta.LengthSquared();
            if (distanceSquared > maximumDistanceSquared)
                continue;

            float targetHeading = MoveToMath.PositionHeading(
                playerWorld,
                targetWorld);
            float relativeAngle = NormalizeSignedDegrees(
                targetHeading - playerHeading);
            int speciesId = candidate?.Properties.GetInt(
                (uint)PropertyInt.CreatureType) ?? 0;
            bool hasShield = candidate is not null
                && objects.GetEquippedBy(candidate.ObjectId).Any(static item =>
                    (item.Type & ItemType.Armor) != 0);
            runtime.ActionOwner.TryGetHealthActivity(
                record.ServerGuid,
                out long healthRevision,
                out double healthAge);
            targets.Add(new RuntimeHostileTargetSnapshot(
                record.ServerGuid,
                candidate?.Name ?? string.Empty,
                candidate?.WeenieClassId ?? 0u,
                MathF.Sqrt(distanceSquared),
                relativeAngle,
                hasHealth,
                health)
            {
                SpeciesId = speciesId,
                MaximumHealth = 0,
                HasShield = hasShield,
                Incarnation = record.Incarnation,
                HealthRevision = healthRevision,
                SecondsSinceHealthUpdate = healthAge,
            });
        }

        return targets.Count == 0
            ? Array.Empty<RuntimeHostileTargetSnapshot>()
            : targets.ToArray();
    }

    public static uint? FindClosest(GameRuntime runtime)
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
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
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
            ClientObject? candidate = objects.Get(record.ServerGuid);
            if (!CombatTargetPolicy.IsHostileMonster(
                    playerGuid,
                    player,
                    candidate))
            {
                continue;
            }
            if (runtime.ActionOwner.Combat.HasHealth(record.ServerGuid)
                && runtime.ActionOwner.Combat.GetHealthPercent(
                    record.ServerGuid) <= 0f)
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

    public static bool IsHostile(
        GameRuntime runtime,
        uint objectId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u
            || playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record)
            || (record.FinalPhysicsState
                & (PhysicsStateFlags.Hidden
                    | PhysicsStateFlags.NoDraw)) != 0)
        {
            return false;
        }
        if (runtime.ActionOwner.Combat.HasHealth(objectId)
            && runtime.ActionOwner.Combat.GetHealthPercent(objectId) <= 0f)
        {
            return false;
        }

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        return CombatTargetPolicy.IsHostileMonster(
            playerGuid,
            objects.Get(playerGuid),
            objects.Get(objectId));
    }

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

    private static float NormalizeSignedDegrees(float degrees)
    {
        float normalized = degrees % 360f;
        if (normalized > 180f)
            normalized -= 360f;
        else if (normalized < -180f)
            normalized += 360f;
        return normalized;
    }
}
