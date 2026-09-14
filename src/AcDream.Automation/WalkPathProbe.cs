using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.Automation;

/// <summary>
/// The body that is asked to walk: the local entity id the shadow registry
/// knows it by, where it stands, the cell that frame is in, and the
/// collision it walks with (the same spheres, scale and step heights the
/// movement controller resolves real steps with).
/// </summary>
internal readonly record struct WalkProbeMover(
    uint EntityId,
    Vector3 Position,
    uint CellId,
    ImmutableArray<FlatCollisionSphere> Spheres,
    float Scale,
    float StepUpHeight,
    float StepDownHeight);

/// <summary>
/// Walks the character's collision body along a compass heading through
/// <see cref="PhysicsEngine"/> one step at a time and reports how far it
/// got. Each step is the transition a real walking step would run - grounded,
/// stepping up and down - so terrain and stairs are followed while walls,
/// fences, closed doors and scenery stop it. Creatures are ignored: they
/// move, and the one being walked to counts as arrived.
/// </summary>
internal static class WalkPathProbe
{
    /// <summary>The standard human body when the controller has no sphere list.</summary>
    public const float FallbackRadius = 0.48f;
    public const float FallbackHeight = 1.835f;

    /// <summary>How much of a step must land along the heading for it to count as walked.</summary>
    private const float ProgressTolerance = 0.05f;

    /// <summary>The deepest drop off a ledge the probe will take rather than call it a cliff.</summary>
    public const float MaximumDropMeters = 3f;

    private const ObjectInfoState MoverFlags = ObjectInfoState.IsPlayer
        | ObjectInfoState.EdgeSlide
        | ObjectInfoState.IgnoreCreatures;

    public static Vector2 HeadingDirection(float headingDegrees)
    {
        // Heading 0 is north (+Y), 90 is east (+X).
        float radians = headingDegrees * (MathF.PI / 180f);
        return new Vector2(MathF.Sin(radians), MathF.Cos(radians));
    }

    public static PluginWalkProbeResult Evaluate(
        PhysicsEngine physics,
        in WalkProbeMover mover,
        float headingDegrees,
        float distance,
        float stepDistance,
        int maximumChecks,
        uint targetEntityId = 0u,
        bool captureDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(physics);
        if (mover.EntityId == 0u
            || mover.CellId == 0u
            || !float.IsFinite(headingDegrees)
            || !float.IsFinite(distance)
            || distance <= 0f
            || !float.IsFinite(stepDistance)
            || stepDistance <= 0f
            || maximumChecks <= 0)
        {
            return new(PluginWalkProbeStatus.Error, 0f, Notice: "The walk probe request is invalid.");
        }

        Vector2 direction = HeadingDirection(headingDegrees);
        var forward = new Vector3(direction.X, direction.Y, 0f);
        float radius = FallbackRadius;
        float height = FallbackHeight;
        if (!mover.Spheres.IsDefaultOrEmpty)
        {
            radius = mover.Spheres[0].Radius * mover.Scale;
            height = mover.Spheres[^1].Origin.Z * mover.Scale + radius;
        }
        // A grounded body: the sweep steps up and down only for a mover it
        // believes is standing on something, so start it on a floor through
        // its feet and let each resolved step refresh the contact plane.
        var probeBody = new PhysicsBody
        {
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            ContactPlaneValid = true,
            ContactPlane = new Plane(Vector3.UnitZ, -mover.Position.Z),
            ContactPlaneCellId = mover.CellId,
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };
        probeBody.SnapToCell(mover.CellId, mover.Position, mover.Position);
        List<PluginProjectileDebugSample>? samples = captureDiagnostics
            ? new List<PluginProjectileDebugSample>(Math.Min(maximumChecks, 256))
            : null;

        Vector3 current = mover.Position;
        uint cellId = mover.CellId;
        float walked = 0f;
        bool grounded = true;
        for (int check = 1; check <= maximumChecks; check++)
        {
            float remaining = distance - walked;
            if (remaining <= PhysicsGlobals.EPSILON)
                return WithSamples(new(PluginWalkProbeStatus.Clear, distance, check - 1), samples);
            float step = MathF.Min(remaining, stepDistance);
            Vector3 next = current + forward * step;

            ResolveResult resolved = physics.ResolveWithTransition(
                current,
                next,
                cellId,
                sphereRadius: radius,
                sphereHeight: height,
                stepUpHeight: mover.StepUpHeight,
                stepDownHeight: mover.StepDownHeight,
                isOnGround: grounded,
                body: probeBody,
                moverFlags: MoverFlags,
                movingEntityId: mover.EntityId,
                designatedTargetId: targetEntityId,
                sphereList: mover.Spheres,
                sphereScale: mover.Scale);

            Vector3 moved = resolved.Position - current;
            float progress = moved.X * direction.X + moved.Y * direction.Y;
            bool reachedTarget = targetEntityId != 0u
                && resolved.LastCollidedObjectId == targetEntityId;
            bool stopped = !resolved.Ok || progress + ProgressTolerance < step;
            if (stopped && !reachedTarget && resolved.Ok && grounded
                && (!resolved.CollisionNormalValid || resolved.CollisionNormal.Z >= PhysicsGlobals.FloorZ))
            {
                // Held back by the edge of what it stands on, not by a wall:
                // a walker would push on and fall. Step off and drop.
                if (TryStepOffLedge(
                        physics, mover, radius, height, forward, direction,
                        resolved.Position, step - MathF.Max(0f, progress),
                        resolved.CellId != 0u ? resolved.CellId : cellId,
                        out Vector3 landed, out uint landedCell, out float extra))
                {
                    resolved = new ResolveResult(landed, landedCell, IsOnGround: true, InContact: true, OnWalkable: true);
                    progress += extra;
                    stopped = progress + ProgressTolerance < step;
                    probeBody.ContactPlaneValid = false;
                }
            }
            samples?.Add(new PluginProjectileDebugSample(
                resolved.Position,
                reachedTarget || !stopped,
                radius));
            if (reachedTarget)
            {
                return WithSamples(
                    new(PluginWalkProbeStatus.Clear, walked + MathF.Max(0f, progress), check, targetEntityId),
                    samples);
            }
            if (stopped)
            {
                uint blocker = resolved.LastCollidedObjectId;
                return WithSamples(
                    new(
                        PluginWalkProbeStatus.Blocked,
                        walked + MathF.Max(0f, progress),
                        check,
                        blocker,
                        blocker == 0u || resolved.CollidedWithEnvironment ? "environment" : null),
                    samples);
            }

            current = resolved.Position;
            if (resolved.CellId != 0u)
                cellId = resolved.CellId;
            grounded = resolved.IsOnGround || resolved.OnWalkable || resolved.InContact;
            probeBody.Position = current;
            probeBody.TransientState = grounded
                ? TransientStateFlags.Contact | TransientStateFlags.OnWalkable
                : TransientStateFlags.None;
            if (!probeBody.ContactPlaneValid && grounded)
            {
                probeBody.ContactPlaneValid = true;
                probeBody.ContactPlane = new Plane(Vector3.UnitZ, -current.Z);
                probeBody.ContactPlaneCellId = cellId;
            }
            walked += step;
        }

        return WithSamples(
            new(
                PluginWalkProbeStatus.BudgetExceeded,
                walked,
                maximumChecks,
                Notice: "The walk collision-check budget was exhausted."),
            samples);
    }

