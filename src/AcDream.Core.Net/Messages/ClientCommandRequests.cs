using System.Buffers.Binary;
using System.Text;

namespace AcDream.Core.Net.Messages;

public static class ClientCommandRequests
{
    public const uint MarketplaceOpcode = 0x028Du;
    public const uint PkArenaOpcode = 0x0027u;
    public const uint PkLiteArenaOpcode = 0x0026u;
    public const uint EnterPkLiteOpcode = 0x028Fu;
    public const uint HouseRecallOpcode = 0x0262u;
    public const uint MansionRecallOpcode = 0x0278u;
    public const uint QueryAgeOpcode = 0x01C2u;
    public const uint QueryBirthOpcode = 0x01C4u;
    public const uint ConfirmationResponseOpcode = 0x0275u;
    public const uint SuicideOpcode = 0x0279u;
    public const uint SetAfkModeOpcode = 0x000Fu;
    public const uint SetAfkMessageOpcode = 0x0010u;
    public const uint EmoteOpcode = 0x01DFu;
    public const uint SoulEmoteOpcode = 0x01E1u;
    public const uint AddFriendOpcode = 0x0018u;
    public const uint AbandonContractOpcode = 0x0316u;
    public const uint RemoveFriendOpcode = 0x0017u;
    public const uint ClearFriendsOpcode = 0x0025u;
    public const uint ModifyCharacterSquelchOpcode = 0x0058u;
    public const uint ModifyAccountSquelchOpcode = 0x0059u;
    public const uint ModifyGlobalSquelchOpcode = 0x005Bu;
    public const uint ClearConsentOpcode = 0x0216u;
    public const uint DisplayConsentOpcode = 0x0217u;
    public const uint RemoveConsentOpcode = 0x0218u;
    public const uint SetDesiredComponentLevelOpcode = 0x0224u;
    public const uint AddSpellFavoriteOpcode = 0x01E3u;
    public const uint RemoveSpellFavoriteOpcode = 0x01E4u;
    public const uint SpellbookFilterOpcode = 0x0286u;
    public const uint RemoveSpellOpcode = 0x01A8u;
    public const uint LegacyFriendsOpcode = 0xF7CDu;

    public const uint IndexChannelsOpcode = 0x0149u;
    public const uint ListChannelsOpcode = 0x0148u;
    public const uint AddChannelOpcode = 0x0145u;
    public const uint RemoveChannelOpcode = 0x0146u;
    public const uint RecallAllegianceHometownOpcode = 0x02ABu;
    public const uint AllegianceInfoRequestOpcode = 0x027Bu;
    public const uint ListAvailableHousesOpcode = 0x0270u;
    public const uint AddPlayerPermissionOpcode = 0x0219u;
    public const uint RemovePlayerPermissionOpcode = 0x021Au;
    public const uint AbandonHouseOpcode = 0x021Fu;
    public const uint QueryAllegianceNameOpcode = 0x0030u;
    public const uint ClearAllegianceNameOpcode = 0x0031u;
    public const uint SetAllegianceNameOpcode = 0x0033u;
    public const uint SetAllegianceOfficerOpcode = 0x003Bu;
    public const uint SetAllegianceOfficerTitleOpcode = 0x003Cu;
    public const uint ListAllegianceOfficerTitlesOpcode = 0x003Du;
    public const uint ClearAllegianceOfficerTitlesOpcode = 0x003Eu;
    public const uint DoAllegianceLockActionOpcode = 0x003Fu;
    public const uint SetAllegianceApprovedVassalOpcode = 0x0040u;
    public const uint AllegianceChatGagOpcode = 0x0041u;
    public const uint DoAllegianceHouseActionOpcode = 0x0042u;
    public const uint AddPermanentGuestOpcode = 0x0245u;
    public const uint RemovePermanentGuestOpcode = 0x0246u;
    public const uint SetOpenHouseStatusOpcode = 0x0247u;
    public const uint ChangeStoragePermissionOpcode = 0x0249u;
    public const uint BootSpecificHouseGuestOpcode = 0x024Au;
    public const uint RemoveAllStoragePermissionOpcode = 0x024Cu;
    public const uint RequestFullGuestListOpcode = 0x024Du;
    public const uint SetMotdOpcode = 0x0254u;
    public const uint QueryMotdOpcode = 0x0255u;
    public const uint ClearMotdOpcode = 0x0256u;
    public const uint AddAllStoragePermissionOpcode = 0x025Cu;
    public const uint RemoveAllPermanentGuestsOpcode = 0x025Eu;
    public const uint BootEveryoneOpcode = 0x025Fu;
    public const uint SetHooksVisibilityOpcode = 0x0266u;
    public const uint ModifyAllegianceGuestPermissionOpcode = 0x0267u;
    public const uint ModifyAllegianceStoragePermissionOpcode = 0x0268u;
    public const uint BreakAllegianceBootOpcode = 0x0277u;
    public const uint AllegianceChatBootOpcode = 0x02A0u;
    public const uint AddAllegianceBanOpcode = 0x02A1u;
    public const uint RemoveAllegianceBanOpcode = 0x02A2u;
    public const uint ListAllegianceBansOpcode = 0x02A3u;
    public const uint RemoveAllegianceOfficerOpcode = 0x02A5u;
    public const uint ListAllegianceOfficersOpcode = 0x02A6u;
    public const uint ClearAllegianceOfficersOpcode = 0x02A7u;
    public const uint HouseQueryOpcode = 0x021Eu;

