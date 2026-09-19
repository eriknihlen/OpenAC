using System.Numerics;
using AcDream.Core.Navigation;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeRouteDriveState
{
    Driving,

    Arrived,

    /// <summary>The body stopped making progress along a leg; plan again from where it stands.</summary>
    Blocked,

    /// <summary>The player moved the character.</summary>
    Interrupted,

    /// <summary>The character entered portal space or left the world.</summary>
    Lost,

    /// <summary>A leap came down on its landing's level but away from where it was planned; plan on from where the body stands.</summary>
    LandedElsewhere,
}

/// <summary>
/// How fast a body runs and turns at a run, in meters and degrees a second, which
/// sets the arc it follows when it turns as it runs.
/// </summary>
public readonly record struct RuntimeRouteTurning(
    float RunSpeed,
    float RunTurnDegreesPerSecond,
    float WalkSpeed = 0f,
    float WalkTurnDegreesPerSecond = 0f);

/// <summary>
/// The body as a route driver sees it on one frame, with how fast it goes and turns when that
/// is known, and how far a walk and a run carry it on as they end, or zero when that is not known.
/// </summary>
public readonly record struct RuntimeRouteDriveSample(
    Vector3 Position,
    float HeadingDegrees,
    RuntimeScriptedMoveSnapshot Moves,
    bool InPortalSpace,
    bool Airborne = false,
    RuntimeRouteTurning? Turning = null,
    bool Still = true,
    float WalkStopMeters = 0f,
    float RunStopMeters = 0f);

/// <summary>The scripted moves a route driver wants begun or stopped on one frame, the power of a jump to charge, and the pace to leave the ground at as it releases.</summary>
/// <summary>
/// How a leap's takeoff was come up to: the leg walked or run to it, the pace, how many moves
/// were begun on that leg, how far short of the takeoff the last move was let go (NaN when the
/// body was already still), how many turns faced the landing and through how many degrees, and
/// how far from the takeoff the jump charged.
/// </summary>
public readonly record struct RuntimeLeapApproach(
    int Leg,
    RuntimeMovePace Pace,
    int MovesBegun,
    float LetGoMeters,
    int Turns,
    float TurnedDegrees,
    float TakeoffError)
{
    /// <summary>The takeoff, measured flat.</summary>
    public Vector2 TakeoffAt { get; init; }

    /// <summary>Where the body was as the last move on the way was begun, when one was.</summary>
    public Vector2 BegunAt { get; init; }

    /// <summary>Where the body was as the last move was let go, when one was.</summary>
    public Vector2 LetGoAt { get; init; }

    /// <summary>Where the body stood as it began turning to face the landing, when it turned.</summary>
    public Vector2 TurnedAt { get; init; }

    /// <summary>Where the body stood as the jump charged.</summary>
    public Vector2 ChargedAt { get; init; }

    /// <summary>Where the body stood as the jump charged, with its height: what a leap flown from here has to be replayed from.</summary>
    public Vector3 ChargedFrom { get; init; }

    /// <summary>The spot the leap was flown at, where that is not the landing the route planned; null where it is.</summary>
    public Vector3? FlownAt { get; init; }

    /// <summary>The power and pace the route planned the leap at.</summary>
    public float PlannedPower { get; init; }

    public bool PlannedRun { get; init; }

    /// <summary>The leap as aimed again from where the body charged, or null where it was flown as planned.</summary>
    public RuntimeLeapAim? Aimed { get; init; }

    /// <summary>How far, in degrees, the body faced off the line to its landing as it charged, clockwise positive.</summary>
    public float FacingError { get; init; }

    /// <summary>How far the body would have walked to the planned takeoff had it not leapt from where it stood.</summary>
    public float WalkSkipped { get; init; }

    /// <summary>How many times the body went back to the takeoff because no leap from where it came to rest was kept.</summary>
    public int Adjustments { get; init; }

    /// <summary>How many sidesteps the body took onto its takeoff, which it does where a walk is too long a move.</summary>
    public int Sidesteps { get; init; }

    /// <summary>Whether the leap was aimed at another spot on the landing's floor, from where the body stood.</summary>
    public bool AimedOnward { get; init; }
}

public readonly record struct RuntimeRouteDriveStep(
    RuntimeMoveRequest? Travel = null,
    RuntimeMoveRequest? Turn = null,
    bool StopTravel = false,
    bool StopTurn = false,
    float? Jump = null,
    RuntimeMovePace? JumpPace = null)
{
    public bool IsEmpty => Travel is null && Turn is null && !StopTravel && !StopTurn && Jump is null;
}

/// <summary>
/// A leg of a route flown rather than walked: the leg that ends at
/// <c>Legs[LegIndex]</c> is a standing long jump from <c>Legs[LegIndex - 1]</c>,
/// charged to <paramref name="Power"/> and left at running or walking pace.
/// </summary>
public readonly record struct RuntimeRouteLeap(int LegIndex, float Power, bool Run);

/// <summary>A leap aimed again from where the body stands: charged to <paramref name="Power"/> and left at running or walking pace.</summary>
public readonly record struct RuntimeLeapAim(float Power, bool Run);

/// <summary>
/// Walks a body along the legs of a planned route with scripted moves. It turns
/// in place onto a leg that points well away from its heading, runs along the
/// leg while steering back onto it with small exact turns, and counts a leg's
/// end as reached once the body is near it or past it. Where it knows how fast
/// the body goes and turns, it cuts a corner without stopping: it starts turning
/// as far before the corner as the arc of a running turn needs and keeps running
/// around it, wherever the check it was given lets a body pass along that arc. A
/// corner it cannot run around, or one sharper than <see cref="SharpestCutDegrees"/>,
/// it runs up to, stops at and turns in place, never slowing to a walk. A leg the body stops making progress on ends the
/// drive as blocked, so the route can be planned again from where it stands.
/// A leap is taken as a standing long jump: the body runs up to the takeoff and, where it
/// knows how far its run carries it on as the run ends, lets go of forward that far short, so it
/// comes to rest on the takeoff; a takeoff too near to run up to it walks up to the same way. It
/// waits until the body stands still, faces the landing, charges the jump, and presses forward
/// at the leap's pace only as the jump releases, so it leaves the ground from the
/// takeoff at that pace. It lets go of forward once it has risen off the ground, and
/// once it comes down waits until it has slid to a stop. A leap that
/// comes down on its landing's level goes on along the route, or asks for a new
/// plan from where it came down when that is far from the landing; one that comes
/// down on another level ends the drive blocked.
/// </summary>
public sealed class RuntimeRouteDriver
{
    public const float ArrivalRadius = 0.5f;
    /// <summary>
    /// The shortest a forward move carries this client's body, however little is asked of it:
    /// measured live, asking for 0.10 m and for 0.25 m both moved 0.76 m, and backward the same.
    /// A sidestep moves what it is asked within about 0.05 m, and asking for less than
    /// <see cref="ShortestSidestepMeters"/> moves nothing at all, so a spot too near to walk to
    /// is stepped onto sideways.
    /// </summary>
    public const float ShortestWalkMeters = 0.76f;

