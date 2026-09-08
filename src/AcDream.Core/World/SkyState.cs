using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.World;

public enum FogMode
{
    Off    = 0,
    Linear = 1,
    Exp    = 2,
    Exp2   = 3,
}

public readonly record struct SkyKeyframe(
    float   Begin,              // [0, 1] day-fraction this keyframe kicks in
    float   SunHeadingDeg,
    float   SunPitchDeg,        // elevation above horizon (-90=below, +90=zenith)
    Vector3 DirColor,           // RGB linear, RAW (NOT × DirBright)
    float   DirBright,          // sun brightness multiplier
    Vector3 AmbColor,           // RGB linear, RAW (NOT × AmbBright)
    float   AmbBright,          // ambient brightness multiplier
    Vector3 FogColor,
    float   FogDensity,         // retained for tests; derive from FogStart/End
    float   FogStart = 80f,
    float   FogEnd   = 350f,
    FogMode FogMode  = FogMode.Linear)
{
    public Vector3 SunColor => DirColor * SkyStateProvider.RetailSunVector(this).Length();

    public Vector3 AmbientColor =>
        AmbColor * (AmbBright + 0.2f * SkyStateProvider.RetailSunVector(this).Length());
}

public sealed class SkyStateProvider
{
    private readonly List<SkyKeyframe> _keyframes;

    public SkyStateProvider(IReadOnlyList<SkyKeyframe> keyframes)
    {
        if (keyframes is null || keyframes.Count == 0)
            throw new ArgumentException("At least one keyframe required", nameof(keyframes));
        // Sort by Begin so the walk is deterministic regardless of input order.
        var sorted = new List<SkyKeyframe>(keyframes);
        sorted.Sort((a, b) => a.Begin.CompareTo(b.Begin));
        _keyframes = sorted;
    }

    public int KeyframeCount => _keyframes.Count;
    public IReadOnlyList<SkyKeyframe> Keyframes => _keyframes;

    public static SkyStateProvider Default()
    {
        // Day fractions: 0.0=midnight, 0.25=dawn, 0.5=noon, 0.75=dusk.
        return new SkyStateProvider(new[]
        {
            new SkyKeyframe(
                Begin: 0.0f,
                SunHeadingDeg: 0f,     // below horizon (north)
                SunPitchDeg:   -30f,
                DirColor:      new Vector3(0.02f, 0.02f, 0.08f), // deep blue
                DirBright:     1.0f,
                AmbColor:      new Vector3(0.05f, 0.05f, 0.12f),
                AmbBright:     1.0f,
                FogColor:      new Vector3(0.02f, 0.02f, 0.05f),
                FogDensity:    0.004f,
                FogStart:      30f,
                FogEnd:        180f,
                FogMode:       FogMode.Linear),
            new SkyKeyframe(
                Begin: 0.25f,
                SunHeadingDeg: 90f,    // east at dawn
                SunPitchDeg:   0f,
                DirColor:      new Vector3(1.0f, 0.7f, 0.4f),    // sunrise warm
                DirBright:     1.0f,
                AmbColor:      new Vector3(0.4f, 0.35f, 0.3f),
                AmbBright:     1.0f,
                FogColor:      new Vector3(0.8f, 0.55f, 0.4f),
                FogDensity:    0.002f,
                FogStart:      60f,
                FogEnd:        260f,
                FogMode:       FogMode.Linear),
            new SkyKeyframe(
                Begin: 0.5f,
                SunHeadingDeg: 180f,   // south at noon
                SunPitchDeg:   70f,
                DirColor:      new Vector3(1.0f, 0.98f, 0.95f),  // bright white-ish
                DirBright:     1.0f,
                AmbColor:      new Vector3(0.5f, 0.5f, 0.55f),
                AmbBright:     1.0f,
                FogColor:      new Vector3(0.7f, 0.75f, 0.85f),
                FogDensity:    0.0008f,
                FogStart:      120f,
                FogEnd:        500f,
                FogMode:       FogMode.Linear),
            new SkyKeyframe(
                Begin: 0.75f,
                SunHeadingDeg: 270f,   // west at dusk
                SunPitchDeg:   0f,
                DirColor:      new Vector3(0.95f, 0.4f, 0.25f),  // sunset red
                DirBright:     1.0f,
                AmbColor:      new Vector3(0.35f, 0.25f, 0.25f),
                AmbBright:     1.0f,
                FogColor:      new Vector3(0.85f, 0.45f, 0.35f),
                FogDensity:    0.002f,
                FogStart:      60f,
                FogEnd:        260f,
                FogMode:       FogMode.Linear),
        });
    }

