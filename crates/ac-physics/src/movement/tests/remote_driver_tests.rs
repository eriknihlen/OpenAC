//! Remote motion: OpenAC `tests/AcDream.Core.Tests/Physics/RemoteMotionCombinerTests.cs`
//! (the cases that chase a live interpolation queue; the rest are in
//! `ac-motion`), `tests/AcDream.Runtime.Tests/Physics/RuntimeRemoteSteadyStatePositionTests.cs`
//! (the snap-or-queue choice and the leash after an accepted position; the
//! route classifier is the runtime's), `InboundInterpretedMotionFactoryTests.cs`
//! and `RemoteInboundMotionDispatcherTests.cs` (the packet funnel; the
//! supersession check is the runtime's), driven through `RemoteMotionDriver`.

use glam::{Quat, Vec3};

use ac_motion::compute_offset;

use crate::interpolation::InterpolationManager;
use crate::movement::{
    NoTargets, ObjectToken, PositionOps, RemoteMotionDriver, RemoteMotionUpdate,
    RemotePositionOutcome, RemotePositionRoute, RemotePositionUpdate, StickyTarget,
    TeleportHookSteps, WeenieError, WireMotionItem, WireMotionState, inbound_interpreted_state,
    run_teleport_hook,
};

use super::support::eq_prec;

// ── RemoteMotionCombinerTests (live queue) ────────────────────────────────

/// OpenAC `InterpolationManager.Enqueue(target, heading, isMovingTo)` with
/// its defaults: no body position, no cell, the 100 m blip distance.
fn enqueue(interp: &mut InterpolationManager, target: Vec3) {
    let _ = interp.enqueue_heading(
        target,
        0.0,
        false,
        None,
        0,
        InterpolationManager::AUTONOMY_BLIP_DISTANCE,
    );
}

#[test]
fn interpolation_only_chases_the_queue() {
    let mut interp = InterpolationManager::new();
    enqueue(&mut interp, Vec3::new(1.0, 0.0, 0.0));
    let offset = compute_offset(
        0.1,
        Vec3::ZERO,
        Vec3::ZERO,
        Quat::IDENTITY,
        &mut interp,
        4.0,
    );
    eq_prec(0.8, offset.x, 3);
    eq_prec(0.0, offset.y, 3);
    eq_prec(0.0, offset.z, 3);
}

#[test]
fn both_active_the_correction_replaces_the_root_motion() {
    let mut interp = InterpolationManager::new();
    enqueue(&mut interp, Vec3::new(1.0, 0.0, 0.0));
    let offset = compute_offset(
        0.1,
        Vec3::ZERO,
        Vec3::new(0.0, 0.4, 0.0),
        Quat::IDENTITY,
        &mut interp,
        4.0,
    );
    eq_prec(0.8, offset.x, 3);
    eq_prec(0.0, offset.y, 3);
    eq_prec(0.0, offset.z, 3);
}

#[test]
fn reaching_the_queue_head_with_zero_root_motion_does_not_overshoot() {
    let mut interp = InterpolationManager::new();
    let target = Vec3::new(0.4, 0.0, 0.0);
    enqueue(&mut interp, target);
    let catch_up = compute_offset(
        0.1,
        Vec3::ZERO,
        Vec3::ZERO,
        Quat::IDENTITY,
        &mut interp,
        4.0,
    );
    let reached = catch_up;
    let after = compute_offset(0.1, reached, Vec3::ZERO, Quat::IDENTITY, &mut interp, 4.0);
    assert_eq!(reached, target);
    assert_eq!(after, Vec3::ZERO);
}

// ── RuntimeRemoteSteadyStatePositionTests ─────────────────────────────────

const CELL: u32 = 0x0101_FFFF;

fn token() -> ObjectToken {
    ObjectToken {
        id: 0x7000_0001,
        incarnation: 1,
    }
}

