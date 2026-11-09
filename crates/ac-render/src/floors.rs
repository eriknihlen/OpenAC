//! The floors around the player, for laying things on them: every face of
//! an interior cell or of a static object the landblocks place (a dungeon's
//! stairs are often such objects) that faces up and is no steeper than a
//! floor, stairs and ramps included, filed by where it lies seen from above,
//! so the floor under a point is found without walking every face.

use std::collections::HashMap;

use ac_nav::geometry::surfaces_of;
use ac_physics::globals::FLOOR_Z;
use ac_physics::{PhysicsEngine, point_in_triangle_xy};
use glam::Vec3;

/// The side of the squares the faces are filed in, metres.
const BUCKET_METRES: f32 = 4.0;

/// A face is filed in at most this many squares a side (from its south-west
/// corner), so a broken, huge face cannot fill the map.
const MAX_BUCKETS_A_SIDE: i32 = 64;

/// The floor faces of a neighbourhood's interior cells and static objects,
/// in its frame.
#[derive(Debug, Clone, Default)]
pub struct Floors {
    faces: Vec<[Vec3; 3]>,
    buckets: HashMap<(i32, i32), Vec<u32>>,
}

impl Floors {
    /// The floors of every interior cell and static object `engine` holds,
    /// in its frame. Objects the server places (doors, chests, creatures)
    /// are not among them.
    #[must_use]
    pub fn of(engine: &PhysicsEngine) -> Self {
        let mut faces = Vec::new();
        for id in engine.landblock_ids() {
            let Some(landblock) = engine.landblock(id) else {
                continue;
            };
            for cell in &landblock.cells {
                faces.extend_from_slice(cell.triangles());
            }
            for owner in engine.shadows.capture_static_owners_for_landblock(id) {
                if let Some(surfaces) = surfaces_of(engine, owner) {
                    faces.extend(surfaces.triangles.iter().map(|t| [t.a, t.b, t.c]));
                }
            }
        }
        Self::from_faces(faces)
    }

    /// The floors among `faces`: those that face up and are no steeper than
    /// a floor.
    #[must_use]
    pub fn from_faces(faces: impl IntoIterator<Item = [Vec3; 3]>) -> Self {
        let mut floors = Self::default();
        for face in faces {
            let [a, b, c] = face;
            if !(a + b + c).is_finite() {
                continue;
            }
            let normal = (b - a).cross(c - a);
            let length = normal.length();
            if length <= 1e-6 || normal.z / length < FLOOR_Z {
                continue;
            }
            let index = floors.faces.len() as u32;
            floors.faces.push(face);
            let low = bucket(a.min(b).min(c));
            let high = bucket(a.max(b).max(c));
            let high = (
                high.0.min(low.0 + MAX_BUCKETS_A_SIDE - 1),
                high.1.min(low.1 + MAX_BUCKETS_A_SIDE - 1),
            );
            for x in low.0..=high.0 {
                for y in low.1..=high.1 {
                    floors.buckets.entry((x, y)).or_default().push(index);
                }
            }
        }
        floors
    }

    /// Whether there are no floors.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.faces.is_empty()
    }

    /// The height of the highest floor under (`x`, `y`) that is no higher
    /// than `at_most`, or `None` where there is none.
    #[must_use]
    pub fn highest_at_or_below(&self, x: f32, y: f32, at_most: f32) -> Option<f32> {
        if !(x + y).is_finite() {
            return None;
        }
        let mut highest: Option<f32> = None;
        for &index in self.buckets.get(&bucket(Vec3::new(x, y, 0.0)))? {
            let [a, b, c] = self.faces[index as usize];
            if let Some(z) = point_in_triangle_xy(x, y, a, b, c)
                && z <= at_most
                && highest.is_none_or(|h| z > h)
            {
                highest = Some(z);
            }
        }
        highest
    }
}

