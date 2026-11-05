//! The outdoor ground of one landblock as physics sees it: height, slope
//! and water under a point.
//!
//! A landblock is 192 m square: a 9 x 9 grid of height samples 24 m apart
//! (arrays indexed `x * 9 + y`), forming 8 x 8 cells. Each cell is two
//! flat triangles cut along the diagonal [`calculate_split_direction`]
//! picks, so the height under a point is a plane through the three corners
//! of the triangle it falls in. Positions are landblock-local meters
//! (0..192 on x and y); points outside are clamped to the edge cells.

use crate::terrain::{CellSplitDirection, calculate_split_direction};
use crate::vec3::{Vec3, cross, neg, normalize, sub};

/// Height samples on a side of a landblock.
pub const HEIGHTMAP_SIDE: usize = 9;

/// Height samples in a landblock.
pub const HEIGHTMAP_LEN: usize = HEIGHTMAP_SIDE * HEIGHTMAP_SIDE;

/// Cells on a side of a landblock.
pub const CELLS_PER_SIDE: usize = 8;

/// Side of one cell, in meters.
pub const CELL_SIZE: f32 = 24.0;

/// Clamp limit for the cell-space coordinate, so a point on the far edge
/// still falls in the last cell.
const MAX_CELL_COORD: f32 = CELLS_PER_SIDE as f32 - 0.001;

/// How much of a cell, or a whole landblock, lies under water.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
#[repr(u8)]
pub enum WaterType {
    /// No water corner.
    #[default]
    NotWater = 0,
    /// Some water corners (a cell), or some water cells (a landblock).
    PartiallyWater = 1,
    /// Every corner (a cell), or every cell (a landblock), under water.
    EntirelyWater = 2,
}

/// The triangle of ground under a point.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TerrainSurfacePolygon {
    /// Ground height at the point, in meters.
    pub z: f32,
    /// Unit normal of the triangle, pointing up.
    pub normal: Vec3,
    /// The triangle's corners in landblock-local meters, in mesh order.
    pub vertices: [Vec3; 3],
}

