//! The polygons of an interior cell as a triangle soup, for a quick floor
//! height under a point.
//!
//! The soup holds every polygon, ceilings included, and answers with the
//! first triangle found; it is a coarse sample, not a placement oracle.

use std::collections::HashMap;

use glam::{Quat, Vec3};

use crate::globals::FLOOR_Z;

/// One cell's polygons in world space, as fan triangles.
#[derive(Debug, Clone, PartialEq)]
pub struct CellSurface {
    cell_id: u32,
    triangles: Vec<[Vec3; 3]>,
}

impl CellSurface {
    /// From world-space vertices by id and each polygon's vertex ids. A
    /// polygon with fewer than three vertices or a missing vertex is
    /// skipped. Polygons are cut into fans `(v0, v1, v2), (v0, v2, v3), ...`.
    #[must_use]
    pub fn new(cell_id: u32, vertices: &HashMap<u16, Vec3>, polygons: &[Vec<u16>]) -> Self {
        let mut triangles = Vec::new();
        for ids in polygons {
            if ids.len() < 3 {
                continue;
            }
            let Some(positions) = ids
                .iter()
                .map(|id| vertices.get(id).copied())
                .collect::<Option<Vec<Vec3>>>()
            else {
                continue;
            };
            for i in 1..positions.len() - 1 {
                triangles.push([positions[0], positions[i], positions[i + 1]]);
            }
        }
        Self { cell_id, triangles }
    }

    /// From polygons given as cell-local vertex lists, placed in the world
    /// by `rotation` then `translation`.
    #[must_use]
    pub fn transformed(
        cell_id: u32,
        polygons: &[Vec<Vec3>],
        rotation: Quat,
        translation: Vec3,
    ) -> Self {
        let place = |v: Vec3| rotation * v + translation;
        let mut triangles = Vec::new();
        for poly in polygons {
            if poly.len() < 3 {
                continue;
            }
            let first = place(poly[0]);
            let mut previous = place(poly[1]);
            for v in &poly[2..] {
                let current = place(*v);
                triangles.push([first, previous, current]);
                previous = current;
            }
        }
        Self { cell_id, triangles }
    }

    /// The cell's id.
    #[must_use]
    pub fn cell_id(&self) -> u32 {
        self.cell_id
    }

    /// The fan triangles, in world space.
    #[must_use]
    pub fn triangles(&self) -> &[[Vec3; 3]] {
        &self.triangles
    }

    /// Height of the first triangle whose outline (seen from above)
    /// contains the point.
    #[must_use]
    pub fn sample_floor_z(&self, world_x: f32, world_y: f32) -> Option<f32> {
        self.triangles
            .iter()
            .find_map(|[a, b, c]| point_in_triangle_xy(world_x, world_y, *a, *b, *c))
    }

    /// The height of the highest level, face-up face under a point that is
    /// no higher than `at_most_z`, or `None` where the cell has none: the
    /// floor a point in the cell stands on, never a ceiling, which faces
    /// down.
    #[must_use]
    pub fn sample_level_z_at_or_below(
        &self,
        world_x: f32,
        world_y: f32,
        at_most_z: f32,
    ) -> Option<f32> {
        let mut highest: Option<f32> = None;
        for [a, b, c] in &self.triangles {
            let normal = (*b - *a).cross(*c - *a);
            let length = normal.length();
            if length <= 1e-6 || normal.z / length < FLOOR_Z {
                continue;
            }
            if let Some(z) = point_in_triangle_xy(world_x, world_y, *a, *b, *c)
                && z <= at_most_z
                && highest.is_none_or(|h| z > h)
            {
                highest = Some(z);
            }
        }
        highest
    }
}

