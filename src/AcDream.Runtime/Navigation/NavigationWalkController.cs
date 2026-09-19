using System.Collections.Concurrent;
using System.Numerics;
using AcDream.Core.Navigation;
using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Navigation;

internal enum NavigationWalkState
{
    None,

    /// <summary>Building a grid, or searching one for a route.</summary>
    Planning,

    Walking,

    /// <summary>
    /// Stopped where the character stands while something else needs the character,
    /// such as a plugin fighting a monster; the walk plans on once nothing has for a moment.
    /// </summary>
    Waiting,

    /// <summary>A route was asked for without walking it, and found.</summary>
    Planned,

    Arrived,

    /// <summary>No route joins the character to the goal.</summary>
    NoRoute,

    /// <summary>The character stopped making progress, even after planning again from where it stood.</summary>
    Blocked,

    /// <summary>A stop, or a newer request, ended it.</summary>
    Stopped,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,

    /// <summary>
    /// The walk ended at the nearest spot it could reach, but no spot it could reach can see
    /// the goal, as when the goal is shut behind a wall or a window whose collision fills it.
    /// </summary>
    ArrivedWithoutSight,
}

/// <summary>
/// Where the most recent walk or route request stands. <paramref name="Sequence"/>
/// grows by one for every request, so a caller can tell its own from a later one.
/// <paramref name="RemainingMeters"/> is the length of route left while the walk
/// goes on; once it has ended, the straight-line distance from the character to
/// where the goal stands then, or NaN when that is unknown. On a walk that ended
/// blocked, <paramref name="BlockedByObjectId"/> is the server object beside the
/// spot where it last stopped making progress; otherwise it is zero.
/// </summary>
internal readonly record struct NavigationWalkReport(
    long Sequence,
    NavigationWalkState State,
    uint ObjectId,
    float RemainingMeters,
    int Replans,
    string Reason,
    uint BlockedByObjectId = 0u);

/// <summary>
/// The local player's body on one frame, as a walk sees it: the cell it stands in,
/// what it can leap, when it can jump at all, whether it is in the air, and how far a
/// walk and a run carry it on as they end, or zero when that is not known.
/// </summary>
internal readonly record struct NavigationWalkBodySample(
    Vector3 Position,
    float HeadingDegrees,
    NavBody Body,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace,
    uint CellId = 0u,
    NavLeapAbility? Leaps = null,
    bool Airborne = false,
    RuntimeRouteTurning? Turning = null,
    float WalkStopMeters = 0f,
    float RunStopMeters = 0f,
    long PlayerMovementInputFrames = 0);

/// <summary>
/// A server object standing where a walk stopped making progress, the point it
/// stands on and radius it fills, whether it moves and moves aside when bumped, as
/// players and creatures that can be attacked or follow an owner do, and whether it
/// is hostile, which stays to fight.
/// </summary>
internal readonly record struct NavigationBlocker(
    uint ObjectId,
    string Name,
    bool IsClosedDoor,
    Vector3 Position = default,
    float Radius = 0f,
    bool Moves = false,
    bool Hostile = false);

/// <summary>A door a walk can open, and the point it stands on and radius it fills, when known.</summary>
internal readonly record struct NavigationDoor(uint ObjectId, string Name, Vector3 Position = default, float Radius = 0f);

/// <summary>The doors a walk meets, as the client sees and opens them.</summary>
internal interface INavigationDoors
{
    /// <summary>
    /// The closed door nearest <paramref name="from"/> whose position lies ahead of it,
    /// within <paramref name="corridor"/> of the straight line from it to <paramref name="to"/>.
    /// </summary>
    bool TryFindClosedDoor(Vector3 from, Vector3 to, float corridor, out NavigationDoor door);

    bool IsOpen(uint doorId);

    /// <summary>Asks the client to use a door, walking into its use range first as a player's click would.</summary>
    void Use(uint doorId);

    /// <summary>
    /// How near a door's middle a character must stand for the client to use it where it
    /// stands, rather than walk into range itself first; zero when the client does not say.
    /// </summary>
    float UseReach(uint doorId) => 0f;

    /// <summary>
    /// Whether the client's appraisal of a door says it is locked, or null when the
    /// client has not appraised it.
    /// </summary>
    bool? IsLocked(uint doorId) => null;

    /// <summary>Asks the server to appraise a door, so <see cref="IsLocked"/> can answer; false when the client would not ask.</summary>
    bool Appraise(uint doorId) => false;
}

/// <summary>The local player's body, as a walk samples and moves it.</summary>
internal interface INavigationWalkBody
{
    /// <summary>False while there is no local player in the world.</summary>
    bool TrySample(out NavigationWalkBodySample sample);

    bool BeginMove(in RuntimeMoveRequest request);

    bool StopMove(RuntimeMoveChannel channel);

    /// <summary>Charges a jump for <paramref name="power"/> of a full charge, from 0 to 1, then releases it.</summary>
    bool BeginJump(float power, RuntimeMovePace? leaveAt) => false;
}

/// <summary>Where objects stand in the physics world, and what stands in a walk's way.</summary>
internal interface INavigationGoalSource
{
    bool TryLocate(uint objectId, out Vector3 position);

    /// <summary>
    /// The server object, such as a closed door, whose edge comes nearest
    /// <paramref name="position"/> within <paramref name="radius"/>.
    /// </summary>
    bool TryFindBlocker(Vector3 position, float radius, out NavigationBlocker blocker)
    {
        blocker = default;
        return false;
    }

    /// <summary>
    /// The objects the server has placed within <paramref name="radius"/> of
    /// <paramref name="around"/> that collide and stand still, such as ore deposits,
    /// chests and vendors, as the points they stand on and the radii they fill. Doors,
    /// corpses, players, creatures that move and the object <paramref name="goalObjectId"/>
    /// are left out, as are the objects named in <paramref name="inGrid"/>, whose whole
    /// collision a grid already holds as floors and walls.
    /// </summary>
    IReadOnlyList<NavAvoidance> FindObstacles(
        Vector3 around,
        float radius,
        uint goalObjectId,
        IReadOnlySet<uint>? inGrid = null) => [];

    /// <summary>
    /// Whether an object the server placed stands where it is and a body meets it, so a
    /// grid takes its collision as floors and walls: anything but a player, a creature
    /// that moves, a door, or a corpse.
    /// </summary>
    bool StandsStill(uint entityLocalId) => false;

    /// <summary>
    /// The players, and creatures that move, within <paramref name="radius"/> of
    /// <paramref name="around"/>, as the points they stand on and the radii they fill.
    /// They move, and move aside when bumped, so routes pass them where they stand now
    /// rather than keep out of those spots for good. The object
    /// <paramref name="goalObjectId"/> is left out.
    /// </summary>
    IReadOnlyList<NavAvoidance> FindCrowd(Vector3 around, float radius, uint goalObjectId) => [];

    /// <summary>
    /// The portals within <paramref name="radius"/> of <paramref name="around"/>, as the
    /// points they stand on and the radii they fill. A portal collides with nothing but
    /// sends a body that walks into it somewhere else, so a route never goes through one,
    /// even where no other way arrives. The object <paramref name="goalObjectId"/>, and a
    /// portal standing at <paramref name="goal"/>, are left out: that is where the walk goes.
    /// </summary>
    IReadOnlyList<NavAvoidance> FindPortals(Vector3 around, float radius, uint goalObjectId, Vector3 goal) => [];

    /// <summary>
    /// Where a place stands in the physics world: a point in the landblock-local frame
    /// of the cell <paramref name="cellId"/>, as the client's /loc gives it, or for a cell
    /// of zero a point measured from the corner of the first landblock. False when the
    /// client cannot place it relative to the character.
    /// </summary>
    bool TryLocatePlace(uint cellId, Vector3 local, out Vector3 position)
    {
        position = default;
        return false;
    }

    /// <summary>
    /// Where a point in the physics world stands measured from the corner of the first
    /// landblock, the way a place with no cell is given. False when the client cannot tell
    /// relative to the character.
    /// </summary>
    bool TryGlobalOf(Vector3 world, out Vector3 global)
    {
        global = default;
        return false;
    }

    /// <summary>
    /// The collision of an object in the physics world, every part a body meets, for a walk
    /// that ends standing on it. False when the object has none, as a creature or a portal.
    /// </summary>
    bool TryGetSurfaces(uint objectId, out NavSurfaces surfaces)
    {
        surfaces = null!;
        return false;
    }

    /// <summary>Whether an object is a player, the only kind of object a walk follows.</summary>
    bool IsPlayer(uint objectId) => false;

    /// <summary>The portal nearest <paramref name="near"/> within <paramref name="radius"/>, as the client knows it.</summary>
    bool TryFindPortal(Vector3 near, float radius, out uint portalId, out Vector3 position)
    {
        portalId = 0u;
        position = default;
        return false;
    }
}

/// <summary>
/// Plans routes to objects over navigation grids built from the physics world,
/// and walks the local player along them with scripted moves. A grid covers a
/// square region around the character and its goal and is built off the update
/// thread. Inside a sealed dungeon a grid covers every cell of the dungeon and no
/// terrain, however large the dungeon, so one grid serves every walk there. A
/// goal too far away for one region is walked to in stages, each planned over a
/// region reaching toward the goal. A walk that stops making progress plans
/// again from where the character stands, keeping out of the spot where it
/// stuck, a few times before it gives up and names what stood beside that spot.
/// A walk that meets a closed door on its way, or stops making progress beside
/// one, has the client open it and plans again once it is open. A walk that
/// arrives turns the character to face its goal. A route keeps out of the objects
/// the server placed, such as ore deposits, and out of creatures that can't be
/// attacked and follow no one, such as vendors, whenever another way arrives. A walk
/// that stops making progress beside an object keeps out of all of it after, and
/// with no other way ends blocked naming it. A walk waits where the character
/// stands while something else needs the character, such as a plugin fighting a
/// monster, and plans again from there once nothing has for a moment. Routes pass
/// creatures and players around them where there is room, and through them where
/// going around would bring the character nearer walls. A walk looks ahead for one
/// that steps onto its route and plans a way around it without stopping, and a walk
/// stopped by one waits for it to move aside before planning around it.
/// </summary>
internal sealed partial class NavigationWalkController
{
    public const float DefaultArrivalMeters = 2.5f;
    public const int MaximumReplans = 6;

    /// <summary>Room kept around the character and the goal inside a planning region.</summary>
    internal const float RegionMargin = 24f;
    internal const float MinimumRegion = 96f;
    internal const float MaximumRegion = 320f;

    /// <summary>The side of the grid kept around the character while the grid is shown and nothing is planned.</summary>
    internal const float ViewRegion = 128f;

    /// <summary>
    /// A goal too far away for one region is walked to in stages, each planned
    /// over a region reaching from the character toward the goal. A stage must
    /// bring the character at least this much nearer, and a walk has at most
    /// this many.
    /// </summary>
    internal const float MinimumStageProgress = 16f;
    internal const int MaximumStages = 12;

    /// <summary>
    /// The largest region a grid covering a whole sealed dungeon may have. It is
    /// larger than every dungeon in the game data, the widest of which spans about
    /// 1,020 m, and bounds the cost of a grid over cells placed far apart.
    /// </summary>
    internal const float MaximumDungeonRegion = 2048f;

    /// <summary>
    /// A walk that stops making progress keeps its next plans out of a spot this
    /// far ahead of the character and this wide, and looks this far around that
    /// spot for what blocked it.
    /// </summary>
    internal const float BlockedSpotAhead = 0.75f;
    internal const float BlockedSpotRadius = 0.6f;

    /// <summary>
    /// A walk that stops making progress again moves this far at a walk to get off what it met,
    /// and waits this long for the move, before planning again. A hop forward is charged to
    /// <see cref="HopPower"/>. The roomiest spot nearby lies within <see cref="RoomierSpotReach"/>,
    /// at least <see cref="RoomierSpotLeast"/> away, with walls at least
    /// <see cref="RoomierSpotMargin"/> farther than where the body stands.
    /// </summary>
    internal const float RecoveryMeters = 1f;

    /// <summary>How many of a walk's stops keep its later plans out of the spot ahead of where it stopped.</summary>
    internal const int AvoidedStops = 2;
    internal const double RecoverySeconds = 1.5d;
    internal const float HopPower = 0.35f;
    internal const float RoomierSpotReach = 2.5f;
    internal const float RoomierSpotLeast = 0.3f;
    internal const float RoomierSpotMargin = 0.15f;
    internal const float BlockerSearchRadius = 1.5f;

    /// <summary>
    /// A route keeps out of the objects the server placed when a route that does
    /// arrives, or ends no more than this much farther from the goal than a route
    /// through them.
    /// </summary>
    internal const float ObstacleDetourReach = 1f;

    /// <summary>
    /// A walk to an object arrives within its arrival distance of the object's side and looks
    /// at that side, since the object's own collision hides its middle. The side is taken this
    /// far out at most, so a walk to a building still ends near where the building stands.
    /// </summary>
    internal const float MaximumGoalRadius = 8f;

    /// <summary>
    /// A follow keeps within this far behind the player by default. Once there it holds until
    /// the player is <see cref="FollowSlackMeters"/> farther away. On the way, it plans again
    /// toward the player once they have moved <see cref="FollowReplanMeters"/> from where the
    /// route was planned to, at most every <see cref="FollowReplanSeconds"/>. A follow with no
    /// route, or blocked, tries again after <see cref="FollowRetrySeconds"/>.
    /// </summary>
    public const float DefaultFollowMeters = 3f;
    internal const float FollowSlackMeters = 1.5f;
    internal const float FollowReplanMeters = 2f;
    internal const double FollowReplanSeconds = 0.5d;
    internal const double FollowRetrySeconds = 1d;

    /// <summary>A follow holding behind the player goes on once the player is this much higher or lower, as on a platform they jumped onto.</summary>
    internal const float FollowClimbMeters = 1.5f;

    /// <summary>
    /// A player with no floor within <see cref="FollowFloorReach"/> of their feet is in the air,
    /// and a follow waits up to <see cref="FollowAloftSeconds"/> for them to land.
    /// </summary>
    internal const float FollowFloorReach = 0.6f;

    internal const double FollowAloftSeconds = 2d;

    /// <summary>A follower stranded on a top steps this far off it toward the player, at most this many times running.</summary>
    internal const float StepOffMeters = 1.5f;
    internal const int MaximumStepOffs = 3;

    /// <summary>A piece of floor with fewer standing points than this, about 4 m², is a top a follower may be stranded on.</summary>
    internal const int SmallTopNodes = 64;

    /// <summary>
    /// A player who vanishes within this far of a portal went through it. The follower walks
    /// to within <see cref="PortalArrivalMeters"/> of the portal and, if walking into it did not
    /// take the character, uses it every <see cref="PortalUseSeconds"/>.
    /// </summary>
    internal const float FollowPortalReach = 6f;
    internal const float PortalArrivalMeters = 0.5f;
    internal const double PortalUseSeconds = 2d;

    /// <summary>
    /// A player seen to move more than this far within <see cref="FollowTeleportSeconds"/> was
    /// moved by a portal or a recall, not by running.
    /// </summary>
    internal const float FollowTeleportMeters = 20f;
    internal const double FollowTeleportSeconds = 1d;

    /// <summary>A player moved at once, with no portal beside them, to farther than this from the follower is out of reach, and the follow sleeps until they are within it again.</summary>
    internal const float FollowTeleportReach = 100f;

    /// <summary>How far above or below a leg an object's footprint still counts as in its way.</summary>
    private const float NavigationObstacleHeight = 2f;

    /// <summary>
    /// The deepest drop a walk plans. Measured live, a character took no damage from
    /// falls of up to 12 m, and from 13 m on took a little more with every meter, so
    /// walks plan only drops that do no damage.
    /// </summary>
    internal const float SafeDropMeters = 12f;

    /// <summary>A grid is used for a route only while the character and the goal are this far inside it.</summary>
    private const float CoverMargin = 8f;

    private const int MaximumBuildsPerPlan = 2;
    private const int ViewRetryTicks = 120;

    /// <summary>How far from the goal an object still counts as the thing the goal stands on.</summary>
    private const float GoalObjectReach = 4f;

    private const float FaceToleranceDegrees = 10f;

