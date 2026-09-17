using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AcDream.Core.Navigation;

/// <summary>
/// What a body can leap: how fast it walks and runs, how high a jump at full
/// power lifts it, the deepest drop a route may take it down, and how the physics
/// carries it through a jump and its landing. A jump at a share of full power lifts
/// the body that share as high, but never less than <see cref="LowestJump"/>.
/// </summary>
public readonly record struct NavLeapAbility(float WalkSpeed, float RunSpeed, float FullJumpHeight, float MaximumDrop, NavLeapPhysics Physics = default)
{
    public const float LowestJump = 0.35f;

    /// <summary>How high a jump at <paramref name="power"/>, from 0 to 1, lifts the body.</summary>
    public float JumpHeight(float power) => MathF.Max(LowestJump, FullJumpHeight * Math.Clamp(power, 0f, 1f));
}

/// <summary>
/// How the physics carries a body through a jump, for a leap to be planned to where the body
/// comes to rest. The body moves in steps <paramref name="StepSeconds"/> long, and gravity pulls
/// on it only through a step that begins with it off the ground. So a body leaving the ground
/// rises through a whole step before it starts to fall, and one coming down keeps its speed
/// along the ground to the end of the step it lands in. Landing throws it back up at
/// <paramref name="Elasticity"/> of the speed it fell at, and at <see cref="LeastBounce"/> or
/// faster it leaves the ground again in a short hop. Once it stays down, friction takes
/// <paramref name="Friction"/> of its speed away each second, step by step, until a step begins
/// slower than <see cref="StoppedSpeed"/>. With no step length a jump flies an unbroken arc and
/// stops where it comes down.
/// </summary>
public readonly record struct NavLeapPhysics(float StepSeconds, float Elasticity, float Friction)
{
    public const float Gravity = 9.8f;
    public const float LeastBounce = 0.25f;
    public const float StoppedSpeed = 0.25f;

    /// <summary>How far above its takeoff a body launched upward at <paramref name="launch"/> is after <paramref name="seconds"/> in the air.</summary>
    public float Height(float launch, float seconds)
    {
        float falling = Falling(seconds);
        return (launch * seconds) - (0.5f * Gravity * falling * falling);
    }

    /// <summary>How fast a body launched upward at <paramref name="launch"/> is falling after <paramref name="seconds"/> in the air; below zero while it still rises.</summary>
    public float FallSpeed(float launch, float seconds) => (Gravity * Falling(seconds)) - launch;

    /// <summary>The launch speed that puts a body <paramref name="rise"/> above its takeoff after <paramref name="seconds"/> in the air.</summary>
    public float LaunchFor(float rise, float seconds)
    {
        float falling = Falling(seconds);
        return (rise + (0.5f * Gravity * falling * falling)) / seconds;
    }

    /// <summary>
    /// How far along the ground a body moving at <paramref name="speed"/> goes on off the ground
    /// from the point its arc comes down through, falling at <paramref name="fallSpeed"/>: through
    /// the rest of the step it lands in, and through any hop its landing throws it into.
    /// </summary>
    public float Carry(float speed, float fallSpeed)
    {
        float step = StepSeconds;
        if (!(step > 0f) || !(speed > 0f))
            return 0f;
        float seconds = step / 2f;
        float fall = fallSpeed + (Gravity * step / 2f);
        for (float hop = Elasticity * fall; hop >= LeastBounce; hop = Elasticity * fall)
        {
            float landing = MathF.Sqrt((hop * hop) + (2f * Gravity * hop * step));
            seconds += step + ((hop + landing) / Gravity) + (step / 2f);
            fall = landing + (Gravity * step / 2f);
        }
        return speed * seconds;
    }

    /// <summary>
    /// How far a body moving at <paramref name="speed"/> slides along the ground once it stays
    /// down, step by step, until a step begins slower than <see cref="StoppedSpeed"/>. Infinity
    /// on ground without friction.
    /// </summary>
    public float GroundSlide(float speed)
    {
        float step = StepSeconds;
        if (!(step > 0f) || !(speed > 0f))
            return 0f;
        float kept = MathF.Pow(1f - Math.Clamp(Friction, 0f, 1f), step);
        if (!(kept < 1f))
            return float.PositiveInfinity;
        float slid = 0f;
        for (float moving = speed; moving >= StoppedSpeed; moving *= kept)
            slid += moving * kept * step;
        return slid;
    }

    /// <summary>
    /// How far along open ground a body moving at <paramref name="speed"/> goes on from the point
    /// its arc comes down through, falling at <paramref name="fallSpeed"/>, to where it comes to
    /// rest: its <see cref="Carry"/> and then its <see cref="GroundSlide"/>.
    /// </summary>
    public float Slide(float speed, float fallSpeed) => Carry(speed, fallSpeed) + GroundSlide(speed);

    /// <summary>How long a body has been falling after <paramref name="seconds"/> in the air: not at all through its first step.</summary>
    private float Falling(float seconds) => MathF.Max(0f, seconds - MathF.Max(0f, StepSeconds));
}

/// <summary>
/// A standing long jump from one node to another: the body stands on
/// <paramref name="From"/> facing <paramref name="To"/>, charges a jump to
/// <paramref name="Power"/>, and leaves the ground at running or walking pace.
/// <paramref name="Risk"/> counts what makes it less than easy: aiming nearer the edge of
/// the floor it lands on than that floor's middle, coming down with little room when
/// flown a little off, clearing the edge it jumps up past by little, or leaving from near
/// the edge of its own floor. An easy leap has none.
/// </summary>
public readonly record struct NavLeap(int From, int To, float Power, bool Run, float Risk = 0f);

/// <summary>
/// A leap a route takes: the leg that ends at <c>Legs[LegIndex]</c> is flown from
/// <c>Legs[LegIndex - 1]</c> as a standing long jump charged to
/// <paramref name="Power"/>, at running or walking pace.
/// </summary>
public readonly record struct NavRouteLeap(int LegIndex, float Power, bool Run);

/// <summary>
/// Finds the leaps a body can take between pieces of floor no walk joins, such as
/// platforms, ledges, and the floor beyond a gap or below a drop. Leaps are aimed at the
/// middle of each other piece in reach: the centre of a small platform, and a band well
/// inside a larger floor. Bands nearer the edge are aimed at too, at a risk, for leaps
/// that reach nothing deeper. Along the line from a spot back toward the piece the body
/// stands on, every takeoff is solved for the jump that comes down on the spot, at a walk and at
/// a run. The least risky, those whose landing moves least when the jump
/// leaves a little off, are flown along their arcs through the grid's walls, floors and
/// ceilings. A leap is kept when it comes down clear on the spot's piece and clears the lip
/// of any higher floor it passes with room to spare, and still does both charged a little
/// more or less, turned a little either way, or left from a little short of its takeoff or
/// a little past it. Arcs fly, land and slide as <see cref="NavLeapPhysics"/> carries a body: a
/// landing is aimed short of its spot by all the body goes on after it comes down, so it comes to
/// rest on the spot, and where that fails, short by only what carries it on off the ground, so it
/// comes down to stay on the spot and slides on past it. Either way it is followed on to where it
/// comes to rest, over a ledge while it is still off the ground but held at one once it slides
/// along the ground.
/// </summary>
internal sealed class NavLeapFinder
{
    private const float Gravity = 9.8f;

