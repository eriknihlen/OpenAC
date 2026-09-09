using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiRectOutlinePainterOrderTests
{
    private sealed class TestElement : UiElement { }

    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer renderer, UiRenderContext ctx) MakeContext(float w, float h)
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(w, h));
        var ctx = new UiRenderContext(renderer, new Vector2(w, h));
        return (renderer, ctx);
    }

    [Fact]
    public void BackPanelBorder_ComposesUnderAFrontSpriteAddedLater()
    {
        var root = new TestElement { Width = 200f, Height = 200f };

        // Back window: added FIRST, drawn first (lower paint order). Its
        // border is the ONLY thing it draws — background left transparent so
        // any leaked geometry in the assertions below can only be the border.
        var backPanel = new UiPanel
        {
            Left = 0f, Top = 0f, Width = 100f, Height = 60f,
            BackgroundColor = default,
            BorderColor = new Vector4(1f, 1f, 1f, 1f),
            BorderThickness = 2f,
        };

        const uint frontTexture = 55u;
        var frontSprite = new UiSolidSpriteFill
        {
            Left = 0f, Top = 0f, Width = 100f, Height = 60f,
            SpriteId = frontTexture,
            SpriteResolve = id => (id, 8, 8),
        };

        root.AddChild(backPanel);
        root.AddChild(frontSprite);

        var (renderer, ctx) = MakeContext(200f, 200f);
        root.DrawSelfAndChildren(ctx);

        var segs = renderer.DebugSpriteSegmentVerts;

        int frontIndex = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].Texture == frontTexture) { frontIndex = i; break; }
        }
        Assert.True(frontIndex >= 0, "the front sprite must be recorded in the sprite bucket");

        int outlineIndex = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].Texture == UiTextureTableHandle.None) { outlineIndex = i; break; }
        }
        Assert.True(
            outlineIndex >= 0,
            "the back panel's border must route through the painter-order sprite bucket " +
            "(an untextured segment, UiTextureTableHandle.None), not the separate rect bucket");
        Assert.True(
            outlineIndex < frontIndex,
            "the back panel's border segment must be submitted BEFORE the front sprite's " +
            "segment so it composites underneath it, matching the actual paint order");

        const int expectedQuads = 4;
        const int expectedVertices = expectedQuads * 6;
        Assert.Equal(
            expectedVertices * TextRenderer.FloatsPerVertex,
            segs[outlineIndex].Verts.Count);

        // No outline geometry may land in TextRenderer's separate untextured
        // rect bucket at all: that bucket always flushes AFTER every sprite
        // segment regardless of submission order, which is exactly the bug —
        // an outline drawn there would win against every window painted after it.
        Assert.Equal(0, renderer.DebugRectVertexCount);
    }
}
