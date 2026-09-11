using System;
using System.Collections.Generic;

namespace AcDream.Core.Items;


[Flags]
public enum ItemType : uint
{
    None                    = 0,
    MeleeWeapon             = 0x00000001,
    Armor                   = 0x00000002,
    Clothing                = 0x00000004,
    Jewelry                 = 0x00000008,
    Creature                = 0x00000010,
    Food                    = 0x00000020,
    Money                   = 0x00000040,
    Misc                    = 0x00000080,
    MissileWeapon           = 0x00000100,
    Container               = 0x00000200,
    Useless                 = 0x00000400,
    Gem                     = 0x00000800,
    SpellComponents         = 0x00001000,
    Writable                = 0x00002000,
    Key                     = 0x00004000,
    Caster                  = 0x00008000,
    Portal                  = 0x00010000,
    Lockable                = 0x00020000,
    PromissoryNote          = 0x00040000,
    ManaStone               = 0x00080000,
    Service                 = 0x00100000,
    MagicWieldable          = 0x00200000,
    CraftCookingBase        = 0x00400000,
    CraftAlchemyBase        = 0x00800000,
    CraftFletchingBase      = 0x01000000,
    CraftAlchemyIntermediate= 0x04000000,
    CraftFletchingIntermediate = 0x08000000,
    LifeStone               = 0x10000000,
    TinkeringTool           = 0x20000000,
    TinkeringMaterial       = 0x40000000,
    Gameboard               = 0x80000000u,

    Vestements              = 0x00000006,   // TYPE_VESTEMENTS
    Weapon                  = 0x00000101,
    WeaponOrCaster          = 0x00008101,   // TYPE_WEAPON_OR_CASTER
    LockableMagicTarget     = 0x00000280,   // TYPE_LOCKABLE_MAGIC_TARGET
    RedirectableItemEnchantmentTarget = 0x00008107,
    PortalMagicTarget       = 0x10010000,   // TYPE_PORTAL_MAGIC_TARGET
    ItemEnchantableTarget   = 0x00088B8F,   // TYPE_ITEM_ENCHANTABLE_TARGET
    Item                    = 0x002DFBEF,   // TYPE_ITEM
    VendorShopkeep          = 0x480467A7,   // TYPE_VENDOR_SHOPKEEP
    VendorGrocer            = 0x00446220,   // TYPE_VENDOR_GROCER
}

[Flags]
public enum EquipMask : uint
{
    None           = 0x00000000,
    HeadWear       = 0x00000001,
    ChestWear      = 0x00000002,
    AbdomenWear    = 0x00000004,
    UpperArmWear   = 0x00000008,
    LowerArmWear   = 0x00000010,
    HandWear       = 0x00000020,
    UpperLegWear   = 0x00000040,
    LowerLegWear   = 0x00000080,
    FootWear       = 0x00000100,
    ChestArmor     = 0x00000200,
    AbdomenArmor   = 0x00000400,
    UpperArmArmor  = 0x00000800,
    LowerArmArmor  = 0x00001000,
    UpperLegArmor  = 0x00002000,
    LowerLegArmor  = 0x00004000,
    NeckWear       = 0x00008000,
    WristWearLeft  = 0x00010000,
    WristWearRight = 0x00020000,
    FingerWearLeft = 0x00040000,
    FingerWearRight = 0x00080000,
    MeleeWeapon    = 0x00100000,
    Shield         = 0x00200000,
    MissileWeapon  = 0x00400000,
    MissileAmmo    = 0x00800000,
    Held           = 0x01000000,
    TwoHanded      = 0x02000000,
    TrinketOne     = 0x04000000,
    Cloak          = 0x08000000,
    SigilOne       = 0x10000000,
    SigilTwo       = 0x20000000,
    SigilThree     = 0x40000000,

    Clothing            = 0x080001FF,
    Armor               = 0x00007E00,   // ARMOR_LOC
    Jewelry             = 0x7C0F8000,   // JEWELRY_LOC
    WristWear           = 0x00030000,   // WRIST_WEAR_LOC
    FingerWear          = 0x000C0000,   // FINGER_WEAR_LOC
    Sigil               = 0x70000000,   // SIGIL_LOC
    ReadySlot           = 0x03F00000,   // READY_SLOT_LOC
    Weapon              = 0x02500000,   // WEAPON_LOC
    WeaponReadySlot     = 0x03500000,   // WEAPON_READY_SLOT_LOC
    All                 = 0x7FFFFFFF,   // ALL_LOC
    CanGoInReadySlot    = 0x7FFFFFFF,
}