    public static byte[] BuildMarketplace(uint sequence) =>
        BuildParameterless(sequence, MarketplaceOpcode);

    public static byte[] BuildPkArena(uint sequence) =>
        BuildParameterless(sequence, PkArenaOpcode);

    public static byte[] BuildPkLiteArena(uint sequence) =>
        BuildParameterless(sequence, PkLiteArenaOpcode);

    public static byte[] BuildEnterPkLite(uint sequence) =>
        BuildParameterless(sequence, EnterPkLiteOpcode);

    public static byte[] BuildHouseRecall(uint sequence) =>
        BuildParameterless(sequence, HouseRecallOpcode);

    public static byte[] BuildMansionRecall(uint sequence) =>
        BuildParameterless(sequence, MansionRecallOpcode);

    public static byte[] BuildQueryAge(uint sequence, uint objectId = 0u) =>
        BuildObjectQuery(sequence, QueryAgeOpcode, objectId);

    public static byte[] BuildQueryBirth(uint sequence, uint objectId = 0u) =>
        BuildObjectQuery(sequence, QueryBirthOpcode, objectId);

    public static byte[] BuildConfirmationResponse(
        uint sequence,
        uint confirmationType,
        uint contextId,
        bool accepted)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body, InteractRequests.GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), ConfirmationResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), confirmationType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), contextId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), accepted ? 1u : 0u);
        return body;
    }

    public static byte[] BuildSuicide(uint sequence) =>
        BuildParameterless(sequence, SuicideOpcode);

    public static byte[] BuildSetAfkMode(uint sequence, bool away) =>
        BuildUInt32(sequence, SetAfkModeOpcode, away ? 1u : 0u);

    public static byte[] BuildSetAfkMessage(uint sequence, string message) =>
        BuildString(sequence, SetAfkMessageOpcode, message);

    public static byte[] BuildEmote(uint sequence, string message) =>
        BuildString(sequence, EmoteOpcode, message);

    public static byte[] BuildSoulEmote(uint sequence, string message) =>
        BuildString(sequence, SoulEmoteOpcode, message);

    public static byte[] BuildAddFriend(uint sequence, string name) =>
        BuildString(sequence, AddFriendOpcode, name);

    public static byte[] BuildAbandonContract(uint sequence, uint contractId) =>
        BuildUInt32(sequence, AbandonContractOpcode, contractId);

    public static byte[] BuildRemoveFriend(uint sequence, uint friendId) =>
        BuildUInt32(sequence, RemoveFriendOpcode, friendId);

    public static byte[] BuildClearFriends(uint sequence) =>
        BuildParameterless(sequence, ClearFriendsOpcode);

    public static byte[] BuildLegacyFriendsCommand(uint command, string name)
    {
        byte[] packedName = PackString16L(name);
        byte[] body = new byte[8 + packedName.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, LegacyFriendsOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), command);
        packedName.CopyTo(body, 8);
        return body;
    }

    public static byte[] BuildModifyCharacterSquelch(
        uint sequence,
        bool add,
        uint characterId,
        string name,
        uint messageType)
    {
        byte[] packedName = PackString16L(name);
        byte[] body = CreateBody(sequence, ModifyCharacterSquelchOpcode, 12 + packedName.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), add ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), characterId);
        packedName.CopyTo(body, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20 + packedName.Length), messageType);
        return body;
    }

    public static byte[] BuildModifyAccountSquelch(uint sequence, bool add, string name)
    {
        byte[] packedName = PackString16L(name);
        byte[] body = CreateBody(sequence, ModifyAccountSquelchOpcode, 4 + packedName.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), add ? 1u : 0u);
        packedName.CopyTo(body, 16);
        return body;
    }

    public static byte[] BuildModifyGlobalSquelch(uint sequence, bool add, uint messageType)
    {
        byte[] body = CreateBody(sequence, ModifyGlobalSquelchOpcode, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), add ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), messageType);
        return body;
    }

    public static byte[] BuildClearConsent(uint sequence) =>
        BuildParameterless(sequence, ClearConsentOpcode);

    public static byte[] BuildDisplayConsent(uint sequence) =>
        BuildParameterless(sequence, DisplayConsentOpcode);

    public static byte[] BuildRemoveConsent(uint sequence, string name) =>
        BuildString(sequence, RemoveConsentOpcode, name);

    public static byte[] BuildSetDesiredComponentLevel(
        uint sequence, uint componentId, uint amount)
    {
        byte[] body = CreateBody(sequence, SetDesiredComponentLevelOpcode, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), componentId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), amount);
        return body;
    }

    public static byte[] BuildAddSpellFavorite(
        uint sequence, uint spellId, int position, int tabIndex)
    {
        byte[] body = CreateBody(sequence, AddSpellFavoriteOpcode, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), spellId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), position);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), tabIndex);
        return body;
    }

    public static byte[] BuildRemoveSpellFavorite(
        uint sequence, uint spellId, int tabIndex)
    {
        byte[] body = CreateBody(sequence, RemoveSpellFavoriteOpcode, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), spellId);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), tabIndex);
        return body;
    }

    public static byte[] BuildSpellbookFilter(uint sequence, uint filters) =>
        BuildUInt32(sequence, SpellbookFilterOpcode, filters);

    public static byte[] BuildRemoveSpell(uint sequence, uint spellId) =>
        BuildUInt32(sequence, RemoveSpellOpcode, spellId);

    // @index — GameActionChannelIndex.Handle: no payload read.
    public static byte[] BuildIndexChannels(uint sequence) =>
        BuildParameterless(sequence, IndexChannelsOpcode);

    public static byte[] BuildListChannel(uint sequence, uint channelId) =>
        BuildUInt32(sequence, ListChannelsOpcode, channelId);

    public static byte[] BuildOnChannel(uint sequence, uint channelId) =>
        BuildUInt32(sequence, AddChannelOpcode, channelId);

    public static byte[] BuildOffChannel(uint sequence, uint channelId) =>
        BuildUInt32(sequence, RemoveChannelOpcode, channelId);

    // @alh / @ah / "@allegiance hometown" —
    // GameActionRecallAllegianceHometown.Handle: no payload read.
    public static byte[] BuildRecallAllegianceHometown(uint sequence) =>
        BuildParameterless(sequence, RecallAllegianceHometownOpcode);

    public static byte[] BuildAllegianceInfoRequest(uint sequence, string playerName) =>
        BuildString(sequence, AllegianceInfoRequestOpcode, playerName);

    public static byte[] BuildListAvailableHouses(uint sequence, uint houseType) =>
        BuildUInt32(sequence, ListAvailableHousesOpcode, houseType);

    // @permit add <name> — GameActionAddPlayerPermission.Handle:
    // ReadString16L() player name.
    public static byte[] BuildAddPlayerPermission(uint sequence, string playerName) =>
        BuildString(sequence, AddPlayerPermissionOpcode, playerName);

    // @permit remove <name> — GameActionRemovePlayerPermission.Handle:
    // ReadString16L() player name.
    public static byte[] BuildRemovePlayerPermission(uint sequence, string playerName) =>
        BuildString(sequence, RemovePlayerPermissionOpcode, playerName);

    // "@house abandon" — GameActionHouseAbandon.Handle: no payload read.
    public static byte[] BuildAbandonHouse(uint sequence) =>
        BuildParameterless(sequence, AbandonHouseOpcode);

    public static byte[] BuildBreakAllegianceBoot(
        uint sequence, string playerName, bool accountBoot) =>
        BuildStringUInt32(
            sequence, BreakAllegianceBootOpcode, playerName, accountBoot ? 1u : 0u);

    public static byte[] BuildAllegianceChatBoot(
        uint sequence, string playerName, string reason) =>
        BuildTwoStrings(sequence, AllegianceChatBootOpcode, playerName, reason);

    public static byte[] BuildAllegianceChatGag(
        uint sequence, string playerName, bool enabled) =>
        BuildStringUInt32(
            sequence, AllegianceChatGagOpcode, playerName, enabled ? 1u : 0u);

    public static byte[] BuildAddAllegianceBan(uint sequence, string playerName) =>
        BuildString(sequence, AddAllegianceBanOpcode, playerName);

    public static byte[] BuildRemoveAllegianceBan(uint sequence, string playerName) =>
        BuildString(sequence, RemoveAllegianceBanOpcode, playerName);

    public static byte[] BuildListAllegianceBans(uint sequence) =>
        BuildParameterless(sequence, ListAllegianceBansOpcode);

    public static byte[] BuildSetAllegianceOfficer(
        uint sequence, string playerName, uint officerLevel) =>
        BuildStringUInt32(
            sequence, SetAllegianceOfficerOpcode, playerName, officerLevel);

    public static byte[] BuildRemoveAllegianceOfficer(
        uint sequence, string playerName) =>
        BuildString(sequence, RemoveAllegianceOfficerOpcode, playerName);

    public static byte[] BuildListAllegianceOfficers(uint sequence) =>
        BuildParameterless(sequence, ListAllegianceOfficersOpcode);

    public static byte[] BuildClearAllegianceOfficers(uint sequence) =>
        BuildParameterless(sequence, ClearAllegianceOfficersOpcode);

    public static byte[] BuildSetAllegianceOfficerTitle(
        uint sequence, uint officerLevel, string title) =>
        BuildUInt32String(
            sequence, SetAllegianceOfficerTitleOpcode, officerLevel, title);

    public static byte[] BuildListAllegianceOfficerTitles(uint sequence) =>
        BuildParameterless(sequence, ListAllegianceOfficerTitlesOpcode);

    public static byte[] BuildClearAllegianceOfficerTitles(uint sequence) =>
        BuildParameterless(sequence, ClearAllegianceOfficerTitlesOpcode);

    public static byte[] BuildQueryAllegianceName(uint sequence) =>
        BuildParameterless(sequence, QueryAllegianceNameOpcode);

    public static byte[] BuildSetAllegianceName(uint sequence, string name) =>
        BuildString(sequence, SetAllegianceNameOpcode, name);

    public static byte[] BuildClearAllegianceName(uint sequence) =>
        BuildParameterless(sequence, ClearAllegianceNameOpcode);

    public static byte[] BuildAllegianceLockAction(uint sequence, uint action) =>
        BuildUInt32(sequence, DoAllegianceLockActionOpcode, action);

    public static byte[] BuildSetAllegianceApprovedVassal(
        uint sequence, string playerName) =>
        BuildString(sequence, SetAllegianceApprovedVassalOpcode, playerName);

    public static byte[] BuildAllegianceHouseAction(uint sequence, uint action) =>
        BuildUInt32(sequence, DoAllegianceHouseActionOpcode, action);

    public static byte[] BuildQueryMotd(uint sequence) =>
        BuildParameterless(sequence, QueryMotdOpcode);

    public static byte[] BuildSetMotd(uint sequence, string motd) =>
        BuildString(sequence, SetMotdOpcode, motd);

    public static byte[] BuildClearMotd(uint sequence) =>
        BuildParameterless(sequence, ClearMotdOpcode);

    public static byte[] BuildSetOpenHouseStatus(uint sequence, bool isOpen) =>
        BuildUInt32(sequence, SetOpenHouseStatusOpcode, isOpen ? 1u : 0u);

    public static byte[] BuildAddPermanentGuest(uint sequence, string playerName) =>
        BuildString(sequence, AddPermanentGuestOpcode, playerName);

    public static byte[] BuildRemovePermanentGuest(uint sequence, string playerName) =>
        BuildString(sequence, RemovePermanentGuestOpcode, playerName);

    public static byte[] BuildRemoveAllPermanentGuests(uint sequence) =>
        BuildParameterless(sequence, RemoveAllPermanentGuestsOpcode);

    public static byte[] BuildChangeStoragePermission(
        uint sequence, string playerName, bool enabled) =>
        BuildStringUInt32(
            sequence, ChangeStoragePermissionOpcode, playerName, enabled ? 1u : 0u);

    public static byte[] BuildAddAllStoragePermission(uint sequence) =>
        BuildParameterless(sequence, AddAllStoragePermissionOpcode);

    public static byte[] BuildRemoveAllStoragePermission(uint sequence) =>
        BuildParameterless(sequence, RemoveAllStoragePermissionOpcode);

    public static byte[] BuildRequestFullGuestList(uint sequence) =>
        BuildParameterless(sequence, RequestFullGuestListOpcode);

    public static byte[] BuildBootSpecificHouseGuest(
        uint sequence, string playerName) =>
        BuildString(sequence, BootSpecificHouseGuestOpcode, playerName);

    public static byte[] BuildBootEveryone(uint sequence) =>
        BuildParameterless(sequence, BootEveryoneOpcode);

    public static byte[] BuildSetHooksVisibility(uint sequence, bool visible) =>
        BuildUInt32(sequence, SetHooksVisibilityOpcode, visible ? 1u : 0u);

    public static byte[] BuildModifyAllegianceGuestPermission(
        uint sequence, bool enabled) =>
        BuildUInt32(
            sequence, ModifyAllegianceGuestPermissionOpcode, enabled ? 1u : 0u);

    public static byte[] BuildModifyAllegianceStoragePermission(
        uint sequence, bool enabled) =>
        BuildUInt32(
            sequence, ModifyAllegianceStoragePermissionOpcode, enabled ? 1u : 0u);

    // Queries the local player's house info (owned house data, or a
    // no-house status) — GameActionHouseQuery.Handle: no payload read.
    public static byte[] BuildHouseQuery(uint sequence) =>
        BuildParameterless(sequence, HouseQueryOpcode);

    private static byte[] BuildParameterless(uint sequence, uint opcode)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, InteractRequests.GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
        return body;
    }

    private static byte[] BuildUInt32(uint sequence, uint opcode, uint value)
    {
        byte[] body = CreateBody(sequence, opcode, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), value);
        return body;
    }

    private static byte[] BuildString(uint sequence, uint opcode, string value)
    {
        byte[] packed = PackString16L(value);
        byte[] body = CreateBody(sequence, opcode, packed.Length);
        packed.CopyTo(body, 12);
        return body;
    }

    private static byte[] BuildStringUInt32(
        uint sequence, uint opcode, string value, uint number)
    {
        byte[] packed = PackString16L(value);
        byte[] body = CreateBody(sequence, opcode, packed.Length + 4);
        packed.CopyTo(body, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12 + packed.Length), number);
        return body;
    }

    private static byte[] BuildUInt32String(
        uint sequence, uint opcode, uint number, string value)
    {
        byte[] packed = PackString16L(value);
        byte[] body = CreateBody(sequence, opcode, 4 + packed.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), number);
        packed.CopyTo(body, 16);
        return body;
    }

    private static byte[] BuildTwoStrings(
        uint sequence, uint opcode, string first, string second)
    {
        byte[] packedFirst = PackString16L(first);
        byte[] packedSecond = PackString16L(second);
        byte[] body = CreateBody(
            sequence, opcode, packedFirst.Length + packedSecond.Length);
        packedFirst.CopyTo(body, 12);
        packedSecond.CopyTo(body, 12 + packedFirst.Length);
        return body;
    }

    private static byte[] CreateBody(uint sequence, uint opcode, int payloadLength)
    {
        byte[] body = new byte[12 + payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, InteractRequests.GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
        return body;
    }

    private static byte[] PackString16L(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] data = Encoding.GetEncoding(1252).GetBytes(value);
        if (data.Length > ushort.MaxValue)
            throw new ArgumentException("String too long for String16L.", nameof(value));
        int unpadded = 2 + data.Length;
        byte[] packed = new byte[(unpadded + 3) & ~3];
        BinaryPrimitives.WriteUInt16LittleEndian(packed, (ushort)data.Length);
        data.CopyTo(packed, 2);
        return packed;
    }

    private static byte[] BuildObjectQuery(uint sequence, uint opcode, uint objectId)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, InteractRequests.GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), objectId);
        return body;
    }
}
