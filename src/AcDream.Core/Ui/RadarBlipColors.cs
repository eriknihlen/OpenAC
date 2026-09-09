using AcDream.Core.Items;

namespace AcDream.Core.Ui;

public readonly record struct RadarObjectTraits(
    bool IsValid = true,
    byte BlipColorOverride = 0,
    bool IsPortal = false,
    bool IsVendor = false,
    bool IsAttackable = false,
    bool IsCreature = false,
    bool IsPlayer = false,
    bool IsAdmin = false,
    bool IsHiddenAdmin = false,
    bool IsPlayerKiller = false,
    bool IsPkLite = false,
    bool IsFreePk = false)
{
    private const uint BfPlayer = 0x00000008u;
    private const uint BfAttackable = 0x00000010u;
    private const uint BfPlayerKiller = 0x00000020u;
    private const uint BfHiddenAdmin = 0x00000040u;
    private const uint BfVendor = 0x00000200u;
    private const uint BfPortal = 0x00040000u;
    private const uint BfAdmin = 0x00100000u;
    private const uint BfFreePk = 0x00200000u;
    private const uint BfPkLite = 0x02000000u;

    public static RadarObjectTraits FromPublicWeenieDescription(
        uint itemType,
        uint bitfield,
        byte blipColorOverride = 0)
        => new(
            IsValid: (bitfield & 0x80000000u) == 0,
            BlipColorOverride: blipColorOverride,
            IsPortal: (bitfield & BfPortal) != 0,
            IsVendor: (bitfield & BfVendor) != 0,
            IsAttackable: (bitfield & BfAttackable) != 0,
            IsCreature: (itemType & (uint)ItemType.Creature) != 0,
            IsPlayer: (bitfield & BfPlayer) != 0,
            IsAdmin: (bitfield & BfAdmin) != 0,
            IsHiddenAdmin: (bitfield & BfHiddenAdmin) != 0,
            IsPlayerKiller: (bitfield & BfPlayerKiller) != 0,
            IsPkLite: (bitfield & BfPkLite) != 0,
            IsFreePk: (bitfield & BfFreePk) != 0);
}

public readonly record struct RadarRelationshipTraits(
    bool IsFellowshipMember = false,
    bool IsFellowshipLeader = false,
    bool IsAllegianceMember = false,
    bool PlayerIsPlayerKiller = false,
    bool PlayerIsPkLite = false);

public static class RadarBlipColors
{
    public readonly record struct Rgba(float Red, float Green, float Blue, float Alpha)
    {
        public byte R => ToByte(Red);
        public byte G => ToByte(Green);
        public byte B => ToByte(Blue);
        public byte A => ToByte(Alpha);

        public Rgba DimRgb(float multiplier)
            => new(Red * multiplier, Green * multiplier, Blue * multiplier, Alpha);

        /// <summary>ImGui/OpenGL-style packed <c>0xAABBGGRR</c> projection.</summary>
        public uint ToAbgr32()
            => ((uint)A << 24) | ((uint)B << 16) | ((uint)G << 8) | R;

        private static byte ToByte(float value)
            => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    public static readonly Rgba Blue = new(0.25f, 0.660000026f, 1f, 1f);
    public static readonly Rgba Gold = new(1f, 0.670000017f, 0f, 1f);
    public static readonly Rgba Yellow = new(1f, 1f, 0.5f, 1f);
    public static readonly Rgba White = new(1f, 1f, 1f, 1f);
    public static readonly Rgba Red = new(1f, 0.25f, 0.389999986f, 1f);
    public static readonly Rgba Purple = new(0.75f, 0.389999986f, 1f, 1f);
    public static readonly Rgba Pink = new(1f, 0.660000026f, 0.75f, 1f);
    public static readonly Rgba Green = new(0f, 0.5f, 0.25f, 1f);
    public static readonly Rgba Cyan = new(0f, 1f, 1f, 1f);
    public static readonly Rgba BrightGreen = new(0f, 1f, 0f, 1f);

    public static readonly Rgba Default = White;
    public static readonly Rgba Item = White;
    public static readonly Rgba Admin = Cyan;
    public static readonly Rgba Advocate = Pink;
    public static readonly Rgba Creature = Gold;
    public static readonly Rgba LifeStone = Blue;
    public static readonly Rgba NPC = Yellow;
    public static readonly Rgba PlayerKiller = Red;
    public static readonly Rgba Portal = Purple;
    public static readonly Rgba Sentinel = Cyan;
    public static readonly Rgba Vendor = Yellow;
    public static readonly Rgba Fellowship = BrightGreen;
    public static readonly Rgba FellowshipLeader = BrightGreen;
    public static readonly Rgba PKLite = Pink;

    public static Rgba For(uint itemType, uint pwdBitfield)
        => For(RadarObjectTraits.FromPublicWeenieDescription(itemType, pwdBitfield));

    public static Rgba For(
        RadarObjectTraits traits,
        RadarRelationshipTraits relationship = default)
    {
        if (!traits.IsValid)
            return Default;

        if (traits.BlipColorOverride != 0)
            return ForOverride(traits.BlipColorOverride);

        if (traits.IsPortal)
            return Portal;

        if (traits.IsVendor)
            return Vendor;

        if (traits.IsAttackable && traits.IsCreature && !traits.IsPlayer)
            return Creature;

        if (!traits.IsPlayer)
            return Default;

        Rgba color = Default;
        if (traits.IsAdmin && !traits.IsHiddenAdmin)
            color = Admin;
        else if (traits.IsPlayerKiller)
            color = PlayerKiller;
        else if (traits.IsPkLite)
            color = PKLite;
        else if (traits.IsFreePk)
            color = Creature;

        if (relationship.IsFellowshipLeader)
            color = FellowshipLeader;
        else if (relationship.IsFellowshipMember)
            color = Fellowship;

        return color;
    }

    public static Rgba ForOverride(byte overrideValue)
        => overrideValue switch
        {
            1 => Blue,
            2 => Gold,
            3 => White,
            4 => Purple,
            5 => Red,
            6 => Pink,
            7 => Green,
            8 => Yellow,
            9 => Cyan,
            10 => BrightGreen,
            _ => Default,
        };
}
