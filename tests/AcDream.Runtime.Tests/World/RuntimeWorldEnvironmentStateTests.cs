using System.Numerics;
using AcDream.Core.World;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests.World;

public sealed class RuntimeWorldEnvironmentStateTests
{
    [Fact]
    public void Initialize_OwnsRetailDayGroupAndOfflineNoon()
    {
        List<string> log = [];
        var state = new RuntimeWorldEnvironmentState(
            new ManualTimeProvider(),
            log.Add);

        state.Initialize(Definition(
            DerethDateTime.DayFractionOriginOffsetTicks,
            ("Sunny", Sky(0f))));

        Assert.True(state.IsInitialized);
        Assert.Equal(0, state.ActiveDayGroupIndex);
        Assert.Equal(WeatherKind.Clear, state.Weather.Kind);
        Assert.InRange(state.WorldTime.DayFraction, 0.499, 0.501);
        Assert.Contains(
            log,
            value => value.Contains(
                "SkyDesc.TickSize=0.8",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TwoInstances_IsolateCalendarClockWeatherAndDebugState()
    {
        var time = new ManualTimeProvider();
        var first = new RuntimeWorldEnvironmentState(time);
        var second = new RuntimeWorldEnvironmentState(time);
        first.Initialize(Definition(3600.0, ("Sunny", Sky(0f))));
        second.Initialize(Definition(
            DerethDateTime.DayFractionOriginOffsetTicks,
            ("Cloudy", Sky(0f))));

        first.SynchronizeFromServer(0.0);
        second.SynchronizeFromServer(0.0);
        _ = first.ApplyAdminEnvirons(0x01u);
        _ = first.CycleTimeOfDay();

        Assert.Equal(3600.0, first.WorldTime.Calendar.OriginOffsetTicks);
        Assert.Equal(
            DerethDateTime.DayFractionOriginOffsetTicks,
            second.WorldTime.Calendar.OriginOffsetTicks);
        Assert.Equal(0f, first.WorldTime.DayFraction);
        Assert.InRange(
            second.WorldTime.DayFraction,
            7.0 / 16.0 - 0.001,
            7.0 / 16.0 + 0.001);
        Assert.Equal(EnvironOverride.RedFog, first.Weather.Override);
        Assert.Equal(EnvironOverride.None, second.Weather.Override);
        Assert.Equal(WeatherKind.Clear, first.Weather.Kind);
        Assert.Equal(WeatherKind.Overcast, second.Weather.Kind);
    }

    [Fact]
    public void SynchronizeFromServer_ClearsDebugOverrideAndAdvancesClock()
    {
        var time = new ManualTimeProvider();
        var state = new RuntimeWorldEnvironmentState(time);
        state.Initialize(Definition(
            DerethDateTime.DayFractionOriginOffsetTicks,
            ("Sunny", Sky(0f))));
        _ = state.CycleTimeOfDay();

        state.SynchronizeFromServer(0.0);
        time.Advance(TimeSpan.FromSeconds(12));

        Assert.Equal(12.0, state.WorldTime.NowTicks, 5);
        Assert.NotEqual(0.0, state.WorldTime.DayFraction);
    }

    [Theory]
    [InlineData(0x00u, EnvironOverride.None)]
    [InlineData(0x01u, EnvironOverride.RedFog)]
    [InlineData(0x06u, EnvironOverride.BlackFog2)]
    public void ApplyAdminEnvirons_FogIsCanonicalRuntimeState(
        uint raw,
        EnvironOverride expected)
    {
        var state = Initialized();

        RuntimeEnvironmentEffect effect = state.ApplyAdminEnvirons(raw);

        Assert.Equal(RuntimeEnvironmentEffectKind.FogOverride, effect.Kind);
        Assert.Equal(expected, effect.FogOverride);
        Assert.Equal(expected, state.Snapshot.EnvironOverride);
    }

    [Fact]
    public void ApplyAdminEnvirons_SoundDoesNotMutateFogState()
    {
        var state = Initialized();
        _ = state.ApplyAdminEnvirons(0x04u);

        RuntimeEnvironmentEffect effect = state.ApplyAdminEnvirons(0x78u);

        Assert.Equal(RuntimeEnvironmentEffectKind.SoundCue, effect.Kind);
        Assert.Equal(RuntimeEnvironmentSoundCue.Thunder3, effect.SoundCue);
        Assert.Equal(EnvironOverride.GreenFog, state.Weather.Override);
    }

    [Fact]
    public void ForcedDayGroup_IsDefinitionScoped()
    {
        var state = new RuntimeWorldEnvironmentState(
            new ManualTimeProvider());
        state.Initialize(new RuntimeWorldEnvironmentDefinition(
            DerethDateTime.DayFractionOriginOffsetTicks,
            sourceTickSize: 0.8,
            lightTickSize: 0.2,
            [
                Group("Sunny", Sky(0f)),
                Group("Cloudy", Sky(0f)),
            ],
            forcedDayGroupIndex: 1));

        Assert.Equal(1, state.ActiveDayGroupIndex);
        Assert.Equal(WeatherKind.Overcast, state.Weather.Kind);
    }

    [Fact]
    public void Initialize_IsOneShot()
    {
        var state = Initialized();

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(
                () => state.Initialize(Definition(
                    3600.0,
                    ("Cloudy", Sky(0f)))));

        Assert.Contains("one-shot", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, state.ActiveDayGroupIndex);
    }

    private static RuntimeWorldEnvironmentState Initialized()
    {
        var state = new RuntimeWorldEnvironmentState(
            new ManualTimeProvider());
        state.Initialize(Definition(
            DerethDateTime.DayFractionOriginOffsetTicks,
            ("Sunny", Sky(0f))));
        return state;
    }

    private static RuntimeWorldEnvironmentDefinition Definition(
        double origin,
        params (string Name, SkyStateProvider Sky)[] groups) =>
        new(
            origin,
            sourceTickSize: 0.8,
            lightTickSize: 0.2,
            groups.Select(value => Group(value.Name, value.Sky)));

    private static RuntimeWorldDayGroupDefinition Group(
        string name,
        SkyStateProvider sky) =>
        new(
            name,
            ChanceOfOccur: 100f,
            SkyObjectCount: 1,
            sky);

    private static SkyStateProvider Sky(float begin) =>
        new(
        [
            new SkyKeyframe(
                Begin: begin,
                SunHeadingDeg: 0f,
                SunPitchDeg: 90f,
                DirColor: Vector3.One,
                DirBright: 1f,
                AmbColor: Vector3.One,
                AmbBright: 1f,
                FogColor: Vector3.Zero,
                FogDensity: 0f),
        ]);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now =
            new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
