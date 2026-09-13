namespace AcDream.Plugin.Abstractions;

public enum PluginWalkProbeStatus
{
    Unavailable = 0,
    /// <summary>The whole distance can be walked.</summary>
    Clear,
    /// <summary>Something stops the body before the distance is covered.</summary>
    Blocked,
    BudgetExceeded,
    Error,
}

/// <summary>
/// One walk-path query: can the character walk from where it stands along a
/// compass heading for a distance? The host sweeps the character's own
/// collision body with its step-up and step-down, so hills, stairs and
/// doorways read the way walking reads them, while walls, fences, closed
/// doors and scenery stop it. Other creatures are ignored; they move.
/// </summary>
public readonly record struct PluginWalkProbeRequest(
    float HeadingDegrees,
    float DistanceMeters)
{
    /// <summary>Meters walked per collision check.</summary>
    public float StepDistance { get; init; } = 1f;

    public int MaximumCollisionChecks { get; init; } = 64;

    /// <summary>
    /// An object the walk is heading for: bumping into it counts as arriving,
    /// not as a block. Zero ignores nothing.
    /// </summary>
    public uint TargetObjectId { get; init; }

    /// <summary>Fill <see cref="PluginWalkProbeResult.DebugSamples"/>.</summary>
    public bool CaptureDiagnostics { get; init; }
}

public readonly record struct PluginWalkProbeResult(
    PluginWalkProbeStatus Status,
    float ClearDistanceMeters,
    int CollisionChecks = 0,
    uint BlockingObjectId = 0u,
    string? Notice = null)
{
    public bool IsClear => Status == PluginWalkProbeStatus.Clear;

    /// <summary>One marker per check, drawable through <see cref="IProjectileAutomation.ShowDebugSamples"/>.</summary>
    public IReadOnlyList<PluginProjectileDebugSample> DebugSamples
        { get; init; } = Array.Empty<PluginProjectileDebugSample>();
}

/// <summary>The character's obstacle sense for walking, on the client's own collision.</summary>
public interface IMovementProbeAutomation
{
    bool IsAvailable => false;

    PluginWalkProbeResult ProbeWalk(in PluginWalkProbeRequest request) =>
        new(PluginWalkProbeStatus.Unavailable, 0f);
}
