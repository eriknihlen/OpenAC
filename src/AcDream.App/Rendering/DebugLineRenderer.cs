using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

public sealed class DebugLineRenderer : IDisposable
{
    internal const int FloatsPerVertex = 6;
    private const int VertexStrideBytes = FloatsPerVertex * sizeof(float);

    /// <summary>
    /// The most vertices one batch, from <see cref="Begin"/> to its flushes,
    /// holds: 4 MiB of them, a quarter of the frame's upload ring. Every batch
    /// is uploaded into that shared per-frame ring, which the rest of the
    /// frame also draws from and which cannot grow mid-frame, so an unbounded
    /// batch (a plugin's world lines, a collision overlay over a busy area)
    /// would overflow it and end the client. Past the budget, further lines
    /// and triangles are left out.
    /// </summary>
    internal const int VertexBudget = 4 * 1024 * 1024 / VertexStrideBytes;

    internal static readonly GpuVertexLayout VertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexStrideBytes,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12),
        ]);

    private readonly ICurrentGpuFrameSource _frameSource;
    private readonly IGpuPipeline _pipeline;
    private readonly IWorldPassScope? _worldPass;
    private readonly IGpuPipeline? _worldPipeline;
    private readonly IGpuPipeline? _hiddenWorldPipeline;
    private readonly IGpuPipeline? _worldSolidPipeline;

    private readonly List<float> _buffer = new(4096);
    private int _vertexCount;

    /// <summary>Lines the world's walls, floors and ceilings hide, drawn with a depth test in the world pass.</summary>
    private readonly List<float> _hiddenBuffer = new(4096);
    private int _hiddenVertexCount;

    /// <summary>Solid triangles drawn in the world pass with its depth, for bars and ribbons plugins put in the world.</summary>
    private readonly List<float> _solidBuffer = new(4096);
    private int _solidVertexCount;

    /// <summary>
    /// Lines draw in a pass of their own, or, given <paramref name="worldPass"/>,
    /// into the world pass while it is open, since a frame holds one pass at a time.
    /// </summary>
    internal DebugLineRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frameSource,
        string shaderDir,
        IWorldPassScope? worldPass = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDir);

        _pipeline = device.CreatePipeline(PipelineFor("debug-line", sampleCount: 1, GpuDepthState.Disabled));
        _worldPass = worldPass;
        if (worldPass is not null)
        {
            _worldPipeline = device.CreatePipeline(PipelineFor("debug-line-world", worldPass.SampleCount, GpuDepthState.Disabled));
            _hiddenWorldPipeline = device.CreatePipeline(PipelineFor(
                "debug-line-world-hidden",
                worldPass.SampleCount,
                new GpuDepthState(Test: true, Write: false, WorldDepthContract.WorldCompare)));
            _worldSolidPipeline = device.CreatePipeline(PipelineFor("world-line-solid", worldPass.SampleCount, GpuDepthState.OpaqueDefault)
                with { Topology = GpuPrimitiveTopology.TriangleList });
        }
    }

    /// <summary>How many more vertices this batch takes before it is full.</summary>
    internal int RemainingVertexBudget => VertexBudget - _vertexCount - _hiddenVertexCount - _solidVertexCount;

    public void Begin()
    {
        _buffer.Clear();
        _vertexCount = 0;
        _hiddenBuffer.Clear();
        _hiddenVertexCount = 0;
        _solidBuffer.Clear();
        _solidVertexCount = 0;
    }

    /// <summary>
    /// Adds a line. A line <paramref name="hiddenByScene"/> is hidden behind the world's walls,
    /// floors and ceilings, as the world itself is; any other is drawn over everything.
    /// </summary>
    public void AddLine(Vector3 a, Vector3 b, Vector3 color, bool hiddenByScene = false)
    {
        if (RemainingVertexBudget < 2)
            return;
        List<float> buffer = hiddenByScene ? _hiddenBuffer : _buffer;
        buffer.Add(a.X); buffer.Add(a.Y); buffer.Add(a.Z);
        buffer.Add(color.X); buffer.Add(color.Y); buffer.Add(color.Z);
        buffer.Add(b.X); buffer.Add(b.Y); buffer.Add(b.Z);
        buffer.Add(color.X); buffer.Add(color.Y); buffer.Add(color.Z);
        if (hiddenByScene)
            _hiddenVertexCount += 2;
        else
            _vertexCount += 2;
    }

    public void AddCylinder(Vector3 basePos, float radius, float height, Vector3 color, bool hiddenByScene = false)
    {
        const int segments = 16;
        Vector3 top = basePos + new Vector3(0, 0, height);

        // Ring vertices
        var baseRing = new Vector3[segments];
        var topRing  = new Vector3[segments];
        for (int i = 0; i < segments; i++)
        {
            float theta = i * (MathF.PI * 2f / segments);
            float cx = MathF.Cos(theta) * radius;
            float cy = MathF.Sin(theta) * radius;
            baseRing[i] = new Vector3(basePos.X + cx, basePos.Y + cy, basePos.Z);
            topRing[i]  = new Vector3(top.X    + cx, top.Y    + cy, top.Z);
        }

        // Base ring
        for (int i = 0; i < segments; i++)
            AddLine(baseRing[i], baseRing[(i + 1) % segments], color, hiddenByScene);
        // Top ring
        for (int i = 0; i < segments; i++)
            AddLine(topRing[i], topRing[(i + 1) % segments], color, hiddenByScene);
        for (int i = 0; i < 4; i++)
        {
            int idx = i * (segments / 4);
            AddLine(baseRing[idx], topRing[idx], color, hiddenByScene);
        }
    }

    /// <summary>
    /// Draw an axis-aligned box as 12 edges.
    /// </summary>
    public void AddBox(Vector3 min, Vector3 max, Vector3 color)
    {
        Vector3[] c =
        {
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, max.Y, max.Z),
        };
        // Bottom
        AddLine(c[0], c[1], color); AddLine(c[1], c[2], color);
        AddLine(c[2], c[3], color); AddLine(c[3], c[0], color);
        // Top
        AddLine(c[4], c[5], color); AddLine(c[5], c[6], color);
        AddLine(c[6], c[7], color); AddLine(c[7], c[4], color);
        // Verticals
        AddLine(c[0], c[4], color); AddLine(c[1], c[5], color);
        AddLine(c[2], c[6], color); AddLine(c[3], c[7], color);
    }

    /// <summary>Upload and draw all accumulated lines, into the world pass when one is open.</summary>
    public void Flush(Matrix4x4 view, Matrix4x4 projection)
    {
        if (_vertexCount == 0 && _hiddenVertexCount == 0) return;

        IGpuFrame frame = _frameSource.CurrentFrame
            ?? throw new InvalidOperationException(
                "DebugLineRenderer.Flush requires an open IGpuFrame (see GpuDeviceFrameLifetime) — " +
                "the host must drive IGpuDevice.BeginFrame() before rendering debug lines.");

        if (_worldPass?.CurrentEncoder is { } world)
        {
            Draw(frame, world, _hiddenWorldPipeline!, view, projection, _hiddenBuffer, _hiddenVertexCount);
            Draw(frame, world, _worldPipeline!, view, projection, _buffer, _vertexCount);
            return;
        }

        using IGpuPassEncoder encoder = frame.BeginPass(new GpuPassDescription
        {
            Name = "debug-line",
            Color = new GpuColorAttachment(
                Target: null,
                Load: GpuLoadOp.Load,
                Store: GpuStoreOp.Store,
                ClearColor: default),
            Depth = null,
            SampleCount = 1,
        });
        // With no world pass open there is no depth to hide lines behind, so every line is drawn over everything.
        Draw(frame, encoder, _pipeline, view, projection, _hiddenBuffer, _hiddenVertexCount);
        Draw(frame, encoder, _pipeline, view, projection, _buffer, _vertexCount);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _worldPipeline?.Dispose();
        _hiddenWorldPipeline?.Dispose();
        _worldSolidPipeline?.Dispose();
    }

    private void Draw(
        IGpuFrame frame,
        IGpuPassEncoder encoder,
        IGpuPipeline pipeline,
        Matrix4x4 view,
        Matrix4x4 projection,
        List<float> buffer,
        int vertexCount)
    {
        if (vertexCount == 0)
            return;
        encoder.BindPipeline(pipeline);

        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ViewProjection = view * projection;
        encoder.SetPushConstants(constants);

        int byteCount = buffer.Count * sizeof(float);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, GpuRingUsage.Vertex);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer).CopyTo(allocation.AsSpan<float>());
        encoder.BindVertexBuffer(0, allocation.Buffer, allocation.OffsetBytes);
        encoder.Draw((uint)vertexCount, 1, 0, 0);
    }

    internal void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        if (RemainingVertexBudget < 3)
            return;
        AddSolidVertex(a, color);
        AddSolidVertex(b, color);
        AddSolidVertex(c, color);
    }

    private void AddSolidVertex(Vector3 position, Vector3 color)
    {
        _solidBuffer.Add(position.X); _solidBuffer.Add(position.Y); _solidBuffer.Add(position.Z);
        _solidBuffer.Add(color.X); _solidBuffer.Add(color.Y); _solidBuffer.Add(color.Z);
        _solidVertexCount++;
    }

    /// <summary>
    /// Draws the accumulated solid triangles into the open world pass, with
    /// its depth, so they sit in the scene like the world does.
    /// </summary>
    internal void FlushWorld(IGpuPassEncoder encoder, Matrix4x4 viewProjection, int width, int height)
    {
        if (_solidVertexCount == 0 || _worldSolidPipeline is null || encoder.Pass.Depth is null)
            return;
        IGpuFrame frame = _frameSource.CurrentFrame
            ?? throw new InvalidOperationException("World lines require an active frame.");
        encoder.BindPipeline(_worldSolidPipeline);
        encoder.SetViewport(0, 0, width, height);
        encoder.SetScissor(0, 0, width, height);
        encoder.SetDepthWrite(true);
        encoder.SetStencil(GpuStencilState.Default);
        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ViewProjection = viewProjection;
        encoder.SetPushConstants(constants);
        GpuRingAllocation allocation = frame.AllocateRing(_solidBuffer.Count * sizeof(float), GpuRingUsage.Vertex);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_solidBuffer).CopyTo(allocation.AsSpan<float>());
        encoder.BindVertexBuffer(0, allocation.Buffer, allocation.OffsetBytes);
        encoder.Draw((uint)_solidVertexCount, 1, 0, 0);
    }

    private static GpuPipelineDescription PipelineFor(string name, int sampleCount, GpuDepthState depth) => new()
    {
        Name = name,
        Shaders = new GpuShaderSet("debug_line"),
        VertexLayout = VertexLayout,
        Topology = GpuPrimitiveTopology.LineList,
        Blend = GpuBlendMode.None,
        Depth = depth,
        Cull = GpuCullMode.None,
        AlphaToCoverage = false,
        ColorWrite = true,
        SampleCount = sampleCount,
    };
}
