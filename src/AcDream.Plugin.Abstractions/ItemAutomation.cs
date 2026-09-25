namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One color-swap range that recolors part of an item, together with a sample
/// of the color it produces, so a plugin can tell two otherwise identical
/// items apart by their dye.
/// </summary>
/// <param name="PaletteId">The color set the swap draws from.</param>
/// <param name="Offset">Where in the item's color range the swap starts.</param>
/// <param name="Length">How much of that range the swap covers.</param>
/// <param name="Red">
/// Red channel of the color in the middle of the swapped range, 0 to 255.
/// </param>
/// <param name="Green">Green channel of that sampled color, 0 to 255.</param>
/// <param name="Blue">Blue channel of that sampled color, 0 to 255.</param>
public readonly record struct PluginPaletteInfo(
    uint PaletteId,
    byte Offset,
    byte Length,
    byte Red,
    byte Green,
    byte Blue);

/// <summary>
/// One item the player owns -- in a pack, equipped, or wielded -- flattened
/// into the fields an inventory plugin needs. Numbers that only an appraisal
/// can supply read as zero until the item has been appraised.
/// </summary>
/// <param name="ObjectId">The item's object id.</param>
/// <param name="WeenieClassId">The item's class id, shared by every copy of it.</param>
/// <param name="Name">The item's display name.</param>
/// <param name="ItemType">The item's type bit mask (weapon, armor, food, and so on).</param>
/// <param name="ContainerObjectId">
/// The container holding the item, or zero when it is not inside one.
/// </param>
/// <param name="WielderObjectId">
/// The creature wielding the item, or zero when nobody is.
/// </param>
/// <param name="ValidLocations">
/// Bit mask of the equipment slots the item may occupy; zero when it cannot
/// be equipped at all.
/// </param>
/// <param name="EquippedLocation">
/// Bit mask of the slot the item currently occupies; zero when it is not
/// equipped.
/// </param>
/// <param name="Useability">
/// How the item may be used -- in particular whether using it needs a second
/// object as a target.
/// </param>
/// <param name="TargetType">
/// The kind of object the item may be applied to, when it is a targeted-use
/// item.
/// </param>
/// <param name="PublicFlags">
/// The object's public flag bits: the same bits that mark something as a
/// door, a corpse, a vendor, and so on.
/// </param>
/// <param name="StackSize">How many of the item this stack holds.</param>
/// <param name="Structure">
/// The item's remaining charges or uses; zero when it has no such counter.
/// </param>
/// <param name="MaximumStructure">The ceiling for <paramref name="Structure"/>.</param>
/// <param name="SpellId">
/// The spell the item casts when used, such as a scroll's; zero when it casts
/// nothing.
/// </param>
/// <param name="PetClass">
/// The kind of pet a pet device summons, when the server says; zero when the
/// item is not one or the server keeps it to itself, as the usual servers do.
/// <see cref="PluginInventoryItem.IsPetDevice"/> does not depend on it alone.
/// </param>
/// <param name="SummoningMastery">
/// The summoning mastery the item belongs to; zero when it has none.
/// </param>
/// <param name="ProcSpellId">
/// The spell the item can cast by itself when it strikes; zero when it has
/// none.
/// </param>
/// <param name="ProcSpellSelfTargeted">
/// True when that cast-on-strike spell lands on the wielder rather than the
/// one struck.
/// </param>
/// <param name="ProcSpellRate">
/// How often the cast-on-strike spell fires, as a share of hits from 0 to 1.
/// </param>
/// <param name="WeaponSkill">
/// The skill the weapon attacks with. Read from the appraised weapon profile
/// when there is one, otherwise from the property table.
/// </param>
/// <param name="DamageType">
/// The damage type the weapon deals, preferring the appraised weapon profile.
/// </param>
/// <param name="Damage">
/// The weapon's damage rating, preferring the appraised weapon profile. Minus
/// one means the server left the value unset, which is not the same as zero
/// damage.
/// </param>
/// <param name="DamageVariance">
/// How far below <paramref name="Damage"/> a hit can roll, as a fraction of
/// it: 0.2 means a hit lands between 80% and 100% of the damage rating.
/// </param>
/// <param name="UseRequiresSkill">
/// The skill needed to use the item; zero when none is.
/// </param>
/// <param name="UseRequiresSkillLevel">
/// How much of that skill the user needs.
/// </param>
/// <param name="UseRequiresSkillSpecialized">
/// How much of that skill the user needs when it is specialised.
/// </param>
public readonly record struct PluginInventoryItem(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    uint ItemType,
    uint ContainerObjectId,
    uint WielderObjectId,
    uint ValidLocations,
    uint EquippedLocation,
    uint Useability,
    uint TargetType,
    uint PublicFlags,
    int StackSize,
    int Structure,
    int MaximumStructure,
    uint SpellId,
    int PetClass,
    int SummoningMastery,
    uint ProcSpellId,
    bool ProcSpellSelfTargeted,
    double ProcSpellRate,
    int WeaponSkill,
    int DamageType,
    int Damage,
    double DamageVariance,
    int UseRequiresSkill,
    int UseRequiresSkillLevel,
    int UseRequiresSkillSpecialized)
{
    /// <summary>True when the item currently occupies an equipment slot.</summary>
    public bool IsEquipped => EquippedLocation != 0u;

    /// <summary>
    /// The shared cooldown every summoning essence belongs to in the game's
    /// item data: using any one of them starts it for all of them. Pass it to
    /// <see cref="ISpellCatalog.GetCooldownRemaining"/> to see how long until
    /// the next summon.
    /// </summary>
    public const uint SummoningCooldownId = 213u;

    /// <summary>
    /// True when the item summons a pet: it belongs to the summoning
    /// essences' shared cooldown (<see cref="SummoningCooldownId"/>), which
    /// the server sends with the item itself, or the server named the pet it
    /// summons (<see cref="PetClass"/>).
    /// </summary>
    public bool IsPetDevice =>
        SharedCooldownId == SummoningCooldownId || PetClass != 0;

    /// <summary>
    /// The shared cooldown the item belongs to: using it starts that
    /// cooldown for every item with the same id. The server sends it with the
    /// item and again in an appraisal; zero when the item has none.
    /// </summary>
    public uint SharedCooldownId { get; init; }

    /// <summary>
    /// How long, in seconds, the item's shared cooldown lasts once it is
    /// used; zero when the server sent no length.
    /// </summary>
    public double CooldownSeconds { get; init; }

    /// <summary>
    /// True when the item can cast a spell of its own on a hit, at some rate
    /// above zero.
    /// </summary>
    public bool HasCastOnStrike => ProcSpellId != 0u && ProcSpellRate > 0d;

    /// <summary>
    /// Which combat role the item fills (melee weapon, missile weapon,
    /// shield, ammunition); zero when it has none.
    /// </summary>
    public int CombatUse { get; init; }

    /// <summary>
    /// The item's spellcraft rating, which sets how hard its own spells are
    /// to activate; zero when it has none.
    /// </summary>
    public int ItemSpellcraft { get; init; }

    /// <summary>
    /// What kind of requirement must be met to wield the item -- a skill, an
    /// attribute, a level -- or zero when there is none.
    /// </summary>
    public int WieldRequirements { get; init; }

    /// <summary>The skill or attribute that requirement names.</summary>
    public int WieldSkillType { get; init; }

    /// <summary>How much of it the wielder needs.</summary>
    public int WieldDifficulty { get; init; }

    /// <summary>The attack styles the weapon offers, as a bit mask.</summary>
    public int AttackType { get; init; }

    /// <summary>The weapon category the item belongs to; zero when it is not a weapon.</summary>
    public int WeaponType { get; init; }
    /// <summary>The ammunition class a launcher takes, or an arrow or bolt belongs to; zero for anything else.</summary>
    public uint AmmoType { get; init; }

    /// <summary>
    /// Which vital the item restores when used (health, stamina, mana); zero
    /// when it restores none.
    /// </summary>
    public int BoosterVital { get; init; }

    /// <summary>How much of that vital one use restores.</summary>
    public int BoostValue { get; init; }

    /// <summary>
    /// A healing kit's effectiveness multiplier; zero for anything that is
    /// not one.
    /// </summary>
    public double HealKitModifier { get; init; }

    /// <summary>
    /// The spells an appraisal reported on the item; empty until it has been
    /// appraised.
    /// </summary>
    public IReadOnlyList<uint> AppraisedSpellIds { get; init; } =
        Array.Empty<uint>();

    /// <summary>The item's damage rating bonus; zero when it carries none.</summary>
    public int GearDamage { get; init; }

    /// <summary>The item's damage-resistance rating bonus; zero when it carries none.</summary>
    public int GearDamageResistance { get; init; }

    /// <summary>The item's critical-chance rating bonus; zero when it carries none.</summary>
    public int GearCriticalChance { get; init; }

    /// <summary>The item's critical-resistance rating bonus; zero when it carries none.</summary>
    public int GearCriticalResistance { get; init; }

    /// <summary>The item's critical-damage rating bonus; zero when it carries none.</summary>
    public int GearCriticalDamage { get; init; }

    /// <summary>
    /// The item's critical-damage-resistance rating bonus; zero when it
    /// carries none.
    /// </summary>
    public int GearCriticalDamageResistance { get; init; }

    /// <summary>
    /// The largest stack the item can form; one for anything that does not
    /// stack.
    /// </summary>
    public int MaximumStackSize { get; init; } = 1;

    /// <summary>
    /// The item's slot number inside its container, or -1 when the client
    /// does not know it.
    /// </summary>
    public int ContainerSlot { get; init; } = -1;

    /// <summary>How many loose items the item can hold, when it is a container.</summary>
    public int ItemsCapacity { get; init; }

    /// <summary>How many packs the item can hold, when it is a container.</summary>
    public int ContainersCapacity { get; init; }

    /// <summary>How much the item weighs against the character's burden limit.</summary>
    public int Burden { get; init; }

    /// <summary>The item's worth in coin, for the whole stack.</summary>
    public int Value { get; init; }

    /// <summary>How much mana the item currently holds.</summary>
    public int ItemCurrentMana { get; init; }

    /// <summary>The most mana the item can hold.</summary>
    public int ItemMaximumMana { get; init; }

    /// <summary>
    /// The item's workmanship as the server sends it with the object itself:
    /// a fractional number from 1 to 10, not the whole-number band an
    /// appraisal shows a player. A bag of salvage carries the average
    /// workmanship of everything melted into it here. Zero when the server
    /// sent none.
    /// </summary>
    public float Workmanship { get; init; }

    /// <summary>
    /// The same value as <see cref="Workmanship"/>, as a double. Nothing is
    /// recovered by widening it -- the server sent a single -- so this is
    /// only a convenience for a crafting calculator that works in doubles.
    /// </summary>
    public double SalvageWorkmanship { get; init; }

    /// <summary>How many times the item has been tinkered; zero when never or unknown.</summary>
    public int NumTimesTinkered { get; init; }

    /// <summary>
    /// The imbue burned into the item: the rends that change which element it
    /// strikes with, plus the critical bonuses. Zero when the item carries no
    /// imbue. Any non-zero value means the item can no longer be imbued
    /// again.
    /// </summary>
    public int ImbuedEffect { get; init; }

    /// <summary>
    /// The item's flat armor value; zero for anything that offers no
    /// protection. Read from the item's own property table, so it is
    /// available without an appraisal.
    /// </summary>
    public int ArmorLevel { get; init; }

    /// <summary>
    /// The top end of the item's damage roll, zero when the client does not
    /// know it. This is <see cref="Damage"/> with the server's "unset"
    /// sentinel folded to zero, so it is always a number a calculation can
    /// use.
    /// </summary>
    public int MaxDamage { get; init; }

    /// <summary>
    /// The damage type recorded in the item's own property table, which is
    /// where a casting weapon's element lives. <see cref="DamageType"/>
    /// prefers the appraised weapon profile and so reports what the weapon
    /// strikes with in melee; for a wand or staff that profile says nothing
    /// about the element it casts. Zero when the property is absent.
    /// </summary>
    public int WandElementalDamageType { get; init; }

    /// <summary>
    /// True when the item is marked as retained, which stops it being
    /// dropped, sold, or salvaged by accident. False when the item carries no
    /// such mark.
    /// </summary>
    public bool Retained { get; init; }

    /// <summary>What the item is made of; zero when the client does not know.</summary>
    public uint MaterialType { get; init; }

    /// <summary>The broad category the item falls into.</summary>
    public PluginObjectClass ObjectClass { get; init; }

    /// <summary>
    /// The color swaps applied to the item's appearance; empty when the
    /// client has no appearance data for it.
    /// </summary>
    public IReadOnlyList<PluginPaletteInfo> Palettes { get; init; } =
        Array.Empty<PluginPaletteInfo>();

    /// <summary>The item's icon, for a plugin that draws its own UI.</summary>
    public uint IconId { get; init; }

    /// <summary>
    /// The icon drawn beneath the item's icon, as a full icon id (for example
    /// the backdrop the server gives a rare item); zero when the item has
    /// none. Loot rules written for the original client's macro tools match
    /// on this value without its <c>0x06000000</c> prefix.
    /// </summary>
    public uint IconUnderlayId { get; init; }

    /// <summary>
    /// The icon drawn over the item's icon, as a full icon id (for example
    /// the mark an imbue adds); zero when the item has none.
    /// </summary>
    public uint IconOverlayId { get; init; }

    /// <summary>
    /// The body parts a piece of clothing or armor covers, as the coverage
    /// bits the server sends with the object; zero for anything that is not
    /// worn.
    /// </summary>
    public uint CoverageMask { get; init; }

    /// <summary>
    /// The item's name for more than one of it; an empty string when the
    /// server did not send one.
    /// </summary>
    public string PluralName { get; init; } = string.Empty;

    /// <summary>
    /// How close, in metres, the character must be to use the item; zero
    /// when the server did not send a distance.
    /// </summary>
    public float UseRadius { get; init; }

    /// <summary>
    /// The raw description words and optional values of the item's latest
    /// full description, as the server sent them; null for an item the
    /// client never received a description of, and on a host that does not
    /// report them. See <see cref="PluginObjectHeader"/>.
    /// </summary>
    public PluginObjectHeader? Header { get; init; }

    /// <summary>
    /// The icon-highlight effect bits the server sends with the object
    /// itself. Bit 0 is "magical", which is how a loot rule can tell that an
    /// item is expected to carry spells before anything has appraised it.
    /// </summary>
    public uint Effects { get; init; }
}

