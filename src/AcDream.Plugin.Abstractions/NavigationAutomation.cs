namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginNavigationPosition(
    uint CellId,
    double EastWest,
    double NorthSouth,
    double Elevation,
    float HeadingDegrees,
    bool IsOutdoor)
{
    public double HorizontalDistanceMeters(in PluginNavigationPosition other)
    {
        double dx = EastWest - other.EastWest;
        double dy = NorthSouth - other.NorthSouth;
        return Math.Sqrt(dx * dx + dy * dy) * 240d;
    }
}

public readonly record struct PluginNavigationObject(
    uint ObjectId,
    string Name,
    PluginNavigationPosition Position)
{
    public bool IsDoor { get; init; }
    public bool IsOpen { get; init; }
    public bool IsLocked { get; init; }
    public bool HasLockState { get; init; }
    public int LockDifficulty { get; init; }
}

/// <summary>The local movement state sampled atomically by a plugin tick.</summary>
public readonly record struct PluginNavigationSnapshot(
    bool IsAvailable,
    bool IsPortalSpace,
    uint LocalObjectId,
    PluginNavigationPosition Position,
    bool IsMoving,
    bool IsAirborne)
{
    public PluginNavigationPosition ConfirmedPosition { get; init; }
    public ulong ConfirmedPositionRevision { get; init; }
}

public readonly record struct PluginMovementIntent(
    bool Forward = false,
    bool Backward = false,
    bool StrafeLeft = false,
    bool StrafeRight = false,
    bool TurnLeft = false,
    bool TurnRight = false,
    bool Run = true,
    bool Jump = false);

public enum PluginNavigationCommandStatus
{
    Unavailable = 0,
    Accepted,
    Rejected,
}

public interface INavigationAutomation
{
    PluginNavigationSnapshot Snapshot { get; }

    bool TryGetObject(uint objectId, out PluginNavigationObject value);

    bool TryFindObject(
        string name,
        in PluginNavigationPosition near,
        double maximumDistanceMeters,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// Detached live world-object projection used by plugin-owned proximity
    /// policies such as VTank's door opener. Hosts may return an empty list.
    /// </summary>
    IReadOnlyList<PluginNavigationObject> CaptureObjects() =>
        Array.Empty<PluginNavigationObject>();

    PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent);

    PluginNavigationCommandStatus ClearMovementIntent();

    PluginNavigationCommandStatus FaceHeading(float headingDegrees) =>
        PluginNavigationCommandStatus.Unavailable;
}
