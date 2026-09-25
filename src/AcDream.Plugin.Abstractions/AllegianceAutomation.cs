namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One member of the character's allegiance, as the server last described
/// them in its statement of the allegiance.
/// </summary>
/// <param name="ObjectId">The member's character object id.</param>
/// <param name="Name">The member's character name.</param>
/// <param name="Rank">The member's rank in the allegiance.</param>
/// <param name="Level">The member's character level.</param>
/// <param name="HeritageGroup">
/// The member's heritage as the server numbers it: 1 Aluvian, 2 Gharu'ndim,
/// 3 Sho, 4 Viamontian, 5 Shadowbound, 6 Gearknight, 7 Tumerok, 8 Lugian,
/// 9 Empyrean, 10 Penumbraen, 11 Undead, 12 Olthoi, 13 Olthoi acid; 0 when
/// the server did not say.
/// </param>
/// <param name="Gender">
/// The member's gender as the server numbers it: 1 male, 2 female; 0 when
/// the server did not say.
/// </param>
/// <param name="IsOnline">Whether the member was logged in when the server said so.</param>
public readonly record struct PluginAllegianceMember(
    uint ObjectId,
    string Name,
    uint Rank,
    uint Level,
    int HeritageGroup,
    int Gender,
    bool IsOnline);

/// <summary>Normalized allegiance state known by the current session.</summary>
/// <param name="Revision">A number that grows whenever any of this changes.</param>
/// <param name="IsKnown">
/// True once the server has stated the allegiance this session; everything
/// else is empty or zero until then.
/// </param>
/// <param name="Name">The allegiance's name; empty when it has none.</param>
/// <param name="Rank">The character's own rank in the allegiance.</param>
/// <param name="MemberCount">
/// How many characters the whole allegiance holds, the monarch included.
/// </param>
/// <param name="VassalCount">
/// How many characters are sworn beneath this character, counting their
/// vassals and theirs all the way down: the character's followers.
/// </param>
/// <param name="MonarchObjectId">
/// The monarch's character object id, or zero when there is none.
/// </param>
public readonly record struct PluginAllegianceSnapshot(
    long Revision,
    bool IsKnown,
    string Name,
    uint Rank,
    uint MemberCount,
    uint VassalCount,
    uint MonarchObjectId)
{
    /// <summary>True when a monarch identity is available.</summary>
    public bool HasMonarch => MonarchObjectId != 0u;

    /// <summary>
    /// Who the monarch is: the character itself when it heads the
    /// allegiance. Null when the character is in no allegiance, before the
    /// server has stated it, and on a host that does not report it.
    /// </summary>
    public PluginAllegianceMember? Monarch { get; init; }

    /// <summary>
    /// Who the character is sworn to. Null for a monarch, for a character in
    /// no allegiance, before the server has stated it, and on a host that
    /// does not report it.
    /// </summary>
    public PluginAllegianceMember? Patron { get; init; }

    /// <summary>
    /// The characters sworn directly to this one, as the server listed them.
    /// Empty when there are none, before the server has stated the
    /// allegiance, and on a host that does not report them.
    /// </summary>
    public IReadOnlyList<PluginAllegianceMember> Vassals
    {
        get => _vassals ?? Array.Empty<PluginAllegianceMember>();
        init => _vassals = value;
    }

    // A default snapshot never runs an initializer, so the empty list is
    // supplied on read rather than stored.
    private readonly IReadOnlyList<PluginAllegianceMember>? _vassals;
}

/// <summary>How the client answered a plugin's allegiance command.</summary>
public enum PluginAllegianceCommandStatus
{
    /// <summary>
    /// The command could not be sent: the character is not in the world,
    /// there is no live session, or this host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The command went out to the server. Whether the server allows it is
    /// the server's own decision, and arrives later as a restated allegiance.
    /// </summary>
    Sent,

    /// <summary>
    /// The client refused the command before sending it, because the object
    /// named cannot be the target of one: a zero, a patron who is not a
    /// player the client can see, or someone who is not in the character's
    /// own allegiance.
    /// </summary>
    InvalidTarget,

    /// <summary>
    /// The client had a target it could act on and still did not send the
    /// command: something about the session itself stood in the way. The
    /// reason, where there is one to give, is in the result's notice. This
    /// is not a statement about the target, and a plugin that is choosing
    /// another target on a refusal should not treat it as one.
    /// </summary>
    Refused,
}

/// <summary>The outcome of one allegiance command.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">Why it was refused, when there is something to say.</param>
public readonly record struct PluginAllegianceCommandResult(
    PluginAllegianceCommandStatus Status,
    string? Notice = null)
{
    /// <summary>True when the command actually went out to the server.</summary>
    public bool Accepted => Status == PluginAllegianceCommandStatus.Sent;
}

/// <summary>
/// Reads authoritative allegiance state, and sends the two allegiance
/// commands the client's own social panel sends. Everything read here is the
/// state the server last told the client; a command only asks, it does not
/// decide.
/// </summary>
public interface IAllegianceAutomation
{
    /// <summary>True when the host is tracking allegiance state.</summary>
    bool IsAvailable => false;

    /// <summary>The latest normalized allegiance snapshot.</summary>
    PluginAllegianceSnapshot Snapshot => default;

    /// <summary>
    /// Asks the server to pledge the character to another player as its
    /// patron. The patron has to be a player the client can currently see,
    /// which is what the client's own panel requires as well: swearing is
    /// done face to face.
    /// </summary>
    /// <param name="patronObjectId">The player to swear to.</param>
    /// <returns>
    /// <see cref="PluginAllegianceCommandStatus.Sent"/> once it is on its way,
    /// <see cref="PluginAllegianceCommandStatus.InvalidTarget"/> when that is
    /// not a player standing there,
    /// <see cref="PluginAllegianceCommandStatus.Refused"/> when the client
    /// would not send it for a reason that is not about the target, and
    /// <see cref="PluginAllegianceCommandStatus.Unavailable"/> off-world.
    /// </returns>
    PluginAllegianceCommandResult Swear(uint patronObjectId) =>
        new(PluginAllegianceCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to break the tie between the character and someone in
    /// its allegiance -- its patron, or one of its vassals. The target has to
    /// be someone the client has been told is in that allegiance; it does not
    /// have to be nearby, or even logged in.
    /// </summary>
    /// <param name="targetObjectId">The patron or vassal to break from.</param>
    /// <returns>
    /// <see cref="PluginAllegianceCommandStatus.Sent"/> once it is on its way,
    /// <see cref="PluginAllegianceCommandStatus.InvalidTarget"/> when that
    /// character is not in the allegiance,
    /// <see cref="PluginAllegianceCommandStatus.Refused"/> when the client
    /// would not send it for a reason that is not about the target, and
    /// <see cref="PluginAllegianceCommandStatus.Unavailable"/> off-world.
    /// </returns>
    PluginAllegianceCommandResult Break(uint targetObjectId) =>
        new(PluginAllegianceCommandStatus.Unavailable);
}
