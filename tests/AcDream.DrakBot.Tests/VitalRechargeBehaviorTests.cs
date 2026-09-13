using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class VitalRechargeBehaviorTests
{
    private static (FakeAutomationSurface Surface, VitalRechargeBehavior Behavior, TickClock Clock) Build(
        VitalSettings? settings = null)
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(50, "Heal Self V", 200, 5));
        surface.SelfBuffs.Add(Spell.SelfBuff(51, "Heal Self VI", 200, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(60, "Revitalize Self IV", 201, 4));
        var clock = new TickClock();
        VitalSettings vitals = settings ?? new VitalSettings();
        var casts = new CastTracker(surface, clock);
        var behavior = new VitalRechargeBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => vitals);
        return (surface, behavior, clock);
    }

    private static Blackboard Board(FakeAutomationSurface surface, TickClock clock) =>
        Blackboard.Capture(surface, clock, 25f, 15f);

    [Fact]
    public void QuietWhenVitalsAreFine()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();

        Assert.False(behavior.WantsControl(Board(surface, clock), out _));
    }

    [Fact]
    public void HealsWithTheBestKnownTierWhenHealthIsLow()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();
        surface.CurrentHealth = 40;

        Assert.True(behavior.WantsControl(Board(surface, clock), out string reason));
        Assert.StartsWith("health", reason, StringComparison.Ordinal);

        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal(["cast:51"], surface.Commands);

        // Still casting: no second request.
        step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Single(surface.Commands);

        surface.CompleteCast(51);
        surface.CurrentHealth = 90;
        step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Done, step.Result);
    }

    [Fact]
    public void HealthOutranksStamina()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();
        surface.CurrentHealth = 40;
        surface.CurrentStamina = 10;

        behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));

        Assert.Equal(["cast:51"], surface.Commands);
    }

    [Fact]
    public void FallsBackToAHealingKitWhenNoHealSpellIsKnown()
    {
        var surface = new FakeAutomationSurface();
        surface.CurrentHealth = 30;
        surface.OwnedItems.Add(Kit(0x700u));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new VitalRechargeBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => new VitalSettings());

        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));

        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal([$"apply:{0x700u}@{surface.ObjectId}"], surface.Commands);
    }

    [Fact]
    public void FailsCleanlyWhenNothingCanHeal()
    {
        var surface = new FakeAutomationSurface();
        surface.CurrentHealth = 30;
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new VitalRechargeBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => new VitalSettings());

        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Empty(surface.Commands);
    }

    [Fact]
    public void AFailedCastBacksOffBeforeRetrying()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();
        surface.CurrentHealth = 40;
        behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        surface.CompleteCast(51, success: false);

        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));

        // Tier VI is on cooldown; the selector still has tier V.
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal(["cast:51", "cast:50"], surface.Commands);
    }

    private static PluginInventoryItem Kit(uint id) => new(
        id, 0u, "Healing Kit", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, 10, 10, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = PluginObjectClass.HealingKit,
    };
}
