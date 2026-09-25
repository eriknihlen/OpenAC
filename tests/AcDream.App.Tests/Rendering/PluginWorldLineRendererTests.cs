using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Rendering;

public sealed class PluginWorldLineRendererTests
{
    private const float Range = 250f;
    private const float PieceMeters = 0.75f;
    private const int Budget = DebugLineRenderer.VertexBudget;

    [Fact]
    public void ALongGroundLineFromTheCameraIsOnlyCutIntoPiecesAsFarAsTheDrawRange()
    {
        using var scene = new Scene(camera: Vector3.Zero);

        scene.Draw(Line(new Vector3(0f, 0f, 0f), new Vector3(5000f, 0f, 0f), followTerrain: true));

        Vector3[] vertices = scene.UploadedVertices();
        Assert.NotEmpty(vertices);
        // The line is drawn out to the edge of the draw range and no further,
        // so it costs the pieces of its visible 250 m, not of its whole 5 km.
        Assert.InRange(vertices.Max(vertex => vertex.X), Range - 1f, Range + PieceMeters + 0.5f);
        Assert.True(scene.DrawnVertexCount <= ((int)MathF.Ceiling(Range / PieceMeters) + 2) * 36);
    }

    [Fact]
    public void AGroundLinePassingTheCameraWithBothEndsOutOfRangeIsDrawnWhereItIsInRange()
    {
        using var scene = new Scene(camera: Vector3.Zero);

        scene.Draw(Line(new Vector3(-1000f, 10f, 0f), new Vector3(1000f, 10f, 0f), followTerrain: true));

        Vector3[] vertices = scene.UploadedVertices();
        Assert.NotEmpty(vertices);
        Assert.All(vertices, vertex => Assert.InRange(vertex.X, -Range - 1f, Range + 1f));
    }

    [Fact]
    public void HoweverManyLinesAPluginSendsTheUploadStaysWithinTheBudget()
    {
        using var scene = new Scene(camera: Vector3.Zero);
        var lines = new List<PluginWorldLine>();
        for (int i = 0; i < 2000; i++)
        {
            float y = (i % 200) - 100f;
            float x = (i / 200 * 10f) - 50f;
            lines.Add(Line(new Vector3(x, y, 0f), new Vector3(x + 10f, y, 0f), followTerrain: true));
        }

        scene.Draw([.. lines]);

        Assert.InRange(scene.DrawnVertexCount, 1, Budget);
        Assert.True(scene.RingBytes <= Budget * 24L);
    }

    [Fact]
    public void WhenTheBudgetRunsOutTheLinesNearestTheCameraAreTheOnesDrawn()
    {
        using var scene = new Scene(camera: Vector3.Zero);
        var lines = new List<PluginWorldLine>();
        for (int i = 0; i < 1000; i++)
        {
            // Far lines, on a ring 200 m out, sent first.
            float angle = i * MathF.Tau / 1000f;
            var at = new Vector3(MathF.Cos(angle) * 200f, MathF.Sin(angle) * 200f, 0f);
            lines.Add(Line(at, at + new Vector3(10f, 0f, 0f), followTerrain: true));
        }
        // The one line right beside the camera is sent last.
        lines.Add(Line(new Vector3(5f, -1f, 0f), new Vector3(5f, 1f, 0f), followTerrain: false));

        scene.Draw([.. lines]);

        Assert.InRange(scene.DrawnVertexCount, 1, Budget);
        Assert.Contains(scene.UploadedVertices(), vertex => MathF.Abs(vertex.X - 5f) < 0.5f && MathF.Abs(vertex.Y) <= 1.5f);
    }

    [Fact]
    public void DebugOverlayLinesStayWithinTheBudget()
    {
        var device = new RecordingGpuDevice(ringCapacityBytes: 64 * 1024 * 1024);
        IGpuFrame frame = device.BeginFrame();
        using var lines = new DebugLineRenderer(device, new FixedFrame(frame), "shaders");

        lines.Begin();
        // A collision overlay over a busy area: one wire cylinder per object.
        for (int i = 0; i < 10_000; i++)
        {
            lines.AddCylinder(new Vector3(i, 0f, 0f), 0.5f, 2f, Vector3.One, hiddenByScene: i % 2 == 0);
            lines.AddBox(new Vector3(i, 0f, 0f), new Vector3(i + 1, 1f, 1f), Vector3.One);
        }
        lines.Flush(Matrix4x4.Identity, Matrix4x4.Identity);

        long drawn = device.Calls.OfType<GpuRecordedDraw>().Sum(draw => (long)draw.VertexCount);
        long uploaded = device.Calls.OfType<GpuRecordedRingAllocation>().Sum(allocation => (long)allocation.ByteCount);
        Assert.InRange(drawn, 1, Budget);
        Assert.True(uploaded <= Budget * 24L);
    }

