//! Position interpolation: chasing the server's positions for an observed
//! (remote) object.
//!
//! Server positions queue up as waypoints; each update the body moves
//! toward the head waypoint at up to twice its own top speed (7.5 m/s when
//! it has none). Progress is checked every five updates; a waypoint the
//! body stalls short of is dropped, and after three stalls the caller is
//! told to snap the body to the last target. A target more than 100 m away
//! is not chased but snapped to.

use std::collections::VecDeque;

use ac_motion::{MotionDeltaFrame, OffsetAdjuster, get_heading, set_heading, set_rotate};
use glam::{Quat, Vec3};

#[derive(Debug, Clone, Copy, PartialEq)]
struct InterpolationNode {
    cell_id: u32,
    target_position: Vec3,
    target_orientation: Quat,
}

/// The target a stalled interpolation asks the caller to snap to.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct InterpolationRecoveryTarget {
    /// Position.
    pub position: Vec3,
    /// Orientation.
    pub orientation: Quat,
    /// Cell.
    pub cell_id: u32,
    /// The manager's version when asked (a stale target is refused).
    pub version: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct InterpolationStep {
    overwrites: bool,
    world_origin: Vec3,
    target_orientation: Quat,
}

/// The interpolation queue of one observed object.
#[derive(Debug, Clone, PartialEq)]
pub struct InterpolationManager {
    queue: VecDeque<InterpolationNode>,
    frame_counter: i32,
    progress_quantum: f32,
    original_distance: f32,
    fail_count: i32,
    version: i64,
    recovery_target: Option<InterpolationNode>,
    last_body_position: Vec3,
    keep_heading: bool,
}

impl Default for InterpolationManager {
    fn default() -> Self {
        Self {
            queue: VecDeque::new(),
            frame_counter: 0,
            progress_quantum: 0.0,
            original_distance: Self::ORIGINAL_DISTANCE_SENTINEL,
            fail_count: 0,
            version: 0,
            recovery_target: None,
            last_body_position: Vec3::ZERO,
            keep_heading: false,
        }
    }
}

/// Retail's small-number tolerance.
const F_EPSILON: f32 = 0.0002;

fn global_to_local_vec(frame_orientation: Quat, world_vec: Vec3) -> Vec3 {
    frame_orientation.conjugate() * world_vec
}

impl InterpolationManager {
    /// Waypoints held before the oldest is dropped.
    pub const QUEUE_CAP: usize = 20;
    /// Catch-up speed as a multiple of the object's top speed.
    pub const MAX_INTERPOLATED_VELOCITY_MOD: f32 = 2.0;
    /// Catch-up speed when the object has none, metres per second.
    pub const MAX_INTERPOLATED_VELOCITY: f32 = 7.5;
    /// A stalled waypoint this close counts as reached, metres.
    pub const MIN_DISTANCE_TO_REACH_POSITION: f32 = 0.20;
    /// A waypoint this close is reached, metres.
    pub const DESIRED_DISTANCE: f32 = 0.05;
    /// Updates between progress checks.
    pub const STALL_CHECK_FRAME_INTERVAL: i32 = 5;
    /// Least fraction of the catch-up speed that counts as progress.
    pub const STALL_PROGRESS_MIN_FRACTION: f32 = 0.30;
    /// Stalls before the caller is told to snap.
    pub const STALL_FAIL_COUNT_THRESHOLD: i32 = 3;
    /// Targets farther than this are snapped to, metres.
    pub const AUTONOMY_BLIP_DISTANCE: f32 = 100.0;
    /// "No distance measured yet".
    pub const ORIGINAL_DISTANCE_SENTINEL: f32 = 999_999.0;

    /// An empty queue.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// True when a waypoint is queued.
    #[must_use]
    pub fn is_active(&self) -> bool {
        !self.queue.is_empty()
    }

    /// Waypoints queued.
    #[must_use]
    pub fn count(&self) -> usize {
        self.queue.len()
    }

    /// Queue depth and stall count.
    #[must_use]
    pub fn diagnostic_state(&self) -> (usize, i32) {
        (self.queue.len(), self.fail_count)
    }

