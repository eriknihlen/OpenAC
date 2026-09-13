using AcDream.Bot.Behaviors;
using AcDream.Bot.Combat;
using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Bot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Tests;

public sealed class CombatBehaviorTests
{
    private static PluginCombatTarget Hostile(uint id, string name, float distance) =>
        new(id, name, 0u, distance, 0f, true, 1f);

    private static (FakeAutomationSurface Surface, CombatBehavior Behavior, TickClock Clock) Build(CombatSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.AttackSpells.Add(Spell.Attack(300, "Flame Bolt VI", 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new CombatBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => settings);
        return (surface, behavior, clock);
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    private static BehaviorStep Step(CombatBehavior behavior, FakeAutomationSurface surface, TickClock clock, double dt = 0.1)
    {
        clock.Advance(dt);
        return behavior.Execute(Context(surface, clock));
    }

    [Fact]
    public void TargetSelectorPrefersPriorityNamesThenNearest()
    {
        var settings = new CombatSettings { PriorityNames = ["Olthoi"], IgnoreNames = ["Rabbit"] };
        PluginCombatTarget[] hostiles =
        [
            Hostile(1, "Rabbit", 1f),
            Hostile(2, "Drudge Slinker", 4f),
            Hostile(3, "Olthoi Worker", 12f),
            Hostile(4, "Drudge Prowler", 3f),
        ];

        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out PluginCombatTarget target));
        Assert.Equal(3u, target.ObjectId);

        settings = settings with { PriorityNames = [] };
        Assert.True(TargetSelector.TrySelect(hostiles, settings, 0u, out target));
        Assert.Equal(4u, target.ObjectId);
    }

    [Fact]
    public void TargetSelectorSticksWithTheCurrentTargetWhenRangesAreClose()
    {
        var settings = new CombatSettings();
        PluginCombatTarget[] hostiles = [Hostile(1, "A", 5f), Hostile(2, "B", 4f)];

        Assert.True(TargetSelector.TrySelect(hostiles, settings, 1u, out PluginCombatTarget target));
        Assert.Equal(1u, target.ObjectId);
    }

    [Fact]
    public void MeleeSwingEntersModeBuildsPowerReleasesAndWaitsForTheServer()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee, Power = 0.8f });
        surface.Hostiles.Add(Hostile(7, "Drudge", 3f));

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out _));

        // Peace -> melee.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["mode:Melee"], surface.Commands);
        // Mode confirmed on the next snapshot; press the attack.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("attack:7:Medium:0.8", surface.Commands[^1]);

        // Power bar still building: hold.
        surface.CombatSnapshot = surface.CombatSnapshot with { PowerBarLevel = 0.3f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.DoesNotContain("release", surface.Commands);

        surface.CombatSnapshot = surface.CombatSnapshot with { PowerBarLevel = 0.85f };
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal("release", surface.Commands[^1]);

        // Waiting on the server.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        surface.CompleteSwing();
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
    }

    [Fact]
    public void ReturnsToPeaceWhenNothingIsLeft()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.Hostiles.Add(Hostile(7, "Drudge", 3f));
        Step(behavior, surface, clock);
        Assert.Equal(PluginCombatMode.Melee, surface.CombatSnapshot.Mode);
        surface.Hostiles.Clear();
        // The mode change still has to be confirmed before standing down.
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("leaving combat", reason);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
        Assert.Equal(PluginCombatMode.Peace, surface.CombatSnapshot.Mode);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void MagicStyleCastsTheBestAttackAtTheTarget()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Magic });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(Hostile(9, "Tusker", 10f));

        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        Assert.Equal(["cast:300@9"], surface.Commands);
        Assert.Equal(StepResult.Continue, Step(behavior, surface, clock).Result);
        surface.CompleteCast(300, target: 9);
        Assert.Equal(StepResult.Done, Step(behavior, surface, clock).Result);
    }

    [Fact]
    public void AStalledSwingIsAbortedAndReported()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 3f));
        Step(behavior, surface, clock);
        Assert.StartsWith("attack:", surface.Commands[^1], StringComparison.Ordinal);

        BehaviorStep step = Step(behavior, surface, clock, dt: 11d);

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Equal("abort", surface.Commands[^1]);
    }

    [Fact]
    public void InterruptMidSwingAborts()
    {
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) =
            Build(new CombatSettings { Style = CombatStyle.Melee });
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Melee };
        surface.Hostiles.Add(Hostile(7, "Drudge", 3f));
        Step(behavior, surface, clock);

        behavior.Interrupt(Context(surface, clock));

        Assert.Equal("abort", surface.Commands[^1]);
    }
}