/// A remote that has had a server position before.
fn sampled_remote(body: Vec3) -> RemoteMotionDriver {
    let mut d = RemoteMotionDriver::new(token(), 0x0001, body, Quat::IDENTITY);
    d.last_server_pos_time = 1_700_000_000.0;
    d
}

fn accepted(
    target: Vec3,
    route: RemotePositionRoute,
    will_be_advanced: bool,
) -> RemotePositionUpdate {
    RemotePositionUpdate {
        world_position: target,
        orientation: Quat::IDENTITY,
        cell_id: CELL,
        wire_velocity: None,
        route,
        will_be_advanced,
        is_grounded: true,
        now: 1_700_000_001.0,
    }
}

fn near(target: Vec3, will_be_advanced: bool) -> RemotePositionUpdate {
    accepted(
        target,
        RemotePositionRoute::NearInterpolate,
        will_be_advanced,
    )
}

#[test]
fn a_body_far_from_the_target_snaps_rather_than_queues() {
    let mut d = sampled_remote(Vec3::ZERO);
    let target = Vec3::new(50.0, 0.0, 0.0);
    assert_eq!(
        d.apply_server_position(&near(target, true)),
        RemotePositionOutcome::Snapped
    );
    assert_eq!(d.obj.body.position(), target);
}

#[test]
fn a_body_not_advanced_snaps_even_when_close() {
    let mut d = sampled_remote(Vec3::new(10.0, 10.0, 5.0));
    let target = Vec3::new(10.5, 10.0, 5.0);
    assert_eq!(
        d.apply_server_position(&near(target, false)),
        RemotePositionOutcome::Snapped
    );
    assert_eq!(d.obj.body.position(), target);
}

// OpenAC `RuntimeWorldObjectHeadingTests.ABodyThatTurnedAfterItWasPlacedReportsItsNewHeading`:
// a body that turns after it was placed reports its
// new heading, not the placement's; its place stays. This position
// is what the runtime hands plugins for another creature
// (`movement::remote::live_position`).
#[test]
fn a_body_that_turned_after_it_was_placed_reports_its_new_heading() {
    let mut d = sampled_remote(Vec3::new(10.0, 20.0, 30.0));
    let placed = d.obj.body.cell_position();
    let turned = Quat::from_axis_angle(Vec3::Z, std::f32::consts::PI / 3.0);
    d.obj.body.orientation = turned;
    let live = d.position();
    let heading = ac_motion::get_heading(live.frame.orientation);
    eq_prec(ac_motion::get_heading(turned), heading, 3);
    assert!((ac_motion::get_heading(placed.frame.orientation) - heading).abs() > 1e-3);
    assert_eq!(live.frame.origin, d.obj.body.position());
}

#[test]
fn the_first_position_before_any_server_sample_snaps() {
    let mut d =
        RemoteMotionDriver::new(token(), 0x0001, Vec3::new(10.0, 10.0, 5.0), Quat::IDENTITY);
    assert_eq!(d.last_server_pos_time, 0.0);
    let target = Vec3::new(10.5, 10.0, 5.0);
    assert_eq!(
        d.apply_server_position(&near(target, true)),
        RemotePositionOutcome::Snapped
    );
    assert_eq!(d.obj.body.position(), target);
}

#[test]
fn a_near_advanced_body_queues_without_touching_its_position() {
    let mut d = sampled_remote(Vec3::new(10.0, 10.0, 5.0));
    assert_eq!(
        d.apply_server_position(&near(Vec3::new(10.5, 10.0, 5.0), true)),
        RemotePositionOutcome::Enqueued
    );
    assert_eq!(d.obj.body.position(), Vec3::new(10.0, 10.0, 5.0));
}

#[test]
fn the_choice_is_indifferent_to_the_sticky_lease() {
    let mut d = sampled_remote(Vec3::ZERO);
    d.cx(&NoTargets).stick_to(0x7000_0002, 1.0, 1.0);
    assert_ne!(d.obj.position.get_sticky_object_id(), 0);
    assert_eq!(
        d.apply_server_position(&near(Vec3::new(50.0, 0.0, 0.0), true)),
        RemotePositionOutcome::Snapped
    );
}

