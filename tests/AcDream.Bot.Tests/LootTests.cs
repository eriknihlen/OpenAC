using AcDream.Bot.Behaviors;
using AcDream.Bot.Loot;
using AcDream.Bot.Profiles;
using AcDream.Bot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot.Tests;

public sealed class LootTests
{
    private static PluginInventoryItem Item(uint id, string name, PluginObjectClass objectClass, int value = 0, float workmanship = 0f) => new(
        id, 0u, name, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = objectClass,
        Value = value,
        Workmanship = workmanship,
    };

    [Fact]
    public void FirstMatchingRuleWins()
    {
        var rules = new LootRuleSet
        {
            Rules =
            [
                new LootRule { Name = "junk", NameContains = "Pyreal", Action = LootAction.Ignore },
                new LootRule { Name = "money", ObjectClass = PluginObjectClass.Money },
            ],
        };

        Assert.Equal("junk", rules.Decide(Item(1, "Pyreal", PluginObjectClass.Money), false).RuleName);
        Assert.Equal(LootAction.Ignore, rules.Decide(Item(1, "Pyreal", PluginObjectClass.Money), false).Action);
        Assert.Equal("money", rules.Decide(Item(2, "Trade Note", PluginObjectClass.Money), false).RuleName);
    }

    [Fact]
    public void RulesThatNeedAppraisalAskForItBeforeDeciding()
    {
        var rules = new LootRuleSet
        {
            Rules = [new LootRule { Name = "gems", ObjectClass = PluginObjectClass.Gem, MinValue = 1000 }],
        };
        PluginInventoryItem gem = Item(5, "Diamond", PluginObjectClass.Gem, value: 5000);

        Assert.True(rules.Decide(gem, isAppraised: false).RequiresAppraisal);
        Assert.Equal(LootAction.Keep, rules.Decide(gem, isAppraised: true).Action);
        Assert.Equal(LootAction.Ignore, rules.Decide(gem with { Value = 10 }, isAppraised: true).Action);
    }

    [Fact]
    public void ItemsMatchingNothingAreIgnoredWithoutAppraisal()
    {
        LootRuleSet rules = LootRuleSet.Default;

        LootDecision decision = rules.Decide(Item(9, "Rotten Fish", PluginObjectClass.Food), false);

        Assert.Equal(LootAction.Ignore, decision.Action);
        Assert.False(decision.RequiresAppraisal);
    }

    [Fact]
    public void RuleSetRoundTripsThroughJson()
    {
        var profile = new BotProfile
        {
            Loot = new LootSettings
            {
                Rules = new LootRuleSet
                {
                    Rules = [new LootRule { Name = "gold", ObjectClass = PluginObjectClass.Gem, MinValue = 1000, Materials = [1u, 2u] }],
                },
            },
        };

        BotProfile restored = BotProfile.FromJson(profile.ToJson());

        LootRule rule = Assert.Single(restored.Loot.Rules.Rules);
        Assert.Equal("gold", rule.Name);
        Assert.Equal(PluginObjectClass.Gem, rule.ObjectClass);
        Assert.Equal(1000, rule.MinValue);
        Assert.Equal([1u, 2u], rule.Materials);
    }

    [Fact]
    public void LootBehaviorOpensAppraisesPicksUpAndFinishesACorpse()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new LootSettings
        {
            Rules = new LootRuleSet
            {
                Rules =
                [
                    new LootRule { Name = "money", ObjectClass = PluginObjectClass.Money },
                    new LootRule { Name = "gems", ObjectClass = PluginObjectClass.Gem, MinValue = 1000 },
                ],
            },
        };
        var behavior = new LootBehavior(() => settings);
        surface.Corpses.Add(new PluginLootContainer(0xC0u, 0u, "Corpse of Drudge", 3f, false, false, false));
        surface.CorpseContents[0xC0u] =
        [
            Item(1, "Pyreal", PluginObjectClass.Money),
            Item(2, "Rotten Fish", PluginObjectClass.Food),
            Item(3, "Ruby", PluginObjectClass.Gem, value: 4000),
        ];

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));
        BehaviorStep Step(double dt = 0.1)
        {
            clock.Advance(dt);
            return behavior.Execute(Context());
        }

        Assert.True(behavior.WantsControl(Context().Board, out _));
        Assert.Equal(StepResult.Continue, Step().Result);
        Assert.Equal(["open:192"], surface.Commands);

        surface.CurrentContainerId = 0xC0u;
        Assert.Equal(StepResult.Continue, Step().Result); // opened -> evaluating
        Assert.Equal(StepResult.Continue, Step().Result); // pyreal pickup
        Assert.Equal("pickup:1", surface.Commands[^1]);
        surface.CompletePickup(1);
        Assert.Equal(StepResult.Continue, Step().Result); // pickup confirmed
        Assert.Equal(StepResult.Continue, Step().Result); // fish ignored, ruby needs appraisal
        Assert.Equal("identify:3", surface.Commands[^1]);
        surface.AppraisedObjects.Add(3);
        Assert.Equal(StepResult.Continue, Step().Result); // appraised -> evaluating
        Assert.Equal(StepResult.Continue, Step().Result); // ruby pickup
        Assert.Equal("pickup:3", surface.Commands[^1]);
        surface.CompletePickup(3);
        Assert.Equal(StepResult.Continue, Step().Result);
        Assert.Equal(StepResult.Done, Step().Result);

        Assert.Equal(1, behavior.FinishedCorpseCount);
        Assert.False(behavior.WantsControl(Context().Board, out _));
    }

    [Fact]
    public void ACorpseThatNeverOpensIsAbandoned()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new LootSettings { StepTimeoutSeconds = 2d };
        var behavior = new LootBehavior(() => settings);
        surface.Corpses.Add(new PluginLootContainer(0xC1u, 0u, "Corpse", 2f, false, false, false));

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));
        behavior.Execute(Context());
        clock.Advance(3d);

        BehaviorStep step = behavior.Execute(Context());

        Assert.Equal(StepResult.Failed, step.Result);
        Assert.Equal(1, behavior.FinishedCorpseCount);
        Assert.False(behavior.WantsControl(Context().Board, out _));
    }
}