    /// Empties the queue and resets the progress check.
    pub fn clear(&mut self) {
        self.version += 1;
        self.recovery_target = None;
        self.queue.clear();
        self.frame_counter = 0;
        self.progress_quantum = 0.0;
        self.original_distance = Self::ORIGINAL_DISTANCE_SENTINEL;
        self.fail_count = 0;
    }

    /// [`Self::enqueue`] with a heading (radians about z).
    pub fn enqueue_heading(
        &mut self,
        target_position: Vec3,
        heading: f32,
        is_moving_to: bool,
        current_body_position: Option<Vec3>,
        target_cell_id: u32,
        autonomy_blip_distance: f32,
    ) -> Option<Quat> {
        self.enqueue(
            target_position,
            Quat::from_axis_angle(Vec3::Z, heading),
            is_moving_to,
            current_body_position,
            None,
            target_cell_id,
            autonomy_blip_distance,
        )
    }

    /// Queues a server position. A target far from the last one (or the
    /// body) is queued as a blip (the caller is told to snap); a target the
    /// body is already at clears the queue (returning the heading to face
    /// when not moving to it); nearer stale waypoints are replaced.
    #[allow(clippy::too_many_arguments)]
    pub fn enqueue(
        &mut self,
        target_position: Vec3,
        target_orientation: Quat,
        is_moving_to: bool,
        current_body_position: Option<Vec3>,
        current_body_orientation: Option<Quat>,
        target_cell_id: u32,
        autonomy_blip_distance: f32,
    ) -> Option<Quat> {
        self.version += 1;
        let reference = match self.queue.back() {
            Some(tail) => tail.target_position,
            None => current_body_position.unwrap_or(target_position),
        };

        let dist = reference.distance(target_position);
        if dist > autonomy_blip_distance {
            // The far branch keeps the existing keep-heading flag.
            let orientation = store_target_orientation(
                target_orientation,
                current_body_orientation,
                self.keep_heading,
            );
            self.enqueue_raw(target_position, orientation, target_cell_id);
            self.fail_count = Self::STALL_FAIL_COUNT_THRESHOLD + 1;
            return None;
        }

        if let Some(body) = current_body_position
            && body.distance(target_position) <= Self::DESIRED_DISTANCE
        {
            self.clear();
            return if is_moving_to {
                None
            } else {
                Some(set_heading(get_heading(target_orientation)))
            };
        }

        while let Some(stale) = self.queue.back() {
            if stale.target_position.distance(target_position) < Self::DESIRED_DISTANCE {
                self.queue.pop_back();
            } else {
                break;
            }
        }
        while self.queue.len() >= Self::QUEUE_CAP {
            self.queue.pop_front();
        }

        self.keep_heading = is_moving_to;
        let orientation = store_target_orientation(
            target_orientation,
            current_body_orientation,
            self.keep_heading,
        );
        self.enqueue_raw(target_position, orientation, target_cell_id);
        None
    }

    fn enqueue_raw(&mut self, target: Vec3, target_orientation: Quat, target_cell_id: u32) {
        self.queue.push_back(InterpolationNode {
            cell_id: target_cell_id,
            target_position: target,
            target_orientation,
        });
    }

    /// The world-space catch-up step for `dt` seconds (zero when not in
    /// contact or nothing to chase).
    pub fn adjust_offset_world(
        &mut self,
        dt: f64,
        current_body_position: Vec3,
        max_speed_from_minterp: f32,
        in_contact: bool,
        is_sticky: bool,
    ) -> Vec3 {
        if !in_contact {
            return Vec3::ZERO;
        }
        let step = self.compute_step(dt, current_body_position, max_speed_from_minterp, is_sticky);
        if step.overwrites {
            step.world_origin
        } else {
            Vec3::ZERO
        }
    }

    /// When the caller should snap the body: after too many stalls, the
    /// last queued (or stalled) target.
    #[must_use]
    pub fn try_get_recovery_target(&self) -> Option<InterpolationRecoveryTarget> {
        if self.fail_count <= Self::STALL_FAIL_COUNT_THRESHOLD
            && (!self.queue.is_empty() || self.fail_count == 0)
        {
            return None;
        }
        let node = self.queue.back().copied().or(self.recovery_target)?;
        Some(InterpolationRecoveryTarget {
            position: node.target_position,
            orientation: node.target_orientation,
            cell_id: node.cell_id,
            version: self.version,
        })
    }

