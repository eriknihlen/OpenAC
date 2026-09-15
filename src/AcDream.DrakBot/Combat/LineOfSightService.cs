using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Combat;

public enum LineOfSightState
{
    /// <summary>The host cannot answer (no projectile collision, or the target is not a body); shoot anyway.</summary>
    Unavailable = 0,
    Clear,
    Blocked,
    /// <summary>Blocked too many times in a row; skipped until the blacklist expires.</summary>
    Blacklisted,
}

/// <summary>What the last sweep to one target said, and how it was modelled.</summary>
public readonly record struct LineOfSightVerdict(
    LineOfSightState State,
    PluginProjectilePathKind Kind,
    PluginAttackHeight Height,
    PluginProjectilePathStatus Status,
    uint BlockingObjectId,
    double EvaluatedAt,
    bool FromCache)
{
    public bool IsClear => State == LineOfSightState.Clear;

    /// <summary>Whether an attack should go ahead: a clear path, or no answer to go on.</summary>
    public bool IsUsable => State is LineOfSightState.Clear or LineOfSightState.Unavailable;

    /// <summary>Blocked by the world itself - a wall, a pillar, a floor - rather than by a creature standing in the way.</summary>
    public bool ByEnvironment { get; init; }
}

/// <summary>Whether a hostile can be seen from where the character stands.</summary>
public enum Sight
{
    /// <summary>A straight line reaches it at some height.</summary>
    Seen,
    /// <summary>Something that is not the world - another creature, a door - stops every line short of it; what lies past that is not known.</summary>
    Obscured,
    /// <summary>The world is between: a wall, a floor, a pillar at every height. Not a hostile to fight or walk at.</summary>
    Hidden,
    /// <summary>No probe to ask.</summary>
    Unknown,
}

/// <summary>What the last walk probe toward one target said.</summary>
public readonly record struct WalkVerdict(
    LineOfSightState State,
    float HeadingDegrees,
    float ClearDistanceMeters,
    uint BlockingObjectId,
    double EvaluatedAt,
    bool FromCache)
{
    public bool IsClear => State == LineOfSightState.Clear;

    /// <summary>Blocked by the world itself - a wall, a floor, a door frame - rather than by a creature or an object standing in the way.</summary>
    public bool ByEnvironment { get; init; }

    public bool IsUsable => State is LineOfSightState.Clear or LineOfSightState.Unavailable;
}

