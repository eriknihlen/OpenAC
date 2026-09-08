using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum LootAction
{
    NoLoot,
    Keep,
    Salvage,
    Sell,
    Read,
    User1,
    User2,
    User3,
    User4,
    User5,
    KeepUpTo,
    ManaStone,
    ManaTank,
}

internal sealed class LootRule
{
    private string _expression = "*";
    private string? _compiledSource;
    private LootRuleExpression? _compiled;

    public string Name { get; set; } = "Rule";
    public string Expression
    {
        get => _expression;
        set => _expression = string.IsNullOrWhiteSpace(value) ? "*" : value.Trim();
    }
    public LootAction Action { get; set; } = LootAction.Keep;
    public int KeepCount { get; set; } = 1;
    public int Priority { get; set; }
    public string CustomExpression { get; set; } = string.Empty;
    public List<VtankLootRequirement> VtankRequirements { get; set; } = [];

    public bool IsMatch(
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IPluginHost? host,
        out string? error)
    {
        if (VtankRequirements.Count > 0)
        {
            return VtankLootRequirementEvaluator.IsMatch(
                VtankRequirements,
                item,
                properties,
                host,
                out error);
        }
        try
        {
            if (_compiled is null
                || !string.Equals(
                    _compiledSource,
                    Expression,
                    StringComparison.Ordinal))
            {
                _compiled = LootRuleExpression.Compile(Expression);
                _compiledSource = Expression;
            }
            error = null;
            return _compiled.IsMatch(item, properties);
        }
        catch (FormatException failure)
        {
            error = failure.Message;
            return false;
        }
    }
}

internal sealed class LootSettings
{
    // Official VTank defaults from uTank2.Resources.defaultsettings.usd.
    public bool Enabled { get; set; }
    public string ExternalClassifierId { get; set; } = string.Empty;
    public bool PriorityBoost { get; set; }
    public bool LootAllCorpses { get; set; }
    public bool LootFellowCorpses { get; set; }
    public bool LootOnlyRareCorpses { get; set; }
    public bool ReadUnknownScrolls { get; set; } = true;
    public bool CombineSalvage { get; set; } = true;
    public int ManaStoneLootCount { get; set; } = 4;
    public int ManaTankMinimumMana { get; set; } = 1000;
    public double CorpseApproachRange { get; set; } = 40d;
    public double CorpseMinimumApproachRange { get; set; } = 3.36d;
    public double CorpseOpenTimeoutSeconds { get; set; } = 1.5d;
    public double CorpseItemAppearanceTimeoutSeconds { get; set; } = 6d;
    public double CorpseItemIdentifyTimeoutSeconds { get; set; } = 60d;
    public int BlacklistCorpseOpenAttemptCount { get; set; } = 30;
    public double BlacklistCorpseOpenTimeoutSeconds { get; set; } = 200d;
    public double CorpseCacheTimeoutMinutes { get; set; } = 60d;
    public int CorpseLootItemMaxAttempts { get; set; } = 20;
    public double ScanIntervalSeconds { get; set; } = 0.25d;
    public List<LootRule> Rules { get; } = [];
    public VtankSalvageCombineSettings SalvageCombine { get; set; } = new();
}

internal readonly record struct LootDecision(
    LootAction Action,
    int Priority,
    int RuleIndex,
    string RuleName,
    string ClassifierId = "");

internal readonly record struct ManaStoneTransferPlan(
    uint StoneObjectId,
    uint TankObjectId,
    string StoneName,
    string TankName);

internal static class ManaStoneTransferPlanner
{
    private const uint ManaStoneItemType = 0x00080000u;
    private const uint RetainedFlag = 0x01000000u;

    public static ManaStoneTransferPlan? Plan(
        IReadOnlyList<PluginInventoryItem> owned,
        IReadOnlyDictionary<uint, LootAction> classified,
        int minimumTankMana)
    {
        PluginInventoryItem stone = owned
            .Where(item => classified.TryGetValue(
                    item.ObjectId,
                    out LootAction action)
                && action == LootAction.ManaStone
                && (item.ItemType & ManaStoneItemType) != 0u)
            .OrderBy(static item => item.ObjectId)
            .FirstOrDefault();
        if (stone.ObjectId == 0u)
            return null;
        int minimum = Math.Clamp(minimumTankMana, 1, int.MaxValue);
        PluginInventoryItem tank = owned
            .Where(item => classified.TryGetValue(
                    item.ObjectId,
                    out LootAction action)
                && action == LootAction.ManaTank
                && item.ItemCurrentMana >= minimum
                && item.Value != 0
                && (item.PublicFlags & RetainedFlag) == 0u)
            .OrderByDescending(static item => item.ItemCurrentMana)
            .ThenBy(static item => item.ObjectId)
            .FirstOrDefault();
        return tank.ObjectId == 0u
            ? null
            : new ManaStoneTransferPlan(
                stone.ObjectId,
                tank.ObjectId,
                stone.Name,
                tank.Name);
    }
}

internal sealed record SalvageBagCombinePlan(
    IReadOnlyList<uint> ObjectIds,
    uint MaterialType,
    IReadOnlyList<string> Names)
{
    public uint FirstObjectId => ObjectIds.Count > 0 ? ObjectIds[0] : 0u;
    public uint SecondObjectId => ObjectIds.Count > 1 ? ObjectIds[1] : 0u;
    public string FirstName => Names.Count > 0 ? Names[0] : string.Empty;
    public string SecondName => Names.Count > 1 ? Names[1] : string.Empty;
}

internal static partial class SalvageBagCombinePlanner
{
    public static SalvageBagCombinePlan? Plan(
        IReadOnlyList<PluginInventoryItem> owned,
        ISet<uint>? abandoned = null,
        VtankSalvageCombineSettings? settings = null)
    {
        settings ??= new VtankSalvageCombineSettings();
        PluginInventoryItem[] bags = owned
            .Where(item => item.MaterialType != 0u
                && SalvageBagName().IsMatch(item.Name)
                && (abandoned is null || !abandoned.Contains(item.ObjectId)))
            .OrderBy(static item => item.MaterialType)
            .ThenBy(static item => item.Workmanship)
            .ThenBy(static item => item.ObjectId)
            .ToArray();
        foreach (IGrouping<uint, PluginInventoryItem> materialGroup in
            bags.GroupBy(static item => item.MaterialType))
        {
            string combine = settings.MaterialCombineStrings.TryGetValue(
                checked((int)materialGroup.Key),
                out string? materialCombine)
                    ? materialCombine
                    : settings.DefaultCombineString;
            IReadOnlyList<(double Minimum, double Maximum)> ranges =
                ParseCombineString(combine);
            foreach (IGrouping<int, PluginInventoryItem> bin in materialGroup
                .GroupBy(item => RangeIndex(ranges, item.Workmanship))
                .OrderBy(static group => group.Key))
            {
                PluginInventoryItem[] candidates = bin.ToArray();
                if (candidates.Length < 2)
                    continue;
                IReadOnlyList<PluginInventoryItem> selected;
                if (settings.MaterialValueModeValues.TryGetValue(
                    checked((int)materialGroup.Key),
                    out int targetValue))
                {
                    if (candidates.Sum(static item => item.Value) >= targetValue)
                    {
                        selected = candidates;
                    }
                    else
                    {
                        selected = FindSubHundredPair(candidates);
                        if (selected.Count == 0)
                            continue;
                    }
                }
                else
                {
                    var maximumBags = new List<PluginInventoryItem>();
                    int units = 0;
                    foreach (PluginInventoryItem candidate in candidates)
                    {
                        maximumBags.Add(candidate);
                        units += Math.Max(0, candidate.Structure);
                        if (units >= 100)
                            break;
                    }
                    selected = maximumBags;
                }
                return new SalvageBagCombinePlan(
                    selected.Select(static item => item.ObjectId).ToArray(),
                    materialGroup.Key,
                    selected.Select(static item => item.Name).ToArray());
            }
        }
        return null;
    }

