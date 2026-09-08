using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public sealed class UiItemListScrollTests
{
    private static UiItemList Grid(int items)
    {
        var list = new UiItemList { Columns = 6, CellWidth = 32, CellHeight = 32, Width = 192, Height = 96 };
        list.Flush();
        for (int i = 0; i < items; i++) list.AddItem(new UiItemSlot());
        list.LayoutCells();
        return list;
    }

    [Fact]
    public void RowCount_ceils_to_whole_rows()
    {
        Assert.Equal(0, UiItemList.RowCount(0, 6));
        Assert.Equal(1, UiItemList.RowCount(1, 6));
        Assert.Equal(7, UiItemList.RowCount(41, 6));
    }

    [Fact]
    public void Grid_drives_scroll_model_from_content()
    {
        var list = Grid(30);
        Assert.Equal(160, list.Scroll.ContentHeight);
        Assert.Equal(96, list.Scroll.ViewHeight);
        Assert.Equal(64, list.Scroll.MaxScroll);
        Assert.True(list.Scroll.HasOverflow);
    }

    [Fact]
    public void At_top_first_three_rows_visible_rest_clipped()
    {
        var list = Grid(30);
        Assert.True(list.GetItem(0)!.Visible);          // row 0, top 0
        Assert.True(list.GetItem(12)!.Visible);         // row 2, top 64 (+32 == 96)
        Assert.False(list.GetItem(18)!.Visible);        // row 3, top 96 (off the bottom)
    }

    [Fact]
    public void Scrolled_one_row_shifts_window_and_clips_top_row()
    {
        var list = Grid(30);
        list.Scroll.SetScrollY(32);
        list.LayoutCells();
        Assert.False(list.GetItem(0)!.Visible);         // row 0 scrolled above
        Assert.Equal(0f, list.GetItem(6)!.Top);         // row 1 now at the view top
        Assert.True(list.GetItem(18)!.Visible);         // row 3 now visible
    }

    [Fact]
    public void Pixel_scroll_keeps_partiallyVisible_edge_rows()
    {
        var list = Grid(30);

        list.Scroll.SetScrollY(8);
        list.LayoutCells();

        Assert.True(list.GetItem(0)!.Visible);
        Assert.Equal(-8f, list.GetItem(0)!.Top);
        Assert.True(list.GetItem(18)!.Visible);
        Assert.Equal(88f, list.GetItem(18)!.Top);
        Assert.False(list.GetItem(24)!.Visible);
        Assert.Null(list.HitTest(5f, -1f));
        Assert.IsType<UiItemSlot>(list.HitTest(5f, 1f));
    }

    [Fact]
    public void Deferred_rebuild_preserves_pixel_scroll_offset()
    {
        var list = Grid(30);
        list.Scroll.SetScrollY(40);

        using (list.DeferLayout())
        {
            list.Flush();
            for (int i = 0; i < 30; i++)
                list.AddItem(new UiItemSlot());
        }

        Assert.Equal(40, list.Scroll.ScrollY);
        Assert.Equal(-8f, list.GetItem(6)!.Top);
    }

    [Fact]
    public void Scroll_survives_the_per_frame_anchor_pass()
    {
        var list = Grid(30);
        for (int i = 0; i < list.GetNumUIItems(); i++)
            list.GetItem(i)!.ApplyAnchor(list.Width, list.Height);
        list.Scroll.SetScrollY(32);                           // scroll one row
        list.LayoutCells();
        for (int i = 0; i < list.GetNumUIItems(); i++)        // second anchor pass (the draw traversal)
            list.GetItem(i)!.ApplyAnchor(list.Width, list.Height);
        Assert.Equal(0f, list.GetItem(6)!.Top);               // row 1 stays at the view top
        Assert.False(list.GetItem(0)!.Visible);
    }

    [Fact]
    public void Wheel_down_scrolls_toward_the_bottom()
    {
        var list = Grid(30);                            // max scroll 64, LineHeight 32
        // Silk wheel +Y = up/older; UiText negates Data0. Wheel DOWN (Data0 < 0) → +ScrollY.
        var e = new UiEvent(0u, null, UiEventType.Scroll, Data0: -1);
        bool handled = list.OnEvent(e);
        Assert.True(handled);
        Assert.Equal(32, list.Scroll.ScrollY);          // one line (32px) down
    }
}
