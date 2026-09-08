using AcDream.App.Rendering;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed record VulkanSwapchainConfiguration(
    Format ImageFormat,
    ColorSpaceKHR ColorSpace,
    PresentModeKHR PresentMode,
    uint ImageCount,
    uint Width,
    uint Height,
    ImageUsageFlags Usage,
    SurfaceTransformFlagsKHR PreTransform,
    CompositeAlphaFlagsKHR CompositeAlpha)
{
    internal bool IsPresentable => Width > 0 && Height > 0;
}

internal static class VulkanSwapchainConfigurationFactory
{
    internal const Format PreferredFormat = Format.B8G8R8A8Unorm;

    internal const ColorSpaceKHR PreferredColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;

    /// <summary>
    /// Two frames in flight (plan §4.8), so three images is the working target:
    /// one presenting, one queued, one being recorded.
    /// </summary>
    internal const uint PreferredImageCount = 3;

    internal const ImageUsageFlags RequiredUsage =
        ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit;

    internal static SurfaceFormatKHR ChooseSurfaceFormat(
        IReadOnlyList<SurfaceFormatKHR> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        if (available.Count == 0)
            return new SurfaceFormatKHR(PreferredFormat, PreferredColorSpace);

        foreach (SurfaceFormatKHR format in available)
        {
            if (format.Format == PreferredFormat && format.ColorSpace == PreferredColorSpace)
                return format;
        }

        foreach (SurfaceFormatKHR format in available)
        {
            if (format.Format == PreferredFormat)
                return format;
        }

        return available[0];
    }

    /// <summary>True when the surface offers the UNORM format the renderer requires.</summary>
    internal static bool OffersUnormFormat(IReadOnlyList<SurfaceFormatKHR> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        foreach (SurfaceFormatKHR format in available)
        {
            if (format.Format == PreferredFormat)
                return true;
        }

        return false;
    }

    internal static PresentModeKHR ChoosePresentMode(
        FramePacingPolicy pacing,
        IReadOnlyList<PresentModeKHR> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        if (pacing.UseVSync)
            return PresentModeKHR.FifoKhr;

        if (available.Contains(PresentModeKHR.ImmediateKhr))
            return PresentModeKHR.ImmediateKhr;
        if (available.Contains(PresentModeKHR.MailboxKhr))
            return PresentModeKHR.MailboxKhr;

        // FIFO is the only mode Vulkan guarantees is present.
        return PresentModeKHR.FifoKhr;
    }

    internal static uint ChooseImageCount(in SurfaceCapabilitiesKHR capabilities)
    {
        uint count = Math.Max(PreferredImageCount, capabilities.MinImageCount);
        if (capabilities.MaxImageCount != 0 && count > capabilities.MaxImageCount)
            count = capabilities.MaxImageCount;
        return count;
    }

    internal static (uint Width, uint Height) ChooseExtent(
        in SurfaceCapabilitiesKHR capabilities,
        uint framebufferWidth,
        uint framebufferHeight)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return (capabilities.CurrentExtent.Width, capabilities.CurrentExtent.Height);

        uint width = Math.Clamp(
            framebufferWidth,
            capabilities.MinImageExtent.Width,
            capabilities.MaxImageExtent.Width);
        uint height = Math.Clamp(
            framebufferHeight,
            capabilities.MinImageExtent.Height,
            capabilities.MaxImageExtent.Height);
        return (width, height);
    }

    internal static SurfaceTransformFlagsKHR ChoosePreTransform(
        in SurfaceCapabilitiesKHR capabilities)
        => capabilities.SupportedTransforms.HasFlag(SurfaceTransformFlagsKHR.IdentityBitKhr)
            ? SurfaceTransformFlagsKHR.IdentityBitKhr
            : capabilities.CurrentTransform;

    internal static CompositeAlphaFlagsKHR ChooseCompositeAlpha(
        in SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.SupportedCompositeAlpha.HasFlag(CompositeAlphaFlagsKHR.OpaqueBitKhr))
            return CompositeAlphaFlagsKHR.OpaqueBitKhr;
        if (capabilities.SupportedCompositeAlpha.HasFlag(CompositeAlphaFlagsKHR.InheritBitKhr))
            return CompositeAlphaFlagsKHR.InheritBitKhr;
        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    /// <summary>True when the surface permits the TRANSFER_SRC usage screenshots need.</summary>
    internal static bool SupportsTransferSource(in SurfaceCapabilitiesKHR capabilities)
        => capabilities.SupportedUsageFlags.HasFlag(ImageUsageFlags.TransferSrcBit);

    internal static VulkanSwapchainConfiguration Create(
        in SurfaceCapabilitiesKHR capabilities,
        IReadOnlyList<SurfaceFormatKHR> formats,
        IReadOnlyList<PresentModeKHR> presentModes,
        FramePacingPolicy pacing,
        uint framebufferWidth,
        uint framebufferHeight)
    {
        SurfaceFormatKHR format = ChooseSurfaceFormat(formats);
        (uint width, uint height) = ChooseExtent(
            capabilities,
            framebufferWidth,
            framebufferHeight);

        ImageUsageFlags usage = SupportsTransferSource(capabilities)
            ? RequiredUsage
            : ImageUsageFlags.ColorAttachmentBit;

        return new VulkanSwapchainConfiguration(
            format.Format,
            format.ColorSpace,
            ChoosePresentMode(pacing, presentModes),
            ChooseImageCount(capabilities),
            width,
            height,
            usage,
            ChoosePreTransform(capabilities),
            ChooseCompositeAlpha(capabilities));
    }
}

