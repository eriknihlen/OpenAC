//! Golden cases from OpenAC `CSequenceTests`, `CSequenceUpdateTests` and
//! `CSequencePhysicsTests` (`tests/AcDream.Core.Tests/Physics/Motion/`).

use std::f32::consts::PI;

use ac_dat::objects::{AnimationFrame, HookData};
use glam::{Quat, Vec3};

use super::*;
use crate::test_support::{
    BACKWARD, BOTH, FORWARD, MapLoader, anim_data, make_anim, set_pos_frames, sound_hook,
    uniform_anim,
};

#[test]
fn hooks_fire_only_in_their_direction() {
    let mut anim = x_anim(3);
    anim.part_frames[1].hooks.push(sound_hook(1, FORWARD));
    anim.part_frames[1].hooks.push(sound_hook(2, BACKWARD));
    let mut loader = MapLoader::default();
    loader.add(1, anim);

    let mut seq = Sequence::new(loader.clone().shared());
    seq.append_animation(&ad_rate(1, 30.0));
    let mut hooks = Recorder::default();
    seq.update(2.0 / 30.0, None, Some(&mut hooks));
    assert_eq!(hooks.0, ["hook:1:1"]);

    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&ad_rate(1, -30.0));
    let mut hooks = Recorder::default();
    // Backwards from 3.0 to 0.5: keyframes 2 and 1 are left.
    seq.update(2.5 / 30.0, None, Some(&mut hooks));
    assert_eq!(hooks.0, ["hook:2:-1"]);
}

fn x_anim(frames: usize) -> ac_dat::objects::Animation {
    make_anim(frames, 1, |f, _| {
        Frame::new(Vec3::new(f as f32, 0.0, 0.0), Quat::IDENTITY)
    })
}

fn new_seq(anims: &[(u32, usize)]) -> Sequence {
    let mut loader = MapLoader::default();
    for &(id, frames) in anims {
        loader.add(id, x_anim(frames));
    }
    Sequence::new(loader.shared())
}

fn ad(id: u32) -> AnimData {
    anim_data(id, 0, -1, 30.0)
}

fn ad_rate(id: u32, framerate: f32) -> AnimData {
    anim_data(id, 0, -1, framerate)
}

// ── append_animation ────────────────────────────────────────────────────

#[test]
fn append_first_seeds_cursor_and_frame_number() {
    let mut seq = new_seq(&[(1, 10)]);
    seq.append_animation(&anim_data(1, 3, -1, 30.0));
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim_index(), Some(0));
    assert_eq!(seq.first_cyclic_index(), Some(0));
    assert_eq!(seq.frame_number(), 3.0);
}

#[test]
fn append_slides_first_cyclic_to_the_new_tail_every_call() {
    let mut seq = new_seq(&[(1, 10), (2, 5), (3, 4)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.append_animation(&ad(3));
    assert_eq!(seq.count(), 3);
    assert_eq!(seq.first_cyclic().unwrap().high_frame, 3);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 9);
    assert_eq!(seq.frame_number(), 0.0);
}

#[test]
fn append_of_an_unresolvable_animation_is_discarded() {
    let mut seq = new_seq(&[(1, 10)]);
    seq.append_animation(&ad(999));
    assert_eq!(seq.count(), 0);
    assert!(seq.curr_anim().is_none());
    assert!(!seq.has_anims());
}

// ── remove family ───────────────────────────────────────────────────────

#[test]
fn remove_cyclic_with_cursor_outside_the_tail_keeps_the_cursor() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.remove_cyclic_anims();
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 9);
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
}

#[test]
fn remove_cyclic_with_cursor_inside_the_tail_snaps_back_to_the_previous_end() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.set_curr_anim_for_test(1);
    seq.remove_cyclic_anims();
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 9);
    assert_eq!(seq.frame_number(), 10.0);
}

