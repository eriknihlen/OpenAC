//! The drawable triangle mesh of one landblock's terrain.
//!
//! 64 cells, each two triangles written as six vertices (no sharing), 384
//! vertices in all, cells in rows from south to north, west to east within
//! a row. Positions are landblock-local meters with z up; every triangle
//! winds counter-clockwise seen from above. All six vertices of a cell
//! carry the cell's packed blend recipe ([`fill_cell_data`]).
//!
//! Vertex normals are smooth: each of the 81 grid points averages the unit
//! normals of every triangle touching it, using the same split per cell as
//! the triangles, so lighting matches the ground physics walks on.

use std::collections::HashMap;

use super::blending::{TerrainBlendingContext, build_surface, fill_cell_data, get_pal_code};
use super::{CellSplitDirection, SurfaceInfo, calculate_split_direction};
use crate::physics::{CELL_SIZE, CELLS_PER_SIDE, HEIGHTMAP_LEN, HEIGHTMAP_SIDE};
use crate::vec3::{Vec3, add, cross, length_squared, normalize, sub};

/// Vertices written per cell (two triangles).
pub const VERTICES_PER_CELL: usize = 6;

/// Vertices in one landblock's mesh.
pub const VERTICES_PER_LANDBLOCK: usize = CELLS_PER_SIDE * CELLS_PER_SIDE * VERTICES_PER_CELL;

/// Side of a landblock, in meters.
pub const LANDBLOCK_SIZE: f32 = CELLS_PER_SIDE as f32 * CELL_SIZE;

/// One terrain vertex.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TerrainVertex {
    /// Landblock-local position in meters.
    pub position: Vec3,
    /// Unit normal (smoothed over the triangles touching the grid point).
    pub normal: Vec3,
    /// The cell's packed blend recipe and split ([`fill_cell_data`]).
    pub data: [u32; 4],
}

/// A landblock's terrain mesh.
#[derive(Debug, Clone, PartialEq)]
pub struct LandblockMeshData {
    /// [`VERTICES_PER_LANDBLOCK`] vertices, six per cell.
    pub vertices: Vec<TerrainVertex>,
    /// Triangle list indices: 0, 1, 2, ... (each vertex used once).
    pub indices: Vec<u32>,
}

/// The terrain of one landblock as the mesh builder reads it.
#[derive(Debug, Clone, Copy)]
pub struct LandblockTerrain<'a> {
    /// Landblock x (high byte of its id).
    pub landblock_x: u32,
    /// Landblock y (second byte of its id).
    pub landblock_y: u32,
    /// Terrain word per grid point, `x * 9 + y`: road bits 0-1, type bits
    /// 2-6.
    pub terrain: &'a [u16; HEIGHTMAP_LEN],
    /// Height index per grid point, `x * 9 + y`.
    pub heights: &'a [u8; HEIGHTMAP_LEN],
    /// Meters per height index (the region's height table).
    pub height_table: &'a [f32; 256],
}

