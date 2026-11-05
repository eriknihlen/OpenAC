//! Which diagonal splits each terrain cell into two triangles.
//!
//! Each 24 m cell is cut along one diagonal; the choice is a fixed hash of
//! the cell's global land coordinate, so the drawn terrain and the
//! collision surface always agree.

/// The diagonal a terrain cell is cut along. The discriminants are the bit
/// the terrain vertex data carries.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum CellSplitDirection {
    /// South-west corner to north-east corner: triangles {SW, SE, NE} and
    /// {SW, NE, NW}.
    SwToNe = 0,
    /// South-east corner to north-west corner: triangles {SW, SE, NW} and
    /// {SE, NE, NW}.
    SeToNw = 1,
}

/// The split of cell (`cell_x`, `cell_y`) (0..8) in landblock
/// (`landblock_x`, `landblock_y`).
#[must_use]
pub fn calculate_split_direction(
    landblock_x: u32,
    cell_x: u32,
    landblock_y: u32,
    cell_y: u32,
) -> CellSplitDirection {
    let x = landblock_x.wrapping_mul(8).wrapping_add(cell_x);
    let y = landblock_y.wrapping_mul(8).wrapping_add(cell_y);
    // As an unsigned fraction of 2^32, at least one half means south-west
    // to north-east.
    let dw = x
        .wrapping_mul(y)
        .wrapping_mul(0x0CCA_C033)
        .wrapping_sub(x.wrapping_mul(0x421B_E3BD))
        .wrapping_add(y.wrapping_mul(0x6C1A_C587))
        .wrapping_sub(0x519B_8F25);
    if dw & 0x8000_0000 != 0 {
        CellSplitDirection::SwToNe
    } else {
        CellSplitDirection::SeToNw
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The split exactly as the game client computes it, from global cell
    /// coordinates: OpenAC tests/AcDream.Core.Tests/Terrain/ClientReference.cs
    /// `IsSWtoNECut` (signed 32-bit wrapping, then `u32 / 2^32 >= 0.5`).
    fn client_is_sw_to_ne(gx: i32, gy: i32) -> bool {
        let v7 = gy
            .wrapping_mul(214_614_067_i32.wrapping_mul(gx).wrapping_add(1_813_693_831))
            .wrapping_sub(1_109_124_029_i32.wrapping_mul(gx))
            .wrapping_sub(1_369_149_221);
        f64::from(v7 as u32) * 2.328_306_4e-10 >= 0.5
    }

    fn split_at(gx: i32, gy: i32) -> bool {
        let (gx, gy) = (gx as u32, gy as u32);
        calculate_split_direction(gx / 8, gx % 8, gy / 8, gy % 8) == CellSplitDirection::SwToNe
    }

    // OpenAC ClientConformanceTests.SplitDirection_MatchesClient.
    #[test]
    fn matches_client_at_sample_cells() {
        for (gx, gy) in [
            (0, 0),
            (1, 0),
            (0, 1),
            (7, 7),
            (127, 127),
            (1016, 1016),
            (2039, 2039),
            (500, 1200),
            (1999, 3),
        ] {
            assert_eq!(split_at(gx, gy), client_is_sw_to_ne(gx, gy), "({gx},{gy})");
        }
    }

    // OpenAC ClientConformanceTests.SplitDirection_MatchesClient_FullSweep.
    #[test]
    fn matches_client_full_sweep() {
        for lb_x in (0..255).step_by(8) {
            for lb_y in (0..255).step_by(8) {
                for cx in 0..8 {
                    for cy in 0..8 {
                        let (gx, gy) = (lb_x * 8 + cx, lb_y * 8 + cy);
                        assert_eq!(split_at(gx, gy), client_is_sw_to_ne(gx, gy));
                    }
                }
            }
        }
    }

    // OpenAC TerrainBlendingTests.CalculateSplitDirection_*.
    #[test]
    fn origin_cell_is_sw_to_ne() {
        assert_eq!(
            calculate_split_direction(0, 0, 0, 0),
            CellSplitDirection::SwToNe
        );
    }

    #[test]
    fn a_landblock_has_both_directions() {
        let mut sw = 0;
        for cx in 0..8 {
            for cy in 0..8 {
                if calculate_split_direction(0xA9, cx, 0xB4, cy) == CellSplitDirection::SwToNe {
                    sw += 1;
                }
            }
        }
        assert!(sw > 0 && sw < 64);
    }
}
