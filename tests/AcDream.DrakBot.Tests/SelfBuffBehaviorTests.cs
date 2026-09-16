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
        surface.SelfBuffs.Add(Spell.SelfBuff(13, "Blood Drinker Self VI", 103, 6));
        surface.CombatSpells.Add(Spell.Debuff(14, "Impenetrability VI", 104, 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new SelfBuffBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            () => settings,
            surface);
        return (surface, behavior, clock);
    }

    private static PluginInventoryItem Armor(uint id, string name) => new(
        id, 0u, name, 0u, 0u, 0x50000001u, 0u, 0x100u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = PluginObjectClass.Armor,
    };

    [Fact]
    public void WeaponAurasAndArmorSpellsFollowThePlayerBuffs()
    {
        var settings = new BuffSettings
        {
            Spells = ["Strength Self"],
            WeaponSpells = ["Blood Drinker Self"],
            ArmorSpells = ["Impenetrability"],
            BuffWeapon = true,
            BuffArmor = true,
            RebuffWhenRemainingSeconds = 60d,
        };
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(settings);
        surface.Enchantments.Add(new PluginActiveEnchantment(10u, 100u, 6, 1000d)); // strength already up
        surface.OwnedItems.Add(Armor(0x8000_0001u, "Coat"));
        surface.OwnedItems.Add(Armor(0x8000_0002u, "Leggings"));

        // The aura first: a self-cast, judged by the registry and the landed record.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Contains("Blood Drinker", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal("cast:13", surface.Commands[^1]);
        surface.CompleteCast(13, target: surface.ObjectId);
        surface.ReportCast(surface.ObjectId, 13u, 1800d);
        behavior.Execute(Context(surface, clock));

        // Then Impenetrability on each piece of armor, once per piece.
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out reason));
        Assert.Contains("Impenetrability", reason);
        behavior.Execute(Context(surface, clock));
        Assert.Equal("cast:14@2147483649", surface.Commands[^1]);
        surface.CompleteCast(14, target: 0x8000_0001u);
        surface.ReportCast(0x8000_0001u, 14u, 1800d);
        behavior.Execute(Context(surface, clock));
        behavior.Execute(Context(surface, clock));
        Assert.Equal("cast:14@2147483650", surface.Commands[^1]);
        surface.CompleteCast(14, target: 0x8000_0002u);
        surface.ReportCast(0x8000_0002u, 14u, 1800d);
        behavior.Execute(Context(surface, clock));

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
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
    public void ABuffThatSucceedsWithoutLandingIsRestedAfterThreeGoes()
    {
        // The host called an Other tier sent with no target a success, nine
        // thousand times in five minutes, with nothing ever in the
        // registry: three such in a row and the spell is put aside.
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) = Build(TwoBuffs);

        for (int go = 0; go < SelfBuffBehavior.NoEffectStrikes; go++)
        {
            Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
            surface.CompleteCast(10);
            Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
            clock.Advance(0.1d);
        }
        BehaviorStep step = behavior.Execute(Context(surface, clock));

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Contains("never landed", step.Reason);
        Assert.Equal(SelfBuffBehavior.NoEffectStrikes, surface.Commands.Count(c => c == "cast:10"));
        Assert.True(behavior.WantsControl(Context(surface, clock).Board, out string reason));
        Assert.Equal("Endurance Self VI due", reason);

        // A real cast that lands, then the next buff, is no strike at all.
        Assert.Equal(StepResult.Continue, behavior.Execute(Context(surface, clock)).Result);
        surface.CompleteCast(11);
        surface.Enchantments.Add(new PluginActiveEnchantment(11, 101, 6, 1800d));
        Assert.Equal(StepResult.Done, behavior.Execute(Context(surface, clock)).Result);
        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }

    [Fact]
    public void DisabledSettingsMeanNoInterest()
    {
        (FakeAutomationSurface surface, SelfBuffBehavior behavior, TickClock clock) =
            Build(TwoBuffs with { Enabled = false });

        Assert.False(behavior.WantsControl(Context(surface, clock).Board, out _));
    }
}
