using System;
using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public static class SocialActions
{
    public const uint GameActionEnvelope = 0xF7B1u;

    // Queries
    public const uint QueryHealthOpcode         = 0x01BFu; // u32 targetGuid
    public const uint QueryItemManaOpcode       = 0x0263u;
    public const uint PingRequestOpcode         = 0x01E9u; // no payload

    public const uint FellowshipCreateOpcode    = 0x00A2u; // string16L name, u32 shareXP
    public const uint FellowshipQuitOpcode      = 0x00A3u; // u32 disband (0/1)
    public const uint FellowshipDismissOpcode   = 0x00A4u; // u32 guid
    public const uint FellowshipRecruitOpcode   = 0x00A5u; // u32 guid
    public const uint FellowshipUpdateRequestOpcode = 0x00A6u; // u32 panelOpen (0/1) — panel visibility, NOT openness
    public const uint FellowshipAssignNewLeaderOpcode = 0x0290u; // u32 newLeaderGuid
    public const uint FellowshipChangeOpennessOpcode  = 0x0291u; // u32 isOpen (0/1) — the REAL openness toggle

    public const uint TitleSetOpcode = 0x002Cu; // u32 titleId

    public const uint SetSingleCharacterOptionOpcode = 0x0005u; // u32 optionId, u32 value (0/1)

    public const uint SetCharacterOptionsOpcode = 0x01A1u;

    private const uint PlayerModulePackHeaderBase =
        0x400u  // PM_Packed_8_SpellLists
        | 0x020u // PM_Packed_SpellbookFilters
        | 0x040u; // PM_Packed_2ndCharacterOptions
    private const uint PlayerModulePackHeaderShortcuts = 0x001u; // PM_Packed_ShortCutManager
    private const uint PlayerModulePackHeaderDesiredComps = 0x008u; // PM_Packed_DesiredComps

    /// <summary>Query a target's health — server replies with UpdateHealth (0x01C0).</summary>
    public static byte[] BuildQueryHealth(uint seq, uint targetGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  QueryHealthOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        return body;
    }

    public static byte[] BuildQueryItemMana(uint seq, uint itemGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  QueryItemManaOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        return body;
    }

    public static byte[] BuildPingRequest(uint seq)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  PingRequestOpcode);
        return body;
    }

    public static byte[] BuildFellowshipCreate(uint seq, string fellowshipName, bool shareXp)
    {
        byte[] name = PackString16L(fellowshipName);
        byte[] body = new byte[12 + name.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  FellowshipCreateOpcode);
        Array.Copy(name, 0, body, 12, name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(12 + name.Length), shareXp ? 1u : 0u);
        return body;
    }

    public static byte[] BuildFellowshipQuit(uint seq, bool disband)
    {
        byte[] body = new byte[16]; // envelope + 1 byte bool aligned to 4
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  FellowshipQuitOpcode);
        body[12] = disband ? (byte)1 : (byte)0;
        return body;
    }

    /// <summary>Dismiss a specific vassal from your fellowship.</summary>
    public static byte[] BuildFellowshipDismiss(uint seq, uint targetGuid)
        => SingleGuid(seq, FellowshipDismissOpcode, targetGuid);

    /// <summary>Recruit a target into your fellowship.</summary>
    public static byte[] BuildFellowshipRecruit(uint seq, uint targetGuid)
        => SingleGuid(seq, FellowshipRecruitOpcode, targetGuid);

    public static byte[] BuildFellowshipUpdateRequest(uint seq, bool panelOpen)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  FellowshipUpdateRequestOpcode);
        body[12] = panelOpen ? (byte)1 : (byte)0;
        return body;
    }

    public static byte[] BuildFellowshipAssignNewLeader(uint seq, uint newLeaderGuid)
        => SingleGuid(seq, FellowshipAssignNewLeaderOpcode, newLeaderGuid);

    public static byte[] BuildFellowshipChangeOpenness(uint seq, bool isOpen)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  FellowshipChangeOpennessOpcode);
        body[12] = isOpen ? (byte)1 : (byte)0;
        return body;
    }

    public static byte[] BuildTitleSet(uint seq, uint titleId)
        => SingleGuid(seq, TitleSetOpcode, titleId);

    public static byte[] BuildSetSingleCharacterOption(uint seq, uint optionId, bool value)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  SetSingleCharacterOptionOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), optionId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), value ? 1u : 0u);
        return body;
    }

    public static byte[] BuildSetCharacterOptions(
        uint seq,
        uint options1,
        uint options2,
        IReadOnlyList<ShortcutEntry> shortcuts,
        IReadOnlyList<IReadOnlyList<uint>> favoriteSpells,
        IReadOnlyDictionary<uint, uint> desiredComponents,
        uint spellbookFilters)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        ArgumentNullException.ThrowIfNull(favoriteSpells);
        ArgumentNullException.ThrowIfNull(desiredComponents);
        if (favoriteSpells.Count != 8)
        {
            throw new ArgumentException(
                "The player-module payload always carries exactly 8 favorite-spell lists.",
                nameof(favoriteSpells));
        }

        uint header = PlayerModulePackHeaderBase;
        if (shortcuts.Count > 0) header |= PlayerModulePackHeaderShortcuts;
        if (desiredComponents.Count > 0) header |= PlayerModulePackHeaderDesiredComps;

        int payloadSize =
            4  // header
            + 4  // options1
            + (shortcuts.Count > 0 ? 4 + 12 * shortcuts.Count : 0)
            + FavoriteSpellsPackSize(favoriteSpells)
            + (desiredComponents.Count > 0 ? 4 + 8 * desiredComponents.Count : 0)
            + 4  // spellbookFilters
            + 4; // options2
        int pad = (4 - (payloadSize & 3)) & 3;

        byte[] body = new byte[12 + payloadSize + pad];
        int p = 0;
        WriteU32(body, ref p, GameActionEnvelope);
        WriteU32(body, ref p, seq);
        WriteU32(body, ref p, SetCharacterOptionsOpcode);
        WriteU32(body, ref p, header);
        WriteU32(body, ref p, options1);

        if (shortcuts.Count > 0)
        {
            WriteU32(body, ref p, (uint)shortcuts.Count);
            foreach (ShortcutEntry entry in shortcuts)
            {
                WriteI32(body, ref p, entry.Index);
                WriteU32(body, ref p, entry.ObjectId);
                WriteU32(body, ref p, entry.SpellId);
            }
        }

        for (int tab = 0; tab < 8; tab++)
        {
            IReadOnlyList<uint> list = favoriteSpells[tab];
            int count = list?.Count ?? 0;
            WriteU32(body, ref p, (uint)count);
            for (int i = 0; i < count; i++)
                WriteU32(body, ref p, list![i]);
        }

        if (desiredComponents.Count > 0)
        {
            WriteU32(body, ref p, (uint)desiredComponents.Count);
            foreach (KeyValuePair<uint, uint> kvp in desiredComponents)
            {
                WriteU32(body, ref p, kvp.Key);
                WriteU32(body, ref p, kvp.Value);
            }
        }

        WriteU32(body, ref p, spellbookFilters);
        WriteU32(body, ref p, options2);
        // Tail pad bytes are already zero from `new byte[]`; nothing to write.
        return body;
    }

    private static int FavoriteSpellsPackSize(
        IReadOnlyList<IReadOnlyList<uint>> favoriteSpells)
    {
        int size = 0;
        for (int tab = 0; tab < 8; tab++)
            size += 4 + 4 * (favoriteSpells[tab]?.Count ?? 0);
        return size;
    }

    private static void WriteU32(byte[] dest, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(pos), value);
        pos += 4;
    }

    private static void WriteI32(byte[] dest, ref int pos, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest.AsSpan(pos), value);
        pos += 4;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] SingleGuid(uint seq, uint sub, uint guid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  sub);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), guid);
        return body;
    }

    private static byte[] PackString16L(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        byte[] data = Encoding.GetEncoding(1252).GetBytes(s);
        if (data.Length > ushort.MaxValue)
            throw new ArgumentException("String too long for 16-bit length prefix.", nameof(s));

        int recordSize = 2 + data.Length;
        int padding = (4 - (recordSize & 3)) & 3;
        byte[] result = new byte[recordSize + padding];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)data.Length);
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }
}