public sealed class PropertyBundle
{
    public Dictionary<uint, int>    Ints      { get; } = new();
    public Dictionary<uint, long>   Int64s    { get; } = new();
    public Dictionary<uint, bool>   Bools     { get; } = new();
    public Dictionary<uint, double> Floats    { get; } = new();
    public Dictionary<uint, string> Strings   { get; } = new();
    public Dictionary<uint, uint>   DataIds   { get; } = new();
    public Dictionary<uint, uint>   InstanceIds { get; } = new();

    public int    GetInt   (uint k, int    def = 0)       => Ints.TryGetValue(k, out var v) ? v : def;
    public long   GetInt64 (uint k, long   def = 0)       => Int64s.TryGetValue(k, out var v) ? v : def;
    public bool   GetBool  (uint k, bool   def = false)   => Bools.TryGetValue(k, out var v) ? v : def;
    public double GetFloat (uint k, double def = 0)       => Floats.TryGetValue(k, out var v) ? v : def;
    public string GetString(uint k, string def = "")      => Strings.TryGetValue(k, out var v) ? v : def;

    public PropertyBundle Clone()
    {
        var copy = new PropertyBundle();
        foreach (var kv in Ints)        copy.Ints[kv.Key]        = kv.Value;
        foreach (var kv in Int64s)      copy.Int64s[kv.Key]      = kv.Value;
        foreach (var kv in Bools)       copy.Bools[kv.Key]       = kv.Value;
        foreach (var kv in Floats)      copy.Floats[kv.Key]      = kv.Value;
        foreach (var kv in Strings)     copy.Strings[kv.Key]     = kv.Value;
        foreach (var kv in DataIds)     copy.DataIds[kv.Key]     = kv.Value;
        foreach (var kv in InstanceIds) copy.InstanceIds[kv.Key] = kv.Value;
        return copy;
    }
}

public sealed class ClientObject
{
    private uint? _houseOwnerId;
    private uint? _monarchId;
    private HouseRestrictionRecord? _restrictions;

    internal event Action<ClientObject>? RestrictionAuthorityChanged;

    public uint ObjectId      { get; init; }
    public uint WeenieClassId { get; set; }    // "blueprint"
    public string Name         { get; set; } = "";
    public string PluralName   { get; set; } = "";
    public ItemType Type      { get; set; }
    public EquipMask ValidLocations { get; set; }
    public EquipMask CurrentlyEquippedLocation { get; set; }
    public uint IconId        { get; set; }    // 0x06xxxxxx
    public uint IconUnderlayId{ get; set; }    // "magic" underlay
    public uint IconOverlayId { get; set; }    // "enchanted" overlay
    public uint Effects       { get; set; }
    public int  StackSize     { get; set; } = 1;
    public int  StackSizeMax  { get; set; } = 1;
    public int  Burden        { get; set; }    // per-stack total
    public int  Value         { get; set; }    // pyreals
    public uint ContainerId   { get; set; }
    public int  ContainerSlot { get; set; } = -1;
    public uint ContainerTypeHint { get; set; }
    public bool Attuned       { get; set; }
    public bool Bonded        { get; set; }
    public uint WielderId    { get; set; }    // PropertyInstanceId.Wielder; 0 = not wielded
    public int  ItemsCapacity     { get; set; }
    public int  ContainersCapacity{ get; set; }
    public uint HookItemTypes { get; set; }
    public uint HookType { get; set; }
    public bool IsHook => HookType != 0u && HookItemTypes != 0u;
    public uint Priority     { get; set; }
    public uint? Useability  { get; set; }    // ITEM_USEABLE from PublicWeenieDesc
    public uint? TargetType  { get; set; }
    public uint? PublicWeenieBitfield { get; set; }
    public uint PetOwnerId { get; set; }
    public byte? CombatUse { get; set; }
    public ushort? AmmoType { get; set; }
    public uint? SpellId { get; set; }
    public IReadOnlyList<uint> AppraisedSpellIds { get; internal set; } =
        Array.Empty<uint>();
    public int LastAppraisalTimeMs { get; internal set; }
    public uint? CooldownId { get; set; }
    public double? CooldownDuration { get; set; }
    public int TradeState { get; set; }
    /// <summary>Non-zero while the item sits in a vendor sell list.</summary>
    public int SellState { get; set; }
    public bool IsComponentPack { get; set; }
    public byte? RadarBlipColor { get; set; }
    public byte? RadarBehavior { get; set; }
    public int  Structure    { get; set; }
    public int  MaxStructure { get; set; }
    public float Workmanship { get; set; }    // 0..10 (fractional on the wire)
    public uint? MaterialType { get; set; }
    public uint? HouseOwnerId
    {
        get => _houseOwnerId;
        set
        {
            if (_houseOwnerId == value) return;
            _houseOwnerId = value;
            RestrictionAuthorityChanged?.Invoke(this);
        }
    }
    public uint? MonarchId
    {
        get => _monarchId;
        set
        {
            if (_monarchId == value) return;
            _monarchId = value;
            RestrictionAuthorityChanged?.Invoke(this);
        }
    }
    public HouseRestrictionRecord? Restrictions
    {
        get => _restrictions;
        set
        {
            if (ReferenceEquals(_restrictions, value)) return;
            _restrictions = value;
            RestrictionAuthorityChanged?.Invoke(this);
        }
    }
    public PropertyBundle Properties { get; } = new();

