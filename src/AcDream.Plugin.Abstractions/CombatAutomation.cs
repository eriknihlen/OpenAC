namespace AcDream.Plugin.Abstractions;

public enum PluginCombatMode
{
    Unknown = 0,
    Peace,
    Melee,
    Missile,
    Magic,
}

public enum PluginAttackHeight
{
    High = 1,
    Medium = 2,
    Low = 3,
}

public readonly record struct PluginCombatTarget(
    uint ObjectId,
    string Name,
    uint WeenieClassId,
    float Distance,
    float RelativeAngleDegrees,
    bool IsHealthKnown,
    float HealthFraction)
{
    public int SpeciesId { get; init; }

    public string SpeciesName { get; init; } = string.Empty;

    /// <summary>Spawn/appraisal maximum HP, or zero until the host knows it.</summary>
    public int MaximumHealth { get; init; }

    public bool HasShield { get; init; }
    public ushort Incarnation { get; init; }

    /// <summary>Monotonic revision of the last server health update.</summary>
    public long HealthRevision { get; init; }

    public double SecondsSinceHealthUpdate { get; init; } =
        double.PositiveInfinity;
}

public readonly record struct PluginCombatSnapshot(
    uint SelectedObjectId,
    PluginCombatMode Mode,
    PluginAttackHeight AttackHeight,
    float DesiredPower,
    float PowerBarLevel,
    bool BuildInProgress,
    bool RequestInProgress,
    bool ServerResponsePending,
    bool RepeatAttackInProgress)
{
    /// <summary>Revision of the last physical AttackDone receipt.</summary>
    public long CompletionRevision { get; init; }
    public uint CompletionSequence { get; init; }
    public uint CompletionWeenieError { get; init; }
}

public enum PluginCombatCommandStatus
{
    Unavailable = 0,
    InvalidTarget,
    WrongMode,
    Busy,
    AlreadyReady,
    ModeChangeSent,
    Started,
    Released,
    Stopped,
    Refused,
}

public readonly record struct PluginCombatCommandResult(
    PluginCombatCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status is
        PluginCombatCommandStatus.AlreadyReady
        or PluginCombatCommandStatus.ModeChangeSent
        or PluginCombatCommandStatus.Started
        or PluginCombatCommandStatus.Released
        or PluginCombatCommandStatus.Stopped;
}

public interface ICombatAutomation
{
    PluginCombatSnapshot Snapshot { get; }

    IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(float maximumDistance);

    PluginCombatCommandResult EnterDefaultMode();

    PluginCombatCommandResult EnterMode(PluginCombatMode mode) =>
        new(PluginCombatCommandStatus.Unavailable);

    PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId,
        PluginAttackHeight height,
        float power);

    PluginCombatCommandResult ReleasePhysicalAttack();
    PluginCombatCommandResult AbortPhysicalAttack();

    PluginCombatCommandResult DismissGhostTarget(uint targetObjectId) =>
        new(PluginCombatCommandStatus.Unavailable);
}
