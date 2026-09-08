using System.Numerics;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class ChatVMRetellAndProvidersTests
{
    [Fact]
    public void BorrowedCommandTargetsRemainLiveAfterOneViewModelDisposes()
    {
        var log = new ChatLog();
        using var targets = new ChatCommandTargetState(log);
        var first = new ChatVM(log, commandTargets: targets);
        using var second = new ChatVM(log, commandTargets: targets);

        first.Dispose();
        log.OnTellReceived("Bestie", "incoming", 0x50000001u, logTextType: 0x03u);

        Assert.Equal("Bestie", second.LastIncomingTellSender);
    }

    [Fact]
    public void ResetSessionTargets_ClearsReplyAndRetellWithoutClearingTranscript()
    {
        var log = new ChatLog();
        var vm = new ChatVM(log);
        log.OnTellReceived("Bestie", "incoming", 0x50000001u, logTextType: 0x03u);
        log.OnSelfSent(ChatKind.Tell, "outgoing", logTextType: 0x04u, targetOrChannel: "Caith");
        Assert.NotNull(vm.LastIncomingTellSender);
        Assert.NotNull(vm.LastOutgoingTellTarget);

        vm.ResetSessionTargets();

        Assert.Null(vm.LastIncomingTellSender);
        Assert.Null(vm.LastOutgoingTellTarget);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void LastOutgoingTellTarget_StartsNull()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);
        Assert.Null(vm.LastOutgoingTellTarget);
    }

    [Fact]
    public void OnSelfSentTell_PopulatesLastOutgoingTellTarget()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        log.OnSelfSent(ChatKind.Tell, "hi there", logTextType: 0x04u, targetOrChannel: "Caith");

        Assert.Equal("Caith", vm.LastOutgoingTellTarget);
        // Inbound-tell tracker must NOT pick up an outgoing echo —
        // the SenderGuid==0 discriminator separates the two paths.
        Assert.Null(vm.LastIncomingTellSender);
    }

    [Fact]
    public void IncomingTell_DoesNotTouchOutgoingTarget()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        log.OnTellReceived("Bestie", "psst", senderGuid: 0x5000_0042, logTextType: 0x03u);

        Assert.Equal("Bestie", vm.LastIncomingTellSender);
        Assert.Null(vm.LastOutgoingTellTarget);
    }

    [Fact]
    public void OutgoingThenIncoming_TracksBothIndependently()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        log.OnSelfSent(ChatKind.Tell, "hi", logTextType: 0x04u, targetOrChannel: "Caith");
        log.OnTellReceived("Bestie", "psst", senderGuid: 0x5000_0042, logTextType: 0x03u);

        Assert.Equal("Caith",  vm.LastOutgoingTellTarget);
        Assert.Equal("Bestie", vm.LastIncomingTellSender);
    }

    [Fact]
    public void ShowFps_WithProvider_AppendsFormattedLine()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log) { FpsProvider = () => 144.2f };

        vm.ShowFps();

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(ChatKind.System, entry.Kind);
        Assert.Equal("Framerate: 144.2 FPS", entry.Text);
    }

    [Fact]
    public void ShowFps_NoProvider_StillProducesDiagnosticLine()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        vm.ShowFps();

        var entry = Assert.Single(log.Snapshot());
        Assert.Contains("provider unavailable", entry.Text);
    }

    [Fact]
    public void ShowLocation_WithProvider_AppendsFormattedLine()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log) { PositionProvider = () => new Vector3(123.4f, 567.8f, 60f) };

        vm.ShowLocation();

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("Location: (123.4, 567.8, 60.0)", entry.Text);
    }

    [Fact]
    public void ShowLocation_NoProvider_StillProducesDiagnosticLine()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        vm.ShowLocation();

        Assert.Contains("provider unavailable", log.Snapshot()[0].Text);
    }


    [Fact]
    public void ShowInterfaceText_WithHook_InvokesHook_NeverTouchesTheChatLog()
    {
        var log = new ChatLog();
        var received = new List<string>();
        var vm = new ChatVM(log) { OnInterfaceText = received.Add };

        vm.ShowInterfaceText("Someone must @tell you first!");

        Assert.Equal("Someone must @tell you first!", Assert.Single(received));
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void ShowInterfaceText_NoHook_FallsBackToChatLog_TaggedClientLocal()
    {
        var log = new ChatLog();
        var vm  = new ChatVM(log);

        vm.ShowInterfaceText("Someone must @tell you first!");

        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("Someone must @tell you first!", entry.Text);
        Assert.Equal((uint)RetailLogTextType.ClientLocal, entry.LogTextType);
        Assert.Equal(ChatKind.System, entry.Kind);
    }
}