/// <summary>
/// A weapon's real damage/offense numbers as the server's appraisal response
/// reported them, straight from the WeaponProfile blob -- not the
/// PropertyInt/PropertyFloat table, which the server does not populate for
/// most weapons. Null until the item has been successfully appraised, or if
/// it never carries a WeaponProfile blob (i.e. it is not a weapon).
/// </summary>
/// <param name="DamageType">The damage type the weapon deals.</param>
/// <param name="WeaponTime">
/// The weapon's speed rating -- a rating, not a duration; higher is slower.
/// </param>
/// <param name="WeaponSkill">The skill the weapon attacks with.</param>
/// <param name="Damage">
/// The weapon's damage rating. Minus one means the server left it unset.
/// </param>
/// <param name="DamageVariance">
/// How far below <paramref name="Damage"/> a hit can roll, as a fraction of
/// it: 0.2 means a hit lands between 80% and 100% of the damage rating.
/// </param>
/// <param name="DamageMod">
/// A multiplier on damage centred on 1.0: 1.05 means five percent more.
/// </param>
/// <param name="WeaponLength">The weapon's reach.</param>
/// <param name="MaxVelocity">The launch speed a missile weapon gives its ammunition.</param>
/// <param name="WeaponOffense">
/// A multiplier on attack skill centred on 1.0: 0.9 means ten percent less.
/// </param>
/// <param name="MaxVelocityEstimated">
/// Non-zero when <paramref name="MaxVelocity"/> is an estimate the server
/// worked out at a nominal strength rather than the wielder's own.
/// </param>
public readonly record struct PluginWeaponProfile(
    int DamageType,
    int WeaponTime,
    uint WeaponSkill,
    int Damage,
    double DamageVariance,
    double DamageMod,
    double WeaponLength,
    double MaxVelocity,
    double WeaponOffense,
    int MaxVelocityEstimated);

