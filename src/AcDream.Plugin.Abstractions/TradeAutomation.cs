namespace AcDream.Plugin.Abstractions;

/// <summary>How the client answered a plugin's secure-trade command.</summary>
public enum PluginTradeCommandStatus
{
    /// <summary>
    /// The command could not be sent: there is no in-world session, or this
    /// host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>No trade window is open, so there is nothing to command.</summary>
    NotOpen,

    /// <summary>The item id was not usable (zero).</summary>
    InvalidItem,

    /// <summary>Another trade request is still outstanding, so this one was not sent.</summary>
    Busy,

    /// <summary>The command was sent to the server.</summary>
    Sent,

    /// <summary>The client declined to send the command.</summary>
    Refused,

    /// <summary>
    /// Accept() was called when the local side had already accepted --
    /// nothing was sent, since the client's own trade window disables its
    /// Accept button the same way once MyAccepted is true.
    /// </summary>
    AlreadyAccepted,
}

/// <summary>The outcome of one trade command, with an optional explanation.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginTradeCommandResult(
    PluginTradeCommandStatus Status,
    string? Notice = null)
{
    /// <summary>True when the command actually went out to the server.</summary>
    public bool Accepted => Status == PluginTradeCommandStatus.Sent;
}

/// <summary>
/// The pair of participants in a trade that just opened. There is no
/// authoritative "who asked first" bit in the tracked trade state, so
/// <paramref name="InitiatorObjectId"/> is always the local player and
/// <paramref name="PartnerObjectId"/> is always the other side, regardless
/// of who actually sent the open request.
/// </summary>
public readonly record struct PluginTradeOpened(
    uint InitiatorObjectId,
    uint PartnerObjectId);

/// <summary>
/// One item newly staged into the trade. <paramref name="Mine"/>
/// distinguishes which side's grid it landed in, since the underlying
/// state tracks the two sides separately.
/// </summary>
public readonly record struct PluginTradeItemAdded(
    uint ItemObjectId,
    bool Mine);

/// <summary>
/// Reads and drives the secure trade window -- the two-sided exchange where
/// each player stages items and both must accept. Commands send exactly what
/// the window's own buttons send; the events are raised on the host's tick
/// thread as the tracked trade state changes.
/// </summary>
public interface ITradeAutomation
{
    /// <summary>
    /// True when this surface can be used: the session is in the world and
    /// the host provides trading.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>True while a trade window is open with a partner.</summary>
    bool IsOpen => false;

    /// <summary>The trade partner's object id, or zero when no trade is open.</summary>
    uint PartnerObjectId => 0u;

    /// <summary>
    /// The trade partner's character name, or an empty string when no trade
    /// is open or the client has no object for the partner.
    /// </summary>
    string PartnerName => string.Empty;

    /// <summary>The items the local player has staged, empty when none are.</summary>
    IReadOnlyList<uint> MyItems => Array.Empty<uint>();

    /// <summary>The items the partner has staged, empty when none are.</summary>
    IReadOnlyList<uint> PartnerItems => Array.Empty<uint>();

    /// <summary>True once the local player has accepted the current contents.</summary>
    bool MyAccepted => false;

    /// <summary>True once the partner has accepted the current contents.</summary>
    bool PartnerAccepted => false;

    /// <summary>
    /// Asks the server to stage one owned item into the local side of the
    /// trade, the same as dragging it into the window.
    /// </summary>
    PluginTradeCommandResult Add(uint itemObjectId) =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>
    /// Accepts the trade as it currently stands. Returns
    /// <see cref="PluginTradeCommandStatus.AlreadyAccepted"/> without
    /// touching the wire when the local side has already accepted.
    /// </summary>
    PluginTradeCommandResult Accept() =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>
    /// Withdraws the local side's acceptance. Unlike <see cref="Accept"/>
    /// this carries no already-done guard and always sends.
    /// </summary>
    PluginTradeCommandResult Decline() =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to clear everything staged on both sides, leaving the
    /// window open.
    /// </summary>
    PluginTradeCommandResult Reset() =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to close the trade outright, cancelling it for both
    /// sides.
    /// </summary>
    PluginTradeCommandResult End() =>
        new(PluginTradeCommandStatus.Unavailable);

    /// <summary>Raised when a trade window opens with a partner.</summary>
    event Action<PluginTradeOpened> Opened
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the trade window closes, for any reason.</summary>
    event Action Closed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when the partner accepts the trade; the argument is the
    /// partner's object id. Named <c>PartnerTradeAccepted</c> rather than
    /// <c>PartnerAccepted</c> because C# forbids a property and an event
    /// with the same name on one interface, and <see cref="PartnerAccepted"/>
    /// is already the live acceptance flag.
    /// </summary>
    event Action<uint> PartnerTradeAccepted
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised once per item newly staged into either side of the trade.
    /// </summary>
    event Action<PluginTradeItemAdded> ItemAdded
    {
        add { }
        remove { }
    }
}
