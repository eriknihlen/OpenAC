//! The animation sequence: the list of animations an object is playing, the
//! playback cursor, and the motion they carry.
//!
//! The list is "link" animations (one-shot transitions) followed by the
//! cyclic tail, the animations that loop. When the cursor runs off the end
//! of the list it wraps to the start of the cyclic tail. Advancing time
//! fires the hooks of every keyframe the cursor leaves and, given a frame,
//! accumulates "root motion": the whole-object movement authored in the
//! animations plus the sequence's velocity and spin.

use std::fmt;
use std::sync::Arc;

use ac_dat::objects::{AnimData, AnimationFrame, AnimationHook, HookData};
use glam::Vec3;

use crate::anim_node::{AnimSequenceNode, AnimationLoader};
use crate::frame::{F_EPSILON, Frame};

/// The hook queued when a link animation finishes.
pub const ANIMATION_DONE_HOOK: AnimationHook = AnimationHook {
    direction: 0,
    data: HookData::AnimationDone,
};

/// Receives the hooks the sequence fires while it advances.
pub trait AnimHookQueue {
    /// A keyframe hook whose direction matched the playback direction.
    fn add_anim_hook(&mut self, hook: &AnimationHook);
    /// A link animation finished.
    fn add_anim_done_hook(&mut self);
}

/// Queues hooks in order; a finished link becomes [`ANIMATION_DONE_HOOK`].
impl AnimHookQueue for Vec<AnimationHook> {
    fn add_anim_hook(&mut self, hook: &AnimationHook) {
        self.push(*hook);
    }

    fn add_anim_done_hook(&mut self) {
        self.push(ANIMATION_DONE_HOOK);
    }
}

/// The animation list, cursor and accumulated motion of one object.
pub struct Sequence {
    anim_list: Vec<AnimSequenceNode>,
    first_cyclic: Option<usize>,
    curr_anim: Option<usize>,
    loader: Arc<dyn AnimationLoader + Send + Sync>,
    frame_number: f64,
    velocity: Vec3,
    omega: Vec3,
    placement_frame: Option<AnimationFrame>,
    placement_frame_id: u32,
}

impl fmt::Debug for Sequence {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Sequence")
            .field("anim_list", &self.anim_list.len())
            .field("first_cyclic", &self.first_cyclic)
            .field("curr_anim", &self.curr_anim)
            .field("frame_number", &self.frame_number)
            .field("velocity", &self.velocity)
            .field("omega", &self.omega)
            .field("placement_frame_id", &self.placement_frame_id)
            .finish_non_exhaustive()
    }
}

impl Sequence {
    /// An empty sequence resolving animations through `loader`.
    #[must_use]
    pub fn new(loader: Arc<dyn AnimationLoader + Send + Sync>) -> Self {
        Self {
            anim_list: Vec::new(),
            first_cyclic: None,
            curr_anim: None,
            loader,
            frame_number: 0.0,
            velocity: Vec3::ZERO,
            omega: Vec3::ZERO,
            placement_frame: None,
            placement_frame_id: 0,
        }
    }

    // ── inspection ──────────────────────────────────────────────────────

    /// The animations, in play order.
    #[must_use]
    pub fn nodes(&self) -> &[AnimSequenceNode] {
        &self.anim_list
    }

    /// Number of animations in the list.
    #[must_use]
    pub fn count(&self) -> usize {
        self.anim_list.len()
    }

    /// True when the list is not empty.
    #[must_use]
    pub fn has_anims(&self) -> bool {
        !self.anim_list.is_empty()
    }

    /// The animation under the cursor.
    #[must_use]
    pub fn curr_anim(&self) -> Option<&AnimSequenceNode> {
        self.curr_anim.map(|i| &self.anim_list[i])
    }

    /// Index of the animation under the cursor.
    #[must_use]
    pub fn curr_anim_index(&self) -> Option<usize> {
        self.curr_anim
    }

    /// The first animation of the cyclic tail.
    #[must_use]
    pub fn first_cyclic(&self) -> Option<&AnimSequenceNode> {
        self.first_cyclic.map(|i| &self.anim_list[i])
    }

    /// Index of the first animation of the cyclic tail.
    #[must_use]
    pub fn first_cyclic_index(&self) -> Option<usize> {
        self.first_cyclic
    }

    /// The cursor: a fractional keyframe number in the current animation.
    #[must_use]
    pub fn frame_number(&self) -> f64 {
        self.frame_number
    }

    /// Move the cursor within the current animation.
    pub fn set_frame_number(&mut self, frame_number: f64) {
        self.frame_number = frame_number;
    }

    /// Velocity, metres per second in the object's own axes.
    #[must_use]
    pub fn velocity(&self) -> Vec3 {
        self.velocity
    }

