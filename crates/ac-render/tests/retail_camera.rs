//! The chase camera's collision against the retail dats: a Holtburg
//! cottage seen in a live run.
//! Ignored by default; `scripts/check.*` runs them when the dats are present.
//!
//! The player stood just inside the cottage's doorway (cell 0xA9B4016A,
//! facing north) with the default boom 2.5 m behind: the wanted eye is
//! outside the room, 1 m beyond the doorway, where no cell the window's
//! point search reaches from the player's cell holds it. The window then
//! rooted the frame in the room with the eye outside its walls, and
//! everything outside the room's own polygons stayed the clear colour.
//! The game sweeps the eye from the pivot (OpenAC
//! `PhysicsCameraCollisionProbe`): through the open doorway it reaches the
//! outdoor cell, and against a wall it stops inside the room.

use std::path::PathBuf;

use ac_dat::objects::Region;
use ac_physics::cell_transit::{CellCollector, build_cell_set_and_pick_containing};
use ac_physics::{CellArray, PhysicsEngine, Sphere};
use ac_render::assets::REGION_ID;
use ac_render::camera::{ChaseCamera, ChaseCollision, ChaseSubject, desired_pose};
use ac_render::camera_collision::{
    CameraCollisionProbe, PhysicsCameraProbe, VIEWER_SPHERE_RADIUS, build_neighbourhood,
};
use ac_render::content::Content;
use ac_render::world_frame::LandblockCoord;
use glam::Vec3;

const ROOM: u32 = 0xA9B4_016A;
const HOLTBURG: LandblockCoord = LandblockCoord { x: 0xA9, y: 0xB4 };

fn holtburg() -> PhysicsEngine {
    neighbourhood(HOLTBURG)
}

fn neighbourhood(center: LandblockCoord) -> PhysicsEngine {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    let content = Content::open(&dir).expect("open the retail dats");
    let region = content.dats.get::<Region>(REGION_ID).unwrap().unwrap();
    build_neighbourhood(&content.dats, &region.land_defs.land_height_table, center)
        .expect("the camera's collision builds")
}

/// The default chase boom's wanted eye for a player at `feet` facing
/// `heading` (a horizontal direction), and the pivot.
fn default_boom(feet: Vec3, heading: Vec3) -> (Vec3, Vec3) {
    let cam = ChaseCamera::default();
    let pivot = feet + Vec3::new(0.0, 0.0, cam.pivot_height);
    let (eye, _) = desired_pose(pivot, heading, cam.distance, cam.pitch, cam.yaw_offset);
    (pivot, eye)
}

/// Whether the room's containment tree holds `point`.
fn in_room(engine: &PhysicsEngine, point: Vec3) -> bool {
    let room = engine.world().unwrap().cell(ROOM).unwrap();
    room.containment_bsp
        .point_inside(room.placement.to_local(point))
}

