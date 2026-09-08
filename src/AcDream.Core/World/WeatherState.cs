using System;
using System.Numerics;

namespace AcDream.Core.World;

public enum WeatherKind
{
    Clear    = 0,
    Overcast = 1,
    Rain     = 2,
    Snow     = 3,
    Storm    = 4,
}

public enum EnvironOverride
{
    None       = 0x00,
    RedFog     = 0x01,
    BlueFog    = 0x02,
    WhiteFog   = 0x03,
    GreenFog   = 0x04,
    BlackFog   = 0x05,
    BlackFog2  = 0x06,
}

public readonly record struct AtmosphereSnapshot(
    WeatherKind   Kind,
    float         Intensity,          // 0..1, eases on state transitions
    Vector3       FogColor,
    float         FogStart,
    float         FogEnd,
    FogMode       FogMode,
    float         LightningFlash,     // 0..1, decays from strike moment
    EnvironOverride Override);

public sealed class WeatherSystem
{
    public const float TransitionSeconds = 10f;

    private const float FlashDecay     = 1f / 0.200f;   // 1 / τ sec
    private const float FlashPeakHoldS = 0.05f;

    private WeatherKind _kind         = WeatherKind.Clear;
    private WeatherKind _previousKind = WeatherKind.Clear;

    private float _flashLevel;
    private float _flashAge;

    private EnvironOverride _override;

    private int _rolledDayIndex = int.MinValue;

    private bool _externallyDriven;

    public Func<bool>? DisableDistanceFogSource { get; set; }

    public WeatherSystem(Random? rng = null)
    {
        _ = rng;
    }

    public WeatherKind Kind => _kind;

    /// <summary>Last-known server fog override (sticky between sync packets).</summary>
    public EnvironOverride Override
    {
        get => _override;
        set => _override = value;
    }

    public void ForceWeather(WeatherKind kind)
    {
        BeginTransition(kind);
        _rolledDayIndex = int.MaxValue;  // "forced" sentinel — don't re-roll
    }

    public void SetKindFromDayGroupName(string? dayGroupName)
    {
        _externallyDriven = true;
        WeatherKind mapped = MapDayGroupNameToKind(dayGroupName);
        if (mapped != _kind) BeginTransition(mapped);
    }

    private static WeatherKind MapDayGroupNameToKind(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return WeatherKind.Clear;
        string lc = name.ToLowerInvariant();
        if (lc.Contains("storm")
         || lc.Contains("snow")
         || lc.Contains("rain")
         || lc.Contains("cloud")
         || lc.Contains("overcast")
         || lc.Contains("dark")
         || lc.Contains("fog"))      return WeatherKind.Overcast;
        return WeatherKind.Clear;
    }

    public void Tick(double nowSeconds, int dayIndex, float dtSeconds)
    {

        if (!_externallyDriven
            && dayIndex != _rolledDayIndex
            && _rolledDayIndex != int.MaxValue)
        {
            _rolledDayIndex = dayIndex;
            var newKind = RollKind(dayIndex);
            if (newKind != _kind) BeginTransition(newKind);
        }

        if (_flashLevel > 0f)
        {
            _flashAge += dtSeconds;
            if (_flashAge < FlashPeakHoldS)
                _flashLevel = 1f;
            else
                _flashLevel = MathF.Exp(-(_flashAge - FlashPeakHoldS) * FlashDecay);
            if (_flashLevel < 1e-3f) _flashLevel = 0f;
        }
    }

    /// <summary>
    /// Trigger a lightning flash manually (server-forced or test hook).
    /// </summary>
    public void TriggerFlash()
    {
        _flashLevel = 1f;
        _flashAge = 0f;
    }

    public AtmosphereSnapshot Snapshot(in SkyKeyframe kf)
    {
        Vector3 fogColor = kf.FogColor;
        float   fogStart = kf.FogStart;
        float   fogEnd   = kf.FogEnd;
        FogMode fogMode  = kf.FogMode;

        if (_override != EnvironOverride.None)
            fogColor = EnvironOverrideColor(_override);

        if (DisableDistanceFogSource?.Invoke() == true)
            fogMode = FogMode.Off;

        return new AtmosphereSnapshot(
            Kind:            _kind,             // informational
            Intensity:       1f,
            FogColor:        fogColor,
            FogStart:        fogStart,
            FogEnd:          fogEnd,
            FogMode:         fogMode,
            LightningFlash:  _flashLevel,       // 0 in production; TriggerFlash hook for tests
            Override:        _override);
    }

    // ----------------------------------------------------------------
    // Internal machinery
    // ----------------------------------------------------------------

    private void BeginTransition(WeatherKind newKind)
    {
        _previousKind = _kind;
        _kind = newKind;
    }

    private static WeatherKind RollKind(int dayIndex)
    {
        int seed = unchecked((int)((uint)dayIndex * 0x9E3779B1u));
        var rng = new Random(seed);
        double r = rng.NextDouble();
        if (r < 0.60) return WeatherKind.Clear;
        if (r < 0.80) return WeatherKind.Overcast;
        if (r < 0.92) return WeatherKind.Rain;
        if (r < 0.97) return WeatherKind.Snow;
        return WeatherKind.Storm;
    }

    private static Vector3 EnvironOverrideColor(EnvironOverride o) => o switch
    {
        EnvironOverride.RedFog    => new Vector3(0.60f, 0.05f, 0.05f),
        EnvironOverride.BlueFog   => new Vector3(0.08f, 0.15f, 0.60f),
        EnvironOverride.WhiteFog  => new Vector3(0.90f, 0.90f, 0.92f),
        EnvironOverride.GreenFog  => new Vector3(0.08f, 0.55f, 0.12f),
        EnvironOverride.BlackFog  => new Vector3(0.02f, 0.02f, 0.02f),
        EnvironOverride.BlackFog2 => new Vector3(0.04f, 0.01f, 0.01f),
        _                         => new Vector3(1f, 1f, 1f),
    };
}
