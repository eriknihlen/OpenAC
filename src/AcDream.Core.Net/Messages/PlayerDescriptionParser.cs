using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public static class PlayerDescriptionParser
{
    [Flags]
    public enum DescriptionPropertyFlag : uint
    {
        None         = 0x0000,
        PropertyInt32  = 0x0001,
        PropertyBool   = 0x0002,
        PropertyDouble = 0x0004,
        PropertyDid    = 0x0008,
        PropertyString = 0x0010,
        Position       = 0x0020,
        PropertyIid    = 0x0040,
        PropertyInt64  = 0x0080,
    }

    [Flags]
    public enum DescriptionVectorFlag : uint
    {
        None        = 0x0000,
        Attribute   = 0x0001,
        Skill       = 0x0002,
        Spell       = 0x0100,
        Enchantment = 0x0200,
    }

    public readonly record struct AttributeEntry(
        uint AtType,
        uint Ranks,
        uint Start,
        uint Xp,
        uint? Current);

    public readonly record struct SkillEntry(
        uint SkillId,
        uint Ranks,
        uint Status,
        uint Xp,
        uint Init,
        uint Resistance,
        double LastUsed);

    public readonly record struct WorldPosition(
        uint LandblockId,
        float X, float Y, float Z,
        float Qw, float Qx, float Qy, float Qz);

    public readonly record struct EnchantmentEntry(
        ushort SpellId,
        ushort Layer,
        ushort SpellCategory,
        ushort HasSpellSetId,
        uint   PowerLevel,
        double StartTime,
        double Duration,
        uint   CasterGuid,
        float  DegradeModifier,
        float  DegradeLimit,
        double LastTimeDegraded,
        uint   StatModType,
        uint   StatModKey,
        float  StatModValue,
        uint?  SpellSetId,
        EnchantmentBucket Bucket);

    public enum EnchantmentBucket : uint
    {
        Multiplicative = 1,
        Additive       = 2,
        Vitae          = 4,
        Cooldown       = 8,
    }

    [Flags]
    public enum EnchantmentMask : uint
    {
        None           = 0,
        Multiplicative = 0x01,
        Additive       = 0x02,
        Vitae          = 0x04,
        Cooldown       = 0x08,
    }

    [Flags]
    public enum CharacterOptionDataFlag : uint
    {
        None                    = 0,
        Shortcut                = 0x00000001,
        SquelchList             = 0x00000002,
        MultiSpellList          = 0x00000004,
        DesiredComps            = 0x00000008,
        ExtendedMultiSpellLists = 0x00000010,
        SpellbookFilters        = 0x00000020,
        CharacterOptions2       = 0x00000040,
        TimestampFormat         = 0x00000080,
        GenericQualitiesData    = 0x00000100,
        GameplayOptions         = 0x00000200,
        SpellLists8             = 0x00000400,
    }

    [Flags]
    public enum CharacterOptions1 : uint
    {
        None = 0,
        AllowGive = 0x00000040,
        HearAllegianceChat = 0x40000000,
        DragItemOnPlayerOpensSecureTrade = 0x04000000,
        Default = 0x50C4A54A,
    }

    [Flags]
    public enum CharacterOptions2 : uint
    {
        None = 0,
        HearGeneralChat = 0x00000100,
        HearTradeChat = 0x00000200,
        HearLFGChat = 0x00000400,
        HearRoleplayChat = 0x00000800,
        HearSocietyChat = 0x00080000,
    }

    public readonly record struct InventoryEntry(
        uint Guid,
        uint ContainerType);

    public readonly record struct EquippedEntry(
        uint Guid,
        uint EquipLocation,
        uint Priority);

    public readonly record struct Parsed(
        uint WeenieType,
        DescriptionPropertyFlag PropertyFlags,
        DescriptionVectorFlag VectorFlags,
        bool HasHealth,
        PropertyBundle Properties,
        IReadOnlyDictionary<uint, WorldPosition> Positions,
        IReadOnlyList<AttributeEntry> Attributes,
        IReadOnlyList<SkillEntry> Skills,
        IReadOnlyDictionary<uint, float> Spells,
        IReadOnlyList<EnchantmentEntry> Enchantments,
        CharacterOptionDataFlag OptionFlags,
        uint Options1,
        uint Options2,
        IReadOnlyList<ShortcutEntry> Shortcuts,
        IReadOnlyList<IReadOnlyList<uint>> HotbarSpells,
        IReadOnlyList<(uint Id, uint Amount)> DesiredComps,
        uint SpellbookFilters,
        ReadOnlyMemory<byte> GameplayOptions,
        IReadOnlyList<InventoryEntry> Inventory,
        IReadOnlyList<EquippedEntry> Equipped,
        bool TrailerTruncated);

    public static Parsed? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;

        int pos = 0;
        try
        {
            DescriptionPropertyFlag propertyFlags = (DescriptionPropertyFlag)ReadU32(payload, ref pos);
            uint weenieType = ReadU32(payload, ref pos);

            var bundle = new PropertyBundle();
            var positions = new Dictionary<uint, WorldPosition>();
            var attributes = new List<AttributeEntry>();
            var skills = new List<SkillEntry>();
            var spells = new Dictionary<uint, float>();
            var enchantments = new List<EnchantmentEntry>();

            // ── Property hashtables (each gated on a flag bit) ──────────────
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyInt32))
                ReadIntTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyInt64))
                ReadInt64Table(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyBool))
                ReadBoolTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyDouble))
                ReadDoubleTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyString))
                ReadStringTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyDid))
                ReadDataIdTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.PropertyIid))
                ReadInstanceIdTable(payload, ref pos, bundle);
            if (propertyFlags.HasFlag(DescriptionPropertyFlag.Position))
                ReadPositionTable(payload, ref pos, positions);

            // ── Vector flags + has_health ───────────────────────────────────
            if (payload.Length - pos < 8) return BuildPartial(weenieType, propertyFlags,
                DescriptionVectorFlag.None, hasHealth: false, bundle, positions, attributes, skills, spells);
            DescriptionVectorFlag vectorFlags = (DescriptionVectorFlag)ReadU32(payload, ref pos);
            bool hasHealth = ReadU32(payload, ref pos) != 0;

            // ── Attribute block (Health/Stam/Mana live at ids 7/8/9) ───────
            if (vectorFlags.HasFlag(DescriptionVectorFlag.Attribute))
                ReadAttributeBlock(payload, ref pos, attributes);

            // ── Skills ──────────────────────────────────────────────────────
            if (vectorFlags.HasFlag(DescriptionVectorFlag.Skill))
                ReadSkillTable(payload, ref pos, skills);

            // ── Spells (learned spellbook) ──────────────────────────────────
            if (vectorFlags.HasFlag(DescriptionVectorFlag.Spell))
                ReadSpellTable(payload, ref pos, spells);

            if (vectorFlags.HasFlag(DescriptionVectorFlag.Enchantment))
                ReadEnchantmentBlock(payload, ref pos, enchantments);

            CharacterOptionDataFlag optionFlags = CharacterOptionDataFlag.None;
            uint options1 = 0;
            uint options2 = 0;
            uint spellbookFilters = 0x3FFFu;
            List<ShortcutEntry> shortcuts = new();
            List<IReadOnlyList<uint>> hotbarSpells = new();
            List<(uint, uint)> desiredComps = new();
            ReadOnlyMemory<byte> gameplayOptions = ReadOnlyMemory<byte>.Empty;
            List<InventoryEntry> inventory = new();
            List<EquippedEntry> equipped = new();
            bool trailerTruncated = false;

            try
            {
                if (payload.Length - pos >= 8)
                {
                    optionFlags = (CharacterOptionDataFlag)ReadU32(payload, ref pos);
                    options1 = ReadU32(payload, ref pos);

                    if (optionFlags.HasFlag(CharacterOptionDataFlag.Shortcut))
                    {
                        uint count = ReadU32(payload, ref pos);
                        if (count > 10_000) throw new FormatException("unreasonable shortcut count");
                        for (uint i = 0; i < count; i++)
                        {
                            int index = unchecked((int)ReadU32(payload, ref pos));
                            uint objectId = ReadU32(payload, ref pos);
                            uint spellId = ReadU32(payload, ref pos);
                            shortcuts.Add(new ShortcutEntry(index, objectId, spellId));
                        }
                    }

                    if (optionFlags.HasFlag(CharacterOptionDataFlag.SpellLists8))
                    {
                        for (int b = 0; b < 8; b++)
                        {
                            uint count = ReadU32(payload, ref pos);
                            if (count > 10_000) throw new FormatException("unreasonable hotbar count");
                            var list = new List<uint>((int)count);
                            for (uint i = 0; i < count; i++)
                                list.Add(ReadU32(payload, ref pos));
                            hotbarSpells.Add(list);
                        }
                    }
                    else if (payload.Length - pos >= 4)
                    {
                        uint count = ReadU32(payload, ref pos);
                        if (count > 10_000) throw new FormatException("unreasonable hotbar count");
                        var list = new List<uint>((int)count);
                        for (uint i = 0; i < count; i++)
                            list.Add(ReadU32(payload, ref pos));
                        hotbarSpells.Add(list);
                    }

                    if (optionFlags.HasFlag(CharacterOptionDataFlag.DesiredComps))
                    {
                        if (payload.Length - pos < 4) throw new FormatException("truncated desired_comps header");
                        ushort count = ReadU16(payload, ref pos);
                        ReadU16(payload, ref pos); // padding/buckets — discarded
                        if (count > 10_000) throw new FormatException("unreasonable desired_comps count");
                        for (int i = 0; i < count; i++)
                        {
                            uint id  = ReadU32(payload, ref pos);
                            uint amt = ReadU32(payload, ref pos);
                            desiredComps.Add((id, amt));
                        }
                    }

                    if (payload.Length - pos >= 4)
                        spellbookFilters = ReadU32(payload, ref pos);

                    if (optionFlags.HasFlag(CharacterOptionDataFlag.CharacterOptions2))
                        options2 = ReadU32(payload, ref pos);

                    if (optionFlags.HasFlag(CharacterOptionDataFlag.GameplayOptions))
                    {
                        int gameplayStart = pos;
                        if (TryHeuristicInventoryStart(payload, gameplayStart, out int invStart, out int end,
                                inventory, equipped))
                        {
                            gameplayOptions = payload.Slice(gameplayStart, invStart - gameplayStart).ToArray();
                            pos = end;
                        }
                    }
                    else
                    {
                        // Strict path: inventory + equipped follow directly.
                        TryUnpackInventoryStrict(payload, ref pos, inventory, equipped);
                    }
                }
            }
            catch (FormatException)
            {
                trailerTruncated = true;
            }

            return new Parsed(
                weenieType, propertyFlags, vectorFlags, hasHealth,
                bundle, positions, attributes, skills, spells, enchantments,
                optionFlags, options1, options2,
                shortcuts, hotbarSpells, desiredComps, spellbookFilters,
                gameplayOptions, inventory, equipped, trailerTruncated);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Parsed BuildPartial(
        uint weenieType, DescriptionPropertyFlag pFlags, DescriptionVectorFlag vFlags,
        bool hasHealth, PropertyBundle bundle,
        Dictionary<uint, WorldPosition> positions,
        List<AttributeEntry> attributes, List<SkillEntry> skills,
        Dictionary<uint, float> spells)
    {
        return new Parsed(weenieType, pFlags, vFlags, hasHealth,
            bundle, positions, attributes, skills, spells,
            System.Array.Empty<EnchantmentEntry>(),
            CharacterOptionDataFlag.None, 0u, 0u,
            System.Array.Empty<ShortcutEntry>(),
            System.Array.Empty<IReadOnlyList<uint>>(),
            System.Array.Empty<(uint, uint)>(),
            0u,
            ReadOnlyMemory<byte>.Empty,
            System.Array.Empty<InventoryEntry>(),
            System.Array.Empty<EquippedEntry>(),
            TrailerTruncated: false);
    }

    // ── Attribute block reader ──────────────────────────────────────────────

    private static void ReadAttributeBlock(
        ReadOnlySpan<byte> src, ref int pos, List<AttributeEntry> attributes)
    {
        uint attrFlags = ReadU32(src, ref pos);

        // Primary attributes (1..=6): 12-byte entries (ranks, start, xp).
        for (uint i = 1; i <= 6; i++)
        {
            uint bit = 1u << (int)(i - 1);
            if ((attrFlags & bit) == 0) continue;
            uint ranks = ReadU32(src, ref pos);
            uint start = ReadU32(src, ref pos);
            uint xp    = ReadU32(src, ref pos);
            attributes.Add(new AttributeEntry(i, ranks, start, xp, Current: null));
        }

        for (uint i = 7; i <= 9; i++)
        {
            uint bit = 1u << (int)(i - 1);
            if ((attrFlags & bit) == 0) continue;
            uint ranks   = ReadU32(src, ref pos);
            uint start   = ReadU32(src, ref pos);
            uint xp      = ReadU32(src, ref pos);
            uint current = ReadU32(src, ref pos);
            attributes.Add(new AttributeEntry(i, ranks, start, xp, current));
        }
    }

    // ── Property hashtable readers ──────────────────────────────────────────

    private static (ushort count, ushort buckets) ReadHeader(ReadOnlySpan<byte> src, ref int pos)
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
            int  val = (int)ReadU32(src, ref pos);
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

    private static void ReadDoubleTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
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

    private static void ReadInstanceIdTable(ReadOnlySpan<byte> src, ref int pos, PropertyBundle bundle)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            uint val = ReadU32(src, ref pos);
            bundle.InstanceIds[key] = val;
        }
    }

    // ── Position table (one position keyed by PositionType u32) ─────────────

    private static void ReadPositionTable(
        ReadOnlySpan<byte> src, ref int pos, Dictionary<uint, WorldPosition> positions)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint key = ReadU32(src, ref pos);
            positions[key] = ReadWorldPosition(src, ref pos);
        }
    }

    private static WorldPosition ReadWorldPosition(ReadOnlySpan<byte> src, ref int pos)
    {
        uint landblockId = ReadU32(src, ref pos);
        float x  = ReadF32(src, ref pos);
        float y  = ReadF32(src, ref pos);
        float z  = ReadF32(src, ref pos);
        float qw = ReadF32(src, ref pos);
        float qx = ReadF32(src, ref pos);
        float qy = ReadF32(src, ref pos);
        float qz = ReadF32(src, ref pos);
        return new WorldPosition(landblockId, x, y, z, qw, qx, qy, qz);
    }

    // ── Skill table ─────────────────────────────────────────────────────────

    private static void ReadSkillTable(ReadOnlySpan<byte> src, ref int pos, List<SkillEntry> skills)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint sk_type = ReadU32(src, ref pos);
            if (src.Length - pos < 4) throw new FormatException("truncated skill ranks/const");
            uint ranks = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos));      pos += 2;
            BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos));                   pos += 2;
            uint status     = ReadU32(src, ref pos);
            uint xp         = ReadU32(src, ref pos);
            uint init       = ReadU32(src, ref pos);
            uint resistance = ReadU32(src, ref pos);
            double lastUsed = ReadF64(src, ref pos);
            skills.Add(new SkillEntry(sk_type, ranks, status, xp, init, resistance, lastUsed));
        }
    }

    // ── Spell table (learned spells) ────────────────────────────────────────

    private static void ReadSpellTable(
        ReadOnlySpan<byte> src, ref int pos, Dictionary<uint, float> spells)
    {
        var (count, _) = ReadHeader(src, ref pos);
        for (int i = 0; i < count; i++)
        {
            uint spellId = ReadU32(src, ref pos);
            float power  = ReadF32(src, ref pos);
            spells[spellId] = power;
        }
    }


    private static void ReadEnchantmentBlock(
        ReadOnlySpan<byte> src, ref int pos, List<EnchantmentEntry> enchantments)
    {
        if (src.Length - pos < 4) return;
        EnchantmentMask mask = (EnchantmentMask)ReadU32(src, ref pos);

        if (mask.HasFlag(EnchantmentMask.Multiplicative))
            ReadEnchantmentList(src, ref pos, enchantments, EnchantmentBucket.Multiplicative);
        if (mask.HasFlag(EnchantmentMask.Additive))
            ReadEnchantmentList(src, ref pos, enchantments, EnchantmentBucket.Additive);
        if (mask.HasFlag(EnchantmentMask.Cooldown))
            ReadEnchantmentList(src, ref pos, enchantments, EnchantmentBucket.Cooldown);
        if (mask.HasFlag(EnchantmentMask.Vitae))
        {
            enchantments.Add(ReadEnchantment(src, ref pos, EnchantmentBucket.Vitae));
        }
    }

    private static void ReadEnchantmentList(
        ReadOnlySpan<byte> src, ref int pos, List<EnchantmentEntry> dest,
        EnchantmentBucket bucket)
    {
        dest.AddRange(EnchantmentWireReader.ReadList(src, ref pos, bucket));
    }

    private static EnchantmentEntry ReadEnchantment(
        ReadOnlySpan<byte> src, ref int pos, EnchantmentBucket bucket)
    {
        return EnchantmentWireReader.Read(src, ref pos, bucket);
    }

    private static bool TryUnpackInventoryStrict(
        ReadOnlySpan<byte> src, ref int pos,
        List<InventoryEntry> inventory, List<EquippedEntry> equipped)
    {
        inventory.Clear();
        equipped.Clear();
        if (pos + 4 > src.Length) return false;
        uint invCount = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos));
        pos += 4;
        if (invCount > 10_000) return false;

        for (uint i = 0; i < invCount; i++)
        {
            if (pos + 8 > src.Length) return false;
            uint guid  = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos));
            uint wtype = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4));
            pos += 8;
            if (wtype > 2) return false;
            inventory.Add(new InventoryEntry(guid, wtype));
        }

        if (pos + 4 > src.Length) return false;
        uint eqCount = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos));
        pos += 4;
        if (eqCount > 10_000) return false;

        for (uint i = 0; i < eqCount; i++)
        {
            if (pos + 12 > src.Length) return false;
            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos));
            uint loc  = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4));
            uint prio = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 8));
            pos += 12;
            equipped.Add(new EquippedEntry(guid, loc, prio));
        }
        return true;
    }

    private static bool TryHeuristicInventoryStart(
        ReadOnlySpan<byte> src, int start,
        out int invStart, out int end,
        List<InventoryEntry> inventory, List<EquippedEntry> equipped)
    {
        invStart = end = 0;
        inventory.Clear();
        equipped.Clear();
        if (start + 8 > src.Length) return false;

        int candidate = start;
        int misalign = candidate & 3;
        if (misalign != 0) candidate += 4 - misalign;

        int last = src.Length - 8;
        while (candidate <= last)
        {
            int tmp = candidate;
            var tmpInv = new List<InventoryEntry>();
            var tmpEq  = new List<EquippedEntry>();
            if (TryUnpackInventoryStrict(src, ref tmp, tmpInv, tmpEq) && tmp == src.Length)
            {
                invStart = candidate;
                end = tmp;
                inventory.AddRange(tmpInv);
                equipped.AddRange(tmpEq);
                return true;
            }
            candidate += 4;
        }
        return false;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 2) throw new FormatException("truncated u16");
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(pos));
        pos += 2;
        return v;
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

    private static float ReadF32(ReadOnlySpan<byte> src, ref int pos)
    {
        if (src.Length - pos < 4) throw new FormatException("truncated f32");
        float v = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(pos));
        pos += 4;
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
