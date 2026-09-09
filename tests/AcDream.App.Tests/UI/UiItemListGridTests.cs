using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public class UiItemListGridTests
{
    [Fact]
    public void CellOffset_RowMajor()
    {
        Assert.Equal((0f, 0f),   UiItemList.CellOffset(0, 3, 36, 36));
        Assert.Equal((72f, 0f),  UiItemList.CellOffset(2, 3, 36, 36));
        Assert.Equal((36f, 36f), UiItemList.CellOffset(4, 3, 36, 36)); // col 1, row 1
    }

    [Fact]
    public void CellOffset_ColumnMajor_UsesLogicalRows()
    {
        Assert.Equal((0f, 0f),   UiItemList.CellOffset(0, 3, 7, UiItemListFlow.ColumnMajor, 36, 36));
        Assert.Equal((0f, 36f),  UiItemList.CellOffset(1, 3, 7, UiItemListFlow.ColumnMajor, 36, 36));
        Assert.Equal((36f, 0f),  UiItemList.CellOffset(3, 3, 7, UiItemListFlow.ColumnMajor, 36, 36));
    }

    [Fact]
    public void GridMode_PositionsCellsInColumns()
    {
        var list = new UiItemList { Columns = 3, CellWidth = 36, CellHeight = 36 };
        list.Flush();
        for (int i = 0; i < 7; i++) list.AddItem(new UiItemSlot());

        var c4 = list.GetItem(4)!;
        Assert.Equal(36f, c4.Left);
        Assert.Equal(36f, c4.Top);
        Assert.Equal(36f, c4.Width);
        Assert.Equal(36f, c4.Height);
    }

    [Fact]
    public void GridMode_ColumnMajor_PositionsCellsDownRowsFirst()
    {
        var list = new UiItemList
        {
            Columns = 3,
            Flow = UiItemListFlow.ColumnMajor,
            CellWidth = 36,
            CellHeight = 36,
        };
        list.Flush();
        for (int i = 0; i < 7; i++) list.AddItem(new UiItemSlot());

        var c3 = list.GetItem(3)!;
        Assert.Equal(36f, c3.Left);
        Assert.Equal(0f, c3.Top);
    }


    [Fact]
    public void FillMode_SizesSingleCellToList()
    {
        var list = new UiItemList { Width = 36, Height = 36 };
        list.Flush();
        list.AddItem(new UiItemSlot());

        var c = list.Cell;
        Assert.Equal(0f, c.Left);
        Assert.Equal(0f, c.Top);
        Assert.Equal(36f, c.Width);
        Assert.Equal(36f, c.Height);
    }
}
