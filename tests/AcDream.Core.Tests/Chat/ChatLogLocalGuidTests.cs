using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class ChatLogLocalGuidTests
{
    [Fact]
    public void OnLocalSpeech_OwnGuidMatch_SubstitutesYou()
    {
        var log = new ChatLog();
        log.SetLocalPlayerGuid(0x5000_000A);

        log.OnLocalSpeech("+Acdream", "hello world",
            senderGuid: 0x5000_000A, isRanged: false, logTextType: 0x02u);

        var entry = log.Snapshot()[0];
        Assert.Equal(ChatKind.LocalSpeech, entry.Kind);
        Assert.Equal("You", entry.Sender);
        Assert.Equal("hello world", entry.Text);
    }

    [Fact]
    public void OnLocalSpeech_DifferentGuid_KeepsSenderName()
    {
        var log = new ChatLog();
        log.SetLocalPlayerGuid(0x5000_000A);

        log.OnLocalSpeech("Caith", "hi",
            senderGuid: 0x5000_0042, isRanged: false, logTextType: 0x02u);

        Assert.Equal("Caith", log.Snapshot()[0].Sender);
    }

    [Fact]
    public void OnLocalSpeech_NoLocalGuidSet_FallsBackToEmptySubstitution()
    {
        // Pre-login (guid not yet known), the existing empty-sender
        // substitution still applies — server-driven ranged echoes
        // arrive with sender="" before the player has a guid.
        var log = new ChatLog();
        log.OnLocalSpeech("", "anyone home?",
            senderGuid: 0u, isRanged: true, logTextType: 0x02u);

        Assert.Equal("You", log.Snapshot()[0].Sender);
        Assert.Equal(ChatKind.RangedSpeech, log.Snapshot()[0].Kind);
    }

    [Fact]
    public void ResetSessionIdentity_RetainsTranscriptButClearsGuidAndDedupeWindow()
    {
        var log = new ChatLog();
        const uint oldGuid = 0x50000001u;
        const uint newGuid = 0x50000002u;
        log.SetLocalPlayerGuid(oldGuid);
        log.OnSystemMessage("session boundary", 1u);
        log.OnLocalSpeech("Old", "before", oldGuid, isRanged: false, logTextType: 0x02u);

        log.ResetSessionIdentity();
        log.OnSystemMessage("session boundary", 1u);
        log.OnLocalSpeech("Old", "after", oldGuid, isRanged: false, logTextType: 0x02u);
        log.SetLocalPlayerGuid(newGuid);
        log.OnLocalSpeech("New", "new", newGuid, isRanged: false, logTextType: 0x02u);

        ChatEntry[] entries = log.Snapshot();
        Assert.Equal(5, entries.Length);
        Assert.Equal("You", entries[1].Sender);
        Assert.Equal("Old", entries[3].Sender);
        Assert.Equal("You", entries[4].Sender);
    }
}
