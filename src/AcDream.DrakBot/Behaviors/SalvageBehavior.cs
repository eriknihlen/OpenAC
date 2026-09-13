using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Salvages what the looter picked up under a salvage rule, and merges
/// under-full salvage bags, the way RynthAi's salvage manager does. The
/// looter queues each item once its pickup lands; when the coast is clear
/// the queued items still in the pack go into one salvage request with the
/// Ust. Every half minute the bags are grouped by material and workmanship
/// band (the loot profile's SalvageCombine block, else 1-6, 7-8, 9, 10) and
/// each group with more than one under-full bag is salvaged together, which
/// the server answers by merging them. A request whose items are all still
/// there after the wait is retried a few times, then given up on.
/// </summary>
public sealed class SalvageBehavior(
    Func<SalvageSettings> settings,
    Func<SalvageCombineSettings?>? combineRules = null,
    Func<IReadOnlyList<PluginInventoryItem>>? pack = null) : IBehavior
{
    private const double ResultWaitSeconds = 6d;
    private const double CombineSweepSeconds = 30d;
    private const double RetryBackoffSeconds = 2d;
    private const int MaxAttempts = 3;
    private const int FullBag = 100;

    private readonly Queue<uint> _queue = new();
    private readonly Dictionary<uint, int> _attempts = new();
    private readonly List<uint> _inFlight = [];
    private double _issuedAt = double.NegativeInfinity;
    private double _retryAt = double.NegativeInfinity;
    private double _lastSweepAt = double.NegativeInfinity;
    private bool _combining;

    public string Name => "salvage";

    public BehaviorPriority Priority => BehaviorPriority.Salvage;

    public int Queued => _queue.Count;

    public bool IsBusy => _inFlight.Count > 0;

    /// <summary>Marks an item the looter picked up for salvage.</summary>
    public void Enqueue(uint objectId)
    {
        if (objectId != 0u && !_queue.Contains(objectId))
            _queue.Enqueue(objectId);
    }

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        SalvageSettings salvage = settings();
        if (!salvage.Enabled)
            return false;
        if (_inFlight.Count > 0)
        {
            reason = _combining ? "merging salvage bags" : "salvaging";
            return true;
        }
        if (board.Hostiles.Count > 0 || board.IsActionPending || board.LootBusy || board.Now < _retryAt)
            return false;
        if (_queue.Count > 0)
        {
            reason = $"{_queue.Count} to salvage";
            return true;
        }
        if (salvage.CombineBags && board.Now - _lastSweepAt >= CombineSweepSeconds)
        {
            // The pack is not on the blackboard; when it can be seen from
            // here, a sweep with nothing to merge does not take control.
            _lastSweepAt = board.Now;
            if (pack is not null && NextBagGroup(pack()) is null)
                return false;
            reason = "salvage bag sweep";
            return true;
        }
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        IAutomationSurface surface = context.Surface;
        IReadOnlyList<PluginInventoryItem> owned = surface.Items.CaptureOwnedItems();

        if (_inFlight.Count > 0)
            return Settle(context, owned);

        uint ust = FindUst(owned);
        if (ust == 0u)
        {
            _lastSweepAt = board.Now;
            if (_queue.Count == 0)
                return BehaviorStep.Done;
            _queue.Clear();
            return BehaviorStep.Fail("no Ust in the pack");
        }

        if (_queue.Count > 0)
        {
            List<uint> batch = [];
            while (_queue.Count > 0)
            {
                uint id = _queue.Dequeue();
                if (Owned(owned, id) is { IsEquipped: false })
                    batch.Add(id);
            }
            if (batch.Count == 0)
                return BehaviorStep.Done;
            _combining = false;
            return Issue(context, ust, batch);
        }

        _lastSweepAt = board.Now;
        List<uint>? group = NextBagGroup(owned);
        if (group is null)
            return BehaviorStep.Done;
        _combining = true;
        context.Log.Info($"merging {group.Count} salvage bags");
        return Issue(context, ust, group);
    }

    public void Interrupt(BehaviorContext context)
    {
    }

    private BehaviorStep Issue(BehaviorContext context, uint ust, List<uint> items)
    {
        PluginItemCommandResult result = context.Surface.Items.Salvage(ust, items);
        if (result.Status == PluginItemCommandStatus.Busy)
        {
            Requeue(items);
            _retryAt = context.Board.Now + RetryBackoffSeconds;
            return BehaviorStep.Continue;
        }
        if (!result.Accepted)
        {
            foreach (uint id in items)
                Retry(id);
            _retryAt = context.Board.Now + RetryBackoffSeconds;
            return BehaviorStep.Fail($"salvage {result.Status}");
        }
        if (!_combining)
            context.Log.Info($"salvaging {items.Count} item(s)");
        _inFlight.AddRange(items);
        _issuedAt = context.Board.Now;
        return BehaviorStep.Continue;
    }

    /// <summary>Waits for the salvaged items to leave the pack; what is still there after the wait is retried.</summary>
    private BehaviorStep Settle(BehaviorContext context, IReadOnlyList<PluginInventoryItem> owned)
    {
        int remaining = 0;
        foreach (uint id in _inFlight)
        {
            if (Owned(owned, id) is not null)
                remaining++;
        }
        bool done = _combining ? remaining < _inFlight.Count : remaining == 0;
        if (!done && context.Board.Now - _issuedAt < ResultWaitSeconds)
            return BehaviorStep.Continue;
        if (!done)
        {
            context.Log.Warn(_combining ? "salvage bags did not merge" : $"{remaining} item(s) were not salvaged");
            if (!_combining)
            {
                foreach (uint id in _inFlight)
                {
                    if (Owned(owned, id) is not null)
                        Retry(id);
                }
            }
            _retryAt = context.Board.Now + RetryBackoffSeconds;
        }
        foreach (uint id in _inFlight)
        {
            if (Owned(owned, id) is null)
                _attempts.Remove(id);
        }
        _inFlight.Clear();
        _combining = false;
        return done ? BehaviorStep.Done : BehaviorStep.Fail("salvage did not complete");
    }

    private void Requeue(List<uint> items)
    {
        foreach (uint id in items)
        {
            if (!_queue.Contains(id))
                _queue.Enqueue(id);
        }
    }

    private void Retry(uint id)
    {
        int attempts = _attempts.TryGetValue(id, out int n) ? n + 1 : 1;
        _attempts[id] = attempts;
        if (attempts < MaxAttempts && !_queue.Contains(id))
            _queue.Enqueue(id);
    }

    /// <summary>The first material/band group with two or more under-full bags.</summary>
    private List<uint>? NextBagGroup(IReadOnlyList<PluginInventoryItem> owned)
    {
        SalvageCombineSettings? rules = combineRules?.Invoke();
        if (rules is { Enabled: false })
            return null;
        var groups = new Dictionary<string, List<uint>>();
        foreach (PluginInventoryItem item in owned)
        {
            if (item.ObjectClass != PluginObjectClass.Salvage || item.IsEquipped)
                continue;
            int max = item.MaximumStructure > 0 ? item.MaximumStructure : FullBag;
            if (item.Structure >= max)
                continue;
            int workmanship = Math.Max(1, (int)Math.Round(item.Workmanship));
            string? band = rules is null
                ? DefaultBand(workmanship)
                : rules.GetBandKey((int)item.MaterialType, workmanship);
            if (band is null)
                continue;
            string key = $"{item.MaterialType}/{band}";
            if (!groups.TryGetValue(key, out List<uint>? group))
                groups[key] = group = [];
            group.Add(item.ObjectId);
        }
        foreach (List<uint> group in groups.Values)
        {
            if (group.Count > 1)
                return group;
        }
        return null;
    }

    private static string? DefaultBand(int workmanship) => workmanship switch
    {
        <= 6 => "1-6",
        7 or 8 => "7-8",
        9 => "9",
        10 => "10",
        _ => null,
    };

    private static uint FindUst(IReadOnlyList<PluginInventoryItem> owned)
    {
        foreach (PluginInventoryItem item in owned)
        {
            if (item.ObjectClass == PluginObjectClass.Ust || item.Name.Equals("Ust", StringComparison.OrdinalIgnoreCase))
                return item.ObjectId;
        }
        return 0u;
    }

    private static PluginInventoryItem? Owned(IReadOnlyList<PluginInventoryItem> owned, uint id)
    {
        foreach (PluginInventoryItem item in owned)
        {
            if (item.ObjectId == id)
                return item;
        }
        return null;
    }
}
