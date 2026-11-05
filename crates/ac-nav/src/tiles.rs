//! One value per column of a square of columns, kept in small square tiles
//! that are allocated the first time something is written into them.

/// Columns along each edge of a tile: four metres at the default cell size.
pub const TILE_COLUMNS: i32 = 1 << TILE_SHIFT;

const TILE_SHIFT: i32 = 4;
const TILE_MASK: i32 = TILE_COLUMNS - 1;

/// One value for each column of a square of columns, kept in tiles of
/// [`TILE_COLUMNS`] by [`TILE_COLUMNS`] columns, so a region holds memory only
/// where something was written rather than over its whole square.
#[derive(Debug, Clone)]
pub struct ColumnTiles<T: Copy + PartialEq> {
    tiles: Vec<Option<Box<[T]>>>,
    empty: T,
    side: i32,
    tiles_per_side: i32,
    allocated_tiles: i32,
}

impl<T: Copy + PartialEq> ColumnTiles<T> {
    /// A square of `side` columns, every one reading `empty`.
    ///
    /// # Panics
    ///
    /// When `side` is not positive.
    #[must_use]
    pub fn new(side: i32, empty: T) -> Self {
        assert!(side > 0, "a square of columns needs a positive side");
        let tiles_per_side = (side + TILE_MASK) >> TILE_SHIFT;
        Self {
            tiles: vec![None; (tiles_per_side * tiles_per_side) as usize],
            empty,
            side,
            tiles_per_side,
            allocated_tiles: 0,
        }
    }

    /// Columns along each edge.
    #[must_use]
    pub fn side(&self) -> i32 {
        self.side
    }

    /// Tiles along each edge.
    #[must_use]
    pub fn tiles_per_side(&self) -> i32 {
        self.tiles_per_side
    }

    /// How many tiles hold memory.
    #[must_use]
    pub fn allocated_tiles(&self) -> i32 {
        self.allocated_tiles
    }

    /// A column's value, or the empty value when nothing was written in its
    /// tile.
    #[must_use]
    pub fn get(&self, x: i32, y: i32) -> T {
        match &self.tiles[self.tile_index(x, y)] {
            Some(tile) => tile[local_index(x, y)],
            None => self.empty,
        }
    }

    /// A column's slot, allocating its tile, filled with the empty value,
    /// when it has none.
    pub fn slot(&mut self, x: i32, y: i32) -> &mut T {
        let index = self.tile_index(x, y);
        if self.tiles[index].is_none() {
            self.tiles[index] =
                Some(vec![self.empty; (TILE_COLUMNS * TILE_COLUMNS) as usize].into_boxed_slice());
            self.allocated_tiles += 1;
        }
        let tile = self.tiles[index].as_mut().expect("allocated above");
        &mut tile[local_index(x, y)]
    }

    /// Whether anything was written in a tile, given the tile's own
    /// coordinates.
    #[must_use]
    pub fn has_tile(&self, tile_x: i32, tile_y: i32) -> bool {
        self.tiles[(tile_y * self.tiles_per_side + tile_x) as usize].is_some()
    }

    fn tile_index(&self, x: i32, y: i32) -> usize {
        ((y >> TILE_SHIFT) * self.tiles_per_side + (x >> TILE_SHIFT)) as usize
    }
}

fn local_index(x: i32, y: i32) -> usize {
    (((y & TILE_MASK) << TILE_SHIFT) | (x & TILE_MASK)) as usize
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Navigation/NavColumnTilesTests.cs`.

    use super::*;

    #[test]
    fn a_column_never_written_reads_the_empty_value_and_holds_no_tile() {
        let tiles = ColumnTiles::<i32>::new(100, -1);

        assert_eq!(tiles.get(42, 17), -1);
        assert_eq!(tiles.allocated_tiles(), 0);
        assert!(!tiles.has_tile(42 / TILE_COLUMNS, 17 / TILE_COLUMNS));
    }

    #[test]
    fn writing_a_column_allocates_only_the_tile_that_holds_it() {
        let mut tiles = ColumnTiles::<i32>::new(100, -1);

        *tiles.slot(0, 0) = 7;
        *tiles.slot(99, 99) = 9;
        *tiles.slot(1, 1) = 8;

        assert_eq!(tiles.allocated_tiles(), 2);
        assert_eq!(tiles.get(0, 0), 7);
        assert_eq!(tiles.get(1, 1), 8);
        assert_eq!(tiles.get(99, 99), 9);
        assert_eq!(tiles.get(2, 2), -1);
        assert_eq!(tiles.get(50, 50), -1);
        assert!(tiles.has_tile(0, 0));
        assert!(!tiles.has_tile(1, 0));
    }

    #[test]
    fn a_square_that_is_not_a_whole_number_of_tiles_still_reaches_its_last_column() {
        let mut tiles = ColumnTiles::<bool>::new(17, false);

        *tiles.slot(16, 16) = true;

        assert_eq!(tiles.tiles_per_side(), 2);
        assert!(tiles.get(16, 16));
        assert!(!tiles.get(15, 16));
    }
}
