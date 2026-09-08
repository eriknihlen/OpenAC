using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiScrollablePanelTests
{
    [Fact]
    public void LayoutScrollableChildren_ClipsRowsOutsideViewport()
    {
        var panel = new UiScrollablePanel { Width = 100, Height = 40, LineHeight = 20 };
        for (int i = 0; i < 4; i++)
            panel.AddChild(new UiPanel { Top = i * 20, Width = 100, Height = 20 });

        panel.LayoutScrollableChildren();
        Assert.True(panel.Children[0].Visible);
        Assert.True(panel.Children[1].Visible);
        Assert.False(panel.Children[2].Visible);

        panel.Scroll.SetScrollY(20);
        panel.LayoutScrollableChildren();
        Assert.False(panel.Children[0].Visible);
        Assert.True(panel.Children[1].Visible);
        Assert.True(panel.Children[2].Visible);
    }

    [Fact]
    public void StraddlingRow_StaysVisible_AndClipsInsteadOfVanishing()
    {
        var panel = new UiScrollablePanel { Width = 300, Height = 430, LineHeight = 20 };
        var header = new UiPanel { Top = 0, Width = 300, Height = 18 };
        var mainBlock = new UiPanel { Top = 18, Width = 300, Height = 260 };
        var header2 = new UiPanel { Top = 278, Width = 300, Height = 18 };
        var straddler = new UiPanel { Top = 296, Width = 300, Height = 260 };  // 296..556 in a 430 viewport
        var below = new UiPanel { Top = 556, Width = 300, Height = 260 };      // fully outside
        foreach (var c in new[] { header, mainBlock, header2, straddler, below })
            panel.AddChild(c);

        panel.LayoutScrollableChildren();

        Assert.True(header.Visible);
        Assert.True(mainBlock.Visible);
        Assert.True(header2.Visible);
        Assert.True(straddler.Visible);   // the pre-fix bug: this was false
        Assert.False(below.Visible);

        // Scrolling far enough hides the first block entirely and keeps the
        // straddler fully in view — intersection semantics on both edges.
        panel.Scroll.SetScrollY(296);
        panel.LayoutScrollableChildren();
        Assert.False(mainBlock.Visible);  // now fully above the viewport
        Assert.True(straddler.Visible);
    }

    [Fact]
    public void ViewportClipsChildDrawingAndHitTesting()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiScrollablePanel { Left = 0, Top = 0, Width = 300, Height = 100, LineHeight = 20 };
        var row = new UiPanel { Top = 60, Width = 300, Height = 120 };  // 60..180, viewport ends at 100
        panel.AddChild(row);
        root.AddChild(panel);
        panel.LayoutScrollableChildren();

        Assert.True(row.Visible);
        Assert.Same(row, root.Pick(150, 80));
        Assert.NotSame(row, root.Pick(150, 140) ?? panel);
    }

    [Fact]
    public void ScrollEvent_MovesByLineHeight()
    {
        var panel = new UiScrollablePanel { Width = 100, Height = 40, LineHeight = 20 };
        for (int i = 0; i < 4; i++)
            panel.AddChild(new UiPanel { Top = i * 20, Width = 100, Height = 20 });
        panel.LayoutScrollableChildren();

        var e = new UiEvent(0u, panel, UiEventType.Scroll, Data0: -1);
        Assert.True(panel.OnEvent(in e));

        Assert.Equal(20, panel.Scroll.ScrollY);
    }
}
