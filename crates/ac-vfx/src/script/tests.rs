//! Cases from OpenAC `tests/AcDream.Core.Tests/Vfx/PhysicsScriptRunnerTests.cs`
//! and the physics-script half of
//! `tests/AcDream.Content.Tests/Vfx/RetailDatLoaderTests.cs`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicUsize, Ordering};

use ac_bits::Writer;
use ac_dat::objects::HookData;
use ac_dat::types::Frame;

use super::*;
use crate::random::ConstantRandom;

type Callback = Box<dyn FnMut(&mut PhysicsScriptRunner, u32, &AnimationHook)>;

#[derive(Default)]
struct RecordingSink {
    calls: Vec<(u32, Vec3, AnimationHook)>,
    callback: Option<Callback>,
    eligible: Option<Box<dyn Fn(u32) -> bool>>,
}

impl ScriptHookSink for RecordingSink {
    fn on_script_hook(
        &mut self,
        runner: &mut PhysicsScriptRunner,
        owner: u32,
        anchor: Vec3,
        hook: &AnimationHook,
    ) {
        self.calls.push((owner, anchor, *hook));
        if let Some(callback) = &mut self.callback {
            callback(runner, owner, hook);
        }
    }

    fn can_advance_owner(&self, owner: u32) -> bool {
        self.eligible.as_ref().is_none_or(|f| f(owner))
    }
}

impl RecordingSink {
    fn emitter_ids(&self) -> Vec<u32> {
        self.calls
            .iter()
            .map(|(_, _, h)| emitter_info_id(h))
            .collect()
    }

    fn owner_emitters(&self) -> Vec<(u32, u32)> {
        self.calls
            .iter()
            .map(|(o, _, h)| (*o, emitter_info_id(h)))
            .collect()
    }
}

pub(crate) fn emitter_info_id(hook: &AnimationHook) -> u32 {
    match hook.data {
        HookData::CreateParticle {
            emitter_info_id, ..
        } => emitter_info_id,
        _ => panic!("not a create-particle hook: {hook:?}"),
    }
}

pub(crate) fn create_hook(emitter_info_id: u32) -> AnimationHook {
    AnimationHook {
        direction: 0,
        data: HookData::CreateParticle {
            emitter_info_id,
            part_index: 0,
            offset: Frame::default(),
            emitter_id: 0,
        },
    }
}

pub(crate) fn build_script(items: &[(f64, AnimationHook)]) -> LoadedPhysicsScript {
    LoadedPhysicsScript {
        id: 0,
        hooks: items.to_vec(),
    }
}

fn make_runner(random: f64, scripts: Vec<(u32, LoadedPhysicsScript)>) -> PhysicsScriptRunner {
    let table: HashMap<u32, Arc<LoadedPhysicsScript>> = scripts
        .into_iter()
        .map(|(id, s)| (id, Arc::new(s)))
        .collect();
    PhysicsScriptRunner::new(
        move |id| Ok(table.get(&id).cloned()),
        Box::new(ConstantRandom(random)),
    )
}

const AA: u32 = 0x3300_00AA;
const BB: u32 = 0x3300_00BB;
const CC: u32 = 0x3300_00CC;

#[test]
fn play_direct_missing_or_zero_input_returns_false_without_queue_corruption() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![]);
    assert!(!runner.play_direct(1, 0));
    assert!(!runner.play_direct(0, 0x3300_0001));
    assert!(!runner.play_direct(1, 0x3300_0001));
    assert_eq!(runner.active_script_count(), 0);
    runner.tick(0.0, &mut sink).unwrap();
    assert!(sink.calls.is_empty());
}

