using System.Numerics;

namespace AcDream.App.Rendering.Gpu;

internal readonly record struct GpuColorAttachment(
    IGpuRenderTarget? Target,
    GpuLoadOp Load,
    GpuStoreOp Store,
    Vector4 ClearColor);

internal readonly record struct GpuDepthAttachment(
    GpuLoadOp Load,
    GpuStoreOp Store,
    float ClearDepth,
    uint ClearStencil,
    IGpuDirectionalDepthTarget? DirectionalTarget = null,
    int Layer = 0);

internal sealed record GpuPassDescription
{
    public required string Name { get; init; }

    public required GpuColorAttachment Color { get; init; }

    /// <summary>False only for dedicated depth-only producers such as directional shadow maps.</summary>
    public bool HasColorAttachment { get; init; } = true;

    /// <summary>Depth/stencil attachment, or null for 2-D passes that need no depth.</summary>
    public GpuDepthAttachment? Depth { get; init; }

    public int SampleCount { get; init; } = 1;

    /// <summary>Non-zero Vulkan multiview mask. Ordinary passes always leave this zero.</summary>
    public uint ViewMask { get; init; }

    public static GpuPassDescription BackbufferClear(string name, Vector4 clearColor, int sampleCount) => new()
    {
        Name = name,
        Color = new GpuColorAttachment(
            Target: null,
            Load: GpuLoadOp.Clear,
            Store: sampleCount > 1 ? GpuStoreOp.Resolve : GpuStoreOp.Store,
            ClearColor: clearColor),
        Depth = new GpuDepthAttachment(
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.DontCare,
            ClearDepth: 1f,
            ClearStencil: 0),
        SampleCount = sampleCount,
    };

    public static GpuPassDescription DirectionalDepth(
        string name,
        IGpuDirectionalDepthTarget target,
        int layer) => new()
    {
        Name = name,
        Color = default,
        HasColorAttachment = false,
        Depth = new GpuDepthAttachment(
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.Store,
            ClearDepth: 1f,
            ClearStencil: 0,
            DirectionalTarget: target,
            Layer: layer),
        SampleCount = 1,
    };

    public static GpuPassDescription DirectionalDepthMultiview(
        string name,
        IGpuDirectionalDepthTarget target,
        uint viewMask) => new()
    {
        Name = name,
        Color = default,
        HasColorAttachment = false,
        Depth = new GpuDepthAttachment(
            GpuLoadOp.Clear,
            GpuStoreOp.Store,
            1f,
            0,
            target,
            Layer: 0),
        SampleCount = 1,
        ViewMask = viewMask,
    };
}
