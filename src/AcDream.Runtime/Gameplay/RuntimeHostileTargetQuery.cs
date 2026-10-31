using System.Numerics;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

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

    /// <summary>The creature's maximum health when the client knows it; 0 until a host learns it.</summary>
    public int MaximumHealth { get; init; }

    public bool HasShield { get; init; }
    public ushort Incarnation { get; init; }
    public long HealthRevision { get; init; }
    public double SecondsSinceHealthUpdate { get; init; } =
        double.PositiveInfinity;

    /// <summary>How far above (positive) or below the player the target stands, in metres.</summary>
    public float HeightDifference { get; init; }

    /// <summary>
    /// True once the client has been told this creature died. This does not
    /// depend on the creature being selected or on its health ever having
    /// been asked for, so it reads the same on a host that draws the world
    /// and on one that does not.
    /// </summary>
    public bool IsDead { get; init; }
}

/// <summary>
/// How much of a monster a caller wants to hear about.
/// </summary>
public enum HostileTargetScope
{
    /// <summary>
    /// The classification alone — attackable, not the player, not another
    /// player, not a pet. A creature that cannot be seen, or whose health has
    /// reached zero, is still in the answer: an automation client decides for
    /// itself when such a monster stops being worth attacking, and it needs
    /// the object in the list to make that call.
    /// </summary>
    Classified,

    /// <summary>
    /// The classification plus the two terms a person picking a target
    /// expects: the creature can be seen, and it is not already dead. This is
    /// what a select-nearest key or an auto-target means by "a monster".
    /// </summary>
    Selectable,
}

/// <summary>
/// The hostile-monster view a client scans. Every entry point answers the
/// same predicate for a given <see cref="HostileTargetScope"/>, so a caller
/// can never pick a target that <see cref="IsHostile"/> would then refuse at
/// the same scope. At the selectable scope the scans are stricter still: they
/// pass over what the selection keys skip, which <see cref="IsHostile"/> and
/// the attack check do not.
/// </summary>
public static class RuntimeHostileTargetQuery
{
    private static bool IsEligible(
        uint playerGuid,
        ClientObject? player,
        RuntimeEntityRecord record,
        ClientObject? candidate,
        GameRuntime runtime,
        HostileTargetScope scope)
    {
        if (record.ServerGuid == playerGuid
            || record.Snapshot.Position is null
            || !CombatTargetPolicy.IsHostileMonster(playerGuid, player, candidate))
        {
            return false;
        }
        return scope == HostileTargetScope.Classified
            || CanBeSeenAndIsAlive(record, runtime);
    }

    /// <summary>
    /// What a scan for a monster to select also passes over: a cloaked
    /// creature and one hidden from the UI, the two the selection keys skip.
    /// Only the scans apply it. A creature like that the player already has
    /// selected may still be attacked, and is still a hostile to the walk.
    /// </summary>
    private static bool ScanPassesOver(
        RuntimeEntityRecord record,
        ClientObject? candidate,
        HostileTargetScope scope)
        => scope == HostileTargetScope.Selectable
            && SelectionEligibility.IsHiddenFromSelection(
                record.FinalPhysicsState,
                candidate?.PublicWeenieBitfield);

    /// <summary>
    /// The two terms a person picking a target expects of anything they pick:
    /// it can be seen, and it is not already dead.
    /// </summary>
    private static bool CanBeSeenAndIsAlive(
        RuntimeEntityRecord record,
        GameRuntime runtime)
    {
        if ((record.FinalPhysicsState
            & (PhysicsStateFlags.Hidden | PhysicsStateFlags.NoDraw)) != 0)
        {
            return false;
        }
        if (runtime.ActionOwner.CreatureDeath.IsDead(
                record.ServerGuid,
                record.Incarnation))
        {
            return false;
        }
        return !runtime.ActionOwner.Combat.HasHealth(record.ServerGuid)
            || runtime.ActionOwner.Combat.GetHealthPercent(record.ServerGuid) > 0f;
    }