    /// Clears the queue after the caller snapped to `target`; false when the
    /// target is stale.
    pub fn complete_recovery(&mut self, target: InterpolationRecoveryTarget) -> bool {
        if target.version != self.version {
            return false;
        }
        self.clear();
        true
    }

    fn compute_step(
        &mut self,
        dt: f64,
        current_body_position: Vec3,
        max_speed_from_minterp: f32,
        is_sticky: bool,
    ) -> InterpolationStep {
        // Invalid time must never poison the body's position.
        if dt <= 0.0 || dt.is_nan() {
            return InterpolationStep::default();
        }
        self.last_body_position = current_body_position;
        let Some(head) = self.queue.front().copied() else {
            return InterpolationStep::default();
        };
        let dist = head.target_position.distance(current_body_position);
        if dist < Self::DESIRED_DISTANCE {
            self.node_completed(true, current_body_position);
            return InterpolationStep::default();
        }

        let scaled = max_speed_from_minterp * Self::MAX_INTERPOLATED_VELOCITY_MOD;
        let catch_up = if scaled < F_EPSILON {
            Self::MAX_INTERPOLATED_VELOCITY
        } else {
            scaled
        };
        self.progress_quantum += dt as f32;
        self.frame_counter += 1;

        if self.frame_counter >= Self::STALL_CHECK_FRAME_INTERVAL {
            let progress = self.original_distance - dist;
            let progressing = progress >= F_EPSILON
                && progress / self.progress_quantum / catch_up >= Self::STALL_PROGRESS_MIN_FRACTION;
            if !is_sticky && !progressing {
                let reached = dist < Self::MIN_DISTANCE_TO_REACH_POSITION;
                if !reached {
                    self.fail_count += 1;
                }
                self.node_completed(reached, current_body_position);
                return InterpolationStep::default();
            }
            self.frame_counter = 0;
            self.progress_quantum = 0.0;
            self.original_distance = dist;
        }

        let step = (catch_up * dt as f32).min(dist);
        let delta = ((head.target_position - current_body_position) / dist) * step;
        InterpolationStep {
            overwrites: true,
            world_origin: delta,
            target_orientation: head.target_orientation,
        }
    }

    fn node_completed(&mut self, succeeded: bool, current_body_position: Vec3) {
        self.version += 1;
        self.frame_counter = 0;
        self.progress_quantum = 0.0;
        let completed = self.queue.pop_front();

        if let Some(next) = self.queue.front() {
            self.original_distance = next.target_position.distance(current_body_position);
        } else {
            self.original_distance = Self::ORIGINAL_DISTANCE_SENTINEL;
            if succeeded {
                self.clear();
            } else {
                self.recovery_target = completed;
            }
        }
    }
}

impl OffsetAdjuster for InterpolationManager {
    fn adjust_offset(
        &mut self,
        dt: f64,
        current_body_position: Vec3,
        current_body_orientation: Quat,
        max_speed: f32,
        offset: &mut MotionDeltaFrame,
        in_contact: bool,
        is_sticky: bool,
    ) -> bool {
        if !in_contact {
            return false;
        }
        let step = self.compute_step(dt, current_body_position, max_speed, is_sticky);
        if !step.overwrites {
            return false;
        }
        offset.origin = global_to_local_vec(current_body_orientation, step.world_origin);
        offset.orientation = if self.keep_heading {
            Quat::IDENTITY
        } else {
            set_rotate(
                offset.origin,
                Quat::IDENTITY,
                current_body_orientation.inverse() * step.target_orientation,
            )
        };
        true
    }
}

fn store_target_orientation(
    target_orientation: Quat,
    current_body_orientation: Option<Quat>,
    keep_heading: bool,
) -> Quat {
    match current_body_orientation {
        Some(current) if keep_heading => set_heading(get_heading(current)),
        _ => target_orientation,
    }
}
