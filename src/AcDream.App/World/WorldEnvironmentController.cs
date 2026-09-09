using AcDream.Core.Audio;
using AcDream.Core.Content;
using AcDream.App.Rendering;
using AcDream.Core.World;
using AcDream.Runtime.World;
using DatReaderWriter.DBObjs;

namespace AcDream.App.World;

internal sealed class WorldEnvironmentController : IWorldSceneSkyStateSource
{
    private readonly Action<string> _log;
    private readonly int? _forcedDayGroupIndex;
    private LoadedSkyDesc? _loadedSkyDesc;

    public WorldEnvironmentController(Action<string>? log = null)
        : this(
            new RuntimeWorldEnvironmentState(log: log),
            forcedDayGroupIndex: null,
            log)
    {
    }

    internal WorldEnvironmentController(
        RuntimeWorldEnvironmentState runtime,
        int? forcedDayGroupIndex = null,
        Action<string>? log = null,
        float? pinnedDayFraction = null)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _forcedDayGroupIndex = forcedDayGroupIndex;
        _log = log ?? (_ => { });
        Runtime.WorldTime.PinnedDayFraction = pinnedDayFraction;
        if (pinnedDayFraction.HasValue)
            _log($"sky: world time PINNED at day fraction {pinnedDayFraction.Value:F4}");
    }

    public RuntimeWorldEnvironmentState Runtime { get; }
    public WorldTimeService WorldTime => Runtime.WorldTime;
    public WeatherSystem Weather => Runtime.Weather;

    public DayGroupData? ActiveDayGroup
    {
        get
        {
            int index = Runtime.ActiveDayGroupIndex;
            return _loadedSkyDesc is not null
                && index >= 0
                && index < _loadedSkyDesc.DayGroups.Count
                    ? _loadedSkyDesc.DayGroups[index]
                    : null;
        }
    }

    public int ActiveDayGroupIndex => Runtime.ActiveDayGroupIndex;

    public float DayFraction => (float)WorldTime.DayFraction;

    public void Initialize(Region region, IDatObjectSource? dats = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        Initialize(
            SkyDescLoader.LoadFromRegion(region, dats),
            region.GameTime?.ZeroTimeOfYear);
    }

    internal void Initialize(
        LoadedSkyDesc? loadedSkyDesc,
        double? zeroTimeOfYear)
    {
        if (Runtime.IsInitialized)
        {
            throw new InvalidOperationException(
                "The world environment is a one-shot Runtime lifetime owner.");
        }

        double origin = zeroTimeOfYear
            ?? DerethDateTime.DayFractionOriginOffsetTicks;
        if (zeroTimeOfYear.HasValue)
        {
            _log(
                $"sky: GameTime ZeroTimeOfYear={zeroTimeOfYear.Value} "
                + $"(was default {DerethDateTime.DayFractionOriginOffsetTicks})");
        }

        RuntimeWorldDayGroupDefinition[] groups =
            loadedSkyDesc?.DayGroups.Select(
                group => new RuntimeWorldDayGroupDefinition(
                    group.Name,
                    group.ChanceOfOccur,
                    group.SkyObjects.Count,
                    new SkyStateProvider(
                        group.SkyTimes.Select(value => value.Keyframe).ToList())))
            .ToArray()
            ?? [];

        var definition = new RuntimeWorldEnvironmentDefinition(
            origin,
            loadedSkyDesc?.TickSize ?? 1.0,
            loadedSkyDesc?.LightTickSize ?? 1.0,
            groups,
            _forcedDayGroupIndex);

        Runtime.Initialize(definition);
        _loadedSkyDesc = loadedSkyDesc;
    }

    public void SynchronizeFromServer(double ticks) =>
        Runtime.SynchronizeFromServer(ticks);

    public Action<uint>? EnvironSoundSink { get; set; }

    public void ApplyAdminEnvirons(uint environChangeType)
    {
        RuntimeEnvironmentEffect effect = Runtime.ApplyAdminEnvirons(environChangeType);
        if (effect.Kind is RuntimeEnvironmentEffectKind.SoundCue)
            EnvironSoundSink?.Invoke(environChangeType);
    }

    public void RefreshSkyForCurrentDay() => Runtime.RefreshDayGroup();

    public string CycleTimeOfDay() => Runtime.CycleTimeOfDay();

    public string CycleWeather() => Runtime.CycleWeather();
}
