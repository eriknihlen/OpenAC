using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiCollapsibleFrameTests
{
    private static UiCollapsibleFrame MakeFrame(out UiPanel row2a, out UiPanel row2b)
    {
        var f = new UiCollapsibleFrame(_ => (1u, 1, 1))
        {
            CollapsedHeight = 96f,
            ExpandedHeight  = 128f,
        };
        row2a = new UiPanel(); row2b = new UiPanel();
        f.SecondRow = new UiElement[] { row2a, row2b };
        return f;
    }

    [Fact]
    public void Tick_belowMidpoint_snapsCollapsed_hidesSecondRow()
    {
        var f = MakeFrame(out var a, out var b);
        f.Height = 100f;
        f.TickForTest(0.016);
        Assert.Equal(96f, f.Height);
        Assert.False(a.Visible);
        Assert.False(b.Visible);
        Assert.False(f.IsExpanded);
    }

    [Fact]
    public void Tick_aboveMidpoint_snapsExpanded_showsSecondRow()
    {
        var f = MakeFrame(out var a, out var b);
        f.Height = 120f;                 // nearer the expanded stop
        f.TickForTest(0.016);
        Assert.Equal(128f, f.Height);
        Assert.True(a.Visible);
        Assert.True(b.Visible);
        Assert.True(f.IsExpanded);
    }

    [Fact]
    public void Tick_notConfigured_isNoOp()
    {
        var f = new UiCollapsibleFrame(_ => (1u, 1, 1));
        f.Height = 50f;
        f.TickForTest(0.016);
        Assert.Equal(50f, f.Height);     // unchanged, no divide/no forced height
    }

    [Fact]
    public void HitEdges_respectsResizableEdgesMask_bottomOnly()
    {
        var panel = new UiPanel { Left = 100, Top = 100, Width = 200, Height = 100,
            Resizable = true, ResizableEdges = ResizeEdges.Bottom };
        // bottom edge (y=200) → Bottom only
        Assert.Equal(ResizeEdges.Bottom, UiRoot.HitEdges(panel, 200, 200, 5));
        // top edge (y=100) → masked out → None
        Assert.Equal(ResizeEdges.None, UiRoot.HitEdges(panel, 200, 100, 5));
    }
}
