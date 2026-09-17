namespace AcDream.Plugin.Abstractions;

/// <summary>How the client answered a plugin's vendor command.</summary>
public enum PluginVendorCommandStatus
{
    /// <summary>
    /// The command could not be run: there is no in-world session, no live
    /// network session, or this host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>No vendor's shop pane is open, so there is nothing to command.</summary>
    NotOpen,

    /// <summary>
    /// The item was not usable for this command -- an unknown id, a count of
    /// zero or less, something the vendor does not list, or, when selling, an
    /// item the player does not own or is currently wearing.
    /// </summary>
    InvalidItem,

    /// <summary>A buy or sell this surface sent is still awaiting its answer.</summary>
    Busy,

    /// <summary>
    /// The command took effect: a staged list changed, or a buy/sell went out
    /// to the server.
    /// </summary>
    Sent,

    /// <summary>The client declined the command.</summary>
    Refused,
}

/// <summary>The outcome of one vendor command, with an optional explanation.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginVendorCommandResult(
    PluginVendorCommandStatus Status,
    string? Notice = null)
{
    /// <summary>True when the command took effect.</summary>
    public bool Accepted => Status == PluginVendorCommandStatus.Sent;
}

/// <summary>One item a vendor currently has listed for sale.</summary>
/// <param name="TemplateObjectId">
/// The id of the vendor's listing. It identifies the shop entry, not an item
/// the player owns, and it is the id every buy command refers to.
/// </param>
/// <param name="WeenieClassId">The item's class id, shared by every copy of it.</param>
/// <param name="Name">The item's display name.</param>
/// <param name="ObjectClass">The broad category the listing falls into.</param>
/// <param name="UnitPrice">
/// What the vendor charges for one of these, already worked out at the
/// vendor's own mark-up.
/// </param>
/// <param name="StackSize">How many the listing sells as one stack.</param>
public readonly record struct PluginVendorItem(
    uint TemplateObjectId,
    uint WeenieClassId,
    string Name,
    PluginObjectClass ObjectClass,
    int UnitPrice,
    int StackSize);

/// <summary>Which direction a completed vendor transaction went.</summary>
public enum PluginVendorTransactionKind
{
    /// <summary>The player bought from the vendor.</summary>
    Buy = 0,

    /// <summary>The player sold to the vendor.</summary>
    Sell,
}

/// <summary>The server's answer to a committed buy or sell.</summary>
/// <param name="Kind">Whether this was a buy or a sell.</param>
/// <param name="Success">True when the server carried the transaction out.</param>
/// <param name="Notice">
/// A short human-readable reason when the transaction failed; null otherwise.
/// </param>
public readonly record struct PluginVendorTransaction(
    PluginVendorTransactionKind Kind,
    bool Success,
    string? Notice);

/// <summary>
/// Reads the open vendor's stock and shops with it. Buying and selling is
/// two-step: items are staged into a local buy or sell list, which costs
/// nothing and touches no wire, and then <see cref="BuyAll"/> or
/// <see cref="SellAll"/> commits the whole list at once and empties it.
/// </summary>
public interface IVendorAutomation
{
    /// <summary>True when the session is in the world, so vendor commands can run.</summary>
    bool IsAvailable => false;

    /// <summary>True while a vendor's shop pane is open.</summary>
    bool IsOpen => false;

    /// <summary>The open vendor's object id, or zero when none is open.</summary>
    uint VendorObjectId => 0u;

    /// <summary>
    /// The open vendor's name, or an empty string when no vendor is open or
    /// the client has no object for it.
    /// </summary>
    string VendorName => string.Empty;

    /// <summary>
    /// Everything the open vendor currently has for sale; empty when no
    /// vendor is open.
    /// </summary>
    IReadOnlyList<PluginVendorItem> Items => Array.Empty<PluginVendorItem>();

    /// <summary>
    /// True while a buy or sell this surface committed is still awaiting the
    /// server's answer. This is vendor-local; it is not the client-wide
    /// inventory-request gate, which vendor transactions never take.
    /// </summary>
    bool IsBusy => false;

    /// <summary>
    /// Reads the property tables the vendor's listing already carried for one
    /// of its items, including the weapon and armor profiles when it has
    /// them. This is the data that arrived with the shop listing, not a fresh
    /// appraisal. Returns false when the client has no object for that
    /// listing id.
    /// </summary>
    bool TryCaptureProperties(
        uint templateObjectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    /// <summary>Every item currently staged to buy, with its requested count.</summary>
    IReadOnlyList<(uint TemplateObjectId, int Count)> BuyList =>
        Array.Empty<(uint, int)>();

    /// <summary>Every owned item currently staged to sell.</summary>
    IReadOnlyList<uint> SellList => Array.Empty<uint>();

    /// <summary>
    /// Stages one of the vendor's listings to buy, adding to the count
    /// already staged for it. Nothing is sent until <see cref="BuyAll"/>.
    /// </summary>
    PluginVendorCommandResult AddToBuyList(uint templateObjectId, int count) =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>
    /// Stages one owned item to sell. The item must be owned by the player,
    /// not currently equipped, and not itself a vendor listing. Nothing is
    /// sent until <see cref="SellAll"/>.
    /// </summary>
    PluginVendorCommandResult AddToSellList(uint itemObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Un-stages a listing from the buy list.</summary>
    PluginVendorCommandResult RemoveFromBuyList(uint templateObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Un-stages an item from the sell list.</summary>
    PluginVendorCommandResult RemoveFromSellList(uint itemObjectId) =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Empties the buy list without sending anything.</summary>
    PluginVendorCommandResult ClearBuyList() =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Empties the sell list without sending anything.</summary>
    PluginVendorCommandResult ClearSellList() =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to buy everything staged in the buy list, then empties
    /// the list. Reports <see cref="PluginVendorCommandStatus.InvalidItem"/>
    /// when nothing is staged and <see cref="PluginVendorCommandStatus.Busy"/>
    /// when an earlier buy or sell is still outstanding. The server's answer
    /// arrives on <see cref="TransactionCompleted"/>.
    /// </summary>
    PluginVendorCommandResult BuyAll() =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to sell everything staged in the sell list, each item
    /// in its full stack, then empties the list. Gated exactly like
    /// <see cref="BuyAll"/>, and answered on
    /// <see cref="TransactionCompleted"/>.
    /// </summary>
    PluginVendorCommandResult SellAll() =>
        new(PluginVendorCommandStatus.Unavailable);

    /// <summary>Raised when a vendor's shop pane becomes the open one.</summary>
    event Action<uint> Opened
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the open vendor's shop pane closes.</summary>
    event Action Closed
    {
        add { }
        remove { }
    }

    /// <summary>Raised when a buy-all or sell-all transaction completes.</summary>
    event Action<PluginVendorTransaction> TransactionCompleted
    {
        add { }
        remove { }
    }
}
