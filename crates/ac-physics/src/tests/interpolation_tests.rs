//! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/InterpolationManagerTests.cs`
//! and `InterpolationRecoveryTests.cs`.

use ac_motion::{MotionDeltaFrame, OffsetAdjuster, get_heading, set_heading};
use glam::{Quat, Vec3};

use super::support::*;
use crate::interpolation::InterpolationManager as M;

const BLIP: f32 = M::AUTONOMY_BLIP_DISTANCE;

fn far_target() -> Vec3 {
    Vec3::new(10.0, 0.0, 0.0)
}

fn enqueue_h(m: &mut M, target: Vec3, heading: f32, moving_to: bool, body: Option<Vec3>) {
    m.enqueue_heading(target, heading, moving_to, body, 0, BLIP);
}

fn adjust(m: &mut M, dt: f64, body: Vec3, max_speed: f32) -> Vec3 {
    m.adjust_offset_world(dt, body, max_speed, true, false)
}

fn quat_alignment(a: Quat, b: Quat) -> f32 {
    a.normalize().dot(b.normalize()).abs()
}

// ── InterpolationManagerTests ──────────────────────────────────────────

#[test]
fn adjust_offset_complete_frame_replaces_origin_and_non_commuting_orientation() {
    let mut m = M::new();
    let current = Quat::from_axis_angle(Vec3::X, 1.1);
    let target = Quat::from_axis_angle(Vec3::Y, -0.9);
    m.enqueue(
        Vec3::new(10.0, 0.0, 0.0),
        target,
        false,
        Some(Vec3::ZERO),
        None,
        0,
        BLIP,
    );
    let mut offset = MotionDeltaFrame {
        origin: Vec3::new(7.0, 8.0, 9.0),
        orientation: Quat::from_axis_angle(Vec3::Z, 0.4),
    };
    let overwritten = m.adjust_offset(0.1, Vec3::ZERO, current, 4.0, &mut offset, true, false);
    assert!(overwritten);
    let expected_local = current.conjugate() * Vec3::new(0.8, 0.0, 0.0);
    assert_eq_prec(expected_local.x, offset.origin.x, 5);
    assert_eq_prec(expected_local.y, offset.origin.y, 5);
    assert_eq_prec(expected_local.z, offset.origin.z, 5);
    let expected_orientation = (current.inverse() * target).normalize();
    let alignment = quat_alignment(expected_orientation, offset.orientation);
    assert!((0.99999..=1.00001).contains(&alignment), "{alignment}");
}

#[test]
fn adjust_offset_move_to_node_keeps_current_heading() {
    let mut m = M::new();
    m.enqueue(
        Vec3::new(10.0, 0.0, 0.0),
        Quat::from_axis_angle(Vec3::Y, -0.9),
        true,
        Some(Vec3::ZERO),
        None,
        0,
        BLIP,
    );
    let mut offset = MotionDeltaFrame {
        origin: Vec3::ZERO,
        orientation: Quat::from_axis_angle(Vec3::X, 0.6),
    };
    assert!(m.adjust_offset(
        0.1,
        Vec3::ZERO,
        Quat::from_axis_angle(Vec3::Z, 0.2),
        4.0,
        &mut offset,
        true,
        false
    ));
    assert_eq!(offset.orientation, Quat::IDENTITY);
}

#[test]
fn adjust_offset_uses_manager_wide_latest_keep_heading_flag() {
    let mut m = M::new();
    let current = Quat::from_axis_angle(Vec3::Z, 0.2);
    m.enqueue(
        Vec3::new(10.0, 0.0, 0.0),
        Quat::from_axis_angle(Vec3::Y, 0.7),
        false,
        Some(Vec3::ZERO),
        Some(current),
        0,
        BLIP,
    );
    m.enqueue(
        Vec3::new(20.0, 0.0, 0.0),
        Quat::from_axis_angle(Vec3::X, -0.6),
        true,
        Some(Vec3::ZERO),
        Some(current),
        0,
        BLIP,
    );
    let mut offset = MotionDeltaFrame::default();
    assert!(m.adjust_offset(0.1, Vec3::ZERO, current, 4.0, &mut offset, true, false));
    assert_eq!(offset.orientation, Quat::IDENTITY);
}

