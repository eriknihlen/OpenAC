using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal readonly record struct VolumetricShaftQuality(
    DirectionalShadowPreset Preset,
    bool EnabledByDefault,
    float ResolutionScale,
    int RayMarchSteps,
    double IncrementalGpuP50BudgetMilliseconds,
    double IncrementalGpuP99BudgetMilliseconds)
{
    internal static VolumetricShaftQuality For(DirectionalShadowPreset preset) =>
        preset switch
        {
            DirectionalShadowPreset.Low => new(
                preset,
                EnabledByDefault: false,
                ResolutionScale: 0.25f,
                RayMarchSteps: 24,
                IncrementalGpuP50BudgetMilliseconds: 0.15,
                IncrementalGpuP99BudgetMilliseconds: 0.30),
            DirectionalShadowPreset.Medium => new(
                preset,
                EnabledByDefault: true,
                ResolutionScale: 0.25f,
                RayMarchSteps: 40,
                IncrementalGpuP50BudgetMilliseconds: 0.25,
                IncrementalGpuP99BudgetMilliseconds: 0.40),
            DirectionalShadowPreset.High => new(
                preset,
                EnabledByDefault: true,
                ResolutionScale: 0.50f,
                RayMarchSteps: 56,
                IncrementalGpuP50BudgetMilliseconds: 0.40,
                IncrementalGpuP99BudgetMilliseconds: 0.65),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };
}

internal readonly record struct VolumetricShaftFrameParameters(
    bool Enabled,
    float ResolutionScale,
    int RayMarchSteps,
    float Density,
    float Strength,
    Vector3 AuthoredSunColor)
{
    internal static VolumetricShaftFrameParameters Disabled =>
        new(false, 0f, 0, 0f, 0f, Vector3.Zero);
}

internal static class VolumetricShaftPolicy
{
    internal static VolumetricShaftFrameParameters Evaluate(
        in VolumetricShaftQuality quality,
        bool userEnabled,
        in DirectionalShadowEnvironmentState shadow,
        WeatherKind weather,
        Vector3 authoredSunColor,
        float authoredSunBrightness)
    {
        if (!userEnabled
            || !shadow.ShouldRender
            || shadow.SourceKind is not Packs.AuthoredCelestialShadowSourceKind.Sun
            || !float.IsFinite(authoredSunBrightness)
            || authoredSunBrightness <= 0f)
            return VolumetricShaftFrameParameters.Disabled;

        float weatherMultiplier = weather switch
        {
            WeatherKind.Clear => 1f,
            WeatherKind.Overcast => 0.18f,
            WeatherKind.Rain => 0.10f,
            WeatherKind.Snow => 0.16f,
            WeatherKind.Storm => 0.06f,
            _ => 0f,
        };
        if (weatherMultiplier <= 0f)
            return VolumetricShaftFrameParameters.Disabled;

        // Peak at a raking but valid sun. The shadow gate already fades the
        // first degrees above the horizon; this term then rolls shafts away
        // toward noon without inventing a time-of-day schedule.
        float elevation = Math.Clamp(shadow.LightElevationSin, 0f, 1f);
        float noonRollOff = 1f - SmoothStep(0.18f, 0.82f, elevation);
        float strength = Math.Clamp(
            shadow.Strength * weatherMultiplier * noonRollOff,
            0f,
            1f);
        if (strength <= 1e-4f)
            return VolumetricShaftFrameParameters.Disabled;

        Vector3 color = Vector3.Max(Vector3.Zero, authoredSunColor)
            * Math.Clamp(authoredSunBrightness, 0f, 8f);
        return new VolumetricShaftFrameParameters(
            Enabled: true,
            quality.ResolutionScale,
            quality.RayMarchSteps,
            Density: 0.035f * strength,
            Strength: strength,
            AuthoredSunColor: color);
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float t = Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
