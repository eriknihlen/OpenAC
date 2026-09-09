using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class InventorySettings
{
    public bool ManaChargesWhenOff { get; set; } = true;
    // Official VTank defaults from uTank2.Resources.defaultsettings.usd.
    public bool AutoStack { get; set; } = true;
    public bool AutoCram { get; set; }
    public bool AutoCraftItems { get; set; } = true;
    public int ArrowheadFletchDifficultyExcess { get; set; } = 10;
    public bool SplitPeas { get; set; } = true;
    public int CriticalComponentMinimum { get; set; } = 4;
    public int NormalComponentMinimum { get; set; } = 20;
    public int IdleComponentMinimum { get; set; } = 20;
    public int IdleHealthKitCount { get; set; } = 2;
    public int IdleStaminaKitCount { get; set; } = 2;
    public int IdleManaKitCount { get; set; } = 2;
    public int IdleHealthFoodCount { get; set; } = 15;
    public int IdleStaminaFoodCount { get; set; } = 15;
    public int IdleManaFoodCount { get; set; } = 15;
    public bool RefillWornMana { get; set; } = true;
    public int RefillWornManaPercent { get; set; } = 33;
    public double ScanIntervalSeconds { get; set; } = 0.25d;
    public LootSettings Loot { get; } = new();
}

internal enum InventoryMaintenanceKind
{
    Merge,
    Cram,
}

internal readonly record struct InventoryMaintenancePlan(
    InventoryMaintenanceKind Kind,
    uint SourceObjectId,
    uint TargetObjectId,
    uint Amount);

internal static class InventoryMaintenancePlanner
{
    private const uint PublicWeenieFoci = 0x00800000u;
    private static readonly ISet<uint> EmptyIgnored = new HashSet<uint>();

    public static InventoryMaintenancePlan? Plan(
        IReadOnlyList<PluginInventoryItem> items,
        uint playerObjectId,
        InventorySettings settings,
        ISet<uint>? ignored = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(settings);
        ignored ??= EmptyIgnored;

        if (settings.AutoStack)
        {
            InventoryMaintenancePlan? stack = PlanStack(items, ignored);
            if (stack is not null)
                return stack;
        }

        return settings.AutoCram
            ? PlanCram(items, playerObjectId, ignored)
            : null;
    }

    private static InventoryMaintenancePlan? PlanStack(
        IReadOnlyList<PluginInventoryItem> items,
        ISet<uint> ignored)
    {
        Dictionary<uint, PluginInventoryItem> byId = items.ToDictionary(
            static item => item.ObjectId);
        foreach (IGrouping<uint, PluginInventoryItem> group in items
            .Where(item => item.ObjectId != 0u
                && item.WeenieClassId != 0u
                && item.MaximumStackSize > 1
                && item.StackSize > 0
                && !item.IsEquipped
                && !ignored.Contains(item.ObjectId))
            .GroupBy(static item => item.WeenieClassId)
            .OrderBy(static group => group.Key))
        {
            PluginInventoryItem[] ordered = group
                .OrderBy(item => BurdenRank(item, byId))
                .ThenBy(static item => item.ContainerSlot)
                .ThenBy(static item => item.ObjectId)
                .ToArray();
            if (ordered.Length < 2)
                continue;

            PluginInventoryItem source = ordered[0];
            for (int i = ordered.Length - 1; i >= 1; i--)
            {
                PluginInventoryItem target = ordered[i];
                int free = target.MaximumStackSize - Math.Max(1, target.StackSize);
                if (free <= 0)
                    continue;
                uint amount = (uint)Math.Min(Math.Max(1, source.StackSize), free);
                return new InventoryMaintenancePlan(
                    InventoryMaintenanceKind.Merge,
                    source.ObjectId,
                    target.ObjectId,
                    amount);
            }
        }
        return null;
    }

    private static InventoryMaintenancePlan? PlanCram(
        IReadOnlyList<PluginInventoryItem> items,
        uint playerObjectId,
        ISet<uint> ignored)
    {
        if (playerObjectId == 0u)
            return null;

        PluginInventoryItem source = items
            .Where(item => item.ContainerObjectId == playerObjectId
                && item.WielderObjectId == 0u
                && item.ItemsCapacity <= 0
                && item.ContainersCapacity <= 0
                && (item.PublicFlags & PublicWeenieFoci) == 0u
                && !ignored.Contains(item.ObjectId))
            .OrderBy(static item => item.ContainerSlot)
            .ThenBy(static item => item.ObjectId)
            .FirstOrDefault();
        if (source.ObjectId == 0u)
            return null;

        Dictionary<uint, int> containedCounts = items
            .Where(static item => item.ContainerObjectId != 0u)
            .GroupBy(static item => item.ContainerObjectId)
            .ToDictionary(static group => group.Key, static group => group.Count());
        PluginInventoryItem destination = items
            .Where(item => item.ContainerObjectId == playerObjectId
                && item.ItemsCapacity > 0
                && !ignored.Contains(item.ObjectId)
                && containedCounts.GetValueOrDefault(item.ObjectId)
                    < item.ItemsCapacity)
            .OrderBy(static item => item.ContainerSlot)
            .ThenBy(static item => item.ObjectId)
            .FirstOrDefault();
        return destination.ObjectId != 0u
            ? new InventoryMaintenancePlan(
                InventoryMaintenanceKind.Cram,
                source.ObjectId,
                destination.ObjectId,
                (uint)Math.Max(1, source.StackSize))
            : null;
    }

