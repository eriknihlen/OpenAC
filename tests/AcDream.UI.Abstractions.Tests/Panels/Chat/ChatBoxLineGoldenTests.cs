using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

/// <summary>
/// The exact characters the chat box puts on screen, one golden per chat kind,
/// speaker and channel shape. These strings are what a player reads, so they
/// are pinned here byte for byte: any change to how a line is built has to
/// change this table first, deliberately, whoever builds the line and wherever
/// that code lives.
/// </summary>
public sealed class ChatBoxLineGoldenTests
{
    private const uint OtherPlayerGuid = 0x5000000Au;

    private static ChatEntry Entry(
        ChatKind kind,
        string sender,
        string text,
        uint senderGuid = 0u,
        uint channelId = 0u,
        string channelName = "",
        uint logTextType = 0u,
        CombatLineKind? combatKind = null)
        => new(kind, sender, text, senderGuid, channelId)
        {
            ChannelName = channelName,
            LogTextType = logTextType,
            CombatKind = combatKind,
        };

    // -- One golden per line shape ---------------------------------------

    [Fact]
    public void LocalSpeechFromAnotherPlayer()
        => Assert.Equal(
            "Bob says, \"hi there\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.LocalSpeech, "Bob", "hi there", OtherPlayerGuid)));

    [Theory]
    [InlineData("You")]
    [InlineData("")]
    public void LocalSpeechFromUs(string sender)
        => Assert.Equal(
            "You say, \"hi there\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.LocalSpeech, sender, "hi there")));

    [Fact]
    public void RangedSpeechFromAnotherPlayer()
        => Assert.Equal(
            "Bob says, \"hi there\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.RangedSpeech, "Bob", "hi there", OtherPlayerGuid)));

    /// <summary>
    /// A ranged line has no sentence of its own for the speaker: the
    /// character's own is printed under its own name, like anyone else's.
    /// </summary>
    [Fact]
    public void RangedSpeechFromUs()
        => Assert.Equal(
            "Acdream says, \"hi there\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.RangedSpeech, "Acdream", "hi there")));

    [Fact]
    public void NamedChannelFromAnotherPlayer()
        => Assert.Equal(
            "[Fellowship] Bob says, \"group up\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.Channel,
                "Bob",
                "group up",
                OtherPlayerGuid,
                channelId: 7u,
                channelName: "Fellowship")));

    [Theory]
    [InlineData("You")]
    [InlineData("")]
    public void NamedChannelFromUs(string sender)
        => Assert.Equal(
            "[Fellowship] You say, \"group up\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.Channel,
                sender,
                "group up",
                channelId: 7u,
                channelName: "Fellowship")));