    /// Spin, radians per second (axis times rate).
    #[must_use]
    pub fn omega(&self) -> Vec3 {
        self.omega
    }

    /// The pose used while no animation plays.
    #[must_use]
    pub fn placement_frame(&self) -> Option<&AnimationFrame> {
        self.placement_frame.as_ref()
    }

    /// The id of the placement pose.
    #[must_use]
    pub fn placement_frame_id(&self) -> u32 {
        self.placement_frame_id
    }

    #[cfg(test)]
    pub(crate) fn set_curr_anim_for_test(&mut self, index: usize) {
        self.curr_anim = (index < self.anim_list.len()).then_some(index);
    }

    // ── list building ───────────────────────────────────────────────────

    /// Append an animation; it becomes the cyclic tail. An animation that
    /// does not resolve is dropped. The first animation also places the
    /// cursor at its starting frame.
    pub fn append_animation(&mut self, anim_data: &AnimData) {
        let node = AnimSequenceNode::from_anim_data(anim_data, &*self.loader);
        if !node.has_anim() {
            return;
        }
        self.anim_list.push(node);
        self.first_cyclic = Some(self.anim_list.len() - 1);
        if self.curr_anim.is_none() {
            self.curr_anim = Some(0);
            self.frame_number = f64::from(self.anim_list[0].starting_frame());
        }
    }

    /// Empty the list, zero the motion and drop the placement pose.
    pub fn clear(&mut self) {
        self.clear_animations();
        self.clear_physics();
        self.placement_frame = None;
        self.placement_frame_id = 0;
    }

    /// Empty the list and reset the cursor.
    pub fn clear_animations(&mut self) {
        self.anim_list.clear();
        self.first_cyclic = None;
        self.curr_anim = None;
        self.frame_number = 0.0;
    }

    /// Zero the velocity and spin.
    pub fn clear_physics(&mut self) {
        self.velocity = Vec3::ZERO;
        self.omega = Vec3::ZERO;
    }

    /// Remove the cyclic tail. A cursor inside it falls back to the end of
    /// the last link animation; the new last animation becomes the tail.
    pub fn remove_cyclic_anims(&mut self) {
        if let Some(fc) = self.first_cyclic {
            if self.curr_anim.is_some_and(|c| c >= fc) {
                // Every tail node's predecessor, once the nodes before it
                // in the tail are gone, is the node just before the tail.
                let prev = fc.checked_sub(1);
                self.curr_anim = prev;
                self.frame_number =
                    prev.map_or(0.0, |p| f64::from(self.anim_list[p].ending_frame()));
            }
            self.anim_list.truncate(fc);
        }
        self.first_cyclic = self.anim_list.len().checked_sub(1);
    }

    /// Remove up to `count` link animations, nearest to the cyclic tail
    /// first. A cursor on a removed link jumps to the start of the tail.
    pub fn remove_link_animations(&mut self, count: i32) {
        for _ in 0..count {
            let Some(fc) = self.first_cyclic else {
                break;
            };
            let Some(prev) = fc.checked_sub(1) else {
                break;
            };
            if self.curr_anim == Some(prev) {
                self.curr_anim = Some(fc);
                self.frame_number = f64::from(self.anim_list[fc].starting_frame());
            }
            self.remove_node(prev);
        }
    }

    /// Remove every link animation.
    pub fn remove_all_link_animations(&mut self) {
        while self.first_cyclic.is_some_and(|fc| fc > 0) {
            self.remove_link_animations(1);
        }
    }

    /// Drop the link animations the cursor has already passed, stopping at
    /// the cursor or at the cyclic tail.
    pub fn apricot(&mut self) {
        if self.anim_list.is_empty() || self.curr_anim == Some(0) {
            return;
        }
        while self.first_cyclic != Some(0) {
            self.remove_node(0);
            if self.anim_list.is_empty() || self.curr_anim == Some(0) {
                break;
            }
        }
    }

    /// Remove node `index`, keeping the cursor and tail on the same nodes.
    fn remove_node(&mut self, index: usize) {
        self.anim_list.remove(index);
        let shift = |slot: &mut Option<usize>| match *slot {
            Some(i) if i == index => *slot = None,
            Some(i) if i > index => *slot = Some(i - 1),
            _ => {}
        };
        shift(&mut self.curr_anim);
        shift(&mut self.first_cyclic);
    }

    // ── motion accumulators ─────────────────────────────────────────────

    /// Replace the velocity.
    pub fn set_velocity(&mut self, v: Vec3) {
        self.velocity = v;
    }

    /// Replace the spin.
    pub fn set_omega(&mut self, w: Vec3) {
        self.omega = w;
    }

