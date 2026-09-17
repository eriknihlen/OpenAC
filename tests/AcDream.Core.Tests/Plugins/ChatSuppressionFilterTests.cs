using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class ChatSuppressionFilterTests
{
    [Fact]
    public void ASuppressedLineNeverReachesTheTranscriptOrItsListeners()
    {
        var log = new ChatLog();
        var appended = new List<string>();
        log.EntryAppended += entry => appended.Add(entry.Text);
        using IDisposable registration = log.Filters.Register(
            static candidate => candidate.Text.Contains(
                "sells",
                StringComparison.Ordinal));

        log.OnSystemMessage("Someone sells a trade note.", 0u);
        log.OnSystemMessage("Someone waves.", 0u);

        Assert.Equal(["Someone waves."], appended);
        ChatEntry kept = Assert.Single(log.Snapshot());
        Assert.Equal("Someone waves.", kept.Text);
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void DisposingARegistrationStopsItSuppressing()
    {
        var log = new ChatLog();
        IDisposable registration = log.Filters.Register(static _ => true);

        log.OnSystemMessage("dropped", 0u);
        Assert.Equal(0, log.Count);

        registration.Dispose();
        log.OnSystemMessage("kept", 0u);

        ChatEntry entry = Assert.Single(log.Snapshot());
        Assert.Equal("kept", entry.Text);
    }

    [Fact]
    public void FiltersRunInRegistrationOrderAndStopAtTheFirstRejection()
    {
        var filters = new ChatSuppressionFilters();
        var order = new List<string>();
        using IDisposable first = filters.Register(candidate =>
        {
            order.Add("first");
            return false;
        });
        using IDisposable second = filters.Register(candidate =>
        {
            order.Add("second");
            return true;
        });
        using IDisposable third = filters.Register(candidate =>
        {
            order.Add("third");
            return false;
        });

        Assert.True(filters.ShouldSuppress(Candidate("anything")));
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public void AThrowingFilterSuppressesNothingAndIsReported()
    {
        var filters = new ChatSuppressionFilters();
        var faults = new List<Exception>();
        filters.FilterFaulted = faults.Add;
        using IDisposable faulty = filters.Register(
            static _ => throw new InvalidOperationException("boom"));

        Assert.False(filters.ShouldSuppress(Candidate("kept")));
        Exception fault = Assert.Single(faults);
        Assert.Equal("boom", fault.Message);
    }

    [Fact]
    public void TheFilterCandidateCarriesTheTextClassCombatKindAndArrivalTime()
    {
        var log = new ChatLog();
        PluginChatMessage? seen = null;
        using IDisposable registration = log.Filters.Register(candidate =>
        {
            seen = candidate;
            return false;
        });
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        log.OnCombatLine(
            "You are hit for 7 points of damage.",
            logTextType: 0x11u,
            kind: CombatLineKind.Warning);

        Assert.NotNull(seen);
        Assert.Equal((int)ChatKind.Combat, seen!.Value.Kind);
        Assert.Equal(0x11, seen.Value.LogTextType);
        Assert.Equal(2, seen.Value.CombatKind);
        Assert.True(seen.Value.Received >= before);
    }

    [Fact]
    public void ALineThatIsNotACombatLineReportsNoCombatKind()
    {
        var log = new ChatLog();
        PluginChatMessage? seen = null;
        using IDisposable registration = log.Filters.Register(candidate =>
        {
            seen = candidate;
            return false;
        });

        log.OnSystemMessage("Welcome.", 0u);

        Assert.NotNull(seen);
        Assert.Equal(0, seen!.Value.CombatKind);
    }

    private static PluginChatMessage Candidate(string text) =>
        new(0UL, 0u, 0, string.Empty, text, string.Empty);
}
