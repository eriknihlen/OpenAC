using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public sealed class UiTemplateListBoxViewportTests
{
    private static UiTemplateListBox MakeListBox(float width, float height)
    {
        var info = new ElementInfo { Id = 0x100001FAu, Type = 5u };
        var box = new UiTemplateListBox(
            info,
            _ => (0u, 0, 0),
            new[] { new UiTemplateListEntry(0x2100002Bu, 0x10000218u) },
            scrollbarElementId: 0x100001FBu)
        {
            Width = width,
            Height = height,
        };
        return box;
    }

    [Fact]
    public void Viewport_FillsTheListBox_NotCollapsedToZero()
    {
        var box = MakeListBox(276f, 560f);
        box.AddPrebuiltRow(new UiText { Width = 260f, Height = 20f });
        box.AddPrebuiltRow(new UiText { Width = 260f, Height = 20f });

        UiScrollablePanel? viewport = box.ViewportForTest;
        Assert.NotNull(viewport);

        viewport!.ApplyAnchor(box.Width, box.Height);

        Assert.Equal(276f, viewport.Width, 3);
        Assert.Equal(560f, viewport.Height, 3);
    }

    [Fact]
    public void Rows_StayVisible_AfterLayout_WhenTheyFitTheViewport()
    {
        var box = MakeListBox(276f, 560f);
        var row0 = new UiText { Width = 260f, Height = 20f };
        var row1 = new UiText { Width = 260f, Height = 20f };
        var row2 = new UiText { Width = 260f, Height = 20f };
        box.AddPrebuiltRow(row0);
        box.AddPrebuiltRow(row1);
        box.AddPrebuiltRow(row2);

        UiScrollablePanel viewport = box.ViewportForTest!;
        viewport.ApplyAnchor(box.Width, box.Height); // size the viewport
        viewport.LayoutScrollableChildren();

        Assert.True(row0.Visible, "row 0 culled — the blank-tab regression");
        Assert.True(row1.Visible, "row 1 culled — the blank-tab regression");
        Assert.True(row2.Visible, "row 2 culled — the blank-tab regression");
    }

    [Fact]
    public void RemoveTail_PreservesPrefixAndRestacksFutureRows()
    {
        var box = MakeListBox(276f, 560f);
        var prefix = new UiText { Width = 260f, Height = 20f };
        var removedA = new UiText { Width = 260f, Height = 30f };
        var removedB = new UiText { Width = 260f, Height = 40f };
        box.AddPrebuiltRow(prefix);
        box.AddPrebuiltRow(removedA);
        box.AddPrebuiltRow(removedB);

        box.RemoveTail(1);
        var replacement = new UiText { Width = 260f, Height = 25f };
        box.AddPrebuiltRow(replacement);

        Assert.Equal(2, box.ItemCount);
        Assert.Same(prefix, box.ViewportForTest!.Children[0]);
        Assert.Same(replacement, box.ViewportForTest.Children[1]);
        Assert.Equal(20f, replacement.Top);
        Assert.Null(removedA.Parent);
        Assert.Null(removedB.Parent);
    }

    [Fact]
    public void Viewport_TracksTheListBox_WhenTheListBoxItselfShrinksOnFirstLayout()
    {
        var slotPolicy = new UiLayoutPolicy(
            leftMode: 1, topMode: 1, rightMode: 1, bottomMode: 1,
            originalChild: UiPixelRect.FromPositionAndSize(2, 25, 298, 575),
            originalParent: UiPixelRect.FromPositionAndSize(0, 0, 300, 600));
        var slot = new UiPanel
        {
            Left = 2, Top = 25, Width = 298, Height = 575,
            LayoutPolicy = slotPolicy,
        };
        var root = new UiPanel { Width = 300, Height = 362 };
        root.AddChild(slot);

        var box = MakeListBox(276f, 560f);
        var listBoxPolicy = new UiLayoutPolicy(
            leftMode: 1, topMode: 1, rightMode: 1, bottomMode: 1,
            originalChild: UiPixelRect.FromPositionAndSize(0, 0, 276, 560),
            originalParent: UiPixelRect.FromPositionAndSize(0, 0, 298, 575));
        box.LayoutPolicy = listBoxPolicy;
        slot.AddChild(box);

        box.AddPrebuiltRow(new UiText { Width = 260f, Height = 20f });
        UiScrollablePanel viewport = box.ViewportForTest!;

        slot.ApplyAnchor(root.Width, root.Height);   // slot shrinks: 575 -> ~337
        box.ApplyAnchor(slot.Width, slot.Height);    // listbox shrinks: 560 -> ~297 (still ahead of the viewport)
        viewport.ApplyAnchor(box.Width, box.Height);

        Assert.Equal(box.Height, viewport.Height, 3);
        Assert.True(
            viewport.Height < 400f,
            $"viewport height {viewport.Height} did not shrink with its ListBox "
                + "(560 == the pre-fix stale-capture bug)");
    }
}
