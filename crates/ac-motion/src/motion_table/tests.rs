//! Golden cases from OpenAC `CMotionTableTests`
//! (`tests/AcDream.Core.Tests/Physics/Motion/CMotionTableTests.cs`).

use std::f32::consts::PI;
use std::sync::Arc;

use glam::{Quat, Vec3};

use super::*;
use crate::test_support::{
    MapLoader, TableBuilder, anim_data, motion_data, uniform_anim, with_bitfield, with_omega,
    with_velocity,
};

const READY: u32 = 0x4100_0003;
const WALK_FORWARD: u32 = 0x4500_0005;
const RUN_FORWARD: u32 = 0x4400_0007;
const TURN_RIGHT: u32 = 0x6500_000D;
const JUMP: u32 = 0x2500_003B;
const THRUST_MED: u32 = 0x1000_0058;
const HAND_COMBAT: u32 = 0x8000_003C;
const NON_COMBAT: u32 = 0x8000_003D;

fn loader(anims: &[(u32, usize)]) -> MapLoader {
    let mut loader = MapLoader::default();
    for &(id, frames) in anims {
        loader.add(id, uniform_anim(frames, 1, Vec3::ZERO, Quat::IDENTITY));
    }
    loader
}

fn seq(loader: MapLoader) -> Sequence {
    Sequence::new(loader.shared())
}

fn empty_seq() -> Sequence {
    seq(MapLoader::default())
}

fn rules(builder: TableBuilder) -> MotionTableRules {
    MotionTableRules::new(Arc::new(builder.build()))
}

fn state(style: u32, substate: u32, substate_mod: f32) -> MotionState {
    let mut s = MotionState::default();
    s.style = style;
    s.substate = substate;
    s.substate_mod = substate_mod;
    s
}

#[test]
fn a_modifier_falls_back_to_the_style_independent_entry() {
    let omega = Vec3::new(0.0, 0.0, 2.0);
    let t = rules(
        nc().cycle(NON_COMBAT, READY, motion_data(0x0300_0120, 30.0))
            .global_modifier(JUMP, with_omega(motion_data(0, 0.0), omega)),
    );
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = seq(loader(&[(0x0300_0120, 4)]));
    t.do_object_motion(READY, &mut s, &mut sq, 1.0).unwrap();
    assert_eq!(
        t.get_object_sequence(JUMP, &mut s, &mut sq, 1.0, false),
        Some(0)
    );
    assert_eq!(sq.omega(), omega);
    assert_eq!(t.stop_sequence_motion(JUMP, 1.0, &mut s, &mut sq), Some(0));
    assert_eq!(sq.omega(), Vec3::ZERO);
}

fn nc() -> TableBuilder {
    TableBuilder::new(NON_COMBAT).style_default(NON_COMBAT, READY)
}

#[test]
fn ready_to_walk_builds_link_then_cycle() {
    let t = rules(
        nc().cycle(NON_COMBAT, READY, motion_data(0x0300_0001, 30.0))
            .cycle(NON_COMBAT, WALK_FORWARD, motion_data(0x0300_0002, 30.0))
            .link(
                NON_COMBAT,
                READY,
                WALK_FORWARD,
                motion_data(0x0300_0003, 30.0),
            ),
    );
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = seq(loader(&[
        (0x0300_0001, 4),
        (0x0300_0002, 4),
        (0x0300_0003, 2),
    ]));
    let ticks = t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, 1.0, false);
    assert_eq!(ticks, Some(1));
    assert_eq!(
        (s.style, s.substate, s.substate_mod),
        (NON_COMBAT, WALK_FORWARD, 1.0)
    );
    assert_eq!(sq.count(), 2);
    assert_eq!(sq.first_cyclic_index(), Some(1));
    assert_eq!(sq.curr_anim_index(), Some(0));
}

