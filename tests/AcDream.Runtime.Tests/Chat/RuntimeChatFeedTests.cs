using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Chat;

public sealed class RuntimeChatFeedTests
{
    private const uint OtherPlayerGuid = 0x5000000Au;

    // -- Wording (the same goldens the chat box is pinned to) -------------

    [Fact]
    public void EachKindIsWordedTheWayTheChatBoxReadsIt()
    {
        static string Word(ChatEntry entry) => RuntimeChatFeed.Format(entry);

        Assert.Equal(
            "Bob says, \"hi there\"",
            Word(new ChatEntry(
                ChatKind.LocalSpeech, "Bob", "hi there", OtherPlayerGuid, 0u)));
        Assert.Equal(
            "You say, \"hi there\"",
            Word(new ChatEntry(ChatKind.LocalSpeech, "You", "hi there", 0u, 0u)));
        Assert.Equal(
            "Bob says, \"hi there\"",
            Word(new ChatEntry(
                ChatKind.RangedSpeech, "Bob", "hi there", OtherPlayerGuid, 0u)));
        Assert.Equal(
            "[Fellowship] Bob says, \"group up\"",
            Word(new ChatEntry(ChatKind.Channel, "Bob", "group up", OtherPlayerGuid, 7u)
            {
                ChannelName = "Fellowship",
            }));
        Assert.Equal(
            "Bob says on the <unknown> channel, \"group up\"",
            Word(new ChatEntry(
                ChatKind.Channel, "Bob", "group up", OtherPlayerGuid, 7u)));
        Assert.Equal(
            "Bob tells you, \"meet me\"",
            Word(new ChatEntry(ChatKind.Tell, "Bob", "meet me", OtherPlayerGuid, 0u)));
        Assert.Equal(
            "You tell Bob, \"meet me\"",
            Word(new ChatEntry(ChatKind.Tell, "Bob", "meet me", 0u, 0u)));
        Assert.Equal(
            "[Popup] You have died.",
            Word(new ChatEntry(ChatKind.Popup, "", "You have died.", 0u, 0u)));
        Assert.Equal(
            "Bob waves.",
            Word(new ChatEntry(ChatKind.Emote, "Bob", "waves.", OtherPlayerGuid, 0u)));
        Assert.Equal(
            "Bob waves.",
            Word(new ChatEntry(
                ChatKind.SoulEmote, "Bob", "waves.", OtherPlayerGuid, 0u)));
        Assert.Equal(
            "Welcome to Dereth.",
            Word(new ChatEntry(ChatKind.System, "", "Welcome to Dereth.", 0u, 0u)));
    }

    [Fact]
    public void AnotherPlayersNameIsMarkedUpAndTheVisibleTextIsUnchanged()
    {
        var entry = new ChatEntry(
            ChatKind.LocalSpeech, "Bob", "hi there", OtherPlayerGuid, 0u);

        Assert.Equal(
            "<Tell:IIDString:1342177290:Bob>Bob<\\Tell> says, \"hi there\"",
            RuntimeChatFeed.FormatTagged(entry));
        Assert.Equal(
            RuntimeChatFeed.Format(entry),
            string.Concat(ChatTagMarkup
                .Parse(RuntimeChatFeed.FormatTagged(entry))
                .Select(span => span.Text)));
    }

    // -- Pull -------------------------------------------------------------

