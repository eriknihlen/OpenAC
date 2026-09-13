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
    }

    public bool IsRunning { get; private set; }

    public IBotClock Clock => _clock;

    public BotProfile Profile { get; set; } = BotProfile.Default;

    public string ActiveBehaviorName => _active?.Name ?? "idle";

    public string LastReason { get; private set; } = string.Empty;

    public IReadOnlyList<IBehavior> Behaviors => _behaviors;

    /// <summary>The snapshot the last tick ran against; what a status window shows.</summary>
    public Blackboard? LastBoard { get; private set; }

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
        if (!IsRunning)
            return;

        Blackboard board = Blackboard.Capture(
            _surface,
            _clock,
            Profile.Combat.EngageDistance,
            Profile.Loot.ScanDistance);
        var context = new BehaviorContext(_surface, _log, board);
        _lastContext = context;
        LastBoard = board;

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

        // A strictly higher-priority behavior may preempt the active one.
        foreach (IBehavior behavior in _behaviors)
        {
            if (_active is not null && behavior.Priority >= _active.Priority)
                break;
            if (!behavior.WantsControl(board, out string reason))
                continue;
            if (!ReferenceEquals(behavior, _active))
            {
                if (_active is not null)
                    SafeInterrupt(_active, context);
                _active = behavior;
                LastReason = reason;
            }
            break;
        }

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
                _active = null;
                return;
        }
    }

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
