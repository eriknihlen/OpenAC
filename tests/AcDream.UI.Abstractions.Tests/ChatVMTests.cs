using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests;

public sealed class ChatVMTests
{
    [Fact]
    public void RecentLines_ReturnsEmpty_ForEmptyLog()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        Assert.Empty(vm.RecentLines());
    }

    [Fact]
    public void RecentLines_ReturnsAllEntries_WhenBelowLimit()
    {
        var log = new ChatLog();
        log.OnLocalSpeech(sender: "Caith", text: "hello", senderGuid: 0x5000_0001u, isRanged: false, logTextType: 0x02u);
        log.OnLocalSpeech(sender: "Regal", text: "world", senderGuid: 0x5000_0002u, isRanged: false, logTextType: 0x02u);

        var vm = new ChatVM(log, displayLimit: 20);
        var lines = vm.RecentLines();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Caith says, \"hello\"", lines[0]);
        Assert.Equal("Regal says, \"world\"", lines[1]);
    }

    [Fact]
    public void RecentLines_ReturnsTail_WhenAboveLimit_InOldestFirstOrder()
    {
        var log = new ChatLog();
        for (int i = 0; i < 30; i++)
            log.OnLocalSpeech(sender: "A", text: $"msg{i}", senderGuid: 0x5000_0001u, isRanged: false, logTextType: 0x02u);

        var vm = new ChatVM(log, displayLimit: 5);
        var lines = vm.RecentLines();

        // Tail = msg25..msg29 (5 entries, oldest first).
        Assert.Equal(5, lines.Count);
        Assert.Equal("A says, \"msg25\"", lines[0]);
        Assert.Equal("A says, \"msg29\"", lines[4]);
    }

    [Fact]
    public void FormatEntry_LocalSpeech_RetailStyleSays()
    {
        var incoming = new ChatEntry(ChatKind.LocalSpeech, "Caith", "hello", 0x5000_0001u, 0);
        Assert.Equal("Caith says, \"hello\"", ChatVM.FormatEntry(incoming));

        var ownEcho = new ChatEntry(ChatKind.LocalSpeech, "", "hi there", 0, 0);
        Assert.Equal("You say, \"hi there\"", ChatVM.FormatEntry(ownEcho));

        var ownEchoSubst = new ChatEntry(ChatKind.LocalSpeech, "You", "shouted echo", 0, 0);
        Assert.Equal("You say, \"shouted echo\"", ChatVM.FormatEntry(ownEchoSubst));
    }

    [Fact]
    public void FormatEntry_RangedSpeech_RetailStyleShouts()
    {
        var incoming = new ChatEntry(ChatKind.RangedSpeech, "Caith", "hello", 0x5000_0001u, 0);
        Assert.Equal("Caith shouts, \"hello\"", ChatVM.FormatEntry(incoming));

        var ownEcho = new ChatEntry(ChatKind.RangedSpeech, "You", "loud", 0, 0);
        Assert.Equal("You shout, \"loud\"", ChatVM.FormatEntry(ownEcho));
    }

    [Fact]
    public void FormatEntry_Channel_UsesChannelNameWhenPresent()
    {
        var named = new ChatEntry(ChatKind.Channel, "Caith", "g'day", 0x5000_0001u, 7u)
        {
            ChannelName = "Trade",
        };
        Assert.Equal("[Trade] Caith says, \"g'day\"", ChatVM.FormatEntry(named));

        var unnamed = new ChatEntry(ChatKind.Channel, "Caith", "g'day", 0x5000_0001u, 7u);
        Assert.Equal("[ch 7] Caith says, \"g'day\"", ChatVM.FormatEntry(unnamed));
    }

    [Fact]
    public void FormatEntry_Tell_RetailStyleTells()
    {
        // SenderGuid != 0 -> incoming whisper -> "Regal tells you, ..."
        var incoming = new ChatEntry(ChatKind.Tell, "Regal", "psst", 0x5000_0002u, 0);
        Assert.Equal("Regal tells you, \"psst\"", ChatVM.FormatEntry(incoming));

        var ownEcho = new ChatEntry(ChatKind.Tell, "Regal", "psst", 0, 0);
        Assert.Equal("You tell Regal, \"psst\"", ChatVM.FormatEntry(ownEcho));
    }

    [Fact]
    public void FormatEntry_System_NoSenderShown()
    {
        var entry = new ChatEntry(ChatKind.System, Sender: "", "Your spell fizzled!", 0, 0);
        Assert.Equal("Your spell fizzled!", ChatVM.FormatEntry(entry));
    }

    [Fact]
    public void FormatEntry_Popup_Prefixed()
    {
        var entry = new ChatEntry(ChatKind.Popup, Sender: "", "A door stands before you.", 0, 0);
        Assert.Equal("[Popup] A door stands before you.", ChatVM.FormatEntry(entry));
    }

    [Fact]
    public void Constructor_ThrowsOnNullLog()
    {
        Assert.Throws<ArgumentNullException>(() => new ChatVM(null!));
    }

    [Fact]
    public void Constructor_ThrowsOnZeroOrNegativeLimit()
    {
        var log = new ChatLog();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatVM(log, displayLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatVM(log, displayLimit: -1));
    }

    [Fact]
    public void RecentLines_ReturnsNewLineData_AfterSubsequentAppend()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        Assert.Empty(vm.RecentLines());

        log.OnLocalSpeech("Caith", "hello", 0x5000_0001u, false, logTextType: 0x02u);
        var after = vm.RecentLines();
        Assert.Single(after);
        Assert.Equal("Caith says, \"hello\"", after[0]);
    }

    [Fact]
    public void ShowSystemMessage_UsesDefaultLogTextType()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        vm.ShowSystemMessage("Unknown command: foo");

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(0x00u, entry.LogTextType);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayTimestamps_PrefixTheComposedLine_NotTheBody(bool timestampsOn)
    {
        var log = new ChatLog { DisplayTimestampsSource = () => timestampsOn };
        log.OnLocalSpeech("Alice", "hi", 0xAAu, isRanged: false, logTextType: 0x02u);
        var vm = new ChatVM(log);

        string plain = Assert.Single(vm.RecentLines());
        var detailed = Assert.Single(vm.RecentLinesDetailed());

        if (timestampsOn)
        {
            Assert.Matches(@"^\d{1,2}:\d{2}:\d{2} Alice says, ""hi""$", plain);
            Assert.Matches(@"^\d{1,2}:\d{2}:\d{2} Alice says, ""hi""$", detailed.Text);
        }
        else
        {
            Assert.Equal("Alice says, \"hi\"", plain);
            Assert.Equal("Alice says, \"hi\"", detailed.Text);
        }

        Assert.Equal("hi", log.Snapshot()[0].Text);
    }

    [Fact]
    public void RecentLines_ShowsPluginSystemMessage_TaggedDefault()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log, displayLimit: 50);

        log.OnSystemMessage(
            "MossTank: buffs applied.",
            chatType: (uint)RetailLogTextType.Default);

        Assert.Equal(
            "MossTank: buffs applied.",
            Assert.Single(vm.RecentLines()));
        Assert.Equal(
            (uint)RetailLogTextType.Default,
            Assert.Single(log.Snapshot()).LogTextType);
    }
}
