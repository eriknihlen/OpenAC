using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class UiMenuPopupRoutingTests
{
    private sealed class ClickRecorder : UiElement
    {
        public int MouseDowns;
        public int Clicks;
        public override bool HandlesClick => true;
        public override bool OnEvent(in UiEvent e)
        {
            if (e.Type == UiEventType.MouseDown) { MouseDowns++; return true; }
            if (e.Type == UiEventType.Click) { Clicks++; return true; }
            return false;
        }
    }

    private static (UiRoot root, UiMenu menu, ClickRecorder stealer, Func<string?> selected)
        BuildOverlappingTree()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var panel = new UiPanel { Left = 0, Top = 0, Width = 300, Height = 300 };

        string? picked = null;
        var menu = new UiMenu
        {
            Left = 10, Top = 20, Width = 46, Height = 20,
            OpenUpward = false,
            Items = new[]
            {
                new UiMenu.MenuItem("A", "a"),
                new UiMenu.MenuItem("B", "b"),
            },
        };
        menu.OnSelect = p => picked = p as string;

        var stealer = new ClickRecorder { Left = 0, Top = 44, Width = 250, Height = 30 };

        panel.AddChild(menu);
        panel.AddChild(stealer);
        root.AddChild(panel);
        return (root, menu, stealer, () => picked);
    }

    private static void Click(UiRoot root, int x, int y)
    {
        root.OnMouseDown(UiMouseButton.Left, x, y);
        root.OnMouseUp(UiMouseButton.Left, x, y);
    }

    [Fact]
    public void OpenPopup_ItemClick_ReachesTheMenu_NotTheOverlappingFrontSibling()
    {
        var (root, menu, stealer, selected) = BuildOverlappingTree();

        Click(root, 30, 50);
        Assert.Equal(1, stealer.MouseDowns);

        Click(root, 30, 30);            // the menu button (screen 10..56 x 20..40)
        Assert.True(menu.IsOpen);

        Click(root, 30, 50);

        Assert.Equal("a", selected());
        Assert.False(menu.IsOpen);
        Assert.Equal(1, stealer.MouseDowns);   // unchanged — the popup won
        Assert.Equal(1, stealer.Clicks);
    }

    [Fact]
    public void OpenPopup_OutsidePress_DismissesAndSwallows_ThenNormalRoutingResumes()
    {
        var (root, menu, stealer, selected) = BuildOverlappingTree();

        Click(root, 30, 30);
        Assert.True(menu.IsOpen);

        Click(root, 230, 60);

        Assert.False(menu.IsOpen);
        Assert.Null(selected());
        Assert.Equal(0, stealer.MouseDowns);
        Assert.Equal(0, stealer.Clicks);

        // With the popup gone, the same spot routes normally again.
        Click(root, 230, 60);
        Assert.Equal(1, stealer.MouseDowns);
        Assert.Equal(1, stealer.Clicks);
    }

    [Fact]
    public void ReopeningAfterDismiss_StillRoutesItemClicks()
    {
        var (root, menu, _, selected) = BuildOverlappingTree();

        Click(root, 30, 30);            // open
        Click(root, 230, 60);           // dismiss (swallowed)
        Assert.False(menu.IsOpen);

        Click(root, 30, 30);            // reopen
        Assert.True(menu.IsOpen);
        Click(root, 30, 50);            // pick item A again
        Assert.Equal("a", selected());
    }

    [Fact]
    public void HidingThePopupsWindowWhileOpen_SelfHeals_NoStalePopupRouting()
    {
        var (root, menu, stealer, selected) = BuildOverlappingTree();

        Click(root, 30, 30);
        Assert.True(menu.IsOpen);

        menu.Parent!.Visible = false;
        Click(root, 230, 60);           // self-heal press (dismissed, swallowed)
        Assert.False(menu.IsOpen);

        Click(root, 230, 60);
        Assert.Equal(0, stealer.MouseDowns);

        menu.Parent!.Visible = true;
        Click(root, 230, 60);
        Assert.Equal(1, stealer.MouseDowns);
        Assert.Null(selected());
    }
}
