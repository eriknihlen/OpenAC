using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.Rendering;

public sealed class DebugLineRenderer : IDisposable
{
    internal const int FloatsPerVertex = 6;
    private const int VertexStrideBytes = FloatsPerVertex * sizeof(float);

    internal static readonly GpuVertexLayout VertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexStrideBytes,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12),
        ]);

    private readonly ICurrentGpuFrameSource _frameSource;
    private readonly IGpuPipeline _pipeline;

    private readonly List<float> _buffer = new(4096);
    private int _vertexCount;

    internal DebugLineRenderer(IGpuDevice device, ICurrentGpuFrameSource frameSource, string shaderDir)
    {
        ArgumentNullException.ThrowIfNull(device);
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDir);

        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "debug-line",
            Shaders = new GpuShaderSet("debug_line"),
            VertexLayout = VertexLayout,
            Topology = GpuPrimitiveTopology.LineList,
            Blend = GpuBlendMode.None,
            Depth = GpuDepthState.Disabled,
            Cull = GpuCullMode.None,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = 1,
        });
    }

    public void Begin()
    {
        _buffer.Clear();
        _vertexCount = 0;
    }

    public void AddLine(Vector3 a, Vector3 b, Vector3 color)
    {
        _buffer.Add(a.X); _buffer.Add(a.Y); _buffer.Add(a.Z);
        _buffer.Add(color.X); _buffer.Add(color.Y); _buffer.Add(color.Z);
        _buffer.Add(b.X); _buffer.Add(b.Y); _buffer.Add(b.Z);
        _buffer.Add(color.X); _buffer.Add(color.Y); _buffer.Add(color.Z);
        _vertexCount += 2;
    }

    public void AddCylinder(Vector3 basePos, float radius, float height, Vector3 color)
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
            AddLine(baseRing[i], baseRing[(i + 1) % segments], color);
        // Top ring
        for (int i = 0; i < segments; i++)
            AddLine(topRing[i], topRing[(i + 1) % segments], color);
        for (int i = 0; i < 4; i++)
        {
            int idx = i * (segments / 4);
            AddLine(baseRing[idx], topRing[idx], color);
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

    /// <summary>Upload + draw all accumulated lines.</summary>
    public void Flush(Matrix4x4 view, Matrix4x4 projection)
    {
        if (_vertexCount == 0) return;

        IGpuFrame frame = _frameSource.CurrentFrame
            ?? throw new InvalidOperationException(
                "DebugLineRenderer.Flush requires an open IGpuFrame (see GpuDeviceFrameLifetime) — " +
                "the host must drive IGpuDevice.BeginFrame() before rendering debug lines.");

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
        encoder.BindPipeline(_pipeline);

        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ViewProjection = view * projection;
        encoder.SetPushConstants(constants);

        int byteCount = _buffer.Count * sizeof(float);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, GpuRingUsage.Vertex);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer).CopyTo(allocation.AsSpan<float>());
        encoder.BindVertexBuffer(0, allocation.Buffer, allocation.OffsetBytes);
        encoder.Draw((uint)_vertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
    }
}