/// The square holding a point seen from above.
fn bucket(point: Vec3) -> (i32, i32) {
    (
        (point.x / BUCKET_METRES).floor() as i32,
        (point.y / BUCKET_METRES).floor() as i32,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn square(x0: f32, y0: f32, side: f32, z: f32) -> [[Vec3; 3]; 2] {
        let a = Vec3::new(x0, y0, z);
        let b = Vec3::new(x0 + side, y0, z);
        let c = Vec3::new(x0 + side, y0 + side, z);
        let d = Vec3::new(x0, y0 + side, z);
        [[a, b, c], [a, c, d]]
    }

    #[test]
    fn the_highest_floor_at_or_below_a_height_is_found() {
        let mut faces = Vec::new();
        faces.extend(square(0.0, 0.0, 10.0, 0.0));
        faces.extend(square(0.0, 0.0, 10.0, 4.0));
        let floors = Floors::from_faces(faces);
        assert_eq!(floors.highest_at_or_below(5.0, 5.0, 10.0), Some(4.0));
        assert_eq!(floors.highest_at_or_below(5.0, 5.0, 3.0), Some(0.0));
        assert_eq!(floors.highest_at_or_below(5.0, 5.0, -1.0), None);
        // Off every floor.
        assert_eq!(floors.highest_at_or_below(15.0, 5.0, 10.0), None);
        // Negative coordinates, as a dungeon's cells have.
        let floors = Floors::from_faces(square(-30.0, -30.0, 20.0, -6.0));
        assert_eq!(floors.highest_at_or_below(-12.5, -25.0, 0.0), Some(-6.0));
    }

    #[test]
    fn ceilings_and_walls_are_not_floors() {
        let [ab, cd] = square(0.0, 0.0, 10.0, 3.0);
        // The same square wound the other way faces down: a ceiling.
        let ceiling = [[ab[0], ab[2], ab[1]], [cd[0], cd[2], cd[1]]];
        let wall = [
            Vec3::new(0.0, 0.0, 0.0),
            Vec3::new(10.0, 0.0, 0.0),
            Vec3::new(10.0, 0.0, 3.0),
        ];
        let floors = Floors::from_faces(ceiling.into_iter().chain([wall]));
        assert!(floors.is_empty());
        assert_eq!(floors.highest_at_or_below(5.0, 5.0, 10.0), None);
    }

    #[test]
    fn a_ramp_is_a_floor_with_its_height_along_it() {
        // Stairs as the collision has them: a ramp 5 m down over 10 m.
        let a = Vec3::new(0.0, 0.0, 5.0);
        let b = Vec3::new(0.0, 10.0, 0.0);
        let c = Vec3::new(2.0, 10.0, 0.0);
        let d = Vec3::new(2.0, 0.0, 5.0);
        let floors = Floors::from_faces([[a, c, b], [a, d, c]]);
        let z = floors.highest_at_or_below(1.0, 5.0, 10.0).unwrap();
        assert!((z - 2.5).abs() < 1e-4, "{z}");
    }

    // The route corners a walk through the Facility Hub dungeon (0x8A02)
    // gave, landblock-local: each pair is joined by stairs, some of them
    // objects the dungeon places rather than its cells. Every corner stands
    // on a floor, and a line between a pair, laid on the floors, stays on
    // them all the way rather than in the air.
    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_facility_hub_routes_lie_on_its_stairs() {
        use crate::world_frame::LandblockCoord;
        use crate::world_lines::{PlacedLine, build};

        let dir = std::env::var_os("OPENAC_DAT_DIR")
            .map(std::path::PathBuf::from)
            .or_else(ac_dat::default_dat_dir)
            .expect("no dat directory: set OPENAC_DAT_DIR");
        let dats = ac_dat::DatCollection::open(dir).expect("open the retail dats");
        let region: ac_dat::objects::Region = dats.get(0x1300_0000).unwrap().expect("region");
        let engine = crate::camera_collision::build_neighbourhood(
            &dats,
            &region.land_defs.land_height_table,
            LandblockCoord { x: 0x8A, y: 0x02 },
        )
        .expect("the neighbourhood");
        let floors = Floors::of(&engine);
        let floor = |x: f32, y: f32, at_most: f32| floors.highest_at_or_below(x, y, at_most);

        let corners = [
            (
                Vec3::new(59.88, -79.88, 5.39),
                Vec3::new(59.88, -61.12, 0.0),
            ),
            (
                Vec3::new(59.88, -49.88, -0.61),
                Vec3::new(64.12, -38.12, -6.0),
            ),
            (
                Vec3::new(99.62, -38.38, -9.0),
                Vec3::new(98.88, -24.12, -12.0),
            ),
        ];
        let lift = 0.1;
        for (a, b) in corners {
            for corner in [a, b] {
                let z = floor(corner.x, corner.y, corner.z + 0.5).expect("a floor at a corner");
                assert!((corner.z - z).abs() <= 0.5, "{corner}: floor at {z}");
            }
            let line = PlacedLine {
                start: a + Vec3::Z * lift,
                end: b + Vec3::Z * lift,
                color_rgb: 0xFFFFFF,
                follow_terrain: true,
                width_metres: 0.01,
            };
            let mut out = Vec::new();
            build(&[line], a, |_, _| None, floor, &mut out);
            assert!(!out.is_empty());
            let mut worst = 0.0f32;
            for v in &out {
                let p = Vec3::from(v.position);
                let z = floor(p.x, p.y, p.z).expect("a floor under the line");
                worst = worst.max(p.z - z - lift);
            }
            assert!(worst <= 0.1, "{a} -> {b}: {worst} m over the floor");
        }
    }
}
