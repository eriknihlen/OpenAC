using AcDream.Core.Social;

namespace AcDream.Core.Tests.Social;

public sealed class SocialStateTests
{
    [Fact]
    public void SquelchState_Clear_ReturnsEmptyDatabase()
    {
        var state = new SquelchState();
        state.Replace(new SquelchDatabase(
            new Dictionary<string, uint> { ["Account"] = 1u },
            new Dictionary<uint, SquelchInfo>
            {
                [2u] = new("Character", false, new HashSet<uint> { 3u }),
            },
            new SquelchInfo("Global", true, new HashSet<uint> { 4u })));

        state.Clear();

        SquelchDatabase snapshot = state.Snapshot();
        Assert.Empty(snapshot.Accounts);
        Assert.Empty(snapshot.Characters);
        Assert.Empty(snapshot.Global.MessageTypes);
        Assert.Equal(string.Empty, snapshot.Global.Name);
        Assert.False(snapshot.Global.AccountWide);
        state.Clear();
        Assert.Empty(state.Snapshot().Accounts);
    }

    [Fact]
    public void FriendsState_AppliesRetailIncrementalUpdateKindsByObjectId()
    {
        var state = new FriendsState();
        state.Apply(Update(FriendsUpdateType.Full,
            Friend(1u, "Alice", online: false),
            Friend(2u, "Bjørn", online: true)));
        state.Apply(Update(FriendsUpdateType.OnlineStatus,
            Friend(1u, "Alice", online: true)));
        state.Apply(Update(FriendsUpdateType.Add,
            Friend(3u, "Cara", online: true)));
        state.Apply(Update(FriendsUpdateType.RemoveSilent,
            Friend(2u, "Bjørn", online: false)));

        Assert.Collection(
            state.Snapshot(),
            alice =>
            {
                Assert.Equal(1u, alice.Id);
                Assert.True(alice.Online);
            },
            cara => Assert.Equal(3u, cara.Id));
    }

    [Fact]
    public void SquelchState_ReplacesDatabaseAtomically()
    {
        var state = new SquelchState();
        var expected = new SquelchDatabase(
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["Account"] = 7u,
            },
            new Dictionary<uint, SquelchInfo>
            {
                [42u] = new("Character", false, new HashSet<uint> { 2u }),
            },
            new SquelchInfo(string.Empty, false, new HashSet<uint> { 7u }));

        state.Replace(expected);

        Assert.Same(expected, state.Snapshot());
    }

    private static FriendEntry Friend(uint id, string name, bool online) =>
        new(id, name, online, AppearOffline: false, [], []);

    private static FriendsUpdate Update(
        FriendsUpdateType type, params FriendEntry[] entries) =>
        new(type, entries);
}
