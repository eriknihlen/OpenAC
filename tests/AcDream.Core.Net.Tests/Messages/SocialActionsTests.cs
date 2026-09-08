using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SocialActionsTests
{
    [Fact]
    public void BuildQueryHealth_HasOpcode0x01BFAndGuid()
    {
        byte[] body = SocialActions.BuildQueryHealth(seq: 3, targetGuid: 0xCAFE);
        Assert.Equal(SocialActions.QueryHealthOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xCAFEu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildQueryItemMana_HasOpcode0x0263AndGuid()
    {
        byte[] body = SocialActions.BuildQueryItemMana(seq: 4, itemGuid: 0x50000A01u);

        Assert.Equal(16, body.Length);
        Assert.Equal(SocialActions.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(SocialActions.QueryItemManaOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x50000A01u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildPingRequest_HasOpcode0x01E9()
    {
        byte[] body = SocialActions.BuildPingRequest(seq: 1);
        Assert.Equal(12, body.Length);
        Assert.Equal(SocialActions.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(SocialActions.PingRequestOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildFellowshipCreate_GoldenByteVector_StringThenShareXpU32()
    {
        byte[] body = SocialActions.BuildFellowshipCreate(
            seq: 7, fellowshipName: "Team", shareXp: true);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x07, 0x00, 0x00, 0x00, // seq 7
            0xA2, 0x00, 0x00, 0x00, // opcode 0x00A2
            0x04, 0x00, 0x54, 0x65, 0x61, 0x6D, 0x00, 0x00, // str16L "Team": u16 len=4, "Team", pad 2
            0x01, 0x00, 0x00, 0x00, // shareXP = 1 (true) — a full u32, NOT a byte
        ];

        Assert.Equal(expected, body);
        Assert.Equal(SocialActions.FellowshipCreateOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildFellowshipCreate_ShareXpFalse_EncodesZeroU32()
    {
        byte[] body = SocialActions.BuildFellowshipCreate(
            seq: 1, fellowshipName: "X", shareXp: false);

        // str16L "X": len=1, "X", pad to 4 => 4 bytes total (2+1+1 pad).
        Assert.Equal(12 + 4 + 4, body.Length);
        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(body.Length - 4)));
    }

    [Fact]
    public void BuildFellowshipQuit_CarriesDisbandFlag()
    {
        byte[] body = SocialActions.BuildFellowshipQuit(seq: 1, disband: true);
        Assert.Equal(SocialActions.FellowshipQuitOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(1, body[12]);
    }

    [Fact]
    public void BuildFellowshipDismiss_HasGuid()
    {
        byte[] body = SocialActions.BuildFellowshipDismiss(seq: 1, targetGuid: 0xDEAD);
        Assert.Equal(SocialActions.FellowshipDismissOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xDEADu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildFellowshipRecruit_HasGuid()
    {
        byte[] body = SocialActions.BuildFellowshipRecruit(seq: 1, targetGuid: 0xBEEF);
        Assert.Equal(SocialActions.FellowshipRecruitOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildFellowshipUpdateRequest_GoldenByteVector_PanelOpenBool()
    {
        byte[] body = SocialActions.BuildFellowshipUpdateRequest(seq: 9, panelOpen: true);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope
            0x09, 0x00, 0x00, 0x00, // seq 9
            0xA6, 0x00, 0x00, 0x00, // opcode 0x00A6
            0x01, 0x00, 0x00, 0x00, // panelOpen = 1
        ];

        Assert.Equal(expected, body);
        Assert.Equal(SocialActions.FellowshipUpdateRequestOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildFellowshipAssignNewLeader_GoldenByteVector()
    {
        byte[] body = SocialActions.BuildFellowshipAssignNewLeader(seq: 2, newLeaderGuid: 0x50001234u);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope
            0x02, 0x00, 0x00, 0x00, // seq 2
            0x90, 0x02, 0x00, 0x00, // opcode 0x0290
            0x34, 0x12, 0x00, 0x50,
        ];

        Assert.Equal(expected, body);
        Assert.Equal(SocialActions.FellowshipAssignNewLeaderOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildFellowshipChangeOpenness_GoldenByteVector()
    {
        byte[] body = SocialActions.BuildFellowshipChangeOpenness(seq: 4, isOpen: false);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope
            0x04, 0x00, 0x00, 0x00, // seq 4
            0x91, 0x02, 0x00, 0x00, // opcode 0x0291
            0x00, 0x00, 0x00, 0x00, // isOpen = 0 (false)
        ];

        Assert.Equal(expected, body);
        Assert.Equal(SocialActions.FellowshipChangeOpennessOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildSetSingleCharacterOption_HasOptionIdThenValue()
    {
        byte[] body = SocialActions.BuildSetSingleCharacterOption(
            seq: 1, optionId: 0x26u, value: true);

        Assert.Equal(20, body.Length);
        Assert.Equal(SocialActions.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(SocialActions.SetSingleCharacterOptionOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x26u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildSetSingleCharacterOption_FalseValueEncodesZero()
    {
        byte[] body = SocialActions.BuildSetSingleCharacterOption(
            seq: 2, optionId: 0x23u, value: false);

        Assert.Equal(0u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }


    [Fact]
    public void BuildSetCharacterOptions_GoldenByteVector_MatchesHandComputedLayout()
    {
        ShortcutEntry[] shortcuts = [new ShortcutEntry(0, 0x80000001u, 0u)];
        IReadOnlyList<uint>[] favorites =
        [
            new uint[] { 1234u }, // tab 0
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
        ];
        var desiredComponents = new Dictionary<uint, uint> { [0x68000001u] = 12u };

        byte[] body = SocialActions.BuildSetCharacterOptions(
            seq: 5u,
            options1: 0x50C4A54Au,
            options2: 0x00948700u,
            shortcuts: shortcuts,
            favoriteSpells: favorites,
            desiredComponents: desiredComponents,
            spellbookFilters: 0x3FFFu);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x05, 0x00, 0x00, 0x00, // seq 5
            0xA1, 0x01, 0x00, 0x00, // opcode 0x01A1
            0x69, 0x04, 0x00, 0x00, // header 0x469 (base 0x460 | shortcuts 0x001 | desiredComps 0x008)
            0x4A, 0xA5, 0xC4, 0x50,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, //   index 0
            0x01, 0x00, 0x00, 0x80,
            0x00, 0x00, 0x00, 0x00, //   spellId 0
            0x01, 0x00, 0x00, 0x00,
            0xD2, 0x04, 0x00, 0x00, //   spellId 1234 (0x4D2)
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, // desiredComps sizeInfo = 1
            0x01, 0x00, 0x00, 0x68,
            0x0C, 0x00, 0x00, 0x00, //   value 12
            0xFF, 0x3F, 0x00, 0x00, // spellbookFilters 0x3FFF
            0x00, 0x87, 0x94, 0x00,
        ];

        Assert.Equal(expected, body);
        Assert.Equal(92, body.Length);
    }

    [Fact]
    public void BuildSetCharacterOptions_OmitsOptionalHeaderBitsWhenSectionsEmpty()
    {
        IReadOnlyList<uint>[] favorites =
        [
            Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(),
            Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(),
        ];

        byte[] body = SocialActions.BuildSetCharacterOptions(
            seq: 1u,
            options1: 0u,
            options2: 0u,
            shortcuts: Array.Empty<ShortcutEntry>(),
            favoriteSpells: favorites,
            desiredComponents: new Dictionary<uint, uint>(),
            spellbookFilters: 0u);

        // Base header only: PM_Packed_8_SpellLists | SpellbookFilters | 2ndCharacterOptions.
        Assert.Equal(0x460u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        // envelope+seq+opcode(12) + header+options1(8) + 8 empty lists(32)
        // + spellbookFilters+options2(8).
        Assert.Equal(12 + 8 + 32 + 8, body.Length);
    }

    [Fact]
    public void BuildSetCharacterOptions_RequiresExactlyEightFavoriteSpellLists()
    {
        IReadOnlyList<uint>[] tooFew = [Array.Empty<uint>(), Array.Empty<uint>()];

        Assert.Throws<ArgumentException>(() => SocialActions.BuildSetCharacterOptions(
            seq: 1u,
            options1: 0u,
            options2: 0u,
            shortcuts: Array.Empty<ShortcutEntry>(),
            favoriteSpells: tooFew,
            desiredComponents: new Dictionary<uint, uint>(),
            spellbookFilters: 0u));
    }

    [Fact]
    public void BuildSetCharacterOptions_RoundTripsThroughPlayerDescriptionParser()
    {
        ShortcutEntry[] shortcuts =
        [
            new ShortcutEntry(3, 0x70000010u, 0u),
            new ShortcutEntry(4, 0x70000011u, 5u),
        ];
        IReadOnlyList<uint>[] favorites = new IReadOnlyList<uint>[8];
        favorites[0] = new uint[] { 111u, 222u };
        for (int tab = 1; tab < 8; tab++)
            favorites[tab] = Array.Empty<uint>();
        var desiredComponents = new Dictionary<uint, uint>
        {
            [0x68000002u] = 3u,
            [0x68000003u] = 7u,
        };

        byte[] body = SocialActions.BuildSetCharacterOptions(
            seq: 9u,
            options1: 0x12345678u,
            options2: 0x0000ABCDu,
            shortcuts: shortcuts,
            favoriteSpells: favorites,
            desiredComponents: desiredComponents,
            spellbookFilters: 0x1234u);

        byte[] packPayload = body[12..];
        byte[] syntheticPlayerDescription = new byte[16 + packPayload.Length];
        packPayload.CopyTo(syntheticPlayerDescription, 16);

        PlayerDescriptionParser.Parsed? parsed =
            PlayerDescriptionParser.TryParse(syntheticPlayerDescription);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Value.TrailerTruncated);
        Assert.Equal(0x12345678u, parsed.Value.Options1);
        Assert.Equal(0x0000ABCDu, parsed.Value.Options2);
        Assert.Equal(0x1234u, parsed.Value.SpellbookFilters);
        Assert.Equal(shortcuts, parsed.Value.Shortcuts);
        Assert.Equal(8, parsed.Value.HotbarSpells.Count);
        Assert.Equal(new uint[] { 111u, 222u }, parsed.Value.HotbarSpells[0]);
        for (int tab = 1; tab < 8; tab++)
            Assert.Empty(parsed.Value.HotbarSpells[tab]);
        Assert.Equal(2, parsed.Value.DesiredComps.Count);
        Assert.Contains((0x68000002u, 3u), parsed.Value.DesiredComps);
        Assert.Contains((0x68000003u, 7u), parsed.Value.DesiredComps);
    }


    [Fact]
    public void BuildTitleSet_GoldenByteVector()
    {
        byte[] body = SocialActions.BuildTitleSet(seq: 7, titleId: 13u);

        byte[] expected =
        [
            0xB1, 0xF7, 0x00, 0x00, // envelope 0xF7B1
            0x07, 0x00, 0x00, 0x00, // seq 7
            0x2C, 0x00, 0x00, 0x00, // opcode 0x002C TitleSet
            0x0D, 0x00, 0x00, 0x00, // titleId 13
        ];
        Assert.Equal(expected, body);
    }

    [Fact]
    public void BuildTitleSet_HasOpcodeAndTitleId()
    {
        byte[] body = SocialActions.BuildTitleSet(seq: 1, titleId: 0xBEEFu);
        Assert.Equal(SocialActions.TitleSetOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xBEEFu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }
}