    public SkyKeyframe Interpolate(float t)
    {
        t = (float)(t - Math.Floor(t));   // wrap to [0, 1)

        // Find k1: last keyframe with Begin <= t.
        int k1Index = _keyframes.Count - 1;
        for (int i = 0; i < _keyframes.Count; i++)
        {
            if (_keyframes[i].Begin <= t)
                k1Index = i;
            else
                break;
        }
        int k2Index = (k1Index + 1) % _keyframes.Count;

        var k1 = _keyframes[k1Index];
        var k2 = _keyframes[k2Index];

        float k1Begin = k1.Begin;
        float k2Begin = k2.Begin;
        if (k2Begin <= k1Begin) k2Begin += 1.0f;  // unroll wrap
        float tWrapped = t;
        if (tWrapped < k1Begin) tWrapped += 1.0f;

        float span = Math.Max(1e-6f, k2Begin - k1Begin);
        float u = (tWrapped - k1Begin) / span;
        u = Math.Clamp(u, 0f, 1f);

        // Angular lerp for sun heading: pick shortest arc.
        float heading = ShortestAngleLerp(k1.SunHeadingDeg, k2.SunHeadingDeg, u);

        return new SkyKeyframe(
            Begin:         t,
            SunHeadingDeg: heading,
            SunPitchDeg:   Lerp(k1.SunPitchDeg, k2.SunPitchDeg, u),
            DirColor:      Vector3.Lerp(k1.DirColor, k2.DirColor, u),
            DirBright:     Lerp(k1.DirBright, k2.DirBright, u),
            AmbColor:      Vector3.Lerp(k1.AmbColor, k2.AmbColor, u),
            AmbBright:     Lerp(k1.AmbBright, k2.AmbBright, u),
            FogColor:      Vector3.Lerp(k1.FogColor, k2.FogColor, u),
            FogDensity:    Lerp(k1.FogDensity, k2.FogDensity, u),
            FogStart:      Lerp(k1.FogStart, k2.FogStart, u),
            FogEnd:        Lerp(k1.FogEnd, k2.FogEnd, u),
            FogMode:       k1.FogMode);
    }

    private static float Lerp(float a, float b, float u) => a + (b - a) * u;

    /// <summary>
    /// Shortest-arc heading lerp: r12 §4. If <c>a=350</c> and <c>b=10</c>
    /// the lerp walks 20° forward through 0° rather than 340° backward.
    /// </summary>
    public static float ShortestAngleLerp(float aDeg, float bDeg, float u)
    {
        float delta = bDeg - aDeg;
        while (delta > 180f)  delta -= 360f;
        while (delta < -180f) delta += 360f;
        return aDeg + delta * u;
    }

    public static Vector3 RetailSunVector(SkyKeyframe kf)
    {
        float h = kf.SunHeadingDeg * (MathF.PI / 180f);
        float p = kf.SunPitchDeg * (MathF.PI / 180f);
        float cosP = MathF.Cos(p);
        float sinP = MathF.Sin(p);
        float B = kf.DirBright;
        return new Vector3(
            B * cosP * MathF.Sin(h),    // x = DirBright × cos(P) × sin(H)
            B * cosP * MathF.Cos(h),    // y = DirBright × cos(P) × cos(H)
            B * sinP);                   // z = DirBright × sin(P)
    }

    public static Vector3 SunDirectionFromKeyframe(SkyKeyframe kf)
    {
        var v = RetailSunVector(kf);
        float len = v.Length();
        return len > 1e-6f ? v / len : Vector3.UnitZ;
    }
}

public sealed class WorldTimeService
{
    private SkyStateProvider _sky;
    private double _lastSyncedTicks;
    private DateTimeOffset _lastSyncedWallClockUtc;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _synchronizationDiagnostic;

    private float? _debugDayFractionOverride;

    public float? PinnedDayFraction { get; set; }

    public double TickSize { get; set; } = 1.0;

    public WorldTimeService(SkyStateProvider sky)
        : this(sky, new DerethCalendar(), TimeProvider.System)
    {
    }

    public WorldTimeService(
        SkyStateProvider sky,
        DerethCalendar calendar,
        TimeProvider? timeProvider = null,
        Action<string>? synchronizationDiagnostic = null)
    {
        _sky = sky ?? throw new ArgumentNullException(nameof(sky));
        Calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _synchronizationDiagnostic = synchronizationDiagnostic;
        _lastSyncedWallClockUtc = _timeProvider.GetUtcNow();
    }

    public DerethCalendar Calendar { get; }

    public void SetProvider(SkyStateProvider sky)
    {
        _sky = sky ?? throw new ArgumentNullException(nameof(sky));
    }

    public void SyncFromServer(double serverTicks)
    {
        _lastSyncedTicks = serverTicks;
        _lastSyncedWallClockUtc = _timeProvider.GetUtcNow();
        _debugDayFractionOverride = null;

        if (_synchronizationDiagnostic is not null)
        {
            var df = Calendar.DayFraction(serverTicks);
            var cal = Calendar.ToCalendar(serverTicks);
            _synchronizationDiagnostic(
                $"[sky-dump] SyncFromServer: ticks={serverTicks:F1} dayFraction={df:F4} " +
                $"calendar=PY{cal.Year} {cal.Month} {cal.Day} {cal.Hour}");
        }
    }

    public void SetDebugTime(float dayFraction)
    {
        _debugDayFractionOverride = dayFraction;
    }

    public void ClearDebugTime() => _debugDayFractionOverride = null;

    public double NowTicks
    {
        get
        {
            double elapsed =
                (_timeProvider.GetUtcNow() - _lastSyncedWallClockUtc)
                .TotalSeconds;
            return _lastSyncedTicks + elapsed * TickSize;
        }
    }

    public double DayFraction
    {
        get
        {
            if (PinnedDayFraction.HasValue)
                return PinnedDayFraction.Value;
            if (_debugDayFractionOverride.HasValue)
                return _debugDayFractionOverride.Value;
            return Calendar.DayFraction(NowTicks);
        }
    }

    public SkyKeyframe CurrentSky => _sky.Interpolate((float)DayFraction);

    public SkyKeyframe SkyAtDayFraction(float dayFraction) =>
        _sky.Interpolate(dayFraction);

    public Vector3 CurrentSunDirection =>
        SkyStateProvider.SunDirectionFromKeyframe(CurrentSky);

    public DerethDateTime.Calendar CurrentCalendar =>
        Calendar.ToCalendar(NowTicks);

    public bool IsDaytime => Calendar.IsDaytime(NowTicks);
}
