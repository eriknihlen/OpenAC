using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Navigation;

public enum RouteActionStatus
{
    Running = 0,
    Done,
    Failed,
}

/// <summary>
/// Runs the steps of a route that are not walking: a chat line, a recall
/// spell, a portal, an NPC or vendor to use. A teleporting step is a small
/// state machine - settle, fire, watch for the teleport, settle again - with
/// the shape RynthSuite's navigation engine arrived at: the cast is retried
/// until the teleport shows, the portal is looked for again until it is in
/// the object table, and the whole thing gives up after a timeout so a route
/// never wedges on one step. A teleport is recognised three ways: the
/// character passed through portal space, moved more than
/// <see cref="TeleportJumpMeters"/>, or changed landblock.
/// </summary>
public sealed class RouteActionRunner
{
    public const double SettleSeconds = 0.6;
    public const double RecallRetrySeconds = 4d;
    public const double PortalRetrySeconds = 1.5;
    public const double UseSettleSeconds = 1.5;
    public const double TimeoutSeconds = 60d;
    public const double TeleportJumpMeters = 50d;
    public const double SearchRadiusMeters = 250d;

    private enum Phase
    {
        Idle,
        Settling,
        Firing,
        PostTeleport,
    }

    private Phase _phase;
    private Waypoint? _waypoint;
    private double _phaseStartedAt;
    private double _startedAt;
    private double _lastFiredAt = double.NegativeInfinity;
    private bool _fired;
    private bool _sawPortalSpace;
    private PluginNavigationPosition _origin;
    private uint _originLandblock;

    public bool IsRunning => _phase != Phase.Idle;

    public Waypoint? Waypoint => _waypoint;

    public string Status { get; private set; } = string.Empty;

    /// <summary>The object a portal or NPC step resolved to, for the dashboard; zero until found.</summary>
    public uint TargetObjectId { get; private set; }

    public void Begin(Waypoint waypoint, in PluginNavigationSnapshot navigation, double now)
    {
        ArgumentNullException.ThrowIfNull(waypoint);
        _waypoint = waypoint;
        _phase = Phase.Settling;
        _phaseStartedAt = now;
        _startedAt = now;
        _lastFiredAt = double.NegativeInfinity;
        _fired = false;
        _sawPortalSpace = false;
        _origin = navigation.Position;
        _originLandblock = navigation.Position.CellId >> 16;
        TargetObjectId = 0u;
        Status = $"{waypoint}: settling";
    }

    public void Cancel()
    {
        _phase = Phase.Idle;
        _waypoint = null;
        TargetObjectId = 0u;
        Status = string.Empty;
    }

    /// <summary>One tick. The walker must already be stopped.</summary>
    public RouteActionStatus Tick(
        IAutomationSurface surface,
        in PluginNavigationSnapshot navigation,
        double now,
        double postTeleportSeconds,
        IPluginLogger log,
        out string failure)
    {
        failure = string.Empty;
        if (_waypoint is null || _phase == Phase.Idle)
            return RouteActionStatus.Done;
        Waypoint waypoint = _waypoint;

        if (now - _startedAt > TimeoutSeconds + postTeleportSeconds)
        {
            failure = $"{waypoint}: gave up after {TimeoutSeconds:0}s";
            Cancel();
            return RouteActionStatus.Failed;
        }

        switch (_phase)
        {
            case Phase.Settling:
                if (now - _phaseStartedAt < SettleSeconds)
                    return RouteActionStatus.Running;
                // Chat needs no settling beyond a stopped character: send and move on.
                if (waypoint.Kind == WaypointKind.Chat)
                {
                    if (!surface.Chat.Submit(waypoint.Text))
                        log.Warn($"nav: chat line was not accepted: {waypoint.Text}");
                    Cancel();
                    return RouteActionStatus.Done;
                }
                _phase = Phase.Firing;
                _phaseStartedAt = now;
                // The teleport is judged from where the character stands as the action fires.
                _origin = navigation.Position;
                _originLandblock = navigation.Position.CellId >> 16;
                return RouteActionStatus.Running;

            case Phase.Firing:
                if (waypoint.Kind is WaypointKind.Recall or WaypointKind.Portal)
                {
                    if (TeleportSeen(navigation, out string how))
                    {
                        log.Info($"nav: teleport seen ({how}); settling {postTeleportSeconds:0.#}s");
                        _phase = Phase.PostTeleport;
                        _phaseStartedAt = now;
                        Status = $"{waypoint}: teleported, settling";
                        return RouteActionStatus.Running;
                    }
                }
                return Fire(surface, navigation, now, log, out failure);

            case Phase.PostTeleport:
                if (now - _phaseStartedAt < postTeleportSeconds)
                    return RouteActionStatus.Running;
                Cancel();
                return RouteActionStatus.Done;

            default:
                return RouteActionStatus.Done;
        }
    }

