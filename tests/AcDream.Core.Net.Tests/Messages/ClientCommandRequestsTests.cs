using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ClientCommandRequestsTests
{
    public static TheoryData<Func<uint, byte[]>, uint> ParameterlessActions => new()
    {
        { ClientCommandRequests.BuildMarketplace, ClientCommandRequests.MarketplaceOpcode },
        { ClientCommandRequests.BuildPkArena, ClientCommandRequests.PkArenaOpcode },
        { ClientCommandRequests.BuildPkLiteArena, ClientCommandRequests.PkLiteArenaOpcode },
        { ClientCommandRequests.BuildEnterPkLite, ClientCommandRequests.EnterPkLiteOpcode },
        { ClientCommandRequests.BuildHouseRecall, ClientCommandRequests.HouseRecallOpcode },
        { ClientCommandRequests.BuildMansionRecall, ClientCommandRequests.MansionRecallOpcode },
        { ClientCommandRequests.BuildSuicide, ClientCommandRequests.SuicideOpcode },
        { ClientCommandRequests.BuildClearFriends, ClientCommandRequests.ClearFriendsOpcode },
        { ClientCommandRequests.BuildClearConsent, ClientCommandRequests.ClearConsentOpcode },
        { ClientCommandRequests.BuildDisplayConsent, ClientCommandRequests.DisplayConsentOpcode },
        { ClientCommandRequests.BuildQueryAllegianceName, 0x0030u },
        { ClientCommandRequests.BuildClearAllegianceName, 0x0031u },
        { ClientCommandRequests.BuildListAllegianceOfficerTitles, 0x003Du },
        { ClientCommandRequests.BuildClearAllegianceOfficerTitles, 0x003Eu },
        { ClientCommandRequests.BuildListAllegianceBans, 0x02A3u },
        { ClientCommandRequests.BuildListAllegianceOfficers, 0x02A6u },
        { ClientCommandRequests.BuildClearAllegianceOfficers, 0x02A7u },
        { ClientCommandRequests.BuildQueryMotd, 0x0255u },
        { ClientCommandRequests.BuildClearMotd, 0x0256u },
        { ClientCommandRequests.BuildRemoveAllPermanentGuests, 0x025Eu },
        { ClientCommandRequests.BuildAddAllStoragePermission, 0x025Cu },
        { ClientCommandRequests.BuildRemoveAllStoragePermission, 0x024Cu },
        { ClientCommandRequests.BuildRequestFullGuestList, 0x024Du },
        { ClientCommandRequests.BuildBootEveryone, 0x025Fu },
    };

    [Theory]
    [MemberData(nameof(ParameterlessActions))]
    public void ParameterlessAction_MatchesRetailEnvelope(
        Func<uint, byte[]> build, uint opcode)
    {
        byte[] body = build(0x1234u);

        Assert.Equal(12, body.Length);
        Assert.Equal(InteractRequests.GameActionEnvelope, Read(body, 0));
        Assert.Equal(0x1234u, Read(body, 4));
        Assert.Equal(opcode, Read(body, 8));
    }

    [Theory]
    [InlineData(false, ClientCommandRequests.QueryAgeOpcode)]
    [InlineData(true, ClientCommandRequests.QueryBirthOpcode)]
    public void CharacterQuery_MatchesRetailPayload(bool birth, uint opcode)
    {
        byte[] body = birth
            ? ClientCommandRequests.BuildQueryBirth(7u, 0x50000001u)
            : ClientCommandRequests.BuildQueryAge(7u, 0x50000001u);

        Assert.Equal(16, body.Length);
        Assert.Equal(InteractRequests.GameActionEnvelope, Read(body, 0));
        Assert.Equal(7u, Read(body, 4));
        Assert.Equal(opcode, Read(body, 8));
        Assert.Equal(0x50000001u, Read(body, 12));
    }

    [Fact]
    public void ConfirmationResponse_MatchesRetailPayload()
    {
        byte[] body = ClientCommandRequests.BuildConfirmationResponse(
            9u, confirmationType: 7u, contextId: 42u, accepted: true);

        Assert.Equal(24, body.Length);
        Assert.Equal(InteractRequests.GameActionEnvelope, Read(body, 0));
        Assert.Equal(9u, Read(body, 4));
        Assert.Equal(ClientCommandRequests.ConfirmationResponseOpcode, Read(body, 8));
        Assert.Equal(7u, Read(body, 12));
        Assert.Equal(42u, Read(body, 16));
        Assert.Equal(1u, Read(body, 20));
    }

    public static TheoryData<Func<uint, string, byte[]>, uint> StringActions => new()
    {
        { ClientCommandRequests.BuildSetAfkMessage, ClientCommandRequests.SetAfkMessageOpcode },
        { ClientCommandRequests.BuildEmote, ClientCommandRequests.EmoteOpcode },
        { ClientCommandRequests.BuildSoulEmote, ClientCommandRequests.SoulEmoteOpcode },
        { ClientCommandRequests.BuildAddFriend, ClientCommandRequests.AddFriendOpcode },
        { ClientCommandRequests.BuildRemoveConsent, ClientCommandRequests.RemoveConsentOpcode },
        { ClientCommandRequests.BuildAddAllegianceBan, 0x02A1u },
        { ClientCommandRequests.BuildRemoveAllegianceBan, 0x02A2u },
        { ClientCommandRequests.BuildRemoveAllegianceOfficer, 0x02A5u },
        { ClientCommandRequests.BuildSetAllegianceName, 0x0033u },
        { ClientCommandRequests.BuildSetAllegianceApprovedVassal, 0x0040u },
        { ClientCommandRequests.BuildSetMotd, 0x0254u },
        { ClientCommandRequests.BuildAddPermanentGuest, 0x0245u },
        { ClientCommandRequests.BuildRemovePermanentGuest, 0x0246u },
        { ClientCommandRequests.BuildBootSpecificHouseGuest, 0x024Au },
    };

    [Theory]
    [MemberData(nameof(StringActions))]
    public void StringAction_PacksAlignedCp1252String16L(
        Func<uint, string, byte[]> build, uint opcode)
    {
        byte[] body = build(11u, "Bjørn");

        Assert.Equal(20, body.Length);
        Assert.Equal(InteractRequests.GameActionEnvelope, Read(body, 0));
        Assert.Equal(11u, Read(body, 4));
        Assert.Equal(opcode, Read(body, 8));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12)));
        Assert.Equal([0x42, 0x6A, 0xF8, 0x72, 0x6E], body[14..19]);
        Assert.Equal(0, body[19]);
    }

    [Fact]
    public void SetAfkMode_MatchesRetailBooleanPayload()
    {
        byte[] body = ClientCommandRequests.BuildSetAfkMode(12u, away: true);

        Assert.Equal(16, body.Length);
        Assert.Equal(ClientCommandRequests.SetAfkModeOpcode, Read(body, 8));
        Assert.Equal(1u, Read(body, 12));
    }

    [Theory]
    [InlineData(0x003Fu, 6u)]
    [InlineData(0x0042u, 5u)]
    public void AllegianceAction_PreservesRetailUInt32Payload(
        uint expectedOpcode,
        uint action)
    {
        byte[] body = expectedOpcode == 0x003Fu
            ? ClientCommandRequests.BuildAllegianceLockAction(12u, action)
            : ClientCommandRequests.BuildAllegianceHouseAction(12u, action);

        Assert.Equal(16, body.Length);
        Assert.Equal(expectedOpcode, Read(body, 8));
        Assert.Equal(action, Read(body, 12));
    }

    [Theory]
    [InlineData(0x0247u)]
    [InlineData(0x0266u)]
    [InlineData(0x0267u)]
    [InlineData(0x0268u)]
    public void HouseBooleanAction_UsesRetailOneOrZeroPayload(uint expectedOpcode)
    {
        byte[] enabled = expectedOpcode switch
        {
            0x0247u => ClientCommandRequests.BuildSetOpenHouseStatus(13u, true),
            0x0266u => ClientCommandRequests.BuildSetHooksVisibility(13u, true),
            0x0267u => ClientCommandRequests.BuildModifyAllegianceGuestPermission(13u, true),
            _ => ClientCommandRequests.BuildModifyAllegianceStoragePermission(13u, true),
        };

        Assert.Equal(16, enabled.Length);
        Assert.Equal(expectedOpcode, Read(enabled, 8));
        Assert.Equal(1u, Read(enabled, 12));
    }

    [Fact]
    public void AllegianceStringBooleanActions_PreserveStringThenUInt32Order()
    {
        byte[] boot = ClientCommandRequests.BuildBreakAllegianceBoot(
            14u, "Bjørn", accountBoot: true);
        byte[] gag = ClientCommandRequests.BuildAllegianceChatGag(
            15u, "Bjørn", enabled: false);
        byte[] storage = ClientCommandRequests.BuildChangeStoragePermission(
            16u, "Bjørn", enabled: true);

        AssertStringThenUInt32(boot, 0x0277u, 1u);
        AssertStringThenUInt32(gag, 0x0041u, 0u);
        AssertStringThenUInt32(storage, 0x0249u, 1u);
    }

    [Fact]
    public void SetAllegianceOfficer_PreservesNameThenLevelOrder()
    {
        byte[] body = ClientCommandRequests.BuildSetAllegianceOfficer(
            17u, "Bjørn", 3u);

        AssertStringThenUInt32(body, 0x003Bu, 3u);
    }

    [Fact]
    public void SetAllegianceOfficerTitle_PreservesLevelThenTitleOrder()
    {
        byte[] body = ClientCommandRequests.BuildSetAllegianceOfficerTitle(
            18u, 2u, "Marshal");

        Assert.Equal(28, body.Length);
        Assert.Equal(0x003Cu, Read(body, 8));
        Assert.Equal(2u, Read(body, 12));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(16)));
        Assert.Equal("Marshal"u8.ToArray(), body[18..25]);
        Assert.Equal([0, 0, 0], body[25..28]);
    }

    [Fact]
    public void AllegianceChatBoot_PreservesTwoAlignedString16LFields()
    {
        byte[] body = ClientCommandRequests.BuildAllegianceChatBoot(
            19u, "Bob", "No.");

        Assert.Equal(28, body.Length);
        Assert.Equal(0x02A0u, Read(body, 8));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12)));
        Assert.Equal("Bob"u8.ToArray(), body[14..17]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));
        Assert.Equal("No."u8.ToArray(), body[22..25]);
    }

    [Fact]
    public void FriendAndGlobalSquelchIntegerActions_MatchRetailPayloads()
    {
        byte[] remove = ClientCommandRequests.BuildRemoveFriend(2u, 0x50000001u);
        byte[] filter = ClientCommandRequests.BuildModifyGlobalSquelch(3u, add: false, 17u);

        Assert.Equal(ClientCommandRequests.RemoveFriendOpcode, Read(remove, 8));
        Assert.Equal(0x50000001u, Read(remove, 12));
        Assert.Equal(ClientCommandRequests.ModifyGlobalSquelchOpcode, Read(filter, 8));
        Assert.Equal(0u, Read(filter, 12));
        Assert.Equal(17u, Read(filter, 16));
    }

    [Fact]
    public void CharacterSquelch_PreservesRetailFieldOrder()
    {
        byte[] body = ClientCommandRequests.BuildModifyCharacterSquelch(
            4u, add: true, 0x50000001u, "Alice", 12u);

        Assert.Equal(32, body.Length);
        Assert.Equal(ClientCommandRequests.ModifyCharacterSquelchOpcode, Read(body, 8));
        Assert.Equal(1u, Read(body, 12));
        Assert.Equal(0x50000001u, Read(body, 16));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(20)));
        Assert.Equal(12u, Read(body, 28));
    }

    [Fact]
    public void AccountSquelch_PreservesRetailFieldOrder()
    {
        byte[] body = ClientCommandRequests.BuildModifyAccountSquelch(
            5u, add: false, "Alice");

        Assert.Equal(24, body.Length);
        Assert.Equal(ClientCommandRequests.ModifyAccountSquelchOpcode, Read(body, 8));
        Assert.Equal(0u, Read(body, 12));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void DesiredComponentLevel_MatchesRetailTwoIntegerPayload()
    {
        byte[] body = ClientCommandRequests.BuildSetDesiredComponentLevel(
            6u, 0x12000042u, 25u);

        Assert.Equal(20, body.Length);
        Assert.Equal(ClientCommandRequests.SetDesiredComponentLevelOpcode, Read(body, 8));
        Assert.Equal(0x12000042u, Read(body, 12));
        Assert.Equal(25u, Read(body, 16));
    }

    [Fact]
    public void AddSpellFavorite_MatchesRetailThreeIntegerPayload()
    {
        byte[] body = ClientCommandRequests.BuildAddSpellFavorite(9u, 42u, 3, 7);

        Assert.Equal(24, body.Length);
        Assert.Equal(ClientCommandRequests.AddSpellFavoriteOpcode, Read(body, 8));
        Assert.Equal(42u, Read(body, 12));
        Assert.Equal(3u, Read(body, 16));
        Assert.Equal(7u, Read(body, 20));
    }

    [Fact]
    public void RemoveFavoriteFilterAndLearnedSpell_MatchRetailPayloads()
    {
        byte[] remove = ClientCommandRequests.BuildRemoveSpellFavorite(2u, 42u, 6);
        byte[] filter = ClientCommandRequests.BuildSpellbookFilter(3u, 0xA5u);
        byte[] removeLearned = ClientCommandRequests.BuildRemoveSpell(4u, 77u);

        Assert.Equal(ClientCommandRequests.RemoveSpellFavoriteOpcode, Read(remove, 8));
        Assert.Equal(42u, Read(remove, 12));
        Assert.Equal(6u, Read(remove, 16));
        Assert.Equal(ClientCommandRequests.SpellbookFilterOpcode, Read(filter, 8));
        Assert.Equal(0xA5u, Read(filter, 12));
        Assert.Equal(16, removeLearned.Length);
        Assert.Equal(ClientCommandRequests.RemoveSpellOpcode, Read(removeLearned, 8));
        Assert.Equal(77u, Read(removeLearned, 12));
    }

    [Fact]
    public void LegacyFriendsCommand_IsAControlMessageWithoutGameActionEnvelope()
    {
        byte[] body = ClientCommandRequests.BuildLegacyFriendsCommand(0u, string.Empty);

        Assert.Equal(12, body.Length);
        Assert.Equal(ClientCommandRequests.LegacyFriendsOpcode, Read(body, 0));
        Assert.Equal(0u, Read(body, 4));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(8)));
    }

    private static uint Read(byte[] body, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset));

    private static void AssertStringThenUInt32(
        byte[] body,
        uint expectedOpcode,
        uint expectedValue)
    {
        Assert.Equal(24, body.Length);
        Assert.Equal(expectedOpcode, Read(body, 8));
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12)));
        Assert.Equal([0x42, 0x6A, 0xF8, 0x72, 0x6E], body[14..19]);
        Assert.Equal(0, body[19]);
        Assert.Equal(expectedValue, Read(body, 20));
    }
}
