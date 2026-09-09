using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ConnectionMeterAnimationTests
{
    [Fact]
    public void Animation_ShowsEveryAuthoredFrame_AndRestartsAtTheCompletingDraw()
    {
        var layout = LayoutImporter.Build(FixtureLoader.LoadConnectionInfos(),
            id => (id, 325, 46), null);
        var meter = Assert.IsType<UiMeter>(layout.FindElement(0x1000041Eu));
        uint[] expectedBack = [0x06001966, 0x06001967, 0x06001968, 0x06001969, 0x0600196A,
            0x0600196B, 0x0600196C, 0x0600196D, 0x0600196E, 0x0600196F,
            0x06001975, 0x06001976, 0x06001977, 0x06001978, 0x06001979];
        for (int index = 0; index < expectedBack.Length; index++)
        {
            double time = index == 14 ? 0.99d : index / 14d;
            Assert.Equal(expectedBack[index], meter.SampleAnimatedTracks(time).Back);
        }
        Assert.Equal(expectedBack[0], meter.SampleAnimatedTracks(0.035d).Back);
        Assert.Equal(expectedBack[1], meter.SampleAnimatedTracks(0.036d).Back);

        meter.AdvanceAnimatedTracks(10d);
        meter.AdvanceAnimatedTracks(10.99d);
        Assert.Equal(expectedBack[14], meter.BackTile);
        meter.AdvanceAnimatedTracks(11.1d);
        Assert.Equal(expectedBack[0], meter.BackTile);
        meter.AdvanceAnimatedTracks(11.136d);
        Assert.Equal(expectedBack[1], meter.BackTile);
    }

    [Theory]
    [InlineData(0x1000041Eu)]
    [InlineData(0x1000041Fu)]
    public void ImportedMeter_PreservesInheritedTrackAndFillAnimations(uint elementId)
    {
        var layout = LayoutImporter.Build(FixtureLoader.LoadConnectionInfos(),
            id => (id, 325, 46), null);
        var meter = Assert.IsType<UiMeter>(layout.FindElement(elementId));

        Assert.Equal((0x06001966u, 0x0600195Cu), meter.SampleAnimatedTracks(0d));
        Assert.Equal((0x0600196Du, 0x06001963u), meter.SampleAnimatedTracks(0.5d));
        Assert.Equal(meter.SampleAnimatedTracks(0.5d), meter.SampleAnimatedTracks(10000.5d));
        Assert.Empty(meter.Children);
    }

    [Theory]
    [InlineData(0f, 1, 0f)]
    [InlineData(0.5f, 2, 162.5f)]
    [InlineData(1f, 2, 325f)]
    public void AnimatedMeter_DrawsTrackAndClipsFillToProgress(float progress, int segments, float width)
    {
        var layout = LayoutImporter.Build(FixtureLoader.LoadConnectionInfos(),
            id => (id, 325, 46), null);
        var meter = Assert.IsType<UiMeter>(layout.FindElement(0x1000041Eu));
        meter.Left = 0f;
        meter.Top = 0f;
        meter.Fill = () => progress;
        using var device = new RecordingGpuDevice();
        using var renderer = new TextRenderer(device, new NullFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        meter.DrawSelfAndChildren(new UiRenderContext(renderer, new Vector2(800f, 600f)));

        var draws = renderer.DebugSpriteSegmentVerts;
        Assert.Equal(segments, draws.Count);
        Assert.Equal(0x06001966u, draws[0].Texture);
        Assert.Equal(0, renderer.DebugRectVertexCount);
        if (progress > 0f)
        {
            Assert.Equal(0x0600195Cu, draws[1].Texture);
            var vertices = draws[1].Verts;
            float maximumX = Enumerable.Range(0, vertices.Count / TextRenderer.FloatsPerVertex)
                .Max(index => vertices[index * TextRenderer.FloatsPerVertex]);
            Assert.Equal(width, maximumX);
        }
    }

    [Fact]
    public void UnrelatedMeterGeometry_DoesNotConsumeAnimationTracks()
    {
        ElementInfo root = FixtureLoader.LoadConnectionInfos();
        ElementInfo meterInfo = Assert.Single(root.Children, child => child.Id == 0x1000041Eu);
        Assert.Single(meterInfo.Children).X = 10f;
        var layout = LayoutImporter.Build(root, id => (id, 325, 46), null);
        var meter = Assert.IsType<UiMeter>(layout.FindElement(0x1000041Eu));
        Assert.Equal((0u, 0u), meter.SampleAnimatedTracks(0.5d));
    }

    private sealed class NullFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }
}
