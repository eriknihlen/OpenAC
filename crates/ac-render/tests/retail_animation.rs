//! The window's animation against the retail dats: a human (Setup
//! 0x02000001, motion table 0x09000001) posed through idle, walk, run,
//! turns both ways, sidesteps both ways, walking backwards and a fall.
//! Ignored by default; `scripts/check.*` runs them when the dats are
//! present.

use std::path::PathBuf;
use std::sync::Arc;

use ac_motion::motion_command::{
    FALLING, NON_COMBAT, READY, RUN_FORWARD, SIDE_STEP_RIGHT, TURN_RIGHT, WALK_FORWARD,
};
use ac_physics::movement::InboundInterpretedState;
use ac_proto::{ModelData, Position, Quaternion, Vector3};
use ac_render::animation::AnimationPresenter;
use ac_render::animation::source::ContentAnimationSource;
use ac_render::content::Content;
use ac_runtime::world::{MotionView, ObjectKey, ObjectView, RadarFacts};
use glam::Vec3;

fn content() -> Arc<Content> {
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    Arc::new(Content::open(&dir).expect("open the retail dats"))
}

fn human(motion: Option<MotionView>) -> ObjectView {
    ObjectView {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        name: None,
        setup_id: Some(0x0200_0001),
        position: Some(Position {
            cell_id: 0xA9B4_0019,
            origin: Vector3 {
                x: 84.0,
                y: 7.1,
                z: 94.0,
            },
            orientation: Quaternion::IDENTITY,
        }),
        parent: None,
        placement: None,
        rest_placement: 0,
        scale: None,
        translucency: None,
        // Gravity: falling needs it.
        state: 0x0000_0400,
        velocity: None,
        model: ModelData::default(),
        motion_table_id: Some(0x0900_0001),
        sound_table_id: None,
        radar: RadarFacts::default(),
        motion,
        effects: ac_runtime::world::EffectFacts::default(),
        step_blend: None,
    }
}

fn moving(forward: u32, forward_speed: f32, side: (u32, f32), turn: (u32, f32)) -> MotionView {
    MotionView {
        state: InboundInterpretedState {
            current_style: NON_COMBAT,
            forward_command: forward,
            forward_speed,
            side_step_command: side.0,
            side_step_speed: side.1,
            turn_command: turn.0,
            turn_speed: turn.1,
            actions: None,
        },
        serial: 0,
        grounded: true,
        standing_long_jump: false,
        body: None,
    }
}

