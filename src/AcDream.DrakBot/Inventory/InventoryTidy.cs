using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Inventory;

/// <summary>
/// Keeps the pack tidy beside the behaviors, the way RynthAi's inventory
/// manager does: cram moves loose items out of the main pack into a side
/// pack with room, so new loot always has a slot; stack merges two partial
/// stacks of the same thing. One move per half second, none while an
/// action is pending or a corpse is open. A move is confirmed on a later
/// pack snapshot; one that never lands is backed off, doubling up to five
/// minutes, so an item the server will not move is not retried forever.
/// </summary>
public sealed class InventoryTidy(Func<InventorySettings> settings)
{
    private const double ActionIntervalSeconds = 0.5d;
    private const double IdleRescanSeconds = 2d;
    private const double ConfirmGraceSeconds = 10d;
    private const double BackoffBaseSeconds = 5d;
    private const double BackoffMaxSeconds = 300d;

    private sealed class Attempt
    {
        public double IssuedAt;
        public bool Confirmed;
        public int Failures;
        public double RetryAt;
        public double TouchedAt;
    }

    private readonly Dictionary<string, Attempt> _attempts = new();
    private double _nextActionAt = double.NegativeInfinity;
    private double _lastPruneAt = double.NegativeInfinity;

    public string Status { get; private set; } = string.Empty;

    /// <summary>Runs one tidy step when nothing else is using the hands.</summary>
    public void Tick(IAutomationSurface surface, Blackboard board)
    {
        InventorySettings tidy = settings();
        if (!tidy.AutoCram && !tidy.AutoStack)
            return;
        if (board.Now < _nextActionAt || board.IsActionPending || board.LootBusy || board.OpenContainerId != 0u)
            return;
        if (board.Now - _lastPruneAt >= 60d)
        {
            Prune(board.Now);
            _lastPruneAt = board.Now;
        }
        IReadOnlyList<PluginInventoryItem> owned = surface.Items.CaptureOwnedItems();
        if (owned.Count == 0)
            return;
        // Cram before stack: a free slot in the main pack matters more than
        // a tidy pile of partials, and the two can otherwise starve each other.
        if ((tidy.AutoCram && Cram(surface, board, owned)) || (tidy.AutoStack && Stack(surface, board, owned)))
        {
            _nextActionAt = board.Now + ActionIntervalSeconds;
            return;
        }
        Status = string.Empty;
        _nextActionAt = board.Now + IdleRescanSeconds;
    }

    private bool Cram(IAutomationSurface surface, Blackboard board, IReadOnlyList<PluginInventoryItem> owned)
    {
        foreach (PluginInventoryItem item in owned)
        {
            if (!IsLooseInMainPack(item, board.SelfId))
                continue;
            string key = $"cram:{item.ObjectId}";
            if (!Ready(key, board.Now))
                continue;
            uint pack = SidePackWithRoom(owned, board.SelfId);
            if (pack == 0u)
                return false; // No side pack with room: nothing to cram anywhere this pass.
            PluginItemCommandResult result = surface.Items.MoveToContainer(item.ObjectId, pack, (uint)Math.Max(1, item.StackSize));
            if (result.Status == PluginItemCommandStatus.Busy)
                return false;
            Status = $"cramming {item.Name}";
            Record(key, board.Now, result.Accepted);
            return true;
        }
        return false;
    }

