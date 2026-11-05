//! Missiles: an object the server marks as a missile, whose setup is one
//! collision sphere and which the runtime does not animate (an arrow, a
//! bolt, a spell projectile), flies by its own physics between the
//! server's updates: its velocity and spin as sent, gravity when its state
//! asks for it, swept against the resident world each quantum. The server's
//! velocity updates replace its velocity; a placement (a teleport, a far
//! snap) puts it where the server says; an ordinary position leaves the
//! flight alone.

use ac_physics::projectile::{ProjectileCollisionSphere, advance_quantum};
use ac_physics::quantum::{
    RetailObjectActivityResult, RetailObjectQuantumClock, evaluate_activity,
};
use ac_physics::{PhysicsBody, PhysicsState, TransientState, Vec3};
use ac_proto::Position;

use super::content::WorldContent;
use super::remote::{wire_from_world, wire_orientation};
use crate::world::{ObjectKey, WorldObject};

/// A missile in flight.
#[derive(Debug)]
pub struct Projectile {
    /// The object.
    pub guid: u32,
    /// Its presentation key.
    pub key: ObjectKey,
    /// Its body.
    pub body: PhysicsBody,
    /// Its one collision sphere.
    pub sphere: ProjectileCollisionSphere,
    /// The cell it is in.
    pub cell_id: u32,
    clock: RetailObjectQuantumClock,
}

/// The friction a missile flies with: as sent when within 0..=1, else the
/// default.
#[must_use]
pub fn normalize_friction(value: Option<f32>) -> f32 {
    match value {
        Some(v) if v.is_finite() && (0.0..=1.0).contains(&v) => v,
        _ => PhysicsBody::DEFAULT_FRICTION,
    }
}

/// The elasticity a missile bounces with: none for a missing, NaN or
/// non-positive value, at most 0.1.
#[must_use]
pub fn normalize_elasticity(value: Option<f32>) -> f32 {
    let v = value.unwrap_or(0.05);
    if v.is_nan() || v <= 0.0 {
        0.0
    } else {
        v.min(0.1)
    }
}

/// The collision sphere of a missile's setup: its one sphere at the
/// object's scale; none when the setup has more or fewer, or the sphere is
/// unusable.
#[must_use]
pub fn collision_sphere(spheres: &[(Vec3, f32)], scale: f32) -> Option<ProjectileCollisionSphere> {
    let [(origin, radius)] = spheres else {
        return None;
    };
    let sphere = ProjectileCollisionSphere {
        setup_local_origin: *origin,
        setup_radius: *radius,
        object_scale: scale,
    };
    sphere.is_valid().then_some(sphere)
}

fn vector(v: Option<ac_proto::Vector3>) -> Vec3 {
    v.map_or(Vec3::ZERO, |v| Vec3::new(v.x, v.y, v.z))
}

fn is_finite(v: Vec3) -> bool {
    v.x.is_finite() && v.y.is_finite() && v.z.is_finite()
}

impl Projectile {
    /// Bind a body for `object` when it is a missile with a one-sphere
    /// setup, at its accepted position (in the engine's frame at `origin`,
    /// the corner of its landblock), flying from `now`.
    #[must_use]
    pub fn bind(
        object: &WorldObject,
        content: &WorldContent,
        origin: Vec3,
        now: f64,
    ) -> Option<Self> {
        let physics = object.physics();
        if physics.state & PhysicsState::MISSILE.bits() == 0 || object.parent().is_some() {
            return None;
        }
        let accepted = object.accepted_position()?;
        let setup = physics.setup_id.and_then(|id| content.setup(id))?;
        let scale = physics.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let spheres: Vec<(Vec3, f32)> = setup
            .spheres
            .iter()
            .map(|s| (Vec3::new(s.origin.x, s.origin.y, s.origin.z), s.radius))
            .collect();
        let sphere = collision_sphere(&spheres, scale)?;
        let velocity = vector(physics.velocity);
        let omega = vector(physics.omega);
        let local = Vec3::new(accepted.origin.x, accepted.origin.y, accepted.origin.z);
        if !is_finite(velocity) || !is_finite(omega) || !is_finite(local) || !now.is_finite() {
            return None;
        }
        let mut body = PhysicsBody::default();
        body.friction = normalize_friction(physics.friction);
        body.elasticity = normalize_elasticity(physics.elasticity);
        body.orientation = wire_orientation(accepted);
        body.state = PhysicsState::from(physics.state);
        body.velocity = velocity;
        body.omega = omega;
        body.last_update_time = now;
        body.in_world = true;
        body.snap_to_cell(accepted.cell_id, local + origin, local);
        body.calc_acceleration();
        if !body.state.contains(PhysicsState::STATIC) {
            body.transient_state.insert(TransientState::ACTIVE);
        }
        Some(Self {
            guid: object.guid(),
            key: object.key(),
            body,
            sphere,
            cell_id: accepted.cell_id,
            clock: RetailObjectQuantumClock::new(),
        })
    }