#[test]
#[ignore = "needs the retail dat files"]
fn the_eye_behind_the_cottage_doorway_is_outdoors_not_in_the_room() {
    let engine = holtburg();
    // A live run's end point, facing north.
    let feet = Vec3::new(75.865, 34.712, 94.005);
    let (pivot, wanted) = default_boom(feet, Vec3::Y);
    assert!(in_room(&engine, pivot));
    assert!(
        !in_room(&engine, wanted),
        "the wanted eye is outside the room"
    );

    // The cause: the point search from the player's cell finds no cell
    // holding the eye and keeps the player's room (flag false).
    let mut cells = CellArray::new();
    let mut collector = CellCollector::new(&mut cells);
    let (picked, contained) = build_cell_set_and_pick_containing(
        engine.world().unwrap(),
        &[Sphere::new(wanted, VIEWER_SPHERE_RADIUS)],
        1,
        ROOM,
        None,
        &mut collector,
    );
    assert_eq!((picked, contained), (ROOM, false));

    // The sweep follows the boom through the open doorway (cells 0x016C
    // and the outdoors) and ends in the outdoor cell holding the eye.
    let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
    let swept = probe.sweep_eye(pivot, wanted, ROOM, 0x5000_0001, feet);
    assert_eq!(swept.viewer_cell, 0xA9B4_001A);
    assert!(swept.eye.distance(wanted) < 0.01, "{swept:?}");

    // The camera publishes that eye and that cell.
    let mut cam = ChaseCamera::default();
    cam.update(
        &ChaseSubject::standing(feet, Vec3::Y.y.atan2(Vec3::Y.x)),
        1.0 / 60.0,
        Some(ChaseCollision {
            probe: &probe,
            cell_id: ROOM,
            self_id: 0x5000_0001,
        }),
    );
    assert_eq!(cam.viewer_cell(), Some(0xA9B4_001A));
    assert!(cam.eye().distance(wanted) < 0.01);
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_boom_into_the_cottage_wall_stops_inside_the_room() {
    let engine = holtburg();
    let room = engine.world().unwrap().cell(ROOM).unwrap();
    // 3.5 m from the room's middle toward its local +y wall (about 5.2 m
    // out), facing back into the room: the 2.5 m boom reaches past the
    // wall.
    let feet = room.placement.to_world(Vec3::new(0.0, 3.5, 0.0));
    let heading = room.placement.to_world(Vec3::new(0.0, -1.0, 0.0)) - room.placement.origin;
    let (pivot, wanted) = default_boom(feet, heading);
    assert!(in_room(&engine, pivot));
    assert!(
        !in_room(&engine, wanted),
        "the wanted eye is beyond the wall"
    );

    let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
    let swept = probe.sweep_eye(pivot, wanted, ROOM, 0x5000_0001, feet);
    assert_eq!(swept.viewer_cell, ROOM, "{swept:?}");
    assert!(in_room(&engine, swept.eye), "{swept:?}");
    let pulled_in = wanted.distance(pivot) - swept.eye.distance(pivot);
    assert!(pulled_in > 0.5, "pulled in only {pulled_in} m: {swept:?}");
}

#[test]
#[ignore = "needs the retail dat files"]
fn an_engine_frame_offset_moves_the_sweep_with_it() {
    let engine = holtburg();
    let feet = Vec3::new(75.865, 34.712, 94.005);
    let (pivot, wanted) = default_boom(feet, Vec3::Y);
    // The drawing frame centred one landblock west of Holtburg: Holtburg's
    // corner (the engine's origin) lies 192 m east.
    let offset = Vec3::new(192.0, 0.0, 0.0);
    let probe = PhysicsCameraProbe::new(&engine, offset);
    let swept = probe.sweep_eye(pivot + offset, wanted + offset, ROOM, 1, feet + offset);
    assert_eq!(swept.viewer_cell, 0xA9B4_001A);
    assert!(swept.eye.distance(wanted + offset) < 0.01, "{swept:?}");
}

/// The neighbourhood's collision rows sit in the player's frame (the nearby
/// dump reads them): beside the Holtburg lifestone (84, 7.1 in 0xA9B4) the
/// nearest row is 24 m away; the lifestone itself is a server object,
/// not part of the dats' collision, so the dump lists no row within 15 m.
#[test]
#[ignore = "needs the retail dat files"]
fn the_nearest_dat_row_to_the_lifestone_is_24_m_away() {
    let engine = holtburg();
    let rows = engine.shadows.capture_entries();
    let player = Vec3::new(84.0, 7.1, 94.0);
    let mut near: Vec<(f32, u32, Vec3)> = rows
        .iter()
        .map(|r| {
            let d = (r.position.truncate() - player.truncate()).length();
            (d, r.entity_id, r.position)
        })
        .collect();
    near.sort_by(|a, b| a.0.total_cmp(&b.0));
    for (d, id, p) in near.iter().take(5) {
        println!("0x{id:08X} at {p} ({d:.2} m)");
    }
    println!("{} rows", rows.len());
    let (d, id, _) = near[0];
    assert_eq!(id, 0xCA9B_405F);
    assert!((15.0..30.0).contains(&d), "{d}");
}

/// The Town Network dungeon (landblock 0x0007) and its hub's south-west
/// corner cell, a triangle whose long side is the diagonal corner wall.
const TOWN_NETWORK: LandblockCoord = LandblockCoord { x: 0x00, y: 0x07 };
const HUB_CORNER: u32 = 0x0007_0134;

/// The Town Network's interior cells whose containment tree holds `point`.
fn town_network_cells_holding(engine: &PhysicsEngine, point: Vec3) -> Vec<u32> {
    let world = engine.world().unwrap();
    world
        .cell_ids()
        .filter(|id| id >> 16 == 0x0007 && id & 0xFFFF >= 0x0100)
        .filter(|id| {
            let cell = world.cell(*id).unwrap();
            cell.containment_bsp
                .point_inside(cell.placement.to_local(point))
        })
        .collect()
}

/// The spot once noted as "the floor near the camera is missing" (the
/// example at `00070134 58 -82 0.005 30`) is outside the dungeon: beyond the hub's
/// diagonal south-west wall, in no cell. The camera there looks at the
/// room from the void, the wall facing away is culled, and the clear
/// colour under the player is the void, not a missing floor. A step
/// inside the wall the corner cell holds the player.
#[test]
#[ignore = "needs the retail dat files"]
fn the_town_network_corner_spot_is_outside_the_hub() {
    let engine = neighbourhood(TOWN_NETWORK);
    let outside = Vec3::new(58.0, -82.0, 0.005);
    assert!(town_network_cells_holding(&engine, outside).is_empty());
    assert!(!engine.adjust_position(HUB_CORNER, outside).1);

    let inside = Vec3::new(61.5, -78.5, 0.005);
    assert_eq!(town_network_cells_holding(&engine, inside), [HUB_CORNER]);
    assert_eq!(
        engine.adjust_position(HUB_CORNER, inside),
        (HUB_CORNER, true)
    );
}

/// Across the Town Network hub, for every standing spot inside a cell and
/// every heading (15 degrees apart), the swept chase eye ends in the cell
/// the sweep reports: the frame's walk starts where the eye is. The grid
/// is offset from whole meters so no spot lies exactly on a tile seam.
#[test]
#[ignore = "needs the retail dat files"]
fn across_the_town_network_hub_the_swept_eye_is_in_its_viewer_cell() {
    let engine = neighbourhood(TOWN_NETWORK);
    let world = engine.world().unwrap();
    let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
    let (mut sweeps, mut misses) = (0, Vec::new());
    for ix in 0..40 {
        for iy in 0..40 {
            let feet = Vec3::new(40.37 + ix as f32, -99.71 + iy as f32, 0.005);
            let Some(&cell) = town_network_cells_holding(&engine, feet).first() else {
                continue;
            };
            for step in 0..24 {
                let h = (step as f32 * 15.0).to_radians();
                let (pivot, wanted) = default_boom(feet, Vec3::new(h.sin(), h.cos(), 0.0));
                let swept = probe.sweep_eye(pivot, wanted, cell, 0x5000_0001, feet);
                sweeps += 1;
                let holds = world.cell(swept.viewer_cell).is_some_and(|c| {
                    c.containment_bsp
                        .point_inside(c.placement.to_local(swept.eye))
                });
                if !holds {
                    misses.push((feet, step * 15, swept));
                }
            }
        }
    }
    assert!(sweeps > 20_000, "{sweeps} sweeps");
    assert!(
        misses.is_empty(),
        "{} misses: {:?}",
        misses.len(),
        &misses[..misses.len().min(5)]
    );
}

/// Near Wai Jhou (landblock 0x3E33): a player reported standing at
/// (161.35, 123.22) in cell 0x0036, on flat ground with a steep slope
/// rising 6 to 8 m behind them, facing away from it.
const WAI_JHOU: LandblockCoord = LandblockCoord { x: 0x3E, y: 0x33 };
const WAI_JHOU_CELL: u32 = 0x3E33_0036;

/// The reported facing (w, x, y, z = -0.506433, 0, 0, -0.862279).
fn wai_jhou_yaw() -> f32 {
    ac_render::camera::yaw_of(glam::Quat::from_xyzw(0.0, 0.0, -0.862_279, -0.506_433))
}

/// Zoom out with the zoom key held for `zoom_frames` frames at 60 frames a
/// second, then hold still until `frames`; the eye's distance from the
/// pivot over the last 100 frames, and the camera.
fn zoom_out_against_the_slope(
    engine: &PhysicsEngine,
    feet: Vec3,
    zoom_frames: usize,
    frames: usize,
) -> (Vec<f32>, ChaseCamera) {
    use ac_render::camera::ChaseAdjustment;
    let probe = PhysicsCameraProbe::new(engine, Vec3::ZERO);
    let subject = ChaseSubject::standing(feet, wai_jhou_yaw());
    let mut cam = ChaseCamera::default();
    let pivot = feet + Vec3::new(0.0, 0.0, cam.pivot_height);
    let mut distances = Vec::new();
    for frame in 0..frames {
        if frame < zoom_frames {
            let zoom_out = ChaseAdjustment {
                zoom_out: true,
                ..ChaseAdjustment::default()
            };
            cam.adjust_held(zoom_out, 1.0 / 60.0);
        }
        cam.update(
            &subject,
            1.0 / 60.0,
            Some(ChaseCollision {
                probe: &probe,
                cell_id: WAI_JHOU_CELL,
                self_id: 0x5000_0001,
            }),
        );
        if frame + 100 >= frames {
            distances.push(cam.eye().distance(pivot));
        }
    }
    (distances, cam)
}

/// Issue #70: zoomed out with the slope behind, the eye is stopped by the
/// slope where the boom first meets it, on the boom's own line, and stays
/// there. It used to be lifted up out of the ground at each frame's sweep,
/// climbing the slope 13 m from the pivot on a 10 m boom and bouncing
/// 0.27 m back and forth every frame.
#[test]
#[ignore = "needs the retail dat files"]
fn zoomed_out_with_a_slope_behind_the_eye_stops_at_the_slope() {
    let engine = neighbourhood(WAI_JHOU);
    let feet = Vec3::new(161.347_4, 123.218_14, -0.094);
    let (distances, cam) = zoom_out_against_the_slope(&engine, feet, 400, 900);
    let pivot = feet + Vec3::new(0.0, 0.0, cam.pivot_height);

    let (min, max) = distances
        .iter()
        .fold((f32::MAX, f32::MIN), |(lo, hi), d| (lo.min(*d), hi.max(*d)));
    assert!(max - min < 0.001, "the eye moves between {min} and {max} m");
    assert!(
        max < cam.distance - 1.0,
        "the eye is {max} m out on a {} m boom: not stopped by the slope",
        cam.distance
    );

    // On the boom's line: no lift off it.
    let heading = Vec3::new(wai_jhou_yaw().cos(), wai_jhou_yaw().sin(), 0.0);
    let (wanted, _) = desired_pose(pivot, heading, cam.distance, cam.pitch, cam.yaw_offset);
    let along = (wanted - pivot).normalize();
    let to_eye = cam.eye() - pivot;
    let off_line = (to_eye - along * to_eye.dot(along)).length();
    assert!(off_line < 0.01, "the eye is {off_line} m off the boom line");

    // Touching the slope: a little further along the boom is in the ground.
    let probe = PhysicsCameraProbe::new(&engine, Vec3::ZERO);
    let further = cam.eye() + along * 0.05;
    let swept = probe.sweep_eye(pivot, further, WAI_JHOU_CELL, 0x5000_0001, feet);
    assert!(
        swept.eye.distance(cam.eye()) < 0.01,
        "nothing stops the eye: {swept:?}"
    );
    assert_eq!(cam.player_translucency(), 0.0);
}

/// Backed up against the slope, the slope pushes the eye in along the boom
/// toward the back of the player's head; at the foot of the slope the eye
/// is at the pivot and the player is drawn fully see-through.
#[test]
#[ignore = "needs the retail dat files"]
fn backed_against_the_slope_the_eye_comes_in_to_the_player() {
    let engine = neighbourhood(WAI_JHOU);
    let back = -Vec3::new(wai_jhou_yaw().cos(), wai_jhou_yaw().sin(), 0.0);
    let start = Vec3::new(161.347_4, 123.218_14, 0.0);
    let mut last = f32::MAX;
    for meters_back in [3.0_f32, 5.0, 6.0, 7.0, 7.5] {
        let mut feet = start + back * meters_back;
        feet.z = engine.sample_terrain_z(feet.x, feet.y).unwrap();
        let (distances, cam) = zoom_out_against_the_slope(&engine, feet, 300, 600);
        let d = *distances.last().unwrap();
        let spread = distances.iter().fold(0.0_f32, |s, x| s.max((x - d).abs()));
        assert!(
            spread < 0.001,
            "{meters_back} m back: the eye moves {spread} m"
        );
        assert!(
            d < last,
            "{meters_back} m back: the eye is {d} m out, was {last}"
        );
        last = d;
        if meters_back == 7.5 {
            assert!(d < 0.2, "at the foot of the slope the eye is {d} m out");
            assert_eq!(cam.player_translucency(), 1.0);
        }
    }
}