#[test]
fn adjust_offset_without_contact_leaves_frame_and_queue_untouched() {
    let mut m = M::new();
    m.enqueue(
        far_target(),
        Quat::from_axis_angle(Vec3::Y, 0.7),
        false,
        Some(Vec3::ZERO),
        None,
        0,
        BLIP,
    );
    let mut offset = MotionDeltaFrame {
        origin: Vec3::new(1.0, 2.0, 3.0),
        orientation: Quat::from_axis_angle(Vec3::X, 0.4),
    };
    let before = offset;
    let overwritten = m.adjust_offset(
        0.1,
        Vec3::ZERO,
        Quat::IDENTITY,
        4.0,
        &mut offset,
        false,
        false,
    );
    assert!(!overwritten);
    assert_eq!(offset, before);
    assert!(m.is_active());
}

#[test]
fn enqueue_already_close_installs_only_target_heading() {
    let mut m = M::new();
    let target =
        (Quat::from_axis_angle(Vec3::X, 0.8) * Quat::from_axis_angle(Vec3::Z, -0.6)).normalize();
    let immediate = m.enqueue(
        Vec3::new(0.01, 0.0, 0.0),
        target,
        false,
        Some(Vec3::ZERO),
        None,
        0,
        BLIP,
    );
    let immediate = immediate.expect("an immediate heading");
    let expected = set_heading(get_heading(target));
    let alignment = quat_alignment(expected, immediate);
    assert!((0.99999..=1.00001).contains(&alignment));
    assert!(quat_alignment(target, immediate) < 0.999);
    assert!(!m.is_active());
}

#[test]
fn enqueue_adds_node_queue_becomes_active() {
    let mut m = M::new();
    assert!(!m.is_active());
    enqueue_h(&mut m, far_target(), 0.0, true, None);
    assert!(m.is_active());
}

#[test]
fn enqueue_drops_oldest_when_at_cap_20() {
    let mut m = M::new();
    for i in 0..M::QUEUE_CAP {
        enqueue_h(&mut m, Vec3::new(i as f32, 0.0, 0.0), 0.0, true, None);
    }
    assert_eq!(m.count(), M::QUEUE_CAP);
    enqueue_h(&mut m, Vec3::new(100.0, 0.0, 0.0), 0.0, true, None);
    assert_eq!(m.count(), M::QUEUE_CAP);
    let result = adjust(&mut m, 0.016, Vec3::new(1.0, 0.0, 0.0), 10.0);
    assert_eq!(result, Vec3::ZERO);
    assert_eq!(m.count(), M::QUEUE_CAP - 1);
}

#[test]
fn enqueue_at_cap_20_head_is_second_original() {
    let mut m = M::new();
    for i in 0..M::QUEUE_CAP {
        enqueue_h(&mut m, Vec3::new(i as f32, 0.0, 0.0), 0.0, true, None);
    }
    enqueue_h(&mut m, Vec3::new(100.0, 0.0, 0.0), 0.0, true, None);
    assert_eq!(
        adjust(&mut m, 0.016, Vec3::new(1.0, 0.0, 0.0), 10.0),
        Vec3::ZERO
    );
}

#[test]
fn enqueue_prunes_duplicate_within_desired_distance() {
    let mut m = M::new();
    let base = Vec3::new(5.0, 0.0, 0.0);
    enqueue_h(&mut m, base, 0.0, true, None);
    enqueue_h(&mut m, base + Vec3::new(0.01, 0.0, 0.0), 0.0, true, None);
    assert_eq!(adjust(&mut m, 0.016, base, 10.0), Vec3::ZERO);
    assert!(!m.is_active());
}

#[test]
fn clear_empties_queue_and_resets_counters() {
    let mut m = M::new();
    enqueue_h(&mut m, far_target(), 0.0, true, None);
    assert!(m.is_active());
    m.clear();
    assert!(!m.is_active());
    assert_eq!(adjust(&mut m, 0.016, Vec3::ZERO, 4.0), Vec3::ZERO);
}

#[test]
fn adjust_offset_empty_queue_returns_zero() {
    let mut m = M::new();
    assert_eq!(adjust(&mut m, 0.016, Vec3::ZERO, 4.0), Vec3::ZERO);
}

