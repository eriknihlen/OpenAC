using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Opens closed doors in the way while the bot walks, the way RynthAi's
/// door controller does: the nearest closed door within range is used,
/// the result watched, a locked one picked with a lockpick from the pack
/// when that is allowed, and a door that will not open is left alone for
/// a while. A door just opened is not re-targeted for a minute, so the
/// walk is not interrupted by a door swinging shut behind the character.
/// </summary>
public sealed class DoorBehavior(
    Func<DoorSettings> settings,
    Func<bool> walking,
    Func<IReadOnlyList<PluginNavigationObject>> scan) : IBehavior
{
    private const double ActionTimeoutSeconds = 3d;
    private const double CooldownSeconds = 5d;
    private const double OpenedSkipSeconds = 60d;
    private const double ScanIntervalSeconds = 0.5d;
    private const int MaxAttempts = 3;

    private enum Phase
    {
        Idle,
        Opening,
        Unlocking,
        RetryOpen,
        Cooldown,
    }

    private readonly Dictionary<uint, double> _openedAt = new();
    private readonly Dictionary<uint, double> _parkedUntil = new();
    private Phase _phase;
    private uint _doorId;
    private double _actionAt;
    private int _attempts;
    private double _lastScanAt = double.NegativeInfinity;
    private uint _candidate;

    public string Name => "doors";

    public BehaviorPriority Priority => BehaviorPriority.Doors;

    public uint CurrentDoorId => _doorId;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        DoorSettings doors = settings();
        if (!doors.Enabled)
            return false;
        if (_phase == Phase.Cooldown)
        {
            if (board.Now - _actionAt >= CooldownSeconds)
                _phase = Phase.Idle;
            return false;
        }
        if (_phase != Phase.Idle)
        {
            reason = "opening a door";
            return true;
        }
        if (!walking() || !board.Navigation.IsAvailable)
            return false;
        // The door list is not on the blackboard; look every half second.
        if (board.Now - _lastScanAt >= ScanIntervalSeconds)
        {
            _lastScanAt = board.Now;
            _candidate = NearestClosedDoor(scan(), board, doors.RangeMeters);
        }
        if (_candidate == 0u)
            return false;
        reason = "door ahead";
        return true;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        DoorSettings doors = settings();
        IAutomationSurface surface = context.Surface;
        double now = board.Now;

        if (_phase == Phase.Idle)
        {
            _doorId = NearestClosedDoor(scan(), board, doors.RangeMeters);
            _candidate = 0u;
            if (_doorId == 0u)
                return BehaviorStep.Done;
            _attempts = 0;
            surface.Navigation.ClearMovementIntent();
            context.Log.Info($"opening door 0x{_doorId:X8}");
            return Use(context, Phase.Opening);
        }

        if (_phase == Phase.Cooldown)
            return BehaviorStep.Done;

        if (IsOpen(surface, _doorId))
        {
            context.Log.Info($"door 0x{_doorId:X8} is open");
            _openedAt[_doorId] = now;
            _phase = Phase.Idle;
            return BehaviorStep.Done;
        }
        if (board.IsActionPending || now - _actionAt < ActionTimeoutSeconds)
            return BehaviorStep.Continue;

        switch (_phase)
        {
            case Phase.Opening:
                // Not open after a use: locked, or the use was lost. Pick it if allowed, else try again.
                if (doors.UseLockpicks && FindLockpick(surface) is { } lockpick)
                {
                    context.Log.Info($"picking door 0x{_doorId:X8} with {lockpick.Name}");
                    PluginItemCommandResult pick = surface.Items.Apply(lockpick.ObjectId, _doorId);
                    if (pick.Status == PluginItemCommandStatus.Busy)
                        return BehaviorStep.Continue;
                    _phase = Phase.Unlocking;
                    _actionAt = now;
                    return BehaviorStep.Continue;
                }
                return Retry(context);
            case Phase.Unlocking:
                return Use(context, Phase.RetryOpen);
            default:
                return Retry(context);
        }
    }

    public void Interrupt(BehaviorContext context)
    {
        if (_phase != Phase.Cooldown)
            _phase = Phase.Idle;
    }

    private BehaviorStep Retry(BehaviorContext context)
    {
        if (++_attempts >= MaxAttempts)
        {
            context.Log.Warn($"door 0x{_doorId:X8} would not open; leaving it for {CooldownSeconds:0}s");
            _parkedUntil[_doorId] = context.Board.Now + OpenedSkipSeconds;
            _phase = Phase.Cooldown;
            _actionAt = context.Board.Now;
            return BehaviorStep.Fail("door would not open");
        }
        return Use(context, Phase.Opening);
    }

    private BehaviorStep Use(BehaviorContext context, Phase next)
    {
        PluginItemCommandResult use = context.Surface.Objects.Use(_doorId);
        if (use.Status == PluginItemCommandStatus.Busy)
            return BehaviorStep.Continue;
        _phase = next;
        _actionAt = context.Board.Now;
        if (!use.Accepted)
        {
            _parkedUntil[_doorId] = context.Board.Now + OpenedSkipSeconds;
            _phase = Phase.Cooldown;
            return BehaviorStep.Fail($"door use {use.Status}");
        }
        return BehaviorStep.Continue;
    }

    private uint NearestClosedDoor(IReadOnlyList<PluginNavigationObject> objects, Blackboard board, float rangeMeters)
    {
        uint best = 0u;
        double bestDistance = rangeMeters;
        foreach (PluginNavigationObject candidate in objects)
        {
            if (!candidate.IsDoor || candidate.IsOpen)
                continue;
            if (_openedAt.TryGetValue(candidate.ObjectId, out double openedAt) && board.Now - openedAt < OpenedSkipSeconds)
                continue;
            if (_parkedUntil.TryGetValue(candidate.ObjectId, out double until) && board.Now < until)
                continue;
            double distance = candidate.Position.HorizontalDistanceMeters(board.Navigation.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate.ObjectId;
            }
        }
        return best;
    }

    private static bool IsOpen(IAutomationSurface surface, uint doorId) =>
        surface.Navigation.TryGetObject(doorId, out PluginNavigationObject door) && door.IsOpen;

    private static PluginInventoryItem? FindLockpick(IAutomationSurface surface)
    {
        foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
        {
            if (item.ObjectClass == PluginObjectClass.Lockpick)
                return item;
        }
        return null;
    }
}
