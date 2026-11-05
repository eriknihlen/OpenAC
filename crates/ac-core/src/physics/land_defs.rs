//! Cell-id rules of the world map.
//!
//! A cell id is a 32-bit number: the high byte is the landblock's x, the
//! next byte its y, the low 16 bits the cell inside the landblock. The map
//! is 255 x 255 landblocks of 8 x 8 outdoor cells, each 24 m square; a
//! global "land coordinate" counts outdoor cells across the whole map
//! (0..2040 on each axis).
//!
//! Outdoor cells inside a landblock are numbered 1..=64 x-major: cell
//! `(cx, cy)` has low word `cx * 8 + cy + 1`.

/// Outdoor cells across the map on each axis (255 landblocks x 8).
pub const LAND_LENGTH: i32 = 0x7F8;

/// Side of one outdoor cell, in meters.
pub const CELL_LENGTH: f32 = 24.0;

/// Side of one landblock (8 cells), in meters.
pub const BLOCK_LENGTH: f32 = 192.0;

/// A block-local coordinate closer to zero than this (meters) is treated
/// as exactly zero before re-seating a position outdoors.
const SNAP_EPSILON: f32 = 0.0002;

/// True if both land coordinates lie on the map.
#[must_use]
pub fn in_bounds(lx: i32, ly: i32) -> bool {
    lx >= 0 && ly >= 0 && lx < LAND_LENGTH && ly < LAND_LENGTH
}

/// The land coordinate of the landblock's (0, 0) cell, whether or not it
/// lies on the map. Block bytes are zero-extended.
fn block_lcoord(cell_id: u32) -> (i32, i32) {
    let lx = (((cell_id >> 24) & 0xFF) << 3) as i32;
    let ly = (((cell_id >> 16) & 0xFF) << 3) as i32;
    (lx, ly)
}

/// The land coordinate of the (0, 0) cell of `cell_id`'s landblock, or
/// `None` for cell id 0 or a landblock off the map.
#[must_use]
pub fn blockid_to_lcoord(cell_id: u32) -> Option<(i32, i32)> {
    if cell_id == 0 {
        return None;
    }
    let (lx, ly) = block_lcoord(cell_id);
    in_bounds(lx, ly).then_some((lx, ly))
}

/// True if `cell_id` can name a place: its low word is an outdoor cell
/// (1..=0x40), an indoor cell (0x100..=0xFFFD) or the whole-landblock
/// marker 0xFFFF, and its landblock lies on the map (both axes).
#[must_use]
pub fn inbound_valid_cell_id(cell_id: u32) -> bool {
    if !cell_low_in_range(cell_id & 0xFFFF) {
        return false;
    }
    let (lx, ly) = block_lcoord(cell_id);
    in_bounds(lx, ly)
}

/// The global land coordinate of an outdoor cell id; `None` for an indoor
/// cell, the landblock marker or an invalid id.
#[must_use]
pub fn gid_to_lcoord(cell_id: u32) -> Option<(i32, i32)> {
    if !inbound_valid_cell_id(cell_id) {
        return None;
    }
    let low = cell_id & 0xFFFF;
    if low >= 0x100 {
        return None;
    }
    let (bx, by) = block_lcoord(cell_id);
    let lx = bx + ((low - 1) >> 3) as i32;
    let ly = by + ((low - 1) & 7) as i32;
    in_bounds(lx, ly).then_some((lx, ly))
}

/// The full outdoor cell id at a global land coordinate; the landblock is
/// derived from the coordinate, so a neighbour's coordinate gives the
/// neighbour's cell. `None` off the map.
#[must_use]
pub fn lcoord_to_gid(lx: i32, ly: i32) -> Option<u32> {
    if !in_bounds(lx, ly) {
        return None;
    }
    let low = ((ly & 7) + ((lx & 7) << 3) + 1) as u32;
    let block = (((lx >> 3) << 8) | (ly >> 3)) as u32;
    Some((block << 16) | low)
}

/// The global land coordinate of the outdoor cell under `block_local`
/// (meters from `cell_id`'s landblock corner). The position may lie
/// outside the landblock (negative or beyond 192 m); the result then names
/// a neighbour's cell. `None` if the low word is not a valid cell or the
/// result is off the map.
#[must_use]
pub fn get_outside_lcoord(cell_id: u32, block_local: [f32; 3]) -> Option<(i32, i32)> {
    if !cell_low_in_range(cell_id & 0xFFFF) {
        return None;
    }
    let (bx, by) = block_lcoord(cell_id);
    let lx = bx.wrapping_add((block_local[0] / CELL_LENGTH).floor() as i32);
    let ly = by.wrapping_add((block_local[1] / CELL_LENGTH).floor() as i32);
    in_bounds(lx, ly).then_some((lx, ly))
}