/// The ground of one landblock with its heights resolved.
#[derive(Debug, Clone)]
pub struct TerrainSurface {
    /// Height in meters per sample, `[x][y]`.
    z: [[f32; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE],
    /// Whether each sample's terrain type is water, `[x][y]`.
    corner_is_water: [[bool; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE],
    /// Water per cell, `[cx][cy]`.
    cell_water: [[WaterType; CELLS_PER_SIDE]; CELLS_PER_SIDE],
    block_water: WaterType,
    landblock_x: u32,
    landblock_y: u32,
}

/// True if a terrain word's type (bits 2-6) is one of the five water types
/// (0x10..=0x14).
fn is_water_terrain(word: u16) -> bool {
    let terrain_type = (word >> 2) & 0x1F;
    (0x10..=0x14).contains(&terrain_type)
}

impl TerrainSurface {
    /// Resolves `heights` (indices into `height_table`, `x * 9 + y`) for
    /// landblock (`landblock_x`, `landblock_y`). `terrain` holds the terrain
    /// words that mark water; without them nothing is water.
    #[must_use]
    pub fn new(
        heights: &[u8; HEIGHTMAP_LEN],
        height_table: &[f32; 256],
        landblock_x: u32,
        landblock_y: u32,
        terrain: Option<&[u16; HEIGHTMAP_LEN]>,
    ) -> Self {
        let mut z = [[0.0; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
        let mut corner_is_water = [[false; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
        for x in 0..HEIGHTMAP_SIDE {
            for y in 0..HEIGHTMAP_SIDE {
                let i = x * HEIGHTMAP_SIDE + y;
                z[x][y] = height_table[usize::from(heights[i])];
                corner_is_water[x][y] = terrain.is_some_and(|t| is_water_terrain(t[i]));
            }
        }

        let mut cell_water = [[WaterType::NotWater; CELLS_PER_SIDE]; CELLS_PER_SIDE];
        for (cx, column) in cell_water.iter_mut().enumerate() {
            for (cy, cell) in column.iter_mut().enumerate() {
                let water_corners = [
                    corner_is_water[cx][cy],
                    corner_is_water[cx + 1][cy],
                    corner_is_water[cx + 1][cy + 1],
                    corner_is_water[cx][cy + 1],
                ]
                .iter()
                .filter(|&&w| w)
                .count();
                *cell = match water_corners {
                    0 => WaterType::NotWater,
                    4 => WaterType::EntirelyWater,
                    _ => WaterType::PartiallyWater,
                };
            }
        }

        let cells = cell_water.iter().flatten();
        let any_water = cells.clone().any(|&w| w != WaterType::NotWater);
        let every_cell_flooded = cells.clone().all(|&w| w == WaterType::EntirelyWater);
        let block_water = if !any_water {
            WaterType::NotWater
        } else if every_cell_flooded {
            WaterType::EntirelyWater
        } else {
            WaterType::PartiallyWater
        };

        Self {
            z,
            corner_is_water,
            cell_water,
            block_water,
            landblock_x,
            landblock_y,
        }
    }

    /// Water over the whole landblock: none, some cells, or every cell
    /// fully under water (open sea).
    #[must_use]
    pub fn block_water_type(&self) -> WaterType {
        self.block_water
    }

    /// True if every cell is fully under water.
    #[must_use]
    pub fn is_entirely_water(&self) -> bool {
        self.block_water == WaterType::EntirelyWater
    }

    fn corners(&self, cx: usize, cy: usize) -> Corners {
        Corners {
            bl: self.z[cx][cy],
            br: self.z[cx + 1][cy],
            tr: self.z[cx + 1][cy + 1],
            tl: self.z[cx][cy + 1],
        }
    }

    fn sw_to_ne(&self, cx: usize, cy: usize) -> bool {
        is_sw_to_ne(self.landblock_x, cx, self.landblock_y, cy)
    }

    /// Ground height in meters at (`local_x`, `local_y`).
    #[must_use]
    pub fn sample_z(&self, local_x: f32, local_y: f32) -> f32 {
        let p = locate(local_x, local_y);
        self.corners(p.cx, p.cy)
            .z(p.tx, p.ty, self.sw_to_ne(p.cx, p.cy))
    }

    /// Ground height in meters and the unit normal of the triangle under
    /// (`local_x`, `local_y`).
    #[must_use]
    pub fn sample_surface(&self, local_x: f32, local_y: f32) -> (f32, Vec3) {
        let p = locate(local_x, local_y);
        let h = self.corners(p.cx, p.cy);
        let sw = self.sw_to_ne(p.cx, p.cy);
        let (dzdx, dzdy) = h.gradient(p.tx, p.ty, sw);
        (h.z(p.tx, p.ty, sw), normalize([-dzdx, -dzdy, 1.0]))
    }

    /// The triangle under (`local_x`, `local_y`) with its corners, the
    /// height at the point and the triangle's upward normal.
    #[must_use]
    pub fn sample_surface_polygon(&self, local_x: f32, local_y: f32) -> TerrainSurfacePolygon {
        let p = locate(local_x, local_y);
        let h = self.corners(p.cx, p.cy);
        let (x0, y0) = (p.cx as f32 * CELL_SIZE, p.cy as f32 * CELL_SIZE);
        let (x1, y1) = ((p.cx + 1) as f32 * CELL_SIZE, (p.cy + 1) as f32 * CELL_SIZE);
        let bl = [x0, y0, h.bl];
        let br = [x1, y0, h.br];
        let tr = [x1, y1, h.tr];
        let tl = [x0, y1, h.tl];

        let sw = self.sw_to_ne(p.cx, p.cy);
        let vertices = if sw {
            if p.tx > p.ty {
                [bl, br, tr]
            } else {
                [bl, tr, tl]
            }
        } else if p.tx + p.ty <= 1.0 {
            [bl, br, tl]
        } else {
            [br, tr, tl]
        };
        let mut normal = normalize(cross(
            sub(vertices[1], vertices[0]),
            sub(vertices[2], vertices[0]),
        ));
        if normal[2] < 0.0 {
            normal = neg(normal);
        }
        TerrainSurfacePolygon {
            z: h.z(p.tx, p.ty, sw),
            normal,
            vertices,
        }
    }

    /// How far (meters) feet may sink below the ground at this point: 0 on
    /// dry cells, 0.9 on fully flooded cells, and on part-water cells 0.45
    /// near a water corner or 0.1 near a dry one (the nearest corner is the
    /// one on the same half of the cell on each axis).
    #[must_use]
    pub fn sample_water_depth(&self, local_x: f32, local_y: f32) -> f32 {
        let p = locate(local_x, local_y);
        match self.cell_water[p.cx][p.cy] {
            WaterType::NotWater => 0.0,
            WaterType::EntirelyWater => 0.9,
            WaterType::PartiallyWater => {
                let vx = p.cx + usize::from(p.tx >= 0.5);
                let vy = p.cy + usize::from(p.ty >= 0.5);
                if self.corner_is_water[vx][vy] {
                    0.45
                } else {
                    0.1
                }
            }
        }
    }
}

/// Ground height in meters at a point, read straight from a landblock's
/// raw heights without building a [`TerrainSurface`]. Same result as
/// [`TerrainSurface::sample_z`].
#[must_use]
pub fn sample_z_from_heightmap(
    heights: &[u8; HEIGHTMAP_LEN],
    height_table: &[f32; 256],
    landblock_x: u32,
    landblock_y: u32,
    local_x: f32,
    local_y: f32,
) -> f32 {
    let p = locate(local_x, local_y);
    let h = Corners::from_heightmap(heights, height_table, p.cx, p.cy);
    h.z(
        p.tx,
        p.ty,
        is_sw_to_ne(landblock_x, p.cx, landblock_y, p.cy),
    )
}

/// The up component of the ground triangle's unit normal at a point: 1 on
/// flat ground, smaller on slopes.
#[must_use]
pub fn sample_normal_z_from_heightmap(
    heights: &[u8; HEIGHTMAP_LEN],
    height_table: &[f32; 256],
    landblock_x: u32,
    landblock_y: u32,
    local_x: f32,
    local_y: f32,
) -> f32 {
    let p = locate(local_x, local_y);
    let h = Corners::from_heightmap(heights, height_table, p.cx, p.cy);
    let (dzdx, dzdy) = h.gradient(
        p.tx,
        p.ty,
        is_sw_to_ne(landblock_x, p.cx, landblock_y, p.cy),
    );
    1.0 / (dzdx * dzdx + dzdy * dzdy + 1.0).sqrt()
}

/// The low word (1..=64) of the outdoor cell under a landblock-local
/// point; cells are numbered x-major.
#[must_use]
pub fn compute_outdoor_cell_low_id(local_x: f32, local_y: f32) -> u32 {
    let last = CELLS_PER_SIDE as i32 - 1;
    let cx = ((local_x / CELL_SIZE) as i32).clamp(0, last) as u32;
    let cy = ((local_y / CELL_SIZE) as i32).clamp(0, last) as u32;
    1 + cx * CELLS_PER_SIDE as u32 + cy
}

/// The full outdoor cell id under a point of landblock `landblock_id`.
#[must_use]
pub fn compute_outdoor_cell_id(landblock_id: u32, local_x: f32, local_y: f32) -> u32 {
    (landblock_id & 0xFFFF_0000) | compute_outdoor_cell_low_id(local_x, local_y)
}

fn is_sw_to_ne(landblock_x: u32, cx: usize, landblock_y: u32, cy: usize) -> bool {
    calculate_split_direction(landblock_x, cx as u32, landblock_y, cy as u32)
        == CellSplitDirection::SwToNe
}

/// A point located in a cell: the cell and the fraction (0..1) across it.
struct CellPoint {
    cx: usize,
    cy: usize,
    tx: f32,
    ty: f32,
}

fn locate(local_x: f32, local_y: f32) -> CellPoint {
    let fx = (local_x / CELL_SIZE).clamp(0.0, MAX_CELL_COORD);
    let fy = (local_y / CELL_SIZE).clamp(0.0, MAX_CELL_COORD);
    let last = CELLS_PER_SIDE as i32 - 1;
    let cx = (fx as i32).clamp(0, last);
    let cy = (fy as i32).clamp(0, last);
    CellPoint {
        cx: cx as usize,
        cy: cy as usize,
        tx: fx - cx as f32,
        ty: fy - cy as f32,
    }
}

/// The four corner heights of a cell (south-west, south-east, north-east,
/// north-west).
#[derive(Clone, Copy)]
struct Corners {
    bl: f32,
    br: f32,
    tr: f32,
    tl: f32,
}

impl Corners {
    fn from_heightmap(
        heights: &[u8; HEIGHTMAP_LEN],
        height_table: &[f32; 256],
        cx: usize,
        cy: usize,
    ) -> Self {
        let at = |x: usize, y: usize| height_table[usize::from(heights[x * HEIGHTMAP_SIDE + y])];
        Self {
            bl: at(cx, cy),
            br: at(cx + 1, cy),
            tr: at(cx + 1, cy + 1),
            tl: at(cx, cy + 1),
        }
    }

    /// Height at fraction (`tx`, `ty`) across the cell, on the plane of the
    /// triangle the point falls in.
    fn z(&self, tx: f32, ty: f32, sw_to_ne: bool) -> f32 {
        let Self { bl, br, tr, tl } = *self;
        if sw_to_ne {
            // Diagonal SW-NE (the line ty = tx).
            if tx > ty {
                bl + (br - bl) * tx + (tr - br) * ty
            } else {
                bl + (tr - tl) * tx + (tl - bl) * ty
            }
        } else if tx + ty <= 1.0 {
            // Diagonal SE-NW (the line tx + ty = 1), lower triangle.
            bl + (br - bl) * tx + (tl - bl) * ty
        } else {
            tr + (tl - tr) * (1.0 - tx) + (br - tr) * (1.0 - ty)
        }
    }

    /// Slope of that triangle: meters of rise per meter along x and y.
    fn gradient(&self, tx: f32, ty: f32, sw_to_ne: bool) -> (f32, f32) {
        let Self { bl, br, tr, tl } = *self;
        let (dx, dy) = if sw_to_ne {
            if tx > ty {
                (br - bl, tr - br)
            } else {
                (tr - tl, tl - bl)
            }
        } else if tx + ty <= 1.0 {
            (br - bl, tl - bl)
        } else {
            (tr - tl, tr - br)
        };
        (dx / CELL_SIZE, dy / CELL_SIZE)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn linear_table(step: f32) -> [f32; 256] {
        std::array::from_fn(|i| i as f32 * step)
    }

    fn flat(value: u8) -> [u8; HEIGHTMAP_LEN] {
        [value; HEIGHTMAP_LEN]
    }

    fn heightmap(f: impl Fn(usize, usize) -> u8) -> [u8; HEIGHTMAP_LEN] {
        std::array::from_fn(|i| f(i / HEIGHTMAP_SIDE, i % HEIGHTMAP_SIDE))
    }

    fn surface(heights: &[u8; HEIGHTMAP_LEN], table: &[f32; 256]) -> TerrainSurface {
        TerrainSurface::new(heights, table, 0, 0, None)
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/Physics/TerrainSurfaceTests.cs.

    #[test]
    fn flat_terrain_is_the_same_everywhere() {
        let s = surface(&flat(50), &linear_table(1.0));
        assert_eq!(s.sample_z(0.0, 0.0), 50.0);
        assert_eq!(s.sample_z(96.0, 96.0), 50.0);
        assert_eq!(s.sample_z(191.0, 191.0), 50.0);
    }

    #[test]
    fn slope_along_x_interpolates_linearly() {
        let h = heightmap(|x, _| (10 + x * 10) as u8);
        let s = surface(&h, &linear_table(1.0));
        assert!((s.sample_z(0.0, 96.0) - 10.0).abs() < 0.05);
        assert!((s.sample_z(96.0, 96.0) - 50.0).abs() < 0.05);
        assert!((s.sample_z(192.0, 96.0) - 90.0).abs() < 0.05);
        assert!((s.sample_z(60.0, 96.0) - 35.0).abs() < 0.05);
    }

    #[test]
    fn out_of_bounds_points_clamp() {
        let s = surface(&flat(42), &linear_table(1.0));
        assert_eq!(s.sample_z(-10.0, -10.0), 42.0);
        assert_eq!(s.sample_z(300.0, 300.0), 42.0);
    }

    fn varied() -> [u8; HEIGHTMAP_LEN] {
        heightmap(|x, y| ((x * 17 + y * 13) % 256) as u8)
    }

    fn grid(step: f32) -> impl Iterator<Item = (f32, f32)> {
        let n = (192.0 / step).ceil() as usize;
        (0..n)
            .flat_map(move |i| (0..n).map(move |j| (0.5 + i as f32 * step, 0.5 + j as f32 * step)))
    }

    #[test]
    fn heightmap_sampler_agrees_with_the_surface() {
        let (h, table) = (varied(), linear_table(1.0));
        let s = TerrainSurface::new(&h, &table, 0xA9, 0xB3, None);
        for (x, y) in grid(5.0) {
            let a = s.sample_z(x, y);
            let b = sample_z_from_heightmap(&h, &table, 0xA9, 0xB3, x, y);
            assert!((a - b).abs() < 1e-4, "({x},{y}) {a} vs {b}");
        }
    }

    #[test]
    fn surface_polygon_contains_the_point() {
        let s = surface(&flat(50), &linear_table(1.0));
        let p = s.sample_surface_polygon(2.0, 2.0);
        assert!(p.vertices.iter().all(|v| v[2] == 50.0));
        assert!((p.normal[2] - 1.0).abs() < 1e-3);
        assert!(p.vertices.iter().any(|v| v[0] == 0.0 && v[1] == 0.0));
    }

    #[test]
    fn normal_z_is_one_on_flat_ground_and_less_on_slopes() {
        let table = linear_table(1.0);
        let nz = sample_normal_z_from_heightmap(&flat(50), &table, 0, 0, 96.0, 96.0);
        assert!((nz - 1.0).abs() < 1e-5);
        let sloped = heightmap(|x, _| (x * 20) as u8);
        let nz = sample_normal_z_from_heightmap(&sloped, &table, 0, 0, 12.0, 12.0);
        assert!(nz > 0.0 && nz < 1.0, "{nz}");
    }

    #[test]
    fn normal_z_agrees_with_sample_surface() {
        let (h, table) = (varied(), linear_table(1.0));
        let s = TerrainSurface::new(&h, &table, 0xA9, 0xB3, None);
        for (x, y) in grid(8.0) {
            let (_, normal) = s.sample_surface(x, y);
            let nz = sample_normal_z_from_heightmap(&h, &table, 0xA9, 0xB3, x, y);
            assert!((normal[2] - nz).abs() < 1e-4, "({x},{y})");
        }
    }

    #[test]
    fn surface_polygon_normal_matches_the_gradient_normal() {
        let (h, table) = (varied(), linear_table(1.0));
        let s = TerrainSurface::new(&h, &table, 0xA9, 0xB3, None);
        for (x, y) in grid(7.0) {
            let (z, normal) = s.sample_surface(x, y);
            let p = s.sample_surface_polygon(x, y);
            assert_eq!(z, p.z);
            for (a, b) in normal.iter().zip(p.normal) {
                assert!((a - b).abs() < 1e-4, "({x},{y})");
            }
        }
    }

    #[test]
    fn outdoor_cell_ids() {
        assert_eq!(compute_outdoor_cell_low_id(0.0, 0.0), 0x0001);
        assert_eq!(compute_outdoor_cell_low_id(24.0, 0.0), 0x0009);
        assert_eq!(compute_outdoor_cell_low_id(191.0, 191.0), 0x0040);
        assert_eq!(
            compute_outdoor_cell_id(0xA9B4_0000, 25.0, 49.0),
            0xA9B4_000B
        );
    }

    // OpenAC tests/AcDream.Core.Tests/Physics/TerrainSurfaceBlockWaterTests.cs.
    #[test]
    fn block_water_type_reflects_every_cell() {
        const WATER: u16 = 0x10 << 2;
        const LAND: u16 = 0x01 << 2;
        let table = [0.0; 256];
        let heights = flat(0);
        let water =
            |t: &[u16; HEIGHTMAP_LEN]| TerrainSurface::new(&heights, &table, 0xA9, 0xB4, Some(t));

        let sea = water(&[WATER; HEIGHTMAP_LEN]);
        assert_eq!(sea.block_water_type(), WaterType::EntirelyWater);
        assert!(sea.is_entirely_water());

        let mut shore = [WATER; HEIGHTMAP_LEN];
        shore[0] = LAND;
        let shore = water(&shore);
        assert_eq!(shore.block_water_type(), WaterType::PartiallyWater);
        assert!(!shore.is_entirely_water());

        assert_eq!(
            water(&[LAND; HEIGHTMAP_LEN]).block_water_type(),
            WaterType::NotWater
        );
    }

    #[test]
    fn water_depth_by_cell_and_nearest_corner() {
        const WATER: u16 = 0x14 << 2;
        let mut t = [0u16; HEIGHTMAP_LEN];
        t[0] = WATER; // corner (0, 0) only: cell (0, 0) is part water
        let s = TerrainSurface::new(&flat(0), &[0.0; 256], 0, 0, Some(&t));
        assert_eq!(s.sample_water_depth(1.0, 1.0), 0.45);
        assert_eq!(s.sample_water_depth(13.0, 1.0), 0.1);
        assert_eq!(s.sample_water_depth(30.0, 30.0), 0.0);
        let sea = TerrainSurface::new(&flat(0), &[0.0; 256], 0, 0, Some(&[WATER; HEIGHTMAP_LEN]));
        assert_eq!(sea.sample_water_depth(100.0, 100.0), 0.9);
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/Terrain/ClientConformanceTests.cs.

    #[test]
    fn split_direction_drives_the_sampled_triangle() {
        let mut h = [0u8; HEIGHTMAP_LEN];
        h[9] = 100; // x = 1, y = 0: the south-east corner of cell (0, 0)
        let z = surface(&h, &linear_table(1.0)).sample_z(20.0, 2.0);
        // Cell (0, 0) of landblock (0, 0) is cut SW-NE: triangle
        // {SW, SE, NE} at (0.833, 0.083) gives 75.
        assert!((74.0..=77.0).contains(&z), "{z}");
    }

    #[test]
    fn heights_at_vertices_match_the_table() {
        let table = linear_table(2.0);
        let h = heightmap(|x, y| (x * 10 + y * 5) as u8);
        let s = surface(&h, &table);
        for vx in 0..=8 {
            for vy in 0..=8 {
                let expected = table[usize::from(h[vx * 9 + vy])];
                let got = s.sample_z(vx as f32 * 24.0, vy as f32 * 24.0);
                assert!((expected - got).abs() < 0.05, "({vx},{vy})");
            }
        }
        assert!((surface(&flat(50), &table).sample_z(96.0, 96.0) - 100.0).abs() < 1e-3);
    }

    #[test]
    fn interpolated_points_stay_in_range() {
        let table = linear_table(2.0);
        let h = heightmap(|x, y| (x * 10 + y * 5) as u8);
        let s = surface(&h, &table);
        let max = table[usize::from(*h.iter().max().unwrap())];
        for (x, y) in [
            (0.0, 0.0),
            (12.0, 12.0),
            (23.9, 23.9),
            (48.0, 72.0),
            (96.0, 96.0),
            (180.0, 180.0),
        ] {
            let z = s.sample_z(x, y);
            assert!((table[0]..=max).contains(&z), "({x},{y}) {z}");
        }
    }
}
