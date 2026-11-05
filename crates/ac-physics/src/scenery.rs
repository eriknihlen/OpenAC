//! Procedural scenery: the trees, rocks and bushes the game scatters over
//! each landblock from the region's scene tables.
//!
//! Every terrain vertex names a terrain type and a scene type; the region
//! maps those to a list of scenes, and a fixed hash of the vertex's global
//! coordinates picks one. Each object of the scene is placed, turned and
//! scaled by further hashes of the same coordinates, so every client sees
//! the same scenery without it being stored anywhere. An object is dropped
//! when its hash says so, when it lands outside the landblock, on a road,
//! in a land cell that holds a building, on ground too steep or too flat
//! for it, or when its shape pokes out of the landblock.
//!
//! Pure logic: the caller supplies the scenes and the objects' shapes.

use crate::terrain::TerrainSurface;
use ac_dat::objects::{BspNode, BspTree, GfxObj, LandBlock, ObjectDesc, Region, Scene, Setup};
use glam::{Mat3, Quat, Vec3};

/// Side of a landblock, metres.
const BLOCK_LENGTH: f32 = 192.0;
/// Side of a land cell, metres.
const CELL_LENGTH: f32 = 24.0;
/// Terrain vertices along a landblock side.
const VERTICES_PER_SIDE: u32 = 9;
/// Width of the road strip along a road vertex, metres.
const ROAD_WIDTH: f32 = 5.0;
/// One over 2^32: turns a hash into a fraction in 0..1.
const HASH_SCALE: f64 = 1.0 / 4_294_967_296.0;

/// One placed scenery object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ScenerySpawn {
    /// GfxObj (`0x01`) or Setup (`0x02`) id.
    pub object_id: u32,
    /// Landblock-local position, metres (z on the ground).
    pub origin: Vec3,
    /// Orientation.
    pub rotation: Quat,
    /// Uniform scale.
    pub scale: f32,
}

/// What the placement needs to know about an object's shape to keep it
/// inside its landblock.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ObjectShape {
    /// A part has a collision tree.
    pub has_physics_bsp: bool,
    /// The sorting sphere: centre (object-local) and radius.
    pub sorting_sphere: (Vec3, f32),
    /// Collision cylinders: base centre (object-local) and radius.
    pub cyl_spheres: Vec<(Vec3, f32)>,
    /// Number of collision spheres.
    pub sphere_count: usize,
}

/// The dat objects scenery placement reads.
pub trait SceneryDats {
    /// A scene by id.
    fn scene(&self, id: u32) -> Option<std::sync::Arc<Scene>>;
    /// An object's shape; `None` when the object cannot be made.
    fn shape(&self, object_id: u32) -> Option<ObjectShape>;
}

