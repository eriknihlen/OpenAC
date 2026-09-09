using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Packs;
using AcDream.Core.World;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowEnvironmentGateTests
{
    [Theory]
    [InlineData(false, false, false, DirectionalShadowGateReason.PackDisabled)]
    [InlineData(true, true, false, DirectionalShadowGateReason.PortalOrLoginCover)]
    [InlineData(true, false, true, DirectionalShadowGateReason.Indoor)]
    internal void NonWorldGates_DisableWithoutCreatingCelestialWork(
        bool enabled,
        bool cover,
        bool indoor,
        DirectionalShadowGateReason expected)
    {
        DirectionalShadowEnvironmentInput input = new(
            enabled,
            cover,
            indoor,
            Celestial(AuthoredCelestialShadowSourceKind.SecondaryMoon, 35f),
            Atmosphere(WeatherKind.Clear));

        DirectionalShadowEnvironmentState result =
            DirectionalShadowEnvironmentGate.Evaluate(
                in input,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.False(result.ShouldRender);
        Assert.Equal(expected, result.Reason);
        Assert.Equal(0f, result.Strength);
    }

    [Fact]
    public void SelectedCelestialBelowHorizon_DisablesAndPreservesSourceIdentity()
    {
        AuthoredCelestialShadowSource source = Celestial(
            AuthoredCelestialShadowSourceKind.SecondaryMoon,
            -2f);
        DirectionalShadowEnvironmentInput input = new(
            true,
            false,
            false,
            source,
            Atmosphere(WeatherKind.Clear));

        DirectionalShadowEnvironmentState result =
            DirectionalShadowEnvironmentGate.Evaluate(
                in input,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.False(result.ShouldRender);
        Assert.Equal(
            DirectionalShadowGateReason.SelectedLightBelowHorizon,
            result.Reason);
        Assert.Equal(source.SurfaceToLightDirection, result.SurfaceToLightDirection);
        Assert.Equal(source.Kind, result.SourceKind);
        Assert.Equal(source.ObjectIndex, result.SourceObjectIndex);
        Assert.Equal(source.GfxObjId, result.SourceGfxObjId);
    }

    [Fact]
    public void NoVisibleCelestial_DisablesBeforeAtmosphereMapping()
    {
        DirectionalShadowEnvironmentInput input = new(
            true,
            false,
            false,
            AuthoredCelestialShadowSource.None(authoredEnergy: 1f),
            Atmosphere(WeatherKind.Clear));

        DirectionalShadowEnvironmentState result =
            DirectionalShadowEnvironmentGate.Evaluate(
                in input,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.False(result.ShouldRender);
        Assert.Equal(DirectionalShadowGateReason.NoVisibleCelestial, result.Reason);
        Assert.Equal(AuthoredCelestialShadowSourceKind.None, result.SourceKind);
    }

    [Fact]
    public void SelectedCelestialWithoutAuthoredEnergy_DisablesAndPreservesSourceIdentity()
    {
        AuthoredCelestialShadowSource source = Celestial(
            AuthoredCelestialShadowSourceKind.DominantMoon,
            35f,
            energy: 0f);
        DirectionalShadowEnvironmentInput input = new(
            true,
            false,
            false,
            source,
            Atmosphere(WeatherKind.Clear));

        DirectionalShadowEnvironmentState result =
            DirectionalShadowEnvironmentGate.Evaluate(
                in input,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.False(result.ShouldRender);
        Assert.Equal(
            DirectionalShadowGateReason.SelectedLightHasNoEnergy,
            result.Reason);
        Assert.Equal(source.SurfaceToLightDirection, result.SurfaceToLightDirection);
        Assert.Equal(source.Kind, result.SourceKind);
        Assert.Equal(source.ObjectIndex, result.SourceObjectIndex);
        Assert.Equal(source.GfxObjId, result.SourceGfxObjId);
    }

    [Fact]
    public void AuthoredWeatherAndDayGroup_SoftenAndReduceButDoNotReplaceSelectedCelestial()
    {
        AuthoredCelestialShadowSource source = Celestial(
            AuthoredCelestialShadowSourceKind.DominantMoon,
            35f);
        DirectionalShadowEnvironmentInput clearInput = new(
            true,
            false,
            false,
            source,
            Atmosphere(WeatherKind.Clear),
            ActiveDayGroupMultiplier: 1f);
        DirectionalShadowEnvironmentInput rainInput = clearInput with
        {
            Atmosphere = Atmosphere(WeatherKind.Rain),
            ActiveDayGroupMultiplier = 0.8f,
        };

        DirectionalShadowEnvironmentState clear =
            DirectionalShadowEnvironmentGate.Evaluate(
                in clearInput,
                DirectionalShadowAtmospherePolicy.BuiltIn);
        DirectionalShadowEnvironmentState rain =
            DirectionalShadowEnvironmentGate.Evaluate(
                in rainInput,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.True(clear.ShouldRender);
        Assert.True(rain.ShouldRender);
        Assert.Equal(source.SurfaceToLightDirection, clear.SurfaceToLightDirection);
        Assert.Equal(clear.SurfaceToLightDirection, rain.SurfaceToLightDirection);
        Assert.Equal(AuthoredCelestialShadowSourceKind.DominantMoon, clear.SourceKind);
        Assert.Equal(clear.SourceKind, rain.SourceKind);
        Assert.Equal(source.ObjectIndex, rain.SourceObjectIndex);
        Assert.Equal(source.GfxObjId, rain.SourceGfxObjId);
        Assert.True(rain.Strength < clear.Strength);
        Assert.True(rain.SoftnessMultiplier > clear.SoftnessMultiplier);
    }

    [Fact]
    public void ZeroDayGroupPolicy_DisablesThroughExplicitAtmosphereMapping()
    {
        DirectionalShadowEnvironmentInput input = new(
            true,
            false,
            false,
            Celestial(AuthoredCelestialShadowSourceKind.Sun, 35f),
            Atmosphere(WeatherKind.Clear),
            ActiveDayGroupMultiplier: 0f);

        DirectionalShadowEnvironmentState result =
            DirectionalShadowEnvironmentGate.Evaluate(
                in input,
                DirectionalShadowAtmospherePolicy.BuiltIn);

        Assert.Equal(DirectionalShadowGateReason.AtmosphereSuppressed, result.Reason);
        Assert.False(result.ShouldRender);
    }

    private static AuthoredCelestialShadowSource Celestial(
        AuthoredCelestialShadowSourceKind kind,
        float elevationDegrees,
        float energy = 1f)
    {
        float elevation = elevationDegrees * MathF.PI / 180f;
        const float heading = 120f * MathF.PI / 180f;
        float horizontal = MathF.Cos(elevation);
        Vector3 direction = new(
            horizontal * MathF.Cos(heading),
            horizontal * MathF.Sin(heading),
            MathF.Sin(elevation));
        uint gfxObjId = kind switch
        {
            AuthoredCelestialShadowSourceKind.Sun =>
                AuthoredCelestialShadowSourceResolver.SunGfxObjId,
            AuthoredCelestialShadowSourceKind.DominantMoon =>
                AuthoredCelestialShadowSourceResolver.DominantMoonGfxObjId,
            AuthoredCelestialShadowSourceKind.SecondaryMoon =>
                AuthoredCelestialShadowSourceResolver.SecondaryMoonGfxObjId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return new AuthoredCelestialShadowSource(
            Kind: kind,
            ObjectIndex: 4,
            GfxObjId: gfxObjId,
            SurfaceToLightDirection: direction,
            ElevationSin: direction.Z,
            AuthoredEnergy: energy);
    }

    private static AtmosphereSnapshot Atmosphere(WeatherKind weather) => new(
        weather,
        Intensity: 1f,
        FogColor: new Vector3(0.4f),
        FogStart: 80f,
        FogEnd: 350f,
        FogMode.Linear,
        LightningFlash: 0f,
        EnvironOverride.None);
}
