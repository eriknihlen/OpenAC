//! The position manager: sticky follow (stay at arm's length from a target,
//! facing it) and the server's leash (a constraint that brakes movement
//! away from an anchor and stops it past the maximum distance). Both adjust
//! the body's per-update movement offset.

use glam::Vec3;

use ac_motion::MotionDeltaFrame;
use ac_motion::move_to_math::{
    EPSILON, cylinder_distance_no_z, get_heading, global_to_local_vec, normalize_check_small,
    position_heading,
};

use crate::body::CellPosition;
use crate::movement::target::{PhysicsObjHost, TargetInfo, TargetOps, TargetStatus};

/// Leash distances by cell: outdoors starts braking at 10 m and stops at
/// 50 m, indoors at 5 m and 20 m.
pub mod constraint_distance {
    const OUTDOOR_START: f32 = 10.0;
    const INDOOR_START: f32 = 5.0;
    const OUTDOOR_MAX: f32 = 50.0;
    const INDOOR_MAX: f32 = 20.0;

    /// An interior cell (low 16 bits at least 0x100).
    #[must_use]
    pub fn is_indoor_cell(obj_cell_id: u32) -> bool {
        (obj_cell_id & 0xFFFF) >= 0x0100
    }

    /// Where braking starts, metres.
    #[must_use]
    pub fn get_start_constraint_distance(obj_cell_id: u32) -> f32 {
        if is_indoor_cell(obj_cell_id) {
            INDOOR_START
        } else {
            OUTDOOR_START
        }
    }

    /// Where movement stops, metres.
    #[must_use]
    pub fn get_max_constraint_distance(obj_cell_id: u32) -> f32 {
        if is_indoor_cell(obj_cell_id) {
            INDOOR_MAX
        } else {
            OUTDOOR_MAX
        }
    }
}

/// Sticky follow state.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct StickyManager {
    /// The target (0 when not stuck).
    pub target_id: u32,
    /// The target's radius, metres.
    pub target_radius: f32,
    /// The target's last reported position.
    pub target_position: CellPosition,
    /// A report has arrived since sticking.
    pub initialized: bool,
    /// When an unconfirmed stick gives up, seconds.
    pub sticky_timeout_time: f64,
}

impl StickyManager {
    /// Gap kept to the target, metres.
    pub const STICKY_RADIUS: f32 = 0.3;
    /// How long a stick waits for its first report, seconds.
    pub const STICKY_TIME: f64 = 1.0;
    const FOLLOW_SPEED_FACTOR: f32 = 5.0;
    const FALLBACK_FOLLOW_SPEED: f32 = 15.0;
}

/// Leash state.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ConstraintManager {
    /// The leash is on.
    pub is_constrained: bool,
    /// Distance from the anchor when leashed, then the last step's length,
    /// metres.
    pub constraint_pos_offset: f32,
    /// The anchor.
    pub constraint_pos: CellPosition,
    /// Braking starts here, metres.
    pub constraint_distance_start: f32,
    /// Movement stops here, metres.
    pub constraint_distance_max: f32,
}

impl ConstraintManager {
    /// Past 90% of the maximum the body is held fully.
    #[must_use]
    pub fn is_fully_constrained(&self) -> bool {
        self.constraint_distance_max * 0.9 < self.constraint_pos_offset
    }

    /// Take the leash off.
    pub fn un_constrain(&mut self) {
        self.is_constrained = false;
    }
}

/// The sticky and constraint managers, created on first use.
#[derive(Debug, Clone, Default)]
pub struct PositionManager {
    /// Sticky follow.
    pub sticky: Option<StickyManager>,
    /// The leash.
    pub constraint: Option<ConstraintManager>,
}

impl PositionManager {
    /// The target stuck to (0 when none).
    #[must_use]
    pub fn get_sticky_object_id(&self) -> u32 {
        self.sticky.map_or(0, |s| s.target_id)
    }

    /// Held fully by the leash.
    #[must_use]
    pub fn is_fully_constrained(&self) -> bool {
        self.constraint.is_some_and(|c| c.is_fully_constrained())
    }

    /// Take the leash off.
    pub fn un_constrain(&mut self) {
        if let Some(c) = &mut self.constraint {
            c.un_constrain();
        }
    }
}

/// An object with a position manager.
pub trait PositionManagerHost: PhysicsObjHost {
    /// Its position manager.
    fn position_manager(&mut self) -> &mut PositionManager;
    /// Its position manager, read-only.
    fn position_manager_ref(&self) -> &PositionManager;
}

/// Position-manager operations, on any [`PositionManagerHost`].
pub trait PositionOps: PositionManagerHost {
    /// Stick to `object_id` (radius and height of the target, metres).
    fn stick_to(&mut self, object_id: u32, radius: f32, height: f32) {
        self.position_manager()
            .sticky
            .get_or_insert_with(StickyManager::default);
        self.sticky_stick_to(object_id, radius, height);
    }

    /// Stop sticking.
    fn un_stick(&mut self) {
        if self.position_manager_ref().sticky.is_some() {
            self.sticky_un_stick();
        }
    }

    /// Put the leash on at `anchor`.
    fn constrain_to(&mut self, anchor: CellPosition, start_distance: f32, max_distance: f32) {
        let here = self.obj_position().frame.origin;
        let c = self
            .position_manager()
            .constraint
            .get_or_insert_with(ConstraintManager::default);
        c.is_constrained = true;
        c.constraint_pos = anchor;
        c.constraint_distance_start = start_distance;
        c.constraint_distance_max = max_distance;
        c.constraint_pos_offset = anchor.frame.origin.distance(here);
    }

