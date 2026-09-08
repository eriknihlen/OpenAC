using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatFailureTrackerTests
{
    [Fact]
    public void HealthUpdateCancelsAccumulatedSuccessfulMisses()
    {
        var tracker = new CombatFailureTracker();
        var settings = new CombatSettings
        {
            BlacklistMonsterAttemptCount = 2,
            BlacklistMonsterTimeoutSeconds = 30,
        };
        tracker.ObserveTargets([Target(1)], 0, settings);
        tracker.BeginAttack(10, 1);
        tracker.RecordSuccessfulAttack(10, 1, settings);

        tracker.ObserveTargets([Target(2)], 2, settings);
        tracker.BeginAttack(10, 2);
        tracker.RecordSuccessfulAttack(10, 3, settings);

        Assert.Equal(
            CombatSuppressionReason.None,
            tracker.Reason(10, 3));
    }

    [Fact]
    public void RepeatedSuccessfulMissesBlacklistUntilTimeout()
    {
        var tracker = new CombatFailureTracker();
        var settings = new CombatSettings
        {
            BlacklistMonsterAttemptCount = 2,
            BlacklistMonsterTimeoutSeconds = 20,
        };
        tracker.ObserveTargets([Target(1)], 0, settings);
        tracker.BeginAttack(10, 1);
        tracker.RecordSuccessfulAttack(10, 1, settings);
        tracker.BeginAttack(10, 1);
        tracker.RecordSuccessfulAttack(10, 2, settings);

        Assert.Equal(
            CombatSuppressionReason.Blacklisted,
            tracker.Reason(10, 10));

        tracker.ObserveTargets([Target(1)], 23, settings);
        Assert.Equal(
            CombatSuppressionReason.None,
            tracker.Reason(10, 23));
    }

    [Fact]
    public void FailedSpellStartsMarkPersistentGhost()
    {
        var tracker = new CombatFailureTracker();
        var settings = new CombatSettings
        {
            DeleteGhostMonsters = true,
            GhostMonsterSpellAttemptCount = 2,
        };
        tracker.ObserveTargets([Target(0)], 0, settings);

        tracker.RecordSpellDidNotStart(10, settings);
        Assert.Equal(CombatSuppressionReason.None, tracker.Reason(10, 0));
        tracker.RecordSpellDidNotStart(10, settings);
        Assert.Equal(CombatSuppressionReason.Ghost, tracker.Reason(10, 100));
    }

    [Fact]
    public void HealthAgeDetectorRequiresEngagementAndConfiguredDelay()
    {
        var tracker = new CombatFailureTracker();
        var settings = new CombatSettings
        {
            DeleteGhostMonstersByHealthTracker = true,
            GhostDeleteHealthTrackerSeconds = 10,
        };
        PluginCombatTarget stale = Target(1) with
        {
            SecondsSinceHealthUpdate = 50,
        };

        tracker.ObserveTargets([stale], 0, settings);
        Assert.Equal(CombatSuppressionReason.None, tracker.Reason(10, 0));
        tracker.BeginEngagement(10, 0);
        tracker.ObserveTargets([stale], 9.9, settings);
        Assert.Equal(CombatSuppressionReason.None, tracker.Reason(10, 9.9));
        tracker.ObserveTargets([stale], 10, settings);
        Assert.Equal(CombatSuppressionReason.Ghost, tracker.Reason(10, 10));
    }

    private static PluginCombatTarget Target(long healthRevision) => new(
        10,
        "Drudge",
        100,
        5,
        0,
        true,
        1f)
    {
        HealthRevision = healthRevision,
        SecondsSinceHealthUpdate = 0,
    };
}
