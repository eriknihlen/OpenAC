using System.Globalization;
using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>
/// The operations both the <c>/drakbot</c> commands and the windows perform:
/// start/stop, profile and route persistence, the draft route being
/// recorded. Keeps the two front ends from drifting apart. It is also what
/// the meta reaches the bot through (<see cref="IMetaBot"/>).
/// </summary>
public sealed class BotController : IMetaBot
{
    private readonly Func<PluginNavigationSnapshot> _navigationSnapshot;
    private readonly IPluginStorage _vtankProfiles;
    private readonly IDungeonAutomation _dungeon;

    public BotController(
        BotEngine engine,
        BotStore store,
        NavigationBehavior navigation,
        SelfBuffBehavior buffs,
        Func<PluginNavigationSnapshot> navigationSnapshot,
        IPluginStorage? vtankProfiles = null,
        IDungeonAutomation? dungeon = null,
        DungeonHazards? hazards = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _navigationSnapshot = navigationSnapshot ?? throw new ArgumentNullException(nameof(navigationSnapshot));
        _vtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        _dungeon = dungeon ?? NoOpAutomationSurface.Instance;
        Hazards = hazards ?? new DungeonHazards(NoOpPluginStorage.Instance);
    }

    public DungeonHazards Hazards { get; }

    public BotEngine Engine { get; }

    public BotStore Store { get; }

    public NavigationBehavior Navigation { get; }

    public SelfBuffBehavior Buffs { get; }

    /// <summary>The meta, once the plugin wires one in.</summary>
    public MetaEngine? Meta
    {
        get => Engine.Meta;
        set => Engine.Meta = value;
    }

