using AcDream.Bot.Behaviors;
using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Bot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Tests;

/// <summary>The real behaviors under the real arbiter, driven tick by tick.</summary>
public sealed class EngineIntegrationTests
{
    private static (FakeAutomationSurface Surface, BotEngine Engine) Build(BotProfile profile)
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(51, "Heal Self VI", 200, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(10, "Strength Self VI", 100, 6));
        surface.AttackSpells.Add(Spell.Attack(300, "Flame Bolt VI", 6));
        var clock = new TickClock();
        var cooldowns = new CastCooldowns(clock);
        var spells = new SpellSelector(surface, surface, cooldowns.IsOnCooldown);
        CastTracker Casts() => new(surface, clock, cooldowns);
        BotEngine engine = null!;
        IBehavior[] behaviors =
        [
            new VitalRechargeBehavior(spells, Casts(), () => engine.Profile.Vitals),
            new SelfBuffBehavior(spells, Casts(), () => engine.Profile.Buffs),
            new CombatBehavior(spells, Casts(), () => engine.Profile.Combat),
            new LootBehavior(() => engine.Profile.Loot),
            new NavigationBehavior(() => engine.Profile.Navigation),
        ];
        engine = new BotEngine(surface, new FakeLogger(), behaviors, clock) { Profile = profile };
        engine.Start();
        return (surface, engine);
    }

    [Fact]
    public void LowHealthInterruptsAFightHealsAndResumesIt()
    {
        var profile = new BotProfile
        {
            Buffs = new BuffSettings { Enabled = false },
            Combat = new CombatSettings { Style = CombatStyle.Magic },
            Loot = new LootSettings { Enabled = false },
        };
        (FakeAutomationSurface surface, BotEngine engine) = Build(profile);
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.Hostiles.Add(new PluginCombatTarget(9, "Tusker", 0u, 8f, 0f, true, 1f));

        engine.Tick(0.1);
        Assert.Equal("combat", engine.ActiveBehaviorName);
        Assert.Equal(["cast:300@9"], surface.Commands);

        // Health tanks while the bolt is in flight.
        surface.CurrentHealth = 30;
        engine.Tick(0.1);
        Assert.Equal("vitals", engine.ActiveBehaviorName);
        // Still casting the bolt: vitals waits rather than spamming the host.
        Assert.Single(surface.Commands);

        surface.CompleteCast(300, target: 9);
        engine.Tick(0.1);
        Assert.Equal(["cast:300@9", "cast:51"], surface.Commands);

        surface.CompleteCast(51);
        surface.CurrentHealth = 100;
        engine.Tick(0.1); // vitals done
        engine.Tick(0.1); // combat resumes
        Assert.Equal("combat", engine.ActiveBehaviorName);
        Assert.Equal("cast:300@9", surface.Commands[^1]);
    }

    [Fact]
    public void BuffsBeforeWalkingAndWalkingOnlyWhenIdle()
    {
        var profile = new BotProfile
        {
            Buffs = new BuffSettings { Spells = ["Strength Self"] },
            Combat = new CombatSettings { Enabled = false },
            Loot = new LootSettings { Enabled = false },
        };
        (FakeAutomationSurface surface, BotEngine engine) = Build(profile);
        surface.Position = surface.Position with { HeadingDegrees = 180f };
        var navigation = (NavigationBehavior)engine.Behaviors.Single(behavior => behavior.Name == "nav");
        navigation.SetRoute(new Navigation.Route
        {
            Waypoints = [new Navigation.Waypoint(Navigation.WaypointKind.Point, 0d, 0.2d)],
        });

        engine.Tick(0.1);
        Assert.Equal("buffs", engine.ActiveBehaviorName);
        Assert.Equal(["cast:10"], surface.Commands);

        surface.CompleteCast(10);
        surface.Enchantments.Add(new PluginActiveEnchantment(10, 100, 6, 1800d));
        engine.Tick(0.1); // buffs done
        engine.Tick(0.1); // nav faces north
        Assert.Equal("nav", engine.ActiveBehaviorName);
        Assert.Equal("face:0", surface.Commands[^1]);
        engine.Tick(0.1);
        Assert.Equal("move:forward", surface.Commands[^1]);

        // A monster shows up: combat is disabled, so nav keeps walking.
        surface.Hostiles.Add(new PluginCombatTarget(9, "Tusker", 0u, 8f, 0f, true, 1f));
        engine.Tick(0.1);
        Assert.Equal("nav", engine.ActiveBehaviorName);

        engine.Stop();
        Assert.Equal("move:clear", surface.Commands[^1]);
    }
}
