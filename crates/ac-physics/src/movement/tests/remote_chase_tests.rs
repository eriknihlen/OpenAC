//! OpenAC `tests/AcDream.Core.Tests/Physics/Motion/RemoteChaseEndToEndHarnessTests.cs`
//! (with its `RemoteChaseHarness`), `RemoteChaseDrainBisectTests.cs` and
//! `LinkStripDrainTests.cs`: a remote creature (interpreter, move-to,
//! sticky, motion table) chasing a scripted player.
//!
//! The harness's target host answers the player's position directly and
//! re-sends it every quantum; here the creature's subscriptions arrive as
//! posted messages the harness answers the same way. OpenAC's
//! harness keeps its re-send period when the move-to subscribes (quantum 0)
//! and takes any other subscription's quantum.

use glam::{Quat, Vec3};

use ac_motion::move_to_math::{cylinder_distance_no_z, get_heading, position_heading, set_heading};
use ac_motion::{MotionDeltaFrame, MovementParameters, MovementType};

use super::motion_fixtures::{
    MapLoader, TableBuilder, motion_data, rest_anim, sequencer, with_omega, with_velocity,
};
use crate::body::{CellPosition, PhysicsBody};
use crate::movement::{
    InboundInterpretedState, InboundMotionAction, InterpreterHost, MotionInterp, MotionSink,
    MovementObject, MovementOps, MovementStruct, ObjectCx, ObjectToken, PositionOps, RemoteWeenie,
    SinkArg, StickyManager, TargetInfo, TargetMessage, TargetStatus, TargetWorld,
};
use ac_core::physics::{PhysicsState, TransientState};

const NON_COMBAT: u32 = 0x8000_003D;
const COMBAT: u32 = 0x8000_003C;
const READY: u32 = 0x4100_0003;
const WALK: u32 = 0x4500_0005;
const RUN: u32 = 0x4400_0007;
const TURN_RIGHT: u32 = 0x6500_000D;
const ATTACK_ACTION: u32 = 0x1000_0063;
const CREATURE: ObjectToken = ObjectToken {
    id: 0x8000_0BE5,
    incarnation: 1,
};
const PLAYER: ObjectToken = ObjectToken {
    id: 0x5000_000A,
    incarnation: 1,
};
const DT: f32 = 1.0 / 60.0;
const OWN_RADIUS: f32 = 0.3;
const STICKY_TARGET_RADIUS: f32 = 0.5;

struct ChaseWorld {
    player_pos: Vec3,
}

impl TargetWorld for ChaseWorld {
    fn resolve(&self, id: u32) -> Option<ObjectToken> {
        (id == PLAYER.id).then_some(PLAYER)
    }
    fn position_of(&self, token: ObjectToken) -> Option<CellPosition> {
        (token == PLAYER).then(|| CellPosition::new(1, self.player_pos, Quat::IDENTITY))
    }
}

pub(super) struct ChaseHarness {
    pub obj: MovementObject<RemoteWeenie>,
    pub player_pos: Vec3,
    pub player_velocity: Vec3,
    target_armed: bool,
    last_delivery_time: f64,
    quantum: f64,
    pub now: f64,
    pub run_installs: i32,
    pub ticks_in_run: i32,
    pub ticks_in_walk_fwd: i32,
    pub total_ticks: i32,
    pub max_run_streak: i32,
    run_streak: i32,
    prev_substate: u32,
}

fn fixture() -> crate::movement::tests::motion_fixtures::MapLoader {
    MapLoader::default()
}

