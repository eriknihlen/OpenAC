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
        surface.CombatSpells.Add(Spell.Debuff(70, "Heal Other V", 210, 5));
        var casts = new CastTracker(surface, clock);
        var behavior = new VitalRechargeBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => vitals,
            surface);
        return (surface, behavior, clock);
    }

    [Fact]
    public void TopsUpToTheIdleThresholdOnlyWithNothingInRange()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build(
            new VitalSettings { HealBelow = 0.5, IdleHealthBelow = 0.95 });
        surface.CurrentHealth = 80;

        Assert.True(behavior.WantsControl(Board(surface, clock), out string reason));
        Assert.Contains("health", reason);

        surface.Hostiles.Add(new PluginCombatTarget(7u, "Drudge", 0u, 8f, 0f, true, 1f));
        Assert.False(behavior.WantsControl(Board(surface, clock), out _));
    }

    [Fact]
    public void HealsTheWorstHurtFellowInRange()
    {
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build(
            new VitalSettings { HealFellowsBelow = 0.6, HealFellowsRangeMeters = 20f, IdleHealthBelow = 0d });
        surface.Fellows.Add(new PluginFellowMember(0x5000_0002u, "Near", 30u, 100u, 0u, 0u, 0u, 0u, 5f));
        surface.Fellows.Add(new PluginFellowMember(0x5000_0003u, "Worse", 10u, 100u, 0u, 0u, 0u, 0u, 8f));
        surface.Fellows.Add(new PluginFellowMember(0x5000_0004u, "Far", 5u, 100u, 0u, 0u, 0u, 0u, 50f));

        Assert.True(behavior.WantsControl(Board(surface, clock), out string reason));
        Assert.Contains("Worse", reason);
        behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal("cast:70@1342177283", surface.Commands[^1]);
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
        Assert.Equal(["mode:Magic", "cast:51"], surface.Commands);

        // Still casting: no second request.
        step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal(2, surface.Commands.Count);

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

        Assert.Equal(["mode:Magic", "cast:51"], surface.Commands);
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
    public void AWandIsWieldedAndMagicModeEnteredBeforeTheCast()
    {
        // The server drops a cast sent from any other mode with a use-done
        // that looks like success, and will not enter magic mode with no
        // caster in hand: the sword goes down, the wand comes up, the mode
        // is asked for, and the heal goes out.
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();
        surface.CurrentHealth = 40;
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Equipment.Add(new PluginEquipmentItem(0x9000_0001u, "Sword", 0x1u, 0x100000u, 0x100000u, 0u, 1u, 1, 0, 0, 0, 0d));
        surface.Equipment.Add(new PluginEquipmentItem(0x9000_0002u, "Wand", 0x8000u, 0x1000000u, 0u, 1u, 0u, 0, 0, 0, 0, 0d));

        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal(["equip:2415919106"], surface.Commands);

        step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Continue, step.Result);
        Assert.Equal(["equip:2415919106", "mode:Magic", "cast:51"], surface.Commands);
    }

    [Fact]
    public void AHealThatMovesNothingIsNotCastAgainAtOnce()
    {
        // A heal of a hundred on a hundred thousand: the host calls the
        // cast a success and the vital stands where it was. Cast again at
        // once it stood the bot still casting for ever; it backs off like
        // a failure instead.
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build();
        surface.MaxHealth = 100_000;
        surface.CurrentHealth = 65_000;

        Assert.Equal(StepResult.Continue, behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock))).Result);
        surface.CompleteCast(51);
        surface.CurrentHealth = 65_100;
        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Contains("next to nothing", step.Reason);
        Assert.False(behavior.WantsControl(Board(surface, clock), out _));
        clock.Advance(VitalRechargeBehavior.FailRetrySeconds + 1d);
        Assert.True(behavior.WantsControl(Board(surface, clock), out _));
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
        Assert.Equal(["mode:Magic", "cast:51", "cast:50"], surface.Commands);
    }

    [Fact]
    public void AVitalWithNoWayToTopItUpIsLeftAloneForAWhile()
    {
        // No mana spell known: a melee character at low mana must not ask
        // for control on every tick, or nothing else ever runs.
        (FakeAutomationSurface surface, VitalRechargeBehavior behavior, TickClock clock) = Build(
            new VitalSettings { IdleManaBelow = 0.9, ManaBelow = 0.25, IdleHealthBelow = 0d, IdleStaminaBelow = 0d });
        surface.CurrentMana = 16;

        Assert.True(behavior.WantsControl(Board(surface, clock), out string reason));
        Assert.Contains("mana", reason);
        BehaviorStep step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Contains("Stamina to Mana Self", step.Reason);
        Assert.Contains("not tried again", step.Reason);

        // Left alone for the wait, then asked again - quietly the second time.
        clock.Advance(5d);
        Assert.False(behavior.WantsControl(Board(surface, clock), out _));
        clock.Advance(11d);
        Assert.True(behavior.WantsControl(Board(surface, clock), out _));
        step = behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Board(surface, clock)));
        Assert.Equal(StepResult.Done, step.Result);
        Assert.Empty(surface.Commands);

        // Health still gets seen to meanwhile.
        surface.CurrentHealth = 40;
        clock.Advance(1d);
        Assert.True(behavior.WantsControl(Board(surface, clock), out reason));
        Assert.Contains("health", reason);
    }

    private static PluginInventoryItem Kit(uint id) => new(
        id, 0u, "Healing Kit", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, 10, 10, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = PluginObjectClass.HealingKit,
    };
}
