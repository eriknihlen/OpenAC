using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class MonsterRuleTests
{
    private static PluginCombatTarget Hostile(uint id, string name, float distance) =>
        new(id, name, 0u, distance, 0f, true, 1f);

    [Fact]
    public void RulesMatchByRegexOrSubstringAndDefaultCoversTheRest()
    {
        MonsterRule[] rules =
        [
            new() { Name = "^Olthoi (Worker|Soldier)$", Priority = 5 },
            new() { Name = "drudge", Priority = 0 },
            new() { Name = MonsterRule.DefaultName, Priority = 1 },
        ];

        Assert.Equal(5, MonsterRules.For(rules, "Olthoi Soldier")!.Priority);
        Assert.Equal(0, MonsterRules.For(rules, "Drudge Slinker")!.Priority);
        Assert.Equal(1, MonsterRules.For(rules, "Rabbit")!.Priority);
        Assert.Null(MonsterRules.For(rules[..2], "Rabbit"));
        Assert.True(new MonsterRule { Name = "[unclosed" }.Matches("An [unclosed name")); // bad regex falls back to a substring
    }

    [Fact]
    public void TargetSelectorRanksByRulePriorityAndLeavesZeroAlone()
    {
        var settings = new CombatSettings
        {
            Monsters =
            [
                new() { Name = "Olthoi", Priority = 3 },
                new() { Name = "Rabbit", Priority = 0 },
                new() { Name = MonsterRule.DefaultName, Priority = 1 },
            ],
        };
        PluginCombatTarget[] hostiles =
        [
            Hostile(1, "Rabbit", 1f),
            Hostile(2, "Drudge Slinker", 4f),
            Hostile(3, "Olthoi Worker", 12f),
            Hostile(4, "Olthoi Soldier", 9f),
        ];

        IReadOnlyList<PluginCombatTarget> ranked = TargetSelector.Rank(hostiles, settings, 0u);

        Assert.Equal([4u, 3u, 2u], ranked.Select(t => t.ObjectId));
    }

    [Fact]
    public void WithoutADefaultRuleUnmatchedMonstersAreLeftAlone()
    {
        var settings = new CombatSettings { Monsters = [new() { Name = "Olthoi", Priority = 1 }] };
        IReadOnlyList<PluginCombatTarget> ranked = TargetSelector.Rank([Hostile(1, "Rabbit", 1f), Hostile(2, "Olthoi", 5f)], settings, 0u);
        Assert.Equal([2u], ranked.Select(t => t.ObjectId));
    }

    [Fact]
    public void SelectorFindsTheLoreNamedTopTierThroughTheFamily()
    {
        var surface = new FakeAutomationSurface();
        surface.AttackSpells.Add(Spell.AttackIn(1, "Force Streak V", 70u, 5));
        surface.AttackSpells.Add(Spell.AttackIn(2, "Force Streak VI", 70u, 6));
        surface.AttackSpells.Add(Spell.AttackIn(3, "Outlander's Insolence", 70u, 7));
        surface.AttackSpells.Add(Spell.AttackIn(4, "Force Bolt VI", 71u, 6));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestOffensive("Pierce", SpellShape.Streak, ring: false, out PluginSpellInfo streak));
        Assert.Equal("Outlander's Insolence", streak.Name);
        Assert.True(selector.TryBestOffensive("Pierce", SpellShape.Bolt, ring: false, out PluginSpellInfo bolt));
        Assert.Equal("Force Bolt VI", bolt.Name);
        Assert.False(selector.TryBestOffensive("Cold", SpellShape.Bolt, ring: false, out _));
    }

    private static (FakeAutomationSurface Surface, CombatBehavior Behavior, TickClock Clock) Build(CombatSettings settings)
    {
        var surface = new FakeAutomationSurface();
        surface.CombatSnapshot = surface.CombatSnapshot with { Mode = PluginCombatMode.Magic };
        surface.AttackSpells.Add(Spell.AttackIn(300, "Flame Bolt VI", 30u, 6));
        surface.AttackSpells.Add(Spell.AttackIn(301, "Ring of Fire", 31u, 6));
        surface.CombatSpells.Add(Spell.Debuff(400, "Imperil Other VI", 40u, 6));
        surface.CombatSpells.Add(Spell.Debuff(401, "Fire Vulnerability Other VI", 41u, 6));
        var clock = new TickClock();
        var casts = new CastTracker(surface, clock);
        var behavior = new CombatBehavior(
            new SpellSelector(surface, surface, casts.IsOnCooldown),
            casts,
            new LineOfSightService(surface, surface, clock, () => settings.LineOfSight),
            () => settings);
        return (surface, behavior, clock);
    }

    private static BehaviorStep Step(CombatBehavior behavior, FakeAutomationSurface surface, TickClock clock)
    {
        clock.Advance(0.1d);
        return behavior.Execute(new BehaviorContext(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f)));
    }

    [Fact]
    public void ACasterLandsTheRulesDebuffsBeforeTheWarSpellAndRingsACrowd()
    {
        var settings = new CombatSettings
        {
            Style = CombatStyle.Magic,
            LineOfSight = new LineOfSightSettings { Enabled = false },
            RingRangeMeters = 8f,
            MinRingTargets = 2,
            Monsters = [new() { Name = MonsterRule.DefaultName, Element = "Fire", Imperil = true, Vulnerability = true, UseRing = true }],
        };
        (FakeAutomationSurface surface, CombatBehavior behavior, TickClock clock) = Build(settings);
        surface.Hostiles.Add(Hostile(7, "Drudge", 6f));

        // Imperil first, then the vulnerability, each recorded as landed when the cast completes.
        Step(behavior, surface, clock);
        Assert.Equal("cast:400@7", surface.Commands[^1]);
        surface.CompleteCast(400, target: 7u);
        surface.ReportCast(7u, 400u, 300d);
        Step(behavior, surface, clock);
        Step(behavior, surface, clock);
        Assert.Equal("cast:401@7", surface.Commands[^1]);
        surface.CompleteCast(401, target: 7u);
        surface.ReportCast(7u, 401u, 300d);

        // Debuffs in place and only one hostile: the bolt.
        Step(behavior, surface, clock);
        Step(behavior, surface, clock);
        Assert.Equal("cast:300@7", surface.Commands[^1]);
        surface.CompleteCast(300, target: 7u);

        // A second hostile within ring range: the ring instead.
        surface.Hostiles.Add(Hostile(8, "Drudge", 5f));
        surface.Landed.Add(new PluginTrackedEnchantment(8u, 400u, 400u, 1, false, 300d));
        surface.Landed.Add(new PluginTrackedEnchantment(8u, 401u, 401u, 1, false, 300d));
        Step(behavior, surface, clock);
        Step(behavior, surface, clock);
        Assert.Equal("cast:301@7", surface.Commands[^1]);
    }
}
