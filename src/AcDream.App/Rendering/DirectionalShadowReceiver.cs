using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

internal readonly record struct DirectionalShadowFrameBinding(
    long FrameSerial,
    bool Enabled,
    IGpuBuffer? Buffer,
    uint OffsetBytes,
    uint SizeBytes,
    GpuTextureSlot TextureSlot,
    int CascadeCount,
    AtmosphericFrameBufferBinding AtmosphericFrame = default)
{
    internal static DirectionalShadowFrameBinding Disabled => default;

    internal bool IsBindableFor(IGpuFrame frame) =>
        Buffer is not null
        && FrameSerial == frame.Serial
        && SizeBytes == DirectionalShadowUniforms.SizeInBytes;

    internal bool IsValidFor(IGpuFrame frame) =>
        IsBindableFor(frame)
        && Enabled
        && TextureSlot.IsAssigned
        && CascadeCount is >= 2 and <= 4;
}

/// <summary>
/// Receiver-side seam. A pack runtime may publish this source at a stable frame
/// boundary without exposing the producer's target or ref-struct allocation.
/// </summary>
internal interface IDirectionalShadowReceiverSource
{
    DirectionalShadowPipelineShaders PipelineShaders { get; }

    bool TryGetCurrentFrameBinding(
        IGpuFrame frame,
        out DirectionalShadowFrameBinding binding);
}

internal readonly record struct DirectionalShadowPipelineShaders(
    GpuShaderSet TerrainCaster,
    GpuShaderSet WorldOpaqueCaster,
    GpuShaderSet WorldAlphaCutoutCaster,
    GpuShaderSet TerrainReceiver,
    GpuShaderSet WorldReceiver)
{
    internal DirectionalShadowMultiviewPipelineShaders? MultiviewCasters { get; init; }

    internal static DirectionalShadowPipelineShaders Local { get; } = new(
        new GpuShaderSet("directional_shadow_terrain"),
        new GpuShaderSet("directional_shadow_world_opaque"),
        new GpuShaderSet("directional_shadow_world_cutout"),
        new GpuShaderSet("terrain_atmospheric"),
        new GpuShaderSet("mesh_atmospheric"))
    {
        MultiviewCasters = new DirectionalShadowMultiviewPipelineShaders(
            new GpuShaderSet("directional_shadow_terrain_multiview"),
            new GpuShaderSet("directional_shadow_world_opaque_multiview"),
            new GpuShaderSet("directional_shadow_world_cutout_multiview")),
    };
}

internal readonly record struct DirectionalShadowMultiviewPipelineShaders(
    GpuShaderSet TerrainCaster,
    GpuShaderSet WorldOpaqueCaster,
    GpuShaderSet WorldAlphaCutoutCaster);

internal readonly record struct DirectionalShadowCascadeBlend(
    int PrimaryCascade,
    int SecondaryCascade,
    float SecondaryWeight,
    bool WithinShadowReach);

internal static class DirectionalShadowReceiverPolicy
{
    internal const string AtmosphericWorldPassName = "atmospheric-world-hdr";

    internal static bool ShouldSelectReceiverPipeline(
        string passName,
        bool sourcePresent,
        bool bindingValid) =>
        sourcePresent
        && bindingValid
        && string.Equals(
            passName,
            AtmosphericWorldPassName,
            StringComparison.Ordinal);

    internal static DirectionalShadowCascadeBlend SelectCascade(
        float cameraDistanceMeters,
        Vector4 splitFarMeters,
        int cascadeCount,
        float blendWidthMeters)
    {
        if (!float.IsFinite(cameraDistanceMeters) || cameraDistanceMeters < 0f)
            throw new ArgumentOutOfRangeException(nameof(cameraDistanceMeters));
        if (cascadeCount is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(cascadeCount));
        if (!float.IsFinite(blendWidthMeters) || blendWidthMeters < 0f)
            throw new ArgumentOutOfRangeException(nameof(blendWidthMeters));

        Span<float> splits = stackalloc float[4]
        {
            splitFarMeters.X,
            splitFarMeters.Y,
            splitFarMeters.Z,
            splitFarMeters.W,
        };
        for (int i = 0; i < cascadeCount; i++)
        {
            if (!float.IsFinite(splits[i])
                || splits[i] <= 0f
                || (i > 0 && splits[i] < splits[i - 1]))
            {
                throw new ArgumentException(
                    "Directional-shadow split distances must be finite, positive, and monotonic.",
                    nameof(splitFarMeters));
            }
        }

        int primary = 0;
        while (primary < cascadeCount && cameraDistanceMeters > splits[primary])
            primary++;
        if (primary == cascadeCount)
            return new DirectionalShadowCascadeBlend(cascadeCount - 1, cascadeCount - 1, 0f, false);

        if (primary == cascadeCount - 1 || blendWidthMeters <= 0f)
            return new DirectionalShadowCascadeBlend(primary, primary, 0f, true);

        float blendStart = MathF.Max(0f, splits[primary] - blendWidthMeters);
        float t = Math.Clamp(
            (cameraDistanceMeters - blendStart) / MathF.Max(blendWidthMeters, 1e-6f),
            0f,
            1f);
        float smooth = t * t * (3f - 2f * t);
        return new DirectionalShadowCascadeBlend(primary, primary + 1, smooth, true);
    }

    internal static float ReceiverBiasMeters(
        in DirectionalShadowWorldBias bias,
        float normalDotSurfaceToLight) =>
        bias.ConstantDepthMeters
        + bias.SlopeDepthMeters * (1f - Math.Clamp(normalDotSurfaceToLight, 0f, 1f));

    internal static bool ShouldSample(
        bool bindingEnabled,
        bool indoor,
        bool hasSelectedCelestialDirectionalLight) =>
        bindingEnabled && !indoor && hasSelectedCelestialDirectionalLight;
}