    public const float ShortestSidestepMeters = 0.15f;

    /// <summary>How far a sidestep carries the body past what it was asked for: 0.15 m moved 0.19 m, 0.30 m moved 0.35 m and 0.50 m moved 0.59 m.</summary>
    public const float SidestepCarryMeters = 0.05f;

    public const float SteerToleranceDegrees = 3f;
    public const float TurnInPlaceDegrees = 30f;

    /// <summary>
    /// How far aside of its leg a body may drift by moving off while it still has some of a
    /// turn in place to make. Moving off with an angle still to turn takes the body round
    /// an arc of the pace's speed over its turn rate, and the drift is what that arc bows
    /// out by: 0.15 m is a walk's worth at <see cref="TurnInPlaceDegrees"/>, and a run's at
    /// about a third of that angle.
    /// </summary>
    public const float MoveOffDriftMeters = 0.15f;

    /// <summary>A corner turning more than this is never cut; the body turns in place at it.</summary>
    public const float SharpestCutDegrees = 120f;

    /// <summary>
    /// A cut starts this many seconds of travel before its arc does: half a frame, since
    /// the body is sampled once a frame and is on average that far past where it
    /// crossed the arc's start.
    /// </summary>
    public const float CutEarlySeconds = 1f / 120f;

    /// <summary>
    /// While cutting a corner, and after a cut until it faces within
    /// <see cref="TurnInPlaceDegrees"/> of the leg ahead, the body stops to turn in place
    /// only when it faces farther than this from where it is headed.
    /// </summary>
    public const float CutTurnInPlaceDegrees = 90f;

    /// <summary>
    /// The body steers for a point this far along its leg past the point on the leg
    /// nearest it, or farther when it is off the leg, <see cref="LookAheadPerMeterAside"/>
    /// for each meter aside, so it turns back onto the leg at no more than about 20°.
    /// </summary>
    public const float LookAheadMeters = 2.5f;

    public const float LookAheadPerMeterAside = 2.75f;

    /// <summary>The points of an arc a cut is checked along lie about this far apart.</summary>
    private const float ArcStepMeters = 0.25f;

    /// <summary>A run without an amount is renewed this often, well inside the client's thirty second limit.</summary>
    public const float RenewTravelSeconds = 20f;

    /// <summary>
    /// A leap starts once the body stands this near its takeoff, or has run or walked up to it
    /// and let go of forward, facing its landing to within <see cref="LeapFacingDegrees"/>. It has come to rest on its
    /// landing's level within <see cref="LandingHeight"/> of the landing's height,
    /// and where it was planned within <see cref="LandingRadius"/> of the landing,
    /// measured flat. A jump still on the ground this long after it releases ends the
    /// drive blocked.
    /// </summary>
    public const float TakeoffRadius = 0.35f;
    public const float LeapFacingDegrees = 2f;
    public const float LandingRadius = 1.5f;
    public const float LandingHeight = 1f;
    public const float TakeoffGraceSeconds = 1f;

    /// <summary>
    /// A leap lets go of forward once the body has risen this far above where it
    /// charged. A body takes its jump's speed from the moves pressed as it leaves the
    /// ground, which can be a frame or more after the charge releases.
    /// </summary>
    public const float LetGoRiseMeters = 0.05f;

    /// <summary>
    /// How much room past how far a run carries the body on a run-up to a takeoff needs. A
    /// takeoff nearer than that, to a body not already running toward it, is walked up to: a
    /// run begun so near is let go of at once and carries the body on past the takeoff, farther
    /// than a steady run does as it ends.
    /// </summary>
    public const float ShortestRunUpMeters = 1.5f;

    private enum LeapPhase
    {
        None,
        Facing,
        Charging,
        Flying,
    }

    private readonly Vector3[] _legs;
    private readonly Dictionary<int, RuntimeRouteLeap> _leaps = [];
    private bool _started;
    private long _travelBaseline;
    private long _turnBaseline;
    private long _strafeBaseline;
    private LeapPhase _phase;
    private float _takeoffHeight;

    /// <summary>The highest the body has been since the leap under way charged.</summary>
    private float _highest;
    private bool _touchedDown;
    private Vector2 _touchdown;
    private readonly bool _takeOverMoves;
    private readonly Func<IReadOnlyList<Vector3>, bool>? _canCutAlong;

    /// <summary>Where the body walks rather than runs, as beside a trap, so it holds its line; null for nowhere.</summary>
    private readonly Func<Vector2, bool>? _carefulAt;
    private int _cutPlannedFor;
    private Cut _cut;
    private Cut _cutting;
    private RuntimeMovePace? _settling;
    private Vector2? _lastPosition;

    /// <summary>
    /// How a corner is cut: at what pace, how far before the corner the turn starts,
    /// the point on the next leg where the cut ends, and the way that leg runs. A cut
    /// with no pace is a corner taken standing.
    /// </summary>
    private readonly record struct Cut(RuntimeMovePace? Pace, float Lead, Vector2 Exit, Vector2 Onward);

