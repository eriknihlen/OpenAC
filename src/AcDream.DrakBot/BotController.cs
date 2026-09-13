using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>
/// The operations both the <c>/bot</c> commands and the windows perform:
/// start/stop, profile and route persistence, the draft route being
/// recorded. Keeps the two front ends from drifting apart.
/// </summary>
public sealed class BotController
{
    private readonly Func<PluginNavigationSnapshot> _navigationSnapshot;

    public BotController(
        BotEngine engine,
        BotStore store,
        NavigationBehavior navigation,
        SelfBuffBehavior buffs,
        Func<PluginNavigationSnapshot> navigationSnapshot)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _navigationSnapshot = navigationSnapshot ?? throw new ArgumentNullException(nameof(navigationSnapshot));
    }

    public BotEngine Engine { get; }

    public BotStore Store { get; }

    public NavigationBehavior Navigation { get; }

    public SelfBuffBehavior Buffs { get; }

    public Route DraftRoute { get; private set; } = new() { Name = "draft" };

    public BotProfile Profile
    {
        get => Engine.Profile;
        set => Engine.Profile = value ?? throw new ArgumentNullException(nameof(value));
    }

    public void Start() => Engine.Start();

    public void Stop() => Engine.Stop();

    public void Toggle()
    {
        if (Engine.IsRunning)
            Engine.Stop();
        else
            Engine.Start();
    }

    public void Update(Func<BotProfile, BotProfile> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Engine.Profile = change(Engine.Profile);
    }

    // ── Profiles ────────────────────────────────────────────────────────

    public bool LoadProfile(string name)
    {
        BotProfile? loaded = Store.LoadProfile(name);
        if (loaded is null)
            return false;
        Engine.Profile = loaded;
        return true;
    }

    public void SaveProfile(string? name = null)
    {
        BotProfile profile = Engine.Profile with { Name = name ?? Engine.Profile.Name };
        Store.SaveProfile(profile);
        Engine.Profile = profile;
    }

    public void ResetProfile() => Engine.Profile = BotProfile.Default;

    // ── Routes ──────────────────────────────────────────────────────────

    /// <summary>Records the character's position as the next draft waypoint.</summary>
    public bool TryAddWaypoint(out PluginNavigationPosition position)
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        position = snapshot.Position;
        if (!snapshot.IsAvailable)
            return false;
        DraftRoute = DraftRoute.Append(Waypoint.At(snapshot.Position));
        return true;
    }

    public void AddPause(double seconds) =>
        DraftRoute = DraftRoute.Append(new Waypoint(WaypointKind.Pause, 0d, 0d) { Seconds = seconds });

    public void RemoveWaypoint(int index)
    {
        if (index < 0 || index >= DraftRoute.Waypoints.Count)
            return;
        List<Waypoint> waypoints = [.. DraftRoute.Waypoints];
        waypoints.RemoveAt(index);
        DraftRoute = DraftRoute with { Waypoints = waypoints };
    }

    public void ClearRoute()
    {
        DraftRoute = new Route { Name = "draft" };
        Navigation.SetRoute(null);
    }

    /// <summary>Starts following the draft route.</summary>
    public void UseDraftRoute() => Navigation.SetRoute(DraftRoute);

    public void SaveRoute(string name)
    {
        Route named = DraftRoute with { Name = name };
        Store.SaveRoute(named);
        DraftRoute = named;
    }

    public bool LoadRoute(string name)
    {
        Route? route = Store.LoadRoute(name);
        if (route is null)
            return false;
        DraftRoute = route;
        Navigation.SetRoute(route);
        return true;
    }
}
