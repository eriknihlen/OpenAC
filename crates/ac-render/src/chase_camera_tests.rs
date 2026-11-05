//! OpenAC `tests/AcDream.App.Tests/Rendering/RetailChaseCameraTests.cs`:
//! the heading (slope alignment, tracked target), the poses of the views,
//! the velocity ring, damping, the mouse filter, the fade and the update.
//! OpenAC's global `CameraDiagnostics` switches are the camera's own
//! settings here (`ChaseTuning`: slope alignment, stiffness); the collision
//! cases are in `camera::tests` and `tests/retail_camera.rs`.

use std::f32::consts::{FRAC_PI_2, FRAC_PI_3, FRAC_PI_4, PI};

use glam::{Vec2, Vec3};

use super::*;

const DT: f32 = 1.0 / 60.0;

/// The default settings with slope alignment off.
fn flat_tuning() -> ChaseTuning {
    ChaseTuning {
        align_to_slope: false,
        ..ChaseTuning::default()
    }
}

fn near(a: f32, b: f32, places: i32) -> bool {
    (a - b).abs() <= 0.5 * 10f32.powi(-places)
}

fn assert_vec(expected: Vec3, actual: Vec3, places: i32) {
    assert!(
        near(expected.x, actual.x, places)
            && near(expected.y, actual.y, places)
            && near(expected.z, actual.z, places),
        "expected {expected}, got {actual}"
    );
}

fn subject(
    position: Vec3,
    yaw: f32,
    velocity: Vec3,
    in_contact: bool,
    normal: Vec3,
) -> ChaseSubject {
    ChaseSubject {
        position,
        yaw,
        velocity,
        in_contact,
        contact_normal: normal,
        tracked_target: None,
    }
}

/// The airborne plane's normal: the up axis of the frame headed along the
/// velocity with its vertical part scaled by 0.1.
fn airborne_normal(velocity: Vec3) -> Vec3 {
    let n = velocity.normalize();
    let t = Vec3::new(n.x, n.y, 0.1 * n.z);
    let forward = t.normalize();
    let right = Vec3::new(forward.y, -forward.x, 0.0).normalize();
    right.cross(forward).normalize()
}

fn project(base: Vec3, normal: Vec3) -> Vec3 {
    (base - normal * base.dot(normal)).normalize()
}

fn flat(yaw: f32) -> Vec3 {
    Vec3::new(yaw.cos(), yaw.sin(), 0.0)
}

const SLOPE_VELOCITY: Vec3 = Vec3::new(0.5, 3.0, 1.5);
const SLOPE_NORMAL: Vec3 = Vec3::new(0.0, -0.5, 0.866);

// ── teleport ───────────────────────────────────────────────────────────

#[test]
fn reset_viewer_to_player_reseeds_the_viewer_and_reextends_from_the_player() {
    let mut cam = ChaseCamera::default();
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    let destination = Vec3::new(200.0, -300.0, 12.0);
    let yaw = FRAC_PI_2;
    cam.reset_viewer_to_player(destination, yaw);
    assert_eq!(cam.eye(), destination);
    assert_eq!(cam.viewer_cell(), None);
    cam.update(&ChaseSubject::standing(destination, yaw), DT, None);
    let first_step = cam.eye().distance(destination);
    assert!((0.001..=0.3).contains(&first_step), "{first_step}");
}

#[test]
fn a_prefilled_ring_does_not_pitch_a_stationary_camera_after_a_teleport() {
    let mut cam = ChaseCamera::default();
    for _ in 0..5 {
        cam.update(
            &subject(Vec3::ZERO, 0.0, Vec3::new(5.0, 5.0, 0.0), true, Vec3::Z),
            DT,
            None,
        );
    }
    let destination = Vec3::new(200.0, -300.0, 12.0);
    cam.reset_viewer_to_player(destination, 0.0);
    let sloped = Vec3::new(-0.5, 0.0, 0.866).normalize();
    cam.update(
        &subject(destination, 0.0, Vec3::ZERO, true, sloped),
        DT,
        None,
    );
    let pivot = destination + Vec3::new(0.0, 0.0, cam.pivot_height);
    let (expected_eye, _) = desired_pose(pivot, flat(0.0), cam.distance, cam.pitch, 0.0);
    let t = damping_alpha(CHASE_STIFFNESS, DT);
    assert_vec(destination.lerp(expected_eye, t), cam.eye(), 4);
}

