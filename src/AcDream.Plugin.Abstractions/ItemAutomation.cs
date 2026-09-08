namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginPaletteInfo(
    uint PaletteId,
    byte Offset,
    byte Length,
    byte Red,
    byte Green,
    byte Blue);

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
    public bool IsEquipped => EquippedLocation != 0u;
    public bool IsPetDevice => PetClass != 0;
    public bool HasCastOnStrike => ProcSpellId != 0u && ProcSpellRate > 0d;
    public int CombatUse { get; init; }
    public int ItemSpellcraft { get; init; }
    public int WieldRequirements { get; init; }
    public int WieldSkillType { get; init; }
    public int WieldDifficulty { get; init; }
    public int AttackType { get; init; }
    public int WeaponType { get; init; }
    public int BoosterVital { get; init; }
    public int BoostValue { get; init; }
    public double HealKitModifier { get; init; }
    public IReadOnlyList<uint> AppraisedSpellIds { get; init; } =
        Array.Empty<uint>();
    public int GearDamage { get; init; }
    public int GearDamageResistance { get; init; }
    public int GearCriticalChance { get; init; }
    public int GearCriticalResistance { get; init; }
    public int GearCriticalDamage { get; init; }
    public int GearCriticalDamageResistance { get; init; }
    public int MaximumStackSize { get; init; } = 1;
    public int ContainerSlot { get; init; } = -1;
    public int ItemsCapacity { get; init; }
    public int ContainersCapacity { get; init; }
    public int Burden { get; init; }
    public int Value { get; init; }
    public int ItemCurrentMana { get; init; }
    public int ItemMaximumMana { get; init; }
    public float Workmanship { get; init; }
    public uint MaterialType { get; init; }
    public PluginObjectClass ObjectClass { get; init; }
    public IReadOnlyList<PluginPaletteInfo> Palettes { get; init; } =
        Array.Empty<PluginPaletteInfo>();
    public uint IconId { get; init; }
}

public readonly record struct PluginItemProperties(
    IReadOnlyDictionary<uint, int> Ints,
    IReadOnlyDictionary<uint, long> Int64s,
    IReadOnlyDictionary<uint, bool> Bools,
    IReadOnlyDictionary<uint, double> Floats,
    IReadOnlyDictionary<uint, string> Strings,
    IReadOnlyDictionary<uint, uint> DataIds,
    IReadOnlyDictionary<uint, uint> InstanceIds);

/// <summary>One server <c>UseDone</c> for a plugin-issued item action.</summary>
public readonly record struct PluginItemUseCompletion(
    long Revision,
    uint SourceObjectId,
    uint TargetObjectId,
    uint WeenieError)
{
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}

public enum PluginItemCommandStatus
{
    Unavailable = 0,
    InvalidItem,
    InvalidTarget,
    Busy,
    Started,
    Refused,
}

public readonly record struct PluginItemCommandResult(
    PluginItemCommandStatus Status,
    string? Notice = null)
{
    public bool Accepted => Status == PluginItemCommandStatus.Started;
}

public enum PluginInventoryCommandKind
{
    Unknown = 0,
    Pickup,
    PutInContainer,
    SplitToContainer,
    Merge,
    Move,
    DropToWorld,
    SplitToWorld,
    Wield,
    Give,
}

public readonly record struct PluginInventoryCompletion(
    long Revision,
    PluginInventoryCommandKind Kind,
    uint SourceObjectId,
    uint WeenieError)
{
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}

public interface IItemAutomation
{
    bool IsAvailable => false;
    bool IsBusy => false;
    int ActiveOwnedPetCount => 0;
    uint ActiveVendorObjectId => 0u;
    PluginItemUseCompletion LastCompletion => default;
    PluginInventoryCompletion LastInventoryCompletion => default;

    IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() =>
        Array.Empty<PluginInventoryItem>();

    bool TryCaptureProperties(
        uint objectId,
        out PluginItemProperties properties)
    {
        properties = default;
        return false;
    }

    PluginItemCommandResult Use(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult Apply(uint objectId, uint targetObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult MoveToContainer(
        uint objectId,
        uint containerObjectId,
        uint amount = 0u,
        int placement = 0) =>
        new(PluginItemCommandStatus.Unavailable);

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

    PluginItemCommandResult Salvage(
        uint toolObjectId,
        IReadOnlyList<uint> itemObjectIds) =>
        new(PluginItemCommandStatus.Unavailable);

    PluginItemCommandResult Sell(uint objectId, uint amount = 0u) =>
        new(PluginItemCommandStatus.Unavailable);
}
