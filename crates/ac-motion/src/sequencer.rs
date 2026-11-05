//! One object's animation: its sequence, its motion-table manager, the
//! hooks fired since they were last taken, and the per-part pose.
//!
//! This is the surface the runtime and the renderer use: start cycles and
//! actions, advance time (collecting root motion and hooks), and read the
//! blended pose of every part.

use std::sync::Arc;

use ac_dat::objects::{AnimData, AnimationHook, Setup};
use glam::{Quat, Vec3};

use crate::anim_node::AnimationLoader;
use crate::cycle_playback::lerp;
use crate::frame::{Frame, quat_from_dat, vec3_from_dat};
use crate::manager::{MotionDoneSink, MotionTableError, MotionTableManager, MotionTableMovement};
use crate::motion_table::MotionTableRules;
use crate::sequence::Sequence;

/// One part's placement relative to the object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PartTransform {
    /// Position, metres.
    pub origin: Vec3,
    /// Orientation.
    pub orientation: Quat,
}

impl PartTransform {
    const IDENTITY: Self = Self {
        origin: Vec3::ZERO,
        orientation: Quat::IDENTITY,
    };
}

/// The animation of one object.
#[derive(Debug)]
pub struct AnimationSequencer {
    part_count: usize,
    sequence: Sequence,
    manager: MotionTableManager,
    initialized: bool,
    pending_hooks: Vec<AnimationHook>,
    part_transforms: Vec<PartTransform>,
}

impl AnimationSequencer {
    /// A sequencer for an object of `part_count` parts using `table`. A
    /// non-zero `default_animation` starts playing at once, before any
    /// motion.
    #[must_use]
    pub fn new(
        part_count: usize,
        default_animation: u32,
        table: Arc<MotionTableRules>,
        loader: Arc<dyn AnimationLoader + Send + Sync>,
    ) -> Self {
        let mut sequencer = Self {
            part_count,
            sequence: Sequence::new(loader),
            manager: MotionTableManager::new(Some(table)),
            initialized: false,
            pending_hooks: Vec::new(),
            part_transforms: vec![PartTransform::IDENTITY; part_count],
        };
        sequencer.initialize_setup_default_animation(default_animation);
        sequencer
    }

    /// A sequencer for `setup` (its part count and default animation).
    #[must_use]
    pub fn from_setup(
        setup: &Setup,
        table: Arc<MotionTableRules>,
        loader: Arc<dyn AnimationLoader + Send + Sync>,
    ) -> Self {
        Self::new(setup.parts.len(), setup.default_animation, table, loader)
    }

    // ── state ───────────────────────────────────────────────────────────

    /// Current stance.
    #[must_use]
    pub fn current_style(&self) -> u32 {
        self.manager.state().style
    }

    /// Current looping state (after left/backward motions are mapped to
    /// their right/forward counterparts at negative speed).
    #[must_use]
    pub fn current_motion(&self) -> u32 {
        self.manager.state().substate
    }

    /// Speed factor of the looping state (signed).
    #[must_use]
    pub fn current_speed_mod(&self) -> f32 {
        self.manager.state().substate_mod
    }

    /// The sequence's velocity, metres per second in the object's axes.
    #[must_use]
    pub fn current_velocity(&self) -> Vec3 {
        self.sequence.velocity()
    }

    /// The sequence's spin, radians per second.
    #[must_use]
    pub fn current_omega(&self) -> Vec3 {
        self.sequence.omega()
    }

    /// Number of animations queued.
    #[must_use]
    pub fn queue_count(&self) -> usize {
        self.sequence.count()
    }

    /// True when an animation is under the cursor.
    #[must_use]
    pub fn has_current_node(&self) -> bool {
        self.sequence.curr_anim().is_some()
    }

    /// The sequence.
    #[must_use]
    pub fn sequence(&self) -> &Sequence {
        &self.sequence
    }

    /// The sequence, for hosts that set placement or physics directly.
    pub fn sequence_mut(&mut self) -> &mut Sequence {
        &mut self.sequence
    }

    /// The motion-table manager.
    #[must_use]
    pub fn manager(&self) -> &MotionTableManager {
        &self.manager
    }

    // ── motion ──────────────────────────────────────────────────────────

    fn ensure_initialized(&mut self) {
        if !self.initialized {
            self.initialized = true;
            self.manager.initialize_state(&mut self.sequence);
        }
    }

    /// Reset to the table's default state now, if that has not happened
    /// yet (it otherwise happens on the first motion).
    pub fn initialize_state(&mut self) {
        self.ensure_initialized();
    }

    /// Reset the motion table to its default state now (the interpreter's
    /// "initialise motion tables"), whether or not that happened before.
    /// The lazy first-motion reset is not marked done by this, as in OpenAC,
    /// so the first motion afterwards resets once more.
    pub fn manager_initialize_state(&mut self) {
        self.manager.initialize_state(&mut self.sequence);
    }

