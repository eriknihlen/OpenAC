using AcDream.App.UI;
using Xunit;

namespace AcDream.App.Tests.UI;

public class UiItemListTests
{
    [Fact]
    public void CooldownPresentation_propagates_to_existing_and_future_cells()
    {
        var list = new UiItemList();
        uint[] sprites = Enumerable.Range(1, 10).Select(i => (uint)i).ToArray();
        Func<uint, int> provider = _ => 7;

        list.CooldownSprites = sprites;
        list.CooldownStepProvider = provider;
        var future = new UiItemSlot();
        list.AddItem(future);

        Assert.Same(sprites, list.Cell.CooldownSprites);
        Assert.Same(provider, list.Cell.CooldownStepProvider);
        Assert.Same(sprites, future.CooldownSprites);
        Assert.Same(provider, future.CooldownStepProvider);
    }

    [Fact]
    public void IsLeafWidget() => Assert.True(new UiItemList().ConsumesDatChildren);

    [Fact]
    public void StartsWithOneCell_forSingleCellSlot()
    {
        var list = new UiItemList();
        Assert.Equal(1, list.GetNumUIItems());
        Assert.NotNull(list.GetItem(0));
    }

    [Fact]
    public void Cell_returnsTheFirstSlot()
    {
        var list = new UiItemList();
        Assert.Same(list.GetItem(0), list.Cell);
    }

    [Fact]
    public void CellEmptySprite_stamps_existing_and_future_cells()
    {
        var list = new UiItemList();
        Assert.Equal(0x060074CFu, list.GetItem(0)!.EmptySprite);     // UiItemSlot default before set

        list.CellEmptySprite = 0x06004D20u;                          // a pack-slot sprite
        Assert.Equal(0x06004D20u, list.GetItem(0)!.EmptySprite);

        list.AddItem(new UiItemSlot());
        Assert.Equal(0x06004D20u, list.GetItem(1)!.EmptySprite);
    }

    [Fact]
    public void CellEmptySprite_zero_leaves_the_UiItemSlot_default()
    {
        var list = new UiItemList();
        list.CellEmptySprite = 0u;
        Assert.Equal(0x060074CFu, list.GetItem(0)!.EmptySprite);     // unchanged default
    }

    [Fact]
    public void CatalogDrop_routes_payload_and_local_coordinates()
    {
        var list = new UiItemList();
        object payload = new();
        (object Payload, int X, int Y)? received = null;
        list.CatalogDropped = (value, x, y) => received = (value, x, y);

        bool handled = list.OnEvent(new UiEvent(
            0, list, UiEventType.DropReleased, Data1: 17, Data2: 9, Payload: payload));

        Assert.True(handled);
        Assert.NotNull(received);
        Assert.Same(payload, received.Value.Payload);
        Assert.Equal((17, 9), (received.Value.X, received.Value.Y));
    }

    [Fact]
    public void RetailHorizontalEmptySlots_FillViewport_AndShrinkOnlyEmptyTail()
    {
        var list = new UiItemList
        {
            Width = 130f,
            Height = 32f,
            CellWidth = 32f,
            CellHeight = 32f,
            SingleRow = true,
            FillVisibleEmptySlots = true,
            EmptySlotFactory = () => new UiCatalogSlot(),
        };
        list.Flush();
        list.AddItem(new UiCatalogSlot { EntryId = 42u });
        list.LayoutCells();

        Assert.Equal(4, list.GetNumUIItems());
        Assert.Equal((0f, 32f, 64f, 96f),
            (list.GetItem(0)!.Left, list.GetItem(1)!.Left,
             list.GetItem(2)!.Left, list.GetItem(3)!.Left));

        list.Width = 65f;
        list.LayoutCells();
        Assert.Equal(2, list.GetNumUIItems());
        Assert.Equal(42u, Assert.IsType<UiCatalogSlot>(list.GetItem(0)).EntryId);

        list.Width = 16f;
        list.LayoutCells();
        Assert.Single(list.Children);
    }

    [Fact]
    public void ScrollItemIntoView_moves_only_when_row_is_outside_viewport()
    {
        var list = new UiItemList
        {
            Width = 100f,
            Height = 64f,
            Columns = 1,
            CellWidth = 100f,
            CellHeight = 32f,
        };
        list.Flush();
        for (int i = 0; i < 5; i++) list.AddItem(new UiItemSlot());

        list.ScrollItemIntoView(3);
        Assert.Equal(64, list.Scroll.ScrollY);
        list.ScrollItemIntoView(2);
        Assert.Equal(64, list.Scroll.ScrollY);
        list.ScrollItemIntoView(0);
        Assert.Equal(0, list.Scroll.ScrollY);
    }

    [Fact]
    public void HorizontalScroll_UsesPixelOffsetAndKeepsPartialEdgeCells()
    {
        var list = new UiItemList
        {
            Width = 80f,
            Height = 32f,
            CellWidth = 32f,
            CellHeight = 32f,
            SingleRow = true,
            HorizontalScroll = true,
        };
        list.Flush();
        for (int i = 0; i < 4; i++) list.AddItem(new UiItemSlot());

        list.Scroll.SetScrollY(17);
        list.LayoutCells();

        Assert.Equal(128, list.Scroll.ContentHeight);
        Assert.Equal(80, list.Scroll.ViewHeight);
        Assert.Equal(-17f, list.GetItem(0)!.Left);
        Assert.True(list.GetItem(0)!.Visible);
        Assert.True(list.GetItem(3)!.Visible);
        Assert.False(list.GetItem(2) is null);

        list.ScrollItemIntoView(3);
        Assert.Equal(48, list.Scroll.ScrollY);
        list.LayoutCells();
        Assert.Equal(48f, list.GetItem(3)!.Left);
        Assert.Equal(80f, list.GetItem(3)!.Left + list.GetItem(3)!.Width);
    }
}
