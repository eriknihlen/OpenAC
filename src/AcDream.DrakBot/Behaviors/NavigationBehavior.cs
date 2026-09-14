using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Walks the loaded route whenever nothing more important wants the
/// character. The <see cref="Walker"/> does the driving: a held run steered
/// toward the waypoint, a turn in place for a sharp corner, and the stuck
/// recoveries; every key is released the moment control is taken away. The
/// steps that act in place (chat, recall, portal, NPC) run through the
/// <see cref="RouteActionRunner"/>. A teleport the route did not ask for -
/// a portal walked into, a recall cast by hand - is noticed too, and the
/// walk settles before it carries on.
/// </summary>
public sealed class NavigationBehavior(Func<NavigationSettings> settings) : IBehavior
{
    private readonly Walker _walker = new();
    private readonly RouteActionRunner _actions = new();
    private RouteFollower? _follower;
    private bool _resumeNearest;
    private bool _followMoving;
    private double _lastFollowLookupAt = double.NegativeInfinity;
    private uint _followId;
    private bool _wasInPortalSpace;
    private PluginNavigationPosition _lastPosition;
    private bool _hasLastPosition;
    private double _settleUntil = double.NegativeInfinity;

    public string Name => "nav";

    public BehaviorPriority Priority => BehaviorPriority.Navigation;

    public Route? Route => _follower?.Route;

    public int WaypointIndex => _follower?.CurrentIndex ?? -1;

    /// <summary>What the route is doing at an action step, for the dashboard.</summary>
    public string ActionStatus => _actions.Status;

    /// <summary>What a vendor step sells: see <see cref="RouteActionRunner.ItemsToSell"/>.</summary>
    public Func<IReadOnlyList<uint>>? ItemsToSell
    {
        get => _actions.ItemsToSell;
        set => _actions.ItemsToSell = value;
    }

    /// <summary>Following a player rather than a route.</summary>
    public bool IsFollowing => settings().Follow.Length > 0;

    /// <summary>Replaces the route; an empty route clears navigation.</summary>
    /// <summary>
    /// Follows a route from its first step, or with <paramref name="joinNearest"/>
    /// from the step nearest the character - how VTank joins a loaded
    /// circular route; a route planned from where the character stands
    /// starts at its first step, which is already the nearest safe one.
    /// </summary>
    public void SetRoute(Route? route, bool joinNearest = false)
    {
        RouteMode mode = route?.Mode ?? settings().Mode;
        _follower = route is null || route.IsEmpty
            ? null
            : new RouteFollower(route, mode);
        _follower?.Reset();
        _actions.Cancel();
        _settleUntil = double.NegativeInfinity;
        _resumeNearest = joinNearest && _follower is not null && mode != RouteMode.Once;
    }

