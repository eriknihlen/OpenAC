//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/LastKnownContactPlaneTests.cs`
//! and `EdgeSlideBackProbePrecipiceSlideTests.cs`.

use glam::Vec3;

use crate::geometry::Plane;
use crate::state::TransitionState;
use crate::transition::{CollisionInfo, Transition};

const CELL: u32 = 0x0001_0001;

fn walkable() -> Plane {
    Plane::new(Vec3::Z, 0.0)
}

fn steep() -> Plane {
    Plane::new(Vec3::new(-0.954, 0.0, 0.301).normalize(), 0.0)
}

#[test]
fn mid_transition_steep_contact_leaves_the_walkable_last_known_plane_intact() {
    let mut ci = CollisionInfo::default();
    ci.init_contact_plane(walkable(), CELL, false);
    ci.set_contact_plane(steep(), CELL, false);
    assert!(ci.contact_plane_valid);
    assert_eq!(ci.contact_plane.normal, steep().normal);
    assert!(ci.last_known_contact_plane_valid);
    assert_eq!(ci.last_known_contact_plane.normal, walkable().normal);
    let slide = steep().normal.cross(ci.last_known_contact_plane.normal);
    assert!(slide.length() > 1e-4);
}

#[test]
fn init_contact_plane_seeds_both_groups() {
    let mut ci = CollisionInfo::default();
    assert!(!ci.last_known_contact_plane_valid);
    ci.init_contact_plane(walkable(), CELL, true);
    assert!(ci.contact_plane_valid);
    assert_eq!(ci.contact_plane.normal, walkable().normal);
    assert_eq!(ci.contact_plane_cell_id, CELL);
    assert!(ci.contact_plane_is_water);
    assert!(ci.last_known_contact_plane_valid);
    assert_eq!(ci.last_known_contact_plane.normal, walkable().normal);
    assert_eq!(ci.last_known_contact_plane_cell_id, CELL);
    assert!(ci.last_known_contact_plane_is_water);
}

#[test]
fn repeated_steep_contact_still_leaves_last_known_intact() {
    let mut ci = CollisionInfo::default();
    ci.init_contact_plane(walkable(), CELL, false);
    ci.set_contact_plane(steep(), CELL, false);
    ci.set_contact_plane(steep(), CELL, false);
    ci.set_contact_plane(steep(), CELL, false);
    assert_eq!(ci.last_known_contact_plane.normal, walkable().normal);
}

fn precipice_setup(check_x: f32) -> Transition {
    let mut t = Transition::new();
    let sp = &mut t.sphere_path;
    sp.global_curr_center[0].center = Vec3::new(0.0, 0.0, 1.0);
    sp.global_curr_center[0].radius = 0.5;
    sp.set_walkable(
        Plane::new(Vec3::Z, -1.0),
        &[
            Vec3::new(-1.0, -1.0, 1.0),
            Vec3::new(1.0, -1.0, 1.0),
            Vec3::new(1.0, 1.0, 1.0),
            Vec3::new(-1.0, 1.0, 1.0),
        ],
        Vec3::Z,
    );
    sp.check_pos = Vec3::new(check_x, 0.0, 1.0);
    sp.global_sphere[0].center = sp.check_pos;
    sp.global_sphere[0].radius = 0.5;
    t
}

#[test]
fn precipice_slide_back_probe_state_crosses_edge_does_not_wedge() {
    let mut t = precipice_setup(1.2);
    let result = t.precipice_slide();
    assert_ne!(result, TransitionState::Collided);
    assert!(matches!(
        result,
        TransitionState::Slid | TransitionState::Adjusted | TransitionState::Ok
    ));
    assert!(!t.sphere_path.walkable_valid);
}

#[test]
fn precipice_slide_no_edge_crossed_returns_collided_matching_retail() {
    let mut t = precipice_setup(0.1);
    assert_eq!(t.precipice_slide(), TransitionState::Collided);
}