/// <summary>What the frame loop must do after an acquire or a present returned.</summary>
internal enum VulkanSwapchainAction
{
    Continue,

    /// <summary>Rebuild the swapchain before doing anything else with it.</summary>
    RecreateNow,

    /// <summary>Usable this frame, but rebuild at the frame boundary.</summary>
    RecreateAtFrameBoundary,

    /// <summary>Nothing to present to — a minimised window. Idle without spinning.</summary>
    Idle,

    Fail,
}

internal static class VulkanSwapchainRecreationPolicy
{
    internal static VulkanSwapchainAction OnAcquire(Result result) => result switch
    {
        Result.Success => VulkanSwapchainAction.Continue,
        // A suboptimal acquire is still a usable image, so the frame renders and
        // the rebuild happens at the boundary rather than mid-frame.
        Result.SuboptimalKhr => VulkanSwapchainAction.RecreateAtFrameBoundary,
        Result.ErrorOutOfDateKhr => VulkanSwapchainAction.RecreateNow,
        Result.Timeout or Result.NotReady => VulkanSwapchainAction.Idle,
        _ => VulkanSwapchainAction.Fail,
    };

    internal static VulkanSwapchainAction OnPresent(Result result) => result switch
    {
        Result.Success => VulkanSwapchainAction.Continue,
        Result.SuboptimalKhr => VulkanSwapchainAction.RecreateAtFrameBoundary,
        Result.ErrorOutOfDateKhr => VulkanSwapchainAction.RecreateNow,
        _ => VulkanSwapchainAction.Fail,
    };

    internal static VulkanSwapchainAction OnFramebufferSize(uint width, uint height)
        => width == 0 || height == 0
            ? VulkanSwapchainAction.Idle
            : VulkanSwapchainAction.Continue;
}

internal static class VulkanBackbufferSwizzle
{
    /// <summary>
    /// Swap the red and blue bytes of every 4-byte pixel in place. Alpha and
    /// green are untouched, so the transform is its own inverse.
    /// </summary>
    internal static void SwapRedAndBlueInPlace(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException(
                "A BGRA/RGBA pixel span must be a whole number of 4-byte pixels.",
                nameof(pixels));
        }

        for (int i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    internal static byte[] ToRgba(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceRowPitchBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRowPitchBytes, width * 4);

        var destination = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> sourceRow =
                source.Slice(y * sourceRowPitchBytes, width * 4);
            Span<byte> destinationRow =
                destination.AsSpan(y * width * 4, width * 4);
            sourceRow.CopyTo(destinationRow);
            SwapRedAndBlueInPlace(destinationRow);
        }

        return destination;
    }

    internal static byte[] ToGlOriginRgba(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int sourceRowPitchBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRowPitchBytes, width * 4);

        var destination = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> sourceRow =
                source.Slice(y * sourceRowPitchBytes, width * 4);
            Span<byte> destinationRow =
                destination.AsSpan((height - 1 - y) * width * 4, width * 4);
            sourceRow.CopyTo(destinationRow);
            SwapRedAndBlueInPlace(destinationRow);
        }

        return destination;
    }
}