// ── heading ────────────────────────────────────────────────────────────

#[test]
fn a_stationary_body_heads_along_its_facing() {
    let h = compute_heading(Vec3::ZERO, FRAC_PI_4, true, Vec3::Z, true);
    assert_vec(flat(FRAC_PI_4), h, 5);
}

#[test]
fn moving_on_flat_ground_heads_horizontally() {
    let h = compute_heading(Vec3::new(3.0, 3.0, 0.0), 0.0, true, Vec3::Z, true);
    assert_vec(Vec3::X, h, 5);
}

#[test]
fn an_uphill_slope_tilts_the_heading() {
    let h = compute_heading(SLOPE_VELOCITY, FRAC_PI_2, true, SLOPE_NORMAL, true);
    assert!(h.z > 0.4, "{h}");
    assert!(near(h.length(), 1.0, 4));
}

#[test]
fn jumping_straight_up_stays_horizontal() {
    let h = compute_heading(Vec3::new(0.0, 0.0, 5.0), 0.0, false, Vec3::ZERO, true);
    assert_vec(Vec3::X, h, 5);
}

#[test]
fn an_airborne_diagonal_jump_tilts_to_the_velocity_plane() {
    let velocity = Vec3::new(3.0, 4.0, 2.0);
    let expected = project(Vec3::X, airborne_normal(velocity));
    let h = compute_heading(velocity, 0.0, false, Vec3::ZERO, true);
    assert_vec(expected, h, 4);
    assert_ne!(h, Vec3::X);
}

#[test]
fn with_alignment_off_velocity_and_plane_are_ignored() {
    let h = compute_heading(Vec3::new(0.0, 0.0, 1.0), 0.0, true, SLOPE_NORMAL, false);
    assert_vec(Vec3::X, h, 5);
}

#[test]
fn a_velocity_exactly_on_a_world_axis_does_not_tilt() {
    let base = flat(FRAC_PI_2);
    assert_eq!(
        compute_heading(
            Vec3::new(5.0, 0.0, 0.0),
            FRAC_PI_2,
            true,
            SLOPE_NORMAL,
            true
        ),
        base
    );
    assert_eq!(
        compute_heading(
            Vec3::new(0.0, 5.0, 0.0),
            FRAC_PI_2,
            true,
            SLOPE_NORMAL,
            true
        ),
        base
    );
}

#[test]
fn a_small_diagonal_velocity_tilts_but_tiny_noise_never_does() {
    let small = compute_heading(
        Vec3::new(0.005, 0.005, 0.0),
        FRAC_PI_4,
        true,
        SLOPE_NORMAL,
        true,
    );
    assert_ne!(small, flat(FRAC_PI_4));
    let noise = compute_heading(Vec3::splat(1e-5), 0.0, true, SLOPE_NORMAL, true);
    assert_eq!(noise, Vec3::X);
}

#[test]
fn the_airborne_normal_for_a_diagonal_velocity_is_the_heading_frame_up() {
    let velocity = Vec3::new(3.0, 4.0, -6.0);
    let expected = project(flat(FRAC_PI_4), airborne_normal(velocity));
    let h = compute_heading(velocity, FRAC_PI_4, false, Vec3::ZERO, true);
    assert_vec(expected, h, 4);
}