#[test]
fn remove_cyclic_of_a_single_node_empties_and_zeroes() {
    let mut seq = new_seq(&[(1, 10)]);
    seq.append_animation(&ad(1));
    seq.remove_cyclic_anims();
    assert_eq!(seq.count(), 0);
    assert!(seq.curr_anim().is_none());
    assert!(seq.first_cyclic().is_none());
    assert_eq!(seq.frame_number(), 0.0);
}

#[test]
fn remove_link_animations_removes_the_predecessors_of_the_tail() {
    let mut seq = new_seq(&[(1, 10), (2, 5), (3, 4)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.append_animation(&ad(3));
    seq.remove_link_animations(1);
    assert_eq!(seq.count(), 2);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 9);
    assert_eq!(seq.first_cyclic().unwrap().high_frame, 3);
}

#[test]
fn remove_link_animations_moves_a_removed_cursor_to_the_tail_start() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.remove_link_animations(1);
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
    assert_eq!(seq.frame_number(), 0.0);
}

#[test]
fn remove_link_animations_with_a_negative_count_removes_nothing() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.remove_link_animations(-1);
    assert_eq!(seq.count(), 2);
}

#[test]
fn remove_all_link_animations_leaves_only_the_tail() {
    let mut seq = new_seq(&[(1, 10), (2, 5), (3, 4)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.append_animation(&ad(3));
    seq.remove_all_link_animations();
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
    assert_eq!(seq.curr_anim().unwrap().high_frame, 3);
}

#[test]
fn apricot_with_the_cursor_at_the_head_does_nothing() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.apricot();
    assert_eq!(seq.count(), 2);
}

#[test]
fn apricot_trims_consumed_heads_and_stops_at_the_cursor() {
    let mut seq = new_seq(&[(1, 10), (2, 5), (3, 4)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.append_animation(&ad(3));
    seq.set_curr_anim_for_test(1);
    seq.apricot();
    assert_eq!(seq.count(), 2);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 4);
}

#[test]
fn apricot_is_bounded_by_the_tail() {
    let mut seq = new_seq(&[(1, 10), (2, 5), (3, 4)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.append_animation(&ad(3));
    seq.set_curr_anim_for_test(2);
    seq.apricot();
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim().unwrap().high_frame, 3);
}

// ── accumulators, framerate, placement ──────────────────────────────────

#[test]
fn physics_set_combine_subtract() {
    let mut seq = new_seq(&[]);
    seq.set_velocity(Vec3::new(1.0, 2.0, 3.0));
    seq.set_omega(Vec3::new(0.0, 0.0, 1.0));
    seq.combine_physics(Vec3::new(1.0, 0.0, 0.0), Vec3::new(0.0, 0.0, 0.5));
    assert_eq!(seq.velocity(), Vec3::new(2.0, 2.0, 3.0));
    assert_eq!(seq.omega(), Vec3::new(0.0, 0.0, 1.5));
    seq.subtract_physics(Vec3::new(2.0, 2.0, 3.0), Vec3::new(0.0, 0.0, 1.5));
    assert_eq!(seq.velocity(), Vec3::ZERO);
    assert_eq!(seq.omega(), Vec3::ZERO);
}

#[test]
fn multiply_cyclic_framerate_touches_only_the_tail() {
    let mut seq = new_seq(&[(1, 10), (2, 5)]);
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq.multiply_cyclic_animation_framerate(2.0);
    assert_eq!(seq.curr_anim().unwrap().framerate, 30.0);
    assert_eq!(seq.first_cyclic().unwrap().framerate, 60.0);
    assert_eq!(seq.velocity(), Vec3::ZERO);
}

#[test]
fn clear_wipes_animations_physics_and_placement() {
    let mut seq = new_seq(&[(1, 10)]);
    seq.append_animation(&ad(1));
    seq.set_velocity(Vec3::ONE);
    seq.set_placement_frame(Some(AnimationFrame::default_for_test()), 0x65);
    seq.clear();
    assert_eq!(seq.count(), 0);
    assert!(seq.curr_anim().is_none());
    assert_eq!(seq.frame_number(), 0.0);
    assert_eq!(seq.velocity(), Vec3::ZERO);
    assert!(seq.placement_frame().is_none());
    assert_eq!(seq.placement_frame_id(), 0);
}

#[test]
fn curr_animframe_falls_back_to_the_placement_pose() {
    let mut seq = new_seq(&[]);
    let pf = AnimationFrame::default_for_test();
    seq.set_placement_frame(Some(pf.clone()), 0x65);
    assert_eq!(seq.curr_animframe(), Some(&pf));
}

#[test]
fn curr_animframe_floors_the_cursor() {
    let mut seq = new_seq(&[(1, 10)]);
    seq.append_animation(&ad(1));
    seq.set_frame_number(2.9);
    assert_eq!(seq.curr_animframe().unwrap().frames[0].origin.x, 2.0);
    assert_eq!(seq.curr_frame_number(), 2);
}

trait DefaultForTest {
    fn default_for_test() -> Self;
}

impl DefaultForTest for AnimationFrame {
    fn default_for_test() -> Self {
        AnimationFrame {
            frames: Vec::new(),
            hooks: Vec::new(),
        }
    }
}

// ── update ──────────────────────────────────────────────────────────────

/// Records hooks as "tag:direction" (the tag is the sound id) and finished
/// links as "ANIMDONE".
#[derive(Default)]
struct Recorder(Vec<String>);

impl AnimHookQueue for Recorder {
    fn add_anim_hook(&mut self, hook: &AnimationHook) {
        let HookData::Sound { id } = hook.data else {
            panic!("unexpected hook {hook:?}");
        };
        self.0.push(format!("hook:{id}:{}", hook.direction as i32));
    }

    fn add_anim_done_hook(&mut self) {
        self.0.push("ANIMDONE".into());
    }
}

/// `frames` keyframes, each with one both-ways hook tagged `base + frame`.
fn hooked_anim(frames: usize, base: u32, pos_frames: bool) -> ac_dat::objects::Animation {
    let mut anim = x_anim(frames);
    for (f, pf) in anim.part_frames.iter_mut().enumerate() {
        pf.hooks.push(sound_hook(base + f as u32, BOTH));
    }
    if pos_frames {
        set_pos_frames(&mut anim, |_| {
            Frame::new(Vec3::new(1.0, 0.0, 0.0), Quat::IDENTITY)
        });
    }
    anim
}

fn cyclic(frames: usize, framerate: f32) -> Sequence {
    let mut loader = MapLoader::default();
    loader.add(1, hooked_anim(frames, 0, false));
    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&ad_rate(1, framerate));
    seq
}

fn link_then_cycle() -> Sequence {
    let mut loader = MapLoader::default();
    loader.add(1, hooked_anim(2, 100, false));
    loader.add(2, hooked_anim(4, 200, false));
    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&ad(1));
    seq.append_animation(&ad(2));
    seq
}

#[test]
fn forward_single_tick_advances_one_frame_and_fires_the_exited_hook() {
    let mut seq = cyclic(5, 30.0);
    let mut hooks = Recorder::default();
    seq.update(1.0 / 30.0, None, Some(&mut hooks));
    assert!((seq.frame_number() - 1.0).abs() < 1e-9);
    assert_eq!(hooks.0, ["hook:0:0"]);
}

#[test]
fn forward_exact_boundary_landing_does_not_finish() {
    let mut seq = cyclic(5, 30.0);
    seq.set_frame_number(3.5);
    let mut hooks = Recorder::default();
    seq.update(0.5 / 30.0, None, Some(&mut hooks));
    assert!((seq.frame_number() - 4.0).abs() < 1e-9);
    assert_eq!(hooks.0, ["hook:3:0"]);
}

#[test]
fn forward_cyclic_wrap_restarts_without_anim_done() {
    let mut seq = cyclic(3, 30.0);
    seq.set_frame_number(2.0);
    let mut hooks = Recorder::default();
    seq.update(1.0 / 30.0, None, Some(&mut hooks));
    assert!(seq.frame_number().abs() < 1e-9);
    assert!(!hooks.0.contains(&"ANIMDONE".to_string()));
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
}

#[test]
fn forward_link_to_cycle_fires_anim_done_and_carries_the_leftover() {
    let mut seq = link_then_cycle();
    let mut hooks = Recorder::default();
    seq.update(0.1, None, Some(&mut hooks));
    assert_eq!(hooks.0, ["hook:100:0", "ANIMDONE", "hook:200:0"]);
    assert!((seq.frame_number() - 1.0).abs() < 1e-6);
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
}

#[test]
fn reverse_playback_fires_descending_hooks() {
    let mut seq = cyclic(5, -30.0);
    assert_eq!(seq.frame_number(), 5.0);
    let mut hooks = Recorder::default();
    for _ in 0..3 {
        seq.update(1.0 / 30.0, None, Some(&mut hooks));
    }
    assert!((seq.frame_number() - 2.0).abs() < 1e-9);
    assert_eq!(hooks.0, ["hook:4:0", "hook:3:0"]);
}

#[test]
fn reverse_advance_wraps_to_the_cyclic_tail() {
    let mut loader = MapLoader::default();
    loader.add(1, hooked_anim(3, 0, false));
    loader.add(2, hooked_anim(4, 10, false));
    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&ad_rate(1, -30.0));
    seq.append_animation(&ad(2));
    seq.set_frame_number(0.5);
    let mut hooks = Recorder::default();
    seq.update(0.02, None, Some(&mut hooks));
    assert_eq!(seq.curr_anim().unwrap().high_frame, 3);
}

