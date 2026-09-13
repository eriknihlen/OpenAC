using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Walks the loaded route whenever nothing more important wants the
/// character. The <see cref="Walker"/> does the driving: a held run steered
/// toward the waypoint, a turn in place for a sharp corner, and the stuck
/// recoveries; every key is released the moment control is taken away.
/// </summary>
public sealed class NavigationBehavior(Func<NavigationSettings> settings) : IBehavior
{
    private readonly Walker _walker = new();
    private RouteFollower? _follower;

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

        if (_walker.ContinueRecovery(host, board.Now))
            return BehaviorStep.Continue;

        NavigationStep step = _follower.Advance(
            board.Navigation.Position,
            board.Now,
            nav.ArrivalDistanceMeters,
            nav.TurnToleranceDegrees);

        switch (step.Action)
        {
            case NavigationAction.Finished:
                _walker.Reset(host);
                return BehaviorStep.Done;

            case NavigationAction.Hold:
                _walker.Reset(host);
                return BehaviorStep.Continue;

            default:
                StuckRecovery? recovery = _walker.Toward(
                    host, board.Navigation.Position, step.HeadingDegrees, board.Now, nav.TurnToleranceDegrees);
                if (recovery is { } move)
                {
                    context.Log.Info($"nav stuck near waypoint {_follower.CurrentIndex + 1}; trying {move}");
                    _walker.BeginRecovery(host, move, board.Now);
                }
                return BehaviorStep.Continue;
        }
    }

    public void Interrupt(BehaviorContext context) =>
        _walker.Reset(context.Surface.Navigation);
}
