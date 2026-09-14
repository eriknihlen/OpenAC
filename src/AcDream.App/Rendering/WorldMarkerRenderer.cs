using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.World;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Rendering;

/// <summary>
/// Draws the rings and strips plugins queued (<see cref="WorldMarkerQueue"/>)
/// as geometry in the world pass, after the scene and its particles: a
/// flat band on the ground with a short wall standing on it for a ring, a
/// flat strip for a line. Translucent, depth-tested against the world and
/// never writing depth, so a wall or a creature in front hides a marker
/// and a marker never hides anything. Map coordinates are turned into the
/// render frame the way the overlay's projection does.
/// </summary>
internal sealed class WorldMarkerRenderer : IDisposable
{
    private const int FloatsPerVertex = 7;
    private const int VertexStrideBytes = FloatsPerVertex * sizeof(float);
    private const int RingSegments = 32;
    /// <summary>Ground geometry sits this far above the recorded floor, clear of z-fighting.</summary>
    private const float GroundLiftMeters = 0.04f;
    private const float WallAlpha = 0.45f;

    private static readonly GpuVertexLayout VertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexStrideBytes,
        [
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float4, 12),
        ]);

    private static readonly float[] Cos = Enumerable.Range(0, RingSegments).Select(i => MathF.Cos(i * MathF.PI * 2f / RingSegments)).ToArray();
    private static readonly float[] Sin = Enumerable.Range(0, RingSegments).Select(i => MathF.Sin(i * MathF.PI * 2f / RingSegments)).ToArray();

    private readonly IWorldPassScope _scope;
    private readonly ICurrentGpuFrameSource _frames;
    private readonly WorldMarkerQueue _queue;
    private readonly LiveWorldOriginState _origin;
    private readonly IGpuPipeline _pipeline;
    private readonly List<float> _vertices = new(8192);
    private readonly List<WorldMarkerQueue.Ring> _rings = [];
    private readonly List<WorldMarkerQueue.Line> _lines = [];
    private int _vertexCount;
    private bool _disposed;

    public WorldMarkerRenderer(
        IGpuDevice device,
        IWorldPassScope scope,
        ICurrentGpuFrameSource frames,
        WorldMarkerQueue queue,
        LiveWorldOriginState origin)
    {
        ArgumentNullException.ThrowIfNull(device);
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "world-marker",
            Shaders = new GpuShaderSet("world_marker"),
            VertexLayout = VertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.StraightAlpha,
            Depth = new GpuDepthState(Test: true, Write: false, WorldDepthContract.WorldCompare),
            Cull = GpuCullMode.None,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = scope.SampleCount,
        });
        _queue.HasRenderer = true;
    }

    /// <summary>Draws what the plugins queued since the last frame. Nothing queued costs nothing.</summary>
    public void Draw(in WorldCameraFrame camera)
    {
        if (_disposed)
            return;
        _rings.Clear();
        _lines.Clear();
        _queue.Drain(_rings, _lines);
        if ((_rings.Count == 0 && _lines.Count == 0) || !_origin.IsKnown)
            return;

        _vertices.Clear();
        _vertexCount = 0;
        int centerX = _origin.CenterX;
        int centerY = _origin.CenterY;
        foreach (WorldMarkerQueue.Ring ring in _rings)
            AddRing(ring, centerX, centerY);
        foreach (WorldMarkerQueue.Line line in _lines)
            AddLine(line, centerX, centerY);
        if (_vertexCount == 0)
            return;

        IGpuFrame? frame = _frames.CurrentFrame;
        IGpuPassEncoder? encoder = _scope.CurrentEncoder;
        if (frame is null || encoder is null)
            return;
        encoder.BindPipeline(_pipeline);
        GpuPushConstants constants = GpuPushConstants.Default;
        constants.ViewProjection = camera.ViewProjection;
        encoder.SetPushConstants(constants);
        int byteCount = _vertices.Count * sizeof(float);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, GpuRingUsage.Vertex);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vertices).CopyTo(allocation.AsSpan<float>());
        encoder.BindVertexBuffer(0, allocation.Buffer, allocation.OffsetBytes);
        encoder.Draw((uint)_vertexCount, 1, 0, 0);
    }

    private static Vector3 ToWorld(in PluginNavigationPosition position, int centerX, int centerY) => new(
        (float)(position.EastWest * 240d + 84d + (127 - centerX) * 192d),
        (float)(position.NorthSouth * 240d + 84d + (127 - centerY) * 192d),
        (float)(position.Elevation * 240d));

    private void AddRing(in WorldMarkerQueue.Ring ring, int centerX, int centerY)
    {
        Vector3 center = ToWorld(ring.Center, centerX, centerY);
        float inner = Math.Max(0.01f, ring.RadiusMeters - ring.ThicknessMeters * 0.5f);
        float outer = ring.RadiusMeters + ring.ThicknessMeters * 0.5f;
        float ground = center.Z + GroundLiftMeters;
        Vector4 wallColor = ring.Color with { W = ring.Color.W * WallAlpha };
        for (int segment = 0; segment < RingSegments; segment++)
        {
            int next = (segment + 1) % RingSegments;
            var innerA = new Vector3(center.X + Cos[segment] * inner, center.Y + Sin[segment] * inner, ground);
            var innerB = new Vector3(center.X + Cos[next] * inner, center.Y + Sin[next] * inner, ground);
            var outerA = new Vector3(center.X + Cos[segment] * outer, center.Y + Sin[segment] * outer, ground);
            var outerB = new Vector3(center.X + Cos[next] * outer, center.Y + Sin[next] * outer, ground);
            Quad(innerA, outerA, outerB, innerB, ring.Color);
            if (ring.HeightMeters > 0f)
            {
                var baseA = new Vector3(center.X + Cos[segment] * ring.RadiusMeters, center.Y + Sin[segment] * ring.RadiusMeters, ground);
                var baseB = new Vector3(center.X + Cos[next] * ring.RadiusMeters, center.Y + Sin[next] * ring.RadiusMeters, ground);
                Vector3 lift = new(0f, 0f, ring.HeightMeters);
                Quad(baseA, baseB, baseB + lift, baseA + lift, wallColor);
            }
        }
    }

    private void AddLine(in WorldMarkerQueue.Line line, int centerX, int centerY)
    {
        Vector3 from = ToWorld(line.From, centerX, centerY);
        Vector3 to = ToWorld(line.To, centerX, centerY);
        from.Z += GroundLiftMeters;
        to.Z += GroundLiftMeters;
        var direction = new Vector2(to.X - from.X, to.Y - from.Y);
        float length = direction.Length();
        if (length < 0.01f)
            return;
        Vector2 side = new Vector2(-direction.Y, direction.X) / length * (line.ThicknessMeters * 0.5f);
        var offset = new Vector3(side, 0f);
        Quad(from - offset, to - offset, to + offset, from + offset, line.Color);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 color)
    {
        Vertex(a, color); Vertex(b, color); Vertex(c, color);
        Vertex(a, color); Vertex(c, color); Vertex(d, color);
    }

    private void Vertex(Vector3 position, Vector4 color)
    {
        _vertices.Add(position.X); _vertices.Add(position.Y); _vertices.Add(position.Z);
        _vertices.Add(color.X); _vertices.Add(color.Y); _vertices.Add(color.Z); _vertices.Add(color.W);
        _vertexCount++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.HasRenderer = false;
        _pipeline.Dispose();
    }
}