/// <summary>
/// A piece of armor's per-damage-type protection modifiers as the server's
/// appraisal response reported them, from the ArmorProfile blob. Null until
/// the item has been successfully appraised, or if it never carries an
/// ArmorProfile blob (i.e. it is not armor). Fields after ArmorLevel are in
/// the blob's own wire order and stay float to match it exactly.
/// </summary>
/// <param name="ArmorLevel">
/// The flat armor value, which comes from the item's own property table
/// rather than the profile blob.
/// </param>
/// <param name="SlashMod">
/// Multiplier on incoming slashing damage: 1.2 means the wearer takes twenty
/// percent more of it, 0.8 twenty percent less.
/// </param>
/// <param name="PierceMod">Multiplier on incoming piercing damage.</param>
/// <param name="BludgeonMod">Multiplier on incoming bludgeoning damage.</param>
/// <param name="ColdMod">Multiplier on incoming cold damage.</param>
/// <param name="FireMod">Multiplier on incoming fire damage.</param>
/// <param name="AcidMod">Multiplier on incoming acid damage.</param>
/// <param name="NetherMod">Multiplier on incoming nether damage.</param>
/// <param name="ElectricMod">Multiplier on incoming lightning damage.</param>
public readonly record struct PluginArmorProfile(
    int ArmorLevel,
    float SlashMod,
    float PierceMod,
    float BludgeonMod,
    float ColdMod,
    float FireMod,
    float AcidMod,
    float NetherMod,
    float ElectricMod);