/// Re-seats a position onto the outdoor cell actually under it.
///
/// On success `cell_id` becomes that outdoor cell (possibly in a
/// neighbouring landblock) and `block_local` x and y are rebased into that
/// landblock's frame (0..192 m); z is untouched. Coordinates within
/// 0.0002 m of zero snap to zero first. On failure `cell_id` becomes 0.
pub fn adjust_to_outside(cell_id: &mut u32, block_local: &mut [f32; 3]) -> bool {
    if cell_low_in_range(*cell_id & 0xFFFF) {
        if block_local[0].abs() < SNAP_EPSILON {
            block_local[0] = 0.0;
        }
        if block_local[1].abs() < SNAP_EPSILON {
            block_local[1] = 0.0;
        }
        if let Some((lx, ly)) = get_outside_lcoord(*cell_id, *block_local)
            && let Some(gid) = lcoord_to_gid(lx, ly)
        {
            *cell_id = gid;
            block_local[0] -= (block_local[0] / BLOCK_LENGTH).floor() * BLOCK_LENGTH;
            block_local[1] -= (block_local[1] / BLOCK_LENGTH).floor() * BLOCK_LENGTH;
            return true;
        }
    }
    *cell_id = 0;
    false
}

/// The offset in meters from `source`'s landblock corner to `dest`'s
/// (z is always 0). Zero when both ids share a landblock.
#[must_use]
pub fn get_block_offset(source: u32, dest: u32) -> [f32; 3] {
    let src_block = source >> 16;
    let dst_block = dest >> 16;
    if src_block == dst_block {
        return [0.0; 3];
    }
    let (src_lx, src_ly) = if source == 0 {
        (0, 0)
    } else {
        (
            ((source >> 21) & 0x7F8) as i32,
            ((src_block & 0xFF) << 3) as i32,
        )
    };
    // A zero destination takes the whole source id on both axes, as the
    // game client does.
    let (dst_lx, dst_ly) = if dest == 0 {
        (source as i32, source as i32)
    } else {
        (
            ((dest >> 21) & 0x7F8) as i32,
            ((dst_block & 0xFF) << 3) as i32,
        )
    };
    let meters = |cells: i32| (f64::from(cells) * f64::from(CELL_LENGTH)) as f32;
    [
        meters(dst_lx.wrapping_sub(src_lx)),
        meters(dst_ly.wrapping_sub(src_ly)),
        0.0,
    ]
}

