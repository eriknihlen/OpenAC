//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/TargetManagerTests.cs`,
//! `StickyManagerTests.cs`, `ConstraintManagerTests.cs`,
//! `PositionManagerFacadeTests.cs`, `ConstraintDistanceTests.cs`, and the
//! leash case of `MotionInterpreterJumpFamilyTests.cs`.

use ac_motion::MotionDeltaFrame;
use glam::{Quat, Vec3};

use super::stub_world::StubWorld;
use super::support::{eq_prec, make_grounded, make_interp};
use crate::body::CellPosition;
use crate::movement::constraint_distance::{
    get_max_constraint_distance, get_start_constraint_distance, is_indoor_cell,
};
use crate::movement::{
    MotionInterp, ObjectToken, PositionOps, StickyManager, TargetInfo, TargetOps, TargetStatus,
    WeenieError,
};

fn at(x: f32, y: f32, z: f32) -> CellPosition {
    CellPosition::new(1, Vec3::new(x, y, z), Quat::IDENTITY)
}

fn two_hosts() -> (StubWorld, ObjectToken, ObjectToken) {
    let mut w = StubWorld::default();
    let me = w.add(10);
    let target = w.add(20);
    (w, me, target)
}

// ── TargetManagerTests ────────────────────────────────────────────────────

#[test]
fn set_target_subscribes_and_gets_a_snapshot() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(target).set_origin(Vec3::new(3.0, 0.0, 0.0));
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    assert!(w.get(target).target_manager.has_voyeur(me.id));
    let calls = &w.get(me).handle_update_target_calls;
    assert_eq!(calls.len(), 1);
    assert_eq!(calls[0].status, TargetStatus::Ok);
    assert_eq!(calls[0].object_id, target.id);
    assert_eq!(
        calls[0].interpolated_position.frame.origin,
        Vec3::new(3.0, 0.0, 0.0)
    );
    assert_eq!(
        w.get(me).target_manager.target_info().unwrap().status,
        TargetStatus::Ok
    );
}

#[test]
fn set_target_zero_times_out_and_watches_nothing() {
    let (mut w, me, _) = two_hosts();
    w.with(me, |cx| cx.set_target(7, 0, 1.0, 0.0));
    assert!(w.get(me).target_manager.target_info().is_none());
    let calls = &w.get(me).handle_update_target_calls;
    assert_eq!(calls.len(), 1);
    assert_eq!(calls[0].status, TargetStatus::TimedOut);
    assert_eq!(calls[0].context_id, 7);
}

#[test]
fn clear_target_unsubscribes() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    assert!(w.get(target).target_manager.has_voyeur(me.id));
    w.with(me, |cx| cx.clear_target());
    assert!(w.get(me).target_manager.target_info().is_none());
    assert!(!w.get(target).target_manager.has_voyeur(me.id));
}

#[test]
fn an_update_for_another_object_is_ignored() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    let before = w.get(me).handle_update_target_calls.len();
    let p = at(0.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.receive_update(TargetInfo::new(999, TargetStatus::Ok, p, p), target);
    });
    assert_eq!(w.get(me).handle_update_target_calls.len(), before);
}

#[test]
fn exit_world_clears_the_target() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    let p = at(0.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.receive_update(
            TargetInfo::new(target.id, TargetStatus::ExitWorld, p, p),
            target,
        );
    });
    assert!(w.get(me).target_manager.target_info().is_none());
    assert!(!w.get(target).target_manager.has_voyeur(me.id));
}

#[test]
fn the_direction_to_the_target_is_recorded() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).set_origin(Vec3::ZERO);
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    let tp = at(0.0, 5.0, 0.0);
    w.with(me, |cx| {
        cx.receive_update(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp), target);
    });
    let heading = w
        .get(me)
        .target_manager
        .target_info()
        .unwrap()
        .interpolated_heading;
    eq_prec(0.0, heading.x, 3);
    eq_prec(1.0, heading.y, 3);
}

#[test]
fn an_update_from_a_same_guid_replacement_is_ignored() {
    let (mut w, me, original) = two_hosts();
    w.with(me, |cx| cx.set_target(0, original.id, 1.0, 0.0));
    w.get_mut(me).handle_update_target_calls.clear();
    let replacement = w.add(original.id);
    let p = at(99.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.receive_update(
            TargetInfo::new(original.id, TargetStatus::ExitWorld, p, p),
            replacement,
        );
    });
    assert!(w.get(me).handle_update_target_calls.is_empty());
    assert!(w.get(me).target_manager.target_info().is_some());
    assert_eq!(
        w.get(me)
            .target_manager
            .get_relationship_target(original.id),
        Some(original)
    );
}