/// The scenery of a landblock. `building_origins` are the landblock-local
/// positions of its buildings (each blocks scenery in its land cell).
pub fn generate(
    region: &Region,
    block: &LandBlock,
    building_origins: &[Vec3],
    dats: &impl SceneryDats,
) -> Vec<ScenerySpawn> {
    let mut out = Vec::new();
    let Some(scene_info) = region.scene_info.as_ref() else {
        return out;
    };
    let block_x = (block.id >> 24) & 0xFF;
    let block_y = (block.id >> 16) & 0xFF;
    let ground = TerrainSurface::from_landblock(block, &region.land_defs.land_height_table);
    let building_cells: Vec<(u32, u32)> = building_origins.iter().map(|o| land_cell(*o)).collect();

    for vx in 0..VERTICES_PER_SIDE {
        for vy in 0..VERTICES_PER_SIDE {
            let word = block.terrain[(vx * VERTICES_PER_SIDE + vy) as usize];
            let terrain_type = usize::from(word.terrain_type());
            let scene_type = usize::from(word.scenery());
            let Some(scene_types) = region
                .terrain_info
                .terrain_types
                .get(terrain_type)
                .map(|t| &t.scene_types)
            else {
                continue;
            };
            let Some(&scene_info_index) = scene_types.get(scene_type) else {
                continue;
            };
            let Some(scenes) = scene_info
                .scene_types
                .get(scene_info_index as usize)
                .map(|s| &s.scenes)
            else {
                continue;
            };
            if scenes.is_empty() {
                continue;
            }
            let gx = block_x * 8 + vx;
            let gy = block_y * 8 + vy;
            let index = scene_index(gx, gy, scenes.len());
            let Some(scene) = dats.scene(scenes[index]) else {
                continue;
            };
            for (k, object) in scene.objects.iter().enumerate() {
                let k = k as u32;
                if object.weenie_obj != 0
                    || placement_noise(gx, gy, k).partial_cmp(&f64::from(object.frequency))
                        != Some(std::cmp::Ordering::Less)
                {
                    continue;
                }
                let displaced = place(object, gx, gy, k);
                let x = vx as f32 * CELL_LENGTH + displaced.x;
                let y = vy as f32 * CELL_LENGTH + displaced.y;
                if !(0.0..BLOCK_LENGTH).contains(&x) || !(0.0..BLOCK_LENGTH).contains(&y) {
                    continue;
                }
                if on_road(block, x, y) {
                    continue;
                }
                let cell = land_cell(Vec3::new(x, y, 0.0));
                if building_cells.contains(&cell) {
                    continue;
                }
                let (z, normal) = ground.sample_surface(x, y);
                if !check_slope(object, normal.z) {
                    continue;
                }
                let origin = Vec3::new(x, y, z);
                let base = quat(object.base_loc.orientation);
                let rotation = if object.align != 0 {
                    obj_align(base, normal)
                } else {
                    obj_frame_rotation(object, base, gx, gy, k)
                };
                let Some(shape) = dats.shape(object.object_id) else {
                    continue;
                };
                if !obj_within_block(&shape, origin, rotation) {
                    continue;
                }
                out.push(ScenerySpawn {
                    object_id: object.object_id,
                    origin,
                    rotation,
                    scale: scale_obj(object, gx, gy, k),
                });
            }
        }
    }
    out
}

/// The land cell (0..8 each way) holding a landblock-local point.
fn land_cell(p: Vec3) -> (u32, u32) {
    let cell = |v: f32| ((v / CELL_LENGTH).floor().max(0.0) as u32).min(7);
    (cell(p.x), cell(p.y))
}

/// A hash as a fraction in 0..1.
fn fraction(hash: u32) -> f64 {
    f64::from(hash) * HASH_SCALE
}

/// Which of `count` scenes a vertex at global coordinates (`gx`, `gy`) uses.
#[must_use]
pub fn scene_index(gx: u32, gy: u32, count: usize) -> usize {
    if count == 1 {
        return 0;
    }
    let hash = gx
        .wrapping_mul(0x2A7F_2B89)
        .wrapping_add(0x6C1A_C587)
        .wrapping_mul(gy)
        .wrapping_sub(gx.wrapping_mul(0x421B_E3BD))
        .wrapping_add(0x7F8C_DA01);
    let index = (fraction(hash) * count as f64).floor() as usize;
    if index >= count { 0 } else { index }
}

/// The per-object hash `placement_noise < frequency` gates on.
#[must_use]
pub fn placement_noise(gx: u32, gy: u32, k: u32) -> f64 {
    let cell = gx
        .wrapping_mul(gy)
        .wrapping_mul(0x5111_BFEF)
        .wrapping_add(0x7089_2FB7);
    let hash = gy
        .wrapping_mul(0x6C1A_C587)
        .wrapping_sub(cell.wrapping_mul(k.wrapping_add(0x5B67)))
        .wrapping_add(gx.wrapping_mul(0xBDE4_1C43));
    fraction(hash)
}

/// The hash the placement, rotation and scale draw from, with the object's
/// own salt.
fn object_hash(gx: u32, gy: u32, k: u32, salt: u32) -> f64 {
    let cell = gx
        .wrapping_mul(gy)
        .wrapping_mul(0x5111_BFEF)
        .wrapping_add(0x7089_2FB7);
    let hash = gy
        .wrapping_mul(0x6C1A_C587)
        .wrapping_sub(cell.wrapping_mul(k.wrapping_add(salt)))
        .wrapping_sub(gx.wrapping_mul(0x421B_E3BD));
    fraction(hash)
}

