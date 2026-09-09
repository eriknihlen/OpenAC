using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MetaEngineTests
{
    [Fact]
    public void RuleFiresOncePerStateEntryAndCanFireAfterReentry()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var profile = new MetaProfile
        {
            Rules =
            [
                Rule(MetaConditionKind.Always, MetaActionKind.ExpressionAction,
                    "setvar['count',getvar['count']+1]"),
            ],
        };
        var engine = new MetaEngine(host, expressions, profile);
        engine.SetEnabled(true);

        engine.EvaluatePass();
        engine.EvaluatePass();
        Assert.Equal(1d, expressions.Evaluate("getvar['count']").AsNumber());
        Assert.Equal(1, engine.FiredRuleCount);

        engine.Transition(MetaEngine.DefaultState);
        engine.EvaluatePass();
        Assert.Equal(2d, expressions.Evaluate("getvar['count']").AsNumber());
    }

    [Fact]
    public void StateTransitionStopsTheOldStatesOrderedPass()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var profile = new MetaProfile
        {
            Rules =
            [
                Rule(MetaConditionKind.Always, MetaActionKind.SetMetaState, "Next"),
                Rule(MetaConditionKind.Always, MetaActionKind.ExpressionAction,
                    "setvar['wrong',1]"),
                Rule(MetaConditionKind.Always, MetaActionKind.ExpressionAction,
                    "setvar['right',1]", "Next"),
            ],
        };
        var engine = new MetaEngine(host, expressions, profile);
        engine.SetEnabled(true);

        engine.EvaluatePass();
        Assert.Equal("Next", engine.CurrentState);
        Assert.Equal(0d, expressions.Evaluate("getvar['wrong']").AsNumber());
        engine.EvaluatePass();
        Assert.Equal(1d, expressions.Evaluate("getvar['right']").AsNumber());
    }

    [Fact]
    public void CallAndReturnUseTheVtankReturnStateStack()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var call = Rule(MetaConditionKind.Always, MetaActionKind.CallMetaState, "Worker");
        call.Action.SecondaryText = "ReturnHere";
        var profile = new MetaProfile
        {
            Rules =
            [
                call,
                Rule(MetaConditionKind.Always, MetaActionKind.ReturnFromCall, state: "Worker"),
                Rule(MetaConditionKind.Always, MetaActionKind.ExpressionAction,
                    "setvar['returned',1]", "ReturnHere"),
            ],
        };
        var engine = new MetaEngine(host, expressions, profile);
        engine.SetEnabled(true);

        engine.EvaluatePass();
        Assert.Equal("Worker", engine.CurrentState);
        Assert.Equal(1, engine.CallDepth);
        engine.EvaluatePass();
        Assert.Equal("ReturnHere", engine.CurrentState);
        Assert.Equal(0, engine.CallDepth);
        engine.EvaluatePass();
        Assert.Equal(1d, expressions.Evaluate("getvar['returned']").AsNumber());
    }

    [Fact]
    public void ChatCapturePublishesGroupsAndColorToExpressionVariables()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var condition = new MetaCondition
        {
            Kind = MetaConditionKind.ChatMessageCapture,
            Text = "^(?<name>.+) tells you, \\\"(?<words>.+)\\\"$",
            SecondaryText = "3;4",
        };
        var profile = new MetaProfile
        {
            Rules =
            [
                new MetaRule
                {
                    Condition = condition,
                    Action = new MetaAction
                    {
                        Kind = MetaActionKind.ExpressionAction,
                        Text = "setvar['matched',1]",
                    },
                },
            ],
        };
        var engine = new MetaEngine(host, expressions, profile);
        engine.SetEnabled(true);
        host.Automation.Messages.Add(new PluginChatMessage(
            1, 20, 3, "Horan", "Horan tells you, \"ready\"", "Tells"));

        engine.OnTick(MetaEngine.DecisionIntervalSeconds);

        Assert.Equal("Horan", expressions.Evaluate(
            "getvar['capturegroup_name']").AsString());
        Assert.Equal("ready", expressions.Evaluate(
            "getvar['capturegroup_words']").AsString());
        Assert.Equal(3d, expressions.Evaluate("getvar['capturecolor']").AsNumber());
        Assert.Equal(1d, expressions.Evaluate("getvar['matched']").AsNumber());
    }

    [Fact]
    public void WatchdogCallsRecoveryStateOnlyWhenMovementStalls()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var setWatchdog = Rule(MetaConditionKind.Always, MetaActionKind.SetWatchdog);
        setWatchdog.Action.Text = "Recover";
        setWatchdog.Action.Number = 5d;
        setWatchdog.Action.SecondaryNumber = 1d;
        var profile = new MetaProfile { Rules = [setWatchdog] };
        var engine = new MetaEngine(host, expressions, profile);
        engine.SetEnabled(true);
        engine.EvaluatePass();

        for (int index = 0; index < 13; index++)
            engine.OnTick(0.1d);

        Assert.Equal("Recover", engine.CurrentState);
        Assert.Equal(1, engine.CallDepth);
    }

    [Fact]
    public void RecursiveCallOverflowDisablesMetaLikeVtank()
    {
        var host = new Host();
        using var expressions = new MossTankExpressionRuntime(host);
        var call = Rule(MetaConditionKind.Always, MetaActionKind.CallMetaState, "Loop");
        call.Action.SecondaryText = "Loop";
        call.State = "Loop";
        var engine = new MetaEngine(
            host,
            expressions,
            new MetaProfile { Rules = [call] });
        engine.Transition("Loop");
        engine.SetEnabled(true);

        for (int index = 0; index <= MetaEngine.MaximumCallDepth; index++)
            engine.EvaluatePass();

        Assert.False(engine.Enabled);
        Assert.Contains("overflow", engine.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static MetaRule Rule(
        MetaConditionKind condition,
        MetaActionKind action,
        string text = "",
        string state = MetaEngine.DefaultState) => new()
    {
        State = state,
        Condition = new MetaCondition { Kind = condition },
        Action = new MetaAction { Kind = action, Text = text },
    };

    private sealed class Host : IPluginHost
    {
        public Host() => Automation = new Automation();
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new Logger();
        public IGameState State { get; } = new State();
        public IEvents Events { get; } = new Events();
        public ISelectionService Selection { get; } = new Selection();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage => NoOpPluginStorage.Instance;
        public Automation Automation { get; }
        IAutomationSurface IPluginHost.Automation => Automation;
    }

    private sealed class Automation :
        IAutomationSurface,
        ICharacterInfo,
        IPluginChat,
        INavigationAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => this;
        public INavigationAutomation Navigation => this;
        public bool IsInWorld => true;
        public string Name => "Meta Tester";
        public string WorldName => "Coldeve";
        public string AccountName => "testaccount";
        public uint ObjectId => 1;
        public uint CurrentHealth { get; set; } = 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills => [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public List<PluginChatMessage> Messages { get; } = [];
        public bool IsPortal { get; set; }
        public PluginNavigationPosition Position { get; set; } = new(
            0x7F7F0001u, 10d, 20d, 0d, 0f, true);

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }
        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
            Messages.Where(message => message.Sequence > afterSequence).ToArray();
        public void PostSystemMessage(string text) { }
        public PluginNavigationSnapshot Snapshot => new(
            true, IsPortal, ObjectId, Position, false, false);
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            value = default;
            return false;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent) => PluginNavigationCommandStatus.Accepted;
        public PluginNavigationCommandStatus ClearMovementIntent() =>
            PluginNavigationCommandStatus.Accepted;
    }

    private sealed class Logger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
    private sealed class State : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }
    private sealed class Events : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }
        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }
    private sealed class Selection : ISelectionService
    {
        public uint? SelectedObjectId => null;
        public uint? PreviousObjectId => null;
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId) => true;
        public bool Clear() => true;
    }
}
