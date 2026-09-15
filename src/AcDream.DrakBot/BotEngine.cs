using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>
/// Priority arbiter. Each tick it snapshots the world once, asks every
/// behavior whether it wants control, and runs the winner. A running behavior
/// keeps control across ticks while it reports <see cref="StepResult.Continue"/>
/// unless a strictly higher-priority behavior wants in, in which case it is
/// interrupted first.
/// </summary>
public sealed class BotEngine
{
    private readonly IAutomationSurface _surface;
    private readonly IPluginLogger _log;
    private readonly TickClock _clock;
    private readonly List<IBehavior> _behaviors;
    private IBehavior? _active;
    private BehaviorContext? _lastContext;
    private double _lastHeartbeatAt = double.NegativeInfinity;
    private bool _wasInWorld;

    public BotEngine(
        IAutomationSurface surface,
        IPluginLogger log,
        IEnumerable<IBehavior> behaviors,
        TickClock? clock = null)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentNullException.ThrowIfNull(behaviors);
        _clock = clock ?? new TickClock();
        _behaviors = behaviors
            .OrderBy(static behavior => behavior.Priority)
            .ToList();
        Inventory = new Inventory.InventoryTidy(() => Profile.Inventory);
    }

    public bool IsRunning { get; private set; }

    public IBotClock Clock => _clock;

    public BotProfile Profile { get; set; } = BotProfile.Default;

    public string ActiveBehaviorName => _active?.Name ?? "idle";
    private double _activeSince;
    private double _heldWarnedAt;
    private const double HeldWarnSeconds = 60d;

    public string LastReason { get; private set; } = string.Empty;

    public IReadOnlyList<IBehavior> Behaviors => _behaviors;

    /// <summary>The snapshot the last tick ran against; what a status window shows.</summary>
    public Blackboard? LastBoard { get; private set; }

    /// <summary>The host the bot acts through.</summary>
    public IAutomationSurface Surface => _surface;

    /// <summary>The meta, when one is wired in. It thinks every tick, beside the behaviors.</summary>
    public Meta.MetaEngine? Meta { get; set; }

    /// <summary>A jump in progress takes the character over from the behaviors until it lands.</summary>
    public Navigation.Jumper Jumper { get; } = new();

    /// <summary>Pack housekeeping, ticked beside the behaviors when the hands are free.</summary>
    public Inventory.InventoryTidy Inventory { get; }

    public void Start()
    {
        if (IsRunning)
            return;
        IsRunning = true;
        LastReason = "started";
        _log.Info("DrakBot started");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        IsRunning = false;
        if (_active is not null && _lastContext is not null)
            SafeInterrupt(_active, _lastContext);
        _active = null;
        LastReason = "stopped";
        _log.Info("DrakBot stopped");
    }

    public void Tick(double elapsedSeconds)
    {
        _clock.Advance(elapsedSeconds);
        if (!IsRunning && Meta is null && !Jumper.IsBusy)
            return;

        Blackboard board = Blackboard.Capture(
            _surface,
            _clock,
            Profile.Combat.EngageDistance,
            Profile.Loot.ScanDistance);
        // The meta drains chat and its timers whether or not the bot runs;
        // its rules only fire while it does. It may change the profile or
        // the route the behaviors are about to read.
        Meta?.Think(board, IsRunning);
        if (Jumper.IsBusy)
        {
            // The jump owns the keys; whatever was running lets go first.
            if (_active is not null && _lastContext is not null)
            {
                SafeInterrupt(_active, _lastContext);
                _active = null;
            }
            if (Jumper.Tick(_surface.Navigation, board.Navigation, board.Now))
            {
                LastReason = "jumping";
                LastBoard = board;
                return;
            }
        }
        if (!IsRunning)
            return;
        var context = new BehaviorContext(_surface, _log, board);
        _lastContext = context;
        LastBoard = board;

        if (board.IsInWorld != _wasInWorld)
        {
            _wasInWorld = board.IsInWorld;
            _log.Info(board.IsInWorld
                ? $"engine: in world at {Describe(board.Navigation.Position)}"
                : "engine: left the world");
        }
        if (!board.IsInWorld)
        {
            if (_active is not null)
            {
                SafeInterrupt(_active, context);
                _active = null;
            }
            LastReason = "not in world";
            return;
        }
        WatchBusy(board);
        if (board.Now - _lastHeartbeatAt >= 2d && _log.Debugs())
        {
            _lastHeartbeatAt = board.Now;
            _log.Debug($"engine: {ActiveBehaviorName} ({LastReason}) at {Describe(board.Navigation.Position)}"
                + $" hp {board.Vitals.Health}/{board.Vitals.MaxHealth} hostiles {board.Hostiles.Count} corpses {board.Corpses.Count}"
                + $" mode {board.Combat.Mode}{(board.IsCasting ? " casting" : string.Empty)}{(board.LootBusy ? " loot-busy" : string.Empty)}");
        }

        // A strictly higher-priority behavior may preempt the active one.
        // The profile's boosts lift navigation or looting above combat,
        // never above survival and buffing.
        PrioritySettings boosts = Profile.Priorities;
        bool engaged = false;
        foreach (IBehavior behavior in _behaviors)
        {
            if (behavior is CombatBehavior { IsEngaged: true })
                engaged = true;
        }
        int Rank(IBehavior behavior) => behavior.Priority switch
        {
            BehaviorPriority.Navigation when boosts.BoostNavigation => (int)BehaviorPriority.Buffing + 5,
            BehaviorPriority.Looting when boosts.BoostLooting && !engaged => (int)BehaviorPriority.Buffing + 6,
            _ => (int)behavior.Priority,
        };
        int activeRank = _active is null ? int.MaxValue : Rank(_active);
        foreach (IBehavior behavior in _behaviors.OrderBy(Rank))
        {
            if (_active is not null && Rank(behavior) >= activeRank)
                break;
            if (!behavior.WantsControl(board, out string reason))
                continue;
            if (!ReferenceEquals(behavior, _active))
            {
                _log.Info($"engine: {(_active is null ? "idle" : _active.Name)} -> {behavior.Name} ({reason})");
                if (_active is not null)
                    SafeInterrupt(_active, context);
                _active = behavior;
                _activeSince = board.Now;
                _heldWarnedAt = board.Now;
                LastReason = reason;
            }
            break;
        }
        // A behaviour that holds the tick for a minute without a word is
        // the kind of silence that hid a cast the host never finished.
        if (_active is not null && board.Now - _heldWarnedAt >= HeldWarnSeconds)
        {
            _heldWarnedAt = board.Now;
            _log.Warn($"engine: {_active.Name} has held control for {board.Now - _activeSince:0}s ({LastReason}); action pending: {board.IsActionPending}, casting: {_surface.Magic.IsCasting}");
        }

        Inventory.Tick(_surface, board);
        if (_active is null)
        {
            LastReason = "nothing to do";
            return;
        }

        BehaviorStep step;
        try
        {
            step = _active.Execute(context);
        }
        catch (Exception error)
        {
            _log.Error($"behavior {_active.Name} threw", error);
            step = BehaviorStep.Fail(error.Message);
        }

        switch (step.Result)
        {
            case StepResult.Continue:
                return;
            case StepResult.Failed:
                _log.Warn($"{_active.Name}: {step.Reason}");
                LastReason = $"{_active.Name} failed: {step.Reason}";
                _active = null;
                return;
            default:
                _log.Debug($"engine: {_active.Name} done");
                _active = null;
                return;
        }
    }

    /// <summary>
    /// The client's busy count is raised when an action is sent and lowered
    /// by the server's reply; a reply that never comes (a door use cut
    /// short by a fight, in the log) leaves the character busy for good,
    /// and every behavior waits on it - the bot stands in peace mode while
    /// monsters circle. Nothing the bot does takes this long, so after the
    /// window one reference is cleared, and another every window while it
    /// stays stuck, the way /ub clearbusy would by hand.
    /// </summary>
    public const double BusyStuckSeconds = 10d;
    private double _busySince = double.NaN;
    private double _lastBusyClearAt = double.NegativeInfinity;

    private void WatchBusy(in Blackboard board)
    {
        if (!board.IsCasting)
        {
            _busySince = double.NaN;
            return;
        }
        if (double.IsNaN(_busySince))
        {
            _busySince = board.Now;
            return;
        }
        if (board.Now - _busySince < BusyStuckSeconds || board.Now - _lastBusyClearAt < BusyStuckSeconds)
            return;
        _lastBusyClearAt = board.Now;
        PluginRecoveryResult result = _surface.Recovery.ClearOneBusyReference();
        _log.Warn(result.Accepted
            ? $"engine: busy for {board.Now - _busySince:0}s with nothing to wait for; cleared one busy reference ({result.PreviousCount} -> {result.CurrentCount})"
            : $"engine: busy for {board.Now - _busySince:0}s and the host would not clear it: {result.Message}");
    }

    /// <summary>A position as the game prints it, with the cell, for log lines.</summary>
    public static string Describe(in PluginNavigationPosition position) =>
        $"{Math.Abs(position.NorthSouth):0.00}{(position.NorthSouth >= 0 ? 'N' : 'S')} {Math.Abs(position.EastWest):0.00}{(position.EastWest >= 0 ? 'E' : 'W')} z{position.Elevation * 240d:0.0} h{position.HeadingDegrees:0} cell 0x{position.CellId:X8}";

    private void SafeInterrupt(IBehavior behavior, BehaviorContext context)
    {
        try
        {
            behavior.Interrupt(context);
        }
        catch (Exception error)
        {
            _log.Error($"behavior {behavior.Name} failed to interrupt", error);
        }
    }
}
