using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Rendering.Packs;

internal static class RenderPackCapabilityResolver
{
    internal const long AbsoluteResidentByteCeiling = 256L * 1024 * 1024;
    internal const long AbsoluteTransientByteCeiling = 512L * 1024 * 1024;
    internal const int DeviceLocalShareDenominator = 8;

    internal static RenderPackHostCapabilities Resolve(GpuCapabilityRecord gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var available = new HashSet<RenderCapability>
        {
            RenderCapability.FullscreenPasses,
            RenderCapability.AuthoredSunDirection,
            RenderCapability.AuthoredCelestialDirectionalLight,
            RenderCapability.AuthoredSunScreenPosition,
            RenderCapability.AuthoredWeather,
            RenderCapability.OutdoorDirectionalShadowCasterReplay,
            RenderCapability.AnimatedCasterTransforms,
            RenderCapability.AlphaCutoutShadowCasters,
        };

        if (gpu.SupportsRgba16FloatRenderTargets)
            available.Add(RenderCapability.MainWorldColorIntermediate);
        if (gpu.SupportsSampledDepth)
        {
            available.Add(RenderCapability.SceneDepthSampling);
            available.Add(RenderCapability.DirectionalShadowMaps);
        }
        if (gpu.SupportsTimestampQueries)
            available.Add(RenderCapability.GpuTimestampQueries);
        if (gpu.SupportsMultiview)
            available.Add(RenderCapability.MultiviewDirectionalShadowCascades);

        long residentBytes = DeviceLocalShare(
            gpu.DeviceLocalMemoryBytes,
            AbsoluteResidentByteCeiling);
        long transientBytes = DeviceLocalShare(
            gpu.DeviceLocalMemoryBytes,
            AbsoluteTransientByteCeiling);
        return new RenderPackHostCapabilities(
            available,
            MaxImageDimension2D: checked((int)Math.Min(
                gpu.MaxImageDimension2D,
                (uint)int.MaxValue)),
            MaxImageArrayLayers: checked((int)Math.Min(
                gpu.MaxImageArrayLayers,
                (uint)int.MaxValue)),
            MaxPackResidentBytes: residentBytes,
            MaxPackTransientBytes: transientBytes,
            MemoryPolicyDescription:
                $"one eighth of {gpu.DeviceLocalMemoryBytes} device-local bytes, "
                + $"capped at {AbsoluteResidentByteCeiling} resident and "
                + $"{AbsoluteTransientByteCeiling} transient bytes");
    }

    private static long DeviceLocalShare(ulong deviceLocalBytes, long ceiling)
    {
        ulong share = deviceLocalBytes / DeviceLocalShareDenominator;
        return (long)Math.Min(share, checked((ulong)ceiling));
    }
}
