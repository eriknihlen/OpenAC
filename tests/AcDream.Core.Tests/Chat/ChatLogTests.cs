using System.Globalization;
using System.Threading;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatLogTests
{
    [Fact]
    public void OnLocalSpeech_AppendsEntry_FiresEvent()
    {
        var log = new ChatLog();
        ChatEntry? seen = null;
        log.EntryAppended += e => seen = e;

        log.OnLocalSpeech("Alice", "hi", 0xAA, isRanged: false, logTextType: 0x02u);

        Assert.Equal(1, log.Count);
        Assert.NotNull(seen);
        Assert.Equal(ChatKind.LocalSpeech, seen!.Value.Kind);
        Assert.Equal("Alice", seen.Value.Sender);
        Assert.Equal("hi", seen.Value.Text);
    }

    [Fact]
    public void OnLocalSpeech_Ranged_SetsRangedKind()
    {
        var log = new ChatLog();
        log.OnLocalSpeech("Bob", "SHOUT", 0xBB, isRanged: true, logTextType: 0x02u);
        Assert.Equal(ChatKind.RangedSpeech, log.Snapshot()[0].Kind);
    }

    [Fact]
    public void OnChannelBroadcast_SetsChannelId()
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(channelId: 42, sender: "Alice", text: "allegiance motd");
        var e = log.Snapshot()[0];
        Assert.Equal(42u, e.ChannelId);
        Assert.Equal(ChatKind.Channel, e.Kind);
    }

    [Fact]
    public void OnTellReceived_SetsTellKind()
    {
        var log = new ChatLog();
        log.OnTellReceived("Alice", "psst", 0xAA, logTextType: 0x03u);
        Assert.Equal(ChatKind.Tell, log.Snapshot()[0].Kind);
    }

    [Fact]
    public void OnSystemMessage_EncodesChatType_AsChannelId()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Your spell fizzled!", chatType: 5);
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.System, e.Kind);
        Assert.Equal(5u, e.ChannelId);
    }

    [Fact]
    public void OnSelfSent_EchoesOutbound()
    {
        var log = new ChatLog();
        log.OnSelfSent(ChatKind.Tell, "hey", logTextType: 0x04u, targetOrChannel: "Alice");
        var e = log.Snapshot()[0];
        Assert.Equal("Alice", e.Sender);
        Assert.Equal("hey", e.Text);
    }

    [Fact]
    public void RingBuffer_DropsOldestBeyondCapacity()
    {
        var log = new ChatLog(maxEntries: 3);
        log.OnLocalSpeech("A", "1", 0, false, logTextType: 0x02u);
        log.OnLocalSpeech("B", "2", 0, false, logTextType: 0x02u);
        log.OnLocalSpeech("C", "3", 0, false, logTextType: 0x02u);
        log.OnLocalSpeech("D", "4", 0, false, logTextType: 0x02u);

        var snap = log.Snapshot();
        Assert.Equal(3, snap.Length);
        Assert.Equal("2", snap[0].Text);   // "1" was dropped
        Assert.Equal("4", snap[2].Text);
    }

    [Fact]
    public void Clear_EmptiesBuffer()
    {
        var log = new ChatLog();
        log.OnLocalSpeech("A", "1", 0, false, logTextType: 0x02u);
        log.Clear();
        Assert.Equal(0, log.Count);
    }


    [Fact]
    public void OnEmote_AppendsEmoteEntry()
    {
        var log = new ChatLog();
        log.OnEmote("Caith", "waves at you", 0xCAFE);
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.Emote, e.Kind);
        Assert.Equal("Caith", e.Sender);
        Assert.Equal("waves at you", e.Text);
        Assert.Equal(0xCAFEu, e.SenderGuid);
    }

    [Fact]
    public void OnSoulEmote_AppendsSoulEmoteEntry()
    {
        var log = new ChatLog();
        log.OnSoulEmote("Bob", "dances", 0xBEEF);
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.SoulEmote, e.Kind);
        Assert.Equal("Bob", e.Sender);
        Assert.Equal("dances", e.Text);
        Assert.Equal(0xBEEFu, e.SenderGuid);
    }

    [Fact]
    public void OnPlayerKilled_AppendsSystemEntry_StoresGuids()
    {
        var log = new ChatLog();
        log.OnPlayerKilled("Caith was killed by a Drudge.",
            victimGuid: 0x12345678u,
            killerGuid: 0x90ABCDEFu,
            localPlayerGuid: 0x11111111u);
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.System, e.Kind);
        Assert.Equal("Caith was killed by a Drudge.", e.Text);
        Assert.Equal(0x12345678u, e.SenderGuid);
        Assert.Equal(0x90ABCDEFu, e.ChannelId);   // killer guid stashed here
    }

    [Theory]
    [InlineData(0x12345678u)]
    [InlineData(0x90ABCDEFu)]
    public void OnPlayerKilled_SuppressesVictimAndKiller(uint localPlayerGuid)
    {
        var log = new ChatLog();
        int appended = 0;
        log.EntryAppended += _ => appended++;

        log.OnPlayerKilled(
            "Caith was killed by a Drudge.",
            victimGuid: 0x12345678u,
            killerGuid: 0x90ABCDEFu,
            localPlayerGuid);

        Assert.Empty(log.Snapshot());
        Assert.Equal(0, appended);
    }


    [Fact]
    public void OnLocalSpeech_EmptySender_SubstitutesYou()
    {
        var log = new ChatLog();
        log.OnLocalSpeech(sender: "", text: "hello", senderGuid: 0, isRanged: false, logTextType: 0x02u);
        var e = log.Snapshot()[0];
        Assert.Equal("You", e.Sender);
        Assert.Equal("hello", e.Text);
    }

    [Fact]
    public void OnLocalSpeech_NonEmptySender_KeepsAsIs()
    {
        var log = new ChatLog();
        log.OnLocalSpeech(sender: "Alice", text: "hi", senderGuid: 0xAA, isRanged: false, logTextType: 0x02u);
        Assert.Equal("Alice", log.Snapshot()[0].Sender);
    }


    [Fact]
    public void OnCombatLine_DefaultsInfoKind_TagsEntryAsCombat()
    {
        var log = new ChatLog();
        log.OnCombatLine("You hit Mosswart for 5 slashing damage (50.0%).", logTextType: 0x06u);
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.Combat, e.Kind);
        Assert.Equal(CombatLineKind.Info, e.CombatKind);
        Assert.Equal("You hit Mosswart for 5 slashing damage (50.0%).", e.Text);
    }

    [Fact]
    public void OnCombatLine_PreservesExplicitKind()
    {
        var log = new ChatLog();
        log.OnCombatLine("Mosswart hit you for 8 fire damage to your chest.",
            logTextType: 0x06u, kind: CombatLineKind.Warning);
        Assert.Equal(CombatLineKind.Warning, log.Snapshot()[0].CombatKind);

        log.OnCombatLine("Attack sequence finished with WeenieError 0x1234.",
            logTextType: 0x06u, kind: CombatLineKind.Error);
        Assert.Equal(CombatLineKind.Error, log.Snapshot()[1].CombatKind);
    }


    [Fact]
    public void OnLocalSpeech_ExplicitLogTextType_PinsSpeechValue()
    {
        var log = new ChatLog();
        log.OnLocalSpeech("Alice", "hi", 0xAA, isRanged: false, logTextType: 0x02u);
        Assert.Equal(0x02u, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnLocalSpeech_PassesWireChatTypeVerbatim()
    {
        var log = new ChatLog();
        log.OnLocalSpeech("Mosswart", "grumble", 0x5000_1234u, isRanged: false, logTextType: 0x0Cu);
        Assert.Equal(0x0Cu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnEmote_HardCodesLogTextType_0x0C()
    {
        var log = new ChatLog();
        log.OnEmote("Caith", "waves at you", 0xCAFE);
        Assert.Equal(0x0Cu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnSoulEmote_HardCodesLogTextType_0x0C()
    {
        var log = new ChatLog();
        log.OnSoulEmote("Bob", "dances", 0xBEEF);
        Assert.Equal(0x0Cu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnPlayerKilled_LogTextType_IsDefault()
    {
        var log = new ChatLog();
        log.OnPlayerKilled(
            "Caith was killed by a Drudge.",
            0x1u,
            0x2u,
            localPlayerGuid: 0x3u);
        Assert.Equal(0x00u, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnPopup_LogTextType_IsDefault()
    {
        var log = new ChatLog();
        log.OnPopup("A modal message.");
        var e = log.Snapshot()[0];
        Assert.Equal(ChatKind.Popup, e.Kind);
        Assert.Equal(0x00u, e.LogTextType);
    }

    [Fact]
    public void OnTellReceived_ExplicitLogTextType_PinsTellValue()
    {
        var log = new ChatLog();
        log.OnTellReceived("Alice", "psst", 0xAA, logTextType: 0x03u);
        Assert.Equal(0x03u, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnTellReceived_PassesWireChatTypeVerbatim()
    {
        var log = new ChatLog();
        log.OnTellReceived("Alice", "psst", 0xAA, logTextType: 0x1Fu);
        Assert.Equal(0x1Fu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnSelfSent_Tell_ExplicitLogTextType_PinsSpeechDirectSendValue()
    {
        var log = new ChatLog();
        log.OnSelfSent(ChatKind.Tell, "hey", logTextType: 0x04u, targetOrChannel: "Alice");
        Assert.Equal(0x04u, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnSelfSent_Channel_ExplicitLogTextType_PinsSocialSendValue()
    {
        var log = new ChatLog();
        log.OnSelfSent(ChatKind.Channel, "hi all", logTextType: 0x0Bu, targetOrChannel: "Fellowship");
        Assert.Equal(0x0Bu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnSelfSent_ExplicitLogTextType_Overrides()
    {
        var log = new ChatLog();
        log.OnSelfSent(ChatKind.Channel, "hi all", logTextType: 0x13u, targetOrChannel: "Fellowship");
        Assert.Equal(0x13u, log.Snapshot()[0].LogTextType);
    }

    [Theory]
    // Fellowship — same type hear + send.
    [InlineData(0x0800u, "Fellowship", 0x13u)]
    // Patron/Vassal/Follower — hear is Social (0xA).
    [InlineData(0x1000u, "Patron", 0x0Au)]
    [InlineData(0x2000u, "Vassal", 0x0Au)]
    [InlineData(0x4000u, "Follower", 0x0Au)]
    // Co-Vassals / Allegiance Broadcast.
    [InlineData(0x1000000u, "Co-Vassals", 0x0Au)]
    [InlineData(0x2000000u, "Allegiance Broadcast", 0x0Au)]
    [InlineData(0x4000000u, "?", 0x08u)]
    // The one named non-family bit inside the generic bucket (Help).
    [InlineData(0x0400u, "Help", 0x0Fu)]
    [InlineData(0x0900u, "Audit", 0x08u)]
    public void OnChannelBroadcast_DerivesLogTextType_FromLegacyChannelBit(
        uint channelBit, string channelName, uint expectedLogTextType)
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(channelBit, sender: "Someone", text: "hi", channelName: channelName);
        Assert.Equal(expectedLogTextType, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnChannelBroadcast_ExplicitLogTextType_OverridesLegacyDerivation()
    {
        var log = new ChatLog();
        log.OnChannelBroadcast(
            channelId: 0x7000_0001u, sender: "Someone", text: "hi",
            logTextType: 0x1Bu, channelName: "General");
        Assert.Equal(0x1Bu, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnSystemMessage_LogTextType_MatchesChatType()
    {
        var log = new ChatLog();
        log.OnSystemMessage("Your spell fizzled!", chatType: 5);
        Assert.Equal(5u, log.Snapshot()[0].LogTextType);
    }

    [Fact]
    public void OnCombatLine_ExplicitLogTextType_PinsGenericCombatValue()
    {
        var log = new ChatLog();
        log.OnCombatLine("You hit Mosswart for 5 slashing damage (50.0%).", logTextType: 0x06u);
        Assert.Equal(0x06u, log.Snapshot()[0].LogTextType);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredEntryText_NeverCarriesTheTimestampPrefix(bool timestampsOn)
    {
        var log = new ChatLog { DisplayTimestampsSource = () => timestampsOn };
        log.OnLocalSpeech("Alice", "hi", 0xAAu, isRanged: false, logTextType: 0x02u);

        Assert.Equal("hi", log.Snapshot()[0].Text);
    }

    [Fact]
    public void FormatTimestampPrefix_UsesLiteralColons_RegardlessOfCurrentCulture()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");

            string prefix = ChatLog.FormatTimestampPrefix(
                new DateTime(2026, 8, 11, 13, 5, 9, DateTimeKind.Utc));

            Assert.Matches(@"^\d{1,2}:\d{2}:\d{2} $", prefix);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