public enum CharacterOptionId : uint
{
    AutoRepeatAttack = 0x00,
    IgnoreAllegianceRequests = 0x01,
    IgnoreFellowshipRequests = 0x02,
    IgnoreTradeRequests = 0x03,
    DisableMostWeatherEffects = 0x04,
    PersistentAtDay = 0x05,
    AllowGive = 0x06,
    ViewCombatTarget = 0x07,
    ShowTooltips = 0x08,
    UseDeception = 0x09,
    ToggleRun = 0x0A,
    StayInChatMode = 0x0B,
    AdvancedCombatUI = 0x0C,
    AutoTarget = 0x0D,
    VividTargetingIndicator = 0x0E,
    FellowshipShareXP = 0x0F,
    AcceptLootPermits = 0x10,
    FellowshipShareLoot = 0x11,
    FellowshipAutoAcceptRequests = 0x12,
    SideBySideVitals = 0x13,
    CoordinatesOnRadar = 0x14,
    SpellDuration = 0x15,
    DisableHouseRestrictionEffects = 0x16,
    DragItemOnPlayerOpensSecureTrade = 0x17,
    DisplayAllegianceLogonNotifications = 0x18,
    UseChargeAttack = 0x19,
    UseCraftSuccessDialog = 0x1A,
    ListenToAllegianceChat = 0x1B,
    DisplayDateOfBirth = 0x1C,
    DisplayAge = 0x1D,
    DisplayChessRank = 0x1E,
    DisplayFishingSkill = 0x1F,
    DisplayNumberDeaths = 0x20,
    DisplayTimeStamps = 0x21,
    SalvageMultiple = 0x22,
    ListenToGeneralChat = 0x23,
    ListenToTradeChat = 0x24,
    ListenToLFGChat = 0x25,
    ListenToRoleplayChat = 0x26,
    AppearOffline = 0x27,
    DisplayNumberCharacterTitles = 0x28,
    MainPackPreferred = 0x29,
    LeadMissileTargets = 0x2A,
    UseFastMissiles = 0x2B,
    FilterLanguage = 0x2C,
    ConfirmVolatileRareUse = 0x2D,
    ListenToSocietyChat = 0x2E,
    ShowHelm = 0x2F,
    DisableDistanceFog = 0x30,
    UseMouseTurning = 0x31,
    ShowCloak = 0x32,
    LockUI = 0x33,
    HearPkDeathMessages = 0x34,
}