#[test]
fn adjust_offset_reaches_node_within_desired_distance_pops_head() {
    let mut m = M::new();
    enqueue_h(&mut m, Vec3::new(0.02, 0.0, 0.0), 0.0, true, None);
    assert_eq!(adjust(&mut m, 0.016, Vec3::ZERO, 4.0), Vec3::ZERO);
    assert!(!m.is_active());
}

#[test]
fn adjust_offset_clamped_to_catch_up_speed_2x_motion_max() {
    let mut m = M::new();
    let max_speed = 4.0;
    let dt = 0.5;
    enqueue_h(&mut m, Vec3::new(100.0, 0.0, 0.0), 0.0, true, None);
    let delta = adjust(&mut m, dt, Vec3::ZERO, max_speed);
    let expected = max_speed * M::MAX_INTERPOLATED_VELOCITY_MOD * dt as f32;
    assert_eq_prec(expected, delta.length(), 4);
}

#[test]
fn adjust_offset_fallback_speed_when_minterp_zero() {
    let mut m = M::new();
    let dt = 0.5;
    enqueue_h(&mut m, Vec3::new(100.0, 0.0, 0.0), 0.0, true, None);
    let delta = adjust(&mut m, dt, Vec3::ZERO, 0.0);
    assert_eq_prec(M::MAX_INTERPOLATED_VELOCITY * dt as f32, delta.length(), 4);
}

#[test]
fn adjust_offset_overshoot_protection_step_clamped_to_distance() {
    let mut m = M::new();
    enqueue_h(&mut m, Vec3::new(0.5, 0.0, 0.0), 0.0, true, None);
    let delta = adjust(&mut m, 1.0, Vec3::ZERO, 10.0);
    assert_eq_prec(0.5, delta.length(), 4);
}

#[test]
fn adjust_offset_stall_counter_increments_each_frame() {
    let mut m = M::new();
    enqueue_h(&mut m, Vec3::new(10.0, 0.0, 0.0), 0.0, true, None);
    for _ in 0..4 {
        adjust(&mut m, 0.016, Vec3::ZERO, 4.0);
    }
    assert!(m.is_active());
}

#[test]
fn adjust_offset_first_window_initializes_progress_baseline() {
    let mut m = M::new();
    enqueue_h(&mut m, Vec3::new(50.0, 0.0, 0.0), 0.0, true, None);
    for _ in 0..M::STALL_CHECK_FRAME_INTERVAL {
        adjust(&mut m, 0.016, Vec3::ZERO, 4.0);
    }
    assert!(m.is_active());
}

#[test]
fn adjust_offset_first_window_does_not_false_fail() {
    let mut m = M::new();
    enqueue_h(&mut m, Vec3::new(50.0, 0.0, 0.0), 0.0, true, None);
    for _ in 0..M::STALL_CHECK_FRAME_INTERVAL {
        adjust(&mut m, 0.016, Vec3::ZERO, 4.0);
    }
    assert!(m.is_active());
}

#[test]
fn adjust_offset_dt_zero_or_negative_returns_zero() {
    let mut m = M::new();
    enqueue_h(&mut m, far_target(), 0.0, true, None);
    assert_eq!(adjust(&mut m, 0.0, Vec3::ZERO, 4.0), Vec3::ZERO);
    assert_eq!(adjust(&mut m, -1.0, Vec3::ZERO, 4.0), Vec3::ZERO);
    assert_eq!(adjust(&mut m, f64::NAN, Vec3::ZERO, 4.0), Vec3::ZERO);
    assert!(m.is_active());
}

// ── InterpolationRecoveryTests ─────────────────────────────────────────

#[test]
fn sliding_along_wall_discards_unreachable_node_for_recovery() {
    let mut m = M::new();
    let mut body = Vec3::ZERO;
    let target = Vec3::new(1.0, 2.0, 0.0);
    enqueue_h(&mut m, target, 0.0, false, Some(body));
    for _ in 0..10 {
        adjust(&mut m, 1.0 / 30.0, body, 4.0);
        body.y += 0.002;
    }
    assert_eq!(m.diagnostic_state().1, 1);
    assert_eq!(m.diagnostic_state().0, 0);
    let recovery = m.try_get_recovery_target().expect("recovery");
    assert_eq!(recovery.position, target);
    assert!(m.complete_recovery(recovery));
    assert!(m.try_get_recovery_target().is_none());
}