#[test]
fn sticky_follows_the_exact_target_not_a_replacement() {
    let (mut w, me, original) = two_hosts();
    w.get_mut(me).position_manager_hooked = true;
    w.get_mut(me).set_origin(Vec3::ZERO);
    w.get_mut(original).set_origin(Vec3::new(5.0, 0.0, 0.0));
    w.with(me, |cx| cx.stick_to(original.id, 0.5, 1.0));
    let replacement = w.add(original.id);
    w.get_mut(replacement).set_origin(Vec3::new(10.0, 0.0, 0.0));
    w.get_mut(original).set_origin(Vec3::new(-5.0, 0.0, 0.0));
    let mut delta = MotionDeltaFrame::default();
    w.with(me, |cx| cx.position_adjust_offset(&mut delta, 0.1));
    assert!(delta.origin.x < 0.0);
    assert_eq!(
        w.get(me)
            .target_manager
            .get_relationship_target(original.id),
        Some(original)
    );
}

#[test]
fn an_unheard_target_times_out_after_ten_seconds() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(target).resolvable = false;
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    assert_eq!(
        w.get(me).target_manager.target_info().unwrap().status,
        TargetStatus::Undefined
    );
    w.get_mut(me).advance_clocks(11.0);
    w.with(me, |cx| cx.handle_targetting());
    assert_eq!(
        w.get(me).target_manager.target_info().unwrap().status,
        TargetStatus::TimedOut
    );
    assert!(
        w.get(me)
            .handle_update_target_calls
            .iter()
            .any(|c| c.status == TargetStatus::TimedOut)
    );
}

#[test]
fn watchers_are_checked_at_most_twice_a_second() {
    let (mut w, me, target) = two_hosts();
    w.with(target, |cx| cx.add_voyeur(me, 0.1, 0.0));
    w.get_mut(target).set_origin(Vec3::new(10.0, 0.0, 0.0));
    w.get_mut(target).physics_timer_time = 0.3;
    let before = w.get(me).handle_update_target_calls.len();
    w.with(target, |cx| cx.handle_targetting());
    assert_eq!(w.get(me).handle_update_target_calls.len(), before);
}

#[test]
fn a_watcher_is_sent_updates_only_past_its_radius() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    let after_subscribe = w.get(me).handle_update_target_calls.len();
    w.get_mut(target).set_origin(Vec3::new(0.5, 0.0, 0.0));
    w.get_mut(target).advance_clocks(1.0);
    w.with(target, |cx| cx.handle_targetting());
    assert_eq!(w.get(me).handle_update_target_calls.len(), after_subscribe);
    w.get_mut(target).set_origin(Vec3::new(2.0, 0.0, 0.0));
    w.get_mut(target).advance_clocks(1.0);
    w.with(target, |cx| cx.handle_targetting());
    let calls = &w.get(me).handle_update_target_calls;
    assert_eq!(calls.len(), after_subscribe + 1);
    let last = calls.last().unwrap();
    assert_eq!(last.status, TargetStatus::Ok);
    eq_prec(2.0, last.interpolated_position.frame.origin.x, 3);
}

#[test]
fn the_interpolated_position_dead_reckons() {
    let (mut w, _, target) = two_hosts();
    w.get_mut(target).set_origin(Vec3::new(1.0, 2.0, 0.0));
    w.get_mut(target).velocity = Vec3::new(10.0, 0.0, 0.0);
    let p = w.with(target, |cx| cx.get_interpolated_position(0.5));
    assert_eq!(p.frame.origin, Vec3::new(6.0, 2.0, 0.0));
}

#[test]
fn re_adding_a_watcher_updates_in_place_without_sending() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(target).set_origin(Vec3::ZERO);
    w.with(target, |cx| cx.add_voyeur(me, 1.0, 0.0));
    let voyeur = |w: &StubWorld| {
        w.get(target)
            .target_manager
            .voyeurs()
            .unwrap()
            .into_iter()
            .find(|(k, _)| *k == me.id)
            .unwrap()
            .1
    };
    assert_eq!(voyeur(&w).last_sent_position.frame.origin, Vec3::ZERO);
    w.get_mut(target).set_origin(Vec3::new(5.0, 0.0, 0.0));
    w.with(target, |cx| cx.add_voyeur(me, 2.5, 0.3));
    let v = voyeur(&w);
    assert_eq!(v.radius, 2.5);
    assert_eq!(v.quantum, 0.3);
    assert_eq!(v.last_sent_position.frame.origin, Vec3::ZERO);
}

