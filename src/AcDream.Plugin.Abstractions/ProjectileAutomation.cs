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

public interface IProjectileAutomation
{
    bool IsAvailable => false;

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
