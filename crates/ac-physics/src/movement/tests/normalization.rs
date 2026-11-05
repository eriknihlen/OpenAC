//! OpenAC `tests/AcDream.Core.Tests/Physics/MotionNormalizationTests.cs`,
//! `MotionVelocityPipelineTests.cs` and
//! `Motion/MotionTableDispatchSinkTests.cs`.

use ac_motion::motion_command::{
    NON_COMBAT, READY, RUN_FORWARD, SIDE_STEP_LEFT, SIDE_STEP_RIGHT, TURN_LEFT, TURN_RIGHT,
    WALK_BACKWARDS, WALK_FORWARD,
};
use ac_motion::{
    AnimationSequencer, BACKWARDS_FACTOR, HoldKey, MAX_SIDESTEP_ANIM_RATE, RUN_ANIM_SPEED,
    RUN_TURN_FACTOR, RawMotionState, SIDESTEP_ANIM_SPEED, SIDESTEP_FACTOR, WALK_ANIM_SPEED,
};
use glam::Vec3;

use super::motion_fixtures::{
    MapLoader, TableBuilder, motion_data, rest_anim, sequencer, with_omega,
};
use super::support::{FakeWeenie, eq_prec, run_rate_weenie};
use crate::movement::{InterpretedMotionSink, MotionInterp, StandaloneInterpreter};

const SIDESTEP_SCALE: f32 = 0.5 * (3.119_999_89 / SIDESTEP_ANIM_SPEED);

fn interp(weenie: Option<Box<dyn crate::movement::WeenieObject>>) -> StandaloneInterpreter {
    StandaloneInterpreter {
        weenie_obj: weenie,
        ..StandaloneInterpreter::default()
    }
}

fn adjust(i: &StandaloneInterpreter, motion: u32, speed: f32, hold: HoldKey) -> (u32, f32) {
    let (mut m, mut s) = (motion, speed);
    i.adjust_motion(&mut m, &mut s, hold);
    (m, s)
}

fn run_to(i: &StandaloneInterpreter, motion: u32, speed: f32) -> (u32, f32) {
    let (mut m, mut s) = (motion, speed);
    i.apply_run_to_command(&mut m, &mut s);
    (m, s)
}

#[test]
fn walk_backwards_becomes_walk_forward_at_minus_0_65() {
    let (m, s) = adjust(&interp(None), WALK_BACKWARDS, 1.0, HoldKey::None);
    assert_eq!(m, WALK_FORWARD);
    eq_prec(-BACKWARDS_FACTOR, s, 5);
    eq_prec(-0.649_999_976, s, 5);
}

#[test]
fn turn_left_becomes_turn_right_negated() {
    let (m, s) = adjust(&interp(None), TURN_LEFT, 1.0, HoldKey::None);
    assert_eq!(m, TURN_RIGHT);
    eq_prec(-1.0, s, 5);
}

#[test]
fn sidestep_left_is_negated_before_the_scale() {
    let (m, s) = adjust(&interp(None), SIDE_STEP_LEFT, 1.0, HoldKey::None);
    assert_eq!(m, SIDE_STEP_RIGHT);
    eq_prec(-SIDESTEP_SCALE, s, 5);
    eq_prec(-1.247_999_95, s, 4);
}

#[test]
fn sidestep_right_is_scaled() {
    let (m, s) = adjust(&interp(None), SIDE_STEP_RIGHT, 1.0, HoldKey::None);
    assert_eq!(m, SIDE_STEP_RIGHT);
    eq_prec(SIDESTEP_SCALE, s, 5);
    eq_prec(1.247_999_95, s, 4);
}

#[test]
fn run_forward_returns_early() {
    let (m, s) = adjust(
        &interp(Some(run_rate_weenie(2.94))),
        RUN_FORWARD,
        1.0,
        HoldKey::Run,
    );
    assert_eq!(m, RUN_FORWARD);
    eq_prec(1.0, s, 5);
}

#[test]
fn other_motions_are_unchanged() {
    let (m, s) = adjust(&interp(None), READY, 1.0, HoldKey::None);
    assert_eq!(m, READY);
    eq_prec(1.0, s, 5);
}

#[test]
fn an_invalid_hold_key_uses_the_current_one() {
    let mut i = interp(None);
    i.interp.raw_state.current_hold_key = HoldKey::Run;
    let (m, s) = adjust(&i, WALK_FORWARD, 1.0, HoldKey::Invalid);
    assert_eq!(m, RUN_FORWARD);
    eq_prec(1.0, s, 5);
}

#[test]
fn hold_key_none_does_not_promote() {
    let mut i = interp(None);
    i.interp.raw_state.current_hold_key = HoldKey::Run;
    let (m, s) = adjust(&i, WALK_FORWARD, 1.0, HoldKey::None);
    assert_eq!(m, WALK_FORWARD);
    eq_prec(1.0, s, 5);
}

#[test]
fn hold_key_run_promotes() {
    let (m, s) = adjust(
        &interp(Some(run_rate_weenie(2.75))),
        WALK_FORWARD,
        1.0,
        HoldKey::Run,
    );
    assert_eq!(m, RUN_FORWARD);
    eq_prec(2.75, s, 5);
}