#[test]
fn contact_uses_the_contact_plane_and_the_air_the_velocity_plane() {
    let velocity = Vec3::new(3.0, 4.0, -6.0);
    let contact = Vec3::new(-0.5, 0.0, 0.866).normalize();
    let on = compute_heading(velocity, FRAC_PI_4, true, contact, true);
    let air = compute_heading(velocity, FRAC_PI_4, false, contact, true);
    assert_vec(project(flat(FRAC_PI_4), contact), on, 4);
    assert_vec(project(flat(FRAC_PI_4), airborne_normal(velocity)), air, 4);
    assert!((on.z - air.z).abs() > 0.05);
}

#[test]
fn a_zero_contact_normal_stays_flat() {
    let h = compute_heading(Vec3::new(3.0, 4.0, -6.0), FRAC_PI_4, true, Vec3::ZERO, true);
    assert_vec(flat(FRAC_PI_4), h, 5);
}

#[test]
fn a_near_parallel_normal_above_the_floor_tilts() {
    let normal = Vec3::new(1.0, 0.0, 0.005).normalize();
    let projected = Vec3::X - normal * Vec3::X.dot(normal);
    assert!((2e-4..=0.01).contains(&projected.length()));
    let h = compute_heading(Vec3::new(5.0, 5.0, 0.0), 0.0, true, normal, true);
    assert_vec(projected.normalize(), h, 4);
}

#[test]
fn the_tracked_heading_points_from_the_pivot_to_the_target() {
    let pivot = Vec3::new(0.0, 0.0, 1.5);
    let h = compute_tracked_heading(pivot, Some(Vec3::new(10.0, 0.0, 0.5))).unwrap();
    assert_vec(Vec3::new(10.0, 0.0, -1.0).normalize(), h, 5);
    let p = Vec3::new(1.0, 2.0, 3.0);
    assert_eq!(compute_tracked_heading(p, None), None);
    assert_eq!(compute_tracked_heading(p, Some(p)), None);
}

// ── poses ──────────────────────────────────────────────────────────────

#[test]
fn the_boom_sits_behind_the_heading_and_its_swing_keeps_looking_at_the_pivot() {
    let pivot = Vec3::new(10.0, 20.0, 1.5);
    let (eye, forward) = desired_pose(pivot, Vec3::X, 5.0, 0.0, 0.0);
    assert_eq!(eye, Vec3::new(5.0, 20.0, 1.5));
    assert_eq!(forward, Vec3::X);
    let (eye, forward) = desired_pose(pivot, Vec3::X, 5.0, 0.0, FRAC_PI_2);
    assert_vec(Vec3::new(10.0, 15.0, 1.5), eye, 5);
    assert_eq!(forward, (pivot - eye).normalize());
}

#[test]
fn the_map_view_looks_steeply_down_from_high_overhead() {
    let pivot = Vec3::new(10.0, 20.0, 1.5);
    let distance = 450f32.hypot(0.75);
    let pitch = 0.75f32.atan2(450.0);
    let (eye, forward) =
        compute_target_direction_pose(pivot, Vec3::X, distance, pitch, Vec3::new(0.0, 0.5, -1.8));
    assert!(eye.z > 430.0, "{eye}");
    let flat_distance = Vec2::new(eye.x, eye.y).distance(Vec2::new(pivot.x, pivot.y));
    assert!((119.0..=122.0).contains(&flat_distance), "{flat_distance}");
    assert!(forward.z < -0.95, "{forward}");
}

// ── ring, damping, filter, fade ────────────────────────────────────────

#[test]
fn the_ring_averages_its_newest_samples() {
    let mut ring = [Vec3::ZERO; 5];
    let mut count = 0;
    for x in [1.0, 1.0, 2.0, 2.0, 3.0] {
        push_velocity(&mut ring, &mut count, Vec3::new(x, 0.0, 0.0));
    }
    assert_eq!(count, 5);
    assert!(near(average_velocity(&ring, count).x, 1.8, 5));
    push_velocity(&mut ring, &mut count, Vec3::new(10.0, 0.0, 0.0));
    assert!(near(average_velocity(&ring, count).x, 3.6, 5));

    let mut ring = [Vec3::ZERO; 5];
    let mut count = 0;
    for _ in 0..5 {
        push_velocity(&mut ring, &mut count, Vec3::X);
    }
    push_velocity(&mut ring, &mut count, Vec3::new(10.0, 0.0, 0.0));
    assert!(near(average_velocity(&ring, count).x, 2.8, 5));

    let mut ring = [Vec3::ZERO; 5];
    let mut count = 0;
    push_velocity(&mut ring, &mut count, Vec3::new(2.0, 0.0, 0.0));
    push_velocity(&mut ring, &mut count, Vec3::new(4.0, 0.0, 0.0));
    assert_eq!(count, 2);
    assert!(near(average_velocity(&ring, count).x, 3.0, 5));
}

