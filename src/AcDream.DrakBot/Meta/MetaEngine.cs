using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// What the meta engine needs from the rest of the bot: the route to swap
/// on EmbedNav, the buff behavior's opinion for NeedToBuff, and the
/// profile toggles a <c>/vt opt set</c> reaches.
/// </summary>
public interface IMetaBot
{
    Route? CurrentRoute { get; }
    int RouteIndex { get; }
    void SetRoute(Route? route, bool enableNavigation);
    bool NeedsAnyBuff(Blackboard board);
    Dictionary<string, (Func<string> Get, Action<string> Set)> OptionMap();
    /// <summary>Loads a meta by name from the VTank profiles folder; null when there is none.</summary>
    LoadedMeta? LoadMetaByName(string name, out string path);
    Route? LoadRouteByName(string name);
    /// <summary>A <c>/vt</c> or <c>/drakbot</c> verb the bot handles itself; true when it did.</summary>
    bool TryHandleCommand(string command);
}

/// <summary>
/// The VTank-style meta: a state machine of rules, evaluated every tick in
/// the current state, first match wins, each rule firing once per visit to
/// its state. Ported from RynthSuite's MetaManager: the same conditions,
/// actions, call stack, watchdog and <c>/vt</c> command translation, over
/// the bot's blackboard and <see cref="MetaWorld"/> instead of the client's
/// memory. Runs beside the behaviors rather than as one of them - it steers
/// the bot's options, route and its own state; the behaviors do the work.
/// </summary>
public sealed class MetaEngine
{
    private readonly MetaWorld _world;
    private readonly IMetaBot _bot;
    private readonly IBotClock _clock;
    private readonly IPluginLogger _log;
    private readonly MetaSettings _settings;
    private readonly Dictionary<string, List<MetaRule>> _stateIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<MetaRule> NoRules = new();
    private readonly Stack<string> _stateStack = new();
    private readonly List<(double At, string Text)> _recentChat = new();
    private ExpressionEngine? _expressions;
    private List<MetaRule> _rules = new();
    private int _indexedCount = -1;
    private string _lastState = string.Empty;
    private double _stateStartedAt;
    private bool _wasRunning;
    private Match? _lastChatMatch;
    private ulong _chatSequence;
    private bool _lastPortalSpace;
    private bool _portalEntered;
    private bool _portalExited;
    private bool _watchdogActive;
    private string _watchdogState = string.Empty;
    private double _watchdogMeters;
    private double _watchdogSeconds;
    private double _watchdogExpiresAt;
    private PluginNavigationPosition _watchdogPosition;
    private bool _viewsWarned;

    private readonly Func<MetaOptions> _options;
    private readonly Action<Func<MetaOptions, MetaOptions>> _updateOptions;