/// Where an object lands within its cell: its base position displaced by
/// up to its ranges, then turned by a quarter, half or three-quarter turn.
#[must_use]
pub fn place(object: &ObjectDesc, gx: u32, gy: u32, k: u32) -> Vec3 {
    let base = object.base_loc.origin;
    let x = if object.displace_x <= 0.0 {
        base.x
    } else {
        (object_hash(gx, gy, k, 45_773) * f64::from(object.displace_x) + f64::from(base.x)) as f32
    };
    let y = if object.displace_y <= 0.0 {
        base.y
    } else {
        (object_hash(gx, gy, k, 72_719) * f64::from(object.displace_y) + f64::from(base.y)) as f32
    };
    let quadrant = fraction(
        gy.wrapping_mul(0x6C1A_C587)
            .wrapping_sub(
                gy.wrapping_mul(0x6F7B_D965)
                    .wrapping_add(0x421B_E3BD)
                    .wrapping_mul(gx),
            )
            .wrapping_sub(0x17FC_EDFD),
    );
    let (x, y) = if quadrant >= 0.75 {
        (y, -x)
    } else if quadrant >= 0.5 {
        (-x, -y)
    } else if quadrant >= 0.25 {
        (-y, x)
    } else {
        (x, y)
    };
    Vec3::new(x, y, base.z)
}

/// The object's scale: a hash-chosen point between its smallest and
/// largest, spread geometrically.
#[must_use]
pub fn scale_obj(object: &ObjectDesc, gx: u32, gy: u32, k: u32) -> f32 {
    let (min, max) = (object.min_scale, object.max_scale);
    if min == max {
        return max;
    }
    (f64::from(max / min).powf(object_hash(gx, gy, k, 32_593)) * f64::from(min)) as f32
}

/// The ground's up-component fits the object's slope range.
#[must_use]
pub fn check_slope(object: &ObjectDesc, normal_z: f32) -> bool {
    normal_z >= object.min_slope && normal_z <= object.max_slope
}

/// The orientation of an object that is not aligned to the ground: its
/// base orientation, turned to a hash-chosen heading when it may rotate.
#[must_use]
pub fn obj_frame_rotation(object: &ObjectDesc, base: Quat, gx: u32, gy: u32, k: u32) -> Quat {
    if object.max_rotation > 0.0 {
        let degrees = (object_hash(gx, gy, k, 63_127) * f64::from(object.max_rotation)) as f32;
        set_heading(base, degrees)
    } else {
        base
    }
}

/// The orientation of an object aligned to the ground: its base
/// orientation turned to face down the slope.
#[must_use]
pub fn obj_align(base: Quat, normal: Vec3) -> Quat {
    set_heading(base, heading_of(-normal))
}

/// A direction's compass heading in degrees (0 = north, +y; clockwise),
/// from its horizontal part; 0 when it has none.
#[must_use]
pub fn heading_of(v: Vec3) -> f32 {
    let flat = Vec3::new(v.x, v.y, 0.0);
    let Some(n) = normalize_checked(flat) else {
        return 0.0;
    };
    (450.0 - n.y.atan2(n.x).to_degrees()) % 360.0
}

/// An orientation turned to face `degrees` (compass), keeping the up
/// component of its current heading.
#[must_use]
pub fn set_heading(orientation: Quat, degrees: f32) -> Quat {
    let radians = f64::from(degrees) * 0.017_453_292_519_943_295;
    let m = Mat3::from_quat(orientation);
    let heading = Vec3::new(
        radians.sin() as f32,
        radians.cos() as f32,
        m.y_axis.z + m.x_axis.z,
    );
    set_vector_heading(orientation, heading)
}

/// An orientation facing along `heading`: a turn about z to its compass
/// heading and a tilt for its up component.
fn set_vector_heading(orientation: Quat, heading: Vec3) -> Quat {
    let Some(n) = normalize_checked(heading) else {
        return orientation;
    };
    let z_degrees = 450.0 - n.y.atan2(n.x).to_degrees();
    let z_rotation = -(z_degrees % 360.0).to_radians();
    let x_rotation = n.z.asin();
    (Quat::from_rotation_y(x_rotation) * Quat::from_rotation_z(z_rotation)).normalize()
}

/// `v` normalized; `None` when it is too short to have a direction.
fn normalize_checked(v: Vec3) -> Option<Vec3> {
    let length = v.length();
    (length >= 0.000_2).then(|| v / length)
}

