//! The requests and results of placing an object (set position).

use ac_core::physics::PhysicsState;
use glam::{Quat, Vec3};

use crate::geometry::Plane;
use crate::shadow_registry::PhysicsShadowCommitAction;
use crate::state::ObjectInfoState;
use crate::transition::CollisionSphere;

/// Why a placement failed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum PhysicsSetPositionError {
    /// It did not.
    #[default]
    Ok = 0,
    /// Something else went wrong.
    GeneralFailure = 1,
    /// No free spot was found.
    NoValidPosition = 2,
    /// The target cell does not exist.
    NoCell = 3,
    /// The collision handler took the collision.
    Collided = 4,
    /// The arguments were invalid.
    InvalidArguments = 0x100,
}

/// What became of a placement's cell.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum PhysicsResidenceDisposition {
    /// Placed.
    #[default]
    Committed,
    /// The cell is not resident yet; the position is kept as asked.
    DeferredCell,
    /// Nothing changed.
    Unchanged,
}

/// Placement flags (retail `SetPositionFlags`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct PhysicsSetPositionFlags(pub u32);

impl PhysicsSetPositionFlags {
    /// None.
    pub const NONE: u32 = 0;
    /// A placement.
    pub const PLACEMENT: u32 = 0x001;
    /// A teleport.
    pub const TELEPORT: u32 = 0x002;
    /// A restore.
    pub const RESTORE: u32 = 0x004;
    /// May slide to a free spot.
    pub const SLIDE: u32 = 0x010;
    /// Do not create cells.
    pub const DO_NOT_CREATE_CELLS: u32 = 0x020;
    /// On failure, scatter around the spot.
    pub const SCATTER: u32 = 0x100;
    /// Only scatter.
    pub const RANDOM_SCATTER: u32 = 0x200;
    /// Along a line.
    pub const LINE: u32 = 0x400;
    /// Send a position event.
    pub const SEND_POSITION_EVENT: u32 = 0x1000;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u32) -> bool {
        self.0 & bits == bits
    }
}

/// What kind of object is placed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum PhysicsPlacementClass {
    /// An ordinary object.
    #[default]
    Ordinary,
    /// On a hook (forced into its cell).
    Hook,
    /// In storage (forced into its cell).
    Storage,
    /// A corpse (forced into its cell).
    Corpse,
}

/// A placement request.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsSetPositionRequest {
    /// World position.
    pub position: Vec3,
    /// Orientation.
    pub orientation: Quat,
    /// The cell.
    pub cell_id: u32,
    /// The position relative to the cell's landblock corner.
    pub cell_local_position: Vec3,
    /// The object's spheres (body frame, unscaled).
    pub spheres: Vec<CollisionSphere>,
    /// The object's scale.
    pub scale: f32,
    /// Step-up height.
    pub step_up_height: f32,
    /// Step-down height.
    pub step_down_height: f32,
    /// The object's physics state.
    pub mover_physics_state: PhysicsState,
    /// The object's mover bits.
    pub mover_flags: ObjectInfoState,
    /// The object's id.
    pub moving_entity_id: u32,
    /// What kind of object.
    pub placement_class: PhysicsPlacementClass,
    /// Flags.
    pub flags: PhysicsSetPositionFlags,
    /// Line placement direction (unused by the ported paths).
    pub line: Vec3,
    /// Scatter half-width on x.
    pub scatter_radius_x: f32,
    /// Scatter half-width on y.
    pub scatter_radius_y: f32,
    /// Scatter tries.
    pub scatter_attempts: u32,
    /// The cell the object is in now, when placed.
    pub current_cell_id: Option<u32>,
}

impl Default for PhysicsSetPositionRequest {
    fn default() -> Self {
        Self {
            position: Vec3::ZERO,
            orientation: Quat::IDENTITY,
            cell_id: 0,
            cell_local_position: Vec3::ZERO,
            spheres: Vec::new(),
            scale: 1.0,
            step_up_height: 0.0,
            step_down_height: 0.0,
            mover_physics_state: PhysicsState::NONE,
            mover_flags: ObjectInfoState(ObjectInfoState::NONE),
            moving_entity_id: 0,
            placement_class: PhysicsPlacementClass::Ordinary,
            flags: PhysicsSetPositionFlags(PhysicsSetPositionFlags::PLACEMENT),
            line: Vec3::ZERO,
            scatter_radius_x: 0.0,
            scatter_radius_y: 0.0,
            scatter_attempts: 0,
            current_cell_id: None,
        }
    }
}