#[test]
fn remove_voyeur_removes_once() {
    let (mut w, me, target) = two_hosts();
    w.with(target, |cx| cx.add_voyeur(me, 1.0, 0.0));
    assert!(w.with(target, |cx| cx.remove_voyeur(me.id, me)));
    assert!(!w.get(target).target_manager.has_voyeur(me.id));
    assert!(!w.with(target, |cx| cx.remove_voyeur(me.id, me)));
}

#[test]
fn a_hidden_watchers_subscription_survives_guid_reuse() {
    let (mut w, watcher, target) = two_hosts();
    w.with(watcher, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    w.get_mut(watcher).handle_update_target_calls.clear();
    w.get_mut(watcher).resolvable = false;
    let replacement = w.add(watcher.id);
    w.with(target, |cx| {
        cx.notify_voyeur_of_event(TargetStatus::Teleported)
    });
    assert_eq!(w.get(watcher).handle_update_target_calls.len(), 1);
    assert!(w.get(replacement).handle_update_target_calls.is_empty());
    assert!(!w.with(target, |cx| cx.remove_voyeur(watcher.id, replacement)));
    assert!(w.with(target, |cx| cx.remove_voyeur(watcher.id, watcher)));
}

#[test]
fn an_event_reaches_every_watcher() {
    let mut w = StubWorld::default();
    let target = w.add(20);
    let w1 = w.add(10);
    let w2 = w.add(11);
    w.with(w1, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    w.with(w2, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    let b1 = w.get(w1).handle_update_target_calls.len();
    let b2 = w.get(w2).handle_update_target_calls.len();
    w.with(target, |cx| {
        cx.notify_voyeur_of_event(TargetStatus::Teleported)
    });
    assert_eq!(w.get(w1).handle_update_target_calls.len(), b1 + 1);
    assert_eq!(w.get(w2).handle_update_target_calls.len(), b2 + 1);
    assert_eq!(
        w.get(w1).handle_update_target_calls.last().unwrap().status,
        TargetStatus::Teleported
    );
}

#[test]
fn a_watcher_tracks_a_moving_target() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(target).set_origin(Vec3::ZERO);
    w.with(me, |cx| cx.set_target(0, target.id, 1.0, 0.0));
    assert_eq!(w.get(me).handle_update_target_calls.len(), 1);
    w.get_mut(target).set_origin(Vec3::new(3.0, 0.0, 0.0));
    w.get_mut(target).advance_clocks(1.0);
    w.with(target, |cx| cx.handle_targetting());
    let calls = &w.get(me).handle_update_target_calls;
    assert_eq!(calls.len(), 2);
    assert_eq!(
        calls.last().unwrap().interpolated_position.frame.origin,
        Vec3::new(3.0, 0.0, 0.0)
    );
    eq_prec(
        3.0,
        w.get(me)
            .target_manager
            .target_info()
            .unwrap()
            .interpolated_position
            .frame
            .origin
            .x,
        3,
    );
}

// ── StickyManagerTests ────────────────────────────────────────────────────

fn sticky(w: &StubWorld, me: ObjectToken) -> StickyManager {
    w.get(me).position_manager.sticky.unwrap()
}

fn stuck_and_initialized(w: &mut StubWorld, me: ObjectToken, target: ObjectToken, origin: Vec3) {
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    w.get_mut(target).set_origin(origin);
    let tp = CellPosition::new(1, origin, Quat::IDENTITY);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp));
    });
}

#[test]
fn stick_to_sets_the_target_and_timeout_and_subscribes() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).cur_time = 100.0;
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 2.0));
    let s = sticky(&w, me);
    assert_eq!(s.target_id, target.id);
    assert_eq!(s.target_radius, 0.5);
    assert!(!s.initialized);
    assert_eq!(s.sticky_timeout_time, 101.0);
    assert!(w.get(target).target_manager_touched);
    assert!(w.get(target).target_manager.has_voyeur(me.id));
}

#[test]
fn an_ok_report_initialises_and_caches_the_position() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let tp = at(3.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp));
    });
    let s = sticky(&w, me);
    assert!(s.initialized);
    assert_eq!(s.target_position.frame.origin, Vec3::new(3.0, 0.0, 0.0));
}