#[test]
fn damping_alpha_is_seven_and_a_half_percent_at_the_default_and_clamps() {
    assert!(near(damping_alpha(0.45, DT), 0.075, 4));
    assert_eq!(damping_alpha(0.45, 1.0), 1.0);
    assert_eq!(damping_alpha(0.45, 0.0), 0.0);
    assert_eq!(damping_alpha(0.0, 1.0), 0.0);
}

#[test]
fn the_mouse_filter_averages_within_its_window() {
    let filter = |raw, weight, now, last: f32| {
        let (mut d, mut t) = (last, 0.0);
        let out = filter_mouse_axis(raw, weight, now, &mut d, &mut t, 0.25);
        (out, d, t)
    };
    assert!(
        near(filter(10.0, 0.5, 1.0, 5.0).0, 10.0, 5),
        "beyond the window: raw"
    );
    assert!(near(filter(20.0, 0.5, 0.1, 10.0).0, 17.5, 5));
    assert!(near(filter(20.0, 0.0, 0.1, 10.0).0, 20.0, 5));
    assert!(near(filter(20.0, 1.0, 0.1, 10.0).0, 15.0, 5));
    let (out, last, time) = filter(20.0, 0.5, 0.1, 10.0);
    assert_eq!(out, last);
    assert!(near(time, 0.1, 5));
}

#[test]
fn the_player_fades_between_45_and_20_centimetres() {
    assert_eq!(compute_translucency(0.45), 0.0);
    assert_eq!(compute_translucency(1.0), 0.0);
    assert!(near(compute_translucency(0.325), 0.5, 4));
    assert_eq!(compute_translucency(0.20), 1.0);
    assert_eq!(compute_translucency(0.10), 1.0);
    assert_eq!(compute_translucency(0.0), 1.0);
}

// ── update ─────────────────────────────────────────────────────────────

#[test]
fn the_ring_freezes_while_alignment_is_off_then_resumes_with_its_old_samples() {
    let mut cam = ChaseCamera {
        pitch: 0.0,
        tuning: ChaseTuning {
            stiffness: 100.0,
            ..ChaseTuning::default()
        },
        ..ChaseCamera::default()
    };
    let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
    let old = Vec3::new(3.0, 4.0, -6.0);
    let resume = Vec3::new(-4.0, 3.0, 5.0);
    for _ in 0..5 {
        cam.update(&subject(Vec3::ZERO, 0.0, old, false, Vec3::ZERO), DT, None);
    }
    cam.tuning.align_to_slope = false;
    for _ in 0..5 {
        cam.update(
            &subject(Vec3::ZERO, 0.0, Vec3::splat(1000.0), false, Vec3::ZERO),
            DT,
            None,
        );
    }
    cam.tuning.align_to_slope = true;
    cam.update(
        &subject(Vec3::ZERO, 0.0, resume, false, Vec3::ZERO),
        DT,
        None,
    );
    let forward = (pivot - cam.eye()).normalize();
    let mut ring = [Vec3::ZERO; 5];
    let mut count = 0;
    for _ in 0..5 {
        push_velocity(&mut ring, &mut count, old);
    }
    push_velocity(&mut ring, &mut count, resume);
    let expected = project(Vec3::X, airborne_normal(average_velocity(&ring, count)));
    assert_vec(expected, forward, 3);
}

