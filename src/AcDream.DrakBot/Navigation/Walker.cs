using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Drives the character toward a heading one tick at a time, the way a
/// player does: a held run forward, steered with the turn keys while it
/// moves. Only a large heading error stops the run for a turn in place,
/// and the run resumes once the error is back under a lower bar, so the
/// gate does not flap on the threshold. Both the route follower and the
/// combat approach walk through this, so they stall, recover and release
/// the keys the same way. The angles are the ones RynthSuite's navigation
/// engine settled on in play.
/// </summary>
public sealed class Walker
{
    /// <summary>A heading error past this stops the run and turns in place.</summary>
    public const float TurnInPlaceDegrees = 20f;

    /// <summary>The run resumes once a turn in place has the error under this.</summary>
    public const float ResumeRunDegrees = 10f;

    /// <summary>Steering starts outside this error and stops inside <see cref="SteerReleaseDegrees"/>.</summary>
    public const float SteerEngageDegrees = 4f;
    public const float SteerReleaseDegrees = 2f;

    public const double RecoveryDurationSeconds = 0.6;

    /// <summary>A turn in place that has not moved the heading this long has the keys pressed again, then counts as a stall.</summary>
    public const double TurnStallSeconds = 2d;
    private const float TurnProgressDegrees = 5f;

    private readonly StuckDetector _stuck = new();
    private PluginMovementIntent? _intent;
    private int _steer;
    private bool _turning;
    private bool _reasserted;
    private double _turnStartedAt;
    private float _turnStartHeading;
    private int _turnStalls;
    private StuckRecovery? _recovery;
    private double _recoveryUntil;

    /// <summary>Whether the character is being driven somewhere (a turn in place is not moving).</summary>
    public bool IsMoving => _intent is { } intent && (intent.Forward || intent.Backward || intent.StrafeLeft || intent.StrafeRight);

    public StuckRecovery? Recovery => _recovery;

    /// <summary>One word on the walker's state, for the log.</summary>
    public string State => _recovery is { } recovery
        ? $"recovering:{recovery}"
        : _turning ? "turning" : _intent is null ? "stopped" : _steer < 0 ? "run+left" : _steer > 0 ? "run+right" : "run";

    /// <summary>
    /// Walks toward <paramref name="heading"/>. Returns a recovery to run
    /// when the walk has stalled; the caller logs it and hands it to
    /// <see cref="BeginRecovery"/>.
    /// </summary>
    public StuckRecovery? Toward(
        INavigationAutomation nav,
        in PluginNavigationPosition position,
        float heading,
        double now,
        float turnInPlaceDegrees = TurnInPlaceDegrees)
    {
        float delta = RouteFollower.HeadingDelta(position.HeadingDegrees, heading);
        float resumeDegrees = ResumeRunFor(turnInPlaceDegrees);
        if (_turning ? Math.Abs(delta) > resumeDegrees : Math.Abs(delta) > turnInPlaceDegrees)
        {
            // Turning in place is not walking: the stall window restarts
            // when the run does, so a slow turn never reads as stuck. The
            // turn keys do it, not a face-heading command: that is an
            // autonomous move the runtime owns, and a run started before
            // it lands can be swallowed when it does.
            if (!_turning)
            {
                _turning = true;
                _turnStalls = 0;
                BeginTurnWatch(position.HeadingDegrees, now);
            }
            _steer = 0;
            _stuck.Reset();
            var turn = new PluginMovementIntent(TurnLeft: delta < 0f, TurnRight: delta > 0f, Run: true);
            Send(nav, turn);
            // A turn key the host is not honouring would spin here forever,
            // with the stall window held at zero. When the heading has not
            // moved for a while the keys are pressed again; a second dead
            // wait is a stall, so the caller sees it and tries a move.
            if (now - _turnStartedAt >= TurnStallSeconds
                && Math.Abs(RouteFollower.HeadingDelta(_turnStartHeading, position.HeadingDegrees)) < TurnProgressDegrees)
            {
                BeginTurnWatch(position.HeadingDegrees, now);
                if (++_turnStalls == 1)
                {
                    nav.ClearMovementIntent();
                    nav.SetMovementIntent(turn);
                    return null;
                }
                _turnStalls = 0;
                return StuckRecovery.BackUp;
            }
            return null;
        }
        if (_turning)
        {
            // The run starts from a clean slate, so the runtime sees a fresh
            // forward edge rather than a held key.
            _turning = false;
            Stop(nav);
        }

        // Hysteresis keeps the turn key from chattering around the heading.
        if (_steer == 0 && Math.Abs(delta) > SteerEngageDegrees)
            _steer = Math.Sign(delta);
        else if (_steer != 0 && (Math.Abs(delta) < SteerReleaseDegrees || Math.Sign(delta) != _steer))
            _steer = 0;
        Send(nav, new PluginMovementIntent(
            Forward: true,
            Run: true,
            TurnLeft: _steer < 0,
            TurnRight: _steer > 0));
        StuckRecovery? stalled = _stuck.Observe(position, now);
        if (stalled is not null && !_reasserted)
        {
            // The first stall is answered by letting go and pressing the
            // keys again: a run the runtime dropped comes back that way,
            // and only a run that really is against something escalates.
            _reasserted = true;
            PluginMovementIntent intent = _intent!.Value;
            nav.ClearMovementIntent();
            nav.SetMovementIntent(intent);
            _stuck.Reset();
            return null;
        }
        if (_stuck.Progressed)
            _reasserted = false;
        return stalled;
    }