/// Barycentric containment in the xy plane (with a 1e-6 margin) and the
/// interpolated height.
#[must_use]
pub fn point_in_triangle_xy(px: f32, py: f32, a: Vec3, b: Vec3, c: Vec3) -> Option<f32> {
    let (v0x, v0y) = (c.x - a.x, c.y - a.y);
    let (v1x, v1y) = (b.x - a.x, b.y - a.y);
    let (v2x, v2y) = (px - a.x, py - a.y);
    let dot00 = v0x * v0x + v0y * v0y;
    let dot01 = v0x * v1x + v0y * v1y;
    let dot02 = v0x * v2x + v0y * v2y;
    let dot11 = v1x * v1x + v1y * v1y;
    let dot12 = v1x * v2x + v1y * v2y;
    let denom = dot00 * dot11 - dot01 * dot01;
    if denom.abs() < 1e-10 {
        return None;
    }
    let inv = 1.0 / denom;
    let u = (dot11 * dot02 - dot01 * dot12) * inv;
    let v = (dot00 * dot12 - dot01 * dot02) * inv;
    if u < -1e-6 || v < -1e-6 || u + v > 1.0 + 1e-6 {
        return None;
    }
    Some(a.z * (1.0 - u - v) + b.z * v + c.z * u)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/CellSurfaceTests.cs`.

    use super::*;

    fn flat_floor(ox: f32, oy: f32, z: f32, half: f32) -> CellSurface {
        let vertices: HashMap<u16, Vec3> = [
            (0, Vec3::new(ox - half, oy - half, z)),
            (1, Vec3::new(ox + half, oy - half, z)),
            (2, Vec3::new(ox + half, oy + half, z)),
            (3, Vec3::new(ox - half, oy + half, z)),
        ]
        .into_iter()
        .collect();
        CellSurface::new(0x0100, &vertices, &[vec![0, 1, 2, 3]])
    }

    #[test]
    fn inside_a_flat_floor() {
        let z = flat_floor(50.0, 50.0, 10.0, 10.0)
            .sample_floor_z(50.0, 50.0)
            .unwrap();
        assert!((z - 10.0).abs() < 0.01);
    }

    #[test]
    fn outside_the_floor() {
        assert!(
            flat_floor(50.0, 50.0, 10.0, 5.0)
                .sample_floor_z(100.0, 100.0)
                .is_none()
        );
    }

    #[test]
    fn at_the_edge() {
        let z = flat_floor(50.0, 50.0, 10.0, 10.0)
            .sample_floor_z(60.0, 50.0)
            .unwrap();
        assert!((z - 10.0).abs() < 0.01);
    }

    #[test]
    fn sloped_floor_interpolates() {
        let vertices: HashMap<u16, Vec3> = [
            (0, Vec3::new(0.0, 0.0, 0.0)),
            (1, Vec3::new(20.0, 0.0, 0.0)),
            (2, Vec3::new(10.0, 20.0, 20.0)),
        ]
        .into_iter()
        .collect();
        let s = CellSurface::new(0x0100, &vertices, &[vec![0, 1, 2]]);
        let z = s.sample_floor_z(10.0, 6.67).unwrap();
        assert!((5.0..=8.0).contains(&z), "z = {z}");
    }

    #[test]
    fn transformed_matches_world_vertices_bit_for_bit() {
        let local = [
            Vec3::new(-2.0, -2.0, 1.0),
            Vec3::new(2.0, -2.0, 2.0),
            Vec3::new(2.0, 2.0, 4.0),
            Vec3::new(-2.0, 2.0, 3.0),
        ];
        let rotation = Quat::from_axis_angle(Vec3::Z, 0.37);
        let translation = Vec3::new(17.0, -9.0, 6.0);
        let prepared = CellSurface::transformed(0x0100, &[local.to_vec()], rotation, translation);
        let world: HashMap<u16, Vec3> = local
            .iter()
            .enumerate()
            .map(|(i, v)| (i as u16, rotation * *v + translation))
            .collect();
        let legacy = CellSurface::new(0x0100, &world, &[vec![0, 1, 2, 3]]);
        let sample = rotation * Vec3::new(0.25, -0.5, 0.0) + translation;
        let expected = legacy.sample_floor_z(sample.x, sample.y).unwrap();
        let actual = prepared.sample_floor_z(sample.x, sample.y).unwrap();
        assert_eq!(expected.to_bits(), actual.to_bits());
    }

    // OpenAC PR #222
    // CellSurfaceTests.SampleLevelZAtOrBelow_TakesTheFloorUnderThePointNotTheCeilingOverIt.
    #[test]
    fn the_level_floor_under_a_point_is_taken_never_the_ceiling_over_it() {
        let vertices: HashMap<u16, Vec3> = [
            (0, Vec3::new(0.0, 0.0, 0.0)),
            (1, Vec3::new(10.0, 0.0, 0.0)),
            (2, Vec3::new(10.0, 10.0, 0.0)),
            (3, Vec3::new(0.0, 10.0, 0.0)),
            (4, Vec3::new(0.0, 0.0, 5.0)),
            (5, Vec3::new(0.0, 10.0, 5.0)),
            (6, Vec3::new(10.0, 10.0, 5.0)),
            (7, Vec3::new(10.0, 0.0, 5.0)),
            (8, Vec3::new(0.0, 0.0, 0.0)),
            (9, Vec3::new(10.0, 0.0, 0.0)),
            (10, Vec3::new(10.0, 0.0, 5.0)),
            (11, Vec3::new(0.0, 0.0, 5.0)),
        ]
        .into_iter()
        .collect();
        let s = CellSurface::new(
            0x0100,
            &vertices,
            &[vec![0, 1, 2, 3], vec![4, 5, 6, 7], vec![8, 9, 10, 11]],
        );

        assert_eq!(s.sample_level_z_at_or_below(5.0, 5.0, 2.5), Some(0.0));
        // The ceiling faces down, so even a point over it stands on the
        // floor, not on the ceiling.
        assert_eq!(s.sample_level_z_at_or_below(5.0, 5.0, 6.0), Some(0.0));
        assert_eq!(s.sample_level_z_at_or_below(5.0, 5.0, -1.0), None);
        assert_eq!(s.sample_level_z_at_or_below(20.0, 5.0, 2.5), None);
    }

    #[test]
    fn bad_polygons_are_skipped() {
        let vertices: HashMap<u16, Vec3> = [(0, Vec3::ZERO), (1, Vec3::X)].into_iter().collect();
        let s = CellSurface::new(1, &vertices, &[vec![0, 1], vec![0, 1, 9]]);
        assert!(s.triangles().is_empty());
        assert_eq!(s.cell_id(), 1);
    }
}