    /// <summary>
    /// Swaps in an edited copy of the route being walked without starting
    /// over: the walk resumes at the waypoint nearest the character.
    /// </summary>
    public void ReplaceRoute(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.IsEmpty)
        {
            SetRoute(null);
            return;
        }
        _follower = new RouteFollower(route, route.Mode ?? settings().Mode);
        _follower.Reset();
        _actions.Cancel();
        _resumeNearest = true;
    }

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        NavigationSettings nav = settings();
        if (!nav.Enabled || !board.Navigation.IsAvailable)
            return false;
        if (nav.Follow.Length > 0)
        {
            if (board.Navigation.IsPortalSpace)
                return false;
            reason = $"following {nav.Follow}";
            return true;
        }
        if (_follower is null || _follower.IsFinished)
            return false;
        // Nothing else should take over mid-teleport: the route is still
        // responsible for noticing the arrival.
        if (board.Navigation.IsPortalSpace && !_actions.IsRunning)
            return false;
        reason = _actions.IsRunning && _actions.Status.Length > 0
            ? _actions.Status
            : $"waypoint {_follower.CurrentIndex + 1}/{_follower.Route.Waypoints.Count}";
        return true;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        INavigationAutomation host = context.Surface.Navigation;
        NavigationSettings nav = settings();
        if (nav.Follow.Length > 0)
            return Follow(context, nav);
        if (_follower is null)
            return BehaviorStep.Done;
        if (_resumeNearest)
        {
            _follower.ResumeNearest(board.Navigation.Position);
            _resumeNearest = false;
        }

        if (_actions.IsRunning)
            return RunAction(context, nav);

        if (NoticeTeleport(board, host, nav, context.Log))
            return BehaviorStep.Continue;
        if (board.Now < _settleUntil)
            return BehaviorStep.Continue;
        if (board.Navigation.IsPortalSpace)
        {
            _walker.Reset(host);
            return BehaviorStep.Continue;
        }

        if (_walker.ContinueRecovery(host, board.Now))
            return BehaviorStep.Continue;

        NavigationStep step = _follower.Advance(
            board.Navigation.Position,
            board.Now,
            nav.ArrivalDistanceMeters,
            nav.TurnToleranceDegrees,
            nav.LookaheadMeters);

        switch (step.Action)
        {
            case NavigationAction.Finished:
                _walker.Reset(host);
                return BehaviorStep.Done;

            case NavigationAction.Hold:
                _walker.Reset(host);
                return BehaviorStep.Continue;

            case NavigationAction.Act:
                _walker.Reset(host);
                _actions.Begin(_follower.Current!, board.Navigation, board.Now);
                context.Log.Info($"nav: {_follower.Current}");
                return RunAction(context, nav);

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

    /// <summary>
    /// Walks after a player's live position: set off once beyond the resume
    /// distance, stop within the stop distance, hold when the player is not
    /// loaded (another landblock, out of range).
    /// </summary>
    private BehaviorStep Follow(BehaviorContext context, NavigationSettings nav)
    {
        Blackboard board = context.Board;
        INavigationAutomation host = context.Surface.Navigation;
        if (board.Navigation.IsPortalSpace)
        {
            _walker.Reset(host);
            return BehaviorStep.Continue;
        }
        if (_walker.ContinueRecovery(host, board.Now))
            return BehaviorStep.Continue;

        uint id = ResolveFollowId(context, nav, board);
        if (id == 0u || !host.TryGetObject(id, out PluginNavigationObject leader))
        {
            _walker.Reset(host);
            _followMoving = false;
            return BehaviorStep.Fail($"{nav.Follow} is not in range");
        }
        double distance = leader.Position.HorizontalDistanceMeters(board.Navigation.Position);
        if (_followMoving ? distance <= nav.FollowStopMeters : distance <= nav.FollowResumeMeters)
        {
            _followMoving = false;
            _walker.Stop(host);
            return BehaviorStep.Continue;
        }
        _followMoving = true;
        float heading = RouteFollower.HeadingTo(board.Navigation.Position, leader.Position);
        StuckRecovery? recovery = _walker.Toward(host, board.Navigation.Position, heading, board.Now, nav.TurnToleranceDegrees);
        if (recovery is { } move)
            _walker.BeginRecovery(host, move, board.Now);
        return BehaviorStep.Continue;
    }

    private uint ResolveFollowId(BehaviorContext context, NavigationSettings nav, Blackboard board)
    {
        if (nav.Follow.Equals("leader", StringComparison.OrdinalIgnoreCase))
        {
            IFellowshipAutomation fellowship = context.Surface.Fellowship;
            return fellowship.IsInFellowship && fellowship.LeaderObjectId != board.SelfId ? fellowship.LeaderObjectId : 0u;
        }
        // A named player: looked up by name now and then, not every tick.
        if (_followId != 0u && context.Surface.Navigation.TryGetObject(_followId, out PluginNavigationObject known)
            && known.Name.Equals(nav.Follow, StringComparison.OrdinalIgnoreCase))
        {
            return _followId;
        }
        if (board.Now - _lastFollowLookupAt < 1d)
            return _followId;
        _lastFollowLookupAt = board.Now;
        _followId = context.Surface.Navigation.TryFindObject(nav.Follow, board.Navigation.Position, 500d, out PluginNavigationObject found)
            ? found.ObjectId
            : 0u;
        return _followId;
    }

    public void Interrupt(BehaviorContext context)
    {
        _walker.Reset(context.Surface.Navigation);
        _followMoving = false;
        // An action mid-flight is abandoned; the step runs again from the
        // start when the route gets control back.
        _actions.Cancel();
    }

    private BehaviorStep RunAction(BehaviorContext context, NavigationSettings nav)
    {
        Blackboard board = context.Board;
        RouteActionStatus status = _actions.Tick(
            context.Surface,
            board.Navigation,
            board.Now,
            nav.PostPortalDelaySeconds,
            context.Log,
            out string failure);
        switch (status)
        {
            case RouteActionStatus.Running:
                return BehaviorStep.Continue;
            case RouteActionStatus.Failed:
                context.Log.Warn($"nav: {failure}; skipping the step");
                _follower!.Complete();
                ForgetPosition();
                return BehaviorStep.Continue;
            default:
                _follower!.Complete();
                ForgetPosition();
                return BehaviorStep.Continue;
        }
    }

    /// <summary>
    /// Watches for a teleport the route did not fire itself: portal space
    /// entered and left, or a jump of more than the action runner's
    /// teleport distance between two ticks. Either way the walk stops and
    /// settles for the post-portal delay before the route carries on from
    /// its current step.
    /// </summary>
    private bool NoticeTeleport(Blackboard board, INavigationAutomation host, NavigationSettings nav, IPluginLogger log)
    {
        PluginNavigationSnapshot navigation = board.Navigation;
        bool teleported = false;
        string how = string.Empty;
        if (navigation.IsPortalSpace)
        {
            _wasInPortalSpace = true;
        }
        else if (_wasInPortalSpace)
        {
            _wasInPortalSpace = false;
            teleported = true;
            how = "left portal space";
        }
        else if (_hasLastPosition
            && navigation.Position.HorizontalDistanceMeters(_lastPosition) > RouteActionRunner.TeleportJumpMeters)
        {
            teleported = true;
            how = "position jumped";
        }
        _lastPosition = navigation.Position;
        _hasLastPosition = !navigation.IsPortalSpace;
        if (!teleported)
            return false;
        log.Info($"nav: teleport noticed ({how}); settling {nav.PostPortalDelaySeconds:0.#}s");
        _walker.Reset(host);
        _settleUntil = board.Now + nav.PostPortalDelaySeconds;
        return true;
    }

    /// <summary>After an action's own teleport the last position is stale; do not read it as a second jump.</summary>
    private void ForgetPosition()
    {
        _hasLastPosition = false;
        _wasInPortalSpace = false;
    }
}