#[test]
fn same_substate_same_sign_takes_the_re_speed_fast_path() {
    let t = rules(nc().cycle(
        NON_COMBAT,
        WALK_FORWARD,
        with_velocity(motion_data(0x0300_0020, 30.0), Vec3::new(0.0, 3.12, 0.0)),
    ));
    let mut s = state(NON_COMBAT, WALK_FORWARD, 1.0);
    let mut sq = seq(loader(&[(0x0300_0020, 4)]));
    assert!(
        t.do_object_motion(WALK_FORWARD, &mut s, &mut sq, 1.0)
            .is_some()
    );
    let count = sq.count();
    let first_cyclic = sq.first_cyclic_index();
    let vel = sq.velocity();

    let ticks = t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, 2.0, false);
    assert_eq!(ticks, Some(0));
    assert_eq!(s.substate_mod, 2.0);
    assert_eq!(sq.count(), count);
    assert_eq!(sq.first_cyclic_index(), first_cyclic);
    assert_eq!(sq.velocity(), vel * 2.0);
}

#[test]
fn fast_path_rescales_framerates() {
    let t = rules(nc().cycle(NON_COMBAT, WALK_FORWARD, motion_data(0x0300_0021, 10.0)));
    let mut s = state(NON_COMBAT, WALK_FORWARD, 1.0);
    let mut sq = seq(loader(&[(0x0300_0021, 4)]));
    t.do_object_motion(WALK_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();
    let before = f64::from(sq.curr_anim().unwrap().framerate);
    t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, 2.5, false)
        .unwrap();
    let after = f64::from(sq.curr_anim().unwrap().framerate);
    assert!((after - before * 2.5).abs() < 1e-3);
}

#[test]
fn sign_flip_on_the_same_substate_routes_through_the_style_default() {
    let t = rules(
        nc().cycle(NON_COMBAT, WALK_FORWARD, motion_data(0x0300_0030, 30.0))
            .link(
                NON_COMBAT,
                WALK_FORWARD,
                READY,
                motion_data(0x0300_0031, 30.0),
            )
            .link(
                NON_COMBAT,
                READY,
                WALK_FORWARD,
                motion_data(0x0300_0032, 30.0),
            ),
    );
    let mut s = state(NON_COMBAT, WALK_FORWARD, 1.0);
    let mut sq = seq(loader(&[
        (0x0300_0030, 4),
        (0x0300_0031, 2),
        (0x0300_0032, 2),
    ]));
    add_motion(&mut sq, Some(&motion_data(0x0300_0030, 30.0)), 1.0);
    assert_eq!(sq.count(), 1);

    let ticks = t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, -1.0, false);
    assert!(ticks.is_some());
    assert_eq!(s.substate_mod, -1.0);
    assert_eq!(s.substate, WALK_FORWARD);
    assert_eq!(sq.count(), 3);
}