    /// Report every queued motion that waits for no animation.
    pub fn check_for_completed_motions(&mut self, sink: &mut dyn MotionDoneSink) {
        self.manager.check_for_completed_motions(sink);
    }

    /// Switch to looping `motion` in `style` at `speed_mod`. The motion goes
    /// to the table as given: left turns, left sidesteps and walking
    /// backwards are the motion interpreter's to turn into their
    /// right/forward counterparts at negative speed. A different non-zero
    /// `style` is entered first; when the
    /// table refuses the stance the object stays in its old stance (see
    /// [`AnimationSequencer::current_style`]) and the cycle is still tried
    /// there. The result is the cycle's: when the table refuses it, the
    /// sequence and the looping state are unchanged.
    pub fn set_cycle(
        &mut self,
        style: u32,
        motion: u32,
        speed_mod: f32,
    ) -> Result<(), MotionTableError> {
        self.ensure_initialized();

        if style != 0 && style != self.manager.state().style {
            // Outcome visible through `current_style`.
            self.manager
                .perform_movement(
                    &mut self.sequence,
                    MotionTableMovement::interpreted(style, 1.0),
                )
                .ok();
        }
        self.perform_movement(MotionTableMovement::interpreted(motion, speed_mod))
    }

    /// Play a one-shot action, modifier or emote over the current cycle.
    /// When the table refuses it nothing changes.
    pub fn play_action(
        &mut self,
        motion_command: u32,
        speed_mod: f32,
    ) -> Result<(), MotionTableError> {
        self.perform_movement(MotionTableMovement::interpreted(motion_command, speed_mod))
    }

    /// Apply a movement request to the motion table.
    pub fn perform_movement(
        &mut self,
        movement: MotionTableMovement,
    ) -> Result<(), MotionTableError> {
        self.ensure_initialized();
        self.manager.perform_movement(&mut self.sequence, movement)
    }

    /// Remove every link animation, so the cyclic tail plays at once.
    pub fn remove_all_link_animations(&mut self) {
        self.sequence.remove_all_link_animations();
    }

    /// Replace the animations with `animation_id` (whole span, 30 frames
    /// per second); velocity, spin and placement stay. Returns whether it
    /// resolved.
    pub fn initialize_setup_default_animation(&mut self, animation_id: u32) -> bool {
        if animation_id == 0 {
            return false;
        }
        self.sequence.clear_animations();
        self.pending_hooks.clear();
        self.sequence.append_animation(&AnimData {
            anim_id: animation_id,
            low_frame: 0,
            high_frame: -1,
            framerate: 30.0,
        });
        self.sequence.curr_anim().is_some()
    }

    // ── completions ─────────────────────────────────────────────────────

    /// An animation finished (the host saw an animation-done hook).
    pub fn animation_done(&mut self, success: bool, sink: &mut dyn MotionDoneSink) {
        self.manager.animation_done(success, sink);
    }

    /// Per-tick completion check.
    pub fn use_time(&mut self, sink: &mut dyn MotionDoneSink) {
        self.manager.use_time(sink);
    }

    /// Entering the world: drop link animations, abandon queued motions.
    pub fn handle_enter_world(&mut self, sink: &mut dyn MotionDoneSink) {
        self.manager.handle_enter_world(&mut self.sequence, sink);
    }

    /// Leaving the world: abandon queued motions.
    pub fn handle_exit_world(&mut self, sink: &mut dyn MotionDoneSink) {
        self.manager.handle_exit_world(sink);
    }

    /// Back to nothing playing (the table stays): queued motions are
    /// abandoned, the sequence and state cleared, and the default state is
    /// entered again on the next motion.
    pub fn reset(&mut self, sink: &mut dyn MotionDoneSink) {
        self.manager.handle_exit_world(sink);
        self.sequence.clear();
        self.pending_hooks.clear();
        let state = self.manager.state_mut();
        state.style = 0;
        state.substate = 0;
        state.substate_mod = 1.0;
        state.clear_modifiers();
        state.clear_actions();
        self.initialized = false;
    }

    // ── time ────────────────────────────────────────────────────────────

    /// Advance `dt` seconds, collecting root motion into `root_motion_frame`
    /// and hooks into the pending list, and return every part's blended
    /// pose. With nothing playing and no frame the parts are at rest; a
    /// non-positive `dt` only samples the pose.
    pub fn advance(&mut self, dt: f32, root_motion_frame: Option<&mut Frame>) -> &[PartTransform] {
        if self.sequence.curr_anim().is_none() && root_motion_frame.is_none() {
            return self.identity_frame();
        }
        if dt <= 0.0 {
            return self.sample_current_pose();
        }
        self.sequence.update(
            f64::from(dt),
            root_motion_frame,
            Some(&mut self.pending_hooks),
        );
        if self.sequence.curr_anim().is_none() {
            self.identity_frame()
        } else {
            self.blended_frame()
        }
    }