#[test]
fn a_report_for_another_object_is_ignored() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let tp = at(0.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(999, TargetStatus::Ok, tp, tp));
    });
    assert!(!sticky(&w, me).initialized);
    assert_eq!(sticky(&w, me).target_id, target.id);
}

#[test]
fn a_bad_report_tears_the_stick_down() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let before = w.get(me).interrupt_current_movement_calls;
    let tp = at(0.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(
            target.id,
            TargetStatus::ExitWorld,
            tp,
            tp,
        ));
    });
    assert_eq!(sticky(&w, me).target_id, 0);
    assert!(!sticky(&w, me).initialized);
    assert_eq!(w.get(me).interrupt_current_movement_calls, before + 1);
}

#[test]
fn an_unconfirmed_stick_times_out_strictly_after_a_second() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).cur_time = 100.0;
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    w.get_mut(me).cur_time = 100.9;
    w.with(me, |cx| cx.position_use_time());
    assert_eq!(sticky(&w, me).target_id, target.id);
    w.get_mut(me).cur_time = 101.0;
    w.with(me, |cx| cx.position_use_time());
    assert_eq!(sticky(&w, me).target_id, target.id);
    let before = w.get(me).interrupt_current_movement_calls;
    w.get_mut(me).cur_time = 101.001;
    w.with(me, |cx| cx.position_use_time());
    assert_eq!(sticky(&w, me).target_id, 0);
    assert_eq!(w.get(me).interrupt_current_movement_calls, before + 1);
}

#[test]
fn re_sticking_tears_the_old_stick_down_first() {
    let mut w = StubWorld::default();
    let me = w.add(10);
    let a = w.add(20);
    let b = w.add(21);
    w.with(me, |cx| cx.stick_to(a.id, 0.5, 1.0));
    let before = w.get(me).interrupt_current_movement_calls;
    w.with(me, |cx| cx.stick_to(b.id, 0.5, 1.0));
    assert_eq!(sticky(&w, me).target_id, b.id);
    assert_eq!(w.get(me).interrupt_current_movement_calls, before + 1);
}

#[test]
fn an_uninitialised_stick_leaves_the_offset_alone() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let mut frame = MotionDeltaFrame {
        origin: Vec3::new(9.0, 9.0, 9.0),
        ..MotionDeltaFrame::default()
    };
    w.with(me, |cx| cx.sticky_adjust_offset(&mut frame, 0.1));
    assert_eq!(frame.origin, Vec3::new(9.0, 9.0, 9.0));
}

fn sticky_offset(me_radius: f32, max_speed: Option<f32>, target_origin: Vec3) -> MotionDeltaFrame {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).radius = me_radius;
    w.get_mut(me).minterp_max_speed = max_speed;
    stuck_and_initialized(&mut w, me, target, target_origin);
    let mut frame = MotionDeltaFrame::default();
    w.with(me, |cx| cx.sticky_adjust_offset(&mut frame, 0.1));
    frame
}

#[test]
fn sticky_steers_toward_the_target_at_the_step_limit() {
    let frame = sticky_offset(0.5, Some(1.0), Vec3::new(5.0, 0.0, 0.0));
    eq_prec(0.5, frame.origin.x, 3);
    eq_prec(0.0, frame.origin.y, 3);
    eq_prec(0.0, frame.origin.z, 3);
    eq_prec(90.0, frame.heading(), 1);
}

#[test]
fn sticky_backs_off_when_too_close() {
    eq_prec(
        -0.4,
        sticky_offset(0.5, Some(1.0), Vec3::new(0.9, 0.0, 0.0))
            .origin
            .x,
        3,
    );
    eq_prec(
        -0.5,
        sticky_offset(0.5, Some(1.0), Vec3::new(0.1, 0.0, 0.0))
            .origin
            .x,
        3,
    );
}

#[test]
fn sticky_uses_the_cached_position_for_an_unresolvable_target() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).radius = 0.5;
    w.get_mut(me).minterp_max_speed = Some(1.0);
    w.get_mut(target).resolvable = false;
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let tp = at(4.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp));
    });
    let mut frame = MotionDeltaFrame::default();
    w.with(me, |cx| cx.sticky_adjust_offset(&mut frame, 0.1));
    eq_prec(0.5, frame.origin.x, 3);
}

