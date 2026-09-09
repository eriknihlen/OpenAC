using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed partial class WalkFrameDriverTests
{
    private static WalkScreenPoint[] NdcToPixelPoints(Vector2[] ndcVerts, float width, float height)
    {
        var points = new WalkScreenPoint[ndcVerts.Length];
        for (int i = 0; i < ndcVerts.Length; i++)
        {
            float px = (ndcVerts[i].X + 1f) * width / 2f;
            float py = (1f - ndcVerts[i].Y) * height / 2f;
            points[i] = new WalkScreenPoint(px, py, 0f, 1f);
        }
        return points;
    }

    [Fact]
    public void ExitSealPath_PlanesMatchClipPlaneSetsIndependentComputation_ThroughCaptureViewsAndAppendClipSlot()
    {
        using var fx = new DispatcherFixture();
        var ctx = new TestContext();
        using ClipFrame clipFrame = ClipFrame.NoClip();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(new List<string>()), new FakeWorldData(), clipFrame: clipFrame);

        const uint cellId = 0x100u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();

        Vector2[] verts =
        [
            new(-0.3f, 0.5f), new(-0.6f, -0.2f), new(0.2f, -0.5f), new(0.6f, 0.3f),
        ];
        Assert.True(WalkCopyView.Append(
            cell.TopView,
            NdcToPixelPoints(verts, ctx.ViewportWidth, ctx.ViewportHeight),
            ctx.Rays,
            ctx.WorldViewpoint));
        ctx.Cells[cellId] = cell;

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        ((IWalkEventSink)driver).OnInteriorFloodDrawTurn(new[] { cellId }, outsideViewCount: 1);
        driver.EndFrame();

        Assert.Equal(1, driver.InteriorFloodViewSliceCountAt(0));
        ReadOnlySpan<Vector4> planes = driver.InteriorFloodViewClipPlanesAt(0, 0);

        Vector4[] expected = ClipPlaneSet.From(new ViewPolygon(verts)).PlaneArray;
        Assert.Equal(expected.Length, planes.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, planes[i].X, 4);
            Assert.Equal(expected[i].Y, planes[i].Y, 4);
            Assert.Equal(expected[i].Z, planes[i].Z, 4);
            Assert.Equal(expected[i].W, planes[i].W, 4);
        }
    }

    [Fact]
    public void ExitSealPath_NineVertexView_UsesFourAabbPlanesContainingEveryVertex()
    {
        using var fx = new DispatcherFixture();
        var ctx = new TestContext();
        using ClipFrame clipFrame = ClipFrame.NoClip();
        var driver = new WalkFrameDriver(
            fx.Dispatcher, new RecordingLeafRenderer(new List<string>()), new FakeWorldData(), clipFrame: clipFrame);

        const uint cellId = 0x100u;
        var cell = new WalkCell { CellId = cellId };
        cell.PushView();

        const int n = 9;
        var verts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float angle = i * MathF.Tau / n;
            verts[i] = new Vector2(0.7f * MathF.Cos(angle), 0.7f * MathF.Sin(angle));
        }
        Assert.True(WalkCopyView.Append(
            cell.TopView,
            NdcToPixelPoints(verts, ctx.ViewportWidth, ctx.ViewportHeight),
            ctx.Rays,
            ctx.WorldViewpoint));
        Assert.Equal(n, cell.TopView.View.Polys[0].VertexCount);
        ctx.Cells[cellId] = cell;

        driver.BeginFrame(ctx, Matrix4x4.Identity, Vector3.Zero);
        ((IWalkEventSink)driver).OnInteriorFloodDrawTurn(new[] { cellId }, outsideViewCount: 1);
        driver.EndFrame();

        ReadOnlySpan<Vector4> planes = driver.InteriorFloodViewClipPlanesAt(0, 0);
        Assert.Equal(4, planes.Length);

        float minX = verts.Min(v => v.X), maxX = verts.Max(v => v.X);
        float minY = verts.Min(v => v.Y), maxY = verts.Max(v => v.Y);

        // Over-include: every source vertex satisfies every plane.
        foreach (Vector2 v in verts)
        {
            var clip = new Vector4(v.X, v.Y, 0f, 1f);
            for (int p = 0; p < planes.Length; p++)
                Assert.True(Vector4.Dot(planes[p], clip) >= -1e-3f);
        }

        // Exactly the four axis-aligned NDC bounds, matching AppendClipSlot's own formula.
        Vector4[] expected =
        [
            new(1f, 0f, 0f, -minX), new(-1f, 0f, 0f, maxX),
            new(0f, 1f, 0f, -minY), new(0f, -1f, 0f, maxY),
        ];
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i].X, planes[i].X, 3);
            Assert.Equal(expected[i].Y, planes[i].Y, 3);
            Assert.Equal(expected[i].Z, planes[i].Z, 3);
            Assert.Equal(expected[i].W, planes[i].W, 2);
        }
    }
}