#[test]
fn passing_window_preserves_failures_until_fourth_failure_requests_tail() {
    let mut m = M::new();
    for i in 0..5 {
        enqueue_h(
            &mut m,
            Vec3::new(10.0 + i as f32, 0.0, 0.0),
            0.0,
            false,
            Some(Vec3::ZERO),
        );
    }
    for _ in 0..10 {
        adjust(&mut m, 1.0 / 30.0, Vec3::ZERO, 4.0);
    }
    assert_eq!(m.diagnostic_state(), (4, 1));
    let mut body = Vec3::ZERO;
    for _ in 0..5 {
        body.x += 0.2;
        adjust(&mut m, 1.0 / 30.0, body, 4.0);
    }
    assert_eq!(m.diagnostic_state(), (4, 1));
    assert!(m.try_get_recovery_target().is_none());
    for _ in 0..15 {
        adjust(&mut m, 1.0 / 30.0, body, 4.0);
    }
    assert_eq!(m.diagnostic_state(), (1, 4));
    let recovery = m.try_get_recovery_target().expect("recovery");
    assert_eq!(recovery.position, Vec3::new(14.0, 0.0, 0.0));
}

#[test]
fn sticky_target_skips_failures_without_consuming_the_node() {
    let mut m = M::new();
    enqueue_h(
        &mut m,
        Vec3::new(2.0, 0.0, 0.0),
        0.0,
        true,
        Some(Vec3::ZERO),
    );
    for _ in 0..50 {
        m.adjust_offset_world(1.0 / 30.0, Vec3::ZERO, 4.0, true, true);
    }
    assert_eq!(m.diagnostic_state(), (1, 0));
    assert!(m.try_get_recovery_target().is_none());
}

#[test]
fn blocked_near_node_completes_without_failure() {
    let mut m = M::new();
    enqueue_h(
        &mut m,
        Vec3::new(0.15, 0.0, 0.0),
        0.0,
        false,
        Some(Vec3::ZERO),
    );
    for _ in 0..10 {
        adjust(&mut m, 1.0 / 30.0, Vec3::ZERO, 4.0);
    }
    assert_eq!(m.diagnostic_state(), (0, 0));
    assert!(m.try_get_recovery_target().is_none());
}

fn far_correction_arms_placement(existing_tail: bool) {
    let mut m = M::new();
    if existing_tail {
        enqueue_h(&mut m, Vec3::X, 0.0, false, Some(Vec3::ZERO));
    }
    let target = Vec3::new(25.0, 0.0, 0.0);
    m.enqueue(
        target,
        Quat::IDENTITY,
        false,
        Some(Vec3::ZERO),
        None,
        0x5A48_01E5,
        20.0,
    );
    assert_eq!(m.diagnostic_state().1, 4);
    let recovery = m.try_get_recovery_target().expect("recovery");
    assert_eq!(recovery.position, target);
    assert_eq!(recovery.cell_id, 0x5A48_01E5);
    let delta = adjust(&mut m, 1.0 / 30.0, Vec3::ZERO, 4.0);
    assert!((0.0..=0.27).contains(&delta.length()));
    let retry = m.try_get_recovery_target().expect("retry");
    assert_eq!(recovery, retry);
    assert!(m.complete_recovery(retry));
    assert_eq!(m.diagnostic_state(), (0, 0));
}

#[test]
fn far_correction_arms_placement_without_existing_tail() {
    far_correction_arms_placement(false);
}

#[test]
fn far_correction_arms_placement_with_existing_tail() {
    far_correction_arms_placement(true);
}

#[test]
fn unacknowledged_recovery_persists_and_cannot_clear_newer_correction() {
    let mut m = M::new();
    enqueue_h(
        &mut m,
        Vec3::new(150.0, 0.0, 0.0),
        0.0,
        false,
        Some(Vec3::ZERO),
    );
    let first = m.try_get_recovery_target().expect("first");
    let retry = m.try_get_recovery_target().expect("retry");
    assert_eq!(first, retry);
    enqueue_h(
        &mut m,
        Vec3::new(160.0, 0.0, 0.0),
        0.0,
        false,
        Some(Vec3::ZERO),
    );
    assert!(!m.complete_recovery(first));
    let newer = m.try_get_recovery_target().expect("newer");
    assert_eq!(newer.position, Vec3::new(160.0, 0.0, 0.0));
    assert!(m.complete_recovery(newer));
}
