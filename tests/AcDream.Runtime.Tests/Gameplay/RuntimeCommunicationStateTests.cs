using System.Globalization;
using System.Threading;
using AcDream.Core.Chat;
using AcDream.Core.Social;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCommunicationStateTests
{
    [Fact]
    public void SocialViewBorrowsFriendsAndSquelchOwners()
    {
        using var state = new RuntimeCommunicationState();
        state.Friends.Apply(new AcDream.Core.Social.FriendsUpdate(
            AcDream.Core.Social.FriendsUpdateType.Full,
            [
                new AcDream.Core.Social.FriendEntry(
                    0x50000001u,
                    "Friend",
                    Online: true,
                    AppearOffline: false,
                    Friends: [],
                    FriendOf: []),
            ]));
        state.Squelch.Replace(new AcDream.Core.Social.SquelchDatabase(
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = 1u,
            },
            new Dictionary<uint, AcDream.Core.Social.SquelchInfo>(),
            new AcDream.Core.Social.SquelchInfo(
                string.Empty,
                false,
                new HashSet<uint> { 3u })));

        RuntimeSocialSnapshot snapshot = state.SocialView.Snapshot;

        Assert.Equal(1, snapshot.FriendCount);
        Assert.Equal(1, snapshot.SquelchedAccountCount);
        Assert.Equal(1, snapshot.GlobalSquelchTypeCount);
        Assert.True(state.SocialView.TryGetFriend(
            0x50000001u,
            out RuntimeFriendSnapshot friend));
        Assert.Equal("Friend", friend.Name);
        Assert.True(friend.Online);
    }

    [Fact]
    public void OwnsOneExactCommunicationGraphAndPublishesCommittedEntries()
    {
        using var state = new RuntimeCommunicationState();
        var observer = new RecordingObserver();
        using IDisposable subscription = state.Events.Subscribe(observer);

        state.Chat.OnTellReceived("Bestie", "hello", 0x50000001u, logTextType: 0x03u);

        RuntimeCommunicationEvent delta = Assert.Single(observer.Events);
        Assert.Equal(1UL, delta.Sequence);
        Assert.Equal(state.Chat.Revision, delta.Entry.Revision);
        Assert.Equal(1, state.View.Count);
        Assert.Equal(state.Chat.Revision, state.View.Revision);
        Assert.Equal(1UL, state.LastSequence);
        Assert.Equal(0, state.PendingDispatchCount);
        Assert.False(state.IsDispatching);
        Assert.Equal("Bestie", state.CommandTargets.LastIncomingTellSender);
        Assert.Equal("hello", delta.Entry.Text);
    }

    [Fact]
    public void ReentrantAppendPreservesSequenceForEveryObserver()
    {
        using var state = new RuntimeCommunicationState();
        var reentrant = new ReentrantObserver(state.Chat);
        var trailing = new RecordingObserver();
        using IDisposable first = state.Events.Subscribe(reentrant);
        using IDisposable second = state.Events.Subscribe(trailing);

        state.Chat.OnSystemMessage("first", 0u);

        Assert.Equal([1UL, 2UL], reentrant.Sequences);
        Assert.Equal(
            [1UL, 2UL],
            trailing.Events.Select(item => item.Sequence).ToArray());
        Assert.Equal(["first", "second"], trailing.Events
            .Select(item => item.Entry.Text)
            .ToArray());
        Assert.Equal(0, state.PendingDispatchCount);
        Assert.False(state.IsDispatching);
    }

    [Fact]
    public void ObserverFailureDoesNotStarveLaterObserverOrOwner()
    {
        using var state = new RuntimeCommunicationState();
        var recording = new RecordingObserver();
        using IDisposable throwing =
            state.Events.Subscribe(new ThrowingObserver());
        using IDisposable trailing = state.Events.Subscribe(recording);

        state.Chat.OnSystemMessage("still committed", 0u);

        Assert.Equal(1, state.Chat.Count);
        Assert.Single(recording.Events);
        Assert.Equal(1, state.DispatchFailureCount);
        Assert.IsType<InvalidOperationException>(state.LastDispatchFailure);
    }

    [Fact]
    public void SessionResetsClearScopedStateWithoutClearingTranscript()
    {
        using var state = new RuntimeCommunicationState();
        state.Chat.OnTellReceived("Bestie", "hello", 0x50000001u, logTextType: 0x03u);
        state.Chat.OnSelfSent(ChatKind.Tell, "outgoing", logTextType: 0x04u, targetOrChannel: "Caith");
        state.TurbineChat.OnChannelsReceived(
            1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u);
        state.Friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Full,
            [new FriendEntry(1u, "Friend", true, false, [], [])]));
        state.Squelch.Replace(new SquelchDatabase(
            new Dictionary<string, uint> { ["account"] = 1u },
            new Dictionary<uint, SquelchInfo>(),
            new SquelchInfo(string.Empty, false, new HashSet<uint>())));

        state.ResetCommandTargets();
        state.ResetChatIdentity();
        state.ResetNegotiatedChannels();
        state.ResetFriends();
        state.ResetSquelch();

        Assert.Equal(2, state.Chat.Count);
        Assert.Null(state.CommandTargets.LastIncomingTellSender);
        Assert.Null(state.CommandTargets.LastOutgoingTellTarget);
        Assert.False(state.TurbineChat.Enabled);
        Assert.Empty(state.Friends.Snapshot());
        Assert.Empty(state.Squelch.Snapshot().Accounts);
    }

    [Fact]
    public void DisposeDetachesStreamAndAllBorrowedState()
    {
        var state = new RuntimeCommunicationState();
        var observer = new RecordingObserver();
        IDisposable subscription = state.Events.Subscribe(observer);

        state.Dispose();
        state.Dispose();
        state.Chat.OnSystemMessage("after dispose", 0u);

        Assert.True(state.IsDisposed);
        Assert.Equal(0, state.SubscriberCount);
        Assert.Empty(observer.Events);
        Assert.Throws<ObjectDisposedException>(
            () => state.Events.Subscribe(new RecordingObserver()));
        subscription.Dispose();
    }

    [Fact]
    public void IndependentRuntimeInstancesNeverShareState()
    {
        using var first = new RuntimeCommunicationState();
        using var second = new RuntimeCommunicationState();

        first.Chat.OnTellReceived("OnlyFirst", "hello", 0x50000001u, logTextType: 0x03u);

        Assert.Equal(1, first.View.Count);
        Assert.Equal(0, second.View.Count);
        Assert.Equal("OnlyFirst", first.CommandTargets.LastIncomingTellSender);
        Assert.Null(second.CommandTargets.LastIncomingTellSender);
    }


    [Fact]
    public void AddText_ClientLocal_RoutesToSpewBoxOnly_NeverChatTranscript()
    {
        using var state = new RuntimeCommunicationState();

        state.AddText("You can't jump while in the air", RetailLogTextType.ClientLocal);

        Assert.Equal(0, state.Chat.Count);
        state.SpewBox.Tick(0d);
        Assert.Equal(1, state.SpewBox.Count);
        Assert.Equal("You can't jump while in the air", state.SpewBox.Snapshot()[0].Text);
    }

    [Theory]
    [InlineData(RetailLogTextType.Default)]
    [InlineData(RetailLogTextType.Magic)]
    [InlineData(RetailLogTextType.System)]
    public void AddText_NonClientLocalTypes_RouteToChatOnly_NeverSpewBox(RetailLogTextType type)
    {
        using var state = new RuntimeCommunicationState();

        state.AddText("Your spell fizzled.", type);

        Assert.Equal(1, state.Chat.Count);
        Assert.Equal("Your spell fizzled.", state.Chat.Snapshot()[0].Text);
        Assert.Equal((uint)type, state.Chat.Snapshot()[0].LogTextType);
        state.SpewBox.Tick(0d);
        Assert.Equal(0, state.SpewBox.Count);
    }

    [Fact]
    public void AddText_TrimsBothEnds_LikeRetailAddTextToScroll()
    {
        using var state = new RuntimeCommunicationState();

        state.AddText("   Out of Range!   ", RetailLogTextType.ClientLocal);

        state.SpewBox.Tick(0d);
        Assert.Equal("Out of Range!", state.SpewBox.Snapshot()[0].Text);
    }

    [Fact]
    public void AddText_EmptyAfterTrim_StillBroadcasts_LikeRetail()
    {
        using var state = new RuntimeCommunicationState();

        state.AddText("   ", RetailLogTextType.ClientLocal);
        state.AddText("   ", RetailLogTextType.Default);

        state.SpewBox.Tick(0d);
        Assert.Equal(1, state.SpewBox.Count);
        Assert.Equal("", state.SpewBox.Snapshot()[0].Text);
        Assert.Equal(1, state.Chat.Count);
        Assert.Equal("", state.Chat.Snapshot()[0].Text);
    }


    [Fact]
    public void AddText_TimestampsUnbound_NoPrefix()
    {
        using var state = new RuntimeCommunicationState();

        state.AddText("Your spell fizzled.", RetailLogTextType.Default);

        Assert.Equal("Your spell fizzled.", state.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void AddText_TimestampsFalse_NoPrefix()
    {
        using var state = new RuntimeCommunicationState { DisplayTimestampsSource = () => false };

        state.AddText("Your spell fizzled.", RetailLogTextType.Default);

        Assert.Equal("Your spell fizzled.", state.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void AddText_TimestampsTrue_StoredBodyStaysClean()
    {
        using var state = new RuntimeCommunicationState { DisplayTimestampsSource = () => true };

        state.AddText("Your spell fizzled.", RetailLogTextType.Default);

        Assert.Equal("Your spell fizzled.", state.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void DisplayTimestampsSource_ForwardsToChat_SoTheDisplaySeamSeesOneSource()
    {
        using var state = new RuntimeCommunicationState { DisplayTimestampsSource = () => true };

        Assert.NotNull(state.Chat.DisplayTimestampsSource);
        Assert.True(state.Chat.DisplayTimestampsSource!());

        state.Chat.OnLocalSpeech("Alice", "hi", 0xAAu, isRanged: false, logTextType: 0x02u);
        Assert.Equal("hi", state.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void AddText_TimestampsTrue_NeverAppliedToClientLocalSpewBox()
    {
        using var state = new RuntimeCommunicationState { DisplayTimestampsSource = () => true };

        state.AddText("Out of Range!", RetailLogTextType.ClientLocal);

        state.SpewBox.Tick(0d);
        Assert.Equal("Out of Range!", state.SpewBox.Snapshot()[0].Text);
    }

    [Fact]
    public void Dispose_ResetsSpewBox()
    {
        var state = new RuntimeCommunicationState();
        state.AddText("about to be torn down", RetailLogTextType.ClientLocal);
        state.SpewBox.Tick(0d);
        Assert.Equal(1, state.SpewBox.Count);

        state.Dispose();

        Assert.Equal(0, state.SpewBox.Count);
    }


    [Fact]
    public void ChatWindows_SeededWithRetailPostInitDefaults_OnConstruction()
    {
        using var state = new RuntimeCommunicationState();

        Assert.True(state.ChatWindows.IsOpen(0));
        Assert.False(state.ChatWindows.IsOpen(1));
        Assert.Equal(0x0000101Cu, state.ChatWindows.GetFilter(1));
        Assert.Equal(0x00040C00u, state.ChatWindows.GetFilter(2));
        Assert.Equal(0x00080000u, state.ChatWindows.GetFilter(3));
        Assert.Equal(0x78000000u, state.ChatWindows.GetFilter(4));
    }

    [Fact]
    public void Dispose_ResetsChatWindowsToRetailDefaults()
    {
        var state = new RuntimeCommunicationState();
        state.ChatWindows.SetOpen(1, true);
        state.ChatWindows.SetFilter(2, 0u);

        state.Dispose();

        Assert.False(state.ChatWindows.IsOpen(1));
        Assert.Equal(0x00040C00u, state.ChatWindows.GetFilter(2));
    }

    private sealed class RecordingObserver : IRuntimeCommunicationObserver
    {
        public List<RuntimeCommunicationEvent> Events { get; } = [];

        public void OnChat(in RuntimeCommunicationEvent delta) =>
            Events.Add(delta);
    }

    private sealed class ReentrantObserver(ChatLog chat)
        : IRuntimeCommunicationObserver
    {
        public List<ulong> Sequences { get; } = [];

        public void OnChat(in RuntimeCommunicationEvent delta)
        {
            Sequences.Add(delta.Sequence);
            if (delta.Sequence == 1UL)
                chat.OnSystemMessage("second", 1u);
        }
    }

    private sealed class ThrowingObserver : IRuntimeCommunicationObserver
    {
        public void OnChat(in RuntimeCommunicationEvent delta) =>
            throw new InvalidOperationException("observer");
    }
}