#[test]
fn two_remotes_in_the_same_tick_apply_independently() {
    let mut far = sampled_remote(Vec3::ZERO);
    let mut close = sampled_remote(Vec3::new(10.0, 10.0, 5.0));
    let far_outcome = far.apply_server_position(&near(Vec3::new(50.0, 0.0, 0.0), true));
    let close_outcome = close.apply_server_position(&near(Vec3::new(10.5, 10.0, 5.0), true));
    assert_eq!(far_outcome, RemotePositionOutcome::Snapped);
    assert_eq!(close_outcome, RemotePositionOutcome::Enqueued);
    assert_eq!(far.obj.body.position(), Vec3::new(50.0, 0.0, 0.0));
    assert_eq!(close.obj.body.position(), Vec3::new(10.0, 10.0, 5.0));
}

#[test]
fn the_leash_anchors_at_the_current_live_position() {
    let mut d = sampled_remote(Vec3::new(1.0, 2.0, 3.0));
    d.obj.body.set_position(Vec3::new(9.0, 9.0, 9.0));
    d.arm_leash();
    let c = d.obj.position.constraint.expect("leash");
    assert!(c.is_constrained);
    assert_eq!(c.constraint_pos.frame.origin, Vec3::new(9.0, 9.0, 9.0));
    eq_prec(0.0, c.constraint_pos_offset, 3);
}

/// OpenAC `TryArmConstraintAfterOperation_MatchesTheCompletePartition`:
/// every operation arms the leash except the airborne no-operation. The
/// placements arm it once the runtime has placed the body.
#[test]
fn the_leash_follows_every_operation_but_the_airborne_no_operation() {
    let routes = [
        (RemotePositionRoute::Teleport, true),
        (RemotePositionRoute::FarSnap, true),
        (RemotePositionRoute::NearInterpolate, true),
        (RemotePositionRoute::UnroutedCatchUp, true),
        (RemotePositionRoute::AirborneNoOperation, false),
    ];
    for (route, expected) in routes {
        let mut d = sampled_remote(Vec3::new(1.0, 2.0, 3.0));
        let outcome = d.apply_server_position(&accepted(Vec3::new(1.5, 2.0, 3.0), route, true));
        if outcome == RemotePositionOutcome::PlacementRequired {
            d.arm_leash();
        }
        let armed = d.obj.position.constraint.is_some_and(|c| c.is_constrained);
        assert_eq!(armed, expected, "{route:?}");
    }
}

// ── RemoteTeleportHookTests ───────────────────────────────────────────────

/// OpenAC `RemoteTeleportHookTests.Execute_UsesRetailOrderAndExactCancelContext`.
#[test]
fn the_teleport_hook_runs_in_the_game_order_with_you_teleported() {
    #[derive(Default)]
    struct Recorder {
        calls: Vec<&'static str>,
        error: u32,
    }
    impl TeleportHookSteps for Recorder {
        fn cancel_move_to(&mut self, error: WeenieError) {
            self.error = error.0;
            self.calls.push("cancel");
        }
        fn un_stick(&mut self) {
            self.calls.push("unstick");
        }
        fn stop_interpolating(&mut self) {
            self.calls.push("stop-interpolating");
        }
        fn un_constrain(&mut self) {
            self.calls.push("unconstrain");
        }
        fn notify_teleported(&mut self) {
            self.calls.push("notify-teleported");
        }
        fn report_collision_end(&mut self) {
            self.calls.push("collision-end");
        }
    }
    let mut r = Recorder::default();
    run_teleport_hook(&mut r);
    assert_eq!(r.error, 0x3C);
    assert_eq!(
        r.calls,
        [
            "cancel",
            "unstick",
            "stop-interpolating",
            "unconstrain",
            "notify-teleported",
            "collision-end",
        ]
    );
}

