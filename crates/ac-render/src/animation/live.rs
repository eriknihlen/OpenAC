//! One server object's animation as the window poses it.
//!
//! The session owns the object's motion and moves its body; the window
//! keeps its own copy of the object's motion interpreter and motion table
//! (built from the object's Setup and motion table) and drives it with the
//! motion state the session publishes, the way the game drives every
//! object: a changed interpreted state, or one the server sent again (a
//! repeated swing), is adopted once (stance, forward,
//! sidestep and turn, then the actions newer than the last stamp seen),
//! leaving or reaching the ground re-applies the motion, and the
//! interpreter hears when each motion's animations finish. Left turns,
//! left sidesteps and walking backwards arrive already turned into their
//! right/forward forms at negative speed, as the interpreter keeps them.
//!
//! Time comes in per frame and is cut into the game's 30 Hz object quanta
//! (an object farther than 96 m from the player is asleep and holds its
//! pose); a static object (doors, for one) steps by the whole frame
//! instead, like the dat statics.

use std::sync::Arc;

use ac_core::physics::{PhysicsState, TransientState};
use ac_dat::objects::{HookData, Setup};
use ac_motion::{AnimationLoader, AnimationSequencer, Frame, MotionTableRules, PartTransform};
use ac_physics::PhysicsBody;
use ac_physics::movement::{
    InboundInterpretedState, InterpreterHost, MotionInterp, MotionSink, MovementObject, NoTargets,
    ObjectToken, RemoteWeenie, SinkArg,
};
use ac_physics::quantum::{RetailObjectActivityResult, evaluate_activity};
use ac_runtime::world::{MotionView, ObjectKey, ObjectView};
use glam::{Quat, Vec3};

use super::hooks::{HookFrameQueue, HookOwner};
use super::pose::{PartPose, compose, rest_poses};
use super::statics::StaticStep;

/// One server object's animation. See the module documentation.
#[derive(Debug)]
pub struct LiveAnimation {
    /// The object.
    pub key: ObjectKey,
    /// Its server id.
    pub guid: u32,
    /// Its Setup (dat id).
    pub setup_id: u32,
    /// Its motion table (dat id), when it has one.
    pub motion_table_id: Option<u32>,
    /// Its Setup.
    pub setup: Arc<Setup>,
    obj: MovementObject<RemoteWeenie>,
    /// Motions go through the interpreter (the object has a motion table).
    interpreted: bool,
    applied: Option<InboundInterpretedState>,
    /// The serial of the motion last followed, with whether the session's
    /// physics moved the body then (each counts on its own).
    applied_serial: Option<(bool, u32)>,
    /// The motion log follows this object (`OPENAC_MOTION_LOG`).
    log: bool,
    grounded: bool,
    hidden: bool,
    is_static: bool,
    /// Whole-elapsed stepping and spin, for static objects.
    pub step: StaticStep,
    /// Its parts' poses (object-local, see [`PartPose`]).
    pub poses: Vec<PartPose>,
    /// The size it wears.
    pub scale: f32,
    frames: Vec<PartTransform>,
}

impl LiveAnimation {
    /// The animation of `view`, drawn with `setup`, moved by `table`. With
    /// a motion table (`interpreted`) the table enters its default state at
    /// once (the idle cycle), the view's motion is adopted, and the object
    /// enters the world (the links into its motion dropped, so it starts in
    /// that motion's cycle); without one the Setup's default animation
    /// plays.
    #[must_use]
    pub fn new(
        view: &ObjectView,
        setup: Arc<Setup>,
        table: Arc<MotionTableRules>,
        interpreted: bool,
        loader: Arc<dyn AnimationLoader + Send + Sync>,
    ) -> Self {
        let mut sequencer = AnimationSequencer::from_setup(&setup, table, loader);
        if interpreted {
            sequencer.initialize_state();
        }
        let token = ObjectToken {
            id: view.guid,
            incarnation: u64::from(view.key.incarnation),
        };
        let mut body = PhysicsBody::new();
        body.in_world = true;
        body.transient_state =
            TransientState::CONTACT | TransientState::ON_WALKABLE | TransientState::ACTIVE;
        let mut obj = MovementObject::new(token, body, RemoteWeenie);
        obj.sequencer = Some(sequencer);
        obj.sink = MotionSink::Sequencer;
        let scale = view.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let mut animation = Self {
            key: view.key,
            guid: view.guid,
            setup_id: view.setup_id.unwrap_or(0),
            motion_table_id: view.motion_table_id,
            poses: rest_poses(&setup, Some(view.rest_placement), scale),
            setup,
            obj,
            interpreted,
            applied: None,
            applied_serial: None,
            log: false,
            grounded: true,
            hidden: false,
            is_static: false,
            step: StaticStep::default(),
            scale,
            frames: Vec::new(),
        };
        animation.follow(view);
        if interpreted {
            // The object enters the world in the state it was described in:
            // the links into that state are dropped, so it shows that
            // state's cycle at once. A corpse described as dead lies dead;
            // it does not fall again.
            let mut cx = animation.obj.cx(&NoTargets);
            InterpreterHost::remove_link_animations(&mut cx);
        }
        animation
    }