    public string GetAppropriateName()
    {
        if (StackSize <= 1) return Name;
        if (!string.IsNullOrEmpty(PluralName)) return PluralName;
        if (string.IsNullOrEmpty(Name)) return Name;
        return Name[^1] == 's' ? Name + "es" : Name + "s";
    }

    public string GetTooltipDisplayName()
    {
        string name = GetAppropriateName();
        return StackSize > 1 ? $"{StackSize} {name}" : name;
    }
}

public readonly record struct WeenieData(
    uint Guid,
    string? Name,
    ItemType? Type,
    uint WeenieClassId,
    uint IconId,
    uint IconOverlayId,
    uint IconUnderlayId,
    uint Effects,
    int? Value,
    int? StackSize,
    int? StackSizeMax,
    int? Burden,
    uint? ContainerId,
    uint? WielderId,
    uint? ValidLocations,
    uint? CurrentWieldedLocation,
    uint? Priority,
    int? ItemsCapacity,
    int? ContainersCapacity,
    int? Structure,
    int? MaxStructure,
    float? Workmanship,
    uint? Useability = null,
    uint? TargetType = null,
    byte? RadarBlipColor = null,
    byte? RadarBehavior = null,
    uint? PublicWeenieBitfield = null,
    byte? CombatUse = null,
    string? PluralName = null,
    uint? PetOwnerId = null,
    ushort? AmmoType = null,
    uint? SpellId = null,
    uint? CooldownId = null,
    double? CooldownDuration = null,
    uint? HookItemTypes = null,
    uint? HookType = null,
    uint? MaterialType = null,
    uint? HouseOwnerId = null,
    uint? MonarchId = null,
    HouseRestrictionRecord? Restrictions = null);

public static class PlayerKillerStatusBitfield
{
    public const int Pk = 0x04;
    public const int Free = 0x20;
    public const int PkLite = 0x40;

    public static uint Apply(uint bitfield, int pkStatus) => pkStatus switch
    {
        Pk     => (bitfield & 0xfddfffffu) | 0x20u,
        PkLite => (bitfield & 0xffdfffdfu) | 0x2000000u,
        Free   => (bitfield & 0xfdffffdfu) | 0x200000u,
        _      => bitfield & 0xfddfffdfu,
    };
}

public static class ItemUseability
{
    public const uint Undef     = 0x0u;
    public const uint No        = 0x1u;
    public const uint Self      = 0x2u;
    public const uint Wielded   = 0x4u;
    public const uint Contained = 0x8u;
    public const uint Viewed    = 0x10u;
    public const uint Remote    = 0x20u;
    public const uint NeverWalk = 0x40u;
    public const uint ObjSelf   = 0x80u;