    /// <summary>The <c>/drakbot</c> command handler, so a meta's chat action can reach the bot's own verbs.</summary>
    public Func<string, bool>? CommandHandler { get; set; }

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
        ApplyProfileMeta();
        return true;
    }

    /// <summary>Loads the meta the profile names, when it differs from the one loaded.</summary>
    public void ApplyProfileMeta()
    {
        string name = Profile.Meta.Name;
        if (Meta is null || name.Length == 0 || name.Equals(Meta.MetaName, StringComparison.OrdinalIgnoreCase))
            return;
        Meta.LoadByName(name);
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

    public bool LoadRoute(string name) => LoadRoute(name, out _);

    public bool LoadRoute(string name, out string? warning)
    {
        Route? route = Store.LoadRoute(name, out warning);
        if (route is null)
            return false;
        DraftRoute = route;
        Navigation.SetRoute(route);
        return true;
    }

    /// <summary>
    /// Reads a VTank <c>.nav</c> file from disk into the draft route and
    /// starts following it. The route keeps the file's own mode.
    /// </summary>
    public Route ImportNavFile(string path, out string? warning)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        Route route = NavFile.Parse(name, File.ReadAllLines(path), out warning);
        DraftRoute = route;
        Navigation.SetRoute(route);
        return route;
    }

    /// <summary>Writes the draft route as a VTank <c>.nav</c> file.</summary>
    public void ExportNavFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, NavFile.Write(DraftRoute));
    }

    public void AddChat(string text) =>
        DraftRoute = DraftRoute.Append(new Waypoint(WaypointKind.Chat, 0d, 0d) { Text = text });

    public void AddRecall(uint spellId) =>
        DraftRoute = DraftRoute.Append(new Waypoint(WaypointKind.Recall, 0d, 0d) { SpellId = spellId });

    /// <summary>Adds a portal or NPC step for the named object, recorded from where the character stands.</summary>
    public bool TryAddUse(WaypointKind kind, string targetName, out PluginNavigationPosition position)
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        position = snapshot.Position;
        if (!snapshot.IsAvailable)
            return false;
        DraftRoute = DraftRoute.Append(new Waypoint(kind, position.EastWest, position.NorthSouth)
        {
            Elevation = position.Elevation,
            TargetName = targetName,
        });
        return true;
    }

    /// <summary>A UB-style jump command: <c>jump[wxzcs] [heading] [ms]</c>.</summary>
    public bool TryJump(string verb, IReadOnlyList<string> arguments, out string message)
    {
        if (!Jumper.TryParse(verb, arguments, out PluginMovementIntent keys, out float heading, out int milliseconds, out message))
            return false;
        Engine.Jumper.Start(keys, heading, milliseconds, Engine.Clock.Now);
        message = float.IsNaN(heading)
            ? $"jumping ({milliseconds} ms)"
            : $"jumping toward {heading:0} ({milliseconds} ms)";
        return true;
    }

    // ── Dungeon planning ────────────────────────────────────────────────

    /// <summary>The loaded dungeon's cell graph around the character, or null when outdoors or unavailable.</summary>
    private Dictionary<uint, PluginDungeonCell>? DungeonGraph(out PluginNavigationSnapshot snapshot, out string problem)
    {
        snapshot = _navigationSnapshot();
        problem = string.Empty;
        if (!snapshot.IsAvailable)
        {
            problem = "position unknown; not in world?";
            return null;
        }
        if (snapshot.Position.IsOutdoor || (snapshot.Position.CellId & 0xFFFFu) < 0x100u)
        {
            problem = "not in a dungeon";
            return null;
        }
        if (!_dungeon.IsAvailable)
        {
            problem = "the host has no dungeon cell graph";
            return null;
        }
        IReadOnlyList<PluginDungeonCell> cells = _dungeon.CaptureCells(snapshot.Position.CellId);
        if (cells.Count == 0)
        {
            problem = "no cells loaded for this dungeon";
            return null;
        }
        return DungeonPathfinder.Graph(cells);
    }

    /// <summary>
    /// Builds a looping patrol over the dungeon's main route from where the
    /// character stands, avoiding marked hazards, and follows it. Standing in
    /// a hazard, the walk starts from the nearest safe cell.
    /// </summary>
    public bool TryStartPatrol(out string message)
    {
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraph(out PluginNavigationSnapshot snapshot, out message);
        if (graph is null)
            return false;
        IReadOnlySet<uint> hazards = Hazards.For(snapshot.Position.CellId);
        uint start = graph.ContainsKey(snapshot.Position.CellId)
            ? snapshot.Position.CellId
            : DungeonPathfinder.NearestCell(graph, snapshot.Position);
        if (hazards.Contains(start))
        {
            uint safe = DungeonPathfinder.NearestSafeCell(graph, start, hazards);
            if (safe != 0u)
                start = safe;
        }
        Route route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, $"patrol {snapshot.Position.CellId >> 16:X4}");
        if (route.IsEmpty)
        {
            message = "nothing to patrol here";
            return false;
        }
        DraftRoute = route;
        Navigation.SetRoute(route);
        if (!Profile.Navigation.Enabled)
            Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
        message = $"patrolling {graph.Count} cells over {route.Waypoints.Count} steps";
        return true;
    }

    /// <summary>Plans a route through the dungeon to a map coordinate and follows it.</summary>
    public bool TryGoTo(double northSouth, double eastWest, out string message)
    {
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraph(out PluginNavigationSnapshot snapshot, out message);
        if (graph is null)
            return false;
        IReadOnlySet<uint> hazards = Hazards.For(snapshot.Position.CellId);
        uint start = graph.ContainsKey(snapshot.Position.CellId)
            ? snapshot.Position.CellId
            : DungeonPathfinder.NearestCell(graph, snapshot.Position);
        var destination = new PluginNavigationPosition(0u, eastWest, northSouth, snapshot.Position.Elevation, 0f, false);
        uint goal = DungeonPathfinder.NearestCell(graph, destination);
        List<uint> path = DungeonPathfinder.FindPath(graph, start, goal, hazards);
        if (path.Count == 0)
        {
            message = "no walkable way there";
            return false;
        }
        Route route = DungeonPathfinder.BuildRoute(graph, path, destination, "goto");
        DraftRoute = route;
        Navigation.SetRoute(route);
        if (!Profile.Navigation.Enabled)
            Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
        message = $"going there through {path.Count} cells, {route.Waypoints.Count} steps";
        return true;
    }

    /// <summary>Marks the cell the character stands in as a hazard.</summary>
    public bool TryMarkHazard(out string message)
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        if (!snapshot.IsAvailable || (snapshot.Position.CellId & 0xFFFFu) < 0x100u)
        {
            message = "not in a dungeon cell";
            return false;
        }
        message = Hazards.Add(snapshot.Position.CellId)
            ? $"cell 0x{snapshot.Position.CellId:X8} marked as a hazard"
            : $"cell 0x{snapshot.Position.CellId:X8} was already marked";
        return true;
    }

    public bool TryUnmarkHazard(out string message)
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        if (!snapshot.IsAvailable || (snapshot.Position.CellId & 0xFFFFu) < 0x100u)
        {
            message = "not in a dungeon cell";
            return false;
        }
        message = Hazards.Remove(snapshot.Position.CellId)
            ? $"cell 0x{snapshot.Position.CellId:X8} is no longer a hazard"
            : $"cell 0x{snapshot.Position.CellId:X8} was not marked";
        return true;
    }

    public void ClearHazards()
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        if (snapshot.IsAvailable)
            Hazards.Clear(snapshot.Position.CellId);
    }

    // ── The meta's view (IMetaBot) ──────────────────────────────────────

    Route? IMetaBot.CurrentRoute => Navigation.Route;

    int IMetaBot.RouteIndex => Navigation.WaypointIndex;

    void IMetaBot.SetRoute(Route? route, bool enableNavigation)
    {
        if (route is not null)
            DraftRoute = route;
        Navigation.SetRoute(route);
        if (enableNavigation && !Profile.Navigation.Enabled)
            Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
    }

    bool IMetaBot.NeedsAnyBuff(Blackboard board) => Buffs.NeedsAnyBuff(board);

    LoadedMeta? IMetaBot.LoadMetaByName(string name, out string path)
    {
        string safe = BotStore.SanitizeName(name);
        path = safe + ".af";
        string? text = _vtankProfiles.ReadText(path);
        if (text is not null)
            return AfFileParser.LoadFromText(text);
        path = safe + ".met";
        byte[]? bytes = _vtankProfiles.ReadBytes(path);
        if (bytes is not null)
            return MetFileParser.LoadFromBytes(bytes);
        path = string.Empty;
        return null;
    }

    Route? IMetaBot.LoadRouteByName(string name)
    {
        Route? route = Store.LoadRoute(name, out _);
        if (route is not null)
            return route;
        string? nav = _vtankProfiles.ReadText(BotStore.SanitizeName(name) + ".nav");
        return nav is null ? null : NavFile.Parse(name, nav.ReplaceLineEndings("\n").Split('\n'), out _);
    }

    bool IMetaBot.TryHandleCommand(string command)
    {
        if (CommandHandler is null)
            return false;
        if (!command.StartsWith("/drakbot ", StringComparison.OrdinalIgnoreCase)
            && !command.StartsWith("/bot ", StringComparison.OrdinalIgnoreCase))
            return false;
        return CommandHandler(command[(command.IndexOf(' ') + 1)..]);
    }

    /// <summary>
    /// The profile fields a meta may read and set through
    /// <c>vtsetsetting</c>, <c>ragetsetting</c> and <c>/vt opt set</c>,
    /// under the names RynthAi and VTank metas use for them.
    /// </summary>
    Dictionary<string, (Func<string> Get, Action<string> Set)> IMetaBot.OptionMap()
    {
        static string B(bool value) => value ? "1" : "0";
        static bool ToBool(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number != 0d
                : value.Equals("true", StringComparison.OrdinalIgnoreCase);
        static string F(double value) => value.ToString("G", CultureInfo.InvariantCulture);
        static bool TryF(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        // VTank recharge thresholds are percentages; the profile keeps fractions.
        static string Pct(double fraction) => Math.Round(fraction * 100d).ToString(CultureInfo.InvariantCulture);

        return new Dictionary<string, (Func<string> Get, Action<string> Set)>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableCombat"] = (() => B(Profile.Combat.Enabled), v => Update(p => p with { Combat = p.Combat with { Enabled = ToBool(v) } })),
            ["EnableBuffing"] = (() => B(Profile.Buffs.Enabled), v => Update(p => p with { Buffs = p.Buffs with { Enabled = ToBool(v) } })),
            ["EnableNavigation"] = (() => B(Profile.Navigation.Enabled), v => Update(p => p with { Navigation = p.Navigation with { Enabled = ToBool(v) } })),
            ["EnableLooting"] = (() => B(Profile.Loot.Enabled), v => Update(p => p with { Loot = p.Loot with { Enabled = ToBool(v) } })),
            ["EnableMeta"] = (() => B(Meta?.Enabled ?? false), v => { if (Meta is not null) Meta.Enabled = ToBool(v); }),
            ["MonsterRange"] = (() => F(Profile.Combat.EngageDistance), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { EngageDistance = (float)n } }); }),
            ["ApproachRange"] = (() => F(Profile.Combat.ApproachRangeMeters), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { ApproachRangeMeters = (float)n } }); }),
            ["MeleeAttackPower"] = (() => Pct(Profile.Combat.Power), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { Power = (float)Math.Clamp(n / 100d, 0d, 1d) } }); }),
            ["MissileAttackPower"] = (() => Pct(Profile.Combat.Power), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { Power = (float)Math.Clamp(n / 100d, 0d, 1d) } }); }),
            ["HealAt"] = (() => Pct(Profile.Vitals.HealBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { HealBelow = n / 100d } }); }),
            ["RestamAt"] = (() => Pct(Profile.Vitals.StaminaBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { StaminaBelow = n / 100d } }); }),
            ["GetManaAt"] = (() => Pct(Profile.Vitals.ManaBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { ManaBelow = n / 100d } }); }),
            ["TopOffHP"] = (() => Pct(Profile.Vitals.IdleHealthBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { IdleHealthBelow = n / 100d } }); }),
            ["TopOffStam"] = (() => Pct(Profile.Vitals.IdleStaminaBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { IdleStaminaBelow = n / 100d } }); }),
            ["TopOffMana"] = (() => Pct(Profile.Vitals.IdleManaBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { IdleManaBelow = n / 100d } }); }),
            ["HealOthersAt"] = (() => Pct(Profile.Vitals.HealFellowsBelow), v => { if (TryF(v, out double n)) Update(p => p with { Vitals = p.Vitals with { HealFellowsBelow = n / 100d } }); }),
            ["RebuffSecondsRemaining"] = (() => F(Profile.Buffs.RebuffWhenRemainingSeconds), v => { if (TryF(v, out double n)) Update(p => p with { Buffs = p.Buffs with { RebuffWhenRemainingSeconds = n } }); }),
            ["RingRange"] = (() => F(Profile.Combat.RingRangeMeters), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { RingRangeMeters = (float)n } }); }),
            ["MinRingTargets"] = (() => F(Profile.Combat.MinRingTargets), v => { if (TryF(v, out double n)) Update(p => p with { Combat = p.Combat with { MinRingTargets = (int)n } }); }),
            ["PeaceModeWhenIdle"] = (() => B(Profile.Combat.LeaveCombatWhenIdle), v => Update(p => p with { Combat = p.Combat with { LeaveCombatWhenIdle = ToBool(v) } })),
            ["CurrentNavPath"] = (() => Navigation.Route?.Name ?? string.Empty, v =>
            {
                Route? route = ((IMetaBot)this).LoadRouteByName(v);
                if (route is not null)
                    ((IMetaBot)this).SetRoute(route, false);
            }),
            ["CurrentMetaPath"] = (() => Meta?.MetaName ?? string.Empty, v => Meta?.LoadByName(v)),
        };
    }
}
