namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Semantic operations and roles the host recognizes for an object.
/// </summary>
[Flags]
public enum PluginObjectCapabilities
{
    /// <summary>No normalized capability is known.</summary>
    None = 0,
    /// <summary>The object can be activated or otherwise interacted with.</summary>
    Interactable = 1 << 0,
    /// <summary>The object is a portal device or portal target.</summary>
    Portal = 1 << 1,
    /// <summary>The object is a door.</summary>
    Door = 1 << 2,
    /// <summary>The object is a vendor.</summary>
    Vendor = 1 << 3,
    /// <summary>The object is a container.</summary>
    Container = 1 << 4,
    /// <summary>The object is a player character.</summary>
    Player = 1 << 5,
    /// <summary>The object is a non-player character.</summary>
    Npc = 1 << 6,
}

/// <summary>
/// A broad category for an object, worked out from its item type and its
/// public flags. It is a convenience for plugins that want to say "this is a
/// weapon" without decoding bit masks themselves.
/// </summary>
public enum PluginObjectClass
{
    /// <summary>Nothing in the object's data placed it in a category.</summary>
    Unknown = 0,

    /// <summary>A hand weapon.</summary>
    MeleeWeapon = 1,

    /// <summary>A piece of armor.</summary>
    Armor = 2,

    /// <summary>A piece of clothing.</summary>
    Clothing = 3,

    /// <summary>A ring, necklace, or other trinket.</summary>
    Jewelry = 4,

    /// <summary>A hostile creature.</summary>
    Monster = 5,

    /// <summary>Something edible or drinkable.</summary>
    Food = 6,

    /// <summary>Coin.</summary>
    Money = 7,

    /// <summary>An item that fits no more specific category.</summary>
    Misc = 8,

    /// <summary>A bow, crossbow, or thrown weapon.</summary>
    MissileWeapon = 9,

    /// <summary>A pack, chest, or other container.</summary>
    Container = 10,

    /// <summary>A gemstone.</summary>
    Gem = 11,

    /// <summary>A spell component.</summary>
    SpellComponent = 12,

    /// <summary>A key.</summary>
    Key = 13,

    /// <summary>A portal.</summary>
    Portal = 14,

    /// <summary>A trade note standing in for coin.</summary>
    TradeNote = 15,

    /// <summary>A mana stone.</summary>
    ManaStone = 16,

    /// <summary>A plant or herb.</summary>
    Plant = 17,

    /// <summary>A raw cooking ingredient.</summary>
    BaseCooking = 18,

    /// <summary>A raw alchemy ingredient.</summary>
    BaseAlchemy = 19,

    /// <summary>A raw fletching ingredient.</summary>
    BaseFletching = 20,

    /// <summary>A finished cooking product.</summary>
    CraftedCooking = 21,

    /// <summary>A finished alchemy product.</summary>
    CraftedAlchemy = 22,

    /// <summary>A finished fletching product.</summary>
    CraftedFletching = 23,

    /// <summary>Another player character.</summary>
    Player = 24,

    /// <summary>A shopkeeper.</summary>
    Vendor = 25,

    /// <summary>A door.</summary>
    Door = 26,

    /// <summary>A corpse.</summary>
    Corpse = 27,

    /// <summary>A lifestone.</summary>
    Lifestone = 28,

    /// <summary>A healing kit.</summary>
    HealingKit = 29,

    /// <summary>A lockpick.</summary>
    Lockpick = 30,

    /// <summary>A wand, staff, or orb -- a spell-casting implement.</summary>
    WandStaffOrb = 31,

    /// <summary>A bundle of items.</summary>
    Bundle = 32,

    /// <summary>A book.</summary>
    Book = 33,

    /// <summary>A journal.</summary>
    Journal = 34,

    /// <summary>A readable sign.</summary>
    Sign = 35,

    /// <summary>A house or a housing fixture.</summary>
    Housing = 36,

    /// <summary>A non-hostile character the player can talk to.</summary>
    Npc = 37,

    /// <summary>A casting focus.</summary>
    Foci = 38,

    /// <summary>Salvage material.</summary>
    Salvage = 39,

    /// <summary>An ultimate salvaging tool.</summary>
    Ust = 40,

    /// <summary>Something that provides a service rather than an item.</summary>
    Services = 41,

    /// <summary>A spell scroll.</summary>
    Scroll = 42,

    /// <summary>A creature fighting on the player's side.</summary>
    CombatPet = 43,
}