#[test]
fn run_to_command_walk_forward() {
    let i = interp(Some(run_rate_weenie(2.94)));
    let (m, s) = run_to(&i, WALK_FORWARD, 1.0);
    assert_eq!(m, RUN_FORWARD);
    eq_prec(2.94, s, 5);
    let (m, s) = run_to(&i, WALK_FORWARD, -0.649_999_976);
    assert_eq!(m, WALK_FORWARD);
    eq_prec(-0.649_999_976 * 2.94, s, 4);
}

#[test]
fn run_to_command_turn_is_1_5() {
    let (m, s) = run_to(&interp(Some(run_rate_weenie(2.94))), TURN_RIGHT, 1.0);
    assert_eq!(m, TURN_RIGHT);
    eq_prec(1.5, s, 5);
}

#[test]
fn run_to_command_sidestep_scaling_and_clamp() {
    let (m, s) = run_to(&interp(Some(run_rate_weenie(2.0))), SIDE_STEP_RIGHT, 1.0);
    assert_eq!(m, SIDE_STEP_RIGHT);
    eq_prec(2.0, s, 5);
    let i = interp(Some(run_rate_weenie(2.94)));
    eq_prec(3.0, run_to(&i, SIDE_STEP_RIGHT, 1.247_999_95).1, 5);
    eq_prec(-3.0, run_to(&i, SIDE_STEP_RIGHT, -1.247_999_95).1, 5);
}

#[test]
fn run_to_command_falls_back_to_my_run_rate() {
    let mut i = interp(Some(Box::new(FakeWeenie {
        run_rate: 999.0,
        inq_run_rate_result: false,
        ..FakeWeenie::default()
    })));
    i.interp.my_run_rate = 1.5;
    let (m, s) = run_to(&i, WALK_FORWARD, 1.0);
    assert_eq!(m, RUN_FORWARD);
    eq_prec(1.5, s, 5);
}

#[test]
fn run_to_command_without_a_weenie_is_rate_one() {
    eq_prec(1.0, run_to(&interp(None), SIDE_STEP_RIGHT, 1.0).1, 5);
}

// ── MotionVelocityPipelineTests ───────────────────────────────────────────

const RUN_RATE: f32 = 2.75;
const SIDE_SCALE: f32 = SIDESTEP_FACTOR * (WALK_ANIM_SPEED / SIDESTEP_ANIM_SPEED);

fn raw(forward: u32, sidestep: u32, turn: u32, hold: HoldKey) -> RawMotionState {
    let axis_key = |command: u32| if command != 0 { hold } else { HoldKey::Invalid };
    let mut r = RawMotionState::default();
    r.current_hold_key = hold;
    r.forward_command = forward;
    r.forward_hold_key = if forward != READY {
        hold
    } else {
        HoldKey::Invalid
    };
    r.forward_speed = 1.0;
    r.sidestep_command = sidestep;
    r.sidestep_hold_key = axis_key(sidestep);
    r.sidestep_speed = 1.0;
    r.turn_command = turn;
    r.turn_hold_key = axis_key(turn);
    r.turn_speed = 1.0;
    r
}

fn pipeline(rate: f32, r: &RawMotionState) -> StandaloneInterpreter {
    let mut i = interp(Some(run_rate_weenie(rate)));
    i.apply_raw_movement_state(r);
    i
}

#[test]
fn run_forward_velocity_is_run_speed_times_rate() {
    let mut i = pipeline(RUN_RATE, &raw(WALK_FORWARD, 0, 0, HoldKey::Run));
    let v = i.get_state_velocity();
    eq_prec(RUN_ANIM_SPEED * RUN_RATE, v.y, 3);
    eq_prec(0.0, v.x, 5);
}

#[test]
fn walk_forward_velocity_is_walk_speed() {
    let mut i = pipeline(RUN_RATE, &raw(WALK_FORWARD, 0, 0, HoldKey::None));
    eq_prec(WALK_ANIM_SPEED, i.get_state_velocity().y, 3);
}

#[test]
fn run_backward_velocity_is_negative() {
    let mut i = pipeline(RUN_RATE, &raw(WALK_BACKWARDS, 0, 0, HoldKey::Run));
    let v = i.get_state_velocity();
    eq_prec(-(WALK_ANIM_SPEED * BACKWARDS_FACTOR * RUN_RATE), v.y, 3);
    assert!(v.y < 0.0);
}

#[test]
fn run_strafe_right_clamps_to_3_75() {
    let mut i = pipeline(RUN_RATE, &raw(READY, SIDE_STEP_RIGHT, 0, HoldKey::Run));
    let v = i.get_state_velocity();
    eq_prec(SIDESTEP_ANIM_SPEED * MAX_SIDESTEP_ANIM_RATE, v.x, 3);
    eq_prec(3.75, v.x, 3);
    eq_prec(0.0, v.y, 5);
}

#[test]
fn run_strafe_right_below_the_clamp() {
    let mut i = pipeline(2.0, &raw(READY, SIDE_STEP_RIGHT, 0, HoldKey::Run));
    eq_prec(
        SIDESTEP_ANIM_SPEED * SIDE_SCALE * 2.0,
        i.get_state_velocity().x,
        3,
    );
}

