using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatVMLastTellSenderTests
{
    [Fact]
    public void DisposeDetachesTranscriptSubscriptionExactlyOnce()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnTellReceived("Before", "ping", 0x5000_0001u, logTextType: 0x03u);

        vm.Dispose();
        vm.Dispose();
        log.OnTellReceived("After", "pong", 0x5000_0002u, logTextType: 0x03u);

        Assert.Equal("Before", vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_StartsNull()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        Assert.Null(vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_PopulatedFromOnTellReceived()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        log.OnTellReceived(sender: "Bestie", text: "ping", senderGuid: 0x5000_00AAu, logTextType: 0x03u);

        Assert.Equal("Bestie", vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_UpdatedToMostRecentIncomingTell()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        log.OnTellReceived("Bestie", "ping", 0x5000_00AAu, logTextType: 0x03u);
        log.OnTellReceived("Regal",  "yo",   0x5000_00BBu, logTextType: 0x03u);

        Assert.Equal("Regal", vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_IgnoresSelfSentEcho()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        log.OnTellReceived("Bestie", "ping", 0x5000_00AAu, logTextType: 0x03u);
        log.OnSelfSent(ChatKind.Tell, "back at you", logTextType: 0x04u, targetOrChannel: "Bestie");

        Assert.Equal("Bestie", vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_IgnoresLocalSpeech()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        log.OnLocalSpeech("Caith", "hello", 0x5000_00CCu, isRanged: false, logTextType: 0x02u);

        Assert.Null(vm.LastIncomingTellSender);
    }

    [Fact]
    public void LastIncomingTellSender_IgnoresChannelBroadcast()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);

        log.OnChannelBroadcast(channelId: 7, sender: "Caith", text: "raid time");

        Assert.Null(vm.LastIncomingTellSender);
    }
}
