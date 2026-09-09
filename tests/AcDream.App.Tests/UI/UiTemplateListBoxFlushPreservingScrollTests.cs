using AcDream.App.UI;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI;

public sealed class UiTemplateListBoxFlushPreservingScrollTests
{
    private static UiTemplateListBox MakeListBox(float width, float height)
    {
        var info = new ElementInfo { Id = 0x10000279u, Type = 5u };
        return new UiTemplateListBox(
            info,
            _ => (0u, 0, 0),
            new[] { new UiTemplateListEntry(0x21000030u, 0x10000281u) },
            scrollbarElementId: 0x1000027Au)
        {
            Width = width,
            Height = height,
        };
    }

    private static void AddRows(UiTemplateListBox box, int count, float rowHeight = 40f)
    {
        for (int i = 0; i < count; i++)
            box.AddPrebuiltRow(new UiText { Width = box.Width, Height = rowHeight });
    }

    [Fact]
    public void Flush_ResetsScrollToZero_TheOrdinaryContract()
    {
        var box = MakeListBox(200f, 100f);
        AddRows(box, 10);
        box.ViewportForTest!.ApplyAnchor(box.Width, box.Height);
        box.ViewportForTest!.LayoutScrollableChildren();
        box.Scroll.SetScrollY(150);
        Assert.Equal(150, box.Scroll.ScrollY);

        box.Flush();

        Assert.Equal(0, box.Scroll.ScrollY);
    }

    [Fact]
    public void FlushPreservingScroll_KeepsTheScrollOffset()
    {
        var box = MakeListBox(200f, 100f);
        AddRows(box, 10);
        box.ViewportForTest!.ApplyAnchor(box.Width, box.Height);
        box.ViewportForTest!.LayoutScrollableChildren();
        box.Scroll.SetScrollY(150);
        Assert.Equal(150, box.Scroll.ScrollY);

        box.FlushPreservingScroll();

        Assert.Equal(150, box.Scroll.ScrollY);

        AddRows(box, 10);
        box.ViewportForTest!.LayoutScrollableChildren();
        Assert.Equal(150, box.Scroll.ScrollY);
    }

    [Fact]
    public void FlushPreservingScroll_ClampsToTheNewShorterContent()
    {
        var box = MakeListBox(200f, 100f);
        AddRows(box, 10);
        box.ViewportForTest!.ApplyAnchor(box.Width, box.Height);
        box.ViewportForTest!.LayoutScrollableChildren();
        box.Scroll.SetScrollY(300);

        box.FlushPreservingScroll();
        // Rebuild with far FEWER rows (e.g. most of the fellowship left).
        AddRows(box, 2);
        box.ViewportForTest!.LayoutScrollableChildren();

        Assert.Equal(0, box.Scroll.ScrollY);
    }

    [Fact]
    public void FlushPreservingScroll_DormantBox_IsANoOp()
    {
        var box = MakeListBox(200f, 100f);
        box.FlushPreservingScroll();
        Assert.Null(box.ViewportForTest);
    }
}