/// Whether a point lies on the road strips of the landblock's road
/// vertices.
#[must_use]
pub fn on_road(block: &LandBlock, x: f32, y: f32) -> bool {
    let cx = (x / CELL_LENGTH) as i32;
    let cy = (y / CELL_LENGTH) as i32;
    let road = |vx: i32, vy: i32| -> bool {
        if !(0..9).contains(&vx) || !(0..9).contains(&vy) {
            return false;
        }
        block.terrain[(vx * 9 + vy) as usize].road() != 0
    };
    let r0 = road(cx, cy);
    let r1 = road(cx, cy + 1);
    let r2 = road(cx + 1, cy);
    let r3 = road(cx + 1, cy + 1);
    if !(r0 || r1 || r2 || r3) {
        return false;
    }
    let dx = x - cx as f32 * CELL_LENGTH;
    let dy = y - cy as f32 * CELL_LENGTH;
    let min = ROAD_WIDTH;
    let max = CELL_LENGTH - ROAD_WIDTH;
    match (r0, r1, r2, r3) {
        (true, true, true, true) => true,
        (true, true, true, false) => dx < min || dy < min,
        (true, true, false, true) => dx < min || dy > max,
        (true, true, false, false) => dx < min,
        (true, false, true, true) => dx > max || dy < min,
        (true, false, true, false) => dy < min,
        (true, false, false, true) => (dx - dy).abs() < min,
        (true, false, false, false) => dx + dy < min,
        (false, true, true, true) => dx > max || dy > max,
        (false, true, true, false) => (dx + dy - CELL_LENGTH).abs() < min,
        (false, true, false, true) => dy > max,
        (false, true, false, false) => CELL_LENGTH + dx - dy < min,
        (false, false, true, true) => dx > max,
        (false, false, true, false) => CELL_LENGTH - dx + dy < min,
        (false, false, false, true) => CELL_LENGTH * 2.0 - dx - dy < min,
        (false, false, false, false) => false,
    }
}

/// Whether an object placed at `origin` with `rotation` (scale 1) stays
/// inside its landblock: its sorting sphere when it has a collision tree,
/// else each collision cylinder, else its origin (or its sorting sphere
/// when it has collision spheres).
#[must_use]
pub fn obj_within_block(shape: &ObjectShape, origin: Vec3, rotation: Quat) -> bool {
    let global = |local: Vec3| rotation * local + origin;
    let in_block = |p: Vec3, radius: f32| {
        p.x >= radius && p.y >= radius && p.x < BLOCK_LENGTH - radius && p.y < BLOCK_LENGTH - radius
    };
    let (sort_center, sort_radius) = shape.sorting_sphere;
    if shape.has_physics_bsp {
        return in_block(global(sort_center), sort_radius);
    }
    if !shape.cyl_spheres.is_empty() {
        return shape
            .cyl_spheres
            .iter()
            .all(|&(low, radius)| in_block(global(low), radius));
    }
    if shape.sphere_count > 0 {
        in_block(global(sort_center), sort_radius)
    } else {
        in_block(origin, 0.0)
    }
}

/// The shape scenery placement keeps inside its landblock: a setup's
/// sorting sphere, cylinders and spheres, or for a lone GfxObj the root
/// sphere of its collision tree (else of its drawing tree). `None` when the
/// object cannot be read.
#[must_use]
pub fn object_shape(
    gfx_obj: &dyn Fn(u32) -> Option<std::sync::Arc<GfxObj>>,
    setup: &dyn Fn(u32) -> Option<std::sync::Arc<Setup>>,
    object_id: u32,
) -> Option<ObjectShape> {
    let root_sphere = |tree: &Option<BspTree>| {
        tree.as_ref().and_then(|t| t.root()).map(|n| match n {
            BspNode::Leaf(l) => l.sphere,
            BspNode::Split(s) => s.sphere,
            BspNode::Portal(p) => p.sphere,
        })
    };
    let vec3 = |v: ac_dat::types::Vector3| Vec3::new(v.x, v.y, v.z);
    match object_id >> 24 {
        0x01 => {
            let gfx = gfx_obj(object_id)?;
            let sphere = root_sphere(&gfx.physics_bsp)
                .or_else(|| root_sphere(&gfx.drawing_bsp))
                .unwrap_or_default();
            Some(ObjectShape {
                has_physics_bsp: gfx.physics_bsp.is_some(),
                sorting_sphere: (vec3(sphere.origin), sphere.radius),
                cyl_spheres: Vec::new(),
                sphere_count: 0,
            })
        }
        0x02 => {
            let setup = setup(object_id)?;
            Some(ObjectShape {
                has_physics_bsp: setup
                    .parts
                    .iter()
                    .any(|&p| gfx_obj(p).is_some_and(|g| g.physics_bsp.is_some())),
                sorting_sphere: (
                    vec3(setup.sorting_sphere.origin),
                    setup.sorting_sphere.radius,
                ),
                cyl_spheres: setup
                    .cyl_spheres
                    .iter()
                    .map(|c| (vec3(c.origin), c.radius))
                    .collect(),
                sphere_count: setup.spheres.len(),
            })
        }
        _ => None,
    }
}

