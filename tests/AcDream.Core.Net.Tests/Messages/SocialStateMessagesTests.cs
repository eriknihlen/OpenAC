using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Net.Messages;
using AcDream.Core.Social;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class SocialStateMessagesTests
{
    [Fact]
    public void FriendsUpdate_UnpacksRetailPListAndUpdateType()
    {
        var payload = new List<byte>();
        WriteU32(payload, 2u);
        WriteFriend(payload, 0x50000001u, online: true, appearOffline: false,
            "Alice", [0x50000002u], [0x50000003u, 0x50000004u]);
        WriteFriend(payload, 0x50000002u, online: false, appearOffline: true,
            "Bjørn", [], []);
        WriteU32(payload, (uint)FriendsUpdateType.Full);

        FriendsUpdate? update = SocialStateMessages.ParseFriendsUpdate([.. payload]);

        Assert.NotNull(update);
        Assert.Equal(FriendsUpdateType.Full, update!.Type);
        Assert.Collection(
            update.Entries,
            alice =>
            {
                Assert.Equal(0x50000001u, alice.Id);
                Assert.Equal("Alice", alice.Name);
                Assert.True(alice.Online);
                Assert.False(alice.AppearOffline);
                Assert.Equal([0x50000002u], alice.Friends);
                Assert.Equal([0x50000003u, 0x50000004u], alice.FriendOf);
            },
            bjorn =>
            {
                Assert.Equal("Bjørn", bjorn.Name);
                Assert.False(bjorn.Online);
                Assert.True(bjorn.AppearOffline);
            });
    }

    [Fact]
    public void FriendsUpdate_RejectsTruncatedFriendData()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x50000001u);

        Assert.Null(SocialStateMessages.ParseFriendsUpdate(payload));
    }

    [Fact]
    public void SquelchDatabase_UnpacksRetailHashTablesAndVlongs()
    {
        var payload = new List<byte>();

        WriteHashHeader(payload, count: 1, buckets: 128);
        WriteString16L(payload, "MutedAccount");
        WriteU32(payload, 0x11223344u);

        WriteHashHeader(payload, count: 1, buckets: 128);
        WriteU32(payload, 0x50000001u);
        WriteSquelchInfo(payload, "Muted Character", accountWide: true, 2u, 17u);

        WriteSquelchInfo(payload, string.Empty, accountWide: false, 7u, 19u);

        SquelchDatabase? database =
            SocialStateMessages.ParseSquelchDatabase([.. payload]);

        Assert.NotNull(database);
        Assert.Equal(0x11223344u, database!.Accounts["mutedaccount"]);
        SquelchInfo character = database.Characters[0x50000001u];
        Assert.Equal("Muted Character", character.Name);
        Assert.True(character.AccountWide);
        Assert.Equal([2u, 17u], character.MessageTypes.Order());
        Assert.False(database.Global.AccountWide);
        Assert.Equal([7u, 19u], database.Global.MessageTypes.Order());
    }

    [Fact]
    public void SquelchDatabase_RejectsNonEmptyZeroBucketTable()
    {
        var payload = new List<byte>();
        WriteHashHeader(payload, count: 1, buckets: 0);

        Assert.Null(SocialStateMessages.ParseSquelchDatabase([.. payload]));
    }

    private static void WriteFriend(
        List<byte> destination,
        uint id,
        bool online,
        bool appearOffline,
        string name,
        IReadOnlyList<uint> friends,
        IReadOnlyList<uint> friendOf)
    {
        WriteU32(destination, id);
        WriteU32(destination, online ? 1u : 0u);
        WriteU32(destination, appearOffline ? 1u : 0u);
        WriteString16L(destination, name);
        WriteU32List(destination, friends);
        WriteU32List(destination, friendOf);
    }

    private static void WriteSquelchInfo(
        List<byte> destination,
        string name,
        bool accountWide,
        params uint[] messageTypes)
    {
        uint highest = messageTypes.Length == 0 ? 0u : messageTypes.Max();
        int wordCount = messageTypes.Length == 0 ? 0 : checked((int)(highest / 32u + 1u));
        var words = new uint[wordCount];
        foreach (uint type in messageTypes)
            words[type / 32u] |= 1u << checked((int)(type % 32u));

        WriteU32(destination, (uint)words.Length);
        foreach (uint word in words) WriteU32(destination, word);
        WriteString16L(destination, name);
        WriteU32(destination, accountWide ? 1u : 0u);
    }

    private static void WriteU32List(List<byte> destination, IReadOnlyList<uint> values)
    {
        WriteU32(destination, (uint)values.Count);
        foreach (uint value in values) WriteU32(destination, value);
    }

    private static void WriteHashHeader(List<byte> destination, ushort count, ushort buckets) =>
        WriteU32(destination, ((uint)buckets << 16) | count);

    private static void WriteString16L(List<byte> destination, string value)
    {
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes(value);
        int start = destination.Count;
        destination.Add((byte)bytes.Length);
        destination.Add((byte)(bytes.Length >> 8));
        destination.AddRange(bytes);
        while ((destination.Count - start & 3) != 0) destination.Add(0);
    }

    private static void WriteU32(List<byte> destination, uint value)
    {
        destination.Add((byte)value);
        destination.Add((byte)(value >> 8));
        destination.Add((byte)(value >> 16));
        destination.Add((byte)(value >> 24));
    }
}