fn cell_low_in_range(low: u32) -> bool {
    matches!(low, 1..=0x40 | 0x100..=0xFFFD | 0xFFFF)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn low_word_classes() {
        assert!(inbound_valid_cell_id(0x0101_0001));
        assert!(inbound_valid_cell_id(0x0101_0040));
        assert!(!inbound_valid_cell_id(0x0101_0041));
        assert!(!inbound_valid_cell_id(0x0101_00FF));
        assert!(inbound_valid_cell_id(0x0101_0100));
        assert!(inbound_valid_cell_id(0x0101_FFFD));
        assert!(!inbound_valid_cell_id(0x0101_FFFE));
        assert!(inbound_valid_cell_id(0x0101_FFFF));
        assert!(!inbound_valid_cell_id(0x0101_0000));
    }

    #[test]
    fn landblock_255_is_off_the_map_on_either_axis() {
        assert!(inbound_valid_cell_id(0xFE00_0001));
        assert!(!inbound_valid_cell_id(0xFF00_0001));
        assert!(!inbound_valid_cell_id(0x00FF_0001));
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/Physics/LandDefsTests.cs.

    #[test]
    fn blockid_to_lcoord_a9b4() {
        assert_eq!(blockid_to_lcoord(0xA9B4_0031), Some((169 * 8, 180 * 8)));
    }

    #[test]
    fn blockid_to_lcoord_high_block_y_is_not_sign_extended() {
        assert_eq!(blockid_to_lcoord(0xFEFE_0001), Some((0xFE * 8, 0xFE * 8)));
    }

    #[test]
    fn blockid_to_lcoord_zero_fails() {
        assert_eq!(blockid_to_lcoord(0), None);
    }

    #[test]
    fn gid_to_lcoord_a9b40031() {
        assert_eq!(gid_to_lcoord(0xA9B4_0031), Some((1358, 1440)));
    }

    #[test]
    fn gid_to_lcoord_indoor_and_invalid_fail() {
        assert_eq!(gid_to_lcoord(0xA9B4_0164), None);
        assert_eq!(gid_to_lcoord(0xA9B4_0000), None);
        assert_eq!(gid_to_lcoord(0xA9B4_0041), None);
    }

    #[test]
    fn lcoord_to_gid_round_trips_and_crosses_south() {
        assert_eq!(lcoord_to_gid(1358, 1440), Some(0xA9B4_0031));
        assert_eq!(lcoord_to_gid(1358, 1439), Some(0xA9B3_0038));
    }

    #[test]
    fn lcoord_to_gid_off_the_map() {
        assert_eq!(lcoord_to_gid(-1, 5), None);
        assert_eq!(lcoord_to_gid(5, -1), None);
        assert_eq!(lcoord_to_gid(0x7F8, 5), None);
        assert_eq!(lcoord_to_gid(5, 0x7F8), None);
    }

    #[test]
    fn get_outside_lcoord_floors_negative_coordinates() {
        assert_eq!(
            get_outside_lcoord(0xA9B4_0031, [150.0, -1.0, 0.0]),
            Some((1358, 1439))
        );
    }

    #[test]
    fn get_outside_lcoord_from_indoor_cell_uses_block_bits() {
        assert_eq!(
            get_outside_lcoord(0xA9B4_0164, [12.0, 12.0, 0.0]),
            Some((1352, 1440))
        );
    }

    fn adjust(cell: u32, pos: [f32; 3]) -> (bool, u32, [f32; 3]) {
        let (mut c, mut p) = (cell, pos);
        let ok = adjust_to_outside(&mut c, &mut p);
        (ok, c, p)
    }

    #[test]
    fn adjust_to_outside_south_crossing() {
        let (ok, cell, pos) = adjust(0xA9B4_0031, [150.0, -1.0, 5.0]);
        assert!(ok);
        assert_eq!(cell, 0xA9B3_0038);
        assert!((pos[0] - 150.0).abs() < 1e-4);
        assert!((pos[1] - 191.0).abs() < 1e-4);
        assert!((pos[2] - 5.0).abs() < 1e-4);
    }

    #[test]
    fn adjust_to_outside_deep_south_capture() {
        let (ok, cell, pos) = adjust(0xA9B4_0031, [150.0, -109.65, 0.0]);
        assert!(ok);
        assert_eq!(cell, 0xA9B3_0034);
        assert!((pos[1] - 82.35).abs() < 1e-3);
    }

    #[test]
    fn adjust_to_outside_northbound_return() {
        let (ok, cell, pos) = adjust(0xA9B3_0038, [150.0, 193.0, 0.0]);
        assert!(ok);
        assert_eq!(cell, 0xA9B4_0031);
        assert!((pos[1] - 1.0).abs() < 1e-4);
    }

    #[test]
    fn adjust_to_outside_within_block_rewrites_cell() {
        let (ok, cell, pos) = adjust(0xA9B4_0031, [12.0, 12.0, 0.0]);
        assert!(ok);
        assert_eq!(cell, 0xA9B4_0001);
        assert_eq!(pos, [12.0, 12.0, 0.0]);
    }

    #[test]
    fn adjust_to_outside_snaps_tiny_negative_to_zero() {
        let (ok, cell, pos) = adjust(0xA9B4_0031, [150.0, -0.0001, 0.0]);
        assert!(ok);
        assert_eq!(cell, 0xA9B4_0031);
        assert_eq!(pos[1], 0.0);
    }

    #[test]
    fn adjust_to_outside_invalid_low_fails_and_zeroes_cell() {
        let (ok, cell, _) = adjust(0xA9B4_0050, [12.0, 12.0, 0.0]);
        assert!(!ok);
        assert_eq!(cell, 0);
    }

    #[test]
    fn inbound_valid_cell_id_low_ranges() {
        for (id, expected) in [
            (0xA9B4_0001, true),
            (0xA9B4_0040, true),
            (0xA9B4_0100, true),
            (0xA9B4_FFFD, true),
            (0xA9B4_FFFF, true),
            (0xA9B4_0000, false),
            (0xA9B4_0041, false),
            (0xA9B4_FFFE, false),
        ] {
            assert_eq!(inbound_valid_cell_id(id), expected, "{id:#010X}");
        }
    }

    // Golden values: OpenAC tests/AcDream.Core.Tests/Physics/LandDefsBlockOffsetTests.cs.

    #[test]
    fn block_offset_same_landblock_is_zero() {
        assert_eq!(get_block_offset(0xC95B_0001, 0xC95B_0008), [0.0; 3]);
    }

    #[test]
    fn block_offset_neighbours() {
        assert_eq!(
            get_block_offset(0xC95B_0001, 0xC95A_0001),
            [0.0, -192.0, 0.0]
        );
        assert_eq!(
            get_block_offset(0xC95B_0001, 0xCA5B_0001),
            [192.0, 0.0, 0.0]
        );
        assert_eq!(
            get_block_offset(0xC95B_0001, 0xCA5C_0001),
            [192.0, 192.0, 0.0]
        );
    }
}
