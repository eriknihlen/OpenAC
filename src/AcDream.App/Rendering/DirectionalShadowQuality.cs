using AcDream.App.Rendering.Packs;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal enum DirectionalShadowPreset : byte
{
    Low,
    Medium,
    High,
}

[Flags]
internal enum DirectionalShadowSemantics : ushort
{
    None = 0,
    Terrain = 1 << 0,
    TreesAndOutdoorStatics = 1 << 1,
    Buildings = 1 << 2,
    Players = 1 << 3,
    Monsters = 1 << 4,
    AnimatedTransforms = 1 << 5,
    AlphaCutoutCasters = 1 << 6,

    Headline = Terrain
        | TreesAndOutdoorStatics
        | Buildings
        | Players
        | Monsters
        | AnimatedTransforms
        | AlphaCutoutCasters,
}

internal readonly record struct DirectionalShadowBiasPolicy(
    float ConstantTexels,
    float SlopeTexels,
    float NormalTexels,
    float MinimumMeters,
    float MaximumMeters)
{
    public DirectionalShadowWorldBias Resolve(float texelWorldSize)
    {
        if (!float.IsFinite(texelWorldSize) || texelWorldSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(texelWorldSize));
        if (!float.IsFinite(MinimumMeters)
            || !float.IsFinite(MaximumMeters)
            || MinimumMeters < 0f
            || MaximumMeters < MinimumMeters)
        {
            throw new InvalidOperationException(
                "Directional-shadow bias bounds must be finite, non-negative, and ordered.");
        }

        var minimumMeters = MinimumMeters;
        var maximumMeters = MaximumMeters;
        return new DirectionalShadowWorldBias(
            ConstantDepthMeters: Math.Clamp(
                ConstantTexels * texelWorldSize,
                minimumMeters,
                maximumMeters),
            SlopeDepthMeters: Math.Clamp(
                SlopeTexels * texelWorldSize,
                minimumMeters,
                maximumMeters),
            NormalOffsetMeters: Math.Clamp(
                NormalTexels * texelWorldSize,
                minimumMeters,
                maximumMeters));
    }
}

internal readonly record struct DirectionalShadowWorldBias(
    float ConstantDepthMeters,
    float SlopeDepthMeters,
    float NormalOffsetMeters);

internal readonly record struct DirectionalShadowQuality(
    DirectionalShadowPreset Preset,
    int CascadeCount,
    int MapResolution,
    float MaximumReachMeters,
    int PcfRadiusTexels,
    long ApproximateDepthMapBytes,
    double IncrementalGpuP50BudgetMilliseconds,
    double IncrementalGpuP99BudgetMilliseconds,
    double IncrementalCpuP50BudgetMilliseconds,
    double IncrementalCpuP99BudgetMilliseconds,
    long PackResidentGpuByteBudget,
    DirectionalShadowSemantics Semantics,
    DirectionalShadowBiasPolicy BiasPolicy)
{
    private const long MiB = 1024L * 1024L;

    public static DirectionalShadowQuality For(DirectionalShadowPreset preset) =>
        preset switch
        {
            DirectionalShadowPreset.Low => Create(
                preset,
                cascades: 2,
                resolution: 768,
                reachMeters: 72f,
                pcfRadius: 0,
                gpuP50: 2.0,
                gpuP99: 3.0,
                cpuP50: 0.15,
                cpuP99: 0.50,
                residentBudget: 64L * MiB,
                bias: new DirectionalShadowBiasPolicy(
                    0.45f, 1.25f, 1.0f, 0.001f, 0.35f)),
            DirectionalShadowPreset.Medium => Create(
                preset,
                cascades: 3,
                resolution: 1536,
                reachMeters: 144f,
                pcfRadius: 1,
                gpuP50: 3.25,
                gpuP99: 4.50,
                cpuP50: 0.25,
                cpuP99: 0.75,
                residentBudget: 128L * MiB,
                bias: new DirectionalShadowBiasPolicy(
                    0.40f, 1.15f, 0.9f, 0.001f, 0.30f)),
            DirectionalShadowPreset.High => Create(
                preset,
                cascades: 4,
                resolution: 2048,
                reachMeters: 240f,
                pcfRadius: 2,
                gpuP50: 4.50,
                gpuP99: 6.00,
                cpuP50: 0.35,
                cpuP99: 1.00,
                residentBudget: 256L * MiB,
                bias: new DirectionalShadowBiasPolicy(
                    0.35f, 1.0f, 0.8f, 0.001f, 0.25f)),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
        };

    private static DirectionalShadowQuality Create(
        DirectionalShadowPreset preset,
        int cascades,
        int resolution,
        float reachMeters,
        int pcfRadius,
        double gpuP50,
        double gpuP99,
        double cpuP50,
        double cpuP99,
        long residentBudget,
        DirectionalShadowBiasPolicy bias) =>
        new(
            preset,
            cascades,
            resolution,
            reachMeters,
            pcfRadius,
            checked((long)cascades * resolution * resolution * sizeof(float)),
            gpuP50,
            gpuP99,
            cpuP50,
            cpuP99,
            residentBudget,
            DirectionalShadowSemantics.Headline,
            bias);
}