    /// Fly `dt` seconds through the resident world (nothing while hidden,
    /// or asleep beyond the player's physics range).
    pub fn advance(
        &mut self,
        engine: &ac_physics::PhysicsEngine,
        dt: f32,
        player_position: Option<Vec3>,
    ) {
        if self.body.state.contains(PhysicsState::HIDDEN) {
            return;
        }
        let position = self.body.position();
        let is_static = self.body.state.contains(PhysicsState::STATIC);
        let gate = evaluate_activity(
            &mut self.clock,
            Some(&mut self.body),
            true,
            true,
            is_static,
            position,
            player_position,
            f64::from(dt),
        );
        // A missile that just woke flies from the next update on.
        if gate != RetailObjectActivityResult::Active {
            return;
        }
        let batch = self.clock.advance(f64::from(dt));
        for i in 0..batch.count() {
            let Some(quantum) = batch.get_quantum(i) else {
                break;
            };
            match advance_quantum(
                engine,
                &mut self.body,
                quantum,
                self.cell_id,
                self.sphere,
                self.key.local_id,
                0,
                false,
            ) {
                Ok(result) => {
                    if result.cell_id != 0 {
                        self.cell_id = result.cell_id;
                    }
                    if !result.simulated {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    }

    /// The server's velocity and spin replace the flight's; a non-finite
    /// value is refused (`false`).
    pub fn apply_vector(&mut self, velocity: Vec3, omega: Vec3, now: f64) -> bool {
        if !is_finite(velocity) || !is_finite(omega) || !now.is_finite() {
            return false;
        }
        self.body.set_velocity(velocity);
        self.body.omega = omega;
        if !self.body.state.contains(PhysicsState::STATIC) {
            self.body.transient_state.insert(TransientState::ACTIVE);
        }
        true
    }

    /// The server's state bits: a missile that becomes one again flies on
    /// from `now`.
    pub fn apply_state(&mut self, state: u32, now: f64) {
        let was_missile = self.body.state.contains(PhysicsState::MISSILE);
        self.body.state = PhysicsState::from(state);
        self.body.calc_acceleration();
        if self.body.state.contains(PhysicsState::MISSILE) && !was_missile && now.is_finite() {
            self.body.last_update_time = now;
        }
    }

    /// Put the missile where the server says (a placement), flying on from
    /// `now`.
    pub fn place(&mut self, accepted: &Position, origin: Vec3, now: f64) {
        let local = Vec3::new(accepted.origin.x, accepted.origin.y, accepted.origin.z);
        self.body
            .snap_to_cell(accepted.cell_id, local + origin, local);
        self.body.orientation = wire_orientation(accepted);
        self.cell_id = accepted.cell_id;
        if now.is_finite() {
            self.body.last_update_time = now;
        }
    }

    /// Where the missile is, as the wire carries positions, given the
    /// corner of its landblock.
    #[must_use]
    pub fn live_position(&self, landblock_origin: Vec3) -> Position {
        // The body's world position, made relative to its landblock's
        // corner (its cell frame is relative to the landblock already).
        let world = ac_physics::CellPosition::new(
            self.cell_id,
            self.body.position(),
            self.body.orientation,
        );
        wire_from_world(&world, landblock_origin)
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `ProjectileController` (`NormalizeFriction`,
    //! `NormalizeElasticity`, `TryGetCollisionSphere`).
    use super::*;

    #[test]
    fn friction_outside_zero_to_one_is_the_default() {
        assert_eq!(normalize_friction(Some(0.3)), 0.3);
        // Bind_AcceptsRetailZeroFrictionBoundary.
        assert_eq!(normalize_friction(Some(0.0)), 0.0);
        assert_eq!(normalize_friction(Some(1.5)), PhysicsBody::DEFAULT_FRICTION);
        assert_eq!(
            normalize_friction(Some(-0.1)),
            PhysicsBody::DEFAULT_FRICTION
        );
        assert_eq!(normalize_friction(None), PhysicsBody::DEFAULT_FRICTION);
        assert_eq!(
            normalize_friction(Some(f32::NAN)),
            PhysicsBody::DEFAULT_FRICTION
        );
    }

    #[test]
    fn elasticity_is_held_to_a_tenth() {
        assert_eq!(normalize_elasticity(None), 0.05);
        assert_eq!(normalize_elasticity(Some(0.5)), 0.1);
        assert_eq!(normalize_elasticity(Some(-1.0)), 0.0);
        assert_eq!(normalize_elasticity(Some(f32::NAN)), 0.0);
    }

    #[test]
    fn only_a_one_sphere_setup_flies() {
        let one = [(Vec3::new(0.0, 0.0, 0.1), 0.2)];
        let s = collision_sphere(&one, 2.0).expect("one sphere");
        assert_eq!(s.radius(), 0.4);
        assert!(collision_sphere(&[], 1.0).is_none());
        assert!(collision_sphere(&[one[0], one[0]], 1.0).is_none());
        assert!(collision_sphere(&[(Vec3::ZERO, 0.0)], 1.0).is_none());
    }
}
