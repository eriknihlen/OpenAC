using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using AcDream.Core.World;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct AtmosphericFrameInputs(
    Vector2 SunScreenUv,
    bool SunIsOnScreen,
    float SunElevationDegrees,
    Vector3 SunColor,
    Vector3 SunDirection,
    float SunDirectionalBrightness,
    Matrix4x4 InverseViewProjection,
    int ActiveDayGroup,
    WeatherKind Weather,
    float WeatherIntensity,
    double DeltaSeconds,
    int ViewportWidth,
    int ViewportHeight,
    bool IsOutdoor);

internal interface IAtmosphericWorldFrameSink
{
    void Publish(
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup);
}

internal sealed class AtmosphericFrameInputState : IAtmosphericWorldFrameSink
{
    private RenderFrameInput _host;
    private RenderFrameFoundation _foundation;
    private AtmosphericFrameInputs _current;
    private bool _published;

    internal void BeginFrame(
        in RenderFrameInput host,
        in RenderFrameFoundation foundation)
    {
        _host = host;
        _foundation = foundation;
        _current = default;
        _published = false;
    }

    public void Publish(
        in RenderFrameFoundation foundation,
        in WorldRenderFrame world,
        int activeDayGroup)
    {
        Vector3 direction = SkyStateProvider.SunDirectionFromKeyframe(foundation.Sky);
        Vector3 sunPoint = world.Camera.Position + (direction * 10_000f);
        Vector4 clip = Vector4.Transform(
            new Vector4(sunPoint, 1f),
            world.Camera.ViewProjection);
        bool finite = float.IsFinite(clip.X)
            && float.IsFinite(clip.Y)
            && float.IsFinite(clip.W)
            && clip.W > 1e-5f;
        Vector2 uv = finite
            ? new Vector2(
                (clip.X / clip.W * 0.5f) + 0.5f,
                0.5f - (clip.Y / clip.W * 0.5f))
            : new Vector2(-1f, -1f);
        bool onScreen = finite
            && uv.X >= 0f && uv.X <= 1f
            && uv.Y >= 0f && uv.Y <= 1f;
        Matrix4x4 inverseViewProjection = Matrix4x4.Invert(
            world.Camera.ViewProjection,
            out Matrix4x4 inverse)
                ? inverse
                : Matrix4x4.Identity;

        _current = new AtmosphericFrameInputs(
            uv,
            onScreen,
            foundation.Sky.SunPitchDeg,
            foundation.Sky.SunColor,
            direction,
            foundation.Sky.DirBright,
            inverseViewProjection,
            activeDayGroup,
            foundation.Atmosphere.Kind,
            Math.Clamp(foundation.Atmosphere.Intensity, 0f, 1f),
            _host.DeltaSeconds,
            _host.ViewportWidth,
            _host.ViewportHeight,
            IsOutdoor: world.Roots.IsAtmosphericallyOutdoor);
        _published = true;
    }

    internal AtmosphericFrameInputs Snapshot()
    {
        if (_published)
            return _current;

        return new AtmosphericFrameInputs(
            new Vector2(-1f, -1f),
            SunIsOnScreen: false,
            _foundation.Sky.SunPitchDeg,
            _foundation.Sky.SunColor,
            SkyStateProvider.SunDirectionFromKeyframe(_foundation.Sky),
            _foundation.Sky.DirBright,
            Matrix4x4.Identity,
            -1,
            _foundation.Atmosphere.Kind,
            Math.Clamp(_foundation.Atmosphere.Intensity, 0f, 1f),
            _host.DeltaSeconds,
            _host.ViewportWidth,
            _host.ViewportHeight,
            IsOutdoor: false);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct AtmosphericFrameUniforms
{
    internal const int SizeInBytes = 192;

    internal AtmosphericFrameUniforms(
        Vector4 sunScreen,
        Vector4 sunColor,
        Vector4 viewport,
        Vector4 weather,
        Vector4 sunDirection,
        Vector4 policy,
        Matrix4x4 inverseViewProjection,
        Vector4 clockWind,
        Vector4 windAmplitude)
    {
        SunScreen = sunScreen;
        SunColor = sunColor;
        Viewport = viewport;
        Weather = weather;
        SunDirection = sunDirection;
        Policy = policy;
        InverseViewProjection = inverseViewProjection;
        ClockWind = clockWind;
        WindAmplitude = windAmplitude;
    }

    internal readonly Vector4 SunScreen;
    internal readonly Vector4 SunColor;
    internal readonly Vector4 Viewport;
    internal readonly Vector4 Weather;
    internal readonly Vector4 SunDirection;
    internal readonly Vector4 Policy;
    internal readonly Matrix4x4 InverseViewProjection;
    internal readonly Vector4 ClockWind;
    internal readonly Vector4 WindAmplitude;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct AtmosphericPackPassUniforms
{
    internal const int SizeInBytes = 64;

    internal AtmosphericPackPassUniforms(
        Vector4 params0,
        Vector4 params1,
        Vector4 params2,
        Vector4 params3)
    {
        Params0 = params0;
        Params1 = params1;
        Params2 = params2;
        Params3 = params3;
    }

    internal readonly Vector4 Params0;
    internal readonly Vector4 Params1;
    internal readonly Vector4 Params2;
    internal readonly Vector4 Params3;

    internal static AtmosphericPackPassUniforms From(Vector4 params0) =>
        new(params0, Vector4.Zero, Vector4.Zero, Vector4.Zero);
}

/// <summary>
/// Shader ABI SSOT for opt-in set 3 binding 8. API v1 exposes 64 scalar values in
/// descriptor declaration order, physically grouped as sixteen std140 vec4s.
/// </summary>
[InlineArray(RenderPackShaderAbi.PackSettingScalarCapacity)]
internal struct PackSettingsUniforms
{
    internal const int SizeInBytes = RenderPackShaderAbi.PackSettingsSizeBytes;
    private float _element0;

    internal static PackSettingsUniforms Create(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        IReadOnlyDictionary<string, string>? userSettingOverrides = null)
    {
        var result = new PackSettingsUniforms();
        int count = Math.Min(
            descriptor.Settings.Count,
            RenderPackShaderAbi.PackSettingScalarCapacity);
        for (int i = 0; i < count; i++)
        {
            RenderSettingDeclaration setting = descriptor.Settings[i];
            string value = RenderPackSettingResolution.Resolve(
                setting,
                preset,
                userSettingOverrides);
            result[i] = RenderPackSettingValueCodec.TryEncode(setting, value, out float encoded)
                ? encoded
                : 0f;
        }
        return result;
    }
}