#[test]
fn hooks_fire_in_order_at_absolute_scheduled_times() {
    let script = build_script(&[
        (0.0, create_hook(100)),
        (0.5, create_hook(101)),
        (1.0, create_hook(102)),
    ]);
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, script)]);
    runner.set_owner_anchor(7, Vec3::new(1.0, 2.0, 3.0));

    assert!(runner.play_direct(7, AA));
    runner.tick(0.25, &mut sink).unwrap();
    runner.tick(0.60, &mut sink).unwrap();
    runner.tick(1.50, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [100, 101, 102]);
    assert!(sink.calls.iter().all(|c| c.1 == Vec3::new(1.0, 2.0, 3.0)));
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn duplicate_plays_append_serially_to_one_owner() {
    let script = build_script(&[(0.0, create_hook(1)), (1.0, create_hook(2))]);
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, script)]);

    assert!(runner.play_direct(1, AA));
    assert!(runner.play_direct(1, AA));
    assert_eq!(runner.active_script_count(), 2);
    assert_eq!(runner.active_owner_count(), 1);

    runner.tick(0.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [1]);
    // At exactly one second the first tail and the second head are both due.
    runner.tick(1.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [1, 2, 1]);
    runner.tick(2.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [1, 2, 1, 2]);
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn different_owners_progress_independently() {
    let script = build_script(&[(0.0, create_hook(1)), (1.0, create_hook(2))]);
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, script)]);

    runner.play_direct(1, AA);
    runner.tick(0.5, &mut sink).unwrap();
    runner.play_direct(2, AA);
    runner.tick(1.0, &mut sink).unwrap();

    assert_eq!(sink.owner_emitters(), [(1, 1), (1, 2), (2, 1)]);
    assert_eq!(runner.active_script_count(), 1);
    assert_eq!(runner.active_owner_count(), 1);
}

#[test]
fn catch_up_tick_drains_every_due_hook_and_queued_script() {
    let script = build_script(&[
        (0.0, create_hook(1)),
        (0.25, create_hook(2)),
        (0.5, create_hook(3)),
    ]);
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, script)]);
    runner.play_direct(1, AA);
    runner.play_direct(1, AA);

    runner.tick(5.0, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [1, 2, 3, 1, 2, 3]);
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn hook_may_append_to_current_owner_without_invalidating_catch_up() {
    let mut sink = RecordingSink {
        callback: Some(Box::new(|runner, owner, hook| {
            if emitter_info_id(hook) == 1 {
                runner.play_direct(owner, BB);
            }
        })),
        ..Default::default()
    };
    let mut runner = make_runner(
        0.5,
        vec![
            (AA, build_script(&[(0.0, create_hook(1))])),
            (BB, build_script(&[(0.0, create_hook(2))])),
        ],
    );
    runner.play_direct(1, AA);

    runner.tick(0.0, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [1, 2]);
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn hook_may_delete_its_owner_without_dispatching_detached_tail() {
    let mut sink = RecordingSink {
        callback: Some(Box::new(|runner, owner, hook| {
            if emitter_info_id(hook) == 1 {
                runner.stop_all_for_entity(owner);
            }
        })),
        ..Default::default()
    };
    let mut runner = make_runner(
        0.5,
        vec![(
            AA,
            build_script(&[(0.0, create_hook(1)), (0.0, create_hook(2))]),
        )],
    );
    runner.play_direct(1, AA);

    runner.tick(0.0, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [1]);
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn schedule_call_pes_uses_injected_uniform_delay_and_near_zero_appends_immediately() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(0.0, create_hook(9))]))]);

    assert!(runner.schedule_call_pes(1, AA, 2.0));
    assert_eq!(runner.scheduled_call_pes_count(), 1);
    runner.tick(0.999, &mut sink).unwrap();
    assert!(sink.calls.is_empty());
    runner.tick(1.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [9]);

    assert!(runner.schedule_call_pes(1, AA, IMMEDIATE_CALL_PES_THRESHOLD_SECONDS / 2.0));
    assert_eq!(runner.active_script_count(), 1);
    runner.tick(1.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [9, 9]);
}

