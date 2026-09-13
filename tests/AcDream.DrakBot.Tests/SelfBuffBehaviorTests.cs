using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class SelfBuffBehaviorTests
{
    private static readonly BuffSettings TwoBuffs = new()
    {
        Spells = ["Strength Self", "Endurance Self"],
        RebuffWhenRemainingSeconds = 60d,
    };

    private static (FakeAutomationSurface Surface, SelfBuffBehavior Behavior, TickClock Clock) Build(BuffSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(10, "Strength Self VI", 100, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(11, "Endurance Self VI", 101, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(12, "Focus Self VI", 102, 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new SelfBuffBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => settings);
        return (surface, behavior, clock);
    }

    private static BehaviorContext Context(FakeAutomationSurface surface, TickClock clock) =>
        new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));

    [Fact]
    public void CastsConfiguredBuffsInOrderAndOnlyThose()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Strength Self VI due", reason);

        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(10);
        surface.Enchantments.Add(new PluginActiveEnchantment(10, 100, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);

        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(11);
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
        Assert.Equal(["cast:10", "cast:11"], surface.Commands);
    }

    [Fact]
    public void RebuffsWhenTheActiveOneIsAboutToExpire()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.Enchantments.Add(new PluginActiveEnchantment(10, 100, 6, 30d));
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 900d));

        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Strength Self VI due", reason);
    }

    [Fact]
    public void DoesNotOvercastAHigherTierAlreadyInForce()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.Enchantments.Add(new PluginActiveEnchantment(999, 100, 7, 20d));
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 900d));

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void ARefusedCastFailsTheStepAndBacksOff()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);
        surface.NextCastResult = PluginCastRequestResult.MissingComponents;

        BehaviorStep step = behavior.Execute(Context(surface, clock));

        Assert.Equal(StepResult.Failed, step.Result);
        // Strength is backing off; Endurance is next in line.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Endurance Self VI due", reason);
    }

    [Fact]
    public void DisabledSettingsMeanNoInterest()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) =
            Build(TwoBuffs with { Enabled = false });

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }
}