/// <summary>
/// Every property the client currently holds for one object, as copies of its
/// raw property tables keyed by property id, plus the two typed appraisal
/// blobs. A table is empty when the object carries no properties of that
/// kind.
/// </summary>
/// <param name="Ints">Integer properties by property id.</param>
/// <param name="Int64s">64-bit integer properties by property id.</param>
/// <param name="Bools">Boolean properties by property id.</param>
/// <param name="Floats">Floating-point properties by property id.</param>
/// <param name="Strings">Text properties by property id.</param>
/// <param name="DataIds">Data-file references by property id.</param>
/// <param name="InstanceIds">References to other objects by property id.</param>
public readonly record struct PluginItemProperties(
    IReadOnlyDictionary<uint, int> Ints,
    IReadOnlyDictionary<uint, long> Int64s,
    IReadOnlyDictionary<uint, bool> Bools,
    IReadOnlyDictionary<uint, double> Floats,
    IReadOnlyDictionary<uint, string> Strings,
    IReadOnlyDictionary<uint, uint> DataIds,
    IReadOnlyDictionary<uint, uint> InstanceIds)
{
    /// <summary>
    /// The weapon numbers from the object's last appraisal, or null when it
    /// has never been appraised or is not a weapon.
    /// </summary>
    public PluginWeaponProfile? WeaponProfile { get; init; }

    /// <summary>
    /// The armor numbers from the object's last appraisal, or null when it
    /// has never been appraised or is not armor.
    /// </summary>
    public PluginArmorProfile? ArmorProfile { get; init; }
}