    /// <summary>
    /// True while a recovery move is still running; the caller continues
    /// without reconsidering the walk. The keys are released when it ends.
    /// </summary>
    public bool ContinueRecovery(INavigationAutomation nav, double now)
    {
        if (_recovery is null)
            return false;
        if (now < _recoveryUntil)
            return true;
        _recovery = null;
        Stop(nav);
        return false;
    }

    public void BeginRecovery(INavigationAutomation nav, StuckRecovery recovery, double now)
    {
        Send(nav, IntentFor(recovery));
        _recovery = recovery;
        _recoveryUntil = now + RecoveryDurationSeconds;
    }

    /// <summary>Releases the keys; the turn-in-place and stall state stay.</summary>
    public void Stop(INavigationAutomation nav)
    {
        _recovery = null;
        _steer = 0;
        if (_intent is null)
            return;
        nav.ClearMovementIntent();
        _intent = null;
    }

    /// <summary>Releases the keys and forgets the walk, for an interrupt or a new destination.</summary>
    public void Reset(INavigationAutomation nav)
    {
        Stop(nav);
        _turning = false;
        _reasserted = false;
        _turnStalls = 0;
        _stuck.Reset();
    }

    private void BeginTurnWatch(float heading, double now)
    {
        _turnStartedAt = now;
        _turnStartHeading = heading;
    }

    /// <summary>The resume bar for a turn-in-place bar: half of it, at least a degree under it.</summary>
    public static float ResumeRunFor(float turnInPlaceDegrees) =>
        Math.Max(1f, Math.Min(turnInPlaceDegrees - 1f, turnInPlaceDegrees * (ResumeRunDegrees / TurnInPlaceDegrees)));

    private void Send(INavigationAutomation nav, in PluginMovementIntent intent)
    {
        if (_intent is { } current && current == intent)
            return;
        nav.SetMovementIntent(intent);
        _intent = intent;
    }

    private static PluginMovementIntent IntentFor(StuckRecovery recovery) => recovery switch
    {
        StuckRecovery.StrafeLeft => new PluginMovementIntent(StrafeLeft: true, Run: true),
        StuckRecovery.StrafeRight => new PluginMovementIntent(StrafeRight: true, Run: true),
        _ => new PluginMovementIntent(Backward: true, Run: true),
    };
}
