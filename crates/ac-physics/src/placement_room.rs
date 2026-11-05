//! Room to set a body down ahead of a standing one: the room a summoned pet
//! needs.
//!
//! The check uses the same placement a newly created object gets when it
//! enters the world, and commits nothing: the placement runs in a scratch
//! transition and only its answer is read. The spot must be free where it
//! is. Sliding aside to a free spot, which an object entering the world may
//! do, is not allowed, because a pet slid behind a wall is exactly what the
//! check is there to avoid. Live creatures and players standing there do not
//! count, as they move on; walls, buildings, terrain, doors and other objects
//! do.
//!
//! The spot is tried at a short ladder of heights around the feet, the first
//! that fits winning: a hand's width up, then climbing in 10 cm steps to
//! 70 cm for ground that rises ahead, then dropping in about 19 cm steps to
//! 66 cm below for ground that falls away or a low ceiling.

use glam::{Quat, Vec3};

use crate::engine::PhysicsEngine;
use crate::globals::EPSILON_SQ;
use crate::set_position::{
    PhysicsResidenceDisposition, PhysicsSetPositionFlags, PhysicsSetPositionRequest,
};
use crate::state::ObjectInfoState;
use crate::transition::CollisionSphere;

/// The farthest ahead a check may look, metres.
pub const MAXIMUM_DISTANCE_METERS: f32 = 10.0;

/// Heights above the standing body's feet the spot is tried at, metres, in
/// order.
pub const LIFTS: [f32; 11] = [
    0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, -0.09075, -0.2815, -0.47225, -0.663,
];

/// What a room check found at the spot it looked at.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum PlacementRoom {
    /// The check could not be made: the spot is not loaded, the body has no
    /// shape, or the distance is out of range.
    #[default]
    Unknown,
    /// The body can be set down at the spot.
    Clear,
    /// Something solid is in the way at every height tried.
    Blocked,
}

/// The body a room check sets down: its collision spheres (body frame,
/// unscaled) and how far it steps up and down.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacementRoomBody<'a> {
    /// The collision spheres.
    pub spheres: &'a [CollisionSphere],
    /// The body's scale.
    pub scale: f32,
    /// Step-up height, metres.
    pub step_up_height: f32,
    /// Step-down height, metres.
    pub step_down_height: f32,
    /// The standing body's own id (its own shadow does not block it).
    pub self_entity_id: u32,
}

/// The answer of a room check and, when clear, where the body would stand.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PlacementRoomResult {
    /// What was found.
    pub room: PlacementRoom,
    /// The cell the body would stand in (clear only).
    pub cell_id: u32,
    /// Where it would stand, relative to the cell's landblock corner.
    pub cell_local_position: Vec3,
    /// Where it would stand, world frame.
    pub position: Vec3,
}

impl PlacementRoomResult {
    fn unknown() -> Self {
        Self::default()
    }
}

/// Looks `distance_meters` ahead of a body standing at `position` (world
/// frame) in `cell_id` at `cell_local_position`, facing along
/// `orientation`. Ahead is the facing flattened onto the ground.
#[must_use]
pub fn check(
    engine: &mut PhysicsEngine,
    body: &PlacementRoomBody<'_>,
    position: Vec3,
    orientation: Quat,
    cell_id: u32,
    cell_local_position: Vec3,
    distance_meters: f32,
) -> PlacementRoomResult {
    if cell_id == 0
        || body.spheres.is_empty()
        || distance_meters.is_nan()
        || distance_meters <= 0.0
        || distance_meters > MAXIMUM_DISTANCE_METERS
    {
        return PlacementRoomResult::unknown();
    }

    let mut forward = orientation * Vec3::Y;
    forward.z = 0.0;
    if forward.length_squared() < EPSILON_SQ {
        return PlacementRoomResult::unknown();
    }
    let ahead = forward.normalize() * distance_meters;

    let mut request = PhysicsSetPositionRequest {
        orientation,
        cell_id,
        spheres: body.spheres.to_vec(),
        scale: body.scale,
        step_up_height: body.step_up_height,
        step_down_height: body.step_down_height,
        mover_flags: ObjectInfoState(ObjectInfoState::IGNORE_CREATURES),
        moving_entity_id: body.self_entity_id,
        flags: PhysicsSetPositionFlags(PhysicsSetPositionFlags::PLACEMENT),
        ..PhysicsSetPositionRequest::default()
    };
    for lift in LIFTS {
        let offset = ahead + Vec3::new(0.0, 0.0, lift);
        request.position = position + offset;
        request.cell_local_position = cell_local_position + offset;
        let result = engine.set_position(&request, None);
        if result.residence == PhysicsResidenceDisposition::DeferredCell {
            return PlacementRoomResult::unknown();
        }
        if result.is_committed() {
            return PlacementRoomResult {
                room: PlacementRoom::Clear,
                cell_id: result.cell_id,
                cell_local_position: result.cell_local_position,
                position: result.position,
            };
        }
    }
    PlacementRoomResult {
        room: PlacementRoom::Blocked,
        ..PlacementRoomResult::default()
    }
}