/// <summary>One server <c>UseDone</c> for a plugin-issued item action.</summary>
/// <param name="Revision">
/// Counts up by one for every completion, so a plugin can tell a fresh one
/// from one it has already seen. Zero means nothing has completed yet.
/// </param>
/// <param name="SourceObjectId">The item that was used.</param>
/// <param name="TargetObjectId">
/// The object it was used on, or zero when the use had no target.
/// </param>
/// <param name="WeenieError">
/// The server's error code; zero means the use went through.
/// </param>
public readonly record struct PluginItemUseCompletion(
    long Revision,
    uint SourceObjectId,
    uint TargetObjectId,
    uint WeenieError)
{
    /// <summary>
    /// True when a use has actually completed and the server reported no
    /// error for it.
    /// </summary>
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}

/// <summary>How the client answered a plugin's item command.</summary>
public enum PluginItemCommandStatus
{
    /// <summary>
    /// The command could not be run: there is no in-world session, or this
    /// host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The item is unknown to the client, or is not one the command may act
    /// on -- usually because the player does not own it.
    /// </summary>
    InvalidItem,

    /// <summary>
    /// The target of the command is unknown or unsuitable: a container that
    /// is not the player's, a missing target object, a salvage tool that is
    /// not a tool, or no open vendor to sell to.
    /// </summary>
    InvalidTarget,

