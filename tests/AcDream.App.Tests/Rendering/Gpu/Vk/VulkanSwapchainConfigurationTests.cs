using System;
using System.Collections.Generic;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanSwapchainConfigurationTests
{
    private static SurfaceCapabilitiesKHR Capabilities(
        uint minImages = 2,
        uint maxImages = 8,
        uint currentWidth = 1280,
        uint currentHeight = 720,
        uint minWidth = 1,
        uint minHeight = 1,
        uint maxWidth = 16384,
        uint maxHeight = 16384,
        ImageUsageFlags usage =
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
        SurfaceTransformFlagsKHR supportedTransforms = SurfaceTransformFlagsKHR.IdentityBitKhr,
        SurfaceTransformFlagsKHR currentTransform = SurfaceTransformFlagsKHR.IdentityBitKhr,
        CompositeAlphaFlagsKHR compositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr)
        => new()
        {
            MinImageCount = minImages,
            MaxImageCount = maxImages,
            CurrentExtent = new Extent2D(currentWidth, currentHeight),
            MinImageExtent = new Extent2D(minWidth, minHeight),
            MaxImageExtent = new Extent2D(maxWidth, maxHeight),
            MaxImageArrayLayers = 1,
            SupportedTransforms = supportedTransforms,
            CurrentTransform = currentTransform,
            SupportedCompositeAlpha = compositeAlpha,
            SupportedUsageFlags = usage,
        };

    /// <summary>
    /// The single highest-severity finding of the V3 audit. acdream is plain
    /// UNORM end to end; an <c>_SRGB</c> swapchain would encode already
    /// display-space values and brighten every frame globally, and it would have
    /// passed silently right up to the V7 differential.
    /// </summary>
    [Fact]
    public void TheSwapchainFormatIsUnormAndNeverSrgb()
    {
        Assert.Equal(
            Format.B8G8R8A8Unorm,
            VulkanSwapchainConfigurationFactory.PreferredFormat);

        SurfaceFormatKHR chosen = VulkanSwapchainConfigurationFactory.ChooseSurfaceFormat(
        [
            new SurfaceFormatKHR(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        ]);

        Assert.Equal(Format.B8G8R8A8Unorm, chosen.Format);
        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, chosen.ColorSpace);
    }

    [Fact]
    public void ASurfaceOfferingOnlySrgbIsDetectedAsMissingUnorm()
    {
        IReadOnlyList<SurfaceFormatKHR> srgbOnly =
        [
            new SurfaceFormatKHR(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        ];

        Assert.False(VulkanSwapchainConfigurationFactory.OffersUnormFormat(srgbOnly));
        Assert.True(
            VulkanSwapchainConfigurationFactory.OffersUnormFormat(
            [
                new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            ]));
    }

    [Fact]
    public void VSyncOnAlwaysSelectsFifo()
    {
        FramePacingPolicy pacing = FramePacingPolicy.Resolve(
            requestedVSync: true,
            uncappedRendering: false,
            monitorRefreshHz: 144);

        Assert.True(pacing.UseVSync);
        Assert.Equal(
            PresentModeKHR.FifoKhr,
            VulkanSwapchainConfigurationFactory.ChoosePresentMode(
                pacing,
                [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr]));
    }

    [Fact]
    public void VSyncOffPrefersImmediateThenMailbox()
    {
        FramePacingPolicy softwareCapped = FramePacingPolicy.Resolve(
            requestedVSync: false,
            uncappedRendering: false,
            monitorRefreshHz: 144);
        Assert.False(softwareCapped.UseVSync);
        Assert.Equal(144d, softwareCapped.SoftwareLimitHz);

        Assert.Equal(
            PresentModeKHR.ImmediateKhr,
            VulkanSwapchainConfigurationFactory.ChoosePresentMode(
                softwareCapped,
                [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr]));

        Assert.Equal(
            PresentModeKHR.MailboxKhr,
            VulkanSwapchainConfigurationFactory.ChoosePresentMode(
                softwareCapped,
                [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr]));
    }

    [Fact]
    public void UncappedRenderingAlsoTakesImmediate()
    {
        FramePacingPolicy uncapped = FramePacingPolicy.Resolve(
            requestedVSync: true,
            uncappedRendering: true,
            monitorRefreshHz: 144);

        Assert.False(uncapped.UseVSync);
        Assert.Null(uncapped.SoftwareLimitHz);
        Assert.Equal(
            PresentModeKHR.ImmediateKhr,
            VulkanSwapchainConfigurationFactory.ChoosePresentMode(
                uncapped,
                [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr]));
    }

    /// <summary>FIFO is the only present mode Vulkan guarantees exists.</summary>
    [Fact]
    public void FifoIsTheFallbackWhenNothingElseIsOffered()
    {
        FramePacingPolicy pacing = FramePacingPolicy.Resolve(false, false, 60);

        Assert.Equal(
            PresentModeKHR.FifoKhr,
            VulkanSwapchainConfigurationFactory.ChoosePresentMode(
                pacing,
                [PresentModeKHR.FifoKhr]));
    }

    [Fact]
    public void ThreeImagesAreRequestedForTwoFramesInFlight()
    {
        Assert.Equal(3u, VulkanSwapchainConfigurationFactory.PreferredImageCount);
        Assert.Equal(
            3u,
            VulkanSwapchainConfigurationFactory.ChooseImageCount(Capabilities()));
    }

    [Fact]
    public void TheImageCountIsRaisedToTheSurfaceMinimum()
    {
        Assert.Equal(
            5u,
            VulkanSwapchainConfigurationFactory.ChooseImageCount(
                Capabilities(minImages: 5, maxImages: 8)));
    }

    [Fact]
    public void AZeroMaximumImageCountMeansNoUpperLimit()
    {
        Assert.Equal(
            3u,
            VulkanSwapchainConfigurationFactory.ChooseImageCount(
                Capabilities(minImages: 2, maxImages: 0)));
    }

    [Fact]
    public void TheImageCountIsClampedToTheSurfaceMaximum()
    {
        Assert.Equal(
            2u,
            VulkanSwapchainConfigurationFactory.ChooseImageCount(
                Capabilities(minImages: 1, maxImages: 2)));
    }

    [Fact]
    public void TheSurfacesOwnExtentWinsWhenItReportsOne()
    {
        (uint width, uint height) = VulkanSwapchainConfigurationFactory.ChooseExtent(
            Capabilities(currentWidth: 2560, currentHeight: 1440),
            framebufferWidth: 1280,
            framebufferHeight: 720);

        Assert.Equal(2560u, width);
        Assert.Equal(1440u, height);
    }

    [Fact]
    public void TheFramebufferSizeIsUsedAndClampedWhenTheSurfaceDefers()
    {
        (uint width, uint height) = VulkanSwapchainConfigurationFactory.ChooseExtent(
            Capabilities(
                currentWidth: uint.MaxValue,
                currentHeight: uint.MaxValue,
                minWidth: 64,
                minHeight: 64,
                maxWidth: 1024,
                maxHeight: 1024),
            framebufferWidth: 4000,
            framebufferHeight: 32);

        Assert.Equal(1024u, width);
        Assert.Equal(64u, height);
    }

    [Fact]
    public void AMinimisedWindowProducesAZeroExtentThatIsNotPresentable()
    {
        VulkanSwapchainConfiguration configuration =
            VulkanSwapchainConfigurationFactory.Create(
                Capabilities(currentWidth: 0, currentHeight: 0),
                [new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)],
                [PresentModeKHR.FifoKhr],
                FramePacingPolicy.Resolve(true, false, 60),
                framebufferWidth: 0,
                framebufferHeight: 0);

        Assert.False(configuration.IsPresentable);
    }

    [Fact]
    public void TheUsageIncludesColourAttachmentAndTransferSource()
    {
        Assert.Equal(
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            VulkanSwapchainConfigurationFactory.RequiredUsage);

        VulkanSwapchainConfiguration configuration =
            VulkanSwapchainConfigurationFactory.Create(
                Capabilities(),
                [new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)],
                [PresentModeKHR.FifoKhr],
                FramePacingPolicy.Resolve(true, false, 60),
                1280,
                720);

        Assert.True(configuration.Usage.HasFlag(ImageUsageFlags.TransferSrcBit));
        Assert.True(configuration.Usage.HasFlag(ImageUsageFlags.ColorAttachmentBit));
        Assert.True(configuration.IsPresentable);
    }

    [Fact]
    public void AnUnsupportedTransferSourceUsageIsDroppedRatherThanForced()
    {
        SurfaceCapabilitiesKHR capabilities =
            Capabilities(usage: ImageUsageFlags.ColorAttachmentBit);

        Assert.False(VulkanSwapchainConfigurationFactory.SupportsTransferSource(capabilities));

        VulkanSwapchainConfiguration configuration =
            VulkanSwapchainConfigurationFactory.Create(
                capabilities,
                [new SurfaceFormatKHR(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr)],
                [PresentModeKHR.FifoKhr],
                FramePacingPolicy.Resolve(true, false, 60),
                1280,
                720);

        Assert.False(configuration.Usage.HasFlag(ImageUsageFlags.TransferSrcBit));
    }

    [Fact]
    public void IdentityPreTransformIsPreferredAndTheCurrentOneIsTheFallback()
    {
        Assert.Equal(
            SurfaceTransformFlagsKHR.IdentityBitKhr,
            VulkanSwapchainConfigurationFactory.ChoosePreTransform(Capabilities()));

        Assert.Equal(
            SurfaceTransformFlagsKHR.Rotate90BitKhr,
            VulkanSwapchainConfigurationFactory.ChoosePreTransform(
                Capabilities(
                    supportedTransforms: SurfaceTransformFlagsKHR.Rotate90BitKhr,
                    currentTransform: SurfaceTransformFlagsKHR.Rotate90BitKhr)));
    }

    [Fact]
    public void CompositeAlphaPrefersOpaqueAndFallsBackToInherit()
    {
        Assert.Equal(
            CompositeAlphaFlagsKHR.OpaqueBitKhr,
            VulkanSwapchainConfigurationFactory.ChooseCompositeAlpha(Capabilities()));

        Assert.Equal(
            CompositeAlphaFlagsKHR.InheritBitKhr,
            VulkanSwapchainConfigurationFactory.ChooseCompositeAlpha(
                Capabilities(compositeAlpha: CompositeAlphaFlagsKHR.InheritBitKhr)));
    }

    /// <summary>
    /// Plan §4.9: "OUT_OF_DATE recreates immediately, SUBOPTIMAL at the next
    /// frame boundary." A suboptimal image is still renderable, so rebuilding
    /// mid-frame would throw away work for nothing.
    /// </summary>
    [Fact]
    public void AcquireResultsMapToTheDocumentedRecreationTiming()
    {
        Assert.Equal(
            VulkanSwapchainAction.Continue,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.Success));
        Assert.Equal(
            VulkanSwapchainAction.RecreateNow,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.ErrorOutOfDateKhr));
        Assert.Equal(
            VulkanSwapchainAction.RecreateAtFrameBoundary,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.SuboptimalKhr));
        Assert.Equal(
            VulkanSwapchainAction.Idle,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.Timeout));
        Assert.Equal(
            VulkanSwapchainAction.Idle,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.NotReady));
        Assert.Equal(
            VulkanSwapchainAction.Fail,
            VulkanSwapchainRecreationPolicy.OnAcquire(Result.ErrorDeviceLost));
    }

    [Fact]
    public void PresentResultsMapToTheDocumentedRecreationTiming()
    {
        Assert.Equal(
            VulkanSwapchainAction.Continue,
            VulkanSwapchainRecreationPolicy.OnPresent(Result.Success));
        Assert.Equal(
            VulkanSwapchainAction.RecreateNow,
            VulkanSwapchainRecreationPolicy.OnPresent(Result.ErrorOutOfDateKhr));
        Assert.Equal(
            VulkanSwapchainAction.RecreateAtFrameBoundary,
            VulkanSwapchainRecreationPolicy.OnPresent(Result.SuboptimalKhr));
        Assert.Equal(
            VulkanSwapchainAction.Fail,
            VulkanSwapchainRecreationPolicy.OnPresent(Result.ErrorSurfaceLostKhr));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0u, 720u)]
    [InlineData(1280u, 0u)]
    public void AZeroAreaFramebufferIsIdleRatherThanAFailedCreate(uint width, uint height)
    {
        Assert.Equal(
            VulkanSwapchainAction.Idle,
            VulkanSwapchainRecreationPolicy.OnFramebufferSize(width, height));
    }

    [Fact]
    public void ANonZeroFramebufferContinues()
    {
        Assert.Equal(
            VulkanSwapchainAction.Continue,
            VulkanSwapchainRecreationPolicy.OnFramebufferSize(1280, 720));
    }

    [Fact]
    public void TheBgraToRgbaSwizzleSwapsOnlyRedAndBlue()
    {
        byte[] bgra = [0x10, 0x20, 0x30, 0x40, 0x01, 0x02, 0x03, 0x04];

        VulkanBackbufferSwizzle.SwapRedAndBlueInPlace(bgra);

        Assert.Equal([0x30, 0x20, 0x10, 0x40, 0x03, 0x02, 0x01, 0x04], bgra);
    }

    [Fact]
    public void TheSwizzleIsItsOwnInverse()
    {
        byte[] pixels = [0x10, 0x20, 0x30, 0x40];
        byte[] original = [.. pixels];

        VulkanBackbufferSwizzle.SwapRedAndBlueInPlace(pixels);
        VulkanBackbufferSwizzle.SwapRedAndBlueInPlace(pixels);

        Assert.Equal(original, pixels);
    }

    [Fact]
    public void APartialPixelSpanIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => VulkanBackbufferSwizzle.SwapRedAndBlueInPlace(new byte[6]));
    }

    /// <summary>
    /// Vulkan reports a row pitch per image layout that is frequently larger than
    /// <c>width * 4</c>; reading it as tight produces a diagonally sheared image.
    /// </summary>
    [Fact]
    public void RowPaddingIsHonouredWhenConvertingToRgba()
    {
        // 2x2 BGRA with 4 bytes of padding per row.
        byte[] source =
        [
            0x01, 0x02, 0x03, 0xFF, 0x11, 0x12, 0x13, 0xFF, 0xAA, 0xAA, 0xAA, 0xAA,
            0x21, 0x22, 0x23, 0xFF, 0x31, 0x32, 0x33, 0xFF, 0xAA, 0xAA, 0xAA, 0xAA,
        ];

        byte[] rgba = VulkanBackbufferSwizzle.ToRgba(source, 2, 2, sourceRowPitchBytes: 12);

        Assert.Equal(
            [
                0x03, 0x02, 0x01, 0xFF, 0x13, 0x12, 0x11, 0xFF,
                0x23, 0x22, 0x21, 0xFF, 0x33, 0x32, 0x31, 0xFF,
            ],
            rgba);
    }

    [Fact]
    public void TheGlOriginConversionFlipsRowsAsWellAsSwizzling()
    {
        byte[] source =
        [
            0x01, 0x02, 0x03, 0xFF,
            0x21, 0x22, 0x23, 0xFF,
        ];

        byte[] rgba = VulkanBackbufferSwizzle.ToGlOriginRgba(
            source,
            width: 1,
            height: 2,
            sourceRowPitchBytes: 4);

        Assert.Equal(
            [
                0x23, 0x22, 0x21, 0xFF,
                0x03, 0x02, 0x01, 0xFF,
            ],
            rgba);
    }

    [Fact]
    public void ARowPitchNarrowerThanTheImageIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VulkanBackbufferSwizzle.ToRgba(new byte[16], 2, 2, sourceRowPitchBytes: 4));
    }

    [Fact]
    public void TheBringUpClearColourIsNeitherBlackNorTheMagentaSentinel()
    {
        float[] colour = VulkanBringUpHost.ClearColor;

        Assert.Equal(4, colour.Length);
        Assert.Equal(1f, colour[3]);
        Assert.True(colour[0] + colour[1] + colour[2] > 0f);
        Assert.False(colour[0] == 1f && colour[1] == 0f && colour[2] == 1f);
        Assert.Equal(2, VulkanBringUpHost.FlightCount);
    }
}