    /// <summary>How far the body flies between the points of its arc that are tested: a column's width.</summary>
    private const float SampleStep = 0.25f;

    /// <summary>
    /// How far, measured flat, a takeoff may stand from the spot it aims at: as far as the body
    /// carries at a run on a full-power jump before it comes back down to the height it left
    /// from, and never less than <see cref="LeastAimReach"/>.
    /// </summary>
    private readonly float _aimReach;
    private const float LeastAimReach = 4f;

    /// <summary>How much farther than its spot an arc is followed before it counts as landing nowhere.</summary>
    private const float FlightOvershoot = 4f;

    /// <summary>The shortest leap kept, measured flat; anything shorter is a step.</summary>
    private const float ShortestLeap = 0.5f;

    /// <summary>
    /// A jump is aimed again for how far the body is carried on off the ground after the arc last
    /// aimed comes down, at most <see cref="AimPasses"/> times, until that distance changes by no
    /// more than <see cref="SlideTolerance"/>.
    /// </summary>
    private const int AimPasses = 3;
    private const float SlideTolerance = 0.05f;

    /// <summary>
    /// How much more power than it was charged to a jump may leave with: a charge
    /// releases on the first frame past its time.
    /// </summary>
    private const float PowerSlack = 1f / 30f;

    /// <summary>How far to either side of its landing a body may face as it leaves: it turns to face the landing to within this.</summary>
    private const float HeadingSlackDegrees = 2f;

    /// <summary>How far short of its takeoff, or past it, a body may stop before it jumps.</summary>
    private const float TakeoffSlack = 0.35f;

    /// <summary>
    /// What each meter a landing may move costs a takeoff, in meters, when takeoffs are
    /// ranked: the farther of how far it moves for <see cref="PowerSlack"/> more or less
    /// power and how far aside turning <see cref="HeadingSlackDegrees"/> takes it. A landing
    /// that may move more than <see cref="EasyShiftMeters"/> is a risk.
    /// </summary>
    private const float ShiftCost = 4f;
    private const float EasyShiftMeters = 0.5f;

    /// <summary>
    /// A leap up onto higher floor must clear that floor's lip by at least
    /// <see cref="LeastLipClearance"/> across the body's whole width. Clearing it by less
    /// than <see cref="EasyLipClearance"/> is a risk.
    /// </summary>
    private const float LeastLipClearance = 0.15f;
    private const float EasyLipClearance = 0.5f;

    /// <summary>A takeoff standing less than this far back from the edge it leaves is a risk.</summary>
    private const float EasyTakeoffBack = 0.5f;

    /// <summary>A leap flown farther than this, measured flat, is a risk: the same small error carries its landing farther.</summary>
    private const float EasyLeapMeters = 10f;

    /// <summary>What a leap's power costs, in meters, when takeoffs are ranked.</summary>
    private const float PowerCost = 2f;

    /// <summary>
    /// How far in from its piece's edge, in steps, a leap aims: at the middle of a piece
    /// no deeper than this, and otherwise in a band this deep. Leaps also aim at a band
    /// <see cref="InsideRoomSteps"/> deep and one <see cref="NearRoomSteps"/> deep, a risk
    /// more for each, so a route uses them only when it must.
    /// </summary>
    private const int CentreRoomSteps = 12;
    private const int InsideRoomSteps = 4;
    private const int NearRoomSteps = 2;

    /// <summary>
    /// A leap flown a little off must still come down at least <see cref="LeastLandingRoomSteps"/>
    /// from its piece's edge, or as far as a narrower piece allows; less than
    /// <see cref="EasyLandingRoomSteps"/> is a risk.
    /// </summary>
    private const int LeastLandingRoomSteps = 2;
    private const int EasyLandingRoomSteps = 4;

    /// <summary>A takeoff nearer the edge of its floor than this many steps, or than that floor's middle, is a risk.</summary>
    private const int EasyTakeoffRoomSteps = 4;

    /// <summary>A spot counts as already aimed at when a kept leap no riskier, from within <see cref="SameTakeoffMeters"/> of a takeoff, comes down within <see cref="SameLandingMeters"/> of it.</summary>
    private const float SameLandingMeters = 0.75f;
    private const float SameTakeoffMeters = 1.5f;

    /// <summary>How far past the heights a jump can reach the nearest spot of a piece may lie and still set the line a leap is aimed along.</summary>
    private const float SpotHeightSlack = 1f;

    /// <summary>
    /// Spots lie on every node of a piece no deeper than <see cref="InsideRoomSteps"/>, every
    /// <see cref="SmallPieceSpotColumns"/> columns on a piece of at most <see cref="SmallPieceNodes"/>
    /// clear nodes, and every <see cref="LargePieceSpotColumns"/> columns on a larger one.
    /// </summary>
    private const int SmallPieceNodes = 1024;
    private const int SmallPieceSpotColumns = 2;
    private const int LargePieceSpotColumns = 4;

    /// <summary>Spots are sorted into square tiles this many columns wide.</summary>
    private const int TileColumns = 16;

    /// <summary>How many of the most promising takeoffs are flown, at each pace, for each spot.</summary>
    private const int TriesPerPace = 2;

    private static readonly bool[] Paces = [false, true];

    private static readonly ConditionalWeakTable<NavGrid, Surfaces> SurfacesOf = new();

    /// <summary>
    /// The piece of floor each node belongs to: clear floor a body walks across belongs to one
    /// piece, a perch too small to walk on is a piece of its own, and a node that is neither is
    /// -1. Worked out once per grid and shared with the leaps found on it.
    /// </summary>
    internal static int[] FloorPieces(NavGrid grid) =>
        SurfacesOf.GetValue(grid, static built => new Surfaces(built)).PieceOf;

    private readonly NavGrid _grid;
    private readonly NavLeapAbility _ability;
    private readonly Surfaces _surfaces;
    private readonly ConcurrentDictionary<int, Dictionary<int, NavLeap[]>> _byPiece = new();
    private readonly ConcurrentDictionary<int, NavLeap[]> _standing = new();