    /// <summary>
    /// Another inventory request is already in flight. The client sends one
    /// at a time; try again once it has finished.
    /// </summary>
    Busy,

    /// <summary>
    /// The request was sent. The server's own answer arrives later, through
    /// the completion properties or the object events.
    /// </summary>
    Started,

    /// <summary>
    /// The client would not send the request; <c>Notice</c> says why when
    /// there is something to say.
    /// </summary>
    Refused,

    /// <summary>
    /// The command was carried out by the client alone and has already taken
    /// effect; nothing was sent to the server.
    /// </summary>
    Completed,
}

/// <summary>The outcome of one item command, with an optional explanation.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginItemCommandResult(
    PluginItemCommandStatus Status,
    string? Notice = null)
{
    /// <summary>True when the request actually went out to the server.</summary>
    public bool Accepted => Status == PluginItemCommandStatus.Started;
}

/// <summary>Which kind of inventory request a completion belongs to.</summary>
public enum PluginInventoryCommandKind
{
    /// <summary>The client could not attribute the completion to a request it made.</summary>
    Unknown = 0,

    /// <summary>Picking an item up off the ground or out of an open container.</summary>
    Pickup,

    /// <summary>Moving a whole item into a container.</summary>
    PutInContainer,

    /// <summary>Splitting part of a stack off into a container.</summary>
    SplitToContainer,

    /// <summary>Merging one stack into another.</summary>
    Merge,

    /// <summary>Repositioning an item the player already holds.</summary>
    Move,

    /// <summary>Dropping a whole item on the ground.</summary>
    DropToWorld,

    /// <summary>Splitting part of a stack off onto the ground.</summary>
    SplitToWorld,

    /// <summary>Wearing or wielding an item.</summary>
    Wield,

    /// <summary>Handing an item to someone else.</summary>
    Give,
}

/// <summary>The server's answer to one inventory request.</summary>
/// <param name="Revision">
/// Counts up by one for every completion, so a plugin can tell a fresh one
/// from one it has already seen. Zero means nothing has completed yet.
/// </param>
/// <param name="Kind">Which kind of request this answers.</param>
/// <param name="SourceObjectId">The item the request acted on.</param>
/// <param name="WeenieError">
/// The server's error code; zero means the request went through.
/// </param>
public readonly record struct PluginInventoryCompletion(
    long Revision,
    PluginInventoryCommandKind Kind,
    uint SourceObjectId,
    uint WeenieError)
{
    /// <summary>
    /// True when a request has actually completed and the server reported no
    /// error for it.
    /// </summary>
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}

/// <summary>
/// Reads and acts on the items the player owns: using them, moving them
/// between packs, splitting and merging stacks, dropping, giving, salvaging,
/// and selling. Every command here only asks the server; the result lands
/// later, through the completion properties or the object events.
/// </summary>
/// <remarks>
/// These commands are not safe to call from another thread. Issue them from
/// the same thread the host raises its tick on, as they touch the same
/// inventory and movement state the client itself does.
/// </remarks>
public interface IItemAutomation
{
    /// <summary>
    /// True when this surface can be used: the session is in the world and
    /// the host wired up item commands.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// True while a use or an inventory request offered right now would come
    /// back <see cref="PluginItemCommandStatus.Busy"/>. Two things put it
    /// there: a request of your own already in flight, and the short pacing
    /// the client keeps between one use and the next. Both mean "not yet"
    /// rather than "no", so a command refused this way has not failed and
    /// should not count against whatever attempt limit or back-off you pair
    /// with a failure -- wait for this to read false and ask again. It never
    /// reads false while a command would be refused as busy; it may read true
    /// a moment longer than a move or a merge strictly needs, because those
    /// do not take the use pacing.
    /// </summary>
    bool IsBusy => false;

