using AcDream.Plugin.Abstractions.Rendering;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Packs;

internal readonly record struct RenderPackResourceBudget(
    long RetainedGpuBytes,
    long MultisampleGpuBytes,
    int LargestImageWidth,
    int LargestImageHeight,
    int LargestImageLayerCount)
{
    internal long TotalGpuBytes => checked(RetainedGpuBytes + MultisampleGpuBytes);
}

internal static class RenderPackResourceBudgetPlanner
{
    private const int HdrColorBytesPerPixel = 8;
    private const int LdrColorBytesPerPixel = 4;
    private const int DirectionalDepthBytesPerPixel = 4;
    private const int MainWorldDepthBytesPerPixel = 4;
    private const int DirectionalShadowTransformFlightSlots = 2;

    internal static RenderPackResourceBudget Resolve(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        int mainWorldWidth,
        int mainWorldHeight,
        int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mainWorldWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mainWorldHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        long mainPixels = checked((long)mainWorldWidth * mainWorldHeight);
        long retained = checked(mainPixels
            * (HdrColorBytesPerPixel + MainWorldDepthBytesPerPixel));
        long multisample = sampleCount > 1
            ? checked(mainPixels
                * (HdrColorBytesPerPixel + MainWorldDepthBytesPerPixel)
                * sampleCount)
            : 0L;
        int largestWidth = mainWorldWidth;
        int largestHeight = mainWorldHeight;
        int largestLayers = 1;

        HashSet<string> writtenResources = descriptor.Passes
            .SelectMany(static pass => pass.ResourceWrites)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RenderResourceDeclaration resource in descriptor.Resources)
        {
            if (resource.Semantic == RenderResourceSemantic.MainWorldHdr
                || !writtenResources.Contains(resource.Id))
            {
                continue;
            }
            if (UsesFusedAtmosphericPostProcess(preset)
                && resource.Semantic is RenderResourceSemantic.BloomPing
                    or RenderResourceSemantic.BloomPong)
            {
                continue;
            }

            if (resource.Kind is not RenderResourceKind.Image2D
                and not RenderResourceKind.Image2DArray)
            {
                throw new NotSupportedException(
                    $"Resource '{resource.Id}' is not an API-v1 image resource.");
            }

            RenderQualityResourceOverride? resourceOverride = preset.ResourceOverrides
                .FirstOrDefault(value => string.Equals(
                    value.ResourceId,
                    resource.Id,
                    StringComparison.OrdinalIgnoreCase));
            RenderExtentDeclaration extent = resourceOverride?.Extent
                ?? resource.Extent
                ?? throw new NotSupportedException(
                    $"Image resource '{resource.Id}' has no extent.");
            (int width, int height) = ResolveExtent(
                resource.Id,
                extent,
                mainWorldWidth,
                mainWorldHeight);
            int layers = extent.Layers;
            if (layers <= 0)
            {
                throw new NotSupportedException(
                    $"Image resource '{resource.Id}' has no image layers.");
            }

            int bytesPerPixel = resource.Format switch
            {
                RenderFormatClass.HdrColor => HdrColorBytesPerPixel,
                RenderFormatClass.LdrColor or RenderFormatClass.SingleChannel =>
                    LdrColorBytesPerPixel,
                RenderFormatClass.DirectionalDepth => DirectionalDepthBytesPerPixel,
                _ => throw new NotSupportedException(
                    $"Image resource '{resource.Id}' has unsupported format "
                    + $"'{resource.Format}'."),
            };
            retained = checked(retained
                + ((long)width * height * layers * bytesPerPixel));
            largestWidth = Math.Max(largestWidth, width);
            largestHeight = Math.Max(largestHeight, height);
            largestLayers = Math.Max(largestLayers, layers);
        }

        if (descriptor.Passes.Any(pass =>
                pass.Semantic == RenderPassSemantic.DirectionalShadowDepth))
        {
            retained = checked(
                retained
                + DirectionalShadowTransformFlightSlots
                    * WorldTransformCapacityPolicy.InitialBindingSizeBytes);
        }