    /// <summary>
    /// Whether an object a player has picked out may be attacked. This is
    /// wider than <see cref="IsHostile"/>, which answers only for monsters:
    /// another player sharing a player-killer status may be attacked when
    /// selected, and is never acquired automatically.
    /// </summary>
    public static bool IsAttackableSelection(GameRuntime runtime, uint objectId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u
            || playerGuid == 0u
            || objectId == playerGuid
            || !runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record)
            || record.Snapshot.Position is null)
        {
            return false;
        }

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        return SelectedObjectHealthPolicy.ObjectIsAttackable(
                playerGuid,
                objects.Get(playerGuid),
                objectId,
                objects.Get(objectId))
            && CanBeSeenAndIsAlive(record, runtime);
    }

    public static IReadOnlyList<RuntimeHostileTargetSnapshot> Capture(
        GameRuntime runtime,
        float maximumDistance,
        HostileTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (float.IsNaN(maximumDistance) || maximumDistance <= 0f)
            return Array.Empty<RuntimeHostileTargetSnapshot>();

        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !TryGetPlayerWorld(runtime, playerRecord, out Vector3 playerWorld, out float playerHeading))
        {
            return Array.Empty<RuntimeHostileTargetSnapshot>();
        }
        float maximumDistanceSquared = maximumDistance * maximumDistance;
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
        var targets = new List<RuntimeHostileTargetSnapshot>();

        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            ClientObject? candidate = objects.Get(record.ServerGuid);
            if (!TryGetWorld(record, out Vector3 targetWorld)
                || !IsEligible(
                    playerGuid,
                    player,
                    record,
                    candidate,
                    runtime,
                    scope)
                || ScanPassesOver(record, candidate, scope))
            {
                continue;
            }

            bool hasHealth = runtime.ActionOwner.Combat.HasHealth(
                record.ServerGuid);
            float health = hasHealth
                ? runtime.ActionOwner.Combat.GetHealthPercent(record.ServerGuid)
                : 1f;

            // The straight-line distance, height included: a monster on
            // the floor above is not two metres away because it is
            // overhead. The height itself is reported too, so a caller
            // can leave other floors alone altogether.
            Vector3 delta = targetWorld - playerWorld;
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
                HeightDifference = delta.Z,
                IsDead = runtime.ActionOwner.CreatureDeath.IsDead(
                    record.ServerGuid,
                    record.Incarnation),
            });
        }

        return targets.Count == 0
            ? Array.Empty<RuntimeHostileTargetSnapshot>()
            : targets.ToArray();
    }

    public static uint? FindClosest(
        GameRuntime runtime,
        HostileTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                playerGuid,
                out RuntimeEntityRecord playerRecord)
            || !TryGetPlayerWorld(runtime, playerRecord, out Vector3 playerWorld, out _))
        {
            return null;
        }
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject? player = objects.Get(playerGuid);
        uint? closest = null;
        float closestDistanceSquared = float.PositiveInfinity;
        foreach (RuntimeEntityRecord record
            in runtime.EntityObjects.Entities.ActiveRecords)
        {
            ClientObject? candidate = objects.Get(record.ServerGuid);
            if (!TryGetWorld(record, out Vector3 targetWorld)
                || !IsEligible(
                    playerGuid,
                    player,
                    record,
                    candidate,
                    runtime,
                    scope)
                || ScanPassesOver(record, candidate, scope))
            {
                continue;
            }

            float distanceSquared = Vector3.DistanceSquared(
                playerWorld,
                targetWorld);
            if (distanceSquared >= closestDistanceSquared)
                continue;
            closestDistanceSquared = distanceSquared;
            closest = record.ServerGuid;
        }
        return closest;
    }

    public static bool IsHostile(
        GameRuntime runtime,
        uint objectId,
        HostileTargetScope scope)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint playerGuid = runtime.PlayerIdentity.ServerGuid;
        if (objectId == 0u
            || playerGuid == 0u
            || !runtime.EntityObjects.Entities.TryGetActive(
                objectId,
                out RuntimeEntityRecord record))
        {
            return false;
        }

        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        return IsEligible(
            playerGuid,
            objects.Get(playerGuid),
            record,
            objects.Get(objectId),
            runtime,
            scope);
    }

    /// <summary>
    /// Where the local player is, in absolute Dereth metres, with its
    /// heading: the movement controller's simulated position when it has
    /// one (the body the player sees), else the server's last word.
    /// </summary>
    private static bool TryGetPlayerWorld(
        GameRuntime runtime,
        RuntimeEntityRecord playerRecord,
        out Vector3 world,
        out float headingDegrees)
    {
        RuntimeMovementSnapshot movement = runtime.Movement.Snapshot;
        if (movement.HasController && movement.Position.ObjCellId != 0u)
        {
            headingDegrees = MoveToMath.GetHeading(movement.Position.Frame.Orientation);
            return RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                playerRecord,
                out world);
        }
        if (playerRecord.Snapshot.Position is { } position)
        {
            headingDegrees = MoveToMath.GetHeading(new Quaternion(
                position.RotationX,
                position.RotationY,
                position.RotationZ,
                position.RotationW));
            return RuntimePhysicsState.TryGetAbsoluteWorldPosition(
                playerRecord,
                out world);
        }
        world = default;
        headingDegrees = 0f;
        return false;
    }

    /// <summary>
    /// Where an entity is, in absolute Dereth metres: its simulated physics
    /// body when it has one, else the server's last position. A monster
    /// walking at the player is metres from where the server last said it
    /// was; the body is what the player sees and swings at, so distance
    /// and bearing come from the same place.
    /// </summary>
    private static bool TryGetWorld(RuntimeEntityRecord record, out Vector3 world) =>
        RuntimePhysicsState.TryGetAbsoluteWorldPosition(record, out world);

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
