using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class PlayerDescriptionParserTests
{
    /// <summary>
    /// Build a minimal PlayerDescription payload with empty property
    /// flags + no-attribute vector flags. Just header bytes — useful
    /// for testing the most basic walk.
    /// </summary>
    private static byte[] BuildEmpty(uint weenieType = 1u)
    {
        // u32 propertyFlags + u32 weenieType + u32 vectorFlags + u32 has_health
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,             0u);          // no property flags
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),   weenieType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),   0u);          // no vector flags
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12),  0u);          // has_health=false
        return body;
    }

    /// <summary>
    /// Build a payload with an Attribute-block-only body
    /// (vector_flags = ATTRIBUTE) populating all 9 entries with known
    /// ranks/start/xp values — primary attrs 1..6 and vitals 7..9.
    /// </summary>
    private static byte[] BuildWithFullAttributeBlock(
        uint healthCurrent, uint stamCurrent, uint manaCurrent)
    {
        // No property tables, no positions.
        // Header (8) + vectorFlags+has_health (8) + attribFlags (4)
        //   + 6 primary entries × 12 + 3 vital entries × 16 = 8+8+4+72+48 = 140.
        byte[] body = new byte[140];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0u); p += 4;          // propertyFlags = 0
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  1u); p += 4;          // weenieType
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x01u); p += 4;       // vectorFlags = ATTRIBUTE
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  1u); p += 4;          // has_health = true
        // attributeFlags = AttributeCache.Full = 0x1FF (bits 0..8)
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x1FFu); p += 4;
        // Primary attrs 1..6 — ranks/start/xp triplets.
        for (uint i = 1; i <= 6; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 10u * i);  p += 4; // ranks
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 50u + i);  p += 4; // start
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), 1000u);    p += 4; // xp
        }
        WriteVital(body, ref p, ranks: 20u, start: 80u, xp: 5000u, current: healthCurrent);
        WriteVital(body, ref p, ranks: 20u, start: 80u, xp: 5000u, current: stamCurrent);
        WriteVital(body, ref p, ranks: 20u, start: 80u, xp: 5000u, current: manaCurrent);
        return body;
    }

    private static void WriteVital(byte[] body, ref int p, uint ranks, uint start, uint xp, uint current)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), ranks);   p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), start);   p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), xp);      p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p), current); p += 4;
    }

    [Fact]
    public void TryParse_ReturnsNull_OnTruncatedHeader()
    {
        byte[] tooShort = new byte[4];
        Assert.Null(PlayerDescriptionParser.TryParse(tooShort));
    }

    [Fact]
    public void TryParse_EmptyBody_ParsesHeaderOnly()
    {
        var p = PlayerDescriptionParser.TryParse(BuildEmpty(weenieType: 0x52u));

        Assert.NotNull(p);
        Assert.Equal(0x52u, p!.Value.WeenieType);
        Assert.Equal(PlayerDescriptionParser.DescriptionPropertyFlag.None, p.Value.PropertyFlags);
        Assert.Equal(PlayerDescriptionParser.DescriptionVectorFlag.None, p.Value.VectorFlags);
        Assert.False(p.Value.HasHealth);
        Assert.Empty(p.Value.Attributes);
    }

    [Fact]
    public void TryParse_LoginInt64Table_PopulatesTotalAndAvailableExperience()
    {
        byte[] body = new byte[44];
        int pos = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0x0080u); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 1u); pos += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos), 2); pos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(pos), 64); pos += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 1u); pos += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(pos), 1_234_567_890L); pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 2u); pos += 4;
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(pos), 75_000L); pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0u); pos += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(pos), 0u);

        var parsed = PlayerDescriptionParser.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(1_234_567_890L, parsed.Value.Properties.GetInt64(1u));
        Assert.Equal(75_000L, parsed.Value.Properties.GetInt64(2u));
    }

    [Fact]
    public void TryParse_AttributeBlock_PopulatesAllNineEntries()
    {
        var p = PlayerDescriptionParser.TryParse(
            BuildWithFullAttributeBlock(healthCurrent: 90, stamCurrent: 75, manaCurrent: 60));

        Assert.NotNull(p);
        Assert.True(p!.Value.HasHealth);
        Assert.Equal(9, p.Value.Attributes.Count);

        for (uint i = 1; i <= 6; i++)
        {
            var attr = p.Value.Attributes.First(a => a.AtType == i);
            Assert.Equal(10u * i, attr.Ranks);
            Assert.Equal(50u + i, attr.Start);
            Assert.Equal(1000u, attr.Xp);
            Assert.Null(attr.Current);
        }

        var health = p.Value.Attributes.First(a => a.AtType == 7);
        Assert.Equal(20u, health.Ranks);
        Assert.Equal(80u, health.Start);
        Assert.Equal(90u, health.Current);
        // Vital 8 = Stamina.
        var stam = p.Value.Attributes.First(a => a.AtType == 8);
        Assert.Equal(75u, stam.Current);
        // Vital 9 = Mana.
        var mana = p.Value.Attributes.First(a => a.AtType == 9);
        Assert.Equal(60u, mana.Current);
    }

    [Fact]
    public void TryParse_SkipsPrimaryAttribute_WhenItsAttributeFlagBitIsClear()
    {
        // attribute_flags only sets bits for Strength (id=1) and Health (id=7).
        // Body shape: 8 header + 8 vector header + 4 attr_flags + 12 (str) + 16 (health) = 48
        byte[] body = new byte[48];
        int p = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  1u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x01u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  1u); p += 4;
        // bit 0 (Strength = id 1) + bit 6 (Health = id 7) = 0x41
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0x41u); p += 4;
        // Strength entry (12 B):
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  100u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  60u); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(p),  0u); p += 4;
        // Health entry (16 B):
        WriteVital(body, ref p, ranks: 25u, start: 75u, xp: 0u, current: 99u);

        var parsed = PlayerDescriptionParser.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Attributes.Count);
        Assert.Equal(1u, parsed.Value.Attributes[0].AtType); // Strength
        Assert.Equal(7u, parsed.Value.Attributes[1].AtType); // Health
        Assert.Equal(99u, parsed.Value.Attributes[1].Current);
    }

    [Fact]
    public void TryParse_PropertyTablesWalked_OffsetReachesAttributeBlock()
    {
        // PROPERTY_INT32 + PROPERTY_STRING tables present, then ATTRIBUTE
        // block. If the walker can't skip past the property tables it'll
        // read garbage from the table bytes as if it were the attribute
        // block — this test fails on any walking error.
        // PROPERTY_INT32 = 0x0001, PROPERTY_STRING = 0x0010 → flags = 0x0011

        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0x0011u);           // propertyFlags = INT32 | STRING
        writer.Write(0x52u);             // weenieType

        // INT32 table: 2 entries.
        writer.Write((ushort)2);
        writer.Write((ushort)32);        // buckets (ignored)
        writer.Write(101u); writer.Write(42);
        writer.Write(102u); writer.Write(-7);

        writer.Write((ushort)1);
        writer.Write((ushort)32);        // buckets
        writer.Write(1u);                // PropertyString.Name = 1
        byte[] name = Encoding.ASCII.GetBytes("Acdream");
        writer.Write((ushort)name.Length);
        writer.Write(name);
        writer.Write(new byte[(4 - ((2 + name.Length) & 3)) & 3]); // pad to 4

        // vectorFlags = ATTRIBUTE, has_health = 1.
        writer.Write(0x01u);
        writer.Write(1u);

        writer.Write(0x40u);
        WriteVitalToWriter(writer, ranks: 30u, start: 70u, xp: 0u, current: 88u);

        byte[] body = sb.ToArray();
        var parsed = PlayerDescriptionParser.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Properties.Ints.Count);
        Assert.Equal(42, parsed.Value.Properties.Ints[101]);
        Assert.Equal(-7, parsed.Value.Properties.Ints[102]);
        Assert.Equal("Acdream", parsed.Value.Properties.Strings[1]);
        Assert.Single(parsed.Value.Attributes);
        Assert.Equal(7u, parsed.Value.Attributes[0].AtType);
        Assert.Equal(88u, parsed.Value.Attributes[0].Current);
    }

    private static void WriteVitalToWriter(BinaryWriter w, uint ranks, uint start, uint xp, uint current)
    {
        w.Write(ranks); w.Write(start); w.Write(xp); w.Write(current);
    }

    [Fact]
    public void TryParse_EnchantmentBlock_PopulatesEnchantments_WithStatModAndBucket()
    {
        // ATTRIBUTE | SPELL | ENCHANTMENT vector flag (= 0x301 minus
        // SKILL = 0x301 incl. ATTRIBUTE+SPELL+ENCHANTMENT). Empty
        // attribute block + empty spell table + 1 multiplicative
        // enchantment + 1 additive enchantment. Verifies end-to-end
        // that the enchantment record schema lands intact.
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);             // propertyFlags
        writer.Write(0x52u);           // weenieType
        // vectorFlags = ATTRIBUTE (0x01) | SPELL (0x100) | ENCHANTMENT (0x200) = 0x301
        writer.Write(0x301u);
        writer.Write(1u);              // has_health
        writer.Write(0u);              // attribute_flags = 0 -> no entries

        writer.Write((ushort)0);
        writer.Write((ushort)0);

        // EnchantmentMask = MULTIPLICATIVE (0x01) | ADDITIVE (0x02) = 0x03
        writer.Write(0x03u);
        // Multiplicative list: 1 entry
        writer.Write(1u);
        WriteEnchantment(writer,
            spellId: 1234, layer: 5, spellCategory: 100, hasSpellSetId: 0,
            powerLevel: 999, startTime: 12.5, duration: 1800.0,
            casterGuid: 0xCAFE0001u, degradeMod: 1.0f, degradeLimit: 0.5f,
            lastDegraded: 0.0, statModType: 0x00010000u, statModKey: 3u /* MaxStamina */,
            statModValue: 1.5f);
        // Additive list: 1 entry
        writer.Write(1u);
        WriteEnchantment(writer,
            spellId: 5678, layer: 6, spellCategory: 101, hasSpellSetId: 0,
            powerLevel: 100, startTime: 13.0, duration: 1500.0,
            casterGuid: 0xCAFE0002u, degradeMod: 1.0f, degradeLimit: 0.5f,
            lastDegraded: 0.0, statModType: 0x00020000u, statModKey: 5u /* MaxMana */,
            statModValue: 25.0f);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Enchantments.Count);

        var mult = parsed.Value.Enchantments[0];
        Assert.Equal((ushort)1234, mult.SpellId);
        Assert.Equal((ushort)5, mult.Layer);
        Assert.Equal(3u, mult.StatModKey);
        Assert.Equal(1.5f, mult.StatModValue);
        Assert.Equal(PlayerDescriptionParser.EnchantmentBucket.Multiplicative, mult.Bucket);

        var add = parsed.Value.Enchantments[1];
        Assert.Equal((ushort)5678, add.SpellId);
        Assert.Equal(5u, add.StatModKey);
        Assert.Equal(25.0f, add.StatModValue);
        Assert.Equal(PlayerDescriptionParser.EnchantmentBucket.Additive, add.Bucket);
    }

    [Fact]
    public void TryParse_VitaeSingleton_AppearsInEnchantments()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);              // propertyFlags
        writer.Write(0x52u);            // weenieType
        writer.Write(0x201u);           // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);               // has_health
        writer.Write(0u);               // empty attribute_flags

        writer.Write(0x04u);
        WriteEnchantment(writer,
            spellId: 7777, layer: 0, spellCategory: 0, hasSpellSetId: 0,
            powerLevel: 0, startTime: 0.0, duration: -1.0,
            casterGuid: 0u, degradeMod: 0f, degradeLimit: 0f,
            lastDegraded: 0.0, statModType: 0u, statModKey: 1u /* MaxHealth */,
            statModValue: 0.95f);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Value.Enchantments);
        Assert.Equal(PlayerDescriptionParser.EnchantmentBucket.Vitae, parsed.Value.Enchantments[0].Bucket);
        Assert.Equal(0.95f, parsed.Value.Enchantments[0].StatModValue);
    }

    private static void WriteEnchantment(BinaryWriter w,
        ushort spellId, ushort layer, ushort spellCategory, ushort hasSpellSetId,
        uint powerLevel, double startTime, double duration, uint casterGuid,
        float degradeMod, float degradeLimit, double lastDegraded,
        uint statModType, uint statModKey, float statModValue)
    {
        w.Write(spellId); w.Write(layer); w.Write(spellCategory); w.Write(hasSpellSetId);
        w.Write(powerLevel); w.Write(startTime); w.Write(duration);
        w.Write(casterGuid);
        w.Write(degradeMod); w.Write(degradeLimit); w.Write(lastDegraded);
        w.Write(statModType); w.Write(statModKey); w.Write(statModValue);
        // Skip optional spell_set_id (only present if hasSpellSetId != 0).
    }

    [Fact]
    public void TryParse_SpellTable_PopulatesSpellsDictionary()
    {
        // ATTRIBUTE | SPELL = 0x101. Empty attribute block (flags=0). Spell
        // table with two entries.
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);                // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x101u);            // vectorFlags = ATTRIBUTE | SPELL
        writer.Write(1u);                // has_health
        writer.Write(0u);                // attribute_flags = 0 → no entries

        writer.Write((ushort)2);
        writer.Write((ushort)64);        // spell buckets
        writer.Write(1234u);  writer.Write(2.0f);
        writer.Write(5678u);  writer.Write(2.0f);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Spells.Count);
        Assert.Equal(2.0f, parsed.Value.Spells[1234u]);
        Assert.Equal(2.0f, parsed.Value.Spells[5678u]);
    }

    [Fact]
    public void TryParse_TrailerOptionFlagsAndOptions1_AreReadAfterEnchantments()
    {
        // ATTRIBUTE | ENCHANTMENT vector flag; empty enchantment mask (0).
        // After mask, trailer adds u32 option_flags + u32 options1.
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);              // propertyFlags
        writer.Write(0x52u);            // weenieType
        writer.Write(0x201u);           // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);               // has_health
        writer.Write(0u);               // empty attribute_flags

        writer.Write(0u);               // EnchantmentMask = empty

        // Trailer header: option_flags + options1
        writer.Write(0u);               // option_flags = None — no further sections
        writer.Write(0xDEADBEEFu);      // options1 sentinel

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(PlayerDescriptionParser.CharacterOptionDataFlag.None, parsed!.Value.OptionFlags);
        Assert.Equal(0xDEADBEEFu, parsed.Value.Options1);
        Assert.Empty(parsed.Value.Shortcuts);
        Assert.Empty(parsed.Value.Inventory);
        Assert.Equal(0u, parsed.Value.Options2);
        Assert.Equal(0x3FFFu, parsed.Value.SpellbookFilters);
        Assert.Empty(parsed.Value.HotbarSpells);
        Assert.Empty(parsed.Value.DesiredComps);
        Assert.True(parsed.Value.GameplayOptions.IsEmpty);
        Assert.Empty(parsed.Value.Equipped);
        Assert.False(parsed.Value.TrailerTruncated);
    }

    [Fact]
    public void TryParse_TrailerShortcuts_PopulatesList()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);                // propertyFlags
        writer.Write(0x52u);              // weenieType
        writer.Write(0x201u);             // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                 // has_health
        writer.Write(0u);                 // empty attribute_flags
        writer.Write(0u);                 // empty enchantment mask

        writer.Write(0x01u);              // option_flags = SHORTCUT
        writer.Write(0xCAFEu);            // options1 sentinel

        writer.Write(2u);
        writer.Write(0); writer.Write(0xAABBCCDDu); writer.Write(0u);
        writer.Write(7); writer.Write(0u);          writer.Write(0x000504D2u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Shortcuts.Count);
        Assert.Equal(0, parsed.Value.Shortcuts[0].Index);
        Assert.Equal(0xAABBCCDDu, parsed.Value.Shortcuts[0].ObjectId);
        Assert.Equal(0u, parsed.Value.Shortcuts[0].SpellId);
        Assert.Equal(7, parsed.Value.Shortcuts[1].Index);
        Assert.Equal(0x000504D2u, parsed.Value.Shortcuts[1].SpellId);
    }

    [Fact]
    public void TryParse_TrailerShortcuts_TruncatedMidList_FlagsTrailerTruncatedAndPreservesPriorEntries()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);                // propertyFlags
        writer.Write(0x52u);              // weenieType
        writer.Write(0x201u);             // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                 // has_health
        writer.Write(0u);                 // empty attribute_flags
        writer.Write(0u);                 // empty enchantment mask

        writer.Write(0x01u);              // option_flags = SHORTCUT
        writer.Write(0u);                 // options1
        writer.Write(3u);
        writer.Write(1u); writer.Write(0xAAAAu); writer.Write((ushort)10); writer.Write((ushort)1);
        // Second entry truncated to 8 bytes — the raw spell-word read throws.
        writer.Write(2u); writer.Write(0xBBBBu);
        // (no raw SpellId word — payload ends here)

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.True(parsed!.Value.TrailerTruncated);
        // First entry survives in the partial list.
        Assert.Single(parsed.Value.Shortcuts);
        Assert.Equal(1, parsed.Value.Shortcuts[0].Index);
    }

    [Fact]
    public void TryParse_TrailerHotbarSpells_SpellLists8_Reads8Lists()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0x400u);            // option_flags = SPELL_LISTS8
        writer.Write(0u);                // options1

        writer.Write(2u); writer.Write(11u); writer.Write(12u);
        writer.Write(1u); writer.Write(21u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(3u); writer.Write(81u); writer.Write(82u); writer.Write(83u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(8, parsed!.Value.HotbarSpells.Count);
        Assert.Equal(new uint[] { 11u, 12u }, parsed.Value.HotbarSpells[0]);
        Assert.Equal(new uint[] { 21u }, parsed.Value.HotbarSpells[1]);
        Assert.Empty(parsed.Value.HotbarSpells[2]);
        Assert.Equal(new uint[] { 81u, 82u, 83u }, parsed.Value.HotbarSpells[7]);
    }

    [Fact]
    public void TryParse_TrailerHotbarSpells_NoSpellLists8_ReadsSingleLegacyList()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0u);                // option_flags = None (no SPELL_LISTS8)
        writer.Write(0u);                // options1

        writer.Write(2u); writer.Write(101u); writer.Write(102u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Value.HotbarSpells);
        Assert.Equal(new uint[] { 101u, 102u }, parsed.Value.HotbarSpells[0]);
    }

    [Fact]
    public void TryParse_TrailerAbsent_LessThan8BytesAfterEnchantments_PreservesUpstreamAndDoesNotFlagTruncation()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);              // propertyFlags
        writer.Write(0x52u);            // weenieType
        writer.Write(0x201u);           // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);               // has_health
        // Attribute block: only Strength (bit 0).
        writer.Write(0x01u);
        writer.Write(50u); writer.Write(10u); writer.Write(0u);
        // Empty enchantment mask.
        writer.Write(0u);
        // Truncated trailer: only 4 bytes (would-be option_flags) instead of 8.
        writer.Write(0xCAFEu);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        // Upstream attribute survived.
        Assert.Single(parsed!.Value.Attributes);
        Assert.Equal(1u, parsed.Value.Attributes[0].AtType);
        // Trailer was absent (< 8 bytes), so no truncation flag and all
        // trailer fields stay at their initial defaults.
        Assert.False(parsed.Value.TrailerTruncated);
        Assert.Equal(PlayerDescriptionParser.CharacterOptionDataFlag.None, parsed.Value.OptionFlags);
        Assert.Equal(0u, parsed.Value.Options1);
    }

    [Fact]
    public void TryParse_TrailerDesiredComps_ReadsIdAmtPairs()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0x08u);
        writer.Write(0u);                // options1

        writer.Write(0u);

        writer.Write((ushort)2);
        writer.Write((ushort)0);
        writer.Write(0xAAu); writer.Write(50u);
        writer.Write(0xBBu); writer.Write(75u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.DesiredComps.Count);
        Assert.Equal((0xAAu, 50u), parsed.Value.DesiredComps[0]);
        Assert.Equal((0xBBu, 75u), parsed.Value.DesiredComps[1]);
    }

    [Fact]
    public void TryParse_TrailerSpellbookFilters_ReadOptionalU32()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0u);                // option_flags = None
        writer.Write(0u);                // options1

        writer.Write(0u);

        // spellbook_filters sentinel.
        writer.Write(0xF00DBA42u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(0xF00DBA42u, parsed!.Value.SpellbookFilters);
    }

    [Fact]
    public void TryParse_TrailerOptions2_GatedOnCharacterOptions2Bit()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0x40u);
        writer.Write(0u);                // options1

        writer.Write(0u);

        // spellbook_filters
        writer.Write(0u);

        // options2 sentinel
        writer.Write(0xC0FFEE01u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(0xC0FFEE01u, parsed!.Value.Options2);
    }

    [Fact]
    public void TryParse_TrailerInventoryEquippedStrict_NoGameplayOptionsBit()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0u);                // option_flags = None — no GAMEPLAY_OPTIONS
        writer.Write(0u);                // options1
        writer.Write(0u);
        writer.Write(0u);                // spellbook_filters

        // Inventory: 2 entries
        writer.Write(2u);
        writer.Write(0x500000A0u); writer.Write(0u);  // NonContainer
        writer.Write(0x500000A1u); writer.Write(1u);

        // Equipped: 1 entry
        writer.Write(1u);
        writer.Write(0x500000B0u); writer.Write(0x00000200u); writer.Write(1u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Inventory.Count);
        Assert.Equal(0x500000A0u, parsed.Value.Inventory[0].Guid);
        Assert.Equal(0u, parsed.Value.Inventory[0].ContainerType);
        Assert.Equal(1u, parsed.Value.Inventory[1].ContainerType);
        Assert.Single(parsed.Value.Equipped);
        Assert.Equal(0x500000B0u, parsed.Value.Equipped[0].Guid);
        Assert.Equal(0x00000200u, parsed.Value.Equipped[0].EquipLocation);
        Assert.Equal(1u, parsed.Value.Equipped[0].Priority);
    }

    [Fact]
    public void TryParse_TrailerGameplayOptions_HeuristicLocatesInventoryStart()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        // option_flags = GAMEPLAY_OPTIONS (0x200)
        writer.Write(0x200u);
        writer.Write(0u);                // options1
        writer.Write(0u);
        writer.Write(0u);                // spellbook_filters

        writer.Write(0xDEADBEEFu);
        writer.Write(0xCAFEBABEu);
        writer.Write(0x12345678u);
        writer.Write(0x87654321u);

        writer.Write(1u);
        writer.Write(0x50000200u); writer.Write(0u);
        writer.Write(1u);
        writer.Write(0x50000300u); writer.Write(0x00000200u); writer.Write(1u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Value.Inventory);
        Assert.Equal(0x50000200u, parsed.Value.Inventory[0].Guid);
        Assert.Single(parsed.Value.Equipped);
        Assert.Equal(0x50000300u, parsed.Value.Equipped[0].Guid);
        Assert.Equal(16, parsed.Value.GameplayOptions.Length);
    }

    [Fact]
    public void TryParse_FullTrailer_AllSectionsPopulated()
    {
        var sb = new MemoryStream();
        using var writer = new BinaryWriter(sb);
        writer.Write(0u);               // propertyFlags
        writer.Write(0x52u);             // weenieType
        writer.Write(0x201u);            // ATTRIBUTE | ENCHANTMENT
        writer.Write(1u);                // has_health
        writer.Write(0u);                // empty attribute_flags
        writer.Write(0u);                // empty enchantment mask

        writer.Write(0x449u);
        writer.Write(0xAA000001u);       // options1

        writer.Write(1u);
        writer.Write(3); writer.Write(0xCAFEFACEu); writer.Write(0x00020064u);

        // 8 hotbars, all empty for brevity.
        for (int i = 0; i < 8; i++) writer.Write(0u);

        writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write(0xC1u); writer.Write(99u);

        // spellbook_filters
        writer.Write(0xF11Du);

        // options2
        writer.Write(0xBB000002u);

        // Inventory + equipped (no GAMEPLAY_OPTIONS, strict path)
        writer.Write(1u);
        writer.Write(0x50000400u); writer.Write(0u);
        writer.Write(1u);
        writer.Write(0x50000500u); writer.Write(0x00000200u); writer.Write(1u);

        var parsed = PlayerDescriptionParser.TryParse(sb.ToArray());

        Assert.NotNull(parsed);
        var v = parsed!.Value;
        Assert.Equal(0xAA000001u, v.Options1);
        Assert.Equal(0xBB000002u, v.Options2);
        Assert.Equal(0xF11Du, v.SpellbookFilters);
        Assert.Single(v.Shortcuts);
        Assert.Equal(0xCAFEFACEu, v.Shortcuts[0].ObjectId);
        Assert.Equal(0x00020064u, v.Shortcuts[0].SpellId);
        Assert.Equal(8, v.HotbarSpells.Count);
        Assert.All(v.HotbarSpells, l => Assert.Empty(l));
        Assert.Single(v.DesiredComps);
        Assert.Equal((0xC1u, 99u), v.DesiredComps[0]);
        Assert.Single(v.Inventory);
        Assert.Equal(0x50000400u, v.Inventory[0].Guid);
        Assert.Single(v.Equipped);
        Assert.Equal(0x50000500u, v.Equipped[0].Guid);
    }
}