        return new RenderPackResourceBudget(
            retained,
            multisample,
            largestWidth,
            largestHeight,
            largestLayers);
    }

    private static bool UsesFusedAtmosphericPostProcess(
        RenderQualityPreset preset) =>
        (preset.ExecutionHints
            & RenderQualityExecutionHints.FusedAtmosphericPostProcess) != 0;

    internal static RenderPackResourceBudget RequireWithinPreset(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        int mainWorldWidth,
        int mainWorldHeight,
        int sampleCount)
    {
        RenderPackResourceBudget budget = Resolve(
            descriptor,
            preset,
            mainWorldWidth,
            mainWorldHeight,
            sampleCount);
        if (budget.RetainedGpuBytes > preset.MaxResidentGpuBytes)
        {
            throw new NotSupportedException(
                $"Render pack preset '{preset.Id}' needs "
                + $"{budget.RetainedGpuBytes} resident GPU bytes at "
                + $"{mainWorldWidth}x{mainWorldHeight}; its declared ceiling is "
                + $"{preset.MaxResidentGpuBytes}. Select a compatible preset or "
                + "reduce the main-world resolution.");
        }
        return budget;
    }

    internal static RenderPackResourceBudget RequireWithinHost(
        RenderPackDescriptor descriptor,
        RenderQualityPreset preset,
        int mainWorldWidth,
        int mainWorldHeight,
        int sampleCount,
        RenderPackHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        RenderPackResourceBudget budget = RequireWithinPreset(
            descriptor,
            preset,
            mainWorldWidth,
            mainWorldHeight,
            sampleCount);
        if (budget.LargestImageWidth > capabilities.MaxImageDimension2D
            || budget.LargestImageHeight > capabilities.MaxImageDimension2D)
        {
            throw new NotSupportedException(
                $"Render pack preset '{preset.Id}' resolves an image to "
                + $"{budget.LargestImageWidth}x{budget.LargestImageHeight} at "
                + $"{mainWorldWidth}x{mainWorldHeight}; this device's maximum "
                + $"2-D image edge is {capabilities.MaxImageDimension2D}.");
        }
        if (budget.LargestImageLayerCount > capabilities.MaxImageArrayLayers)
        {
            throw new NotSupportedException(
                $"Render pack preset '{preset.Id}' needs "
                + $"{budget.LargestImageLayerCount} image-array layers; this "
                + $"device provides {capabilities.MaxImageArrayLayers}.");
        }
        if (budget.RetainedGpuBytes > capabilities.MaxPackResidentBytes)
        {
            throw new NotSupportedException(
                $"Render pack preset '{preset.Id}' needs "
                + $"{budget.RetainedGpuBytes} resident GPU bytes at "
                + $"{mainWorldWidth}x{mainWorldHeight}; this host permits "
                + $"{capabilities.MaxPackResidentBytes} under its "
                + $"{capabilities.MemoryPolicyDescription} policy.");
        }
        if (budget.MultisampleGpuBytes > capabilities.MaxPackTransientBytes)
        {
            throw new NotSupportedException(
                $"Render pack preset '{preset.Id}' needs "
                + $"{budget.MultisampleGpuBytes} transient multisample GPU bytes "
                + $"at {mainWorldWidth}x{mainWorldHeight} x{sampleCount}; this "
                + $"host permits {capabilities.MaxPackTransientBytes} under its "
                + $"{capabilities.MemoryPolicyDescription} policy.");
        }
        return budget;
    }

    private static (int Width, int Height) ResolveExtent(
        string resourceId,
        RenderExtentDeclaration extent,
        int mainWorldWidth,
        int mainWorldHeight)
    {
        if (!double.IsFinite(extent.Width)
            || !double.IsFinite(extent.Height)
            || extent.Width <= 0d
            || extent.Height <= 0d)
        {
            throw new NotSupportedException(
                $"Image resource '{resourceId}' has an invalid extent.");
        }

        try
        {
            return extent.Mode switch
            {
                RenderExtentMode.AbsolutePixels =>
                    (checked((int)extent.Width), checked((int)extent.Height)),
                RenderExtentMode.RelativeToMainWorld or RenderExtentMode.RelativeToOutput =>
                    (Math.Max(1, checked((int)Math.Ceiling(mainWorldWidth * extent.Width))),
                     Math.Max(1, checked((int)Math.Ceiling(mainWorldHeight * extent.Height)))),
                _ => throw new NotSupportedException(
                    $"Image resource '{resourceId}' has unsupported extent mode "
                    + $"'{extent.Mode}'."),
            };
        }
        catch (OverflowException error)
        {
            throw new NotSupportedException(
                $"Image resource '{resourceId}' extent overflows the host image range.",
                error);
        }
    }
}