#[test]
fn sticky_without_an_interpreter_follows_at_15() {
    eq_prec(
        1.5,
        sticky_offset(0.5, None, Vec3::new(50.0, 0.0, 0.0)).origin.x,
        3,
    );
}

// ── ConstraintManagerTests ────────────────────────────────────────────────

fn constrained(anchor_x: f32, start: f32, max: f32, in_contact: bool) -> (StubWorld, ObjectToken) {
    let mut w = StubWorld::default();
    let me = w.add(10);
    w.get_mut(me).set_origin(Vec3::ZERO);
    w.get_mut(me).in_contact = in_contact;
    w.with(me, |cx| cx.constrain_to(at(anchor_x, 0.0, 0.0), start, max));
    (w, me)
}

#[test]
fn the_leash_starts_at_the_distance_from_the_anchor() {
    let (w, me) = constrained(5.0, 2.0, 10.0, true);
    let c = w.get(me).position_manager.constraint.unwrap();
    assert!(c.is_constrained);
    eq_prec(5.0, c.constraint_pos_offset, 3);
}

#[test]
fn fully_constrained_only_past_90_percent() {
    let (mut w, me) = constrained(8.0, 2.0, 10.0, true);
    assert!(!w.get(me).position_manager.is_fully_constrained());
    w.with(me, |cx| cx.constrain_to(at(9.5, 0.0, 0.0), 2.0, 10.0));
    assert!(w.get(me).position_manager.is_fully_constrained());
    assert!(!crate::movement::ConstraintManager::default().is_fully_constrained());
}

fn constrained_step(
    anchor_x: f32,
    start: f32,
    max: f32,
    in_contact: bool,
    step: Vec3,
) -> (Vec3, f32) {
    let (mut w, me) = constrained(anchor_x, start, max, in_contact);
    let mut frame = MotionDeltaFrame {
        origin: step,
        ..MotionDeltaFrame::default()
    };
    w.with(me, |cx| cx.constraint_adjust_offset(&mut frame, 0.1));
    (
        frame.origin,
        w.get(me)
            .position_manager
            .constraint
            .unwrap()
            .constraint_pos_offset,
    )
}

#[test]
fn the_leash_tapers_inside_the_band() {
    let (o, off) = constrained_step(5.0, 2.0, 10.0, true, Vec3::X);
    eq_prec(0.625, o.x, 3);
    eq_prec(0.625, off, 3);
}

#[test]
fn the_leash_stops_past_the_maximum() {
    let (o, _) = constrained_step(20.0, 2.0, 10.0, true, Vec3::X);
    assert_eq!(o, Vec3::ZERO);
}

#[test]
fn the_leash_passes_below_the_start() {
    let (o, off) = constrained_step(1.0, 2.0, 10.0, true, Vec3::X);
    eq_prec(1.0, o.x, 3);
    eq_prec(1.0, off, 3);
}

#[test]
fn in_the_air_the_leash_only_tracks_the_step() {
    let (o, off) = constrained_step(20.0, 2.0, 10.0, false, Vec3::new(3.0, 4.0, 0.0));
    assert_eq!(o, Vec3::new(3.0, 4.0, 0.0));
    eq_prec(5.0, off, 3);
}

#[test]
fn without_a_leash_nothing_changes() {
    let mut w = StubWorld::default();
    let me = w.add(10);
    let mut frame = MotionDeltaFrame {
        origin: Vec3::new(7.0, 0.0, 0.0),
        ..MotionDeltaFrame::default()
    };
    w.with(me, |cx| cx.position_adjust_offset(&mut frame, 0.1));
    assert_eq!(frame.origin, Vec3::new(7.0, 0.0, 0.0));
}

#[test]
fn un_constrain_clears_the_flag() {
    let (mut w, me) = constrained(5.0, 2.0, 10.0, true);
    w.get_mut(me).position_manager.un_constrain();
    assert!(
        !w.get(me)
            .position_manager
            .constraint
            .unwrap()
            .is_constrained
    );
}

// ── PositionManagerFacadeTests ────────────────────────────────────────────

#[test]
fn un_stick_before_sticking_does_nothing() {
    let (mut w, me, _) = two_hosts();
    w.with(me, |cx| cx.un_stick());
    assert!(w.get(me).position_manager.sticky.is_none());
    assert_eq!(w.get(me).position_manager.get_sticky_object_id(), 0);
}

#[test]
fn stick_to_creates_the_sticky_manager() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    assert!(w.get(me).position_manager.sticky.is_some());
    assert_eq!(w.get(me).position_manager.get_sticky_object_id(), target.id);
}

