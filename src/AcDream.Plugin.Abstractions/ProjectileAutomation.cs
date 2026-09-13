using System.Numerics;

namespace AcDream.Plugin.Abstractions;

public enum PluginProjectilePathKind
{
    Straight = 0,
    Arc,
    Missile,
}

/// <summary>Why a projectile-path query did or did not admit the shot.</summary>
public enum PluginProjectilePathStatus
{
    Unavailable = 0,
    Clear,
    Blocked,
    InvalidTarget,
    BudgetExceeded,
    Error,
}

public readonly record struct PluginProjectileDebugSample(
    Vector3 WorldPosition,
    bool IsClear,
    float Radius);

public readonly record struct PluginProjectilePathResult(
    PluginProjectilePathStatus Status,
    int CollisionChecks = 0,
    uint BlockingObjectId = 0u,
    string? Notice = null)
{
    public bool IsClear => Status == PluginProjectilePathStatus.Clear;
    public IReadOnlyList<PluginProjectileDebugSample> DebugSamples
        { get; init; } = Array.Empty<PluginProjectileDebugSample>();
}

/// <summary>
/// One projectile-path query. The radius, step and budget defaults suit a
/// war-spell bolt or an arrow; <see cref="LaunchSpeed"/> lets a caller
/// model a slower, higher lob than the host's default for
/// <see cref="PluginProjectilePathKind.Arc"/> and
/// <see cref="PluginProjectilePathKind.Missile"/>.
/// </summary>
public readonly record struct PluginProjectilePathRequest(
    uint TargetObjectId,
    PluginProjectilePathKind Kind,
    PluginAttackHeight TargetHeight)
{
    public float ProjectileRadius { get; init; } = 0.25f;

    /// <summary>World meters swept per collision check.</summary>
    public float StepDistance { get; init; } = 1.5f;

    public int MaximumCollisionChecks { get; init; } = 128;

    /// <summary>
    /// Horizontal launch speed in meters per second for the gravity-bound
    /// kinds; zero or negative takes the host's default. Ignored for
    /// <see cref="PluginProjectilePathKind.Straight"/>.
    /// </summary>
    public float LaunchSpeed { get; init; }

    /// <summary>Fill <see cref="PluginProjectilePathResult.DebugSamples"/>.</summary>
    public bool CaptureDiagnostics { get; init; }
}

public interface IProjectileAutomation
{
    bool IsAvailable => false;

    /// <summary>
    /// Structured form of <see cref="EvaluatePath(uint, PluginProjectilePathKind, PluginAttackHeight, float, float, int)"/>.
    /// Hosts that do not understand <see cref="PluginProjectilePathRequest.LaunchSpeed"/>
    /// fall back to the positional overloads with their default speeds.
    /// </summary>
    PluginProjectilePathResult EvaluatePath(in PluginProjectilePathRequest request) =>
        request.CaptureDiagnostics
            ? EvaluatePathWithDiagnostics(
                request.TargetObjectId,
                request.Kind,
                request.TargetHeight,
                request.ProjectileRadius,
                request.StepDistance,
                request.MaximumCollisionChecks)
            : EvaluatePath(
                request.TargetObjectId,
                request.Kind,
                request.TargetHeight,
                request.ProjectileRadius,
                request.StepDistance,
                request.MaximumCollisionChecks);

    PluginProjectilePathResult EvaluatePath(
        uint targetObjectId,
        PluginProjectilePathKind kind,
        PluginAttackHeight targetHeight,
        float projectileRadius,
        float stepDistance,
        int maximumCollisionChecks) =>
        new(PluginProjectilePathStatus.Unavailable);

    /// <summary>
    /// Same bounded query with VTank's optional per-quantum debug markers.
    /// Older hosts safely fall back to the ordinary result.
    /// </summary>
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

    void ShowDebugSamples(
        IReadOnlyList<PluginProjectileDebugSample> samples)
    {
    }
}