impl ChaseHarness {
    pub fn new() -> Self {
        const READY_NC: u32 = 0x200;
        const READY_C: u32 = 0x201;
        const WALK_ANIM: u32 = 0x202;
        const RUN_ANIM: u32 = 0x203;
        const READY_TO_WALK: u32 = 0x204;
        const WALK_TO_READY: u32 = 0x205;
        const READY_TO_RUN: u32 = 0x206;
        const RUN_TO_READY: u32 = 0x207;
        const STANCE_LINK: u32 = 0x208;
        const ATTACK_LINK: u32 = 0x209;
        const TURN_ANIM: u32 = 0x20A;
        let mut loader = fixture();
        for (id, frames) in [
            (READY_NC, 30),
            (READY_C, 30),
            (WALK_ANIM, 30),
            (RUN_ANIM, 30),
            (READY_TO_WALK, 15),
            (WALK_TO_READY, 15),
            (READY_TO_RUN, 15),
            (RUN_TO_READY, 15),
            (STANCE_LINK, 15),
            (ATTACK_LINK, 45),
            (TURN_ANIM, 30),
        ] {
            loader.add(id, rest_anim(frames, 1));
        }
        let mut table = TableBuilder::new(NON_COMBAT)
            .style_default(NON_COMBAT, READY)
            .style_default(COMBAT, READY)
            .cycle(NON_COMBAT, READY, motion_data(READY_NC, 30.0))
            .cycle(COMBAT, READY, motion_data(READY_C, 30.0));
        for style in [NON_COMBAT, COMBAT] {
            table = table
                .cycle(
                    style,
                    WALK,
                    with_velocity(motion_data(WALK_ANIM, 30.0), Vec3::new(0.0, 3.12, 0.0)),
                )
                .cycle(
                    style,
                    RUN,
                    with_velocity(motion_data(RUN_ANIM, 30.0), Vec3::new(0.0, 4.0, 0.0)),
                )
                .link(style, READY, WALK, motion_data(READY_TO_WALK, 30.0))
                .link(style, WALK, READY, motion_data(WALK_TO_READY, 30.0))
                .link(style, READY, RUN, motion_data(READY_TO_RUN, 30.0))
                .link(style, RUN, READY, motion_data(RUN_TO_READY, 30.0));
        }
        table = table
            .link(NON_COMBAT, READY, COMBAT, motion_data(STANCE_LINK, 30.0))
            .link(COMBAT, READY, NON_COMBAT, motion_data(STANCE_LINK, 30.0))
            .link(COMBAT, READY, ATTACK_ACTION, motion_data(ATTACK_LINK, 30.0))
            .global_modifier(
                TURN_RIGHT,
                with_omega(motion_data(TURN_ANIM, 30.0), Vec3::new(0.0, 0.0, -1.5)),
            );
        let mut seq = sequencer(table.rules(), loader);
        seq.initialize_state();
        seq.set_cycle(NON_COMBAT, READY, 1.0).ok();

        let mut body = PhysicsBody::new();
        body.state = PhysicsState::REPORT_COLLISIONS;
        body.transient_state =
            TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
        body.in_world = true;
        body.orientation = set_heading(0.0);

        let mut obj = MovementObject::new(CREATURE, body, RemoteWeenie);
        obj.cell_id = 1;
        obj.radius = OWN_RADIUS;
        obj.height = 1.0;
        obj.contact_means_walkable = true;
        obj.minterp_max_speed_unadjusted = true;
        obj.can_move_to = true;
        obj.position_attached = true;
        let prev_substate = seq.manager().state().substate;
        obj.sequencer = Some(seq);
        obj.sink = MotionSink::Sequencer;
        let mut h = Self {
            obj,
            player_pos: Vec3::ZERO,
            player_velocity: Vec3::ZERO,
            target_armed: false,
            last_delivery_time: f64::NEG_INFINITY,
            quantum: 0.5,
            now: 0.0,
            run_installs: 0,
            ticks_in_run: 0,
            ticks_in_walk_fwd: 0,
            total_ticks: 0,
            max_run_streak: 0,
            run_streak: 0,
            prev_substate,
        };
        h.with(|cx| {
            crate::movement::MovementManagerHost::make_move_to_manager(cx);
        });
        h
    }

