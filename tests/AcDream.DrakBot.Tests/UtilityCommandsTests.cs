using AcDream.Core.Selection;
using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class UtilityCommandsTests
{
    private sealed class FakeBot : IMetaBot
    {
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase) { ["EnableCombat"] = "1", ["MonsterRange"] = "20" };
        public Route? CurrentRoute => null;
        public int RouteIndex => 0;
        public void SetRoute(Route? route, bool enableNavigation) { }
        public bool NeedsAnyBuff(Blackboard board) => false;
        public Dictionary<string, (Func<string> Get, Action<string> Set)> OptionMap() =>
            Options.Keys.ToDictionary(
                key => key,
                key => ((Func<string>)(() => Options[key]), (Action<string>)(value => Options[key] = value)),
                StringComparer.OrdinalIgnoreCase);
        public LoadedMeta? LoadMetaByName(string name, out string path) { path = name; return null; }
        public Route? LoadRouteByName(string name) => null;
        public bool TryHandleCommand(string command) => false;
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
        public bool IsAvailable => true;
        public string? ReadText(string key) => _texts.TryGetValue(key, out string? text) ? text : null;
        public void WriteText(string key, string content) => _texts[key] = content;
    }

    private static PluginInventoryItem Item(uint id, string name, uint container, bool worn = false, int stack = 1) => new(
        id, 0u, name, 0u, container, 0u, 0u, worn ? 0x10u : 0u, 0u, 0u, 0u, stack, 0, 0, 0u, 0, 0, 0u,
        false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private static (UtilityCommands Commands, FakeAutomationSurface Surface, FakeBot Bot, TickClock Clock) Build()
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var bot = new FakeBot();
        var options = new MetaOptions { Enabled = true };
        var world = new MetaWorld(surface, new MemoryStorage(), new FakeLogger(), new SelectionState(), () => clock.Now);
        var meta = new MetaEngine(world, bot, clock, new FakeLogger(), () => options, change => options = change(options));
        var commands = new UtilityCommands(
            world,
            meta,
            surface,
            name => name == "salvage" ? Utl : null,
            verb => { surface.Commands.Add("bot:" + verb); return true; });
        meta.Utility = commands;
        return (commands, surface, bot, clock);
    }

    private static readonly VTankLootProfile Utl = new()
    {
        Rules =
        [
            new VTankLootRule
            {
                Name = "Gems",
                Action = VTankLootAction.Keep,
                Conditions = [new VTankLootCondition(VTankNodeTypes.ObjectClass, "0", [((int)PluginObjectClass.Gem).ToString()])],
            },
        ],
    };

    [Fact]
    public void OptionsAreReadSetRememberedAndRestoredUnderVTankNames()
    {
        (UtilityCommands commands, FakeAutomationSurface surface, FakeBot bot, _) = Build();
        Assert.True(commands.TryHandle("/mt opt remember attackdistance"));
        Assert.True(commands.TryHandle("/ub opt set attackdistance 35"));
        Assert.Equal("35", bot.Options["MonsterRange"]);
        Assert.True(commands.TryHandle("/mt opt set enablecombat false"));
        Assert.Equal("0", bot.Options["EnableCombat"]);
        Assert.True(commands.TryHandle("/mt opt restore attackdistance"));
        Assert.Equal("20", bot.Options["MonsterRange"]);
        Assert.True(commands.TryHandle("/mt opt get attackdistance"));
        Assert.Contains(surface.SystemMessages, line => line.Contains("attackdistance = 20"));
        Assert.False(commands.TryHandle("/vt opt set attackdistance 1"));
        Assert.False(commands.TryHandle("/mt nosuchverb"));
    }

    [Fact]
    public void UseSelectAndCastResolveNamesInThePackAndOnTheLandscape()
    {
        (UtilityCommands commands, FakeAutomationSurface surface, _, _) = Build();
        surface.OwnedItems.Add(Item(11, "Healing Kit", surface.ObjectId));
        surface.OwnedItems.Add(Item(12, "Portal Gem", surface.ObjectId));
        surface.WorldObjects.Add(new PluginWorldObject(0x3001u, 0u, "Town Crier", PluginObjectClass.Npc, 0u, 0u, 0u));
        surface.WorldObjects.Add(new PluginWorldObject(0x3002u, 0u, "Gateway", PluginObjectClass.Portal, 0u, 0u, 0u));
        surface.SelfBuffs.Add(Spell.SelfBuff(2062u, "Regeneration Self VI", 100u, 6));

        Assert.True(commands.TryHandle("/mt usep heal"));
        Assert.Equal("use:11", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/ub use closestportal"));
        Assert.Equal("useobject:12290", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt use Town Crier"));
        Assert.Equal("useobject:12289", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt usei Town Crier")); // inventory only: not found, answered in chat
        Assert.Contains(surface.SystemMessages, line => line.Contains("not found: 'Town Crier'"));
        Assert.True(commands.TryHandle("/mt castp regeneration"));
        Assert.Equal("cast:2062", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt cast 2062 on Town Crier"));
        Assert.Equal("cast:2062@12289", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt combatstate magic"));
        Assert.Equal("mode:Magic", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt face 90"));
        Assert.Equal("face:90", surface.Commands[^1]);
    }

    [Fact]
    public void PackVerbsMoveGiveDropAndDequip()
    {
        (UtilityCommands commands, FakeAutomationSurface surface, _, _) = Build();
        surface.OwnedItems.Add(Item(11, "Sturdy Iron Key", surface.ObjectId));
        surface.OwnedItems.Add(Item(12, "Yumi", surface.ObjectId, worn: true));
        surface.WorldObjects.Add(new PluginWorldObject(0x3001u, 0u, "Ulgrim", PluginObjectClass.Npc, 0u, 0u, 0u));

        Assert.True(commands.TryHandle("/mt give Sturdy Iron Key to Ulgrim"));
        Assert.Equal("give:11>12289", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt dropp key"));
        Assert.Equal("drop:11", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt dequip Yumi"));
        Assert.Equal($"move:12>{surface.ObjectId}", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt fellow recruit Ulgrim"));
        Assert.Equal("recruit:12289", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt fellow open"));
        Assert.Equal("fellowopen:True", surface.Commands[^1]);
        Assert.True(commands.TryHandle("/mt send /tell Ulgrim, hello"));
        Assert.Equal("chat:/tell Ulgrim, hello", surface.Commands[^1]);
    }

    [Fact]
    public void GiveAllQueuesEveryMatchingStackAndHandsThemOverOneAtATime()
    {
        (UtilityCommands commands, FakeAutomationSurface surface, _, TickClock clock) = Build();
        surface.OwnedItems.Add(Item(11, "Prismatic Taper", surface.ObjectId, stack: 100));
        surface.OwnedItems.Add(Item(12, "Prismatic Taper", surface.ObjectId, stack: 40));
        surface.OwnedItems.Add(Item(13, "Yumi", surface.ObjectId, worn: true));
        surface.OwnedItems.Add(Item(14, "Diamond", surface.ObjectId) with { ObjectClass = PluginObjectClass.Gem });
        surface.WorldObjects.Add(new PluginWorldObject(0x3001u, 0u, "Drakkon's Mule", PluginObjectClass.Player, 0u, 0u, 0u));

        Assert.True(commands.TryHandle("/ra givea Prismatic Taper to Drakkon's Mule"));
        Assert.Equal(2, commands.QueuedGives);
        commands.Tick(clock.Now);
        Assert.Equal("give:11>12289", surface.Commands[^1]);
        commands.Tick(clock.Now); // too soon for the next
        Assert.Equal(1, commands.QueuedGives);
        clock.Advance(0.3d);
        commands.Tick(clock.Now);
        Assert.Equal("give:12>12289", surface.Commands[^1]);
        Assert.Equal(0, commands.QueuedGives);

        // A counted give hands over the first stack at once; a partial player name works too.
        Assert.True(commands.TryHandle("/ra givepp 1 taper to mule"));
        Assert.Equal("give:11>12289", surface.Commands[^1]);

        // A profile give keeps what the rules keep: the gem, not the tapers.
        Assert.True(commands.TryHandle("/ra igp salvage to mule"));
        Assert.Equal(1, commands.QueuedGives);
        clock.Advance(0.3d);
        commands.Tick(clock.Now);
        Assert.Equal("give:14>12289", surface.Commands[^1]);

        Assert.True(commands.TryHandle("/ra givea stop"));
        Assert.True(commands.TryHandle("/ra start"));
        Assert.Equal("bot:start", surface.Commands[^1]);
    }

    [Fact]
    public void LootWaitsForTheCorpseToOpen()
    {
        (UtilityCommands commands, FakeAutomationSurface surface, _, TickClock clock) = Build();
        Assert.True(commands.TryHandle("/mt lootp pyreal"));
        Assert.DoesNotContain(surface.Commands, command => command.StartsWith("use", StringComparison.Ordinal));

        surface.CurrentContainerId = 0x7000u;
        surface.CorpseContents[0x7000u] = [Item(0x7001u, "Pyreals", 0x7000u)];
        clock.Advance(1d);
        commands.Tick(clock.Now);
        Assert.Contains("useobject:28673", surface.Commands);
    }
}
