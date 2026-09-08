using System.Globalization;
using AcDream.Core.World;

namespace AcDream.Runtime.World;

public sealed record RuntimeWorldDayGroupDefinition(
    string Name,
    float ChanceOfOccur,
    int SkyObjectCount,
    SkyStateProvider Sky)
{
    public string Name { get; } = Name ?? string.Empty;
    public SkyStateProvider Sky { get; } =
        Sky ?? throw new ArgumentNullException(nameof(Sky));
}

public sealed class RuntimeWorldEnvironmentDefinition
{
    private readonly RuntimeWorldDayGroupDefinition[] _dayGroups;

    public RuntimeWorldEnvironmentDefinition(
        double originOffsetTicks,
        double sourceTickSize,
        double lightTickSize,
        IEnumerable<RuntimeWorldDayGroupDefinition>? dayGroups,
        int? forcedDayGroupIndex = null)
    {
        if (!double.IsFinite(originOffsetTicks))
            throw new ArgumentOutOfRangeException(nameof(originOffsetTicks));
        if (!double.IsFinite(sourceTickSize))
            throw new ArgumentOutOfRangeException(nameof(sourceTickSize));
        if (!double.IsFinite(lightTickSize))
            throw new ArgumentOutOfRangeException(nameof(lightTickSize));

        OriginOffsetTicks = originOffsetTicks;
        SourceTickSize = sourceTickSize;
        LightTickSize = lightTickSize;
        _dayGroups = dayGroups?.ToArray() ?? [];
        if (forcedDayGroupIndex is < 0
            || forcedDayGroupIndex >= _dayGroups.Length)
        {
            forcedDayGroupIndex = null;
        }
        ForcedDayGroupIndex = forcedDayGroupIndex;
    }

    public double OriginOffsetTicks { get; }
    public double SourceTickSize { get; }
    public double LightTickSize { get; }
    public IReadOnlyList<RuntimeWorldDayGroupDefinition> DayGroups => _dayGroups;
    public int? ForcedDayGroupIndex { get; }
}

public enum RuntimeEnvironmentEffectKind
{
    Unknown,
    FogOverride,
    SoundCue,
}

public enum RuntimeEnvironmentSoundCue
{
    Roar = 0x65,
    Bell = 0x66,
    Chant1 = 0x67,
    Chant2 = 0x68,
    DarkWhispers1 = 0x69,
    DarkWhispers2 = 0x6A,
    DarkLaugh = 0x6B,
    DarkWind = 0x6C,
    DarkSpeech = 0x6D,
    Drums = 0x6E,
    GhostSpeak = 0x6F,
    Breathing = 0x70,
    Howl = 0x71,
    LostSouls = 0x72,
    Squeal = 0x75,
    Thunder1 = 0x76,
    Thunder2 = 0x77,
    Thunder3 = 0x78,
    Thunder4 = 0x79,
    Thunder5 = 0x7A,
    Thunder6 = 0x7B,
}

public readonly record struct RuntimeEnvironmentEffect(
    RuntimeEnvironmentEffectKind Kind,
    uint RawValue,
    EnvironOverride FogOverride = EnvironOverride.None,
    RuntimeEnvironmentSoundCue? SoundCue = null);

public readonly record struct RuntimeWorldEnvironmentSnapshot(
    long Revision,
    bool IsInitialized,
    int ActiveDayGroupIndex,
    long ActiveDayIndex,
    WeatherKind Weather,
    EnvironOverride EnvironOverride);

public readonly record struct RuntimeWorldEnvironmentOwnershipSnapshot(
    bool IsInitialized,
    int DayGroupDefinitionCount,
    int ActiveDayGroupCount);

public interface IRuntimeWorldEnvironmentView
{
    RuntimeWorldEnvironmentSnapshot Snapshot { get; }
    RuntimeWorldEnvironmentOwnershipSnapshot Ownership { get; }
}

