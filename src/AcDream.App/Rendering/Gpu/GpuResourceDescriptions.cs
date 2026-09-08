namespace AcDream.App.Rendering.Gpu;

internal readonly record struct GpuBufferDescription(
    string Name,
    long SizeBytes,
    GpuBufferUsage Usage,
    GpuMemoryResidency Residency);

internal readonly record struct GpuTextureDescription(
    string Name,
    GpuTextureKind Kind,
    GpuTextureFormat Format,
    int Width,
    int Height,
    int LayerCount,
    int MipLevelCount);

internal readonly record struct GpuSamplerDescription(
    GpuFilter MinFilter,
    GpuFilter MagFilter,
    GpuMipFilter MipFilter,
    GpuAddressMode AddressU,
    GpuAddressMode AddressV,
    float MaxAnisotropy)
{
    /// <summary>Trilinear repeat — the default for world materials.</summary>
    public static GpuSamplerDescription WorldRepeat { get; } = new(
        GpuFilter.Linear,
        GpuFilter.Linear,
        GpuMipFilter.Linear,
        GpuAddressMode.Repeat,
        GpuAddressMode.Repeat,
        MaxAnisotropy: 1f);

    public static GpuSamplerDescription WorldClamp { get; } = new(
        GpuFilter.Linear,
        GpuFilter.Linear,
        GpuMipFilter.Linear,
        GpuAddressMode.ClampToEdge,
        GpuAddressMode.ClampToEdge,
        MaxAnisotropy: 1f);

    public static GpuSamplerDescription UiNearest { get; } = new(
        GpuFilter.Nearest,
        GpuFilter.Nearest,
        GpuMipFilter.None,
        GpuAddressMode.ClampToEdge,
        GpuAddressMode.ClampToEdge,
        MaxAnisotropy: 1f);

    public static GpuSamplerDescription ShadowNearestClamp { get; } = new(
        GpuFilter.Nearest,
        GpuFilter.Nearest,
        GpuMipFilter.Nearest,
        GpuAddressMode.ClampToEdge,
        GpuAddressMode.ClampToEdge,
        MaxAnisotropy: 1f);
}

internal readonly record struct GpuRenderTargetDescription(
    string Name,
    int Width,
    int Height,
    GpuTextureFormat ColorFormat,
    GpuTextureFormat? DepthFormat,
    int SampleCount,
    bool SampleableDepth = false);

internal readonly record struct GpuDirectionalDepthTargetDescription(
    string Name,
    int Resolution,
    int LayerCount,
    GpuTextureFormat DepthFormat = GpuTextureFormat.Depth24Stencil8);

internal readonly record struct GpuTextureSlot(uint Index)
{
    /// <summary>Sentinel for "no texture assigned". Must never reach a shader.</summary>
    public static GpuTextureSlot Unassigned { get; } = new(uint.MaxValue);

    public bool IsAssigned => Index != uint.MaxValue;

    public override string ToString() =>
        IsAssigned ? $"slot#{Index}" : "slot#unassigned";
}