/// On a body: the interpolation queue, the sticky target and the leash
/// are gone after the hook.
#[test]
fn a_teleported_body_lets_go_of_its_queue_its_target_and_its_leash() {
    let mut d = sampled_remote(Vec3::new(1.0, 2.0, 3.0));
    let outcome = d.apply_server_position(&accepted(
        Vec3::new(1.5, 2.0, 3.0),
        RemotePositionRoute::NearInterpolate,
        true,
    ));
    assert_eq!(outcome, RemotePositionOutcome::Enqueued);
    d.obj.cx(&NoTargets).stick_to(0x7000_0042, 0.5, 1.0);
    assert!(d.interpolation.is_active());
    assert!(d.obj.position.constraint.is_some_and(|c| c.is_constrained));

    d.teleport_hook();

    assert!(!d.interpolation.is_active());
    assert_eq!(d.interpolation.count(), 0);
    assert_eq!(d.obj.position.get_sticky_object_id(), 0);
    assert!(!d.obj.position.constraint.is_some_and(|c| c.is_constrained));
}

// ── InboundInterpretedMotionFactoryTests ──────────────────────────────────

#[test]
fn an_empty_wire_state_uses_the_constructor_defaults() {
    let r = inbound_interpreted_state(&WireMotionState::default(), 0x4100_0000);
    assert_eq!(r.current_style, 0x8000_003D);
    assert_eq!(r.forward_command, 0x4100_0003);
    assert_eq!(r.forward_speed, 1.0);
    assert_eq!(r.side_step_command, 0);
    assert_eq!(r.side_step_speed, 1.0);
    assert_eq!(r.turn_command, 0);
    assert_eq!(r.turn_speed, 1.0);
    assert!(r.actions.is_none());
}

#[test]
fn an_attack_keeps_its_stamp_autonomy_and_speed() {
    let wire = WireMotionState {
        stance: 0x3E,
        forward_command: Some(0x0003),
        commands: vec![WireMotionItem {
            command: 0x0058,
            packed_sequence: 0x8007,
            speed: 1.75,
        }],
        ..WireMotionState::default()
    };
    let r = inbound_interpreted_state(&wire, 0x4100_0000);
    assert_eq!(r.current_style, 0x8000_003E);
    let actions = r.actions.expect("actions");
    assert_eq!(actions.len(), 1);
    assert_eq!(actions[0].command, 0x1000_0058);
    assert_eq!(actions[0].stamp, 7);
    assert!(actions[0].autonomous);
    assert_eq!(actions[0].speed, 1.75);
}

#[test]
fn axes_and_forward_commands_use_the_command_catalog() {
    let wire = WireMotionState {
        stance: 0x3F,
        forward_command: Some(0x0007),
        forward_speed: Some(2.85),
        side_step_command: Some(0x000F),
        side_step_speed: Some(-1.2),
        turn_command: Some(0x000D),
        turn_speed: Some(1.5),
        ..WireMotionState::default()
    };
    let r = inbound_interpreted_state(&wire, 0x4100_0000);
    assert_eq!(r.forward_command, 0x4400_0007);
    assert_eq!(r.forward_speed, 2.85);
    assert_eq!(r.side_step_command, 0x6500_000F);
    assert_eq!(r.side_step_speed, -1.2);
    assert_eq!(r.turn_command, 0x6500_000D);
    assert_eq!(r.turn_speed, 1.5);
}

// ── RemoteInboundMotionDispatcherTests ────────────────────────────────────

fn animationless_remote() -> RemoteMotionDriver {
    RemoteMotionDriver::new(token(), 0x0101_0001, Vec3::ZERO, Quat::IDENTITY)
}