    internal static bool SameVtankWorkmanshipBand(float left, float right) =>
        (left < 7f && right < 7f)
        || (left >= 7f && left < 9f && right >= 7f && right < 9f)
        || (left >= 9f && left < 10f && right >= 9f && right < 10f)
        || (left == 10f && right == 10f);

    internal static bool SameCombineBand(
        float left,
        float right,
        string combineString)
    {
        IReadOnlyList<(double Minimum, double Maximum)> ranges =
            ParseCombineString(combineString);
        return RangeIndex(ranges, left) == RangeIndex(ranges, right);
    }

    private static IReadOnlyList<PluginInventoryItem> FindSubHundredPair(
        IReadOnlyList<PluginInventoryItem> candidates)
    {
        for (int left = 0; left < candidates.Count - 1; left++)
        {
            for (int right = left + 1; right < candidates.Count; right++)
            {
                if (Math.Max(0, candidates[left].Structure)
                    + Math.Max(0, candidates[right].Structure) < 100)
                {
                    return [candidates[left], candidates[right]];
                }
            }
        }
        return [];
    }

    private static IReadOnlyList<(double Minimum, double Maximum)>
        ParseCombineString(string? source)
    {
        var result = new List<(double, double)>();
        foreach (string token in (source ?? string.Empty).Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] bounds = token.Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bounds.Length == 0
                || !double.TryParse(bounds[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double minimum))
            {
                continue;
            }
            double maximum = minimum;
            if (bounds.Length > 1)
            {
                _ = double.TryParse(bounds[1], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out maximum);
            }
            result.Add((minimum, maximum));
        }
        return result;
    }

    private static int RangeIndex(
        IReadOnlyList<(double Minimum, double Maximum)> ranges,
        double value)
    {
        for (int index = 0; index < ranges.Count; index++)
        {
            if (ranges[index].Minimum > value)
                return index - 1;
            if (ranges[index].Minimum <= value
                && ranges[index].Maximum >= value)
            {
                return index;
            }
        }
        return ranges.Count;
    }

    [GeneratedRegex(@"^Salvage(?:d)?.* \([0-9]{1,2}\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SalvageBagName();
}

internal static class LootRuleEngine
{
    public static LootDecision? Decide(
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IReadOnlyList<LootRule> rules,
        IReadOnlyList<PluginInventoryItem> ownedItems,
        IReadOnlyDictionary<string, int>? pendingByName = null,
        IPluginHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(ownedItems);

        for (int index = 0; index < rules.Count; index++)
        {
            LootRule rule = rules[index];
            if (!rule.IsMatch(item, properties, host, out _))
                continue;
            if (rule.Action == LootAction.NoLoot)
                return null;
            if (rule.Action == LootAction.KeepUpTo)
            {
                int limit = Math.Max(0, rule.KeepCount);
                string itemName = item.Name;
                int held = ownedItems
                    .Where(owned => string.Equals(
                        owned.Name,
                        itemName,
                        StringComparison.OrdinalIgnoreCase))
                    .Sum(static owned => Math.Max(1, owned.StackSize));
                if (pendingByName is not null
                    && pendingByName.TryGetValue(item.Name, out int pending))
                {
                    held += pending;
                }
                if (held >= limit)
                    return null;
            }
            return new LootDecision(
                rule.Action,
                rule.Priority,
                index,
                string.IsNullOrWhiteSpace(rule.Name)
                    ? $"Rule {index + 1}"
                    : rule.Name);
        }
        return null;
    }
}

internal sealed class LootController
{
    private const double PickupTimeoutSeconds = 4d;

    private readonly IPluginHost _host;
    private readonly LootSettings _settings;
    private readonly Dictionary<uint, double> _completedCorpses = [];
    private readonly Dictionary<uint, int> _corpseOpenAttempts = [];
    private readonly Dictionary<uint, double> _corpseBlacklistedAt = [];
    private readonly Dictionary<uint, int> _itemAttempts = [];
    private readonly Dictionary<string, int> _pendingByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, LootAction> _classifiedOwnedItems = [];
    private readonly Dictionary<uint, string> _externalClassifierByItem = [];
    private readonly Dictionary<uint, LootDecision?> _decisions = [];
    private readonly Dictionary<uint, double> _corpseFirstSeen = [];
    private double _scanRemaining;
    private double _stateAge;
    private uint _activeCorpse;
    private bool _activeCorpseSawContents;
    private uint _waitingItem;
    private string _waitingName = string.Empty;
    private LootAction _waitingAction;
    private int _waitingQuantity;
    private PluginInventoryItem _waitingItemSnapshot;
    private string _waitingClassifierId = string.Empty;
    private long _waitingInventoryRevision;
    private uint _awaitingAppraisal;
    private uint _awaitingCorpseAppraisal;
    private double _lifetime;
    private uint _postUseItem;
    private string _postUseName = string.Empty;
    private bool _postUseStarted;
    private long _postUseRevision;
    private uint _salvagePendingItem;
    private string _salvagePendingName = string.Empty;
    private int _salvageAttempts;
    private uint _sellPendingItem;
    private string _sellPendingName = string.Empty;
    private ManaStoneTransferPlan? _manaTransfer;
    private long _manaTransferRevision;
    private SalvageBagCombinePlan? _combinePending;
    private readonly Dictionary<uint, int> _combineAttempts = [];
    private readonly HashSet<uint> _abandonedCombineBags = [];

    public LootController(
        IPluginHost host,
        LootSettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Status { get; private set; } = "Looting disabled.";
    public IReadOnlyDictionary<uint, LootAction> ClassifiedOwnedItems =>
        _classifiedOwnedItems;

    public bool Tick(double elapsedSeconds, bool canAct)
    {
        ILootAutomation loot = _host.Automation.Loot;
        if (!_settings.Enabled)
        {
            ResetTransient();
            Status = "Looting disabled.";
            return false;
        }
        if (!_host.Automation.IsAvailable || !loot.IsAvailable)
        {
            Reset();
            Status = "Looting unavailable.";
            return false;
        }
        if (_settings.Rules.Count == 0
            && string.IsNullOrWhiteSpace(_settings.ExternalClassifierId))
        {
            ResetTransient();
            Status = "Loot profile has no rules.";
            return false;
        }

        if (_activeCorpse == 0u && _waitingItem == 0u)
            PruneRemovedExternalItems();

        _stateAge += Math.Max(0d, elapsedSeconds);
        _lifetime += Math.Max(0d, elapsedSeconds);
        if (_waitingItem != 0u)
            return ContinuePickup(loot);
        if (_postUseItem != 0u)
            return ContinuePostUse(canAct);
        if (_activeCorpse == 0u
            && (_manaTransfer is not null
                || HasManaStoneTransfer())
            && ContinueManaStoneTransfer(canAct))
        {
            return true;
        }
        if (_activeCorpse == 0u
            && (_salvagePendingItem != 0u
                || _classifiedOwnedItems.Values.Contains(LootAction.Salvage))
            && ContinueSalvage(canAct))
        {
            return true;
        }
        if (_activeCorpse == 0u
            && _settings.CombineSalvage
            && (_combinePending is not null || HasSalvageBagCombine())
            && ContinueSalvageBagCombine(canAct))
        {
            return true;
        }
        if (_activeCorpse == 0u
            && (_sellPendingItem != 0u
                || _classifiedOwnedItems.Values.Contains(LootAction.Sell))
            && ContinueSell(canAct))
        {
            return true;
        }

        uint current = loot.CurrentContainerId;
        if (_activeCorpse != 0u && current == _activeCorpse)
            return ContinueCurrentCorpse(loot, canAct);

        if (_activeCorpse != 0u
            && loot.RequestedContainerId == _activeCorpse)
        {
            if (_stateAge <= Math.Max(0.25d, _settings.CorpseOpenTimeoutSeconds))
            {
                Status = "Waiting for corpse contents…";
                return true;
            }
            uint failedCorpse = _activeCorpse;
            BlacklistFailedCorpse(failedCorpse);
            _activeCorpse = 0u;
            _activeCorpseSawContents = false;
            _stateAge = 0d;
            if (IsCorpseBlacklisted(failedCorpse))
                return false;
        }

        if (!canAct || loot.IsBusy)
            return false;

        _scanRemaining -= Math.Max(0d, elapsedSeconds);
        if (_scanRemaining > 0d)
            return false;
        _scanRemaining = Math.Clamp(_settings.ScanIntervalSeconds, 0.05d, 5d);

        IReadOnlyList<PluginLootContainer> corpses = loot.CaptureCorpses(
            (float)Math.Clamp(_settings.CorpseApproachRange, 2d, 100d));
        PruneCorpseCache();
        foreach (PluginLootContainer seen in corpses)
            _corpseFirstSeen.TryAdd(seen.ObjectId, _lifetime);

        if (_awaitingCorpseAppraisal != 0u)
        {
            PluginAppraisalState appraisal = loot.Appraisal;
            if (appraisal.CurrentObjectId == _awaitingCorpseAppraisal
                && appraisal.AwaitingObjectId != _awaitingCorpseAppraisal)
            {
                _awaitingCorpseAppraisal = 0u;
                _stateAge = 0d;
            }
            else if (_stateAge < Math.Max(
                1d,
                _settings.CorpseOpenTimeoutSeconds * 2d))
            {
                Status = "Identifying corpse…";
                return true;
            }
            else
            {
                MarkCorpseComplete(_awaitingCorpseAppraisal);
                _awaitingCorpseAppraisal = 0u;
                _stateAge = 0d;
            }
        }

        PluginLootContainer? next = null;
        foreach (PluginLootContainer candidateCorpse in corpses
            .Where(corpse => !_completedCorpses.ContainsKey(corpse.ObjectId))
            .Where(corpse => !IsCorpseBlacklisted(corpse.ObjectId))
            .OrderBy(static corpse => corpse.Distance)
            .ThenBy(static corpse => corpse.ObjectId))
        {
            if (!candidateCorpse.IsIdentified)
            {
                PluginItemCommandResult identify = loot.Identify(
                    candidateCorpse.ObjectId);
                if (identify.Accepted)
                {
                    _awaitingCorpseAppraisal = candidateCorpse.ObjectId;
                    _stateAge = 0d;
                    Status = $"Identifying {candidateCorpse.Name}…";
                    return true;
                }
                if (identify.Status == PluginItemCommandStatus.Busy)
                    return true;
                continue;
            }
            if (!CanLoot(candidateCorpse))
                continue;
            next = candidateCorpse;
            break;
        }
        if (next is not { } corpse)
        {
            Status = "No nearby corpses.";
            return false;
        }

        PluginItemCommandResult opened = loot.Open(corpse.ObjectId);
        if (!opened.Accepted)
        {
            Status = opened.Status == PluginItemCommandStatus.Busy
                ? "Waiting to open corpse…"
                : $"Could not open {corpse.Name}.";
            return opened.Status == PluginItemCommandStatus.Busy;
        }
        _activeCorpse = corpse.ObjectId;
        _activeCorpseSawContents = false;
        _stateAge = 0d;
        Status = $"Opening {corpse.Name}…";
        return true;
    }

    public void Reset()
    {
        foreach ((uint objectId, string classifierId) in
                 _externalClassifierByItem.ToArray())
        {
            _host.LootClassifiers.TryNotifyItemRemoved(classifierId, objectId);
        }
        ResetTransient();
        _completedCorpses.Clear();
        _corpseOpenAttempts.Clear();
        _corpseBlacklistedAt.Clear();
        _itemAttempts.Clear();
        _pendingByName.Clear();
        _classifiedOwnedItems.Clear();
        _externalClassifierByItem.Clear();
        _decisions.Clear();
        _corpseFirstSeen.Clear();
        _scanRemaining = 0d;
        _lifetime = 0d;
        _postUseItem = 0u;
        _postUseName = string.Empty;
        _postUseStarted = false;
        _postUseRevision = 0L;
        _salvagePendingItem = 0u;
        _salvagePendingName = string.Empty;
        _salvageAttempts = 0;
        _sellPendingItem = 0u;
        _sellPendingName = string.Empty;
        _manaTransfer = null;
        _manaTransferRevision = 0L;
        _combinePending = null;
        _combineAttempts.Clear();
        _abandonedCombineBags.Clear();
        Status = _settings.Enabled ? "Idle." : "Looting disabled.";
    }

    private bool ContinueCurrentCorpse(ILootAutomation loot, bool canAct)
    {
        if (_stateAge < 0.10d)
        {
            Status = "Reading corpse contents…";
            return true;
        }

        IReadOnlyList<PluginInventoryItem> contents =
            loot.CaptureCurrentContents();
        if (contents.Count > 0)
            _activeCorpseSawContents = true;
        if (contents.Count == 0
            && !_activeCorpseSawContents
            && _stateAge < Math.Clamp(
                _settings.CorpseItemAppearanceTimeoutSeconds,
                0d,
                300d))
        {
            Status = "Waiting for corpse items to appear…";
            return true;
        }
        IReadOnlyList<PluginInventoryItem> owned =
            _host.Automation.Items.CaptureOwnedItems();
        if (_awaitingAppraisal != 0u)
        {
            PluginAppraisalState appraisal = loot.Appraisal;
            if (appraisal.CurrentObjectId == _awaitingAppraisal
                && appraisal.AwaitingObjectId != _awaitingAppraisal)
            {
                if (contents.FirstOrDefault(
                        item => item.ObjectId == _awaitingAppraisal) is { } item
                    && item.ObjectId != 0u)
                {
                    PluginItemProperties identified = default;
                    _ = loot.TryCaptureProperties(item.ObjectId, out identified);
                    _decisions[item.ObjectId] = DecideItem(
                        item,
                        identified,
                        owned,
                        _pendingByName);
                }
                _awaitingAppraisal = 0u;
                _stateAge = 0d;
            }
            else if (_stateAge < Math.Clamp(
                _settings.CorpseItemIdentifyTimeoutSeconds,
                1d,
                600d))
            {
                Status = "Identifying corpse item…";
                return true;
            }
            else
            {
                IncrementAttempt(_awaitingAppraisal);
                _awaitingAppraisal = 0u;
                _stateAge = 0d;
            }
        }

        foreach (PluginInventoryItem item in contents)
        {
            if (_decisions.ContainsKey(item.ObjectId))
                continue;
            if (!canAct || loot.IsBusy)
                return true;

            PluginAppraisalState appraisal = loot.Appraisal;
            if (appraisal.CurrentObjectId != item.ObjectId)
            {
                PluginItemCommandResult identify = loot.Identify(item.ObjectId);
                if (identify.Accepted)
                {
                    _awaitingAppraisal = item.ObjectId;
                    _stateAge = 0d;
                    Status = $"Identifying {item.Name}…";
                    return true;
                }
                if (identify.Status == PluginItemCommandStatus.Busy)
                    return true;
            }

            PluginItemProperties properties = default;
            _ = loot.TryCaptureProperties(item.ObjectId, out properties);
            _decisions[item.ObjectId] = DecideItem(
                item,
                properties,
                owned,
                _pendingByName);
        }

        var candidates = new List<(PluginInventoryItem Item, LootDecision Decision)>();
        foreach (PluginInventoryItem item in contents)
        {
            if (_itemAttempts.TryGetValue(item.ObjectId, out int attempts)
                && attempts >= Math.Clamp(
                    _settings.CorpseLootItemMaxAttempts,
                    1,
                    100))
            {
                continue;
            }
            if (_decisions.TryGetValue(item.ObjectId, out LootDecision? cached)
                && cached is { } decision)
            {
                candidates.Add((item, decision));
            }
        }

        if (candidates.Count == 0)
        {
            foreach (PluginInventoryItem item in contents)
                _decisions.Remove(item.ObjectId);
            MarkCorpseComplete(_activeCorpse);
            _activeCorpse = 0u;
            _activeCorpseSawContents = false;
            _stateAge = 0d;
            Status = "Corpse complete.";
            return false;
        }
        if (!canAct || loot.IsBusy)
            return true;

        (PluginInventoryItem Item, LootDecision Decision) chosen = candidates
            .OrderByDescending(static candidate => candidate.Decision.Priority)
            .ThenBy(static candidate => candidate.Decision.RuleIndex)
            .ThenBy(static candidate => candidate.Item.ContainerSlot)
            .ThenBy(static candidate => candidate.Item.ObjectId)
            .First();
        PluginItemCommandResult pickup = loot.Pickup(chosen.Item.ObjectId);
        if (!pickup.Accepted)
        {
            IncrementAttempt(chosen.Item.ObjectId);
            Status = $"Pickup refused: {chosen.Item.Name}.";
            return pickup.Status == PluginItemCommandStatus.Busy;
        }

        _waitingItem = chosen.Item.ObjectId;
        _waitingName = chosen.Item.Name;
        _waitingAction = chosen.Decision.Action;
        _waitingQuantity = Math.Max(1, chosen.Item.StackSize);
        _waitingItemSnapshot = chosen.Item;
        _waitingClassifierId = chosen.Decision.ClassifierId;
        _waitingInventoryRevision = loot.LastInventoryCompletion.Revision;
        _stateAge = 0d;
        if (chosen.Decision.Action == LootAction.KeepUpTo)
        {
            _pendingByName.TryGetValue(chosen.Item.Name, out int pending);
            _pendingByName[chosen.Item.Name] =
                pending + _waitingQuantity;
        }
        Status = $"Looting {chosen.Item.Name} ({chosen.Decision.RuleName})…";
        return true;
    }

    private bool ContinuePickup(ILootAutomation loot)
    {
        PluginInventoryCompletion completion = loot.LastInventoryCompletion;
        bool advanced = completion.Revision > _waitingInventoryRevision
            && completion.SourceObjectId == _waitingItem;
        bool stillInCorpse = loot.CaptureCurrentContents().Any(
            item => item.ObjectId == _waitingItem);
        if (!advanced && stillInCorpse && _stateAge < PickupTimeoutSeconds)
        {
            Status = $"Waiting for {_waitingName}…";
            return true;
        }

        bool success = !stillInCorpse || (advanced && completion.IsSuccess);
        if (success)
        {
            _classifiedOwnedItems[_waitingItem] = _waitingAction;
            if (_waitingClassifierId.Length != 0)
            {
                _externalClassifierByItem[_waitingItem] = _waitingClassifierId;
                _host.LootClassifiers.TryNotifyLooted(
                    _waitingClassifierId,
                    new PluginLootedItem(
                        _waitingItemSnapshot,
                        (PluginLootAction)(int)_waitingAction));
            }
            _decisions.Remove(_waitingItem);
            Status = $"Looted {_waitingName}.";
            _itemAttempts.Remove(_waitingItem);
            if (_waitingAction == LootAction.Read)
            {
                _postUseItem = _waitingItem;
                _postUseName = _waitingName;
                _postUseStarted = false;
                _postUseRevision = 0L;
            }
        }
        else
        {
            IncrementAttempt(_waitingItem);
            Status = $"Retrying {_waitingName}.";
        }
        if (_waitingAction == LootAction.KeepUpTo
            && _pendingByName.TryGetValue(_waitingName, out int pending))
        {
            if (pending <= _waitingQuantity)
                _pendingByName.Remove(_waitingName);
            else
                _pendingByName[_waitingName] = pending - _waitingQuantity;
        }
        _waitingItem = 0u;
        _waitingName = string.Empty;
        _waitingAction = LootAction.NoLoot;
        _waitingQuantity = 0;
        _waitingItemSnapshot = default;
        _waitingClassifierId = string.Empty;
        _waitingInventoryRevision = 0L;
        _stateAge = 0d;
        return true;
    }

    private bool ContinuePostUse(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        if (_postUseStarted)
        {
            PluginItemUseCompletion completion = items.LastCompletion;
            if (completion.Revision <= _postUseRevision
                || completion.SourceObjectId != _postUseItem)
            {
                if (_stateAge < PickupTimeoutSeconds)
                {
                    Status = $"Reading {_postUseName}…";
                    return true;
                }
                Status = $"Read timed out: {_postUseName}.";
            }
            else
            {
                Status = completion.IsSuccess
                    ? $"Read {_postUseName}."
                    : $"Could not read {_postUseName}.";
            }
            _postUseItem = 0u;
            _postUseName = string.Empty;
            _postUseStarted = false;
            _postUseRevision = 0L;
            _stateAge = 0d;
            return true;
        }
        if (!canAct || !items.IsAvailable || items.IsBusy)
            return true;
        PluginItemCommandResult use = items.Use(_postUseItem);
        if (!use.Accepted)
        {
            if (use.Status == PluginItemCommandStatus.Busy)
                return true;
            Status = $"Could not read {_postUseName}.";
            _postUseItem = 0u;
            _postUseName = string.Empty;
            return false;
        }
        _postUseRevision = items.LastCompletion.Revision;
        _postUseStarted = true;
        _stateAge = 0d;
        Status = $"Reading {_postUseName}…";
        return true;
    }

    private bool ContinueSalvage(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        IReadOnlyList<PluginInventoryItem> owned = items.CaptureOwnedItems();
        if (_salvagePendingItem != 0u)
        {
            if (!owned.Any(item => item.ObjectId == _salvagePendingItem))
            {
                RemoveClassifiedOwned(_salvagePendingItem);
                Status = $"Salvaged {_salvagePendingName}.";
                _salvagePendingItem = 0u;
                _salvagePendingName = string.Empty;
                _salvageAttempts = 0;
                _stateAge = 0d;
                return true;
            }
            if (_stateAge < PickupTimeoutSeconds)
            {
                Status = $"Salvaging {_salvagePendingName}…";
                return true;
            }
            _salvagePendingItem = 0u;
            _salvagePendingName = string.Empty;
            _stateAge = 0d;
            _salvageAttempts++;
        }

        var ownedById = owned.ToDictionary(static item => item.ObjectId);
        foreach (uint stale in _classifiedOwnedItems
            .Where(entry => entry.Value == LootAction.Salvage
                && !ownedById.ContainsKey(entry.Key))
            .Select(static entry => entry.Key)
            .ToArray())
        {
            RemoveClassifiedOwned(stale);
        }
        uint sourceId = _classifiedOwnedItems
            .Where(static entry => entry.Value == LootAction.Salvage)
            .Select(static entry => entry.Key)
            .FirstOrDefault(ownedById.ContainsKey);
        if (sourceId == 0u)
            return false;
        if (!canAct || !items.IsAvailable || items.IsBusy)
            return true;

        const uint tinkeringTool = 0x20000000u;
        PluginInventoryItem tool = owned.FirstOrDefault(
            item => (item.ItemType & tinkeringTool) != 0u);
        if (tool.ObjectId == 0u)
        {
            Status = "Salvage action is waiting for a salvage tool.";
            return false;
        }

        PluginInventoryItem source = ownedById[sourceId];
        PluginItemCommandResult result = items.Salvage(tool.ObjectId, [sourceId]);
        if (!result.Accepted)
        {
            Status = result.Status == PluginItemCommandStatus.Busy
                ? "Waiting to salvage…"
                : $"Could not salvage {source.Name}.";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _salvagePendingItem = sourceId;
        _salvagePendingName = source.Name;
        _stateAge = 0d;
        Status = $"Salvaging {source.Name}…";
        return true;
    }

    private bool ContinueSell(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        IReadOnlyList<PluginInventoryItem> owned = items.CaptureOwnedItems();
        if (_sellPendingItem != 0u)
        {
            if (!owned.Any(item => item.ObjectId == _sellPendingItem))
            {
                RemoveClassifiedOwned(_sellPendingItem);
                Status = $"Sold {_sellPendingName}.";
                _sellPendingItem = 0u;
                _sellPendingName = string.Empty;
                _stateAge = 0d;
                return true;
            }
            if (_stateAge < PickupTimeoutSeconds)
            {
                Status = $"Selling {_sellPendingName}…";
                return true;
            }
            _sellPendingItem = 0u;
            _sellPendingName = string.Empty;
            _stateAge = 0d;
        }

        var ownedById = owned.ToDictionary(static item => item.ObjectId);
        foreach (uint stale in _classifiedOwnedItems
            .Where(entry => entry.Value == LootAction.Sell
                && !ownedById.ContainsKey(entry.Key))
            .Select(static entry => entry.Key)
            .ToArray())
        {
            RemoveClassifiedOwned(stale);
        }
        uint sourceId = _classifiedOwnedItems
            .Where(static entry => entry.Value == LootAction.Sell)
            .Select(static entry => entry.Key)
            .FirstOrDefault(ownedById.ContainsKey);
        if (sourceId == 0u)
            return false;
        if (items.ActiveVendorObjectId == 0u)
        {
            Status = "Sell loot is queued until a vendor is open.";
            return false;
        }
        if (!canAct || !items.IsAvailable || items.IsBusy)
            return true;

        PluginInventoryItem source = ownedById[sourceId];
        PluginItemCommandResult result = items.Sell(sourceId);
        if (!result.Accepted)
        {
            Status = result.Status == PluginItemCommandStatus.Busy
                ? "Waiting to sell…"
                : result.Notice ?? $"Could not sell {source.Name}.";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _sellPendingItem = sourceId;
        _sellPendingName = source.Name;
        _stateAge = 0d;
        Status = $"Selling {source.Name}…";
        return true;
    }

    private bool HasManaStoneTransfer()
    {
        if (!_classifiedOwnedItems.Values.Contains(LootAction.ManaStone)
            || !_classifiedOwnedItems.Values.Contains(LootAction.ManaTank))
        {
            return false;
        }
        return ManaStoneTransferPlanner.Plan(
            _host.Automation.Items.CaptureOwnedItems(),
            _classifiedOwnedItems,
            _settings.ManaTankMinimumMana) is not null;
    }

    private bool ContinueManaStoneTransfer(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        if (_manaTransfer is { } pending)
        {
            PluginItemUseCompletion completion = items.LastCompletion;
            if (completion.Revision <= _manaTransferRevision
                || completion.SourceObjectId != pending.StoneObjectId)
            {
                if (_stateAge < PickupTimeoutSeconds)
                {
                    Status = $"Filling {pending.StoneName}…";
                    return true;
                }
                Status = $"Mana stone fill timed out: {pending.StoneName}.";
            }
            else
            {
                Status = completion.IsSuccess
                    ? $"Filled {pending.StoneName}."
                    : $"Could not fill {pending.StoneName}.";
            }
            RemoveClassifiedOwned(pending.StoneObjectId);
            RemoveClassifiedOwned(pending.TankObjectId);
            _manaTransfer = null;
            _manaTransferRevision = 0L;
            _stateAge = 0d;
            return true;
        }
        if (!canAct || !items.IsAvailable || items.IsBusy)
            return true;

        ManaStoneTransferPlan? plan = ManaStoneTransferPlanner.Plan(
            items.CaptureOwnedItems(),
            _classifiedOwnedItems,
            _settings.ManaTankMinimumMana);
        if (plan is not { } next)
            return false;
        PluginItemCommandResult result = items.Apply(
            next.StoneObjectId,
            next.TankObjectId);
        if (!result.Accepted)
        {
            Status = result.Status == PluginItemCommandStatus.Busy
                ? "Waiting to fill mana stone…"
                : $"Could not use {next.StoneName} on {next.TankName}.";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _manaTransfer = next;
        _manaTransferRevision = items.LastCompletion.Revision;
        _stateAge = 0d;
        Status = $"Filling {next.StoneName} from {next.TankName}…";
        return true;
    }

    private bool HasSalvageBagCombine() => SalvageBagCombinePlanner.Plan(
        _host.Automation.Items.CaptureOwnedItems(),
        _abandonedCombineBags,
        _settings.SalvageCombine) is not null;

    private bool ContinueSalvageBagCombine(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        IReadOnlyList<PluginInventoryItem> owned = items.CaptureOwnedItems();
        if (_combinePending is { } pending)
        {
            bool allExist = pending.ObjectIds.All(
                id => owned.Any(item => item.ObjectId == id));
            if (!allExist)
            {
                foreach (uint id in pending.ObjectIds)
                    _combineAttempts.Remove(id);
                _combinePending = null;
                _stateAge = 0d;
                Status = "Combined salvage bags.";
                return true;
            }
            if (_stateAge < PickupTimeoutSeconds)
            {
                Status = "Combining salvage bags…";
                return true;
            }

            _combineAttempts.TryGetValue(pending.FirstObjectId, out int attempts);
            attempts++;
            _combineAttempts[pending.FirstObjectId] = attempts;
            if (attempts > 40)
            {
                _abandonedCombineBags.Add(pending.FirstObjectId);
                _combineAttempts.Remove(pending.FirstObjectId);
                Status = $"Abandoned bugged salvage bag {pending.FirstName}.";
            }
            else
            {
                Status = $"Retrying salvage combine ({attempts}/40)…";
            }
            _combinePending = null;
            _stateAge = 0d;
            return attempts <= 40;
        }
        if (!canAct || !items.IsAvailable || items.IsBusy)
            return true;

        SalvageBagCombinePlan? plan = SalvageBagCombinePlanner.Plan(
            owned,
            _abandonedCombineBags,
            _settings.SalvageCombine);
        if (plan is not { } next)
            return false;
        const uint tinkeringTool = 0x20000000u;
        PluginInventoryItem tool = owned.FirstOrDefault(
            item => (item.ItemType & tinkeringTool) != 0u);
        if (tool.ObjectId == 0u)
        {
            Status = "Salvage combine is waiting for a salvage tool.";
            return false;
        }
        PluginItemCommandResult result = items.Salvage(
            tool.ObjectId,
            next.ObjectIds);
        if (!result.Accepted)
        {
            Status = result.Status == PluginItemCommandStatus.Busy
                ? "Waiting to combine salvage…"
                : "Could not combine salvage bags.";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _combinePending = next;
        _stateAge = 0d;
        Status = "Combining salvage bags…";
        return true;
    }

    private void IncrementAttempt(uint objectId)
    {
        _itemAttempts.TryGetValue(objectId, out int attempts);
        _itemAttempts[objectId] = attempts + 1;
    }

    private bool CanLoot(in PluginLootContainer corpse)
    {
        if (_settings.LootOnlyRareCorpses && !corpse.IsGeneratedRare)
            return false;
        string killer = KillerName(corpse.LongDescription);
        string character = _host.Automation.Character.Name;
        if (killer.Length != 0
            && character.Length != 0
            && string.Equals(killer, character, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (corpse.IsGeneratedRare)
            return false;

        double firstSeen = _corpseFirstSeen.TryGetValue(
            corpse.ObjectId,
            out double value) ? value : _lifetime;
        double age = _lifetime - firstSeen;
        PluginFellowMember? fellow = _host.Automation.Fellowship
            .CaptureMembers()
            .FirstOrDefault(member => string.Equals(
                member.Name,
                killer,
                StringComparison.OrdinalIgnoreCase));
        if (fellow is { ObjectId: not 0u } member)
        {
            if (!_settings.LootFellowCorpses)
                return false;
            return member.ShareLoot || age >= 100d;
        }

        return _settings.LootAllCorpses && age >= 100d;
    }

    private LootDecision? DecideItem(
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IReadOnlyList<PluginInventoryItem> owned,
        IReadOnlyDictionary<string, int> pending)
    {
        LootDecision? decision = string.IsNullOrWhiteSpace(
            _settings.ExternalClassifierId)
            ? LootRuleEngine.Decide(
                item,
                properties,
                _settings.Rules,
                owned,
                pending,
                _host)
            : DecideWithExternalClassifier(item, properties, owned, pending);
        if (decision is not null || !IsReadableUnknownScroll(item))
        {
            if (decision is not null)
                return decision;
        }
        else
        {
            return new LootDecision(
                LootAction.Read,
                Priority: 0,
                RuleIndex: int.MaxValue,
                RuleName: "Unknown Scroll");
        }

        LootAction? manaAction = AutomaticManaAction(item, owned);
        return manaAction is { } action
            ? new LootDecision(
                action,
                Priority: 0,
                RuleIndex: int.MaxValue,
                RuleName: action.ToString())
            : null;
    }

    private LootDecision? DecideWithExternalClassifier(
        in PluginInventoryItem item,
        in PluginItemProperties properties,
        IReadOnlyList<PluginInventoryItem> owned,
        IReadOnlyDictionary<string, int> pending)
    {
        var context = new PluginLootClassificationContext(
            item,
            properties,
            owned);
        if (!_host.LootClassifiers.TryClassify(
                _settings.ExternalClassifierId,
                context,
                out PluginLootClassification classification)
            || !classification.Matched
            || !Enum.IsDefined(classification.Action))
        {
            return null;
        }

        LootAction action = (LootAction)(int)classification.Action;
        if (action == LootAction.NoLoot)
            return null;
        if (action == LootAction.KeepUpTo)
        {
            int limit = Math.Max(0, classification.KeepCount);
            string itemName = item.Name;
            int held = owned
                .Where(ownedItem => string.Equals(
                    ownedItem.Name,
                    itemName,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(static ownedItem => Math.Max(1, ownedItem.StackSize));
            if (pending.TryGetValue(itemName, out int pendingCount))
                held += pendingCount;
            if (held >= limit)
                return null;
        }

        return new LootDecision(
            action,
            classification.Priority,
            RuleIndex: -1,
            RuleName: string.IsNullOrWhiteSpace(classification.RuleName)
                ? _settings.ExternalClassifierId
                : classification.RuleName.Trim(),
            ClassifierId: _settings.ExternalClassifierId);
    }

    private void RemoveClassifiedOwned(uint objectId)
    {
        _classifiedOwnedItems.Remove(objectId);
        if (!_externalClassifierByItem.Remove(objectId, out string? classifierId))
            return;
        _host.LootClassifiers.TryNotifyItemRemoved(classifierId, objectId);
    }

    private void PruneRemovedExternalItems()
    {
        if (_externalClassifierByItem.Count == 0)
            return;
        HashSet<uint> owned = _host.Automation.Items.CaptureOwnedItems()
            .Select(static item => item.ObjectId)
            .ToHashSet();
        foreach (uint removed in _externalClassifierByItem.Keys
            .Where(objectId => !owned.Contains(objectId))
            .ToArray())
        {
            RemoveClassifiedOwned(removed);
        }
    }

    private LootAction? AutomaticManaAction(
        in PluginInventoryItem item,
        IReadOnlyList<PluginInventoryItem> owned)
    {
        const uint manaStoneType = 0x00080000u;
        const uint retainedFlag = 0x01000000u;
        int desired = Math.Clamp(_settings.ManaStoneLootCount, 0, 100);
        int stones = owned.Count(ownedItem =>
            (ownedItem.ItemType & manaStoneType) != 0u);
        stones += _classifiedOwnedItems.Values.Count(
            static action => action == LootAction.ManaStone);
        if ((item.ItemType & manaStoneType) != 0u && stones < desired)
            return LootAction.ManaStone;
        if (stones < desired
            && item.ItemCurrentMana >= Math.Clamp(
                _settings.ManaTankMinimumMana,
                1,
                int.MaxValue)
            && item.Value != 0
            && (item.PublicFlags & retainedFlag) == 0u)
        {
            return LootAction.ManaTank;
        }
        return null;
    }

    private bool IsReadableUnknownScroll(in PluginInventoryItem item)
    {
        if (!_settings.ReadUnknownScrolls
            || item.SpellId == 0u
            || _host.Automation.Spells.IsKnown(item.SpellId))
        {
            return false;
        }

        const uint miscItemType = 0x00000080u;
        bool scrollShape = (item.ItemType & miscItemType) != 0u
            && item.Name.EndsWith(" Scroll", StringComparison.OrdinalIgnoreCase);
        if (!scrollShape
            || !_host.Automation.Spells.TryGet(item.SpellId, out PluginSpellInfo spell))
        {
            return false;
        }
        return _host.Automation.Character.TryGetSkill(
                spell.School,
                out PluginSkillInfo skill)
            && spell.Difficulty - 15 <= skill.Current;
    }

    private void BlacklistFailedCorpse(uint corpseId)
    {
        if (corpseId == 0u)
            return;
        _corpseOpenAttempts.TryGetValue(corpseId, out int attempts);
        attempts++;
        int threshold = Math.Clamp(
            _settings.BlacklistCorpseOpenAttemptCount,
            1,
            1000);
        if (attempts < threshold)
        {
            _corpseOpenAttempts[corpseId] = attempts;
            Status = $"Retrying corpse ({attempts}/{threshold})…";
            return;
        }
        _corpseOpenAttempts.Remove(corpseId);
        _corpseBlacklistedAt[corpseId] = _lifetime;
        Status = $"Blacklisted unopenable corpse for "
            + $"{Math.Clamp(_settings.BlacklistCorpseOpenTimeoutSeconds, 1d, 3600d):0} seconds.";
    }

    private bool IsCorpseBlacklisted(uint corpseId)
    {
        if (!_corpseBlacklistedAt.TryGetValue(corpseId, out double since))
            return false;
        double timeout = Math.Clamp(
            _settings.BlacklistCorpseOpenTimeoutSeconds,
            1d,
            3600d);
        if (_lifetime - since < timeout)
            return true;
        _corpseBlacklistedAt.Remove(corpseId);
        return false;
    }

    private void MarkCorpseComplete(uint corpseId)
    {
        if (corpseId == 0u)
            return;
        _completedCorpses[corpseId] = _lifetime;
        _corpseOpenAttempts.Remove(corpseId);
        _corpseBlacklistedAt.Remove(corpseId);
    }

    private void PruneCorpseCache()
    {
        double expiry = Math.Clamp(
            _settings.CorpseCacheTimeoutMinutes,
            1d,
            1440d) * 60d;
        foreach (uint id in _completedCorpses
            .Where(entry => _lifetime - entry.Value >= expiry)
            .Select(static entry => entry.Key)
            .ToArray())
        {
            _completedCorpses.Remove(id);
            _corpseFirstSeen.Remove(id);
        }
    }

    internal static string KillerName(string description)
    {
        const string prefix = "Killed by ";
        if (string.IsNullOrWhiteSpace(description)
            || !description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        string remainder = description[prefix.Length..];
        int period = remainder.IndexOf('.');
        if (period >= 0)
            remainder = remainder[..period];
        return remainder.Trim();
    }

    private void ResetTransient()
    {
        _activeCorpse = 0u;
        _activeCorpseSawContents = false;
        _waitingItem = 0u;
        _waitingName = string.Empty;
        _waitingAction = LootAction.NoLoot;
        _waitingQuantity = 0;
        _waitingItemSnapshot = default;
        _waitingClassifierId = string.Empty;
        _waitingInventoryRevision = 0L;
        _awaitingAppraisal = 0u;
        _awaitingCorpseAppraisal = 0u;
        _postUseItem = 0u;
        _postUseName = string.Empty;
        _postUseStarted = false;
        _postUseRevision = 0L;
        _salvagePendingItem = 0u;
        _salvagePendingName = string.Empty;
        _salvageAttempts = 0;
        _sellPendingItem = 0u;
        _sellPendingName = string.Empty;
        _manaTransfer = null;
        _manaTransferRevision = 0L;
        _combinePending = null;
        _stateAge = 0d;
    }
}

internal sealed class LootRuleExpression
{
    private readonly Clause[][] _groups;

    private LootRuleExpression(Clause[][] groups) => _groups = groups;

    public static LootRuleExpression Compile(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string normalized = source.Trim();
        if (normalized is "*" || normalized.Equals(
                "DEFAULT",
                StringComparison.OrdinalIgnoreCase))
        {
            return new LootRuleExpression([[]]);
        }

        Clause[][] groups = Split(normalized, "||")
            .Select(group => Split(group, "&&")
                .Select(ParseClause)
                .ToArray())
            .ToArray();
        if (groups.Length == 0 || groups.Any(static group => group.Length == 0))
            throw new FormatException("Loot expression contains an empty condition.");
        return new LootRuleExpression(groups);
    }

    public bool IsMatch(
        in PluginInventoryItem item,
        in PluginItemProperties properties)
    {
        if (_groups.Length == 1 && _groups[0].Length == 0)
            return true;
        foreach (Clause[] group in _groups)
        {
            bool all = true;
            foreach (Clause clause in group)
            {
                if (!clause.IsMatch(item, properties))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                return true;
        }
        return false;
    }

    private static Clause ParseClause(string text)
    {
        foreach (string operation in new[] { ">=", "<=", "!=", "==", "~=", ">", "<" })
        {
            int offset = FindOutsideQuotes(text, operation);
            if (offset < 0)
                continue;
            string field = text[..offset].Trim();
            string expected = Unquote(text[(offset + operation.Length)..].Trim());
            if (field.Length == 0 || expected.Length == 0)
                throw new FormatException($"Invalid loot condition '{text.Trim()}'.");
            return new Clause(field, operation, expected);
        }
        throw new FormatException(
            $"Loot condition '{text.Trim()}' needs a comparison operator.");
    }

    private static string[] Split(string source, string delimiter)
    {
        var result = new List<string>();
        int start = 0;
        char quote = '\0';
        for (int index = 0; index <= source.Length - delimiter.Length; index++)
        {
            char current = source[index];
            if (current is '\'' or '"')
                quote = quote == '\0' ? current : quote == current ? '\0' : quote;
            if (quote != '\0'
                || !source.AsSpan(index).StartsWith(
                    delimiter,
                    StringComparison.Ordinal))
            {
                continue;
            }
            result.Add(source[start..index].Trim());
            start = index + delimiter.Length;
            index += delimiter.Length - 1;
        }
        result.Add(source[start..].Trim());
        return result.ToArray();
    }

    private static int FindOutsideQuotes(string source, string operation)
    {
        char quote = '\0';
        for (int index = 0; index <= source.Length - operation.Length; index++)
        {
            char current = source[index];
            if (current is '\'' or '"')
                quote = quote == '\0' ? current : quote == current ? '\0' : quote;
            if (quote == '\0'
                && source.AsSpan(index).StartsWith(
                    operation,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static string Unquote(string value) => value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"')
            || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private readonly record struct Clause(
        string Field,
        string Operation,
        string Expected)
    {
        public bool IsMatch(
            in PluginInventoryItem item,
            in PluginItemProperties properties)
        {
            Value actual = Resolve(item, properties, Field);
            if (Operation == "~=")
            {
                if (actual.Kind != ValueKind.Text)
                    throw new FormatException("~= is only valid for text fields.");
                return actual.Text.Contains(Expected, StringComparison.OrdinalIgnoreCase);
            }
            int comparison = actual.Kind switch
            {
                ValueKind.Number => actual.Number.CompareTo(ParseNumber(Expected)),
                ValueKind.Boolean => actual.Boolean.CompareTo(ParseBoolean(Expected)),
                _ => string.Compare(
                    actual.Text,
                    Expected,
                    StringComparison.OrdinalIgnoreCase),
            };
            return Operation switch
            {
                "==" => comparison == 0,
                "!=" => comparison != 0,
                ">" => comparison > 0,
                "<" => comparison < 0,
                ">=" => comparison >= 0,
                "<=" => comparison <= 0,
                _ => false,
            };
        }

        private static Value Resolve(
            in PluginInventoryItem item,
            in PluginItemProperties properties,
            string field)
        {
            string key = field.Trim().ToLowerInvariant();
            return key switch
            {
                "name" => Value.FromText(item.Name),
                "wcid" or "typeid" => Value.FromNumber(item.WeenieClassId),
                "itemtype" or "type" => Value.FromNumber(item.ItemType),
                "stack" or "stacksize" => Value.FromNumber(item.StackSize),
                "maxstack" => Value.FromNumber(item.MaximumStackSize),
                "value" => Value.FromNumber(item.Value),
                "burden" => Value.FromNumber(item.Burden),
                "workmanship" => Value.FromNumber(item.Workmanship),
                "material" => Value.FromNumber(item.MaterialType),
                _ => ResolveRaw(key, properties),
            };
        }

        private static Value ResolveRaw(
            string field,
            in PluginItemProperties properties)
        {
            if (!TryRawKey(field, out string table, out uint key))
                throw new FormatException($"Unknown loot field '{field}'.");
            return table switch
            {
                "int" => Value.FromNumber(
                    properties.Ints?.TryGetValue(key, out int value) == true
                        ? value : 0),
                "int64" => Value.FromNumber(
                    properties.Int64s?.TryGetValue(key, out long value) == true
                        ? value : 0),
                "bool" => Value.FromBoolean(
                    properties.Bools?.TryGetValue(key, out bool value) == true
                        && value),
                "float" => Value.FromNumber(
                    properties.Floats?.TryGetValue(key, out double value) == true
                        ? value : 0d),
                "string" => Value.FromText(
                    properties.Strings?.TryGetValue(key, out string? value) == true
                        ? value : string.Empty),
                "did" => Value.FromNumber(
                    properties.DataIds?.TryGetValue(key, out uint value) == true
                        ? value : 0u),
                "iid" => Value.FromNumber(
                    properties.InstanceIds?.TryGetValue(key, out uint value) == true
                        ? value : 0u),
                _ => throw new FormatException($"Unknown raw table '{table}'."),
            };
        }

        private static bool TryRawKey(
            string field,
            out string table,
            out uint key)
        {
            int open = field.IndexOf('[', StringComparison.Ordinal);
            int close = field.LastIndexOf(']');
            table = open > 0 ? field[..open] : string.Empty;
            key = 0u;
            return open > 0 && close == field.Length - 1
                && uint.TryParse(
                    field.AsSpan(open + 1, close - open - 1),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out key);
        }

        private static double ParseNumber(string value) =>
            double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed)
                ? parsed
                : throw new FormatException($"'{value}' is not a number.");

        private static bool ParseBoolean(string value) =>
            bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new FormatException($"'{value}' is not true or false.");
    }

    private enum ValueKind
    {
        Number,
        Text,
        Boolean,
    }

    private readonly record struct Value(
        ValueKind Kind,
        double Number,
        string Text,
        bool Boolean)
    {
        public static Value FromNumber(double value) =>
            new(ValueKind.Number, value, string.Empty, false);
        public static Value FromText(string value) =>
            new(ValueKind.Text, 0d, value ?? string.Empty, false);
        public static Value FromBoolean(bool value) =>
            new(ValueKind.Boolean, 0d, string.Empty, value);
    }
}