/// Builds the mesh. Blend recipes are looked up in `surface_cache` by
/// palette code and built (then cached) only for codes not seen before.
#[must_use]
pub fn build_landblock_mesh(
    block: &LandblockTerrain<'_>,
    ctx: &TerrainBlendingContext,
    surface_cache: &mut HashMap<u32, SurfaceInfo>,
) -> LandblockMeshData {
    let heights = resolve_heights(block);
    let normals = vertex_normals(&heights, block.landblock_x, block.landblock_y);
    let corner_word = |x: usize, y: usize| block.terrain[x * HEIGHTMAP_SIDE + y];

    let mut vertices = Vec::with_capacity(VERTICES_PER_LANDBLOCK);
    for cy in 0..CELLS_PER_SIDE {
        for cx in 0..CELLS_PER_SIDE {
            let words = [
                corner_word(cx, cy),
                corner_word(cx + 1, cy),
                corner_word(cx + 1, cy + 1),
                corner_word(cx, cy + 1),
            ];
            let pal_code = get_pal_code(
                words.map(|w| u32::from(w & 0x3)),
                words.map(|w| u32::from((w >> 2) & 0x1F)),
            );
            let surface = *surface_cache
                .entry(pal_code)
                .or_insert_with(|| build_surface(pal_code, ctx));

            let split = calculate_split_direction(
                block.landblock_x,
                cx as u32,
                block.landblock_y,
                cy as u32,
            );
            let data = fill_cell_data(&surface, split);

            let corner = |x: usize, y: usize| TerrainVertex {
                position: grid_position(&heights, x, y),
                normal: normals[x][y],
                data,
            };
            let (bl, br, tr, tl) = (
                corner(cx, cy),
                corner(cx + 1, cy),
                corner(cx + 1, cy + 1),
                corner(cx, cy + 1),
            );
            match split {
                CellSplitDirection::SwToNe => vertices.extend([bl, br, tr, bl, tr, tl]),
                CellSplitDirection::SeToNw => vertices.extend([bl, br, tl, br, tr, tl]),
            }
        }
    }

    LandblockMeshData {
        vertices,
        indices: (0..VERTICES_PER_LANDBLOCK as u32).collect(),
    }
}