    /// <summary>
    /// While walking, a closed door within this far ahead along the leg and this
    /// near the line to it is opened before the character reaches it. The check runs
    /// every few frames. The walk drives the last of the way itself, until the
    /// character stands within the client's use range of the door or as near its edge
    /// as the body fits with this much room to spare, so the client uses the door
    /// where the character stands instead of walking into range on its own. The door
    /// is used only this many frames after the walk stops the character, because a
    /// stop that lands after the use cancels a walk the client started and drops the
    /// use with it. A door is waited on this long to open once used, and a door that
    /// closes again before the walk is through is used at most this often.
    /// </summary>
    internal const float DoorLookAheadMeters = 3f;
    internal const float DoorCorridorMeters = 1.2f;
    internal const float DoorUseMargin = 0.1f;
    internal const int DoorUseSettleTicks = 3;
    internal const float DoorOpenWaitSeconds = 5f;
    internal const int MaximumDoorUses = 2;
    private const int DoorCheckTicks = 5;

    /// <summary>
    /// A door the client has never appraised is appraised before a walk uses it,
    /// and the answer is waited on this long, so a locked door is walked around
    /// instead of tried. A door that is locked or would not open is kept out of
    /// the walk's later plans, and a walk with no other way ends blocked naming it.
    /// </summary>
    internal const float DoorAppraisalWaitSeconds = 2f;

    /// <summary>
    /// A walk that waited while something else needed the character plans on only
    /// once nothing has needed it for this long, so a fight that pauses between
    /// blows does not set the walk going between them.
    /// </summary>
    internal const double PauseSettleSeconds = 1.5d;

    /// <summary>
    /// The character stands still once it has stayed within <see cref="StillMeters"/> of one
    /// spot for <see cref="StillSeconds"/>, as it must before charging a leap.
    /// </summary>
    internal const float StillMeters = 0.05f;
    internal const double StillSeconds = 0.25d;

    /// <summary>
    /// Routes pass the creatures and players within <see cref="CrowdReach"/> of the
    /// character where they stand when planned. While walking, the next
    /// <see cref="CrowdLookAheadMeters"/> of the route are looked along every
    /// <see cref="CrowdCheckTicks"/> frames for one that has stepped onto it since, and a
    /// way around it is planned while the walk goes on, at most once every
    /// <see cref="DetourIntervalSeconds"/>. A creature counts as one the route was
    /// planned with while it stands within <see cref="CrowdMovedMeters"/> of where it stood.
    /// </summary>
    internal const float CrowdReach = 30f;
    internal const float CrowdLookAheadMeters = 6f;
    internal const int CrowdCheckTicks = 15;
    internal const float CrowdMovedMeters = 0.75f;
    internal const double DetourIntervalSeconds = 1d;

    /// <summary>
    /// A walk stopped by a creature or player waits this long for it to move aside,
    /// at most this many times, before planning a way around it, and may wait again
    /// once it has walked on this far. A creature's spot is never kept out of for the
    /// rest of a walk, since it moves.
    /// </summary>
    internal const double ShuffleWaitSeconds = 1.5d;
    internal const int MaximumShuffles = 2;
    internal const float ShuffleProgressMeters = 3f;

    private readonly PhysicsEngine _physics;
    private readonly INavigationWalkBody _body;
    private readonly INavigationGoalSource _goals;
    private readonly Action<string>? _say;
    private readonly INavigationDoors? _doors;
    private readonly Func<uint, bool>? _isSealedDungeon;
    private readonly object _gate = new();

    private Request? _incoming;
    private bool _stopIncoming;
    private long _sequence;
    private NavigationWalkReport _report;

    private NavGrid? _grid;

    /// <summary>What searches off the update thread found worth narrating, said on the next frame.</summary>
    private readonly ConcurrentQueue<string> _searchNotes = new();

    private Task<NavGrid>? _building;

    /// <summary>
    /// The landblock of the sealed dungeon the grid covers whole, or zero for a
    /// grid over a region of the world; and the same for the grid being built.
    /// </summary>
    private uint _gridDungeon;
    private uint _buildingDungeon;

    /// <summary>What sent the grid being built building, as the build reports when it lands.</summary>
    private string _buildingWhy = "a walk needs one over its route";

    /// <summary>
    /// What the objects in the grid's region came to when they were last looked at, so
    /// that a change is built for once it has stayed put from one look to the next.
    /// </summary>
    private ulong _objectsLastSeen;

    /// <summary>
    /// The cell last asked about whether it lies in a sealed dungeon, and the
    /// answer, since each answer reads the game data.
    /// </summary>
    private uint _classifiedCellId;
    private bool _classifiedSealed;
    private long _tick;
    private double _seconds;
    private Vector3 _stillAt;
    private double _stillSince = double.NaN;
    private long _viewRetryTick;

    private Request? _active;
    private Task<NavRoute>? _routing;
    private Request? _routingFor;
    private RuntimeRouteDriver? _driver;