    /// <summary>
    /// How many living creatures the player currently owns as pets; zero when
    /// none, or when the session is not in the world.
    /// </summary>
    int ActiveOwnedPetCount => 0;

    /// <summary>
    /// The vendor whose shop is open, or zero when none is. This is what
    /// <see cref="Sell"/> sells to.
    /// </summary>
    uint ActiveVendorObjectId => 0u;

    /// <summary>
    /// The server's answer to the last item use. Default until something
    /// completes.
    /// </summary>
    PluginItemUseCompletion LastCompletion => default;

    /// <summary>
    /// The server's answer to the last inventory request -- a move, split,
    /// merge, drop, or give. Default until something completes.
    /// </summary>
    PluginInventoryCompletion LastInventoryCompletion => default;

    /// <summary>
    /// Lists everything the player owns, including what is inside their packs
    /// and what they have equipped, ordered by name. Returns an empty list
    /// when the session is not in the world.
    /// </summary>
    IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() =>
        Array.Empty<PluginInventoryItem>();

    /// <summary>
    /// Reads the property tables the client holds for one owned item,
    /// including its weapon and armor profiles once it has been appraised.
    /// Returns false for anything the player does not own -- use the object
    /// surface for that.
    /// </summary>
    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    /// <summary>
    /// Uses an object. An owned item is used where it lies. Anything else --
    /// a vendor, a corpse, a chest, a character to talk to -- is approached
    /// first if it is out of reach, exactly as double-clicking it does; in
    /// that case <see cref="PluginItemCommandStatus.Started"/> means the walk
    /// began, not that the use has happened. An owned item that needs a
    /// target of its own is refused with a notice saying to call
    /// <see cref="Apply"/> instead.
    /// </summary>
    PluginItemCommandResult Use(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Uses one owned item on another object -- a lockpick on a chest, a
    /// tinkering tool on a weapon. The first object must be one the player
    /// owns; the second only has to be one the client knows about.
    /// </summary>
    PluginItemCommandResult Apply(uint objectId, uint targetObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to move an owned item into one of the player's
    /// containers, or into the player themselves for the main pack. Pass an
    /// <paramref name="amount"/> of zero to move the whole stack, or a
    /// smaller number to split that many off into the container instead;
    /// <paramref name="placement"/> is the slot to drop it into. An amount
    /// larger than the stack is refused with a notice.
    /// </summary>
    PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount = 0u,
        int placement = 0) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// The same move, able to join a stack the way dropping a stack onto a
    /// pack does. With <paramref name="joinStack"/> true the client first
    /// looks for a stack of the same thing in the container -- its own items
    /// first, then those in each pack inside it -- that has room for
    /// everything being moved; the first one found is joined instead, with
    /// no slot needed, and the request is a merge. A stack that could take
    /// only part of it is passed over. When no stack qualifies, or with
    /// <paramref name="joinStack"/> false, this is exactly
    /// <see cref="MoveToContainer(uint, uint, uint, int)"/>.
    /// </summary>
    /// <remarks>
    /// A host that cannot join stacks passes a move with
    /// <paramref name="joinStack"/> false on to the plain move, and answers
    /// one with <paramref name="joinStack"/> true
    /// <see cref="PluginItemCommandStatus.Unavailable"/> without moving
    /// anything.
    /// </remarks>
    /// <param name="objectId">The owned item to move.</param>
    /// <param name="containerObjectId">
    /// The container to move it into, or the player for the main pack.
    /// </param>
    /// <param name="amount">
    /// Zero for the whole stack, or how many to split off and move.
    /// </param>
    /// <param name="placement">The slot to put it in when it takes one.</param>
    /// <param name="joinStack">Whether to join a stack already in the container.</param>
    PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount,
        int placement,
        bool joinStack) =>
        joinStack
            ? new(PluginItemCommandStatus.Unavailable)
            : MoveToContainer(objectId, containerObjectId, amount, placement);

