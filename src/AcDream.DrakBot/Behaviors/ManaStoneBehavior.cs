using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Keeps worn items charged with mana stones, the way RynthAi's mana stone
/// manager does. Each idle step picks one of two moves: a worn item under
/// a quarter of its mana and a charged stone in the pack - use the stone
/// on the character; else an empty stone and an unworn item in the pack
/// carrying at least the tap threshold of mana - drain it into the stone
/// (the item is destroyed, the stone charged). A stone used on the
/// character may be consumed too; another is looted later. Wands are
/// never drained.
/// </summary>
public sealed class ManaStoneBehavior(Func<ManaStoneSettings> settings) : IBehavior
{
    private const double WornRefillFraction = 0.25;
    private const double ActionSettleSeconds = 8d;
    private const double UseOnSelfCooldownSeconds = 300d;
    private const double ThinkIntervalSeconds = 0.6d;

    private readonly Dictionary<uint, double> _cooldownUntil = new();
    private double _busyUntil = double.NegativeInfinity;
    private double _lastThinkAt = double.NegativeInfinity;
    private double _refillCooldownUntil = double.NegativeInfinity;
    private uint _stoneId;
    private uint _itemId;
    private string _reason = string.Empty;

    public string Name => "manastones";

    public BehaviorPriority Priority => BehaviorPriority.Buffing;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        if (!settings().Enabled)
            return false;
        if (board.Now < _busyUntil)
        {
            reason = _reason;
            return true;
        }
        if (board.IsActionPending || board.Now - _lastThinkAt < ThinkIntervalSeconds)
            return false;
        // Hostiles in range: stones can wait.
        if (board.Hostiles.Count > 0)
            return false;
        reason = "mana stones";
        return true;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        ManaStoneSettings stones = settings();
        IAutomationSurface surface = context.Surface;
        if (board.Now < _busyUntil)
            return BehaviorStep.Continue;

        if (_stoneId != 0u)
        {
            PluginItemCommandResult result = surface.Items.Apply(_stoneId, _itemId);
            if (result.Status == PluginItemCommandStatus.Busy)
                return BehaviorStep.Continue;
            uint stone = _stoneId;
            uint item = _itemId;
            _stoneId = 0u;
            _itemId = 0u;
            if (!result.Accepted)
            {
                _cooldownUntil[stone] = board.Now + 30d;
                return BehaviorStep.Fail($"mana stone use {result.Status}");
            }
            context.Log.Info(item == board.SelfId ? "recharging worn items from a mana stone" : "draining an item into a mana stone");
            _busyUntil = board.Now + ActionSettleSeconds;
            if (item == board.SelfId)
                _refillCooldownUntil = board.Now + UseOnSelfCooldownSeconds;
            else
                _cooldownUntil[item] = board.Now + 300d;
            return BehaviorStep.Continue;
        }

        Plan(surface, board, stones);
        return _stoneId != 0u ? BehaviorStep.Continue : BehaviorStep.Done;
    }

    public void Interrupt(BehaviorContext context)
    {
        _stoneId = 0u;
        _itemId = 0u;
    }

    /// <summary>What the behavior is about to do, for the dashboard.</summary>
    public string Status => _reason;

    /// <summary>Looks at the pack and picks the next move, if any.</summary>
    private void Plan(IAutomationSurface surface, Blackboard board, ManaStoneSettings stones)
    {
        _lastThinkAt = board.Now;
        _stoneId = 0u;
        _itemId = 0u;
        _reason = string.Empty;
        IReadOnlyList<PluginInventoryItem> owned = surface.Items.CaptureOwnedItems();

        bool wornNeedsMana = false;
        foreach (PluginInventoryItem item in owned)
        {
            if (item.IsEquipped && item.ItemMaximumMana > 0 && item.ItemCurrentMana < item.ItemMaximumMana * WornRefillFraction)
            {
                wornNeedsMana = true;
                break;
            }
        }
        if (wornNeedsMana && board.Now >= _refillCooldownUntil)
        {
            foreach (PluginInventoryItem stone in owned)
            {
                if (!IsStone(stone, stones) || stone.ItemCurrentMana <= 0 || OnCooldown(stone.ObjectId, board.Now))
                    continue;
                _stoneId = stone.ObjectId;
                _itemId = board.SelfId;
                _reason = "worn items need mana";
                return;
            }
        }

        if (stones.TapThresholdMana <= 0)
            return;
        PluginInventoryItem? empty = null;
        foreach (PluginInventoryItem stone in owned)
        {
            if (IsStone(stone, stones) && stone.ItemCurrentMana <= 0 && !OnCooldown(stone.ObjectId, board.Now))
            {
                empty = stone;
                break;
            }
        }
        if (empty is null)
            return;
        foreach (PluginInventoryItem item in owned)
        {
            if (item.IsEquipped || IsStone(item, stones) || item.ObjectClass == PluginObjectClass.WandStaffOrb)
                continue;
            if (item.ItemCurrentMana < stones.TapThresholdMana || OnCooldown(item.ObjectId, board.Now))
                continue;
            _stoneId = empty.Value.ObjectId;
            _itemId = item.ObjectId;
            _reason = $"drain {item.Name}";
            return;
        }
    }

    private bool OnCooldown(uint objectId, double now) =>
        _cooldownUntil.TryGetValue(objectId, out double until) && now < until;

    private static bool IsStone(in PluginInventoryItem item, ManaStoneSettings stones)
    {
        if (item.ObjectClass != PluginObjectClass.ManaStone)
            return false;
        if (stones.StoneNames.Count == 0)
            return true;
        foreach (string name in stones.StoneNames)
        {
            if (item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
