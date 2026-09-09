using System.Buffers.Binary;
using AcDream.Core.Social;

namespace AcDream.Core.Net.Messages;

public static class SocialStateMessages
{
    public static FriendsUpdate? ParseFriendsUpdate(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            uint count = ReadU32(payload, ref pos);
            if (count > 65_536) return null;
            var entries = new List<FriendEntry>((int)count);
            for (uint i = 0; i < count; i++)
            {
                uint id = ReadU32(payload, ref pos);
                bool online = ReadU32(payload, ref pos) != 0;
                bool appearOffline = ReadU32(payload, ref pos) != 0;
                string name = StringReader.ReadString16L(payload, ref pos);
                IReadOnlyList<uint> friends = ReadUInt32List(payload, ref pos);
                IReadOnlyList<uint> friendOf = ReadUInt32List(payload, ref pos);
                entries.Add(new FriendEntry(id, name, online, appearOffline, friends, friendOf));
            }

            FriendsUpdateType type = (FriendsUpdateType)ReadU32(payload, ref pos);
            return Enum.IsDefined(type) ? new FriendsUpdate(type, entries) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static SquelchDatabase? ParseSquelchDatabase(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        try
        {
            IReadOnlyDictionary<string, uint> accounts = ReadAccountTable(payload, ref pos);
            IReadOnlyDictionary<uint, SquelchInfo> characters = ReadCharacterTable(payload, ref pos);
            SquelchInfo global = ReadSquelchInfo(payload, ref pos);
            return pos <= payload.Length
                ? new SquelchDatabase(accounts, characters, global)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, uint> ReadAccountTable(
        ReadOnlySpan<byte> source, ref int pos)
    {
        (ushort count, ushort buckets) = ReadHashHeader(source, ref pos);
        if (buckets == 0 && count != 0)
            throw new FormatException("invalid account squelch table");
        var result = new Dictionary<string, uint>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string name = StringReader.ReadString16L(source, ref pos);
            result[name] = ReadU32(source, ref pos);
        }
        return result;
    }

    private static IReadOnlyDictionary<uint, SquelchInfo> ReadCharacterTable(
        ReadOnlySpan<byte> source, ref int pos)
    {
        (ushort count, ushort buckets) = ReadHashHeader(source, ref pos);
        if (buckets == 0 && count != 0)
            throw new FormatException("invalid character squelch table");
        var result = new Dictionary<uint, SquelchInfo>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = ReadU32(source, ref pos);
            result[id] = ReadSquelchInfo(source, ref pos);
        }
        return result;
    }

    private static SquelchInfo ReadSquelchInfo(ReadOnlySpan<byte> source, ref int pos)
    {
        uint wordCount = ReadU32(source, ref pos);
        if (wordCount > 4) throw new FormatException("invalid vlong word count");
        var messageTypes = new HashSet<uint>();
        for (uint word = 0; word < wordCount; word++)
        {
            uint bits = ReadU32(source, ref pos);
            for (uint bit = 0; bit < 32; bit++)
            {
                if ((bits & (1u << (int)bit)) != 0)
                    messageTypes.Add(word * 32 + bit);
            }
        }

        string name = StringReader.ReadString16L(source, ref pos);
        bool accountWide = ReadU32(source, ref pos) != 0;
        return new SquelchInfo(name, accountWide, messageTypes);
    }

    private static (ushort Count, ushort Buckets) ReadHashHeader(
        ReadOnlySpan<byte> source, ref int pos)
    {
        uint header = ReadU32(source, ref pos);
        return ((ushort)(header & 0xFFFFu), (ushort)(header >> 16));
    }

    private static IReadOnlyList<uint> ReadUInt32List(ReadOnlySpan<byte> source, ref int pos)
    {
        uint count = ReadU32(source, ref pos);
        if (count > 10_000) throw new FormatException("invalid packed list count");
        var result = new uint[count];
        for (int i = 0; i < result.Length; i++) result[i] = ReadU32(source, ref pos);
        return result;
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos, 4));
        pos += 4;
        return value;
    }
}
