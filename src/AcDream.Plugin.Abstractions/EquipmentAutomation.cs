namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One item the player owns that can be worn or wielded, with the numbers an
/// equipment-policy plugin needs to choose between candidates.
/// </summary>
/// <param name="ObjectId">The item's object id.</param>
/// <param name="Name">The item's display name.</param>
/// <param name="ItemType">
/// The item's type bit mask (weapon, armor, clothing, and so on).
/// </param>
/// <param name="ValidLocations">
/// Bit mask of the equipment slots this item is allowed to occupy. An item
/// with no valid slot is not reported by this surface at all.
/// </param>
/// <param name="EquippedLocation">
/// Bit mask of the slot the item currently occupies, or zero when it is not
/// equipped.
/// </param>
/// <param name="ContainerObjectId">
/// The container holding the item, or zero when it is not inside one.
/// </param>
/// <param name="WielderObjectId">
/// The creature wielding the item, or zero when nobody is.
/// </param>
/// <param name="CombatUse">
/// What combat role the item fills (melee weapon, missile weapon, shield,
/// ammunition) as the item's own data reports it; zero when it has none.
/// </param>
/// <param name="DamageType">The damage type the item deals, from its property table.</param>
/// <param name="WeaponSkill">The skill the item attacks with, from its property table.</param>
/// <param name="Damage">The item's damage rating, from its property table.</param>
/// <param name="DamageVariance">
/// How far below <paramref name="Damage"/> a hit can roll, as a fraction of
/// it: 0.2 means a hit lands between 80% and 100% of the damage rating.
/// </param>
public readonly record struct PluginEquipmentItem(
    uint ObjectId,
    string Name,
    uint ItemType,
    uint ValidLocations,
    uint EquippedLocation,
    uint ContainerObjectId,
    uint WielderObjectId,
    byte CombatUse,
    int DamageType,
    int WeaponSkill,
    int Damage,
    double DamageVariance)
{
    /// <summary>True when the item currently occupies an equipment slot.</summary>
    public bool IsEquipped => EquippedLocation != 0u;

    /// <summary>
    /// The ammunition type a launcher fires or a stack of ammunition is;
    /// zero for anything else.
    /// </summary>
    public uint AmmoType { get; init; }

    /// <summary>How many of the item this stack holds; at least one.</summary>
    public int StackSize { get; init; } = 1;

    /// <summary>The weapon category the item belongs to; zero when it is not a weapon.</summary>
    public int WeaponType { get; init; }
}

/// <summary>How the client answered a plugin's equip request.</summary>
public enum PluginEquipmentCommandStatus
{
    /// <summary>The surface is not usable right now -- no session is in the world, or this host does not provide it.</summary>
    Unavailable = 0,

    /// <summary>The object id is unknown to the client, or the item cannot be equipped at all.</summary>
    InvalidItem,

    /// <summary>An equip the client started earlier is still running.</summary>
    Busy,

    /// <summary>The item already occupies a slot that satisfies the request; nothing was sent.</summary>
    AlreadyEquipped,

    /// <summary>The request went out to the server.</summary>
    Started,

    /// <summary>The client declined to send the request.</summary>
    Refused,
}

/// <summary>The outcome of one equip request, with an optional explanation.</summary>
/// <param name="Status">What the client did with the request.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginEquipmentCommandResult(
    PluginEquipmentCommandStatus Status,
    string? Notice = null)
{
    /// <summary>
    /// True when the item is now on its way to the requested slot, or was
    /// already there.
    /// </summary>
    public bool Accepted => Status is
        PluginEquipmentCommandStatus.AlreadyEquipped
        or PluginEquipmentCommandStatus.Started;
}

/// <summary>
/// Reads what the player owns that can be worn or wielded, and asks the
/// server to equip a chosen item.
/// </summary>
public interface IEquipmentAutomation
{
    /// <summary>
    /// True when this surface can be used: the session is in the world and
    /// the host wired up equipping.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>True while an equip the client started is still running.</summary>
    bool IsBusy => false;

    /// <summary>
    /// Lists every owned item that can occupy an equipment slot, equipped
    /// items first and then by name. Returns an empty list when the surface
    /// is unavailable.
    /// </summary>
    IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();

    /// <summary>
    /// Asks the server to wear or wield an owned item, the same way dragging
    /// it onto the character does. Pass zero for
    /// <paramref name="requestedLocation"/> to let the client pick the slot,
    /// or a slot bit mask to ask for a specific one.
    /// </summary>
    PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
}
