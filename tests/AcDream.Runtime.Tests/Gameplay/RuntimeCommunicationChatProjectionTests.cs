using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCommunicationChatProjectionTests
{
    [Fact]
    public void TheProjectedEntryCarriesTheTextClassCombatKindAndArrivalTime()
    {
        using var state = new RuntimeCommunicationState();
        var observer = new RecordingObserver();
        using IDisposable subscription = state.Events.Subscribe(observer);
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        state.Chat.OnCombatLine(
            "Your Olthoi Worker is hit for 12 points of damage.",
            logTextType: 0x0Eu,
            kind: CombatLineKind.Info);

        RuntimeChatEntry entry = Assert.Single(observer.Entries);
        Assert.Equal(0x0E, entry.LogTextType);
        Assert.Equal(1, entry.CombatKind);
        Assert.True(entry.Received >= before);
    }

    [Fact]
    public void ASuppressedLineIsNeverProjectedToObservers()
    {
        using var state = new RuntimeCommunicationState();
        var observer = new RecordingObserver();
        using IDisposable subscription = state.Events.Subscribe(observer);
        using IDisposable filter = state.Chat.Filters.Register(
            static candidate => candidate.Text.StartsWith(
                "noise",
                StringComparison.Ordinal));

        state.Chat.OnSystemMessage("noise to drop", 0u);
        state.Chat.OnSystemMessage("signal to keep", 0u);

        RuntimeChatEntry entry = Assert.Single(observer.Entries);
        Assert.Equal("signal to keep", entry.Text);
    }

    [Fact]
    public void AStatusNoticeIsOfferedToTheFiltersUnderItsOwnKind()
    {
        using var state = new RuntimeCommunicationState();
        var seen = new List<PluginChatMessage>();
        using IDisposable filter = state.Chat.Filters.Register(candidate =>
        {
            seen.Add(candidate);
            return false;
        });

        state.AddText("You're too busy!", RetailLogTextType.ClientLocal);

        PluginChatMessage candidate = Assert.Single(seen);
        Assert.Equal(PluginChatMessage.StatusTextKind, candidate.Kind);
        Assert.Equal("You're too busy!", candidate.Text);
        Assert.Equal((int)RetailLogTextType.ClientLocal, candidate.LogTextType);
        state.SpewBox.Tick(0d);
        Assert.Equal(1, state.SpewBox.Count);
    }

    [Fact]
    public void ASuppressedStatusNoticeIsNeverShown()
    {
        using var state = new RuntimeCommunicationState();
        using IDisposable filter = state.Chat.Filters.Register(
            static candidate =>
                candidate.Kind == PluginChatMessage.StatusTextKind);

        state.AddText("You're too busy!", RetailLogTextType.ClientLocal);

        state.SpewBox.Tick(0d);
        Assert.Equal(0, state.SpewBox.Count);
        Assert.Equal(0, state.Chat.Count);
    }

    [Fact]
    public void AStatusFilterDoesNotTouchOrdinaryTranscriptLines()
    {
        using var state = new RuntimeCommunicationState();
        using IDisposable filter = state.Chat.Filters.Register(
            static candidate =>
                candidate.Kind == PluginChatMessage.StatusTextKind);

        state.AddText("A regular line.", RetailLogTextType.Default);

        Assert.Equal(1, state.Chat.Count);
    }

    [Fact]
    public void TheDeathReportReachesSubscribersUntilTheStateIsDisposed()
    {
        var state = new RuntimeCommunicationState();
        var messages = new List<string>();
        state.LocalPlayerDied += messages.Add;

        state.ReportLocalPlayerDeath("You have died!");
        state.Dispose();
        state.ReportLocalPlayerDeath("after teardown");

        Assert.Equal(["You have died!"], messages);
    }

    private sealed class RecordingObserver : IRuntimeCommunicationObserver
    {
        internal List<RuntimeChatEntry> Entries { get; } = [];

        public void OnChat(in RuntimeCommunicationEvent delta) =>
            Entries.Add(delta.Entry);
    }
}