    private bool Stack(IAutomationSurface surface, Blackboard board, IReadOnlyList<PluginInventoryItem> owned)
    {
        var groups = new Dictionary<string, List<PluginInventoryItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginInventoryItem item in owned)
        {
            if (item.IsEquipped || item.ObjectClass == PluginObjectClass.Container)
                continue;
            int max = MaximumStack(item);
            if (max <= 1 || item.StackSize <= 0 || item.StackSize >= max)
                continue;
            if (!groups.TryGetValue(item.Name, out List<PluginInventoryItem>? group))
                groups[item.Name] = group = [];
            group.Add(item);
        }
        foreach (List<PluginInventoryItem> group in groups.Values)
        {
            if (group.Count < 2)
                continue;
            // The smallest partial goes onto the largest.
            group.Sort((a, b) => b.StackSize.CompareTo(a.StackSize));
            for (int t = 0; t < group.Count; t++)
            {
                for (int s = group.Count - 1; s > t; s--)
                {
                    string key = $"stack:{group[s].ObjectId}>{group[t].ObjectId}";
                    if (!Ready(key, board.Now))
                        continue;
                    PluginItemCommandResult result = surface.Items.Merge(group[s].ObjectId, group[t].ObjectId);
                    if (result.Status == PluginItemCommandStatus.Busy)
                        return false;
                    Status = $"stacking {group[s].Name}";
                    Record(key, board.Now, result.Accepted);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a move may be issued under this key: not in flight, not backed
    /// off. An attempt whose item is still there on a later snapshot past
    /// the grace window is a confirmed failure and starts the backoff.
    /// </summary>
    private bool Ready(string key, double now)
    {
        if (!_attempts.TryGetValue(key, out Attempt? attempt))
            return true;
        attempt.TouchedAt = now;
        if (!attempt.Confirmed)
        {
            if (now - attempt.IssuedAt < ConfirmGraceSeconds)
                return false;
            Fail(attempt, now);
        }
        return now >= attempt.RetryAt;
    }

    private void Record(string key, double now, bool accepted)
    {
        if (!_attempts.TryGetValue(key, out Attempt? attempt))
            _attempts[key] = attempt = new Attempt();
        attempt.IssuedAt = now;
        attempt.TouchedAt = now;
        attempt.Confirmed = false;
        if (!accepted)
            Fail(attempt, now);
    }

    private static void Fail(Attempt attempt, double now)
    {
        attempt.Confirmed = true;
        attempt.Failures++;
        double delay = Math.Min(BackoffMaxSeconds, BackoffBaseSeconds * (1 << Math.Min(6, attempt.Failures - 1)));
        attempt.RetryAt = now + delay;
    }

    private void Prune(double now)
    {
        List<string>? dead = null;
        foreach ((string key, Attempt attempt) in _attempts)
        {
            if (now - attempt.TouchedAt > 600d)
                (dead ??= []).Add(key);
        }
        if (dead is null)
            return;
        foreach (string key in dead)
            _attempts.Remove(key);
    }

    private static bool IsLooseInMainPack(in PluginInventoryItem item, uint selfId)
    {
        if (item.ContainerObjectId != selfId || item.IsEquipped)
            return false;
        if (item.ObjectClass is PluginObjectClass.Container or PluginObjectClass.Foci)
            return false;
        return item.ItemsCapacity == 0 && item.ContainersCapacity == 0;
    }

    /// <summary>A side pack in the main pack with two free slots, fullest first so packs fill in order.</summary>
    private static uint SidePackWithRoom(IReadOnlyList<PluginInventoryItem> owned, uint selfId)
    {
        uint best = 0u;
        int bestFree = int.MaxValue;
        foreach (PluginInventoryItem pack in owned)
        {
            if (pack.ContainerObjectId != selfId || pack.ItemsCapacity <= 0 || pack.IsEquipped)
                continue;
            int used = 0;
            foreach (PluginInventoryItem item in owned)
            {
                if (item.ContainerObjectId == pack.ObjectId)
                    used++;
            }
            int free = pack.ItemsCapacity - used;
            if (free >= 2 && free < bestFree)
            {
                bestFree = free;
                best = pack.ObjectId;
            }
        }
        return best;
    }

    /// <summary>The stack ceiling, from the item when known, else by name for the common stackables.</summary>
    public static int MaximumStack(in PluginInventoryItem item)
    {
        if (item.MaximumStackSize > 1)
            return item.MaximumStackSize;
        string name = item.Name;
        if (name.Contains("Pyreal", StringComparison.OrdinalIgnoreCase))
            return 25000;
        if (name.Contains("Trade Note", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Taper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Scarab", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Prismatic", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pea", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Grain", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        if (name.Contains("Arrow", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bolt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quarrel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Atlatl Dart", StringComparison.OrdinalIgnoreCase))
        {
            return 250;
        }
        return 1;
    }
}
