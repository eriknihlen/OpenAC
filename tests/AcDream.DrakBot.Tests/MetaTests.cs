using AcDream.Core.Selection;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class MetaTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ── .af parsing ─────────────────────────────────────────────────────

    [Fact]
    public void ParsesARealMetaWithItsStatesRulesAndEmbeddedRoutes()
    {
        LoadedMeta meta = AfFileParser.Load(Fixture("conquest10.af"));

        Assert.Empty(meta.Warnings);
        Assert.Equal(9, meta.Rules.Select(rule => rule.State).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(meta.Rules, rule => rule.State == "Dead" && rule.Condition == MetaConditionType.Expression && rule.ConditionData == "vitae[]>=11");

        MetaRule turnIn = meta.Rules.First(rule => rule.State == "CarapaceTurnIn" && rule.Condition == MetaConditionType.All && rule.Children.Count == 2);
        Assert.Equal(MetaConditionType.InventoryItemCount_LE, turnIn.Children[0].Condition);
        Assert.Equal("Olthoi Slayer Carapace,0", turnIn.Children[0].ConditionData);
        Assert.Equal(MetaActionType.All, turnIn.Action);
        Assert.Equal(MetaActionType.SetMetaState, turnIn.ActionChildren[0].Action);
        Assert.Equal("Rebuff", turnIn.ActionChildren[0].ActionData);

        Assert.Contains("ToSanamarCollector", meta.EmbeddedNavs.Keys);
        Route route = NavFile.Parse("ToSanamarCollector", meta.EmbeddedNavs["ToSanamarCollector"], out string? warning);
        Assert.Null(warning);
        Assert.Equal(RouteMode.Once, route.Mode);
        Assert.Equal(WaypointKind.Recall, route.Waypoints[0].Kind);
        Assert.Contains(route.Waypoints, waypoint => waypoint.Kind == WaypointKind.Portal && waypoint.TargetName == "Portal to Yaraq");
        Assert.Equal(WaypointKind.Chat, NavFile.Parse("b", meta.EmbeddedNavs["CQUseBuffer"], out _).Waypoints[0].Kind);
    }

    [Fact]
    public void AfRoundTripsThroughTheWriter()
    {
        LoadedMeta meta = AfFileParser.Load(Fixture("conquest10.af"));
        string text = AfFileWriter.SaveToString(meta.Rules, meta.EmbeddedNavs);
        LoadedMeta back = AfFileParser.LoadFromText(text);

        Assert.Equal(meta.Rules.Count, back.Rules.Count);
        Assert.Equal(meta.EmbeddedNavs.Keys.OrderBy(k => k), back.EmbeddedNavs.Keys.OrderBy(k => k));
        for (int index = 0; index < meta.Rules.Count; index++)
        {
            Assert.Equal(meta.Rules[index].State, back.Rules[index].State);
            Assert.Equal(meta.Rules[index].Condition, back.Rules[index].Condition);
            Assert.Equal(meta.Rules[index].Action, back.Rules[index].Action);
        }
    }

    // ── expressions ─────────────────────────────────────────────────────

    private static (ExpressionEngine Engine, FakeAutomationSurface Surface) Expressions()
    {
        var surface = new FakeAutomationSurface();
        var world = new MetaWorld(surface, new MemoryStorage(), new FakeLogger(), new SelectionState(), () => 0d);
        var engine = new ExpressionEngine(world);
        engine.SetPlayerId(surface.ObjectId);
        return (engine, surface);
    }

    [Theory]
    [InlineData("1+2*3", "7")]
    [InlineData("(1+2)*3", "9")]
    [InlineData("10/4", "2.5")]
    [InlineData("7%3", "1")]
    [InlineData("1==1", "1")]
    [InlineData("2>3", "0")]
    [InlineData("1&&0", "0")]
    [InlineData("1||0", "1")]
    [InlineData("iif[1>0, `yes`, `no`]", "yes")]
    [InlineData("strlen[`hello`]", "5")]
    [InlineData("`Drudge Slasher`#`drudge`", "1")]
    [InlineData("`abc`+`def`", "abcdef")]
    [InlineData("listcount[listadd[listadd[listcreate[], 1], 2]]", "2")]
    [InlineData("floor[2.7]", "2")]
    public void EvaluatesTheVTankExpressionLanguage(string expression, string expected)
    {
        (ExpressionEngine engine, _) = Expressions();
        Assert.Equal(expected, engine.Evaluate(expression));
    }

    [Fact]
    public void VariablesReadTheWorldThroughTheSurface()
    {
        (ExpressionEngine engine, FakeAutomationSurface surface) = Expressions();
        surface.CurrentHealth = 37;
        surface.MaxHealth = 120;
        surface.Position = new PluginNavigationPosition(0x00A9_0100u, 0.5d, -0.25d, 0d, 0f, true);

        Assert.Equal("1", engine.Evaluate("setvar[x, 5]>0"));
        Assert.Equal("5", engine.Evaluate("getvar[x]"));
        Assert.Equal("6", engine.Evaluate("$x+1"));
        Assert.Equal("37", engine.Evaluate("getcharvital_current[1]"));
        Assert.Equal("120", engine.Evaluate("getcharvital_buffedmax[1]"));
        Assert.Equal(((double)0x00A9_0000u).ToString("G"), engine.Evaluate("getplayerlandblock[]"));
        Assert.Equal("1", engine.Evaluate("coordinategetns[getplayercoordinates[]] == -0.25"));
        Assert.Equal("0", engine.Evaluate("isportaling[]"));
    }

    [Fact]
    public void PersistentVariablesLiveInPluginStorage()
    {
        var surface = new FakeAutomationSurface();
        var storage = new MemoryStorage();
        var world = new MetaWorld(surface, storage, new FakeLogger(), new SelectionState(), () => 0d);
        var engine = new ExpressionEngine(world);
        engine.SetPlayerId(surface.ObjectId);
        surface.Name = "Drakkon";

        engine.Evaluate("setpvar[kills, 3]");
        engine.Evaluate("setgvar[shared, yes]");
        engine.FlushVars(force: true);

        Assert.Contains("meta/pvars/Drakkon.txt", storage.Text.Keys);
        Assert.Contains("meta/gvars.txt", storage.Text.Keys);
        var again = new ExpressionEngine(new MetaWorld(surface, storage, new FakeLogger(), new SelectionState(), () => 0d));
        again.SetPlayerId(surface.ObjectId);
        Assert.Equal("3", again.Evaluate("getpvar[kills]"));
        Assert.Equal("yes", again.Evaluate("getgvar[shared]"));
    }

    [Fact]
    public void WorldObjectFunctionsFindThingsAndActOnThem()
    {
        (ExpressionEngine engine, FakeAutomationSurface surface) = Expressions();
        surface.WorldObjects.Add(new PluginWorldObject(0x7000_0001u, 100u, "Portal to Town Network", PluginObjectClass.Portal, 0u, 0u, 0u)
        {
            IsLandscape = true, HasPosition = true, Position = new PluginNavigationPosition(0x00010100u, 0.01d, 0d, 0d, 0f, true),
        });
        surface.ObjectPositions[0x7000_0001u] = new PluginNavigationPosition(0x00010100u, 0.01d, 0d, 0d, 0f, true);

        // Handles are held in variables, as a meta would hold them.
        string handle = engine.Evaluate("setvar[p, wobjectfindnearestbyobjectclass[14]]");
        Assert.Contains("Portal to Town Network", handle);
        Assert.Equal("Portal to Town Network", engine.Evaluate("wobjectgetname[$p]"));
        Assert.Equal("1879048193", engine.Evaluate("wobjectgetid[$p]"));
        Assert.Equal("0", engine.Evaluate("actiontryuseitem[$p]"));
        Assert.Contains("useobject:1879048193", surface.Commands);
    }

    // ── the state machine ───────────────────────────────────────────────

    private sealed class MemoryStorage : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
        public bool IsAvailable => true;
        public string? ReadText(string key) => Text.TryGetValue(key, out string? value) ? value : null;
        public IReadOnlyList<string> List(string prefix) => Text.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        public void WriteText(string key, string content) => Text[key] = content;
        public bool Delete(string key) => Text.Remove(key);
    }

    private sealed class FakeBot : IMetaBot
    {
        public Route? CurrentRoute { get; private set; }
        public int RouteIndex => 0;
        public bool NeedsBuff { get; set; }
        public List<string> Commands { get; } = [];
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase) { ["EnableCombat"] = "1" };
        public Dictionary<string, LoadedMeta> Metas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Route> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SetRoute(Route? route, bool enableNavigation) => CurrentRoute = route;
        public bool NeedsAnyBuff(Blackboard board) => NeedsBuff;
        public Dictionary<string, (Func<string> Get, Action<string> Set)> OptionMap() =>
            Options.Keys.ToDictionary(
                key => key,
                key => ((Func<string>)(() => Options[key]), (Action<string>)(value => Options[key] = value)),
                StringComparer.OrdinalIgnoreCase);
        public LoadedMeta? LoadMetaByName(string name, out string path)
        {
            path = name + ".af";
            return Metas.TryGetValue(name, out LoadedMeta? meta) ? meta : null;
        }
        public Route? LoadRouteByName(string name) => Routes.TryGetValue(name, out Route? route) ? route : null;
        public bool TryHandleCommand(string command)
        {
            if (!command.StartsWith("/drakbot ", StringComparison.OrdinalIgnoreCase))
                return false;
            Commands.Add(command);
            return true;
        }
    }

    private static (MetaEngine Meta, FakeAutomationSurface Surface, FakeBot Bot, TickClock Clock) Engine(string af)
    {
        var surface = new FakeAutomationSurface();
        var clock = new TickClock();
        var bot = new FakeBot();
        var options = new MetaOptions { Enabled = true };
        var world = new MetaWorld(surface, new MemoryStorage(), new FakeLogger(), new SelectionState(), () => clock.Now);
        var meta = new MetaEngine(world, bot, clock, new FakeLogger(), () => options, change => options = change(options));
        meta.Load(AfFileParser.LoadFromText(af), "test");
        return (meta, surface, bot, clock);
    }

    private static Blackboard Board(FakeAutomationSurface surface, TickClock clock) =>
        Blackboard.Capture(surface, clock, 25f, 15f);

    private const string HuntMeta = """
        STATE: {Default} ~~ {
        	IF:	Always
        		DO:	DoAll
        					Chat {/vt opt set enablecombat false}
        					SetState {Hunt}
        ~~ }
        STATE: {Hunt} ~~ {
        	IF:	MainHealthPHE 50
        		DO:	CallState {Flee}
        	IF:	SecsInStateGE 10
        		DO:	Chat {/say still hunting}
        	IF:	ChatCapture ^\[Fellowship\] (?<who>\w+) says, "go (\w+)"
        		DO:	SetState {{1}}
        ~~ }
        STATE: {Flee} ~~ {
        	IF:	Expr getcharvital_current[1] > 80
        		DO:	Return
        ~~ }
        STATE: {Town} ~~ {
        	IF:	Always
        		DO:	EmbedNav TownLoop {[none]}
        ~~ }
        NAV: TownLoop circular ~~ {
        	pnt 1 2 0
        	pnt 3 4 0
        ~~ }
        """;

    [Fact]
    public void RulesFireOncePerStateVisitAndActionsReachTheBot()
    {
        (MetaEngine meta, FakeAutomationSurface surface, FakeBot bot, TickClock clock) = Engine(HuntMeta);
        Assert.Equal("Default", meta.CurrentState);

        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Hunt", meta.CurrentState);
        Assert.Equal("0", bot.Options["EnableCombat"]); // /vt opt set translated, not sent to chat
        Assert.DoesNotContain(surface.Commands, c => c.StartsWith("chat:", StringComparison.Ordinal));

        // Nothing fires while healthy and fresh in the state.
        clock.Advance(1d);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Hunt", meta.CurrentState);

        // Ten seconds in: the chat line goes out once.
        clock.Advance(10d);
        meta.Think(Board(surface, clock), botRunning: true);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Single(surface.Commands, "chat:/say still hunting");
    }

    [Fact]
    public void CallAndReturnKeepAStateStack()
    {
        (MetaEngine meta, FakeAutomationSurface surface, _, TickClock clock) = Engine(HuntMeta);
        meta.Think(Board(surface, clock), botRunning: true); // -> Hunt

        surface.CurrentHealth = 40;
        clock.Advance(0.1d);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Flee", meta.CurrentState);
        Assert.Equal(1, meta.StackDepth);

        surface.CurrentHealth = 95;
        clock.Advance(0.1d);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Hunt", meta.CurrentState);
        Assert.Equal(0, meta.StackDepth);
    }

    [Fact]
    public void ChatCaptureGroupsFillTheActionAndEmbedNavLoadsTheRoute()
    {
        (MetaEngine meta, FakeAutomationSurface surface, FakeBot bot, TickClock clock) = Engine(HuntMeta);
        meta.Think(Board(surface, clock), botRunning: true); // -> Hunt

        surface.Hear("[Fellowship] Drakkon says, \"go Town\"");
        clock.Advance(0.1d);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Town", meta.CurrentState);

        clock.Advance(0.1d);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.NotNull(bot.CurrentRoute);
        Assert.Equal(2, bot.CurrentRoute!.Waypoints.Count);
        Assert.Equal(RouteMode.Loop, bot.CurrentRoute.Mode);
    }

    [Fact]
    public void TheRulesRestWhileTheBotIsStoppedAndReArmOnStart()
    {
        (MetaEngine meta, FakeAutomationSurface surface, _, TickClock clock) = Engine(HuntMeta);
        meta.Think(Board(surface, clock), botRunning: false);
        Assert.Equal("Default", meta.CurrentState);
        meta.Think(Board(surface, clock), botRunning: true);
        Assert.Equal("Hunt", meta.CurrentState);
    }

    [Fact]
    public void VtCommandsAreTranslated()
    {
        (MetaEngine meta, FakeAutomationSurface surface, FakeBot bot, _) = Engine(HuntMeta);
        bot.Metas["other"] = AfFileParser.LoadFromText("STATE: {Start} ~~ {\n\tIF:\tAlways\n\t\tDO:\tNone\n~~ }\n");

        meta.SendCommand("/vt setmetastate Flee");
        Assert.Equal("Flee", meta.CurrentState);
        meta.SendCommand("/vt meta load other");
        Assert.Equal("other", meta.MetaName);
        Assert.Equal("Start", meta.CurrentState);
        meta.SendCommand("/vt forcebuff");
        Assert.Contains("/drakbot forcebuff", bot.Commands);
        meta.SendCommand("/say hello");
        Assert.Contains("chat:/say hello", surface.Commands);
    }
}
