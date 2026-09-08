using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

public sealed partial class PortalDepthMaskRenderer
{
    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;
    private IGpuPipeline? _depthWritePipeline;
    private bool _rhiFrameStarted;

    /// <summary>One position per vertex — the only attribute <c>portal_depth.vert</c> reads.</summary>
    internal static GpuVertexLayout PortalVertexLayout { get; } = GpuVertexLayout.Interleaved(
        strideBytes: 3 * sizeof(float),
        ImmutableArray.Create(
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0)));

    internal PortalDepthMaskRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _resources = new ResourceCleanupGroup();

        try
        {
            int samples = scope.SampleCount;
            _depthWritePipeline = CreatePortalPipeline(
                device,
                "portal-depth-write",
                GpuCompareOp.Always,
                depthWrite: true,
                stencilTest: false,
                GpuStencilState.Default,
                samples);
        }
        catch
        {
            DisposeRhiResources();
            throw;
        }
    }

    private static IGpuPipeline CreatePortalPipeline(
        IGpuDevice device,
        string name,
        GpuCompareOp depthCompare,
        bool depthWrite,
        bool stencilTest,
        GpuStencilState stencil,
        int sampleCount) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = new GpuShaderSet("portal_depth"),
            VertexLayout = PortalVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = new GpuDepthState(Test: true, Write: depthWrite, depthCompare),
            Cull = GpuCullMode.None,
            FrontFace = GpuFrontFace.CounterClockwise,
            AlphaToCoverage = false,
            ColorWrite = false,
            StencilTest = stencilTest,
            Stencil = stencil,
            SampleCount = sampleCount,
        });

    private void DrawDepthFanRhi(
        ReadOnlySpan<Vector3> worldVerts,
        in Matrix4x4 viewProjection,
        ReadOnlySpan<Vector4> planes,
        bool forceFarZ)
    {
        if (!_rhiFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing portal depth masks.");

        int n = Math.Min(worldVerts.Length, MaxFanVerts);
        int planeCount = Math.Min(planes.Length, ClipFrame.MaxPlanes);
        IGpuPassEncoder encoder = _scope!.RequireEncoder();
        IGpuFrame frame = _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "PortalDepthMaskRenderer requires an open IGpuFrame (see GpuDeviceFrameLifetime).");

        // The fan, expanded exactly: triangle i is (v0, v[i+1], v[i+2]).
        int triangleCount = n - 2;
        int vertexCount = triangleCount * 3;
        GpuRingAllocation vertices = frame.AllocateRing(
            vertexCount * 3 * sizeof(float),
            GpuRingUsage.Vertex);
        Span<float> positions = vertices.AsSpan<float>();
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            WritePosition(positions, triangle * 9, worldVerts[0]);
            WritePosition(positions, triangle * 9 + 3, worldVerts[triangle + 1]);
            WritePosition(positions, triangle * 9 + 6, worldVerts[triangle + 2]);
        }

        GpuRingAllocation clip = frame.AllocateRing(
            ClipFrame.TerrainUboBytes,
            GpuRingUsage.Uniform);
        clip.Data.Clear();
        MemoryMarshal.Write(clip.Data, in planeCount);
        Span<Vector4> clipPlanes = MemoryMarshal.Cast<byte, Vector4>(
            clip.Data[ClipFrame.CellClipPlanesOffset..]);
        for (int i = 0; i < planeCount; i++)
            clipPlanes[i] = planes[i];

        RecordPortalPass(
            encoder,
            _depthWritePipeline!,
            clip,
            vertices,
            vertexCount,
            in viewProjection,
            renderPass: forceFarZ ? 1 : 0);
    }

    private static void RecordPortalPass(
        IGpuPassEncoder encoder,
        IGpuPipeline pipeline,
        in GpuRingAllocation clip,
        in GpuRingAllocation vertices,
        int vertexCount,
        in Matrix4x4 viewProjection,
        int renderPass)
    {
        encoder.BindPipeline(pipeline);
        encoder.SetPushConstants(new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            // portal_depth.vert's "render pass" IS the seal/punch selector —
            // the GL arm's uForceFarZ, rehomed onto the shared block.
            RenderPass = renderPass,
            LightDebug = 0,
            TextureIndexA = 0,
            TextureIndexB = 0,
            ParamA = 0f,
            ParamB = 0f,
        });
        encoder.BindUniformBuffer(
            ClipFrame.TerrainClipUboBinding,
            clip.Buffer,
            clip.OffsetBytes,
            (uint)ClipFrame.TerrainUboBytes);
        encoder.BindVertexBuffer(0, vertices.Buffer, vertices.OffsetBytes);
        encoder.Draw((uint)vertexCount, 1, 0, 0);
    }

    private static void WritePosition(Span<float> destination, int offset, Vector3 position)
    {
        destination[offset] = position.X;
        destination[offset + 1] = position.Y;
        destination[offset + 2] = position.Z;
    }

    private void DisposeRhiResources()
    {
        List<Exception>? failures = null;
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        Attempt(() => _depthWritePipeline?.Dispose());
        _depthWritePipeline = null;
        _rhiFrameStarted = false;

        if (failures is not null)
        {
            throw new AggregateException(
                "The portal depth mask's RHI resources did not fully release.",
                failures);
        }
    }
}