    /// [`AnimationSequencer::advance`] without building the pose: the same
    /// sequence update, root motion and hooks.
    pub fn advance_root_motion_only(&mut self, dt: f32, root_motion_frame: Option<&mut Frame>) {
        if self.sequence.curr_anim().is_none() && root_motion_frame.is_none() {
            return;
        }
        if dt <= 0.0 {
            return;
        }
        self.sequence.update(
            f64::from(dt),
            root_motion_frame,
            Some(&mut self.pending_hooks),
        );
    }

    /// The current pose, without advancing time.
    pub fn sample_current_pose(&mut self) -> &[PartTransform] {
        if self.sequence.curr_anim().is_none() {
            self.identity_frame()
        } else {
            self.blended_frame()
        }
    }

    /// Hooks fired since the last [`AnimationSequencer::consume_pending_hooks`].
    #[must_use]
    pub fn pending_hooks(&self) -> &[AnimationHook] {
        &self.pending_hooks
    }

    /// Take the hooks fired so far, in firing order.
    pub fn consume_pending_hooks(&mut self) -> Vec<AnimationHook> {
        std::mem::take(&mut self.pending_hooks)
    }

    fn identity_frame(&mut self) -> &[PartTransform] {
        self.part_transforms.fill(PartTransform::IDENTITY);
        &self.part_transforms
    }

    /// Blend each part between the keyframe under the cursor and the next
    /// one in the playback direction, holding the last keyframe of the span
    /// rather than blending across the loop seam.
    fn blended_frame(&mut self) -> &[PartTransform] {
        let Some(curr) = self.sequence.curr_anim() else {
            return self.identity_frame();
        };
        let Some(anim) = curr.anim() else {
            return self.identity_frame();
        };
        let num_part_frames = i32::try_from(anim.part_frames.len()).unwrap_or(i32::MAX);
        let range_lo = curr.low_frame.min(curr.high_frame).max(0);
        let range_hi = curr.low_frame.max(curr.high_frame).min(num_part_frames - 1);

        let frame_number = self.sequence.frame_number();
        let floor = frame_number.floor();
        let frame_idx = (floor as i32).max(range_lo).min(range_hi);
        let next_idx = if curr.framerate >= 0.0 {
            let n = frame_idx + 1;
            if n > range_hi || n >= num_part_frames {
                frame_idx
            } else {
                n
            }
        } else {
            let n = frame_idx - 1;
            if n < range_lo { frame_idx } else { n }
        };
        let t = (frame_number - floor).clamp(0.0, 1.0) as f32;

        let (Some(f0), Some(f1)) = (
            usize::try_from(frame_idx)
                .ok()
                .and_then(|i| anim.part_frames.get(i)),
            usize::try_from(next_idx)
                .ok()
                .and_then(|i| anim.part_frames.get(i)),
        ) else {
            return self.identity_frame();
        };

        let authored = self.part_count.min(f0.frames.len());
        for i in 0..authored {
            let p0 = &f0.frames[i];
            let p1 = f1.frames.get(i).unwrap_or(p0);
            self.part_transforms[i] = PartTransform {
                origin: lerp(vec3_from_dat(p0.origin), vec3_from_dat(p1.origin), t),
                orientation: slerp_retail_client(
                    quat_from_dat(p0.orientation),
                    quat_from_dat(p1.orientation),
                    t,
                ),
            };
        }
        &self.part_transforms[..authored]
    }
}

/// Spherical blend of two rotations along the shorter arc. Falls back to a
/// straight blend when they are nearly equal or when the spherical weights
/// leave `[0, 1]`. The result is not renormalised.
#[must_use]
pub fn slerp_retail_client(q1: Quat, q2: Quat, t: f32) -> Quat {
    const SLERP_EPSILON: f32 = 1e-4;
    let mut dot = q1.w * q2.w + q1.x * q2.x + q1.y * q2.y + q1.z * q2.z;
    let q2s = if dot < 0.0 {
        dot = -dot;
        Quat::from_xyzw(-q2.x, -q2.y, -q2.z, -q2.w)
    } else {
        q2
    };

    let (w1, w2) = if 1.0 - dot <= SLERP_EPSILON {
        (1.0 - t, t)
    } else {
        let omega = dot.acos();
        let inv_sin = 1.0 / omega.sin();
        let c1 = ((1.0 - t) * omega).sin() * inv_sin;
        let c2 = (t * omega).sin() * inv_sin;
        if (0.0..=1.0).contains(&c1) && (0.0..=1.0).contains(&c2) {
            (c1, c2)
        } else {
            (1.0 - t, t)
        }
    };
    Quat::from_xyzw(
        w1 * q1.x + w2 * q2s.x,
        w1 * q1.y + w2 * q2s.y,
        w1 * q1.z + w2 * q2s.z,
        w1 * q1.w + w2 * q2s.w,
    )
}

#[cfg(test)]
mod tests;
