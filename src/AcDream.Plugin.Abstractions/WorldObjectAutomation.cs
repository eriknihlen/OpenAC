namespace AcDream.Plugin.Abstractions;

public enum PluginObjectClass
{
    Unknown = 0,
    MeleeWeapon = 1,
    Armor = 2,
    Clothing = 3,
    Jewelry = 4,
    Monster = 5,
    Food = 6,
    Money = 7,
    Misc = 8,
    MissileWeapon = 9,
    Container = 10,
    Gem = 11,
    SpellComponent = 12,
    Key = 13,
    Portal = 14,
    TradeNote = 15,
    ManaStone = 16,
    Plant = 17,
    BaseCooking = 18,
    BaseAlchemy = 19,
    BaseFletching = 20,
    CraftedCooking = 21,
    CraftedAlchemy = 22,
    CraftedFletching = 23,
    Player = 24,
    Vendor = 25,
    Door = 26,
    Corpse = 27,
    Lifestone = 28,
    HealingKit = 29,
    Lockpick = 30,
    WandStaffOrb = 31,
    Bundle = 32,
    Book = 33,
    Journal = 34,
    Sign = 35,
    Housing = 36,
    Npc = 37,
    Foci = 38,
    Salvage = 39,
    Ust = 40,
    Services = 41,
    Scroll = 42,
    CombatPet = 43,
}

public readonly record struct PluginWorldObject(
    uint ObjectId,
    uint WeenieClassId,
    string Name,
    PluginObjectClass ObjectClass,
    uint ItemType,
    uint ContainerObjectId,
    uint WielderObjectId)
{
    public bool IsOwned { get; init; }
    public bool IsLandscape { get; init; }
    public bool HasPosition { get; init; }
    public PluginNavigationPosition Position { get; init; }
    public bool HasAppraisalData { get; init; }
    public int LastIdTime { get; init; }
    public bool IsDoorOpen { get; init; }
    public int StackSize { get; init; } = 1;
    public int ItemsCapacity { get; init; }
    public int ContainersCapacity { get; init; }
    public IReadOnlyList<uint> SpellIds { get; init; } = Array.Empty<uint>();
    public IReadOnlyList<uint> ActiveSpellIds { get; init; } = Array.Empty<uint>();
    public uint IconId { get; init; }
}

public interface IWorldObjectAutomation
{
    bool IsAvailable => false;
    uint OpenContainerObjectId => 0u;

    IReadOnlyList<PluginWorldObject> CaptureObjects() =>
        Array.Empty<PluginWorldObject>();

    bool TryGet(uint objectId, out PluginWorldObject value)
    {
        value = default;
        return false;
    }

    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);
}
