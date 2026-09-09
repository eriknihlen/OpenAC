using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Packs;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class VolumetricShaftQualityTests
{
    [Fact]
    public void Weak_hardware_preserves_contract_but_defaults_volumetrics_off()
    {
        VolumetricShaftQuality low = VolumetricShaftQuality.For(DirectionalShadowPreset.Low);
        VolumetricShaftQuality medium = VolumetricShaftQuality.For(DirectionalShadowPreset.Medium);
        VolumetricShaftQuality high = VolumetricShaftQuality.For(DirectionalShadowPreset.High);

        Assert.False(low.EnabledByDefault);
        Assert.Equal(0.25f, medium.ResolutionScale);
        Assert.Equal(0.50f, high.ResolutionScale);
        Assert.True(low.RayMarchSteps < medium.RayMarchSteps);
        Assert.True(medium.RayMarchSteps < high.RayMarchSteps);
    }

    [Fact]
    public void Clear_raking_sun_is_stronger_than_noon_and_overcast()
    {
        VolumetricShaftQuality quality =
            VolumetricShaftQuality.For(DirectionalShadowPreset.High);
        DirectionalShadowEnvironmentState raking = Enabled(elevationSin: 0.20f);
        DirectionalShadowEnvironmentState noon = Enabled(elevationSin: 0.95f);

        VolumetricShaftFrameParameters clear = VolumetricShaftPolicy.Evaluate(
            in quality,
            userEnabled: true,
            in raking,
            WeatherKind.Clear,
            Vector3.One,
            1f);
        VolumetricShaftFrameParameters highSun = VolumetricShaftPolicy.Evaluate(
            in quality,
            userEnabled: true,
            in noon,
            WeatherKind.Clear,
            Vector3.One,
            1f);
        VolumetricShaftFrameParameters overcast = VolumetricShaftPolicy.Evaluate(
            in quality,
            userEnabled: true,
            in raking,
            WeatherKind.Overcast,
            Vector3.One,
            1f);

        Assert.True(clear.Enabled);
        Assert.True(clear.Strength > highSun.Strength);
        Assert.True(clear.Strength > overcast.Strength);
    }

    [Fact]
    public void Missing_shadow_indoor_or_user_off_cannot_leave_shafts_enabled()
    {
        VolumetricShaftQuality quality =
            VolumetricShaftQuality.For(DirectionalShadowPreset.Medium);
        DirectionalShadowEnvironmentState indoor = new(
            DirectionalShadowGateReason.Indoor,
            Vector3.UnitZ,
            0.5f,
            0f,
            1f);
        DirectionalShadowEnvironmentState outdoor = Enabled(0.2f);

        Assert.False(VolumetricShaftPolicy.Evaluate(
            in quality, true, in indoor, WeatherKind.Clear, Vector3.One, 1f).Enabled);
        Assert.False(VolumetricShaftPolicy.Evaluate(
            in quality, false, in outdoor, WeatherKind.Clear, Vector3.One, 1f).Enabled);
    }

    [Fact]
    public void Moon_shadow_source_never_manufactures_sun_shafts()
    {
        VolumetricShaftQuality quality =
            VolumetricShaftQuality.For(DirectionalShadowPreset.High);
        DirectionalShadowEnvironmentState moon = Enabled(0.35f) with
        {
            SourceKind = AuthoredCelestialShadowSourceKind.DominantMoon,
        };

        VolumetricShaftFrameParameters result = VolumetricShaftPolicy.Evaluate(
            in quality,
            userEnabled: true,
            in moon,
            WeatherKind.Clear,
            Vector3.One,
            authoredSunBrightness: 1f);

        Assert.False(result.Enabled);
    }

    private static DirectionalShadowEnvironmentState Enabled(float elevationSin) => new(
        DirectionalShadowGateReason.Enabled,
        Vector3.Normalize(new Vector3(0.5f, 0.5f, elevationSin)),
        elevationSin,
        Strength: 1f,
        SoftnessMultiplier: 1f,
        SourceKind: AuthoredCelestialShadowSourceKind.Sun);
}