public sealed class RuntimeWorldEnvironmentState
    : IRuntimeWorldEnvironmentView
{
    private static readonly WeatherKind[] DebugWeatherKinds =
    [
        WeatherKind.Clear,
        WeatherKind.Overcast,
        WeatherKind.Rain,
        WeatherKind.Snow,
        WeatherKind.Storm,
    ];

    private readonly Action<string> _log;
    private RuntimeWorldEnvironmentDefinition? _definition;
    private long _activeDayIndex = long.MinValue;
    private int _timeDebugStep;
    private int _weatherDebugStep;
    private long _revision;

    public RuntimeWorldEnvironmentState(
        TimeProvider? timeProvider = null,
        Action<string>? log = null,
        Action<string>? timeSyncDiagnostic = null)
    {
        WorldTime = new WorldTimeService(
            SkyStateProvider.Default(),
            new DerethCalendar(),
            timeProvider,
            timeSyncDiagnostic);
        Weather = new WeatherSystem();
        _log = log ?? (_ => { });
    }

    public WorldTimeService WorldTime { get; }
    public WeatherSystem Weather { get; }
    public int ActiveDayGroupIndex { get; private set; } = -1;
    public bool IsInitialized => _definition is not null;

    public RuntimeWorldEnvironmentSnapshot Snapshot => new(
        _revision,
        IsInitialized,
        ActiveDayGroupIndex,
        _activeDayIndex,
        Weather.Kind,
        Weather.Override);

    public RuntimeWorldEnvironmentOwnershipSnapshot Ownership =>
        CaptureOwnership();

    public RuntimeWorldEnvironmentOwnershipSnapshot CaptureOwnership() =>
        new(
            IsInitialized,
            _definition?.DayGroups.Count ?? 0,
            ActiveDayGroupIndex >= 0 ? 1 : 0);

    public void Initialize(RuntimeWorldEnvironmentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_definition is not null)
        {
            throw new InvalidOperationException(
                "The world environment is a one-shot Runtime lifetime owner.");
        }

        _definition = definition;
        WorldTime.Calendar.SetOriginOffset(definition.OriginOffsetTicks);
        WorldTime.TickSize = 1.0;
        ActiveDayGroupIndex = -1;
        _activeDayIndex = long.MinValue;
        _revision++;

        if (definition.DayGroups.Count > 0)
        {
            _log(string.Create(
                CultureInfo.InvariantCulture,
                $"sky: loaded Region 0x13000000 — {definition.DayGroups.Count} day groups, "
                + $"SkyDesc.TickSize={definition.SourceTickSize} (throttle, not rate), "
                + $"LightTickSize={definition.LightTickSize}"));
            RefreshDayGroup();
        }

        WorldTime.SyncFromServer(DerethDateTime.DayTicks / 16.0);
        _revision++;
    }

    public void SynchronizeFromServer(double ticks)
    {
        WorldTime.SyncFromServer(ticks);
        _revision++;
        if (IsInitialized)
            RefreshDayGroup();
    }

    public RuntimeEnvironmentEffect ApplyAdminEnvirons(uint changeType)
    {
        if (changeType <= 0x06u)
        {
            var value = (EnvironOverride)changeType;
            Weather.Override = value;
            _revision++;
            _log($"live: AdminEnvirons fog override = {value}");
            return new RuntimeEnvironmentEffect(
                RuntimeEnvironmentEffectKind.FogOverride,
                changeType,
                value);
        }

        RuntimeEnvironmentSoundCue? cue =
            Enum.IsDefined(typeof(RuntimeEnvironmentSoundCue), (int)changeType)
                ? (RuntimeEnvironmentSoundCue)(int)changeType
                : null;
        if (cue is null)
        {
            _log(
                $"live: AdminEnvirons sound cue = Unknown(0x{changeType:X2}) "
                + $"(0x{changeType:X2}) — audio binding pending");
            return new RuntimeEnvironmentEffect(
                RuntimeEnvironmentEffectKind.Unknown,
                changeType);
        }

        _log(
            $"live: AdminEnvirons sound cue = {cue}Sound "
            + $"(0x{changeType:X2}) — audio binding pending");
        return new RuntimeEnvironmentEffect(
            RuntimeEnvironmentEffectKind.SoundCue,
            changeType,
            SoundCue: cue);
    }

    public void RefreshDayGroup()
    {
        RuntimeWorldEnvironmentDefinition? definition = _definition;
        if (definition is null)
            return;
        if (definition.DayGroups.Count == 0)
            return;

        double ticks = WorldTime.NowTicks;
        DerethCalendar calendar = WorldTime.Calendar;
        int absoluteYear = calendar.AbsoluteYear(ticks);
        int dayOfYear = calendar.DayOfYear(ticks);
        int daysPerYear =
            DerethDateTime.DaysInAMonth * DerethDateTime.MonthsInAYear;
        long dayIndex = (long)absoluteYear * daysPerYear + dayOfYear;
        int index = SkyDayGroupSelector.SelectIndex(
            definition.DayGroups.Count,
            absoluteYear,
            daysPerYear,
            dayOfYear,
            definition.ForcedDayGroupIndex);

        if (dayIndex == _activeDayIndex && index == ActiveDayGroupIndex)
            return;

        _activeDayIndex = dayIndex;
        ActiveDayGroupIndex = index;
        RuntimeWorldDayGroupDefinition group = definition.DayGroups[index];
        WorldTime.SetProvider(group.Sky);
        Weather.SetKindFromDayGroupName(group.Name);
        _revision++;

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"sky: PY{absoluteYear} day{dayOfYear} → DayGroup[{index}] \"{group.Name}\" "
            + $"(Chance={group.ChanceOfOccur:F2}, {group.SkyObjectCount} objects, "
            + $"{group.Sky.KeyframeCount} keyframes, weather={Weather.Kind})"));
    }

    public string CycleTimeOfDay()
    {
        _timeDebugStep = (_timeDebugStep + 1) % 5;
        float? selection = _timeDebugStep switch
        {
            0 => null,
            1 => 0.0f,
            2 => 0.25f,
            3 => 0.5f,
            4 => 0.75f,
            _ => null,
        };

        _revision++;
        if (selection.HasValue)
        {
            WorldTime.SetDebugTime(selection.Value);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Time override = {selection.Value:F2}");
        }

        WorldTime.ClearDebugTime();
        return "Time override cleared";
    }

    public string CycleWeather()
    {
        _weatherDebugStep =
            (_weatherDebugStep + 1) % DebugWeatherKinds.Length;
        WeatherKind kind = DebugWeatherKinds[_weatherDebugStep];
        Weather.ForceWeather(kind);
        _revision++;
        return $"Weather = {kind}";
    }

}