#[test]
fn the_first_update_snaps_and_the_second_eases() {
    let mut cam = ChaseCamera {
        distance: 5.0,
        pitch: 0.0,
        tuning: flat_tuning(),
        ..ChaseCamera::default()
    };
    cam.update(
        &ChaseSubject::standing(Vec3::new(10.0, 20.0, 30.0), 0.0),
        DT,
        None,
    );
    assert_vec(Vec3::new(5.0, 20.0, 31.5), cam.eye(), 4);

    let mut cam = ChaseCamera {
        distance: 5.0,
        pitch: 0.0,
        tuning: flat_tuning(),
        ..ChaseCamera::default()
    };
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    cam.update(
        &ChaseSubject::standing(Vec3::new(10.0, 0.0, 0.0), 0.0),
        DT,
        None,
    );
    assert!(near(cam.eye().x, -4.25, 3));
    assert!(near(cam.eye().y, 0.0, 4) && near(cam.eye().z, 1.5, 4));
}

#[test]
fn in_first_person_the_eye_follows_exactly_and_only_the_look_eases() {
    let mut cam = ChaseCamera {
        tuning: ChaseTuning {
            stiffness: 0.02,
            align_to_slope: false,
            ..ChaseTuning::default()
        },
        ..ChaseCamera::default()
    };
    cam.set_first_person_view();
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    cam.update(
        &ChaseSubject::standing(Vec3::new(5.0, 0.0, 0.0), 0.0),
        DT,
        None,
    );
    assert_vec(
        Vec3::new(5.0 + FIRST_PERSON_FORWARD, 0.0, 1.5),
        cam.eye(),
        4,
    );
}

#[test]
fn the_fade_follows_the_eye_and_first_person_is_fully_faded() {
    let mut cam = ChaseCamera {
        distance: 5.0,
        pitch: 0.0,
        tuning: flat_tuning(),
        ..ChaseCamera::default()
    };
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    assert_eq!(cam.player_translucency(), 0.0);

    let mut cam = ChaseCamera::default();
    cam.set_first_person_view();
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    assert!(cam.is_in_head());
    assert_eq!(cam.eye(), Vec3::new(FIRST_PERSON_FORWARD, 0.0, 1.5));
    assert_eq!(cam.player_translucency(), 1.0);
    let (_, forward) = compute_in_head_pose(Vec3::new(0.0, 0.0, 1.5), Vec3::X, None);
    assert_eq!(forward, Vec3::X);
}

// ── adjustments ────────────────────────────────────────────────────────

#[test]
fn zoom_scales_by_a_fifth_and_is_refused_at_the_limits() {
    let mut cam = ChaseCamera {
        distance: 5.0,
        ..ChaseCamera::default()
    };
    cam.adjust_distance(1.0);
    assert!(near(cam.distance, 6.0, 5));
    cam.adjust_distance(-1.0);
    assert!(near(cam.distance, 4.8, 5));

    let mut far = ChaseCamera {
        distance: 9.9,
        pitch: 0.0,
        ..ChaseCamera::default()
    };
    far.adjust_distance(1.0);
    assert!(near(far.distance, 9.9, 5));
    let mut close = ChaseCamera {
        distance: 0.55,
        pitch: 0.0,
        ..ChaseCamera::default()
    };
    close.adjust_distance(-1.0);
    assert!(near(close.distance, 0.55, 5));

    let mut diagonal = ChaseCamera {
        distance: 12.0,
        pitch: 0.0,
        yaw_offset: FRAC_PI_4,
        ..ChaseCamera::default()
    };
    diagonal.adjust_distance(0.5);
    assert!(near(diagonal.distance, 13.2, 5));
}

#[test]
fn zooming_out_leaves_first_person_and_zooming_in_stays() {
    let mut cam = ChaseCamera::default();
    cam.set_first_person_view();
    cam.adjust_distance(1.0);
    assert!(!cam.is_in_head());
    assert!(near(cam.distance, 0.6f32.hypot(0.5), 5));

    let mut cam = ChaseCamera::default();
    cam.set_first_person_view();
    cam.adjust_distance(-1.0);
    assert!(cam.is_in_head());
    assert!(near(cam.distance, FIRST_PERSON_FORWARD, 5));
}

