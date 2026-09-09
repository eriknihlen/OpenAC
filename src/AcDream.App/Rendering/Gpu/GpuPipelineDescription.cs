using System.Collections.Immutable;

namespace AcDream.App.Rendering.Gpu;

internal enum GpuVertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,

    /// <summary>Four unsigned bytes scaled to [0,1] floats — a shader <c>vec4</c> input.</summary>
    UByte4Normalized,

    UByte4UInt,

    UInt1,
}

internal enum GpuVertexInputRate
{
    /// <summary>The binding advances once per vertex — the default for every layout written before V6l.</summary>
    Vertex,

    /// <summary>The binding advances once per instance (GL divisor 1).</summary>
    Instance,
}

internal readonly record struct GpuVertexBinding(
    uint Binding,
    uint StrideBytes,
    GpuVertexInputRate InputRate = GpuVertexInputRate.Vertex);

internal readonly record struct GpuVertexAttribute(
    uint Location,
    GpuVertexFormat Format,
    uint OffsetBytes,
    uint Binding = 0);

internal sealed record GpuVertexLayout(
    ImmutableArray<GpuVertexBinding> Bindings,
    ImmutableArray<GpuVertexAttribute> Attributes)
{
    public static GpuVertexLayout Interleaved(
        uint strideBytes,
        ImmutableArray<GpuVertexAttribute> attributes) =>
        new(
            [new GpuVertexBinding(0, strideBytes, GpuVertexInputRate.Vertex)],
            attributes);

    public uint StrideBytes =>
        Bindings.IsDefaultOrEmpty ? 0u : Bindings[0].StrideBytes;

    public uint StrideOf(uint binding)
    {
        foreach (GpuVertexBinding candidate in Bindings)
        {
            if (candidate.Binding == binding)
                return candidate.StrideBytes;
        }

        throw new ArgumentOutOfRangeException(
            nameof(binding),
            binding,
            "The vertex layout declares no such binding.");
    }

    /// <summary>How often <paramref name="binding"/> advances.</summary>
    public GpuVertexInputRate InputRateOf(uint binding)
    {
        foreach (GpuVertexBinding candidate in Bindings)
        {
            if (candidate.Binding == binding)
                return candidate.InputRate;
        }

        throw new ArgumentOutOfRangeException(
            nameof(binding),
            binding,
            "The vertex layout declares no such binding.");
    }

    /// <summary>
    /// The world mesh vertex shared by <c>mesh_modern</c>, EnvCells, and terrain:
    /// position, normal, texcoord — 32 bytes, matching the format
    /// <c>ObjectMeshManager</c> packs into <c>GlobalMeshBuffer</c>.
    /// </summary>
    public static GpuVertexLayout WorldMesh { get; } = Interleaved(
        strideBytes: 32,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12),
            new GpuVertexAttribute(2, GpuVertexFormat.Float2, 24),
        ]);

    public static GpuVertexLayout None { get; } = new([], []);
}

internal readonly record struct GpuShaderSet
{
    internal GpuShaderSet(string name)
        : this(name, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty)
    {
    }

    internal GpuShaderSet(
        string name,
        ReadOnlyMemory<byte> vertexSpirv,
        ReadOnlyMemory<byte> fragmentSpirv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (vertexSpirv.IsEmpty != fragmentSpirv.IsEmpty)
            throw new ArgumentException("Both SPIR-V stages must be supplied together.");
        Name = name;
        VertexSpirv = vertexSpirv;
        FragmentSpirv = fragmentSpirv;
    }

    internal string Name { get; }

    internal ReadOnlyMemory<byte> VertexSpirv { get; }

    internal ReadOnlyMemory<byte> FragmentSpirv { get; }

    internal bool HasEmbeddedSpirv => !VertexSpirv.IsEmpty;
}

internal readonly record struct GpuDepthState(bool Test, bool Write, GpuCompareOp Compare)
{
    /// <summary>Standard opaque geometry: test and write, nearer wins.</summary>
    public static GpuDepthState OpaqueDefault { get; } = new(Test: true, Write: true, GpuCompareOp.LessOrEqual);

    /// <summary>Translucent geometry: test against existing depth but do not occlude later draws.</summary>
    public static GpuDepthState TranslucentDefault { get; } = new(Test: true, Write: false, GpuCompareOp.LessOrEqual);

    /// <summary>Sky and 2-D overlays: depth is irrelevant.</summary>
    public static GpuDepthState Disabled { get; } = new(Test: false, Write: false, GpuCompareOp.Always);
}

internal readonly record struct GpuStencilState(
    GpuCompareOp Compare,
    GpuStencilOp Fail,
    GpuStencilOp DepthFail,
    GpuStencilOp Pass,
    uint Reference,
    uint CompareMask,
    uint WriteMask)
{
    /// <summary>GL's and Vulkan's own defaults: always pass, never write.</summary>
    public static GpuStencilState Default { get; } = new(
        GpuCompareOp.Always,
        GpuStencilOp.Keep,
        GpuStencilOp.Keep,
        GpuStencilOp.Keep,
        Reference: 0,
        CompareMask: 0xFF,
        WriteMask: 0xFF);
}

internal sealed record GpuPipelineDescription
{
    public uint ViewMask { get; init; }
    /// <summary>Stable identifier, e.g. <c>"mesh-opaque"</c>. Surfaced to RenderDoc and validation layers.</summary>
    public required string Name { get; init; }

    /// <summary>The GLSL pair this pipeline draws with.</summary>
    public required GpuShaderSet Shaders { get; init; }

    public required GpuVertexLayout VertexLayout { get; init; }

    public GpuPrimitiveTopology Topology { get; init; } = GpuPrimitiveTopology.TriangleList;

    public GpuBlendMode Blend { get; init; } = GpuBlendMode.None;

    public GpuDepthState Depth { get; init; } = GpuDepthState.OpaqueDefault;

    public GpuCullMode Cull { get; init; } = GpuCullMode.Back;

    public GpuFrontFace FrontFace { get; init; } = GpuFrontFace.CounterClockwise;

    public bool AlphaToCoverage { get; init; }

    public bool ColorWrite { get; init; } = true;

    public bool HasColorAttachment { get; init; } = true;

    public bool StencilTest { get; init; }

    public GpuStencilState Stencil { get; init; } = GpuStencilState.Default;

    public GpuTextureFormat ColorFormat { get; init; } = GpuTextureFormat.Rgba8UnormRenderTarget;

    public bool AllowColorFormatVariants { get; init; } = true;

    public bool UsesRenderPackShaderAbi { get; init; }

    public int SampleCount { get; init; } = 1;
}