    public MetaEngine(
        MetaWorld world,
        IMetaBot bot,
        IBotClock clock,
        IPluginLogger log,
        Func<MetaOptions> options,
        Action<Func<MetaOptions, MetaOptions>> updateOptions)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _updateOptions = updateOptions ?? throw new ArgumentNullException(nameof(updateOptions));
        _settings = new MetaSettings(bot.OptionMap);
        if (MetaSchema.DriftError is not null)
            _log.Error("meta schema drift: " + MetaSchema.DriftError, null);
    }

    /// <summary>The profile's meta switch; the rules only fire while it is on.</summary>
    public bool Enabled
    {
        get => _options().Enabled;
        set => _updateOptions(o => o with { Enabled = value });
    }

    public bool Debug
    {
        get => _options().Debug;
        set => _updateOptions(o => o with { Debug = value });
    }

    public string CurrentState => _settings.CurrentState;

    public IReadOnlyList<MetaRule> Rules => _rules;

    public string MetaName { get; private set; } = string.Empty;

    public Dictionary<string, List<string>> EmbeddedNavs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double SecondsInState => _clock.Now - _stateStartedAt;

    public int StackDepth => _stateStack.Count;

    public bool WatchdogActive => _watchdogActive;

    public string LastFired { get; private set; } = string.Empty;

    public double LastFiredAt { get; private set; } = double.NegativeInfinity;

    public string LastError { get; private set; } = string.Empty;

    public ExpressionEngine Expressions => _expressions ??= CreateExpressions();

    private ExpressionEngine CreateExpressions()
    {
        var engine = new ExpressionEngine(_world);
        engine.SetPlayerId(_world.GetPlayerId());
        engine.SetSettings(_settings);
        return engine;
    }

    // ── Loading ──────────────────────────────────────────────────────────

    public void Load(LoadedMeta meta, string name)
    {
        ArgumentNullException.ThrowIfNull(meta);
        _rules = meta.Rules;
        _indexedCount = -1;
        EmbeddedNavs.Clear();
        foreach (KeyValuePair<string, List<string>> nav in meta.EmbeddedNavs)
            EmbeddedNavs[nav.Key] = nav.Value;
        MetaName = name;
        _stateStack.Clear();
        _watchdogActive = false;
        SetState(_rules.Count > 0 ? _rules[0].State : "Default");
        foreach (string warning in meta.Warnings)
            _log.Warn($"meta '{name}': {warning}");
    }

    public void Clear()
    {
        _rules = new List<MetaRule>();
        _indexedCount = -1;
        EmbeddedNavs.Clear();
        MetaName = string.Empty;
        _stateStack.Clear();
        _watchdogActive = false;
        SetState("Default");
    }

    public void SetState(string state)
    {
        _settings.CurrentState = state;
        _settings.ForceStateReset = true;
    }

    /// <summary>The set of state names the loaded rules mention.</summary>
    public IReadOnlyList<string> StateNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MetaRule rule in _rules)
            names.Add(rule.State);
        return names.ToArray();
    }

    // ── Tick ─────────────────────────────────────────────────────────────

    /// <summary>One engine tick. Chat is drained every tick so the quest cache and chat conditions stay current.</summary>
    public void Think(Blackboard board, bool botRunning)
    {
        DrainChat();
        _world.Tick();
        Utility?.Tick(board.Now);
        _expressions?.PumpDelayedExecs();
        _expressions?.FlushVars();

        try
        {
            if (!botRunning || !Enabled || _rules.Count == 0)
            {
                if (_wasRunning && !botRunning)
                    _expressions?.FlushVars(force: true);
                _wasRunning = botRunning;
                return;
            }
            // A fresh start re-arms every rule, so an Always rule that
            // latched before the stop fires again.
            if (!_wasRunning)
                _settings.ForceStateReset = true;
            _wasRunning = true;

            if (_settings.ForceStateReset)
            {
                _stateStartedAt = board.Now;
                _watchdogActive = false;
                _settings.ForceStateReset = false;
                foreach (MetaRule rule in _rules)
                    rule.HasFired = false;
            }
            _lastState = _settings.CurrentState;
            double secondsInState = board.Now - _stateStartedAt;

            bool portalSpace = board.Navigation.IsPortalSpace;
            _portalEntered = portalSpace && !_lastPortalSpace;
            _portalExited = !portalSpace && _lastPortalSpace;
            _lastPortalSpace = portalSpace;

            if (_watchdogActive && board.Navigation.IsAvailable)
            {
                double moved = board.Navigation.Position.HorizontalDistanceMeters(_watchdogPosition);
                if (moved < _watchdogMeters)
                {
                    if (board.Now > _watchdogExpiresAt)
                    {
                        _watchdogActive = false;
                        _world.WriteToChat($"[DrakBot] watchdog: no progress, state -> {_watchdogState}", 1);
                        SetState(_watchdogState);
                        return;
                    }
                }
                else
                {
                    _watchdogPosition = board.Navigation.Position;
                    _watchdogExpiresAt = board.Now + _watchdogSeconds;
                }
            }

            if (_indexedCount != _rules.Count)
                RebuildStateIndex();
            _stateIndex.TryGetValue(_settings.CurrentState, out List<MetaRule>? bucket);
            foreach (MetaRule rule in bucket ?? NoRules)
            {
                if (rule.HasFired)
                    continue;
                // Capture groups are scoped to the rule that captured them.
                _lastChatMatch = null;
                if (!EvaluateCondition(rule, board, secondsInState))
                    continue;
                rule.HasFired = true;
                rule.LastFiredAt = board.Now;
                LastFired = $"{rule.State}: {Describe(rule)}";
                LastFiredAt = board.Now;
                if (Debug)
                    _world.WriteToChat($"[Meta] {LastFired}", 1);
                ExecuteAction(rule);
                if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
                    break;
            }
        }
        catch (Exception error)
        {
            // Localise a throwing condition or action to this tick; the
            // reset re-arms the rules cleanly next tick.
            LastError = $"{_settings.CurrentState}: {error.GetType().Name}: {Truncate(error.Message, 100)}";
            _log.Error($"meta: rule evaluation failed in '{_settings.CurrentState}'", error);
            _settings.ForceStateReset = true;
        }
    }

    private void DrainChat()
    {
        IReadOnlyList<PluginChatMessage> messages = _world.Surface.Chat.CaptureMessages(_chatSequence);
        double now = _clock.Now;
        foreach (PluginChatMessage message in messages)
        {
            if (message.Sequence > _chatSequence)
                _chatSequence = message.Sequence;
            _recentChat.Add((now, message.Text));
            _world.OnChatLine(message.Text);
        }
        // Chat conditions look at the last second of lines.
        _recentChat.RemoveAll(entry => now - entry.At > 1d);
    }

    // ── Conditions ───────────────────────────────────────────────────────

    private bool EvaluateCondition(MetaRule rule, Blackboard board, double secondsInState)
    {
        switch (rule.Condition)
        {
            case MetaConditionType.Always: return true;
            case MetaConditionType.Never: return false;

            case MetaConditionType.All:
                if (rule.Children.Count == 0) return true;
                foreach (MetaRule child in rule.Children)
                    if (!EvaluateCondition(child, board, secondsInState)) return false;
                return true;

            case MetaConditionType.Any:
                if (rule.Children.Count == 0) return true;
                foreach (MetaRule child in rule.Children)
                    if (EvaluateCondition(child, board, secondsInState)) return true;
                return false;

            case MetaConditionType.Not:
                return rule.Children.Count > 0 && !EvaluateCondition(rule.Children[0], board, secondsInState);

            case MetaConditionType.MainHealthLE:
                return Int(rule.ConditionData, out int health) && board.Vitals.Health <= health;
            case MetaConditionType.MainManaLE:
                return Int(rule.ConditionData, out int mana) && board.Vitals.Mana <= mana;
            case MetaConditionType.MainStamLE:
                return Int(rule.ConditionData, out int stamina) && board.Vitals.Stamina <= stamina;
            case MetaConditionType.MainHealthPHE:
                return Int(rule.ConditionData, out int healthPercent) && board.Vitals.MaxHealth > 0
                    && board.Vitals.Health * 100d / board.Vitals.MaxHealth <= healthPercent;
            case MetaConditionType.MainManaPHE:
                return Int(rule.ConditionData, out int manaPercent) && board.Vitals.MaxMana > 0
                    && board.Vitals.Mana * 100d / board.Vitals.MaxMana <= manaPercent;
            case MetaConditionType.CharacterDeath:
                return board.IsInWorld && board.Vitals.Health == 0;
            case MetaConditionType.VitaePHE:
                return false; // vitae is not carried by the contract

            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
                return Double(rule.ConditionData, out double seconds) && secondsInState >= seconds;

            case MetaConditionType.ChatMessage:
            case MetaConditionType.ChatMessageCapture:
            {
                if (rule.ConditionData.Length == 0) return false;
                foreach ((double _, string text) in _recentChat)
                {
                    Match match = RegexCache.Match(text, rule.ConditionData);
                    if (!match.Success)
                        continue;
                    if (rule.Condition == MetaConditionType.ChatMessageCapture)
                        _lastChatMatch = match;
                    return true;
                }
                return false;
            }

            case MetaConditionType.PackSlots_LE:
                return Int(rule.ConditionData, out int slots) && _world.Surface.Character.MainPackFreeSlots <= slots;

            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                string[] parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 || !Int(parts[1], out int wanted)) return false;
                string itemName = parts[0].Trim();
                int count = 0;
                foreach (MetaObject item in _world.GetInventory())
                    if (item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                        count += Math.Max(1, item.StackCount);
                return rule.Condition == MetaConditionType.InventoryItemCount_LE ? count <= wanted : count >= wanted;
            }

            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                string[] parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 || !uint.TryParse(parts[0].Trim(), out uint spellId) || !Double(parts[1], out double wanted))
                    return false;
                double remaining = 0d;
                foreach (PluginActiveEnchantment enchantment in board.Enchantments)
                {
                    if (enchantment.SpellId != spellId)
                        continue;
                    remaining = enchantment.SecondsRemaining;
                    break;
                }
                return rule.Condition == MetaConditionType.TimeLeftOnSpell_GE ? remaining >= wanted : remaining <= wanted;
            }

            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                string[] parts = rule.ConditionData.Split(',');
                if (parts.Length < 3 || !Double(parts[1], out double maxDistance) || !Int(parts[2], out int minCount))
                    return false;
                Regex? pattern = RegexCache.Get(parts[0].Trim(), RegexOptions.IgnoreCase);
                if (pattern is null) return false;
                int matches = 0;
                foreach (PluginCombatTarget hostile in board.Hostiles)
                {
                    if (hostile.IsHealthKnown && hostile.HealthFraction <= 0f) continue;
                    if (hostile.Distance <= maxDistance && pattern.IsMatch(hostile.Name))
                        matches++;
                }
                return matches >= minCount;
            }

            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                // The bot keeps no monster rules; every hostile counts.
                string[] parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 || !Int(parts[0], out int minCount) || !Double(parts[1], out double maxDistance))
                    return false;
                int matches = 0;
                foreach (PluginCombatTarget hostile in board.Hostiles)
                    if (!(hostile.IsHealthKnown && hostile.HealthFraction <= 0f) && hostile.Distance <= maxDistance)
                        matches++;
                return matches >= minCount;
            }

            case MetaConditionType.NoMonstersWithinDistance:
            {
                double maxDistance = Double(rule.ConditionData, out double parsed) && parsed > 0d ? parsed : 20d;
                foreach (PluginCombatTarget hostile in board.Hostiles)
                    if (!(hostile.IsHealthKnown && hostile.HealthFraction <= 0f) && hostile.Distance <= maxDistance)
                        return false;
                return true;
            }

            case MetaConditionType.NavrouteEmpty:
            {
                Route? route = _bot.CurrentRoute;
                return route is null || route.IsEmpty || _bot.RouteIndex < 0 || _bot.RouteIndex >= route.Waypoints.Count;
            }

            case MetaConditionType.Landblock_EQ:
                return board.Navigation.IsAvailable && Hex(rule.ConditionData, out uint landblock)
                    && (board.Navigation.Position.CellId >> 16) == landblock;
            case MetaConditionType.Landcell_EQ:
                return board.Navigation.IsAvailable && Hex(rule.ConditionData, out uint cell)
                    && board.Navigation.Position.CellId == cell;

            case MetaConditionType.PortalspaceEntered: return _portalEntered;
            case MetaConditionType.PortalspaceExited: return _portalExited;

            case MetaConditionType.AnyVendorOpen:
            case MetaConditionType.VendorClosed:
                return false; // vendor windows are not carried by the contract

            case MetaConditionType.Expression:
            {
                if (rule.ConditionData.Length == 0) return false;
                string value = Expressions.Evaluate(rule.ConditionData);
                // Fail closed: an erroring expression must not fire a rule.
                if (value.StartsWith("ERR:", StringComparison.Ordinal))
                {
                    LastError = $"{rule.State}: {Truncate(value, 120)}";
                    if (Debug)
                        _world.WriteToChat($"[Meta] expression error in '{rule.State}': {Truncate(value, 80)}", 1);
                    return false;
                }
                return ExpressionEngine.ToBool(value);
            }

            case MetaConditionType.BurdenPercentage_GE:
            {
                if (!Int(rule.ConditionData, out int threshold)) return false;
                uint player = _world.GetPlayerId();
                if (!_world.TryGetObjectIntProperty(player, 5u, out int burden)
                    || !_world.TryGetObjectIntProperty(player, 96u, out int capacity) || capacity <= 0)
                    return false;
                return burden * 100 / capacity >= threshold;
            }

            case MetaConditionType.DistAnyRoutePT_GE:
            {
                if (!Double(rule.ConditionData, out double threshold) || !board.Navigation.IsAvailable) return false;
                Route? route = _bot.CurrentRoute;
                if (route is null || route.IsEmpty) return false;
                double nearest = double.PositiveInfinity;
                foreach (Waypoint waypoint in route.Waypoints)
                {
                    if (waypoint.Kind != WaypointKind.Point) continue;
                    // VTank measures this one in map units, not meters.
                    double east = waypoint.EastWest - board.Navigation.Position.EastWest;
                    double north = waypoint.NorthSouth - board.Navigation.Position.NorthSouth;
                    nearest = Math.Min(nearest, Math.Sqrt(east * east + north * north));
                }
                return !double.IsPositiveInfinity(nearest) && nearest >= threshold;
            }

            case MetaConditionType.NeedToBuff:
                return _bot.NeedsAnyBuff(board);

            default:
                return false;
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────

    private void ExecuteAction(MetaRule rule)
    {
        string Data(string raw)
        {
            if (raw.Length == 0 || _lastChatMatch is not { Success: true }) return raw;
            string result = raw;
            for (int group = 0; group < _lastChatMatch.Groups.Count; group++)
                result = result.Replace("{" + group + "}", _lastChatMatch.Groups[group].Value, StringComparison.Ordinal);
            return result;
        }

        switch (rule.Action)
        {
            case MetaActionType.All:
                foreach (MetaRule child in rule.ActionChildren.Count > 0 ? rule.ActionChildren : rule.Children)
                    ExecuteAction(child);
                break;

            case MetaActionType.ChatCommand:
                SendCommand(Data(rule.ActionData));
                break;

            case MetaActionType.SetMetaState:
            {
                string next = Data(rule.ActionData);
                if (next.Length > 0) SetState(next);
                break;
            }

            case MetaActionType.CallMetaState:
            {
                string next = Data(rule.ActionData);
                if (next.Length > 0)
                {
                    _stateStack.Push(_settings.CurrentState);
                    SetState(next);
                }
                break;
            }

            case MetaActionType.ReturnFromCall:
                if (_stateStack.Count > 0) SetState(_stateStack.Pop());
                break;

            case MetaActionType.EmbeddedNavRoute:
                EmbedNav(rule.ActionData);
                break;

            case MetaActionType.SetWatchdog:
            {
                if (_watchdogActive) break;
                string[] parts = rule.ActionData.Split(';');
                if (parts.Length < 3) break;
                _watchdogState = parts[0].Trim();
                _watchdogMeters = Double(parts[1], out double meters) ? meters : 5d;
                _watchdogSeconds = Double(parts[2], out double seconds) ? seconds : 5d;
                _watchdogExpiresAt = _clock.Now + _watchdogSeconds;
                _watchdogPosition = _world.Surface.Navigation.Snapshot.Position;
                _watchdogActive = true;
                break;
            }

            case MetaActionType.ClearWatchdog:
                _watchdogActive = false;
                break;

            case MetaActionType.SetRAOption:
            {
                string[] parts = Data(rule.ActionData).Split(';');
                if (parts.Length >= 2)
                    SetOption(parts[0].Trim(), parts[1].Trim());
                break;
            }

            case MetaActionType.GetRAOption:
            {
                string[] parts = Data(rule.ActionData).Split(';');
                if (parts.Length >= 2)
                    Expressions.SetVariable(parts[0].Trim(), GetOption(parts[1].Trim()));
                break;
            }

            case MetaActionType.ChatExpression:
            {
                string source = Data(rule.ActionData);
                if (source.Length == 0) break;
                string message = Expressions.Evaluate(source);
                if (message.Length > 0)
                    SendCommand(message);
                break;
            }

            case MetaActionType.ExpressionAction:
            {
                string source = Data(rule.ActionData);
                if (source.Length > 0 && !Expressions.TryExecuteAction(source))
                    _world.WriteToChat($"[DrakBot] unknown meta action: {source}", 1);
                break;
            }

            case MetaActionType.CreateView:
            case MetaActionType.DestroyView:
            case MetaActionType.DestroyAllViews:
                if (!_viewsWarned)
                {
                    _viewsWarned = true;
                    LastError = $"{rule.Action} ignored: VTank views are not supported";
                    _world.WriteToChat("[DrakBot] this meta uses VTank views (CreateView/DestroyView); those actions are ignored.", 1);
                }
                break;
        }
    }

    /// <summary>The MagTools / UtilityBelt verbs (<c>/mt</c>, <c>/ub</c>), when the plugin wired them.</summary>
    public UtilityCommands? Utility { get; set; }

    /// <summary>A chat line from a rule: <c>/vt</c> is translated, the bot's own verbs handled, anything else submitted.</summary>
    public void SendCommand(string command)
    {
        if (command.Length == 0)
            return;
        if (TryHandleVtCommand(command) || _bot.TryHandleCommand(command) || Utility?.TryHandle(command) == true)
            return;
        _world.InvokeChatParser(command);
    }

    /// <summary>An option by its VTank or bot name, for <c>/mt opt get</c>.</summary>
    public string GetOptionValue(string name)
    {
        string mapped = VtOptionMap.TryGetValue(name, out string? target) ? target : name;
        if (_settings.BuildMap().TryGetValue(mapped, out (Func<string> Get, Action<string> Set) entry))
            return entry.Get();
        return Expressions.GetOption(name);
    }

    public void SetOptionValue(string name, string value) => SetOption(name, value);

    private void EmbedNav(string actionData)
    {
        if (string.IsNullOrWhiteSpace(actionData))
            return;
        string routeName = actionData.Split(';')[0];
        if (!EmbeddedNavs.ContainsKey(routeName))
        {
            // Legacy names look like "nav0__MatronHive1_nav".
            string normalized = Regex.Replace(routeName, @"^nav\d+_+", string.Empty);
            if (normalized.EndsWith("_nav", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^4];
            normalized = normalized.Replace('_', ' ').Trim();
            foreach (string key in EmbeddedNavs.Keys)
            {
                if (key.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    || key.Replace(".nav", string.Empty, StringComparison.OrdinalIgnoreCase).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    routeName = key;
                    break;
                }
            }
        }

        Route? route;
        if (EmbeddedNavs.TryGetValue(routeName, out List<string>? lines))
        {
            route = NavFile.Parse(routeName, lines, out string? warning);
            if (warning is not null)
                _log.Warn($"meta: embedded route '{routeName}': {warning}");
        }
        else
        {
            route = _bot.LoadRouteByName(routeName);
        }
        if (route is null)
        {
            _log.Warn($"meta: route '{routeName}' not found (embedded: {string.Join(", ", EmbeddedNavs.Keys)})");
            _world.WriteToChat($"[DrakBot] meta route missing: {routeName}", 1);
            return;
        }
        _bot.SetRoute(route, enableNavigation: true);
        _world.WriteToChat($"[DrakBot] route -> {routeName} ({route.Waypoints.Count} steps)", 1);
    }

    // ── /vt command translation ──────────────────────────────────────────

    private static readonly Dictionary<string, string> VtOptionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enablecombat"] = "EnableCombat",
        ["enablelooting"] = "EnableLooting",
        ["enablenav"] = "EnableNavigation",
        ["enablebuffing"] = "EnableBuffing",
        ["enablemeta"] = "EnableMeta",
        ["attackdistance"] = "MonsterRange",
        ["approachdistance"] = "ApproachRange",
        ["recharge-norm-hitp"] = "HealAt",
        ["recharge-norm-mana"] = "GetManaAt",
        ["recharge-norm-stam"] = "RestamAt",
        ["recharge-notarg-hitp"] = "TopOffHP",
        ["recharge-notarg-mana"] = "TopOffMana",
        ["recharge-notarg-stam"] = "TopOffStam",
        ["rebuftimeremainingseconds"] = "RebuffSecondsRemaining",
        ["idlepeacemode"] = "PeaceModeWhenIdle",
        ["summonpets"] = "SummonPets",
        ["opendoors"] = "OpenDoors",
        ["autocram"] = "EnableAutocram",
        ["autostack"] = "EnableAutostack",
        ["enablemanatapping"] = "EnableManaTapping",
        ["manatapminmana"] = "ManaTapMinMana",
        ["manastonelootcount"] = "ManaStoneKeepCount",
        ["dooropenrange"] = "OpenDoorRange",
        ["petmonsterdensity"] = "PetMinMonsters",
        ["petcustomrange"] = "CustomPetRange",
    };

    /// <summary>Translates a VTank <c>/vt</c> command into the bot's own terms. True when handled.</summary>
    public bool TryHandleVtCommand(string command)
    {
        if (!command.StartsWith("/vt ", StringComparison.OrdinalIgnoreCase) && !command.Equals("/vt", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;
        string sub = parts[1].ToLowerInvariant();

        if (sub == "opt" && parts.Length >= 5 && parts[2].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            SetOption(parts[3], parts[4]);
            return true;
        }
        if (sub == "meta" && parts.Length >= 4 && parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            LoadByName(string.Join(' ', parts, 3, parts.Length - 3));
            return true;
        }
        if (sub == "nav" && parts.Length >= 4 && parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(' ', parts, 3, parts.Length - 3);
            Route? route = _bot.LoadRouteByName(name);
            if (route is null)
                _world.WriteToChat($"[DrakBot] route not found: {name}", 1);
            else
                _bot.SetRoute(route, enableNavigation: true);
            return true;
        }
        if (sub == "setmetastate" && parts.Length >= 3)
        {
            SetState(string.Join(' ', parts, 2, parts.Length - 2));
            return true;
        }
        if (sub == "echo" && parts.Length >= 3)
        {
            _world.WriteToChat(string.Join(' ', parts, 2, parts.Length - 2), 1);
            return true;
        }
        if (sub == "reverseroute")
        {
            Route? route = _bot.CurrentRoute;
            if (route is not null && !route.IsEmpty)
                _bot.SetRoute(route with { Waypoints = route.Waypoints.Reverse().ToArray() }, enableNavigation: true);
            return true;
        }
        if (sub is "loot" or "lootprofile" && parts.Length >= 4 && parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            SetOption("CurrentLootPath", string.Join(' ', parts, 3, parts.Length - 3));
            return true;
        }
        if (sub is "settings" or "loot" or "lootprofile")
            return true; // no counterpart; swallowed rather than sent to the server
        // Anything else is handed to the bot's own verbs as /drakbot <verb>.
        return _bot.TryHandleCommand("/drakbot " + string.Join(' ', parts, 1, parts.Length - 1));
    }

    /// <summary>Loads <c>&lt;name&gt;.af</c> or <c>.met</c> from the VTank profiles folder.</summary>
    public bool LoadByName(string name)
    {
        LoadedMeta? meta = _bot.LoadMetaByName(name, out string path);
        if (meta is null)
        {
            _world.WriteToChat($"[DrakBot] meta not found: {name}", 1);
            return false;
        }
        Load(meta, name);
        _updateOptions(o => o with { Name = name });
        _world.WriteToChat(
            $"[DrakBot] meta '{name}' loaded: {meta.Rules.Count} rules, {meta.EmbeddedNavs.Count} routes"
            + (meta.Warnings.Count > 0 ? $"; {meta.Warnings.Count} warning(s): {meta.Warnings[0]}" : string.Empty),
            1);
        _log.Info($"meta: loaded {path}");
        return true;
    }

    private void SetOption(string name, string value)
    {
        string normalized = value.ToLowerInvariant() switch
        {
            "true" or "on" => "1",
            "false" or "off" => "0",
            _ => value,
        };
        string mapped = VtOptionMap.TryGetValue(name, out string? target) ? target : name;
        if (_settings.BuildMap().TryGetValue(mapped, out (Func<string> Get, Action<string> Set) entry))
        {
            entry.Set(normalized);
            return;
        }
        Expressions.SetOption(name, normalized);
    }

    private string GetOption(string name)
    {
        string mapped = VtOptionMap.TryGetValue(name, out string? target) ? target : name;
        return _settings.BuildMap().TryGetValue(mapped, out (Func<string> Get, Action<string> Set) entry)
            ? entry.Get()
            : Expressions.GetOption(name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void RebuildStateIndex()
    {
        _stateIndex.Clear();
        foreach (MetaRule rule in _rules)
        {
            if (!rule.Enabled) continue;
            if (!_stateIndex.TryGetValue(rule.State, out List<MetaRule>? bucket))
                _stateIndex[rule.State] = bucket = new List<MetaRule>();
            bucket.Add(rule);
        }
        _indexedCount = _rules.Count;
    }

    private static string Describe(MetaRule rule)
    {
        string condition = rule.Condition.ToString();
        if (rule.ConditionData.Length > 0)
            condition += $"({Truncate(rule.ConditionData, 40)})";
        string action = rule.Action.ToString();
        if (rule.ActionData.Length > 0)
            action += $"({Truncate(rule.ActionData, 60)})";
        return $"{condition} -> {action}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static bool Int(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool Double(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool Hex(string text, out uint value)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            || uint.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
