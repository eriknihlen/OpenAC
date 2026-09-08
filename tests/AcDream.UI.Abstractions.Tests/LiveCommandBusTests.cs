namespace AcDream.UI.Abstractions.Tests;

public sealed class LiveCommandBusTests
{
    private sealed record FakeCmd(int Value);
    private sealed record OtherCmd(string Tag);

    [Fact]
    public void Publish_InvokesRegisteredHandler_ForMatchingType()
    {
        var bus = new LiveCommandBus();
        FakeCmd? captured = null;
        bus.Register<FakeCmd>(c => captured = c);

        bus.Publish(new FakeCmd(42));

        Assert.NotNull(captured);
        Assert.Equal(42, captured!.Value);
    }

    [Fact]
    public void Publish_DoesNotThrow_WhenNoHandlerRegistered()
    {
        var bus = new LiveCommandBus();

        // No handler for OtherCmd — must not throw.
        bus.Publish(new OtherCmd("x"));
    }

    [Fact]
    public void Register_Twice_ForSameType_Throws()
    {
        var bus = new LiveCommandBus();
        bus.Register<FakeCmd>(_ => { });

        Assert.Throws<InvalidOperationException>(() => bus.Register<FakeCmd>(_ => { }));
    }

    [Fact]
    public void Clear_ReleasesHandlersAndMakesExistingBusInert()
    {
        var bus = new LiveCommandBus();
        int calls = 0;
        bus.Register<FakeCmd>(_ => calls++);
        bus.Publish(new FakeCmd(1));

        bus.Clear();
        bus.Publish(new FakeCmd(2));

        Assert.Equal(1, calls);
    }
}

public sealed class ChannelResolverTests
{
    [Fact]
    public void Resolve_LegacyChannels_MatchesHoltburgerIds()
    {
        Assert.Equal((0x00000800u, "Fellowship"), AsTuple(ChannelResolver.Resolve(ChatChannelKind.Fellowship)));
        Assert.Equal((0x02000000u, "Allegiance"), AsTuple(ChannelResolver.Resolve(ChatChannelKind.AllegianceBroadcast)));
        Assert.Equal((0x00001000u, "Vassals"),    AsTuple(ChannelResolver.Resolve(ChatChannelKind.Vassals)));
        Assert.Equal((0x00002000u, "Patron"),     AsTuple(ChannelResolver.Resolve(ChatChannelKind.Patron)));
        Assert.Equal((0x00004000u, "Monarch"),    AsTuple(ChannelResolver.Resolve(ChatChannelKind.Monarch)));
        Assert.Equal((0x01000000u, "CoVassals"),  AsTuple(ChannelResolver.Resolve(ChatChannelKind.CoVassals)));
    }

    [Fact]
    public void Resolve_NonLegacyChannels_ReturnsNull()
    {
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.Say));
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.Tell));
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.General));
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.Trade));
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.Allegiance));
        Assert.Null(ChannelResolver.Resolve(ChatChannelKind.Unknown));
    }

    private static (uint ChannelId, string DisplayName) AsTuple(ChannelResolver.Resolved? r)
    {
        Assert.NotNull(r);
        return (r!.Value.ChannelId, r.Value.DisplayName);
    }
}