#[test]
fn pitch_in_first_person_raises_the_look_without_moving_the_eye() {
    let mut cam = ChaseCamera::default();
    cam.set_first_person_view();
    cam.adjust_pitch(1.0);
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), DT, None);
    assert!(cam.is_in_head());
    assert_eq!(cam.eye(), Vec3::new(FIRST_PERSON_FORWARD, 0.0, 1.5));
    assert!(cam.forward().z > 0.0);
    for _ in 0..10 {
        cam.adjust_pitch(1.0);
    }
    cam.update(&ChaseSubject::standing(Vec3::ZERO, 0.0), 1.0, None);
    assert!(near(cam.forward().normalize().z, 0.8 / 1.64f32.sqrt(), 5));
}

#[test]
fn pitch_and_yaw_turn_eight_degrees_per_unit() {
    let mut cam = ChaseCamera {
        distance: 5.0,
        pitch: 0.0,
        ..ChaseCamera::default()
    };
    cam.adjust_pitch(1.0);
    assert!(near(cam.pitch, OFFSET_ANGLE_RADIANS, 6));
    assert!(near(cam.distance, 5.0, 6));
    let mut low = ChaseCamera {
        distance: 3.0,
        pitch: -0.55,
        ..ChaseCamera::default()
    };
    low.adjust_pitch(-1.0);
    assert_eq!(low.pitch, -0.55);
    assert_eq!(low.distance, 3.0);
    let mut cam = ChaseCamera::default();
    cam.adjust_yaw(1.0);
    assert!(near(cam.yaw_offset, OFFSET_ANGLE_RADIANS, 6));
}

#[test]
fn a_rotate_in_the_head_view_turns_the_character_and_stays_first_person() {
    // RetailChaseCamera: a rotate left is +8 compass degrees, a
    // rotate right -8; the view stays in the head and the boom does not
    // swing; nothing is taken twice.
    for (adjustment, step) in [(-1.0, 8.0), (1.0, -8.0)] {
        let mut cam = ChaseCamera::default();
        cam.set_first_person_view();
        cam.adjust_yaw(adjustment);
        cam.adjust_yaw(adjustment);
        assert!(cam.is_in_head());
        assert_eq!(cam.yaw_offset, 0.0);
        assert_eq!(cam.take_first_person_turn(), Some(step));
        assert_eq!(cam.take_first_person_turn(), None);
    }
    // Out of the head a rotate swings the boom and turns nothing.
    let mut cam = ChaseCamera::default();
    cam.adjust_yaw(1.0);
    assert_eq!(cam.take_first_person_turn(), None);
}

// ── which views tilt ───────────────────────────────────────────────────

fn on_slope(cam: &mut ChaseCamera, tracked: Option<Vec3>) {
    cam.update(
        &ChaseSubject {
            tracked_target: tracked,
            ..subject(Vec3::ZERO, FRAC_PI_2, SLOPE_VELOCITY, true, SLOPE_NORMAL)
        },
        DT,
        None,
    );
}

#[test]
fn look_down_and_map_views_do_not_tilt() {
    for map in [false, true] {
        let mut cam = ChaseCamera::default();
        if map {
            cam.toggle_map_mode_view();
        } else {
            cam.toggle_look_down_view();
        }
        on_slope(&mut cam, None);
        let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
        let (expected, _) = compute_target_direction_pose(
            pivot,
            flat(FRAC_PI_2),
            cam.distance,
            cam.pitch,
            Vec3::new(0.0, 0.5, -1.8),
        );
        assert_vec(expected, cam.eye(), 4);
    }
}

