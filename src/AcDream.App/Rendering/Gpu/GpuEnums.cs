namespace AcDream.App.Rendering.Gpu;

/// <summary>Which RHI backend is servicing the device.</summary>
internal enum GpuBackendKind
{
    Recording,

    Vulkan,
}

[Flags]
internal enum GpuBufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Storage = 1 << 2,
    Uniform = 1 << 3,
    Indirect = 1 << 4,
    TransferSource = 1 << 5,
    TransferDestination = 1 << 6,
}

internal enum GpuMemoryResidency
{
    /// <summary>Device-local, written only through staged transfers. Mesh arenas, textures.</summary>
    DeviceLocal,

    /// <summary>Persistently mapped and CPU-writable. Per-frame rings and staging.</summary>
    HostWritable,

    /// <summary>Mapped and CPU-readable. Screenshot and diagnostic readback only.</summary>
    HostReadable,
}

/// <summary>Which alignment and usage a per-frame ring allocation must satisfy.</summary>
internal enum GpuRingUsage
{
    Storage,
    Uniform,
    Indirect,
    Vertex,
    Index,
}

internal enum GpuTextureFormat
{
    Rgba8Unorm,
    R8Unorm,
    Bc1Unorm,
    Bc2Unorm,
    Bc3Unorm,

    Rgba8UnormRenderTarget,

    Rgba16FloatRenderTarget,

    Depth24Stencil8,
}

/// <summary>Texture shape. acdream uses 2D for UI art and 2D arrays for every world material.</summary>
internal enum GpuTextureKind
{
    Texture2D,
    Texture2DArray,
}

internal enum GpuFilter
{
    Nearest,
    Linear,
}

internal enum GpuMipFilter
{
    None,
    Nearest,
    Linear,
}

internal enum GpuAddressMode
{
    Repeat,
    ClampToEdge,
}

internal enum GpuBlendMode
{
    /// <summary>Opaque: blending disabled.</summary>
    None,

    /// <summary>Straight alpha: <c>SrcAlpha, OneMinusSrcAlpha</c>.</summary>
    StraightAlpha,

    PremultipliedAlpha,

    /// <summary>Additive: <c>SrcAlpha, One</c>.</summary>
    Additive,

    RawAdditive,

    InverseAdditive,

    InverseAlpha,
}

internal enum GpuCompareOp
{
    Never,
    Less,
    LessOrEqual,
    Equal,
    Greater,
    GreaterOrEqual,
    Always,
}

internal enum GpuCullMode
{
    None,
    Back,
    Front,
}

internal enum GpuStencilOp
{
    /// <summary>Leave the stored value alone.</summary>
    Keep,

    /// <summary>Store zero.</summary>
    Zero,

    /// <summary>Store the reference value.</summary>
    Replace,
}

/// <summary>
/// Triangle winding treated as front-facing. Both backends receive the SAME value
/// from renderers; the Vulkan backend inverts it internally because it renders
/// with a negative viewport height, which mirrors framebuffer space. That flip
/// lives in exactly one mapping function so no renderer ever reasons about it.
/// </summary>
internal enum GpuFrontFace
{
    CounterClockwise,
    Clockwise,
}

internal enum GpuPrimitiveTopology
{
    TriangleList,
    LineList,
}

internal enum GpuIndexType
{
    UInt16,
    UInt32,
}

internal enum GpuLoadOp
{
    DontCare,

    Clear,

    Load,
}

internal enum GpuStoreOp
{
    DontCare,

    Store,

    Resolve,
}
