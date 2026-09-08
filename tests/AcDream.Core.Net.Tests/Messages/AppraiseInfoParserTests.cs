using System;
using System.Buffers.Binary;
using System.IO;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class AppraiseInfoParserTests
{
    [Theory]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable, 0x0001u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.BoolStatsTable, 0x0002u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.FloatStatsTable, 0x0004u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.StringStatsTable, 0x0008u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.SpellBook, 0x0010u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.WeaponProfile, 0x0020u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.HookProfile, 0x0040u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.ArmorProfile, 0x0080u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.CreatureProfile, 0x0100u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.ArmorEnchantmentBitfield, 0x0200u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.ResistEnchantmentBitfield, 0x0400u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.WeaponEnchantmentBitfield, 0x0800u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.DidStatsTable, 0x1000u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.Int64StatsTable, 0x2000u)]
    [InlineData(AppraiseInfoParser.IdentifyResponseFlags.ArmorLevels, 0x4000u)]
    public void IdentifyResponseFlags_MatchRetailWireValues(
        AppraiseInfoParser.IdentifyResponseFlags flag,
        uint wireValue)
        => Assert.Equal(wireValue, (uint)flag);

    private static byte[] BuildPayload(
        uint guid,
        AppraiseInfoParser.IdentifyResponseFlags flags,
        bool success,
        (uint key, int value)[]? ints = null,
        (uint key, bool value)[]? bools = null,
        (uint key, string value)[]? strings = null,
        uint[]? spellBook = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(guid);
        bw.Write((uint)flags);
        bw.Write((uint)(success ? 1 : 0));

        if (flags.HasFlag(AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable) && ints is not null)
        {
            bw.Write((ushort)ints.Length);
            bw.Write((ushort)16);  // numBuckets hint
            foreach (var (k, v) in ints)
            {
                bw.Write(k);
                bw.Write(v);
            }
        }

        if (flags.HasFlag(AppraiseInfoParser.IdentifyResponseFlags.BoolStatsTable) && bools is not null)
        {
            bw.Write((ushort)bools.Length);
            bw.Write((ushort)8);
            foreach (var (k, v) in bools)
            {
                bw.Write(k);
                bw.Write(v ? 1u : 0u);
            }
        }

        if (flags.HasFlag(AppraiseInfoParser.IdentifyResponseFlags.StringStatsTable) && strings is not null)
        {
            bw.Write((ushort)strings.Length);
            bw.Write((ushort)8);
            foreach (var (k, v) in strings)
            {
                bw.Write(k);
                WriteString16L(bw, v);
            }
        }

        if (flags.HasFlag(AppraiseInfoParser.IdentifyResponseFlags.SpellBook) && spellBook is not null)
        {
            bw.Write((uint)spellBook.Length);
            foreach (var sid in spellBook) bw.Write(sid);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteString16L(BinaryWriter bw, string s)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(s);
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
        int record = 2 + bytes.Length;
        int pad = (4 - (record & 3)) & 3;
        for (int i = 0; i < pad; i++) bw.Write((byte)0);
    }

    [Fact]
    public void TryParse_GuidAndFlags_ExtractedCorrectly()
    {
        byte[] payload = BuildPayload(
            guid: 0xDEADBEEFu,
            flags: AppraiseInfoParser.IdentifyResponseFlags.None,
            success: true);

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Equal(0xDEADBEEFu, parsed!.Value.Guid);
        Assert.True(parsed.Value.Success);
    }

    [Fact]
    public void TryParse_IntStatsTable_PopulatesIntProperties()
    {
        byte[] payload = BuildPayload(
            guid: 1,
            flags: AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable,
            success: true,
            ints: new[] { ((uint)1, 100), ((uint)5, -50), ((uint)9, 42) });

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Value.Properties.Ints.Count);
        Assert.Equal(100, parsed.Value.Properties.Ints[1]);
        Assert.Equal(-50, parsed.Value.Properties.Ints[5]);
        Assert.Equal(42, parsed.Value.Properties.Ints[9]);
    }

    [Fact]
    public void TryParse_BoolStatsTable_ConvertsU32ToBool()
    {
        byte[] payload = BuildPayload(
            guid: 1,
            flags: AppraiseInfoParser.IdentifyResponseFlags.BoolStatsTable,
            success: true,
            bools: new[] { ((uint)1, true), ((uint)2, false) });

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.True(parsed!.Value.Properties.Bools[1]);
        Assert.False(parsed.Value.Properties.Bools[2]);
    }

    [Fact]
    public void TryParse_StringStatsTable_ParsesPaddedStrings()
    {
        byte[] payload = BuildPayload(
            guid: 1,
            flags: AppraiseInfoParser.IdentifyResponseFlags.StringStatsTable,
            success: true,
            strings: new[] { ((uint)1, "Excalibur"), ((uint)2, "Rusty Dagger") });

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Equal("Excalibur", parsed!.Value.Properties.Strings[1]);
        Assert.Equal("Rusty Dagger", parsed.Value.Properties.Strings[2]);
    }

    [Fact]
    public void TryParse_SpellBook_ReturnsSpellIdArray()
    {
        byte[] payload = BuildPayload(
            guid: 1,
            flags: AppraiseInfoParser.IdentifyResponseFlags.SpellBook,
            success: true,
            spellBook: new uint[] { 0x3E1, 0x3E2, 0x3E3 });

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Value.SpellBook.Length);
        Assert.Equal(0x3E1u, parsed.Value.SpellBook[0]);
    }

    [Fact]
    public void TryParse_MultipleTables_AllParsed()
    {
        var flags = AppraiseInfoParser.IdentifyResponseFlags.IntStatsTable
                  | AppraiseInfoParser.IdentifyResponseFlags.BoolStatsTable
                  | AppraiseInfoParser.IdentifyResponseFlags.SpellBook;

        byte[] payload = BuildPayload(
            guid: 1, flags, success: true,
            ints: new[] { ((uint)1, 100) },
            bools: new[] { ((uint)2, true) },
            spellBook: new uint[] { 42 });

        var parsed = AppraiseInfoParser.TryParse(payload);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Value.Properties.Ints);
        Assert.Single(parsed.Value.Properties.Bools);
        Assert.Single(parsed.Value.SpellBook);
    }

    [Fact]
    public void TryParse_Truncated_ReturnsNull()
    {
        Assert.Null(AppraiseInfoParser.TryParse(new byte[4]));
    }

    [Fact]
    public void TryParse_TruncatedGatedField_RejectsEntirePacket()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x50000001u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.HookProfile);
        bw.Write(1u);
        bw.Write(0x3u);

        Assert.Null(AppraiseInfoParser.TryParse(ms.ToArray()));
    }

    // ── Profile blobs ────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ArmorProfile_ParsesEightF32()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);   // guid
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.ArmorProfile);
        bw.Write(1u);   // success
        // 8 float protections
        bw.Write(0.1f); bw.Write(0.2f); bw.Write(0.3f); bw.Write(0.4f);
        bw.Write(0.5f); bw.Write(0.6f); bw.Write(0.7f); bw.Write(0.8f);

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Value.ArmorProfile);
        var armor = parsed.Value.ArmorProfile!.Value;
        Assert.Equal(0.1f, armor.SlashingProtection, 4);
        Assert.Equal(0.8f, armor.LightningProtection, 4);
    }

    [Fact]
    public void TryParse_ArmorLevels_NinePerBodyPart()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.ArmorLevels);
        bw.Write(1u);
        bw.Write(100);  // Head
        bw.Write(200);
        bw.Write(150);  // Abdomen
        bw.Write(125); bw.Write(110); bw.Write(90);
        bw.Write(140); bw.Write(130); bw.Write(80);

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed!.Value.ArmorLevels);
        Assert.Equal(100, parsed.Value.ArmorLevels!.Value.Head);
        Assert.Equal(200, parsed.Value.ArmorLevels.Value.Chest);
        Assert.Equal(80, parsed.Value.ArmorLevels.Value.Foot);
    }

    [Fact]
    public void TryParse_WeaponProfile_TenFieldsMixedPrimitives()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.WeaponProfile);
        bw.Write(1u);
        bw.Write(4u);       // DamageType (Fire?)
        bw.Write(30u);      // WeaponTime
        bw.Write(44u);      // WeaponSkill
        bw.Write(25u);      // Damage
        bw.Write(0.25);     // DamageVariance (f64)
        bw.Write(1.5);      // DamageMod
        bw.Write(1.0);      // WeaponLength
        bw.Write(2.0);      // MaxVelocity
        bw.Write(1.1);      // WeaponOffense
        bw.Write(5u);       // MaxVelocityEstimated

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed!.Value.WeaponProfile);
        var w = parsed.Value.WeaponProfile!.Value;
        Assert.Equal(4u, w.DamageType);
        Assert.Equal(25u, w.Damage);
        Assert.Equal(1.5, w.DamageMod, 4);
        Assert.Equal(5u, w.MaxVelocityEstimated);
    }

    [Fact]
    public void TryParse_CreatureProfile_AttributesPresent_WhenShowAttributesFlag()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.CreatureProfile);
        bw.Write(1u);
        bw.Write(0x08u);   // flags: ShowAttributes only
        bw.Write(500u);    // Health
        bw.Write(600u);    // HealthMax
        bw.Write(80u);     // Str
        bw.Write(70u);     // End
        bw.Write(65u);     // Quic
        bw.Write(60u);
        bw.Write(100u);    // Focus
        bw.Write(100u);    // Self
        bw.Write(200u);    // Stamina
        bw.Write(150u);    // Mana
        bw.Write(250u);    // StaminaMax
        bw.Write(200u);    // ManaMax

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed!.Value.CreatureProfile);
        var c = parsed.Value.CreatureProfile!.Value;
        Assert.Equal(0x08u, c.Flags);
        Assert.Equal(500u, c.Health);
        Assert.Equal(600u, c.HealthMax);
        Assert.Equal((uint?)80u, c.Strength);
        Assert.Equal((uint?)150u, c.Mana);
    }

    [Fact]
    public void TryParse_CreatureProfile_NoAttributesFlag_OnlyHealth()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.CreatureProfile);
        bw.Write(1u);
        bw.Write(0u);      // flags = 0
        bw.Write(300u);    // Health
        bw.Write(400u);    // HealthMax

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed!.Value.CreatureProfile);
        var c = parsed.Value.CreatureProfile!.Value;
        Assert.Null(c.Strength);
        Assert.Null(c.AttributeHighlights);
        Assert.Equal(300u, c.Health);
    }

    [Fact]
    public void TryParse_LiteralAceCreatureFlag_DoesNotDecodeAsWeapon()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x50000001u);
        bw.Write(0x0100u);
        bw.Write(1u);
        bw.Write(0u);
        bw.Write(300u);
        bw.Write(400u);

        AppraiseInfoParser.Parsed? parsed =
            AppraiseInfoParser.TryParse(ms.ToArray());

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Value.CreatureProfile);
        Assert.Null(parsed.Value.WeaponProfile);
        Assert.Equal(300u, parsed.Value.CreatureProfile.Value.Health);
    }

    [Fact]
    public void TryParse_ArmorEnchantmentBitfield_HighlightAndColor()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0u);
        bw.Write((uint)AppraiseInfoParser.IdentifyResponseFlags.ArmorEnchantmentBitfield);
        bw.Write(1u);
        bw.Write((ushort)0x00FF);
        bw.Write((ushort)0x0042);

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());
        Assert.NotNull(parsed!.Value.ArmorEnchantments);
        Assert.Equal((ushort)0x00FF, parsed.Value.ArmorEnchantments!.Value.Highlight);
        Assert.Equal((ushort)0x0042, parsed.Value.ArmorEnchantments.Value.Color);
    }

    [Fact]
    public void TryParse_HookProfile_PreservesAlignmentForFollowingFields()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x50000001u);
        bw.Write((uint)(
            AppraiseInfoParser.IdentifyResponseFlags.HookProfile
            | AppraiseInfoParser.IdentifyResponseFlags.ArmorEnchantmentBitfield));
        bw.Write(1u);
        bw.Write(0x0000000Bu);
        bw.Write(0x00100000u);
        bw.Write(0x00000004u);
        bw.Write((ushort)0x1234);
        bw.Write((ushort)0x5678);

        var parsed = AppraiseInfoParser.TryParse(ms.ToArray());

        Assert.NotNull(parsed);
        Assert.Equal(
            new AppraiseInfoParser.HookProfile(0xBu, 0x00100000u, 4u),
            parsed.Value.HookProfile);
        Assert.Equal(
            ((ushort)0x1234, (ushort)0x5678),
            parsed.Value.ArmorEnchantments);
    }
}