#[test]
fn an_orbit_past_the_threshold_or_ninety_degrees_does_not_tilt() {
    let base = ChaseCamera::default();
    let threshold = (1.0 / (base.distance * base.pitch.cos())).asin();
    for yaw_offset in [threshold + 0.02, 170.0 * PI / 180.0] {
        let mut cam = ChaseCamera {
            yaw_offset,
            ..ChaseCamera::default()
        };
        on_slope(&mut cam, None);
        let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
        let (expected, _) = desired_pose(
            pivot,
            flat(FRAC_PI_2),
            cam.distance,
            cam.pitch,
            cam.yaw_offset,
        );
        assert_vec(expected, cam.eye(), 4);
    }
}

#[test]
fn an_orbit_within_the_threshold_tilts_in_the_tilted_frame() {
    let base = ChaseCamera::default();
    let threshold = (1.0 / (base.distance * base.pitch.cos())).asin();
    for yaw_offset in [0.2, threshold - 0.02] {
        let mut cam = ChaseCamera {
            yaw_offset,
            ..ChaseCamera::default()
        };
        on_slope(&mut cam, None);
        let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
        let tilted = compute_heading(SLOPE_VELOCITY, FRAC_PI_2, true, SLOPE_NORMAL, true);
        let (expected, _) = desired_pose(pivot, tilted, cam.distance, cam.pitch, cam.yaw_offset);
        assert_vec(expected, cam.eye(), 4);
    }
}

#[test]
fn first_person_tilts_and_a_tracked_target_does_not() {
    let mut cam = ChaseCamera::default();
    cam.set_first_person_view();
    on_slope(&mut cam, None);
    let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
    let tilted = compute_heading(SLOPE_VELOCITY, FRAC_PI_2, true, SLOPE_NORMAL, true);
    let (expected, _) = compute_in_head_pose(pivot, tilted, None);
    assert_vec(expected, cam.eye(), 4);

    let mut cam = ChaseCamera::default();
    let target = Vec3::new(10.0, 0.0, cam.pivot_height);
    on_slope(&mut cam, Some(target));
    let tracked = compute_tracked_heading(pivot, Some(target)).unwrap();
    let (expected, _) = desired_pose(pivot, tracked, cam.distance, cam.pitch, cam.yaw_offset);
    assert_vec(expected, cam.eye(), 4);
}

#[test]
fn the_default_pose_matches_the_flat_orbit_formula() {
    for (align, normal) in [(false, SLOPE_NORMAL), (true, Vec3::Z)] {
        for yaw_offset in [0.0, 0.6] {
            let mut cam = ChaseCamera {
                yaw_offset,
                tuning: ChaseTuning {
                    align_to_slope: align,
                    ..ChaseTuning::default()
                },
                ..ChaseCamera::default()
            };
            let velocity = Vec3::new(3.0, 4.0, -2.0);
            cam.update(
                &subject(Vec3::ZERO, FRAC_PI_3, velocity, true, normal),
                DT,
                None,
            );
            let pivot = Vec3::new(0.0, 0.0, cam.pivot_height);
            let old = compute_heading(velocity, FRAC_PI_3 + yaw_offset, true, normal, align);
            let (expected, _) = desired_pose(pivot, old, cam.distance, cam.pitch, 0.0);
            assert_vec(expected, cam.eye(), 4);
        }
    }
}

#[test]
fn the_views_toggle_back_to_the_view_before_them() {
    let mut cam = ChaseCamera::default();
    let before = (cam.distance, cam.pitch);
    cam.toggle_look_down_view();
    assert!(cam.is_looking_down() && !cam.is_map_mode());
    cam.toggle_map_mode_view();
    assert!(cam.is_map_mode());
    cam.toggle_map_mode_view();
    assert!(!cam.is_looking_down() && !cam.is_map_mode());
    assert_eq!((cam.distance, cam.pitch), before);
    cam.toggle_look_down_view();
    cam.adjust_yaw(1.0);
    assert!(
        !cam.is_looking_down(),
        "an adjustment leaves the look-down view"
    );
    cam.set_default_view();
    assert!(near(cam.distance, 2.75f32.hypot(0.825), 5));
    assert_eq!(cam.yaw_offset, 0.0);
}
