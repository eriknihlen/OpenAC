using AcDream.Plugins.MossTank;

namespace AcDream.Plugins.MossTank.Tests;

public class MacroSchedulerTests
{
    private sealed class Probe : IMacroRule
    {
        private bool _running;

        public Probe(string name, bool valid = false)
        {
            Name = name;
            Valid = valid;
        }

        public string Name { get; }

        public bool Valid { get; set; }

        /// <summary>Validity the rule reports only while it may act.</summary>
        public bool RequiresCanAct { get; set; } = true;

        public List<bool> CanActSeen { get; } = [];

        public int ValidNowCalls { get; private set; }

        public List<bool> RunningWrites { get; } = [];

        public bool ValidNow(in MacroPassContext context)
        {
            ValidNowCalls++;
            CanActSeen.Add(context.CanAct);
            if (RequiresCanAct && !context.CanAct)
                return false;
            return Valid;
        }

        public bool Running
        {
            get => _running;
            set
            {
                RunningWrites.Add(value);
                _running = value;
            }
        }
    }

    private sealed class Provider : IMacroRuleProvider
    {
        public IMacroRule Create(MacroRuleSlot slot) => new Probe(slot.ToString());

        public IMacroRule Create(MacroIndependentSlot slot) =>
            new Probe(slot.ToString());
    }

    private static readonly string[] VtankOrder =
    [
        "Sentinel START",
        "SplitPeasCritical",
        "CraftFoodCritical",
        "RechargeSelfNormal",
        "RefillWieldedMana",
        "BuffSelfNormal",
        "SplitPeasNormal",
        "Sentinel POSTBUFF",
        "DispelSelf",
        "UseDispelItem",
        "UseHealersHeart",
        "RechargeOther",
        "DispelAllies",
        "CraftFood",
        "RefillPetChargesNormal",
        "Sentinel POSTHELPER",
        "FellowshipManager",
        "Sentinel POSTAUTOFELLOW",
        "OpenDoor",
        "Sentinel PREPRIORITYLOOTACTIONS",
        "ReadScrollPriority",
        "StackCramPriority",
        "SalvageItemsPriority",
        "Sentinel POSTPRIORITYLOOTACTIONS",
        "Sentinel PREPRIORITYLOOT",
        "NavigateCorpsePriority",
        "OpenCorpsePriority",
        "LootCorpsePriority",
        "CorpseWaitPriority",
        "Sentinel POSTPRIORITYLOOT",
        "Sentinel PREPRIORITYNAV",
        "NavigateRoutePriority",
        "Sentinel POSTPRIORITYNAV",
        "Sentinel PREATTACK",
        "Attack",
        "Sentinel POSTATTACK",
        "Sentinel PREIDLESTATUS",
        "SplitPeasIdle",
        "CraftFoodIdle",
        "RefillPetChargesIdle",
        "Sentinel PREIDLELOOTACTIONS",
        "ReadScrollIdle",
        "StackCramIdle",
        "SalvageItemsIdle",
        "Sentinel POSTIDLELOOTACTIONS",
        "Sentinel PREIDLELOOT",
        "NavigateCorpseIdle",
        "OpenCorpseIdle",
        "LootCorpseIdle",
        "CorpseWaitIdle",
        "Sentinel POSTIDLELOOT",
        "Sentinel PREIDLEBUFF",
        "BuffSelfIdle",
        "Sentinel POSTIDLEBUFF",
        "Sentinel PRETARGETAPPROACH",
        "NavigateMonster",
        "Sentinel POSTTARGETAPPROACH",
        "Sentinel PREIDLERECHARGE",
        "RechargeSelfNoTarget",
        "Sentinel POSTIDLERECHARGE",
        "Sentinel PRENAVROUTE",
        "NavigateRouteIdle",
        "Sentinel POSTNAVROUTE",
        "RandomHelper",
        "Sentinel END",
        "IdlePeace",
    ];