internal enum DirectionalShadowGateReason : byte
{
    Enabled,
    PackDisabled,
    PortalOrLoginCover,
    Indoor,
    NoVisibleCelestial,
    SelectedLightBelowHorizon,
    SelectedLightHasNoEnergy,
    AtmosphereSuppressed,
    ResidentWindowUnavailable,
}

internal readonly record struct DirectionalShadowAtmospherePolicy(
    float MinimumLightElevationSin,
    float FullStrengthLightElevationSin,
    float ClearStrength,
    float OvercastStrength,
    float RainStrength,
    float SnowStrength,
    float StormStrength,
    float ClearSoftness,
    float OvercastSoftness,
    float RainSoftness,
    float SnowSoftness,
    float StormSoftness)
{
    public static DirectionalShadowAtmospherePolicy BuiltIn { get; } = new(
        MinimumLightElevationSin: MathF.Sin(MathF.PI / 180f),
        FullStrengthLightElevationSin: MathF.Sin(12f * MathF.PI / 180f),
        ClearStrength: 1.0f,
        OvercastStrength: 0.65f,
        RainStrength: 0.45f,
        SnowStrength: 0.60f,
        StormStrength: 0.25f,
        ClearSoftness: 1.0f,
        OvercastSoftness: 1.5f,
        RainSoftness: 1.8f,
        SnowSoftness: 1.6f,
        StormSoftness: 2.0f);

    public float StrengthFor(WeatherKind weather) => weather switch
    {
        WeatherKind.Clear => ClearStrength,
        WeatherKind.Overcast => OvercastStrength,
        WeatherKind.Rain => RainStrength,
        WeatherKind.Snow => SnowStrength,
        WeatherKind.Storm => StormStrength,
        _ => throw new ArgumentOutOfRangeException(nameof(weather), weather, null),
    };

    public float SoftnessFor(WeatherKind weather) => weather switch
    {
        WeatherKind.Clear => ClearSoftness,
        WeatherKind.Overcast => OvercastSoftness,
        WeatherKind.Rain => RainSoftness,
        WeatherKind.Snow => SnowSoftness,
        WeatherKind.Storm => StormSoftness,
        _ => throw new ArgumentOutOfRangeException(nameof(weather), weather, null),
    };
}

internal readonly record struct DirectionalShadowEnvironmentInput(
    bool PackEnabled,
    bool PortalOrLoginCoverVisible,
    bool PlayerInsideCell,
    AuthoredCelestialShadowSource Source,
    AtmosphereSnapshot Atmosphere,
    float ActiveDayGroupMultiplier = 1f);