/// <summary>
/// Turns an object's item-type mask and public flags into a
/// <see cref="PluginObjectClass"/>. It is a plain function over those two
/// numbers, so a value that exists only as loose fields -- a vendor's shop
/// listing, for instance -- can be classified without a full object.
/// </summary>
public static class PluginObjectClassifier
{
    /// <summary>
    /// Classifies one object from its item-type mask and public flags. The
    /// public flags win where the two disagree, so a container that is also a
    /// corpse reads as a corpse. Reports
    /// <see cref="PluginObjectClass.Unknown"/> when neither says anything.
    /// </summary>
    public static PluginObjectClass Classify(uint itemType, uint publicWeenieBitfield)
    {
        PluginObjectClass result = itemType switch
        {
            _ when (itemType & 0x00000001u) != 0u => PluginObjectClass.MeleeWeapon,
            _ when (itemType & 0x00000002u) != 0u => PluginObjectClass.Armor,
            _ when (itemType & 0x00000004u) != 0u => PluginObjectClass.Clothing,
            _ when (itemType & 0x00000008u) != 0u => PluginObjectClass.Jewelry,
            _ when (itemType & 0x00000010u) != 0u => PluginObjectClass.Monster,
            _ when (itemType & 0x00000020u) != 0u => PluginObjectClass.Food,
            _ when (itemType & 0x00000040u) != 0u => PluginObjectClass.Money,
            _ when (itemType & 0x00000080u) != 0u => PluginObjectClass.Misc,
            _ when (itemType & 0x00000100u) != 0u => PluginObjectClass.MissileWeapon,
            _ when (itemType & 0x00000200u) != 0u => PluginObjectClass.Container,
            _ when (itemType & 0x00000400u) != 0u => PluginObjectClass.Bundle,
            _ when (itemType & 0x00000800u) != 0u => PluginObjectClass.Gem,
            _ when (itemType & 0x00001000u) != 0u => PluginObjectClass.SpellComponent,
            _ when (itemType & 0x00004000u) != 0u => PluginObjectClass.Key,
            _ when (itemType & 0x00008000u) != 0u => PluginObjectClass.WandStaffOrb,
            _ when (itemType & 0x00010000u) != 0u => PluginObjectClass.Portal,
            _ when (itemType & 0x00040000u) != 0u => PluginObjectClass.TradeNote,
            _ when (itemType & 0x00080000u) != 0u => PluginObjectClass.ManaStone,
            _ when (itemType & 0x00100000u) != 0u => PluginObjectClass.Services,
            _ when (itemType & 0x00200000u) != 0u => PluginObjectClass.Plant,
            _ when (itemType & 0x00400000u) != 0u => PluginObjectClass.BaseCooking,
            _ when (itemType & 0x00800000u) != 0u => PluginObjectClass.BaseAlchemy,
            _ when (itemType & 0x01000000u) != 0u => PluginObjectClass.BaseFletching,
            _ when (itemType & 0x02000000u) != 0u => PluginObjectClass.CraftedCooking,
            _ when (itemType & 0x04000000u) != 0u => PluginObjectClass.CraftedAlchemy,
            _ when (itemType & 0x08000000u) != 0u => PluginObjectClass.CraftedFletching,
            _ when (itemType & 0x20000000u) != 0u => PluginObjectClass.Ust,
            _ when (itemType & 0x40000000u) != 0u => PluginObjectClass.Salvage,
            _ => PluginObjectClass.Unknown,
        };

        result = publicWeenieBitfield switch
        {
            _ when (publicWeenieBitfield & 0x00000008u) != 0u => PluginObjectClass.Player,
            _ when (publicWeenieBitfield & 0x00000200u) != 0u => PluginObjectClass.Vendor,
            _ when (publicWeenieBitfield & 0x00001000u) != 0u => PluginObjectClass.Door,
            _ when (publicWeenieBitfield & 0x00002000u) != 0u => PluginObjectClass.Corpse,
            _ when (publicWeenieBitfield & 0x00004000u) != 0u => PluginObjectClass.Lifestone,
            _ when (publicWeenieBitfield & 0x00008000u) != 0u => PluginObjectClass.Food,
            _ when (publicWeenieBitfield & 0x00010000u) != 0u => PluginObjectClass.HealingKit,
            _ when (publicWeenieBitfield & 0x00020000u) != 0u => PluginObjectClass.Lockpick,
            _ when (publicWeenieBitfield & 0x00040000u) != 0u => PluginObjectClass.Portal,
            _ when (publicWeenieBitfield & 0x00800000u) != 0u => PluginObjectClass.Foci,
            _ when (publicWeenieBitfield & 0x00000001u) != 0u => PluginObjectClass.Container,
            _ => result,
        };

        if ((itemType & 0x00002000u) != 0u && result == PluginObjectClass.Unknown)
        {
            result = (publicWeenieBitfield & 0x00000002u) != 0u
                ? PluginObjectClass.Journal
                : (publicWeenieBitfield & 0x00000004u) != 0u
                    ? PluginObjectClass.Sign
                    : (publicWeenieBitfield & 0x0000000Fu) != 0u
                        ? PluginObjectClass.Book
                        : result;
        }
        if (result == PluginObjectClass.Monster && (publicWeenieBitfield & 0x10u) == 0u)
            result = PluginObjectClass.Npc;
        if (result == PluginObjectClass.Monster && (publicWeenieBitfield & 0x04000000u) != 0u)
            result = PluginObjectClass.CombatPet;
        return result;
    }

