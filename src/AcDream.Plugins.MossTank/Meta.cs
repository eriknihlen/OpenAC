using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank;

internal enum MetaConditionKind
{
    Never,
    Always,
    All,
    Any,
    ChatMessage,
    PackSlotsLessThanOrEqual,
    SecondsInStateGreaterThanOrEqual,
    NavigationRouteEmpty,
    CharacterDeath,
    AnyVendorOpen,
    VendorClosed,
    InventoryItemCountLessThanOrEqual,
    InventoryItemCountGreaterThanOrEqual,
    MonsterNameCountWithinDistance,
    MonsterPriorityCountWithinDistance,
    NeedToBuff,
    NoMonstersWithinDistance,
    LandblockEquals,
    LandcellEquals,
    PortalspaceEntered,
    PortalspaceExited,
    Not,
    PersistentSecondsInStateGreaterThanOrEqual,
    TimeLeftOnSpellGreaterThanOrEqual,
    BurdenPercentGreaterThanOrEqual,
    DistanceFromAnyRoutePointGreaterThanOrEqual,
    Expression,
    ChatMessageCapture,
}

internal enum MetaActionKind
{
    None,
    SetMetaState,
    ChatCommand,
    All,
    LoadEmbeddedNavigationRoute,
    CallMetaState,
    ReturnFromCall,
    ExpressionAction,
    ChatExpression,
    SetWatchdog,
    ClearWatchdog,
    GetVtankOption,
    SetVtankOption,
    CreateView,
    DestroyView,
    DestroyAllViews,
}

internal sealed class MetaCondition
{
    public MetaConditionKind Kind { get; set; } = MetaConditionKind.Always;
    public string Text { get; set; } = string.Empty;
    public string SecondaryText { get; set; } = string.Empty;
    public double Number { get; set; }
    public double SecondaryNumber { get; set; }
    public double TertiaryNumber { get; set; }
    public List<MetaCondition> Children { get; set; } = [];

    public static MetaCondition Always() => new() { Kind = MetaConditionKind.Always };
}

internal sealed class MetaAction
{
    public MetaActionKind Kind { get; set; } = MetaActionKind.None;
    public string Text { get; set; } = string.Empty;
    public string SecondaryText { get; set; } = string.Empty;
    public double Number { get; set; }
    public double SecondaryNumber { get; set; }
    public List<MetaAction> Children { get; set; } = [];
    public NavigationSettings? EmbeddedRoute { get; set; }
}

internal sealed class MetaRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string State { get; set; } = MetaEngine.DefaultState;
    public MetaCondition Condition { get; set; } = MetaCondition.Always();
    public MetaAction Action { get; set; } = new();
    public bool Enabled { get; set; } = true;
}

internal sealed class MetaProfile
{
    public List<MetaRule> Rules { get; set; } = [];
}

internal sealed class MetaServices
{
    public Func<bool> IsNavigationRouteEmpty { get; init; } = static () => true;
    public Func<bool> NeedsBuff { get; init; } = static () => false;
    public Func<double> DistanceFromAnyRoutePoint { get; init; } =
        static () => double.PositiveInfinity;
    public Func<int, double, int> CountMonstersByPriority { get; init; } =
        static (_, _) => 0;
    public Action<NavigationSettings?> LoadEmbeddedNavigationRoute { get; init; } = static _ => { };
    public Func<string, ExpressionValue> GetOption { get; init; } =
        static _ => ExpressionValue.Zero;
    public Func<string, ExpressionValue, bool> SetOption { get; init; } =
        static (_, _) => false;
    public Func<string, string, bool> CreateView { get; init; } =
        static (_, _) => false;
    public Func<string, bool> DestroyView { get; init; } = static _ => false;
    public Action DestroyAllViews { get; init; } = static () => { };
}

internal sealed class MetaEngine
{
    public const string DefaultState = "Default";
    public const double DecisionIntervalSeconds = 0.293d;
    public const int MaximumCallDepth = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IPluginHost _host;
    private readonly MossTankExpressionRuntime _expressions;
    private readonly MetaServices _services;
    private readonly HashSet<Guid> _fired = [];
    private readonly Stack<string> _callStack = [];
    private readonly List<PluginChatMessage> _chatBatch = [];
    private MetaProfile _profile;
    private double _decisionAccumulator;
    private double _stateSeconds;
    private double _persistentStateSeconds;
    private ulong _chatSequence;
    private bool _wasPortalSpace;
    private bool _wasDead;
    private bool _portalEntered;
    private bool _portalExited;
    private bool _deathEdge;
    private Watchdog? _watchdog;
    private string _status = "Meta disabled.";

