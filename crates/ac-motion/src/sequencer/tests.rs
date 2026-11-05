//! Golden cases from OpenAC `AnimationSequencerTests`,
//! `AnimationSequencerCutoverTraceTests` and
//! `AnimationSequencerRootMotionOnlyTests`
//! (`tests/AcDream.Core.Tests/Physics/`).

use std::f32::consts::PI;
use std::fmt::Write as _;

use ac_dat::objects::{Animation, HookData};

use super::*;
use crate::manager::NoMotionDone;
use crate::sequence::ANIMATION_DONE_HOOK;
use crate::test_support::{
    BACKWARD, BOTH, FORWARD, MapLoader, TableBuilder, anim_data, make_anim, motion_data,
    set_pos_frames, sound_hook, uniform_anim, with_omega, with_velocity,
};

fn rules(builder: TableBuilder) -> Arc<MotionTableRules> {
    Arc::new(MotionTableRules::new(Arc::new(builder.build())))
}

fn sequencer(parts: usize, builder: TableBuilder, loader: MapLoader) -> AnimationSequencer {
    AnimationSequencer::new(parts, 0, rules(builder), loader.shared())
}

/// A table whose default stance `style` defaults to `motion`, with a cycle
/// for it and optionally one link.
fn make_mtable(
    style: u32,
    motion: u32,
    cycle_anim: u32,
    link: Option<(u32, u32, u32)>,
    framerate: f32,
) -> TableBuilder {
    let mut b = TableBuilder::new(style).style_default(style, motion).cycle(
        style,
        motion,
        motion_data(cycle_anim, framerate),
    );
    if let Some((from, to, link_anim)) = link {
        b = b.link(style, from, to, motion_data(link_anim, framerate));
    }
    b
}

fn uniform(frames: usize, origin: Vec3) -> Animation {
    uniform_anim(frames, 1, origin, Quat::IDENTITY)
}

fn loader(anims: Vec<(u32, Animation)>) -> MapLoader {
    let mut l = MapLoader::default();
    for (id, a) in anims {
        l.add(id, a);
    }
    l
}

fn span(anim_id: u32, low: i32, high: i32, framerate: f32) -> ac_dat::objects::MotionData {
    let mut md = motion_data(anim_id, framerate);
    md.anims = vec![anim_data(anim_id, low, high, framerate)];
    md
}

// ── slerp ───────────────────────────────────────────────────────────────

#[test]
fn slerp_matches_a_true_slerp_for_orthogonal_rotations() {
    let q1 = Quat::from_axis_angle(Vec3::Z, 0.0);
    let q2 = Quat::from_axis_angle(Vec3::Z, PI / 2.0);
    for t in [0.0, 0.25, 0.5, 0.75, 1.0] {
        let got = slerp_retail_client(q1, q2, t);
        let expected = Quat::from_axis_angle(Vec3::Z, t * PI / 2.0);
        assert!(
            (got - expected).length() < 1e-4,
            "t={t}: {got} vs {expected}"
        );
    }
}

#[test]
fn slerp_takes_the_short_arc_for_a_negative_dot() {
    let q1 = Quat::from_axis_angle(Vec3::Z, 0.1);
    let q2 = -q1;
    let got = slerp_retail_client(q1, q2, 0.0);
    assert!(!got.is_nan());
}

#[test]
fn slerp_of_equal_rotations_is_a_straight_blend() {
    let q = Quat::from_axis_angle(Vec3::Y, 0.3);
    assert!((slerp_retail_client(q, q, 0.5) - q).length() < 1e-4);
}

// ── construction, cycles, reset ─────────────────────────────────────────

#[test]
fn without_a_cycle_advance_returns_rest_poses() {
    let mut seq = sequencer(3, TableBuilder::new(0), MapLoader::default());
    let poses = seq.advance(0.033, None);
    assert_eq!(poses.len(), 3);
    assert!(poses.iter().all(|p| *p == PartTransform::IDENTITY));
}

#[test]
fn a_missing_cycle_leaves_sequence_and_state_untouched() {
    const STYLE: u32 = 0x8000_003C;
    const READY: u32 = 0x4100_0003;
    let mut seq = sequencer(
        2,
        make_mtable(STYLE, READY, 0x0300_0001, None, 30.0),
        loader(vec![(
            0x0300_0001,
            uniform_anim(2, 2, Vec3::ZERO, Quat::IDENTITY),
        )]),
    );
    seq.initialize_state();
    assert_eq!(seq.current_motion(), READY);
    let nodes = seq.queue_count();
    assert!(seq.set_cycle(STYLE, 0x4400_0007, 1.0).is_err());
    assert_eq!(seq.current_motion(), READY);
    assert_eq!(seq.queue_count(), nodes);
    assert!(seq.has_current_node());
}