    /// The sequencer (tests and diagnostics).
    #[must_use]
    pub fn sequencer(&self) -> Option<&AnimationSequencer> {
        self.obj.sequencer.as_ref()
    }

    /// How many motions the interpreter waits on (tests and diagnostics).
    #[must_use]
    pub fn pending_motions(&self) -> usize {
        self.obj.interp.pending_motion_count()
    }

    /// Follow the object's latest view: its size, its physics state, its
    /// ground contact (leaving or reaching the ground re-applies the
    /// motion) and its motion state (adopted when it changed, or when the
    /// session took it up again). Returns whether a motion state was
    /// adopted.
    pub fn follow(&mut self, view: &ObjectView) -> bool {
        self.scale = view.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let state = PhysicsState::from(view.state);
        let hidden = state.contains(PhysicsState::HIDDEN);
        if hidden != self.hidden {
            // Going hidden or coming back re-enters the animation into the
            // world: its link animations are dropped, as the session's body
            // drops them.
            self.log_edge(if hidden { "went hidden" } else { "came back" });
            let mut cx = self.obj.cx(&NoTargets);
            InterpreterHost::remove_link_animations(&mut cx);
        }
        self.hidden = hidden;
        self.is_static = state.contains(PhysicsState::STATIC);
        self.obj.body.state = state;
        match &view.motion {
            Some(motion) => self.follow_motion(motion),
            None => false,
        }
    }

    /// Follow one motion the object showed (its latest view's, or one its
    /// own movement passed through between server states): its ground
    /// contact, and its state when it changed or the session took it up
    /// again. Returns whether a motion state was adopted.
    pub fn follow_motion(&mut self, motion: &MotionView) -> bool {
        if !self.interpreted {
            return false;
        }
        // A body the session moves had its standing long jump when its axes
        // changed (keys pressed while a jump charges from standing still
        // keep it in its ready pose); a server state sets it once taken up.
        let live = motion.body.is_some();
        if live {
            self.obj.interp.standing_long_jump = motion.standing_long_jump;
        }
        self.follow_ground(motion.grounded);
        // The game takes up every interpreted state the server sends, a
        // repeated one too: a swing sent again as the same forward action
        // plays again. A state the session took up once more (its serial
        // grew, counted by the same owner as before) is adopted again even
        // when it reads the same; the queued states (`take_up`) normally
        // bring it first, leaving the serial as seen.
        let source = (motion.body.is_some(), motion.serial);
        let repeated = self
            .applied_serial
            .is_some_and(|(live, serial)| live == source.0 && serial != source.1);
        let adopt = repeated || self.applied.as_ref() != Some(&motion.state);
        if adopt {
            let mut cx = self.obj.cx(&NoTargets);
            cx.move_to_interpreted_state(&motion.state, SinkArg::Default);
            self.applied = Some(motion.state.clone());
        }
        if !live {
            self.obj.interp.standing_long_jump = motion.standing_long_jump;
        }
        self.applied_serial = Some(source);
        adopt
    }

    /// Take up one motion state the object took up in the session, in the
    /// order it did: its ground contact, then its interpreted state, even
    /// when it reads the same as the last (a swing sent again). A state
    /// already taken up (the same serial from the same owner) is skipped.
    /// Returns whether it was taken up.
    pub fn take_up(&mut self, motion: &MotionView) -> bool {
        if !self.interpreted {
            return false;
        }
        let source = (motion.body.is_some(), motion.serial);
        if self.applied_serial == Some(source) {
            return false;
        }
        self.follow_ground(motion.grounded);
        let mut cx = self.obj.cx(&NoTargets);
        cx.move_to_interpreted_state(&motion.state, SinkArg::Default);
        // As the session did: the standing long jump is set once the state
        // is taken up.
        self.obj.interp.standing_long_jump = motion.standing_long_jump;
        self.applied = Some(motion.state.clone());
        self.applied_serial = Some(source);
        true
    }

    /// Leaving or reaching the ground re-applies the motion (not before
    /// the first motion was adopted).
    fn follow_ground(&mut self, grounded: bool) {
        if grounded == self.grounded {
            return;
        }
        let first = self.applied.is_none();
        self.grounded = grounded;
        let contact = TransientState::CONTACT | TransientState::ON_WALKABLE;
        if grounded {
            self.obj.body.transient_state.insert(contact);
        } else {
            self.obj.body.transient_state.remove(contact);
        }
        if !first {
            // Landing or leaving the ground drops the link animations (an
            // action under way among them), as the session's body does.
            self.log_edge(if grounded {
                "hit the ground"
            } else {
                "left the ground"
            });
            let mut cx = self.obj.cx(&NoTargets);
            if grounded {
                cx.hit_ground();
            } else {
                cx.leave_ground();
            }
        }
    }

    /// Whether the motion log follows this object.
    pub fn set_motion_log(&mut self, on: bool) {
        self.log = on;
    }

