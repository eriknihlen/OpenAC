using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class SpellCastTrackerTests
{
    private static SpellCastTracker Armed(
        out List<SpellCastOutcomeInfo> outcomes,
        bool hitsMultipleTargets = false,
        string spellName = "Flame Bolt VII",
        string targetName = "Drudge")
    {
        var tracker = new SpellCastTracker();
        List<SpellCastOutcomeInfo> log = [];
        tracker.Completed += log.Add;
        outcomes = log;
        tracker.Begin(100u, spellName, 10u, targetName, hitsMultipleTargets, 5L);
        return tracker;
    }

    private static PluginCastCompletion Receipt(
        long revision = 6L,
        uint spellId = 100u,
        uint weenieError = 0u) =>
        new(revision, spellId, 10u, weenieError);

    [Fact]
    public void ArmingLeavesIdle()
    {
        SpellCastTracker tracker = Armed(out _);

        Assert.True(tracker.IsBusy);
        Assert.Equal(SpellCastTrackerState.AwaitingLaunch, tracker.State);
        Assert.Equal(100u, tracker.SpellId);
        Assert.Equal(10u, tracker.TargetObjectId);
    }

    [Fact]
    public void ACleanReceiptStartsTheResultWaitAndDoesNotReleaseTheLatch()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);

        tracker.ObserveCompletion(Receipt());

        Assert.True(tracker.IsBusy);
        Assert.Equal(SpellCastTrackerState.AwaitingResult, tracker.State);
        Assert.Empty(outcomes);
    }

    /// <summary>
    /// <c>gj.cs:442-453</c> — a success line ends the wait.
    /// Mutation: drop the <c>Success</c> arm from <c>ObserveChat</c> and this
    /// fails, because the tracker stays busy.
    /// </summary>
    [Fact]
    public void ASuccessLineReleasesTheLatch()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "You blast Drudge for 42 points with Flame Bolt VII.");

        Assert.False(tracker.IsBusy);
        Assert.Equal(SpellCastOutcome.Success, Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public void AKillLineReleasesTheLatchAndNamesTheTarget()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "You killed Drudge!");

        Assert.False(tracker.IsBusy);
        SpellCastOutcomeInfo info = Assert.Single(outcomes);
        Assert.Equal(SpellCastOutcome.Kill, info.Outcome);
        Assert.Equal(10u, info.TargetObjectId);
    }

    [Fact]
    public void AResultNamingAnotherSpellIsNotThisCastsResult()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "You blast Drudge for 42 points with Frost Bolt VII.");

        Assert.True(tracker.IsBusy);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void AResultNamingAnotherTargetIsNotThisCastsResult()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "You killed Olthoi Soldier!");

        Assert.True(tracker.IsBusy);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void APermanentFailIsIgnoredForAMultiTargetSpell()
    {
        SpellCastTracker tracker = Armed(
            out List<SpellCastOutcomeInfo> outcomes,
            hitsMultipleTargets: true);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "Drudge is an invalid target.");

        Assert.True(tracker.IsBusy);
        Assert.Empty(outcomes);
    }

    /// <summary>
    /// <c>gj.cs:409-413</c> — and it DOES end the wait for a single-target one.
    /// Mutation: return <c>None</c> from the <c>PermanentFail</c> arm and this
    /// fails.
    /// </summary>
    [Fact]
    public void APermanentFailEndsTheWaitForASingleTargetSpell()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(1uL, "Drudge is an invalid target.");

        Assert.False(tracker.IsBusy);
        Assert.Equal(
            SpellCastOutcome.PermanentFail,
            Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public void TheResultWaitExpiresAtFourTicksOfNineHundredAndSevenMilliseconds()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.Advance(3.6d);
        Assert.True(tracker.IsBusy);
        Assert.Empty(outcomes);

        tracker.Advance(0.03d);

        Assert.False(tracker.IsBusy);
        Assert.Equal(
            SpellCastOutcome.ResultTimeout,
            Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public void TheLaunchWaitExpiresAfterFiveSeconds()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);

        tracker.Advance(4.9d);
        Assert.True(tracker.IsBusy);

        tracker.Advance(0.2d);

        Assert.False(tracker.IsBusy);
        Assert.Equal(
            SpellCastOutcome.LaunchTimeout,
            Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public void ARefusedReceiptReleasesImmediatelyAndCarriesTheError()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);

        tracker.ObserveCompletion(Receipt(weenieError: 0x1Du));

        Assert.False(tracker.IsBusy);
        SpellCastOutcomeInfo info = Assert.Single(outcomes);
        Assert.Equal(SpellCastOutcome.Rejected, info.Outcome);
        Assert.Equal(0x1Du, info.WeenieError);
    }

    [Fact]
    public void TheReceiptTheCastWasIssuedAgainstIsNotItsAnswer()
    {
        SpellCastTracker tracker = Armed(out _);

        tracker.ObserveCompletion(Receipt(revision: 5L));

        Assert.Equal(SpellCastTrackerState.AwaitingLaunch, tracker.State);
    }

    [Fact]
    public void TheSameChatLineIsFoldedOnce()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());

        tracker.ObserveChat(7uL, "You killed Drudge!");
        tracker.Begin(100u, "Flame Bolt VII", 10u, "Drudge", false, 8L);
        tracker.ObserveCompletion(Receipt(revision: 9L));
        tracker.ObserveChat(7uL, "You killed Drudge!");

        Assert.Single(outcomes);
        Assert.True(tracker.IsBusy);
    }

    [Fact]
    public void TheSameReceiptIsFoldedOnce()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);
        tracker.ObserveCompletion(Receipt());
        tracker.Advance(3.0d);

        tracker.ObserveCompletion(Receipt());
        tracker.Advance(0.7d);

        Assert.Equal(
            SpellCastOutcome.ResultTimeout,
            Assert.Single(outcomes).Outcome);
    }

    [Fact]
    public void OnlyTheAwaitedTargetsDeletionEndsTheWait()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);

        tracker.ResetForTarget(11u);
        Assert.True(tracker.IsBusy);

        tracker.ResetForTarget(10u);

        Assert.False(tracker.IsBusy);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void AResultArrivingBeforeTheReceiptStillEndsTheWait()
    {
        SpellCastTracker tracker = Armed(out List<SpellCastOutcomeInfo> outcomes);

        tracker.ObserveChat(1uL, "You killed Drudge!");

        Assert.False(tracker.IsBusy);
        Assert.Equal(SpellCastOutcome.Kill, Assert.Single(outcomes).Outcome);
    }

    [Theory]
    [InlineData(new uint[0], 0u, false)]
    [InlineData(new[] { 110u, 77u }, 0u, true)]
    [InlineData(new[] { 77u, 110u }, 0u, false)]
    [InlineData(new uint[0], 638u, true)]
    public void HitsMultipleTargetsMatchesMySpell(
        uint[] components,
        uint family,
        bool expected)
    {
        var spell = new PluginSpellInfo(
            100u,
            "Spell",
            family,
            Tier: 1,
            Difficulty: 10,
            ManaCost: 5,
            DurationSeconds: 0f,
            School: 34u,
            "d",
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            FormulaComponentIds = components,
        };

        Assert.Equal(expected, SpellCastTracker.HitsMultipleTargetsFor(spell));
    }
    [Fact]
    public void TheSpokenEchoMovesTheTrackerFromLaunchToTheResultWait()
    {
        var tracker = new SpellCastTracker();
        tracker.Begin(1u, "Strength Self VI", 5u, "yourself", false, 0L, "casfaen");
        Assert.Equal(SpellCastTrackerState.AwaitingLaunch, tracker.State);

        // gj.cs:352 — ToLowerInvariant().Replace(" ", "").
        tracker.ObserveChat(1uL, "Cas Faen", ownSpeech: true);

        Assert.Equal(SpellCastTrackerState.AwaitingResult, tracker.State);
    }

    [Fact]
    public void ASpokenEchoNamingAnotherSpellEndsTheWait()
    {
        var tracker = new SpellCastTracker();
        tracker.Begin(1u, "Strength Self VI", 5u, "yourself", false, 0L, "casfaen");

        tracker.ObserveChat(1uL, "Zharalim Retaanu", ownSpeech: true);

        Assert.Equal(SpellCastTrackerState.Idle, tracker.State);
        Assert.False(tracker.IsBusy);
    }

    [Fact]
    public void AnotherPlayerSpeakingTheSameWordsIsNotOurEcho()
    {
        var tracker = new SpellCastTracker();
        tracker.Begin(1u, "Strength Self VI", 5u, "yourself", false, 0L, "casfaen");

        tracker.ObserveChat(1uL, "Cas Faen", ownSpeech: false);

        Assert.Equal(SpellCastTrackerState.AwaitingLaunch, tracker.State);
    }

    [Fact]
    public void TheRavenFurySayingClassifiesItAsMultiTarget()
    {
        PluginSpellInfo raven = new(
            1u,
            "Curse of Raven Fury",
            Family: 500u,
            Tier: 1,
            Difficulty: 10,
            ManaCost: 5,
            DurationSeconds: 60f,
            School: 31u,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            Saying = "tugakquati",
        };

        Assert.True(SpellCastTracker.HitsMultipleTargetsFor(raven));
        Assert.False(SpellCastTracker.HitsMultipleTargetsFor(
            raven with { Saying = "casfaen" }));
    }

}
