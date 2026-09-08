using System;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanViewportMappingTests
{
    [Fact]
    public void FullViewportBecomesANegativeHeightRectangleAnchoredAtTheBottom()
    {
        Viewport viewport = VulkanViewportMapping.ToVulkan(0, 0, 1280, 720, attachmentHeight: 720);

        Assert.Equal(0f, viewport.X);
        Assert.Equal(720f, viewport.Y);
        Assert.Equal(1280f, viewport.Width);
        Assert.Equal(-720f, viewport.Height);
        Assert.Equal(0f, viewport.MinDepth);
        Assert.Equal(1f, viewport.MaxDepth);
    }

    [Fact]
    public void AnOffsetViewportKeepsItsGlBottomLeftMeaning()
    {
        // A 100x50 viewport whose bottom edge sits 30 px above the bottom of a
        // 720 px attachment.
        Viewport viewport = VulkanViewportMapping.ToVulkan(10, 30, 100, 50, attachmentHeight: 720);

        Assert.Equal(10f, viewport.X);
        Assert.Equal(690f, viewport.Y);
        Assert.Equal(-50f, viewport.Height);
    }

    [Fact]
    public void ScissorFlipsAgainstTheAttachmentBecauseTheViewportSignDoesNotDoItForUs()
    {
        // GL rectangle: 100 px wide, 50 px tall, bottom edge 30 px up.
        Rect2D scissor = VulkanViewportMapping.ScissorToVulkan(10, 30, 100, 50, attachmentHeight: 720);

        Assert.Equal(10, scissor.Offset.X);
        // Top edge measured from the top: 720 - (30 + 50).
        Assert.Equal(640, scissor.Offset.Y);
        Assert.Equal(100u, scissor.Extent.Width);
        Assert.Equal(50u, scissor.Extent.Height);
    }

    [Fact]
    public void AFullAttachmentScissorIsUnchangedByTheFlip()
    {
        Rect2D scissor = VulkanViewportMapping.ScissorToVulkan(0, 0, 1280, 720, attachmentHeight: 720);

        Assert.Equal(0, scissor.Offset.X);
        Assert.Equal(0, scissor.Offset.Y);
        Assert.Equal(1280u, scissor.Extent.Width);
        Assert.Equal(720u, scissor.Extent.Height);
    }

    [Fact]
    public void AScissorStraddlingTheTopEdgeIsClampedRatherThanRejected()
    {
        Rect2D scissor = VulkanViewportMapping.ScissorToVulkan(0, 700, 100, 50, attachmentHeight: 720);

        Assert.Equal(0, scissor.Offset.Y);
        Assert.Equal(20u, scissor.Extent.Height);
    }

    [Fact]
    public void FrontFacePassesThroughSoRenderersDeclareTheGlWinding()
    {
        Assert.Equal(
            FrontFace.CounterClockwise,
            VulkanViewportMapping.ToVulkan(GpuFrontFace.CounterClockwise));
        Assert.Equal(
            FrontFace.Clockwise,
            VulkanViewportMapping.ToVulkan(GpuFrontFace.Clockwise));
    }

    [Fact]
    public void TheViewportFlipTravelsAloneAndTheWindingIsUntouched()
    {
        Viewport once = VulkanViewportMapping.ToVulkan(0, 0, 640, 480, attachmentHeight: 480);
        Assert.Equal(-480f, once.Height);
        Assert.Equal(480f, once.Y);

        Assert.Equal(
            FrontFace.CounterClockwise,
            VulkanViewportMapping.ToVulkan(GpuFrontFace.CounterClockwise));
    }

    [Fact]
    public void CullModesMapStraightAcross()
    {
        Assert.Equal(CullModeFlags.None, VulkanViewportMapping.ToVulkan(GpuCullMode.None));
        Assert.Equal(CullModeFlags.BackBit, VulkanViewportMapping.ToVulkan(GpuCullMode.Back));
        Assert.Equal(CullModeFlags.FrontBit, VulkanViewportMapping.ToVulkan(GpuCullMode.Front));
    }

    [Fact]
    public void AllRetailBlendModesAreRepresentable()
    {
        Assert.Equal(
            (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.StraightAlpha));
        Assert.Equal(
            (BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.PremultipliedAlpha));
        Assert.Equal(
            (BlendFactor.SrcAlpha, BlendFactor.One),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.Additive));
        Assert.Equal(
            (BlendFactor.One, BlendFactor.One),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.RawAdditive));
        Assert.Equal(
            (BlendFactor.OneMinusSrcAlpha, BlendFactor.One),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.InverseAdditive));
        Assert.Equal(
            (BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha),
            VulkanViewportMapping.BlendFactorsOf(GpuBlendMode.InverseAlpha));
    }

    [Fact]
    public void IntegerVertexAttributesTakeAUintFormatNotANormalisedOne()
    {
        Assert.Equal(Format.R8G8B8A8Unorm, VulkanViewportMapping.ToVulkan(GpuVertexFormat.UByte4Normalized));
        Assert.Equal(Format.R8G8B8A8Uint, VulkanViewportMapping.ToVulkan(GpuVertexFormat.UByte4UInt));
    }

    [Fact]
    public void ResolveIsExpressedByAResolveTargetRatherThanAStoreOp()
    {
        Assert.Equal(AttachmentStoreOp.DontCare, VulkanViewportMapping.ToVulkan(GpuStoreOp.Resolve));
        Assert.Equal(AttachmentStoreOp.Store, VulkanViewportMapping.ToVulkan(GpuStoreOp.Store));
        Assert.Equal(AttachmentStoreOp.DontCare, VulkanViewportMapping.ToVulkan(GpuStoreOp.DontCare));
    }

    [Fact]
    public void PipelineCacheHeaderValidationRejectsAnotherDevicesBlob()
    {
        byte[] uuid = new byte[16];
        for (int i = 0; i < 16; i++)
            uuid[i] = (byte)(i + 1);

        byte[] blob = new byte[64];
        BitConverter.GetBytes(32u).CopyTo(blob, 0);
        BitConverter.GetBytes(1u).CopyTo(blob, 4);
        BitConverter.GetBytes(0x1002u).CopyTo(blob, 8);
        BitConverter.GetBytes(0x7550u).CopyTo(blob, 12);
        uuid.CopyTo(blob, 16);

        Assert.NotNull(VulkanPipelineCache.ValidateHeader(blob, 0x1002, 0x7550, uuid));
        uuid[0] = 0xFF;
        Assert.Null(VulkanPipelineCache.ValidateHeader(blob, 0x1002, 0x7550, uuid));
    }

    [Fact]
    public void PipelineCacheHeaderValidationRejectsTruncatedAndForeignBlobs()
    {
        byte[] uuid = new byte[16];
        Assert.Null(VulkanPipelineCache.ValidateHeader(null, 1, 1, uuid));
        Assert.Null(VulkanPipelineCache.ValidateHeader(new byte[8], 1, 1, uuid));

        byte[] blob = new byte[32];
        BitConverter.GetBytes(32u).CopyTo(blob, 0);
        BitConverter.GetBytes(1u).CopyTo(blob, 4);
        BitConverter.GetBytes(0x8086u).CopyTo(blob, 8);
        Assert.Null(VulkanPipelineCache.ValidateHeader(blob, 0x1002, 0, uuid));
    }
}
