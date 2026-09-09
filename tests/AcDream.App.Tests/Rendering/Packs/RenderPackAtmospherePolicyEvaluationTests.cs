using AcDream.App.Rendering.Packs;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class RenderPackAtmospherePolicyEvaluationTests
{
    private static readonly FoliageWindWeatherPoint[] FiveKindTable =
    [
        new("Clear", 0.25, 0.15),
        new("Overcast", 0.60, 0.35),
        new("Rain", 0.85, 0.60),
        new("Snow", 0.35, 0.20),
        new("Storm", 1.00, 0.75),
    ];

    [Theory]
    [InlineData(WeatherKind.Clear, 0.25f, 0.15f)]
    [InlineData(WeatherKind.Overcast, 0.60f, 0.35f)]
    [InlineData(WeatherKind.Rain, 0.85f, 0.60f)]
    [InlineData(WeatherKind.Snow, 0.35f, 0.20f)]
    [InlineData(WeatherKind.Storm, 1.00f, 0.75f)]
    public void FoliageWindResolvesEachOfTheFiveDeclaredWeatherKinds(
        WeatherKind kind,
        float expectedMean,
        float expectedGust)
    {
        (float mean, float gust) = RenderPackAtmospherePolicyEvaluation.FoliageWind(
            FiveKindTable,
            kind);

        Assert.Equal(expectedMean, mean);
        Assert.Equal(expectedGust, gust);
    }

    [Fact]
    public void FoliageWindFallsBackToTheClearRowForAnUnlistedKind()
    {
        FoliageWindWeatherPoint[] table =
        [
            new("Clear", 0.25, 0.15),
            new("Storm", 1.00, 0.75),
        ];

        (float mean, float gust) = RenderPackAtmospherePolicyEvaluation.FoliageWind(
            table,
            WeatherKind.Overcast);

        Assert.Equal(0.25f, mean);
        Assert.Equal(0.15f, gust);
    }

    [Fact]
    public void FoliageWindReturnsZeroWhenNeitherTheKindNorClearIsDeclared()
    {
        FoliageWindWeatherPoint[] table = [new("Storm", 1.00, 0.75)];

        (float mean, float gust) = RenderPackAtmospherePolicyEvaluation.FoliageWind(
            table,
            WeatherKind.Overcast);

        Assert.Equal(0f, mean);
        Assert.Equal(0f, gust);
    }

    [Fact]
    public void FoliageWindReturnsZeroForANullTable()
    {
        (float mean, float gust) = RenderPackAtmospherePolicyEvaluation.FoliageWind(
            null,
            WeatherKind.Clear);

        Assert.Equal(0f, mean);
        Assert.Equal(0f, gust);
    }

    [Fact]
    public void FoliageWindMatchesByExactNameNotSubstringOrCase()
    {
        FoliageWindWeatherPoint[] table =
        [
            new("Rainy", 0.99, 0.99),
            new("rain", 0.99, 0.99),
            new("Clear", 0.10, 0.05),
        ];

        (float mean, float gust) = RenderPackAtmospherePolicyEvaluation.FoliageWind(
            table,
            WeatherKind.Rain);

        Assert.Equal(0.10f, mean);
        Assert.Equal(0.05f, gust);
    }

    [Fact]
    public void EaseTowardTargetDoesNotMoveWithZeroDelta()
    {
        float result = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
            current: 0.25f,
            target: 0.85f,
            deltaSeconds: 0f,
            transitionSeconds: 10f);

        Assert.Equal(0.25f, result);
    }

    [Fact]
    public void EaseTowardTargetConvergesWithoutOvershootOrDiscontinuity()
    {
        const float start = 0.25f;
        const float target = 0.85f;
        const float transitionSeconds = 10f;
        const float tickSeconds = 0.1f;
        float current = start;
        float previous = current;
        const int ticks = 4000;

        for (int i = 0; i < ticks; i++)
        {
            current = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
                current,
                target,
                tickSeconds,
                transitionSeconds);

            Assert.True(
                current >= previous - 1e-6f,
                $"tick {i}: value regressed from {previous} to {current}");
            Assert.True(
                current <= target + 1e-6f,
                $"tick {i}: value {current} overshot target {target}");
            previous = current;
        }

        Assert.True(MathF.Abs(current - target) < 0.01f, $"did not converge: {current}");
    }

    [Fact]
    public void EaseTowardTargetSnapsInOneStepWhenTransitionSecondsIsZeroOrLess()
    {
        float result = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
            current: 0f,
            target: 1f,
            deltaSeconds: 0.001f,
            transitionSeconds: 0f);

        Assert.Equal(1f, result);
    }

    [Fact]
    public void EaseTowardTargetClampsAnOversizedDeltaInsteadOfOvershooting()
    {
        // A delta larger than the transition window (e.g. after a long
        // pause) must not overshoot past the target.
        float result = RenderPackAtmospherePolicyEvaluation.EaseTowardTarget(
            current: 0f,
            target: 1f,
            deltaSeconds: 1000f,
            transitionSeconds: 10f);

        Assert.Equal(1f, result);
    }
}
