using AcDream.Bot.Behaviors;
using AcDream.Bot.Tests.Fakes;

namespace AcDream.Bot.Tests;

public sealed class BotEngineTests
{
    [Fact]
    public void DoesNothingUntilStarted()
    {
        var surface = new FakeAutomationSurface();
        var behavior = new ScriptedBehavior("a", BehaviorPriority.Combat) { Wants = true };
        var engine = new BotEngine(surface, new FakeLogger(), [behavior]);

        engine.Tick(0.1);

        Assert.Equal(0, behavior.Executions);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void RunsTheHighestPriorityBehaviorThatWantsControl()
    {
        var surface = new FakeAutomationSurface();
        var low = new ScriptedBehavior("nav", BehaviorPriority.Navigation) { Wants = true };
        var high = new ScriptedBehavior("vitals", BehaviorPriority.Survival) { Wants = true };
        var engine = new BotEngine(surface, new FakeLogger(), [low, high]);
        engine.Start();

        engine.Tick(0.1);

        Assert.Equal(1, high.Executions);
        Assert.Equal(0, low.Executions);
        Assert.Equal("vitals", engine.ActiveBehaviorName);
    }

    [Fact]
    public void ARunningBehaviorKeepsControlWhileItContinues()
    {
        var surface = new FakeAutomationSurface();
        var nav = new ScriptedBehavior("nav", BehaviorPriority.Navigation) { Wants = true };
        var loot = new ScriptedBehavior("loot", BehaviorPriority.Looting) { Wants = false };
        var engine = new BotEngine(surface, new FakeLogger(), [nav, loot]);
        engine.Start();
        engine.Tick(0.1);

        // Loot now wants in, but nav is mid-step and loot is not strictly higher
        // than... it is: Looting < Navigation, so it preempts.
        loot.Wants = true;
        engine.Tick(0.1);

        Assert.Equal(1, nav.Interrupts);
        Assert.Equal(1, loot.Executions);
    }

    [Fact]
    public void ALowerPriorityBehaviorWaitsForTheRunningOneToFinish()
    {
        var surface = new FakeAutomationSurface();
        var combat = new ScriptedBehavior("combat", BehaviorPriority.Combat) { Wants = true };
        var nav = new ScriptedBehavior("nav", BehaviorPriority.Navigation) { Wants = true };
        var engine = new BotEngine(surface, new FakeLogger(), [nav, combat]);
        engine.Start();

        engine.Tick(0.1);
        engine.Tick(0.1);
        Assert.Equal(2, combat.Executions);
        Assert.Equal(0, nav.Executions);

        combat.Wants = false;
        combat.Next = BehaviorStep.Done;
        engine.Tick(0.1);
        engine.Tick(0.1);

        Assert.Equal(1, nav.Executions);
    }

    [Fact]
    public void StopInterruptsTheActiveBehavior()
    {
        var surface = new FakeAutomationSurface();
        var behavior = new ScriptedBehavior("a", BehaviorPriority.Combat) { Wants = true };
        var engine = new BotEngine(surface, new FakeLogger(), [behavior]);
        engine.Start();
        engine.Tick(0.1);

        engine.Stop();

        Assert.Equal(1, behavior.Interrupts);
        Assert.Equal("idle", engine.ActiveBehaviorName);
    }

    [Fact]
    public void LeavingTheWorldInterruptsAndIdles()
    {
        var surface = new FakeAutomationSurface();
        var behavior = new ScriptedBehavior("a", BehaviorPriority.Combat) { Wants = true };
        var engine = new BotEngine(surface, new FakeLogger(), [behavior]);
        engine.Start();
        engine.Tick(0.1);

        surface.IsInWorld = false;
        engine.Tick(0.1);

        Assert.Equal(1, behavior.Interrupts);
        Assert.Equal(1, behavior.Executions);
        Assert.Equal("not in world", engine.LastReason);
    }

    [Fact]
    public void AThrowingBehaviorIsLoggedAndReleased()
    {
        var surface = new FakeAutomationSurface();
        var log = new FakeLogger();
        var behavior = new ScriptedBehavior("boom", BehaviorPriority.Combat)
        {
            Wants = true,
            Throw = new InvalidOperationException("nope"),
        };
        var engine = new BotEngine(surface, log, [behavior]);
        engine.Start();

        engine.Tick(0.1);

        Assert.Contains(log.Lines, line => line.StartsWith("error: behavior boom threw", StringComparison.Ordinal));
        Assert.Equal("idle", engine.ActiveBehaviorName);
        Assert.True(engine.IsRunning);
    }

    private sealed class ScriptedBehavior(string name, BehaviorPriority priority) : IBehavior
    {
        public string Name => name;
        public BehaviorPriority Priority => priority;
        public bool Wants { get; set; }
        public BehaviorStep Next { get; set; } = BehaviorStep.Continue;
        public Exception? Throw { get; set; }
        public int Executions { get; private set; }
        public int Interrupts { get; private set; }

        public bool WantsControl(Blackboard board, out string reason)
        {
            reason = name;
            return Wants;
        }

        public BehaviorStep Execute(BehaviorContext context)
        {
            Executions++;
            if (Throw is not null)
                throw Throw;
            return Next;
        }

        public void Interrupt(BehaviorContext context) => Interrupts++;
    }
}