fn quat(q: ac_dat::types::Quaternion) -> Quat {
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_dat::objects::TerrainInfo;
    use ac_dat::types::{Frame, Quaternion, Vector3};

    fn object(displace: f32) -> ObjectDesc {
        ObjectDesc {
            object_id: 0x0100_0001,
            base_loc: Frame {
                origin: Vector3 {
                    x: 1.0,
                    y: 2.0,
                    z: 0.5,
                },
                orientation: Quaternion {
                    w: 1.0,
                    x: 0.0,
                    y: 0.0,
                    z: 0.0,
                },
            },
            frequency: 1.0,
            displace_x: displace,
            displace_y: displace,
            min_scale: 0.8,
            max_scale: 1.2,
            max_rotation: 360.0,
            min_slope: 0.0,
            max_slope: 1.0,
            align: 0,
            orient: 0,
            weenie_obj: 0,
        }
    }

    // Golden hashes at points spread over the world's vertex grid: the
    // scene picked out of seven, the placement noise, and where an object
    // with base (1, 2) and a 12 m displacement range lands. The game keeps
    // the hashes in 32-bit unsigned arithmetic and scales them to 0..1, so
    // the values are exact to within that scaling.
    const GOLDEN: [(u32, u32, u32, usize, f64, f32, f32); 6] = [
        (0, 0, 0, 3, 0.956_417_105_9, 4.579_441, -6.951_578),
        (37, 41, 1, 0, 0.286_291_405_1, 5.309_215, 10.653_508),
        (518, 1023, 2, 0, 0.086_660_453_6, -9.343_902, 6.577_094),
        (1999, 7, 3, 0, 0.022_137_385_3, 3.057_761, -1.870_245_6),
        (1210, 1640, 0, 5, 0.984_152_307_0, -8.420_999, 5.626_501),
        (2039, 2039, 3, 5, 0.507_952_111_3, -3.147_04, 8.717_746),
    ];

    #[test]
    fn hashes_match_the_golden_values() {
        for (gx, gy, j, scene, noise, x, y) in GOLDEN {
            assert_eq!(scene_index(gx, gy, 7), scene, "{gx} {gy}");
            let ours = placement_noise(gx, gy, j);
            assert!((ours - noise).abs() < 1e-9, "{gx} {gy} {j}: {ours}");
            let p = place(&object(12.0), gx, gy, j);
            assert!(
                (p.x - x).abs() < 1e-4 && (p.y - y).abs() < 1e-4,
                "{gx} {gy} {j}: {p} vs ({x}, {y})"
            );
        }
    }

    #[test]
    fn hashes_stay_in_range_over_a_sweep() {
        for gx in (0..2040).step_by(37) {
            for gy in (0..2040).step_by(41) {
                assert!(scene_index(gx, gy, 7) < 7);
                for j in 0..4 {
                    let noise = placement_noise(gx, gy, j);
                    assert!((0.0..1.0).contains(&noise), "{gx} {gy} {j}: {noise}");
                    // Displaced by up to 12 m from (1, 2), then turned by a
                    // quarter: every coordinate stays within 14 m.
                    let p = place(&object(12.0), gx, gy, j);
                    assert!(p.x.abs() <= 14.0 && p.y.abs() <= 14.0, "{gx} {gy} {j}: {p}");
                    assert!(p.x.abs() >= 1.0 || p.y.abs() >= 1.0, "{gx} {gy} {j}: {p}");
                }
            }
        }
    }

    #[test]
    fn placement_keeps_the_base_when_it_does_not_displace() {
        // gx = gy = 0: the quadrant hash is 0 - 0 - 0x17FCEDFD, fraction
        // 0.906, the three-quarter turn (y, -x).
        let p = place(&object(0.0), 0, 0, 0);
        assert_eq!(p, Vec3::new(2.0, -1.0, 0.5));
    }

    #[test]
    fn scale_is_fixed_when_the_range_is_empty_and_inside_it_otherwise() {
        let mut o = object(0.0);
        assert!((0.8..=1.2).contains(&scale_obj(&o, 5, 9, 1)));
        o.min_scale = 1.5;
        o.max_scale = 1.5;
        assert_eq!(scale_obj(&o, 5, 9, 1), 1.5);
    }

    #[test]
    fn heading_is_compass_degrees() {
        assert!((heading_of(Vec3::Y) - 0.0).abs() < 1e-4);
        assert!((heading_of(Vec3::X) - 90.0).abs() < 1e-4);
        assert!((heading_of(-Vec3::Y) - 180.0).abs() < 1e-4);
        assert_eq!(heading_of(Vec3::Z), 0.0);
    }

    #[test]
    fn set_heading_turns_local_north_to_the_heading() {
        for degrees in [0.0_f32, 45.0, 90.0, 200.0] {
            let q = set_heading(Quat::IDENTITY, degrees);
            let forward = q * Vec3::Y;
            let r = degrees.to_radians();
            assert!(
                (forward - Vec3::new(r.sin(), r.cos(), 0.0)).length() < 1e-4,
                "{degrees}: {forward}"
            );
        }
    }

    fn block_with_roads(roads: [(usize, u16); 4]) -> LandBlock {
        let mut terrain = [TerrainInfo(0); 81];
        for (i, r) in roads {
            terrain[i] = TerrainInfo(r);
        }
        LandBlock {
            id: 0x0101_FFFF,
            has_objects: false,
            terrain,
            height: [0; 81],
        }
    }

    #[test]
    fn roads_cover_strips_along_their_vertices() {
        // Road on vertex (0, 0) only: the corner triangle dx + dy < 5.
        let b = block_with_roads([(0, 1), (1, 0), (9, 0), (10, 0)]);
        assert!(on_road(&b, 2.0, 2.0));
        assert!(!on_road(&b, 3.0, 3.0));
        // Roads on (0, 0) and (0, 1): the strip x < 5 along the west edge.
        let b = block_with_roads([(0, 1), (1, 2), (9, 0), (10, 0)]);
        assert!(on_road(&b, 4.9, 20.0));
        assert!(!on_road(&b, 5.1, 20.0));
        // No roads nearby.
        let b = block_with_roads([(40, 1), (1, 0), (9, 0), (10, 0)]);
        assert!(!on_road(&b, 2.0, 2.0));
    }

    #[test]
    fn within_block_tests_the_shape_the_object_has() {
        let bsp = ObjectShape {
            has_physics_bsp: true,
            sorting_sphere: (Vec3::ZERO, 3.0),
            ..ObjectShape::default()
        };
        assert!(obj_within_block(
            &bsp,
            Vec3::new(10.0, 10.0, 0.0),
            Quat::IDENTITY
        ));
        assert!(!obj_within_block(
            &bsp,
            Vec3::new(2.0, 10.0, 0.0),
            Quat::IDENTITY
        ));
        assert!(!obj_within_block(
            &bsp,
            Vec3::new(10.0, 190.0, 0.0),
            Quat::IDENTITY
        ));
        let cyl = ObjectShape {
            cyl_spheres: vec![(Vec3::new(0.0, 4.0, 0.0), 1.0)],
            ..ObjectShape::default()
        };
        // The cylinder sits 4 m north of the origin.
        assert!(!obj_within_block(
            &cyl,
            Vec3::new(10.0, 187.5, 0.0),
            Quat::IDENTITY
        ));
        assert!(obj_within_block(
            &cyl,
            Vec3::new(10.0, 186.5, 0.0),
            Quat::IDENTITY
        ));
        let bare = ObjectShape::default();
        assert!(obj_within_block(
            &bare,
            Vec3::new(0.0, 0.0, 0.0),
            Quat::IDENTITY
        ));
        assert!(!obj_within_block(
            &bare,
            Vec3::new(192.0, 0.0, 0.0),
            Quat::IDENTITY
        ));
    }
}
