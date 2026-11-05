//! Committing a movement result to a body: contact bits, ground edges and
//! the collision response (bounce or stop).

use ac_core::physics::{PhysicsState, TransientState};
use glam::Vec3;

use crate::body::PhysicsBody;

pub use crate::engine::is_walkable_contact;

/// Sets the contact, walkable and water bits from a placement and
/// recomputes acceleration.
pub fn apply_set_position_contact(body: &mut PhysicsBody, in_contact: bool, on_walkable: bool) {
    body.transient_state
        .set(TransientState::CONTACT, in_contact);
    body.transient_state
        .set(TransientState::ON_WALKABLE, in_contact && on_walkable);
    let water = body.contact_plane_is_water;
    body.transient_state
        .set(TransientState::WATER_CONTACT, water);
    body.calc_acceleration();
}

/// Commits a set-position transition: contact bits, the ground edge
/// callbacks, then the collision response. `is_current` lets a callback
/// that replaced the body stop the commit; `is_velocity_current` skips the
/// response when the velocity was replaced. False when stopped.
#[allow(clippy::too_many_arguments)]
pub fn commit_set_position_transition(
    body: &mut PhysicsBody,
    in_contact: bool,
    on_walkable: bool,
    collision_normal_valid: bool,
    collision_normal: Vec3,
    previous_contact: bool,
    previous_on_walkable: bool,
    hit_ground: Option<&mut dyn FnMut(&mut PhysicsBody)>,
    leave_ground: Option<&mut dyn FnMut(&mut PhysicsBody)>,
    is_current: Option<&dyn Fn() -> bool>,
    is_velocity_current: Option<&dyn Fn() -> bool>,
) -> bool {
    if !commit_set_position_contact_transition(
        body,
        in_contact,
        on_walkable,
        previous_on_walkable,
        hit_ground,
        leave_ground,
        is_current,
    ) {
        return false;
    }
    if is_velocity_current.is_some_and(|f| !f()) {
        return is_current.is_none_or(|f| f());
    }
    let now_on_walkable = body.on_walkable();
    handle_all_collisions(
        body,
        collision_normal_valid,
        collision_normal,
        previous_contact,
        previous_on_walkable,
        now_on_walkable,
    );
    is_current.is_none_or(|f| f())
}

/// The contact half of [`commit_set_position_transition`]: bits, then the
/// hit-ground or leave-ground callback on a walkable edge.
pub fn commit_set_position_contact_transition(
    body: &mut PhysicsBody,
    in_contact: bool,
    on_walkable: bool,
    previous_on_walkable: bool,
    hit_ground: Option<&mut dyn FnMut(&mut PhysicsBody)>,
    leave_ground: Option<&mut dyn FnMut(&mut PhysicsBody)>,
    is_current: Option<&dyn Fn() -> bool>,
) -> bool {
    let final_on_walkable =
        commit_set_position_contact_prefix(body, in_contact, on_walkable, previous_on_walkable);

    if !previous_on_walkable && final_on_walkable {
        if let Some(f) = hit_ground {
            f(body);
        }
        if is_current.is_some_and(|f| !f()) {
            return false;
        }
    } else if previous_on_walkable && !final_on_walkable {
        if let Some(f) = leave_ground {
            f(body);
        }
        if is_current.is_some_and(|f| !f()) {
            return false;
        }
    }
    commit_set_position_post_ground(body);
    is_current.is_none_or(|f| f())
}

/// Sets the bits as retail's set-position does: the previous walkable bit
/// and the contact bit first (acceleration from those), then the final
/// walkable and water bits. Returns the final walkable bit.
pub fn commit_set_position_contact_prefix(
    body: &mut PhysicsBody,
    in_contact: bool,
    on_walkable: bool,
    previous_on_walkable: bool,
) -> bool {
    body.transient_state
        .set(TransientState::ON_WALKABLE, previous_on_walkable);
    body.transient_state
        .set(TransientState::CONTACT, in_contact);
    body.calc_acceleration();
    let final_on_walkable = in_contact && on_walkable;
    body.transient_state
        .set(TransientState::ON_WALKABLE, final_on_walkable);
    let water = body.contact_plane_is_water;
    body.transient_state
        .set(TransientState::WATER_CONTACT, water);
    final_on_walkable
}

/// Recomputes acceleration after the ground callbacks.
pub fn commit_set_position_post_ground(body: &mut PhysicsBody) {
    body.calc_acceleration();
}

/// The collision response: a body stuck in the air (stationary-fall count
/// above 1) stops; otherwise a body hitting something it was not walking
/// on bounces off it (`v += -(v·n)(elasticity + 1) n` when moving into it)
/// or, when inelastic, stops.
pub fn handle_all_collisions(
    body: &mut PhysicsBody,
    collision_normal_valid: bool,
    collision_normal: Vec3,
    _prev_contact: bool,
    prev_on_walkable: bool,
    now_on_walkable: bool,
) {
    let sledding = body.state.contains(PhysicsState::SLEDDING);
    let should_reflect = !(prev_on_walkable && now_on_walkable && !sledding);

    if body.frames_stationary_fall <= 1 {
        if should_reflect && collision_normal_valid {
            if body.state.contains(PhysicsState::INELASTIC) {
                body.velocity = Vec3::ZERO;
            } else {
                let dot = body.velocity.dot(collision_normal);
                if dot < 0.0 {
                    let k = -(dot * (body.elasticity + 1.0));
                    body.velocity += collision_normal * k;
                }
            }
        }
    } else {
        body.velocity = Vec3::ZERO;
    }
}