    public MetaEngine(
        IPluginHost host,
        MossTankExpressionRuntime expressions,
        MetaProfile profile,
        MetaServices? services = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _expressions = expressions ?? throw new ArgumentNullException(nameof(expressions));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _services = services ?? new MetaServices();
        _wasPortalSpace = host.Automation.Navigation.Snapshot.IsPortalSpace;
        _wasDead = IsDead();
    }

    public bool Enabled { get; private set; }
    public string CurrentState { get; private set; } = DefaultState;
    public string Status => _status;
    public int CallDepth => _callStack.Count;
    public int FiredRuleCount => _fired.Count;
    public IReadOnlyCollection<string> States => _profile.Rules
        .Select(static rule => NormalizeState(rule.State))
        .Append(DefaultState)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return;
        Enabled = enabled;
        if (enabled)
        {
            _stateSeconds = 0d;
            _decisionAccumulator = DecisionIntervalSeconds;
            _status = $"Meta running: {CurrentState}.";
        }
        else
        {
            _status = "Meta disabled.";
            _watchdog = null;
        }
    }

    public void ResetSession()
    {
        Enabled = false;
        CurrentState = DefaultState;
        _fired.Clear();
        _callStack.Clear();
        _chatBatch.Clear();
        _decisionAccumulator = 0d;
        _stateSeconds = 0d;
        _persistentStateSeconds = 0d;
        _chatSequence = 0u;
        _wasPortalSpace = _host.Automation.Navigation.Snapshot.IsPortalSpace;
        _wasDead = IsDead();
        _portalEntered = false;
        _portalExited = false;
        _deathEdge = false;
        _watchdog = null;
        _status = "Meta disabled.";
    }

    public void ReplaceProfile(MetaProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Transition(DefaultState);
    }

    public void Transition(string state)
    {
        CurrentState = NormalizeState(state);
        _fired.Clear();
        _stateSeconds = 0d;
        _persistentStateSeconds = 0d;
        _watchdog = null;
        _status = $"Meta transitioned to {CurrentState}.";
    }

    public void OnTick(double elapsedSeconds)
    {
        if (elapsedSeconds < 0d || !double.IsFinite(elapsedSeconds))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        _expressions.OnTick(elapsedSeconds);
        CaptureEdgesAndChat();
        if (!Enabled)
            return;

        _stateSeconds += elapsedSeconds;
        _persistentStateSeconds += elapsedSeconds;
        _decisionAccumulator += elapsedSeconds;
        UpdateWatchdog(elapsedSeconds);
        if (_decisionAccumulator < DecisionIntervalSeconds)
            return;
        _decisionAccumulator %= DecisionIntervalSeconds;
        EvaluatePass();
        _portalEntered = false;
        _portalExited = false;
        _deathEdge = false;
        _chatBatch.Clear();
    }

    public void EvaluatePass()
    {
        if (!Enabled)
            return;
        if (WatchdogExpired())
        {
            if (_callStack.Count >= MaximumCallDepth)
            {
                DisableWithError("Meta Error: Call stack overflow (watchdog loop?).");
                return;
            }
            string target = _watchdog!.Value.State;
            _callStack.Push(CurrentState);
            Transition(target);
            _status = $"Meta watchdog expired; calling {target}.";
            return;
        }

        MetaRule[] rules = _profile.Rules.Where(rule =>
            rule.Enabled
            && NormalizeState(rule.State).Equals(
                CurrentState,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (MetaRule rule in rules)
        {
            if (_fired.Contains(rule.Id) || !EvaluateCondition(rule.Condition))
                continue;
            _fired.Add(rule.Id);
            _status = $"Meta executing {Describe(rule.Action)}.";
            bool continuePass;
            try
            {
                continuePass = ExecuteAction(rule.Action);
            }
            catch (Exception error)
            {
                _status = $"Meta action failed: {error.Message}";
                _host.Log.Error(_status, error);
                continuePass = false;
            }
            if (!continuePass)
                break;
        }
    }

    internal void TriggerFakeDeath()
    {
        _deathEdge = true;
        if (Enabled)
            EvaluatePass();
        _deathEdge = false;
    }

    private bool EvaluateCondition(MetaCondition condition) => condition.Kind switch
    {
        MetaConditionKind.Never => false,
        MetaConditionKind.Always => true,
        MetaConditionKind.All => condition.Children.All(EvaluateCondition),
        MetaConditionKind.Any => condition.Children.Any(EvaluateCondition),
        MetaConditionKind.Not => condition.Children.Count != 0
            && !EvaluateCondition(condition.Children[0]),
        MetaConditionKind.ChatMessage => ChatMatch(condition, capture: false),
        MetaConditionKind.ChatMessageCapture => ChatMatch(condition, capture: true),
        MetaConditionKind.PackSlotsLessThanOrEqual =>
            EvaluateNumber("getfreeitemslots[]") <= condition.Number,
        MetaConditionKind.SecondsInStateGreaterThanOrEqual =>
            _stateSeconds >= condition.Number,
        MetaConditionKind.PersistentSecondsInStateGreaterThanOrEqual =>
            _persistentStateSeconds >= condition.Number,
        MetaConditionKind.NavigationRouteEmpty => _services.IsNavigationRouteEmpty(),
        MetaConditionKind.CharacterDeath => _deathEdge,
        MetaConditionKind.AnyVendorOpen => hostItems().ActiveVendorObjectId != 0u,
        MetaConditionKind.VendorClosed => hostItems().ActiveVendorObjectId == 0u,
        MetaConditionKind.InventoryItemCountLessThanOrEqual =>
            InventoryCount(condition.Text) <= condition.Number,
        MetaConditionKind.InventoryItemCountGreaterThanOrEqual =>
            InventoryCount(condition.Text) >= condition.Number,
        MetaConditionKind.MonsterNameCountWithinDistance =>
            MonsterCount(condition.Text, condition.SecondaryNumber) >= condition.Number,
        MetaConditionKind.MonsterPriorityCountWithinDistance =>
            _services.CountMonstersByPriority(
                checked((int)condition.TertiaryNumber),
                condition.SecondaryNumber) >= condition.Number,
        MetaConditionKind.NeedToBuff => _services.NeedsBuff(),
        MetaConditionKind.NoMonstersWithinDistance =>
            _host.Automation.Combat.CaptureHostileTargets(
                checked((float)condition.Number)).Count == 0,
        MetaConditionKind.LandblockEquals =>
            (_host.Automation.Navigation.Snapshot.Position.CellId & 0xFFFF0000u)
                == unchecked((uint)checked((int)condition.Number)),
        MetaConditionKind.LandcellEquals =>
            _host.Automation.Navigation.Snapshot.Position.CellId
                == unchecked((uint)checked((int)condition.Number)),
        MetaConditionKind.PortalspaceEntered => _portalEntered,
        MetaConditionKind.PortalspaceExited => _portalExited,
        MetaConditionKind.TimeLeftOnSpellGreaterThanOrEqual =>
            SpellTimeLeft(condition) >= condition.SecondaryNumber,
        MetaConditionKind.BurdenPercentGreaterThanOrEqual =>
            EvaluateNumber("getcharburden[]") >= condition.Number,
        MetaConditionKind.DistanceFromAnyRoutePointGreaterThanOrEqual =>
            _services.DistanceFromAnyRoutePoint() >= condition.Number,
        MetaConditionKind.Expression =>
            _expressions.Evaluate(condition.Text).IsTruthy,
        _ => false,
    };

    private bool ExecuteAction(MetaAction action)
    {
        switch (action.Kind)
        {
            case MetaActionKind.None:
                return true;
            case MetaActionKind.SetMetaState:
                Transition(action.Text);
                return false;
            case MetaActionKind.ChatCommand:
                _host.Automation.Chat.Submit(action.Text);
                return true;
            case MetaActionKind.All:
                foreach (MetaAction child in action.Children)
                {
                    if (!ExecuteAction(child))
                        return false;
                }
                return true;
            case MetaActionKind.LoadEmbeddedNavigationRoute:
                _services.LoadEmbeddedNavigationRoute(action.EmbeddedRoute);
                return true;
            case MetaActionKind.CallMetaState:
                if (_callStack.Count >= MaximumCallDepth)
                {
                    DisableWithError("Meta Error: Call stack overflow (recursive call loop?).");
                    return false;
                }
                _callStack.Push(string.IsNullOrWhiteSpace(action.SecondaryText)
                    ? CurrentState
                    : NormalizeState(action.SecondaryText));
                Transition(action.Text);
                return false;
            case MetaActionKind.ReturnFromCall:
                if (_callStack.Count == 0)
                {
                    DisableWithError("Meta Error: Call stack underflow, cannot return.");
                    return false;
                }
                Transition(_callStack.Pop());
                return false;
            case MetaActionKind.ExpressionAction:
                _expressions.Evaluate(action.Text);
                return true;
            case MetaActionKind.ChatExpression:
                ExpressionValue result = _expressions.Evaluate(action.Text);
                if (result.ToDisplayString().Length != 0)
                    _host.Automation.Chat.Submit(result.ToDisplayString());
                return true;
            case MetaActionKind.SetWatchdog:
                SetWatchdog(
                    action.Text,
                    action.Number <= 0d ? 5d : action.Number,
                    action.SecondaryNumber <= 0d ? 10d : action.SecondaryNumber);
                return true;
            case MetaActionKind.ClearWatchdog:
                _watchdog = null;
                return true;
            case MetaActionKind.GetVtankOption:
                _expressions.State.Set(
                    ExpressionVariableScope.Session,
                    string.IsNullOrWhiteSpace(action.SecondaryText)
                        ? "option"
                        : action.SecondaryText,
                    _services.GetOption(action.Text));
                return true;
            case MetaActionKind.SetVtankOption:
                return _services.SetOption(
                    action.Text,
                    _expressions.Evaluate(action.SecondaryText));
            case MetaActionKind.CreateView:
                return _services.CreateView(action.Text, action.SecondaryText);
            case MetaActionKind.DestroyView:
                return _services.DestroyView(action.Text);
            case MetaActionKind.DestroyAllViews:
                _services.DestroyAllViews();
                return true;
            default:
                return false;
        }
    }

    private void CaptureEdgesAndChat()
    {
        bool portal = _host.Automation.Navigation.Snapshot.IsPortalSpace;
        _portalEntered |= !_wasPortalSpace && portal;
        _portalExited |= _wasPortalSpace && !portal;
        _wasPortalSpace = portal;
        bool dead = IsDead();
        _deathEdge |= !_wasDead && dead;
        _wasDead = dead;

        IReadOnlyList<PluginChatMessage> messages =
            _host.Automation.Chat.CaptureMessages(_chatSequence);
        foreach (PluginChatMessage message in messages)
        {
            _chatBatch.Add(message);
            _chatSequence = Math.Max(_chatSequence, message.Sequence);
        }
    }

    private bool ChatMatch(MetaCondition condition, bool capture)
    {
        Regex regex;
        try
        {
            regex = new Regex(
                condition.Text,
                RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        HashSet<int>? acceptedKinds = ParseKinds(condition.SecondaryText);
        foreach (PluginChatMessage message in _chatBatch)
        {
            if (acceptedKinds is not null && !acceptedKinds.Contains(message.Kind))
                continue;
            Match match = regex.Match(message.Text);
            if (!match.Success)
                continue;
            if (capture)
            {
                foreach (string name in regex.GetGroupNames())
                {
                    Group group = match.Groups[name];
                    string variable = "capturegroup_" + name;
                    if (group.Success)
                    {
                        _expressions.State.Set(
                            ExpressionVariableScope.Session,
                            variable,
                            ExpressionValue.String(group.Value));
                    }
                    else
                    {
                        _expressions.State.Clear(
                            ExpressionVariableScope.Session,
                            variable);
                    }
                }
                _expressions.State.Set(
                    ExpressionVariableScope.Session,
                    "capturecolor",
                    ExpressionValue.Number(message.Kind));
            }
            return true;
        }
        return false;
    }

    private static HashSet<int>? ParseKinds(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        var result = new HashSet<int>();
        foreach (string part in source.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out int kind))
                return [];
            result.Add(kind);
        }
        return result;
    }

    private double InventoryCount(string name)
    {
        string escaped = name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return EvaluateNumber($"getitemcountininventorybyname['{escaped}']");
    }

    private int MonsterCount(string pattern, double distance)
    {
        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException)
        {
            return 0;
        }
        return _host.Automation.Combat.CaptureHostileTargets(
            checked((float)distance)).Count(target => regex.IsMatch(target.Name));
    }

    private double SpellTimeLeft(MetaCondition condition)
    {
        uint spellId = condition.Number > 0d
            ? checked((uint)condition.Number)
            : _host.Automation.Spells.KnownSelfBuffs
                .Concat(_host.Automation.Spells.KnownCombatSpells)
                .FirstOrDefault(spell => spell.Name.Equals(
                    condition.Text,
                    StringComparison.OrdinalIgnoreCase)).SpellId;
        foreach (PluginActiveEnchantment enchantment in
            _host.Automation.Character.ActiveEnchantments)
        {
            if (enchantment.SpellId == spellId)
                return enchantment.SecondsRemaining;
        }
        return 0d;
    }

    private double EvaluateNumber(string source) =>
        _expressions.Evaluate(source).AsNumber(source);

    private IItemAutomation hostItems() => _host.Automation.Items;

    private bool IsDead()
    {
        ICharacterInfo character = _host.Automation.Character;
        return character.IsInWorld
            && character.MaxHealth > 0u
            && character.CurrentHealth == 0u;
    }

    private void SetWatchdog(string state, double rangeMeters, double seconds)
    {
        PluginNavigationPosition position =
            _host.Automation.Navigation.Snapshot.Position;
        _watchdog = new Watchdog(
            NormalizeState(state),
            Math.Max(0d, rangeMeters),
            Math.Max(0.001d, seconds),
            0d,
            0d,
            Enumerable.Repeat(position, 10).ToArray());
    }

    private void UpdateWatchdog(double elapsedSeconds)
    {
        if (_watchdog is not Watchdog watchdog)
            return;
        watchdog = watchdog with
        {
            TotalSeconds = watchdog.TotalSeconds + elapsedSeconds,
            SampleSeconds = watchdog.SampleSeconds + elapsedSeconds,
        };
        double interval = watchdog.TimeSpanSeconds / 10d;
        if (watchdog.SampleSeconds >= interval)
        {
            int index = ((int)Math.Floor(watchdog.TotalSeconds / interval)) % 10;
            watchdog.Samples[index] = _host.Automation.Navigation.Snapshot.Position;
            watchdog = watchdog with { SampleSeconds = watchdog.SampleSeconds % interval };
        }
        _watchdog = watchdog;
    }

    private bool WatchdogExpired()
    {
        if (_watchdog is not Watchdog watchdog
            || watchdog.TotalSeconds < watchdog.TimeSpanSeconds)
        {
            return false;
        }
        PluginNavigationPosition current =
            _host.Automation.Navigation.Snapshot.Position;
        return watchdog.Samples.All(sample =>
            sample.HorizontalDistanceMeters(current) <= watchdog.RangeMeters);
    }

    private void DisableWithError(string message)
    {
        Enabled = false;
        _status = message + " Meta disabled.";
        _host.Automation.Chat.PostSystemMessage(_status);
        _host.Log.Error(_status);
    }

    private static string NormalizeState(string? state) =>
        string.IsNullOrWhiteSpace(state) ? DefaultState : state.Trim();

    private static string Describe(MetaAction action) => action.Kind switch
    {
        MetaActionKind.SetMetaState => $"Set Meta State {action.Text}",
        MetaActionKind.CallMetaState => $"Call Meta State {action.Text}",
        MetaActionKind.ChatCommand => $"Chat {action.Text}",
        _ => action.Kind.ToString(),
    };

    private readonly record struct Watchdog(
        string State,
        double RangeMeters,
        double TimeSpanSeconds,
        double TotalSeconds,
        double SampleSeconds,
        PluginNavigationPosition[] Samples);
}