    [Fact]
    public void UnnamedChannelFallsBackToItsNumber()
        => Assert.Equal(
            "Bob says on the <unknown> channel, \"group up\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.Channel, "Bob", "group up", OtherPlayerGuid, channelId: 7u)));

    [Fact]
    public void UnnamedChannelFromUsFallsBackToItsNumber()
        => Assert.Equal(
            "You say on the <unknown> channel, \"group up\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.Channel, "You", "group up", channelId: 7u)));

    [Fact]
    public void TellReceived()
        => Assert.Equal(
            "Bob tells you, \"meet me\"",
            ChatVM.FormatEntry(Entry(
                ChatKind.Tell, "Bob", "meet me", OtherPlayerGuid)));

    [Fact]
    public void TellSent()
        // Our own tell carries no sender id, and the name is the one we sent to.
        => Assert.Equal(
            "You tell Bob, \"meet me\"",
            ChatVM.FormatEntry(Entry(ChatKind.Tell, "Bob", "meet me")));

    [Fact]
    public void SystemTextIsShownAsSent()
        => Assert.Equal(
            "Your Cooking skill is now trained!",
            ChatVM.FormatEntry(Entry(
                ChatKind.System,
                "",
                "Your Cooking skill is now trained!",
                logTextType: (uint)RetailLogTextType.Advancement)));

    [Fact]
    public void CombatTextIsShownAsSent()
        => Assert.Equal(
            "You slash a Drudge Slinker for 14 points of damage!",
            ChatVM.FormatEntry(Entry(
                ChatKind.Combat,
                "",
                "You slash a Drudge Slinker for 14 points of damage!",
                logTextType: (uint)RetailLogTextType.CombatSelf,
                combatKind: CombatLineKind.Info)));

    [Fact]
    public void PopupTextCarriesItsOwnMarker()
        => Assert.Equal(
            "[Popup] You have died.",
            ChatVM.FormatEntry(Entry(ChatKind.Popup, "", "You have died.")));

    [Fact]
    public void EmoteReadsAsAThirdPersonAction()
        => Assert.Equal(
            "Bob waves.",
            ChatVM.FormatEntry(Entry(
                ChatKind.Emote,
                "Bob",
                "waves.",
                OtherPlayerGuid,
                logTextType: (uint)RetailLogTextType.Emote)));

    [Fact]
    public void SoulEmoteReadsExactlyLikeAnEmote()
        => Assert.Equal(
            "Bob waves.",
            ChatVM.FormatEntry(Entry(
                ChatKind.SoulEmote,
                "Bob",
                "waves.",
                OtherPlayerGuid,
                logTextType: (uint)RetailLogTextType.Emote)));

    // -- The clickable-sender variant ------------------------------------

    [Theory]
    [InlineData(ChatKind.LocalSpeech, "<Tell:IIDString:1342177290:Bob>Bob<\\Tell> says, \"hi there\"")]
    [InlineData(ChatKind.RangedSpeech, "<Tell:IIDString:1342177290:Bob>Bob<\\Tell> says, \"hi there\"")]
    [InlineData(ChatKind.Tell, "<Tell:IIDString:1342177290:Bob>Bob<\\Tell> tells you, \"hi there\"")]
    public void AnotherPlayersNameIsWrappedForClickToTell(
        ChatKind kind, string expected)
        => Assert.Equal(
            expected,
            ChatVM.FormatEntryTagged(Entry(kind, "Bob", "hi there", OtherPlayerGuid)));

    [Fact]
    public void AChannelLineTagsTheSpeakerInsideTheChannelBrackets()
        => Assert.Equal(
            "[Fellowship] <Tell:IIDString:1342177290:Bob>Bob<\\Tell> says, \"group up\"",
            ChatVM.FormatEntryTagged(Entry(
                ChatKind.Channel,
                "Bob",
                "group up",
                OtherPlayerGuid,
                channelId: 7u,
                channelName: "Fellowship")));

    [Theory]
    [InlineData(ChatKind.LocalSpeech, "You", "You say, \"hi there\"")]
    [InlineData(ChatKind.System, "", "hi there")]
    [InlineData(ChatKind.Emote, "Bob", "Bob hi there")]
    public void LinesWithNoClickableSenderAreIdenticalToThePlainForm(
        ChatKind kind, string sender, string expected)
    {
        ChatEntry entry = Entry(kind, sender, "hi there");
        Assert.Equal(expected, ChatVM.FormatEntryTagged(entry));
        Assert.Equal(ChatVM.FormatEntry(entry), ChatVM.FormatEntryTagged(entry));
    }

    // -- The whole-line reader the panel consumes ------------------------

    [Fact]
    public void TheDetailedLineCarriesTheVisibleTextAndTheEntrysClassification()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnCombatLine(
            "A Drudge Slinker slashes you for 9 points of damage!",
            (uint)RetailLogTextType.CombatEnemy,
            CombatLineKind.Warning);

        FormattedLine line = Assert.Single(vm.RecentLinesDetailed());

        Assert.Equal("A Drudge Slinker slashes you for 9 points of damage!", line.Text);
        Assert.Equal(ChatKind.Combat, line.Kind);
        Assert.Equal(CombatLineKind.Warning, line.CombatKind);
        Assert.Equal((uint)RetailLogTextType.CombatEnemy, line.LogTextType);
        Assert.Null(line.Spans);
        Assert.Equal(1L, line.Sequence);
    }

    [Fact]
    public void ADetailedLineWithAClickableSenderSplitsIntoSpansThatRejoinToTheVisibleText()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, isRanged: false,
            logTextType: (uint)RetailLogTextType.Speech);

