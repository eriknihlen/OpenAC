using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class TextRendererLinearTwinTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static TextRenderer BuildRenderer()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        return renderer;
    }

    [Fact]
    public void CanvasScaleOne_DrawSprite_NeverConsultsResolver_KeepsOriginalHandle()
    {
        TextRenderer renderer = BuildRenderer();
        renderer.LinearTwinResolver = _ => throw new System.InvalidOperationException(
            "LinearTwinResolver must not be consulted while CanvasScale == One.");

        renderer.DrawSprite(5u, 0, 0, 10, 10, 0, 0, 1, 1, Vector4.One);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(5u, seg.Texture);
    }

    [Fact]
    public void CanvasScaleNotOne_DrawSprite_SwapsHandleThroughResolver()
    {
        TextRenderer renderer = BuildRenderer();
        renderer.CanvasScale = new Vector2(2.4f, 1.8f);
        renderer.LinearTwinResolver = handle => handle == 5u ? 999u : handle;

        renderer.DrawSprite(5u, 0, 0, 10, 10, 0, 0, 1, 1, Vector4.One);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(999u, seg.Texture);
    }

    [Fact]
    public void CanvasScaleNotOne_ResolverIsIdentityForUnknownHandles()
    {
        TextRenderer renderer = BuildRenderer();
        renderer.CanvasScale = new Vector2(2.4f, 1.8f);
        renderer.LinearTwinResolver = handle => handle == 5u ? 999u : handle;

        renderer.DrawSprite(7u, 0, 0, 10, 10, 0, 0, 1, 1, Vector4.One);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(7u, seg.Texture);
    }

    [Fact]
    public void CanvasScaleNotOne_NoResolverWired_KeepsOriginalHandle_DoesNotThrow()
    {
        TextRenderer renderer = BuildRenderer();
        renderer.CanvasScale = new Vector2(2.4f, 1.8f);

        renderer.DrawSprite(5u, 0, 0, 10, 10, 0, 0, 1, 1, Vector4.One);

        var seg = Assert.Single(renderer.DebugSpriteSegments);
        Assert.Equal(5u, seg.Texture);
    }
}
