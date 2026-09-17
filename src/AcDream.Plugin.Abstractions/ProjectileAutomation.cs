using System.Numerics;

namespace AcDream.Plugin.Abstractions;

/// <summary>How a projectile flies, which decides its speed and whether it falls.</summary>
public enum PluginProjectilePathKind
{
    /// <summary>A fast flat shot that keeps its height for the whole flight.</summary>
    Straight = 0,

    /// <summary>A slow shot lobbed high and pulled down by gravity.</summary>
    Arc,

    /// <summary>A faster shot that still drops under gravity along the way.</summary>
    Missile,
}

/// <summary>Why a projectile-path query did or did not admit the shot.</summary>
public enum PluginProjectilePathStatus
{
    /// <summary>
    /// The host cannot answer path queries: no session is in the world, or this
    /// host has no collision probe.
    /// </summary>
    Unavailable = 0,

    /// <summary>Nothing stopped the shot before it reached the target.</summary>
    Clear,

    /// <summary>
    /// Something stood in the way. The blocking object's id is reported when
    /// the obstruction was an object rather than the landscape.
    /// </summary>
    Blocked,

    /// <summary>
    /// The query itself was unusable: a zero target id, a radius or step
    /// distance that is not a positive finite number, a check budget that is
    /// not positive, a target the client does not know, or a target standing at
    /// the shooter's own spot.
    /// </summary>
    InvalidTarget,

    /// <summary>
    /// The flight was still going when the caller's collision-check budget ran
    /// out, so the shot is neither proven clear nor proven blocked.
    /// </summary>
    BudgetExceeded,

    /// <summary>
    /// The query failed part way: the trajectory stopped being a usable number,
    /// or the collision probe threw. The notice carries the reason.
    /// </summary>
    Error,
}

/// <summary>
/// One step of the simulated flight, for drawing the path while debugging.
/// </summary>
/// <param name="WorldPosition">Where the step ended, in world meters.</param>
/// <param name="IsClear">
/// True when the step passed unobstructed or landed on the intended target;
/// false at the step that was stopped by something else.
/// </param>
/// <param name="Radius">The probe sphere's radius in meters.</param>
public readonly record struct PluginProjectileDebugSample(
    Vector3 WorldPosition,
    bool IsClear,
    float Radius);

/// <summary>The answer to one projectile-path query.</summary>
/// <param name="Status">Whether the shot was admitted, and if not, why not.</param>
/// <param name="CollisionChecks">
/// How many collision steps the query actually performed.
/// </param>
/// <param name="BlockingObjectId">
/// The object that stopped the shot, or the target's own id when the flight
/// ended on the target. Zero when the landscape stopped it or nothing did.
/// </param>
/// <param name="Notice">
/// A short explanation for a budget or error result; null otherwise.
/// </param>
public readonly record struct PluginProjectilePathResult(
    PluginProjectilePathStatus Status,
    int CollisionChecks = 0,
    uint BlockingObjectId = 0u,
    string? Notice = null)
{
    /// <summary>True only when the status is <see cref="PluginProjectilePathStatus.Clear"/>.</summary>
    public bool IsClear => Status == PluginProjectilePathStatus.Clear;

    /// <summary>
    /// The per-step markers of the simulated flight. Empty unless the query was
    /// made through the diagnostics overload.
    /// </summary>
    public IReadOnlyList<PluginProjectileDebugSample> DebugSamples
        { get; init; } = Array.Empty<PluginProjectileDebugSample>();
}

/// <summary>
/// Asking the client whether a shot at a target would actually get there, by
/// simulating the projectile against world collision.
/// </summary>
public interface IProjectileAutomation
{
    /// <summary>
    /// True when this host can answer path queries. False on a host with no
    /// collision probe or no session in the world, which is what the default
    /// implementation always reports.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// Fly a probe sphere from the player to the target and report what it hit.
    /// </summary>
    /// <param name="targetObjectId">The object being shot at.</param>
    /// <param name="kind">Which flight model to simulate.</param>
    /// <param name="targetHeight">
    /// Where on the target to aim: low, medium, or high, which the host turns
    /// into an aim point 0.3, 0.9, or 1.5 meters above the target's base.
    /// </param>
    /// <param name="projectileRadius">
    /// The radius of the probe sphere in meters; must be positive.
    /// </param>
    /// <param name="stepDistance">
    /// How far the probe advances between collision checks, in meters; smaller
    /// steps cost more checks but miss less. Must be positive.
    /// </param>
    /// <param name="maximumCollisionChecks">
    /// The most collision checks this query may spend before giving up with
    /// <see cref="PluginProjectilePathStatus.BudgetExceeded"/>.
    /// </param>
    /// <returns>
    /// The outcome of the flight. The default implementation always returns
    /// <see cref="PluginProjectilePathStatus.Unavailable"/>.
    /// </returns>
    PluginProjectilePathResult EvaluatePath(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks) =>
        new(PluginProjectilePathStatus.Unavailable);

    /// <summary>
    /// The same bounded query, but the result also carries a marker for every
    /// simulated step. A host that does not collect markers falls back to the
    /// ordinary query and returns a result with no samples.
    /// </summary>
    /// <param name="targetObjectId">The object being shot at.</param>
    /// <param name="kind">Which flight model to simulate.</param>
    /// <param name="targetHeight">Where on the target to aim.</param>
    /// <param name="projectileRadius">The probe sphere's radius in meters.</param>
    /// <param name="stepDistance">
    /// How far the probe advances between collision checks, in meters.
    /// </param>
    /// <param name="maximumCollisionChecks">The collision-check budget.</param>
    /// <returns>The outcome of the flight, with its per-step markers.</returns>
    PluginProjectilePathResult EvaluatePathWithDiagnostics(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks) =>
        EvaluatePath(
            targetObjectId,
            kind,
            targetHeight,
            projectileRadius,
            stepDistance,
            maximumCollisionChecks);

    /// <summary>
    /// Hand the markers back to the client so it can draw the simulated path in
    /// the world for a short moment. Markers with a non-finite position or a
    /// radius of zero or less are dropped, and a host may cap how many it keeps.
    /// The default implementation draws nothing.
    /// </summary>
    /// <param name="samples">The markers to draw.</param>
    void ShowDebugSamples(
        IReadOnlyList<PluginProjectileDebugSample> samples)
    {
    }
}