        FormattedLine line = Assert.Single(vm.RecentLinesDetailed());

        Assert.Equal("Bob says, \"hi there\"", line.Text);
        Assert.NotNull(line.Spans);
        Assert.Equal(
            line.Text, string.Concat(line.Spans!.Select(span => span.Text)));
        Assert.Equal("Bob", line.Spans[0].Text);
        Assert.NotNull(line.Spans[0].Tag);
    }

    [Fact]
    public void TheFlatReaderShowsTheSameTextAsTheDetailedOne()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnLocalSpeech(
            "Bob", "hi there", OtherPlayerGuid, isRanged: false, logTextType: 2u);
        log.OnSystemMessage("Welcome to Dereth.", chatType: 0u);

        Assert.Equal(
            vm.RecentLinesDetailed().Select(line => line.Text).ToArray(),
            vm.RecentLines().ToArray());
        Assert.Equal(
            ["Bob says, \"hi there\"", "Welcome to Dereth."],
            vm.RecentLines().ToArray());
    }

    // -- Timestamps ------------------------------------------------------

    [Fact]
    public void TheTimestampPrefixIsHoursWithoutAPadThenTwoDigitMinutesAndSeconds()
    {
        // Built from a local wall-clock reading so the golden does not move
        // with the machine's time zone.
        var local = new DateTime(2026, 9, 20, 13, 4, 5, DateTimeKind.Local);

        Assert.Equal(
            "13:04:05 ", ChatLog.FormatTimestampPrefix(local.ToUniversalTime()));
    }

    [Fact]
    public void TimestampsOffLeaveTheLineExactlyAsFormatted()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => false };
        var vm = new ChatVM(log);
        log.OnLocalSpeech("Bob", "hi there", OtherPlayerGuid, false, 2u);

        Assert.Equal("Bob says, \"hi there\"", vm.RecentLines()[0]);
        Assert.Equal("Bob says, \"hi there\"", vm.RecentLinesDetailed()[0].Text);
    }

    [Fact]
    public void TimestampsOnPutThePrefixInFrontOfTheSameLine()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => true };
        var vm = new ChatVM(log);
        log.OnLocalSpeech("Bob", "hi there", OtherPlayerGuid, false, 2u);
        string prefix = ChatLog.FormatTimestampPrefix(log.Snapshot()[0].Received);

        Assert.Equal(prefix + "Bob says, \"hi there\"", vm.RecentLines()[0]);

        FormattedLine line = vm.RecentLinesDetailed()[0];
        Assert.Equal(prefix + "Bob says, \"hi there\"", line.Text);
        Assert.Equal(prefix, line.Spans![0].Text);
        Assert.Equal(ChatSpanRole.Timestamp, line.Spans[0].Role);
        Assert.Equal(line.Text, string.Concat(line.Spans.Select(span => span.Text)));
    }

    [Fact]
    public void TimestampsOnAlsoPrefixALineWithNoClickableSender()
    {
        var log = new ChatLog { DisplayTimestampsSource = () => true };
        var vm = new ChatVM(log);
        log.OnSystemMessage("Welcome to Dereth.", chatType: 0u);
        string prefix = ChatLog.FormatTimestampPrefix(log.Snapshot()[0].Received);

        FormattedLine line = vm.RecentLinesDetailed()[0];
        Assert.Equal(prefix + "Welcome to Dereth.", line.Text);
        Assert.Equal(2, line.Spans!.Count);
        Assert.Equal(prefix, line.Spans[0].Text);
        Assert.Equal("Welcome to Dereth.", line.Spans[1].Text);
    }
}