type Grid<T> = [[T; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];

fn resolve_heights(block: &LandblockTerrain<'_>) -> Grid<f32> {
    let mut heights = [[0.0; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
    for (x, column) in heights.iter_mut().enumerate() {
        for (y, h) in column.iter_mut().enumerate() {
            *h = block.height_table[usize::from(block.heights[x * HEIGHTMAP_SIDE + y])];
        }
    }
    heights
}

fn grid_position(heights: &Grid<f32>, x: usize, y: usize) -> Vec3 {
    [x as f32 * CELL_SIZE, y as f32 * CELL_SIZE, heights[x][y]]
}

/// Per grid point, the normalized sum of the unit normals of the triangles
/// touching it; straight up where the sum is zero.
fn vertex_normals(heights: &Grid<f32>, landblock_x: u32, landblock_y: u32) -> Grid<Vec3> {
    let mut sums = [[[0.0; 3]; HEIGHTMAP_SIDE]; HEIGHTMAP_SIDE];
    let mut accumulate = |corners: [(usize, usize); 3]| {
        let [p0, p1, p2] = corners.map(|(x, y)| grid_position(heights, x, y));
        let c = cross(sub(p1, p0), sub(p2, p0));
        if length_squared(c) <= 0.0 {
            return;
        }
        let face = normalize(c);
        for (x, y) in corners {
            sums[x][y] = add(sums[x][y], face);
        }
    };

    for cy in 0..CELLS_PER_SIDE {
        for cx in 0..CELLS_PER_SIDE {
            let (bl, br, tr, tl) = ((cx, cy), (cx + 1, cy), (cx + 1, cy + 1), (cx, cy + 1));
            match calculate_split_direction(landblock_x, cx as u32, landblock_y, cy as u32) {
                CellSplitDirection::SwToNe => {
                    accumulate([bl, br, tr]);
                    accumulate([bl, tr, tl]);
                }
                CellSplitDirection::SeToNw => {
                    accumulate([bl, br, tl]);
                    accumulate([br, tr, tl]);
                }
            }
        }
    }

    sums.map(|column| {
        column.map(|sum| {
            if length_squared(sum) > 0.0 {
                normalize(sum)
            } else {
                [0.0, 0.0, 1.0]
            }
        })
    })
}

#[cfg(test)]
mod tests {
    use super::super::AlphaMap;
    use super::*;
    use crate::physics::sample_z_from_heightmap;

    fn height_table() -> [f32; 256] {
        std::array::from_fn(|i| i as f32 * 2.0)
    }

    fn ctx() -> TerrainBlendingContext {
        let mut c = TerrainBlendingContext::empty();
        c.terrain_type_to_layer[0] = 0;
        c
    }

    struct Block {
        terrain: [u16; HEIGHTMAP_LEN],
        heights: [u8; HEIGHTMAP_LEN],
        table: [f32; 256],
    }

    impl Block {
        fn flat(height: u8) -> Self {
            Self {
                terrain: [0; HEIGHTMAP_LEN],
                heights: [height; HEIGHTMAP_LEN],
                table: height_table(),
            }
        }

        fn with_heights(f: impl Fn(usize, usize) -> u8) -> Self {
            let mut b = Self::flat(0);
            b.heights = std::array::from_fn(|i| f(i / HEIGHTMAP_SIDE, i % HEIGHTMAP_SIDE));
            b
        }

        fn view(&self, landblock_x: u32, landblock_y: u32) -> LandblockTerrain<'_> {
            LandblockTerrain {
                landblock_x,
                landblock_y,
                terrain: &self.terrain,
                heights: &self.heights,
                height_table: &self.table,
            }
        }

        fn mesh(&self, landblock_x: u32, landblock_y: u32) -> LandblockMeshData {
            build_landblock_mesh(
                &self.view(landblock_x, landblock_y),
                &ctx(),
                &mut HashMap::new(),
            )
        }

        fn position(&self, x: usize, y: usize) -> Vec3 {
            [
                x as f32 * CELL_SIZE,
                y as f32 * CELL_SIZE,
                self.table[usize::from(self.heights[x * HEIGHTMAP_SIDE + y])],
            ]
        }
    }

    // Golden values below: OpenAC tests/AcDream.Core.Tests/Terrain/LandblockMeshTests.cs.

    #[test]
    fn flat_block_has_384_vertices_and_128_triangles() {
        let m = Block::flat(0).mesh(0, 0);
        assert_eq!(m.vertices.len(), 384);
        assert_eq!(m.indices.len(), 128 * 3);
    }

    #[test]
    fn vertices_cover_exactly_192_meters() {
        let m = Block::flat(0).mesh(0, 0);
        let xs = m.vertices.iter().map(|v| v.position[0]);
        let ys = m.vertices.iter().map(|v| v.position[1]);
        assert_eq!(xs.clone().fold(f32::MAX, f32::min), 0.0);
        assert_eq!(xs.fold(f32::MIN, f32::max), 192.0);
        assert_eq!(ys.clone().fold(f32::MAX, f32::min), 0.0);
        assert_eq!(ys.fold(f32::MIN, f32::max), 192.0);
    }

    #[test]
    fn flat_block_is_level_with_upward_normals() {
        let m = Block::flat(10).mesh(0, 0);
        assert!(m.vertices.iter().all(|v| v.position[2] == 20.0));
        assert!(m.vertices.iter().all(|v| v.normal == [0.0, 0.0, 1.0]));
    }

    #[test]
    fn a_cells_vertices_share_its_data() {
        let m = Block::flat(0).mesh(0, 0);
        for cell in m.vertices.chunks(VERTICES_PER_CELL) {
            assert!(cell.iter().all(|v| v.data == cell[0].data));
        }
    }

    #[test]
    fn identical_cells_share_one_cached_recipe() {
        let b = Block::flat(0);
        let mut cache = HashMap::new();
        let _ = build_landblock_mesh(&b.view(0, 0), &ctx(), &mut cache);
        assert_eq!(cache.len(), 1);
    }

    #[test]
    fn distinct_terrain_types_give_distinct_palette_codes() {
        let mut b = Block::flat(0);
        b.terrain[4 * 9 + 4] = 4 << 2;
        let mut c = TerrainBlendingContext::empty();
        c.terrain_type_to_layer[0] = 0;
        c.terrain_type_to_layer[4] = 1;
        c.corner_alphas = (0..4)
            .map(|i| AlphaMap {
                layer: i,
                code: 1 << i,
            })
            .collect();
        let mut cache = HashMap::new();
        let _ = build_landblock_mesh(&b.view(0, 0), &c, &mut cache);
        assert!(cache.len() >= 2, "{}", cache.len());
    }

    #[test]
    fn every_triangle_winds_counter_clockwise_from_above() {
        let b = Block::with_heights(|x, y| ((x * 9 + y) * 37 % 64) as u8);
        for (lx, ly) in [(0, 0), (0xA9, 0xB4), (3, 7)] {
            let m = b.mesh(lx, ly);
            for t in m.indices.chunks(3) {
                let [p0, p1, p2] = [0, 1, 2].map(|k| m.vertices[t[k] as usize].position);
                let cross_z = (p1[0] - p0[0]) * (p2[1] - p0[1]) - (p1[1] - p0[1]) * (p2[0] - p0[0]);
                assert!(cross_z > 0.0, "lb ({lx},{ly}) triangle {}", t[0] / 3);
            }
        }
    }

    #[test]
    fn heightmap_is_x_major() {
        let mut b = Block::flat(0);
        b.heights[2 * 9] = 5; // x = 2, y = 0
        let m = b.mesh(0, 0);
        let at = |x: f32, y: f32| {
            m.vertices
                .iter()
                .find(|v| (v.position[0] - x).abs() < 0.01 && (v.position[1] - y).abs() < 0.01)
                .unwrap()
                .position[2]
        };
        assert_eq!(at(48.0, 0.0), 10.0);
        assert_eq!(at(0.0, 48.0), 0.0);
    }

    #[test]
    fn normals_average_the_incident_faces_not_central_differences() {
        let b = Block::with_heights(|x, y| {
            ((x * x * 3 + y * y * 5 + x * y * 11 + x * 7 + y * 13) % 96) as u8
        });
        let m = b.mesh(0xA9, 0xB4);

        let mut sums: HashMap<[u32; 3], Vec3> = HashMap::new();
        let key = |p: Vec3| p.map(f32::to_bits);
        for t in m.indices.chunks(3) {
            let [p0, p1, p2] = [0, 1, 2].map(|k| m.vertices[t[k] as usize].position);
            let n = normalize(cross(sub(p1, p0), sub(p2, p0)));
            for p in [p0, p1, p2] {
                let s = sums.entry(key(p)).or_insert([0.0; 3]);
                *s = add(*s, n);
            }
        }
        for v in &m.vertices {
            let expected = normalize(sums[&key(v.position)]);
            for (e, n) in expected.iter().zip(v.normal) {
                assert!((e - n).abs() < 1e-6);
            }
            assert!((length_squared(v.normal).sqrt() - 1.0).abs() < 1e-6);
        }

        let height = |x: usize, y: usize| b.position(x, y)[2];
        let mut differs = false;
        for x in 0..HEIGHTMAP_SIDE {
            for y in 0..HEIGHTMAP_SIDE {
                let (xl, xr) = (x.saturating_sub(1), (x + 1).min(8));
                let (yd, yu) = (y.saturating_sub(1), (y + 1).min(8));
                let dx = (height(xr, y) - height(xl, y)) / ((xr - xl) as f32 * CELL_SIZE);
                let dy = (height(x, yu) - height(x, yd)) / ((yu - yd) as f32 * CELL_SIZE);
                let old = normalize([-dx, -dy, 1.0]);
                let p = b.position(x, y);
                let actual = m.vertices.iter().find(|v| v.position == p).unwrap().normal;
                differs |= length_squared(sub(old, actual)).sqrt() > 1e-4;
            }
        }
        assert!(differs);
    }

    #[test]
    fn positions_follow_the_split_and_indices_are_sequential() {
        let b = Block::with_heights(|x, y| ((x * 17 + y * 29 + x * y * 3) % 80) as u8);
        for (lx, ly) in [(0, 0), (0xA9, 0xB4)] {
            let m = b.mesh(lx, ly);
            assert!(m.indices.iter().enumerate().all(|(i, &v)| v == i as u32));
            let mut vi = 0;
            for cy in 0..CELLS_PER_SIDE {
                for cx in 0..CELLS_PER_SIDE {
                    let (bl, br, tr, tl) = (
                        b.position(cx, cy),
                        b.position(cx + 1, cy),
                        b.position(cx + 1, cy + 1),
                        b.position(cx, cy + 1),
                    );
                    let expected = match calculate_split_direction(lx, cx as u32, ly, cy as u32) {
                        CellSplitDirection::SwToNe => [bl, br, tr, bl, tr, tl],
                        CellSplitDirection::SeToNw => [bl, br, tl, br, tr, tl],
                    };
                    for p in expected {
                        assert_eq!(m.vertices[vi].position, p);
                        vi += 1;
                    }
                }
            }
            assert_eq!(vi, VERTICES_PER_LANDBLOCK);
        }
    }

    /// Height of the mesh under a point, by barycentric search: OpenAC
    /// tests/AcDream.Core.Tests/Terrain/TerrainModernConformanceTests.cs
    /// `SampleMeshZ`, except that it takes the triangle the point is most
    /// inside rather than the first within the edge tolerance (a point a
    /// hair outside a steep triangle would otherwise read its extended
    /// plane).
    fn sample_mesh_z(m: &LandblockMeshData, lx: f32, ly: f32) -> Option<f32> {
        let mut best: Option<(f32, f32)> = None;
        for t in m.indices.chunks(3) {
            let [v0, v1, v2] = [0, 1, 2].map(|k| m.vertices[t[k] as usize].position);
            let denom = (v1[1] - v2[1]) * (v0[0] - v2[0]) + (v2[0] - v1[0]) * (v0[1] - v2[1]);
            if denom.abs() < 1e-9 {
                continue;
            }
            let a = ((v1[1] - v2[1]) * (lx - v2[0]) + (v2[0] - v1[0]) * (ly - v2[1])) / denom;
            let b = ((v2[1] - v0[1]) * (lx - v2[0]) + (v0[0] - v2[0]) * (ly - v2[1])) / denom;
            let c = 1.0 - a - b;
            const EPS: f32 = 1e-4;
            let inside = a.min(b).min(c);
            if inside >= -EPS && best.is_none_or(|(most, _)| inside > most) {
                best = Some((inside, a * v0[2] + b * v1[2] + c * v2[2]));
            }
        }
        best.map(|(_, z)| z)
    }

    /// The drawn mesh and the physics ground agree within a millimeter:
    /// OpenAC TerrainModernConformanceTests (there over real landblocks;
    /// here over generated heights at the same landblock coordinates).
    #[test]
    fn mesh_height_agrees_with_physics_height() {
        let landblocks = [
            (0xA9, 0xB0),
            (0xA9, 0xB1),
            (0x80, 0x80),
            (0xCB, 0x99),
            (0xC0, 0x40),
            (0x00, 0x00),
            (0x7F, 0x7F),
            (0xFE, 0xFE),
            (0x01, 0x85),
            (0x4D, 0x96),
        ];
        let table: [f32; 256] = std::array::from_fn(|i| i as f32 * 2.0 + 0.25 * (i % 3) as f32);
        let mut rng = 0x2545_F491_u32;
        let mut next = move || {
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            rng
        };
        for (lx, ly) in landblocks {
            let mut b = Block::flat(0);
            b.table = table;
            // Rolling ground like the dats': a gentle slope plus up to 15
            // height steps of noise between neighbouring samples.
            b.heights = std::array::from_fn(|i| {
                let (x, y) = (i / HEIGHTMAP_SIDE, i % HEIGHTMAP_SIDE);
                (x * 9 + y * 5) as u8 + (next() % 16) as u8
            });
            let m = b.mesh(lx, ly);
            for _ in 0..200 {
                let x = (next() % 1_000_000) as f32 / 1_000_000.0 * 191.975;
                let y = (next() % 1_000_000) as f32 / 1_000_000.0 * 191.975;
                let mesh_z = sample_mesh_z(&m, x, y).unwrap();
                let physics_z = sample_z_from_heightmap(&b.heights, &b.table, lx, ly, x, y);
                assert!(
                    (mesh_z - physics_z).abs() < 0.001,
                    "lb {lx:02X}{ly:02X} ({x},{y}): mesh {mesh_z} physics {physics_z}"
                );
            }
        }
    }
}
