using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatTargetSelectorTests
{
    [Fact]
    public void FirstValidCandidateBeatsTheEmptyBestSoFar()
    {
        CombatTargetCandidate chosen = Select(
            [Candidate(10, priority: 0, distance: 12, angle: 40)],
            TargetSelectionMethod.Range);

        Assert.Equal(10u, chosen.ObjectId);
    }

    [Fact]
    public void PriorityWinsBeforeDistance()
    {
        // dz.cs:740-750 — step 1.
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 1, distance: 2),
                Candidate(20, priority: 4, distance: 12),
            ],
            TargetSelectionMethod.Range);

        Assert.Equal(20u, chosen.ObjectId);
    }

    [Fact]
    public void DebuffEachFirstAllLetsDebuffNeedBeatAHigherPriority()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 4, distance: 2),
                Candidate(20, priority: 1, distance: 12, needsDebuff: true),
            ],
            TargetSelectionMethod.Range,
            DebuffEachFirst.All);

        Assert.Equal(20u, chosen.ObjectId);
    }

    [Fact]
    public void DebuffEachFirstPriorityNeverCrossesAPriorityDifference()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 4, distance: 2),
                Candidate(20, priority: 1, distance: 12, needsDebuff: true),
            ],
            TargetSelectionMethod.Range,
            DebuffEachFirst.Priority);

        Assert.Equal(10u, chosen.ObjectId);
    }

    [Fact]
    public void DebuffEachFirstPriorityBreaksATieOnDebuffNeed()
    {
        // dz.cs:771-781 — step 2 within one priority tier.
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 2, distance: 2),
                Candidate(20, priority: 2, distance: 12, needsDebuff: true),
            ],
            TargetSelectionMethod.Range,
            DebuffEachFirst.Priority);

        Assert.Equal(20u, chosen.ObjectId);
    }

    [Fact]
    public void DebuffEachFirstOneSkipsTheDebuffNeedStepEntirely()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 2, distance: 2),
                Candidate(20, priority: 2, distance: 12, needsDebuff: true),
            ],
            TargetSelectionMethod.Range,
            DebuffEachFirst.One);

        Assert.Equal(10u, chosen.ObjectId);
    }

    [Fact]
    public void HigherUrgencyScoreWinsOutright()
    {
        // dz.cs:782-787 — step 3, ahead of TargetLock, wield-match, sticky and
        // the selection method.
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 2, distance: 2),
                Candidate(20, priority: 2, distance: 12, urgency: 3),
            ],
            TargetSelectionMethod.Range);

        Assert.Equal(20u, chosen.ObjectId);
    }

    [Fact]
    public void TargetLockOnlyBreaksATieAndNeverBeatsPriorityOrUrgency()
    {
        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 1, distance: 2, lockSelection: true),
                Candidate(20, priority: 4, distance: 12),
            ],
            TargetSelectionMethod.Range).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 2, lockSelection: true),
                Candidate(20, priority: 2, distance: 12, urgency: 1),
            ],
            TargetSelectionMethod.Range).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 2),
                Candidate(20, priority: 2, distance: 12, lockSelection: true),
            ],
            TargetSelectionMethod.Range).ObjectId);
    }

    [Fact]
    public void WieldMatchPrefersFewerRewieldsButOnlyUnderTargetSelectMethodBoth()
    {
        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 2, angle: 1, weapon: 7, offhand: 8),
                Candidate(20, priority: 2, distance: 3, angle: 9, weapon: 5, offhand: 6),
            ],
            TargetSelectionMethod.Both,
            angleRange: 10d,
            wieldedWeapon: 5u,
            wieldedOffhand: 6u).ObjectId);

        Assert.Equal(10u, Select(
            [
                Candidate(10, priority: 2, distance: 2, angle: 1, weapon: 7, offhand: 8),
                Candidate(20, priority: 2, distance: 3, angle: 9, weapon: 5, offhand: 6),
            ],
            TargetSelectionMethod.Range,
            angleRange: 10d,
            wieldedWeapon: 5u,
            wieldedOffhand: 6u).ObjectId);
    }

    [Fact]
    public void StickyPreviousTargetHoldsAgainstAnEqualNewcomer()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 2, distance: 2),
                Candidate(20, priority: 2, distance: 12, lastTarget: true),
            ],
            TargetSelectionMethod.Range);

        Assert.Equal(20u, chosen.ObjectId);
    }

    [Fact]
    public void StickyLastTargetStillLosesToAHigherPriority()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 4, distance: 12),
                Candidate(20, priority: 2, distance: 2, lastTarget: true),
            ],
            TargetSelectionMethod.Range);

        Assert.Equal(10u, chosen.ObjectId);
    }

    [Fact]
    public void SelectionMethodDistancePrefersNearerThenAngle()
    {
        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 12, angle: 1),
                Candidate(20, priority: 2, distance: 2, angle: 40),
            ],
            TargetSelectionMethod.Range).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 5, angle: 40),
                Candidate(20, priority: 2, distance: 5, angle: 1),
            ],
            TargetSelectionMethod.Range).ObjectId);
    }

    [Fact]
    public void SelectionMethodAnglePrefersStraighterThenDistance()
    {
        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 2, angle: 40),
                Candidate(20, priority: 2, distance: 12, angle: 1),
            ],
            TargetSelectionMethod.Angle).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 12, angle: 5),
                Candidate(20, priority: 2, distance: 2, angle: 5),
            ],
            TargetSelectionMethod.Angle).ObjectId);
    }

    [Fact]
    public void SelectionMethodBothPrefersInsideTheCutoffRegardlessOfAngle()
    {
        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 40, angle: 1),
                Candidate(20, priority: 2, distance: 3, angle: 80),
            ],
            TargetSelectionMethod.Both,
            angleRange: 10d).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 3, angle: 80),
                Candidate(20, priority: 2, distance: 8, angle: 5),
            ],
            TargetSelectionMethod.Both,
            angleRange: 10d).ObjectId);

        Assert.Equal(20u, Select(
            [
                Candidate(10, priority: 2, distance: 40, angle: 1),
                Candidate(20, priority: 2, distance: 20, angle: 70),
            ],
            TargetSelectionMethod.Both,
            angleRange: 10d).ObjectId);
    }

    [Fact]
    public void AnExactTieKeepsTheIncumbent()
    {
        CombatTargetCandidate chosen = Select(
            [
                Candidate(10, priority: 2, distance: 5, angle: 5),
                Candidate(20, priority: 2, distance: 5, angle: 5),
            ],
            TargetSelectionMethod.Range);

        Assert.Equal(10u, chosen.ObjectId);
    }

    [Fact]
    public void NoCandidatesYieldsTheEmptyBest()
    {
        // dz.cs:925 — `if (this.a.b != 0)`.
        Assert.Equal(0u, Select([], TargetSelectionMethod.Range).ObjectId);
    }

    private static CombatTargetCandidate Select(
        IReadOnlyList<CombatTargetCandidate> candidates,
        TargetSelectionMethod method,
        DebuffEachFirst debuffEachFirst = DebuffEachFirst.One,
        double angleRange = 5d,
        uint wieldedWeapon = 0u,
        uint wieldedOffhand = 0u) =>
        CombatTargetSelector.Select(
            candidates,
            debuffEachFirst,
            method,
            angleRange,
            wieldedWeapon,
            wieldedOffhand);

    private static CombatTargetCandidate Candidate(
        uint objectId,
        int priority,
        double distance,
        double angle = 0d,
        int urgency = 0,
        bool needsDebuff = false,
        bool lockSelection = false,
        bool lastTarget = false,
        uint weapon = 0u,
        uint offhand = 0u) => new(
            new PluginCombatTarget(
                objectId,
                $"Monster {objectId}",
                objectId + 1000u,
                (float)distance,
                (float)angle,
                true,
                1f),
            new ResolvedMonsterRule(new MonsterRule("DEFAULT", priority), null),
            priority,
            distance,
            angle,
            urgency,
            needsDebuff,
            lockSelection,
            lastTarget,
            weapon,
            offhand);
}
