using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static class VulkanTextureFormatMapping
{
    internal const Format CanonicalColorAttachmentFormat = Format.B8G8R8A8Unorm;

    /// <summary>The Vulkan format acdream uploads this surface as. BC formats transcode nothing.</summary>
    internal static Format FormatOf(GpuTextureFormat format) => format switch
    {
        GpuTextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
        GpuTextureFormat.R8Unorm => Format.R8Unorm,
        GpuTextureFormat.Bc1Unorm => Format.BC1RgbaUnormBlock,
        GpuTextureFormat.Bc2Unorm => Format.BC2UnormBlock,
        GpuTextureFormat.Bc3Unorm => Format.BC3UnormBlock,
        GpuTextureFormat.Rgba8UnormRenderTarget => CanonicalColorAttachmentFormat,
        GpuTextureFormat.Rgba16FloatRenderTarget => Format.R16G16B16A16Sfloat,
        GpuTextureFormat.Depth24Stencil8 => Format.D24UnormS8Uint,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown texture format."),
    };

    internal static bool IsDepthStencil(GpuTextureFormat format) =>
        format == GpuTextureFormat.Depth24Stencil8;

    internal static bool IsRenderTarget(GpuTextureFormat format) =>
        format is GpuTextureFormat.Rgba8UnormRenderTarget
            or GpuTextureFormat.Rgba16FloatRenderTarget
            or GpuTextureFormat.Depth24Stencil8;

    /// <summary>Bytes one texel occupies. Only meaningful for uncompressed formats.</summary>
    internal static int BytesPerTexel(GpuTextureFormat format) => format switch
    {
        GpuTextureFormat.Rgba8Unorm or GpuTextureFormat.Rgba8UnormRenderTarget => 4,
        GpuTextureFormat.Rgba16FloatRenderTarget => 8,
        GpuTextureFormat.R8Unorm => 1,
        GpuTextureFormat.Depth24Stencil8 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "A block-compressed format has no texel size."),
    };

    /// <summary>Bytes one mip level of one array layer occupies.</summary>
    internal static int LevelSizeBytes(GpuTextureFormat format, int width, int height) =>
        BlockCompressionCodec.IsBlockCompressed(format)
            ? BlockCompressionCodec.LevelSizeBytes(format, width, height)
            : width * height * BytesPerTexel(format);

    /// <summary>Dimensions of mip level <paramref name="level"/>, floored at 1 texel.</summary>
    internal static (int Width, int Height) LevelExtent(int width, int height, int level)
    {
        for (int i = 0; i < level; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return (width, height);
    }

    internal static int FullMipLevelCount(int width, int height)
    {
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            levels++;
        }

        return levels;
    }

    internal static ImageAspectFlags AspectOf(GpuTextureFormat format) =>
        IsDepthStencil(format)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.ColorBit;

    internal static SampleCountFlags SampleCountOf(int sampleCount) => sampleCount switch
    {
        <= 1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        <= 4 => SampleCountFlags.Count4Bit,
        _ => SampleCountFlags.Count8Bit,
    };

    internal static ImageViewType ViewTypeOf(GpuTextureKind kind) => kind switch
    {
        GpuTextureKind.Texture2D => ImageViewType.Type2D,
        GpuTextureKind.Texture2DArray => ImageViewType.Type2DArray,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown texture kind."),
    };

    internal static ImageViewType SampledViewTypeOf(GpuTextureKind kind) => kind switch
    {
        GpuTextureKind.Texture2D or GpuTextureKind.Texture2DArray => ImageViewType.Type2DArray,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown texture kind."),
    };

    internal static Filter FilterOf(GpuFilter filter) => filter switch
    {
        GpuFilter.Nearest => Filter.Nearest,
        GpuFilter.Linear => Filter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown filter."),
    };

    internal static SamplerMipmapMode MipmapModeOf(GpuMipFilter filter) => filter switch
    {
        // No mip filtering still needs a mode; NEAREST with a zero LOD range is
        // the standard way to express "level 0 only".
        GpuMipFilter.None or GpuMipFilter.Nearest => SamplerMipmapMode.Nearest,
        GpuMipFilter.Linear => SamplerMipmapMode.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown mip filter."),
    };

    internal static SamplerAddressMode AddressModeOf(GpuAddressMode mode) => mode switch
    {
        GpuAddressMode.Repeat => SamplerAddressMode.Repeat,
        GpuAddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown address mode."),
    };
}