    /// <summary>
    /// A drive along <paramref name="legs"/>. With <paramref name="takeOverMoves"/>, moves
    /// already under way when it starts, such as those of a drive it replaces, count as
    /// its own, so a new plan goes on without stopping the character to start again.
    /// With <paramref name="canCutAlong"/>, which answers whether a body can pass along
    /// a path of points, the drive cuts the corners whose arcs it lets through.
    /// </summary>
    public RuntimeRouteDriver(
        IReadOnlyList<Vector3> legs,
        IReadOnlyList<RuntimeRouteLeap>? leaps = null,
        bool takeOverMoves = false,
        Func<IReadOnlyList<Vector3>, bool>? canCutAlong = null,
        Func<Vector2, bool>? carefulAt = null,
        Func<Vector3, Vector3, bool, bool, RuntimeLeapAim?>? aimLeapFrom = null,
        Func<Vector3, Vector3, bool, (RuntimeLeapAim Aim, Vector3 Spot)?>? aimOnward = null,
        Func<Vector3, Vector3, bool>? sameFloor = null)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (legs.Count < 2)
            throw new ArgumentException("A route needs a start and at least one leg end.", nameof(legs));
        _legs = [.. legs];
        foreach (RuntimeRouteLeap leap in leaps ?? [])
        {
            if (leap.LegIndex < 1 || leap.LegIndex >= _legs.Length)
                throw new ArgumentException("A leap must end at one of the route's leg ends.", nameof(leaps));
            _leaps[leap.LegIndex] = leap;
        }
        LegIndex = 1;
        _takeOverMoves = takeOverMoves;
        _canCutAlong = canCutAlong;
        _carefulAt = carefulAt;
        _aimLeapFrom = aimLeapFrom;
        _aimOnward = aimOnward;
        _sameFloor = sameFloor;
    }

    /// <summary>Whether two points stand on the same piece of floor, as a roof or a rock; null where only their heights can say.</summary>
    private readonly Func<Vector3, Vector3, bool>? _sameFloor;

    /// <summary>Where the body last stood still and was aimed from at the leap ahead, so it is asked once a stop.</summary>
    private Vector2? _aimedFromHere;

    /// <summary>How far above or below its takeoff a body with no floor to compare may stand and still leap from where it stands.</summary>
    public const float LeapFromHereHeight = 0.5f;

    /// <summary>
    /// Aims a leap again from where the body stands, given where it stands, the leap's landing and
    /// whether the route runs it, by the same checks the route's own leaps are kept by; null when no
    /// leap from there is kept. Null when leaps are flown only as planned.
    /// </summary>
    private readonly Func<Vector3, Vector3, bool, bool, RuntimeLeapAim?>? _aimLeapFrom;

    /// <summary>A leap onto the floor a planned landing lies on, taken from where the body stands.</summary>
    private readonly Func<Vector3, Vector3, bool, (RuntimeLeapAim Aim, Vector3 Spot)?>? _aimOnward;

    /// <summary>The spot a leap aimed onward flies at, in place of the landing the route planned.</summary>
    private Vector3? _flyAt;

    /// <summary>The takeoff a body with no leap kept from where it came to rest walks back to before it faces its leap again; null otherwise.</summary>
    private Vector2? _walkingBack;

    /// <summary>Whether the leap being faced has been aimed again from where the body came to rest, and the aim.</summary>
    private bool _aimAsked;

    private RuntimeLeapAim? _aim;

    /// <summary>
    /// How many times a body that came to rest off its takeoff, where no leap aimed from there is
    /// kept, goes back to the takeoff before it flies the planned leap from wherever it stands.
    /// </summary>
    public const int MostTakeoffAdjustments = 3;

    /// <summary>
    /// How near its takeoff a body where no leap aimed from there is kept stands before it flies
    /// the planned leap. A planned leap no aim from a step off it keeps holds only from its very
    /// takeoff: live, one flown from 0.29 m off came down beyond its rock and fell 160 m.
    /// </summary>
    public const float NoAimTakeoffRadius = 0.1f;

    /// <summary>
    /// How near the takeoff the route planned a leap from a body stands to be at that takeoff.
    /// A leap aimed again from inside it is kept the way the route's own leap is, without the
    /// check that a landing narrower than the body still holds the body when the leap leaves a
    /// little off, so it is the very slack the planner checks its own leaps through and no
    /// wider: from farther the check is what keeps a leap off a rock it would come down beside.
    /// </summary>
    public const float AtItsTakeoffRadius = NavLeapFinder.TakeoffSlack;

    public RuntimeRouteDriveState State { get; private set; } = RuntimeRouteDriveState.Driving;

    /// <summary>The index in <see cref="Legs"/> of the leg end the body is heading for.</summary>
    public int LegIndex { get; private set; }

    public IReadOnlyList<Vector3> Legs => _legs;

    /// <summary>Whether the body is facing, charging or flying a leap.</summary>
    public bool IsLeaping => _phase != LeapPhase.None;

    /// <summary>How the leap under way was come up to, for narration: the pace, how many moves were begun on the way, how far short of the takeoff the last was let go, and the turns taken to face the landing.</summary>
    public RuntimeLeapApproach Approach => _approach;

    private RuntimeLeapApproach _approach;

    /// <summary>How far from its planned landing, measured flat, the last leap came to rest.</summary>
    public float LandingError { get; private set; }

    /// <summary>How far, measured flat, the body slid on after the last leap came down before it came to rest.</summary>
    public float LandingSlide { get; private set; }

    /// <summary>
    /// Where the last leap came to rest against its planned landing, along the line from where it
    /// charged: how far past the landing, below zero for short of it, and how far to its side.
    /// </summary>
    public float LandingLong { get; private set; }

    public float LandingAside { get; private set; }

    /// <summary>How far, measured flat, the last leap flew from where it charged to where it came down, and how far its landing stood from there.</summary>
    public float LandingFlown { get; private set; }

    public float LandingPlanned { get; private set; }

    /// <summary>How far, in degrees, the body slid on after the last leap came down, off the line from where it charged to its landing, clockwise positive.</summary>
    public float LandingSlideDegrees { get; private set; }

    /// <summary>How far, in degrees, the last leap flew off the line from where it charged to its landing, clockwise positive.</summary>
    public float LandingFlightDegrees { get; private set; }

    /// <summary>How far above its planned landing, below zero for below it, the last leap came to rest.</summary>
    public float LandingRise { get; private set; }

    /// <summary>Whether the last leap faced was charged and flown, rather than left to walk back to its takeoff first.</summary>
    public bool LeapFlew { get; private set; }

    /// <summary>Whether the leap last charged came down: false for one that never left the ground.</summary>
    public bool LeapLanded => _touchedDown;

    /// <summary>Where the body was as the last leap came down.</summary>
    public Vector3 LandingTouchdown { get; private set; }

    /// <summary>How far above where it charged the last leap rose at its highest.</summary>
    public float LandingPeak { get; private set; }

    private void Measure(Vector2 position, float height, Vector3 landing)
    {
        Vector2 charged = _approach.ChargedAt;
        Vector2 toward = Flat(landing) - charged;
        float planned = toward.Length();
        Vector2 along = planned > 1e-3f ? toward / planned : Vector2.UnitY;
        Vector2 off = position - Flat(landing);
        LandingLong = Vector2.Dot(off, along);
        LandingAside = (off.X * along.Y) - (off.Y * along.X);
        LandingFlown = Vector2.Distance(_touchdown, charged);
        Vector2 slid = position - _touchdown;
        LandingSlideDegrees = slid.LengthSquared() > 1e-4f && planned > 1e-3f
            ? SignedDegrees(CompassHeading(slid) - CompassHeading(toward))
            : 0f;
        Vector2 flight = _touchdown - charged;
        LandingFlightDegrees = flight.LengthSquared() > 1e-6f && planned > 1e-3f
            ? SignedDegrees(CompassHeading(flight) - CompassHeading(toward))
            : 0f;
        LandingPlanned = planned;
        LandingRise = height - landing.Z;
        LandingPeak = _highest - _takeoffHeight;
    }

    /// <summary>How many corners the drive has come to and planned to run around, or turn in place at.</summary>
    public int CornersRunAround { get; private set; }

    public int CornersTurnedInPlace { get; private set; }

    public RuntimeRouteDriveStep Advance(in RuntimeRouteDriveSample sample)
    {
        if (State != RuntimeRouteDriveState.Driving)
            return default;
        if (!_started)
        {
            _started = true;
            _travelBaseline = Baseline(sample.Moves.Travel);
            _turnBaseline = Baseline(sample.Moves.Turn);
            _strafeBaseline = Baseline(sample.Moves.Strafe);
        }

        RuntimeMoveChannelSnapshot travel = sample.Moves.Travel;
        RuntimeMoveChannelSnapshot turn = sample.Moves.Turn;
        RuntimeMoveChannelSnapshot strafe = sample.Moves.Strafe;
        bool travelIsOurs = travel.Sequence > _travelBaseline;
        bool turnIsOurs = turn.Sequence > _turnBaseline;
        bool travelling = travelIsOurs && travel.State == RuntimeScriptedMoveState.Moving;
        bool turning = turnIsOurs && turn.State == RuntimeScriptedMoveState.Moving;
        bool forward = travelling && travel.Request.Direction == RuntimeMoveDirection.Forward;
        bool running = forward && travel.Request.Pace == RuntimeMovePace.Run;
        float stopMeters = !forward ? 0f : running ? sample.RunStopMeters : sample.WalkStopMeters;

        if (sample.InPortalSpace)
            return Finish(RuntimeRouteDriveState.Lost, travelling, turning);
        if ((travelIsOurs && travel.State == RuntimeScriptedMoveState.Interrupted)
            || (turnIsOurs && turn.State == RuntimeScriptedMoveState.Interrupted)
            || (strafe.Sequence > _strafeBaseline && strafe.State == RuntimeScriptedMoveState.Interrupted))
        {
            _phase = LeapPhase.None;
            State = RuntimeRouteDriveState.Interrupted;
            return default;
        }
        if (travelIsOurs && travel.State == RuntimeScriptedMoveState.Blocked)
            return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);

        var position = new Vector2(sample.Position.X, sample.Position.Y);
        float stepped = _lastPosition is { } before ? Vector2.Distance(position, before) : 0f;
        _lastPosition = position;
        if (_phase != LeapPhase.None)
            return AdvanceLeap(sample, position, travel, travelling, turning);

        if (_cutting.Pace is not null
            && (Vector2.Distance(position, _cutting.Exit) <= ArrivalRadius
                || Vector2.Dot(position - _cutting.Exit, _cutting.Onward) >= 0f))
        {
            _settling = _cutting.Pace;
            _cutting = default;
        }
        // A sidestep runs to its end before the spot it steps onto counts as reached: a turn
        // or a walk begun across one spoils it. It goes out on a strafe channel of its own, so
        // that is where it is watched, and not on the travel channel the legs are walked on.
        if (_sidestepTo is { } stepping
            && StillStepping(sample, position, stepping, travelling, turning, out RuntimeRouteDriveStep onward))
        {
            return onward;
        }
        if (_walkingBack is { } back)
        {
            if (Vector2.Distance(position, back) > MathF.Max(NoAimTakeoffRadius, stopMeters > 0f ? stopMeters + (stepped / 2f) : 0f))
            {
                // A gap a forward move overshoots is stepped across sideways, as the step onto a
                // takeoff is: asked to walk 0.25 m back the body moves 0.76 m, past the takeoff
                // it went back for and off the far side of a rock top.
                if (Sidestep(position, back, sample, travelling, turning) is { } stepBack)
                {
                    if (stepBack.Travel is not null)
                        _approach = _approach with { Sidesteps = _approach.Sidesteps + 1, BegunAt = position };
                    return stepBack;
                }
                float offBack = SignedDegrees(CompassHeading(back - position) - sample.HeadingDegrees);
                if (MathF.Abs(offBack) > TurnInPlaceDegrees)
                    return new RuntimeRouteDriveStep(Turn: turning ? null : TurnBy(offBack, RuntimeMovePace.Walk), StopTravel: travelling);
                bool walkAgain = !travelling
                    || travel.Request.Direction != RuntimeMoveDirection.Forward
                    || travel.Request.Pace != RuntimeMovePace.Walk;
                return new RuntimeRouteDriveStep(
                    Travel: walkAgain ? new RuntimeMoveRequest(RuntimeMoveDirection.Forward, RuntimeMovePace.Walk, 0f) : null,
                    Turn: !turning && MathF.Abs(offBack) > SteerToleranceDegrees ? TurnBy(offBack, RuntimeMovePace.Walk) : null);
            }
            _walkingBack = null;
        }
        LeapFromHere(sample, position, travelling);
        if (_cutting.Pace is null)
        {
            while (LegIndex < _legs.Length && !_leaps.ContainsKey(LegIndex))
            {
                if (Reached(position, LegIndex, stopMeters > 0f ? stopMeters + (stepped / 2f) : 0f)
                    || AimsFromHere(sample, LegIndex, travelling))
                {
                    LegIndex++;
                    continue;
                }
                Cut cut = CutFor(LegIndex, sample.Turning);
                if (cut.Pace is not null && AlongTo(position, LegIndex) <= cut.Lead)
                {
                    _cutting = cut;
                    _settling = null;
                    LegIndex++;
                }
                break;
            }
        }
        if (LegIndex >= _legs.Length)
            return Finish(RuntimeRouteDriveState.Arrived, travelling, turning);
        // A body still sliding near a takeoff may come to rest where a leap aimed from there is
        // kept, which a walk begun now would spoil.
        if (_aimLeapFrom is not null
            && _leaps.ContainsKey(LegIndex + 1)
            && !travelling
            && !sample.Still
            && Vector2.Distance(position, Flat(_legs[LegIndex])) <= ShortestRunUpMeters)
        {
            return default;
        }
        if (_leaps.ContainsKey(LegIndex))
        {
            _phase = LeapPhase.Facing;
            _aimAsked = false;
            LeapFlew = false;
            Vector2 takeoffAt = Flat(_legs[LegIndex - 1]);
            if (_approach.Leg != LegIndex - 1)
                _approach = new RuntimeLeapApproach(LegIndex - 1, RuntimeMovePace.Walk, 0, float.NaN, 0, 0f, float.NaN);
            _approach = _approach with
            {
                LetGoMeters = travelling ? Vector2.Distance(position, takeoffAt) : float.NaN,
                TakeoffAt = takeoffAt,
                LetGoAt = travelling ? position : default,
            };
            return AdvanceLeap(sample, position, travel, travelling, turning);
        }

        if (_leaps.ContainsKey(LegIndex + 1)
            && Sidestep(position, Flat(_legs[LegIndex]), sample, travelling, turning) is { } across)
        {
            if (_approach.Leg != LegIndex)
                _approach = new RuntimeLeapApproach(LegIndex, RuntimeMovePace.Walk, 0, float.NaN, 0, 0f, float.NaN);
            if (across.Travel is not null)
                _approach = _approach with { Sidesteps = _approach.Sidesteps + 1, BegunAt = position };
            return across;
        }

        Vector2 aim = _cutting.Pace is null ? AimAlong(position, LegIndex) : _cutting.Exit;
        float error = SignedDegrees(CompassHeading(aim - position) - sample.HeadingDegrees);
        if (_settling is not null && MathF.Abs(error) <= TurnInPlaceDegrees)
            _settling = null;
        bool lenient = _cutting.Pace is not null || _settling is not null;
        // A turn is made at the pace the body travels at. The client holds run or walk
        // for turning as for travelling, and a turn asked for at a run while the body
        // walks flips its hold to run as the walk ends, which sends the rest of that
        // walk out at a run: beside a trap, straight into it.
        RuntimeMovePace pace = PaceFor(position, sample.WalkStopMeters, sample.RunStopMeters, running);
        if (MathF.Abs(error) > (lenient ? CutTurnInPlaceDegrees : MoveOffDegrees(sample.Turning, pace)))
        {
            return new RuntimeRouteDriveStep(
                Turn: turning ? null : TurnBy(error, pace),
                StopTravel: travelling);
        }
        bool renew = !travelling
            || travel.Request.Direction != RuntimeMoveDirection.Forward
            || travel.Request.Pace != pace
            || travel.ElapsedSeconds >= RenewTravelSeconds;
        if (_leaps.ContainsKey(LegIndex + 1))
        {
            if (_approach.Leg != LegIndex)
                _approach = new RuntimeLeapApproach(LegIndex, pace, 0, float.NaN, 0, 0f, float.NaN);
            if (renew)
                _approach = _approach with { Pace = pace, MovesBegun = _approach.MovesBegun + 1, BegunAt = position };
        }
        return new RuntimeRouteDriveStep(
            Travel: renew ? new RuntimeMoveRequest(RuntimeMoveDirection.Forward, pace, 0f) : null,
            Turn: !turning && MathF.Abs(error) > SteerToleranceDegrees ? TurnBy(error, pace) : null);
    }

    private long Baseline(in RuntimeMoveChannelSnapshot channel) =>
        _takeOverMoves && channel.State == RuntimeScriptedMoveState.Moving
            ? channel.Sequence - 1
            : channel.Sequence;

    /// <summary>Ends the drive at once, stopping whatever it had begun.</summary>
    public RuntimeRouteDriveStep Cancel()
    {
        if (State != RuntimeRouteDriveState.Driving)
            return default;
        _phase = LeapPhase.None;
        _sidestepTo = null;
        State = RuntimeRouteDriveState.Interrupted;
        return new RuntimeRouteDriveStep(StopTravel: _started, StopTurn: _started);
    }

    /// <summary>
    /// Takes the leap whose leg the body is on: faces its landing from where the
    /// body stopped at the takeoff, charges the jump standing, leaves the ground pressing forward at its pace, lets go of forward once it has risen, waits for the body to come to rest after it lands,
    /// and once the body comes down, goes on along the route from a landing near
    /// the leg's end or ends the drive blocked from anywhere else.
    /// </summary>
    private RuntimeRouteDriveStep AdvanceLeap(
        in RuntimeRouteDriveSample sample,
        Vector2 position,
        RuntimeMoveChannelSnapshot travel,
        bool travelling,
        bool turning)
    {
        RuntimeRouteLeap leap = _leaps[LegIndex];
        Vector3 landing = _flyAt ?? _legs[LegIndex];
        switch (_phase)
        {
            case LeapPhase.Facing:
            {
                if (travelling)
                    return new RuntimeRouteDriveStep(StopTravel: true);
                if (turning || !sample.Still)
                    return default;
                if (!_aimAsked)
                {
                    _aimAsked = true;
                    _aim = _aimLeapFrom?.Invoke(
                        sample.Position,
                        landing,
                        leap.Run,
                        Vector2.Distance(position, Flat(_legs[LegIndex - 1])) <= AtItsTakeoffRadius);

                    // Nothing says the landing the route planned is the only one this spot can
                    // reach: where no leap at it is kept, a leap from here onto the same floor is
                    // taken instead, and the walk plans on from wherever it comes to rest.
                    if (_aim is null && _flyAt is null && _aimOnward?.Invoke(sample.Position, landing, leap.Run) is { } onward)
                    {
                        _aim = onward.Aim;
                        _flyAt = onward.Spot;
                        landing = onward.Spot;
                        _approach = _approach with { AimedOnward = true };
                    }
                    // No leap from where the body came to rest is kept, and it stands off its
                    // takeoff: it goes back to the takeoff rather than fly the planned leap from
                    // the wrong spot.
                    float offTakeoff = Vector2.Distance(position, Flat(_legs[LegIndex - 1]));
                    if (_aimLeapFrom is not null && _aim is null && offTakeoff > NoAimTakeoffRadius)
                    {
                        // Going back is worth it only for a gap some move this client makes can
                        // close: nothing it does moves the body less than a shortest sidestep,
                        // so for anything shorter the walk plans again instead of stepping back
                        // and forth over the takeoff until its tries run out.
                        if (_approach.Adjustments < MostTakeoffAdjustments && offTakeoff > ShortestSidestepMeters)
                        {
                            // Walked straight back to rather than by stepping back a leg: the takeoff can
                            // be where the leap before came down, whose own leg is that leap.
                            _approach = _approach with { Adjustments = _approach.Adjustments + 1 };
                            _phase = LeapPhase.None;
                            _walkingBack = Flat(_legs[LegIndex - 1]);
                            return default;
                        }

                        // The body kept no leap from where it stands and cannot get back to its
                        // takeoff: on a rock top narrower than the body, a walk back slides along
                        // the edge and never arrives. The leap the route planned holds only from
                        // that takeoff, so the walk plans again from here rather than fly it from
                        // the wrong spot, as one flown from 0.29 m off did before coming down
                        // beyond its rock and falling 160 m.
                        State = RuntimeRouteDriveState.Blocked;
                        return new RuntimeRouteDriveStep(StopTravel: travelling);
                    }
                }
                float error = SignedDegrees(CompassHeading(Flat(landing) - position) - sample.HeadingDegrees);
                if (MathF.Abs(error) > LeapFacingDegrees)
                {
                    _approach = _approach with
                    {
                        Turns = _approach.Turns + 1,
                        TurnedDegrees = _approach.TurnedDegrees + MathF.Abs(error),
                        TurnedAt = _approach.Turns == 0 ? position : _approach.TurnedAt,
                    };
                    return new RuntimeRouteDriveStep(Turn: TurnBy(error, RuntimeMovePace.Run));
                }
                RuntimeLeapAim? aimed = _aim;
                RuntimeLeapAim flown = aimed ?? new RuntimeLeapAim(leap.Power, leap.Run);
                _approach = _approach with
                {
                    TakeoffError = Vector2.Distance(position, Flat(_legs[LegIndex - 1])),
                    ChargedAt = position,
                    ChargedFrom = sample.Position,
                    FlownAt = _flyAt,
                    FacingError = -error,
                    PlannedPower = leap.Power,
                    PlannedRun = leap.Run,
                    Aimed = aimed,
                };
                _takeoffHeight = sample.Position.Z;
                _highest = sample.Position.Z;
                LeapFlew = true;
                _touchedDown = false;
                _phase = LeapPhase.Charging;
                return new RuntimeRouteDriveStep(
                    Jump: flown.Power,
                    JumpPace: flown.Run ? RuntimeMovePace.Run : RuntimeMovePace.Walk);
            }
            case LeapPhase.Charging:
                if (sample.Airborne)
                {
                    _phase = LeapPhase.Flying;
                    return default;
                }
                if (sample.Moves.JumpCharging)
                    return default;
                return !travelling || travel.ElapsedSeconds > TakeoffGraceSeconds
                    ? Finish(RuntimeRouteDriveState.Blocked, travelling, turning)
                    : default;
            default:
                _highest = MathF.Max(_highest, sample.Position.Z);
                if (sample.Airborne)
                {
                    return travelling && sample.Position.Z > _takeoffHeight + LetGoRiseMeters
                        ? new RuntimeRouteDriveStep(StopTravel: true)
                        : default;
                }
                if (!_touchedDown)
                {
                    _touchedDown = true;
                    _touchdown = position;
                    LandingTouchdown = sample.Position;
                }
                if (travelling)
                    return new RuntimeRouteDriveStep(StopTravel: true);
                if (!sample.Still)
                    return default;
                _phase = LeapPhase.None;
                LandingSlide = Vector2.Distance(position, _touchdown);
                Measure(position, sample.Position.Z, landing);
                if (MathF.Abs(sample.Position.Z - landing.Z) > LandingHeight)
                    return Finish(RuntimeRouteDriveState.Blocked, travelling, turning);
                LandingError = Vector2.Distance(position, Flat(landing));
                if (LandingError > LandingRadius)
                    return Finish(RuntimeRouteDriveState.LandedElsewhere, travelling, turning);
                // A leap aimed onward came down on the right floor but not on the route's own
                // landing, so the rest of the route is planned again from where the body is.
                if (_flyAt is not null)
                    return Finish(RuntimeRouteDriveState.LandedElsewhere, travelling, turning);
                LegIndex++;
                return default;
        }
    }

    private RuntimeRouteDriveStep Finish(RuntimeRouteDriveState state, bool travelling, bool turning)
    {
        _phase = LeapPhase.None;
        _flyAt = null;
        _cutting = default;
        _settling = null;
        _sidestepTo = null;
        State = state;
        return new RuntimeRouteDriveStep(StopTravel: travelling, StopTurn: turning);
    }

    /// <summary>
    /// Whether the body has reached a leg's end: near it, or past it. The takeoff
    /// of a leap counts only once the body stands within <see cref="TakeoffRadius"/> of it,
    /// never for having passed it, and a body running or walking up to it once it is as near as
    /// <paramref name="stopMeters"/>: how far it goes on as its move ends, and half the way it
    /// moved since the frame before, for where between frames it passes that point.
    /// </summary>
    /// <summary>
    /// Whether a body standing still within <see cref="ShortestRunUpMeters"/> of a leg's takeoff
    /// is at its takeoff already, because a leap aimed from exactly where it stands is kept by the
    /// same checks as the route's own. A walk up to the planned spot from so near is let go of
    /// before it settles and slides on past it by as much as it stood away.
    /// </summary>
    /// <summary>
    /// A body standing still anywhere on the floor it walks across to the next leap's takeoff first
    /// tries that leap from where it stands, turning to face it but walking nowhere: a leap across
    /// a roof or a rock top kept from here by the same checks as the route's own is taken from here,
    /// rather than after a walk to the spot the route happened to plan it from. Asked once a stop.
    /// </summary>
    private void LeapFromHere(in RuntimeRouteDriveSample sample, Vector2 position, bool travelling)
    {
        if (_aimLeapFrom is null || travelling || !sample.Still || _cutting.Pace is not null)
            return;
        int leap = LegIndex;
        while (leap < _legs.Length && !_leaps.ContainsKey(leap))
            leap++;
        if (leap >= _legs.Length || LegIndex >= leap)
            return;
        if (_aimedFromHere is { } asked && Vector2.Distance(asked, position) < StillMeters)
            return;
        _aimedFromHere = position;
        Vector3 takeoff = _legs[leap - 1];
        bool onItsFloor = _sameFloor?.Invoke(sample.Position, takeoff)
            ?? MathF.Abs(sample.Position.Z - takeoff.Z) <= LeapFromHereHeight;
        if (!onItsFloor
            || _aimLeapFrom(
                sample.Position,
                _legs[leap],
                _leaps[leap].Run,
                Vector2.Distance(position, Flat(_legs[leap - 1])) <= AtItsTakeoffRadius) is null)
            return;
        float skipped = 0f;
        Vector2 from = position;
        for (int index = LegIndex; index < leap; index++)
        {
            skipped += Vector2.Distance(from, Flat(_legs[index]));
            from = Flat(_legs[index]);
        }
        LegIndex = leap;
        _approach = new RuntimeLeapApproach(leap - 1, RuntimeMovePace.Walk, 0, float.NaN, 0, 0f, float.NaN)
        {
            WalkSkipped = skipped,
        };
    }

    /// <summary>How far a body may drift standing still and still be where it last stood.</summary>
    private const float StillMeters = 0.05f;

    private bool AimsFromHere(in RuntimeRouteDriveSample sample, int index, bool travelling)
    {
        if (_aimLeapFrom is null
            || travelling
            || !sample.Still
            || !_leaps.TryGetValue(index + 1, out RuntimeRouteLeap leap))
        {
            return false;
        }
        var position = new Vector2(sample.Position.X, sample.Position.Y);
        // On the takeoff's own floor, as a leap taken from anywhere on it is: another floor
        // a step away in plan can lie below or above this one, and a leap kept from there
        // would carry the body off it in place of the walk to the takeoff.
        bool onItsFloor = _sameFloor?.Invoke(sample.Position, _legs[index])
            ?? MathF.Abs(sample.Position.Z - _legs[index].Z) <= LeapFromHereHeight;
        return onItsFloor
            && Vector2.Distance(position, Flat(_legs[index])) <= ShortestRunUpMeters
            && _aimLeapFrom(
                sample.Position,
                _legs[index + 1],
                leap.Run,
                Vector2.Distance(position, Flat(_legs[index])) <= AtItsTakeoffRadius) is not null;
    }

    private bool Reached(Vector2 position, int index, float stopMeters)
    {
        Vector2 end = Flat(_legs[index]);
        // A takeoff is a spot, not a line: a body past the line through it but off to its side,
        // as one landing short of a leap's landing is, would charge from the wrong place.
        if (_leaps.ContainsKey(index + 1))
        {
            return Vector2.Distance(position, end) <= MathF.Max(TakeoffRadius, stopMeters);
        }
        Vector2 along = end - Flat(_legs[index - 1]);
        bool past = along.LengthSquared() > 1e-6f && Vector2.Dot(position - end, along) >= 0f;
        return past || Vector2.Distance(position, end) <= ArrivalRadius;
    }

    /// <summary>Whether the route turns more than <see cref="TurnInPlaceDegrees"/> at a leg's end between two walked legs.</summary>
    private bool TurnsSharplyAt(int corner)
    {
        if (corner + 1 >= _legs.Length || _leaps.ContainsKey(corner) || _leaps.ContainsKey(corner + 1))
            return false;
        Vector2 current = Flat(_legs[corner]) - Flat(_legs[corner - 1]);
        Vector2 next = Flat(_legs[corner + 1]) - Flat(_legs[corner]);
        return MathF.Abs(SignedDegrees(CompassHeading(next) - CompassHeading(current))) > TurnInPlaceDegrees;
    }

    /// <summary>
    /// The pace to go at: a cut's own while cutting and until the body faces the leg ahead; a walk
    /// up to a takeoff the body is not already running toward and nearer than a run carries it on
    /// with <see cref="ShortestRunUpMeters"/> to spare, where how far a walk carries the body on is
    /// known; and otherwise a run.
    /// </summary>
    /// <summary>
    /// A step onto a spot the body stands too near to walk to: it turns until the spot lies
    /// square to its side and steps across, which this client does to the centimetre where a
    /// forward move would carry it three quarters of a metre whatever it was asked for. Null
    /// when the spot is farther than a walk's own shortest carry, or so near that a sidestep
    /// would not move at all.
    /// </summary>
    /// <summary>The spot a sidestep under way steps onto, which it runs to before that spot counts as reached; null while none is.</summary>
    private Vector2? _sidestepTo;

    /// <summary>What the strafe channel stood at as a sidestep was asked for, so the move that follows it is known to be the one asked for.</summary>
    private long _sidestepAsked = -1;

    /// <summary>
    /// Whether a sidestep is still to run, and what to send while it is. A sidestep goes out on
    /// the strafe channel, not the travel channel the legs are walked on, so it is watched
    /// there: while the client still holds ours the drive sends nothing, and one the client
    /// never took up is asked for again, the way a walk is renewed, rather than left for the
    /// leg's own turn and travel to move the body off the spot.
    /// </summary>
    private bool StillStepping(
        in RuntimeRouteDriveSample sample,
        Vector2 position,
        Vector2 spot,
        bool travelling,
        bool turning,
        out RuntimeRouteDriveStep step)
    {
        step = default;
        RuntimeMoveChannelSnapshot strafe = sample.Moves.Strafe;
        if (strafe.Sequence > _sidestepAsked)
        {
            if (strafe.State == RuntimeScriptedMoveState.Moving)
                return true;
            _sidestepTo = null;
            return false;
        }
        if (Sidestep(position, spot, sample, travelling, turning) is { } again)
        {
            step = again;
            return true;
        }
        _sidestepTo = null;
        return false;
    }

    private RuntimeRouteDriveStep? Sidestep(
        Vector2 position,
        Vector2 spot,
        in RuntimeRouteDriveSample sample,
        bool travelling,
        bool turning)
    {
        float gap = Vector2.Distance(position, spot);
        if (gap > ShortestWalkMeters || gap < ShortestSidestepMeters || !sample.Still)
            return null;
        float bearing = CompassHeading(spot - position);
        float toTheRight = SignedDegrees(bearing - 90f - sample.HeadingDegrees);
        float toTheLeft = SignedDegrees(bearing + 90f - sample.HeadingDegrees);
        bool right = MathF.Abs(toTheRight) <= MathF.Abs(toTheLeft);
        float error = right ? toTheRight : toTheLeft;
        if (MathF.Abs(error) > SteerToleranceDegrees)
            return new RuntimeRouteDriveStep(Turn: turning ? null : TurnBy(error, RuntimeMovePace.Walk), StopTravel: travelling);
        if (travelling || turning)
            return default(RuntimeRouteDriveStep);
        _sidestepTo = spot;
        _sidestepAsked = sample.Moves.Strafe.Sequence;
        return new RuntimeRouteDriveStep(
            Travel: new RuntimeMoveRequest(
                right ? RuntimeMoveDirection.StrafeRight : RuntimeMoveDirection.StrafeLeft,
                RuntimeMovePace.Walk,
                MathF.Max(ShortestSidestepMeters, gap - SidestepCarryMeters)));
    }

    private RuntimeMovePace PaceFor(Vector2 position, float walkStopMeters, float runStopMeters, bool running) =>
        _carefulAt?.Invoke(position) == true
            ? RuntimeMovePace.Walk
            : (_cutting.Pace ?? _settling)
        ?? (walkStopMeters > 0f
            && !running
            && _leaps.ContainsKey(LegIndex + 1)
            && Vector2.Distance(position, Flat(_legs[LegIndex])) < MathF.Max(0f, runStopMeters) + ShortestRunUpMeters
                ? RuntimeMovePace.Walk
                : RuntimeMovePace.Run);

    /// <summary>
    /// The point the body steers for on the leg to a leg end: past the point on the leg
    /// nearest the body by <see cref="LookAheadMeters"/>, or more when the body is off the
    /// leg, and never past the leg's end.
    /// </summary>
    private Vector2 AimAlong(Vector2 position, int index)
    {
        Vector2 start = Flat(_legs[index - 1]);
        Vector2 end = Flat(_legs[index]);
        Vector2 along = end - start;
        float length = along.Length();
        if (length < 1e-3f)
            return end;
        Vector2 direction = along / length;
        Vector2 offset = position - start;
        float aside = MathF.Abs((offset.X * direction.Y) - (offset.Y * direction.X));
        float reach = MathF.Max(Vector2.Dot(offset, direction), 0f) + MathF.Max(LookAheadMeters, aside * LookAheadPerMeterAside);
        return reach >= length ? end : start + (direction * reach);
    }

    /// <summary>How far the body is from a leg's end, measured along the leg.</summary>
    private float AlongTo(Vector2 position, int index)
    {
        Vector2 end = Flat(_legs[index]);
        Vector2 along = end - Flat(_legs[index - 1]);
        float length = along.Length();
        return length > 1e-3f ? Vector2.Dot(end - position, along / length) : Vector2.Distance(position, end);
    }

    /// <summary>How the corner at a leg's end is taken, worked out once, the first time the body heads for it.</summary>
    private Cut CutFor(int corner, RuntimeRouteTurning? turning)
    {
        if (_cutPlannedFor != corner)
        {
            _cutPlannedFor = corner;
            _cut = PlanCut(corner, turning);
            if (_cut.Pace == RuntimeMovePace.Run)
                CornersRunAround++;
            else if (TurnsSharplyAt(corner))
                CornersTurnedInPlace++;
        }
        return _cut;
    }

    /// <summary>
    /// A cut around the corner at a leg's end, at a run, where the arc of a running turn
    /// passes; none where it does not, so the body stops and turns there. An arc must start and
    /// end within the legs beside the corner, no nearer the middle of a leg the body
    /// cuts into from another corner. Leaps' takeoffs and landings are not cut.
    /// </summary>
    private Cut PlanCut(int corner, RuntimeRouteTurning? turning)
    {
        if (_canCutAlong is null
            || turning is not { } speeds
            || corner + 1 >= _legs.Length
            || _leaps.ContainsKey(corner)
            || _leaps.ContainsKey(corner + 1))
        {
            return default;
        }
        Vector2 before = Flat(_legs[corner - 1]);
        Vector2 at = Flat(_legs[corner]);
        Vector2 after = Flat(_legs[corner + 1]);
        float inbound = Vector2.Distance(before, at);
        float outbound = Vector2.Distance(at, after);
        if (inbound < 1e-3f || outbound < 1e-3f)
            return default;
        float turn = SignedDegrees(CompassHeading(after - at) - CompassHeading(at - before));
        if (MathF.Abs(turn) <= SteerToleranceDegrees || MathF.Abs(turn) > SharpestCutDegrees)
            return default;
        float room = MathF.Min(corner == 1 ? inbound : inbound * 0.5f, outbound * 0.5f);
        return TryCut(corner, turn, room, RuntimeMovePace.Run, speeds.RunSpeed, speeds.RunTurnDegreesPerSecond);
    }

    private Cut TryCut(int corner, float turn, float room, RuntimeMovePace pace, float speed, float degreesPerSecond)
    {
        if (speed <= 0f || degreesPerSecond <= 0f)
            return default;
        float radius = speed / (degreesPerSecond * (MathF.PI / 180f));
        float tangent = radius * MathF.Tan(MathF.Abs(turn) * 0.5f * (MathF.PI / 180f));
        if (tangent > room)
            return default;
        Vector3[] arc = Arc(corner, turn, radius, tangent);
        if (!_canCutAlong!(arc))
            return default;
        Vector2 onward = Vector2.Normalize(Flat(_legs[corner + 1]) - Flat(_legs[corner]));
        return new Cut(pace, tangent + (speed * CutEarlySeconds), Flat(arc[^1]), onward);
    }

    /// <summary>
    /// The arc a body turning at a steady rate, around a circle of <paramref name="radius"/>,
    /// follows around a corner turning <paramref name="turn"/> degrees, right for more than
    /// zero: from <paramref name="tangent"/> before the corner on the leg in to that far
    /// along the leg out, as points about <see cref="ArcStepMeters"/> apart at the legs' heights.
    /// </summary>
    private Vector3[] Arc(int corner, float turn, float radius, float tangent)
    {
        Vector3 before = _legs[corner - 1];
        Vector3 at = _legs[corner];
        Vector3 after = _legs[corner + 1];
        Vector3 start = Vector3.Lerp(at, before, tangent / Vector2.Distance(Flat(before), Flat(at)));
        Vector3 end = Vector3.Lerp(at, after, tangent / Vector2.Distance(Flat(at), Flat(after)));
        Vector2 inbound = Vector2.Normalize(Flat(at) - Flat(before));
        Vector2 inside = turn > 0f ? new Vector2(inbound.Y, -inbound.X) : new Vector2(-inbound.Y, inbound.X);
        Vector2 centre = Flat(start) + (inside * radius);
        Vector2 spoke = Flat(start) - centre;
        float sweep = -turn * (MathF.PI / 180f);
        int steps = Math.Max(1, (int)MathF.Ceiling(radius * MathF.Abs(sweep) / ArcStepMeters));
        var points = new Vector3[steps + 1];
        for (int step = 0; step <= steps; step++)
        {
            float share = (float)step / steps;
            float cos = MathF.Cos(sweep * share);
            float sin = MathF.Sin(sweep * share);
            Vector2 point = centre + new Vector2((spoke.X * cos) - (spoke.Y * sin), (spoke.X * sin) + (spoke.Y * cos));
            points[step] = new Vector3(point, start.Z + ((end.Z - start.Z) * share));
        }
        return points;
    }

    /// <summary>
    /// The most of a turn in place a body may have left when it moves off at a pace, so that
    /// the arc it then goes round bows out from its leg by no more than
    /// <see cref="MoveOffDriftMeters"/>: never more than <see cref="TurnInPlaceDegrees"/>, and
    /// that much where the body's speed and turn rate at the pace are not known.
    /// </summary>
    internal static float MoveOffDegrees(RuntimeRouteTurning? turning, RuntimeMovePace pace)
    {
        if (turning is not { } speeds)
            return TurnInPlaceDegrees;
        (float speed, float degreesPerSecond) = pace == RuntimeMovePace.Run
            ? (speeds.RunSpeed, speeds.RunTurnDegreesPerSecond)
            : (speeds.WalkSpeed, speeds.WalkTurnDegreesPerSecond);
        if (!(speed > 0f) || !(degreesPerSecond > 0f))
            return TurnInPlaceDegrees;
        float radius = speed / (degreesPerSecond * (MathF.PI / 180f));
        if (radius <= MoveOffDriftMeters)
            return TurnInPlaceDegrees;
        float degrees = MathF.Acos(1f - (MoveOffDriftMeters / radius)) * (180f / MathF.PI);
        return Math.Clamp(degrees, SteerToleranceDegrees, TurnInPlaceDegrees);
    }

    /// <summary>A turn through an angle at a pace: the body's hold for turning is the same as for travelling.</summary>
    private static RuntimeMoveRequest TurnBy(float degrees, RuntimeMovePace pace) =>
        new(
            degrees > 0f ? RuntimeMoveDirection.TurnRight : RuntimeMoveDirection.TurnLeft,
            pace,
            MathF.Abs(degrees));

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    private static float CompassHeading(Vector2 direction) =>
        NormalizedDegrees(MathF.Atan2(direction.X, direction.Y) * (180f / MathF.PI));

    private static float SignedDegrees(float degrees) => (((degrees % 360f) + 540f) % 360f) - 180f;

    private static float NormalizedDegrees(float degrees) => ((degrees % 360f) + 360f) % 360f;
}