#[test]
fn set_cycle_loads_the_animation() {
    let rot = Quat::from_axis_angle(Vec3::Z, 0.5);
    let anim = make_anim(2, 2, |f, _| {
        Frame::new(Vec3::new(1.0 + f as f32, 0.0, 0.0), rot)
    });
    let mut seq = sequencer(
        2,
        make_mtable(0x003D, 0x0003, 0x0300_0001, None, 30.0),
        loader(vec![(0x0300_0001, anim)]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    let poses = seq.advance(0.001, None);
    assert_eq!(poses.len(), 2);
    assert!((poses[0].orientation.z - rot.z).abs() < 0.1);
}

#[test]
fn the_setup_default_animation_plays_from_construction() {
    let anim = make_anim(2, 1, |f, _| {
        Frame::new(Vec3::new(2.0 * f as f32, 0.0, 0.0), Quat::IDENTITY)
    });
    let seq = AnimationSequencer::new(
        1,
        0x0300_AA01,
        rules(TableBuilder::new(0)),
        loader(vec![(0x0300_AA01, anim)]).shared(),
    );
    assert!(seq.has_current_node());
    let node = seq.sequence().curr_anim().unwrap();
    assert_eq!(
        (node.framerate, node.low_frame, node.high_frame),
        (30.0, 0, 1)
    );
}

#[test]
fn the_frame_wraps_at_the_high_frame() {
    let mut seq = sequencer(
        1,
        TableBuilder::new(0x003D)
            .style_default(0x003D, 0x0003)
            .cycle(0x003D, 0x0003, span(0x0300_0002, 0, 3, 10.0)),
        loader(vec![(0x0300_0002, uniform(4, Vec3::ZERO))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.advance(0.5, None);
    assert_eq!(seq.advance(0.01, None).len(), 1);
}

const NC: u32 = 0x8000_003D;
const IDLE: u32 = 0x4000_0003;
const WALK: u32 = 0x4000_0005;

/// Idle (1 frame) and a cycle reached from idle through a link.
fn idle_link_cycle(
    idle_anim: (u32, Animation),
    cycle_anim: (u32, Animation),
    link_anim: (u32, Animation),
    framerate: f32,
) -> AnimationSequencer {
    let table = make_mtable(
        NC,
        WALK,
        cycle_anim.0,
        Some((IDLE, WALK, link_anim.0)),
        framerate,
    )
    .style_default(NC, IDLE)
    .cycle(NC, IDLE, motion_data(idle_anim.0, framerate));
    sequencer(1, table, loader(vec![idle_anim, cycle_anim, link_anim]))
}

#[test]
fn a_transition_link_plays_before_the_cycle() {
    let mut seq = idle_link_cycle(
        (0x0300_0012, uniform(1, Vec3::ZERO)),
        (0x0300_0010, uniform(4, Vec3::new(1.0, 0.0, 0.0))),
        (0x0300_0011, uniform(2, Vec3::new(0.0, 1.0, 0.0))),
        30.0,
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.set_cycle(NC, WALK, 1.0).unwrap();
    let p = seq.advance(0.001, None)[0];
    assert!(p.origin.y > p.origin.x);
}

#[test]
fn entering_the_world_strips_the_ready_to_dead_transition() {
    const READY: u32 = 0x4100_0003;
    const DEAD: u32 = 0x4000_0011;
    let table = make_mtable(
        NC,
        DEAD,
        0x0300_0020,
        Some((READY, DEAD, 0x0300_0021)),
        30.0,
    )
    .style_default(NC, READY)
    .cycle(NC, READY, motion_data(0x0300_0022, 30.0));
    let mut seq = sequencer(
        1,
        table,
        loader(vec![
            (0x0300_0022, uniform(1, Vec3::ZERO)),
            (0x0300_0021, uniform(2, Vec3::new(0.0, 1.0, 0.0))),
            (0x0300_0020, uniform(1, Vec3::new(1.0, 0.0, 0.0))),
        ]),
    );
    seq.initialize_state();
    seq.set_cycle(NC, DEAD, 1.0).unwrap();
    let p = seq.advance(0.001, None)[0];
    assert!(p.origin.y > p.origin.x, "the link exists before entering");

    seq.handle_enter_world(&mut NoMotionDone);
    let p = seq.advance(0.001, None)[0];
    assert!(
        p.origin.x > p.origin.y,
        "the corpse starts on the dead cycle"
    );
    assert_eq!(seq.queue_count(), 1);
}

#[test]
fn a_link_tail_holds_its_last_frame() {
    let link = make_anim(3, 1, |f, _| {
        Frame::new(Vec3::new(0.0, 10.0 - 5.0 * f as f32, 0.0), Quat::IDENTITY)
    });
    let mut seq = idle_link_cycle(
        (0x0300_0082, uniform(1, Vec3::ZERO)),
        (0x0300_0080, uniform(1, Vec3::ZERO)),
        (0x0300_0081, link),
        30.0,
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.set_cycle(NC, WALK, 1.0).unwrap();
    seq.advance(0.0833, None);
    let pos = seq.sequence().frame_number();
    assert!((2.4..=2.7).contains(&pos), "{pos}");
    let p = seq.advance(0.0001, None)[0];
    assert!(
        p.origin.y < 1.0,
        "held at the last frame, got {}",
        p.origin.y
    );
}

#[test]
fn a_cyclic_seam_holds_the_last_frame() {
    let anim = make_anim(4, 1, |f, _| {
        Frame::new(
            Vec3::ZERO,
            Quat::from_axis_angle(Vec3::Y, PI / 6.0 * f as f32),
        )
    });
    let mut seq = sequencer(
        1,
        TableBuilder::new(0x003D)
            .style_default(0x003D, 0x0003)
            .cycle(0x003D, 0x0003, span(0x0300_0002, 0, 3, 10.0)),
        loader(vec![(0x0300_0002, anim)]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.advance(0.35, None);
    let q = seq.advance(0.0001, None)[0].orientation;
    let pos = seq.sequence().frame_number();
    assert!((3.4..=3.7).contains(&pos), "{pos}");
    let angle = 2.0 * q.y.abs().atan2(q.w.abs()) * 180.0 / PI;
    assert!((89.0..=91.0).contains(&angle), "{angle}");
}

#[test]
fn stopping_from_walk_backward_uses_the_reversed_windup() {
    const WALK_FORWARD: u32 = 0x4000_0005;
    const READY: u32 = 0x4000_0003;
    let table = make_mtable(
        NC,
        READY,
        0x0300_0090,
        Some((READY, WALK_FORWARD, 0x0300_0091)),
        30.0,
    )
    .cycle(NC, WALK_FORWARD, motion_data(0x0300_0090, 30.0));
    let mut seq = sequencer(
        1,
        table,
        loader(vec![
            (0x0300_0090, uniform(1, Vec3::ZERO)),
            (0x0300_0091, uniform(4, Vec3::new(0.0, 7.0, 0.0))),
        ]),
    );
    // Walking backward reaches the table as walking forward at -0.65 (the
    // interpreter's adjustment).
    seq.set_cycle(NC, WALK_FORWARD, -0.65).unwrap();
    seq.set_cycle(NC, READY, 1.0).unwrap();
    let p = seq.advance(0.001, None)[0];
    assert!(p.origin.y > 5.0, "got {}", p.origin.y);
}

#[test]
fn a_cycle_without_a_link_switches_directly() {
    let mut seq = sequencer(
        1,
        make_mtable(0x003D, 0x0003, 0x0300_0020, None, 30.0),
        loader(vec![(0x0300_0020, uniform(3, Vec3::new(5.0, 0.0, 0.0)))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    assert!(seq.advance(0.001, None)[0].origin.x > 4.0);
}

#[test]
fn the_same_cycle_twice_changes_nothing() {
    let mut seq = sequencer(
        1,
        make_mtable(0x003D, 0x0003, 0x0300_0030, None, 30.0),
        loader(vec![(0x0300_0030, uniform(4, Vec3::ZERO))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.advance(0.1, None);
    let before = seq.sequence().frame_number();
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    assert_eq!(seq.sequence().frame_number(), before);
}

#[test]
fn reset_clears_everything() {
    let mut seq = sequencer(
        1,
        make_mtable(0x003D, 0x0003, 0x0300_0040, None, 30.0),
        loader(vec![(0x0300_0040, uniform(4, Vec3::ONE))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.advance(0.2, None);
    let mut done = Vec::new();
    seq.reset(&mut done);
    assert_eq!((seq.current_style(), seq.current_motion()), (0, 0));
    assert!(
        seq.advance(0.033, None)
            .iter()
            .all(|p| *p == PartTransform::IDENTITY)
    );
}

// ── negative speeds ─────────────────────────────────────────────────────

#[test]
fn turn_right_at_negative_speed_starts_at_the_reverse_end() {
    const TURN_RIGHT: u32 = 0x4045_000D;
    let anim = make_anim(4, 1, |f, _| {
        Frame::new(Vec3::new(0.0, 0.0, f as f32), Quat::IDENTITY)
    });
    let mut seq = sequencer(
        1,
        make_mtable(NC, TURN_RIGHT, 0x0300_0050, None, 10.0),
        loader(vec![(0x0300_0050, anim)]),
    );
    // Turning left reaches the table as turning right at -1 (the
    // interpreter's adjustment).
    seq.set_cycle(NC, TURN_RIGHT, -1.0).unwrap();
    assert_eq!(seq.current_motion(), TURN_RIGHT);
    assert_eq!(seq.current_speed_mod(), -1.0);
    // Reverse start is the bare high frame + 1.
    assert_eq!(seq.sequence().frame_number(), 4.0);
}

#[test]
fn negative_framerate_moves_the_cursor_backwards_and_wraps() {
    let mut seq = sequencer(
        1,
        TableBuilder::new(NC).style_default(NC, IDLE).cycle(
            NC,
            IDLE,
            span(0x0300_0060, 0, 7, -10.0),
        ),
        loader(vec![(0x0300_0060, uniform(8, Vec3::ZERO))]),
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    let before = seq.sequence().frame_number();
    seq.advance(0.05, None);
    assert!(seq.sequence().frame_number() < before);

    let mut seq = sequencer(
        1,
        TableBuilder::new(0x003D)
            .style_default(0x003D, 0x0003)
            .cycle(0x003D, 0x0003, span(0x0300_0070, 0, 3, -10.0)),
        loader(vec![(0x0300_0070, uniform(4, Vec3::ZERO))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.advance(0.5, None);
    assert_eq!(seq.advance(0.01, None).len(), 1);
    let pos = seq.sequence().frame_number();
    assert!((0.0..4.0).contains(&pos), "{pos}");
}

#[test]
fn the_link_drains_then_the_cycle_loops() {
    let mut seq = idle_link_cycle(
        (0x0300_0082, uniform(1, Vec3::ZERO)),
        (0x0300_0080, uniform(4, Vec3::new(9.0, 0.0, 0.0))),
        (0x0300_0081, uniform(2, Vec3::new(0.0, 5.0, 0.0))),
        10.0,
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.set_cycle(NC, WALK, 1.0).unwrap();
    seq.advance(0.25, None);
    assert!(seq.advance(0.001, None)[0].origin.x > 8.0);

    let mut seq = sequencer(
        1,
        make_mtable(0x003D, 0x0003, 0x0300_0090, None, 10.0),
        loader(vec![(0x0300_0090, uniform(4, Vec3::X))]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    for _ in 0..10 {
        seq.advance(0.2, None);
    }
    assert_eq!(seq.advance(0.001, None).len(), 1);
    let pos = seq.sequence().frame_number();
    assert!((0.0..4.0).contains(&pos), "{pos}");
}

// ── hooks ───────────────────────────────────────────────────────────────

fn hooked_cycle(
    style: u32,
    motion: u32,
    hooks: &[(usize, u32, u32)],
    md: ac_dat::objects::MotionData,
) -> AnimationSequencer {
    let mut anim = uniform(4, Vec3::ZERO);
    for &(frame, id, dir) in hooks {
        anim.part_frames[frame].hooks.push(sound_hook(id, dir));
    }
    let anim_id = md.anims[0].anim_id;
    sequencer(
        1,
        TableBuilder::new(style)
            .style_default(style, motion)
            .cycle(style, motion, md),
        loader(vec![(anim_id, anim)]),
    )
}

fn sound_ids(hooks: &[AnimationHook]) -> Vec<u32> {
    hooks
        .iter()
        .filter_map(|h| match h.data {
            HookData::Sound { id } => Some(id),
            _ => None,
        })
        .collect()
}

#[test]
fn a_hook_fires_only_when_its_frame_is_left() {
    let mut seq = hooked_cycle(
        0x003D,
        0x0003,
        &[(1, 0x0A00_0042, FORWARD)],
        motion_data(0x0300_0100, 10.0),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.consume_pending_hooks();
    seq.advance(0.05, None);
    assert!(seq.consume_pending_hooks().is_empty());
}

#[test]
fn forward_hooks_fire_on_the_crossed_frames() {
    let mut seq = hooked_cycle(
        0x003D,
        0x0003,
        &[(0, 0x0A00_0001, FORWARD), (2, 0x0A00_0002, FORWARD)],
        motion_data(0x0300_0101, 10.0),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.consume_pending_hooks();
    seq.advance(0.15, None);
    assert_eq!(sound_ids(&seq.consume_pending_hooks()), [0x0A00_0001]);
    seq.advance(0.2, None);
    assert_eq!(sound_ids(&seq.consume_pending_hooks()), [0x0A00_0002]);
}

#[test]
fn both_way_hooks_fire_going_forwards() {
    let mut seq = hooked_cycle(
        0x003D,
        0x0003,
        &[(0, 0x0A00_0003, BOTH)],
        motion_data(0x0300_0102, 10.0),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    seq.consume_pending_hooks();
    seq.advance(0.15, None);
    assert_eq!(seq.consume_pending_hooks().len(), 1);
}

#[test]
fn direction_filters_hooks_in_reverse_playback() {
    let mut seq = hooked_cycle(
        NC,
        IDLE,
        &[(2, 0x0A00_0004, FORWARD)],
        span(0x0300_0103, 0, 3, -10.0),
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.consume_pending_hooks();
    seq.advance(0.25, None);
    assert!(!sound_ids(&seq.consume_pending_hooks()).contains(&0x0A00_0004));

    let mut seq = hooked_cycle(
        NC,
        IDLE,
        &[(2, 0x0A00_0005, BACKWARD)],
        span(0x0300_0104, 0, 3, -10.0),
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.consume_pending_hooks();
    seq.advance(0.25, None);
    assert!(sound_ids(&seq.consume_pending_hooks()).contains(&0x0A00_0005));
}

#[test]
fn a_draining_link_queues_an_animation_done_hook() {
    let mut seq = idle_link_cycle(
        (0x0300_0132, uniform(1, Vec3::ZERO)),
        (0x0300_0130, uniform(4, Vec3::ZERO)),
        (0x0300_0131, uniform(2, Vec3::ZERO)),
        10.0,
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.set_cycle(NC, WALK, 1.0).unwrap();
    seq.consume_pending_hooks();
    seq.advance(0.25, None);
    assert!(seq.consume_pending_hooks().contains(&ANIMATION_DONE_HOOK));
}

// ── root motion, velocity, spin ─────────────────────────────────────────

#[test]
fn position_frames_accumulate_into_the_root_frame() {
    let mut anim = uniform(4, Vec3::ZERO);
    set_pos_frames(&mut anim, |_| Frame::new(Vec3::X, Quat::IDENTITY));
    let mut seq = sequencer(
        1,
        make_mtable(0x003D, 0x0003, 0x0300_0110, None, 10.0),
        loader(vec![(0x0300_0110, anim)]),
    );
    let _ = seq.set_cycle(0x003D, 0x0003, 1.0);
    let mut root = Frame::IDENTITY;
    seq.advance(0.25, Some(&mut root));
    assert!((1.8..=2.2).contains(&root.origin.x), "{}", root.origin.x);
}

fn velocity_cycle(motion: u32, md: ac_dat::objects::MotionData) -> AnimationSequencer {
    let anim_id = md.anims[0].anim_id;
    sequencer(
        1,
        TableBuilder::new(NC)
            .style_default(NC, motion)
            .cycle(NC, motion, md),
        loader(vec![(anim_id, uniform(4, Vec3::ZERO))]),
    )
}

#[test]
fn velocity_comes_from_the_motion_data_scaled_by_speed() {
    let md = with_velocity(span(0x0300_0120, 0, 3, 10.0), Vec3::new(0.0, 4.0, 0.0));
    let mut seq = velocity_cycle(IDLE, md.clone());
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    assert_eq!(seq.current_velocity(), Vec3::new(0.0, 4.0, 0.0));

    let mut seq = velocity_cycle(IDLE, md);
    seq.set_cycle(NC, IDLE, 0.5).unwrap();
    assert_eq!(seq.current_velocity(), Vec3::new(0.0, 2.0, 0.0));
}

#[test]
fn locomotion_without_authored_velocity_has_none() {
    let mut seq = velocity_cycle(0x4000_0007, motion_data(0x0300_0406, 10.0));
    seq.set_cycle(NC, 0x4000_0007, 2.0).unwrap();
    assert_eq!(seq.current_velocity(), Vec3::ZERO);
}

#[test]
fn re_speeding_a_cycle_rescales_rate_and_velocity_and_keeps_the_cursor() {
    const RUN: u32 = 0x4000_0007;
    let anim = make_anim(10, 1, |f, _| {
        Frame::new(Vec3::new(0.0, 0.0, f as f32), Quat::IDENTITY)
    });
    let md = with_velocity(span(0x0300_0401, 0, 9, 10.0), Vec3::new(0.0, 4.0, 0.0));
    let table = TableBuilder::new(NC)
        .style_default(NC, RUN)
        .cycle(NC, RUN, md);
    let l = loader(vec![(0x0300_0401, anim)]);

    // Half speed: one second plays five keyframes.
    let mut seq = sequencer(1, table.clone(), l.clone());
    seq.set_cycle(NC, RUN, 1.0).unwrap();
    seq.set_cycle(NC, RUN, 0.5).unwrap();
    seq.advance(1.0, None);
    let z = seq.advance(0.001, None)[0].origin.z;
    assert!((4.0..=6.0).contains(&z), "{z}");
    assert!((seq.current_velocity().y - 2.0).abs() < 0.05);

    // The cursor stays where it was.
    let mut seq = sequencer(1, table, l);
    seq.set_cycle(NC, RUN, 1.0).unwrap();
    seq.advance(0.3, None);
    let mid = seq.sequence().frame_number();
    seq.set_cycle(NC, RUN, 2.0).unwrap();
    assert!((seq.sequence().frame_number() - mid).abs() < 1e-5);
    assert_eq!(seq.current_speed_mod(), 2.0);
}

#[test]
fn velocity_scales_with_every_speed_change() {
    const RUN: u32 = 0x4000_0007;
    let md = with_velocity(motion_data(0x0300_0405, 10.0), Vec3::new(0.0, 4.0, 0.0));
    let mut seq = velocity_cycle(RUN, md.clone());
    seq.set_cycle(NC, RUN, 1.0).unwrap();
    assert!((seq.current_velocity().y - 4.0).abs() < 1e-3);
    let mut seq = velocity_cycle(RUN, md);
    seq.set_cycle(NC, RUN, 1.5).unwrap();
    assert!((seq.current_velocity().y - 6.0).abs() < 1e-3);
    seq.set_cycle(NC, RUN, 0.5).unwrap();
    assert!((seq.current_velocity().y - 2.0).abs() < 1e-2);
}

#[test]
fn the_same_cycle_at_the_same_speed_stays_put() {
    const RUN: u32 = 0x4000_0007;
    let mut seq = velocity_cycle(RUN, motion_data(0x0300_0404, 30.0));
    seq.set_cycle(NC, RUN, 1.5).unwrap();
    seq.advance(0.2, None);
    let before = seq.sequence().frame_number();
    seq.set_cycle(NC, RUN, 1.5).unwrap();
    assert!((seq.sequence().frame_number() - before).abs() < 1e-5);
    assert_eq!(seq.current_speed_mod(), 1.5);
}

#[test]
fn spin_scales_with_speed() {
    let md = with_omega(motion_data(0x0300_0701, 10.0), Vec3::new(0.0, 0.0, 1.0));
    let mut seq = velocity_cycle(0x4000_000D, md);
    seq.set_cycle(NC, 0x4000_000D, 2.0).unwrap();
    assert!((seq.current_omega().z - 2.0).abs() < 1e-3);
}

#[test]
fn velocity_persists_through_the_link() {
    let table = TableBuilder::new(NC)
        .style_default(NC, IDLE)
        .cycle(NC, IDLE, motion_data(0x0300_0603, 10.0))
        .cycle(
            NC,
            WALK,
            with_velocity(motion_data(0x0300_0601, 10.0), Vec3::new(0.0, 3.12, 0.0)),
        )
        .link(NC, IDLE, WALK, motion_data(0x0300_0602, 10.0));
    let mut seq = sequencer(
        1,
        table,
        loader(vec![
            (0x0300_0601, uniform(4, Vec3::ZERO)),
            (0x0300_0602, uniform(2, Vec3::ZERO)),
            (0x0300_0603, uniform(1, Vec3::ZERO)),
        ]),
    );
    seq.set_cycle(NC, IDLE, 1.0).unwrap();
    seq.set_cycle(NC, WALK, 1.0).unwrap();
    assert!((seq.current_velocity().y - 3.12).abs() < 1e-2);
    seq.advance(0.25, None);
    assert!((seq.current_velocity().y - 3.12).abs() < 1e-2);
}

// ── actions and modifiers ───────────────────────────────────────────────

fn idle_with_link(style: u32, action: u32, action_anim: Animation) -> AnimationSequencer {
    const READY: u32 = 0x4100_0003;
    let table = TableBuilder::new(style)
        .style_default(style, READY)
        .cycle(style, READY, motion_data(0x0300_0501, 10.0))
        .link(style, READY, action, motion_data(0x0300_0502, 10.0));
    let mut seq = sequencer(
        1,
        table,
        loader(vec![
            (0x0300_0501, uniform(4, Vec3::ZERO)),
            (0x0300_0502, action_anim),
        ]),
    );
    seq.set_cycle(style, READY, 1.0).unwrap();
    seq
}

#[test]
fn an_action_plays_its_link() {
    let mut seq = idle_with_link(
        0x8000_003E,
        0x1000_0058,
        uniform(3, Vec3::new(99.0, 0.0, 0.0)),
    );
    seq.advance(0.01, None);
    seq.play_action(0x1000_0058, 1.0).unwrap();
    assert!((seq.advance(0.01, None)[0].origin.x - 99.0).abs() < 0.1);
}

#[test]
fn an_action_survives_an_immediate_ready_echo() {
    let mut seq = idle_with_link(NC, 0x1000_0052, uniform(3, Vec3::new(12.0, 0.0, 0.0)));
    seq.play_action(0x1000_0052, 1.0).unwrap();
    seq.set_cycle(NC, 0x4100_0003, 1.0).unwrap();
    assert!((seq.advance(0.01, None)[0].origin.x - 12.0).abs() < 0.1);
    assert_eq!(seq.current_motion(), 0x4100_0003);
}

#[test]
fn an_emote_routes_through_the_action_branch() {
    let mut seq = idle_with_link(NC, 0x1300_0087, uniform(5, Vec3::new(0.0, 55.0, 0.0)));
    seq.play_action(0x1300_0087, 1.0).unwrap();
    assert!((seq.advance(0.01, None)[0].origin.y - 55.0).abs() < 0.1);
}

#[test]
fn a_modifier_adds_spin_without_queueing_animations() {
    const READY: u32 = 0x4100_0003;
    const JUMP: u32 = 0x2500_003B;
    let table = TableBuilder::new(NC)
        .style_default(NC, READY)
        .cycle(NC, READY, motion_data(0x0300_0510, 10.0))
        .modifier(
            NC,
            JUMP,
            with_omega(motion_data(0x0300_0511, 10.0), Vec3::new(0.0, 0.0, 2.5)),
        );
    let mut seq = sequencer(
        1,
        table,
        loader(vec![
            (0x0300_0510, uniform(4, Vec3::ZERO)),
            (0x0300_0511, uniform(3, Vec3::new(0.0, 0.0, 77.0))),
        ]),
    );
    seq.set_cycle(NC, READY, 1.0).unwrap();
    let before = seq.queue_count();
    seq.play_action(JUMP, 1.0).unwrap();
    assert_eq!(seq.queue_count(), before);
    assert!((seq.current_omega().z - 2.5).abs() < 1e-3);
}

#[test]
fn an_unknown_action_is_refused_and_changes_nothing() {
    let mut seq = sequencer(
        1,
        TableBuilder::new(0x003D).cycle(0x003D, 0x4100_0003, motion_data(0x0300_0530, 10.0)),
        loader(vec![(0x0300_0530, uniform(4, Vec3::ZERO))]),
    );
    let _ = seq.set_cycle(0x003D, 0x4100_0003, 1.0);
    seq.advance(0.05, None);
    let before = seq.queue_count();
    assert!(seq.play_action(0x1000_1234, 1.0).is_err());
    assert_eq!(seq.queue_count(), before);
}

#[test]
fn the_default_animation_keeps_physics_and_placement() {
    let mut seq = sequencer(
        1,
        TableBuilder::new(0),
        loader(vec![(0x0300_0531, uniform(2, Vec3::ZERO))]),
    );
    let placement = ac_dat::objects::AnimationFrame {
        frames: Vec::new(),
        hooks: Vec::new(),
    };
    seq.sequence_mut().set_velocity(Vec3::new(1.0, 2.0, 3.0));
    seq.sequence_mut().set_omega(Vec3::new(0.1, 0.2, 0.3));
    seq.sequence_mut()
        .set_placement_frame(Some(placement.clone()), 0x1234);
    assert!(seq.initialize_setup_default_animation(0x0300_0531));
    assert_eq!(seq.current_velocity(), Vec3::new(1.0, 2.0, 3.0));
    assert_eq!(seq.current_omega(), Vec3::new(0.1, 0.2, 0.3));
    assert_eq!(seq.sequence().placement_frame(), Some(&placement));
    assert_eq!(seq.sequence().placement_frame_id(), 0x1234);
}

// ── trace conformance (AnimationSequencerCutoverTraceTests) ─────────────

mod trace {
    use super::*;

    const STYLE2: u32 = 0x8000_004C;
    const READY: u32 = 0x4100_0003;
    const WALK: u32 = 0x4500_0005;
    const RUN: u32 = 0x4400_0007;
    const FALLING: u32 = 0x4000_0015;
    const EMOTE_ACTION: u32 = 0x1000_0062;
    const TURN_MOD: u32 = 0x6500_000D;

    fn new_seq() -> AnimationSequencer {
        let mut l = MapLoader::default();
        for (id, frames) in [
            (0x100, 4),
            (0x101, 6),
            (0x102, 6),
            (0x103, 2),
            (0x104, 2),
            (0x105, 3),
            (0x108, 3),
            (0x109, 2),
            (0x10A, 2),
            (0x10B, 5),
            (0x10C, 4),
            (0x107, 4),
        ] {
            l.add(id, uniform(frames, Vec3::ZERO));
        }
        let table = TableBuilder::new(NC)
            .style_default(NC, READY)
            .style_default(STYLE2, READY)
            .cycle(NC, READY, motion_data(0x100, 30.0))
            .cycle(
                NC,
                WALK,
                with_velocity(motion_data(0x101, 30.0), Vec3::new(0.0, 3.12, 0.0)),
            )
            .cycle(
                NC,
                RUN,
                with_velocity(motion_data(0x102, 30.0), Vec3::new(0.0, 4.0, 0.0)),
            )
            .cycle(NC, FALLING, motion_data(0x10C, 30.0))
            .cycle(STYLE2, READY, motion_data(0x107, 30.0))
            .link(NC, READY, WALK, motion_data(0x103, 30.0))
            .link(NC, READY, RUN, motion_data(0x104, 30.0))
            .link(NC, WALK, READY, motion_data(0x105, 30.0))
            .link(NC, RUN, READY, motion_data(0x108, 30.0))
            .link(NC, READY, EMOTE_ACTION, motion_data(0x10B, 30.0))
            .link(NC, READY, STYLE2, motion_data(0x10A, 30.0))
            .modifier(
                NC,
                TURN_MOD,
                with_omega(motion_data(0x109, 30.0), Vec3::new(0.0, 0.0, 1.5)),
            );
        sequencer(1, table, l)
    }

    fn describe(seq: &AnimationSequencer) -> String {
        let core = seq.sequence();
        let mut s = String::new();
        for (i, n) in core.nodes().iter().enumerate() {
            if i > 0 {
                s.push(',');
            }
            let id = n.anim().map_or(0, |a| a.id);
            write!(s, "{id:X}@{:.1}", n.framerate).unwrap();
            if core.first_cyclic_index() == Some(i) {
                s.push('*');
            }
            if core.curr_anim_index() == Some(i) {
                s.push('^');
            }
        }
        let (v, o) = (core.velocity(), core.omega());
        write!(
            s,
            " | frame={:.1} vel=({:.2},{:.2},{:.2}) om=({:.2},{:.2},{:.2}) style={:08X} motion={:08X} mod={:.2}",
            core.frame_number(),
            v.x,
            v.y,
            v.z,
            o.x,
            o.y,
            o.z,
            seq.current_style(),
            seq.current_motion(),
            seq.current_speed_mod()
        )
        .unwrap();
        s
    }

    #[test]
    fn s1_spawn_idle() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "100@30.0*^ | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00"
        );
    }

    #[test]
    fn s2_idle_to_walk_link_then_cycle() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "103@30.0^,101@30.0* | frame=0.0 vel=(0.00,3.12,0.00) om=(0.00,0.00,0.00) style=8000003D motion=45000005 mod=1.00"
        );
    }

    #[test]
    fn s2b_link_drain_fires_one_animation_done() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        let mut done = 0;
        for _ in 0..10 {
            seq.advance(0.02, None);
            done += seq
                .consume_pending_hooks()
                .iter()
                .filter(|h| **h == ANIMATION_DONE_HOOK)
                .count();
        }
        assert_eq!(done, 1);
    }

    #[test]
    fn s3_walk_to_run_cyclic_to_cyclic() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        seq.set_cycle(NC, RUN, 2.0).unwrap();
        assert_eq!(
            describe(&seq),
            "103@30.0^,105@30.0,104@60.0,102@60.0* | frame=0.0 vel=(0.00,8.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=44000007 mod=2.00"
        );
    }

    #[test]
    fn s4_run_re_speed_fast_path() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        seq.set_cycle(NC, RUN, 2.0).unwrap();
        seq.set_cycle(NC, RUN, 2.5).unwrap();
        assert_eq!(
            describe(&seq),
            "103@30.0^,105@30.0,104@60.0,102@75.0* | frame=0.0 vel=(0.00,10.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=44000007 mod=2.50"
        );
    }

    #[test]
    fn s5_walk_backward_is_walk_forward_at_negative_speed() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        // Walking backward as the interpreter hands it over: forward at -0.65.
        seq.set_cycle(NC, WALK, -0.65).unwrap();
        assert_eq!(
            describe(&seq),
            "105@-19.5^,101@-19.5* | frame=3.0 vel=(-0.00,-2.03,-0.00) om=(-0.00,-0.00,-0.00) style=8000003D motion=45000005 mod=-0.65"
        );
    }

    #[test]
    fn s6_walk_back_to_ready_settles() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        // Walking backward as the interpreter hands it over: forward at -0.65.
        seq.set_cycle(NC, WALK, -0.65).unwrap();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "105@-19.5^,103@-30.0,100@30.0* | frame=3.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00"
        );
    }

    #[test]
    fn s7_emote_action_mid_ready() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.play_action(EMOTE_ACTION, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "10B@30.0^,100@30.0* | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=41000003 mod=1.00"
        );
    }

    #[test]
    fn s8_leave_ground_link_strip_engages_falling_at_once() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        seq.set_cycle(NC, FALLING, 1.0).unwrap();
        seq.remove_all_link_animations();
        assert_eq!(
            describe(&seq),
            "10C@30.0*^ | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000003D motion=40000015 mod=1.00"
        );
    }

    #[test]
    fn s9_turn_modifier() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
        seq.play_action(TURN_MOD, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "103@30.0^,101@30.0* | frame=0.0 vel=(0.00,3.12,0.00) om=(0.00,0.00,1.50) style=8000003D motion=45000005 mod=1.00"
        );
    }

    #[test]
    fn s10_style_change() {
        let mut seq = new_seq();
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(STYLE2, READY, 1.0).unwrap();
        assert_eq!(
            describe(&seq),
            "10A@30.0^,107@30.0* | frame=0.0 vel=(0.00,0.00,0.00) om=(0.00,0.00,0.00) style=8000004C motion=41000003 mod=1.00"
        );
    }
}

// ── root-motion-only advance (AnimationSequencerRootMotionOnlyTests) ────

mod root_only {
    use super::*;

    const READY: u32 = 0x4100_0003;
    const WALK: u32 = 0x4500_0005;
    const RUN: u32 = 0x4400_0007;

    fn anim(frames: usize) -> Animation {
        let mut a = make_anim(frames, 2, |f, p| {
            Frame::new(
                Vec3::new(0.01 * p as f32, 0.02 * f as f32, 0.03),
                Quat::from_axis_angle(Vec3::Z, 0.07 * (f + p) as f32),
            )
        });
        for (f, pf) in a.part_frames.iter_mut().enumerate() {
            if f & 1 == 1 {
                pf.hooks.push(sound_hook(0, BOTH));
            }
        }
        set_pos_frames(&mut a, |_| {
            Frame::new(
                Vec3::new(0.0, 0.1, 0.0),
                Quat::from_axis_angle(Vec3::Z, 0.035),
            )
        });
        a
    }

    fn fixture() -> (TableBuilder, MapLoader) {
        let table = TableBuilder::new(NC)
            .style_default(NC, READY)
            .cycle(NC, READY, motion_data(0x200, 30.0))
            .cycle(
                NC,
                WALK,
                with_omega(
                    with_velocity(motion_data(0x201, 30.0), Vec3::new(0.0, 3.12, 0.0)),
                    Vec3::new(0.0, 0.0, 0.4),
                ),
            )
            .cycle(
                NC,
                RUN,
                with_velocity(motion_data(0x202, 30.0), Vec3::new(0.0, 4.0, 0.0)),
            )
            .link(NC, READY, WALK, motion_data(0x203, 30.0))
            .link(NC, WALK, RUN, motion_data(0x204, 30.0));
        let mut l = MapLoader::default();
        for (id, frames) in [(0x200, 4), (0x201, 6), (0x202, 6), (0x203, 2), (0x204, 3)] {
            l.add(id, anim(frames));
        }
        (table, l)
    }

    fn new_root() -> Frame {
        Frame::new(
            Vec3::new(11.0, 22.0, 33.0),
            Quat::from_axis_angle(Vec3::Z, 0.5),
        )
    }

    fn frame_bits(f: &Frame) -> [u32; 7] {
        [
            f.origin.x.to_bits(),
            f.origin.y.to_bits(),
            f.origin.z.to_bits(),
            f.orientation.x.to_bits(),
            f.orientation.y.to_bits(),
            f.orientation.z.to_bits(),
            f.orientation.w.to_bits(),
        ]
    }

    fn state_bits(seq: &AnimationSequencer) -> String {
        let core = seq.sequence();
        let mut s = String::new();
        for (i, n) in core.nodes().iter().enumerate() {
            write!(
                s,
                "{:X}@{:08X}:{}-{}",
                n.anim().map_or(0, |a| a.id),
                n.framerate.to_bits(),
                n.low_frame,
                n.high_frame
            )
            .unwrap();
            if core.first_cyclic_index() == Some(i) {
                s.push('*');
            }
            if core.curr_anim_index() == Some(i) {
                s.push('^');
            }
            s.push(';');
        }
        let (v, o) = (core.velocity(), core.omega());
        write!(
            s,
            " frame={:016X} vel=({:08X},{:08X},{:08X}) om=({:08X},{:08X},{:08X}) style={:08X} motion={:08X} mod={:08X} queue={} hasNode={}",
            core.frame_number().to_bits(),
            v.x.to_bits(),
            v.y.to_bits(),
            v.z.to_bits(),
            o.x.to_bits(),
            o.y.to_bits(),
            o.z.to_bits(),
            seq.current_style(),
            seq.current_motion(),
            seq.current_speed_mod().to_bits(),
            seq.queue_count(),
            seq.has_current_node()
        )
        .unwrap();
        s
    }

    struct Arm {
        seq: AnimationSequencer,
        root: Frame,
        hooks: Vec<AnimationHook>,
    }

    impl Arm {
        fn new(arrange: fn(&mut AnimationSequencer)) -> Self {
            let (table, l) = fixture();
            let mut seq = sequencer(2, table, l);
            arrange(&mut seq);
            seq.consume_pending_hooks();
            Self {
                seq,
                root: new_root(),
                hooks: Vec::new(),
            }
        }

        fn step_full(&mut self, dt: f32) {
            self.seq.advance(dt, Some(&mut self.root));
            self.hooks.extend(self.seq.consume_pending_hooks());
        }

        fn step_root_only(&mut self, dt: f32) {
            self.seq.advance_root_motion_only(dt, Some(&mut self.root));
            self.hooks.extend(self.seq.consume_pending_hooks());
        }
    }

    fn assert_same(full: &Arm, other: &Arm) {
        assert_eq!(frame_bits(&full.root), frame_bits(&other.root));
        assert_eq!(state_bits(&full.seq), state_bits(&other.seq));
        assert_eq!(full.hooks, other.hooks);
    }

    fn check(arrange: fn(&mut AnimationSequencer), steps: &[f32]) {
        let mut full = Arm::new(arrange);
        let mut root_only = Arm::new(arrange);
        assert_eq!(state_bits(&full.seq), state_bits(&root_only.seq));
        for &dt in steps {
            full.step_full(dt);
            root_only.step_root_only(dt);
            assert_same(&full, &root_only);
        }
        assert_ne!(frame_bits(&new_root()), frame_bits(&full.root));
    }

    fn walk(seq: &mut AnimationSequencer) {
        seq.set_cycle(NC, READY, 1.0).unwrap();
        seq.set_cycle(NC, WALK, 1.0).unwrap();
    }

    #[test]
    fn step_patterns_carry_the_same_root_motion() {
        check(walk, &[1.0]);
        check(walk, &[1.0 / 300.0; 300]);
        check(walk, &[1.0 / 120.0; 40]);
        check(walk, &[1.0 / 30.0; 24]);
        check(walk, &[0.017, 0.051, 0.009, 0.121, 0.033, 0.2, 0.004]);
        check(walk, &[0.02, 0.08, 0.02, 0.5]);
        check(walk, &[0.0, -0.01, 0.05, 0.0, 0.05, -1.0, 0.05]);
    }

    #[test]
    fn transitions_and_long_loops_carry_the_same_root_motion() {
        check(
            |seq| {
                walk(seq);
                seq.set_cycle(NC, RUN, 2.0).unwrap();
            },
            &[1.0 / 60.0; 90],
        );
        check(
            |seq| seq.set_cycle(NC, READY, 1.0).unwrap(),
            &[1.0 / 45.0; 200],
        );
    }

    #[test]
    fn interleaving_both_calls_matches_the_full_advance() {
        let mut full = Arm::new(walk);
        let mut mixed = Arm::new(walk);
        for i in 0..120 {
            let dt = 1.0 / 72.0;
            full.step_full(dt);
            if i % 3 == 0 {
                mixed.step_full(dt);
            } else {
                mixed.step_root_only(dt);
            }
            assert_same(&full, &mixed);
        }
    }

    #[test]
    fn authored_travel_is_three_metres_a_second() {
        let (table, mut l) = fixture();
        let mut straight = anim(4);
        set_pos_frames(&mut straight, |_| {
            Frame::new(Vec3::new(0.0, 0.1, 0.0), Quat::IDENTITY)
        });
        l.add(0x200, straight);
        let mut seq = sequencer(2, table, l);
        seq.set_cycle(NC, READY, 1.0).unwrap();
        let mut root = Frame::IDENTITY;
        for _ in 0..3 {
            seq.advance_root_motion_only(1.0 / 30.0, Some(&mut root));
        }
        assert!((root.origin.y - 0.3).abs() < 1e-4);
        assert!(root.origin.x.abs() < 1e-5);
        assert!(root.origin.z.abs() < 1e-5);
    }

    #[test]
    fn with_nothing_playing_and_no_frame_nothing_changes() {
        let (table, l) = fixture();
        let mut seq = sequencer(2, table, l);
        seq.reset(&mut NoMotionDone);
        let before = state_bits(&seq);
        seq.advance_root_motion_only(0.05, None);
        assert_eq!(state_bits(&seq), before);
        assert!(seq.pending_hooks().is_empty());
    }
}
