using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.Automation;

/// <summary>
/// One end of a projectile-path query: the physics identity the shadow
/// registry knows the creature by (its local entity id, not the server
/// guid), where its feet are, the cell that frame is expressed in, and how
/// tall it stands so launch and aim points can be placed on the body.
/// </summary>
internal readonly record struct ProjectilePathEndpoint(
    uint EntityId,
    Vector3 Position,
    uint CellId,
    float Height);

/// <summary>
/// Sweeps a missile-flagged sphere from the shooter to the target through
/// <see cref="PhysicsEngine"/> and reports whether anything but the target
/// stops it. This is the same collision the client runs for a live
/// projectile, so a wall, a door, a hill or a scenery object blocks the path
/// the way it would block the real shot, while other creatures, ethereal
/// objects and the shooter are ignored the way the sweep ignores them for a
/// designated-target missile.
/// </summary>
internal static class ProjectilePathProbe
{
    /// <summary>Horizontal launch speed of a war-spell arc when the caller gives none.</summary>
    public const float DefaultArcLaunchSpeed = 37.5185f;

    /// <summary>Horizontal launch speed of an arrow or bolt when the caller gives none.</summary>
    public const float DefaultMissileLaunchSpeed = 46f;

    /// <summary>Bolts and streaks fly flat; the speed only sets the sampling cadence.</summary>
    public const float StraightSpeed = 100f;

    /// <summary>Standing height assumed for a creature whose collision body cannot be measured.</summary>
    public const float FallbackHeight = 1.8f;

    private const float Gravity = 9.8f;

    /// <summary>Launch point as a fraction of the shooter's height: about the hands.</summary>
    private const float LaunchHeightFraction = 0.67f;

    /// <summary>How far ahead of the shooter's origin the projectile appears.</summary>
    private static float LaunchForward(PluginProjectilePathKind kind) => kind switch
    {
        PluginProjectilePathKind.Arc => 0.44f,
        PluginProjectilePathKind.Missile => 0.61f,
        _ => 0.66f,
    };

    /// <summary>Aim point on the target as a fraction of its height.</summary>
    public static float AimHeightFraction(PluginAttackHeight height) => height switch
    {
        PluginAttackHeight.Low => 0.2f,
        PluginAttackHeight.High => 0.8f,
        _ => 0.5f,
    };

    public static float DefaultLaunchSpeed(PluginProjectilePathKind kind) => kind switch
    {
        PluginProjectilePathKind.Arc => DefaultArcLaunchSpeed,
        PluginProjectilePathKind.Missile => DefaultMissileLaunchSpeed,
        _ => StraightSpeed,
    };

    /// <summary>
    /// Standing height of an entity from the collision shapes it registered:
    /// the top of its highest cylinder or sphere above its feet. False when
    /// the registry holds nothing measurable for it (BSP-only bodies, or an
    /// entity that is not in the world).
    /// </summary>
    public static bool TryMeasureHeight(
        PhysicsEngine physics,
        uint entityId,
        Vector3 feet,
        out float height)
    {
        ArgumentNullException.ThrowIfNull(physics);
        height = 0f;
        if (entityId == 0u)
            return false;
        IReadOnlyList<uint> cells = physics.ShadowObjects.GetOwnerCells(entityId);
        float top = float.NegativeInfinity;
        for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
        {
            IReadOnlyList<ShadowEntry> entries = physics.ShadowObjects.GetObjectsInCell(cells[cellIndex]);
            for (int index = 0; index < entries.Count; index++)
            {
                ShadowEntry entry = entries[index];
                if (entry.EntityId != entityId)
                    continue;
                float entryTop = entry.CollisionType switch
                {
                    ShadowCollisionType.Cylinder => entry.Position.Z
                        + (entry.CylHeight > 0f ? entry.CylHeight : entry.Radius * 4f),
                    ShadowCollisionType.Sphere => entry.Position.Z + entry.Radius,
                    _ => float.NegativeInfinity,
                };
                if (entryTop > top)
                    top = entryTop;
            }
        }
        if (!float.IsFinite(top))
            return false;
        height = top - feet.Z;
        return float.IsFinite(height) && height > PhysicsGlobals.EPSILON;
    }