    /// <summary>
    /// From a rim the grounded sweep would not leave, walk the rest of the
    /// step in the air and fall onto whatever is below, within
    /// <see cref="MaximumDropMeters"/>. False when the air step hits a wall or
    /// nothing catches the fall.
    /// </summary>
    private static bool TryStepOffLedge(
        PhysicsEngine physics,
        in WalkProbeMover mover,
        float radius,
        float height,
        Vector3 forward,
        Vector2 direction,
        Vector3 rim,
        float remainingStep,
        uint cellId,
        out Vector3 landed,
        out uint landedCell,
        out float progress)
    {
        landed = rim;
        landedCell = cellId;
        progress = 0f;
        if (remainingStep <= PhysicsGlobals.EPSILON)
            return false;
        ResolveResult air = physics.ResolveWithTransition(
            rim,
            rim + forward * remainingStep,
            cellId,
            sphereRadius: radius,
            sphereHeight: height,
            stepUpHeight: mover.StepUpHeight,
            stepDownHeight: mover.StepDownHeight,
            isOnGround: false,
            moverFlags: MoverFlags,
            movingEntityId: mover.EntityId,
            sphereList: mover.Spheres,
            sphereScale: mover.Scale);
        Vector3 moved = air.Position - rim;
        float airProgress = moved.X * direction.X + moved.Y * direction.Y;
        if (!air.Ok || airProgress + ProgressTolerance < remainingStep)
            return false;
        uint airCell = air.CellId != 0u ? air.CellId : cellId;

        ResolveResult drop = physics.ResolveWithTransition(
            air.Position,
            air.Position with { Z = air.Position.Z - MaximumDropMeters },
            airCell,
            sphereRadius: radius,
            sphereHeight: height,
            stepUpHeight: mover.StepUpHeight,
            stepDownHeight: mover.StepDownHeight,
            isOnGround: false,
            moverFlags: MoverFlags,
            movingEntityId: mover.EntityId,
            sphereList: mover.Spheres,
            sphereScale: mover.Scale);
        float fell = air.Position.Z - drop.Position.Z;
        if (!drop.Ok || fell >= MaximumDropMeters - ProgressTolerance)
            return false;
        landed = drop.Position;
        landedCell = drop.CellId != 0u ? drop.CellId : airCell;
        progress = airProgress;
        return true;
    }

    private static PluginWalkProbeResult WithSamples(
        PluginWalkProbeResult result,
        List<PluginProjectileDebugSample>? samples) => samples is null
            ? result
            : result with { DebugSamples = samples.ToArray() };
}