fn pose_anim(x: f32) -> ac_dat::objects::Animation {
    let mut anim = uniform_anim(1, 1, Vec3::ZERO, Quat::IDENTITY);
    set_pos_frames(&mut anim, |_| {
        Frame::new(Vec3::new(x, 0.0, 0.0), Quat::IDENTITY)
    });
    anim
}

fn pose_pair(rate1: f32, rate2: f32) -> Sequence {
    let mut loader = MapLoader::default();
    loader.add(1, pose_anim(1.0));
    loader.add(2, pose_anim(2.0));
    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&anim_data(1, 0, 0, rate1));
    seq.append_animation(&anim_data(2, 0, 0, rate2));
    seq
}

#[test]
fn advance_with_positive_time_skips_a_forward_outgoing_pose() {
    let mut seq = pose_pair(30.0, 30.0);
    seq.set_frame_number(0.0);
    let mut frame = Frame::new(Vec3::new(10.0, 0.0, 0.0), Quat::IDENTITY);
    seq.advance_to_next_animation(0.1, Some(&mut frame));
    assert!((frame.origin.x - 12.0).abs() < 1e-5);
}

#[test]
fn advance_with_negative_time_skips_a_backward_outgoing_pose() {
    let mut seq = pose_pair(-30.0, -30.0);
    seq.set_frame_number(0.0);
    let mut frame = Frame::new(Vec3::new(10.0, 0.0, 0.0), Quat::IDENTITY);
    seq.advance_to_next_animation(-0.1, Some(&mut frame));
    assert!((frame.origin.x - 12.0).abs() < 1e-5);
}