#[test]
fn style_change_builds_exit_link_style_link_and_new_cycle() {
    let t = rules(
        nc().style_default(HAND_COMBAT, READY)
            .cycle(NON_COMBAT, WALK_FORWARD, motion_data(0x0300_0043, 30.0))
            .link(
                NON_COMBAT,
                WALK_FORWARD,
                READY,
                motion_data(0x0300_0040, 30.0),
            )
            .link(
                NON_COMBAT,
                READY,
                HAND_COMBAT,
                motion_data(0x0300_0041, 30.0),
            )
            .cycle(HAND_COMBAT, READY, motion_data(0x0300_0042, 30.0)),
    );
    let mut s = state(NON_COMBAT, WALK_FORWARD, 1.0);
    // As in the OpenAC case, the walk cycle's animation does not resolve, so
    // the walk leaves only its exit link, which the style change replaces.
    let mut sq = seq(loader(&[
        (0x0300_0040, 2),
        (0x0300_0041, 2),
        (0x0300_0042, 4),
    ]));
    t.do_object_motion(WALK_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();
    assert!(
        t.get_object_sequence(HAND_COMBAT, &mut s, &mut sq, 1.0, false)
            .is_some()
    );
    assert_eq!((s.style, s.substate), (HAND_COMBAT, READY));
    assert_eq!(sq.count(), 3);
    let ids: Vec<u32> = sq.nodes().iter().map(|n| n.anim().unwrap().id).collect();
    assert_eq!(ids, [0x0300_0040, 0x0300_0041, 0x0300_0042]);
}

#[test]
fn style_change_to_the_current_style_is_a_no_op() {
    let t = rules(TableBuilder::new(NON_COMBAT));
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = empty_seq();
    assert_eq!(
        t.get_object_sequence(NON_COMBAT, &mut s, &mut sq, 1.0, false),
        Some(0)
    );
    assert_eq!(sq.count(), 0);
}

#[test]
fn action_with_a_direct_link_queues_the_action_and_re_adds_the_cycle() {
    let t = rules(
        nc().cycle(NON_COMBAT, RUN_FORWARD, motion_data(0x0300_0050, 30.0))
            .link(
                NON_COMBAT,
                RUN_FORWARD,
                THRUST_MED,
                motion_data(0x0300_0051, 30.0),
            ),
    );
    let mut s = state(NON_COMBAT, RUN_FORWARD, 1.0);
    let mut sq = seq(loader(&[(0x0300_0050, 4), (0x0300_0051, 3)]));
    t.do_object_motion(RUN_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();

    let ticks = t.get_object_sequence(THRUST_MED, &mut s, &mut sq, 1.0, false);
    assert_eq!(ticks, Some(1));
    assert_eq!(sq.count(), 2);
    assert_eq!(s.actions().len(), 1);
    assert_eq!(s.actions()[0].motion, THRUST_MED);
    assert_eq!(s.substate, RUN_FORWARD);
}

#[test]
fn action_without_a_direct_link_goes_out_and_back() {
    let t = rules(
        nc().cycle(NON_COMBAT, RUN_FORWARD, motion_data(0x0300_0060, 30.0))
            .link(
                NON_COMBAT,
                RUN_FORWARD,
                READY,
                motion_data(0x0300_0061, 30.0),
            )
            .link(
                NON_COMBAT,
                READY,
                THRUST_MED,
                motion_data(0x0300_0062, 30.0),
            )
            .link(
                NON_COMBAT,
                READY,
                RUN_FORWARD,
                motion_data(0x0300_0063, 30.0),
            ),
    );
    let mut s = state(NON_COMBAT, RUN_FORWARD, 1.5);
    let mut sq = seq(loader(&[
        (0x0300_0060, 4),
        (0x0300_0061, 2),
        (0x0300_0062, 3),
        (0x0300_0063, 2),
    ]));
    add_motion(&mut sq, Some(&motion_data(0x0300_0060, 30.0)), 1.5);
    assert_eq!(sq.count(), 1);

    let ticks = t.get_object_sequence(THRUST_MED, &mut s, &mut sq, 1.0, false);
    assert_eq!(ticks, Some(3));
    assert_eq!(sq.count(), 4);
    assert_eq!((s.substate, s.substate_mod), (RUN_FORWARD, 1.5));
    // The cycle comes back at the old speed: 30 × 1.5.
    assert!((sq.first_cyclic().unwrap().framerate - 45.0).abs() < 1e-3);
}

#[test]
fn turn_in_place_from_ready_resolves_as_a_cycle() {
    let t = rules(
        nc().cycle(NON_COMBAT, READY, motion_data(0x0300_0070, 30.0))
            .cycle(NON_COMBAT, TURN_RIGHT, motion_data(0x0300_0071, 30.0))
            .link(
                NON_COMBAT,
                READY,
                TURN_RIGHT,
                motion_data(0x0300_0072, 30.0),
            ),
    );
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = seq(loader(&[
        (0x0300_0070, 4),
        (0x0300_0071, 4),
        (0x0300_0072, 2),
    ]));
    assert!(
        t.get_object_sequence(TURN_RIGHT, &mut s, &mut sq, 1.0, false)
            .is_some()
    );
    assert_eq!(s.substate, TURN_RIGHT);
    assert_eq!(sq.count(), 2);
}

#[test]
fn turn_while_running_falls_to_the_modifier_and_only_adds_spin() {
    let run_velocity = Vec3::new(0.0, 4.0, 0.0);
    let turn_omega = Vec3::new(0.0, 0.0, -(PI / 2.0));
    let t = rules(
        nc().cycle(
            NON_COMBAT,
            RUN_FORWARD,
            with_velocity(motion_data(0x0300_0080, 30.0), run_velocity),
        )
        .cycle(
            NON_COMBAT,
            TURN_RIGHT,
            with_bitfield(motion_data(0x0300_0081, 30.0), 2),
        )
        .modifier(
            NON_COMBAT,
            TURN_RIGHT,
            with_omega(motion_data(0, 0.0), turn_omega),
        ),
    );
    let mut s = state(NON_COMBAT, RUN_FORWARD, 1.0);
    let mut sq = seq(loader(&[(0x0300_0080, 4), (0x0300_0081, 4)]));
    t.do_object_motion(RUN_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();
    let (count, cyclic, curr) = (sq.count(), sq.first_cyclic_index(), sq.curr_anim_index());

    assert_eq!(
        t.get_object_sequence(TURN_RIGHT, &mut s, &mut sq, 1.0, false),
        Some(0)
    );
    assert_eq!(sq.count(), count);
    assert_eq!(sq.first_cyclic_index(), cyclic);
    assert_eq!(sq.curr_anim_index(), curr);
    assert_eq!(s.substate, RUN_FORWARD);
    assert!(s.modifiers().iter().any(|m| m.motion == TURN_RIGHT));
    assert_eq!(sq.velocity(), run_velocity);
    assert_eq!(sq.omega(), turn_omega);
}

#[test]
fn stopping_a_modifier_subtracts_and_unlinks_it() {
    let jump_omega = Vec3::new(0.0, 0.0, 5.0);
    let t = rules(nc().modifier(
        NON_COMBAT,
        JUMP,
        with_omega(motion_data(0, 0.0), jump_omega),
    ));
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = empty_seq();
    s.add_modifier_no_check(JUMP, 1.0);
    sq.combine_physics(Vec3::ZERO, jump_omega);

    assert_eq!(t.stop_sequence_motion(JUMP, 1.0, &mut s, &mut sq), Some(0));
    assert_eq!(sq.omega(), Vec3::ZERO);
    assert!(!s.modifiers().iter().any(|m| m.motion == JUMP));
}

#[test]
fn stopping_an_unknown_modifier_fails() {
    let t = rules(TableBuilder::new(NON_COMBAT));
    let mut s = state(NON_COMBAT, READY, 1.0);
    assert_eq!(
        t.stop_sequence_motion(JUMP, 1.0, &mut s, &mut empty_seq()),
        None
    );
}

#[test]
fn stop_completely_drains_modifiers_then_returns_to_the_style_default() {
    let jump_omega = Vec3::new(1.0, 0.0, 0.0);
    let t = rules(
        nc().cycle(NON_COMBAT, RUN_FORWARD, motion_data(0x0300_0090, 30.0))
            .cycle(NON_COMBAT, READY, motion_data(0x0300_0091, 30.0))
            .link(
                NON_COMBAT,
                RUN_FORWARD,
                READY,
                motion_data(0x0300_0092, 30.0),
            )
            .modifier(
                NON_COMBAT,
                JUMP,
                with_omega(motion_data(0, 0.0), jump_omega),
            ),
    );
    let mut s = state(NON_COMBAT, RUN_FORWARD, 1.0);
    let mut sq = seq(loader(&[
        (0x0300_0090, 4),
        (0x0300_0091, 4),
        (0x0300_0092, 2),
    ]));
    t.do_object_motion(RUN_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();
    s.add_modifier_no_check(JUMP, 1.0);
    sq.combine_physics(Vec3::ZERO, jump_omega);

    assert!(t.stop_object_completely(&mut s, &mut sq).is_some());
    assert!(s.modifiers().is_empty());
    assert_eq!(s.substate, READY);
    assert_eq!(sq.omega(), Vec3::ZERO);
}

#[test]
fn stop_completely_gives_up_on_a_modifier_without_data() {
    // The retail loop would spin forever here; we stop trying. The looping
    // state's stop still reports success (its rebuild failing is ignored).
    let t = rules(nc());
    let mut s = state(NON_COMBAT, READY, 1.0);
    s.add_modifier_no_check(JUMP, 1.0);
    assert_eq!(t.stop_object_completely(&mut s, &mut empty_seq()), Some(0));
    assert_eq!(s.modifiers().len(), 1);
}

#[test]
fn a_missing_cycle_fails_and_touches_nothing() {
    let t = rules(nc().cycle(NON_COMBAT, READY, motion_data(0x0300_00A0, 30.0)));
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = seq(loader(&[(0x0300_00A0, 4)]));
    t.do_object_motion(READY, &mut s, &mut sq, 1.0).unwrap();
    let (count, vel, omega) = (sq.count(), sq.velocity(), sq.omega());

    assert_eq!(
        t.get_object_sequence(RUN_FORWARD, &mut s, &mut sq, 1.0, false),
        None
    );
    assert_eq!((sq.count(), sq.velocity(), sq.omega()), (count, vel, omega));
    assert_eq!(s.substate, READY);
}

#[test]
fn zero_style_or_substate_fails() {
    let t = rules(TableBuilder::new(0));
    let mut sq = empty_seq();
    let mut s = MotionState::default();
    assert_eq!(
        t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, 1.0, false),
        None
    );
    let mut s = state(NON_COMBAT, 0, 1.0);
    assert_eq!(
        t.get_object_sequence(WALK_FORWARD, &mut s, &mut sq, 1.0, false),
        None
    );
}

#[test]
fn modifier_class_target_equal_to_the_style_default_is_a_no_op() {
    let t = rules(TableBuilder::new(NON_COMBAT).style_default(NON_COMBAT, JUMP));
    let mut s = state(NON_COMBAT, JUMP, 1.0);
    let mut sq = empty_seq();
    assert_eq!(
        t.get_object_sequence(JUMP, &mut s, &mut sq, 1.0, false),
        Some(0)
    );
    assert_eq!(sq.count(), 0);
}

#[test]
fn re_modify_replays_active_modifiers() {
    let jump_omega = Vec3::new(0.0, 0.0, 9.0);
    let t = rules(
        nc().cycle(NON_COMBAT, RUN_FORWARD, motion_data(0x0300_00B0, 30.0))
            .modifier(
                NON_COMBAT,
                JUMP,
                with_omega(motion_data(0, 0.0), jump_omega),
            ),
    );
    let mut s = state(NON_COMBAT, RUN_FORWARD, 1.0);
    let mut sq = seq(loader(&[(0x0300_00B0, 4)]));
    t.do_object_motion(RUN_FORWARD, &mut s, &mut sq, 1.0)
        .unwrap();
    s.add_modifier_no_check(JUMP, 1.0);
    sq.clear_physics();

    t.re_modify(&mut sq, &mut s);
    assert_eq!(sq.omega(), jump_omega);
    assert!(s.modifiers().iter().any(|m| m.motion == JUMP));
}

#[test]
fn re_modify_with_no_modifiers_does_nothing() {
    let t = rules(TableBuilder::new(NON_COMBAT));
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = empty_seq();
    t.re_modify(&mut sq, &mut s);
    assert!(s.modifiers().is_empty());
    assert_eq!(sq.count(), 0);
}

#[test]
fn same_sign_matches_retail() {
    for (a, b, expected) in [
        (1.0, 1.0, true),
        (-1.0, -1.0, true),
        (1.0, -1.0, false),
        (-1.0, 1.0, false),
        (0.0, 1.0, true),
        (0.0, -1.0, false),
    ] {
        assert_eq!(same_sign(a, b), expected, "{a} {b}");
    }
}

fn one_anim_seq(rate: f32) -> Sequence {
    let mut sq = seq(loader(&[(0x0300_00C0, 4)]));
    sq.append_animation(&anim_data(0x0300_00C0, 0, -1, rate));
    sq
}

#[test]
fn change_cycle_speed_rescales_by_the_ratio() {
    let mut sq = one_anim_seq(10.0);
    change_cycle_speed(&mut sq, 1.0, 2.0);
    assert!((sq.curr_anim().unwrap().framerate - 20.0).abs() < 1e-3);
}

#[test]
fn change_cycle_speed_from_and_to_about_zero_zeroes_the_rate() {
    let mut sq = one_anim_seq(10.0);
    change_cycle_speed(&mut sq, 0.0001, 0.0001);
    assert_eq!(sq.curr_anim().unwrap().framerate, 0.0);
}

#[test]
fn change_cycle_speed_from_about_zero_to_nonzero_does_nothing() {
    let mut sq = one_anim_seq(10.0);
    change_cycle_speed(&mut sq, 0.0001, 5.0);
    assert_eq!(sq.curr_anim().unwrap().framerate, 10.0);
}

#[test]
fn add_motion_of_nothing_does_nothing() {
    let mut sq = empty_seq();
    add_motion(&mut sq, None, 1.0);
    assert_eq!(sq.count(), 0);
    assert_eq!(sq.velocity(), Vec3::ZERO);
}

#[test]
fn add_motion_overwrites_velocity_even_with_zero() {
    let mut sq = seq(loader(&[(0x0300_00D0, 2)]));
    sq.set_velocity(Vec3::splat(9.0));
    add_motion(&mut sq, Some(&motion_data(0x0300_00D0, 10.0)), 1.0);
    assert_eq!(sq.velocity(), Vec3::ZERO);
}

#[test]
fn combine_and_subtract_motion_change_physics_only() {
    let md = with_velocity(motion_data(0, 0.0), Vec3::new(2.0, 0.0, 0.0));
    let mut sq = empty_seq();
    sq.set_velocity(Vec3::new(1.0, 0.0, 0.0));
    combine_motion(&mut sq, Some(&md), 1.0);
    assert_eq!(sq.velocity(), Vec3::new(3.0, 0.0, 0.0));
    sq.set_velocity(Vec3::new(5.0, 0.0, 0.0));
    subtract_motion(&mut sq, Some(&md), 1.0);
    assert_eq!(sq.velocity(), Vec3::new(3.0, 0.0, 0.0));
    assert_eq!(sq.count(), 0);
}

#[test]
fn get_link_forward_and_reversed() {
    let link = motion_data(0x0300_00E0, 30.0);
    let t = rules(TableBuilder::new(0).link(NON_COMBAT, READY, WALK_FORWARD, link.clone()));
    assert_eq!(
        t.get_link(NON_COMBAT, READY, 1.0, WALK_FORWARD, 1.0),
        Some(&link)
    );

    // Either speed negative: the link stored from the target back to the
    // source.
    let reversed = motion_data(0x0300_00E1, 30.0);
    let t = rules(TableBuilder::new(0).link(NON_COMBAT, WALK_FORWARD, READY, reversed.clone()));
    assert_eq!(
        t.get_link(NON_COMBAT, READY, 1.0, WALK_FORWARD, -1.0),
        Some(&reversed)
    );
}

#[test]
fn get_link_forward_falls_back_to_the_stance_wide_link() {
    let link = motion_data(0x0300_00E2, 30.0);
    let t = rules(TableBuilder::new(0).link(NON_COMBAT, 0, WALK_FORWARD, link.clone()));
    assert_eq!(
        t.get_link(NON_COMBAT, RUN_FORWARD, 1.0, WALK_FORWARD, 1.0),
        Some(&link)
    );
}

#[test]
fn is_allowed() {
    let gated = with_bitfield(motion_data(0x0300_00F1, 30.0), 2);
    let ungated = motion_data(0x0300_00F0, 30.0);
    let t = rules(TableBuilder::new(0));
    assert!(t.is_allowed(WALK_FORWARD, Some(&ungated), &state(NON_COMBAT, READY, 1.0)));
    assert!(t.is_allowed(
        TURN_RIGHT,
        Some(&gated),
        &state(NON_COMBAT, TURN_RIGHT, 1.0)
    ));
    assert!(!t.is_allowed(TURN_RIGHT, None, &state(NON_COMBAT, READY, 1.0)));

    let t = rules(TableBuilder::new(0).style_default(NON_COMBAT, READY));
    assert!(t.is_allowed(TURN_RIGHT, Some(&gated), &state(NON_COMBAT, READY, 1.0)));
    assert!(!t.is_allowed(
        TURN_RIGHT,
        Some(&gated),
        &state(NON_COMBAT, RUN_FORWARD, 1.0)
    ));
}

#[test]
fn set_default_state_is_a_hard_reset() {
    let t = rules(nc().cycle(NON_COMBAT, READY, motion_data(0x0300_0100, 30.0)));
    let mut s = state(HAND_COMBAT, RUN_FORWARD, 3.0);
    s.add_modifier_no_check(JUMP, 1.0);
    s.add_action(THRUST_MED, 1.0);
    let mut sq = seq(loader(&[(0x0300_0100, 4), (0x0300_0101, 2)]));
    sq.append_animation(&anim_data(0x0300_0101, 0, -1, 5.0));

    assert_eq!(t.set_default_state(&mut s, &mut sq), Some(0));
    assert_eq!(
        (s.style, s.substate, s.substate_mod),
        (NON_COMBAT, READY, 1.0)
    );
    assert!(s.modifiers().is_empty());
    assert!(s.actions().is_empty());
    assert_eq!(sq.count(), 1);
}

#[test]
fn set_default_state_without_a_default_entry_fails() {
    let t = rules(TableBuilder::new(NON_COMBAT));
    let mut s = MotionState::default();
    assert_eq!(t.set_default_state(&mut s, &mut empty_seq()), None);
}

#[test]
fn do_and_stop_object_motion_delegate() {
    let t = rules(nc().cycle(NON_COMBAT, READY, motion_data(0x0300_0110, 30.0)));
    let mut s = state(NON_COMBAT, READY, 1.0);
    let mut sq = seq(loader(&[(0x0300_0110, 4)]));
    assert!(t.do_object_motion(READY, &mut s, &mut sq, 1.0).is_some());
    assert_eq!(sq.count(), 1);

    let jump_omega = Vec3::new(0.0, 0.0, 3.0);
    let t = rules(TableBuilder::new(NON_COMBAT).modifier(
        NON_COMBAT,
        JUMP,
        with_omega(motion_data(0, 0.0), jump_omega),
    ));
    let mut s = state(NON_COMBAT, READY, 1.0);
    s.add_modifier_no_check(JUMP, 1.0);
    let mut sq = empty_seq();
    sq.combine_physics(Vec3::ZERO, jump_omega);
    assert!(t.stop_object_motion(JUMP, 1.0, &mut s, &mut sq).is_some());
    assert_eq!(sq.omega(), Vec3::ZERO);
}

#[test]
fn a_cycle_with_no_animations_wraps_its_tick_count() {
    // An empty cycle asks for (0 - 1) completions as unsigned, as retail
    // does; the manager then never completes it by counting.
    let mut empty = motion_data(0, 30.0);
    empty.anims.clear();
    let t = rules(nc().cycle(NON_COMBAT, READY, empty));
    let mut s = MotionState::default();
    assert_eq!(
        t.set_default_state(&mut s, &mut empty_seq()),
        Some(u32::MAX)
    );
}