#[test]
fn published_animation_phase_runs_immediate_script_but_defers_timed_call_one_pass() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.0, vec![(AA, build_script(&[(0.0, create_hook(9))]))]);

    runner.publish_time(1.0).unwrap();
    assert!(runner.schedule_call_pes(1, AA, 0.5));
    assert!(runner.schedule_call_pes(2, AA, IMMEDIATE_CALL_PES_THRESHOLD_SECONDS / 2.0));
    runner.tick(1.0, &mut sink).unwrap();
    assert_eq!(sink.owner_emitters(), [(2, 9)]);

    runner.publish_time(1.1).unwrap();
    runner.tick(1.1, &mut sink).unwrap();
    assert_eq!(sink.owner_emitters(), [(2, 9), (1, 9)]);
}

#[test]
fn update_phase_play_uses_published_current_clock_for_positive_hook_offset() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(0.1, create_hook(9))]))]);

    runner.publish_time(1.0).unwrap();
    assert!(runner.play_direct(1, AA));
    runner.tick(1.0, &mut sink).unwrap();
    assert!(sink.calls.is_empty());

    runner.publish_time(1.1).unwrap();
    runner.tick(1.1, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [9]);
}

#[test]
fn ineligible_owner_retains_scripts_and_timed_calls_until_reentry() {
    use std::sync::Arc as StdArc;
    use std::sync::atomic::AtomicBool;
    let eligible = StdArc::new(AtomicBool::new(true));
    let flag = StdArc::clone(&eligible);
    let mut sink = RecordingSink {
        eligible: Some(Box::new(move |_| flag.load(Ordering::SeqCst))),
        ..Default::default()
    };
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(1.0, create_hook(9))]))]);
    runner.play_direct(1, AA);
    runner.schedule_call_pes(2, AA, 2.0);

    eligible.store(false, Ordering::SeqCst);
    runner.tick(2.0, &mut sink).unwrap();
    assert!(sink.calls.is_empty());
    assert_eq!(runner.active_script_count(), 1);
    assert_eq!(runner.scheduled_call_pes_count(), 1);

    eligible.store(true, Ordering::SeqCst);
    runner.tick(2.0, &mut sink).unwrap();
    assert_eq!(sink.owner_emitters(), [(1, 9)]);
    runner.tick(3.0, &mut sink).unwrap();
    assert_eq!(sink.owner_emitters(), [(1, 9), (2, 9)]);
}

#[test]
fn zero_time_recursive_call_chain_is_rejected_without_hanging() {
    let mut sink = RecordingSink {
        callback: Some(Box::new(|runner, owner, hook| {
            let target = if emitter_info_id(hook) == 1 { BB } else { AA };
            runner.play_direct(owner, target);
        })),
        ..Default::default()
    };
    let mut runner = make_runner(
        0.5,
        vec![
            (AA, build_script(&[(0.0, create_hook(1))])),
            (BB, build_script(&[(0.0, create_hook(2))])),
        ],
    );
    runner.play_direct(1, AA);

    runner.tick(0.0, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [1, 2]);
    let diagnostics = runner.take_diagnostics();
    assert_eq!(
        diagnostics
            .iter()
            .filter(|m| m.to_lowercase().contains("recursive"))
            .count(),
        1
    );
    assert_eq!(runner.active_script_count(), 0);
}

#[test]
fn positive_duration_recursive_chain_drains_catch_up_without_false_cycle_rejection() {
    const FIRST: u32 = 0x3300_0428;
    const SECOND: u32 = 0x3300_0429;
    let mut sink = RecordingSink {
        callback: Some(Box::new(|runner, owner, hook| {
            let target = if emitter_info_id(hook) == 1 {
                SECOND
            } else {
                FIRST
            };
            runner.play_direct(owner, target);
        })),
        ..Default::default()
    };
    let mut runner = make_runner(
        0.5,
        vec![
            (FIRST, build_script(&[(2.8, create_hook(1))])),
            (SECOND, build_script(&[(2.8, create_hook(2))])),
        ],
    );
    runner.play_direct(1, FIRST);

    runner.tick(8.4, &mut sink).unwrap();

    assert_eq!(sink.emitter_ids(), [1, 2, 1]);
    assert!(
        !runner
            .take_diagnostics()
            .iter()
            .any(|m| m.to_lowercase().contains("recursive"))
    );
    assert_eq!(runner.active_script_count(), 1);
}

