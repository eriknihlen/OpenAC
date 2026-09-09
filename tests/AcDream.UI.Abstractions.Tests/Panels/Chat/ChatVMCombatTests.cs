using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatVMCombatTests
{
    [Fact]
    public void FormatEntry_CombatKind_PassesThroughVerbatim()
    {
        var entry = new ChatEntry(
            Kind: ChatKind.Combat,
            Sender: "",
            Text: "You hit Mosswart for 5 slashing damage (50.0%).",
            SenderGuid: 0,
            ChannelId: 0)
        { CombatKind = CombatLineKind.Info };

        Assert.Equal(
            "You hit Mosswart for 5 slashing damage (50.0%).",
            ChatVM.FormatEntry(entry));
    }

    [Fact]
    public void RecentLinesDetailed_CombatEntry_RetainsCombatKind()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnCombatLine("Mosswart hit you for 8 fire damage to your chest.",
            logTextType: 0x06u, kind: CombatLineKind.Warning);

        var lines = vm.RecentLinesDetailed();
        var line = Assert.Single(lines);
        Assert.Equal(ChatKind.Combat, line.Kind);
        Assert.Equal(CombatLineKind.Warning, line.CombatKind);
        Assert.Equal("Mosswart hit you for 8 fire damage to your chest.", line.Text);
    }

    [Fact]
    public void RecentLinesDetailed_NonCombatEntry_HasNullCombatKind()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnLocalSpeech("Alice", "hi", senderGuid: 0xAA, isRanged: false, logTextType: 0x02u);

        var line = Assert.Single(vm.RecentLinesDetailed());
        Assert.Equal(ChatKind.LocalSpeech, line.Kind);
        Assert.Null(line.CombatKind);
        Assert.Equal("Alice says, \"hi\"", line.Text);
    }

    private sealed class RecordingChatBus : ICommandBus
    {
        public void Publish<T>(T command) where T : notnull { /* no-op */ }
    }
}