    private static long BurdenRank(
        PluginInventoryItem item,
        IReadOnlyDictionary<uint, PluginInventoryItem> byId)
    {
        long parent = item.ContainerObjectId != 0u
            && byId.TryGetValue(item.ContainerObjectId, out PluginInventoryItem container)
                ? Math.Max(0, container.Burden) + 1L
                : 0L;
        return Math.Max(0, item.Burden) + (10_000L * parent);
    }

}

/// <summary>
/// Executes one StackCram operation at a time and waits for the host's
/// authoritative inventory receipt before planning the next one.
/// </summary>
internal sealed class InventoryMaintenanceController
{
    private const int RetailAbandonAttempts = 80;
    private readonly IPluginHost _host;
    private readonly InventorySettings _settings;
    private readonly Dictionary<(uint Source, uint Target), int> _attempts = [];
    private readonly HashSet<uint> _ignored = [];
    private InventoryMaintenancePlan? _pending;
    private long _observedRevision;
    private double _untilScan;

    public InventoryMaintenanceController(
        IPluginHost host,
        InventorySettings settings)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public string Status { get; private set; } = "Stack/Cram idle";

    /// <summary>Returns true only when StackCram owns this scheduler tick.</summary>
    public bool Tick(double elapsedSeconds, bool canAct)
    {
        IItemAutomation commands = _host.Automation.Items;
        ObserveCompletion(commands);
        if (_pending is not null)
        {
            if (commands.IsBusy)
                return true;
            _pending = null;
        }
        if (!canAct || !_host.Automation.IsAvailable || !commands.IsAvailable)
            return false;
        if (!_settings.AutoStack && !_settings.AutoCram)
        {
            Status = "Stack/Cram disabled";
            return false;
        }
        if (commands.IsBusy)
            return false;

        _untilScan -= Math.Max(0d, elapsedSeconds);
        if (_untilScan > 0d)
            return false;
        _untilScan = Math.Max(0.05d, _settings.ScanIntervalSeconds);

        IReadOnlyList<PluginInventoryItem> inventory = commands.CaptureOwnedItems();
        _ignored.RemoveWhere(id => !inventory.Any(item => item.ObjectId == id));
        InventoryMaintenancePlan? plan = InventoryMaintenancePlanner.Plan(
            inventory,
            _host.Automation.Character.ObjectId,
            _settings,
            _ignored);
        if (plan is not { } next)
        {
            Status = "Stack/Cram idle";
            return false;
        }

        PluginItemCommandResult result = next.Kind == InventoryMaintenanceKind.Merge
            ? commands.Merge(next.SourceObjectId, next.TargetObjectId, next.Amount)
            : commands.MoveToContainer(
                next.SourceObjectId,
                next.TargetObjectId,
                next.Amount);
        if (!result.Accepted)
        {
            Status = $"Stack/Cram waiting: {result.Status}";
            return result.Status == PluginItemCommandStatus.Busy;
        }

        _pending = next;
        Status = next.Kind == InventoryMaintenanceKind.Merge
            ? "Stacking items"
            : "Moving an item to a side pack";
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _attempts.Clear();
        _ignored.Clear();
        _untilScan = 0d;
        Status = "Stack/Cram idle";
    }

    private void ObserveCompletion(IItemAutomation commands)
    {
        PluginInventoryCompletion completion = commands.LastInventoryCompletion;
        if (completion.Revision == 0 || completion.Revision == _observedRevision)
            return;
        _observedRevision = completion.Revision;
        if (_pending is not { } pending
            || completion.SourceObjectId != pending.SourceObjectId)
        {
            return;
        }

        if (!completion.IsSuccess)
        {
            var key = (pending.SourceObjectId, pending.TargetObjectId);
            int attempts = _attempts.GetValueOrDefault(key) + 1;
            _attempts[key] = attempts;
            Status = $"Stack/Cram failed (0x{completion.WeenieError:X})";
            if (attempts > RetailAbandonAttempts)
            {
                _ignored.Add(pending.SourceObjectId);
                _ignored.Add(pending.TargetObjectId);
                _host.Automation.Chat.PostSystemMessage(
                    "[MossTank] Abandoned trying to stack/cram two bugged items.");
            }
        }
        else
        {
            _attempts.Remove((pending.SourceObjectId, pending.TargetObjectId));
        }
        _pending = null;
        _untilScan = 0d;
    }
}