    private RouteActionStatus Fire(
        IAutomationSurface surface,
        in PluginNavigationSnapshot navigation,
        double now,
        IPluginLogger log,
        out string failure)
    {
        failure = string.Empty;
        Waypoint waypoint = _waypoint!;
        switch (waypoint.Kind)
        {
            case WaypointKind.Recall:
                // Retried until the teleport shows, so a fizzle does not strand the route.
                if (now - _lastFiredAt >= RecallRetrySeconds)
                {
                    PluginCastRequestResult result = surface.Magic.RequestCast(waypoint.SpellId);
                    _lastFiredAt = now;
                    Status = $"{waypoint}: cast {result}";
                    if (result is PluginCastRequestResult.UnknownSpell)
                    {
                        failure = $"recall spell {waypoint.SpellId} is not known";
                        Cancel();
                        return RouteActionStatus.Failed;
                    }
                }
                return RouteActionStatus.Running;

            case WaypointKind.Portal:
                // Fire once: a second use would cancel the walk the first began.
                // Until the object is in the table (it can take a moment after a
                // teleport) look again every so often.
                if (!_fired && now - _lastFiredAt >= PortalRetrySeconds)
                {
                    _lastFiredAt = now;
                    if (TryUseNamed(surface, navigation, waypoint, log))
                        _fired = true;
                }
                return RouteActionStatus.Running;

            case WaypointKind.Npc:
            case WaypointKind.Vendor:
                if (!_fired)
                {
                    if (now - _lastFiredAt < PortalRetrySeconds)
                        return RouteActionStatus.Running;
                    _lastFiredAt = now;
                    if (!TryUseNamed(surface, navigation, waypoint, log))
                        return RouteActionStatus.Running;
                    _fired = true;
                    _phaseStartedAt = now;
                    return RouteActionStatus.Running;
                }
                // Give the use a moment to land (the dialogue or vendor window opens), then move on.
                if (now - _phaseStartedAt < UseSettleSeconds)
                    return RouteActionStatus.Running;
                Cancel();
                return RouteActionStatus.Done;

            default:
                Cancel();
                return RouteActionStatus.Done;
        }
    }

    private bool TeleportSeen(in PluginNavigationSnapshot navigation, out string how)
    {
        if (navigation.IsPortalSpace)
        {
            _sawPortalSpace = true;
            how = string.Empty;
            return false;
        }
        if (_sawPortalSpace)
        {
            how = "left portal space";
            return true;
        }
        if (navigation.Position.HorizontalDistanceMeters(_origin) > TeleportJumpMeters)
        {
            how = "position jumped";
            return true;
        }
        uint landblock = navigation.Position.CellId >> 16;
        if (_originLandblock != 0u && landblock != 0u && landblock != _originLandblock)
        {
            how = "landblock changed";
            return true;
        }
        how = string.Empty;
        return false;
    }

    /// <summary>
    /// Finds the named object and uses it. The recorded position wins between
    /// same-named objects; otherwise the nearest within the search radius.
    /// </summary>
    private bool TryUseNamed(
        IAutomationSurface surface,
        in PluginNavigationSnapshot navigation,
        Waypoint waypoint,
        IPluginLogger log)
    {
        string name = waypoint.TargetName.Trim();
        if (name.Length == 0)
        {
            Status = $"{waypoint}: no name";
            return false;
        }
        PluginNavigationPosition near = waypoint.HasTargetPosition
            ? waypoint.TargetPosition()
            : navigation.Position;

        uint bestId = waypoint.Kind == WaypointKind.Vendor ? waypoint.VendorId : 0u;
        double best = double.PositiveInfinity;
        if (bestId == 0u)
        {
            foreach (PluginWorldObject candidate in surface.Objects.CaptureObjects())
            {
                if (candidate.IsOwned || !candidate.HasPosition)
                    continue;
                if (!Matches(candidate.Name, name))
                    continue;
                double distance = candidate.Position.HorizontalDistanceMeters(near);
                if (distance > SearchRadiusMeters || distance >= best)
                    continue;
                best = distance;
                bestId = candidate.ObjectId;
            }
        }
        if (bestId == 0u)
        {
            Status = $"{waypoint}: looking for '{name}'";
            return false;
        }

        PluginItemCommandResult result = surface.Objects.Use(bestId);
        if (result.Status is not PluginItemCommandStatus.Started)
        {
            Status = $"{waypoint}: use {result.Status}";
            log.Info($"nav: use of '{name}' (0x{bestId:X8}) {result.Status}; will retry");
            return false;
        }
        TargetObjectId = bestId;
        Status = $"{waypoint}: using";
        log.Info($"nav: using '{name}' 0x{bestId:X8}");
        return true;
    }

    private static bool Matches(string candidate, string wanted) =>
        candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)
        || candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase);
}