    public NavigationWalkController(
        PhysicsEngine physics,
        INavigationWalkBody body,
        INavigationGoalSource goals,
        Action<string>? say = null,
        INavigationDoors? doors = null,
        Func<uint, bool>? isSealedDungeon = null)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _goals = goals ?? throw new ArgumentNullException(nameof(goals));
        _say = say;
        _doors = doors;
        _isSealedDungeon = isSealedDungeon;
    }

    /// <summary>Whether to keep a grid built around the character while nothing is planned.</summary>
    public bool ShowGrid { get; set; }

    /// <summary>
    /// What needs the character now, such as a plugin fighting a monster, or null
    /// when nothing does. A walk waits while it names something. It is asked on
    /// every frame a walk is under way.
    /// </summary>
    public Func<string?>? PausedBy { get; set; }

    /// <summary>
    /// Where a walk's full narration goes besides the reports every walk makes: the same
    /// lines, and details a debugging player wants on top, such as where the walk started,
    /// the goal it heads for, and the points of the route it found. Null while nobody listens.
    /// </summary>
    public Action<string>? Narration { get; set; }

    /// <summary>Reports a line of the walk's progress, and narrates it to whoever listens.</summary>
    private void Say(string line)
    {
        _say?.Invoke(line);
        Narration?.Invoke(line);
    }

    /// <summary>Narrates a detail only a debugging listener wants.</summary>
    private void Detail(string line) => Narration?.Invoke(line);

    private static string Inv(FormattableString text)
        => text.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>
    /// Where the planner's own flight puts the leap just flown down and at rest: flown from exactly
    /// where the body charged, at the power and pace it was flown at, toward the spot it was aimed
    /// at, which for a leap aimed onward is that spot and not the landing the route planned. Only
    /// the leap that was flown can be replayed against what it did.
    /// </summary>
    private string ModelledTouchdown(RuntimeRouteDriver driver)
    {
        RuntimeLeapApproach approach = driver.Approach;
        int landing = approach.Leg + 1;
        if (_grid is not { } grid
            || _leapAbility is not { } ability
            || landing < 1
            || landing >= driver.Legs.Count)
        {
            return string.Empty;
        }
        Vector3 end = approach.FlownAt ?? driver.Legs[landing];
        Vector3 start = approach.ChargedFrom;
        bool run = approach.Aimed?.Run ?? approach.PlannedRun;
        if (_aimFinder is not { } cached || !ReferenceEquals(cached.Grid, grid) || cached.Ability != ability)
            _aimFinder = cached = (grid, ability, new NavLeapFinder(grid, ability));
        return cached.Finder.Follow(start, end, Flown(approach), run, out Vector3 cameDown, out Vector3 rests)
            ? $", where the planner's flight comes down at {Point(cameDown)} and comes to rest at {Point(rests)}"
            : ", where the planner's flight comes down nowhere";
    }

    /// <summary>The power a leap was flown at: as aimed again where it was, else as planned.</summary>
    private static float Flown(in RuntimeLeapApproach approach) => approach.Aimed?.Power ?? approach.PlannedPower;

    /// <summary>How high the body's own jump at the power a leap was flown at lifts it, as the planner takes it, and the arc's peak with the step it rises through before gravity takes it.</summary>
    private string PlannedRise(in RuntimeLeapApproach approach)
    {
        if (_leapAbility is not { } ability)
            return "unknown";
        float height = ability.JumpHeight(Flown(approach));
        float launch = MathF.Sqrt(2f * NavLeapPhysics.Gravity * height);
        float peak = height + (launch * MathF.Max(0f, ability.Physics.StepSeconds));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{height:0.00} m, {peak:0.00} m with its first step, of {ability.FullJumpHeight:0.00} m at full power");
    }

    /// <summary>Degrees signed, clockwise positive, with nothing written as minus zero.</summary>
    private static string Degrees(float degrees) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(MathF.Abs(degrees) < 0.05f ? 0f : degrees):+0.0;-0.0;0.0}");

    private static string Spot(Vector2 at) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({at.X:0.00}, {at.Y:0.00})");

    private static string Point(Vector3 at) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0})");

    /// <summary>The grid most recently built.</summary>
    public NavGrid? Grid => _grid;

    /// <summary>
    /// The portals within <paramref name="radius"/> of a point that routes keep out of,
    /// other than the one the latest walk is going to, for a picture of the keep-out.
    /// </summary>
    public IReadOnlyList<NavAvoidance> PortalsNear(Vector3 around, float radius) =>
        _goals.FindPortals(around, radius, Report.ObjectId, Goal?.Position ?? new Vector3(float.MaxValue));

    /// <summary>The route most recently searched for.</summary>
    public NavRoute? Route { get; private set; }

    /// <summary>The goal of the most recent request that found its object, and how near counts as arriving.</summary>
    public (Vector3 Position, float ArrivalMeters)? Goal { get; private set; }

    /// <summary>The index in the route's legs of the leg end the character is walking toward, while it walks.</summary>
    public int? LegIndex => _driver?.LegIndex;

    /// <summary>Whether the route being walked is one stage of a walk to a goal beyond it.</summary>
    public bool IsStaged => _active is { Staged: true };

    /// <summary>Whether a grid is being built or a route searched for, off the update thread.</summary>
    internal bool IsSearching => _routing is not null || _building is not null;

    /// <summary>Whether a grid build or route search under way has finished, so the next tick takes it up.</summary>
    internal bool SearchFinished => _building is { IsCompleted: true } || _routing is { IsCompleted: true };

    /// <summary>Whether a request is waiting, being planned or being walked.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                if (_incoming is not null)
                    return true;
            }
            return _active is not null;
        }
    }

    public NavigationWalkReport Report
    {
        get
        {
            lock (_gate)
                return _report;
        }
    }

    /// <summary>Asks for a route to an object without walking it, and returns the request's sequence.</summary>
    public long RouteTo(uint objectId, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(objectId, arrivalMeters, walk: false);

    /// <summary>Asks to walk to an object, and returns the request's sequence.</summary>
    public long WalkTo(uint objectId, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(objectId, arrivalMeters, walk: true);

    /// <summary>
    /// Asks to walk to a place, a point in the landblock-local frame of the cell
    /// <paramref name="cellId"/> as the client's /loc gives it, or for a cell of zero a
    /// point measured from the corner of the first landblock, and returns the request's
    /// sequence. A walk to a place reports no object and faces nothing on arrival.
    /// </summary>
    public long WalkToPlace(uint cellId, Vector3 local, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(0u, arrivalMeters, walk: true, (cellId, local));

    /// <summary>
    /// Asks to walk onto an object and stand on its top, the highest floor on the object a body
    /// stands on, jumping up where the character can, and returns the request's sequence. The
    /// walk arrives within <paramref name="arrivalMeters"/> of the middle of that top and never
    /// on the ground beside the object.
    /// </summary>
    public long StandOn(uint objectId, float arrivalMeters = DefaultArrivalMeters) =>
        Enqueue(objectId, arrivalMeters, walk: true, onto: true);

    /// <summary>
    /// Asks to follow a player, keeping within <paramref name="bufferMeters"/> behind them, and
    /// returns the request's sequence. A follow never ends by itself: it plans again as the
    /// player moves, tries again when no route arrives or the way is blocked, waits while the
    /// player is out of sight or the character is in portal space, and follows a player who
    /// vanishes beside a portal through it. A stop, a newer request, the player's own movement
    /// input or leaving the world ends it. Anything but a player ends it at once.
    /// </summary>
    public long Follow(uint playerId, float bufferMeters = DefaultFollowMeters) =>
        Enqueue(playerId, bufferMeters, walk: true, follow: true);

    /// <summary>Ends the request under way, stopping the moves its walk began.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _incoming = null;
            _stopIncoming = true;
        }
    }

    /// <summary>
    /// Advances planning and walking by one update frame, <paramref name="elapsedSeconds"/> long.
    /// Call it on the thread that owns the physics world.
    /// </summary>
    public void Tick(double elapsedSeconds)
    {
        _tick++;
        if (elapsedSeconds > 0d && double.IsFinite(elapsedSeconds))
            _seconds += elapsedSeconds;
        CollectBuild();
        CollectRoute();

        Request? incoming;
        bool stop;
        lock (_gate)
        {
            incoming = _incoming;
            stop = _stopIncoming;
            _incoming = null;
            _stopIncoming = false;
        }

        bool inWorld = _body.TrySample(out NavigationWalkBodySample sample);
        if (inWorld && sample.Leaps is { } ability)
            _leapAbility = ability;
        if (inWorld && (double.IsNaN(_stillSince) || Vector3.Distance(sample.Position, _stillAt) > StillMeters))
        {
            _stillAt = sample.Position;
            _stillSince = _seconds;
        }
        if (stop || incoming is not null)
            Cancel(incoming is null ? "stopped" : "a newer request replaced it");
        if (incoming is not null)
            Begin(incoming, inWorld);

        if (_active is { } active)
        {
            if (!inWorld)
                End(active, NavigationWalkState.Lost, "the character left the world");
            else if (PlayerTookTheCharacter(active, sample))
                End(active, NavigationWalkState.Interrupted, "the player moved the character");
            else
                Advance(active, sample);
            return;
        }
        ReleaseUnloadedGrid();
        if (inWorld && ShowGrid)
            KeepViewGrid(sample);
    }

    /// <summary>A square region holding both points with room around them, or false when they are too far apart.</summary>
    internal static bool TryChooseRegion(
        Vector3 from,
        Vector3 to,
        out float originX,
        out float originY,
        out float size) =>
        TryChooseSquare(
            new Vector2(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y)),
            new Vector2(MathF.Max(from.X, to.X), MathF.Max(from.Y, to.Y)),
            MaximumRegion,
            out originX,
            out originY,
            out size);

    /// <summary>
    /// The region one stage of a walk to a goal too far away for one region is
    /// planned over: the largest region, holding the character with room behind
    /// it and reaching as far toward the goal as it can.
    /// </summary>
    internal static void ChooseStageRegion(
        Vector3 from,
        Vector3 to,
        out float originX,
        out float originY,
        out float size)
    {
        size = MaximumRegion;
        float half = size * 0.5f;
        var toward = new Vector2(to.X - from.X, to.Y - from.Y);
        float longest = MathF.Max(MathF.Abs(toward.X), MathF.Abs(toward.Y));
        Vector2 centre = new Vector2(from.X, from.Y)
            + (longest > 1e-3f ? toward * ((half - RegionMargin) / longest) : Vector2.Zero);
        originX = Snap(centre.X - half);
        originY = Snap(centre.Y - half);
    }

    /// <summary>A square region holding a rectangle with room around it, or false when it would be larger than <paramref name="largest"/>.</summary>
    private static bool TryChooseSquare(
        Vector2 minimum,
        Vector2 maximum,
        float largest,
        out float originX,
        out float originY,
        out float size)
    {
        float span = MathF.Max(maximum.X - minimum.X, maximum.Y - minimum.Y) + (2f * RegionMargin);
        size = MathF.Ceiling(MathF.Max(MinimumRegion, span) / 16f) * 16f;
        if (size > largest)
        {
            originX = 0f;
            originY = 0f;
            return false;
        }
        originX = Snap(((minimum.X + maximum.X) * 0.5f) - (size * 0.5f));
        originY = Snap(((minimum.Y + maximum.Y) * 0.5f) - (size * 0.5f));
        return true;
    }

    private long Enqueue(uint objectId, float arrivalMeters, bool walk, (uint CellId, Vector3 Local)? place = null, bool onto = false, bool follow = false)
    {
        if (!(arrivalMeters > 0f) || !float.IsFinite(arrivalMeters))
            arrivalMeters = DefaultArrivalMeters;
        lock (_gate)
        {
            long sequence = ++_sequence;
            _incoming = new Request(sequence, objectId, arrivalMeters, walk, place) { Onto = onto, Follow = follow };
            _stopIncoming = false;
            _report = new NavigationWalkReport(
                sequence,
                NavigationWalkState.Planning,
                objectId,
                float.NaN,
                0,
                "waiting for the next frame");
            return sequence;
        }
    }

    private void Begin(Request request, bool inWorld)
    {
        if (!inWorld)
        {
            End(request, NavigationWalkState.Lost, "the character is not in the world");
            return;
        }
        if (request.Follow)
        {
            BeginFollow(request);
            return;
        }
        bool located = request.Place is { } place
            ? _goals.TryLocatePlace(place.CellId, place.Local, out Vector3 goal)
            : _goals.TryLocate(request.ObjectId, out goal);
        if (!located)
        {
            End(request, NavigationWalkState.NoRoute, $"the client has no position for {Label(request)}");
            return;
        }

        request.Goal = goal;
        request.GoalKnown = true;
        if (request.Place is null && !request.Onto && _goals.TryGetSurfaces(request.ObjectId, out NavSurfaces surfaces))
            request.GoalRadius = RadiusOf(surfaces, goal);
        if (Narration is not null && request.Place is null)
            Detail($"{(request.Walk ? "Walk" : "Route")} {(request.Onto ? "onto" : "to")} {Label(request)}: {CollisionOf(request.ObjectId, goal)}");
        if (Narration is not null && _body.TrySample(out NavigationWalkBodySample start))
        {
            Detail(Inv(
                $"{(request.Walk ? "Walk" : "Route")} {(request.Onto ? "onto" : "to")} {Label(request)}: from {Point(start.Position)} in cell 0x{start.CellId:X8} toward {Point(goal)}, within {request.ArrivalMeters:0.##} m"));
        }
        _active = request;
        Route = null;
        Goal = (goal, request.ArrivalMeters);
        Publish(request, NavigationWalkState.Planning, "planning", float.NaN);
    }

    private void Advance(Request active, in NavigationWalkBodySample sample)
    {
        if (active.Follow && FollowHolds(active, sample))
            return;
        if (Paused(active, sample))
            return;
        if (active.WaitingOn is { } wait)
        {
            WaitForDoor(active, wait, sample);
            return;
        }
        if (active.ShuffleUntil > _seconds)
            return;
        if (_driver is { } driver)
        {
            Drive(active, driver, sample);
            return;
        }
        if (_building is not null || _routing is not null)
            return;
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return;
        }

        // A route starts from floor, so a walk asked for in the air plans once the character lands, however long it is aloft.
        if (sample.Airborne)
        {
            Publish(active, NavigationWalkState.Planning, "waiting for the character to land", float.NaN);
            return;
        }

        bool inDungeon = TryMeasureDungeon(sample.CellId, out uint dungeon, out Vector2 cellsMinimum, out Vector2 cellsMaximum);
        uint gridDungeon = inDungeon ? dungeon : 0u;
        NavGrid? usable = _grid is { } built
            && built.Body == sample.Body
            && _gridDungeon == gridDungeon
            && built.Contains(sample.Position, CoverMargin)
            && !IsStale(built, gridDungeon)
                ? built
                : null;
        if (usable is { } have && !active.BuiltForObjects && GoalStandsOnWhatTheGridHasNot(have, active, sample.Body))
        {
            active.BuiltForObjects = true;
            active.RebuildWhy = "the goal stands on something the grid has none of";
            usable = null;
        }
        bool staged = false;
        if (usable is null || !usable.Contains(active.Goal, CoverMargin))
        {
            if (inDungeon)
            {
                if (TryChooseSquare(
                        Vector2.Min(cellsMinimum, Vector2.Min(Flat(sample.Position), Flat(active.Goal))),
                        Vector2.Max(cellsMaximum, Vector2.Max(Flat(sample.Position), Flat(active.Goal))),
                        MaximumDungeonRegion,
                        out float dungeonX,
                        out float dungeonY,
                        out float dungeonSize))
                {
                    BuildGrid(active, sample, dungeonX, dungeonY, dungeonSize, dungeon);
                }
                else
                {
                    End(
                        active,
                        NavigationWalkState.NoRoute,
                        Inv($"the goal lies too far outside this dungeon, {HorizontalDistance(sample.Position, active.Goal):0} m away"));
                }
                return;
            }
            if (TryChooseRegion(sample.Position, active.Goal, out float originX, out float originY, out float size))
            {
                BuildGrid(active, sample, originX, originY, size, dungeon: 0u);
                return;
            }
            if (usable is null || !ReachesToward(usable, sample.Position, active.Goal))
            {
                ChooseStageRegion(sample.Position, active.Goal, out originX, out originY, out size);
                BuildGrid(active, sample, originX, originY, size, dungeon: 0u);
                return;
            }
            staged = true;
        }

        SearchRoute(active, usable, sample, staged, detour: false);
    }

    /// <summary>
    /// Starts searching a grid for a route from where the character stands to the
    /// goal, or toward it for one stage, passing the creatures and players near the
    /// character where they stand now. A detour is searched while the walk goes on.
    /// </summary>
    private void SearchRoute(Request active, NavGrid grid, in NavigationWalkBodySample sample, bool staged, bool detour)
    {
        Vector3 from = sample.Position;
        Vector3 to = active.Goal;
        float arrival = PlanningRadius(active.Chasing is null ? active.ArrivalMeters : PortalArrivalMeters);
        NavAvoidance[] avoid = [.. active.Avoid, .. active.StuckSpots, .. active.PassingAvoid, .. Portals(grid, sample.Body, active.GoalObjectId, to)];
        active.PassingAvoid.Clear();
        NavAvoidance[] obstacles = Obstacles(grid, sample.Body, active.GoalObjectId);
        NavAvoidance[] crowd = Crowd(sample, active.GoalObjectId);
        active.PlannedObstacles = obstacles;
        active.PlannedAvoid = avoid;
        active.PlannedCrowd = crowd;
        active.Detouring = detour;
        NavLeapAbility? leaps = sample.Leaps;
        // A walk to a place ends on the floor the place stands on; a walk to an object ends
        // beside it, since what an object stands on, such as a table, is not always floor.
        // A follow arrives on the player's own floor when a route reaches it, such as a
        // platform the player jumped onto, and otherwise as near the player as it can.
        bool onGoalFloor = active.Place is not null;
        bool followFloor = active.Follow && active.Chasing is null;
        float goalRadius = active.GoalRadius;
        NavSurfaces? onto = null;
        if (active.Onto && !staged)
        {
            if (!_goals.TryGetSurfaces(active.ObjectId, out NavSurfaces surfaces))
            {
                End(active, NavigationWalkState.NoRoute, $"the client has no collision for {Label(active)} to stand on");
                return;
            }
            onto = surfaces;
        }
        active.Staged = staged;
        if (Narration is not null && !detour)
        {
            Detail(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{(active.Walk ? "Walk" : "Route")} {(active.Onto ? "onto" : "to")} {Label(active)}: searching "
                + $"{(staged ? "toward" : "for")} {Point(to)} from {Point(from)} over {grid.Size:0} m from ({grid.OriginX:0}, {grid.OriginY:0}); "
                + $"arrival within {arrival:0.0} m{(goalRadius > 0f ? $" of the side, the object reaching {goalRadius:0.0} m" : string.Empty)}; "
                + $"keeping out of {avoid.Length} spots and {obstacles.Length} placed objects, passing {crowd.Length} creatures; "
                + $"{(leaps is null ? "no leaps" : "leaps allowed")}"));
        }
        // A follow that plans again around where it stuck holds only if planning through that spot fails too.
        ConcurrentQueue<string>? notes = active.StuckSpots.Count > 0 ? null : _searchNotes;
        _routingFor = active;
        _routing = staged
            ? Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => NavRouter.FindToward(grid, from, to, RegionMargin, spots, leaps, crowd)))
            : onto is not null
                ? Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => NavRouter.FindOnto(grid, from, onto, arrival, spots, leaps, crowd)))
                : followFloor
                    ? Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => FollowRoute(grid, from, to, arrival, spots, leaps, crowd, goalRadius, notes)))
                    : Task.Run(() => AroundObstacles(to, avoid, obstacles, spots => NavRouter.Find(grid, from, to, arrival, spots, leaps, crowd, onGoalFloor, goalRadius)));
    }

    /// <summary>The creatures and players near the character, each widened by the body's radius, for a route to pass.</summary>
    private NavAvoidance[] Crowd(in NavigationWalkBodySample sample, uint goalObjectId)
    {
        IReadOnlyList<NavAvoidance> found = _goals.FindCrowd(sample.Position, CrowdReach, goalObjectId);
        var crowd = new NavAvoidance[found.Count];
        for (int index = 0; index < found.Count; index++)
            crowd[index] = found[index] with { Radius = found[index].Radius + sample.Body.Radius };
        return crowd;
    }

    /// <summary>Whether a creature or player the route was not planned with stands across the next stretch of it.</summary>
    private bool CrowdSteppedOnto(Request active, RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (driver.LegIndex >= driver.Legs.Count)
            return false;
        foreach (NavAvoidance spot in Crowd(sample, active.ObjectId))
        {
            if (HorizontalDistance(spot.Centre, sample.Position) > CrowdLookAheadMeters + spot.Radius
                || active.PlannedCrowd.Any(planned => HorizontalDistance(planned.Centre, spot.Centre) <= CrowdMovedMeters))
            {
                continue;
            }
            Vector3 from = sample.Position;
            float left = CrowdLookAheadMeters;
            for (int index = driver.LegIndex; index < driver.Legs.Count && left > 0f; index++)
            {
                Vector3 to = driver.Legs[index];
                float length = HorizontalDistance(from, to);
                Vector3 end = length > left ? Vector3.Lerp(from, to, left / length) : to;
                if (FlatDistanceToSegment(spot.Centre, from, end) < spot.Radius
                    && MathF.Abs(spot.Centre.Z - end.Z) <= NavigationObstacleHeight)
                {
                    return true;
                }
                left -= length;
                from = to;
            }
        }
        return false;
    }

    /// <summary>Starts planning a way around creatures on the route from where the character is, while the walk goes on.</summary>
    private void StartDetour(Request active, in NavigationWalkBodySample sample)
    {
        if (_grid is not { } grid
            || grid.Body != sample.Body
            || !grid.Contains(sample.Position, CoverMargin)
            || (!active.Staged && !grid.Contains(active.Goal, CoverMargin)))
        {
            return;
        }
        active.DetouredAt = _seconds;
        SearchRoute(active, grid, sample, active.Staged, detour: true);
    }

    /// <summary>
    /// A route's legs from where the character is once planning has taken a moment:
    /// the character's position, then the leg ends it has not yet passed.
    /// </summary>
    private static List<Vector3> OnwardLegs(NavRoute route, Vector3 position, out int skipped)
    {
        int first = 1;
        while (first < route.Legs.Count - 1
            && Vector2.Dot(Flat(route.Legs[first]) - Flat(position), Flat(route.Legs[first]) - Flat(route.Legs[first - 1])) <= 0f)
        {
            first++;
        }
        skipped = first - 1;
        var legs = new List<Vector3>(route.Legs.Count - skipped) { position };
        for (int index = first; index < route.Legs.Count; index++)
            legs.Add(route.Legs[index]);
        return legs;
    }

    private static float FlatDistanceToSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        Vector2 start = Flat(from);
        Vector2 along = Flat(to) - start;
        float lengthSquared = along.LengthSquared();
        float t = lengthSquared > 1e-6f ? Math.Clamp(Vector2.Dot(Flat(point) - start, along) / lengthSquared, 0f, 1f) : 0f;
        return Vector2.Distance(Flat(point), start + (along * t));
    }

    private void Drive(Request active, RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (_doors is not null
            && _tick % DoorCheckTicks == 0
            && !driver.IsLeaping
            && ClosedDoorAhead(driver, sample) is { } door
            && !active.AvoidedDoors.Contains(door.ObjectId)
            && WithinDoorUseRange(door, sample))
        {
            Apply(driver.Cancel());
            _driver = null;
            OpenDoor(active, door, sample.Body.Radius);
            return;
        }
        if (active.Shuffles > 0 && HorizontalDistance(sample.Position, active.ShuffledAt) >= ShuffleProgressMeters)
            active.Shuffles = 0;
        if (_tick % CrowdCheckTicks == 0
            && !driver.IsLeaping
            && _routing is null
            && _seconds - active.DetouredAt >= DetourIntervalSeconds
            && CrowdSteppedOnto(active, driver, sample))
        {
            StartDetour(active, sample);
        }
        bool wasLeaping = driver.IsLeaping;
        RuntimeRouteDriveStep step = driver.Advance(new RuntimeRouteDriveSample(
            sample.Position,
            sample.HeadingDegrees,
            sample.Moves,
            sample.InPortalSpace,
            sample.Airborne,
            sample.Turning,
            Still: !double.IsNaN(_stillSince) && _seconds - _stillSince >= StillSeconds,
            WalkStopMeters: sample.WalkStopMeters,
            RunStopMeters: sample.RunStopMeters));
        if (!Apply(step))
        {
            Apply(driver.Cancel());
            End(active, NavigationWalkState.Stopped, "the client refused a move");
            return;
        }
        if (step.Jump is not null)
        {
            RuntimeLeapApproach approach = driver.Approach;
            string moves = approach.MovesBegun switch
            {
                0 => "from where it stood",
                1 => $"after one {approach.Pace.ToString().ToLowerInvariant()} begun at {Spot(approach.BegunAt)}",
                _ => $"after {approach.MovesBegun} {approach.Pace.ToString().ToLowerInvariant()} moves, the last begun at {Spot(approach.BegunAt)}",
            };
            string letGo = float.IsNaN(approach.LetGoMeters) ? string.Empty : $", let go {approach.LetGoMeters:0.00} m short at {Spot(approach.LetGoAt)}";
            string turned = approach.Turns == 0 ? ", no turn" : $", {approach.Turns} turn{(approach.Turns == 1 ? string.Empty : "s")} through {approach.TurnedDegrees:0} degrees from {Spot(approach.TurnedAt)}";
            string aimed = approach.Aimed is { } aim
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; aimed from here at {aim.Power:0.00} power {(aim.Run ? "running" : "walking")}, planned {approach.PlannedPower:0.00} {(approach.PlannedRun ? "running" : "walking")}")
                : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; no leap from here is kept ({_aimRefused ?? "not aimed"}), so flown as planned at {approach.PlannedPower:0.00} power");
            string skipped = approach.WalkSkipped > 0f
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; leapt from where it stood rather than walk {approach.WalkSkipped:0.0} m to the planned takeoff")
                : string.Empty;
            string adjusted = approach.Adjustments == 0 ? string.Empty
                : $"; went back to the takeoff {approach.Adjustments} time{(approach.Adjustments == 1 ? string.Empty : "s")} first";
            Detail(
                $"Walk to {Label(active)}: charging the jump {approach.TakeoffError:0.00} m from its takeoff {Spot(approach.TakeoffAt)} at {Spot(approach.ChargedAt)}, {moves}{letGo}{turned}{aimed}{skipped}{adjusted}");
        }
        // Said once for each leap: a body sent back to its takeoff leaves the leap and takes it
        // up again, and how often it did is in the line above.
        if (driver.IsLeaping && !wasLeaping && active.AnnouncedLeap != driver.LegIndex)
        {
            active.AnnouncedLeap = driver.LegIndex;
            Vector3 takeoff = driver.Legs[driver.LegIndex - 1];
            Vector3 landing = driver.Legs[driver.LegIndex];
            Say(Inv(
                $"Walk to {Label(active)}: leaping from {takeoff.Z:0.0} m to {landing.Z:0.0} m, {HorizontalDistance(takeoff, landing):0.0} m on"));
        }
        else if (wasLeaping && !driver.IsLeaping && driver.LeapFlew && driver.State == RuntimeRouteDriveState.Driving)
        {
            Say(Inv(
                $"Walk to {Label(active)}: the leap landed at {sample.Position.Z:0.0} m, {driver.LandingError:0.0} m from where it was planned, after sliding {driver.LandingSlide:0.0} m"));
        }
        // Only a leap that came down has a landing to measure: one that never left the ground
        // ends the drive with nothing of its own to say.
        if (wasLeaping && !driver.IsLeaping && driver.LeapLanded && sample.Airborne is false && driver.State != RuntimeRouteDriveState.Interrupted)
        {
            Detail(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Walk to {Label(active)}: the leap came to rest {MathF.Abs(driver.LandingLong):0.00} m {(driver.LandingLong < 0f ? "short of" : "past")} its landing and "
                + $"{MathF.Abs(driver.LandingAside):0.00} m to its {(driver.LandingAside < 0f ? "left" : "right")}, {driver.LandingRise:+0.00;-0.00} m above it; "
                + $"it came down {driver.LandingFlown:0.00} m from where it charged, of {driver.LandingPlanned:0.00} m to its landing, "
                + $"flying {Degrees(driver.LandingFlightDegrees)} degrees off the line to it after charging {Degrees(driver.Approach.FacingError)} degrees off it, "
                + $"then sliding {driver.LandingSlide:0.00} m {Degrees(driver.LandingSlideDegrees)} degrees off that line; "
                + $"it rose {driver.LandingPeak:0.00} m at its highest, where a jump at {Flown(driver.Approach):0.00} power rises {PlannedRise(driver.Approach)}; "
                + $"it came down at {Point(driver.LandingTouchdown)}{ModelledTouchdown(driver)}"));
        }

        switch (driver.State)
        {
            case RuntimeRouteDriveState.Driving:
                Publish(active, NavigationWalkState.Walking, "walking", Remaining(active, driver, sample.Position));
                break;
            case RuntimeRouteDriveState.Arrived when active.Recovering:
                active.Recovering = false;
                _driver = null;
                active.Builds = 0;
                Publish(active, NavigationWalkState.Planning, "at a roomier spot; planning again", float.NaN);
                break;
            case RuntimeRouteDriveState.Arrived when active.Staged:
                NextStage(active, sample);
                break;
            case RuntimeRouteDriveState.Arrived:
                if (active.Place is null && !active.Onto)
                    Face(Locate(active), sample);
                if (!active.EndsInSight)
                {
                    End(
                        active,
                        NavigationWalkState.ArrivedWithoutSight,
                        active.ArrivalReason ?? "no spot the character could reach can see the goal");
                    break;
                }
                End(
                    active,
                    NavigationWalkState.Arrived,
                    active.ArrivalReason is { } why ? $"arrived; {why}" : "arrived");
                break;
            case RuntimeRouteDriveState.Blocked:
                Blocked(active, sample);
                break;
            case RuntimeRouteDriveState.Interrupted:
                End(active, NavigationWalkState.Interrupted, "the player moved the character");
                break;
            case RuntimeRouteDriveState.LandedElsewhere:
                _driver = null;
                active.Builds = 0;
                string elsewhere = Inv($"the leap landed at {sample.Position.Z:0.0} m, {driver.LandingError:0.0} m from where it was planned, after sliding {driver.LandingSlide:0.0} m; planning on from there");
                Publish(active, NavigationWalkState.Planning, elsewhere, float.NaN);
                Say($"Walk to {Label(active)}: {elsewhere}");
                break;
            default:
                End(active, NavigationWalkState.Lost, "the character entered portal space");
                break;
        }
    }

    /// <summary>
    /// Opens a closed door beside the spot ahead of a character that stopped making
    /// progress. Otherwise keeps the next plans out of that spot, notes what stands
    /// beside it, and plans again or gives up.
    /// </summary>
    private void Blocked(Request active, in NavigationWalkBodySample sample)
    {
        active.Recovering = false;
        if (Narration is not null)
            NarrateStuck(active, sample);
        float radians = sample.HeadingDegrees * (MathF.PI / 180f);
        Vector3 spot = sample.Position
            + (new Vector3(MathF.Sin(radians), MathF.Cos(radians), 0f) * BlockedSpotAhead);
        active.BlockedBy = _goals.TryFindBlocker(spot, BlockerSearchRadius, out NavigationBlocker blocker)
            ? blocker
            : null;
        if (_doors is not null && active.BlockedBy is { IsClosedDoor: true } door)
        {
            _driver = null;
            OpenDoor(active, new NavigationDoor(door.ObjectId, door.Name, door.Position, door.Radius), sample.Body.Radius);
            return;
        }
        if (active.BlockedBy is { Moves: true } mover)
        {
            if (active.Shuffles < MaximumShuffles && !mover.Hostile)
            {
                active.Shuffles++;
                active.ShuffledAt = sample.Position;
                active.ShuffleUntil = _seconds + ShuffleWaitSeconds;
                active.Builds = 0;
                _driver = null;
                string waiting = $"{mover.Name} (0x{mover.ObjectId:X8}) stands in the way; waiting for it to move ({active.Shuffles} of {MaximumShuffles})";
                Publish(active, NavigationWalkState.Walking, waiting, float.NaN);
                Say($"Walk to {Label(active)}: {waiting}");
                return;
            }
            if (mover.Radius > 0f)
                active.PassingAvoid.Add(new NavAvoidance(mover.Position, mover.Radius + sample.Body.Radius));
        }
        else
        {
            if (active.BlockedBy is { Radius: > 0f } solid)
            {
                active.Avoid.Add(new NavAvoidance(solid.Position, solid.Radius + sample.Body.Radius));
                active.AvoidedObject = solid;
            }
            // Only the first stops keep later plans out of the spot ahead: a body that keeps
            // stopping in one place would otherwise ring itself in, as on the only stairs down.
            if (active.Replans < AvoidedStops)
                active.StuckSpots.Add(new NavAvoidance(spot, BlockedSpotRadius));
        }
        if (active.Replans >= MaximumReplans)
        {
            End(active, NavigationWalkState.Blocked, BlockedReason(active));
            return;
        }

        // Planning again from the very spot a body is pressed against something rarely frees it,
        // so each stop after the first tries a different way off it first.
        int attempt = active.Recoveries++;
        active.Replans++;
        active.Builds = 0;
        _driver = null;
        string again = $"{BlockedReason(active)}; planning again ({active.Replans} of {MaximumReplans})";
        if (Recover(active, sample, attempt) is { } recovery)
            again += $", {recovery} first";
        Publish(active, NavigationWalkState.Planning, again, float.NaN);
        Say($"Walk to {Label(active)}: {again}");
    }

    /// <summary>
    /// Tries a way off whatever a stopped body met, a different one at each stop: none at the
    /// first, then stepping back, moving to the roomiest spot nearby, sidestepping toward the more
    /// open side, sidestepping the other way, and hopping forward, and round those ways again
    /// for a follow that keeps stopping. Says what it tried, or null.
    /// </summary>
    private string? Recover(Request active, in NavigationWalkBodySample sample, int attempt)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        int way = attempt == 0 ? 0 : ((attempt - 1) % (MaximumReplans - 1)) + 1;
        switch (way)
        {
            case 1:
                return Moves(active, RuntimeMoveDirection.Backward, $"stepping back {RecoveryMeters:0.#} m");
            case 2:
                if (!TryFindRoomierSpot(sample, out Vector3 roomier))
                    return "finding no roomier spot nearby";
                active.Recovering = true;
                _driver = Drive(active, new RuntimeRouteDriver([sample.Position, roomier], [], canCutAlong: null));
                return string.Create(culture, $"moving to the roomiest spot nearby at {Point(roomier)}");
            case 3:
            case 4:
                bool openLeft = RoomToward(sample, left: true) >= RoomToward(sample, left: false);
                bool left = way == 3 ? openLeft : !openLeft;
                return Moves(
                    active,
                    left ? RuntimeMoveDirection.StrafeLeft : RuntimeMoveDirection.StrafeRight,
                    $"sidestepping {RecoveryMeters:0.#} m to the {(left ? "left" : "right")}{(way == 3 ? ", the more open side" : string.Empty)}");
            case 5:
                if (!_body.BeginJump(HopPower, RuntimeMovePace.Walk))
                    return null;
                active.ShuffleUntil = _seconds + RecoverySeconds;
                return "hopping forward";
            default:
                return null;
        }
    }

    private string? Moves(Request active, RuntimeMoveDirection direction, string what)
    {
        if (!_body.BeginMove(new RuntimeMoveRequest(direction, RuntimeMovePace.Walk, RecoveryMeters)))
            return null;
        active.ShuffleUntil = _seconds + RecoverySeconds;
        return what;
    }

    /// <summary>How far the nearest wall stands from the floor a step to one side of the body, or zero with no floor there.</summary>
    private float RoomToward(in NavigationWalkBodySample sample, bool left)
    {
        if (_grid is not { } grid)
            return 0f;
        float radians = sample.HeadingDegrees * (MathF.PI / 180f);
        var side = new Vector3(MathF.Cos(radians), -MathF.Sin(radians), 0f) * (left ? -RecoveryMeters : RecoveryMeters);
        int node = grid.FindStandingNode(sample.Position + side, 0.5f, NavRouter.StartHeightTolerance);
        return node >= 0 && grid.IsClear(node) ? MathF.Min(grid.WallDistance(node), 100f) : 0f;
    }

    /// <summary>
    /// The clear floor within <see cref="RoomierSpotReach"/> a body walks straight to that stands
    /// farthest from walls, such as the middle of a staircase, when it has clearly more room than
    /// where the body stands.
    /// </summary>
    private bool TryFindRoomierSpot(in NavigationWalkBodySample sample, out Vector3 spot)
    {
        spot = default;
        if (_grid is not { } grid)
            return false;
        int here = grid.FindStandingNode(sample.Position, 0.5f, NavRouter.StartHeightTolerance);
        if (here < 0)
            return false;
        float room = MathF.Min(grid.WallDistance(here), 100f);
        float best = room + RoomierSpotMargin;
        int reach = (int)MathF.Ceiling(RoomierSpotReach / grid.CellSize);
        (int cx, int cy) = grid.ColumnOf(here);
        for (int y = cy - reach; y <= cy + reach; y++)
        {
            for (int x = cx - reach; x <= cx + reach; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    Vector3 at = grid.Position(node);
                    float wall = MathF.Min(grid.WallDistance(node), 100f);
                    if (!grid.IsClear(node)
                        || wall <= best
                        || MathF.Abs(at.Z - sample.Position.Z) > NavRouter.StartHeightTolerance
                        || HorizontalDistance(at, sample.Position) > RoomierSpotReach
                        || HorizontalDistance(at, sample.Position) < RoomierSpotLeast
                        || !grid.CanWalkStraight(here, node))
                    {
                        continue;
                    }
                    best = wall;
                    spot = at;
                }
            }
        }
        return best > room + RoomierSpotMargin;
    }

    /// <summary>Narrates where a walk stopped making progress: the body's spot and heading, the leg it was on, and how near the walls were.</summary>
    private void NarrateStuck(Request active, in NavigationWalkBodySample sample)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string leg = _driver is { } driver && driver.LegIndex < driver.Legs.Count
            ? string.Create(
                culture,
                $"leg {driver.LegIndex} of {driver.Legs.Count - 1} toward {Point(driver.Legs[driver.LegIndex])}, {Vector3.Distance(sample.Position, driver.Legs[driver.LegIndex]):0.0} m away")
            : "no leg";
        string walls = "no floor the grid knows under it";
        if (_grid is { } grid && grid.Contains(sample.Position))
        {
            int node = grid.FindStandingNode(sample.Position, 0.5f, NavRouter.StartHeightTolerance);
            if (node >= 0)
            {
                walls = string.Create(
                    culture,
                    $"nearest wall {grid.WallDistance(node):0.00} m from the floor at {Point(grid.Position(node))}, "
                    + $"{(grid.IsClear(node) ? "clear" : "too near a wall or ledge to be clear")}, body radius {sample.Body.Radius:0.00} m");
            }
        }
        Detail(string.Create(
            culture,
            $"Walk to {Label(active)}: stopped making progress at {Point(sample.Position)}, heading {sample.HeadingDegrees:0}°, on {leg}; {walls}"));
    }

    /// <summary>
    /// Stops a walk where the character stands while something else needs the
    /// character, and plans again from there once nothing has needed it for
    /// <see cref="PauseSettleSeconds"/>. A leap already in the air lands first. A
    /// route planned from where the character stood before is not walked, and a
    /// door the walk was opening is judged again on the new plan.
    /// </summary>
    private bool Paused(Request active, in NavigationWalkBodySample sample)
    {
        if (!active.Walk || _driver is { IsLeaping: true })
            return false;
        string? need = PausedBy?.Invoke();
        if (need is null && active.PausedFor is null)
            return false;
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return true;
        }
        if (need is not null)
        {
            active.FreeSince = null;
            if (_driver is { } driver)
            {
                Apply(driver.Cancel());
                _driver = null;
            }
            if (ReferenceEquals(_routingFor, active))
                _routingFor = null;
            if (active.WaitingOn is { Appraising: false } opening)
                active.DoorUses[opening.Door.ObjectId]--;
            active.WaitingOn = null;
            if (need != active.PausedFor)
            {
                active.PausedFor = need;
                string waiting = $"waiting: {need}";
                Publish(active, NavigationWalkState.Waiting, waiting, float.NaN);
                Say($"Walk to {Label(active)}: {waiting}");
            }
            return true;
        }
        active.FreeSince ??= _seconds;
        if (_seconds - active.FreeSince.Value < PauseSettleSeconds)
            return true;
        active.PausedFor = null;
        active.FreeSince = null;
        active.Builds = 0;
        const string again = "nothing needs the character any more; planning on from where it stands";
        Publish(active, NavigationWalkState.Planning, again, float.NaN);
        Say($"Walk to {Label(active)}: {again}");
        return true;
    }

    /// <summary>A closed door within the look-ahead along the leg the character is walking, if there is one.</summary>
    private NavigationDoor? ClosedDoorAhead(RuntimeRouteDriver driver, in NavigationWalkBodySample sample)
    {
        if (driver.LegIndex >= driver.Legs.Count)
            return null;
        var from = new Vector2(sample.Position.X, sample.Position.Y);
        Vector3 end = driver.Legs[driver.LegIndex];
        Vector2 toward = new Vector2(end.X, end.Y) - from;
        float length = toward.Length();
        if (length < 1e-3f)
            return null;
        Vector2 ahead = from + (toward * (MathF.Min(length, DoorLookAheadMeters) / length));
        return _doors!.TryFindClosedDoor(sample.Position, new Vector3(ahead, end.Z), DoorCorridorMeters, out NavigationDoor door)
            ? door
            : null;
    }

    /// <summary>
    /// Whether the character stands near enough a door for the client to use it where
    /// it stands: within the use range the client reports, or as near the door's edge
    /// as the body fits. A door whose middle the client does not give is used where
    /// the walk first meets it.
    /// </summary>
    private bool WithinDoorUseRange(in NavigationDoor door, in NavigationWalkBodySample sample)
    {
        float reach = MathF.Max(
            _doors!.UseReach(door.ObjectId),
            door.Radius > 0f ? door.Radius + sample.Body.Radius + DoorUseMargin : 0f);
        return !(reach > 0f) || HorizontalDistance(sample.Position, door.Position) <= reach;
    }

    /// <summary>
    /// Has the client open a closed door the walk has stopped at and waits for it
    /// to open. A door the client has never appraised is appraised first, and one
    /// that is locked, or keeps closing, is planned around.
    /// </summary>
    private void OpenDoor(Request active, NavigationDoor door, float bodyRadius)
    {
        bool? locked = _doors!.IsLocked(door.ObjectId);
        if (locked is null && active.AppraisedDoors.Add(door.ObjectId) && _doors.Appraise(door.ObjectId))
        {
            active.WaitingOn = new DoorWait(door, _tick, Used: false, _seconds + DoorAppraisalWaitSeconds, Appraising: true);
            string checking = $"checking whether {door.Name} (0x{door.ObjectId:X8}) is locked";
            Publish(active, NavigationWalkState.Walking, checking, float.NaN);
            Say($"Walk to {Label(active)}: {checking}");
            return;
        }
        if (locked == true)
        {
            AvoidDoor(active, door, bodyRadius, "a locked door");
            return;
        }
        int uses = active.DoorUses.GetValueOrDefault(door.ObjectId);
        if (uses >= MaximumDoorUses)
        {
            AvoidDoor(active, door, bodyRadius, "a door kept closing");
            return;
        }
        active.DoorUses[door.ObjectId] = uses + 1;
        active.WaitingOn = new DoorWait(door, _tick + DoorUseSettleTicks, Used: false, DeadlineSeconds: 0d);
        string opening = $"opening {door.Name} (0x{door.ObjectId:X8})";
        Publish(active, NavigationWalkState.Walking, opening, float.NaN);
        Say($"Walk to {Label(active)}: {opening}");
    }

    /// <summary>
    /// Uses the door once the character's stop has landed, and plans again once
    /// the door is open. A door still closed when the wait runs out ends the walk
    /// blocked, since using a door again closes one that opened late.
    /// </summary>
    private void WaitForDoor(Request active, DoorWait wait, in NavigationWalkBodySample sample)
    {
        if (sample.InPortalSpace)
        {
            End(active, NavigationWalkState.Lost, "the character entered portal space");
            return;
        }
        if (_doors!.IsOpen(wait.Door.ObjectId))
        {
            active.WaitingOn = null;
            active.Builds = 0;
            Publish(active, NavigationWalkState.Planning, $"{wait.Door.Name} is open, planning again", float.NaN);
            return;
        }
        if (wait.Appraising)
        {
            if (_doors.IsLocked(wait.Door.ObjectId) is null && _seconds < wait.DeadlineSeconds)
                return;
            active.WaitingOn = null;
            OpenDoor(active, wait.Door, sample.Body.Radius);
            return;
        }
        if (!wait.Used)
        {
            if (_tick < wait.UseAtTick)
                return;
            _doors.Use(wait.Door.ObjectId);
            active.WaitingOn = wait with { Used = true, DeadlineSeconds = _seconds + DoorOpenWaitSeconds };
            return;
        }
        if (_seconds >= wait.DeadlineSeconds)
            AvoidDoor(active, wait.Door, sample.Body.Radius, "a closed door would not open");
    }

    /// <summary>
    /// Keeps the walk's later plans out of a door that is locked or would not open,
    /// widened by the body's radius, and plans again. A door whose footprint is not
    /// known, or that the walk already kept out of, ends the walk blocked.
    /// </summary>
    private void AvoidDoor(Request active, NavigationDoor door, float bodyRadius, string why)
    {
        active.WaitingOn = null;
        if (door.Radius <= 0f || !active.AvoidedDoors.Add(door.ObjectId))
        {
            BlockedAtDoor(active, door, why);
            return;
        }
        active.Avoid.Add(new NavAvoidance(door.Position, door.Radius + bodyRadius));
        active.AvoidedDoor = (door, why);
        active.Builds = 0;
        _driver = null;
        string around = $"{why}: {door.Name} (0x{door.ObjectId:X8}); planning a way around it";
        Publish(active, NavigationWalkState.Planning, around, float.NaN);
        Say($"Walk to {Label(active)}: {around}");
    }

    private void BlockedAtDoor(Request active, NavigationDoor door, string why)
    {
        active.WaitingOn = null;
        active.BlockedBy = new NavigationBlocker(door.ObjectId, door.Name, IsClosedDoor: true);
        End(active, NavigationWalkState.Blocked, $"{why}: {door.Name} (0x{door.ObjectId:X8})");
    }

    /// <summary>Plans the next stage of a walk from where its last stage ended, or gives up after too many.</summary>
    private void NextStage(Request active, in NavigationWalkBodySample sample)
    {
        _driver = null;
        active.Staged = false;
        active.Builds = 0;
        active.Stages++;
        active.Goal = Locate(active);
        Goal = (active.Goal, active.ArrivalMeters);
        float away = HorizontalDistance(sample.Position, active.Goal);
        if (active.Stages >= MaximumStages)
        {
            End(active, NavigationWalkState.NoRoute, Inv($"the goal was still {away:0} m away after {active.Stages} stages"));
            return;
        }
        active.BuiltForObjects = false;
        active.RebuildWhy = null;
        string next = Inv($"stage {active.Stages} walked; planning the next toward the goal, {away:0} m away");
        Publish(active, NavigationWalkState.Planning, next, float.NaN);
        Say($"Walk to {Label(active)}: {next}");
    }

    private void BuildGrid(
        Request active,
        in NavigationWalkBodySample sample,
        float originX,
        float originY,
        float size,
        uint dungeon)
    {
        if (active.Builds >= MaximumBuildsPerPlan)
        {
            End(active, NavigationWalkState.NoRoute, "no grid covers both the character and the goal");
            return;
        }
        active.Builds++;
        if (!StartBuild(
                originX,
                originY,
                size,
                sample.Body,
                dungeon,
                active.RebuildWhy ?? "a walk needs one over its route"))
            End(active, NavigationWalkState.NoRoute, "no collision is loaded around the character");
    }

    /// <summary>
    /// The landblock holding the sealed dungeon a cell lies in, and the horizontal
    /// extent of every cell there, or false outside a sealed dungeon or before its
    /// cells are resident.
    /// </summary>
    private bool TryMeasureDungeon(uint cellId, out uint landblockId, out Vector2 minimum, out Vector2 maximum)
    {
        landblockId = (cellId & 0xFFFF0000u) | 0xFFFFu;
        minimum = default;
        maximum = default;
        if (_isSealedDungeon is null || cellId == 0u)
            return false;
        if (cellId != _classifiedCellId)
        {
            _classifiedCellId = cellId;
            _classifiedSealed = _isSealedDungeon(cellId);
        }
        return _classifiedSealed && NavGeometry.TryMeasureCells(_physics, landblockId, out minimum, out maximum);
    }

    /// <summary>The objects the server placed on a grid, each widened by the body's radius, for a route to keep out of.</summary>
    private NavAvoidance[] Obstacles(NavGrid grid, NavBody body, uint goalObjectId)
    {
        float half = grid.Size * 0.5f;
        var centre = new Vector3(grid.OriginX + half, grid.OriginY + half, 0f);
        IReadOnlyList<NavAvoidance> found = _goals.FindObstacles(
            centre,
            half * MathF.Sqrt(2f),
            goalObjectId,
            grid.ObjectIds);
        var obstacles = new NavAvoidance[found.Count];
        for (int index = 0; index < found.Count; index++)
            obstacles[index] = found[index] with { Radius = found[index].Radius + body.Radius };
        return obstacles;
    }

    /// <summary>
    /// The portals on a grid other than the one the walk goes to, each widened by the
    /// body's radius. Unlike the objects the server placed, a route never goes through
    /// one because no other way arrives: that would end the walk somewhere else.
    /// </summary>
    private NavAvoidance[] Portals(NavGrid grid, NavBody body, uint goalObjectId, Vector3 goal)
    {
        float half = grid.Size * 0.5f;
        var centre = new Vector3(grid.OriginX + half, grid.OriginY + half, 0f);
        IReadOnlyList<NavAvoidance> found = _goals.FindPortals(centre, half * MathF.Sqrt(2f), goalObjectId, goal);
        var portals = new NavAvoidance[found.Count];
        for (int index = 0; index < found.Count; index++)
            portals[index] = found[index] with { Radius = found[index].Radius + body.Radius };
        return portals;
    }

    /// <summary>
    /// The route that keeps out of the objects the server placed, unless only a
    /// route through them arrives, or one through them ends more than
    /// <see cref="ObstacleDetourReach"/> nearer the goal.
    /// </summary>
    private static NavRoute AroundObstacles(
        Vector3 goal,
        NavAvoidance[] avoid,
        NavAvoidance[] obstacles,
        Func<IReadOnlyList<NavAvoidance>, NavRoute> find)
    {
        if (obstacles.Length == 0)
            return find(avoid);
        NavRoute around = find([.. avoid, .. obstacles]);
        if (around.Outcome == NavRouteOutcome.Routed && around.Reason == "routed")
            return around;
        NavRoute through = find(avoid);
        if (around.Outcome != NavRouteOutcome.Routed)
            return through;
        if (through.Outcome != NavRouteOutcome.Routed)
            return around;
        return HorizontalDistance(around.Legs[^1], goal) <= HorizontalDistance(through.Legs[^1], goal) + ObstacleDetourReach
            ? around
            : through;
    }

    /// <summary>Whether a grid reaches far enough toward a goal beyond it to plan a stage over from where the character stands.</summary>
    private static bool ReachesToward(NavGrid grid, Vector3 from, Vector3 goal)
    {
        if (grid.Size <= 2f * RegionMargin)
            return false;
        float x = Math.Clamp(goal.X, grid.OriginX + RegionMargin, grid.OriginX + grid.Size - RegionMargin);
        float y = Math.Clamp(goal.Y, grid.OriginY + RegionMargin, grid.OriginY + grid.Size - RegionMargin);
        return HorizontalDistance(from, goal) - HorizontalDistance(new Vector3(x, y, 0f), goal)
            >= 2f * MinimumStageProgress;
    }

    private bool Apply(in RuntimeRouteDriveStep step)
    {
        if (step.StopTravel)
            _body.StopMove(RuntimeMoveChannel.Travel);
        if (step.StopTurn)
            _body.StopMove(RuntimeMoveChannel.Turn);
        bool accepted = true;
        if (step.Travel is { } travel)
            accepted &= _body.BeginMove(travel);
        if (step.Turn is { } turn)
            accepted &= _body.BeginMove(turn);
        if (step.Jump is { } power)
            accepted &= _body.BeginJump(power, step.JumpPace);
        return accepted;
    }

    private void Face(Vector3 goal, in NavigationWalkBodySample sample)
    {
        float dx = goal.X - sample.Position.X;
        float dy = goal.Y - sample.Position.Y;
        if ((dx * dx) + (dy * dy) < 0.01f)
            return;
        float heading = MathF.Atan2(dx, dy) * (180f / MathF.PI);
        float error = ((((heading - sample.HeadingDegrees) % 360f) + 540f) % 360f) - 180f;
        if (MathF.Abs(error) <= FaceToleranceDegrees)
            return;
        _body.BeginMove(new RuntimeMoveRequest(
            error > 0f ? RuntimeMoveDirection.TurnRight : RuntimeMoveDirection.TurnLeft,
            RuntimeMovePace.Run,
            MathF.Abs(error)));
    }

    private void Cancel(string reason)
    {
        if (_active is not { } active)
            return;
        if (_driver is { } driver)
            Apply(driver.Cancel());
        End(active, NavigationWalkState.Stopped, reason);
    }

    private void End(Request request, NavigationWalkState state, string reason)
    {
        if (request.Follow
            && ReferenceEquals(_active, request)
            && state is NavigationWalkState.Arrived or NavigationWalkState.ArrivedWithoutSight
                or NavigationWalkState.NoRoute or NavigationWalkState.Blocked)
        {
            KeepFollowing(request, state, reason);
            return;
        }
        if (ReferenceEquals(_active, request))
        {
            _active = null;
            _driver = null;
        }
        float remaining = request.GoalKnown && _body.TrySample(out NavigationWalkBodySample sample)
            ? HorizontalDistance(sample.Position, Locate(request))
            : float.NaN;
        Publish(request, state, reason, remaining);
        Say($"{(request.Walk ? "Walk" : "Route")} to {Label(request)}: {reason}");
        if (request.Drives.Count > 0)
        {
            Say(
                $"Walk to {Label(request)}: ran around {request.Drives.Sum(drive => drive.CornersRunAround)} corners "
                + $"and turned in place at {request.Drives.Sum(drive => drive.CornersTurnedInPlace)}");
        }
    }

    private void Publish(Request request, NavigationWalkState state, string reason, float remainingMeters)
    {
        lock (_gate)
        {
            if (request.Sequence < _report.Sequence)
                return;
            _report = new NavigationWalkReport(
                request.Sequence,
                state,
                request.ObjectId,
                remainingMeters,
                request.Replans,
                reason,
                state == NavigationWalkState.Blocked ? request.BlockedBy?.ObjectId ?? 0u : 0u);
        }
    }

    /// <summary>
    /// Whether a grid no longer stands for the world: the resident landblocks it was
    /// built from have changed, or the landblock of a grid over a whole sealed dungeon
    /// has left. Objects arriving and leaving are <see cref="ObjectsMoved"/>, which a walk
    /// asks about as it plans and the grid kept around the character follows as it is shown.
    /// </summary>
    /// <summary>
    /// Lets go of the grid kept between walks once nothing it was built over is loaded any
    /// more: every landblock of a grid over a region, or the cells of a sealed dungeon's grid,
    /// as when the character portals away. A grid still partly loaded is kept, since the next
    /// walk nearby can use it. A dungeon's grid can hold well over a hundred megabytes.
    /// </summary>
    private void ReleaseUnloadedGrid()
    {
        if (_grid is not { } grid || _building is not null)
            return;
        bool unloaded = _gridDungeon != 0u
            ? IsStale(grid, _gridDungeon)
            : !grid.LandblockIds.Any(_physics.IsLandblockTerrainResident);
        if (!unloaded)
            return;
        _grid = null;
        _gridDungeon = 0u;
    }

    private bool IsStale(NavGrid grid, uint dungeon)
    {
        if (dungeon != 0u)
            return !NavGeometry.TryMeasureCells(_physics, dungeon, out _, out _);
        IReadOnlyList<uint> resident = NavGeometry.OverlappingLandblocks(_physics, grid.OriginX, grid.OriginY, grid.Size);
        if (resident.Count != grid.LandblockIds.Count)
            return true;
        foreach (uint landblockId in resident)
        {
            if (!grid.LandblockIds.Contains(landblockId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Starts building a grid over a region, or over the whole of the sealed
    /// dungeon whose landblock <paramref name="dungeon"/> names when it is not zero.
    /// <paramref name="why"/> is what sent it building, which the build reports when it
    /// lands: a grid built again and again has a reason, and it is worth telling.
    /// </summary>
    private bool StartBuild(float originX, float originY, float size, NavBody body, uint dungeon, string why)
    {
        NavGeometry? geometry = dungeon == 0u
            ? NavGeometry.Capture(_physics, originX, originY, size, _goals.StandsStill)
            : NavGeometry.CaptureDungeon(_physics, dungeon, originX, originY, size, _goals.StandsStill);
        if (geometry is null)
            return false;
        _buildingDungeon = dungeon;
        _buildingWhy = why;
        _building = Task.Run(() => NavGrid.Build(geometry, body));
        return true;
    }

    /// <summary>
    /// Whether the objects a body meets in a grid's region have changed since it was
    /// built. A walk asks this as it plans, and plans on the answer at once: an object
    /// the server placed after the grid was built is floor the walk must be given, and
    /// waiting to be sure of it would be waiting with the walk standing still.
    /// </summary>
    private bool ObjectsMoved(NavGrid grid) => Objects(grid) != grid.ObjectFingerprint;

    /// <summary>
    /// Whether the objects have changed and stayed changed, which is what the grid kept
    /// around the character is built again for. It is built again for as long as the
    /// answer is yes, so the answer must not be yes for a thing on its way through: what
    /// the region holds has to read the same twice running first. The ground hardly ever
    /// changes, and what changes it is a thing the server places and leaves, which reads
    /// the same from the moment it lands; anything crossing the region is somewhere else
    /// on the next look. A dungeon's grid takes far longer to build than to look at, and
    /// one built for a thing in flight is out of date before the build finishes.
    /// </summary>
    private bool ObjectsMovedAndStayed(NavGrid grid)
    {
        ulong now = Objects(grid);
        bool stayed = now != grid.ObjectFingerprint && now == _objectsLastSeen;
        _objectsLastSeen = now;
        return stayed;
    }

    /// <summary>
    /// Whether the goal stands on or in an object whose collision the grid has none of,
    /// which is an object the server placed after the grid was built. A route over such a
    /// grid still arrives: it ends on the floor beside the object or under it, rather than
    /// on top, as a walk onto a rock the grid never saw does. Asked once as a walk plans,
    /// not on every tick, since it reads the objects standing around the goal. The grid's
    /// own rule for what it holds decides, so that what it is asked about and what it was
    /// built from are the same objects.
    /// </summary>
    private bool GoalStandsOnWhatTheGridHasNot(NavGrid grid, Request active, NavBody body)
    {
        var goal = new Vector2(active.Goal.X, active.Goal.Y);
        foreach (ShadowEntry entry in _physics.ShadowObjects.AllEntriesForDebug())
        {
            if (grid.ObjectIds.Contains(entry.EntityId) || !_goals.StandsStill(entry.EntityId))
                continue;
            // How near the goal a part comes is measured from its footprint and never from
            // the position it is registered at: a part's collision can sit well off that
            // position, and the radius kept beside it is its model's own, measured about the
            // model's middle rather than about where the part stands.
            NavAvoidance footprint = NavGeometry.FootprintOf(entry, _physics.DataCache);
            float fromGoal = Vector2.Distance(new Vector2(footprint.Centre.X, footprint.Centre.Y), goal);
            if (fromGoal > GoalObjectReach + footprint.Radius)
                continue;
            if (fromGoal <= footprint.Radius + body.Radius)
                return true;
        }
        return false;
    }

    /// <summary>What the objects a body meets in a grid's region come to now.</summary>
    private ulong Objects(NavGrid grid) =>
        NavGeometry.FingerprintObjects(
            _physics,
            grid.OriginX,
            grid.OriginY,
            grid.Size,
            _goals.StandsStill);

    /// <summary>Keeps a grid around the character while it is shown: the whole dungeon inside a sealed one.</summary>
    private void KeepViewGrid(in NavigationWalkBodySample sample)
    {
        if (_building is not null || _tick < _viewRetryTick)
            return;
        if (TryMeasureDungeon(sample.CellId, out uint dungeon, out Vector2 minimum, out Vector2 maximum))
        {
            string? whyDungeon = _grid is { } whole && whole.Body == sample.Body && _gridDungeon == dungeon
                ? IsStale(whole, dungeon)
                    ? "the dungeon's landblock left"
                    : ObjectsMovedAndStayed(whole) ? "the objects in it changed" : null
                : "there was none over this dungeon";
            if (whyDungeon is null)
                return;
            Vector2 at = Flat(sample.Position);
            if (!TryChooseSquare(
                    Vector2.Min(minimum, at),
                    Vector2.Max(maximum, at),
                    MaximumDungeonRegion,
                    out float originX,
                    out float originY,
                    out float size)
                || !StartBuild(originX, originY, size, sample.Body, dungeon, whyDungeon))
            {
                _viewRetryTick = _tick + ViewRetryTicks;
            }
            return;
        }
        string? why = _grid is { } grid && grid.Body == sample.Body && _gridDungeon == 0u
            ? grid.Contains(sample.Position, ViewRegion / 8f)
                ? ObjectsMovedAndStayed(grid) ? "the objects in it changed" : null
                : "the character walked out of it"
            : "there was none around the character";
        if (why is null)
            return;
        float half = ViewRegion * 0.5f;
        if (!StartBuild(Snap(sample.Position.X - half), Snap(sample.Position.Y - half), ViewRegion, sample.Body, dungeon: 0u, why))
            _viewRetryTick = _tick + ViewRetryTicks;
    }

    private void CollectBuild()
    {
        if (_building is not { IsCompleted: true } building)
            return;
        _building = null;
        if (!building.IsCompletedSuccessfully)
        {
            string message = building.Exception?.GetBaseException().Message ?? "it was cancelled";
            Say($"Navmesh: the build failed: {message}");
            if (_active is { } active)
                End(active, NavigationWalkState.NoRoute, $"the grid build failed: {message}");
            return;
        }

        NavGrid grid = building.Result;
        _grid = grid;
        _gridDungeon = _buildingDungeon;
        NavGridBuildReport report = grid.Report;
        Say(Inv(
            $"Navmesh: {report.Nodes} standing points ({report.ClearNodes} clear) over {grid.Size:0} m from {grid.LandblockIds.Count} landblocks in {report.Milliseconds:0} ms, built because {_buildingWhy}"));
    }

    private void CollectRoute()
    {
        while (_searchNotes.TryDequeue(out string? note))
        {
            if (_active is { } noted)
                Detail($"Follow {Label(noted)}: {note}");
        }
        if (_routing is not { IsCompleted: true } routing)
            return;
        _routing = null;
        Request? requester = _routingFor;
        _routingFor = null;
        if (requester is null || !ReferenceEquals(requester, _active))
            return;
        if (requester.Detouring)
        {
            requester.Detouring = false;
            if (_driver is not { State: RuntimeRouteDriveState.Driving, IsLeaping: false }
                || !routing.IsCompletedSuccessfully
                || routing.Result is not { Outcome: NavRouteOutcome.Routed } detour
                || detour.Legs.Count < 2
                || !_body.TrySample(out NavigationWalkBodySample now))
            {
                return;
            }
            Route = detour;
            List<Vector3> onward = OnwardLegs(detour, now.Position, out int skipped);
            RuntimeRouteLeap[] leaps = [.. detour.Leaps
                .Where(leap => leap.LegIndex - skipped >= 1)
                .Select(leap => new RuntimeRouteLeap(leap.LegIndex - skipped, leap.Power, leap.Run))];
            _driver = Drive(requester, new RuntimeRouteDriver(onward, leaps, takeOverMoves: true, canCutAlong: CornerCuts(), aimLeapFrom: AimLeapFrom, aimOnward: AimOnward, sameFloor: SameFloor));
            if (requester.Follow)
                Detail(Inv($"Follow {Label(requester)}: planned again toward the player on the way, {detour.Length:0.0} m"));
            else
                Say($"Walk to {Label(requester)}: planned a way around a creature or player on the route");
            return;
        }
        if (!routing.IsCompletedSuccessfully)
        {
            End(
                requester,
                NavigationWalkState.NoRoute,
                $"the search failed: {routing.Exception?.GetBaseException().Message ?? "it was cancelled"}");
            return;
        }

        NavRoute route = routing.Result;
        Route = route;
        if (route.Outcome == NavRouteOutcome.Routed)
        {
            requester.AvoidedDoor = null;
            requester.AvoidedObject = null;
            int passed = PassedObstacles(route, requester.PlannedObstacles);
            if (passed > 0)
                Say($"Route: no way around {passed} of the objects the server placed arrives, so the route passes them");
        }
        if (route.Outcome != NavRouteOutcome.Routed && requester.StuckSpots.Count > 0)
        {
            // Where the only way on passes the spot the character stuck at, such as a narrow
            // stair, keeping out of it leaves no route: plan through it again, and let the next
            // stop try the next way off.
            requester.StuckSpots.Clear();
            string through = "no route keeps out of where the character stuck; planning through it again";
            Publish(requester, NavigationWalkState.Planning, through, float.NaN);
            Say($"{(requester.Follow ? "Follow" : "Walk to")} {Label(requester)}: {through}");
            return;
        }
        if (route.Outcome != NavRouteOutcome.Routed
            && !requester.BuiltForObjects
            && _grid is { } searched
            && ObjectsMoved(searched))
        {
            // The grid was built before the server had placed everything here: a dungeon's
            // is built on arrival, and objects are placed as the character comes near them.
            // A route keeps out of the objects the grid has none of, as obstacles, which is
            // right until the way on is over them — the rocks of a jump puzzle are the
            // route. Having found none, build the grid again with what stands here now and
            // search once more, rather than asking on every tick whether anything moved.
            requester.BuiltForObjects = true;
            requester.RebuildWhy = "no route was found with the objects the grid had";
            _grid = null;
            string again = $"{requester.RebuildWhy}; building it again with what stands here now";
            Publish(requester, NavigationWalkState.Planning, again, float.NaN);
            Say($"{(requester.Follow ? "Follow" : "Walk to")} {Label(requester)}: {again}");
            return;
        }
        if (route.Outcome != NavRouteOutcome.Routed)
        {
            if (requester.AvoidedDoor is { } avoided)
            {
                requester.BlockedBy = new NavigationBlocker(avoided.Door.ObjectId, avoided.Door.Name, IsClosedDoor: true);
                End(
                    requester,
                    NavigationWalkState.Blocked,
                    $"{avoided.Why}: {avoided.Door.Name} (0x{avoided.Door.ObjectId:X8}), and no other way around it was found");
            }
            else if (requester.AvoidedObject is { } solid)
            {
                requester.BlockedBy = solid;
                End(
                    requester,
                    NavigationWalkState.Blocked,
                    $"{solid.Name} (0x{solid.ObjectId:X8}) stands in the way, and no other way around it was found");
            }
            else if (requester.Replans > 0)
                End(requester, NavigationWalkState.Blocked, $"{BlockedReason(requester)}, and no other way around it was found");
            else
            {
                if (Narration is not null)
                    NarrateNoRoute(requester, route);
                End(requester, NavigationWalkState.NoRoute, route.Reason);
            }
            return;
        }
        Say(Inv(
            $"Route: {route.Legs.Count - 1} legs, {route.Length:0.0} m, {route.Expansions} expansions in {route.Milliseconds:0} ms"));
        if (Narration is not null)
        {
            const int shown = 24;
            string points = string.Join(" -> ", route.Legs.Take(shown).Select(Point));
            string more = route.Legs.Count > shown ? $" and {route.Legs.Count - shown} more" : string.Empty;
            string leaps = route.Leaps.Count == 0
                ? "no leaps"
                : string.Join(", ", route.Leaps.Select(leap =>
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"leg {leap.LegIndex} at {leap.Power:0.00} power {(leap.Run ? "running" : "walking")}")));
            Detail($"Route points: {points}{more}; {leaps}; {(route.EndsInSight ? "ends in sight of the goal" : "ends out of sight of the goal")}");
        }
        if (requester.Staged)
        {
            float left = HorizontalDistance(route.Legs[^1], requester.Goal);
            float before = _body.TrySample(out NavigationWalkBodySample here)
                ? HorizontalDistance(here.Position, requester.Goal)
                : left;
            if (before - left < MinimumStageProgress)
            {
                End(requester, NavigationWalkState.NoRoute, Inv($"no way on toward the goal was found; it is {before:0} m away"));
                return;
            }
            if (!requester.Walk)
            {
                End(
                    requester,
                    NavigationWalkState.Planned,
                    Inv($"a route was found for the first {route.Length:0} m; the rest is planned on the way"));
                return;
            }
            _driver = Drive(requester, new RuntimeRouteDriver(route.Legs, LeapsOf(route), canCutAlong: CornerCuts(), aimLeapFrom: AimLeapFrom, aimOnward: AimOnward, sameFloor: SameFloor));
            Publish(requester, NavigationWalkState.Walking, "walking", route.Length + left);
            return;
        }
        if (!requester.Walk)
        {
            End(requester, NavigationWalkState.Planned, "a route was found");
            return;
        }
        if (route.Legs.Count < 2)
        {
            if (requester.Place is null && !requester.Onto && _body.TrySample(out NavigationWalkBodySample sample))
                Face(Locate(requester), sample);
            if (!route.EndsInSight)
                End(requester, NavigationWalkState.ArrivedWithoutSight, $"already there; {route.Reason}");
            else
                End(requester, NavigationWalkState.Arrived, "already there");
            return;
        }
        requester.ArrivalReason = route.Reason == "routed" ? null : route.Reason;
        requester.EndsInSight = route.EndsInSight;
        _driver = Drive(requester, new RuntimeRouteDriver(route.Legs, LeapsOf(route), canCutAlong: CornerCuts(), aimLeapFrom: AimLeapFrom, aimOnward: AimOnward, sameFloor: SameFloor));
        Publish(requester, NavigationWalkState.Walking, "walking", route.Length);
    }

    private static string BlockedReason(Request request) =>
        request.BlockedBy is { } blocker
            ? $"the character stopped making progress beside {(blocker.IsClosedDoor ? "a closed door, " : blocker.Hostile ? "a hostile monster, " : string.Empty)}"
                + $"{blocker.Name} (0x{blocker.ObjectId:X8})"
            : "the character stopped making progress";

    /// <summary>
    /// How near the goal a route must end for the character to stop within
    /// <paramref name="arrivalMeters"/> of it, since the driver counts a leg's end
    /// as reached a little before the character stands on it.
    /// </summary>
    internal static float PlanningRadius(float arrivalMeters) =>
        MathF.Max(arrivalMeters - RuntimeRouteDriver.ArrivalRadius, NavGrid.DefaultCellSize);

    /// <summary>
    /// A follow's route: onto the floor the player stands on, walking or leaping. A route that
    /// only ends near the player, such as on a top beside the one they stand on, is not taken:
    /// with no way onto their floor the follower holds where it stands and tries again.
    /// </summary>
    private static NavRoute FollowRoute(
        NavGrid grid,
        Vector3 from,
        Vector3 player,
        float arrival,
        IReadOnlyList<NavAvoidance> spots,
        NavLeapAbility? leaps,
        NavAvoidance[] crowd,
        float playerRadius,
        ConcurrentQueue<string>? notes = null)
    {
        NavRoute onFloor = NavRouter.Find(grid, from, player, arrival, spots, leaps, crowd, arriveOnGoalFloor: true, playerRadius);
        if (onFloor.Outcome == NavRouteOutcome.Routed && onFloor.Reason == "routed")
            return onFloor;
        string why = onFloor.Outcome == NavRouteOutcome.Routed
            ? $"no route reaches the player's floor; the nearest a route comes is short of it ({onFloor.Reason})"
            : $"no route reaches the player's floor ({onFloor.Reason})";
        notes?.Enqueue($"{why}; holding where the character stands");
        return onFloor with
        {
            Outcome = NavRouteOutcome.NoPath,
            Reason = why,
            Path = [],
            Legs = [],
            Leaps = [],
        };
    }

    /// <summary>Starts a follow: players only, and waiting for the player when they are out of sight.</summary>
    private void BeginFollow(Request request)
    {
        if (!_goals.IsPlayer(request.ObjectId))
        {
            End(request, NavigationWalkState.NoRoute, $"only players can be followed, and {Label(request)} is not one");
            return;
        }
        _active = request;
        Route = null;
        if (Narration is not null && _body.TrySample(out NavigationWalkBodySample start))
        {
            Detail(Inv(
                $"Follow {Label(request)}: from {Point(start.Position)} in cell 0x{start.CellId:X8}, keeping within {request.ArrivalMeters:0.##} m; {CollisionOf(request.ObjectId, start.Position)}"));
        }
        if (!_goals.TryLocate(request.ObjectId, out Vector3 leader))
        {
            FollowWaits(request, $"waiting to see {Label(request)}");
            return;
        }
        SeeLeader(request, leader);
        Publish(request, NavigationWalkState.Planning, "planning", float.NaN);
    }

    /// <summary>Takes where the player stands now as the follow's goal.</summary>
    private void SeeLeader(Request request, Vector3 leader)
    {
        if (!request.LeaderSeen && _goals.TryGetSurfaces(request.ObjectId, out NavSurfaces surfaces))
            request.LeaderRadius = RadiusOf(surfaces, leader);
        request.LeaderSeen = true;
        request.LeaderLastSeen = leader;
        request.Goal = leader;
        request.GoalKnown = true;
        request.GoalRadius = request.LeaderRadius;
        Goal = (leader, request.ArrivalMeters);
    }

    /// <summary>
    /// Keeps a follow on the player each frame, before the walk plans or drives: waits out
    /// portal space, sees the player or where they went, plans again as they move, and holds
    /// behind them once there. True when nothing more is to be done this frame.
    /// </summary>
    private bool FollowHolds(Request active, in NavigationWalkBodySample sample)
    {
        if (sample.InPortalSpace)
        {
            StopDriving();
            active.WasInPortalSpace = true;
            FollowWaits(active, "in portal space");
            return true;
        }
        if (active.WasInPortalSpace)
        {
            active.WasInPortalSpace = false;
            active.Chasing = null;
            RestartFollow(active);
            Say($"Follow {Label(active)}: out of portal space; looking for the player");
        }
        if (active.Chasing is { } portal)
            return ChasePortal(active, portal);

        if (!_goals.TryLocate(active.ObjectId, out Vector3 leader))
        {
            if (active.LeaderSeen
                && _driver is null
                && _goals.TryFindPortal(active.LeaderLastSeen, FollowPortalReach, out uint portalId, out Vector3 portalAt))
            {
                ChaseThroughPortal(active, portalId, portalAt, "vanished beside");
                return false;
            }
            if (_driver is not null)
                return false;
            FollowWaits(active, $"waiting to see {Label(active)}");
            return true;
        }

        // The client keeps a player who enters a portal or recalls, and moves them to where
        // they went at once, far faster than anyone runs.
        if (active.LeaderSeen
            && _seconds - active.LeaderSeenAt <= FollowTeleportSeconds
            && HorizontalDistance(leader, active.LeaderLastSeen) > FollowTeleportMeters)
        {
            Detail(
                $"Follow {Label(active)}: the player moved from {Point(active.LeaderLastSeen)} to {Point(leader)} at once, "
                + "as a portal or a recall moves them");
            if (_goals.TryFindPortal(active.LeaderLastSeen, FollowPortalReach, out uint enteredId, out Vector3 enteredAt))
            {
                StopDriving();
                _routingFor = null;
                ChaseThroughPortal(active, enteredId, enteredAt, "entered");
                return false;
            }
            StopDriving();
            if (HorizontalDistance(sample.Position, leader) > FollowTeleportReach)
            {
                active.Asleep = true;
                Say(Inv(
                    $"Follow {Label(active)}: the player was moved out of reach, {HorizontalDistance(sample.Position, leader):0} m away, with no portal beside them; waiting until they come back within reach"));
            }
            RestartFollow(active);
        }
        active.LeaderSeenAt = _seconds;
        active.LeaderLastSeen = leader;

        // Asleep after the player was moved out of reach: wait, without walking, until they
        // are within reach again.
        if (active.Asleep)
        {
            float gone = HorizontalDistance(sample.Position, leader);
            if (gone > FollowTeleportReach)
            {
                StopDriving();
                Publish(active, NavigationWalkState.Waiting, Inv($"the player is out of reach, {gone:0} m away; waiting for them to come back"), float.NaN);
                return true;
            }
            active.Asleep = false;
            RestartFollow(active);
            Say(Inv($"Follow {Label(active)}: the player is back within reach, {gone:0} m away; following"));
        }

        // A player with no floor under them is in the air, as mid-jump: planning toward them
        // then aims at whatever stands near the arc, so the follow waits for them to land.
        if (_grid is { } seen && seen.Contains(leader) && !HasFloorNear(seen, leader))
        {
            if (double.IsNaN(active.LeaderAloftSince))
                active.LeaderAloftSince = _seconds;
            if (_seconds - active.LeaderAloftSince < FollowAloftSeconds)
            {
                if (_driver is not null || _building is not null || _routing is not null)
                    return false;
                Publish(active, NavigationWalkState.Walking, "waiting for the player to land", float.NaN);
                return true;
            }
        }
        else
        {
            active.LeaderAloftSince = double.NaN;
        }

        if (_driver is { } driver)
        {
            active.LeaderLastSeen = leader;
            if (HorizontalDistance(leader, active.Goal) > FollowReplanMeters
                && _seconds - active.FollowReplannedAt >= FollowReplanSeconds
                && !driver.IsLeaping
                && _routing is null)
            {
                active.FollowReplannedAt = _seconds;
                Vector3 planned = active.Goal;
                SeeLeader(active, leader);
                Detail($"Follow {Label(active)}: the player moved from {Point(planned)} to {Point(leader)}; planning again on the way");
                StartDetour(active, sample);
                if (_routing is null)
                {
                    StopDriving();
                    active.Builds = 0;
                }
            }
            return false;
        }

        bool wasSeen = active.LeaderSeen;
        if (_building is not null || _routing is not null)
        {
            active.LeaderLastSeen = leader;
            return false;
        }
        if (_seconds < active.FollowRetryAt)
            return true;
        float away = HorizontalDistance(sample.Position, leader);
        if (active.Settled)
        {
            active.LeaderLastSeen = leader;
            bool movedAcross = HorizontalDistance(leader, active.SettledLeader) > FollowSlackMeters
                && away > active.ArrivalMeters + active.LeaderRadius + FollowSlackMeters;
            bool climbed = MathF.Abs(leader.Z - active.SettledLeader.Z) > FollowClimbMeters;
            if (!movedAcross && !climbed)
            {
                Publish(active, NavigationWalkState.Walking, Inv($"keeping behind {Label(active)}, {away:0.0} m away"), away);
                return true;
            }
            Detail(Inv($"Follow {Label(active)}: the player moved to {Point(leader)}, {away:0.0} m away; following"));
            RestartFollow(active);
        }
        if (!wasSeen || active.WaitingToSee)
        {
            active.WaitingToSee = false;
            RestartFollow(active);
            Say($"Follow {Label(active)}: the player is in sight at {Point(leader)}");
        }
        SeeLeader(active, leader);
        return false;
    }

    /// <summary>Turns a follow toward the portal the player went through.</summary>
    private void ChaseThroughPortal(Request active, uint portalId, Vector3 portalAt, string how)
    {
        active.Chasing = portalId;
        active.Goal = portalAt;
        active.GoalKnown = true;
        active.GoalRadius = 0f;
        RestartFollow(active);
        Goal = (portalAt, PortalArrivalMeters);
        string through = $"the player {how} portal 0x{portalId:X8} at {Point(portalAt)}; following through it";
        Publish(active, NavigationWalkState.Walking, through, float.NaN);
        Say($"Follow {Label(active)}: {through}");
    }

    /// <summary>Walks into the portal the player left by, uses it once there, and waits for it to take the character.</summary>
    private bool ChasePortal(Request active, uint portal)
    {
        if (_driver is not null || _building is not null || _routing is not null)
            return false;
        if (!active.Settled)
            return false;
        if (double.IsNaN(active.PortalUsedAt) || _seconds - active.PortalUsedAt >= PortalUseSeconds)
        {
            active.PortalUsedAt = _seconds;
            if (_doors is not null)
            {
                _doors.Use(portal);
                Say($"Follow {Label(active)}: using portal 0x{portal:X8}");
            }
        }
        Publish(active, NavigationWalkState.Walking, $"entering portal 0x{portal:X8} after {Label(active)}", float.NaN);
        return true;
    }

    /// <summary>A follow's walk ended, as a walk would: it holds behind the player, or tries again shortly.</summary>
    private void KeepFollowing(Request request, NavigationWalkState state, string reason)
    {
        _driver = null;
        request.Builds = 0;
        request.Replans = 0;
        request.Avoid.Clear();
        request.StuckSpots.Clear();
        if (state is NavigationWalkState.Arrived or NavigationWalkState.ArrivedWithoutSight)
        {
            request.Recoveries = 0;
            request.Settled = true;
            request.FollowSaid = null;
            request.StepOffs = 0;
            request.SettledLeader = request.LeaderLastSeen;
            if (request.Chasing is { } portal)
            {
                Detail($"Follow {Label(request)}: at portal 0x{portal:X8}");
                return;
            }
            string holding = $"keeping behind {Label(request)}";
            Publish(request, NavigationWalkState.Walking, holding, float.NaN);
            Detail($"Follow {Label(request)}: {holding}; {reason}");
            return;
        }
        request.Settled = false;
        request.FollowRetryAt = _seconds + FollowRetrySeconds;
        if (TryStepOff(request))
            return;
        string again = $"{reason}; trying again";
        Publish(request, NavigationWalkState.Waiting, again, float.NaN);
        if (request.FollowSaid != reason)
        {
            request.FollowSaid = reason;
            Say($"Follow {Label(request)}: {again}");
        }
        else
        {
            Detail($"Follow {Label(request)}: {again}");
        }
    }

    /// <summary>
    /// Steps a follower off a top too small to walk on, toward the player, when no route leaves
    /// it: a leap down from such a top is not planned, and a short walk off its edge drops the
    /// character to the floor below, where the next plan starts. At most
    /// <see cref="MaximumStepOffs"/> times running.
    /// </summary>
    private bool TryStepOff(Request request)
    {
        if (_grid is not { } grid
            || request.StepOffs >= MaximumStepOffs
            || !_body.TrySample(out NavigationWalkBodySample sample)
            || !grid.Contains(sample.Position)
            || !OnSmallTop(grid, sample.Position))
        {
            return false;
        }
        request.StepOffs++;
        Vector3 toward = Locate(request);
        Face(toward, sample);
        _body.BeginMove(new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Walk, StepOffMeters));
        string stepping = $"standing on a top no route leaves; stepping off toward the player ({request.StepOffs} of {MaximumStepOffs})";
        Publish(request, NavigationWalkState.Walking, stepping, float.NaN);
        Say($"Follow {Label(request)}: {stepping}");
        return true;
    }

    /// <summary>
    /// Whether a body at <paramref name="at"/> stands on a top too small to be more than a
    /// perch, fewer than <see cref="SmallTopNodes"/> standing points, or on no floor the grid
    /// knows at all.
    /// </summary>
    private static bool OnSmallTop(NavGrid grid, Vector3 at)
    {
        int node = grid.FindStandingNode(at, NavRouter.StartRadius, NavRouter.StartHeightTolerance);
        if (node < 0)
            return true;
        int[] pieces = NavLeapFinder.FloorPieces(grid);
        int piece = pieces[node];
        if (piece < 0)
            return true;
        int count = 0;
        for (int other = 0; other < pieces.Length; other++)
        {
            if (pieces[other] == piece && ++count >= SmallTopNodes)
                return false;
        }
        return true;
    }

    /// <summary>Whether any floor a body stands on lies within reach of a point, as under a player's feet.</summary>
    private static bool HasFloorNear(NavGrid grid, Vector3 at)
    {
        int reach = (int)MathF.Ceiling(FollowFloorReach / grid.CellSize);
        int centreX = (int)MathF.Floor((at.X - grid.OriginX) / grid.CellSize);
        int centreY = (int)MathF.Floor((at.Y - grid.OriginY) / grid.CellSize);
        for (int y = centreY - reach; y <= centreY + reach; y++)
        {
            for (int x = centreX - reach; x <= centreX + reach; x++)
            {
                (int first, int count) = grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    if (MathF.Abs(grid.Position(node).Z - at.Z) <= FollowFloorReach)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>Holds a follow where the character stands, saying why once.</summary>
    private void FollowWaits(Request request, string why)
    {
        request.Settled = false;
        request.WaitingToSee = why.StartsWith("waiting to see", StringComparison.Ordinal);
        Publish(request, NavigationWalkState.Waiting, why, float.NaN);
        if (request.FollowSaid != why)
        {
            request.FollowSaid = why;
            Say($"Follow {Label(request)}: {why}");
        }
    }

    /// <summary>Starts a follow's next plan afresh.</summary>
    private static void RestartFollow(Request request)
    {
        request.Settled = false;
        request.Builds = 0;
        request.Replans = 0;
        request.Stages = 0;
        request.Avoid.Clear();
        request.StuckSpots.Clear();
        request.FollowRetryAt = 0d;
        request.FollowSaid = null;
    }

    private void StopDriving()
    {
        if (_driver is { } driver)
            Apply(driver.Cancel());
        _driver = null;
    }

    /// <summary>How far around a goal a walk that found no route says what it looked at.</summary>
    private const float NarratedGoalSurroundings = 12f;

    /// <summary>
    /// Narrates why no route reached a goal: the spots kept out of near it, and a ring of spots
    /// around it, each with whether a body stands there, sees the goal, and whether the line to
    /// the goal crosses a spot kept out of.
    /// </summary>
    private void NarrateNoRoute(Request request, NavRoute route)
    {
        if (_grid is not { } grid || request.Staged)
            return;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Vector3 goal = request.Goal;
        Detail(string.Create(
            culture,
            $"No route to {Label(request)} ({route.Outcome}, {route.Expansions} expansions); goal {Point(goal)} "
            + $"{(grid.Contains(goal) ? "inside" : "outside")} the grid"));
        IEnumerable<string> near = request.PlannedAvoid
            .Where(spot => HorizontalDistance(spot.Centre, goal) <= NarratedGoalSurroundings + spot.Radius)
            .Select(spot => string.Create(culture, $"{Point(spot.Centre)} r {spot.Radius:0.0}"));
        Detail($"Kept out of near the goal: {string.Join("; ", near.DefaultIfEmpty("nothing"))}");

        float ring = request.GoalRadius + MathF.Min(request.ArrivalMeters, 2.5f);
        var spots = new List<string>();
        for (int step = 0; step < 16; step++)
        {
            float angle = step * MathF.PI / 8f;
            var at = new Vector3(goal.X + (MathF.Sin(angle) * ring), goal.Y + (MathF.Cos(angle) * ring), goal.Z);
            int node = grid.FindNode(at, 0.75f, NavRouter.GoalHeightTolerance);
            if (node < 0)
            {
                spots.Add(string.Create(culture, $"{step * 22.5f:0}°: no clear spot"));
                continue;
            }
            Vector3 feet = grid.Position(node);
            var toward = new Vector2(feet.X - goal.X, feet.Y - goal.Y);
            float away = toward.Length();
            Vector3 seen = request.GoalRadius > 0f && away > 1e-3f
                ? goal + new Vector3(toward * (MathF.Min(request.GoalRadius + (grid.CellSize * 1.5f), away) / away), 0f)
                : goal;
            bool sees = grid.CanSee(node, seen);
            bool crossed = request.PlannedAvoid.Any(spot => FlatDistanceToSegment(spot.Centre, feet, seen) < spot.Radius);
            string why = string.Empty;
            if (!sees)
            {
                Vector3 target = seen + new Vector3(0f, 0f, NavGrid.TargetHeight);
                why = target.Z >= grid.Ceiling(node)
                    ? string.Create(culture, $" (the goal at {target.Z:0.0} m is above the ceiling at {grid.Ceiling(node):0.0} m)")
                    : grid.SightBlockers(node, seen) is { Count: > 0 } blockers
                        ? string.Create(
                            culture,
                            $" (a wall from {blockers[0].Low:0.0} to {blockers[0].High:0.0} m at "
                            + $"({grid.OriginX + ((blockers[0].X + 0.5f) * grid.CellSize):0.0}, {grid.OriginY + ((blockers[0].Y + 0.5f) * grid.CellSize):0.0}) "
                            + $"crosses the line at {blockers[0].LineHeight:0.0} m)")
                        : " (the goal is below this spot's floor)";
            }
            spots.Add(string.Create(
                culture,
                $"{step * 22.5f:0}°: z {feet.Z:0.0}, {(sees ? "sees" : "no sight")}{why}{(crossed ? ", line crosses a kept-out spot" : string.Empty)}"));
        }
        Detail(string.Create(culture, $"Around the goal at {ring:0.0} m: {string.Join("; ", spots)}"));
    }

    /// <summary>What a walk found of an object's collision, for narration.</summary>
    private string CollisionOf(uint objectId, Vector3 at)
    {
        if (!_goals.TryGetSurfaces(objectId, out NavSurfaces surfaces))
            return "the client has no collision for it, so it is seen and reached at the point it stands on";
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (NavTriangle triangle in surfaces.Triangles)
        {
            minimum = Vector3.Min(minimum, Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C)));
            maximum = Vector3.Max(maximum, Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)));
        }
        foreach (NavCylinder cylinder in surfaces.Cylinders)
        {
            minimum = Vector3.Min(minimum, cylinder.Base - new Vector3(cylinder.Radius, cylinder.Radius, 0f));
            maximum = Vector3.Max(maximum, cylinder.Base + new Vector3(cylinder.Radius, cylinder.Radius, cylinder.Height));
        }
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"collision of {surfaces.Triangles.Count} triangles and {surfaces.Cylinders.Count} cylinders from {Point(minimum)} to {Point(maximum)}, "
            + $"reaching {RadiusOf(surfaces, at):0.0} m from where it stands");
    }

    /// <summary>The widest an object's collision reaches from where it stands, measured flat, and no more than <see cref="MaximumGoalRadius"/>.</summary>
    private static float RadiusOf(NavSurfaces surfaces, Vector3 at)
    {
        var middle = new Vector2(at.X, at.Y);
        float radius = 0f;
        foreach (NavTriangle triangle in surfaces.Triangles)
        {
            radius = MathF.Max(radius, Vector2.Distance(Flat(triangle.A), middle));
            radius = MathF.Max(radius, Vector2.Distance(Flat(triangle.B), middle));
            radius = MathF.Max(radius, Vector2.Distance(Flat(triangle.C), middle));
        }
        foreach (NavCylinder cylinder in surfaces.Cylinders)
            radius = MathF.Max(radius, Vector2.Distance(Flat(cylinder.Base), middle) + cylinder.Radius);
        return MathF.Min(radius, MaximumGoalRadius);
    }

    /// <summary>Where a request's goal stands now, or where it stood when planned once the client has lost it.</summary>
    private Vector3 Locate(Request request)
    {
        bool located = request.Place is { } place
            ? _goals.TryLocatePlace(place.CellId, place.Local, out Vector3 position)
            : _goals.TryLocate(request.GoalObjectId, out position);
        return located ? position : request.Goal;
    }

    /// <summary>
    /// How a request's goal is named in what a walk says: its object's id, a place with a cell
    /// by its cell and point, and a place with no cell by its map coordinates.
    /// </summary>
    private static string Label(Request request)
    {
        if (request.Place is not { } place)
            return $"0x{request.ObjectId:X8}";
        if (place.CellId != 0u)
            return $"0x{place.CellId:X8} [{place.Local.X:0.0} {place.Local.Y:0.0} {place.Local.Z:0.0}]";
        float northSouth = (place.Local.Y - (127f * NavGeometry.LandblockSize) - 84f) / 240f;
        float eastWest = (place.Local.X - (127f * NavGeometry.LandblockSize) - 84f) / 240f;
        return $"{MathF.Abs(northSouth):0.000}{(northSouth < 0f ? 'S' : 'N')}, {MathF.Abs(eastWest):0.000}{(eastWest < 0f ? 'W' : 'E')}";
    }

    /// <summary>The route left to walk, and for one stage of a longer walk the straight line on from its end to the goal.</summary>
    private static float Remaining(Request request, RuntimeRouteDriver driver, Vector3 position)
    {
        float remaining = 0f;
        var at = new Vector2(position.X, position.Y);
        for (int index = driver.LegIndex; index < driver.Legs.Count; index++)
        {
            var end = new Vector2(driver.Legs[index].X, driver.Legs[index].Y);
            remaining += Vector2.Distance(at, end);
            at = end;
        }
        return request.Staged
            ? remaining + Vector2.Distance(at, new Vector2(request.Goal.X, request.Goal.Y))
            : remaining;
    }

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    /// <summary>How many of the objects the server placed a route's legs pass through.</summary>
    private static int PassedObstacles(NavRoute route, NavAvoidance[] obstacles)
    {
        int passed = 0;
        foreach (NavAvoidance obstacle in obstacles)
        {
            var centre = Flat(obstacle.Centre);
            for (int index = 1; index < route.Legs.Count; index++)
            {
                Vector2 start = Flat(route.Legs[index - 1]);
                Vector2 along = Flat(route.Legs[index]) - start;
                float lengthSquared = along.LengthSquared();
                float t = lengthSquared > 1e-6f ? Math.Clamp(Vector2.Dot(centre - start, along) / lengthSquared, 0f, 1f) : 0f;
                if (Vector2.Distance(centre, start + (along * t)) < obstacle.Radius
                    && MathF.Abs(route.Legs[index].Z - obstacle.Centre.Z) <= NavigationObstacleHeight)
                {
                    passed++;
                    break;
                }
            }
        }
        return passed;
    }

    private static RuntimeRouteLeap[] LeapsOf(NavRoute route) =>
        [.. route.Leaps.Select(leap => new RuntimeRouteLeap(leap.LegIndex, leap.Power, leap.Run))];

    private static RuntimeRouteDriver Drive(Request request, RuntimeRouteDriver driver)
    {
        request.Drives.Add(driver);
        return driver;
    }

    /// <summary>What a drive cuts corners along: arcs the grid lets a body brush along, or none before there is a grid.</summary>
    private Func<IReadOnlyList<Vector3>, bool>? CornerCuts() => _grid is { } grid ? grid.CanBrushAlong : null;

    /// <summary>What the body could leap when it was last sampled, which a leap is aimed again with.</summary>
    private NavLeapAbility? _leapAbility;

    /// <summary>The leap finder a leap was last aimed again with, kept while the grid and the body's leaping stay the same.</summary>
    private (NavGrid Grid, NavLeapAbility Ability, NavLeapFinder Finder)? _aimFinder;

    /// <summary>
    /// Aims a leap again from exactly where the body stands, on the grid its route was planned
    /// over, by the same checks the route's own leaps are kept by; null where no leap from there is
    /// kept, or where the grid does not hold where the body stands or the landing.
    /// </summary>
    private RuntimeLeapAim? AimLeapFrom(Vector3 standing, Vector3 landing, bool run, bool atItsTakeoff)
    {
        if (_grid is not { } grid
            || _leapAbility is not { } ability
            || !grid.Contains(standing)
            || !grid.Contains(landing))
        {
            return null;
        }
        if (_aimFinder is not { } cached || !ReferenceEquals(cached.Grid, grid) || cached.Ability != ability)
            _aimFinder = cached = (grid, ability, new NavLeapFinder(grid, ability));
        NavLeapAim? aimed = cached.Finder.AimFrom(standing, landing, run, atItsTakeoff, out string? refused);
        _aimRefused = refused;
        return aimed is { } aim ? new RuntimeLeapAim(aim.Power, aim.Run) : null;
    }

    /// <summary>
    /// A leap from where the body stands onto the floor a planned landing lies on, for a body that
    /// keeps no aim at the landing itself, with the spot it comes to rest on.
    /// </summary>
    private (RuntimeLeapAim Aim, Vector3 Spot)? AimOnward(Vector3 standing, Vector3 landing, bool run)
    {
        if (_grid is not { } grid
            || _leapAbility is not { } ability
            || !grid.Contains(standing)
            || !grid.Contains(landing))
        {
            return null;
        }
        if (_aimFinder is not { } cached || !ReferenceEquals(cached.Grid, grid) || cached.Ability != ability)
            _aimFinder = cached = (grid, ability, new NavLeapFinder(grid, ability));
        return cached.Finder.AimOnward(standing, landing, run, out Vector3 spot) is { } aimed
            ? (new RuntimeLeapAim(aimed.Power, aimed.Run), spot)
            : null;
    }

    /// <summary>Whether two points stand on the same piece of floor of the grid, as one roof or one rock; false where the grid holds either on none.</summary>
    private bool SameFloor(Vector3 first, Vector3 second)
    {
        if (_grid is not { } grid || !grid.Contains(first) || !grid.Contains(second))
            return false;
        int one = grid.FindStandingNode(first, 0.5f, NavRouter.StartHeightTolerance);
        int other = grid.FindStandingNode(second, 0.5f, NavRouter.StartHeightTolerance);
        if (one < 0 || other < 0)
            return false;
        int[] pieces = NavLeapFinder.FloorPieces(grid);
        return pieces[one] >= 0 && pieces[one] == pieces[other];
    }

    /// <summary>Why the last leap aimed again from where the body stood was not kept, for narration.</summary>
    private string? _aimRefused;

    private static float HorizontalDistance(Vector3 from, Vector3 to) =>
        Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));

    private static float Snap(float coordinate) =>
        MathF.Floor(coordinate / NavGrid.DefaultCellSize) * NavGrid.DefaultCellSize;

    /// <summary>A door a walk waits on: to appraise it, or to use it and see it open.</summary>
    private readonly record struct DoorWait(
        NavigationDoor Door,
        long UseAtTick,
        bool Used,
        double DeadlineSeconds,
        bool Appraising = false);

    /// <summary>
    /// Whether the player's own movement input has asked the character to move since the walk
    /// began, whatever the walk is doing: planning, driving, waiting on something else, or
    /// waiting on a door or a landing. A route asked for alone is never ended by it.
    /// </summary>
    private static bool PlayerTookTheCharacter(Request request, in NavigationWalkBodySample sample)
    {
        if (!request.Walk)
            return false;
        if (request.PlayerMovementInputFrames is not { } began)
        {
            request.PlayerMovementInputFrames = sample.PlayerMovementInputFrames;
            return false;
        }
        return sample.PlayerMovementInputFrames > began;
    }

    private sealed class Request
    {
        /// <summary>How many frames of the player's own movement input the character had seen when the walk first sampled it.</summary>
        public long? PlayerMovementInputFrames { get; set; }

        public Request(long sequence, uint objectId, float arrivalMeters, bool walk, (uint CellId, Vector3 Local)? place = null)
        {
            Sequence = sequence;
            ObjectId = objectId;
            ArrivalMeters = arrivalMeters;
            Walk = walk;
            Place = place;
        }

        /// <summary>How far the object walked to reaches from where it stands, measured flat; zero for a place or an object with no collision.</summary>
        public float GoalRadius { get; set; }

        /// <summary>Whether the drive under way only moves the body to a roomier spot to get it unstuck.</summary>
        public bool Recovering { get; set; }

        /// <summary>Whether the request follows a player rather than walking once.</summary>
        public bool Follow { get; init; }

        /// <summary>The portal a follow is walking into after the player, if any.</summary>
        public uint? Chasing { get; set; }

        /// <summary>The object a walk plans to: the portal a follow chases, or the request's own object.</summary>
        public uint GoalObjectId => Chasing ?? ObjectId;

        /// <summary>Whether a follow has seen the player, where last, and how far the player's collision reaches.</summary>
        public bool LeaderSeen { get; set; }

        public Vector3 LeaderLastSeen { get; set; }

        /// <summary>When a follow last saw the player, in the controller's seconds.</summary>
        public double LeaderSeenAt { get; set; } = double.NegativeInfinity;

        /// <summary>Whether a follow sleeps because the player was moved out of reach, until they come back within it.</summary>
        public bool Asleep { get; set; }

        /// <summary>Where the player stood when the follow last settled behind them.</summary>
        public Vector3 SettledLeader { get; set; }

        /// <summary>Since when the player has had no floor under them, or NaN while they stand on one.</summary>
        public double LeaderAloftSince { get; set; } = double.NaN;

        /// <summary>How often running the follower has stepped off a top no route left.</summary>
        public int StepOffs { get; set; }

        public float LeaderRadius { get; set; }

        /// <summary>Whether a follow stands behind the player, or at the portal it chases, and holds there.</summary>
        public bool Settled { get; set; }

        /// <summary>Whether a follow is waiting for the player to come into sight.</summary>
        public bool WaitingToSee { get; set; }

        /// <summary>Whether a follow was in portal space the frame before.</summary>
        public bool WasInPortalSpace { get; set; }

        /// <summary>When a follow may plan again after no route or a block, and when it last planned again on the way, in the controller's seconds.</summary>
        public double FollowRetryAt { get; set; }

        public double FollowReplannedAt { get; set; } = double.NegativeInfinity;

        /// <summary>When a follow last used the portal it chases, in the controller's seconds.</summary>
        public double PortalUsedAt { get; set; } = double.NaN;

        /// <summary>The last reason a follow said it waits or tries again, so each is said once.</summary>
        public string? FollowSaid { get; set; }

        /// <summary>Whether the walk ends standing on top of its object rather than beside it.</summary>
        public bool Onto { get; init; }

        /// <summary>The place walked to, as a cell and a landblock-local point, for a walk to a place rather than an object.</summary>
        public (uint CellId, Vector3 Local)? Place { get; }

        public long Sequence { get; }

        public uint ObjectId { get; }

        public float ArrivalMeters { get; }

        /// <summary>False when only a route was asked for.</summary>
        public bool Walk { get; }

        public Vector3 Goal { get; set; }

        public bool GoalKnown { get; set; }

        public int Replans { get; set; }

        /// <summary>
        /// The ways off tried since the walk last arrived, which picks the next one. A follow
        /// keeps it when it starts over, so a follower stuck in one place goes on to new ways off.
        /// </summary>
        public int Recoveries { get; set; }

        /// <summary>Grids built for the current plan.</summary>
        public int Builds { get; set; }

        /// <summary>
        /// Whether this walk has already built its grid again over the objects standing
        /// here. It does that once for each stretch it walks, where a search found no
        /// route and the grid was built without some of what stands here: a goal nothing
        /// reaches must not build grid after grid, so another one is earned only by
        /// walking a stage of the way first.
        /// </summary>
        public bool BuiltForObjects { get; set; }

        /// <summary>Why this walk asked for its grid again, as the build reports when it lands.</summary>
        public string? RebuildWhy { get; set; }

        /// <summary>Whether the route planned or walked is one stage of a walk to a goal beyond it.</summary>
        public bool Staged { get; set; }

        /// <summary>The stages of the walk already walked.</summary>
        public int Stages { get; set; }

        /// <summary>The objects and doors in the way, which later plans keep out of.</summary>
        public List<NavAvoidance> Avoid { get; } = [];

        /// <summary>
        /// The spots where the walk stopped making progress, which later plans keep out of
        /// while a route still arrives without them.
        /// </summary>
        public List<NavAvoidance> StuckSpots { get; } = [];

        /// <summary>
        /// The object the walk last stopped making progress beside, whose whole
        /// footprint later plans keep out of, named if no way around it is found.
        /// </summary>
        public NavigationBlocker? AvoidedObject { get; set; }

        /// <summary>The spots the latest plan kept out of: where the walk stuck, and portals.</summary>
        public NavAvoidance[] PlannedAvoid { get; set; } = [];

        /// <summary>The objects the server placed that the latest plan was asked to keep out of.</summary>
        public NavAvoidance[] PlannedObstacles { get; set; } = [];

        /// <summary>What stood beside the spot where the walk last stopped making progress.</summary>
        public NavigationBlocker? BlockedBy { get; set; }

        /// <summary>Why the route ends farther from the goal than the arrival radius, when it does.</summary>
        public string? ArrivalReason { get; set; }

        /// <summary>Whether the route being walked ends where the character can see the goal.</summary>
        public bool EndsInSight { get; set; } = true;

        /// <summary>The leg of the leap the walk last said it was taking, so a body sent back to its takeoff is not announced again.</summary>
        public int AnnouncedLeap { get; set; } = -1;

        /// <summary>The door the walk is waiting on to open, if any.</summary>
        public DoorWait? WaitingOn { get; set; }

        /// <summary>How often the walk has used each door.</summary>
        public Dictionary<uint, int> DoorUses { get; } = [];

        /// <summary>The doors the walk asked the server to appraise.</summary>
        public HashSet<uint> AppraisedDoors { get; } = [];

        /// <summary>The doors, locked or that would not open, that later plans keep out of.</summary>
        public HashSet<uint> AvoidedDoors { get; } = [];

        /// <summary>The door the walk last planned a way around, and why, named if no way around it is found.</summary>
        public (NavigationDoor Door, string Why)? AvoidedDoor { get; set; }

        /// <summary>The creatures and players the latest plan passed, where they stood, each widened by the body's radius.</summary>
        public NavAvoidance[] PlannedCrowd { get; set; } = [];

        /// <summary>Whether the route being searched for is a way around creatures, searched while the walk goes on.</summary>
        public bool Detouring { get; set; }

        /// <summary>When the walk last planned a way around creatures, in the controller's seconds.</summary>
        public double DetouredAt { get; set; } = double.NegativeInfinity;

        /// <summary>The drives the walk has made, which count the corners they took.</summary>
        public List<RuntimeRouteDriver> Drives { get; } = [];

        /// <summary>How often the walk has waited for a creature or player to move aside since it last walked on.</summary>
        public int Shuffles { get; set; }

        /// <summary>Where the walk last waited for a creature or player to move aside, and until when.</summary>
        public Vector3 ShuffledAt { get; set; }

        public double ShuffleUntil { get; set; }

        /// <summary>Spots the next plan alone keeps out of, such as where a creature that would not move aside stood.</summary>
        public List<NavAvoidance> PassingAvoid { get; } = [];

        /// <summary>What the walk waits on while something else needs the character.</summary>
        public string? PausedFor { get; set; }

        /// <summary>Since when nothing has needed the character while the walk waits, in the controller's seconds.</summary>
        public double? FreeSince { get; set; }
    }
}