#[test]
fn cross_owner_reentrant_play_waits_for_next_global_script_pass() {
    let mut sink = RecordingSink {
        callback: Some(Box::new(|runner, _, hook| {
            if emitter_info_id(hook) == 1 {
                runner.play_direct(2, CC);
            }
        })),
        ..Default::default()
    };
    let mut runner = make_runner(
        0.5,
        vec![
            (AA, build_script(&[(0.0, create_hook(1))])),
            (BB, build_script(&[(0.0, create_hook(2))])),
            (CC, build_script(&[(0.0, create_hook(3))])),
        ],
    );
    runner.play_direct(1, AA);
    runner.play_direct(2, BB);

    runner.tick(0.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [1, 2]);

    runner.tick(0.0, &mut sink).unwrap();
    assert_eq!(sink.emitter_ids(), [1, 2, 3]);
}

#[test]
fn non_finite_timeline_is_rejected_before_it_can_bypass_recursion_guard() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(f64::NAN, create_hook(1))]))]);

    assert!(!runner.play_direct(1, AA));
    runner.tick(0.0, &mut sink).unwrap();

    assert!(sink.calls.is_empty());
    assert!(
        runner
            .take_diagnostics()
            .iter()
            .any(|m| m.contains("non-finite"))
    );
}

#[test]
fn schedule_call_pes_rejects_non_finite_inputs_and_stop_clears_delayed_calls() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(0.0, create_hook(9))]))]);

    assert!(!runner.schedule_call_pes(1, AA, f32::NAN));
    assert!(!runner.schedule_call_pes(1, AA, f32::INFINITY));
    assert!(runner.schedule_call_pes(1, AA, 2.0));
    runner.stop_all_for_entity(1);
    runner.tick(10.0, &mut sink).unwrap();

    assert_eq!(runner.scheduled_call_pes_count(), 0);
    assert!(sink.calls.is_empty());
}

#[test]
fn current_anchor_is_read_when_hook_fires() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![(AA, build_script(&[(1.0, create_hook(1))]))]);
    runner.set_owner_anchor(1, Vec3::new(1.0, 0.0, 0.0));
    runner.play_direct(1, AA);
    runner.set_owner_anchor(1, Vec3::new(7.0, 8.0, 9.0));

    runner.tick(1.0, &mut sink).unwrap();

    assert_eq!(sink.calls.len(), 1);
    assert_eq!(sink.calls[0].1, Vec3::new(7.0, 8.0, 9.0));
}

#[test]
fn loader_failure_is_cached_and_does_not_corrupt_other_owners() {
    let good = Arc::new(build_script(&[(0.0, create_hook(1))]));
    let failing_loads = Arc::new(AtomicUsize::new(0));
    let counter = Arc::clone(&failing_loads);
    let mut runner = PhysicsScriptRunner::new(
        move |id| {
            if id == AA {
                return Ok(Some(Arc::clone(&good)));
            }
            counter.fetch_add(1, Ordering::SeqCst);
            Err(ScriptLoadError("fixture DAT failure".into()))
        },
        Box::new(ConstantRandom(0.5)),
    );
    let mut sink = RecordingSink::default();

    assert!(!runner.play_direct(1, BB));
    assert!(!runner.play_direct(1, BB));
    assert!(runner.play_direct(2, AA));
    runner.tick(0.0, &mut sink).unwrap();

    assert_eq!(failing_loads.load(Ordering::SeqCst), 1);
    assert_eq!(sink.emitter_ids(), [1]);
}

