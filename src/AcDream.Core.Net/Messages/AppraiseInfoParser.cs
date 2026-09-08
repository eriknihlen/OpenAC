using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public static class AppraiseInfoParser
{
    [Flags]
    public enum IdentifyResponseFlags : uint
    {
        None                     = 0x0000_0000,
        IntStatsTable            = 0x0000_0001,
        BoolStatsTable           = 0x0000_0002,
        FloatStatsTable          = 0x0000_0004,
        StringStatsTable         = 0x0000_0008,
        SpellBook                = 0x0000_0010,
        WeaponProfile            = 0x0000_0020,
        HookProfile              = 0x0000_0040,
        ArmorProfile             = 0x0000_0080,
        CreatureProfile          = 0x0000_0100,
        ArmorEnchantmentBitfield = 0x0000_0200,
        ResistEnchantmentBitfield= 0x0000_0400,
        WeaponEnchantmentBitfield= 0x0000_0800,
        DidStatsTable            = 0x0000_1000,
        Int64StatsTable          = 0x0000_2000,
        ArmorLevels              = 0x0000_4000,
    }

    /// <summary>Per-damage-type protections from an ArmorProfile blob.</summary>
    public readonly record struct ArmorProfile(
        float SlashingProtection,
        float PiercingProtection,
        float BludgeoningProtection,
        float ColdProtection,
        float FireProtection,
        float AcidProtection,
        float NetherProtection,
        float LightningProtection);

    /// <summary>Per-slot AL (armor level) from an ArmorLevels blob.</summary>
    public readonly record struct ArmorLevel(
        int Head, int Chest, int Abdomen,
        int UpperArm, int LowerArm, int Hand,
        int UpperLeg, int LowerLeg, int Foot);

    /// <summary>Melee/missile weapon statistics from a WeaponProfile blob.</summary>
    public readonly record struct WeaponProfile(
        uint DamageType,
        uint WeaponTime,
        uint WeaponSkill,
        uint Damage,
        double DamageVariance,
        double DamageMod,
        double WeaponLength,
        double MaxVelocity,
        double WeaponOffense,
        uint MaxVelocityEstimated);

    public readonly record struct HookProfile(
        uint Flags,
        uint ValidLocations,
        uint AmmoType);

    public readonly record struct CreatureProfile(
        uint Flags,
        uint Health,
        uint HealthMax,
        uint? Strength,
        uint? Endurance,
        uint? Quickness,
        uint? Coordination,
        uint? Focus,
        uint? Self,
        uint? Stamina,
        uint? Mana,
        uint? StaminaMax,
        uint? ManaMax,
        ushort? AttributeHighlights,
        ushort? AttributeColors);

    public readonly record struct Parsed(
        uint Guid,
        IdentifyResponseFlags Flags,
        bool Success,
        PropertyBundle Properties,
        uint[] SpellBook,
        ArmorProfile? ArmorProfile,
        CreatureProfile? CreatureProfile,
        WeaponProfile? WeaponProfile,
        HookProfile? HookProfile,
        ArmorLevel? ArmorLevels,
        (ushort Highlight, ushort Color)? ArmorEnchantments,
        (ushort Highlight, ushort Color)? WeaponEnchantments,
        (ushort Highlight, ushort Color)? ResistEnchantments);

    public static Parsed? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;

        int pos = 0;
        uint guid    = ReadU32(payload, ref pos);
        uint rawFlags= ReadU32(payload, ref pos);
        uint success = ReadU32(payload, ref pos);

        var flags = (IdentifyResponseFlags)rawFlags;
        var bundle = new PropertyBundle();
        uint[] spellBook = Array.Empty<uint>();
        ArmorProfile? armor = null;
        CreatureProfile? creature = null;
        WeaponProfile? weapon = null;
        HookProfile? hook = null;
        ArmorLevel? levels = null;
        (ushort H, ushort C)? armorEnc = null, weaponEnc = null, resistEnc = null;

        try
        {
            if (flags.HasFlag(IdentifyResponseFlags.IntStatsTable))
                ReadIntTable(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.Int64StatsTable))
                ReadInt64Table(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.BoolStatsTable))
                ReadBoolTable(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.FloatStatsTable))
                ReadFloatTable(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.StringStatsTable))
                ReadStringTable(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.DidStatsTable))
                ReadDataIdTable(payload, ref pos, bundle);
            if (flags.HasFlag(IdentifyResponseFlags.SpellBook))
                spellBook = ReadSpellBook(payload, ref pos);

            // Profile blobs (AppraiseInfo.Write line 751 onward, in flag order).
            if (flags.HasFlag(IdentifyResponseFlags.ArmorProfile))
                armor = ReadArmorProfile(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.CreatureProfile))
                creature = ReadCreatureProfile(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.WeaponProfile))
                weapon = ReadWeaponProfile(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.HookProfile))
                hook = ReadHookProfile(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.ArmorEnchantmentBitfield))
                armorEnc = ReadEnchantmentBitfield(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.WeaponEnchantmentBitfield))
                weaponEnc = ReadEnchantmentBitfield(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.ResistEnchantmentBitfield))
                resistEnc = ReadEnchantmentBitfield(payload, ref pos);
            if (flags.HasFlag(IdentifyResponseFlags.ArmorLevels))
                levels = ReadArmorLevel(payload, ref pos);
        }
        catch (FormatException)
        {
            return null;
        }

        return new Parsed(
            guid, flags, success != 0, bundle, spellBook,
            armor, creature, weapon, hook, levels,
            armorEnc, weaponEnc, resistEnc);
    }

    // ── Profile readers ──────────────────────────────────────────────────────

    private static ArmorProfile ReadArmorProfile(ReadOnlySpan<byte> src, ref int pos)
    {
        return new ArmorProfile(
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos),
            ReadF32(src, ref pos));
    }

    private static ArmorLevel ReadArmorLevel(ReadOnlySpan<byte> src, ref int pos)
    {
        return new ArmorLevel(
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos),
            (int)ReadU32(src, ref pos));
    }

    private static WeaponProfile ReadWeaponProfile(ReadOnlySpan<byte> src, ref int pos)
    {
        return new WeaponProfile(
            DamageType:           ReadU32(src, ref pos),
            WeaponTime:           ReadU32(src, ref pos),
            WeaponSkill:          ReadU32(src, ref pos),
            Damage:               ReadU32(src, ref pos),
            DamageVariance:       ReadF64(src, ref pos),
            DamageMod:            ReadF64(src, ref pos),
            WeaponLength:         ReadF64(src, ref pos),
            MaxVelocity:          ReadF64(src, ref pos),
            WeaponOffense:        ReadF64(src, ref pos),
            MaxVelocityEstimated: ReadU32(src, ref pos));
    }

    private static HookProfile ReadHookProfile(ReadOnlySpan<byte> src, ref int pos)
        => new(
            Flags: ReadU32(src, ref pos),
            ValidLocations: ReadU32(src, ref pos),
            AmmoType: ReadU32(src, ref pos));

    private static CreatureProfile ReadCreatureProfile(ReadOnlySpan<byte> src, ref int pos)
    {
        uint flags = ReadU32(src, ref pos);
        uint health = ReadU32(src, ref pos);
        uint healthMax = ReadU32(src, ref pos);

        uint? str = null, end = null, quic = null, coord = null, focus = null, self = null;
        uint? sta = null, mana = null, staMax = null, manaMax = null;
        ushort? attrHighlights = null, attrColors = null;

        // Flag 0x08 = ShowAttributes
        if ((flags & 0x08u) != 0)
        {
            str    = ReadU32(src, ref pos);
            end    = ReadU32(src, ref pos);
            quic   = ReadU32(src, ref pos);
            coord  = ReadU32(src, ref pos);
            focus  = ReadU32(src, ref pos);
            self   = ReadU32(src, ref pos);
            sta    = ReadU32(src, ref pos);
            mana   = ReadU32(src, ref pos);
            staMax = ReadU32(src, ref pos);
            manaMax= ReadU32(src, ref pos);
        }
        if ((flags & 0x01u) != 0)
        {
            if (src.Length - pos < 4) throw new FormatException("truncated creature buffs");
            attrHighlights = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos)); pos += 2;
            attrColors     = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos)); pos += 2;
        }

        return new CreatureProfile(
            flags, health, healthMax,
            str, end, quic, coord, focus, self,
            sta, mana, staMax, manaMax,
            attrHighlights, attrColors);
    }

    private static (ushort Highlight, ushort Color) ReadEnchantmentBitfield(
        ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated enchantment bitfield");
        ushort highlight = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos)); pos += 2;
        ushort color     = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos)); pos += 2;
        return (highlight, color);
    }

    private static float ReadF32(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated f32");
        float v = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(pos));
        pos += 4;
        return v;
    }

    // ── Table readers ────────────────────────────────────────────────────────

    private static (ushort count, ushort buckets) ReadHeader(
        ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated table header");
        ushort count   = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos));
        ushort buckets = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos + 2));
        pos += 4;
        return (count, buckets);
    }

    private static void ReadIntTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            int val  = (int)ReadU32(src, ref pos);
            bundle.Ints[key] = val;
        }
    }

    private static void ReadInt64Table(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            long val = ReadI64(src, ref pos);
            bundle.Int64s[key] = val;
        }
    }

    private static void ReadBoolTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            uint val = ReadU32(src, ref pos);
            bundle.Bools[key] = val != 0;
        }
    }

    private static void ReadFloatTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            double val = ReadF64(src, ref pos);
            bundle.Floats[key] = val;
        }
    }

    private static void ReadStringTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            string val = ReadString16L(src, ref pos);
            bundle.Strings[key] = val;
        }
    }

    private static void ReadDataIdTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            uint val = ReadU32(src, ref pos);
            bundle.DataIds[key] = val;
        }
    }

    private static uint[] ReadSpellBook(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated spellbook count");
        uint count = ReadU32(src, ref pos);
        if (count > 4096) throw new FormatException("unreasonable spellbook count");
        if (src.Length - pos < count * 4) throw new FormatException("truncated spellbook body");
        uint[] result = new uint[count];
        for (int i = 0; i < count; i++) result[i] = ReadU32(src, ref pos);
        return result;
    }

    // ── Primitive readers ───────────────────────────────────────────────────

    private static uint ReadU32(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated u32");
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos));
        pos += 4;
        return v;
    }

    private static long ReadI64(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 8) throw new FormatException("truncated i64");
        long v = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(pos));
        pos += 8;
        return v;
    }

    private static double ReadF64(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 8) throw new FormatException("truncated f64");
        double v = BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(pos));
        pos += 8;
        return v;
    }

    private static string ReadString16L(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 2) throw new FormatException("truncated string length");
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos));
        pos += 2;
        if (src.Length - pos < len) throw new FormatException("truncated string body");
        string v = Encoding.GetEncoding(1252).GetString(src.Slice(pos, len));
        pos += len;
        int record = 2 + len;
        int pad = (4 - (record & 3)) & 3;
        pos += pad;
        return v;
    }
}
