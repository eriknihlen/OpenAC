using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal static class RenderPackAtmospherePolicyEvaluation
{
    internal static DirectionalShadowAtmospherePolicy NeutralDirectionalShadowElevation { get; } =
        DirectionalShadowAtmospherePolicy.BuiltIn with
        {
            MinimumLightElevationSin = -1.001f,
            FullStrengthLightElevationSin = -1f,
        };

    internal static float Ray(
        IReadOnlyList<SunElevationResponsePoint>? points,
        float elevationDegrees,
        float fallback = 1f) => Evaluate(
            points,
            elevationDegrees,
            static value => (float)value,
            static value => (float)value,
            fallback);

    internal static float DirectionalShadow(
        IReadOnlyList<SunElevationResponsePoint>? points,
        float elevationDegrees,
        float fallback = 0f) => DirectionalShadowFromSin(
            points,
            MathF.Sin(elevationDegrees * (MathF.PI / 180f)),
            fallback);

    internal static float DirectionalShadowFromSin(
        IReadOnlyList<SunElevationResponsePoint>? points,
        float lightElevationSin,
        float fallback = 0f) => Evaluate(
            points,
            Math.Clamp(lightElevationSin, -1f, 1f),
            static degrees => MathF.Sin((float)degrees * (MathF.PI / 180f)),
            static value => (float)value,
            fallback);

    internal static (float Mean, float Gust) FoliageWind(
        IReadOnlyList<FoliageWindWeatherPoint>? points,
        WeatherKind weather)
    {
        if (points is null)
            return (0f, 0f);
        string kind = weather.ToString();
        FoliageWindWeatherPoint? clear = null;
        foreach (FoliageWindWeatherPoint point in points)
        {
            if (string.Equals(point.WeatherKind, kind, StringComparison.Ordinal))
                return ((float)point.Mean, (float)point.Gust);
            if (clear is null
                && string.Equals(point.WeatherKind, nameof(WeatherKind.Clear), StringComparison.Ordinal))
            {
                clear = point;
            }
        }
        return clear is { } fallback ? ((float)fallback.Mean, (float)fallback.Gust) : (0f, 0f);
    }

    internal static float EaseTowardTarget(
        float current,
        float target,
        float deltaSeconds,
        float transitionSeconds)
    {
        float rate = transitionSeconds <= 0f
            ? 1f
            : Math.Clamp(deltaSeconds / transitionSeconds, 0f, 1f);
        return current + ((target - current) * rate);
    }

    internal static float VolumetricShaft(
        IReadOnlyList<SunElevationResponsePoint>? points,
        float elevationDegrees,
        float fallback = 0f) => Evaluate(
            points,
            elevationDegrees,
            static value => (float)value,
            static value => value * value * (3f - (2f * value)),
            fallback);

    private static float Evaluate(
        IReadOnlyList<SunElevationResponsePoint>? points,
        float input,
        Func<double, float> transformPoint,
        Func<float, float> transformInterpolation,
        float fallback)
    {
        if (points is null || points.Count == 0)
            return fallback;
        float first = transformPoint(points[0].ElevationDegrees);
        if (input <= first)
            return (float)points[0].Multiplier;
        for (int i = 1; i < points.Count; i++)
        {
            SunElevationResponsePoint upper = points[i];
            float upperInput = transformPoint(upper.ElevationDegrees);
            if (input > upperInput)
                continue;
            SunElevationResponsePoint lower = points[i - 1];
            float lowerInput = transformPoint(lower.ElevationDegrees);
            float span = upperInput - lowerInput;
            float t = span <= 0f
                ? 0f
                : Math.Clamp((input - lowerInput) / span, 0f, 1f);
            t = transformInterpolation(t);
            return (float)(lower.Multiplier
                + ((upper.Multiplier - lower.Multiplier) * t));
        }
        return (float)points[^1].Multiplier;
    }
}