    [Fact]
    public void TableIsVtanksSixtySixEntriesInOrder()
    {
        Assert.Equal(66, MacroRuleTable.Entries.Count);
        Assert.Equal(
            VtankOrder,
            MacroRuleTable.Entries.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void BuiltListMatchesTheTableOrder()
    {
        MacroScheduler scheduler = MacroRuleTable.Build(new Provider());
        Assert.Equal(
            VtankOrder,
            scheduler.MainRules.Select(rule => rule.Name).ToArray());
        Assert.Equal(
            ["SummonPet"],
            scheduler.IndependentRules.Select(rule => rule.Name).ToArray());
    }

    [Fact]
    public void SentinelsSitAtVtanksPositions()
    {
        int[] sentinels = MacroRuleTable.Entries
            .Select(static (entry, index) => (entry, index))
            .Where(static pair => pair.entry.Slot is null)
            .Select(static pair => pair.index)
            .ToArray();
        Assert.Equal(
            [
                0, 7, 15, 17, 19, 23, 24, 29, 30, 32, 33, 35, 36, 40, 44, 45,
                50, 51, 53, 54, 56, 57, 59, 60, 62, 64,
            ],
            sentinels);
        Assert.Equal(
            MacroRuleSlot.IdlePeace,
            MacroRuleTable.Entries[^1].Slot);
        Assert.Equal("Sentinel END", MacroRuleTable.Entries[^2].Name);
    }

    [Fact]
    public void SentinelNeverWins()
    {
        var sentinel = new MacroRuleSentinel("START");
        var context = new MacroPassContext(1d, CanAct: true);
        Assert.False(sentinel.ValidNow(in context));
        sentinel.Running = true;
        Assert.False(sentinel.Running);
    }

    private static MacroScheduler Started(params IMacroRule[] rules)
    {
        var scheduler = new MacroScheduler(rules);
        scheduler.Start();
        return scheduler;
    }

    [Fact]
    public void FirstValidRuleWinsAndEveryOtherRuleIsToldRunningFalse()
    {
        var high = new Probe("high");
        var winner = new Probe("winner", valid: true);
        var low = new Probe("low", valid: true);
        MacroScheduler scheduler = Started(high, winner, low);

        scheduler.RunPass(1d);

        Assert.Same(winner, scheduler.LastExecutedRule);
        Assert.True(winner.Running);
        Assert.False(high.Running);
        Assert.False(low.Running);
        Assert.Equal([false], high.RunningWrites);
        Assert.Equal([false], low.RunningWrites);
        Assert.Equal([true], winner.RunningWrites);
    }

    [Fact]
    public void RulesBelowTheWinnerAreEvaluatedWithCanActFalse()
    {
        var winner = new Probe("winner", valid: true);
        var below = new Probe("below", valid: true);
        MacroScheduler scheduler = Started(winner, below);

        scheduler.RunPass(1d);

        Assert.Equal([true], winner.CanActSeen);
        Assert.Equal([false], below.CanActSeen);
        Assert.Equal(1, below.ValidNowCalls);
    }

    [Fact]
    public void AClaimBelowTheWinnerDoesNotStealTheSlot()
    {
        var winner = new Probe("winner", valid: true);
        var below = new Probe("below", valid: true) { RequiresCanAct = false };
        MacroScheduler scheduler = Started(winner, below);

        scheduler.RunPass(1d);

        Assert.Same(winner, scheduler.LastExecutedRule);
        Assert.False(below.Running);
    }

    [Fact]
    public void ARuleThatBecomesInvalidIsToldRunningFalseOnTheNextPass()
    {
        var rule = new Probe("rule", valid: true);
        MacroScheduler scheduler = Started(rule);

        scheduler.RunPass(1d);
        Assert.True(rule.Running);

        rule.Valid = false;
        scheduler.RunPass(1d);

        Assert.False(rule.Running);
        Assert.Equal([true, false], rule.RunningWrites);
        Assert.Null(scheduler.LastExecutedRule);
    }

    [Fact]
    public void StopTearsDownEveryRule()
    {
        var main = new Probe("main", valid: true);
        var independent = new Probe("independent", valid: true);
        var scheduler = new MacroScheduler([main], [independent]);
        scheduler.Start();
        scheduler.RunPass(1d);
        Assert.True(main.Running);
        Assert.True(independent.Running);

        scheduler.Stop();

        Assert.False(main.Running);
        Assert.False(independent.Running);
        Assert.False(scheduler.IsRunning);
        Assert.Null(scheduler.LastExecutedRule);
    }

    [Fact]
    public void HeartbeatRunsOnePassEveryTwoHundredNinetyThreeMilliseconds()
    {
        var rule = new Probe("rule");
        var scheduler = new MacroScheduler([rule]);

        // Stopped: no pass at all, however much time goes by.
        Assert.False(scheduler.Advance(10d));
        Assert.Equal(0, rule.ValidNowCalls);

        scheduler.Start();
        Assert.True(scheduler.Advance(0d));
        Assert.Equal(1L, scheduler.PassCount);

        // Then the 293 ms heartbeat, re-armed on every fire.
        Assert.False(scheduler.Advance(0.1d));
        Assert.False(scheduler.Advance(0.1d));
        Assert.False(scheduler.Advance(0.09d));
        Assert.Equal(1L, scheduler.PassCount);
        Assert.True(scheduler.Advance(0.01d));
        Assert.Equal(2L, scheduler.PassCount);
        Assert.False(scheduler.Advance(0.1d));
    }

    [Fact]
    public void PassElapsedIsAccumulatedAcrossFrames()
    {
        var recorder = new ElapsedRecorder();
        var scheduler = new MacroScheduler([recorder]);
        scheduler.Start();
        scheduler.Advance(0d);
        recorder.Seen.Clear();

        scheduler.Advance(0.1d);
        scheduler.Advance(0.1d);
        scheduler.Advance(0.1d);

        double seen = Assert.Single(recorder.Seen);
        Assert.Equal(0.3d, seen, 6);
    }

    private sealed class ElapsedRecorder : IMacroRule
    {
        public string Name => "recorder";

        public List<double> Seen { get; } = [];

        public bool ValidNow(in MacroPassContext context)
        {
            Seen.Add(context.ElapsedSeconds);
            return false;
        }

        public bool Running { get; set; }
    }

    [Fact]
    public void PokeForcesAnImmediatePass()
    {
        var rule = new Probe("rule");
        var scheduler = new MacroScheduler([rule]);
        scheduler.Start();
        scheduler.Advance(0d);
        Assert.Equal(1L, scheduler.PassCount);

        Assert.False(scheduler.Advance(0.05d));
        scheduler.Poke();
        Assert.True(scheduler.Advance(0.001d));
        Assert.Equal(2L, scheduler.PassCount);
    }

    [Fact]
    public void PokeWhileStoppedIsIgnored()
    {
        var scheduler = new MacroScheduler([new Probe("rule")]);
        scheduler.Poke();
        Assert.False(scheduler.Advance(0d));
        Assert.Equal(0L, scheduler.PassCount);
    }

    [Fact]
    public void SuspensionSkipsTheMainTrackButNotTheIndependentTrack()
    {
        var main = new Probe("main", valid: true);
        var independent = new Probe("independent", valid: true);
        var scheduler = new MacroScheduler([main], [independent]);
        scheduler.Start();

        scheduler.Suspend();
        Assert.True(scheduler.IsSuspended);
        scheduler.RunPass(1d);

        Assert.True(independent.Running);
        Assert.False(main.Running);
        Assert.Equal(0, main.ValidNowCalls);

        scheduler.Resume();
        Assert.False(scheduler.IsSuspended);
        scheduler.RunPass(1d);
        Assert.True(main.Running);
    }

    [Fact]
    public void ReleasingTheLastSuspensionPokesThePass()
    {
        var main = new Probe("main", valid: true);
        var scheduler = new MacroScheduler([main]);
        scheduler.Start();
        scheduler.Advance(0d);
        Assert.Equal(1L, scheduler.PassCount);

        scheduler.Suspend();
        scheduler.Suspend();
        scheduler.Resume();
        Assert.True(scheduler.IsSuspended);
        Assert.False(scheduler.Advance(0.01d));

        scheduler.Resume();
        Assert.False(scheduler.IsSuspended);
        Assert.True(scheduler.Advance(0.01d));
        Assert.Equal(2L, scheduler.PassCount);
    }

    [Fact]
    public void TheMetaPassRunsInsideThePassAndHonoursTheSuspension()
    {
        var seen = new List<double>();
        var main = new Probe("main", valid: true);
        var independent = new Probe("independent", valid: true);
        var scheduler = new MacroScheduler([main], [independent])
        {
            MetaPass = seen.Add,
        };
        scheduler.Start();

        scheduler.RunPass(0.3d);
        Assert.Equal([0.3d], seen);

        scheduler.Suspend();
        scheduler.RunPass(0.3d);
        Assert.Equal([0.3d], seen);
        Assert.True(independent.Running);

        scheduler.Resume();
        scheduler.RunPass(0.3d);
        Assert.Equal([0.3d, 0.6d], seen);
        Assert.Equal(0.9d, seen.Sum(), precision: 10);
    }


    [Fact]
    public void StartingTheMacroAgainOwesTheMetaNoSuspendedBacklog()
    {
        var seen = new List<double>();
        var main = new Probe("main", valid: true);
        var scheduler = new MacroScheduler([main], [])
        {
            MetaPass = seen.Add,
        };
        scheduler.Start();

        scheduler.Suspend();
        scheduler.RunPass(0.3d);
        scheduler.RunPass(0.3d);
        Assert.Empty(seen);

        scheduler.Stop();
        scheduler.Resume();
        scheduler.Start();
        scheduler.RunPass(0.3d);

        Assert.Equal([0.3d], seen);
    }
    [Fact]
    public void IndependentRulesAreAssignedRunningFromValidNowEveryPass()
    {
        var independent = new Probe("independent", valid: true);
        var scheduler = new MacroScheduler([], [independent]);
        scheduler.Start();

        scheduler.RunPass(1d);
        Assert.True(independent.Running);

        independent.Valid = false;
        scheduler.RunPass(1d);
        Assert.False(independent.Running);
        Assert.Equal([true, false], independent.RunningWrites);
    }

    [Fact]
    public void PreChainGateBlocksTheWrapperBeforeThePrimaryIsConsulted()
    {
        var primary = new Probe("primary", valid: true);
        bool gate = false;
        var chain = new MacroRulePreChain(primary, [() => gate]);
        var context = new MacroPassContext(1d, CanAct: true);

        Assert.False(chain.ValidNow(in context));
        Assert.Equal(0, primary.ValidNowCalls);

        gate = true;
        Assert.True(chain.ValidNow(in context));
        Assert.Equal(1, primary.ValidNowCalls);
    }

    [Fact]
    public void PreChainFallbackDoesNotMakeTheWrapperValid()
    {
        var primary = new Probe("primary");
        var fallback = new Probe("fallback", valid: true);
        var chain = new MacroRulePreChain(primary, null, [fallback]);
        var context = new MacroPassContext(1d, CanAct: true);

        Assert.False(chain.ValidNow(in context));
    }

    [Fact]
    public void PreChainRunsAValidFallbackInsteadOfThePrimary()
    {
        var primary = new Probe("primary", valid: true);
        var fallback = new Probe("fallback", valid: true);
        var chain = new MacroRulePreChain(primary, null, [fallback]);

        chain.Running = true;

        Assert.True(fallback.Running);
        Assert.False(primary.Running);
        Assert.Empty(primary.RunningWrites);

        chain.Running = false;
        Assert.False(fallback.Running);
        Assert.Equal([false], primary.RunningWrites);
    }

    [Fact]
    public void PreChainRunsThePrimaryWhenNoFallbackIsValid()
    {
        var primary = new Probe("primary", valid: true);
        var fallback = new Probe("fallback");
        var chain = new MacroRulePreChain(primary, null, [fallback]);

        chain.Running = true;

        Assert.True(primary.Running);
        Assert.False(fallback.Running);
    }


    private static List<(MacroLogChannel Channel, string Message)> Recorder(
        MacroScheduler scheduler)
    {
        var log = new List<(MacroLogChannel Channel, string Message)>();
        scheduler.Log = (channel, message) => log.Add((channel, message));
        return log;
    }

    [Fact]
    public void ActiveRuleHeaderNamesTheCurrentMainRuleCountEveryPass()
    {
        var scheduler = new MacroScheduler(
            [new Probe("a"), new Probe("b"), new Probe("c")]);
        List<(MacroLogChannel Channel, string Message)> log = Recorder(scheduler);
        scheduler.Start();

        scheduler.RunPass(1d);

        Assert.Contains(
            (MacroLogChannel.ActiveRule,
                "----------- Primary logic loop started (3 rules) -----------"),
            log);
    }

    [Fact]
    public void ActiveRulePickedLineNamesTheWinnerWithItsListIndexAndTheLockSuffix()
    {
        var skipped = new Probe("skipped", valid: false);
        var winner = new Probe("winner", valid: true);
        var scheduler = new MacroScheduler([skipped, winner])
        {
            LockStateSuffix = () => "   I=True, N=False, S=False",
        };
        List<(MacroLogChannel Channel, string Message)> log = Recorder(scheduler);
        scheduler.Start();

        scheduler.RunPass(1d);

        Assert.Contains(
            (MacroLogChannel.ActiveRule, "Picked winner P: 1   I=True, N=False, S=False"),
            log);
    }

    [Fact]
    public void ActiveRulePostsAllRulesInactiveWhenNoRuleIsValid()
    {
        var scheduler = new MacroScheduler([new Probe("a"), new Probe("b")])
        {
            LockStateSuffix = () => "   I=False, N=False, S=False",
        };
        List<(MacroLogChannel Channel, string Message)> log = Recorder(scheduler);
        scheduler.Start();

        scheduler.RunPass(1d);

        Assert.Contains(
            (MacroLogChannel.ActiveRule, "All rules inactive.   I=False, N=False, S=False"),
            log);
        Assert.DoesNotContain(log, entry => entry.Message.StartsWith("Picked", StringComparison.Ordinal));
    }

    [Fact]
    public void RuleInfoPostsRunningForTheWinnerOnEveryPassItWinsAndNeverForALoser()
    {
        var loser = new Probe("loser", valid: true) { RequiresCanAct = false };
        var buffSelf = new Probe("BuffSelf", valid: true);
        var scheduler = new MacroScheduler([buffSelf, loser]);
        List<(MacroLogChannel Channel, string Message)> log = Recorder(scheduler);
        scheduler.Start();

        scheduler.RunPass(1d);
        scheduler.RunPass(1d);

        Assert.Equal(
            [
                (MacroLogChannel.RuleInfo, "(BuffSelf) Running"),
                (MacroLogChannel.RuleInfo, "(BuffSelf) Running"),
            ],
            log.Where(entry => entry.Channel == MacroLogChannel.RuleInfo).ToArray());
    }

    [Fact]
    public void RuleInfoStopsWhenTheWinnerStopsWinning()
    {
        var rule = new Probe("BuffSelf", valid: true);
        var scheduler = new MacroScheduler([rule]);
        List<(MacroLogChannel Channel, string Message)> log = Recorder(scheduler);
        scheduler.Start();

        scheduler.RunPass(1d);
        rule.Valid = false;
        scheduler.RunPass(1d);

        Assert.Single(log, entry => entry.Channel == MacroLogChannel.RuleInfo);
    }

    /// <summary>
    /// Unbound Log/LockStateSuffix must never throw — the default state for
    /// every scheduler that has not gone through MossTankPanel's binding
    /// (every other test in this file, for instance).
    /// </summary>
    [Fact]
    public void UnboundLogAndLockStateSuffixDoNotThrow()
    {
        var scheduler = new MacroScheduler([new Probe("a", valid: true)]);
        scheduler.Start();

        Exception? error = Record.Exception(() => scheduler.RunPass(1d));

        Assert.Null(error);
    }
}
