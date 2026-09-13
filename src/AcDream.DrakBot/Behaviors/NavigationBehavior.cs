using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Walks the loaded route whenever nothing more important wants the
/// character. Turning is delegated to the host's turn-to-heading; walking is
/// a held forward intent that is dropped the moment control is taken away.
/// </summary>
public sealed class NavigationBehavior(Func<NavigationSettings> settings) : IBehavior
{
    private const double RecoveryDurationSeconds = 0.6;
    private const double FaceReissueSeconds = 1.5;

    private readonly StuckDetector _stuck = new();
    private RouteFollower? _follower;
    private StuckRecovery? _recovery;
    private double _recoveryUntil;
    private float _lastFaceHeading = float.NaN;
    private double _lastFaceAt = double.NegativeInfinity;
    private bool _moving;

    public string Name => "nav";

    public BehaviorPriority Priority => BehaviorPriority.Navigation;

    public Route? Route => _follower?.Route;

    public int WaypointIndex => _follower?.CurrentIndex ?? -1;

    /// <summary>Replaces the route; an empty route clears navigation.</summary>
    public void SetRoute(Route? route)
    {
        _follower = route is null || route.IsEmpty
            ? null
            : new RouteFollower(route, settings().Mode);
        _follower?.Reset();
        _stuck.Reset();
        _recovery = null;
    }

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        if (!settings().Enabled || _follower is null || _follower.IsFinished)
            return false;
        if (!board.Navigation.IsAvailable || board.Navigation.IsPortalSpace)
            return false;
        reason = $"waypoint {_follower.CurrentIndex + 1}/{_follower.Route.Waypoints.Count}";
        return true;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        INavigationAutomation host = context.Surface.Navigation;
        NavigationSettings nav = settings();
        if (_follower is null)
            return BehaviorStep.Done;

        if (_recovery is not null)
        {
            if (board.Now < _recoveryUntil)
                return BehaviorStep.Continue;
            _recovery = null;
            host.ClearMovementIntent();
            _moving = false;
        }

        NavigationStep step = _follower.Advance(
            board.Navigation.Position,
            board.Now,
            nav.ArrivalDistanceMeters,
            nav.TurnToleranceDegrees);

        switch (step.Action)
        {
            case NavigationAction.Finished:
                StopMoving(host);
                return BehaviorStep.Done;

            case NavigationAction.Hold:
                StopMoving(host);
                _stuck.Reset();
                return BehaviorStep.Continue;

            case NavigationAction.Turn:
                StopMoving(host);
                bool stale = float.IsNaN(_lastFaceHeading)
                    || Math.Abs(RouteFollower.HeadingDelta(_lastFaceHeading, step.HeadingDegrees)) > nav.TurnToleranceDegrees
                    || board.Now - _lastFaceAt > FaceReissueSeconds;
                if (stale)
                {
                    host.FaceHeading(step.HeadingDegrees);
                    _lastFaceHeading = step.HeadingDegrees;
                    _lastFaceAt = board.Now;
                }
                return BehaviorStep.Continue;

            default:
                if (!_moving)
                {
                    host.SetMovementIntent(new PluginMovementIntent(Forward: true, Run: true));
                    _moving = true;
                }
                StuckRecovery? recovery = _stuck.Observe(board.Navigation.Position, board.Now);
                if (recovery is not null)
                {
                    context.Log.Info($"nav stuck near waypoint {_follower.CurrentIndex + 1}; trying {recovery}");
                    host.SetMovementIntent(IntentFor(recovery.Value));
                    _recovery = recovery;
                    _recoveryUntil = board.Now + RecoveryDurationSeconds;
                }
                return BehaviorStep.Continue;
        }
    }

    public void Interrupt(BehaviorContext context)
    {
        StopMoving(context.Surface.Navigation);
        _recovery = null;
        _stuck.Reset();
    }

    private void StopMoving(INavigationAutomation host)
    {
        if (!_moving)
            return;
        host.ClearMovementIntent();
        _moving = false;
    }

    private static PluginMovementIntent IntentFor(StuckRecovery recovery) => recovery switch
    {
        StuckRecovery.Jump => new PluginMovementIntent(Forward: true, Run: true, Jump: true),
        StuckRecovery.StrafeLeft => new PluginMovementIntent(StrafeLeft: true, Run: true),
        StuckRecovery.StrafeRight => new PluginMovementIntent(StrafeRight: true, Run: true),
        _ => new PluginMovementIntent(Backward: true, Run: true),
    };
}
