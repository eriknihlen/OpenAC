namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One nearby corpse a plugin could loot, with how far away it is and where
/// it stands in the client's open-container cycle.
/// </summary>
/// <param name="ObjectId">The corpse's object id.</param>
/// <param name="WeenieClassId">The corpse's class id.</param>
/// <param name="Name">The corpse's display name.</param>
/// <param name="Distance">Distance from the local player in metres.</param>
/// <param name="HasBeenOpened">
/// True when this session has already opened that corpse at least once.
/// </param>
/// <param name="IsRequested">
/// True when the client has asked to open this corpse and is still waiting
/// for the server.
/// </param>
/// <param name="IsCurrent">True when this corpse is the one currently open.</param>
public readonly record struct PluginLootContainer(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    float Distance,
    bool HasBeenOpened,
    bool IsRequested,
    bool IsCurrent)
{
    /// <summary>
    /// The corpse's long description, which only arrives with an appraisal;
    /// an empty string until then.
    /// </summary>
    public string LongDescription { get; init; } = string.Empty;

    /// <summary>True when the corpse is flagged as holding a generated rare.</summary>
    public bool IsGeneratedRare { get; init; }

    /// <summary>
    /// True once the client holds the corpse's appraisal text, which is what
    /// fills <see cref="LongDescription"/>.
    /// </summary>
    public bool IsIdentified { get; init; }
}

/// <summary>
/// The state of the client's single shared appraisal slot, shared by the
/// user's own assess action and every plugin's Identify request.
/// </summary>
/// <param name="Revision">
/// Bumps on every state change to this slot; cheap to poll for "did
/// anything happen" without comparing the other fields.
/// </param>
/// <param name="AwaitingObjectId">
/// The object id currently awaiting a response, or 0 if none is in
/// flight. Only one request -- of either origin -- can be in flight at a
/// time; a plugin's own Identify can still be displaced by a later one
/// (its response is then dropped), but never by the reverse: a plugin
/// Identify while the user's own assess is awaiting is refused outright.
/// </param>
/// <param name="CurrentObjectId">
/// The completion signal: the object id of the most recently completed
/// appraisal response, of either origin. This is NOT the client's
/// examination window's displayed object -- a plugin's background
/// Identify never opens or retargets that window, so this can (and
/// routinely does) advance to an object the window is not showing. Poll
/// this against the id you passed to Identify to learn when your own
/// request completed.
/// </param>
public readonly record struct PluginAppraisalState(
    long Revision,
    uint AwaitingObjectId,
    uint CurrentObjectId);

/// <summary>
/// Looting: finding nearby corpses, opening one, reading what is inside, and
/// moving items out of it into the player's packs. Everything here is scoped
/// to the client's one open external container.
/// </summary>
public interface ILootAutomation
{
    /// <summary>
    /// True when this surface can be used: the session is in the world and
    /// the host wired up using, picking up, and appraising.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// True while an inventory request is already in flight, so the next
    /// command here would come back <see cref="PluginItemCommandStatus.Busy"/>.
    /// </summary>
    bool IsBusy => false;

    /// <summary>
    /// The container the client has asked to open and is still waiting for,
    /// or zero when nothing is pending.
    /// </summary>
    uint RequestedContainerId => 0u;

    /// <summary>
    /// The container currently open, or zero when none is.
    /// </summary>
    uint CurrentContainerId => 0u;

    /// <summary>
    /// The server's answer to the last item use, including one this surface
    /// sent. Default until something completes.
    /// </summary>
    PluginItemUseCompletion LastItemUseCompletion => default;

    /// <summary>
    /// The server's answer to the last inventory request -- a pickup, move,
    /// or similar. Default until something completes.
    /// </summary>
    PluginInventoryCompletion LastInventoryCompletion => default;

    /// <summary>
    /// The shared appraisal slot's current state, for polling an
    /// <see cref="Identify"/> to completion.
    /// </summary>
    PluginAppraisalState Appraisal => default;

    /// <summary>
    /// Lists the corpses lying loose within
    /// <paramref name="maximumDistance"/> metres, nearest first. Corpses held
    /// inside something else are left out. Returns an empty list when the
    /// surface is unavailable or the distance is not a positive number.
    /// </summary>
    IReadOnlyList<PluginLootContainer> CaptureCorpses(float maximumDistance) =>
        Array.Empty<PluginLootContainer>();

    /// <summary>
    /// Lists everything inside the currently open container, following into
    /// any containers nested in it. Returns an empty list when no container
    /// is open.
    /// </summary>
    IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() =>
        Array.Empty<PluginInventoryItem>();

    /// <summary>
    /// Reads the property tables the client holds for one item inside the
    /// currently open container. Returns false when no container is open or
    /// the object is not in it -- use the object surface to read anything
    /// else.
    /// </summary>
    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    /// <summary>
    /// Asks the server to open a corpse or other openable container, exactly
    /// as using it does. Reports
    /// <see cref="PluginItemCommandStatus.InvalidTarget"/> for an object that
    /// is neither, and <see cref="PluginItemCommandStatus.Busy"/> while
    /// another inventory request is in flight. The container's contents
    /// arrive later, not from this call.
    /// </summary>
    PluginItemCommandResult Open(uint containerObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Identifies an item scoped to loot handling: only the currently open
    /// corpse/container's contents, or the corpse itself, is a valid
    /// target. Use IWorldObjectAutomation.Identify to assess any object
    /// the client can currently see (owned, equipped, landscape, a vendor
    /// listing, or open-container content) -- this member exists
    /// separately because a loot-sorting plugin should not accidentally
    /// identify something outside the container it is currently working.
    /// </summary>
    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to move one item out of the currently open container
    /// and into the player's packs, letting the client choose the pack the
    /// same way its own loot click does; pass true for
    /// <paramref name="mainPack"/> to prefer the main pack. Reports
    /// <see cref="PluginItemCommandStatus.InvalidItem"/> for anything that is
    /// not in the open container. If no pack has room, the client says so
    /// locally and nothing goes to the server.
    /// </summary>
    PluginItemCommandResult Pickup(
        uint objectId,
        bool mainPack = false) =>
        new(PluginItemCommandStatus.Unavailable);
}