#[test]
fn no_leash_is_not_fully_constrained() {
    let (w, me, _) = two_hosts();
    assert!(!w.get(me).position_manager.is_fully_constrained());
    assert!(w.get(me).position_manager.constraint.is_none());
}

#[test]
fn constrain_to_creates_the_leash() {
    let (w, me) = constrained(5.0, 2.0, 10.0, true);
    assert!(
        w.get(me)
            .position_manager
            .constraint
            .unwrap()
            .is_constrained
    );
}

#[test]
fn target_reports_reach_the_sticky_manager() {
    let (mut w, me, target) = two_hosts();
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    let tp = at(2.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp));
    });
    assert!(sticky(&w, me).initialized);
}

#[test]
fn the_offset_goes_through_sticky_then_the_leash() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).radius = 0.5;
    w.get_mut(me).minterp_max_speed = Some(1.0);
    w.get_mut(me).in_contact = true;
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    w.get_mut(target).set_origin(Vec3::new(5.0, 0.0, 0.0));
    let tp = at(5.0, 0.0, 0.0);
    w.with(me, |cx| {
        cx.position_handle_update_target(TargetInfo::new(target.id, TargetStatus::Ok, tp, tp));
    });
    w.get_mut(me).set_origin(Vec3::ZERO);
    w.with(me, |cx| cx.constrain_to(at(6.0, 0.0, 0.0), 2.0, 10.0));
    let mut frame = MotionDeltaFrame::default();
    w.with(me, |cx| cx.position_adjust_offset(&mut frame, 0.1));
    eq_prec(0.25, frame.origin.x, 3);
}

#[test]
fn use_time_drives_the_sticky_timeout() {
    let (mut w, me, target) = two_hosts();
    w.get_mut(me).cur_time = 100.0;
    w.with(me, |cx| cx.stick_to(target.id, 0.5, 1.0));
    w.get_mut(me).cur_time = 101.001;
    w.with(me, |cx| cx.position_use_time());
    assert_eq!(w.get(me).position_manager.get_sticky_object_id(), 0);
}

// ── ConstraintDistanceTests ───────────────────────────────────────────────

#[test]
fn leash_distances_by_cell() {
    const OUTDOOR: u32 = 0x1234_0007;
    const INDOOR: u32 = 0x1234_0105;
    assert_eq!(get_start_constraint_distance(OUTDOOR), 10.0);
    assert_eq!(get_start_constraint_distance(INDOOR), 5.0);
    assert_eq!(get_max_constraint_distance(OUTDOOR), 50.0);
    assert_eq!(get_max_constraint_distance(INDOOR), 20.0);
    for (cell, indoor) in [
        (0x00FF, false),
        (0x0100, true),
        (0x0000, false),
        (0xFFFF, true),
    ] {
        assert_eq!(is_indoor_cell(cell), indoor);
    }
    assert_eq!(
        get_start_constraint_distance(OUTDOOR),
        get_start_constraint_distance(OUTDOOR)
    );
}

// ── MotionInterpreterJumpFamilyTests: the leash case ──────────────────────

#[test]
fn an_overstrained_leash_refuses_the_jump() {
    let mut w = StubWorld::default();
    let host = w.add(10);
    const OUTDOOR: u32 = 0x1234_0007;
    w.get_mut(host).position = CellPosition::new(OUTDOOR, Vec3::ZERO, Quat::IDENTITY);
    let start = get_start_constraint_distance(OUTDOOR);
    let max = get_max_constraint_distance(OUTDOOR);
    assert_eq!((start, max), (10.0, 50.0));
    let anchor = w.get(host).position;
    w.with(host, |cx| cx.constrain_to(anchor, start, max));
    assert!(!w.get(host).position_manager.is_fully_constrained());
    let mut frame = MotionDeltaFrame {
        origin: Vec3::new(46.0, 0.0, 0.0),
        ..MotionDeltaFrame::default()
    };
    w.with(host, |cx| cx.position_adjust_offset(&mut frame, 1.0 / 30.0));
    assert!(w.get(host).position_manager.is_fully_constrained());

    let mut body = make_grounded();
    body.is_fully_constrained = w.get(host).position_manager.is_fully_constrained();
    let interp = make_interp(Some(body), None);
    assert_eq!(
        interp.jump_is_allowed(0.5).0,
        WeenieError::GENERAL_MOVEMENT_FAILURE
    );
}