    [Fact]
    public void ASnapshotCarriesTheFinishedTextAndTheEntrysClassification()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);
        log.OnCombatLine(
            "A Drudge Slinker slashes you for 9 points of damage!",
            (uint)RetailLogTextType.CombatEnemy,
            CombatLineKind.Warning);

        RuntimeChatLine line = Assert.Single(feed.Snapshot());

        Assert.Equal(
            "A Drudge Slinker slashes you for 9 points of damage!", line.Text);
        Assert.Equal(ChatKind.Combat, line.Kind);
        Assert.Equal((uint)RetailLogTextType.CombatEnemy, line.LogTextType);
        Assert.Equal(CombatLineKind.Warning, line.CombatKind);
        Assert.Equal(1L, line.Sequence);
        Assert.Null(line.Spans);
    }

    [Fact]
    public void ASnapshotReturnsTheTailOldestFirst()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);
        for (int i = 1; i <= 5; i++)
            log.OnSystemMessage($"line {i}", chatType: 0u);

        Assert.Equal(
            ["line 3", "line 4", "line 5"],
            feed.Snapshot(limit: 3).Select(line => line.Text).ToArray());
        Assert.Equal(5, feed.Snapshot(limit: 50).Count);
    }

    [Fact]
    public void ASnapshotOfAnEmptyLogIsEmptyAndTheRevisionTracksTheLog()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);

        Assert.Empty(feed.Snapshot());
        Assert.Equal(log.Revision, feed.Revision);

        log.OnSystemMessage("Welcome to Dereth.", chatType: 0u);
        Assert.Equal(log.Revision, feed.Revision);
    }

    [Fact]
    public void ALimitBelowOneIsRefused()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);

        Assert.Throws<ArgumentOutOfRangeException>(() => feed.Snapshot(limit: 0));
    }

    // -- Push -------------------------------------------------------------

    [Fact]
    public void EveryAppendedLineIsPushedOnceInArrivalOrderWithTheSameText()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);
        var pushed = new List<RuntimeChatLine>();
        feed.LineAppended += pushed.Add;

        log.OnLocalSpeech("Bob", "hi there", OtherPlayerGuid, false, 2u);
        log.OnSystemMessage("Welcome to Dereth.", chatType: 0u);

        Assert.Equal(
            ["Bob says, \"hi there\"", "Welcome to Dereth."],
            pushed.Select(line => line.Text).ToArray());
        Assert.Equal(
            feed.Snapshot().Select(line => line.Text).ToArray(),
            pushed.Select(line => line.Text).ToArray());
        Assert.Equal(
            feed.Snapshot().Select(line => line.Sequence).ToArray(),
            pushed.Select(line => line.Sequence).ToArray());
    }

    [Fact]
    public void ADisposedFeedStopsPushing()
    {
        var log = new ChatLog();
        var feed = new RuntimeChatFeed(log);
        var pushed = new List<RuntimeChatLine>();
        feed.LineAppended += pushed.Add;

        log.OnSystemMessage("first", chatType: 0u);
        feed.Dispose();
        log.OnSystemMessage("second", chatType: 0u);

        Assert.Equal(["first"], pushed.Select(line => line.Text).ToArray());

        // Disposing twice is harmless.
        feed.Dispose();
    }

    // -- Per-window membership --------------------------------------------

    [Fact]
    public void WithoutWindowFiltersEveryLineBelongsToEveryWindow()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);

        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.Speech));
        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.Allegiance));
    }

    [Fact]
    public void AWindowShowsOnlyTheTextClassesItsFilterIsSetTo()
    {
        var log = new ChatLog();
        var windows = new ChatWindowState();
        using var feed = new RuntimeChatFeed(log, windows);

        // Speech only, nothing else.
        windows.SetFilter(0, 1UL << (int)RetailLogTextType.Speech);
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, false,
            (uint)RetailLogTextType.Speech);
        log.OnSystemMessage(
            "Your Cooking skill is now trained!",
            (uint)RetailLogTextType.Advancement);

        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.Speech));
        Assert.False(feed.BelongsTo(0, (uint)RetailLogTextType.Advancement));
        Assert.Equal(2, feed.Snapshot().Count);
        Assert.Equal(
            ["Bob says, \"hi there\""],
            feed.SnapshotForWindow(0).Select(line => line.Text).ToArray());
    }

    [Fact]
    public void TheMainWindowsDefaultFilterShowsOrdinarySpeechAndCombat()
    {
        var log = new ChatLog();
        var windows = new ChatWindowState();
        using var feed = new RuntimeChatFeed(log, windows);

        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.Speech));
        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.Tell));
        Assert.True(feed.BelongsTo(0, (uint)RetailLogTextType.CombatSelf));
    }

    // -- Timestamps -------------------------------------------------------

    [Fact]
    public void TimestampsOffLeaveTheLineAsWorded()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => false };
        using var feed = new RuntimeChatFeed(log);
        log.OnLocalSpeech("Bob", "hi there", OtherPlayerGuid, false, 2u);

        Assert.Equal("Bob says, \"hi there\"", feed.Snapshot()[0].Text);
    }

    [Fact]
    public void TimestampsOnPutThePrefixInFrontAndInItsOwnSpan()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => true };
        using var feed = new RuntimeChatFeed(log);
        log.OnLocalSpeech("Bob", "hi there", OtherPlayerGuid, false, 2u);
        string prefix = ChatLog.FormatTimestampPrefix(log.Snapshot()[0].Received);

        RuntimeChatLine line = feed.Snapshot()[0];
        Assert.Equal(prefix + "Bob says, \"hi there\"", line.Text);
        Assert.Equal(prefix, line.Spans![0].Text);
        Assert.Equal(ChatSpanRole.Timestamp, line.Spans[0].Role);
        Assert.Equal(line.Text, string.Concat(line.Spans.Select(span => span.Text)));
    }

    [Fact]
    public void APushedLineCarriesTheTimestampToo()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => true };
        using var feed = new RuntimeChatFeed(log);
        RuntimeChatLine? pushed = null;
        feed.LineAppended += line => pushed = line;

        log.OnSystemMessage("Welcome to Dereth.", chatType: 0u);
        string prefix = ChatLog.FormatTimestampPrefix(log.Snapshot()[0].Received);

        Assert.Equal(prefix + "Welcome to Dereth.", pushed?.Text);
    }

    // -- The session owner supplies one ------------------------------------

    [Fact]
    public void TheCommunicationOwnerCarriesAFeedWiredToItsLogAndItsWindowFilters()
    {
        using var state = new RuntimeCommunicationState();
        state.ChatWindows.SetFilter(0, 1UL << (int)RetailLogTextType.Speech);
        state.Chat.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, false,
            (uint)RetailLogTextType.Speech);
        state.Chat.OnSystemMessage(
            "Your Cooking skill is now trained!",
            (uint)RetailLogTextType.Advancement);

        Assert.Equal(
            ["Bob says, \"hi there\""],
            state.ChatFeed.SnapshotForWindow(0).Select(line => line.Text).ToArray());
    }

    // -- The short tag a colourless front end puts in front of a line ------

    [Fact]
    public void ALineThatAlreadyNamesItsChannelIsNotTaggedWithTheChannelAgain()
    {
        // Before this the console printed the label twice:
        // "[fellowship] [Fellowship] Bob says, ...".
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);
        log.OnChannelBroadcast(
            7u, "Bob", "group up",
            (uint)RetailLogTextType.Fellowship, "Fellowship");

        RuntimeChatLine line = feed.Snapshot()[0];

        Assert.Equal(string.Empty, RuntimeChatLineTags.For(line));
        Assert.Equal("[Fellowship] Bob says, \"group up\"", line.Text);
    }

    [Fact]
    public void EveryOtherLineStillCarriesTheTagForItsTextClass()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log);
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, false,
            (uint)RetailLogTextType.Speech);

        Assert.Equal("[say] ", RuntimeChatLineTags.For(feed.Snapshot()[0]));
    }

    [Fact]
    public void DisposingTheCommunicationOwnerStopsItsFeed()
    {
        var state = new RuntimeCommunicationState();
        var pushed = new List<RuntimeChatLine>();
        state.ChatFeed.LineAppended += pushed.Add;

        state.Chat.OnSystemMessage("first", chatType: 0u);
        state.Dispose();
        state.Chat.OnSystemMessage("second", chatType: 0u);

        Assert.Equal(["first"], pushed.Select(line => line.Text).ToArray());
    }
}