    /// <summary>Maps a normalized object class to semantic interaction capabilities.</summary>
    public static PluginObjectCapabilities Capabilities(PluginObjectClass objectClass) =>
        objectClass switch
        {
            PluginObjectClass.Portal => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Portal,
            PluginObjectClass.Door => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Door,
            PluginObjectClass.Vendor => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Vendor,
            PluginObjectClass.Container => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Container,
            PluginObjectClass.Player => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Player,
            PluginObjectClass.Npc => PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Npc,
            PluginObjectClass.Corpse
                or PluginObjectClass.Lifestone
                or PluginObjectClass.Services => PluginObjectCapabilities.Interactable,
            _ => PluginObjectCapabilities.None,
        };
}

/// <summary>
/// One object the client is currently tracking, whether it stands in the
/// world, sits in a container, or is carried by someone.
/// </summary>
/// <param name="ObjectId">The object's id.</param>
/// <param name="WeenieClassId">
/// The object's class id, shared by every copy of it; zero when the client
/// has no detail for the object beyond its presence.
/// </param>
/// <param name="Name">
/// The object's display name. Falls back to the name the world update
/// carried, and finally to the object id written in hexadecimal.
/// </param>
/// <param name="ObjectClass">The broad category the object falls into.</param>
/// <param name="ItemType">The object's item-type bit mask.</param>
/// <param name="ContainerObjectId">
/// The container holding the object, or zero when it is not inside one.
/// </param>
/// <param name="WielderObjectId">
/// The creature carrying or wearing the object, or zero when nobody is.
/// </param>
public readonly record struct PluginWorldObject(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    PluginObjectClass ObjectClass,
    uint ItemType,
    uint ContainerObjectId,
    uint WielderObjectId)
{
    /// <summary>
    /// Semantic operations the host can perform or recognize for this object.
    /// Plugins should use these flags instead of decoding item-type or public
    /// weenie bitfields themselves.
    /// </summary>
    public PluginObjectCapabilities Capabilities { get; init; }

    /// <summary>True when the host recognizes an activation interaction.</summary>
    public bool CanActivate =>
        (Capabilities & PluginObjectCapabilities.Interactable) != 0;

    /// <summary>
    /// True when the local player owns the object, counting anything nested
    /// inside a pack they carry.
    /// </summary>
    public bool IsOwned { get; init; }

    /// <summary>
    /// True when the object stands out in the world: it has a position and is
    /// neither owned, contained, nor carried.
    /// </summary>
    public bool IsLandscape { get; init; }

    /// <summary>True when the client knows where the object is.</summary>
    public bool HasPosition { get; init; }

    /// <summary>
    /// Where the object is; only meaningful when
    /// <see cref="HasPosition"/> is true.
    /// </summary>
    public PluginNavigationPosition Position { get; init; }

    /// <summary>
    /// True once the client holds property data for the object, which is what
    /// an appraisal delivers.
    /// </summary>
    public bool HasAppraisalData { get; init; }

    /// <summary>
    /// The client's own millisecond timestamp for the object's last
    /// appraisal; zero when it has never been appraised.
    /// </summary>
    public int LastIdTime { get; init; }

    /// <summary>True when the object is a door and that door stands open.</summary>
    public bool IsDoorOpen { get; init; }

    /// <summary>How many of the object this stack holds; at least one.</summary>
    public int StackSize { get; init; } = 1;

    /// <summary>How many loose items the object can hold, when it is a container.</summary>
    public int ItemsCapacity { get; init; }

    /// <summary>How many packs the object can hold, when it is a container.</summary>
    public int ContainersCapacity { get; init; }

    /// <summary>
    /// The spells an appraisal reported on the object; empty until it has
    /// been appraised.
    /// </summary>
    public IReadOnlyList<uint> SpellIds { get; init; } = Array.Empty<uint>();

    /// <summary>
    /// The enchantments currently running on the object. Only the local
    /// player's own entry is ever filled in, and only on a host that tracks
    /// them; every other object reports an empty list.
    /// </summary>
    public IReadOnlyList<uint> ActiveSpellIds { get; init; } = Array.Empty<uint>();

    /// <summary>The object's icon, for a plugin that draws its own UI.</summary>
    public uint IconId { get; init; }

    /// <summary>
    /// A portal's appraised destination label, or null when the client does
    /// not know one. This is a display label, not a destination cell id.
    /// </summary>
    public string? PortalDestination { get; init; }

    /// <summary>
    /// A portal's minimum character level, or null when no minimum is known.
    /// </summary>
    public int? PortalMinimumLevel { get; init; }

    /// <summary>
    /// A portal's maximum character level, or null when no maximum is known.
    /// </summary>
    public int? PortalMaximumLevel { get; init; }
}