#[test]
fn run_strafe_left_is_minus_3_75() {
    let mut i = pipeline(RUN_RATE, &raw(READY, SIDE_STEP_LEFT, 0, HoldKey::Run));
    let v = i.get_state_velocity();
    eq_prec(-(SIDESTEP_ANIM_SPEED * MAX_SIDESTEP_ANIM_RATE), v.x, 3);
    assert!(v.x < 0.0);
}

#[test]
fn run_turns_at_1_5() {
    let i = pipeline(RUN_RATE, &raw(READY, 0, TURN_RIGHT, HoldKey::Run));
    assert_eq!(i.interp.interpreted_state.turn_command, TURN_RIGHT);
    eq_prec(RUN_TURN_FACTOR, i.interp.interpreted_state.turn_speed, 5);
    let i = pipeline(RUN_RATE, &raw(READY, 0, TURN_LEFT, HoldKey::Run));
    assert_eq!(i.interp.interpreted_state.turn_command, TURN_RIGHT);
    eq_prec(-RUN_TURN_FACTOR, i.interp.interpreted_state.turn_speed, 5);
    let i = pipeline(RUN_RATE, &raw(READY, 0, TURN_RIGHT, HoldKey::None));
    eq_prec(1.0, i.interp.interpreted_state.turn_speed, 5);
}

#[test]
fn idle_has_no_velocity() {
    let mut i = pipeline(RUN_RATE, &raw(READY, 0, 0, HoldKey::None));
    let v = i.get_state_velocity();
    eq_prec(0.0, v.x, 5);
    eq_prec(0.0, v.y, 5);
}

#[test]
fn an_attack_forward_command_has_no_velocity() {
    for attack in [0x1000_0062, 0x1000_0063, 0x1000_0064, 0x1000_0186] {
        let mut i = interp(Some(run_rate_weenie(RUN_RATE)));
        i.interp.interpreted_state.forward_command = attack;
        i.interp.interpreted_state.forward_speed = 0.97;
        assert_eq!(i.get_state_velocity(), Vec3::ZERO);
    }
}

// ── MotionTableDispatchSinkTests ──────────────────────────────────────────

const WALK_ANIM: u32 = 0x201;
const READY_ANIM: u32 = 0x200;

fn dispatch_sequencer(with_turn_modifier: bool) -> AnimationSequencer {
    let mut loader = MapLoader::default();
    loader.add(READY_ANIM, rest_anim(4, 1));
    loader.add(WALK_ANIM, rest_anim(6, 1));
    let mut table = TableBuilder::new(NON_COMBAT)
        .style_default(NON_COMBAT, READY)
        .cycle(NON_COMBAT, READY, motion_data(READY_ANIM, 30.0))
        .cycle(NON_COMBAT, WALK_FORWARD, motion_data(WALK_ANIM, 30.0));
    if with_turn_modifier {
        table = table.modifier(
            NON_COMBAT,
            TURN_RIGHT,
            with_omega(motion_data(READY_ANIM, 30.0), Vec3::new(0.0, 0.0, 1.2)),
        );
    }
    sequencer(table.rules(), loader)
}

#[test]
fn a_cycle_installs_the_substate_lazily() {
    let mut seq = dispatch_sequencer(true);
    seq.apply_motion(WALK_FORWARD, 2.0);
    assert_eq!(seq.current_motion(), WALK_FORWARD);
    assert_eq!(seq.current_speed_mod(), 2.0);
    assert_eq!(seq.current_velocity(), Vec3::ZERO);
}

#[test]
fn a_turn_is_a_modifier_over_the_cycle() {
    let mut seq = dispatch_sequencer(true);
    seq.apply_motion(WALK_FORWARD, 1.0);
    seq.apply_motion(TURN_RIGHT, 1.5);
    assert_eq!(seq.current_motion(), WALK_FORWARD);
    eq_prec(1.2 * 1.5, seq.current_omega().z, 3);
}

#[test]
fn stopping_the_turn_unwinds_its_spin() {
    let mut seq = dispatch_sequencer(true);
    seq.apply_motion(WALK_FORWARD, 1.0);
    seq.apply_motion(TURN_RIGHT, 1.5);
    seq.stop_motion(TURN_RIGHT);
    eq_prec(0.0, seq.current_omega().z, 3);
}

#[test]
fn stopping_the_substate_returns_to_the_default() {
    let mut seq = dispatch_sequencer(true);
    seq.apply_motion(WALK_FORWARD, 1.0);
    seq.stop_motion(WALK_FORWARD);
    assert_eq!(seq.current_motion(), READY);
}

#[test]
fn a_motion_missing_everywhere_changes_nothing() {
    let mut seq = dispatch_sequencer(false);
    seq.apply_motion(WALK_FORWARD, 1.0);
    seq.apply_motion(RUN_FORWARD, 2.0);
    assert_eq!(seq.current_motion(), WALK_FORWARD);
    assert_eq!(seq.current_speed_mod(), 1.0);
}
