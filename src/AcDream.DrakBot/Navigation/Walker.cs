using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Drives the character toward a heading one tick at a time, the way a
/// player does: a held run forward, steered with the turn keys while it
/// moves. Only a large heading error stops the run for a turn in place.
/// Both the route follower and the combat approach walk through this, so
/// they stall, recover and release the keys the same way.
/// </summary>
public sealed class Walker
{
    /// <summary>A heading error past this stops the run and turns in place.</summary>
    public const float TurnInPlaceDegrees = 40f;

    /// <summary>Steering starts outside this error and stops inside <see cref="SteerReleaseDegrees"/>.</summary>
    public const float SteerEngageDegrees = 6f;
    public const float SteerReleaseDegrees = 2f;

    public const double RecoveryDurationSeconds = 0.6;
    private const double FaceReissueSeconds = 1.5;

    private readonly StuckDetector _stuck = new();
    private PluginMovementIntent? _intent;
    private int _steer;
    private float _lastFaceHeading = float.NaN;
    private double _lastFaceAt = double.NegativeInfinity;
    private StuckRecovery? _recovery;
    private double _recoveryUntil;

    public bool IsMoving => _intent is not null;

    public StuckRecovery? Recovery => _recovery;

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
        if (Math.Abs(delta) > turnInPlaceDegrees)
        {
            // Turning in place is not walking: the stall window restarts
            // when the run does, so a slow turn never reads as stuck.
            Stop(nav);
            _stuck.Reset();
            bool stale = float.IsNaN(_lastFaceHeading)
                || Math.Abs(RouteFollower.HeadingDelta(_lastFaceHeading, heading)) > turnInPlaceDegrees
                || now - _lastFaceAt > FaceReissueSeconds;
            if (stale)
            {
                nav.FaceHeading(heading);
                _lastFaceHeading = heading;
                _lastFaceAt = now;
            }
            return null;
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
        return _stuck.Observe(position, now);
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
        _stuck.Reset();
        _lastFaceHeading = float.NaN;
        _lastFaceAt = double.NegativeInfinity;
    }

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