    /// Add to the velocity and spin.
    pub fn combine_physics(&mut self, v: Vec3, w: Vec3) {
        self.velocity += v;
        self.omega += w;
    }

    /// Subtract from the velocity and spin.
    pub fn subtract_physics(&mut self, v: Vec3, w: Vec3) {
        self.velocity -= v;
        self.omega -= w;
    }

    /// Scale the rate of every animation in the cyclic tail.
    pub fn multiply_cyclic_animation_framerate(&mut self, factor: f32) {
        if let Some(fc) = self.first_cyclic {
            for node in &mut self.anim_list[fc..] {
                node.multiply_framerate(factor);
            }
        }
    }

    // ── placement and pose ──────────────────────────────────────────────

    /// Set the pose used while no animation plays.
    pub fn set_placement_frame(&mut self, frame: Option<AnimationFrame>, id: u32) {
        self.placement_frame = frame;
        self.placement_frame_id = id;
    }

    /// The keyframe under the cursor, or the placement pose when nothing
    /// plays.
    #[must_use]
    pub fn curr_animframe(&self) -> Option<&AnimationFrame> {
        match self.curr_anim {
            None => self.placement_frame.as_ref(),
            Some(c) => self.anim_list[c].part_frame(self.curr_frame_number()),
        }
    }

    /// The whole keyframe the cursor is in.
    #[must_use]
    pub fn curr_frame_number(&self) -> i32 {
        self.frame_number.floor() as i32
    }

    // ── advancing ───────────────────────────────────────────────────────

    /// Move `frame` by the sequence's velocity and spin for `quantum`
    /// seconds; the step's size comes from `quantum`, its sign from
    /// `sign_source`.
    pub fn apply_physics(&self, frame: &mut Frame, quantum: f64, sign_source: f64) {
        let mut signed = quantum.abs();
        if sign_source < 0.0 {
            signed = -signed;
        }
        let sq = signed as f32;
        frame.origin += self.velocity * sq;
        frame.rotate(self.omega * sq);
    }