    /// The animation drawn now: its dat id, the frame under the cursor,
    /// and its last frame (`None` before any animation is queued).
    #[must_use]
    pub fn drawing(&self) -> Option<(u32, f64, i32)> {
        let sequence = self.obj.sequencer.as_ref()?.sequence();
        let node = sequence.curr_anim()?;
        Some((
            node.anim().map_or(0, |a| a.id),
            sequence.frame_number(),
            node.high_frame,
        ))
    }

    fn log_edge(&self, what: &str) {
        if self.log {
            eprintln!(
                "[motion] {} window 0x{:08X} {what}: link animations dropped",
                ac_runtime::world::motion_log_stamp(),
                self.guid
            );
        }
    }

    /// The motion log's line for a motion state the window `did` (took
    /// up, followed): the object, the state, and the motions its
    /// animation now plays out (the queue of motions waiting on their
    /// animations, oldest first) and the cycle under them.
    #[must_use]
    pub fn motion_log_line(&self, did: &str, motion: &MotionView) -> String {
        use std::fmt::Write;
        let mut line = format!(
            "[motion] {} window 0x{:08X} {did} serial={} {} playing=[",
            ac_runtime::world::motion_log_stamp(),
            self.guid,
            motion.serial,
            ac_runtime::world::motion_line(&motion.state)
        );
        if let Some(sequencer) = self.obj.sequencer.as_ref() {
            for (i, pending) in sequencer.manager().pending_animations().enumerate() {
                if i > 0 {
                    line.push(' ');
                }
                let _ = write!(line, "0x{:08X}", pending.motion);
            }
            let _ = write!(line, "] cycle=0x{:08X}", sequencer.current_motion());
        } else {
            line.push(']');
        }
        line
    }

    /// Advance by a frame of `elapsed` seconds with the object's root at
    /// `position` (drawing frame) and the player at `player`, capturing the
    /// hooks into `queue`. Returns whether the pose moved.
    pub fn tick(
        &mut self,
        elapsed: f64,
        position: Vec3,
        player: Option<Vec3>,
        queue: &mut HookFrameQueue,
    ) -> bool {
        if self.is_static {
            return self.tick_static(elapsed, queue);
        }
        let gate = evaluate_activity(
            &mut self.obj.clock,
            Some(&mut self.obj.body),
            true,
            true,
            false,
            position,
            player,
            elapsed,
        );
        if gate != RetailObjectActivityResult::Active {
            return false;
        }
        let batch = self.obj.clock.advance(elapsed);
        if batch.count() == 0 {
            return false;
        }
        let mut posed = false;
        for qi in 0..batch.count() {
            let quantum = batch.get_quantum(qi).unwrap_or(0.0);
            if !self.hidden
                && let Some(sequencer) = self.obj.sequencer.as_mut()
            {
                let mut root = Frame::IDENTITY;
                let frames = sequencer.advance(quantum, Some(&mut root));
                self.frames.clear();
                self.frames.extend_from_slice(frames);
                posed = true;
            }
            self.capture_hooks(queue);
            let mut cx = self.obj.cx(&NoTargets);
            InterpreterHost::check_for_completed_motions(&mut cx);
        }
        if self.hidden
            && let Some(sequencer) = self.obj.sequencer.as_mut()
        {
            self.frames.clear();
            self.frames
                .extend_from_slice(sequencer.sample_current_pose());
            posed = true;
        }
        if posed {
            compose(&self.setup, &self.frames, self.scale, &mut self.poses);
        }
        posed
    }

    fn tick_static(&mut self, elapsed: f64, queue: &mut HookFrameQueue) -> bool {
        let Some(step) = self.step.take(elapsed) else {
            return false;
        };
        let Some(sequencer) = self.obj.sequencer.as_mut() else {
            return false;
        };
        let frames = sequencer.advance(step as f32, None);
        self.frames.clear();
        self.frames.extend_from_slice(frames);
        compose(&self.setup, &self.frames, self.scale, &mut self.poses);
        self.step.turn(step);
        if let Some(sequencer) = self.obj.sequencer.as_ref() {
            self.step.apply_omega_hooks(sequencer.pending_hooks());
        }
        self.capture_hooks(queue);
        let mut cx = self.obj.cx(&NoTargets);
        InterpreterHost::check_for_completed_motions(&mut cx);
        true
    }

    /// Take the hooks the last step reached: an animation-done hook
    /// completes its motion at once; all of them go to the frame's queue.
    fn capture_hooks(&mut self, queue: &mut HookFrameQueue) {
        let Some(sequencer) = self.obj.sequencer.as_mut() else {
            return;
        };
        let hooks = sequencer.consume_pending_hooks();
        let done = hooks
            .iter()
            .filter(|h| matches!(h.data, HookData::AnimationDone))
            .count();
        let mut cx = self.obj.cx(&NoTargets);
        for _ in 0..done {
            cx.animation_done(true);
        }
        queue.capture(HookOwner::Object(self.key.local_id), self.guid, hooks);
    }

    /// The root turn a spin hook made (static objects), applied before the
    /// object's own orientation.
    #[must_use]
    pub fn spin(&self) -> Quat {
        self.step.spin
    }
}
