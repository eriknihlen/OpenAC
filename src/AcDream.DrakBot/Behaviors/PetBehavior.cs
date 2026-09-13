using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Keeps a combat pet up while there is something to fight, the way
/// RynthAi's pet manager does. Pets come from essence devices: using one
/// with no target summons a pet named "&lt;character&gt;'s ..." that fights
/// for a while and despawns, and each use spends a charge (the device's
/// structure); an empty device is refilled by applying an Encapsulated
/// Spirit to it. One pet at a time, summoned only when at least the
/// configured number of hostiles is within range, and re-summoned when
/// the pet goes.
/// </summary>
public sealed class PetBehavior(Func<PetSettings> settings) : IBehavior
{
    private const double UseSettleSeconds = 8d;
    private const double AssumeActiveSeconds = 20d;
    private const double DeviceParkSeconds = 30d;

    private readonly Dictionary<uint, double> _parkedUntil = new();
    private double _busyUntil = double.NegativeInfinity;
    private double _assumeActiveUntil = double.NegativeInfinity;

    public string Name => "pets";

    public BehaviorPriority Priority => BehaviorPriority.Buffing;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        PetSettings pets = settings();
        if (!pets.Enabled || pets.Devices.Count == 0)
            return false;
        if (board.Now < _busyUntil)
        {
            reason = "summoning";
            return true;
        }
        if (board.Now < _assumeActiveUntil || board.IsActionPending)
            return false;
        int crowd = 0;
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (!(hostile.IsHealthKnown && hostile.HealthFraction <= 0f) && hostile.Distance <= pets.RangeMeters)
                crowd++;
        }
        if (crowd < Math.Max(1, pets.MinimumHostiles))
            return false;
        reason = $"{crowd} hostiles, summoning";
        return true;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        PetSettings pets = settings();
        IAutomationSurface surface = context.Surface;
        if (board.Now < _busyUntil)
            return BehaviorStep.Continue;

        if (IsPetActive(surface, board.SelfId))
        {
            _assumeActiveUntil = board.Now + AssumeActiveSeconds;
            return BehaviorStep.Done;
        }
        // Summoning must be trained to use an essence; the skill id is the game's (54).
        bool trained = surface.Character.SummoningMastery > 0
            || (surface.Character.TryGetSkill(54u, out PluginSkillInfo summoning) && (int)summoning.Training >= 2);
        if (!trained)
            return BehaviorStep.Fail("summoning is not trained");

        foreach (string wanted in pets.Devices)
        {
            PluginInventoryItem? device = FindDevice(surface, wanted);
            if (device is null)
                continue;
            if (_parkedUntil.TryGetValue(device.Value.ObjectId, out double until) && board.Now < until)
                continue;

            bool hasCharges = device.Value.MaximumStructure == 0 || device.Value.Structure > 0;
            if (hasCharges)
            {
                PluginItemCommandResult use = surface.Items.Use(device.Value.ObjectId);
                if (use.Status == PluginItemCommandStatus.Busy)
                    return BehaviorStep.Continue;
                if (!use.Accepted)
                {
                    _parkedUntil[device.Value.ObjectId] = board.Now + DeviceParkSeconds;
                    continue;
                }
                context.Log.Info($"summoning a pet from {device.Value.Name} ({device.Value.Structure} charges)");
                _busyUntil = board.Now + UseSettleSeconds;
                _assumeActiveUntil = board.Now + AssumeActiveSeconds;
                return BehaviorStep.Continue;
            }

            if (pets.RefillFromSpirits && FindSpirit(surface) is { } spirit)
            {
                PluginItemCommandResult refill = surface.Items.Apply(spirit.ObjectId, device.Value.ObjectId);
                if (refill.Status == PluginItemCommandStatus.Busy)
                    return BehaviorStep.Continue;
                if (refill.Accepted)
                {
                    context.Log.Info($"refilling {device.Value.Name} from {spirit.Name}");
                    _busyUntil = board.Now + UseSettleSeconds;
                    return BehaviorStep.Continue;
                }
            }
            _parkedUntil[device.Value.ObjectId] = board.Now + DeviceParkSeconds;
        }
        return BehaviorStep.Fail("no usable pet device");
    }

    public void Interrupt(BehaviorContext context)
    {
    }

    private static bool IsPetActive(IAutomationSurface surface, uint selfId)
    {
        string name = surface.Character.Name;
        if (name.Length == 0)
            return false;
        string prefix = name + "'s ";
        foreach (PluginWorldObject value in surface.Objects.CaptureObjects())
        {
            if (!value.IsOwned && value.ObjectId != selfId && value.Name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static PluginInventoryItem? FindDevice(IAutomationSurface surface, string wanted)
    {
        foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
        {
            if (item.IsPetDevice && (item.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)))
                return item;
        }
        return null;
    }

    private static PluginInventoryItem? FindSpirit(IAutomationSurface surface)
    {
        foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
        {
            if (item.Name.Contains("Encapsulated Spirit", StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }
}