/// <summary>
/// The outcome of a world-object activation (portal, door, NPC, or other
/// interactable landscape object). This reports how the activation
/// completed, including failure and interruption states.
/// </summary>
/// <param name="Revision">
/// Counts up by one for every completion, so a plugin can tell a fresh one
/// from one it has already seen. Zero means nothing has completed yet.
/// </param>
/// <param name="ObjectId">The object that was activated.</param>
/// <param name="Outcome">The activation outcome.</param>
/// <param name="WeenieError">
/// The server's error code, when applicable; zero means success.
/// </param>
public readonly record struct PluginActivationCompletion(
    long Revision,
    uint ObjectId,
    PluginActivationOutcome Outcome,
    uint WeenieError)
{
    /// <summary>
    /// True when the activation completed successfully with no error.
    /// </summary>
    public bool IsSuccess => Revision != 0 && Outcome == PluginActivationOutcome.Completed && WeenieError == 0u;
}

/// <summary>How a world-object activation concluded.</summary>
public enum PluginActivationOutcome
{
    /// <summary>No activation has completed yet, or the outcome is unknown.</summary>
    None = 0,

    /// <summary>The activation completed successfully.</summary>
    Completed,

    /// <summary>
    /// The activation was refused by the server; check
    /// <see cref="PluginActivationCompletion.WeenieError"/>.
    /// </summary>
    Refused,

    /// <summary>
    /// The approach to the target was interrupted (movement cancelled,
    /// target went out of range, or the player moved).
    /// </summary>
    Interrupted,

    /// <summary>
    /// The target became invalid or was destroyed before the activation
    /// could complete.
    /// </summary>
    TargetLost,

    /// <summary>The host could not reach the target (blocked path).</summary>
    Blocked,

    /// <summary>The activation timed out waiting for a server response.</summary>
    TimedOut,
}

/// <summary>
/// Reads the objects the client currently knows about -- everything in the
/// world around the player as well as everything they carry -- and asks the
/// server to appraise one of them.
/// </summary>
public interface IWorldObjectAutomation
{
    /// <summary>True when the session is in the world, so these reads are meaningful.</summary>
    bool IsAvailable => false;

    /// <summary>
    /// The external container currently open (a corpse, a chest, a storage
    /// crate), or zero when none is.
    /// </summary>
    uint OpenContainerObjectId => 0u;

    /// <summary>
    /// Lists every object the client is tracking, ordered by object id.
    /// Returns an empty list when the session is not in the world.
    /// </summary>
    IReadOnlyList<PluginWorldObject> CaptureObjects() =>
        Array.Empty<PluginWorldObject>();

    /// <summary>
    /// Reads one object by id. Returns false when the session is not in the
    /// world or the client has never seen that object.
    /// </summary>
    bool TryGet(uint objectId, out PluginWorldObject value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// Reads the property tables the client holds for one object, including
    /// its weapon and armor profiles once it has been appraised. Returns
    /// false when the client has no object for that id; an object it has
    /// never appraised yields mostly empty tables rather than false.
    /// </summary>
    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    /// <summary>
    /// The most recent activation completion. Default until anything
    /// completes, fails, or is interrupted.
    /// </summary>
    PluginActivationCompletion LastActivationCompletion => default;

    /// <summary>
    /// One integer property of one object. A caller reading a single property
    /// inside a per-object predicate should use this rather than
    /// <see cref="TryCaptureProperties"/>, which copies the object's whole
    /// property bundle. The default answers from that copy, so a surface that
    /// has not specialised it is correct but not cheap.
    /// </summary>
    bool TryGetIntProperty(uint objectId, uint property, out int value)
    {
        if (TryCaptureProperties(objectId, out PluginItemProperties properties)
            && properties.Ints is { } ints
            && ints.TryGetValue(property, out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Requests an appraisal of any object present in the object table --
    /// owned inventory, equipped, landscape, a vendor listing, or an open
    /// container's content -- through the same appraisal request the
    /// client's own assess uses, gated the same way (Busy while another
    /// inventory request is in flight). Contrast ILootAutomation.Identify,
    /// which only accepts the currently open corpse/container's contents.
    /// </summary>
    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Activates a known world object such as a portal, door, vendor, NPC, or
    /// external container. The host may approach an out-of-range object first;
    /// <see cref="PluginItemCommandStatus.Started"/> means the interaction
    /// was accepted, not that the world transition has completed. Use
    /// <see cref="IEvents.PortalTransition"/> and object changes for follow-up
    /// state.
    /// </summary>
    PluginItemCommandResult Activate(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
}
