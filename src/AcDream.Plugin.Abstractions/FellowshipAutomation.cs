namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One member of the player's fellowship: the vitals the server sends for
/// the roster, plus how far away the member currently is.
/// </summary>
/// <param name="ObjectId">The member's object id.</param>
/// <param name="Name">The member's character name.</param>
/// <param name="CurrentHealth">The member's current health.</param>
/// <param name="MaxHealth">The member's maximum health.</param>
/// <param name="CurrentStamina">The member's current stamina.</param>
/// <param name="MaxStamina">The member's maximum stamina.</param>
/// <param name="CurrentMana">The member's current mana.</param>
/// <param name="MaxMana">The member's maximum mana.</param>
/// <param name="Distance">
/// Distance from the local player in metres; zero for the local player's own
/// entry.
/// </param>
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
    /// <summary>True when the member takes a share of fellowship loot.</summary>
    public bool ShareLoot { get; init; }
}

/// <summary>How the client answered a plugin's fellowship command.</summary>
public enum PluginFellowshipCommandStatus
{
    /// <summary>
    /// The command could not be sent: there is no in-world session, or this
    /// host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>The command was sent to the server.</summary>
    Accepted,

    /// <summary>
    /// The client refused the command before sending it, because an argument
    /// was not usable (an empty fellowship name, a zero target id).
    /// </summary>
    Rejected,
}

/// <summary>The outcome of one fellowship command.</summary>
/// <param name="Status">What the client did with the command.</param>
public readonly record struct PluginFellowshipCommandResult(
    PluginFellowshipCommandStatus Status)
{
    /// <summary>True when the command actually went out to the server.</summary>
    public bool Accepted => Status == PluginFellowshipCommandStatus.Accepted;
}

/// <summary>
/// Reads the player's fellowship and sends the fellowship commands the
/// client's own social panel sends. Everything here reports the state the
/// server last told the client; a command only asks, it does not decide.
/// </summary>
public interface IFellowshipAutomation
{
    /// <summary>True when the local player is currently in a fellowship.</summary>
    bool IsInFellowship => false;

    /// <summary>The fellowship's name, or an empty string when there is none.</summary>
    string Name => string.Empty;

    /// <summary>The leader's object id, or zero when there is no fellowship.</summary>
    uint LeaderObjectId => 0u;

    /// <summary>True when players may join the fellowship without being recruited.</summary>
    bool IsOpen => false;

    /// <summary>True when the fellowship's roster is locked against changes.</summary>
    bool IsLocked => false;

    /// <summary>How many members the fellowship has; zero when there is none.</summary>
    int MemberCount => 0;

    /// <summary>
    /// Lists the other members of the fellowship, leaving out the local
    /// player and anyone whose distance the client cannot work out right now.
    /// Returns an empty list when the player is not in a fellowship.
    /// </summary>
    IReadOnlyList<PluginFellowMember> CaptureMembers() =>
        Array.Empty<PluginFellowMember>();

    /// <summary>
    /// Like <see cref="CaptureMembers"/>, but includes the local player's own
    /// entry, whose distance reads zero. Returns an empty list when the
    /// player is not in a fellowship.
    /// </summary>
    IReadOnlyList<PluginFellowMember> CaptureRoster() => CaptureMembers();

    /// <summary>
    /// Asks the server to start a new fellowship with this name, optionally
    /// sharing experience between members. Rejected when the name is blank.
    /// </summary>
    PluginFellowshipCommandResult Create(string name, bool shareExperience) =>
        new(PluginFellowshipCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to recruit another player into the fellowship.
    /// Rejected when the target id is zero.
    /// </summary>
    PluginFellowshipCommandResult Recruit(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to remove a member from the fellowship. Rejected when
    /// the target id is zero.
    /// </summary>
    PluginFellowshipCommandResult Dismiss(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to leave the fellowship; pass true to disband it for
    /// everyone instead of just leaving.
    /// </summary>
    PluginFellowshipCommandResult Quit(bool disband) =>
        new(PluginFellowshipCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to hand leadership to another member. Rejected when
    /// the target id is zero.
    /// </summary>
    PluginFellowshipCommandResult AssignLeader(uint targetObjectId) =>
        new(PluginFellowshipCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to open the fellowship to anyone who wants to join, or
    /// to close it again.
    /// </summary>
    PluginFellowshipCommandResult SetOpen(bool isOpen) =>
        new(PluginFellowshipCommandStatus.Unavailable);
}
