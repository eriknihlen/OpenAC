using AcDream.Bot.Combat;
using AcDream.Bot.Profiles;
using AcDream.Bot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Tests;

public sealed class LineOfSightServiceTests
{
    private const uint Target = 0x8000_0001u;

    private static (FakeAutomationSurface Surface, LineOfSightService Service, TickClock Clock) Build(
        LineOfSightSettings? settings = null)
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        LineOfSightSettings options = settings ?? new LineOfSightSettings();
        return (surface, new LineOfSightService(surface, surface, clock, () => options), clock);
    }

    [Fact]
    public void KindFollowsTheStyleAndTheArcSettingOutdoorsOnly()
    {
        var straight = new LineOfSightSettings();
        var arc = new LineOfSightSettings { WarSpellPath = WarSpellPath.Arc };
        var arcEverywhere = arc with { StraightPathIndoors = false };

        Assert.Equal(PluginProjectilePathKind.Missile, LineOfSightService.KindFor(CombatStyle.Missile, arc, outdoors: true));
        Assert.Equal(PluginProjectilePathKind.Straight, LineOfSightService.KindFor(CombatStyle.Magic, straight, outdoors: true));
        Assert.Equal(PluginProjectilePathKind.Arc, LineOfSightService.KindFor(CombatStyle.Magic, arc, outdoors: true));
        Assert.Equal(PluginProjectilePathKind.Straight, LineOfSightService.KindFor(CombatStyle.Magic, arc, outdoors: false));
        Assert.Equal(PluginProjectilePathKind.Arc, LineOfSightService.KindFor(CombatStyle.Magic, arcEverywhere, outdoors: false));
        Assert.False(LineOfSightService.AppliesTo(CombatStyle.Melee));
    }

    [Fact]
    public void LaunchSpeedsAndSweepOptionsReachTheRequest()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build(new LineOfSightSettings
        {
            WarSpellPath = WarSpellPath.Arc,
            ArcLaunchSpeed = 20f,
            MissileLaunchSpeed = 33f,
            ProjectileRadius = 0.4f,
            StepDistanceMeters = 2f,
            MaximumCollisionChecks = 40,
        });

        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.High, outdoors: true);
        PluginProjectilePathRequest request = surface.LastPathRequest!.Value;
        Assert.Equal(PluginProjectilePathKind.Arc, request.Kind);
        Assert.Equal(20f, request.LaunchSpeed);
        Assert.Equal(0.4f, request.ProjectileRadius);
        Assert.Equal(2f, request.StepDistance);
        Assert.Equal(40, request.MaximumCollisionChecks);
        Assert.Equal(PluginAttackHeight.High, request.TargetHeight);

        service.Evaluate(Target, CombatStyle.Missile, PluginAttackHeight.Low, outdoors: false);
        Assert.Equal(33f, surface.LastPathRequest!.Value.LaunchSpeed);
    }

    [Fact]
    public void VerdictsAreCachedUntilTheyAge()
    {
        (FakeAutomationSurface surface, LineOfSightService service, TickClock clock) =
            Build(new LineOfSightSettings { CacheSeconds = 1d });

        LineOfSightVerdict first = service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        LineOfSightVerdict again = service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.True(first.IsClear);
        Assert.False(first.FromCache);
        Assert.True(again.FromCache);
        Assert.Equal(1, surface.PathEvaluations);

        // Another height is another sweep.
        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.High, true);
        Assert.Equal(2, surface.PathEvaluations);

        clock.Advance(1.5);
        Assert.False(service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true).FromCache);
        Assert.Equal(3, surface.PathEvaluations);
    }

    [Fact]
    public void AnyHeightTriesTheOthersWhenThePreferredIsBlocked()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build();
        surface.PathStatuses[(Target, PluginAttackHeight.Medium)] = PluginProjectilePathStatus.Blocked;
        surface.PathStatuses[(Target, PluginAttackHeight.High)] = PluginProjectilePathStatus.Blocked;

        LineOfSightVerdict verdict = service.EvaluateAnyHeight(Target, CombatStyle.Missile, PluginAttackHeight.Medium, true);

        Assert.True(verdict.IsClear);
        Assert.Equal(PluginAttackHeight.Low, verdict.Height);
        Assert.Equal(["2147483649:Missile:Medium", "2147483649:Missile:High", "2147483649:Missile:Low"], surface.PathQueries);
        Assert.Equal(0, service.StrikesFor(Target));
    }

    [Fact]
    public void StrikesCountOncePerFreshSweepAndBlacklistThenExpire()
    {
        (FakeAutomationSurface surface, LineOfSightService service, TickClock clock) = Build(new LineOfSightSettings
        {
            BlacklistStrikes = 2,
            BlacklistSeconds = 10d,
            CacheSeconds = 1d,
        });
        surface.BlockPath(Target);

        LineOfSightVerdict blocked = service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.Equal(LineOfSightState.Blocked, blocked.State);
        Assert.Equal(surface.BlockingObjectId, blocked.BlockingObjectId);

        Assert.False(service.ReportBlocked(Target));
        Assert.Equal(1, service.StrikesFor(Target));
        // Re-reading the cached verdict is not a new strike.
        service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.False(service.ReportBlocked(Target));
        Assert.Equal(1, service.StrikesFor(Target));

        clock.Advance(1.5);
        service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.True(service.ReportBlocked(Target));
        Assert.True(service.IsBlacklisted(Target));
        Assert.InRange(service.BlacklistSecondsRemaining(Target), 9.9d, 10d);
        Assert.Equal(LineOfSightState.Blacklisted, service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true).State);
        int sweeps = surface.PathEvaluations;

        clock.Advance(10.5);
        Assert.False(service.IsBlacklisted(Target));
        Assert.Equal(0, service.StrikesFor(Target));
        // The path is swept again once the blacklist has lapsed.
        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.Equal(sweeps + 1, surface.PathEvaluations);
    }

    [Fact]
    public void AClearSweepForgivesEarlierStrikes()
    {
        (FakeAutomationSurface surface, LineOfSightService service, TickClock clock) = Build();
        surface.BlockPath(Target);
        service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        service.ReportBlocked(Target);
        Assert.Equal(1, service.StrikesFor(Target));

        surface.ClearPath(Target);
        clock.Advance(2d);
        Assert.True(service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true).IsClear);
        Assert.Equal(0, service.StrikesFor(Target));
    }

    [Fact]
    public void NoAnswerFromTheHostFailsOpenWithoutStrikes()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build();
        surface.DefaultPathStatus = PluginProjectilePathStatus.BudgetExceeded;

        LineOfSightVerdict verdict = service.EvaluateAnyHeight(Target, CombatStyle.Missile, PluginAttackHeight.Medium, true);
        Assert.Equal(LineOfSightState.Unavailable, verdict.State);
        Assert.True(verdict.IsUsable);
        Assert.False(service.ReportBlocked(Target));

        surface.ProjectilesAvailable = false;
        surface.DefaultPathStatus = PluginProjectilePathStatus.Blocked;
        Assert.True(service.EvaluateAnyHeight(Target, CombatStyle.Missile, PluginAttackHeight.Medium, true).IsUsable);

        (_, LineOfSightService disabled, _) = Build(new LineOfSightSettings { Enabled = false });
        Assert.Equal(LineOfSightState.Unavailable, disabled.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true).State);
        Assert.Equal(LineOfSightState.Unavailable, service.Evaluate(Target, CombatStyle.Melee, PluginAttackHeight.Medium, true).State);
    }

    [Fact]
    public void DiagnosticsAreRequestedAndShownEveryReadWhileOn()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) =
            Build(new LineOfSightSettings { ShowDebugSamples = true });

        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);

        Assert.True(surface.LastPathRequest!.Value.CaptureDiagnostics);
        Assert.Equal(1, surface.PathEvaluations);
        Assert.Equal(2, surface.ShownSamples.Count);

        (FakeAutomationSurface quiet, LineOfSightService silent, _) = Build();
        silent.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.False(quiet.LastPathRequest!.Value.CaptureDiagnostics);
        Assert.Empty(quiet.ShownSamples);
    }

    [Fact]
    public void ForgetDropsEverythingAboutATarget()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build(new LineOfSightSettings { BlacklistStrikes = 1 });
        surface.BlockPath(Target);
        service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        service.ReportBlocked(Target);
        Assert.True(service.IsBlacklisted(Target));
        Assert.True(service.TryGetLast(Target, out _));

        service.Forget(Target);

        Assert.False(service.IsBlacklisted(Target));
        Assert.False(service.TryGetLast(Target, out _));
        int before = surface.PathEvaluations;
        service.Evaluate(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        Assert.Equal(before + 1, surface.PathEvaluations);
    }

    // ── walking ───────────────────────────────────────────────────────────

    [Fact]
    public void WalkHeadingIsTheDirectOneWhenItIsOpen()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build();

        Assert.True(service.TryFindWalkHeading(Target, 45f, 11f, out WalkVerdict verdict));

        Assert.True(verdict.IsClear);
        Assert.Equal(45f, verdict.HeadingDegrees);
        Assert.Equal(11f, verdict.ClearDistanceMeters);
        Assert.Equal(["45:11:2147483649"], surface.WalkQueries);
        Assert.True(service.ChecksWalking);
    }

    [Fact]
    public void WalkSteersThroughTheFanInOrderOutToTheLookahead()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) =
            Build(new LineOfSightSettings { WalkLookaheadMeters = 4f });
        surface.BlockedWalkHeadings.UnionWith([0, 30, 330]);

        Assert.True(service.TryFindWalkHeading(Target, 0f, 11f, out WalkVerdict verdict));

        Assert.Equal(60f, verdict.HeadingDegrees);
        Assert.Equal(["0:11:2147483649", "30:4:2147483649", "330:4:2147483649", "60:4:2147483649"], surface.WalkQueries);
        Assert.True(service.TryGetLastWalk(Target, out WalkVerdict last));
        Assert.Equal(60f, last.HeadingDegrees);
    }

    [Fact]
    public void NoOpenHeadingArmsAWalkStrikeOncePerFreshProbe()
    {
        (FakeAutomationSurface surface, LineOfSightService service, TickClock clock) =
            Build(new LineOfSightSettings { BlacklistStrikes = 2, CacheSeconds = 1d });
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;

        Assert.False(service.TryFindWalkHeading(Target, 0f, 8f, out WalkVerdict verdict));
        Assert.Equal(LineOfSightState.Blocked, verdict.State);
        Assert.Equal(0f, verdict.HeadingDegrees);
        Assert.Equal(7, surface.WalkQueries.Count);
        Assert.False(service.ReportBlocked(Target));
        Assert.Equal(1, service.StrikesFor(Target));

        // Cached: no new probes, no new strike.
        Assert.False(service.TryFindWalkHeading(Target, 0f, 8f, out _));
        Assert.Equal(7, surface.WalkQueries.Count);
        Assert.False(service.ReportBlocked(Target));
        Assert.Equal(1, service.StrikesFor(Target));

        clock.Advance(1.5);
        Assert.False(service.TryFindWalkHeading(Target, 0f, 8f, out _));
        Assert.True(service.ReportBlocked(Target));
        Assert.True(service.IsBlacklisted(Target));
        Assert.False(service.TryFindWalkHeading(Target, 0f, 8f, out WalkVerdict listed));
        Assert.Equal(LineOfSightState.Blacklisted, listed.State);
    }

    [Fact]
    public void EachSenseForgivesOnlyItsOwnStrikes()
    {
        (FakeAutomationSurface surface, LineOfSightService service, TickClock clock) =
            Build(new LineOfSightSettings { BlacklistStrikes = 4, CacheSeconds = 0.5d });
        surface.BlockPath(Target);
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;

        service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true);
        service.TryFindWalkHeading(Target, 0f, 8f, out _);
        Assert.False(service.ReportBlocked(Target));
        Assert.Equal(2, service.StrikesFor(Target));

        // A clear walk forgives the walk strike, not the shot strike.
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Clear;
        clock.Advance(1d);
        Assert.True(service.TryFindWalkHeading(Target, 0f, 8f, out _));
        Assert.Equal(1, service.StrikesFor(Target));

        // A clear shot forgives the rest.
        surface.ClearPath(Target);
        clock.Advance(1d);
        Assert.True(service.EvaluateAnyHeight(Target, CombatStyle.Magic, PluginAttackHeight.Medium, true).IsClear);
        Assert.Equal(0, service.StrikesFor(Target));
    }

    [Fact]
    public void WalkChecksOffOrUnavailableFailOpen()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) =
            Build(new LineOfSightSettings { CheckWalkPath = false });
        surface.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;
        Assert.False(service.ChecksWalking);
        Assert.True(service.TryFindWalkHeading(Target, 0f, 8f, out WalkVerdict verdict));
        Assert.Equal(LineOfSightState.Unavailable, verdict.State);
        Assert.Empty(surface.WalkQueries);

        (FakeAutomationSurface noProbe, LineOfSightService withoutHost, _) = Build();
        noProbe.MovementProbeAvailable = false;
        noProbe.DefaultWalkStatus = PluginWalkProbeStatus.Blocked;
        Assert.False(withoutHost.ChecksWalking);
        Assert.True(withoutHost.TryFindWalkHeading(Target, 0f, 8f, out _));
        Assert.Empty(noProbe.WalkQueries);
    }

    [Fact]
    public void AShortCachedWalkDoesNotAnswerALongerOne()
    {
        (FakeAutomationSurface surface, LineOfSightService service, _) = Build();

        service.EvaluateWalk(Target, 0f, 4f);
        WalkVerdict longer = service.EvaluateWalk(Target, 0f, 11f);
        WalkVerdict again = service.EvaluateWalk(Target, 0f, 6f);

        Assert.False(longer.FromCache);
        Assert.True(again.FromCache);
        Assert.Equal(2, surface.WalkQueries.Count);
    }
}