internal readonly record struct DirectionalShadowEnvironmentState(
    DirectionalShadowGateReason Reason,
    System.Numerics.Vector3 SurfaceToLightDirection,
    float LightElevationSin,
    float Strength,
    float SoftnessMultiplier,
    AuthoredCelestialShadowSourceKind SourceKind =
        AuthoredCelestialShadowSourceKind.None,
    int SourceObjectIndex = -1,
    uint SourceGfxObjId = 0u)
{
    public bool ShouldRender => Reason is DirectionalShadowGateReason.Enabled;
}

internal static class DirectionalShadowEnvironmentGate
{
    private const float MinimumDirectionalEnergy = 1e-5f;

    public static DirectionalShadowEnvironmentState Evaluate(
        in DirectionalShadowEnvironmentInput input,
        in DirectionalShadowAtmospherePolicy policy)
    {
        if (!input.PackEnabled)
            return Disabled(DirectionalShadowGateReason.PackDisabled);
        if (input.PortalOrLoginCoverVisible)
            return Disabled(DirectionalShadowGateReason.PortalOrLoginCover);
        if (input.PlayerInsideCell)
            return Disabled(DirectionalShadowGateReason.Indoor);

        if (!input.Source.IsAvailable)
            return Disabled(DirectionalShadowGateReason.NoVisibleCelestial);

        System.Numerics.Vector3 surfaceToLight =
            input.Source.SurfaceToLightDirection;
        float elevation = input.Source.ElevationSin;
        if (!float.IsFinite(elevation)
            || elevation <= policy.MinimumLightElevationSin)
        {
            return new DirectionalShadowEnvironmentState(
                DirectionalShadowGateReason.SelectedLightBelowHorizon,
                surfaceToLight,
                elevation,
                0f,
                1f,
                input.Source.Kind,
                input.Source.ObjectIndex,
                input.Source.GfxObjId);
        }

        float energy = input.Source.AuthoredEnergy;
        if (!float.IsFinite(energy)
            || energy <= MinimumDirectionalEnergy)
        {
            return new DirectionalShadowEnvironmentState(
                DirectionalShadowGateReason.SelectedLightHasNoEnergy,
                surfaceToLight,
                elevation,
                0f,
                1f,
                input.Source.Kind,
                input.Source.ObjectIndex,
                input.Source.GfxObjId);
        }

        float elevationSpan = MathF.Max(
            1e-5f,
            policy.FullStrengthLightElevationSin - policy.MinimumLightElevationSin);
        float elevationStrength = Math.Clamp(
            (elevation - policy.MinimumLightElevationSin) / elevationSpan,
            0f,
            1f);
        float weatherStrength = policy.StrengthFor(input.Atmosphere.Kind);
        float atmosphereProgress = Math.Clamp(input.Atmosphere.Intensity, 0f, 1f);
        float dayGroupStrength = Math.Clamp(input.ActiveDayGroupMultiplier, 0f, 1f);
        float strength = elevationStrength
            * Math.Clamp(energy, 0f, 1f)
            * weatherStrength
            * atmosphereProgress
            * dayGroupStrength;
        if (!float.IsFinite(strength) || strength <= 0f)
        {
            return new DirectionalShadowEnvironmentState(
                DirectionalShadowGateReason.AtmosphereSuppressed,
                surfaceToLight,
                elevation,
                0f,
                policy.SoftnessFor(input.Atmosphere.Kind),
                input.Source.Kind,
                input.Source.ObjectIndex,
                input.Source.GfxObjId);
        }

        return new DirectionalShadowEnvironmentState(
            DirectionalShadowGateReason.Enabled,
            surfaceToLight,
            elevation,
            Math.Clamp(strength, 0f, 1f),
            MathF.Max(1f, policy.SoftnessFor(input.Atmosphere.Kind)),
            input.Source.Kind,
            input.Source.ObjectIndex,
            input.Source.GfxObjId);
    }

    private static DirectionalShadowEnvironmentState Disabled(
        DirectionalShadowGateReason reason) =>
        new(reason, System.Numerics.Vector3.UnitZ, 0f, 0f, 1f);
}
