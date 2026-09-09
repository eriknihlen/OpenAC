using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AcDream.Core.Ui;

namespace AcDream.Core.Net.Messages;

public static class ClientCommandResponses
{

    public static IReadOnlyList<string>? ParseChannelIndex(ReadOnlySpan<byte> payload) =>
        ParseStringList(payload);

    public static IReadOnlyList<string>? ParseChannelList(ReadOnlySpan<byte> payload) =>
        ParseStringList(payload);

    private static IReadOnlyList<string>? ParseStringList(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint count = ReadU32(payload, ref pos);
            var list = new List<string>();
            for (uint i = 0; i < count; i++)
                list.Add(StringReader.ReadString16L(payload, ref pos));
            return list;
        }
        catch (FormatException) { return null; }
    }

    public static IEnumerable<string> FormatChannelIndexLines(IReadOnlyList<string> channels)
    {
        yield return "The following channels are available to you:";
        foreach (string channel in channels)
            yield return channel;
    }

    public static IEnumerable<string> FormatChannelListLines(IReadOnlyList<string> names)
    {
        yield return "The following characters are currently listening on the channel:";
        foreach (string name in names)
            yield return name;
    }


    public readonly record struct AvailableHousesResponse(
        uint HouseType,
        IReadOnlyList<uint> Locations,
        int TotalAvailable);

    public static AvailableHousesResponse? ParseAvailableHouses(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint houseType = ReadU32(payload, ref pos);
            uint count = ReadU32(payload, ref pos);
            var locations = new uint[count];
            for (uint i = 0; i < count; i++)
                locations[i] = ReadU32(payload, ref pos);
            int totalAvailable = unchecked((int)ReadU32(payload, ref pos));
            return new AvailableHousesResponse(houseType, locations, totalAvailable);
        }
        catch (FormatException) { return null; }
    }

    private static string HouseTypeName(uint houseType) => houseType switch
    {
        1u => "cottages",
        2u => "villas",
        3u => "mansions",
        4u => "apartments",
        _ => "",
    };

    public static IEnumerable<string> FormatAvailableHousesLines(AvailableHousesResponse response)
    {
        yield return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"There are {response.TotalAvailable} {HouseTypeName(response.HouseType)} available.");

        if (response.HouseType != 4u)
        {
            foreach (uint landblockId in response.Locations)
            {
                if (RadarCoordinates.TryFromCell(landblockId, out var coordinates))
                    yield return $"     {coordinates.YText}, {coordinates.XText}";
            }

            if (response.TotalAvailable > 0x190)
                yield return "There were too many houses to display all the locations. Only the first 400 locations are displayed here.";
        }
    }


    public readonly record struct AllegianceMemberRecord(
        uint CharacterId,
        uint ParentGuid,
        bool IsLoggedIn,
        string Name,
        ushort Rank = 0,
        uint Level = 0,
        ushort Loyalty = 0,
        ushort Leadership = 0,
        uint CpCached = 0,
        uint CpTithed = 0,
        byte Gender = 0,
        byte HeritageGroup = 0,
        bool MayPassupExperience = false);

    private const uint LoggedInBit = 0x1u;
    private const uint HasAllegianceAgeBit = 0x4u;
    private const uint HasPackedLevelBit = 0x8u;
    private const uint MayPassupExperienceBit = 0x10u;

    private readonly record struct AllegianceProfileBody(
        uint TotalMembers,
        uint TotalVassals,
        ushort RecordCount,
        ushort OldVersion,
        string Motd,
        string MotdSetBy,
        uint ChatRoomId,
        string AllegianceName,
        uint NameLastSetTime,
        bool IsLocked,
        uint ApprovedVassal,
        AllegianceMemberRecord? Monarch,
        IReadOnlyList<AllegianceMemberRecord> Records);

    internal static class AllegianceProfileLookups
    {
        public static AllegianceMemberRecord? FindData(
            AllegianceMemberRecord? monarch,
            IReadOnlyList<AllegianceMemberRecord> records,
            uint guid)
        {
            if (monarch is { } m && m.CharacterId == guid) return monarch;
            foreach (AllegianceMemberRecord record in records)
                if (record.CharacterId == guid) return record;
            return null;
        }

        public static AllegianceMemberRecord? FindPatron(
            AllegianceMemberRecord? monarch,
            IReadOnlyList<AllegianceMemberRecord> records,
            uint guid)
        {
            if (monarch is { } m && m.CharacterId == guid) return null;
            foreach (AllegianceMemberRecord record in records)
                if (record.CharacterId == guid) return FindData(monarch, records, record.ParentGuid);
            return null;
        }

        public static IEnumerable<AllegianceMemberRecord> FindVassals(
            IReadOnlyList<AllegianceMemberRecord> records, uint guid)
        {
            for (int i = records.Count - 1; i >= 0; i--)
                if (records[i].ParentGuid == guid)
                    yield return records[i];
        }
    }

    public readonly record struct AllegianceInfoResponse(
        uint TargetGuid,
        uint TotalMembers,
        uint TotalVassals,
        ushort RecordCount,
        string AllegianceName,
        AllegianceMemberRecord? Monarch,
        IReadOnlyList<AllegianceMemberRecord> Records,
        ushort OldVersion = 0,
        string Motd = "",
        string MotdSetBy = "",
        uint ChatRoomId = 0,
        uint NameLastSetTime = 0,
        bool IsLocked = false,
        uint ApprovedVassal = 0)
    {
        public AllegianceMemberRecord? FindData(uint guid) =>
            AllegianceProfileLookups.FindData(Monarch, Records, guid);

        public AllegianceMemberRecord? FindPatron(uint guid) =>
            AllegianceProfileLookups.FindPatron(Monarch, Records, guid);

        public IEnumerable<AllegianceMemberRecord> FindVassals(uint guid) =>
            AllegianceProfileLookups.FindVassals(Records, guid);
    }

    public static AllegianceInfoResponse? ParseAllegianceInfoResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint targetGuid = ReadU32(payload, ref pos);
            AllegianceProfileBody? body = ReadAllegianceProfileBody(payload, ref pos);
            if (body is null) return null;
            AllegianceProfileBody b = body.Value;
            return new AllegianceInfoResponse(
                targetGuid, b.TotalMembers, b.TotalVassals, b.RecordCount,
                b.AllegianceName, b.Monarch, b.Records,
                b.OldVersion, b.Motd, b.MotdSetBy, b.ChatRoomId, b.NameLastSetTime,
                b.IsLocked, b.ApprovedVassal);
        }
        catch (FormatException) { return null; }
    }

    public readonly record struct AllegianceUpdate(
        uint Rank,
        uint TotalMembers,
        uint TotalVassals,
        ushort RecordCount,
        string AllegianceName,
        AllegianceMemberRecord? Monarch,
        IReadOnlyList<AllegianceMemberRecord> Records,
        ushort OldVersion = 0,
        string Motd = "",
        string MotdSetBy = "",
        uint ChatRoomId = 0,
        uint NameLastSetTime = 0,
        bool IsLocked = false,
        uint ApprovedVassal = 0)
    {
        public AllegianceMemberRecord? FindData(uint guid) =>
            AllegianceProfileLookups.FindData(Monarch, Records, guid);

        public AllegianceMemberRecord? FindPatron(uint guid) =>
            AllegianceProfileLookups.FindPatron(Monarch, Records, guid);

        public IEnumerable<AllegianceMemberRecord> FindVassals(uint guid) =>
            AllegianceProfileLookups.FindVassals(Records, guid);
    }

    public static AllegianceUpdate? ParseAllegianceUpdate(ReadOnlySpan<byte> payload)
    {
        try
        {
            int pos = 0;
            uint rank = ReadU32(payload, ref pos);
            AllegianceProfileBody? body = ReadAllegianceProfileBody(payload, ref pos);
            if (body is null) return null;
            AllegianceProfileBody b = body.Value;
            return new AllegianceUpdate(
                rank, b.TotalMembers, b.TotalVassals, b.RecordCount,
                b.AllegianceName, b.Monarch, b.Records,
                b.OldVersion, b.Motd, b.MotdSetBy, b.ChatRoomId, b.NameLastSetTime,
                b.IsLocked, b.ApprovedVassal);
        }
        catch (FormatException) { return null; }
    }

    private static AllegianceProfileBody? ReadAllegianceProfileBody(ReadOnlySpan<byte> payload, ref int pos)
    {
        uint totalMembers = ReadU32(payload, ref pos);
        uint totalVassals = ReadU32(payload, ref pos);
        ushort recordCount = ReadU16(payload, ref pos);
        ushort oldVersion = ReadU16(payload, ref pos);


        if (oldVersion >= 6)
        {
            ushort officerCount = ReadU16(payload, ref pos);
            _ = ReadU16(payload, ref pos);
            for (int i = 0; i < officerCount; i++)
            {
                _ = ReadU32(payload, ref pos); // guid
                _ = ReadU32(payload, ref pos); // officer level
            }
        }
        else if (oldVersion >= 1)
        {
            _ = ReadU32(payload, ref pos); // old single spokesperson id
        }

        if (oldVersion >= 9)
        {
            int titleCount = unchecked((int)ReadU32(payload, ref pos));
            for (int i = 0; i < titleCount; i++)
                _ = StringReader.ReadString16L(payload, ref pos);
        }

        if (oldVersion >= 2)
        {
            _ = ReadU32(payload, ref pos); // monarchBroadcastTime
            _ = ReadU32(payload, ref pos); // monarchBroadcastsToday
            _ = ReadU32(payload, ref pos); // spokesBroadcastTime
            _ = ReadU32(payload, ref pos); // spokesBroadcastsToday
        }

        // Gate 3 (MotdAdded, oldVersion >= 3).
        string motd = "";
        string motdSetBy = "";
        if (oldVersion >= 3)
        {
            motd = StringReader.ReadString16L(payload, ref pos);
            motdSetBy = StringReader.ReadString16L(payload, ref pos);
        }

        uint chatRoomId = 0;
        if (oldVersion >= 4)
            chatRoomId = ReadU32(payload, ref pos);

        if (oldVersion >= 7)
        {
            for (int i = 0; i < 8; i++)
                _ = ReadU32(payload, ref pos);
        }

        // Gate 8 (AllegianceName, oldVersion >= 8).
        string allegianceName = "";
        uint nameLastSetTime = 0;
        if (oldVersion >= 8)
        {
            allegianceName = StringReader.ReadString16L(payload, ref pos);
            nameLastSetTime = ReadU32(payload, ref pos);
        }

        // Gate 10 (LockedState, oldVersion >= 10).
        bool isLocked = false;
        if (oldVersion >= 10)
            isLocked = ReadU32(payload, ref pos) != 0u;

        // Gate 11 (ApprovedVassal, oldVersion >= 11).
        uint approvedVassal = 0;
        if (oldVersion >= 11)
            approvedVassal = ReadU32(payload, ref pos);

        AllegianceMemberRecord? monarch = null;
        var records = new List<AllegianceMemberRecord>();
        if (recordCount > 0)
        {
            AllegianceMemberRecord monarchRecord = ReadAllegianceData(payload, ref pos, parentGuid: 0u);
            if (monarchRecord.CharacterId == 0u)
                return null;
            monarch = monarchRecord;
            var knownIds = new HashSet<uint> { monarchRecord.CharacterId };

            for (int i = 1; i < recordCount; i++)
            {
                uint parentGuid = ReadU32(payload, ref pos);
                AllegianceMemberRecord record = ReadAllegianceData(payload, ref pos, parentGuid);

                if (record.CharacterId == 0u
                    || !knownIds.Contains(parentGuid)
                    || parentGuid == record.CharacterId
                    || knownIds.Contains(record.CharacterId))
                {
                    return null;
                }

                knownIds.Add(record.CharacterId);
                records.Add(record);
            }

            monarch = monarch.Value with { MayPassupExperience = false };
        }

        return new AllegianceProfileBody(
            totalMembers, totalVassals, recordCount, oldVersion,
            motd, motdSetBy, chatRoomId, allegianceName, nameLastSetTime,
            isLocked, approvedVassal, monarch, records);
    }

    private static AllegianceMemberRecord ReadAllegianceData(
        ReadOnlySpan<byte> payload, ref int pos, uint parentGuid)
    {
        uint characterId = ReadU32(payload, ref pos);
        uint cpCached = ReadU32(payload, ref pos);
        uint cpTithed = ReadU32(payload, ref pos);
        uint bitfield = ReadU32(payload, ref pos);
        byte gender = ReadByte(payload, ref pos);
        byte heritageGroup = ReadByte(payload, ref pos);
        ushort rank = ReadU16(payload, ref pos);
        uint level = 0;
        if ((bitfield & HasPackedLevelBit) != 0u)
            level = ReadU32(payload, ref pos);
        ushort loyalty = ReadU16(payload, ref pos);
        ushort leadership = ReadU16(payload, ref pos);
        if ((bitfield & HasAllegianceAgeBit) != 0u)
        {
            _ = ReadU32(payload, ref pos);
            _ = ReadU32(payload, ref pos); // allegianceAge — same
        }
        else
        {
            _ = ReadU32(payload, ref pos); // legacy uTimeOnline low (double, pre-HasAllegianceAge)
            _ = ReadU32(payload, ref pos); // legacy uTimeOnline high
        }
        string name = StringReader.ReadString16L(payload, ref pos);

        bool mayPassupExperience = (bitfield & MayPassupExperienceBit) != 0u
            || (bitfield & HasPackedLevelBit) == 0u;

        return new AllegianceMemberRecord(
            characterId, parentGuid, (bitfield & LoggedInBit) != 0u, name,
            rank, level, loyalty, leadership, cpCached, cpTithed,
            gender, heritageGroup, mayPassupExperience);
    }

    private static string OnlineMarker(AllegianceMemberRecord member) =>
        member.IsLoggedIn ? " *" : "";

    public static IEnumerable<string> FormatAllegianceInfoLines(AllegianceInfoResponse response)
    {
        AllegianceMemberRecord? self = response.FindData(response.TargetGuid);
        if (self is not { } selfRecord)
            yield break;

        yield return "Note: An asterisk (*) indicates that the character is currently online.";
        yield return $"Allegiance information for {selfRecord.Name}{OnlineMarker(selfRecord)}:";

        if (response.FindPatron(response.TargetGuid) is { } patron)
            yield return $"   Patron: {patron.Name}{OnlineMarker(patron)}";

        bool wroteVassalHeader = false;
        foreach (AllegianceMemberRecord vassal in response.FindVassals(response.TargetGuid))
        {
            if (!wroteVassalHeader)
            {
                yield return "   Vassals: ";
                wroteVassalHeader = true;
            }
            yield return $"      {vassal.Name}{OnlineMarker(vassal)}";
        }
    }

    // ── Shared primitive readers (throw on truncation, like StringReader) ──

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return value;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated u16");
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 1) throw new FormatException("truncated byte");
        byte value = source[pos];
        pos += 1;
        return value;
    }
}