    /// <summary>
    /// Asks the server to pour one owned stack into another owned stack of
    /// the same thing. Pass an <paramref name="amount"/> of zero to move the
    /// whole source stack; an amount larger than it is refused with a notice.
    /// </summary>
    PluginItemCommandResult Merge(
        uint sourceObjectId,
        uint targetObjectId,
        uint amount = 0u) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>Drop all or an exact partial stack on the ground.</summary>
    PluginItemCommandResult Drop(uint objectId, uint amount = 0u) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>Give all or an exact partial stack to a world target.</summary>
    PluginItemCommandResult Give(
        uint objectId,
        uint targetObjectId,
        uint amount = 0u) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Asks the server to break a set of owned items down with an owned
    /// salvaging tool. Reports
    /// <see cref="PluginItemCommandStatus.InvalidTarget"/> when the tool is
    /// not one the player owns or is not a salvaging tool, and
    /// <see cref="PluginItemCommandStatus.InvalidItem"/> for an empty list or
    /// any item the player does not own.
    /// </summary>
    PluginItemCommandResult Salvage(
        uint toolObjectId,
        IReadOnlyList<uint> itemObjectIds) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Asks the open vendor to buy an owned item, all of it or an exact
    /// partial stack. Reports
    /// <see cref="PluginItemCommandStatus.InvalidTarget"/> when no vendor is
    /// open, and <see cref="PluginItemCommandStatus.Refused"/> with a notice
    /// when this particular vendor will not take the item -- wrong kind of
    /// goods, worth too little or too much, or an item that cannot be sold.
    /// </summary>
    PluginItemCommandResult Sell(uint objectId, uint amount = 0u) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <summary>
    /// Lets go of an item the client still lists in the player's packs but
    /// the server no longer has. The item leaves the client exactly as it
    /// would if the server had deleted it: out of its pack, out of every
    /// inventory list and window, and <see cref="IEvents.ObjectChanged"/>
    /// reports it released. Nothing is sent to the server, and the server is
    /// not asked whether the item exists; if it does after all, it comes back
    /// the next time the server describes the inventory, at the latest on the
    /// next login.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only an item the server has just refused to appraise is let go of:
    /// <see cref="PluginWorldObject.LastAppraisalUnsuccessful"/> must be true
    /// for it -- so the server has said nothing else about the item since --
    /// and that refusal must have arrived within the last 30 seconds of game
    /// time. The server also refuses an item made to resist appraisal and a
    /// repeat request sent within about five seconds of an unsuccessful one,
    /// so a careful caller asks again after that pause and drops only an item
    /// refused twice, straight after the second refusal.
    /// </para>
    /// <para>
    /// Reports <see cref="PluginItemCommandStatus.Completed"/> when the item is
    /// gone from the client. Reports
    /// <see cref="PluginItemCommandStatus.InvalidItem"/> for an unknown id, the
    /// character itself, anything the player does not carry, and an item the
    /// client has no server record of to let go of.
    /// Reports <see cref="PluginItemCommandStatus.Refused"/> with a notice for
    /// an item that is worn or wielded, a pack that still holds anything, an
    /// item the server has not refused to appraise, and one whose refusal is
    /// more than 30 seconds old. Reports
    /// <see cref="PluginItemCommandStatus.Busy"/> while an appraisal of this
    /// very item is still awaited -- its answer, not the earlier one, is what
    /// counts -- and whenever <see cref="IsBusy"/> reads true, since an item
    /// request in flight may be about this item too. <see cref="IsBusy"/> does
    /// not cover an appraisal in flight, so a caller that has just asked about
    /// the item waits for the answer before letting go of it.
    /// </para>
    /// </remarks>
    PluginItemCommandResult ForgetStaleItem(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
}