/// Play `motion` for `seconds` at 60 frames a second; every pose finite,
/// and the pose moves.
fn play(
    presenter: &mut AnimationPresenter,
    source: &mut ContentAnimationSource,
    motion: MotionView,
    seconds: f64,
) -> usize {
    let view = human(Some(motion));
    presenter.sync([&view].into_iter(), source);
    let mut distinct = 0;
    let mut last = presenter.object(1).unwrap().poses.clone();
    let frames = (seconds * 60.0) as usize;
    for _ in 0..frames {
        presenter.tick(
            1.0 / 60.0,
            Some(Vec3::ZERO),
            |_| Some(Vec3::ONE),
            |_, l| Some(l),
        );
        let live = presenter.object(1).unwrap();
        for pose in &live.poses {
            assert!(
                pose.visual.is_finite() && pose.rigid.is_finite(),
                "{pose:?}"
            );
        }
        if live.poses != last {
            distinct += 1;
            last.clone_from(&live.poses);
        }
        // The interpreter's queue drains (the motions complete): it never
        // piles up (as in OpenAC).
        assert!(live.pending_motions() < 8, "{}", live.pending_motions());
    }
    distinct
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_human_poses_through_walk_run_turn_and_fall_without_nan() {
    let mut source = ContentAnimationSource::new(content());
    let mut presenter = AnimationPresenter::new();
    let idle = moving(READY, 1.0, (0, 1.0), (0, 1.0));
    let none = (0, 1.0);
    let cases = [
        ("idle", idle.clone()),
        ("walk", moving(WALK_FORWARD, 1.0, none, none)),
        ("run", moving(RUN_FORWARD, 2.5, none, none)),
        ("turn right", moving(READY, 1.0, none, (TURN_RIGHT, 1.5))),
        ("turn left", moving(READY, 1.0, none, (TURN_RIGHT, -1.5))),
        (
            "sidestep right",
            moving(READY, 1.0, (SIDE_STEP_RIGHT, 1.0), none),
        ),
        (
            "sidestep left",
            moving(READY, 1.0, (SIDE_STEP_RIGHT, -1.0), none),
        ),
        ("walk backwards", moving(WALK_FORWARD, -0.65, none, none)),
        (
            "run and turn",
            moving(RUN_FORWARD, 2.5, none, (TURN_RIGHT, 1.5)),
        ),
    ];
    for (name, motion) in cases {
        let distinct = play(&mut presenter, &mut source, motion, 2.0);
        assert!(
            distinct > 30,
            "{name}: the pose changed only {distinct} times"
        );
        let live = presenter.object(1).unwrap();
        assert_eq!(
            live.sequencer().unwrap().current_style(),
            NON_COMBAT,
            "{name}"
        );
    }
    // Leaving the ground plays the fall.
    let mut airborne = moving(READY, 1.0, none, none);
    airborne.grounded = false;
    play(&mut presenter, &mut source, airborne, 1.0);
    let live = presenter.object(1).unwrap();
    assert_eq!(live.sequencer().unwrap().current_motion(), FALLING);
    // Landing goes back to standing.
    play(&mut presenter, &mut source, idle, 2.0);
    let live = presenter.object(1).unwrap();
    assert_eq!(live.sequencer().unwrap().current_motion(), READY);
}

#[test]
#[ignore = "needs the retail dat files"]
fn left_and_backward_motions_play_their_right_and_forward_cycles_reversed() {
    let mut source = ContentAnimationSource::new(content());
    let none = (0, 1.0);
    for (motion, cycle, sign) in [
        (
            moving(READY, 1.0, none, (TURN_RIGHT, -1.5)),
            TURN_RIGHT,
            -1.0,
        ),
        (
            moving(READY, 1.0, (SIDE_STEP_RIGHT, -1.0), none),
            SIDE_STEP_RIGHT,
            -1.0,
        ),
        (moving(WALK_FORWARD, -0.65, none, none), WALK_FORWARD, -1.0),
        (moving(WALK_FORWARD, 1.0, none, none), WALK_FORWARD, 1.0),
    ] {
        let mut presenter = AnimationPresenter::new();
        play(&mut presenter, &mut source, motion, 1.0);
        let sequencer = presenter.object(1).unwrap().sequencer().unwrap();
        assert_eq!(sequencer.current_motion(), cycle);
        assert_eq!(sequencer.current_speed_mod().signum(), sign);
    }
}

/// The server sends a melee swing as the forward command of a motion
/// update, and a repeated attack at the same height and power sends the
/// same command again, for players and monsters alike. The game takes up every update it accepts, so the
/// swing plays each time (OpenAC `RemoteInboundMotionDispatcher.Apply` ->
/// `MotionInterpreter.MoveToInterpretedState` per `UpdateMotion`, with the
/// window's frames composed from that same sequencer). The window's copy
/// once adopted only a changed state and played the second identical swing
/// not at all; a state merely seen again (the same serial) plays nothing.
#[test]
#[ignore = "needs the retail dat files"]
fn a_swing_sent_again_plays_again() {
    use ac_motion::motion_command::{SLASH_HIGH, SWORD_COMBAT};
    let mut source = ContentAnimationSource::new(content());
    let mut presenter = AnimationPresenter::new();
    let none = (0, 1.0);
    let mut ready = moving(READY, 1.0, none, none);
    ready.state.current_style = SWORD_COMBAT;
    play(&mut presenter, &mut source, ready, 2.0);
    let mut swing = moving(SLASH_HIGH, 1.25, none, none);
    swing.state.current_style = SWORD_COMBAT;
    for serial in 1..=3 {
        swing.serial = serial;
        let view = human(Some(swing.clone()));
        presenter.sync([&view].into_iter(), &mut source);
        let live = presenter.object(1).unwrap();
        assert!(live.pending_motions() > 0, "swing {serial} did not start");
        // The same view every frame until the swing is done: no replay.
        play(&mut presenter, &mut source, swing.clone(), 3.0);
        let live = presenter.object(1).unwrap();
        assert_eq!(live.pending_motions(), 0, "swing {serial} did not finish");
        presenter.sync([&view].into_iter(), &mut source);
        let live = presenter.object(1).unwrap();
        assert_eq!(live.pending_motions(), 0, "swing {serial} replayed");
    }
}

/// The game applies every motion state in the order it arrives, and the
/// window's frames come from that one sequencer (OpenAC
/// `RemoteInboundMotionDispatcher.Apply` per `UpdateMotion`, its animation
/// sink the presentation sequencer): a swing replaced before the next
/// frame by another server state, or by the body's own movement (a turn
/// the keys or a plugin make), has still been played. The window takes up
/// each state the session took up, in order, before it follows the
/// object's latest view; following only the latest view lost the swing.
#[test]
#[ignore = "needs the retail dat files"]
fn a_swing_replaced_before_the_next_frame_still_plays() {
    use ac_motion::motion_command::{SLASH_HIGH, SWORD_COMBAT};
    use ac_runtime::world::{AppliedMotion, BodyFacts};
    let none = (0, 1.0);
    let simulated = |forward: u32, speed: f32, turn: (u32, f32), serial: u32| {
        let mut m = moving(forward, speed, none, turn);
        m.state.current_style = SWORD_COMBAT;
        m.serial = serial;
        m.body = Some(BodyFacts::default());
        m
    };
    let applied = |motion: &MotionView| AppliedMotion {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        motion: motion.clone(),
        taken_up: true,
    };
    let swinging = |presenter: &AnimationPresenter| {
        presenter
            .object(1)
            .unwrap()
            .sequencer()
            .unwrap()
            .manager()
            .pending_animations()
            .any(|p| p.motion == SLASH_HIGH)
    };
    let swing = simulated(SLASH_HIGH, 1.25, none, 1);
    let cases = [
        // The server's swing, then the server's ready, in one pass.
        ("ready", simulated(READY, 1.0, none, 2), true),
        // The server's swing, then the body turns on its own (same serial).
        ("turn", simulated(READY, 1.0, (TURN_RIGHT, 1.5), 1), true),
        // The latest view alone, as before: the swing is never seen.
        ("view only", simulated(READY, 1.0, none, 2), false),
    ];
    for (name, after, queued) in cases {
        let mut source = ContentAnimationSource::new(content());
        let mut presenter = AnimationPresenter::new();
        play(
            &mut presenter,
            &mut source,
            simulated(READY, 1.0, none, 0),
            2.0,
        );
        if queued {
            presenter.queue_motions([applied(&swing)]);
            if after.serial != swing.serial {
                presenter.queue_motions([applied(&after)]);
            }
        }
        let view = human(Some(after.clone()));
        presenter.sync([&view].into_iter(), &mut source);
        assert_eq!(swinging(&presenter), queued, "{name}");
        if queued {
            play(&mut presenter, &mut source, after.clone(), 3.0);
            let live = presenter.object(1).unwrap();
            assert!(!swinging(&presenter), "{name}: the swing finished");
            // Then the cycle the latest state asks for (turning in place
            // loops the turn).
            let cycle = if after.state.turn_command != 0 {
                after.state.turn_command
            } else {
                after.state.forward_command
            };
            assert_eq!(live.sequencer().unwrap().current_motion(), cycle, "{name}");
        }
    }
}

/// A live log (a two-handed sword): the swing
/// (SlashMed at speed 2) is taken up, then the server's turn to the target
/// (movement type 8) reaches the window as the latest view: ready, turning
/// right, the same serial. A turn is a cycle change, and the motion table
/// replaces only the cyclic tail of the sequence; the action queued before
/// it stays and plays out (OpenAC `CMotionTable.GetObjectSequence`, its
/// `RemoveCyclicAnims` then the link and the new cycle; tests
/// `CMotionTableTests.GetObjectSequence_ActionDirectLink_QueuesActionAndReAddsBaseCycle`,
/// `GetObjectSequence_TurnInPlace_ResolvesAsCycleFromReady`). Whether the
/// turn comes in the same frame or later in the swing, the swing is drawn
/// from its first frame to its last, then the turn.
#[test]
#[ignore = "needs the retail dat files"]
fn a_turn_followed_after_a_swing_lets_the_swing_play_to_its_end() {
    use ac_motion::motion_command::{SLASH_MED, TWO_HANDED_SWORD_COMBAT};
    use ac_runtime::world::{AppliedMotion, BodyFacts};
    let none = (0, 1.0);
    let simulated = |forward: u32, speed: f32, turn: (u32, f32), serial: u32| {
        let mut m = moving(forward, speed, none, turn);
        m.state.current_style = TWO_HANDED_SWORD_COMBAT;
        m.serial = serial;
        m.body = Some(BodyFacts::default());
        m
    };
    let tick = |presenter: &mut AnimationPresenter| {
        presenter.tick(
            1.0 / 60.0,
            Some(Vec3::ZERO),
            |_| Some(Vec3::ONE),
            |_, l| Some(l),
        );
    };
    for frames_before_the_turn in [0usize, 1, 10, 30] {
        let mut source = ContentAnimationSource::new(content());
        let mut presenter = AnimationPresenter::new();
        play(
            &mut presenter,
            &mut source,
            simulated(READY, 1.0, none, 52),
            2.0,
        );
        let swing = simulated(SLASH_MED, 2.0, none, 53);
        presenter.queue_motions([AppliedMotion {
            key: ObjectKey {
                local_id: 1,
                incarnation: 1,
            },
            guid: 0x5000_0001,
            motion: swing.clone(),
            taken_up: true,
        }]);
        presenter.sync([&human(Some(swing))].into_iter(), &mut source);
        let (swing_anim, first, last) = presenter.object(1).unwrap().drawing().unwrap();
        assert!(first < 1.0, "the swing starts at its first frame");
        let turned = human(Some(simulated(READY, 1.0, (TURN_RIGHT, 1.0), 53)));
        let mut reached = first;
        for frame in 0..180 {
            if frame == frames_before_the_turn {
                presenter.sync([&turned].into_iter(), &mut source);
            }
            match presenter.object(1).unwrap().drawing() {
                Some((anim, at, _)) if anim == swing_anim => reached = reached.max(at),
                _ => break,
            }
            tick(&mut presenter);
        }
        // Sampled per frame: the last sample is within one 30 Hz quantum
        // (1.6 frames at 48 frames a second) of the last frame.
        assert!(
            reached + 2.0 >= f64::from(last),
            "turn after {frames_before_the_turn} frames: the swing was left at frame \
             {reached:.1} of {last}"
        );
        let live = presenter.object(1).unwrap();
        assert_eq!(
            live.sequencer().unwrap().current_motion(),
            TURN_RIGHT,
            "then the turn"
        );
    }
}

/// A live log: the character is
/// turning to its target (the server's type-8 turn, followed as ready and
/// turning right), and 0.43 s later the server's swing (SlashMed at speed
/// 2, turn speed 2.25) is taken up. In the session the swing first
/// interrupts the turn (OpenAC `RemoteInboundMotionDispatcher.Apply`:
/// `InterruptCurrentMovement` before `MoveToInterpretedState`), so the
/// swing starts from ready. A two-handed stance has no turning cycle of its
/// own (the turn comes from the default stance), and the motion table has
/// no action from a substate its stance has no cycle for (OpenAC
/// `CMotionTable.GetObjectSequence`), so taking the swing up while still
/// turning lost it: the window drew ready instead. The session hands the
/// window the state the interrupt left (`AppliedMotion::taken_up` false)
/// before the swing; followed in order, the swing is drawn from its first
/// frame to its last.
#[test]
#[ignore = "needs the retail dat files"]
fn a_swing_taken_up_while_turning_to_the_target_plays_to_its_end() {
    use ac_motion::motion_command::{SLASH_MED, TWO_HANDED_SWORD_COMBAT};
    use ac_runtime::world::{AppliedMotion, BodyFacts};
    let none = (0, 1.0);
    let simulated = |forward: u32, speed: f32, turn: (u32, f32), serial: u32| {
        let mut m = moving(forward, speed, none, turn);
        m.state.current_style = TWO_HANDED_SWORD_COMBAT;
        m.serial = serial;
        m.body = Some(BodyFacts::default());
        m
    };
    let event = |motion: &MotionView, taken_up: bool| AppliedMotion {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        motion: motion.clone(),
        taken_up,
    };
    let tick = |presenter: &mut AnimationPresenter| {
        presenter.tick(
            1.0 / 60.0,
            Some(Vec3::ZERO),
            |_| Some(Vec3::ONE),
            |_, l| Some(l),
        );
    };
    for interrupted_first in [true, false] {
        let mut source = ContentAnimationSource::new(content());
        let mut presenter = AnimationPresenter::new();
        play(
            &mut presenter,
            &mut source,
            simulated(READY, 1.0, none, 24),
            2.0,
        );
        // The turn to the target, 0.43 s of it.
        let turning = simulated(READY, 1.0, (TURN_RIGHT, 1.0), 24);
        presenter.sync([&human(Some(turning))].into_iter(), &mut source);
        for _ in 0..26 {
            tick(&mut presenter);
        }
        assert_eq!(
            presenter
                .object(1)
                .unwrap()
                .sequencer()
                .unwrap()
                .current_motion(),
            TURN_RIGHT
        );
        let mut swing = simulated(SLASH_MED, 2.0, none, 25);
        swing.state.turn_speed = 2.25;
        if interrupted_first {
            presenter.queue_motions([event(&simulated(READY, 1.0, none, 24), false)]);
        }
        presenter.queue_motions([event(&swing, true)]);
        presenter.sync([&human(Some(swing))].into_iter(), &mut source);
        let live = presenter.object(1).unwrap();
        let swinging = live
            .sequencer()
            .unwrap()
            .manager()
            .pending_animations()
            .any(|p| p.motion == SLASH_MED);
        assert_eq!(
            swinging, interrupted_first,
            "interrupted {interrupted_first}"
        );
        if !interrupted_first {
            continue;
        }
        let (swing_anim, first, last) = live.drawing().unwrap();
        assert!(first < 2.0, "the swing starts at its first frame");
        let mut reached = first;
        for _ in 0..180 {
            match presenter.object(1).unwrap().drawing() {
                Some((anim, at, _)) if anim == swing_anim => reached = reached.max(at),
                _ => break,
            }
            tick(&mut presenter);
        }
        assert!(
            reached + 2.0 >= f64::from(last),
            "the swing was left at frame {reached:.1} of {last}"
        );
    }
}

/// The server's log out: the
/// character's motion in NonCombat, Ready, with the LogOut action in its
/// command list, not autonomous. The window takes the state up and plays
/// the action (OpenAC `RemoteInboundMotionDispatcher.Apply` ->
/// `MotionInterpreter.MoveToInterpretedState`, the actions newer than the
/// last stamp replayed).
#[test]
#[ignore = "needs the retail dat files"]
fn the_log_out_motion_the_server_sends_plays_in_the_window() {
    use ac_motion::motion_command::LOG_OUT;
    use ac_physics::movement::InboundMotionAction;
    use ac_runtime::world::{AppliedMotion, BodyFacts};
    let none = (0, 1.0);
    let mut source = ContentAnimationSource::new(content());
    let mut presenter = AnimationPresenter::new();
    let mut ready = moving(READY, 1.0, none, none);
    ready.body = Some(BodyFacts::default());
    play(&mut presenter, &mut source, ready.clone(), 2.0);
    let mut logout = ready;
    logout.serial = 1;
    logout.state.actions = Some(vec![InboundMotionAction {
        command: LOG_OUT,
        stamp: 1,
        autonomous: false,
        speed: 1.0,
    }]);
    presenter.queue_motions([AppliedMotion {
        key: ObjectKey {
            local_id: 1,
            incarnation: 1,
        },
        guid: 0x5000_0001,
        motion: logout.clone(),
        taken_up: true,
    }]);
    presenter.sync([&human(Some(logout.clone()))].into_iter(), &mut source);
    let playing = |presenter: &AnimationPresenter| {
        presenter
            .object(1)
            .unwrap()
            .sequencer()
            .unwrap()
            .manager()
            .pending_animations()
            .any(|p| p.motion == LOG_OUT)
    };
    assert!(playing(&presenter), "the log out plays");
    play(&mut presenter, &mut source, logout, 1.0);
    assert!(playing(&presenter), "and goes on playing");
}

/// The server kills a creature in two steps: it sends
/// the creature's motion as dead (NonCombat, Dead), which the creature
/// plays from where it stands (the fall, then the dead cycle); when that
/// animation's time is up it removes the creature and creates its corpse at
/// the same place, described as dead too (its current motion state
/// NonCombat, Dead). An object created in a motion enters the world with
/// the links into that motion dropped (OpenAC `SpawnMotionInitializer.Create`:
/// `InitializeState`, `SetCycle`, then `MotionTableManager.HandleEnterWorld`),
/// so the corpse lies dead from its first frame. The window's copy played
/// the corpse's fall from standing again: the death played twice (seen
/// live).
#[test]
#[ignore = "needs the retail dat files"]
fn a_corpse_created_dead_lies_dead_without_falling_again() {
    use ac_motion::motion_command::DEAD;
    let none = (0, 1.0);
    let mut source = ContentAnimationSource::new(content());

    // The creature, standing, takes up dead: it falls, then lies dead.
    let mut presenter = AnimationPresenter::new();
    play(
        &mut presenter,
        &mut source,
        moving(READY, 1.0, none, none),
        2.0,
    );
    let mut dead = moving(DEAD, 1.0, none, none);
    dead.serial = 1;
    presenter.sync([&human(Some(dead.clone()))].into_iter(), &mut source);
    let (fall, _, _) = presenter.object(1).unwrap().drawing().unwrap();
    play(&mut presenter, &mut source, dead.clone(), 5.0);
    let live = presenter.object(1).unwrap();
    assert_eq!(live.sequencer().unwrap().current_motion(), DEAD);
    let (lying, _, _) = live.drawing().unwrap();
    assert_ne!(fall, lying, "the creature falls before it lies dead");

    // Its corpse, a new object created dead: it lies dead at once and
    // stays so.
    let mut presenter = AnimationPresenter::new();
    let corpse = human(Some(dead));
    presenter.sync([&corpse].into_iter(), &mut source);
    let live = presenter.object(1).unwrap();
    assert_eq!(live.pending_motions(), 0, "the corpse waits on no fall");
    for frame in 0..300 {
        let drawn = presenter.object(1).unwrap().drawing().unwrap().0;
        assert_eq!(
            drawn, lying,
            "frame {frame}: the corpse draws 0x{drawn:08X}, not the dead cycle 0x{lying:08X}"
        );
        presenter.tick(
            1.0 / 60.0,
            Some(Vec3::ZERO),
            |_| Some(Vec3::ONE),
            |_, l| Some(l),
        );
    }
}

/// OpenAC `InstalledStaticAnimatingOmegaTests.EverySetOmegaAnimationIsPureYawOnAMeshOffsetFromItsAxis`:
/// every Setup whose default animation has a spin hook spins about the
/// vertical axis only, with its parts more than a metre from that axis (the
/// ambient flyers: birds, butterflies), so the spin reads as flight.
#[test]
#[ignore = "needs the retail dat files"]
fn every_spin_hook_is_pure_yaw_on_a_mesh_off_its_axis() {
    use ac_dat::objects::{Animation, HookData, Setup};
    let dir = std::env::var_os("OPENAC_DAT_DIR")
        .map(PathBuf::from)
        .or_else(ac_dat::default_dat_dir)
        .expect("no dat directory: set OPENAC_DAT_DIR");
    let dats = ac_dat::DatCollection::open(&dir).expect("open the retail dats");
    let mut found = Vec::new();
    for entry in dats.portal.entries() {
        let id = entry.unwrap().id;
        if id >> 24 != 0x02 {
            continue;
        }
        let Ok(Some(setup)) = dats.portal.get::<Setup>(id) else {
            continue;
        };
        if setup.default_animation == 0 {
            continue;
        }
        let Ok(Some(animation)) = dats.portal.get::<Animation>(setup.default_animation) else {
            continue;
        };
        for frame in &animation.part_frames {
            for hook in &frame.hooks {
                let HookData::SetOmega { axis } = hook.data else {
                    continue;
                };
                assert!(axis.x.abs() < 1e-5 && axis.y.abs() < 1e-5, "{id:#010X}");
                assert!(axis.z != 0.0, "{id:#010X}");
                let radius = setup
                    .placement_frames
                    .iter()
                    .flat_map(|(_, p)| &p.frames)
                    .map(|f| f64::from(f.origin.x).hypot(f64::from(f.origin.y)))
                    .fold(0.0, f64::max);
                found.push((id, radius));
            }
        }
    }
    assert!(!found.is_empty());
    for (id, radius) in found {
        assert!(
            radius > 1.0,
            "setup {id:#010X} spins {radius:.3} m from its mesh"
        );
    }
}

/// The player charging a jump from standing still, as the session's motion
/// interpreter publishes it: its forward axis says run once the run key
/// goes down, and its standing long jump is set.
fn charging_standing_jump(forward: u32, forward_speed: f32) -> MotionView {
    let mut motion = moving(forward, forward_speed, (0, 1.0), (0, 1.0));
    motion.standing_long_jump = true;
    motion.body = Some(ac_runtime::world::BodyFacts {
        velocity: [0.0; 3],
        in_contact: true,
        contact_normal: [0.0, 0.0, 1.0],
    });
    motion
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_standing_jump_charge_stays_ready_while_the_run_key_is_held() {
    let mut source = ContentAnimationSource::new(content());
    let mut presenter = AnimationPresenter::new();
    play(
        &mut presenter,
        &mut source,
        charging_standing_jump(READY, 1.0),
        1.0,
    );
    // The run key goes down during the charge: the axis says run, the body
    // keeps its ready pose until the jump.
    play(
        &mut presenter,
        &mut source,
        charging_standing_jump(RUN_FORWARD, 2.5),
        1.0,
    );
    let live = presenter.object(1).unwrap();
    assert_eq!(live.sequencer().unwrap().current_motion(), READY);
    // The charge ends without a jump (the flag drops): nothing re-applies
    // the motion, as in the session's interpreter; the next change does.
    let mut released = charging_standing_jump(RUN_FORWARD, 2.5);
    released.standing_long_jump = false;
    play(&mut presenter, &mut source, released, 0.5);
    let mut walking = charging_standing_jump(WALK_FORWARD, 1.0);
    walking.standing_long_jump = false;
    play(&mut presenter, &mut source, walking, 0.5);
    let live = presenter.object(1).unwrap();
    assert_eq!(live.sequencer().unwrap().current_motion(), WALK_FORWARD);
}
