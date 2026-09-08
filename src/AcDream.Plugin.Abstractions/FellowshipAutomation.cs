namespace AcDream.Plugin.Abstractions;

/// <summary>One authoritative fellowship-roster entry plus live range.</summary>
public readonly record struct PluginFellowMember(
    uint ObjectId,
    string Name,
    uint CurrentHealth,
    uint MaxHealth,
    uint CurrentStamina,
    uint MaxStamina,
    uint CurrentMana,
    uint MaxMana,
    float Distance)
{
    public bool ShareLoot { get; init; }
}

public enum PluginFellowshipCommandStatus
{
    Unavailable = 0,
    Accepted,
    Rejected,
}

public readonly record struct PluginFellowshipCommandResult(
    PluginFellowshipCommandStatus Status)
{
    public bool Accepted => Status == PluginFellowshipCommandStatus.Accepted;
}

public interface IFellowshipAutomation
{
    bool IsInFellowship => false;
    string Name => string.Empty;
    uint LeaderObjectId => 0u;
    bool IsOpen => false;
    bool IsLocked => false;
    int MemberCount => 0;
    IReadOnlyList<PluginFellowMember> CaptureMembers() =>
        Array.Empty<PluginFellowMember>();

    IReadOnlyList<PluginFellowMember> CaptureRoster() => CaptureMembers();

    PluginFellowshipCommandResult Create(string name, bool shareExperience) =>
        new(PluginFellowshipCommandStatus.Unavailable);
    PluginFellowshipCommandResult Recruit(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);
    PluginFellowshipCommandResult Dismiss(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);
    PluginFellowshipCommandResult Quit(bool disband) =>
        new(PluginFellowshipCommandStatus.Unavailable);
    PluginFellowshipCommandResult AssignLeader(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);
    PluginFellowshipCommandResult SetOpen(bool isOpen) =>
        new(PluginFellowshipCommandStatus.Unavailable);
}