    public static PluginProjectilePathResult Evaluate(
        PhysicsEngine physics,
        in ProjectilePathEndpoint source,
        in ProjectilePathEndpoint target,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float radius,
        float stepDistance,
        int maximumChecks,
        float launchSpeed = 0f,
        bool captureDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(physics);
        if (source.EntityId == 0u
            || target.EntityId == 0u
            || source.CellId == 0u
            || !float.IsFinite(radius)
            || radius <= 0f
            || !float.IsFinite(stepDistance)
            || stepDistance <= 0f
            || maximumChecks <= 0)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        Vector3 baseDelta = target.Position - source.Position;
        var horizontal = new Vector2(baseDelta.X, baseDelta.Y);
        float horizontalDistance = horizontal.Length();
        if (!float.IsFinite(horizontalDistance)
            || horizontalDistance <= PhysicsGlobals.EPSILON)
        {
            return new(PluginProjectilePathStatus.InvalidTarget);
        }

        float sourceHeight = HeightOrFallback(source.Height);
        float targetStanding = HeightOrFallback(target.Height);
        Vector2 direction = horizontal / horizontalDistance;
        float forward = LaunchForward(kind);
        Vector3 start = source.Position + new Vector3(
            direction.X * forward,
            direction.Y * forward,
            sourceHeight * LaunchHeightFraction);
        Vector3 destination = target.Position + new Vector3(
            0f,
            0f,
            targetStanding * AimHeightFraction(targetHeight));
        Vector3 delta = destination - start;
        horizontal = new Vector2(delta.X, delta.Y);
        horizontalDistance = horizontal.Length();
        if (horizontalDistance <= PhysicsGlobals.EPSILON)
            return new(PluginProjectilePathStatus.Clear);
        direction = horizontal / horizontalDistance;

        bool ballistic = kind != PluginProjectilePathKind.Straight;
        float speed = ballistic && float.IsFinite(launchSpeed) && launchSpeed > 0f
            ? launchSpeed
            : DefaultLaunchSpeed(kind);
        float totalTime = horizontalDistance / speed;
        // The shot is launched so that, under gravity, it lands on the aim
        // point: constant horizontal speed and whatever vertical speed makes
        // z(totalTime) meet the target.
        float verticalSpeed = ballistic
            ? (delta.Z + 0.5f * Gravity * totalTime * totalTime) / totalTime
            : delta.Z / totalTime;
        var launch = new Vector3(direction.X * speed, direction.Y * speed, verticalSpeed);

        uint cellId = source.CellId;
        var probeBody = new PhysicsBody
        {
            State = PhysicsStateFlags.Missile
                | PhysicsStateFlags.Inelastic
                | PhysicsStateFlags.ReportCollisions,
        };
        List<PluginProjectileDebugSample>? debugSamples = captureDiagnostics
            ? new List<PluginProjectileDebugSample>(Math.Min(maximumChecks, 512))
            : null;

        Vector3 current = start;
        float elapsed = 0f;
        for (int check = 1; check <= maximumChecks; check++)
        {
            float remaining = totalTime - elapsed;
            if (remaining <= PhysicsGlobals.EPSILON)
            {
                return WithSamples(
                    new(PluginProjectilePathStatus.Clear, check - 1),
                    debugSamples);
            }
            Vector3 velocity = ballistic
                ? launch with { Z = launch.Z - Gravity * elapsed }
                : launch;
            float velocityMagnitude = velocity.Length();
            if (!float.IsFinite(velocityMagnitude)
                || velocityMagnitude <= PhysicsGlobals.EPSILON)
            {
                return WithSamples(
                    new(
                        PluginProjectilePathStatus.Error,
                        check - 1,
                        Notice: "The projectile trajectory became invalid."),
                    debugSamples);
            }
            float quantum = MathF.Min(remaining, stepDistance / velocityMagnitude);
            float nextTime = elapsed + quantum;
            Vector3 next = quantum >= remaining - PhysicsGlobals.EPSILON
                ? destination
                : PositionAt(start, launch, nextTime, ballistic);

            ResolveResult resolved = physics.ResolveWithTransition(
                current,
                next,
                cellId,
                radius,
                sphereHeight: 0f,
                stepUpHeight: 0f,
                stepDownHeight: 0f,
                isOnGround: false,
                body: probeBody,
                moverFlags: ObjectInfoState.PathClipped,
                movingEntityId: source.EntityId,
                localSphereOrigin: Vector3.Zero,
                designatedTargetId: target.EntityId);
            float requestedDistance = Vector3.Distance(current, next);
            float deliveredDistance = Vector3.Distance(current, resolved.Position);
            bool stopped = !resolved.Ok
                || resolved.CollidedWithEnvironment
                || resolved.LastCollidedObjectId != 0u
                || resolved.CollisionNormalValid
                || deliveredDistance + 0.01f < requestedDistance;
            bool targetHit = resolved.LastCollidedObjectId == target.EntityId;
            debugSamples?.Add(new PluginProjectileDebugSample(
                resolved.Position,
                targetHit || !stopped,
                radius));
            if (targetHit)
            {
                return WithSamples(
                    new(PluginProjectilePathStatus.Clear, check, target.EntityId),
                    debugSamples);
            }
            if (stopped)
            {
                return WithSamples(
                    new(
                        PluginProjectilePathStatus.Blocked,
                        check,
                        resolved.LastCollidedObjectId,
                        resolved.CollidedWithEnvironment || resolved.LastCollidedObjectId == 0u
                            ? "environment"
                            : null),
                    debugSamples);
            }

            current = resolved.Position;
            if (resolved.CellId != 0u)
                cellId = resolved.CellId;
            elapsed = nextTime;
        }

        return WithSamples(
            new(
                PluginProjectilePathStatus.BudgetExceeded,
                maximumChecks,
                Notice: "The projectile collision-check budget was exhausted."),
            debugSamples);
    }

    private static Vector3 PositionAt(Vector3 start, Vector3 launch, float time, bool ballistic)
    {
        Vector3 position = start + launch * time;
        if (ballistic)
            position.Z -= 0.5f * Gravity * time * time;
        return position;
    }

    private static float HeightOrFallback(float height) =>
        float.IsFinite(height) && height > PhysicsGlobals.EPSILON ? height : FallbackHeight;

    private static PluginProjectilePathResult WithSamples(
        PluginProjectilePathResult result,
        List<PluginProjectileDebugSample>? samples) => samples is null
            ? result
            : result with { DebugSamples = samples.ToArray() };
}
