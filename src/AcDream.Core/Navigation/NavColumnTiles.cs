namespace AcDream.Core.Navigation;

/// <summary>
/// One value for each column of a square of columns, kept in small square tiles
/// that are allocated the first time a value is written into them, so a region
/// holds memory only where something was written rather than over its whole square.
/// </summary>
internal sealed class NavColumnTiles<T>
    where T : unmanaged
{
    /// <summary>Columns along each edge of a tile: four metres at the default cell size.</summary>
    internal const int TileColumns = 1 << TileShift;

    private const int TileShift = 4;
    private const int TileMask = TileColumns - 1;

    private readonly T[]?[] _tiles;
    private readonly T _empty;

    public NavColumnTiles(int side, T empty)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(side);
        Side = side;
        TilesPerSide = (side + TileMask) >> TileShift;
        _tiles = new T[]?[TilesPerSide * TilesPerSide];
        _empty = empty;
    }

    public int Side { get; }

    public int TilesPerSide { get; }

    public int AllocatedTiles { get; private set; }

    /// <summary>A column's value, or the empty value when nothing was written in its tile.</summary>
    public T Get(int x, int y)
    {
        T[]? tile = _tiles[TileIndex(x, y)];
        return tile is null ? _empty : tile[LocalIndex(x, y)];
    }

    /// <summary>A column's slot, allocating its tile, filled with the empty value, when it has none.</summary>
    public ref T Slot(int x, int y)
    {
        int index = TileIndex(x, y);
        T[]? tile = _tiles[index];
        if (tile is null)
        {
            tile = new T[TileColumns * TileColumns];
            if (!EqualityComparer<T>.Default.Equals(_empty, default))
                Array.Fill(tile, _empty);
            _tiles[index] = tile;
            AllocatedTiles++;
        }
        return ref tile[LocalIndex(x, y)];
    }

    /// <summary>Whether anything was written in a tile, given the tile's own coordinates.</summary>
    public bool HasTile(int tileX, int tileY) => _tiles[(tileY * TilesPerSide) + tileX] is not null;

    private int TileIndex(int x, int y) => ((y >> TileShift) * TilesPerSide) + (x >> TileShift);

    private static int LocalIndex(int x, int y) => ((y & TileMask) << TileShift) | (x & TileMask);
}
