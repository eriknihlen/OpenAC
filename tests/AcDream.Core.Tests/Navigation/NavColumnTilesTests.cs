using AcDream.Core.Navigation;

namespace AcDream.Core.Tests.Navigation;

public sealed class NavColumnTilesTests
{
    [Fact]
    public void AColumnNeverWrittenReadsTheEmptyValueAndHoldsNoTile()
    {
        var tiles = new NavColumnTiles<int>(100, empty: -1);

        Assert.Equal(-1, tiles.Get(42, 17));
        Assert.Equal(0, tiles.AllocatedTiles);
        Assert.False(tiles.HasTile(42 / NavColumnTiles<int>.TileColumns, 17 / NavColumnTiles<int>.TileColumns));
    }

    [Fact]
    public void WritingAColumnAllocatesOnlyTheTileThatHoldsIt()
    {
        var tiles = new NavColumnTiles<int>(100, empty: -1);

        tiles.Slot(0, 0) = 7;
        tiles.Slot(99, 99) = 9;
        tiles.Slot(1, 1) = 8;

        Assert.Equal(2, tiles.AllocatedTiles);
        Assert.Equal(7, tiles.Get(0, 0));
        Assert.Equal(8, tiles.Get(1, 1));
        Assert.Equal(9, tiles.Get(99, 99));
        Assert.Equal(-1, tiles.Get(2, 2));
        Assert.Equal(-1, tiles.Get(50, 50));
        Assert.True(tiles.HasTile(0, 0));
        Assert.False(tiles.HasTile(1, 0));
    }

    [Fact]
    public void ASquareThatIsNotAWholeNumberOfTilesStillReachesItsLastColumn()
    {
        var tiles = new NavColumnTiles<bool>(17, empty: false);

        tiles.Slot(16, 16) = true;

        Assert.Equal(2, tiles.TilesPerSide);
        Assert.True(tiles.Get(16, 16));
        Assert.False(tiles.Get(15, 16));
    }
}
