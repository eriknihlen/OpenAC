using System.Numerics;
using AcDream.App.World;
using AcDream.Core.World;
using AcDream.Runtime.World;

namespace AcDream.App.Tests.World;

[Collection(WorldEnvironmentControllerCollection.Name)]
public sealed class WorldEnvironmentControllerTests
{
    [Fact]
    public void Initialize_InstallsDatDayGroupAndSeedsOfflineNoon()
    {
        List<string> log = [];
        var controller = CreateController(log);
        LoadedSkyDesc sky = SingleGroupSky("Sunny", begin: 0f);

        controller.Initialize(sky, zeroTimeOfYear: null);

        Assert.Same(sky.DayGroups[0], controller.ActiveDayGroup);
        Assert.Equal(WeatherKind.Clear, controller.Weather.Kind);
        Assert.Equal(1.0, controller.WorldTime.TickSize);
        Assert.InRange(controller.DayFraction, 0.499f, 0.501f);
        Assert.Contains(log, line => line.Contains("loaded Region 0x13000000", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("DayGroup[0] \"Sunny\"", StringComparison.Ordinal));
    }

    [Fact]
    public void SynchronizeFromServer_RefreshesAtMostOnceWithinSameDay()
    {
        List<string> log = [];
        var controller = CreateController(log);
        controller.Initialize(SingleGroupSky("Cloudy", begin: 0f), zeroTimeOfYear: null);

        controller.SynchronizeFromServer(100_000.0);
        int selectedAfterFirstSync = log.Count(IsDayGroupSelection);
        controller.SynchronizeFromServer(100_000.0);

        Assert.Equal(selectedAfterFirstSync, log.Count(IsDayGroupSelection));
        Assert.Equal(WeatherKind.Overcast, controller.Weather.Kind);
    }

    [Fact]
    public void Initialize_IsOneShotAndRetainsFirstPublishedEnvironment()
    {
        var controller = CreateController([]);
        LoadedSkyDesc first = SingleGroupSky("Sunny", begin: 0f);
        controller.Initialize(first, zeroTimeOfYear: null);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => controller.Initialize(
                SingleGroupSky("Cloudy", begin: 0f),
                zeroTimeOfYear: 3600.0));

        Assert.Contains("one-shot", error.Message, StringComparison.Ordinal);
        Assert.Same(first.DayGroups[0], controller.ActiveDayGroup);
        Assert.Equal(WeatherKind.Clear, controller.Weather.Kind);
    }

    [Fact]
    public void Initialize_WithoutGameTimeUsesInstanceFallbackOrigin()
    {
        var first = CreateController([]);
        var second = CreateController([]);

        first.Initialize(
            SingleGroupSky("Sunny", begin: 0f),
            zeroTimeOfYear: 3600.0);
        second.Initialize(
            SingleGroupSky("Sunny", begin: 0f),
            zeroTimeOfYear: null);

        Assert.Equal(3600.0, first.WorldTime.Calendar.OriginOffsetTicks);
        Assert.Equal(
            DerethDateTime.DayFractionOriginOffsetTicks,
            second.WorldTime.Calendar.OriginOffsetTicks);
        Assert.InRange(second.DayFraction, 0.499f, 0.501f);
    }

    [Theory]
    [InlineData(0x00u, EnvironOverride.None)]
    [InlineData(0x01u, EnvironOverride.RedFog)]
    [InlineData(0x06u, EnvironOverride.BlackFog2)]
    public void ApplyAdminEnvirons_FogValuesSetStickyOverride(
        uint raw,
        EnvironOverride expected)
    {
        List<string> log = [];
        var controller = CreateController(log);

        controller.ApplyAdminEnvirons(raw);

        Assert.Equal(expected, controller.Weather.Override);
        Assert.Contains(log, line => line.Contains(expected.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyAdminEnvirons_SoundValuePreservesWeatherAndLogsRetailName()
    {
        List<string> log = [];
        var controller = CreateController(log);
        controller.Weather.Override = EnvironOverride.GreenFog;

        controller.ApplyAdminEnvirons(0x78u);

        Assert.Equal(EnvironOverride.GreenFog, controller.Weather.Override);
        Assert.Contains(log, line => line.Contains("Thunder3Sound", StringComparison.Ordinal));
    }

    [Fact]
    public void CycleTimeOfDay_PreservesAcceptedFiveStepSequence()
    {
        var controller = CreateController([]);

        Assert.Equal("Time override = 0.00", controller.CycleTimeOfDay());
        Assert.Equal(0f, controller.DayFraction);
        Assert.Equal("Time override = 0.25", controller.CycleTimeOfDay());
        Assert.Equal(0.25f, controller.DayFraction);
        Assert.Equal("Time override = 0.50", controller.CycleTimeOfDay());
        Assert.Equal(0.5f, controller.DayFraction);
        Assert.Equal("Time override = 0.75", controller.CycleTimeOfDay());
        Assert.Equal(0.75f, controller.DayFraction);
        Assert.Equal("Time override cleared", controller.CycleTimeOfDay());
    }

    [Fact]
    public void CycleWeather_PreservesAcceptedOrder()
    {
        var controller = CreateController([]);

        Assert.Equal("Weather = Overcast", controller.CycleWeather());
        Assert.Equal(WeatherKind.Overcast, controller.Weather.Kind);
        Assert.Equal("Weather = Rain", controller.CycleWeather());
        Assert.Equal(WeatherKind.Rain, controller.Weather.Kind);
        Assert.Equal("Weather = Snow", controller.CycleWeather());
        Assert.Equal(WeatherKind.Snow, controller.Weather.Kind);
        Assert.Equal("Weather = Storm", controller.CycleWeather());
        Assert.Equal(WeatherKind.Storm, controller.Weather.Kind);
        Assert.Equal("Weather = Clear", controller.CycleWeather());
        Assert.Equal(WeatherKind.Clear, controller.Weather.Kind);
    }

    private static WorldEnvironmentController CreateController(List<string> log) =>
        new(
            new RuntimeWorldEnvironmentState(log: log.Add),
            forcedDayGroupIndex: null,
            log.Add);

    private static LoadedSkyDesc SingleGroupSky(string name, float begin)
    {
        var keyframe = new SkyKeyframe(
            Begin: begin,
            SunHeadingDeg: 0f,
            SunPitchDeg: 90f,
            DirColor: Vector3.One,
            DirBright: 1f,
            AmbColor: Vector3.One,
            AmbBright: 1f,
            FogColor: Vector3.Zero,
            FogDensity: 0f);
        var group = new DayGroupData
        {
            ChanceOfOccur = 100f,
            Name = name,
            SkyTimes =
            [
                new DatSkyKeyframeData { Keyframe = keyframe },
            ],
        };
        return new LoadedSkyDesc
        {
            TickSize = 0.8,
            LightTickSize = 0.2,
            DayGroups = [group],
        };
    }

    private static bool IsDayGroupSelection(string line) =>
        line.Contains("→ DayGroup[", StringComparison.Ordinal);
}

[CollectionDefinition(Name)]
public sealed class WorldEnvironmentControllerCollection
{
    public const string Name = "World environment";
}
