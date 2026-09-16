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
        DungeonHazards? hazards = null,
        BotLog? log = null)
    {
        Log = log ?? new BotLog(NoOpPluginLogger.Instance);
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        Navigation.Rejoin = RejoinRoute;
        Navigation.JumpToward = heading => Engine.Jumper.Start(new PluginMovementIntent(Forward: true), heading, 350, Engine.Clock.Now);
        Buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _navigationSnapshot = navigationSnapshot ?? throw new ArgumentNullException(nameof(navigationSnapshot));
        _vtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        Files = new BotFiles(store.Storage, _vtankProfiles);
        Files.EnsureLayout();
        _dungeon = dungeon ?? NoOpAutomationSurface.Instance;
        Hazards = hazards ?? new DungeonHazards(NoOpPluginStorage.Instance);
        foreach (IBehavior behavior in Engine.Behaviors)
        {
            if (behavior is CombatBehavior combat)
                combat.IsHazardCell = cell => Hazards.For(cell).Contains(cell);
        }
        Navigation.GaveUp += (cell, key) => Hazards.AddGivenUp(cell, key);
    }

    public DungeonHazards Hazards { get; }

    /// <summary>The bot's files on disk: the folder layout, the VTank-format files, the log dump.</summary>
    public BotFiles Files { get; }

    /// <summary>The bot's log ring; also what the engine and behaviors write to.</summary>
    public BotLog Log { get; }

    /// <summary>
    /// Visible objects named like a hotspot (lava, acid pool, magma...)
    /// mark their cell as a hazard when sighted, the way RynthAi's object
    /// cache does; the patrol reroutes around a new one.
    /// </summary>
    private static readonly string[] HazardNames =
    [
        "lava", "pool of acid", "acid pool", "pool of fire", "pool of cold", "cesspool", "hot spring", "magma",
    ];

    /// <summary>The landscape scan the hazard watch reads; set by the plugin.</summary>
    public Func<IReadOnlyList<PluginWorldObject>>? ObjectScan { get; set; }

    private double _lastHazardScanAt = double.NegativeInfinity;
    private double _loginPatrolFirstTryAt = double.NaN;
    private bool _loginPatrolWaitTold;
    private bool _loginPatrolOutdoorsTold;
    /// <summary>How long the login patrol keeps asking for the dungeon before it stops trying.</summary>
    public const double LoginPatrolGiveUpSeconds = 120d;

    /// <summary>A changed profile is written this long after the last change, so a dragged slider is one write.</summary>
    public const double AutoSaveDelaySeconds = 1d;
    private double _profileDirtyAt = double.NaN;
    private uint _patrolLandblock;
    private bool _patrolOnLoginDone;

    /// <summary>The route being walked is a dungeon patrol built here.</summary>
    public bool IsPatrolling => _patrolLandblock != 0u && Navigation.Route is { } route && route.Name.StartsWith("patrol ", StringComparison.Ordinal);

    /// <summary>Hazard cells marked for the dungeon the character is in.</summary>
    public int HazardCount
    {
        get
        {
            PluginNavigationSnapshot snapshot = _navigationSnapshot();
            return snapshot.IsAvailable ? Hazards.For(snapshot.Position.CellId).Count : 0;
        }
    }

    /// <summary>
    /// Once a second: sight hazards among the visible objects and reroute an
    /// active patrol around a new one; start the login patrol when asked.
    /// </summary>
    public void Tick(double now)
    {
        if (!double.IsNaN(_profileDirtyAt) && now - _profileDirtyAt >= AutoSaveDelaySeconds)
            FlushProfile();
        if (now - _lastHazardScanAt < 1d)
            return;
        _lastHazardScanAt = now;
        DrainCommandFile();
        LogChat();
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        bool loginPatrolPending = Profile.Navigation.PatrolOnLogin && !_patrolOnLoginDone;
        if (!snapshot.IsAvailable)
        {
            if (loginPatrolPending && !_loginPatrolWaitTold)
            {
                _loginPatrolWaitTold = true;
                Log.Info("patrol: waiting for the character's position before starting on login");
            }
            return;
        }
        bool inDungeon = !snapshot.Position.IsOutdoor && (snapshot.Position.CellId & 0xFFFFu) >= 0x100u;
        if (loginPatrolPending && !inDungeon && !_loginPatrolOutdoorsTold)
        {
            // Outdoors there is nothing to patrol yet; said once, and the
            // patrol starts when the character walks into a dungeon.
            _loginPatrolOutdoorsTold = true;
            Log.Info($"patrol: not started on login; the character is outdoors in 0x{snapshot.Position.CellId:X8} and the patrol starts in the first dungeon it enters");
        }

        if (loginPatrolPending && inDungeon)
        {
            // The dungeon's cells stream in some seconds after the character
            // does; a try that finds none is not the answer, so this keeps
            // asking once a second until the patrol is built (and gives up
            // quietly if the bot has been started by hand meanwhile).
            if (Engine.IsRunning)
            {
                _patrolOnLoginDone = true;
            }
            else if (TryStartPatrol(out string message, quiet: true))
            {
                _patrolOnLoginDone = true;
                Log.Info(double.IsNaN(_loginPatrolFirstTryAt)
                    ? "patrol: started on login"
                    : $"patrol: started on login after {now - _loginPatrolFirstTryAt:0}s");
                Engine.Start();
            }
            else if (double.IsNaN(_loginPatrolFirstTryAt))
            {
                _loginPatrolFirstTryAt = now;
                Log.Info($"patrol: waiting to start on login ({message})");
            }
            else if (now - _loginPatrolFirstTryAt > LoginPatrolGiveUpSeconds)
            {
                _patrolOnLoginDone = true;
                Log.Warn($"patrol: not started on login after {LoginPatrolGiveUpSeconds:0}s ({message}); use Dungeon Patrol when ready");
            }
        }

        // A patrol belongs to its dungeon. Teleported out of it - an admin
        // command, a portal, a recall - the route is twenty kilometres
        // away and the walk would detour at the horizon for ever: in a
        // new dungeon the patrol is rebuilt for it, outdoors it is put
        // down with a word.
        if (IsPatrolling && _patrolLandblock != (snapshot.Position.CellId & 0xFFFF0000u))
        {
            if (inDungeon)
            {
                Log.Warn($"patrol: the character is in another dungeon now (0x{snapshot.Position.CellId & 0xFFFF0000u:X8}); building a patrol for it");
                if (!TryStartPatrol(out string problem, quiet: true))
                    Log.Warn($"patrol: could not patrol here ({problem}); route cleared");
            }
            else
            {
                Log.Warn($"patrol: the character is outdoors at {BotEngine.Describe(snapshot.Position)}, far from its dungeon; patrol stopped");
                ClearRoute();
                _patrolLandblock = 0u;
                // Back in a dungeon later, the login patrol may start again.
                _patrolOnLoginDone = false;
                _loginPatrolFirstTryAt = double.NaN;
            }
            return;
        }

        if (!inDungeon || ObjectScan is null)
            return;
        bool sighted = false;
        foreach (PluginWorldObject candidate in ObjectScan())
        {
            if (candidate.IsOwned || !candidate.HasPosition || candidate.Position.IsOutdoor)
                continue;
            uint cell = candidate.Position.CellId;
            if ((cell & 0xFFFFu) < 0x100u || !IsHazardName(candidate.Name))
                continue;
            if (Hazards.Add(cell))
            {
                sighted = true;
                Log.Info($"hazard: '{candidate.Name}' sighted in cell 0x{cell:X8}; marked");
            }
        }
        if (sighted && IsPatrolling && _patrolLandblock == (snapshot.Position.CellId & 0xFFFF0000u))
            RebuildPatrol();
    }

    private bool IsNoPatrolLandblock(uint cellId)
    {
        string landblock = (cellId >> 16).ToString("X4");
        foreach (string excluded in Profile.Navigation.NoPatrolLandblocks)
        {
            if (string.Equals(excluded.Trim().TrimStart('0', 'x', 'X').PadLeft(4, '0'), landblock, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsHazardName(string name)
    {
        foreach (string pattern in HazardNames)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The route being walked, rejoined from where the character stands by
    /// a path through the dungeon's doorways; null outside a dungeon or
    /// when no path helps. Also what patrol resumes by after a fight
    /// dragged the character into another room.
    /// </summary>
    private Route? RejoinRoute(PluginNavigationPosition position, int index)
    {
        Route? route = Navigation.Route;
        if (route is null)
            return null;
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraphCore(out _, out _);
        if (graph is null)
            return null;
        Route? rejoined = DungeonPathfinder.Rejoin(graph, route, index, position, Hazards.For(position.CellId), BlockedEdges(graph, position.CellId));
        if (rejoined is not null && route.Name == DraftRoute?.Name)
            DraftRoute = rejoined;
        return rejoined;
    }

    /// <summary>
    /// The crossings the walk has given up on in this landblock, as edges of
    /// the graph: a route point behind a wall was given up from a cell, and
    /// the doorway of that cell nearest the point is the crossing the body
    /// could not make. A path that took it again would bring the walk back
    /// to the same wall - the lead-in did, three times a minute, once.
    /// </summary>
    private HashSet<ulong> BlockedEdges(Dictionary<uint, PluginDungeonCell> graph, uint anyCellOfLandblock)
    {
        var edges = new HashSet<ulong>();
        foreach (string key in Hazards.GivenUpFor(anyCellOfLandblock))
        {
            // "{cell:X8}:{ew*960}:{ns*960}", as NavigationBehavior.GivenUpKey writes it.
            string[] parts = key.Split(':');
            if (parts.Length != 3
                || !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint cell)
                || !long.TryParse(parts[1], out long ew) || !long.TryParse(parts[2], out long ns))
            {
                continue;
            }
            var point = new PluginNavigationPosition(cell, ew / 960d, ns / 960d, 0d, 0f, false);
            ulong edge = DungeonPathfinder.GivenUpEdge(graph, cell, point);
            if (edge != 0ul)
                edges.Add(edge);
        }
        return edges;
    }

    /// <summary>
    /// The dungeon as the bot sees it, to a file: every cell with its
    /// position, neighbours and doorways, the hazards, the given-up
    /// crossings, and the patrol as built - for working out, away from the
    /// client, why a wing is not walked.
    /// </summary>
    public bool TryDumpDungeon(out string message)
    {
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraph(out PluginNavigationSnapshot snapshot, out string problem);
        if (graph is null)
        {
            message = problem;
            return false;
        }
        uint landblock = snapshot.Position.CellId & 0xFFFF0000u;
        IReadOnlySet<uint> hazards = Hazards.For(snapshot.Position.CellId);
        HashSet<ulong> blocked = BlockedEdges(graph, snapshot.Position.CellId);
        var dump = new
        {
            landblock = $"{landblock >> 16:X4}",
            at = new { cell = snapshot.Position.CellId, ew = snapshot.Position.EastWest, ns = snapshot.Position.NorthSouth, z = snapshot.Position.Elevation },
            hazards = hazards.OrderBy(h => h).ToArray(),
            givenUp = Hazards.GivenUpFor(snapshot.Position.CellId).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            blockedEdges = blocked.Select(e => new { a = (uint)(e >> 32), b = (uint)(e & 0xFFFFFFFFu) }).ToArray(),
            cells = graph.Values.OrderBy(c => c.CellId).Select(c => new
            {
                id = c.CellId,
                ew = c.EastWest,
                ns = c.NorthSouth,
                z = c.Elevation,
                neighbors = c.Neighbors,
                doorways = c.Doorways.Select(d => new { to = d.OtherCellId, ew = d.EastWest, ns = d.NorthSouth, z = d.Elevation, floor = d.IsFloorOpening }).ToArray(),
            }).ToArray(),
            patrol = Navigation.Route is { } route
                ? route.Waypoints.Select(w => new { kind = w.Kind.ToString(), ew = w.EastWest, ns = w.NorthSouth, z = w.Elevation }).ToArray()
                : [],
        };
        string json = System.Text.Json.JsonSerializer.Serialize(dump, BotProfile.JsonOptions);
        string path = Files.WriteDungeonDump($"{landblock >> 16:X4}", json);
        message = $"dungeon {landblock >> 16:X4}: {graph.Count} cells, {hazards.Count} hazards, {blocked.Count} closed crossings -> {path}";
        return true;
    }

    /// <summary>A new hazard mid-patrol: the same patrol again around it, resumed at the nearest step.</summary>
    private void RebuildPatrol()
    {
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraph(out PluginNavigationSnapshot snapshot, out _);
        if (graph is null)
            return;
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
        Route route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, $"patrol {snapshot.Position.CellId >> 16:X4}", Profile.Navigation.CrossHazards, BlockedEdges(graph, snapshot.Position.CellId));
        if (route.IsEmpty)
        {
            Log.Warn("patrol: nothing left to walk after the new hazard");
            return;
        }
        Log.Info($"patrol: rebuilt around {hazards.Count} hazard cell(s): {route.Waypoints.Count} steps");
        LogRoute(route, graph);
        DraftRoute = route;
        Navigation.ReplaceRoute(route);
    }

    /// <summary>Every step of a planned route, with the cells it came from, at debug.</summary>
    private void LogRoute(Route route, Dictionary<uint, PluginDungeonCell> graph)
    {
        if (!Log.Debugs())
            return;
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        IEnumerable<PluginDungeonCell> cells = graph.Count <= 200
            ? graph.Values
            : graph.Values.Where(cell => cell.CellId == snapshot.Position.CellId
                || (graph.TryGetValue(snapshot.Position.CellId, out PluginDungeonCell here) && here.Neighbors.Contains(cell.CellId)));
        foreach (PluginDungeonCell cell in cells)
        {
            Log.Debug($"  cell 0x{cell.CellId:X8}{(cell.CellId == snapshot.Position.CellId ? " (here)" : string.Empty)} at {BotEngine.Describe(cell.Position)} -> {string.Join(", ", cell.Neighbors.Select(n => $"0x{n & 0xFFFFu:X4}"))}"
                + (cell.Doorways.Count > 0 ? $" doorways {string.Join(", ", cell.Doorways.Select(d => $"0x{d.OtherCellId & 0xFFFFu:X4}@{Math.Abs(d.NorthSouth):0.00}{(d.NorthSouth >= 0 ? 'N' : 'S')} {Math.Abs(d.EastWest):0.00}{(d.EastWest >= 0 ? 'E' : 'W')} z{d.Elevation * 240d:0.0}"))}" : " (no doorway positions)"));
        }
        for (int index = 0; index < route.Waypoints.Count; index++)
        {
            Waypoint waypoint = route.Waypoints[index];
            Log.Debug($"  step {index + 1}{(index == route.LoopStart && route.LoopStart > 0 ? " (loop start)" : string.Empty)}: {waypoint.Kind} {Math.Abs(waypoint.NorthSouth):0.00}{(waypoint.NorthSouth >= 0 ? 'N' : 'S')} {Math.Abs(waypoint.EastWest):0.00}{(waypoint.EastWest >= 0 ? 'E' : 'W')} z{waypoint.Elevation * 240d:0.0}");
        }
    }

    /// <summary>Marks a hazard and reroutes the patrol at once.</summary>
    public bool TryMarkHazardAndReroute(out string message)
    {
        bool marked = TryMarkHazard(out message);
        if (marked && IsPatrolling)
            RebuildPatrol();
        return marked;
    }

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

    /// <summary>
    /// A file of /drakbot commands, one per line, dropped in the bot's
    /// folder: read and deleted once a second, each line run as if typed.
    /// For driving the bot from outside the client - a script, a remote,
    /// a developer with the log open - without a chat box.
    /// </summary>
    public const string CommandFileName = "commands.txt";

    /// <summary>
    /// What the server said, at debug level: a door that is locked, a
    /// swing the server refused, a spell that fizzled all arrive as chat,
    /// and a log without them is a mystery with half the clues missing.
    /// </summary>
    private ulong _chatSequence;
    public Func<ulong, IReadOnlyList<PluginChatMessage>>? ChatAfter { get; set; }

    private void LogChat()
    {
        if (ChatAfter is null)
            return;
        IReadOnlyList<PluginChatMessage> messages = ChatAfter(_chatSequence);
        foreach (PluginChatMessage message in messages)
        {
            if (message.Sequence > _chatSequence)
                _chatSequence = message.Sequence;
            NoticeEnvironmentalDamage(message.Text);
            if (!Log.Debugs())
                continue;
            string who = string.IsNullOrEmpty(message.Sender) ? string.Empty : $"{message.Sender}: ";
            Log.Debug($"chat[{message.Kind}]: {who}{message.Text}");
        }
    }

    /// <summary>
    /// "You suffer 47 damage from acid!" every few seconds is the ground
    /// itself: a pool with no object to sight, or one sighted too late.
    /// Two such ticks in the same cell within ten seconds mark it a
    /// hazard, and the patrol is rebuilt around it; the walk away is what
    /// stops the bleeding. A poison landing from a monster says
    /// something else ("You are poisoned"), and a single tick while
    /// passing through is not a hazard.
    /// </summary>
    private const double EnvironmentalDamageWindowSeconds = 10d;
    private uint _environmentalDamageCell;
    private double _environmentalDamageAt = double.NegativeInfinity;

    private void NoticeEnvironmentalDamage(string text)
    {
        if (!text.StartsWith("You suffer ", StringComparison.Ordinal) || !text.Contains(" damage from ", StringComparison.Ordinal))
            return;
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        if (!snapshot.IsAvailable || snapshot.Position.IsOutdoor || (snapshot.Position.CellId & 0xFFFFu) < 0x100u)
            return;
        uint cell = snapshot.Position.CellId;
        double now = Engine.Clock.Now;
        bool second = cell == _environmentalDamageCell && now - _environmentalDamageAt <= EnvironmentalDamageWindowSeconds;
        _environmentalDamageCell = cell;
        _environmentalDamageAt = now;
        if (!second || !Hazards.Add(cell))
            return;
        string source = text[(text.IndexOf(" damage from ", StringComparison.Ordinal) + " damage from ".Length)..].TrimEnd('!', '.', ' ');
        Log.Warn($"hazard: taking {source} damage in cell 0x{cell:X8} at {BotEngine.Describe(snapshot.Position)}; marked, patrol rebuilt around it");
        if (IsPatrolling && _patrolLandblock == (cell & 0xFFFF0000u))
            RebuildPatrol();
    }

    private void DrainCommandFile()
    {
        if (CommandHandler is null || Files.Directory is not { } root)
            return;
        string path = Path.Combine(root, CommandFileName);
        if (!File.Exists(path))
            return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
            File.Delete(path);
        }
        catch (IOException)
        {
            return; // still being written; next second
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            Log.Info($"command file: {line}");
            if (line.StartsWith("/mt ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("/ub ", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("/ra ", StringComparison.OrdinalIgnoreCase))
            {
                // The VTank-style utility verbs, /ub opt set and the rest.
                if (Meta?.Utility?.TryHandle(line) != true)
                    Log.Warn($"command file: {line.Split(' ')[0]} {line.Split(' ').ElementAtOrDefault(1)} is not a command the bot knows");
                continue;
            }
            if (line.StartsWith("/drakbot ", StringComparison.OrdinalIgnoreCase))
                line = line["/drakbot ".Length..];
            else if (line.StartsWith("/bot ", StringComparison.OrdinalIgnoreCase))
                line = line["/bot ".Length..];
            CommandHandler(line);
        }
    }

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

    /// <summary>
    /// Changes the live profile. Every change is saved under the profile's
    /// own name a moment later - there is no separate "save" to remember,
    /// as with RynthAi - and the profile in use is what the bot starts
    /// with next time.
    /// </summary>
    public void Update(Func<BotProfile, BotProfile> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        BotProfile changed = change(Engine.Profile);
        if (ReferenceEquals(changed, Engine.Profile) || changed == Engine.Profile)
            return;
        Engine.Profile = changed;
        if (double.IsNaN(_profileDirtyAt))
            _profileDirtyAt = Engine.Clock.Now;
    }

    /// <summary>Writes a pending change now: on shutdown, before a profile switch.</summary>
    public void FlushProfile()
    {
        if (double.IsNaN(_profileDirtyAt))
            return;
        _profileDirtyAt = double.NaN;
        if (!Store.IsAvailable)
            return;
        try
        {
            Store.SaveProfile(Engine.Profile);
            Store.LastProfileName = Engine.Profile.Name;
        }
        catch (Exception error)
        {
            Log.Warn($"profile: could not save '{Engine.Profile.Name}': {error.Message}");
        }
    }

    // ── Profiles ────────────────────────────────────────────────────────

    public bool LoadProfile(string name)
    {
        FlushProfile();
        BotProfile? loaded = Store.LoadProfile(name);
        if (loaded is null)
            return false;
        Engine.Profile = loaded;
        if (Store.IsAvailable)
            Store.LastProfileName = name;
        ApplyProfileMeta();
        ApplyProfileRoute();
        return true;
    }

    /// <summary>Loads the route the profile names, when it differs from the one being walked.</summary>
    public void ApplyProfileRoute()
    {
        string name = Profile.Navigation.RouteName;
        if (name.Length == 0 || name.Equals(Navigation.Route?.Name, StringComparison.OrdinalIgnoreCase))
            return;
        Route? route = ((IMetaBot)this).LoadRouteByName(name);
        if (route is null)
        {
            Log.Warn($"route: '{name}' from the profile not found");
            return;
        }
        DraftRoute = route;
        Navigation.SetRoute(route, joinNearest: true);
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
        _profileDirtyAt = double.NaN;
        if (Store.IsAvailable)
            Store.LastProfileName = profile.Name;
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

    /// <summary>Puts a step into the draft at <paramref name="index"/>; past the end appends.</summary>
    public void InsertWaypoint(int index, Waypoint waypoint)
    {
        ArgumentNullException.ThrowIfNull(waypoint);
        List<Waypoint> waypoints = [.. DraftRoute.Waypoints];
        waypoints.Insert(Math.Clamp(index, 0, waypoints.Count), waypoint);
        DraftRoute = DraftRoute with { Waypoints = waypoints };
        SyncWalkedRoute();
    }

    /// <summary>A walk on the draft picks up the draft's edits without starting over.</summary>
    private void SyncWalkedRoute()
    {
        if (Navigation.Route is { } walked && walked.Name == DraftRoute.Name)
            Navigation.ReplaceRoute(DraftRoute);
    }

    /// <summary>The step the character would record where it stands, without adding it.</summary>
    public bool TryMakeWaypoint(WaypointKind kind, out Waypoint waypoint)
    {
        PluginNavigationSnapshot snapshot = _navigationSnapshot();
        waypoint = null!;
        if (!snapshot.IsAvailable)
            return false;
        waypoint = kind == WaypointKind.Point
            ? Waypoint.At(snapshot.Position)
            : new Waypoint(kind, snapshot.Position.EastWest, snapshot.Position.NorthSouth) { Elevation = snapshot.Position.Elevation };
        return true;
    }

    /// <summary>Sets the draft's own mode; null follows the profile's.</summary>
    public void SetDraftMode(RouteMode? mode)
    {
        DraftRoute = DraftRoute with { Mode = mode };
        SyncWalkedRoute();
    }

    /// <summary>A saved route or a VTank <c>.nav</c> by name into the draft, followed at once.</summary>
    public bool LoadRouteByName(string name)
    {
        Route? route = ((IMetaBot)this).LoadRouteByName(name);
        if (route is null)
        {
            Log.Warn($"route: '{name}' not found");
            return false;
        }
        Log.Info($"route: loaded '{name}' ({route.Waypoints.Count} steps, {route.Mode?.ToString() ?? "profile mode"})");
        DraftRoute = route;
        Navigation.SetRoute(route, joinNearest: true);
        RememberRoute(name);
        return true;
    }

    /// <summary>A route loaded by name is the profile's route from now on.</summary>
    private void RememberRoute(string name)
    {
        if (!name.Equals(Profile.Navigation.RouteName, StringComparison.Ordinal))
            Update(p => p with { Navigation = p.Navigation with { RouteName = name } });
    }

    public void RemoveWaypoint(int index)
    {
        if (index < 0 || index >= DraftRoute.Waypoints.Count)
            return;
        List<Waypoint> waypoints = [.. DraftRoute.Waypoints];
        waypoints.RemoveAt(index);
        DraftRoute = DraftRoute with { Waypoints = waypoints };
        SyncWalkedRoute();
    }

    public void ClearRoute()
    {
        DraftRoute = new Route { Name = "draft" };
        Navigation.SetRoute(null);
    }

    /// <summary>Starts following the draft route.</summary>
    public void UseDraftRoute()
    {
        Log.Info($"route: following '{DraftRoute.Name}' ({DraftRoute.Waypoints.Count} steps)");
        Navigation.SetRoute(DraftRoute);
    }

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
        Log.Info($"route: loaded '{name}' ({route.Waypoints.Count} steps, {route.Mode?.ToString() ?? "profile mode"})");
        DraftRoute = route;
        Navigation.SetRoute(route, joinNearest: true);
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
        Navigation.SetRoute(route, joinNearest: true);
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
        Dictionary<uint, PluginDungeonCell>? graph = DungeonGraphCore(out snapshot, out problem);
        if (graph is null)
            Log.Warn($"dungeon: {problem} (at {(snapshot.IsAvailable ? BotEngine.Describe(snapshot.Position) : "unknown")})");
        else if (!graph.ContainsKey(snapshot.Position.CellId))
            Log.Warn($"dungeon: the character's cell 0x{snapshot.Position.CellId:X8} is not among the {graph.Count} cells reported; using the nearest");
        return graph;
    }

    private Dictionary<uint, PluginDungeonCell>? DungeonGraphCore(out PluginNavigationSnapshot snapshot, out string problem)
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
    public bool TryStartPatrol(out string message) => TryStartPatrol(out message, quiet: false);

    /// <param name="quiet">No warning when the dungeon is not there yet: the login patrol asks every second.</param>
    private bool TryStartPatrol(out string message, bool quiet)
    {
        Dictionary<uint, PluginDungeonCell>? graph = quiet
            ? DungeonGraphCore(out PluginNavigationSnapshot snapshot, out message)
            : DungeonGraph(out snapshot, out message);
        if (graph is null)
            return false;
        if (IsNoPatrolLandblock(snapshot.Position.CellId))
        {
            message = $"landblock 0x{snapshot.Position.CellId >> 16:X4} is on the no-patrol list (a town, not a dungeon)";
            return false;
        }
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
        Route route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, $"patrol {snapshot.Position.CellId >> 16:X4}", Profile.Navigation.CrossHazards, BlockedEdges(graph, snapshot.Position.CellId));
        if (route.IsEmpty)
        {
            message = "nothing to patrol here";
            Log.Warn($"patrol: nothing to walk from cell 0x{start:X8} ({graph.Count} cells, {hazards.Count} hazards)");
            return false;
        }
        Log.Info($"patrol: built from {BotEngine.Describe(snapshot.Position)} start cell 0x{start:X8}"
            + $"{(start != snapshot.Position.CellId ? " (moved off a hazard/unknown cell)" : string.Empty)}: {graph.Count} cells, {hazards.Count} hazards, {route.Waypoints.Count} steps, loop from step {route.LoopStart + 1}");
        LogRoute(route, graph);
        DraftRoute = route;
        Navigation.SetRoute(route);
        _patrolLandblock = snapshot.Position.CellId & 0xFFFF0000u;
        Navigation.RememberGivenUp(Hazards.GivenUpFor(_patrolLandblock));
        if (!Profile.Navigation.Enabled)
            Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
        message = $"patrolling {graph.Count} cells over {route.Waypoints.Count} steps"
            + (hazards.Count > 0 ? $", {hazards.Count} hazard cell(s) avoided" : string.Empty);
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
        List<uint> path = DungeonPathfinder.FindPath(graph, start, goal, hazards, Profile.Navigation.CrossHazards, BlockedEdges(graph, snapshot.Position.CellId));
        if (path.Count == 0)
        {
            message = "no walkable way there";
            return false;
        }
        Route route = DungeonPathfinder.BuildRoute(graph, path, destination, "goto");
        Log.Info($"goto: {BotEngine.Describe(snapshot.Position)} -> {Math.Abs(northSouth):0.00}{(northSouth >= 0 ? 'N' : 'S')} {Math.Abs(eastWest):0.00}{(eastWest >= 0 ? 'E' : 'W')}: {path.Count} cells, {route.Waypoints.Count} steps");
        LogRoute(route, graph);
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
        // A meta's route is a loaded one: joined at the nearest step.
        Navigation.SetRoute(route, joinNearest: true);
        if (enableNavigation && !Profile.Navigation.Enabled)
            Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
    }

    bool IMetaBot.NeedsAnyBuff(Blackboard board) => Buffs.NeedsAnyBuff(board);

    /// <summary>The <c>.af</c> and <c>.met</c> metas on offer (the bot's metas folder, then the client's vtank folder), by file name.</summary>
    public IReadOnlyList<string> MetaFileNames() => Files.MetaFileNames();

    /// <summary>The <c>.utl</c> loot profiles on offer, by file name.</summary>
    public IReadOnlyList<string> UtlFileNames() => Files.UtlFileNames();

    /// <summary>The VTank <c>.nav</c> routes on offer, by file name.</summary>
    public IReadOnlyList<string> NavFileNames() => Files.NavFileNames();

    /// <summary>Writes the loaded meta as <c>metas/&lt;name&gt;.af</c> in the bot's folder.</summary>
    public bool SaveMeta(string name, out string message)
    {
        if (Meta is null)
        {
            message = "no meta engine";
            return false;
        }
        string safe = BotStore.SanitizeName(name.EndsWith(".af", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name);
        if (safe.Length == 0)
        {
            message = "enter a file name";
            return false;
        }
        try
        {
            Files.WriteMeta(safe, AfFileWriter.SaveToString(Meta.EditableRules, Meta.EmbeddedNavs));
            Update(p => p with { Meta = p.Meta with { Name = safe } });
            message = $"saved {safe}.af";
            return true;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            message = $"save failed: {error.Message}";
            return false;
        }
    }

    LoadedMeta? IMetaBot.LoadMetaByName(string name, out string path)
    {
        if (!Files.TryReadMeta(name, out string? text, out byte[]? bytes, out path))
            return null;
        return text is not null ? AfFileParser.LoadFromText(text) : MetFileParser.LoadFromBytes(bytes!);
    }

    Route? IMetaBot.LoadRouteByName(string name)
    {
        Route? route = Store.LoadRoute(name, out _);
        if (route is not null)
            return route;
        string? nav = Files.ReadNav(name);
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
            ["SummonPets"] = (() => B(Profile.Pets.Enabled), v => Update(p => p with { Pets = p.Pets with { Enabled = ToBool(v) } })),
            ["OpenDoors"] = (() => B(Profile.Doors.Enabled), v => Update(p => p with { Doors = p.Doors with { Enabled = ToBool(v) } })),
            ["BoostNavPriority"] = (() => B(Profile.Priorities.BoostNavigation), v => Update(p => p with { Priorities = p.Priorities with { BoostNavigation = ToBool(v) } })),
            ["BoostLootPriority"] = (() => B(Profile.Priorities.BoostLooting), v => Update(p => p with { Priorities = p.Priorities with { BoostLooting = ToBool(v) } })),
            ["EnableCombineSalvage"] = (() => B(Profile.Salvage.CombineBags), v => Update(p => p with { Salvage = p.Salvage with { CombineBags = ToBool(v) } })),
            ["EnableAutocram"] = (() => B(Profile.Inventory.AutoCram), v => Update(p => p with { Inventory = p.Inventory with { AutoCram = ToBool(v) } })),
            ["EnableAutostack"] = (() => B(Profile.Inventory.AutoStack), v => Update(p => p with { Inventory = p.Inventory with { AutoStack = ToBool(v) } })),
            ["EnableManaTapping"] = (() => B(Profile.ManaStones.Enabled), v => Update(p => p with { ManaStones = p.ManaStones with { Enabled = ToBool(v) } })),
            ["ManaTapMinMana"] = (() => Profile.ManaStones.TapThresholdMana.ToString(), v => { if (int.TryParse(v, out int n)) Update(p => p with { ManaStones = p.ManaStones with { TapThresholdMana = n } }); }),
            ["ManaStoneKeepCount"] = (() => Profile.ManaStones.KeepCount.ToString(), v => { if (int.TryParse(v, out int n)) Update(p => p with { ManaStones = p.ManaStones with { KeepCount = n } }); }),
            ["OpenDoorRange"] = (() => F(Profile.Doors.RangeMeters), v => { if (TryF(v, out double n)) Update(p => p with { Doors = p.Doors with { RangeMeters = (float)n } }); }),
            ["PetMinMonsters"] = (() => F(Profile.Pets.MinimumHostiles), v => { if (TryF(v, out double n)) Update(p => p with { Pets = p.Pets with { MinimumHostiles = (int)n } }); }),
            ["CustomPetRange"] = (() => F(Profile.Pets.RangeMeters), v => { if (TryF(v, out double n)) Update(p => p with { Pets = p.Pets with { RangeMeters = (float)n } }); }),
            ["CurrentNavPath"] = (() => Navigation.Route?.Name ?? string.Empty, v =>
            {
                Route? route = ((IMetaBot)this).LoadRouteByName(v);
                if (route is not null)
                {
                    ((IMetaBot)this).SetRoute(route, false);
                    RememberRoute(v);
                }
            }),
            ["CurrentMetaPath"] = (() => Meta?.MetaName ?? string.Empty, v => Meta?.LoadByName(v)),
            ["CurrentLootPath"] = (() => Profile.Loot.UtlProfile, v => Update(p => p with { Loot = p.Loot with { UtlProfile = v.EndsWith(".utl", StringComparison.OrdinalIgnoreCase) ? v[..^4] : v } })),
        };
    }
}
