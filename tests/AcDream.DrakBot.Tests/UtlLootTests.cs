using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class UtlLootTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static PluginInventoryItem Item(uint id, string name, PluginObjectClass objectClass, int value = 0, float workmanship = 0f, uint wcid = 0u) => new(
        id, wcid, name, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0)
    {
        ObjectClass = objectClass,
        Value = value,
        Workmanship = workmanship,
    };

    [Fact]
    public void ParsesARealLootSnobProfileAndWritesItBack()
    {
        VTankLootProfile profile = VTankLootParser.Load(Fixture("lootsnob-head.utl"));

        Assert.Equal(1, profile.FileVersion);
        Assert.True(profile.Rules.Count > 20);
        VTankLootRule aetheria = profile.Rules.First(r => r.Name.Contains("Aetheria (5)", StringComparison.Ordinal));
        Assert.Equal(VTankLootAction.Keep, aetheria.Action);
        Assert.Equal(2, aetheria.Conditions.Count);
        Assert.Equal(VTankNodeTypes.StringValueMatch, aetheria.Conditions[0].NodeType);
        Assert.Equal(VTankNodeTypes.LongValKeyE, aetheria.Conditions[1].NodeType);
        Assert.Equal("218103849", aetheria.Conditions[1].DataLines[1]);
        Assert.False(profile.Rules[0].Enabled); // the "leave this disabled" header rule

        string text = VTankLootWriter.Serialize(profile);
        VTankLootProfile back = VTankLootParser.LoadFromText(text);
        Assert.Equal(profile.Rules.Count, back.Rules.Count);
        Assert.Equal(profile.Rules.Select(r => r.Name), back.Rules.Select(r => r.Name));
    }

    [Fact]
    public void DecalKeysAreAnsweredFromTheItemAndItsAppraisal()
    {
        VTankLootProfile profile = VTankLootParser.Load(Fixture("lootsnob-head.utl"));
        VTankLootRule aetheria = profile.Rules.First(r => r.Name.Contains("Aetheria (5)", StringComparison.Ordinal));
        var surface = new FakeAutomationSurface();
        var context = new UtlLootContext(surface);

        PluginInventoryItem plain = Item(1, "Aetheria", PluginObjectClass.Misc);
        Assert.True(UtlLootEvaluator.NeedsAppraisal(aetheria));
        Assert.False(UtlLootEvaluator.Match(aetheria, plain, context)); // no overlay known yet

        surface.Properties[1] = new PluginItemProperties(
            new Dictionary<uint, int>(), new Dictionary<uint, long>(), new Dictionary<uint, bool>(),
            new Dictionary<uint, double>(), new Dictionary<uint, string>(),
            new Dictionary<uint, uint> { [50u] = 27704u }, new Dictionary<uint, uint>());
        Assert.True(UtlLootEvaluator.Match(aetheria, plain, new UtlLootContext(surface)));
        Assert.False(UtlLootEvaluator.Match(aetheria, Item(1, "Rotten Fish", PluginObjectClass.Food), new UtlLootContext(surface)));
    }

    [Fact]
    public void RulesByNameAndClassDecideWithoutAnAppraisal()
    {
        VTankLootProfile profile = VTankLootParser.LoadFromText(string.Join('\n',
        [
            "UTL", "1", "2",
            "coins", "", "0;1;7", "1", "7",           // ObjectClass == Money
            "keys", "", "0;1;1", "8", "Key", "1",      // name matches Key
        ]));
        var context = new UtlLootContext(new FakeAutomationSurface());

        Assert.False(UtlLootEvaluator.NeedsAppraisal(profile.Rules[0]));
        Assert.False(UtlLootEvaluator.NeedsAppraisal(profile.Rules[1]));
        Assert.Equal("coins", UtlLootEvaluator.FirstMatch(profile, Item(1, "Pyreal", PluginObjectClass.Money), context)!.Name);
        Assert.Equal("keys", UtlLootEvaluator.FirstMatch(profile, Item(2, "Rusty Key", PluginObjectClass.Key), context)!.Name);
        Assert.Null(UtlLootEvaluator.FirstMatch(profile, Item(3, "Rotten Fish", PluginObjectClass.Food), context));
    }

    [Fact]
    public void TheLootBehaviorFollowsTheNamedProfile()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var settings = new LootSettings { UtlProfile = "snob" };
        VTankLootProfile profile = VTankLootParser.LoadFromText(string.Join('\n',
        [
            "UTL", "1", "2",
            "coins", "", "0;1;7", "1", "7",
            "good gems", "", "0;1;7;3", "1", "11", "8", "1000", "19",   // Gem with Value >= 1000
        ]));
        var behavior = new LootBehavior(() => settings, name => name == "snob" ? profile : null);
        surface.Corpses.Add(new PluginLootContainer(0xC0u, 0u, "Corpse of Drudge", 3f, false, false, false));
        surface.CorpseContents[0xC0u] =
        [
            Item(1, "Pyreal", PluginObjectClass.Money),
            Item(2, "Rotten Fish", PluginObjectClass.Food),
            Item(3, "Ruby", PluginObjectClass.Gem, value: 4000),
        ];

        BehaviorContext Context() => new(surface, new FakeLogger(), Blackboard.Capture(surface, clock, 25f, 15f));
        BehaviorStep Step()
        {
            clock.Advance(0.1d);
            return behavior.Execute(Context());
        }

        Step();
        surface.CurrentContainerId = 0xC0u;
        Step();
        Step();
        Assert.Equal("pickup:1", surface.Commands[^1]);
        surface.CompletePickup(1);
        Step();
        Step();
        Assert.Equal("identify:3", surface.Commands[^1]); // the fish needs no look; the ruby's value does
        surface.AppraisedObjects.Add(3);
        Step();
        Step();
        Assert.Equal("pickup:3", surface.Commands[^1]);
    }
}
