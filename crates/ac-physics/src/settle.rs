//! Settling a freshly spawned body onto the ground.
//!
//! Retail gains a spawned object's ground contact from its first gravity
//! frame; a body that does not simulate (a standing remote creature) gets
//! the same from one short downward sweep at creation.

use glam::Vec3;

use crate::body::PhysicsBody;
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::obj_update::commit_set_position_transition;
use crate::state::ObjectInfoState;

/// How far below the spawn point the settle sweep reaches, metres.
pub const SETTLE_DISTANCE: f32 = 0.5;

/// Sweeps the body half a metre down from `world_position`; when it lands
/// in contact the position, cell and contact state are committed (with the
/// ground callbacks). False (body untouched) otherwise.
#[allow(clippy::too_many_arguments)]
pub fn try_settle(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    world_position: Vec3,
    cell_id: u32,
    sphere_radius: f32,
    sphere_height: f32,
    mover_flags: ObjectInfoState,
    moving_entity_id: u32,
    hit_ground: &mut dyn FnMut(&mut PhysicsBody),
    leave_ground: &mut dyn FnMut(&mut PhysicsBody),
) -> bool {
    if cell_id == 0 {
        return false;
    }
    let settle = engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: world_position,
            target_pos: world_position - Vec3::new(0.0, 0.0, SETTLE_DISTANCE),
            cell_id,
            sphere_radius,
            sphere_height,
            step_up_height: 0.4,
            step_down_height: 0.4,
            is_on_ground: false,
            mover_flags,
            moving_entity_id,
            ..ResolveRequest::default()
        },
        Some(body),
    );
    if !settle.ok || !settle.in_contact {
        return false;
    }
    let resolved_cell_id = if settle.cell_id != 0 {
        settle.cell_id
    } else {
        cell_id
    };
    body.commit_transition_position(resolved_cell_id, settle.position);
    commit_set_position_transition(
        body,
        settle.in_contact,
        settle.on_walkable,
        settle.collision_normal_valid,
        settle.collision_normal,
        false,
        false,
        Some(hit_ground),
        Some(leave_ground),
        None,
        None,
    );
    true
}