    public NavLeapFinder(NavGrid grid, NavLeapAbility ability)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _ability = ability;
        _surfaces = SurfacesOf.GetValue(grid, static built => new Surfaces(built));
        _aimReach = AimReachOf(ability);
    }

    /// <summary>How far, measured flat, a body carries at a run on a full-power jump before it is back down at the height it left from.</summary>
    internal static float AimReachOf(NavLeapAbility ability)
    {
        float speed = MathF.Max(ability.RunSpeed, ability.WalkSpeed);
        if (!(speed > 0f))
            return LeastAimReach;
        float launch = Launch(ability.JumpHeight(1f));
        float seconds = 0f;
        do
        {
            seconds += 0.01f;
        }
        while (ability.Physics.Height(launch, seconds) > 0f && seconds < 10f);
        return MathF.Max(LeastAimReach, speed * seconds);
    }

    /// <summary>
    /// The leaps a body standing on <paramref name="node"/> can take, found once for each
    /// piece of floor. A piece's leaps are solved from the takeoffs each aim traced across
    /// it, and a body walks to one of those before it jumps. Floor with no room to walk on
    /// offers no such walk: wherever a body stands on it, it stands at the takeoff, so the
    /// whole piece's leaps are open to it, each taken from where the body really is.
    /// </summary>
    public NavLeap[] From(int node)
    {
        int piece = _surfaces.PieceOf[node];
        if (piece < 0)
            return [];
        Dictionary<int, NavLeap[]> byTakeoff = _byPiece.GetOrAdd(piece, FindFrom);
        if (byTakeoff.TryGetValue(node, out NavLeap[]? leaps))
            return leaps;
        return _surfaces.Deepest[piece] == 0
            ? _standing.GetOrAdd(node, _ => Standing(byTakeoff, node))
            : [];
    }

    /// <summary>Every leap of a piece with no room to walk on, taken from where a body stands on it.</summary>
    private static NavLeap[] Standing(Dictionary<int, NavLeap[]> byTakeoff, int node)
    {
        var standing = new List<NavLeap>();
        foreach ((_, NavLeap[] leaps) in byTakeoff)
        {
            foreach (NavLeap leap in leaps)
            {
                if (leap.To != node)
                    standing.Add(leap with { From = node });
            }
        }
        return [.. standing];
    }

    /// <summary>The leaps from anywhere on a piece of floor to the spots of every other piece in reach, by takeoff.</summary>
    private Dictionary<int, NavLeap[]> FindFrom(int piece)
    {
        Surfaces surfaces = _surfaces;
        int side = surfaces.TileSide;
        int reach = TilesWithin(_aimReach);
        var holds = new bool[side * side];
        var near = new bool[side * side];
        for (int tile = 0; tile < holds.Length; tile++)
        {
            if (!surfaces.TileHolds(tile, piece))
                continue;
            holds[tile] = true;
            int tileX = tile % side;
            int tileY = tile / side;
            for (int y = Math.Max(0, tileY - reach); y <= Math.Min(side - 1, tileY + reach); y++)
            {
                for (int x = Math.Max(0, tileX - reach); x <= Math.Min(side - 1, tileX + reach); x++)
                    near[(y * side) + x] = true;
            }
        }

        var found = new Dictionary<int, List<NavLeap>>();
        var taken = new HashSet<(int From, int To, bool Run)>();
        var kept = new Dictionary<int, List<(Vector3 From, Vector3 To, float Risk)>>();
        var candidates = new List<Candidate>();
        for (int tile = 0; tile < near.Length; tile++)
        {
            if (!near[tile])
                continue;
            for (int index = surfaces.TileStart[tile]; index < surfaces.TileStart[tile + 1]; index++)
            {
                int spot = surfaces.Spots[index];
                if (surfaces.PieceOf[spot] != piece)
                    AimAt(piece, holds, spot, surfaces.SpotTiers[index], candidates, found, taken, kept);
            }
        }

        var leaps = new Dictionary<int, NavLeap[]>(found.Count);
        foreach ((int from, List<NavLeap> list) in found)
            leaps[from] = [.. list];
        return leaps;
    }

    /// <summary>
    /// Aims leaps from a piece of floor at a spot on another, <paramref name="tier"/> bands
    /// out from that piece's middle. Along the line from the spot back toward the piece's
    /// nearest spot, each of the piece's takeoffs is solved for the jump that comes down on
    /// the spot. At each pace the least risky are flown until one lands with room to spare.
    /// </summary>
    private void AimAt(
        int piece,
        bool[] holds,
        int spot,
        int tier,
        List<Candidate> candidates,
        Dictionary<int, List<NavLeap>> found,
        HashSet<(int From, int To, bool Run)> taken,
        Dictionary<int, List<(Vector3 From, Vector3 To, float Risk)>> kept)
    {
        Vector3 landing = _grid.Position(spot);
        int nearest = NearestSpot(piece, holds, landing);
        if (nearest < 0)
            return;
        Vector3 toward = _grid.Position(nearest);
        Vector2 back = Flat(toward) - Flat(landing);
        float length = back.Length();
        if (length < 1e-3f)
            return;
        Line line = Trace(spot, back / length, piece, toward.Z);
        if (line.Takeoffs.Count == 0)
            return;

        candidates.Clear();
        float runOut = _grid.BorderDistance(spot) * _grid.CellSize;
        int easyTakeoffRoom = Math.Min(EasyTakeoffRoomSteps, _surfaces.Deepest[piece] - 1);
        foreach ((int node, float distance) in line.Takeoffs)
        {
            Vector3 from = _grid.Position(node);
            float rise = landing.Z - from.Z;
            float standsBack = MathF.Max(0f, distance - line.EdgeAt);
            float beforeFace = distance - line.FaceAt - _grid.Body.Radius;
            float takeoffRisk = (standsBack < EasyTakeoffBack ? 1f : 0f)
                + (_grid.BorderDistance(node) < easyTakeoffRoom ? 1f : 0f);
            foreach (bool run in Paces)
            {
                float speed = run ? _ability.RunSpeed : _ability.WalkSpeed;
                bool aimsToRest = AimShort(from, landing, speed, settles: true, out Vector3 aimAt, out float power, out float launch);
                bool aimsToLand = AimShort(from, landing, speed, settles: false, out Vector3 landAt, out float landPower, out float landLaunch);
                float restClearance = aimsToRest ? Clearance(launch) : float.NegativeInfinity;
                float landClearance = aimsToLand ? Clearance(landLaunch) : float.NegativeInfinity;
                aimsToRest &= restClearance >= LeastLipClearance;
                aimsToLand &= landClearance >= LeastLipClearance;
                if (!aimsToRest && !aimsToLand)
                    continue;
                if (!aimsToRest)
                    (aimAt, power, launch) = (landAt, landPower, landLaunch);
                float clearance = aimsToRest ? restClearance : landClearance;
                bool twin = aimsToRest && aimsToLand && Vector2.Distance(Flat(aimAt), Flat(landAt)) > SlideTolerance;
                float flat = Vector2.Distance(Flat(from), Flat(aimAt));
                float shift = MathF.Max(
                    LandingShift(launch, speed, rise),
                    flat * MathF.Tan(HeadingSlackDegrees * (MathF.PI / 180f)));
                float lipRisk = clearance < EasyLipClearance ? 1f : 0f;
                float risk = tier
                    + takeoffRisk
                    + lipRisk
                    + (shift > EasyShiftMeters ? 1f : 0f)
                    + (flat > EasyLeapMeters ? 1f : 0f);
                float cost = flat + MathF.Abs(rise) + (power * PowerCost) + (shift * ShiftCost);
                candidates.Add(new Candidate(node, aimAt, power, run, risk, lipRisk, cost, aimsToRest, landAt, twin ? landPower : float.NaN));

                float Clearance(float leaves) => rise <= 0f ? float.PositiveInfinity
                    : beforeFace > 0f ? _ability.Physics.Height(leaves, beforeFace / speed) - rise
                    : float.NegativeInfinity;
            }
        }
        if (candidates.Count == 0)
            return;
        candidates.Sort(static (a, b) => a.Risk != b.Risk ? a.Risk.CompareTo(b.Risk) : a.Cost.CompareTo(b.Cost));

        int target = _surfaces.PieceOf[spot];
        int deepest = _surfaces.Deepest[target];
        int leastRoom = Math.Max(0, Math.Min(LeastLandingRoomSteps, deepest - 1));
        int easyRoom = Math.Max(0, Math.Min(EasyLandingRoomSteps, deepest - 1));
        if (!kept.TryGetValue(target, out List<(Vector3 From, Vector3 To, float Risk)>? keptOnTarget))
            kept[target] = keptOnTarget = [];
        // Floor narrower than the body cannot hold a jump that leaves a little off, so on
        // a post or a sign a jump is kept for what it is - one that has to be flown as
        // aimed - and carries the risk of that, rather than being ruled out.
        bool fussy = runOut < _grid.Body.Radius;
        bool Flies(
            int node,
            Vector3 aimAt,
            float charged,
            bool running,
            bool settles,
            out int lands,
            out float lip,
            out int room,
            out bool absorbs)
        {
            lands = FlyAt(node, aimAt, charged, running, target, settles, out lip, out room);
            absorbs = false;
            if (lands < 0 || room < leastRoom)
                return false;
            int steady = WorstRoom(node, aimAt, charged, running, target, leastRoom, settles);
            absorbs = steady >= leastRoom;
            if (absorbs)
                room = Math.Min(room, steady);
            return absorbs || fussy;
        }

        foreach (bool run in Paces)
        {
            int tries = 0;
            foreach (Candidate candidate in candidates)
            {
                if (candidate.Run != run)
                    continue;
                if (tries++ == TriesPerPace)
                    break;
                Vector3 from = _grid.Position(candidate.Node);
                if (AlreadyAimed(keptOnTarget, from, landing, candidate.Risk))
                    break;
                float power = candidate.Power;
                bool flies = Flies(
                    candidate.Node,
                    candidate.AimAt,
                    power,
                    run,
                    candidate.Settles,
                    out int lands,
                    out float lip,
                    out int room,
                    out bool absorbs);
                if (!flies && !float.IsNaN(candidate.LandPower))
                {
                    power = candidate.LandPower;
                    flies = Flies(
                        candidate.Node,
                        candidate.LandAt,
                        power,
                        run,
                        settles: false,
                        out lands,
                        out lip,
                        out room,
                        out absorbs);
                }
                if (!flies)
                    continue;
                float risk = candidate.Risk
                    - candidate.LipRisk
                    + (lip < EasyLipClearance ? 1f : 0f)
                    + (room < easyRoom ? 1f : 0f)
                    + (absorbs ? 0f : 1f);
                if (taken.Add((candidate.Node, lands, run)))
                {
                    if (!found.TryGetValue(candidate.Node, out List<NavLeap>? list))
                        found[candidate.Node] = list = [];
                    list.Add(new NavLeap(candidate.Node, lands, power, run, risk));
                    keptOnTarget.Add((from, _grid.Position(lands), risk));
                }
                break;
            }
        }
    }

    /// <summary>Whether a leap no riskier than <paramref name="risk"/> is already kept from near a takeoff to near a spot.</summary>
    private static bool AlreadyAimed(List<(Vector3 From, Vector3 To, float Risk)> kept, Vector3 from, Vector3 spot, float risk)
    {
        foreach ((Vector3 keptFrom, Vector3 keptTo, float keptRisk) in kept)
        {
            if (keptRisk <= risk
                && Vector2.Distance(Flat(keptTo), Flat(spot)) <= SameLandingMeters
                && Vector2.Distance(Flat(keptFrom), Flat(from)) <= SameTakeoffMeters)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Aims a jump from <paramref name="from"/> at <paramref name="speed"/> at <paramref name="spot"/>:
    /// with <paramref name="settles"/>, so the body comes to rest on the spot, its arc coming down
    /// short of it by all the body goes on after it lands; otherwise so the body comes down to stay
    /// on the spot, short of it by only what carries the body on off the ground, and slides on past
    /// it. It first aims short by the least that could be, landing without a hop, then again for
    /// the arc last aimed, keeping the last arc a jump can fly. Gives the point that arc is aimed to
    /// come down through, and the jump's power and launch speed; false when no jump comes down short
    /// enough.
    /// </summary>
    private bool AimShort(Vector3 from, Vector3 spot, float speed, bool settles, out Vector3 aimAt, out float power, out float launch)
    {
        aimAt = spot;
        power = 0f;
        launch = 0f;
        Vector2 toward = Flat(spot) - Flat(from);
        float distance = toward.Length();
        if (distance < 1e-3f)
            return false;
        bool aimed = false;
        float shortBy = ShortBy(speed, 0f, settles);
        for (int pass = 0; pass < AimPasses; pass++)
        {
            float flat = distance - shortBy;
            var point = new Vector3(Flat(spot) - (toward * (shortBy / distance)), spot.Z);
            if (flat < ShortestLeap || !Aim(from, point, speed, out float charged, out float leaves))
                break;
            (aimAt, power, launch, aimed) = (point, charged, leaves, true);
            float goesOn = ShortBy(speed, _ability.Physics.FallSpeed(leaves, flat / speed), settles);
            if (MathF.Abs(goesOn - shortBy) <= SlideTolerance)
                break;
            shortBy = goesOn;
        }
        return aimed;
    }

    /// <summary>
    /// How far short of its spot a jump at <paramref name="speed"/> that comes down falling at
    /// <paramref name="fall"/> is aimed: by all the body goes on, to rest on the spot, or by what
    /// carries it on off the ground, to come down to stay on it.
    /// </summary>
    private float ShortBy(float speed, float fall, bool settles) =>
        settles ? _ability.Physics.Slide(speed, fall) : _ability.Physics.Carry(speed, fall);

    /// <summary>
    /// The power and launch speed of the jump that, leaving <paramref name="from"/> at
    /// <paramref name="speed"/>, comes down through <paramref name="to"/>. When even the
    /// lowest jump flies past it, that jump. False when no jump comes down through it.
    /// </summary>
    private bool Aim(Vector3 from, Vector3 to, float speed, out float power, out float launch)
    {
        power = 0f;
        launch = 0f;
        float distance = Vector2.Distance(Flat(from), Flat(to));
        if (!(speed > 0f) || distance < ShortestLeap)
            return false;
        float time = distance / speed;
        float needed = _ability.Physics.LaunchFor(to.Z - from.Z, time);
        float lowest = Launch(NavLeapAbility.LowestJump);
        if (needed > MathF.Max(lowest, Launch(_ability.FullJumpHeight)))
            return false;
        if (needed >= lowest && _ability.Physics.FallSpeed(needed, time) <= 0f)
            return false;
        launch = MathF.Max(needed, lowest);
        float height = launch * launch / (2f * Gravity);
        power = _ability.FullJumpHeight > NavLeapAbility.LowestJump
            ? Math.Clamp(height / _ability.FullJumpHeight, NavLeapAbility.LowestJump / _ability.FullJumpHeight, 1f)
            : 1f;
        return true;
    }

    /// <summary>
    /// The nearest spot of a piece, measured flat, within <see cref="_aimReach"/> of a
    /// point and at a height a jump from it could reach the point from, or -1.
    /// </summary>
    private int NearestSpot(int piece, bool[] holds, Vector3 point)
    {
        Surfaces surfaces = _surfaces;
        int side = surfaces.TileSide;
        float tileSize = TileColumns * _grid.CellSize;
        int centreX = (int)MathF.Floor((point.X - _grid.OriginX) / tileSize);
        int centreY = (int)MathF.Floor((point.Y - _grid.OriginY) / tileSize);
        int reach = TilesWithin(_aimReach);
        int nearest = -1;
        float best = _aimReach * _aimReach;
        for (int ring = 0; ring <= reach; ring++)
        {
            float ringNearest = (ring - 1) * tileSize;
            if (ring > 1 && ringNearest * ringNearest >= best)
                break;
            for (int y = centreY - ring; y <= centreY + ring; y++)
            {
                if ((uint)y >= (uint)side)
                    continue;
                bool edgeRow = y == centreY - ring || y == centreY + ring;
                for (int x = centreX - ring; x <= centreX + ring; x += edgeRow ? 1 : 2 * Math.Max(ring, 1))
                {
                    int tile = (y * side) + x;
                    if ((uint)x >= (uint)side || !holds[tile])
                        continue;
                    for (int index = surfaces.TileStart[tile]; index < surfaces.TileStart[tile + 1]; index++)
                    {
                        int spot = surfaces.Spots[index];
                        if (surfaces.PieceOf[spot] != piece)
                            continue;
                        Vector3 at = _grid.Position(spot);
                        float rise = point.Z - at.Z;
                        if (rise > _ability.FullJumpHeight + SpotHeightSlack || rise < -(_ability.MaximumDrop + SpotHeightSlack))
                            continue;
                        float squared = Vector2.DistanceSquared(Flat(at), Flat(point));
                        if (squared < best)
                        {
                            best = squared;
                            nearest = spot;
                        }
                    }
                }
            }
        }
        return nearest;
    }

    /// <summary>
    /// Follows a line flat from a spot, column by column, out to <see cref="_aimReach"/>:
    /// how far along it the spot's floor stops being walkable, how far along the piece's
    /// floor begins, and the piece's clear node in each column it crosses, with its
    /// distance from the spot.
    /// </summary>
    private Line Trace(int spot, Vector2 direction, int piece, float height)
    {
        Vector3 start = _grid.Position(spot);
        (int x, int y) = _grid.ColumnOf(spot);
        float cell = _grid.CellSize;
        float localX = start.X - _grid.OriginX;
        float localY = start.Y - _grid.OriginY;
        int stepX = MathF.Sign(direction.X);
        int stepY = MathF.Sign(direction.Y);
        float nextX = stepX == 0
            ? float.PositiveInfinity
            : (stepX > 0 ? ((x + 1) * cell) - localX : localX - (x * cell)) / MathF.Abs(direction.X);
        float nextY = stepY == 0
            ? float.PositiveInfinity
            : (stepY > 0 ? ((y + 1) * cell) - localY : localY - (y * cell)) / MathF.Abs(direction.Y);
        float acrossX = stepX == 0 ? float.PositiveInfinity : cell / MathF.Abs(direction.X);
        float acrossY = stepY == 0 ? float.PositiveInfinity : cell / MathF.Abs(direction.Y);

        int onFloor = spot;
        float faceAt = float.PositiveInfinity;
        float edgeAt = float.PositiveInfinity;
        var takeoffs = new List<(int Node, float Distance)>();
        while (true)
        {
            float entered;
            int step;
            if (nextX < nextY)
            {
                x += stepX;
                entered = nextX;
                nextX += acrossX;
                step = NavGrid.DirectionOf(stepX, 0);
            }
            else
            {
                y += stepY;
                entered = nextY;
                nextY += acrossY;
                step = NavGrid.DirectionOf(0, stepY);
            }
            if (entered > _aimReach || (uint)x >= (uint)_grid.Side || (uint)y >= (uint)_grid.Side)
                break;
            if (onFloor >= 0)
            {
                int next = _grid.Link(onFloor, step);
                if (next < 0 || _surfaces.PieceOf[next] == piece)
                {
                    faceAt = entered;
                    onFloor = -1;
                }
                else
                {
                    onFloor = next;
                }
            }
            (int first, int count) = _grid.NodesInColumn(x, y);
            int best = -1;
            for (int node = first; node < first + count; node++)
            {
                if (_surfaces.PieceOf[node] == piece
                    && (best < 0 || MathF.Abs(_grid.Position(node).Z - height) < MathF.Abs(_grid.Position(best).Z - height)))
                {
                    best = node;
                }
            }
            if (best < 0)
                continue;
            Vector3 at = _grid.Position(best);
            edgeAt = MathF.Min(edgeAt, entered);
            height = at.Z;
            takeoffs.Add((best, Vector2.Distance(Flat(start), Flat(at))));
        }
        return new Line(faceAt, edgeAt, takeoffs);
    }

    /// <summary>
    /// Flies a jump from a node toward a point and returns the clear node of the target piece the
    /// body comes to rest on, or -1, with how far it cleared the lips it passed on the way and its
    /// room as <see cref="Settle"/> counts it for a leap aimed, with <paramref name="settles"/>, to
    /// rest on its spot, or else only to come down to stay on it.
    /// </summary>
    private int FlyAt(int from, Vector3 toward, float power, bool run, int target, bool settles, out float lip, out int room)
    {
        lip = float.PositiveInfinity;
        room = -1;
        Vector3 start = _grid.Position(from);
        Vector2 line = Flat(toward) - Flat(start);
        float distance = line.Length();
        if (distance < 1e-3f)
            return -1;
        Vector2 heading = line / distance;
        int touchdown = Fly(start, heading, power, run, distance + FlightOvershoot, out lip, out float fall);
        if (touchdown < 0 || lip < LeastLipClearance)
            return -1;
        (float carry, float slide) = GoesOn(run, fall);
        int rest = Settle(touchdown, heading, carry, slide, target, settles, out room);
        return rest >= 0 && Lands(from, rest, start, target) ? rest : -1;
    }

    /// <summary>
    /// The least room, in steps from the target piece's edge, a jump from a node toward a
    /// spot comes down with when it leaves a little off: turned <see cref="HeadingSlackDegrees"/>
    /// either way, from <see cref="TakeoffSlack"/> short of the node or past it, or charged
    /// <see cref="PowerSlack"/> more or less, each followed through its landing slide. Zero for
    /// a landing on the piece's very edge, and -1 as soon as one misses the piece, touches a
    /// lip on the way, slides off, or keeps less than <paramref name="least"/>.
    /// Room is counted as <see cref="Settle"/> counts it for a leap aimed, with
    /// <paramref name="settles"/>, to rest on its spot, or else only to come down to stay on it.
    /// </summary>
    private int WorstRoom(int from, Vector3 spot, float power, bool run, int target, int least, bool settles)
    {
        Vector3 start = _grid.Position(from);
        Vector2 toward = Flat(spot) - Flat(start);
        float distance = toward.Length();
        Vector2 heading = toward / distance;
        float longest = distance + FlightOvershoot;
        var slack = new Vector3(heading * TakeoffSlack, 0f);
        int worst = int.MaxValue;
        Vector2 turnedLeft = Turned(heading, HeadingSlackDegrees);
        Vector2 turnedRight = Turned(heading, -HeadingSlackDegrees);
        bool Keeps(int touchdown, float lip, float fall, Vector2 along)
        {
            int room = -1;
            if (touchdown >= 0 && lip >= 0f)
            {
                (float carry, float slide) = GoesOn(run, fall);
                Settle(touchdown, along, carry, slide, target, settles, out room);
            }
            worst = Math.Min(worst, room);
            return worst >= least;
        }
        bool keeps = Keeps(Fly(start, turnedLeft, power, run, longest, out float left, out float leftFall), left, leftFall, turnedLeft)
            && Keeps(Fly(start, turnedRight, power, run, longest, out float right, out float rightFall), right, rightFall, turnedRight)
            && Keeps(Fly(start - slack, heading, power, run, longest + TakeoffSlack, out float shortOf, out float shortOfFall), shortOf, shortOfFall, heading)
            && Keeps(Fly(start + slack, heading, power, run, longest, out float pastIt, out float pastItFall), pastIt, pastItFall, heading)
            && (power >= 1f || Keeps(Fly(start, heading, MathF.Min(1f, power + PowerSlack), run, longest, out float stronger, out float strongerFall), stronger, strongerFall, heading))
            && Keeps(Fly(start, heading, MathF.Max(0f, power - PowerSlack), run, longest, out float weaker, out float weakerFall), weaker, weakerFall, heading);
        return keeps ? worst : -1;
    }

    /// <summary>
    /// About how far along the ground a jump's landing, <paramref name="rise"/> above its
    /// takeoff, moves for <see cref="PowerSlack"/> more or less power: far for a jump that
    /// comes down near the top of its arc, little for one that comes down steeply.
    /// </summary>
    private float LandingShift(float launch, float speed, float rise)
    {
        float fall = (launch * launch) - (2f * Gravity * rise);
        if (!(fall > 0f))
            return float.PositiveInfinity;
        float launchPerPower = Gravity * MathF.Max(_ability.FullJumpHeight, NavLeapAbility.LowestJump) / launch;
        float timePerLaunch = (1f + (launch / MathF.Sqrt(fall))) / Gravity;
        return speed * timePerLaunch * launchPerPower * PowerSlack;
    }

    /// <summary>
    /// How far a body leaping at the pace goes on off the ground after its arc comes down falling
    /// at <paramref name="fall"/>, and how far it then slides along the ground.
    /// </summary>
    private (float Carry, float Slide) GoesOn(bool run, float fall)
    {
        float speed = run ? _ability.RunSpeed : _ability.WalkSpeed;
        return (_ability.Physics.Carry(speed, fall), _ability.Physics.GroundSlide(speed));
    }

    /// <summary>
    /// Follows a body on from the node it came down on along a heading, over floor linked step by
    /// step: off the ground for <paramref name="carry"/>, stopping against a wall or a rise and
    /// going over a ledge, and then sliding along the ground for <paramref name="slide"/>, held at
    /// a ledge as well as by a wall or a rise. Floor linked on from the piece that belongs to no piece,
    /// such as the strip along its edge or too near a wall for the body to stand clear, still carries
    /// a sliding body, with no room. Returns the node of the piece where it comes to rest, or the last
    /// one it slid over when it comes to rest on such floor beside it; or -1 when it never stands on
    /// the piece, comes down off it, goes over a ledge, or slides onto another piece's floor. Its room
    /// is in steps from the target piece's edge. For a leap aimed, with <paramref name="settles"/>, to rest
    /// on its spot, that is its room where it comes to rest, unless a ledge held it; otherwise, and
    /// then, its room where it came back down to stay.
    /// </summary>
    private int Settle(int touchdown, Vector2 heading, float carry, float slide, int target, bool settles, out int room)
    {
        room = RoomOn(touchdown, target);
        if (room < 0)
            return -1;
        int cameDown = room;
        float goesOn = carry + slide;
        if (!(goesOn > 0f))
            return touchdown;
        Vector3 start = _grid.Position(touchdown);
        (int x, int y) = _grid.ColumnOf(touchdown);
        float cell = _grid.CellSize;
        float localX = start.X - _grid.OriginX;
        float localY = start.Y - _grid.OriginY;
        int stepX = MathF.Sign(heading.X);
        int stepY = MathF.Sign(heading.Y);
        float nextX = stepX == 0
            ? float.PositiveInfinity
            : (stepX > 0 ? ((x + 1) * cell) - localX : localX - (x * cell)) / MathF.Abs(heading.X);
        float nextY = stepY == 0
            ? float.PositiveInfinity
            : (stepY > 0 ? ((y + 1) * cell) - localY : localY - (y * cell)) / MathF.Abs(heading.Y);
        float acrossX = stepX == 0 ? float.PositiveInfinity : cell / MathF.Abs(heading.X);
        float acrossY = stepY == 0 ? float.PositiveInfinity : cell / MathF.Abs(heading.Y);
        int at = touchdown;
        int standing = _surfaces.PieceOf[touchdown] == target ? touchdown : -1;
        while (true)
        {
            float entered;
            int direction;
            if (nextX < nextY)
            {
                entered = nextX;
                nextX += acrossX;
                direction = NavGrid.DirectionOf(stepX, 0);
            }
            else
            {
                entered = nextY;
                nextY += acrossY;
                direction = NavGrid.DirectionOf(0, stepY);
            }
            bool aloft = entered <= carry;
            int next = entered > goesOn ? -1 : _grid.Link(at, direction);
            if (entered > goesOn || (next < 0 && StopsAgainst(at, direction)))
            {
                room = standing < 0 ? -1 : settles ? room : cameDown;
                return standing;
            }
            if (next < 0)
            {
                room = aloft || standing < 0 ? -1 : cameDown;
                return aloft ? -1 : standing;
            }
            int nextRoom = RoomOn(next, target);
            if (nextRoom < 0)
            {
                if (aloft || _surfaces.PieceOf[next] >= 0)
                {
                    room = -1;
                    return -1;
                }
                nextRoom = 0;
            }
            room = nextRoom;
            if (aloft)
                cameDown = nextRoom;
            at = next;
            if (_surfaces.PieceOf[at] == target)
                standing = at;
        }
    }

    /// <summary>Whether a body sliding from a node toward the neighbouring column is stopped there by a wall or a rise, rather than going over a ledge.</summary>
    private bool StopsAgainst(int node, int direction)
    {
        Vector3 here = _grid.Position(node);
        (int x, int y) = _grid.ColumnOf(node);
        (int stepX, int stepY) = NavGrid.StepOf(direction);
        (int first, int count) = _grid.NodesInColumn(x + stepX, y + stepY);
        for (int other = first; other < first + count; other++)
        {
            float rise = _grid.Position(other).Z - here.Z;
            if (rise > _grid.Body.StepUpHeight && rise < _grid.Body.Height)
                return true;
        }
        var beside = new Vector3(here.X + (stepX * _grid.CellSize), here.Y + (stepY * _grid.CellSize), here.Z);
        return !_grid.IsOpenLine(here, beside);
    }

    /// <summary>How far in from a piece's edge, in steps, a landing node lies: zero for an edge node beside the piece, -1 off it.</summary>
    private int RoomOn(int node, int target)
    {
        if (node < 0)
            return -1;
        if (_surfaces.PieceOf[node] == target)
            return _grid.BorderDistance(node);
        return OnPiece(node, target) ? 0 : -1;
    }

    /// <summary>
    /// Flies a standing long jump from a point along a heading and returns the node it
    /// comes down on, or -1 when the arc strikes something, falls deeper than a route
    /// may drop, or lands nowhere within <paramref name="longest"/>, measured flat. A body
    /// coming down onto a slope meets it with its side before its feet reach the floor
    /// under them, so floor within the body's radius below its feet counts as floor it
    /// comes down on. It also gives how far the body's whole width cleared the lips of
    /// higher floor it passed before coming down, as <see cref="LipAhead"/> measures them:
    /// infinity for none; and how fast it was falling as its arc met the floor it comes
    /// down on.
    /// </summary>
    private int Fly(Vector3 start, Vector2 heading, float power, bool run, float longest, out float lip, out float fall)
    {
        lip = float.PositiveInfinity;
        fall = 0f;
        float speed = run ? _ability.RunSpeed : _ability.WalkSpeed;
        if (!(speed > 0f))
            return -1;
        float launch = Launch(_ability.JumpHeight(power));
        float previous = start.Z;
        for (float flown = SampleStep; flown <= longest; flown += SampleStep)
        {
            float time = flown / speed;
            float height = start.Z + _ability.Physics.Height(launch, time);
            var feet = new Vector3(start.X + (heading.X * flown), start.Y + (heading.Y * flown), height);
            if (!_grid.Contains(feet) || height < start.Z - _ability.MaximumDrop)
                return -1;
            if (height < previous)
            {
                int landing = _grid.LandingUnder(feet, previous);
                if (landing >= 0)
                {
                    float between = Math.Clamp((previous - _grid.Position(landing).Z) / (previous - height), 0f, 1f);
                    fall = _ability.Physics.FallSpeed(launch, time - ((1f - between) * SampleStep / speed));
                    return landing;
                }
            }
            if (!_grid.IsOpenAir(feet))
            {
                int touching = height < previous
                    ? _grid.LandingUnder(feet - new Vector3(0f, 0f, _grid.Body.Radius), previous)
                    : -1;
                if (touching >= 0)
                    fall = _ability.Physics.FallSpeed(launch, time);
                return touching;
            }
            lip = MathF.Min(lip, LipAhead(feet, heading));
            previous = height;
        }
        return -1;
    }

    /// <summary>
    /// How far above floor ahead of it a body in the air clears, where that floor stands more
    /// than a step higher than the floor under the body's centre: the lip of a higher floor it
    /// is passing over, measured across the body's whole width. Infinity when there is none,
    /// and below zero when such floor reaches above the body's feet.
    /// </summary>
    private float LipAhead(Vector3 feet, Vector2 heading)
    {
        float cell = _grid.CellSize;
        float radius = _grid.Body.Radius;
        int centreX = (int)MathF.Floor((feet.X - _grid.OriginX) / cell);
        int centreY = (int)MathF.Floor((feet.Y - _grid.OriginY) / cell);
        float under = FloorBelow(centreX, centreY, feet.Z) + _grid.Body.StepUpHeight;
        float top = feet.Z + _grid.Body.Height;
        int reach = (int)MathF.Ceiling(radius / cell);
        float lip = float.PositiveInfinity;
        for (int y = centreY - reach; y <= centreY + reach; y++)
        {
            for (int x = centreX - reach; x <= centreX + reach; x++)
            {
                float dx = _grid.OriginX + ((x + 0.5f) * cell) - feet.X;
                float dy = _grid.OriginY + ((y + 0.5f) * cell) - feet.Y;
                if ((dx * dx) + (dy * dy) > radius * radius || (dx * heading.X) + (dy * heading.Y) <= 0f)
                    continue;
                (int first, int count) = _grid.NodesInColumn(x, y);
                for (int node = first; node < first + count; node++)
                {
                    float floor = _grid.Position(node).Z;
                    if (floor > under && floor < top)
                        lip = MathF.Min(lip, feet.Z - floor);
                }
            }
        }
        return lip;
    }

    /// <summary>The top of the highest floor in a column at or below a height, or negative infinity.</summary>
    private float FloorBelow(int x, int y, float height)
    {
        (int first, int count) = _grid.NodesInColumn(x, y);
        float highest = float.NegativeInfinity;
        for (int node = first; node < first + count; node++)
        {
            float floor = _grid.Position(node).Z;
            if (floor <= height && floor > highest)
                highest = floor;
        }
        return highest;
    }

    /// <summary>Whether a landing is one to keep: on the target piece, beyond a step's reach, and not ground the body could walk to.</summary>
    private bool Lands(int from, int landing, Vector3 start, int target)
    {
        Vector3 there = _grid.Position(landing);
        return landing != from
            && _surfaces.PieceOf[landing] == target
            && Vector2.Distance(Flat(start), Flat(there)) >= ShortestLeap
            && _grid.NodesAlong(from, landing).Count == 0;
    }

    /// <summary>Whether a node stands on a piece of floor: one of its clear nodes, or an edge node beside one.</summary>
    private bool OnPiece(int node, int target)
    {
        if (node < 0)
            return false;
        if (_surfaces.PieceOf[node] == target)
            return true;
        for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
        {
            int next = _grid.Link(node, direction);
            if (next >= 0 && _surfaces.PieceOf[next] == target)
                return true;
        }
        return false;
    }

    private static Vector2 Turned(Vector2 heading, float degrees)
    {
        float radians = degrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2((heading.X * cos) - (heading.Y * sin), (heading.X * sin) + (heading.Y * cos));
    }

    private int TilesWithin(float meters) => (int)MathF.Ceiling(meters / (TileColumns * _grid.CellSize));

    private static float Launch(float height) => MathF.Sqrt(2f * Gravity * height);

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    /// <summary>
    /// A takeoff solved for a spot: the point its touchdown is aimed at, short of the spot by all
    /// the body goes on after it lands, or where no such jump can be flown, by what carries it on
    /// off the ground; the jump's power and pace; its risk before it is flown; the part of
    /// that risk its lip clearance, as estimated before flying, adds; and what it costs to rank.
    /// </summary>
    /// <remarks>
    /// <paramref name="Settles"/> tells whether it is aimed to come to rest on the spot. When it is,
    /// <paramref name="LandAt"/> and <paramref name="LandPower"/> give its twin, aimed only to come
    /// down to stay on the spot, flown when it fails; no power for none.
    /// </remarks>
    private readonly record struct Candidate(int Node, Vector3 AimAt, float Power, bool Run, float Risk, float LipRisk, float Cost, bool Settles, Vector3 LandAt, float LandPower);

    /// <summary>
    /// What a line from a spot crosses: the distance at which the spot's floor stops being
    /// walkable, the distance at which the takeoff piece's floor begins, and that piece's
    /// clear nodes along it with their distances from the spot.
    /// </summary>
    private readonly record struct Line(float FaceAt, float EdgeAt, List<(int Node, float Distance)> Takeoffs);

    /// <summary>
    /// A grid's pieces of floor, the clear nodes walks join in both directions, and the
    /// spots near each piece's edges that leaps aim at, sorted into square tiles.
    /// </summary>
    private sealed class Surfaces
    {
        public Surfaces(NavGrid grid)
        {
            int count = grid.NodeCount;
            var parent = new int[count];
            for (int node = 0; node < count; node++)
                parent[node] = node;
            for (int node = 0; node < count; node++)
            {
                if (!grid.IsClear(node))
                    continue;
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next <= node || !grid.IsClear(next))
                        continue;
                    (int stepX, int stepY) = NavGrid.StepOf(direction);
                    if (grid.Link(next, NavGrid.DirectionOf(-stepX, -stepY)) == node)
                        parent[Root(parent, next)] = Root(parent, node);
                }
            }

            // A perch is floor a body stands on with no room to walk clear of its edges,
            // such as the top of a post, and it touches no clear floor. The band along the
            // edge of ordinary floor touches clear floor, so it stays no piece of its own.
            var perch = new bool[count];
            for (int node = 0; node < count; node++)
            {
                if (grid.IsClear(node) || !grid.IsStandable(node))
                    continue;
                bool besideClear = false;
                for (int direction = 0; direction < NavGrid.DirectionCount && !besideClear; direction++)
                {
                    int next = grid.Link(node, direction);
                    besideClear = next >= 0 && grid.IsClear(next);
                }
                perch[node] = !besideClear;
            }
            for (int node = 0; node < count; node++)
            {
                if (!perch[node])
                    continue;
                for (int direction = 0; direction < NavGrid.DirectionCount; direction++)
                {
                    int next = grid.Link(node, direction);
                    if (next <= node || !perch[next])
                        continue;
                    (int stepX, int stepY) = NavGrid.StepOf(direction);
                    if (grid.Link(next, NavGrid.DirectionOf(-stepX, -stepY)) == node)
                        parent[Root(parent, next)] = Root(parent, node);
                }
            }

            PieceOf = new int[count];
            var pieceOfRoot = new int[count];
            Array.Fill(pieceOfRoot, -1);
            var sizes = new List<int>();
            var deepest = new List<int>();
            for (int node = 0; node < count; node++)
            {
                if (!grid.IsClear(node) && !perch[node])
                {
                    PieceOf[node] = -1;
                    continue;
                }
                int root = Root(parent, node);
                if (pieceOfRoot[root] < 0)
                {
                    pieceOfRoot[root] = sizes.Count;
                    sizes.Add(0);
                    deepest.Add(0);
                }
                int piece = pieceOfRoot[root];
                PieceOf[node] = piece;
                sizes[piece]++;
                deepest[piece] = Math.Max(deepest[piece], grid.BorderDistance(node));
            }
            Sizes = [.. sizes];
            Deepest = [.. deepest];

            TileSide = (grid.Side + TileColumns - 1) / TileColumns;
            TileStart = new int[(TileSide * TileSide) + 1];
            for (int node = 0; node < count; node++)
            {
                if (TierOf(grid, node) >= 0)
                    TileStart[TileOf(grid, node) + 1]++;
            }
            for (int tile = 0; tile < TileSide * TileSide; tile++)
                TileStart[tile + 1] += TileStart[tile];
            Spots = new int[TileStart[^1]];
            SpotTiers = new byte[Spots.Length];
            int[] filled = TileStart[..^1];
            for (int node = 0; node < count; node++)
            {
                int tier = TierOf(grid, node);
                if (tier < 0)
                    continue;
                int index = filled[TileOf(grid, node)]++;
                Spots[index] = node;
                SpotTiers[index] = (byte)tier;
            }
        }

        /// <summary>The piece of floor each node belongs to, or -1 for a node that is not clear.</summary>
        public int[] PieceOf { get; }

        /// <summary>How many clear nodes each piece holds.</summary>
        public int[] Sizes { get; }

        /// <summary>How many steps from its edge each piece's deepest node lies.</summary>
        public int[] Deepest { get; }

        public int TileSide { get; }

        /// <summary>Where each tile's spots begin in <see cref="Spots"/>, with the count of all spots at the end.</summary>
        public int[] TileStart { get; }

        public int[] Spots { get; }

        /// <summary>How many bands out from its piece's middle each spot lies: 0 in the middle, 1 and 2 nearer the edge.</summary>
        public byte[] SpotTiers { get; }

        public bool TileHolds(int tile, int piece)
        {
            for (int index = TileStart[tile]; index < TileStart[tile + 1]; index++)
            {
                if (PieceOf[Spots[index]] == piece)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// How many bands out from its piece's middle a node lies as a spot, or -1 for a node
        /// that is not one: 0 at the middle, <see cref="CentreRoomSteps"/> deep on a deeper piece;
        /// 1 at <see cref="InsideRoomSteps"/> deep; 2 at <see cref="NearRoomSteps"/> deep.
        /// </summary>
        private int TierOf(NavGrid grid, int node)
        {
            int piece = PieceOf[node];
            if (piece < 0)
                return -1;
            int deepest = Deepest[piece];
            int room = grid.BorderDistance(node);
            int middle = Math.Min(deepest, CentreRoomSteps);
            int tier;
            if (room >= middle - 1 && room <= middle + 2)
                tier = 0;
            else if (deepest >= InsideRoomSteps + 5 && room >= InsideRoomSteps && room <= InsideRoomSteps + 3)
                tier = 1;
            else if (deepest >= NearRoomSteps + 3 && room >= NearRoomSteps && room <= NearRoomSteps + 1)
                tier = 2;
            else
                return -1;
            int columns = deepest <= InsideRoomSteps ? 1
                : Sizes[piece] <= SmallPieceNodes ? SmallPieceSpotColumns
                : LargePieceSpotColumns;
            (int x, int y) = grid.ColumnOf(node);
            return x % columns == 0 && y % columns == 0 ? tier : -1;
        }

        private static int TileOf(NavGrid grid, int node)
        {
            (int x, int y) = grid.ColumnOf(node);
            return ((y / TileColumns) * ((grid.Side + TileColumns - 1) / TileColumns)) + (x / TileColumns);
        }

        private static int Root(int[] parent, int node)
        {
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }
            return node;
        }
    }
}