#[test]
fn tick_rejects_non_finite_or_backwards_time() {
    let mut sink = RecordingSink::default();
    let mut runner = make_runner(0.5, vec![]);
    runner.tick(1.0, &mut sink).unwrap();

    assert!(runner.tick(0.5, &mut sink).is_err());
    assert!(runner.tick(f64::NAN, &mut sink).is_err());
    assert!(runner.tick(f64::INFINITY, &mut sink).is_err());
}

// ---- Loader (OpenAC RetailDatLoaderTests, physics-script cases) ----

fn script_bytes(id: u32, entries: &[(f64, &[u8])]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(id);
    w.u32(entries.len() as u32);
    for (time, hook) in entries {
        w.f64(*time);
        w.bytes(hook);
    }
    w.into_vec()
}

fn blocking_hook_bytes() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x1A); // create blocking particle
    w.u32(0); // both directions
    w.u32(0x3200_F001);
    w.u32(0xFFFF_FFFF); // the root, distinct from part zero
    for v in [1.0f32, 2.0, 3.0, 0.5, 0.5, -0.5, -0.5] {
        w.f32(v);
    }
    w.u32(7);
    w.into_vec()
}

fn animation_done_bytes() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0x04);
    w.u32(1); // forward
    w.into_vec()
}

#[test]
fn physics_script_blocking_hook_retains_payload_and_following_cursor() {
    let bytes = script_bytes(
        0x3300_F001,
        &[
            (0.0, &blocking_hook_bytes()),
            (1.25, &animation_done_bytes()),
        ],
    );
    let script = parse_physics_script(0x3300_F001, &bytes).unwrap();
    assert_eq!(script.id, 0x3300_F001);
    assert_eq!(script.hooks.len(), 2);
    assert_eq!(script.hooks[0].0, 0.0);
    let HookData::CreateBlockingParticle {
        emitter_info_id,
        part_index,
        offset,
        emitter_id,
    } = script.hooks[0].1.data
    else {
        panic!("expected a blocking particle hook");
    };
    assert_eq!(emitter_info_id, 0x3200_F001);
    assert_eq!(part_index, 0xFFFF_FFFF);
    assert_eq!(
        (offset.origin.x, offset.origin.y, offset.origin.z),
        (1.0, 2.0, 3.0)
    );
    assert_eq!(
        (
            offset.orientation.w,
            offset.orientation.x,
            offset.orientation.y,
            offset.orientation.z
        ),
        (0.5, 0.5, -0.5, -0.5)
    );
    assert_eq!(emitter_id, 7);
    assert_eq!(script.hooks[1].0, 1.25);
    assert_eq!(script.hooks[1].1.data, HookData::AnimationDone);
}

#[test]
fn physics_script_sorts_hooks_by_start_time_after_decode() {
    let bytes = script_bytes(
        0x3300_F001,
        &[
            (2.0, &blocking_hook_bytes()),
            (1.25, &animation_done_bytes()),
        ],
    );
    let script = parse_physics_script(0x3300_F001, &bytes).unwrap();
    assert_eq!(script.hooks[0].0, 1.25);
    assert_eq!(script.hooks[0].1.data, HookData::AnimationDone);
    assert_eq!(script.hooks[1].0, 2.0);
    assert!(matches!(
        script.hooks[1].1.data,
        HookData::CreateBlockingParticle { .. }
    ));
}

#[test]
fn physics_script_rejects_embedded_id_mismatch_trailing_bytes_and_non_finite_time() {
    let bytes = script_bytes(0x3300_F001, &[(0.0, &animation_done_bytes())]);
    assert!(parse_physics_script(0x3300_F002, &bytes).is_err());

    let mut trailing = bytes.clone();
    trailing.push(0);
    assert!(parse_physics_script(0x3300_F001, &trailing).is_err());

    let nan = script_bytes(0x3300_F001, &[(f64::NAN, &animation_done_bytes())]);
    let error = parse_physics_script(0x3300_F001, &nan).unwrap_err();
    assert!(error.0.contains("non-finite"));
}