    /// A target report for the sticky follow.
    fn position_handle_update_target(&mut self, info: TargetInfo) {
        if self.position_manager_ref().sticky.is_some() {
            self.sticky_handle_update_target(info);
        }
    }

    /// Adjust this update's movement: sticky follow, then the leash.
    fn position_adjust_offset(&mut self, offset: &mut MotionDeltaFrame, quantum: f64) {
        if self.position_manager_ref().sticky.is_some() {
            self.sticky_adjust_offset(offset, quantum);
        }
        if self.position_manager_ref().constraint.is_some() {
            self.constraint_adjust_offset(offset, quantum);
        }
    }

    /// Per-update check: an unconfirmed stick times out.
    fn position_use_time(&mut self) {
        if self.position_manager_ref().sticky.is_some() {
            self.sticky_use_time();
        }
    }

    #[doc(hidden)]
    fn sticky_tear_down(&mut self) {
        if let Some(s) = &mut self.position_manager().sticky {
            s.target_id = 0;
            s.initialized = false;
        }
        self.clear_target();
        self.obj_interrupt_current_movement();
    }

    #[doc(hidden)]
    fn sticky_un_stick(&mut self) {
        if self.position_manager_ref().get_sticky_object_id() == 0 {
            return;
        }
        self.sticky_tear_down();
    }

    #[doc(hidden)]
    fn sticky_stick_to(&mut self, object_id: u32, target_radius: f32, _target_height: f32) {
        if self.position_manager_ref().get_sticky_object_id() != 0 {
            self.sticky_tear_down();
        }
        let timeout = self.obj_cur_time() + StickyManager::STICKY_TIME;
        if let Some(s) = &mut self.position_manager().sticky {
            s.target_radius = target_radius;
            s.target_id = object_id;
            s.initialized = false;
            s.sticky_timeout_time = timeout;
        }
        self.set_target(0, object_id, 0.5, 0.5);
    }

    #[doc(hidden)]
    fn sticky_use_time(&mut self) {
        let Some(s) = self.position_manager_ref().sticky else {
            return;
        };
        if s.target_id == 0 {
            return;
        }
        if self.obj_cur_time() > s.sticky_timeout_time {
            self.sticky_tear_down();
        }
    }

    #[doc(hidden)]
    fn sticky_handle_update_target(&mut self, info: TargetInfo) {
        let Some(s) = self.position_manager_ref().sticky else {
            return;
        };
        if info.object_id != s.target_id {
            return;
        }
        if info.status == TargetStatus::Ok {
            if let Some(s) = &mut self.position_manager().sticky {
                s.initialized = true;
                s.target_position = info.target_position;
            }
            return;
        }
        if s.target_id != 0 {
            self.sticky_tear_down();
        }
    }

    /// Step toward the target to keep 0.3 m between the cylinders (or back
    /// off when closer), at up to five times the top speed (15 m/s without
    /// an interpreter), flat, and face it.
    #[doc(hidden)]
    fn sticky_adjust_offset(&mut self, offset: &mut MotionDeltaFrame, quantum: f64) {
        let Some(s) = self.position_manager_ref().sticky else {
            return;
        };
        if s.target_id == 0 || !s.initialized {
            return;
        }
        let me = self.obj_position();
        let target_pos = self
            .target_manager_ref()
            .get_relationship_target(s.target_id)
            .and_then(|t| self.obj_position_of(t))
            .unwrap_or(s.target_position);

        let world_offset = target_pos.frame.origin - me.frame.origin;
        let mut local = global_to_local_vec(me.frame.orientation, world_offset);
        local.z = 0.0;
        offset.origin = local;

        let dist = cylinder_distance_no_z(
            self.obj_radius(),
            me.frame.origin,
            s.target_radius,
            target_pos.frame.origin,
        ) - StickyManager::STICKY_RADIUS;

        if normalize_check_small(&mut offset.origin) {
            offset.origin = Vec3::ZERO;
        }

        let mut speed = 0.0;
        if let Some(max_speed) = self.obj_minterp_max_speed() {
            speed = max_speed * StickyManager::FOLLOW_SPEED_FACTOR;
        }
        if speed < EPSILON {
            speed = StickyManager::FALLBACK_FOLLOW_SPEED;
        }
        let mut delta = speed * quantum as f32;
        if delta >= dist.abs() {
            delta = dist;
        } else if dist < 0.0 {
            delta = -delta;
        }
        offset.origin *= delta;

        let cur_heading = get_heading(me.frame.orientation);
        let target_heading = position_heading(me.frame.origin, target_pos.frame.origin);
        let mut heading = target_heading - cur_heading;
        if heading.abs() < EPSILON {
            heading = 0.0;
        }
        if heading < -EPSILON {
            heading += 360.0;
        }
        offset.set_heading(heading);
    }

    /// The leash: on the ground, a step is scaled down linearly between the
    /// start and maximum distances and dropped past the maximum; the step's
    /// length is then remembered as the new offset (in the air too).
    #[doc(hidden)]
    fn constraint_adjust_offset(&mut self, offset: &mut MotionDeltaFrame, _quantum: f64) {
        let in_contact = self.obj_in_contact();
        let Some(c) = &mut self.position_manager().constraint else {
            return;
        };
        if !c.is_constrained {
            return;
        }
        if in_contact {
            if c.constraint_pos_offset < c.constraint_distance_max {
                if c.constraint_pos_offset > c.constraint_distance_start {
                    let taper = (c.constraint_distance_max - c.constraint_pos_offset)
                        / (c.constraint_distance_max - c.constraint_distance_start);
                    offset.origin *= taper;
                }
            } else {
                offset.origin = Vec3::ZERO;
            }
        }
        c.constraint_pos_offset = offset.origin.length();
    }
}

impl<T: PositionManagerHost + ?Sized> PositionOps for T {}