/// What a failed placement's collision handler sees.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PhysicsSetPositionCollisionReport {
    /// Contact plane valid.
    pub contact_plane_valid: bool,
    /// Contact plane.
    pub contact_plane: Plane,
    /// Contact plane cell.
    pub contact_plane_cell_id: u32,
    /// Contact plane is water.
    pub contact_plane_is_water: bool,
    /// Last-known plane valid.
    pub last_known_contact_plane_valid: bool,
    /// Last-known plane.
    pub last_known_contact_plane: Plane,
    /// Last-known plane cell.
    pub last_known_contact_plane_cell_id: u32,
    /// Last-known plane is water.
    pub last_known_contact_plane_is_water: bool,
    /// Sliding normal valid.
    pub sliding_normal_valid: bool,
    /// Sliding normal.
    pub sliding_normal: Vec3,
    /// Collision normal valid.
    pub collision_normal_valid: bool,
    /// Collision normal.
    pub collision_normal: Vec3,
    /// Hit the environment.
    pub collided_with_environment: bool,
    /// Stationary-fall count.
    pub frames_stationary_fall: i32,
    /// Adjust offset.
    pub adjust_offset: Vec3,
    /// The last object hit.
    pub last_collided_object_id: Option<u32>,
    /// Every object hit.
    pub collided_object_ids: Vec<u32>,
}

/// A placement's result.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PhysicsSetPositionResult {
    /// Why it failed (`Ok` when it did not).
    pub error: PhysicsSetPositionError,
    /// What became of the cell.
    pub residence: PhysicsResidenceDisposition,
    /// World position.
    pub position: Vec3,
    /// Orientation.
    pub orientation: Quat,
    /// The cell.
    pub cell_id: u32,
    /// Landblock-relative position.
    pub cell_local_position: Vec3,
    /// Touching a surface.
    pub in_contact: bool,
    /// On walkable ground.
    pub on_walkable: bool,
    /// Contact plane.
    pub contact_plane: Plane,
    /// Contact plane cell.
    pub contact_plane_cell_id: u32,
    /// Contact plane is water.
    pub contact_plane_is_water: bool,
    /// Sliding normal valid.
    pub sliding_normal_valid: bool,
    /// Sliding normal.
    pub sliding_normal: Vec3,
    /// Collision normal valid.
    pub collision_normal_valid: bool,
    /// Collision normal.
    pub collision_normal: Vec3,
    /// Stationary-fall count.
    pub frames_stationary_fall: i32,
    /// Hit the environment.
    pub collided_with_environment: bool,
    /// The collision handler took the collision.
    pub collision_handler_result: bool,
    /// The object changed cell.
    pub cell_changed: bool,
    /// How to update the object's shadow rows.
    pub shadow_action: PhysicsShadowCommitAction,
    /// The cells to put the rows in (for [`PhysicsShadowCommitAction::Replace`]).
    pub cross_cell_ids: Vec<u32>,
    /// Objects hit.
    pub collided_object_ids: Vec<u32>,
    /// Every cell the placement looked at.
    pub queried_cell_ids: Vec<u32>,
    /// Forced into its cell (a hook, a storage chest, a corpse): only the
    /// frame and the cell were set; the contact fields say nothing, and
    /// the body keeps the contact it had.
    pub forced_into_cell: bool,
}

impl PhysicsSetPositionResult {
    /// No error.
    #[must_use]
    pub fn is_successful(&self) -> bool {
        self.error == PhysicsSetPositionError::Ok
    }

    /// Placed.
    #[must_use]
    pub fn is_committed(&self) -> bool {
        self.is_successful() && self.residence == PhysicsResidenceDisposition::Committed
    }

    /// Waiting for the cell.
    #[must_use]
    pub fn is_deferred(&self) -> bool {
        self.is_successful() && self.residence == PhysicsResidenceDisposition::DeferredCell
    }
}
