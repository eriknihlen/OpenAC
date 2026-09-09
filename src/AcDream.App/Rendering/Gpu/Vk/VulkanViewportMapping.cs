using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static class VulkanViewportMapping
{
    internal static Viewport ToVulkan(
        int x,
        int y,
        int width,
        int height,
        uint attachmentHeight,
        float minDepth = 0f,
        float maxDepth = 1f) => new()
        {
            X = x,
            Y = attachmentHeight - (float)y,
            Width = width,
            Height = -height,
            MinDepth = minDepth,
            MaxDepth = maxDepth,
        };

    internal static Rect2D ScissorToVulkan(int x, int y, int width, int height, uint attachmentHeight)
    {
        int top = (int)attachmentHeight - (y + height);
        int clampedTop = Math.Max(0, top);
        int clampedHeight = Math.Max(0, Math.Min(height + Math.Min(0, top), (int)attachmentHeight - clampedTop));
        int clampedX = Math.Max(0, x);
        int clampedWidth = Math.Max(0, width + Math.Min(0, x));
        return new Rect2D(
            new Offset2D(clampedX, clampedTop),
            new Extent2D((uint)clampedWidth, (uint)clampedHeight));
    }

    internal static FrontFace ToVulkan(GpuFrontFace frontFace) => frontFace switch
    {
        GpuFrontFace.CounterClockwise => FrontFace.CounterClockwise,
        GpuFrontFace.Clockwise => FrontFace.Clockwise,
        _ => throw new ArgumentOutOfRangeException(nameof(frontFace), frontFace, "Unknown winding."),
    };

    internal static CullModeFlags ToVulkan(GpuCullMode cullMode) => cullMode switch
    {
        GpuCullMode.None => CullModeFlags.None,
        GpuCullMode.Back => CullModeFlags.BackBit,
        GpuCullMode.Front => CullModeFlags.FrontBit,
        _ => throw new ArgumentOutOfRangeException(nameof(cullMode), cullMode, "Unknown cull mode."),
    };

    internal static CompareOp ToVulkan(GpuCompareOp compare) => compare switch
    {
        GpuCompareOp.Never => CompareOp.Never,
        GpuCompareOp.Less => CompareOp.Less,
        GpuCompareOp.LessOrEqual => CompareOp.LessOrEqual,
        GpuCompareOp.Equal => CompareOp.Equal,
        GpuCompareOp.Greater => CompareOp.Greater,
        GpuCompareOp.GreaterOrEqual => CompareOp.GreaterOrEqual,
        GpuCompareOp.Always => CompareOp.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(compare), compare, "Unknown compare op."),
    };

    internal static PrimitiveTopology ToVulkan(GpuPrimitiveTopology topology) => topology switch
    {
        GpuPrimitiveTopology.TriangleList => PrimitiveTopology.TriangleList,
        GpuPrimitiveTopology.LineList => PrimitiveTopology.LineList,
        _ => throw new ArgumentOutOfRangeException(nameof(topology), topology, "Unknown topology."),
    };

    internal static IndexType ToVulkan(GpuIndexType indexType) => indexType switch
    {
        GpuIndexType.UInt16 => IndexType.Uint16,
        GpuIndexType.UInt32 => IndexType.Uint32,
        _ => throw new ArgumentOutOfRangeException(nameof(indexType), indexType, "Unknown index type."),
    };

    internal static Format ToVulkan(GpuVertexFormat format) => format switch
    {
        GpuVertexFormat.Float1 => Format.R32Sfloat,
        GpuVertexFormat.Float2 => Format.R32G32Sfloat,
        GpuVertexFormat.Float3 => Format.R32G32B32Sfloat,
        GpuVertexFormat.Float4 => Format.R32G32B32A32Sfloat,
        GpuVertexFormat.UByte4Normalized => Format.R8G8B8A8Unorm,
        GpuVertexFormat.UByte4UInt => Format.R8G8B8A8Uint,
        GpuVertexFormat.UInt1 => Format.R32Uint,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown vertex format."),
    };

    internal static StencilOp ToVulkan(GpuStencilOp op) => op switch
    {
        GpuStencilOp.Keep => StencilOp.Keep,
        GpuStencilOp.Zero => StencilOp.Zero,
        GpuStencilOp.Replace => StencilOp.Replace,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown stencil operation."),
    };

    internal static (BlendFactor Source, BlendFactor Destination) BlendFactorsOf(GpuBlendMode blend) => blend switch
    {
        GpuBlendMode.StraightAlpha => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha),
        GpuBlendMode.PremultipliedAlpha => (BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
        GpuBlendMode.Additive => (BlendFactor.SrcAlpha, BlendFactor.One),
        GpuBlendMode.RawAdditive => (BlendFactor.One, BlendFactor.One),
        GpuBlendMode.InverseAdditive => (BlendFactor.OneMinusSrcAlpha, BlendFactor.One),
        GpuBlendMode.InverseAlpha => (BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha),
        GpuBlendMode.None => (BlendFactor.One, BlendFactor.Zero),
        _ => throw new ArgumentOutOfRangeException(nameof(blend), blend, "Unknown blend mode."),
    };

    internal static AttachmentLoadOp ToVulkan(GpuLoadOp load) => load switch
    {
        GpuLoadOp.DontCare => AttachmentLoadOp.DontCare,
        GpuLoadOp.Clear => AttachmentLoadOp.Clear,
        GpuLoadOp.Load => AttachmentLoadOp.Load,
        _ => throw new ArgumentOutOfRangeException(nameof(load), load, "Unknown load op."),
    };

    internal static AttachmentStoreOp ToVulkan(GpuStoreOp store) => store switch
    {
        GpuStoreOp.DontCare or GpuStoreOp.Resolve => AttachmentStoreOp.DontCare,
        GpuStoreOp.Store => AttachmentStoreOp.Store,
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, "Unknown store op."),
    };
}
