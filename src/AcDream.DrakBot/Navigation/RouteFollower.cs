using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// Pure route-walking arithmetic: which waypoint is next and how to reach
/// it. Two habits from RynthSuite's navigation engine keep a runner from
/// circling: a waypoint counts as passed once the character was near it and
/// the distance starts growing again (the closest approach), and near a
/// waypoint the aim point blends toward the next one so a corner is cut
/// rather than squared.
/// </summary>
public sealed class RouteFollower
{
    /// <summary>The closest approach counts inside this many arrival distances.</summary>
    public const double SweepMultiplier = 2.5;

    /// <summary>The distance must grow by this much past the closest approach.</summary>
    public const double SweepGrowthMeters = 0.3;

    private int _index;
    private int _direction = 1;
    private double _pauseUntil = double.NegativeInfinity;
    private double _closest = double.PositiveInfinity;

    public RouteFollower(Route route, RouteMode mode)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Mode = mode;
    }

    public Route Route { get; }

    public RouteMode Mode { get; }

    public int CurrentIndex => _index;

    public bool IsFinished { get; private set; }

    public Waypoint? Current =>
        Route.IsEmpty || IsFinished ? null : Route.Waypoints[_index];

    /// <summary>Compass heading (0 = north, 90 = east) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static float HeadingTo(in PluginNavigationPosition from, in PluginNavigationPosition to)
    {
        double east = to.EastWest - from.EastWest;
        double north = to.NorthSouth - from.NorthSouth;
        double heading = 450d - Math.Atan2(north, east) * (180d / Math.PI);
        heading %= 360d;
        if (heading < 0d)
            heading += 360d;
        return (float)heading;
    }

    /// <summary>Signed smallest turn from <paramref name="current"/> to <paramref name="target"/>, in (-180, 180].</summary>
    public static float HeadingDelta(float current, float target)
    {
        float delta = (target - current) % 360f;
        if (delta > 180f)
            delta -= 360f;
        if (delta <= -180f)
            delta += 360f;
        return delta;
    }

    /// <summary>
    /// Decides the next movement toward the current waypoint. Advances the
    /// waypoint cursor as points are reached and pauses elapse.
    /// </summary>
    public NavigationStep Advance(
        in PluginNavigationPosition position,
        double now,
        double arrivalDistanceMeters,
        float turnToleranceDegrees,
        double lookaheadMeters = 0d)
    {
        Waypoint? waypoint = Current;
        if (waypoint is null)
            return NavigationStep.Finished;

        if (waypoint.Kind == WaypointKind.Pause)
        {
            if (double.IsNegativeInfinity(_pauseUntil))
                _pauseUntil = now + waypoint.Seconds;
            if (now < _pauseUntil)
                return NavigationStep.Hold;
            _pauseUntil = double.NegativeInfinity;
            Step();
            return Current is null ? NavigationStep.Finished : NavigationStep.Hold;
        }

        PluginNavigationPosition target = waypoint.ToPosition();
        double distance = position.HorizontalDistanceMeters(target);
        if (distance <= arrivalDistanceMeters
            || (_closest < arrivalDistanceMeters * SweepMultiplier && distance > _closest + SweepGrowthMeters))
        {
            Step();
            return Current is null ? NavigationStep.Finished : NavigationStep.Hold;
        }
        _closest = Math.Min(_closest, distance);

        // Cut the corner: inside the lookahead, aim between this waypoint
        // and the next travel point in proportion to how close this one is.
        if (lookaheadMeters > 0d && distance < lookaheadMeters && TryPeekNext(out Waypoint? next))
        {
            double t = 1d - distance / lookaheadMeters;
            target = target with
            {
                EastWest = target.EastWest + (next.EastWest - target.EastWest) * t,
                NorthSouth = target.NorthSouth + (next.NorthSouth - target.NorthSouth) * t,
            };
        }

        float heading = HeadingTo(position, target);
        float delta = HeadingDelta(position.HeadingDegrees, heading);
        return Math.Abs(delta) > turnToleranceDegrees
            ? NavigationStep.Turn(heading, distance)
            : NavigationStep.Walk(heading, distance);
    }

    public void Reset()
    {
        _index = 0;
        _direction = 1;
        _pauseUntil = double.NegativeInfinity;
        _closest = double.PositiveInfinity;
        IsFinished = Route.IsEmpty;
    }

    /// <summary>Restart from the waypoint nearest to <paramref name="position"/>.</summary>
    public void ResumeNearest(in PluginNavigationPosition position)
    {
        Reset();
        double best = double.PositiveInfinity;
        for (int index = 0; index < Route.Waypoints.Count; index++)
        {
            Waypoint waypoint = Route.Waypoints[index];
            if (waypoint.Kind != WaypointKind.Point)
                continue;
            double distance = position.HorizontalDistanceMeters(waypoint.ToPosition());
            if (distance < best)
            {
                best = distance;
                _index = index;
            }
        }
    }

    /// <summary>The next waypoint the cursor would step to, when it is a travel point.</summary>
    private bool TryPeekNext(out Waypoint next)
    {
        int count = Route.Waypoints.Count;
        int index;
        switch (Mode)
        {
            case RouteMode.Once:
                index = _index + 1 < count ? _index + 1 : -1;
                break;
            case RouteMode.PingPong:
                // At either end the cursor bounces, so the next point is the one behind.
                bool bounces = _index + _direction < 0 || _index + _direction >= count;
                index = count <= 1 ? -1 : _index + (bounces ? -_direction : _direction);
                break;
            default:
                index = (_index + 1) % count;
                break;
        }
        if (index < 0 || index == _index || Route.Waypoints[index].Kind != WaypointKind.Point)
        {
            next = null!;
            return false;
        }
        next = Route.Waypoints[index];
        return true;
    }

    private void Step()
    {
        _closest = double.PositiveInfinity;
        int count = Route.Waypoints.Count;
        switch (Mode)
        {
            case RouteMode.Once:
                if (_index + 1 >= count)
                    IsFinished = true;
                else
                    _index++;
                break;
            case RouteMode.PingPong:
                if (count <= 1)
                {
                    IsFinished = true;
                    break;
                }
                if (_index + _direction < 0 || _index + _direction >= count)
                    _direction = -_direction;
                _index += _direction;
                break;
            default:
                _index = (_index + 1) % count;
                break;
        }
    }
}

public enum NavigationAction
{
    Hold = 0,
    Turn,
    Walk,
    Finished,
}

public readonly record struct NavigationStep(
    NavigationAction Action,
    float HeadingDegrees,
    double DistanceMeters)
{
    public static NavigationStep Hold { get; } = new(NavigationAction.Hold, 0f, 0d);

    public static NavigationStep Finished { get; } = new(NavigationAction.Finished, 0f, 0d);

    public static NavigationStep Turn(float heading, double distance) =>
        new(NavigationAction.Turn, heading, distance);

    public static NavigationStep Walk(float heading, double distance) =>
        new(NavigationAction.Walk, heading, distance);
}
