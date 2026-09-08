using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct DirectionalShadowUniforms
{
    internal const int SizeInBytes = 336;

    public readonly Matrix4x4 WorldToClip0;
    public readonly Matrix4x4 WorldToClip1;
    public readonly Matrix4x4 WorldToClip2;
    public readonly Matrix4x4 WorldToClip3;
    public readonly Vector4 SplitFarMeters;
    public readonly Vector4 Control;
    public readonly Vector4 BiasMeters;
    public readonly UInt4 TextureAndFlags;
    public readonly Vector4 LightDirectionAndSource;

    internal DirectionalShadowUniforms(
        Matrix4x4 worldToClip0,
        Matrix4x4 worldToClip1,
        Matrix4x4 worldToClip2,
        Matrix4x4 worldToClip3,
        Vector4 splitFarMeters,
        Vector4 control,
        Vector4 biasMeters,
        UInt4 textureAndFlags,
        Vector4 lightDirectionAndSource)
    {
        WorldToClip0 = worldToClip0;
        WorldToClip1 = worldToClip1;
        WorldToClip2 = worldToClip2;
        WorldToClip3 = worldToClip3;
        SplitFarMeters = splitFarMeters;
        Control = control;
        BiasMeters = biasMeters;
        TextureAndFlags = textureAndFlags;
        LightDirectionAndSource = lightDirectionAndSource;
    }

    internal static DirectionalShadowUniforms Create(
        ReadOnlySpan<DirectionalShadowCascade> cascades,
        in DirectionalShadowEnvironmentState environment,
        in DirectionalShadowQuality quality,
        GpuTextureSlot textureSlot)
    {
        if (cascades.Length != quality.CascadeCount)
            throw new ArgumentException("The cascade span must match the selected quality.", nameof(cascades));
        if (!textureSlot.IsAssigned)
            throw new ArgumentException("The directional depth array requires an assigned texture slot.", nameof(textureSlot));

        Matrix4x4 matrix0 = cascades[0].WorldToShadowClip;
        Matrix4x4 matrix1 = cascades.Length > 1 ? cascades[1].WorldToShadowClip : Matrix4x4.Identity;
        Matrix4x4 matrix2 = cascades.Length > 2 ? cascades[2].WorldToShadowClip : Matrix4x4.Identity;
        Matrix4x4 matrix3 = cascades.Length > 3 ? cascades[3].WorldToShadowClip : Matrix4x4.Identity;
        float split0 = cascades[0].SplitFarMeters;
        float split1 = cascades.Length > 1 ? cascades[1].SplitFarMeters : quality.MaximumReachMeters;
        float split2 = cascades.Length > 2 ? cascades[2].SplitFarMeters : quality.MaximumReachMeters;
        float split3 = cascades.Length > 3 ? cascades[3].SplitFarMeters : quality.MaximumReachMeters;

        DirectionalShadowWorldBias bias = cascades[^1].Bias;
        float effectiveReachMeters = cascades[^1].SplitFarMeters;
        return new DirectionalShadowUniforms(
            matrix0,
            matrix1,
            matrix2,
            matrix3,
            new Vector4(split0, split1, split2, split3),
            new Vector4(
                environment.Strength,
                environment.SoftnessMultiplier,
                effectiveReachMeters,
                MathF.Max(1f, effectiveReachMeters * 0.02f)),
            new Vector4(
                bias.ConstantDepthMeters,
                bias.SlopeDepthMeters,
                bias.NormalOffsetMeters,
                cascades[0].CasterDepthPaddingMeters),
            new UInt4(
                textureSlot.Index,
                checked((uint)quality.CascadeCount),
                checked((uint)quality.MapResolution),
                1u | (checked((uint)quality.PcfRadiusTexels) << 8)),
            new Vector4(
                environment.SurfaceToLightDirection,
                checked((uint)environment.SourceKind)));
    }
}

/// <summary>Four uints with the exact 16-byte std140 uvec4 representation.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct UInt4(uint x, uint y, uint z, uint w)
{
    public readonly uint X = x;
    public readonly uint Y = y;
    public readonly uint Z = z;
    public readonly uint W = w;
}
