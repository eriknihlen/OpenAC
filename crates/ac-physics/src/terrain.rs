//! The outdoor ground of one landblock as a collision surface.
//!
//! A landblock is 192 m square: 8 x 8 cells of 24 m, with a height at each
//! of the 9 x 9 cell corners. Each cell is cut into two triangles along one
//! of its diagonals; which diagonal is a fixed pseudo-random choice per cell
//! of the world map. Heights come from a 256-entry table indexed by a byte
//! per corner. A corner whose terrain type is a water type makes the cells
//! around it (partly) water.

use glam::Vec3;

/// Corners along a side.
const HEIGHTMAP_SIDE: usize = 9;

/// The ground triangle under a point.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TerrainSurfacePolygon {
    /// Height of the ground at the point, metres.
    pub z: f32,
    /// The triangle's unit normal, pointing up.
    pub normal: Vec3,
    /// The triangle's corners (landblock-local metres).
    pub vertices: [Vec3; 3],
}

/// The ground of one landblock.
#[derive(Debug, Clone, PartialEq)]
pub struct TerrainSurface {
    /// Corner heights, `[x][y]`.
    z: [[f32; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE],
    /// Corners whose terrain type is water, `[x][y]`.
    corner_is_water: [[bool; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE],
    /// Per cell: 0 dry, 1 partly water, 2 entirely water, `[x][y]`.
    cell_water_type: [[u8; Self::CELLS_PER_SIDE]; Self::CELLS_PER_SIDE],
    block_water_type: u8,
    landblock_x: u32,
    landblock_y: u32,
}

impl TerrainSurface {
    /// Width of a cell, metres.
    pub const CELL_SIZE: f32 = 24.0;
    /// Cells along a side of a landblock.
    pub const CELLS_PER_SIDE: usize = 8;

    /// The ground from 81 height-table indices and 81 terrain bytes (both
    /// indexed `x * 9 + y`), the region's height table and the landblock's
    /// map coordinates (which pick each cell's diagonal). A terrain byte's
    /// bits 2..6 are its type; types `0x10..=0x14` are water. Without
    /// terrain bytes nothing is water.
    #[must_use]
    pub fn new(
        heights: &[u8; 81],
        height_table: &[f32; 256],
        landblock_x: u32,
        landblock_y: u32,
        terrain_types: Option<&[u8; 81]>,
    ) -> Self {
        let mut z = [[0.0; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
        for (x, column) in z.iter_mut().enumerate() {
            for (y, h) in column.iter_mut().enumerate() {
                *h = Self::vertex_height(heights, height_table, x, y);
            }
        }
        let mut corner_is_water = [[false; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
        if let Some(types) = terrain_types {
            for (x, column) in corner_is_water.iter_mut().enumerate() {
                for (y, w) in column.iter_mut().enumerate() {
                    let type_bits = (types[x * HEIGHTMAP_SIDE + y] >> 2) & 0x1F;
                    *w = (0x10..=0x14).contains(&type_bits);
                }
            }
        }
        let mut cell_water_type = [[0u8; Self::CELLS_PER_SIDE]; Self::CELLS_PER_SIDE];
        let mut any_water = false;
        let mut every_cell_flooded = true;
        for (cx, column) in cell_water_type.iter_mut().enumerate() {
            for (cy, kind) in column.iter_mut().enumerate() {
                let water_corners = [
                    corner_is_water[cx][cy],
                    corner_is_water[cx + 1][cy],
                    corner_is_water[cx + 1][cy + 1],
                    corner_is_water[cx][cy + 1],
                ]
                .iter()
                .filter(|w| **w)
                .count();
                *kind = match water_corners {
                    0 => 0,
                    4 => 2,
                    _ => 1,
                };
                any_water |= *kind != 0;
                every_cell_flooded &= *kind == 2;
            }
        }
        let block_water_type = if !any_water {
            0
        } else if every_cell_flooded {
            2
        } else {
            1
        };
        Self {
            z,
            corner_is_water,
            cell_water_type,
            block_water_type,
            landblock_x,
            landblock_y,
        }
    }

    /// The ground's height, metres, at corner `(x, y)` (`x` east, `y`
    /// north of the landblock's south-west corner, each 0 to 8): its
    /// height index (the 81 indexed `x * 9 + y`) through the region's
    /// height table.
    #[must_use]
    pub fn vertex_height(heights: &[u8; 81], height_table: &[f32; 256], x: usize, y: usize) -> f32 {
        height_table[usize::from(heights[x * HEIGHTMAP_SIDE + y])]
    }

    /// The whole block: 0 no water, 1 some water, 2 every cell fully under
    /// water (open sea).
    #[must_use]
    pub fn block_water_type(&self) -> u8 {
        self.block_water_type
    }

    /// True for open sea.
    #[must_use]
    pub fn is_entirely_water(&self) -> bool {
        self.block_water_type == 2
    }

    /// Ground height at a landblock-local point (clamped into the block).
    #[must_use]
    pub fn sample_z(&self, local_x: f32, local_y: f32) -> f32 {
        let c = CellCoords::new(local_x, local_y);
        let [h_bl, h_br, h_tr, h_tl] = self.corners(c.cx, c.cy);
        let split = is_split_sw_to_ne(self.landblock_x, c.cx as u32, self.landblock_y, c.cy as u32);
        interpolate_z_in_triangle(h_bl, h_br, h_tr, h_tl, c.tx, c.ty, split)
    }

    /// [`Self::sample_z`] straight from the dat arrays, without building a
    /// surface.
    #[must_use]
    pub fn sample_z_from_heightmap(
        heights: &[u8; 81],
        height_table: &[f32; 256],
        landblock_x: u32,
        landblock_y: u32,
        local_x: f32,
        local_y: f32,
    ) -> f32 {
        let c = CellCoords::new(local_x, local_y);
        let [h_bl, h_br, h_tr, h_tl] = heightmap_corners(heights, height_table, c.cx, c.cy);
        let split = is_split_sw_to_ne(landblock_x, c.cx as u32, landblock_y, c.cy as u32);
        interpolate_z_in_triangle(h_bl, h_br, h_tr, h_tl, c.tx, c.ty, split)
    }

    /// The up-component of the ground normal at a point, straight from the
    /// dat arrays.
    #[must_use]
    pub fn sample_normal_z_from_heightmap(
        heights: &[u8; 81],
        height_table: &[f32; 256],
        landblock_x: u32,
        landblock_y: u32,
        local_x: f32,
        local_y: f32,
    ) -> f32 {
        let c = CellCoords::new(local_x, local_y);
        let [h_bl, h_br, h_tr, h_tl] = heightmap_corners(heights, height_table, c.cx, c.cy);
        let split = is_split_sw_to_ne(landblock_x, c.cx as u32, landblock_y, c.cy as u32);
        let (dzdx, dzdy) = slopes(h_bl, h_br, h_tr, h_tl, c.tx, c.ty, split);
        1.0 / (dzdx * dzdx + dzdy * dzdy + 1.0).sqrt()
    }

    /// Ground height and unit normal at a point.
    #[must_use]
    pub fn sample_surface(&self, local_x: f32, local_y: f32) -> (f32, Vec3) {
        let c = CellCoords::new(local_x, local_y);
        let [h_bl, h_br, h_tr, h_tl] = self.corners(c.cx, c.cy);
        let split = is_split_sw_to_ne(self.landblock_x, c.cx as u32, self.landblock_y, c.cy as u32);
        let z = interpolate_z_in_triangle(h_bl, h_br, h_tr, h_tl, c.tx, c.ty, split);
        let (dzdx, dzdy) = slopes(h_bl, h_br, h_tr, h_tl, c.tx, c.ty, split);
        (z, crate::geometry::normalize(Vec3::new(-dzdx, -dzdy, 1.0)))
    }

    /// The ground triangle under a point, with the height at the point.
    #[must_use]
    pub fn sample_surface_polygon(&self, local_x: f32, local_y: f32) -> TerrainSurfacePolygon {
        let c = CellCoords::new(local_x, local_y);
        let [h_bl, h_br, h_tr, h_tl] = self.corners(c.cx, c.cy);
        let split = is_split_sw_to_ne(self.landblock_x, c.cx as u32, self.landblock_y, c.cy as u32);
        let (x0, y0) = (c.cx as f32 * Self::CELL_SIZE, c.cy as f32 * Self::CELL_SIZE);
        let (x1, y1) = (
            (c.cx + 1) as f32 * Self::CELL_SIZE,
            (c.cy + 1) as f32 * Self::CELL_SIZE,
        );
        let bl = Vec3::new(x0, y0, h_bl);
        let br = Vec3::new(x1, y0, h_br);
        let tr = Vec3::new(x1, y1, h_tr);
        let tl = Vec3::new(x0, y1, h_tl);
        let (tx, ty) = (c.tx, c.ty);
        let (z, vertices) = if split {
            if tx > ty {
                (h_bl + (h_br - h_bl) * tx + (h_tr - h_br) * ty, [bl, br, tr])
            } else {
                (h_bl + (h_tr - h_tl) * tx + (h_tl - h_bl) * ty, [bl, tr, tl])
            }
        } else if tx + ty <= 1.0 {
            (h_bl + (h_br - h_bl) * tx + (h_tl - h_bl) * ty, [bl, br, tl])
        } else {
            (
                h_tr + (h_tl - h_tr) * (1.0 - tx) + (h_br - h_tr) * (1.0 - ty),
                [br, tr, tl],
            )
        };
        let mut normal = crate::geometry::normalize(
            (vertices[1] - vertices[0]).cross(vertices[2] - vertices[0]),
        );
        if normal.z < 0.0 {
            normal = -normal;
        }
        TerrainSurfacePolygon {
            z,
            normal,
            vertices,
        }
    }

    /// How deep a body sinks into water at a point, metres: 0 on dry cells,
    /// 0.9 on cells entirely under water, and on part-water cells 0.45 near
    /// a water corner or 0.1 near a dry one.
    #[must_use]
    pub fn sample_water_depth(&self, local_x: f32, local_y: f32) -> f32 {
        let c = CellCoords::new(local_x, local_y);
        match self.cell_water_type[c.cx][c.cy] {
            0 => 0.0,
            2 => 0.9,
            _ => {
                let vx = c.cx + usize::from(c.tx >= 0.5);
                let vy = c.cy + usize::from(c.ty >= 0.5);
                if self.corner_is_water[vx][vy] {
                    0.45
                } else {
                    0.1
                }
            }
        }
    }

    /// The outdoor cell number (1..=64) under a landblock-local point.
    #[must_use]
    pub fn compute_outdoor_cell_id(&self, local_x: f32, local_y: f32) -> u32 {
        Self::compute_outdoor_cell_low_id(local_x, local_y)
    }

    /// The outdoor cell number (1..=64) under a landblock-local point.
    #[must_use]
    pub fn compute_outdoor_cell_low_id(local_x: f32, local_y: f32) -> u32 {
        let last = Self::CELLS_PER_SIDE as i32 - 1;
        let cx = ((local_x / Self::CELL_SIZE) as i32).clamp(0, last);
        let cy = ((local_y / Self::CELL_SIZE) as i32).clamp(0, last);
        (1 + cx * Self::CELLS_PER_SIDE as i32 + cy) as u32
    }

    /// The full outdoor cell id under a point of landblock `landblock_id`.
    #[must_use]
    pub fn compute_outdoor_cell_id_in(landblock_id: u32, local_x: f32, local_y: f32) -> u32 {
        (landblock_id & 0xFFFF_0000) | Self::compute_outdoor_cell_low_id(local_x, local_y)
    }

    fn corners(&self, cx: usize, cy: usize) -> [f32; 4] {
        [
            self.z[cx][cy],
            self.z[cx + 1][cy],
            self.z[cx + 1][cy + 1],
            self.z[cx][cy + 1],
        ]
    }
}

/// A point resolved to its cell and its position inside it (0..1 each
/// way).
struct CellCoords {
    cx: usize,
    cy: usize,
    tx: f32,
    ty: f32,
}

impl CellCoords {
    fn new(local_x: f32, local_y: f32) -> Self {
        let cells = TerrainSurface::CELLS_PER_SIDE as f32;
        let last = TerrainSurface::CELLS_PER_SIDE as i32 - 1;
        let fx = (local_x / TerrainSurface::CELL_SIZE).clamp(0.0, cells - 0.001);
        let fy = (local_y / TerrainSurface::CELL_SIZE).clamp(0.0, cells - 0.001);
        // A NaN coordinate truncates to cell 0.
        let cx = (fx as i32).clamp(0, last);
        let cy = (fy as i32).clamp(0, last);
        Self {
            cx: cx as usize,
            cy: cy as usize,
            tx: fx - cx as f32,
            ty: fy - cy as f32,
        }
    }
}

fn heightmap_corners(
    heights: &[u8; 81],
    height_table: &[f32; 256],
    cx: usize,
    cy: usize,
) -> [f32; 4] {
    let h = |x: usize, y: usize| TerrainSurface::vertex_height(heights, height_table, x, y);
    [h(cx, cy), h(cx + 1, cy), h(cx + 1, cy + 1), h(cx, cy + 1)]
}

/// Height inside a cell from its four corners (bottom-left, bottom-right,
/// top-right, top-left) and the position inside it.
fn interpolate_z_in_triangle(
    h_bl: f32,
    h_br: f32,
    h_tr: f32,
    h_tl: f32,
    tx: f32,
    ty: f32,
    split_sw_to_ne: bool,
) -> f32 {
    if split_sw_to_ne {
        // Diagonal from bottom-left to top-right.
        if tx > ty {
            return h_bl + (h_br - h_bl) * tx + (h_tr - h_br) * ty;
        }
        return h_bl + (h_tr - h_tl) * tx + (h_tl - h_bl) * ty;
    }
    // Diagonal from bottom-right to top-left.
    if tx + ty <= 1.0 {
        return h_bl + (h_br - h_bl) * tx + (h_tl - h_bl) * ty;
    }
    h_tr + (h_tl - h_tr) * (1.0 - tx) + (h_br - h_tr) * (1.0 - ty)
}

/// Rise per metre along x and y of the triangle the point is in.
fn slopes(
    h_bl: f32,
    h_br: f32,
    h_tr: f32,
    h_tl: f32,
    tx: f32,
    ty: f32,
    split_sw_to_ne: bool,
) -> (f32, f32) {
    let size = TerrainSurface::CELL_SIZE;
    if split_sw_to_ne {
        if tx > ty {
            ((h_br - h_bl) / size, (h_tr - h_br) / size)
        } else {
            ((h_tr - h_tl) / size, (h_tl - h_bl) / size)
        }
    } else if tx + ty <= 1.0 {
        ((h_br - h_bl) / size, (h_tl - h_bl) / size)
    } else {
        ((h_tr - h_tl) / size, (h_tr - h_br) / size)
    }
}

/// Whether the cell at global cell coordinates (landblock * 8 + cell) is
/// cut from bottom-left to top-right: the sign bit of a fixed hash.
fn is_split_sw_to_ne(landblock_x: u32, cell_x: u32, landblock_y: u32, cell_y: u32) -> bool {
    let x = landblock_x.wrapping_mul(8).wrapping_add(cell_x);
    let y = landblock_y.wrapping_mul(8).wrapping_add(cell_y);
    let dw = x
        .wrapping_mul(y)
        .wrapping_mul(0x0CCA_C033)
        .wrapping_sub(x.wrapping_mul(0x421B_E3BD))
        .wrapping_add(y.wrapping_mul(0x6C1A_C587))
        .wrapping_sub(0x519B_8F25);
    dw & 0x8000_0000 != 0
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/TerrainSurfaceTests.cs`,
    //! `TerrainSurfaceBlockWaterTests.cs` and `WaterSemanticsTests.cs`.

    use super::*;

    fn linear_height_table() -> [f32; 256] {
        std::array::from_fn(|i| i as f32)
    }

    fn filled(value: u8) -> [u8; 81] {
        [value; 81]
    }

    fn surface(heights: &[u8; 81]) -> TerrainSurface {
        TerrainSurface::new(heights, &linear_height_table(), 0, 0, None)
    }

    fn varied() -> [u8; 81] {
        std::array::from_fn(|i| ((i / 9 * 17 + i % 9 * 13) % 256) as u8)
    }

    #[test]
    fn flat_terrain_is_the_same_everywhere() {
        let s = surface(&filled(50));
        assert_eq!(s.sample_z(0.0, 0.0), 50.0);
        assert_eq!(s.sample_z(96.0, 96.0), 50.0);
        assert_eq!(s.sample_z(191.0, 191.0), 50.0);
    }

    #[test]
    fn slope_along_x_interpolates_linearly() {
        let heights: [u8; 81] = std::array::from_fn(|i| (10 + i / 9 * 10) as u8);
        let s = surface(&heights);
        let close = |a: f32, b: f32| (a - b).abs() < 0.05;
        assert!(close(s.sample_z(0.0, 96.0), 10.0));
        assert!(close(s.sample_z(96.0, 96.0), 50.0));
        assert!(close(s.sample_z(192.0, 96.0), 90.0));
        assert!(close(s.sample_z(60.0, 96.0), 35.0));
    }

    #[test]
    fn out_of_bounds_clamps() {
        let s = surface(&filled(42));
        assert_eq!(s.sample_z(-10.0, -10.0), 42.0);
        assert_eq!(s.sample_z(300.0, 300.0), 42.0);
        // A NaN point does not panic; it has no height.
        assert!(s.sample_z(f32::NAN, f32::NAN).is_nan());
    }

    #[test]
    fn heightmap_sampler_agrees_with_the_surface() {
        let heights = varied();
        let table = linear_height_table();
        let (lbx, lby) = (0xA9, 0xB3);
        let s = TerrainSurface::new(&heights, &table, lbx, lby, None);
        let mut lx = 0.5;
        while lx < 192.0 {
            let mut ly = 0.5;
            while ly < 192.0 {
                let a = s.sample_z(lx, ly);
                let b = TerrainSurface::sample_z_from_heightmap(&heights, &table, lbx, lby, lx, ly);
                assert!((a - b).abs() < 0.0001, "({lx}, {ly}): {a} vs {b}");
                ly += 5.0;
            }
            lx += 5.0;
        }
    }

    #[test]
    fn surface_polygon_is_the_containing_triangle() {
        let s = surface(&filled(50));
        let p = s.sample_surface_polygon(2.0, 2.0);
        assert!(p.vertices.iter().all(|v| v.z == 50.0));
        assert!((p.normal.z - 1.0).abs() < 1e-3);
        assert!(p.vertices.iter().any(|v| v.x == 0.0 && v.y == 0.0));
    }

    #[test]
    fn normal_z_flat_and_sloped() {
        let table = linear_height_table();
        let nz =
            TerrainSurface::sample_normal_z_from_heightmap(&filled(50), &table, 0, 0, 96.0, 96.0);
        assert!((nz - 1.0).abs() < 1e-5);
        let sloped: [u8; 81] = std::array::from_fn(|i| (i / 9 * 20) as u8);
        let nz = TerrainSurface::sample_normal_z_from_heightmap(&sloped, &table, 0, 0, 12.0, 12.0);
        assert!(nz < 1.0 && nz > 0.0, "nz = {nz}");
    }

    #[test]
    fn normal_z_agrees_with_sample_surface() {
        let heights = varied();
        let table = linear_height_table();
        let (lbx, lby) = (0xA9, 0xB3);
        let s = TerrainSurface::new(&heights, &table, lbx, lby, None);
        let mut lx = 0.5;
        while lx < 192.0 {
            let mut ly = 0.5;
            while ly < 192.0 {
                let (_, n) = s.sample_surface(lx, ly);
                let nz = TerrainSurface::sample_normal_z_from_heightmap(
                    &heights, &table, lbx, lby, lx, ly,
                );
                assert!((n.z - nz).abs() < 0.0001, "({lx}, {ly}): {} vs {nz}", n.z);
                ly += 8.0;
            }
            lx += 8.0;
        }
    }

    #[test]
    fn outdoor_cell_ids() {
        let s = surface(&filled(50));
        assert_eq!(s.compute_outdoor_cell_id(0.0, 0.0), 0x0001);
        assert_eq!(s.compute_outdoor_cell_id(24.0, 0.0), 0x0009);
        assert_eq!(s.compute_outdoor_cell_id(191.0, 191.0), 0x0040);
        assert_eq!(
            TerrainSurface::compute_outdoor_cell_id_in(0xA9B4_FFFF, 191.0, 191.0),
            0xA9B4_0040
        );
    }

    const WATER_VERTEX: u8 = 0x10 << 2;
    const LAND_VERTEX: u8 = 0x01 << 2;

    fn water_surface(terrain: &[u8; 81]) -> TerrainSurface {
        TerrainSurface::new(&[0; 81], &[0.0; 256], 0xA9, 0xB4, Some(terrain))
    }

    #[test]
    fn block_water_type_reflects_every_cell() {
        assert_eq!(water_surface(&filled(WATER_VERTEX)).block_water_type(), 2);
        assert!(water_surface(&filled(WATER_VERTEX)).is_entirely_water());
        let mut shore = filled(WATER_VERTEX);
        shore[0] = LAND_VERTEX;
        assert_eq!(water_surface(&shore).block_water_type(), 1);
        assert!(!water_surface(&shore).is_entirely_water());
        assert_eq!(water_surface(&filled(LAND_VERTEX)).block_water_type(), 0);
    }

    #[test]
    fn water_depth_golden_values() {
        assert_eq!(
            water_surface(&filled(0)).sample_water_depth(12.0, 12.0),
            0.0
        );
        assert_eq!(
            water_surface(&filled(WATER_VERTEX)).sample_water_depth(12.0, 12.0),
            0.9
        );
        let mut types = [0u8; 81];
        types[9 + 1] = WATER_VERTEX; // corner (1, 1)
        let s = water_surface(&types);
        assert_eq!(s.sample_water_depth(18.0, 18.0), 0.45);
        assert_eq!(s.sample_water_depth(4.0, 4.0), 0.1);
    }

    #[test]
    fn split_hash_is_a_fixed_pattern() {
        // Both diagonals occur, and the pattern is per global cell.
        let splits: Vec<bool> = (0..8)
            .map(|c| is_split_sw_to_ne(0xA9, c, 0xB4, 0))
            .collect();
        assert!(splits.contains(&true) && splits.contains(&false));
        assert_eq!(is_split_sw_to_ne(1, 0, 0, 0), is_split_sw_to_ne(0, 8, 0, 0));
    }
}