    private static PluginWorldLine Line(Vector3 start, Vector3 end, bool followTerrain) =>
        new(At(start), At(end), 0xFF40FFu) { FollowTerrain = followTerrain };

    // With the world's origin at the centre landblock, a local position is
    // the map position times 240 plus 84 m on each ground axis.
    private static PluginNavigationPosition At(Vector3 local) => new(
        CellId: 0u,
        EastWest: (local.X - 84d) / 240d,
        NorthSouth: (local.Y - 84d) / 240d,
        Elevation: local.Z / 240d,
        HeadingDegrees: 0f,
        IsOutdoor: true);

    private sealed class Scene : IDisposable
    {
        private readonly RecordingGpuDevice _device = new(ringCapacityBytes: 64 * 1024 * 1024);
        private readonly IGpuFrame _frame;
        private readonly DebugLineRenderer _lines;
        private readonly PluginWorldLineStore _store = new();
        private readonly PluginWorldLineRenderer _renderer;

        public Scene(Vector3 camera)
        {
            _frame = _device.BeginFrame();
            _lines = new DebugLineRenderer(_device, new FixedFrame(_frame), "shaders", new WorldPassScope());
            var origin = new LiveWorldOriginState();
            origin.TryInitialize(127, 127);
            _renderer = new PluginWorldLineRenderer(_store, _lines, new FixedCamera(camera), origin, new PhysicsEngine());
        }

        public long DrawnVertexCount => _device.Calls.OfType<GpuRecordedDraw>().Sum(draw => (long)draw.VertexCount);

        public long RingBytes => _device.Calls.OfType<GpuRecordedRingAllocation>().Sum(allocation => (long)allocation.ByteCount);

        public void Draw(params PluginWorldLine[] lines)
        {
            _store.CreateLayer().SetLines(lines);
            using IGpuPassEncoder encoder = _frame.BeginPass(WorldPass());
            _renderer.Render(encoder, 1280, 720);
        }

        public Vector3[] UploadedVertices()
        {
            var result = new List<Vector3>();
            foreach (GpuRecordedRingAllocation allocation in _device.Calls.OfType<GpuRecordedRingAllocation>())
            {
                ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(
                    _device.RingBytes.Slice((int)allocation.OffsetBytes, allocation.ByteCount));
                for (int i = 0; i + 5 < floats.Length; i += 6)
                    result.Add(new Vector3(floats[i], floats[i + 1], floats[i + 2]));
            }
            return [.. result];
        }

        public void Dispose() => _lines.Dispose();
    }

    private static GpuPassDescription WorldPass() => new()
    {
        Name = "vk-world",
        Color = new GpuColorAttachment(
            Target: null,
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.Store,
            ClearColor: default),
        Depth = new GpuDepthAttachment(
            Load: GpuLoadOp.Clear,
            Store: GpuStoreOp.DontCare,
            ClearDepth: 1f,
            ClearStencil: 0),
        SampleCount = 4,
    };

    private sealed class FixedCamera(Vector3 position) : IWorldFrameCameraSource
    {
        public WorldCameraFrame Resolve() => new(
            Camera: null!,
            Projection: Matrix4x4.Identity,
            ViewProjection: Matrix4x4.Identity,
            Frustum: default,
            InverseView: Matrix4x4.Identity,
            Position: position);
    }

    private sealed class FixedFrame(IGpuFrame frame) : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => frame;
    }

    private sealed class WorldPassScope : IWorldPassScope
    {
        public int SampleCount => 4;

        public IGpuPassEncoder? CurrentEncoder => null;

        public int AttachmentWidth => 1280;

        public int AttachmentHeight => 720;

        public WorldFrameSections Sections { get; } = new();

        public IGpuPassEncoder RequireEncoder() =>
            throw new InvalidOperationException("No world pass is open.");

        public void ClearInteriorDepth()
        {
        }

        public IDisposable Publish(IGpuPassEncoder encoder) =>
            throw new NotSupportedException();
    }
}