#[test]
fn advance_at_exactly_epsilon_applies_the_pose_but_not_the_physics() {
    let mut seq = pose_pair(30.0, F_EPSILON);
    seq.set_velocity(Vec3::X);
    let mut frame = Frame::new(Vec3::new(10.0, 0.0, 0.0), Quat::IDENTITY);
    seq.advance_to_next_animation(0.1, Some(&mut frame));
    assert!((frame.origin.x - 12.0).abs() < 1e-5);
}

#[test]
fn zero_framerate_applies_physics_only() {
    let mut seq = cyclic(3, 0.0);
    seq.set_velocity(Vec3::new(2.0, 0.0, 0.0));
    let mut frame = Frame::IDENTITY;
    let mut hooks = Recorder::default();
    seq.update(0.5, Some(&mut frame), Some(&mut hooks));
    assert!((frame.origin.x - 1.0).abs() < 1e-5);
    assert!(hooks.0.is_empty());
    assert_eq!(seq.frame_number(), 0.0);
}

#[test]
fn empty_list_applies_the_accumulated_physics() {
    let mut seq = new_seq(&[]);
    seq.set_velocity(Vec3::new(0.0, 3.0, 0.0));
    let mut frame = Frame::IDENTITY;
    seq.update(2.0, Some(&mut frame), None);
    assert!((frame.origin.y - 6.0).abs() < 1e-5);
}