#[test]
fn an_animationless_type_zero_update_runs_the_whole_funnel() {
    let mut d = animationless_remote();
    let update = RemoteMotionUpdate {
        state: WireMotionState {
            stance: 0x3F,
            forward_command: Some(0x0007),
            forward_speed: Some(2.5),
            side_step_command: Some(0x000F),
            side_step_speed: Some(0.75),
            turn_command: Some(0x000D),
            turn_speed: Some(1.25),
            ..WireMotionState::default()
        },
        movement_type: 0,
        move_to: None,
        sticky_object: Some(StickyTarget {
            id: 0x7000_0099,
            radius: 0.6,
            height: 1.8,
        }),
        standing_long_jump: true,
        fallback_forward_class: 0x4100_0000,
    };
    let r = d.apply_motion(&NoTargets, 1.0, &update);
    assert!(!r.routed_move_to);
    assert!(r.applied_interpreted_state);
    assert!(r.forward_command_changed());
    let s = &d.obj.interp.interpreted_state;
    assert_eq!(s.current_style, 0x8000_003F);
    assert_eq!(s.forward_command, 0x4400_0007);
    assert_eq!(s.forward_speed, 2.5);
    assert_eq!(s.side_step_command, 0x6500_000F);
    assert_eq!(s.side_step_speed, 0.75);
    assert_eq!(s.turn_command, 0x6500_000D);
    assert_eq!(s.turn_speed, 1.25);
    assert!(d.obj.interp.standing_long_jump);
    // Sticking is the funnel's last step, at the target's girth (OpenAC
    // `RemoteInboundMotionDispatcher.Apply` -> `StickToObjectFromWire`).
    assert_eq!(d.obj.position.get_sticky_object_id(), 0x7000_0099);
    assert_eq!(d.obj.position.sticky.map(|s| s.target_radius), Some(0.6));
}

#[test]
fn an_unknown_movement_type_runs_the_head_but_not_the_type_zero_tail() {
    let mut d = animationless_remote();
    let update = RemoteMotionUpdate {
        state: WireMotionState {
            stance: 0x3E,
            forward_command: Some(0x0007),
            ..WireMotionState::default()
        },
        movement_type: 5,
        fallback_forward_class: 0x4100_0000,
        ..RemoteMotionUpdate::default()
    };
    let r = d.apply_motion(&NoTargets, 1.0, &update);
    assert!(!r.routed_move_to);
    assert!(!r.applied_interpreted_state);
    assert_eq!(d.obj.interp.interpreted_state.forward_command, 0x4100_0003);
    assert_eq!(d.obj.position.get_sticky_object_id(), 0);
}

/// A placement forced into its cell (a hook, a storage chest, a corpse)
/// sets only the body's frame and cell; the body keeps the contact it had
/// on the ground. Taking the placement's empty contact fields, it lost it
/// and gravity pulled it out of the cell it was forced into.
#[test]
fn a_placement_forced_into_its_cell_keeps_the_bodys_contact() {
    let token = ObjectToken {
        id: 0x7000_0001,
        incarnation: 1,
    };
    let mut driver = RemoteMotionDriver::new(
        token,
        0xA9B4_0001,
        Vec3::new(10.0, 10.0, 50.0),
        Quat::IDENTITY,
    );
    driver.obj.body.contact_plane_valid = true;
    assert!(driver.obj.body.in_contact() && driver.obj.body.on_walkable());
    let placed = crate::PhysicsSetPositionResult {
        residence: crate::PhysicsResidenceDisposition::Committed,
        position: Vec3::new(12.0, 10.0, 51.0),
        orientation: Quat::IDENTITY,
        cell_id: 0xA9B4_0002,
        cell_local_position: Vec3::new(12.0, 10.0, 51.0),
        forced_into_cell: true,
        ..crate::PhysicsSetPositionResult::default()
    };
    assert!(driver.commit_placement(&NoTargets, &placed, 1.0));
    assert_eq!(driver.obj.body.position(), placed.position);
    assert_eq!(driver.obj.cell_id, placed.cell_id);
    assert!(driver.obj.body.in_contact(), "keeps its contact");
    assert!(driver.obj.body.on_walkable(), "keeps its walkable ground");
    assert!(
        driver.obj.body.contact_plane_valid,
        "keeps its contact plane"
    );
}