    /// Advance `time_elapsed` seconds. Root motion goes into `frame` when
    /// given; hooks go to `hooks` when given (and are dropped otherwise).
    /// With nothing to play, only the velocity and spin move the frame.
    pub fn update(
        &mut self,
        time_elapsed: f64,
        frame: Option<&mut Frame>,
        hooks: Option<&mut (dyn AnimHookQueue + '_)>,
    ) {
        if !self.anim_list.is_empty() && self.curr_anim.is_some() {
            self.update_internal(time_elapsed, frame, hooks);
            self.apricot();
        } else if let Some(frame) = frame {
            self.apply_physics(frame, time_elapsed, time_elapsed);
        }
    }

    /// The cursor walk behind [`Sequence::update`]: step through keyframes,
    /// fire their hooks, and carry leftover time into the next animation
    /// when one finishes.
    pub fn update_internal(
        &mut self,
        mut time_elapsed: f64,
        mut frame: Option<&mut Frame>,
        mut hooks: Option<&mut (dyn AnimHookQueue + '_)>,
    ) {
        let epsilon = f64::from(F_EPSILON);
        loop {
            let Some(c) = self.curr_anim else {
                return;
            };
            let curr = self.anim_list[c].clone();
            let framerate = f64::from(curr.framerate);
            let frametime = framerate * time_elapsed;
            let mut last_frame = self.frame_number.floor() as i32;

            self.frame_number += frametime;
            let mut frame_time_elapsed = 0.0;
            let mut anim_done = false;

            if frametime > 0.0 {
                if f64::from(curr.high_frame) < self.frame_number.floor() {
                    let mut frame_offset = self.frame_number - f64::from(curr.high_frame) - 1.0;
                    if frame_offset < 0.0 {
                        frame_offset = 0.0;
                    }
                    if framerate.abs() > epsilon {
                        frame_time_elapsed = frame_offset / framerate;
                    }
                    self.frame_number = f64::from(curr.high_frame);
                    anim_done = true;
                }
                while self.frame_number.floor() > f64::from(last_frame) {
                    if let Some(f) = frame.as_deref_mut() {
                        if let Some(pos) = curr.pos_frame(last_frame) {
                            f.combine(&pos);
                        }
                        if framerate.abs() > epsilon {
                            self.apply_physics(f, 1.0 / framerate, time_elapsed);
                        }
                    }
                    execute_hooks(curr.part_frame(last_frame), 1, hooks.as_deref_mut());
                    last_frame += 1;
                }
            } else if frametime < 0.0 {
                if f64::from(curr.low_frame) > self.frame_number.floor() {
                    let mut frame_offset = self.frame_number - f64::from(curr.low_frame);
                    if frame_offset > 0.0 {
                        frame_offset = 0.0;
                    }
                    if framerate.abs() > epsilon {
                        frame_time_elapsed = frame_offset / framerate;
                    }
                    self.frame_number = f64::from(curr.low_frame);
                    anim_done = true;
                }
                while self.frame_number.floor() < f64::from(last_frame) {
                    if let Some(f) = frame.as_deref_mut() {
                        if let Some(pos) = curr.pos_frame(last_frame) {
                            f.subtract1(&pos);
                        }
                        if framerate.abs() > epsilon {
                            self.apply_physics(f, 1.0 / framerate, time_elapsed);
                        }
                    }
                    execute_hooks(curr.part_frame(last_frame), -1, hooks.as_deref_mut());
                    last_frame -= 1;
                }
            } else {
                if let Some(f) = frame.as_deref_mut()
                    && time_elapsed.abs() > epsilon
                {
                    self.apply_physics(f, time_elapsed, time_elapsed);
                }
                return;
            }

            if !anim_done {
                return;
            }

            if let Some(h) = hooks.as_deref_mut()
                && !self.anim_list.is_empty()
                && self.first_cyclic != Some(0)
            {
                h.add_anim_done_hook();
            }

            self.advance_to_next_animation(time_elapsed, frame.as_deref_mut());
            time_elapsed = frame_time_elapsed;
        }
    }

    /// Move the cursor to the next animation (or the previous one when
    /// time runs backwards), wrapping to the cyclic tail (or the list's
    /// end). Root motion for the boundary keyframes goes into `frame`.
    pub fn advance_to_next_animation(&mut self, time_elapsed: f64, mut frame: Option<&mut Frame>) {
        let Some(c) = self.curr_anim else {
            return;
        };
        let epsilon = f64::from(F_EPSILON);
        let outgoing = self.anim_list[c].clone();

        if time_elapsed >= 0.0 {
            if let Some(f) = frame.as_deref_mut()
                && outgoing.framerate < 0.0
            {
                if let Some(pos) = outgoing.pos_frame(self.frame_number as i32) {
                    f.subtract1(&pos);
                }
                if f64::from(outgoing.framerate).abs() > epsilon {
                    self.apply_physics(f, 1.0 / f64::from(outgoing.framerate), time_elapsed);
                }
            }

            let next = if c + 1 < self.anim_list.len() {
                Some(c + 1)
            } else {
                self.first_cyclic
            };
            self.curr_anim = next;
            let Some(n) = next else {
                return;
            };
            self.frame_number = f64::from(self.anim_list[n].starting_frame());

            let incoming = self.anim_list[n].clone();
            if let Some(f) = frame
                && incoming.framerate > 0.0
            {
                if let Some(pos) = incoming.pos_frame(self.frame_number as i32) {
                    f.combine(&pos);
                }
                if f64::from(incoming.framerate).abs() > epsilon {
                    self.apply_physics(f, 1.0 / f64::from(incoming.framerate), time_elapsed);
                }
            }
        } else {
            if let Some(f) = frame.as_deref_mut()
                && outgoing.framerate >= 0.0
            {
                if let Some(pos) = outgoing.pos_frame(self.frame_number as i32) {
                    f.subtract1(&pos);
                }
                if f64::from(outgoing.framerate).abs() > epsilon {
                    self.apply_physics(f, 1.0 / f64::from(outgoing.framerate), time_elapsed);
                }
            }

            let prev = if c > 0 {
                Some(c - 1)
            } else {
                self.anim_list.len().checked_sub(1)
            };
            self.curr_anim = prev;
            let Some(p) = prev else {
                return;
            };
            self.frame_number = f64::from(self.anim_list[p].ending_frame());

            let incoming = self.anim_list[p].clone();
            if let Some(f) = frame
                && incoming.framerate < 0.0
            {
                if let Some(pos) = incoming.pos_frame(self.frame_number as i32) {
                    f.combine(&pos);
                }
                if f64::from(incoming.framerate).abs() > epsilon {
                    self.apply_physics(f, 1.0 / f64::from(incoming.framerate), time_elapsed);
                }
            }
        }
    }
}

/// Queue the hooks of `part_frame` that fire in `direction` (1 forward, -1
/// backward); a hook stored with direction 0 fires both ways.
fn execute_hooks(
    part_frame: Option<&AnimationFrame>,
    direction: i32,
    hooks: Option<&mut (dyn AnimHookQueue + '_)>,
) {
    let (Some(part_frame), Some(hooks)) = (part_frame, hooks) else {
        return;
    };
    for hook in &part_frame.hooks {
        let dir = hook.direction as i32;
        if dir == 0 || dir == direction {
            hooks.add_anim_hook(hook);
        }
    }
}

#[cfg(test)]
mod tests;