#[test]
fn position_frames_combine_into_the_callers_frame() {
    let mut loader = MapLoader::default();
    loader.add(1, hooked_anim(5, 0, true));
    let mut seq = Sequence::new(loader.shared());
    seq.append_animation(&ad(1));
    let mut frame = Frame::IDENTITY;
    seq.update(2.0 / 30.0, Some(&mut frame), None);
    assert!((frame.origin.x - 2.0).abs() < 1e-5);
    assert!((seq.frame_number() - 2.0).abs() < 1e-9);
}

#[test]
fn update_trims_consumed_link_nodes() {
    let mut seq = link_then_cycle();
    assert_eq!(seq.count(), 2);
    seq.update(0.1, None, None);
    assert_eq!(seq.count(), 1);
    assert_eq!(seq.curr_anim_index(), seq.first_cyclic_index());
}

#[test]
fn without_a_hook_queue_no_anim_done_is_reported_anywhere() {
    // Hooks are dropped, but the cursor still moves into the cycle.
    let mut seq = link_then_cycle();
    seq.update(0.1, None, None);
    assert!((seq.frame_number() - 1.0).abs() < 1e-6);
}

// ── apply_physics ───────────────────────────────────────────────────────

fn assert_vec(expected: Vec3, actual: Vec3) {
    assert!(
        (expected - actual).length() < 1e-5,
        "expected {expected}, got {actual}"
    );
}

#[test]
fn apply_physics_adds_velocity_times_quantum() {
    let mut seq = new_seq(&[]);
    seq.set_velocity(Vec3::new(2.0, 0.0, 0.0));
    let mut frame = Frame::new(Vec3::ONE, Quat::IDENTITY);
    seq.apply_physics(&mut frame, 0.5, 1.0);
    assert_vec(Vec3::new(2.0, 1.0, 1.0), frame.origin);
}

#[test]
fn apply_physics_takes_the_magnitude_from_quantum_and_the_sign_from_the_source() {
    let mut seq = new_seq(&[]);
    seq.set_velocity(Vec3::new(2.0, 0.0, 0.0));
    let mut f1 = Frame::IDENTITY;
    seq.apply_physics(&mut f1, -0.5, 1.0);
    assert_vec(Vec3::new(1.0, 0.0, 0.0), f1.origin);
    let mut f2 = Frame::IDENTITY;
    seq.apply_physics(&mut f2, 0.5, -1.0);
    assert_vec(Vec3::new(-1.0, 0.0, 0.0), f2.origin);
}

#[test]
fn apply_physics_spins_the_frame() {
    // π rad/s for half a second: a quarter turn, +x maps to +y.
    let mut seq = new_seq(&[]);
    seq.set_omega(Vec3::new(0.0, 0.0, PI));
    let mut frame = Frame::IDENTITY;
    seq.apply_physics(&mut frame, 0.5, 1.0);
    assert_vec(Vec3::Y, frame.orientation * Vec3::X);
}

#[test]
fn apply_physics_without_motion_changes_nothing() {
    let seq = new_seq(&[]);
    let mut frame = Frame::new(
        Vec3::new(3.0, 4.0, 5.0),
        Quat::from_axis_angle(Vec3::Y, 0.3),
    );
    let before = frame;
    seq.apply_physics(&mut frame, 1.0, 1.0);
    assert_eq!(frame, before);
}