/// <summary>
/// The bot's obstacle sense, on top of the host's collision sweeps. For
/// ranged combat it picks the trajectory kind from the combat style and
/// asks whether a shot can reach; for walking it asks whether the
/// character's body can get there, steering through a fan of headings when
/// the direct one is blocked. Verdicts are remembered for a short time so a
/// target is not swept every tick, and a blacklist of targets that stayed
/// blocked - by either sense - stops the bot staring at them. Everything
/// here is policy; the geometry is the client's.
/// </summary>
public sealed class LineOfSightService(
    IProjectileAutomation projectiles,
    IMovementProbeAutomation movement,
    IBotClock clock,
    Func<LineOfSightSettings> settings)
{
    private static readonly PluginAttackHeight[] Heights =
    [
        PluginAttackHeight.Medium, PluginAttackHeight.High, PluginAttackHeight.Low,
    ];

    /// <summary>Steering offsets tried when the direct heading is blocked, in order.</summary>
    public static readonly float[] SteerOffsetsDegrees = [30f, -30f, 60f, -60f, 90f, -90f];

    private const float HeadingBucketDegrees = 5f;

    private readonly Dictionary<(uint Target, PluginProjectilePathKind Kind, PluginAttackHeight Height), Entry> _cache = [];
    private readonly Dictionary<(uint Target, int HeadingBucket), WalkEntry> _walkCache = [];
    private readonly Dictionary<uint, Strikes> _strikes = [];
    private readonly Dictionary<uint, LineOfSightVerdict> _last = [];
    private readonly Dictionary<uint, WalkVerdict> _lastWalk = [];
    private readonly HashSet<uint> _strikePending = [];
    private readonly HashSet<uint> _walkStrikePending = [];

    private sealed class Entry
    {
        public LineOfSightVerdict Verdict;
        public IReadOnlyList<PluginProjectileDebugSample> Samples = [];
    }

    private sealed class WalkEntry
    {
        public WalkVerdict Verdict;
        public float DistanceMeters;
        public IReadOnlyList<PluginProjectileDebugSample> Samples = [];
    }

    private struct Strikes
    {
        /// <summary>Strikes from blocked shots and from blocked walks; either sense forgives only its own.</summary>
        public int Shots;
        public int Walks;
        /// <summary>Walks that ran out of time; an open heading does not forgive these, only reaching the target would.</summary>
        public int Unreachable;
        public double BlacklistedUntil;
        public readonly int Count => Shots + Walks + Unreachable;
    }

    /// <summary>Whether the style shoots something the sweep can model.</summary>
    public static bool AppliesTo(CombatStyle style) => style != CombatStyle.Melee;

    public static PluginProjectilePathKind KindFor(
        CombatStyle style,
        LineOfSightSettings settings,
        bool outdoors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (style == CombatStyle.Missile)
            return PluginProjectilePathKind.Missile;
        bool arc = settings.WarSpellPath == WarSpellPath.Arc
            && (outdoors || !settings.StraightPathIndoors);
        return arc ? PluginProjectilePathKind.Arc : PluginProjectilePathKind.Straight;
    }

    public bool IsEnabled => settings().Enabled;

    /// <summary>Whether walks are checked at all: the option is on and the host can sweep a body.</summary>
    public bool ChecksWalking
    {
        get
        {
            LineOfSightSettings options = settings();
            return options.Enabled && options.CheckWalkPath && movement.IsAvailable;
        }
    }

    /// <summary>Sweep one path, or replay the recent verdict for it.</summary>
    public LineOfSightVerdict Evaluate(
        uint targetId,
        CombatStyle style,
        PluginAttackHeight height,
        bool outdoors)
    {
        LineOfSightSettings options = settings();
        double now = clock.Now;
        PluginProjectilePathKind kind = KindFor(style, options, outdoors);
        if (targetId == 0u || !options.Enabled || !AppliesTo(style))
            return Remember(targetId, Unavailable(kind, height, now));
        if (IsBlacklisted(targetId))
        {
            return Remember(targetId, new LineOfSightVerdict(
                LineOfSightState.Blacklisted, kind, height,
                PluginProjectilePathStatus.Blocked, 0u, now, FromCache: true));
        }

        (uint, PluginProjectilePathKind, PluginAttackHeight) key = (targetId, kind, height);
        if (_cache.TryGetValue(key, out Entry? entry)
            && now - entry.Verdict.EvaluatedAt <= options.CacheSeconds)
        {
            ShowSamples(options, entry);
            return Remember(targetId, entry.Verdict with { FromCache = true });
        }

        if (!projectiles.IsAvailable)
            return Remember(targetId, Unavailable(kind, height, now));

        LineOfSightVerdict verdict = Probe(targetId, kind, height, options, now);
        if (verdict.State == LineOfSightState.Blocked)
            _strikePending.Add(targetId);
        return Remember(targetId, verdict);
    }

    /// <summary>One sweep, cached; the caller decides whether a blocked answer is a strike.</summary>
    private LineOfSightVerdict Probe(uint targetId, PluginProjectilePathKind kind, PluginAttackHeight height, LineOfSightSettings options, double now)
    {
        Prune(now, options.CacheSeconds);
        PluginProjectilePathResult result = projectiles.EvaluatePath(
            new PluginProjectilePathRequest(targetId, kind, height)
            {
                ProjectileRadius = options.ProjectileRadius,
                StepDistance = options.StepDistanceMeters,
                MaximumCollisionChecks = options.MaximumCollisionChecks,
                LaunchSpeed = kind switch
                {
                    PluginProjectilePathKind.Arc => options.ArcLaunchSpeed,
                    PluginProjectilePathKind.Missile => options.MissileLaunchSpeed,
                    _ => 0f,
                },
                CaptureDiagnostics = options.ShowDebugSamples,
            });
        LineOfSightState state = result.Status switch
        {
            PluginProjectilePathStatus.Clear => LineOfSightState.Clear,
            PluginProjectilePathStatus.Blocked => LineOfSightState.Blocked,
            // A spent budget or an error says nothing about the path.
            _ => LineOfSightState.Unavailable,
        };
        var verdict = new LineOfSightVerdict(
            state, kind, height, result.Status, result.BlockingObjectId, now, FromCache: false)
        {
            ByEnvironment = state == LineOfSightState.Blocked
                && (result.BlockingObjectId == 0u || string.Equals(result.Notice, "environment", StringComparison.OrdinalIgnoreCase)),
        };
        var entry = new Entry { Verdict = verdict, Samples = result.DebugSamples };
        _cache[(targetId, kind, height)] = entry;
        ShowSamples(options, entry);
        return verdict;
    }

    /// <summary>
    /// Whether the character can see a hostile: a straight, flat sweep to
    /// it at the middle height, then the high and the low, cached like a
    /// shot. Hidden when the world blocks every one - the monster on the
    /// floor above, behind the wall, round the corner - and such a monster
    /// is simply not a hostile to fight or walk at, with no strike and no
    /// blacklist to wait out: it is looked at again each cache period and
    /// fought the moment it comes into view. A creature in the way is not
    /// a wall, but it is not a sighting either: the sweep stops at it and
    /// says nothing about what is past it - a swarm's last straggler
    /// round the corner looked seen through the bodies in front, and the
    /// character ran round the corner after it. Obscured, then, and the
    /// creature in front is the one to fight. Unknown without a probe, or
    /// with the line-of-sight check turned off.
    /// </summary>
    public Sight See(uint targetId)
    {
        LineOfSightSettings options = settings();
        if (targetId == 0u || !options.Enabled || !projectiles.IsAvailable)
            return Sight.Unknown;
        double now = clock.Now;
        bool obscured = false;
        foreach (PluginAttackHeight height in Heights)
        {
            LineOfSightVerdict verdict = Cached(targetId, PluginProjectilePathKind.Straight, height, options, now)
                ?? Probe(targetId, PluginProjectilePathKind.Straight, height, options, now);
            if (verdict.State == LineOfSightState.Clear)
                return Sight.Seen;
            if (verdict.State == LineOfSightState.Blocked && verdict.BlockingObjectId == targetId)
                return Sight.Seen;
            if (verdict.State == LineOfSightState.Unavailable)
                return Sight.Unknown;
            if (verdict.State == LineOfSightState.Blocked && !verdict.ByEnvironment)
            {
                // A creature in the way is a strike for the ranged styles,
                // as their own sweep would have made it, so a target kept
                // covered is blacklisted in due course; the world in the
                // way is not.
                if (!verdict.FromCache)
                    _strikePending.Add(targetId);
                obscured = true;
            }
        }
        return obscured ? Sight.Obscured : Sight.Hidden;
    }

    private LineOfSightVerdict? Cached(uint targetId, PluginProjectilePathKind kind, PluginAttackHeight height, LineOfSightSettings options, double now)
    {
        if (_cache.TryGetValue((targetId, kind, height), out Entry? entry) && now - entry.Verdict.EvaluatedAt <= options.CacheSeconds)
        {
            ShowSamples(options, entry);
            return entry.Verdict with { FromCache = true };
        }
        return null;
    }

    /// <summary>
    /// The preferred aim height first, then the other two; the first clear
    /// verdict wins, otherwise the preferred one's. A clear answer at any
    /// height resets the target's strikes.
    /// </summary>
    public LineOfSightVerdict EvaluateAnyHeight(
        uint targetId,
        CombatStyle style,
        PluginAttackHeight preferred,
        bool outdoors)
    {
        LineOfSightVerdict first = Evaluate(targetId, style, preferred, outdoors);
        if (first.State != LineOfSightState.Blocked)
        {
            if (first.IsClear)
                ReportClear(targetId);
            return first;
        }
        foreach (PluginAttackHeight height in Heights)
        {
            if (height == preferred)
                continue;
            LineOfSightVerdict verdict = Evaluate(targetId, style, height, outdoors);
            if (verdict.IsClear)
            {
                ReportClear(targetId);
                return Remember(targetId, verdict);
            }
        }
        return Remember(targetId, first);
    }

    /// <summary>
    /// Can the character walk <paramref name="distanceMeters"/> along
    /// <paramref name="headingDegrees"/>? Cached per target and heading
    /// (to five degrees) for the cache period, and only as long as the
    /// cached probe went at least as far.
    /// </summary>
    public WalkVerdict EvaluateWalk(uint targetId, float headingDegrees, float distanceMeters)
    {
        LineOfSightSettings options = settings();
        double now = clock.Now;
        float heading = NormalizeHeading(headingDegrees);
        if (!options.Enabled || !options.CheckWalkPath || !float.IsFinite(distanceMeters) || distanceMeters <= 0f)
            return RememberWalk(targetId, new WalkVerdict(LineOfSightState.Unavailable, heading, 0f, 0u, now, false));
        if (targetId != 0u && IsBlacklisted(targetId))
            return RememberWalk(targetId, new WalkVerdict(LineOfSightState.Blacklisted, heading, 0f, 0u, now, true));

        (uint, int) key = (targetId, (int)MathF.Round(heading / HeadingBucketDegrees));
        if (_walkCache.TryGetValue(key, out WalkEntry? entry)
            && now - entry.Verdict.EvaluatedAt <= options.CacheSeconds
            && (entry.DistanceMeters >= distanceMeters - 0.01f || !entry.Verdict.IsClear))
        {
            ShowSamples(options, entry.Samples);
            return RememberWalk(targetId, entry.Verdict with { FromCache = true });
        }
        if (!movement.IsAvailable)
            return RememberWalk(targetId, new WalkVerdict(LineOfSightState.Unavailable, heading, 0f, 0u, now, false));

        PluginWalkProbeResult result = movement.ProbeWalk(new PluginWalkProbeRequest(heading, distanceMeters)
        {
            StepDistance = Math.Min(options.StepDistanceMeters, 1f),
            MaximumCollisionChecks = options.MaximumCollisionChecks,
            TargetObjectId = targetId,
            CaptureDiagnostics = options.ShowDebugSamples,
        });
        LineOfSightState state = result.Status switch
        {
            PluginWalkProbeStatus.Clear => LineOfSightState.Clear,
            PluginWalkProbeStatus.Blocked => LineOfSightState.Blocked,
            _ => LineOfSightState.Unavailable,
        };
        var verdict = new WalkVerdict(state, heading, result.ClearDistanceMeters, result.BlockingObjectId, now, false)
        {
            ByEnvironment = state == LineOfSightState.Blocked
                && (result.BlockingObjectId == 0u || string.Equals(result.Notice, "environment", StringComparison.OrdinalIgnoreCase)),
        };
        entry = new WalkEntry { Verdict = verdict, DistanceMeters = distanceMeters, Samples = result.DebugSamples };
        _walkCache[key] = entry;
        ShowSamples(options, entry.Samples);
        return RememberWalk(targetId, verdict);
    }

    /// <summary>
    /// The heading to walk toward a target: the direct one if the body can
    /// cover the distance, else the first open heading of the steering fan
    /// out to the lookahead. False when nothing is open; a fresh all-blocked
    /// answer arms a strike against the target, like a blocked shot.
    /// </summary>
    public bool TryFindWalkHeading(
        uint targetId,
        float headingToTarget,
        float distanceMeters,
        out WalkVerdict verdict)
    {
        verdict = EvaluateWalk(targetId, headingToTarget, distanceMeters);
        if (verdict.IsUsable)
        {
            if (verdict.IsClear && targetId != 0u)
                ReportWalkClear(targetId);
            return true;
        }
        if (verdict.State == LineOfSightState.Blacklisted)
            return false;

        float lookahead = MathF.Min(distanceMeters, MathF.Max(1f, settings().WalkLookaheadMeters));
        bool anyFresh = !verdict.FromCache;
        WalkVerdict direct = verdict;
        foreach (float offset in SteerOffsetsDegrees)
        {
            WalkVerdict side = EvaluateWalk(targetId, headingToTarget + offset, lookahead);
            anyFresh |= !side.FromCache;
            if (side.IsUsable)
            {
                verdict = side;
                RememberWalk(targetId, side);
                return true;
            }
        }
        verdict = direct;
        RememberWalk(targetId, direct);
        if (anyFresh && targetId != 0u)
            _walkStrikePending.Add(targetId);
        return false;
    }

    /// <summary>
    /// The direct walk to a target is blocked by the world: arms a walk
    /// strike for the next <see cref="ReportBlocked"/> when the verdict is
    /// fresh, so a wall counts once per sweep, like a blocked shot.
    /// </summary>
    public void ReportWalkWalledOff(uint targetId, in WalkVerdict verdict)
    {
        if (targetId != 0u && !verdict.FromCache)
            _walkStrikePending.Add(targetId);
    }

    /// <summary>A walk toward the target is open: its walk strikes are forgiven.</summary>
    public void ReportWalkClear(uint targetId)
    {
        _walkStrikePending.Remove(targetId);
        if (_strikes.TryGetValue(targetId, out Strikes strikes) && strikes.Walks != 0)
        {
            strikes.Walks = 0;
            if (strikes.Count == 0 && strikes.BlacklistedUntil <= 0d)
                _strikes.Remove(targetId);
            else
                _strikes[targetId] = strikes;
        }
    }

    /// <summary>The most recent walk verdict for a target, for a status window.</summary>
    public bool TryGetLastWalk(uint targetId, out WalkVerdict verdict) =>
        _lastWalk.TryGetValue(targetId, out verdict);

    public bool IsBlacklisted(uint targetId)
    {
        if (!_strikes.TryGetValue(targetId, out Strikes strikes))
            return false;
        if (strikes.BlacklistedUntil <= 0d)
            return false;
        if (clock.Now < strikes.BlacklistedUntil)
            return true;
        _strikes.Remove(targetId);
        return false;
    }

    public double BlacklistSecondsRemaining(uint targetId) =>
        IsBlacklisted(targetId) ? _strikes[targetId].BlacklistedUntil - clock.Now : 0d;

    public int StrikesFor(uint targetId) =>
        _strikes.TryGetValue(targetId, out Strikes strikes) ? strikes.Count : 0;

    /// <summary>
    /// Counts one strike against a target the bot gave up shooting at. A
    /// strike is only taken once per fresh sweep, so re-reading a cached
    /// verdict every tick does not stack them. Returns true when the target
    /// has just been blacklisted.
    /// </summary>
    public bool ReportBlocked(uint targetId)
    {
        if (targetId == 0u)
            return false;
        bool shot = _strikePending.Remove(targetId);
        bool walk = _walkStrikePending.Remove(targetId);
        if (!shot && !walk)
            return false;
        LineOfSightSettings options = settings();
        _strikes.TryGetValue(targetId, out Strikes strikes);
        if (shot)
            strikes.Shots++;
        if (walk)
            strikes.Walks++;
        if (strikes.Count >= Math.Max(1, options.BlacklistStrikes))
        {
            strikes.BlacklistedUntil = clock.Now + Math.Max(0d, options.BlacklistSeconds);
            _strikes[targetId] = strikes;
            return true;
        }
        _strikes[targetId] = strikes;
        return false;
    }

    /// <summary>
    /// Puts a target on the blacklist outright, for the blacklist period:
    /// a host that refuses to swing at it three times over is not going to
    /// change its mind on the fourth, and three refusals are a third of a
    /// second, not three timeouts.
    /// </summary>
    public void Blacklist(uint targetId)
    {
        if (targetId == 0u)
            return;
        _strikePending.Remove(targetId);
        _walkStrikePending.Remove(targetId);
        LineOfSightSettings options = settings();
        _strikes.TryGetValue(targetId, out Strikes strikes);
        strikes.Unreachable = Math.Max(strikes.Unreachable, Math.Max(1, options.BlacklistStrikes));
        strikes.BlacklistedUntil = clock.Now + Math.Max(0d, options.BlacklistSeconds);
        _strikes[targetId] = strikes;
    }

    /// <summary>
    /// A walk toward the target ran out of time without reaching it: a strike
    /// of its own, whatever the sweeps said, so a monster on the far side of
    /// a wall (or one the character cannot get at) is left alone for a while
    /// instead of being walked at again the moment the walk gives up.
    /// Returns true when the target has just been blacklisted.
    /// </summary>
    public bool ReportUnreachable(uint targetId)
    {
        if (targetId == 0u)
            return false;
        _strikePending.Remove(targetId);
        _walkStrikePending.Remove(targetId);
        LineOfSightSettings options = settings();
        _strikes.TryGetValue(targetId, out Strikes strikes);
        strikes.Unreachable++;
        if (strikes.Count >= Math.Max(1, options.BlacklistStrikes))
        {
            strikes.BlacklistedUntil = clock.Now + Math.Max(0d, options.BlacklistSeconds);
            _strikes[targetId] = strikes;
            return true;
        }
        _strikes[targetId] = strikes;
        return false;
    }

    /// <summary>A shot at the target is open: its shot strikes are forgiven.</summary>
    public void ReportClear(uint targetId)
    {
        _strikePending.Remove(targetId);
        if (_strikes.TryGetValue(targetId, out Strikes strikes) && strikes.Shots != 0)
        {
            strikes.Shots = 0;
            if (strikes.Count == 0 && strikes.BlacklistedUntil <= 0d)
                _strikes.Remove(targetId);
            else
                _strikes[targetId] = strikes;
        }
    }

    /// <summary>Drop everything known about a target that left the world.</summary>
    public void Forget(uint targetId)
    {
        _strikes.Remove(targetId);
        _strikePending.Remove(targetId);
        _walkStrikePending.Remove(targetId);
        _last.Remove(targetId);
        _lastWalk.Remove(targetId);
        foreach ((uint Target, PluginProjectilePathKind, PluginAttackHeight) key in _cache.Keys.ToArray())
        {
            if (key.Target == targetId)
                _cache.Remove(key);
        }
        foreach ((uint Target, int) key in _walkCache.Keys.ToArray())
        {
            if (key.Target == targetId)
                _walkCache.Remove(key);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        _walkCache.Clear();
        _strikes.Clear();
        _last.Clear();
        _lastWalk.Clear();
        _strikePending.Clear();
        _walkStrikePending.Clear();
    }

    /// <summary>The most recent verdict for a target, for a status window.</summary>
    public bool TryGetLast(uint targetId, out LineOfSightVerdict verdict) =>
        _last.TryGetValue(targetId, out verdict);

    /// <summary>Drops verdicts for targets nobody has asked about lately.</summary>
    private void Prune(double now, double cacheSeconds)
    {
        const int pruneAbove = 64;
        if (_cache.Count + _walkCache.Count < pruneAbove)
            return;
        double keepFor = Math.Max(cacheSeconds, 1d) * 4d;
        foreach ((uint Target, PluginProjectilePathKind, PluginAttackHeight) key in _cache.Keys.ToArray())
        {
            if (now - _cache[key].Verdict.EvaluatedAt > keepFor)
                _cache.Remove(key);
        }
        foreach (uint target in _last.Keys.ToArray())
        {
            if (now - _last[target].EvaluatedAt > keepFor && !IsBlacklisted(target))
                _last.Remove(target);
        }
        foreach ((uint, int) key in _walkCache.Keys.ToArray())
        {
            if (now - _walkCache[key].Verdict.EvaluatedAt > keepFor)
                _walkCache.Remove(key);
        }
        foreach (uint target in _lastWalk.Keys.ToArray())
        {
            if (now - _lastWalk[target].EvaluatedAt > keepFor && !IsBlacklisted(target))
                _lastWalk.Remove(target);
        }
    }

    private WalkVerdict RememberWalk(uint targetId, WalkVerdict verdict)
    {
        if (targetId != 0u)
            _lastWalk[targetId] = verdict;
        return verdict;
    }

    public static float NormalizeHeading(float headingDegrees)
    {
        float heading = headingDegrees % 360f;
        if (heading < 0f)
            heading += 360f;
        return heading;
    }

    private LineOfSightVerdict Remember(uint targetId, LineOfSightVerdict verdict)
    {
        if (targetId != 0u)
            _last[targetId] = verdict;
        return verdict;
    }

    private void ShowSamples(LineOfSightSettings options, Entry entry) =>
        ShowSamples(options, entry.Samples);

    private void ShowSamples(LineOfSightSettings options, IReadOnlyList<PluginProjectileDebugSample> samples)
    {
        // The host keeps markers for a fraction of a second, so they are
        // re-sent on every read while the toggle is on.
        if (options.ShowDebugSamples && samples.Count > 0)
            projectiles.ShowDebugSamples(samples);
    }

    private static LineOfSightVerdict Unavailable(
        PluginProjectilePathKind kind,
        PluginAttackHeight height,
        double now) => new(
            LineOfSightState.Unavailable, kind, height,
            PluginProjectilePathStatus.Unavailable, 0u, now, FromCache: false);
}