    /// Run `f` on the creature, then answer its subscriptions.
    pub fn with<R>(&mut self, f: impl FnOnce(&mut ObjectCx<'_, RemoteWeenie>) -> R) -> R {
        self.obj.sim_time = self.now;
        let world = ChaseWorld {
            player_pos: self.player_pos,
        };
        let r = f(&mut self.obj.cx(&world));
        self.answer_messages();
        r
    }

    fn answer_messages(&mut self) {
        loop {
            let messages = self.obj.take_target_messages();
            if messages.is_empty() {
                return;
            }
            for m in messages {
                match m {
                    TargetMessage::AddVoyeur {
                        target, quantum, ..
                    } => {
                        self.target_armed = target == PLAYER;
                        if quantum != 0.0 {
                            self.quantum = quantum;
                        }
                        self.deliver_target_info();
                    }
                    TargetMessage::RemoveVoyeur { .. } => self.target_armed = false,
                    TargetMessage::ReceiveTargetUpdate { .. } => {}
                }
            }
        }
    }

    fn deliver_target_info(&mut self) {
        if !self.target_armed {
            return;
        }
        self.last_delivery_time = self.now;
        let pos = CellPosition::new(1, self.player_pos, Quat::IDENTITY);
        let info = TargetInfo::new(PLAYER.id, TargetStatus::Ok, pos, pos);
        let world = ChaseWorld {
            player_pos: self.player_pos,
        };
        let mut cx = self.obj.cx(&world);
        cx.movement_handle_update_target(info);
        cx.position_handle_update_target(info);
    }

    pub fn um_interpreted(
        &mut self,
        stance: u32,
        forward: u32,
        forward_speed: f32,
        actions: Vec<InboundMotionAction>,
    ) {
        self.with(|cx| {
            cx.interrupt_current_movement();
            cx.unstick_from_object();
            let ims = InboundInterpretedState {
                current_style: stance,
                forward_command: forward,
                forward_speed,
                actions: (!actions.is_empty()).then_some(actions),
                ..InboundInterpretedState::default()
            };
            cx.move_to_interpreted_state(&ims, SinkArg::Default);
        });
    }

    #[allow(clippy::too_many_arguments)]
    pub fn um_move_to_object(
        &mut self,
        speed: f32,
        sticky: bool,
        use_spheres: bool,
        target_radius: f32,
        target_height: f32,
        wire_stance: u32,
    ) {
        let player_pos = self.player_pos;
        self.with(|cx| {
            cx.interrupt_current_movement();
            cx.unstick_from_object();
            if wire_stance != 0 {
                let style = 0x8000_0000 | wire_stance;
                if cx.obj.interp.interpreted_state.current_style != style {
                    cx.do_motion(style, &MovementParameters::default());
                }
            }
            let mp = MovementParameters {
                can_walk: true,
                can_run: true,
                can_charge: true,
                can_sidestep: false,
                can_walk_backwards: false,
                speed,
                distance_to_object: 0.6,
                walk_run_threshold: 15.0,
                fail_distance: f32::MAX,
                sticky,
                use_spheres,
                ..MovementParameters::default()
            };
            cx.movement_perform(&MovementStruct {
                kind: MovementType::MoveToObject,
                object_id: PLAYER.id,
                top_level_id: PLAYER.id,
                pos: CellPosition::new(1, player_pos, Quat::IDENTITY),
                params: Some(mp),
                radius: target_radius,
                height: target_height,
                ..MovementStruct::default()
            });
        });
    }

    pub fn um_move_to(&mut self) {
        self.um_move_to_object(2.08, false, false, 0.0, 0.0, 0);
    }

    pub fn substate(&self) -> u32 {
        self.obj
            .sequencer
            .as_ref()
            .unwrap()
            .manager()
            .state()
            .substate
    }

    pub fn style(&self) -> u32 {
        self.obj.sequencer.as_ref().unwrap().manager().state().style
    }

    pub fn tick(&mut self) {
        self.now += f64::from(DT);
        self.total_ticks += 1;
        self.player_pos += self.player_velocity * DT;
        let mut root = ac_motion::Frame::IDENTITY;
        self.obj
            .sequencer
            .as_mut()
            .unwrap()
            .advance(DT, Some(&mut root));
        let mut delta = MotionDeltaFrame {
            origin: root.origin,
            orientation: root.orientation,
        };
        self.with(|cx| cx.position_adjust_offset(&mut delta, f64::from(DT)));
        let body = &mut self.obj.body;
        if delta.origin != Vec3::ZERO {
            let p = body.position() + body.orientation * delta.origin;
            body.set_position(p);
        }
        if delta.orientation != Quat::IDENTITY {
            body.orientation = (body.orientation * delta.orientation).normalize();
        }
        body.calc_acceleration();
        body.update_physics_internal(DT);
        self.with(|cx| cx.take_reached_points());
        if self.target_armed && self.now - self.last_delivery_time >= self.quantum {
            self.deliver_target_info();
        }
        self.with(|cx| {
            cx.movement_use_time();
            InterpreterHost::check_for_completed_motions(cx);
            cx.position_use_time();
        });
        let substate = self.substate();
        if substate == RUN {
            self.ticks_in_run += 1;
        }
        if substate == WALK {
            self.ticks_in_walk_fwd += 1;
        }
        let in_fwd = substate == RUN || substate == WALK;
        if in_fwd {
            self.run_streak += 1;
            self.max_run_streak = self.max_run_streak.max(self.run_streak);
        } else {
            self.run_streak = 0;
        }
        if in_fwd && self.prev_substate != RUN && self.prev_substate != WALK {
            self.run_installs += 1;
        }
        self.prev_substate = substate;
    }

    pub fn heading(&self) -> f32 {
        get_heading(self.obj.body.orientation)
    }

    pub fn dist_to_player(&self) -> f32 {
        self.obj.body.position().distance(self.player_pos)
    }

    pub fn sticky_id(&self) -> u32 {
        self.obj.position.get_sticky_object_id()
    }

    pub fn pending_animations(&self) -> usize {
        self.obj
            .sequencer
            .as_ref()
            .unwrap()
            .manager()
            .pending_animations()
            .count()
    }
}

fn seconds(s: f32) -> i32 {
    (s / DT).round() as i32
}

fn in_forward(h: &ChaseHarness) -> bool {
    h.substate() == RUN || h.substate() == WALK
}

// ── RemoteChaseEndToEndHarnessTests ───────────────────────────────────────

#[test]
fn a_single_arm_turns_then_installs_forward() {
    for bearing in [90.0f32, 270.0, 170.0] {
        let mut h = ChaseHarness::new();
        let rad = (90.0 - bearing).to_radians();
        h.player_pos = Vec3::new(rad.cos(), rad.sin(), 0.0) * 15.0;
        for _ in 0..seconds(0.5) {
            h.tick();
        }
        h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
        for _ in 0..seconds(1.5) {
            h.tick();
        }
        h.um_move_to();
        let mut install = -1;
        for i in 0..seconds(6.0) {
            h.tick();
            if install < 0 && in_forward(&h) {
                install = i;
            }
        }
        assert!(install >= 0, "never installed (bearing {bearing})");
        assert!(
            install <= seconds(4.0),
            "took {install} ticks (bearing {bearing})"
        );
    }
}

#[test]
fn a_fleeing_target_keeps_the_run_across_rearms() {
    let mut h = ChaseHarness::new();
    h.player_pos = Vec3::new(0.0, 10.0, 0.0);
    h.player_velocity = Vec3::new(0.0, 4.0, 0.0);
    h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
    for _ in 0..seconds(1.5) {
        h.tick();
    }
    let mut arms = 0;
    let chase_ticks = seconds(12.0);
    for i in 0..chase_ticks {
        if i % seconds(2.0) == 0 {
            h.um_move_to();
            arms += 1;
        }
        h.tick();
    }
    let fwd = h.ticks_in_run + h.ticks_in_walk_fwd;
    let fraction = fwd as f32 / chase_ticks as f32;
    assert!(
        h.run_installs >= arms - 1,
        "installs {} arms {arms}",
        h.run_installs
    );
    assert!(fraction > 0.5, "forward only {fraction}");
}

#[test]
fn after_an_attack_the_queue_drains_and_a_rearm_runs() {
    let mut h = ChaseHarness::new();
    h.player_pos = Vec3::new(0.0, 12.0, 0.0);
    h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
    for _ in 0..seconds(1.5) {
        h.tick();
    }
    h.um_move_to();
    for _ in 0..seconds(3.0) {
        h.tick();
    }
    h.um_interpreted(
        COMBAT,
        READY,
        1.0,
        vec![InboundMotionAction {
            command: ATTACK_ACTION,
            stamp: 1,
            autonomous: false,
            speed: 1.0,
        }],
    );
    for _ in 0..seconds(2.5) {
        h.tick();
    }
    assert!(
        !h.obj.interp.motions_pending(),
        "pending motions did not empty"
    );
    h.player_pos += Vec3::new(0.0, 10.0, 0.0);
    h.um_move_to();
    let mut install = -1;
    for i in 0..seconds(5.0) {
        h.tick();
        if install < 0 && in_forward(&h) {
            install = i;
        }
    }
    assert!(install >= 0, "no reinstall after the re-arm");
}

fn abs_heading_diff(a: f32, b: f32) -> f32 {
    let d = (a - b).abs() % 360.0;
    if d > 180.0 { 360.0 - d } else { d }
}

#[test]
fn a_sticky_arrival_tracks_a_strafing_target_then_the_lease_expires() {
    let mut h = ChaseHarness::new();
    h.player_pos = Vec3::new(0.0, 10.0, 0.0);
    h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
    for _ in 0..seconds(1.5) {
        h.tick();
    }
    h.um_move_to_object(2.08, true, true, STICKY_TARGET_RADIUS, 1.2, 0);
    let mut stick = -1;
    let mut i = 0;
    while i < seconds(8.0) && stick < 0 {
        h.tick();
        if h.sticky_id() == PLAYER.id {
            stick = i;
        }
        i += 1;
    }
    assert!(stick >= 0, "never stuck; dist {}", h.dist_to_player());
    assert_eq!(h.obj.move_to.movement_type_state, MovementType::Invalid);
    h.player_velocity = Vec3::new(2.0, 0.0, 0.0);
    for _ in 0..seconds(0.8) {
        h.tick();
    }
    assert_eq!(h.sticky_id(), PLAYER.id);
    let gap = cylinder_distance_no_z(
        OWN_RADIUS,
        h.obj.body.position(),
        STICKY_TARGET_RADIUS,
        h.player_pos,
    );
    assert!(
        (gap - StickyManager::STICKY_RADIUS).abs() < 0.1,
        "gap {gap}"
    );
    let bearing = position_heading(h.obj.body.position(), h.player_pos);
    assert!(abs_heading_diff(h.heading(), bearing) < 5.0);
    h.player_velocity = Vec3::ZERO;
    for _ in 0..seconds(0.5) {
        h.tick();
    }
    assert_eq!(h.sticky_id(), 0);
}

#[test]
fn the_next_move_unsticks_then_rearrival_resticks() {
    let mut h = ChaseHarness::new();
    h.player_pos = Vec3::new(0.0, 8.0, 0.0);
    h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
    for _ in 0..seconds(1.5) {
        h.tick();
    }
    h.um_move_to_object(2.08, true, true, STICKY_TARGET_RADIUS, 1.2, 0);
    for _ in 0..seconds(8.0) {
        h.tick();
        if h.sticky_id() != 0 {
            break;
        }
    }
    assert_eq!(h.sticky_id(), PLAYER.id);
    h.player_pos += Vec3::new(0.0, 8.0, 0.0);
    h.um_move_to_object(2.08, true, true, STICKY_TARGET_RADIUS, 1.2, 0);
    assert_eq!(h.sticky_id(), 0);
    assert_eq!(
        h.obj.move_to.movement_type_state,
        MovementType::MoveToObject
    );
    let mut restick = -1;
    let mut i = 0;
    while i < seconds(8.0) && restick < 0 {
        h.tick();
        if h.sticky_id() == PLAYER.id {
            restick = i;
        }
        i += 1;
    }
    assert!(restick >= 0, "no re-stick; dist {}", h.dist_to_player());
}

#[test]
fn a_chase_with_a_stance_applies_the_stance_first() {
    let mut h = ChaseHarness::new();
    h.player_pos = Vec3::new(0.0, 12.0, 0.0);
    for _ in 0..seconds(0.5) {
        h.tick();
    }
    assert_eq!(h.style(), NON_COMBAT);
    h.um_move_to_object(2.08, false, false, 0.0, 0.0, COMBAT & 0xFFFF);
    assert_eq!(h.obj.interp.interpreted_state.current_style, COMBAT);
    let mut install = -1;
    let mut i = 0;
    while i < seconds(6.0) && install < 0 {
        h.tick();
        if in_forward(&h) {
            install = i;
        }
        i += 1;
    }
    assert!(install >= 0);
    assert_eq!(h.style(), COMBAT);
}

// ── RemoteChaseDrainBisectTests ───────────────────────────────────────────

#[test]
fn a_stance_change_drains_tick_by_tick() {
    let mut h = ChaseHarness::new();
    for _ in 0..30 {
        h.tick();
    }
    h.um_interpreted(COMBAT, READY, 1.0, Vec::new());
    for _ in 1..=90 {
        h.now += f64::from(DT);
        h.with(|cx| {
            crate::movement::MoveToOps::move_to_use_time(cx);
            if cx.obj.body.on_walkable() {
                let v = cx.get_state_velocity();
                cx.obj.body.set_local_velocity(v, false);
            }
        });
        h.obj.sequencer.as_mut().unwrap().advance(DT, None);
        h.with(|cx| {
            cx.take_reached_points();
            InterpreterHost::check_for_completed_motions(cx);
        });
        if !h.obj.interp.motions_pending() && h.pending_animations() == 0 {
            return;
        }
    }
    panic!("the drain chain wedged after the stance change");
}

// ── LinkStripDrainTests ───────────────────────────────────────────────────

#[test]
fn dropping_link_animations_drains_both_queues() {
    let mut h = ChaseHarness::new();
    let p = MovementParameters::default();
    h.with(|cx| {
        cx.do_motion(ac_motion::motion_command::WALK_FORWARD, &p);
        cx.set_hold_run(true, false);
        cx.stop_motion(ac_motion::motion_command::WALK_FORWARD, &p);
    });
    assert!(h.obj.interp.motions_pending());
    assert!(h.pending_animations() > 0);
    h.with(|cx| cx.remove_link_animations());
    assert!(!h.obj.interp.motions_pending());
    assert_eq!(h.pending_animations(), 0);
}

#[test]
fn after_the_drain_new_motions_queue_and_complete() {
    let mut h = ChaseHarness::new();
    let p = MovementParameters::default();
    h.with(|cx| {
        cx.do_motion(ac_motion::motion_command::WALK_FORWARD, &p);
        cx.remove_link_animations();
    });
    assert!(!h.obj.interp.motions_pending());
    h.with(|cx| {
        cx.do_motion(ac_motion::motion_command::TURN_RIGHT, &p);
    });
    assert!(h.obj.interp.motions_pending());
    while h.pending_animations() > 0 {
        h.with(|cx| cx.animation_done(true));
    }
    h.with(|cx| InterpreterHost::check_for_completed_motions(cx));
    assert!(!h.obj.interp.motions_pending());
}
