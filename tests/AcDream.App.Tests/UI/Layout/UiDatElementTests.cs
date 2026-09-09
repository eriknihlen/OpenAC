using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
namespace AcDream.App.Tests.UI.Layout;

public class UiDatElementTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    private static (TextRenderer renderer, UiRenderContext ctx) BuildRenderContext()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(1920f, 1080f));
        var ctx = new UiRenderContext(renderer, new Vector2(1920f, 1080f));
        return (renderer, ctx);
    }

    [Fact]
    public void OwnBackground_TilesUvPastOne_WhenRectExceedsNativeSize()
    {
        var info = new ElementInfo { Width = 1920, Height = 1080 };
        info.StateMedia[""] = (0x06007576u, 1);
        var e = new UiDatElement(info, _ => (7u, 800, 600))
        {
            Left = 0,
            Top = 0,
            Width = 1920,
            Height = 1080,
        };

        (TextRenderer renderer, UiRenderContext ctx) = BuildRenderContext();
        e.DrawSelfAndChildren(ctx);

        var (texture, verts) = Assert.Single(renderer.DebugSpriteSegmentVerts);
        Assert.Equal(7u, texture);
        float uMax = verts[1 * 8 + 2];
        float vMax = verts[1 * 8 + 3];
        Assert.Equal(1920f / 800f, uMax, 3);
        Assert.Equal(1080f / 600f, vMax, 3);
    }

    [Fact]
    public void CanvasScale_StretchesQuadGeometry_LeavesUvsAuthored()
    {
        var info = new ElementInfo { Width = 800, Height = 600 };
        info.StateMedia[""] = (0x06007576u, 1);
        var e = new UiDatElement(info, _ => (7u, 800, 600))
        {
            Left = 0,
            Top = 0,
            Width = 800,
            Height = 600,
        };

        (TextRenderer renderer, UiRenderContext ctx) = BuildRenderContext();
        renderer.CanvasScale = new Vector2(1920f / 800f, 1080f / 600f);
        try
        {
            e.DrawSelfAndChildren(ctx);
        }
        finally
        {
            renderer.CanvasScale = Vector2.One;
        }

        var (_, verts) = Assert.Single(renderer.DebugSpriteSegmentVerts);
        Assert.Equal(1920f, verts[1 * 8 + 0], 3);
        Assert.Equal(1080f, verts[1 * 8 + 1], 3);
        Assert.Equal(1f, verts[1 * 8 + 2], 3);
        Assert.Equal(1f, verts[1 * 8 + 3], 3);
    }

    [Fact]
    public void ActiveMedia_PrefersNamedStateOverDirect()
    {
        var info = new ElementInfo();
        info.StateMedia[""] = (0x06000001, 1);          // DirectState (DrawMode Normal=1)
        info.StateMedia["ShowDetail"] = (0x06000002, 3); // named (Alphablend=3)
        var e = new UiDatElement(info, _ => (0, 0, 0)) { ActiveState = "ShowDetail" };
        Assert.Equal(0x06000002u, e.ActiveMedia().File);
        Assert.Equal(3, e.ActiveMedia().DrawMode);
        e.ActiveState = "";
        Assert.Equal(0x06000001u, e.ActiveMedia().File);
        Assert.Equal(1, e.ActiveMedia().DrawMode);
    }

    [Fact]
    public void ActiveMedia_NoMedia_ReturnsZero()
    {
        var e = new UiDatElement(new ElementInfo(), _ => (0, 0, 0));
        Assert.Equal(0u, e.ActiveMedia().File);
        Assert.Equal(0, e.ActiveMedia().DrawMode);
    }

    [Fact]
    public void ActiveMedia_MissingNamedState_FallsBackToDirect()
    {
        var info = new ElementInfo();
        info.StateMedia[""] = (0x06000005, 1);
        var e = new UiDatElement(info, _ => (0, 0, 0)) { ActiveState = "NoSuchState" };
        Assert.Equal(0x06000005u, e.ActiveMedia().File);
    }

    // ── G1 tests: DefaultStateName + "Normal" implicit default ───────────────

    [Fact]
    public void UiDatElement_DefaultsActiveStateToNormal_WhenNormalPresent()
    {
        var info = new ElementInfo();
        info.StateMedia["Normal"] = (0x0000AAAAu, 1);
        info.StateMedia["Hover"]  = (0x0000BBBBu, 1);

        var e = new UiDatElement(info, _ => (0, 0, 0));

        // Should have defaulted to "Normal" state.
        Assert.Equal(0x0000AAAAu, e.ActiveMedia().File);
    }

    [Fact]
    public void UiDatElement_DefaultsActiveStateToDefaultStateName_WhenSet()
    {
        var info = new ElementInfo { DefaultStateName = "Minimized" };
        info.StateMedia["Minimized"] = (0x0000BBBBu, 1);
        info.StateMedia["Maximized"] = (0x0000CCCCu, 1);
        info.StateMedia["Normal"]    = (0x0000DDDDu, 1);

        var e = new UiDatElement(info, _ => (0, 0, 0));

        // DefaultStateName "Minimized" wins over "Normal" implicit default.
        Assert.Equal(0x0000BBBBu, e.ActiveMedia().File);
    }

    [Fact]
    public void UiDatElement_NoDefaultStateName_NoNormal_DefaultsToDirectState()
    {
        var info = new ElementInfo();
        info.StateMedia[""] = (0x06007777u, 1);

        var e = new UiDatElement(info, _ => (0, 0, 0));

        // No DefaultStateName, no "Normal" state → ActiveState stays "" (DirectState).
        Assert.Equal(0x06007777u, e.ActiveMedia().File);
    }

    [Fact]
    public void NumericStateBridge_SetsNamedStateWithoutChangingGeometry()
    {
        var info = new ElementInfo { X = 4, Y = 5, Width = 30, Height = 40 };
        info.States[RetailUiStateIds.ShowDetail] = new UiStateInfo
        {
            Id = RetailUiStateIds.ShowDetail,
            Name = "ShowDetail",
            Image = new UiImageMedia(0x06000009u, 3),
        };
        info.StateMedia["ShowDetail"] = (0x06000009u, 3);
        var element = new UiDatElement(info, _ => (0u, 0, 0))
        {
            Left = info.X,
            Top = info.Y,
            Width = info.Width,
            Height = info.Height,
        };

        Assert.True(element.TrySetRetailState(RetailUiStateIds.ShowDetail));

        Assert.Equal(RetailUiStateIds.ShowDetail, element.ActiveRetailStateId);
        Assert.Equal((0x06000009u, 3), element.ActiveMedia());
        Assert.Equal((4f, 5f, 30f, 40f),
            (element.Left, element.Top, element.Width, element.Height));
    }

    [Fact]
    public void NumericStateBridge_MissingStateCommitsBaseState()
    {
        var info = new ElementInfo { DefaultStateName = "Normal" };
        info.StateMedia["Normal"] = (0x06000001u, 1);
        var element = new UiDatElement(info, _ => (0u, 0, 0));

        Assert.True(element.TrySetRetailState(RetailUiStateIds.ShowDetail));
        Assert.Equal("", element.ActiveState);
    }

    [Fact]
    public void TrySetRetailState_UnauthoredNormal_ClearsRolloverMedia()
    {
        var info = new ElementInfo();
        info.States[UiStateInfo.DirectStateId] =
            new UiStateInfo { Id = UiStateInfo.DirectStateId };
        info.States[2u] = new UiStateInfo { Id = 2u, Name = "Normal_rollover" };
        info.StateMedia["Normal_rollover"] = (0x06005EB6u, 1);
        var element = new UiDatElement(info, _ => (0u, 0, 0));

        Assert.True(element.TrySetRetailState(2u));
        Assert.Equal("Normal_rollover", element.ActiveState);

        Assert.True(element.TrySetRetailState(UiButtonStateMachine.Normal));
        Assert.Equal("", element.ActiveState);
        Assert.Equal((0u, 0), element.ActiveMedia());
    }

    [Fact]
    public void TrySetRetailState_UnauthoredStateFallback_RestoresBaseInvisible()
    {
        var info = new ElementInfo();
        var baseState = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        baseState.Properties.Values[0x3Bu] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = true,
        };
        info.States[UiStateInfo.DirectStateId] = baseState;
        info.StateMedia["Normal_rollover"] = (0x06005EB6u, 1);
        var element = new UiDatElement(info, _ => (0u, 0, 0));
        element.Visible = true;

        Assert.True(element.TrySetRetailState(UiButtonStateMachine.Normal));

        Assert.Equal("", element.ActiveState);
        Assert.False(element.Visible);
    }

    [Fact]
    public void TrySetRetailState_UnauthoredStateFallback_CascadesBasePerBaseDescriptor()
    {
        var childInfo = new ElementInfo();
        childInfo.StateMedia[""] = (0x06000002u, 1);
        childInfo.StateMedia["Normal_rollover"] = (0x06005EB6u, 1);
        childInfo.States[2u] = new UiStateInfo { Id = 2u, Name = "Normal_rollover" };
        var child = new UiDatElement(childInfo, _ => (0u, 0, 0));
        Assert.True(child.TrySetRetailState(2u));

        var parentInfo = new ElementInfo();
        parentInfo.States[UiStateInfo.DirectStateId] = new UiStateInfo
        {
            Id = UiStateInfo.DirectStateId,
            PassToChildren = true,
        };
        var parent = new UiDatElement(parentInfo, _ => (0u, 0, 0));
        parent.AddChild(child);

        Assert.True(parent.TrySetRetailState(UiButtonStateMachine.Normal));

        Assert.Equal("", child.ActiveState);
    }
}