    public const uint SourceMask = 0x0000FFFFu;
    public const uint TargetMask = 0xFFFF0000u;

    public static bool IsTargeted(uint useability)
        => (useability & TargetMask) != 0;

    public static bool AllowsSelfTarget(uint useability)
        => ((useability >> 16) & Self) != 0;

    public static bool AllowsObjectSelfTarget(uint useability)
        => ((useability >> 16) & ObjSelf) != 0;

    public static uint SourceFlags(uint useability)
        => useability & SourceMask;

    public static uint LeastLimitedSourceUse(uint useability)
    {
        uint s = SourceFlags(useability);
        if ((s & Remote) != 0) return Remote;
        if ((s & Viewed) != 0) return Viewed;
        if ((s & Contained) != 0) return Contained;
        if ((s & Wielded) != 0) return Wielded;
        if ((s & Self) != 0) return Self;
        return Undef;
    }

    public static uint TargetFlags(uint useability)
        => (useability & TargetMask) >> 16;

    public static bool IsUseable(uint useability)
        => (useability & No) == 0;

    public static uint LeastLimitedTargetUse(uint useability)
    {
        uint t = TargetFlags(useability);
        if ((t & Remote) != 0) return Remote;
        if ((t & Viewed) != 0) return Viewed;
        if ((t & Contained) != 0) return Contained;
        if ((t & Wielded) != 0) return Wielded;
        if ((t & Self) != 0) return Self;
        return t & ObjSelf;
    }

    public static bool IsDirectUseable(uint useability)
        => !IsTargeted(useability) && IsUseable(useability);
}

public sealed class Container
{
    public uint ObjectId        { get; init; }
    public int  Capacity        { get; set; } = 102;   // main inv default
    public int  SideCapacity    { get; set; } = 0;     // 0 for side-pack
    public int  BurdenLimit     { get; set; }
    public List<ClientObject> Items { get; } = new();
    public List<Container>    SidePacks { get; } = new();   // empty for side-pack
    public bool IsSidePack      => SideCapacity == 0;
}

public static class BurdenMath
{
    public const int BurdenPerStrength = 150;

    public const int AugBurdenPerRank = 30;
    public const int AugBurdenCap = 150;

    public static int EncumbranceCapacity(int strength, int aug)
    {
        if (strength <= 0) return 0;
        int bonus = aug * AugBurdenPerRank;
        if (bonus < 0) bonus = 0;
        if (bonus > AugBurdenCap) bonus = AugBurdenCap;
        return strength * BurdenPerStrength + bonus * strength;
    }

    public static float LoadRatio(int capacity, int burden)
        => capacity <= 0 ? 0f : (float)burden / capacity;

    public static float LoadModifier(float load)
        => load <= 1f ? 1f : load < 2f ? 2f - load : 0f;

    public static int LoadPenaltyPercent(float load)
        => (10 - (int)(LoadModifier(load) * 10f)) * 10;

    public static float LoadToFill(float load)
    {
        float fill = load / 3f;
        if (fill < 0f) return 0f;
        return fill > 1f ? 1f : fill;
    }

    public static int LoadToPercent(float load)
        => (int)System.MathF.Floor(LoadToFill(load) * 300f);

    public static int ComputeMax(int strength, int bonusBurden)
        => BurdenPerStrength * strength + strength * bonusBurden;

    public static int ComputeCarryLimit(int strength, int bonusBurden)
        => 3 * ComputeMax(strength, bonusBurden);

    public static float ComputeEncumbranceMod(int currentBurden, int maxBurden)
    {
        if (maxBurden <= 0) return 1f;
        float ratio = (float)currentBurden / maxBurden;
        // Roughly 1.0 until 50%, then linear decay to ~0.7 at 100%, 0.1 at 300%.
        if (ratio <= 0.5f) return 1f;
        if (ratio <= 1.0f) return 1f - (ratio - 0.5f) * 0.6f;        // 1.0 → 0.7
        if (ratio <= 3.0f) return 0.7f - (ratio - 1.0f) * 0.3f;      // 0.7 → 0.1
        return 0.1f;
    }
}
