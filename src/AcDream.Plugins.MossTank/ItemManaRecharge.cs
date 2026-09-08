using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal readonly record struct ItemManaRechargePlan(
    uint ChargeObjectId,
    uint TargetObjectId,
    string ChargeName,
    string TargetName,
    int CurrentMana,
    int MaximumMana);

internal static class ItemManaRechargePlanner
{
    private const uint ManaStoneItemType = 0x00080000u;

    public static ItemManaRechargePlan? Plan(
        IReadOnlyList<PluginInventoryItem> inventory,
        ISet<string> consumableNames,
        int thresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(consumableNames);
        int threshold = Math.Clamp(thresholdPercent, 0, 99);
        PluginInventoryItem charge = inventory
            .Where(item => (item.ItemType & ManaStoneItemType) != 0u
                && consumableNames.Contains(item.Name)
                && !item.IsEquipped)
            .Where(static item => item.ItemCurrentMana > 0)
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ThenBy(static item => item.ObjectId)
            .FirstOrDefault();
        if (charge.ObjectId == 0u)
            return null;

        PluginInventoryItem target = inventory
            .Where(item => item.IsEquipped
                && item.ItemMaximumMana > 0
                && 100L * Math.Max(0, item.ItemCurrentMana)
                    / item.ItemMaximumMana < threshold)
            .OrderBy(item => 100d * Math.Max(0, item.ItemCurrentMana)
                / item.ItemMaximumMana)
            .ThenBy(static item => item.ObjectId)
            .FirstOrDefault();
        return target.ObjectId == 0u
            ? null
            : new ItemManaRechargePlan(
                charge.ObjectId,
                target.ObjectId,
                charge.Name,
                target.Name,
                target.ItemCurrentMana,
                target.ItemMaximumMana);
    }
}

internal sealed class ItemManaRechargeController
{
    private readonly IPluginHost _host;
    private readonly InventorySettings _settings;
    private readonly CombatSettings _profiles;
    private ItemManaRechargePlan? _pending;
    private long _observedCompletion;

    public ItemManaRechargeController(
        IPluginHost host,
        InventorySettings settings,
        CombatSettings profiles)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public string Status { get; private set; } = "Worn mana ready";

    public bool Tick(bool canAct)
    {
        IItemAutomation items = _host.Automation.Items;
        ObserveCompletion(items);
        if (_pending is not null)
        {
            if (items.IsBusy)
                return true;
            _pending = null;
        }
        if (!canAct
            || !_settings.RefillWornMana
            || !_host.Automation.IsAvailable
            || !items.IsAvailable
            || items.IsBusy)
        {
            return false;
        }

        ItemManaRechargePlan? plan = ItemManaRechargePlanner.Plan(
            items.CaptureOwnedItems(),
            _profiles.ConsumableNames,
            _settings.RefillWornManaPercent);
        if (plan is not { } next)
        {
            Status = "Worn mana ready";
            return false;
        }
        PluginItemCommandResult result = items.Apply(
            next.ChargeObjectId,
            next.TargetObjectId);
        if (!result.Accepted)
        {
            Status = $"Mana refill waiting: {result.Status}";
            return result.Status == PluginItemCommandStatus.Busy;
        }
        _pending = next;
        Status = $"Refilling {next.TargetName} ({next.CurrentMana}/{next.MaximumMana})";
        return true;
    }

    public void Reset()
    {
        _pending = null;
        Status = "Worn mana ready";
    }

    private void ObserveCompletion(IItemAutomation items)
    {
        PluginItemUseCompletion completion = items.LastCompletion;
        if (completion.Revision == 0 || completion.Revision == _observedCompletion)
            return;
        _observedCompletion = completion.Revision;
        if (_pending is not { } pending
            || completion.SourceObjectId != pending.ChargeObjectId)
        {
            return;
        }
        Status = completion.IsSuccess
            ? $"Refilled {pending.TargetName}"
            : $"Mana refill failed (0x{completion.WeenieError:X})";
        _pending = null;
    }
}
