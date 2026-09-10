using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public static class GameEvents
{

    public readonly record struct ChannelBroadcast(
        uint ChannelId,
        string SenderName,
        string Message);

    public static ChannelBroadcast? ParseChannelBroadcast(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        if (payload.Length < 4) return null;
        uint channelId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        pos += 4;
        try
        {
            string sender  = ReadString16L(payload, ref pos);
            string message = ReadString16L(payload, ref pos);
            return new ChannelBroadcast(channelId, sender, message);
        }
        catch { return null; }
    }

    /// <summary>0x02BD Tell payload.</summary>
    public readonly record struct Tell(
        string Message,
        string SenderName,
        uint SenderGuid,
        uint TargetGuid,
        uint ChatType);

    public static Tell? ParseTell(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            string message = ReadString16L(payload, ref pos);
            string sender  = ReadString16L(payload, ref pos);
            if (payload.Length - pos < 12) return null;
            uint senderGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint targetGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint chatType   = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            return new Tell(message, sender, senderGuid, targetGuid, chatType);
        }
        catch { return null; }
    }

    public static string? ParseTransient(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return ReadString16L(payload, ref pos); }
        catch { return null; }
    }

    /// <summary>0x0004 PopupString — modal dialog text.</summary>
    public static string? ParsePopupString(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return ReadString16L(payload, ref pos); } catch { return null; }
    }

    public readonly record struct QueryAgeResponse(string Name, string Age);

    public static QueryAgeResponse? ParseQueryAgeResponse(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            string name = ReadString16L(payload, ref pos);
            string age = ReadString16L(payload, ref pos);
            return new QueryAgeResponse(name, age);
        }
        catch { return null; }
    }

    // ── Errors ──────────────────────────────────────────────────────────────

    public static uint? ParseWeenieError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>0x028B WeenieErrorWithString.</summary>
    public readonly record struct WeenieErrorWithString(uint ErrorCode, string Interpolation);

    public static WeenieErrorWithString? ParseWeenieErrorWithString(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        uint code = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        int pos = 4;
        try
        {
            string interp = ReadString16L(payload, ref pos);
            return new WeenieErrorWithString(code, interp);
        }
        catch { return null; }
    }


    /// <summary>0x01C0 UpdateHealth: (guid, healthPercent 0..1).</summary>
    public readonly record struct UpdateHealth(uint TargetGuid, float HealthPercent);

    public static UpdateHealth? ParseUpdateHealth(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        uint guid  = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        float pct  = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(4));
        return new UpdateHealth(guid, pct);
    }

    // ── Pings / misc ────────────────────────────────────────────────────────

    /// <summary>0x01EA PingResponse has no payload; receipt is the acknowledgement.</summary>
    public static bool ParsePingResponse(ReadOnlySpan<byte> payload)
        => payload.IsEmpty;

    // ── Spells / magic ──────────────────────────────────────────────────────

    /// <summary>0x02C1 MagicUpdateSpell: spell id added to spellbook.</summary>
    public static uint? ParseMagicUpdateSpell(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }


    /// <summary>0x01AC VictimNotification - death message for the victim.</summary>
    public readonly record struct VictimNotification(string DeathMessage);

    public static VictimNotification? ParseVictimNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return new VictimNotification(ReadString16L(payload, ref pos)); }
        catch { return null; }
    }

    /// <summary>0x01AD KillerNotification - death message for the killer.</summary>
    public readonly record struct KillerNotification(string DeathMessage);

    public static KillerNotification? ParseKillerNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return new KillerNotification(ReadString16L(payload, ref pos)); }
        catch { return null; }
    }

    /// <summary>0x01B1 AttackerNotification - "you hit X".</summary>
    public readonly record struct AttackerNotification(
        string DefenderName,
        uint DamageType,
        double HealthPercent,
        uint Damage,
        uint Critical,
        ulong AttackConditions);

    public static AttackerNotification? ParseAttackerNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            string name = ReadString16L(payload, ref pos);
            if (payload.Length - pos < 28) return null;
            uint damageType = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            double pct      = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(pos)); pos += 8;
            uint damage     = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint crit       = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            ulong cond      = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(pos)); pos += 8;
            return new AttackerNotification(name, damageType, pct, damage, crit, cond);
        }
        catch { return null; }
    }

    /// <summary>0x01B2 DefenderNotification - "X hit you".</summary>
    public readonly record struct DefenderNotification(
        string AttackerName,
        uint DamageType,
        double HealthPercent,
        uint Damage,
        uint HitQuadrant,
        uint Critical,
        ulong AttackConditions);

    public static DefenderNotification? ParseDefenderNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            string name = ReadString16L(payload, ref pos);
            if (payload.Length - pos < 32) return null;
            uint dtype = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            double pct = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(pos)); pos += 8;
            uint dmg   = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint quad  = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint crit  = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            ulong cond = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(pos)); pos += 8;
            return new DefenderNotification(name, dtype, pct, dmg, quad, crit, cond);
        }
        catch { return null; }
    }

    /// <summary>0x01B3 EvasionAttackerNotification - "X evaded".</summary>
    public static string? ParseEvasionAttackerNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return ReadString16L(payload, ref pos); } catch { return null; }
    }

    /// <summary>0x01B4 EvasionDefenderNotification - "you evaded X".</summary>
    public static string? ParseEvasionDefenderNotification(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try { return ReadString16L(payload, ref pos); } catch { return null; }
    }

    public static bool ParseCombatCommenceAttack(ReadOnlySpan<byte> payload) => payload.Length == 0;

    /// <summary>0x01A7 AttackDone - single WeenieError value.</summary>
    public readonly record struct AttackDone(uint AttackSequence, uint WeenieError);

    public static AttackDone? ParseAttackDone(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return new AttackDone(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    // ── Spell enchantments ──────────────────────────────────────────────────

    /// <summary>
    /// 0x02C3 MagicRemoveEnchantment — (layerId, spellId).
    /// </summary>
    public readonly record struct LayeredSpellId(ushort SpellId, ushort Layer)
    {
        public uint Packed => SpellId | ((uint)Layer << 16);
    }

    public readonly record struct MagicRemoveEnchantment(ushort SpellId, ushort Layer);

    public static MagicRemoveEnchantment? ParseMagicRemoveEnchantment(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return new MagicRemoveEnchantment(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2)));
    }

    /// <summary>0x01A8 MagicRemoveSpell — spell id removed from spellbook.</summary>
    public static uint? ParseMagicRemoveSpell(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static PlayerDescriptionParser.EnchantmentEntry? ParseMagicUpdateEnchantment(
        ReadOnlySpan<byte> payload)
    {
        int position = 0;
        try { return EnchantmentWireReader.Read(payload, ref position); }
        catch (FormatException) { return null; }
    }

    public static IReadOnlyList<PlayerDescriptionParser.EnchantmentEntry>?
        ParseMagicUpdateMultipleEnchantments(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        try { return EnchantmentWireReader.ReadList(payload, ref position); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// 0x02C7 MagicDispelEnchantment — (layerId, spellId).
    /// Structure matches MagicRemoveEnchantment.
    /// </summary>
    public static MagicRemoveEnchantment? ParseMagicDispelEnchantment(ReadOnlySpan<byte> payload)
        => ParseMagicRemoveEnchantment(payload);

    public static IReadOnlyList<LayeredSpellId>? ParseMagicLayeredSpellList(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (count > 0x4000 || payload.Length - 4 < checked((int)count * 4)) return null;
        var result = new LayeredSpellId[count];
        for (int i = 0; i < result.Length; i++)
        {
            int offset = 4 + i * 4;
            result[i] = new LayeredSpellId(
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 2, 2)));
        }
        return result;
    }

    // ── Appraise / identify ─────────────────────────────────────────────────

    /// <summary>0x00C9 IdentifyObjectResponse header.</summary>
    public readonly record struct IdentifyResponseHeader(
        uint Guid,
        uint AppraiseFlags,
        bool Success);

    public static IdentifyResponseHeader? ParseIdentifyResponseHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;
        uint guid    = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint flags   = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
        uint success = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8));
        return new IdentifyResponseHeader(guid, flags, success != 0);
    }

    /// <summary>0x0023 WieldObject: server-driven equip.</summary>
    public readonly record struct WieldObject(
        uint ItemGuid,
        uint EquipLoc);

    public static WieldObject? ParseWieldObject(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new WieldObject(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)));
    }

    public readonly record struct InventoryPutObjInContainer(
        uint ItemGuid,
        uint ContainerGuid,
        uint Placement,
        uint ContainerType);

    public static InventoryPutObjInContainer? ParsePutObjInContainer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) return null;
        return new InventoryPutObjInContainer(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12)));
    }

    public readonly record struct ViewContentsEntry(uint Guid, uint ContainerType);
    public readonly record struct ViewContents(uint ContainerGuid, System.Collections.Generic.IReadOnlyList<ViewContentsEntry> Items);

    public static ViewContents? ParseViewContents(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        uint containerGuid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
        int pos = 8;
        if ((long)payload.Length - pos < (long)count * 8) return null;
        var items = new ViewContentsEntry[count];
        for (int i = 0; i < count; i++)
        {
            uint guid = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            items[i] = new ViewContentsEntry(guid, type);
        }
        return new ViewContents(containerGuid, items);
    }

    // ── Other small-payload events ──────────────────────────────────────────

    public static uint? ParseUseDone(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>0x019A InventoryPutObjectIn3D: server dropped item to ground.</summary>
    public static uint? ParsePutObjectIn3D(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public readonly record struct InventoryServerSaveFailed(uint ItemGuid, uint WeenieError);

    public static InventoryServerSaveFailed? ParseInventoryServerSaveFailed(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new InventoryServerSaveFailed(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)));
    }

    public readonly record struct SalvageResult(
        uint MaterialType,
        double Workmanship,
        uint Units);

    public readonly record struct SalvageOperationsResult(
        uint SkillId,
        IReadOnlyList<uint> UnsuitableItemGuids,
        IReadOnlyList<SalvageResult> Results,
        int AugmentationBonusPercent);

    public static SalvageOperationsResult? ParseSalvageOperationsResult(
        ReadOnlySpan<byte> payload)
    {
        const int ResultSize = 16;
        int position = 0;
        if (payload.Length < 16)
            return null;

        uint skillId = BinaryPrimitives.ReadUInt32LittleEndian(payload); position += 4;
        uint unsuitableCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position)); position += 4;
        if (unsuitableCount > (uint)((payload.Length - position) / sizeof(uint)))
            return null;

        var unsuitable = new uint[(int)unsuitableCount];
        for (int index = 0; index < unsuitable.Length; index++)
        {
            unsuitable[index] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position));
            position += sizeof(uint);
        }

        if (payload.Length - position < sizeof(uint) + sizeof(int))
            return null;
        uint resultCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position));
        position += sizeof(uint);
        if (resultCount > (uint)((payload.Length - position - sizeof(int)) / ResultSize))
            return null;

        var results = new SalvageResult[(int)resultCount];
        for (int index = 0; index < results.Length; index++)
        {
            uint materialType = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position)); position += 4;
            double workmanship = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(position)); position += 8;
            uint units = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position)); position += 4;
            results[index] = new SalvageResult(materialType, workmanship, units);
        }

        if (payload.Length - position < sizeof(int))
            return null;
        int augmentationBonus = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(position));
        return new SalvageOperationsResult(skillId, unsuitable, results, augmentationBonus);
    }

    public static uint? ParseCloseGroundContainer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }


    public readonly record struct RegisterTrade(uint Initiator, uint Partner, ulong Stamp);

    public static RegisterTrade? ParseRegisterTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) return null;
        return new RegisterTrade(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)));
    }

    public static uint? ParseCloseTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public readonly record struct AddToTrade(uint ItemGuid, uint Side, uint SlotIndex);

    public static AddToTrade? ParseAddToTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;
        return new AddToTrade(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)));
    }

    public readonly record struct RemoveFromTrade(uint ItemGuid, uint Mode);

    public static RemoveFromTrade? ParseRemoveFromTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new RemoveFromTrade(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)));
    }

    public static uint? ParseAcceptTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    /// <summary>0x0203 DeclineTrade: who declined.</summary>
    public static uint? ParseDeclineTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static uint? ParseResetTrade(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public readonly record struct TradeFailure(uint ItemGuid, uint Reason);

    public static TradeFailure? ParseTradeFailure(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new TradeFailure(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)));
    }

    public readonly record struct QueryItemManaResponse(uint ItemGuid, float ManaPercent, bool Valid);

    public static QueryItemManaResponse? ParseQueryItemManaResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;
        return new QueryItemManaResponse(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)) != 0);
    }

    public readonly record struct CharacterConfirmationRequest(
        uint Type,
        uint ContextId,
        string Message);

    public static CharacterConfirmationRequest? ParseCharacterConfirmationRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        int pos = 0;
        uint type      = BinaryPrimitives.ReadUInt32LittleEndian(payload);             pos += 4;
        uint contextId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));  pos += 4;
        try
        {
            string msg = ReadString16L(payload, ref pos);
            return new CharacterConfirmationRequest(type, contextId, msg);
        }
        catch { return null; }
    }

    public readonly record struct CharacterConfirmationDone(uint Type, uint ContextId);

    public static CharacterConfirmationDone? ParseCharacterConfirmationDone(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new CharacterConfirmationDone(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)));
    }

    public enum ConfirmationType : uint
    {
        SwearAllegiance = 1,
        AlterSkill = 2,
        AlterAttribute = 3,
        Fellowship = 4,
        CraftInteraction = 5,
        Augmentation = 6,
        YesNo = 7,
    }

    public readonly record struct ConfirmationResponse(
        ConfirmationType Type,
        uint ContextId,
        bool Accepted);

    public static ConfirmationResponse? ParseConfirmationResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return null;
        return new ConfirmationResponse(
            (ConfirmationType)BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)) != 0);
    }


    public readonly record struct FellowMember(
        uint Guid,
        uint CpCache,
        uint LumCache,
        uint Level,
        uint MaxHealth,
        uint MaxStamina,
        uint MaxMana,
        uint CurrentHealth,
        uint CurrentStamina,
        uint CurrentMana,
        uint ShareLoot,
        string Name);

    /// <summary>One entry of the <c>_fellows_departed</c> hash table (lane B §2.11/§3.9 field 8).</summary>
    public readonly record struct FellowshipDepartedMember(uint Guid, int DepartedTimestamp);

    public readonly record struct FellowshipFullUpdate(
        IReadOnlyList<FellowMember> Members,
        string Name,
        uint LeaderGuid,
        bool ShareXp,
        bool EvenXpSplit,
        bool OpenFellow,
        bool Locked,
        IReadOnlyList<FellowshipDepartedMember> Departed);

    public static FellowshipFullUpdate? ParseFellowshipFullUpdate(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            ushort memberCount = FellowshipReadU16(payload, ref pos);
            _ = FellowshipReadU16(payload, ref pos);
            var members = new List<FellowMember>(memberCount);
            for (int i = 0; i < memberCount; i++)
            {
                uint guid = FellowshipReadU32(payload, ref pos);
                members.Add(ReadFellow(payload, ref pos, guid));
            }

            string name = ReadString16L(payload, ref pos);
            uint leaderGuid = FellowshipReadU32(payload, ref pos);
            bool shareXp = FellowshipReadU32(payload, ref pos) != 0u;
            bool evenXpSplit = FellowshipReadU32(payload, ref pos) != 0u;
            bool openFellow = FellowshipReadU32(payload, ref pos) != 0u;
            bool locked = FellowshipReadU32(payload, ref pos) != 0u;

            ushort departedCount = FellowshipReadU16(payload, ref pos);
            _ = FellowshipReadU16(payload, ref pos);
            var departed = new List<FellowshipDepartedMember>(departedCount);
            for (int i = 0; i < departedCount; i++)
            {
                uint guid = FellowshipReadU32(payload, ref pos);
                int timestamp = unchecked((int)FellowshipReadU32(payload, ref pos));
                departed.Add(new FellowshipDepartedMember(guid, timestamp));
            }

            return new FellowshipFullUpdate(
                members, name, leaderGuid, shareXp, evenXpSplit, openFellow, locked, departed);
        }
        catch (FormatException) { return null; }
    }

    public readonly record struct FellowshipUpdateFellow(
        uint MemberGuid,
        FellowMember Member,
        uint UpdateType);

    public static FellowshipUpdateFellow? ParseFellowshipUpdateFellow(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint guid = FellowshipReadU32(payload, ref pos);
            FellowMember member = ReadFellow(payload, ref pos, guid);
            uint updateType = FellowshipReadU32(payload, ref pos);
            return new FellowshipUpdateFellow(guid, member, updateType);
        }
        catch (FormatException) { return null; }
    }

    public readonly record struct FellowshipQuitNotice(uint QuitterGuid);

    public static FellowshipQuitNotice? ParseFellowshipQuit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return new FellowshipQuitNotice(BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    public readonly record struct FellowshipDismissNotice(uint DismissedGuid);

    public static FellowshipDismissNotice? ParseFellowshipDismiss(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return new FellowshipDismissNotice(BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    public static bool ParseFellowshipDisband(ReadOnlySpan<byte> payload) => true;

    public readonly record struct FellowshipFellowUpdateDone(uint? RawValue);

    public static FellowshipFellowUpdateDone ParseFellowshipFellowUpdateDone(ReadOnlySpan<byte> payload)
        => new(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : null);

    public readonly record struct FellowshipFellowStatsDone(uint? RawValue);

    public static FellowshipFellowStatsDone ParseFellowshipFellowStatsDone(ReadOnlySpan<byte> payload)
        => new(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : null);


    public readonly record struct CharacterTitleTable(
        uint DisplayTitleId,
        IReadOnlyList<uint> TitleIds);

    public static CharacterTitleTable? ParseCharacterTitleTable(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            _ = FellowshipReadU32(payload, ref pos);
            uint displayTitleId = FellowshipReadU32(payload, ref pos);
            uint count = FellowshipReadU32(payload, ref pos);
            if (count > 65_536) return null;
            var titleIds = new uint[count];
            for (int i = 0; i < titleIds.Length; i++)
                titleIds[i] = FellowshipReadU32(payload, ref pos);
            return new CharacterTitleTable(displayTitleId, titleIds);
        }
        catch (FormatException) { return null; }
    }

    public readonly record struct UpdateTitle(uint TitleId, bool SetAsDisplay);

    public static UpdateTitle? ParseUpdateTitle(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint titleId = FellowshipReadU32(payload, ref pos);
            bool setAsDisplay = FellowshipReadU32(payload, ref pos) != 0u;
            return new UpdateTitle(titleId, setAsDisplay);
        }
        catch (FormatException) { return null; }
    }

    private static FellowMember ReadFellow(ReadOnlySpan<byte> payload, ref int pos, uint guid)
    {
        uint cpCache = FellowshipReadU32(payload, ref pos);
        uint lumCache = FellowshipReadU32(payload, ref pos);
        uint level = FellowshipReadU32(payload, ref pos);
        uint maxHealth = FellowshipReadU32(payload, ref pos);
        uint maxStamina = FellowshipReadU32(payload, ref pos);
        uint maxMana = FellowshipReadU32(payload, ref pos);
        uint currentHealth = FellowshipReadU32(payload, ref pos);
        uint currentStamina = FellowshipReadU32(payload, ref pos);
        uint currentMana = FellowshipReadU32(payload, ref pos);
        uint shareLoot = FellowshipReadU32(payload, ref pos); // RAW — D5/lane B §4.1: != 0, NEVER == 1
        string name = ReadString16L(payload, ref pos);
        return new FellowMember(
            guid, cpCache, lumCache, level, maxHealth, maxStamina, maxMana,
            currentHealth, currentStamina, currentMana, shareLoot, name);
    }

    private static uint FellowshipReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }

    private static ushort FellowshipReadU16(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated u16");
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        return value;
    }


    public readonly record struct AllegianceLoginNotification(uint CharacterGuid, bool IsLoggedIn);

    public static AllegianceLoginNotification? ParseAllegianceLoginNotification(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) return null;
        return new AllegianceLoginNotification(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)) != 0u);
    }

    public static uint? ParseAllegianceUpdateDone(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static uint? ParseAllegianceUpdateAborted(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    // ── House ────────────────────────────────────────────────────────────────

    public readonly record struct HouseUpdateRestrictions(
        byte Sequence,
        uint SenderId,
        HouseRestrictionRecord Restrictions);

    public static HouseUpdateRestrictions? ParseHouseUpdateRestrictions(ReadOnlySpan<byte> payload)
    {
        // Sequence(1) + SenderId(4) + RestrictionDB{Version(4)+Flags(4)+MonarchId(4)+PHashTable-header(4)} = 21
        if (payload.Length < 21) return null;
        int pos = 0;
        byte sequence = payload[pos]; pos += 1;
        uint senderId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;

        pos += 4;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
        uint allegianceMonarchId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;

        uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
        uint entryCount = packedSize & 0xFFFFFFu;
        long entryBytes = (long)entryCount * 8;
        if (payload.Length - pos < entryBytes) return null;

        var guests = new Dictionary<uint, uint>((int)entryCount);
        for (uint i = 0; i < entryCount; i++)
        {
            uint guestId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint permission = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            guests[guestId] = permission;
        }

        return new HouseUpdateRestrictions(
            sequence,
            senderId,
            new HouseRestrictionRecord(
                OpenToPublic: flags != 0,
                AllegianceMonarchId: allegianceMonarchId,
                Guests: guests));
    }


    public readonly record struct HousePayment(
        int Num, int Paid, uint WeenieID, string Name, string PluralName);

    public readonly record struct HouseData(
        uint BuyTime,
        uint RentTime,
        uint Type,
        bool MaintenanceFree,
        IReadOnlyList<HousePayment> Buy,
        IReadOnlyList<HousePayment> Rent,
        CreateObject.ServerPosition Position);

    public static HouseData? ParseHouseData(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            if (payload.Length - pos < 16) return null;
            uint buyTime = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint rentTime = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            bool maintenanceFree = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)) != 0; pos += 4;

            List<HousePayment>? buy = ReadHousePaymentList(payload, ref pos);
            if (buy is null) return null;
            List<HousePayment>? rent = ReadHousePaymentList(payload, ref pos);
            if (rent is null) return null;

            if (payload.Length - pos < 32) return null;
            var position = new CreateObject.ServerPosition(
                LandblockId: BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos + 0)),
                PositionX:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 4)),
                PositionY:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 8)),
                PositionZ:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 12)),
                RotationW:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 16)),
                RotationX:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 20)),
                RotationY:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 24)),
                RotationZ:   BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(pos + 28)));

            return new HouseData(buyTime, rentTime, type, maintenanceFree, buy, rent, position);
        }
        catch { return null; }
    }

    private static List<HousePayment>? ReadHousePaymentList(ReadOnlySpan<byte> payload, ref int pos)
    {
        if (payload.Length - pos < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
        var list = new List<HousePayment>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            if (payload.Length - pos < 8) return null;
            int num = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos)); pos += 4;
            int paid = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(pos)); pos += 4;
            if (payload.Length - pos < 4) return null;
            uint weenieId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos)); pos += 4;
            string name = ReadString16L(payload, ref pos);
            string pluralName = ReadString16L(payload, ref pos);
            list.Add(new HousePayment(num, paid, weenieId, name, pluralName));
        }
        return list;
    }

    public static uint? ParseHouseStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static uint? ParseUpdateRentTime(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static IReadOnlyList<HousePayment>? ParseUpdateRentPayment(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        return ReadHousePaymentList(payload, ref pos);
    }

    // ── Shared string reader (matches LoginRequest.ReadString16L) ───────────

    private static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = Encoding.GetEncoding(1252).GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }
}
