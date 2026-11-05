//! Ordinary bodies: a server object the runtime neither animates (no
//! motion) nor flies as a missile, once the server gives it a velocity or
//! spin, moves by its own physics between the server's updates: each
//! quantum its velocity, gravity and spin integrate, and the move is swept
//! through the resident world with its setup's shape (crossing into the
//! next cell or landblock as it goes). An object with no shape to sweep
//! (a sign hung on a wall) keeps its place and only turns; its velocity
//! keeps integrating.

use ac_physics::obj_update::commit_set_position_transition;
use ac_physics::quantum::{
    RetailObjectActivityResult, RetailObjectQuantumClock, evaluate_activity,
};
use ac_physics::{
    CollisionSphere, ObjectInfoState, PhysicsBody, PhysicsEngine, PhysicsState, Quat,
    ResolveRequest, TransientState, Vec3,
};
use ac_proto::Position;

use super::content::WorldContent;
use super::remote::{wire_from_world, wire_orientation};
use crate::world::{ObjectKey, WorldObject};

/// Below this radius (and with no sphere list) an object has nothing to
/// sweep.
const MINIMUM_SWEEP_RADIUS: f32 = 0.05;

/// What an ordinary body sweeps with: its setup's cylinder, sphere list and
/// step heights at the object's scale.
#[derive(Debug, Clone, Default)]
pub struct OrdinaryShape {
    /// Cylinder radius, metres.
    pub radius: f32,
    /// Cylinder height, metres.
    pub height: f32,
    /// The setup's spheres (override the cylinder when present).
    pub spheres: Vec<CollisionSphere>,
    /// The spheres' scale.
    pub sphere_scale: f32,
    /// Step-up height, metres.
    pub step_up_height: f32,
    /// Step-down height, metres.
    pub step_down_height: f32,
}

impl OrdinaryShape {
    /// Whether there is anything to sweep.
    #[must_use]
    pub fn sweeps(&self) -> bool {
        !self.spheres.is_empty() || self.radius >= MINIMUM_SWEEP_RADIUS
    }
}

/// What one quantum did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OrdinaryStep {
    /// The cell the body is in after it.
    pub cell_id: u32,
    /// Its position or orientation changed.
    pub frame_changed: bool,
}

/// One quantum of an ordinary body in `cell_id`: the root motion's step
/// (`root_origin` at `object_scale`, in the body's frame, only while on
/// walkable ground; `root_orientation` turns it), the physics integration,
/// then the sweep of the move with `shape`. A refused sweep keeps the
/// origin; with no shape the origin is kept and a body standing on walkable
/// ground goes inactive.
#[allow(clippy::too_many_arguments)]
pub fn advance_quantum(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    cell_id: u32,
    shape: &OrdinaryShape,
    root_origin: Vec3,
    root_orientation: Quat,
    object_scale: f32,
    quantum: f32,
    moving_entity_id: u32,
    is_player: bool,
) -> OrdinaryStep {
    let prior_position = body.position();
    let prior_orientation = body.orientation;
    let previous_contact = body.in_contact();
    let previous_on_walkable = body.on_walkable();

    let mut candidate_position = prior_position;
    if body.on_walkable() && root_origin != Vec3::ZERO {
        candidate_position += prior_orientation * (root_origin * object_scale);
    }
    let mut candidate_orientation = prior_orientation;
    if root_orientation != Quat::IDENTITY {
        candidate_orientation = ac_motion::set_rotate(
            candidate_position,
            prior_orientation,
            prior_orientation * root_orientation,
        );
    }
    body.set_frame_in_current_cell(candidate_position, candidate_orientation);
    body.calc_acceleration();
    body.update_physics_internal(quantum);
    let (position, orientation) = (body.position(), body.orientation);
    body.set_frame_in_current_cell(position, orientation);

    let integrated = body.position();
    let mut resolved_cell = cell_id;
    let hold_origin = |body: &mut PhysicsBody, deactivate_on_walkable: bool| {
        let orientation = body.orientation;
        body.set_frame_in_current_cell(prior_position, orientation);
        body.cached_velocity = Vec3::ZERO;
        if deactivate_on_walkable && body.on_walkable() {
            body.transient_state.remove(TransientState::ACTIVE);
        }
    };
    if integrated != prior_position && cell_id != 0 && shape.sweeps() {
        let mover_flags = if is_player {
            ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE
        } else {
            ObjectInfoState::EDGE_SLIDE
        };
        let resolved = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: prior_position,
                target_pos: integrated,
                cell_id,
                sphere_radius: shape.radius,
                sphere_height: shape.height,
                step_up_height: shape.step_up_height,
                step_down_height: shape.step_down_height,
                is_on_ground: previous_on_walkable,
                mover_flags: ObjectInfoState(mover_flags),
                moving_entity_id,
                sphere_list: shape.spheres.clone(),
                sphere_scale: shape.sphere_scale,
                ..ResolveRequest::default()
            },
            Some(body),
        );
        if resolved.ok {
            resolved_cell = if resolved.cell_id != 0 {
                resolved.cell_id
            } else {
                cell_id
            };
            body.commit_transition_position(resolved_cell, resolved.position);
            commit_set_position_transition(
                body,
                resolved.in_contact,
                resolved.on_walkable,
                resolved.collision_normal_valid,
                resolved.collision_normal,
                previous_contact,
                previous_on_walkable,
                None,
                None,
                None,
                None,
            );
            body.cached_velocity = if quantum > 0.0 {
                (body.position() - prior_position) / quantum
            } else {
                Vec3::ZERO
            };
        } else {
            hold_origin(body, false);
        }
    } else if integrated != prior_position && !shape.sweeps() {
        hold_origin(body, true);
    } else {
        body.cached_velocity = Vec3::ZERO;
    }
    OrdinaryStep {
        cell_id: resolved_cell,
        frame_changed: body.position() != prior_position || body.orientation != prior_orientation,
    }
}

fn vector(v: Option<ac_proto::Vector3>) -> Vec3 {
    v.map_or(Vec3::ZERO, |v| Vec3::new(v.x, v.y, v.z))
}

fn is_finite(v: Vec3) -> bool {
    v.x.is_finite() && v.y.is_finite() && v.z.is_finite()
}

/// An ordinary body in the world.
#[derive(Debug)]
pub struct OrdinaryBody {
    /// The object.
    pub guid: u32,
    /// Its presentation key.
    pub key: ObjectKey,
    /// Its body.
    pub body: PhysicsBody,
    /// What it sweeps with.
    pub shape: OrdinaryShape,
    /// The cell it is in.
    pub cell_id: u32,
    clock: RetailObjectQuantumClock,
}

impl OrdinaryBody {
    /// A body for `object` at its accepted position (in the engine's frame
    /// at `origin`, the corner of its landblock), moving from `now` with
    /// the velocity and spin its description carries. `None` for a held
    /// object, or one without a placement.
    #[must_use]
    pub fn bind(
        object: &WorldObject,
        content: &WorldContent,
        origin: Vec3,
        now: f64,
    ) -> Option<Self> {
        if object.parent().is_some() {
            return None;
        }
        let accepted = object.accepted_position()?;
        let physics = object.physics();
        let local = Vec3::new(accepted.origin.x, accepted.origin.y, accepted.origin.z);
        let velocity = vector(physics.velocity);
        let omega = vector(physics.omega);
        if !is_finite(velocity) || !is_finite(omega) || !is_finite(local) || !now.is_finite() {
            return None;
        }
        let scale = physics.scale.filter(|s| *s > 0.0).unwrap_or(1.0);
        let mut shape = OrdinaryShape {
            sphere_scale: scale,
            ..OrdinaryShape::default()
        };
        if let Some(setup) = physics.setup_id.and_then(|id| content.setup(id)) {
            shape.radius = setup.radius * scale;
            shape.height = setup.height * scale;
            shape.step_up_height = setup.step_up_height * scale;
            shape.step_down_height = setup.step_down_height * scale;
            shape.spheres = setup
                .spheres
                .iter()
                .map(|s| {
                    CollisionSphere::new(Vec3::new(s.origin.x, s.origin.y, s.origin.z), s.radius)
                })
                .collect();
        }
        let mut body = PhysicsBody::default();
        body.friction = super::projectile::normalize_friction(physics.friction);
        body.elasticity = super::projectile::normalize_elasticity(physics.elasticity);
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
            shape,
            cell_id: accepted.cell_id,
            clock: RetailObjectQuantumClock::new(),
        })
    }

    /// Move `dt` seconds through the resident world (nothing while hidden,
    /// or asleep beyond the player's physics range). True when it moved or
    /// turned.
    pub fn advance(&mut self, engine: &PhysicsEngine, dt: f32, player: Option<Vec3>) -> bool {
        if self.body.state.contains(PhysicsState::HIDDEN) {
            return false;
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
            player,
            f64::from(dt),
        );
        if gate != RetailObjectActivityResult::Active {
            return false;
        }
        let batch = self.clock.advance(f64::from(dt));
        let mut changed = false;
        for i in 0..batch.count() {
            let Some(quantum) = batch.get_quantum(i) else {
                break;
            };
            let step = advance_quantum(
                engine,
                &mut self.body,
                self.cell_id,
                &self.shape,
                Vec3::ZERO,
                Quat::IDENTITY,
                1.0,
                quantum,
                self.key.local_id,
                false,
            );
            self.cell_id = step.cell_id;
            changed |= step.frame_changed;
        }
        changed
    }

    /// The server's velocity and spin replace the body's; a non-finite
    /// value is refused (`false`).
    pub fn apply_vector(&mut self, velocity: Vec3, omega: Vec3) -> bool {
        if !is_finite(velocity) || !is_finite(omega) {
            return false;
        }
        self.body.set_velocity(velocity);
        self.body.omega = omega;
        if !self.body.state.contains(PhysicsState::STATIC) {
            self.body.transient_state.insert(TransientState::ACTIVE);
        }
        true
    }

    /// The server's state bits.
    pub fn apply_state(&mut self, state: u32) {
        self.body.state = PhysicsState::from(state);
        self.body.calc_acceleration();
    }

    /// Where the body is, as the wire carries positions, given the corner
    /// of its landblock.
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
    //! OpenAC `tests/AcDream.App.Tests/Physics/LiveEntityOrdinaryPhysicsUpdaterTests.cs`
    //! (the step `RuntimeOrdinaryPhysicsUpdater.TryBegin` runs; the window's
    //! commit of the snapshot is the runtime's live position here).

    use ac_physics::{CollisionWorld, TerrainSurface};

    use super::*;

    const SOURCE_CELL: u32 = 0xA9B4_0039;
    const DESTINATION_CELL: u32 = 0xAAB4_0001;

    /// Two flat landblocks side by side at height 50.
    fn boundary_engine() -> PhysicsEngine {
        let mut engine = PhysicsEngine::new();
        engine.attach_world(CollisionWorld::new());
        for (id, x) in [(0xA9B4_FFFF, 0.0), (0xAAB4_FFFF, 192.0)] {
            engine.add_landblock(
                id,
                TerrainSurface::new(&[0u8; 81], &[50.0; 256], 0, 0, None),
                Vec::new(),
                Vec::new(),
                x,
                0.0,
            );
        }
        engine
    }

    fn body_at(position: Vec3, transient: TransientState) -> PhysicsBody {
        let mut body = PhysicsBody::default();
        body.transient_state = transient;
        body.in_world = true;
        body.snap_to_cell(SOURCE_CELL, position, position);
        body
    }

    #[test]
    fn a_root_step_crosses_into_the_next_landblock_and_takes_its_cell() {
        let engine = boundary_engine();
        let mut body = body_at(
            Vec3::new(191.0, 10.0, 50.0),
            TransientState::ACTIVE | TransientState::CONTACT | TransientState::ON_WALKABLE,
        );
        let shape = OrdinaryShape {
            radius: 0.48,
            height: 1.835,
            sphere_scale: 1.0,
            step_up_height: 0.4,
            step_down_height: 0.4,
            ..OrdinaryShape::default()
        };
        let step = advance_quantum(
            &engine,
            &mut body,
            SOURCE_CELL,
            &shape,
            Vec3::new(2.0, 0.0, 0.0),
            Quat::IDENTITY,
            1.0,
            0.1,
            1,
            true,
        );
        assert_eq!(step.cell_id, DESTINATION_CELL);
        assert_eq!(body.cell_position().obj_cell_id, DESTINATION_CELL);
        assert!(body.position().x > 192.0, "{:?}", body.position());
        assert!(step.frame_changed);
    }

    /// A server object whose setup carries no collision shape (a sign hung
    /// on a wall) keeps its origin under gravity; only its velocity
    /// integrates.
    #[test]
    fn no_collision_shape_holds_the_origin_under_gravity() {
        let engine = boundary_engine();
        let hung = Vec3::new(100.0, 10.0, 60.0);
        let mut body = body_at(hung, TransientState::ACTIVE);
        body.state = PhysicsState::GRAVITY;
        body.set_velocity(Vec3::new(0.0, 0.0, -1.0));
        let shape = OrdinaryShape {
            sphere_scale: 1.0,
            step_up_height: 0.01,
            step_down_height: 0.01,
            ..OrdinaryShape::default()
        };
        for _ in 0..5 {
            let step = advance_quantum(
                &engine,
                &mut body,
                SOURCE_CELL,
                &shape,
                Vec3::ZERO,
                Quat::IDENTITY,
                1.0,
                0.1,
                1,
                true,
            );
            assert_eq!(step.cell_id, SOURCE_CELL);
        }
        assert_eq!(body.position(), hung);
        assert!(body.velocity.z < 0.0, "the velocity keeps integrating");
        assert_eq!(body.cached_velocity, Vec3::ZERO);
    }

    #[test]
    fn a_body_with_a_velocity_falls_onto_the_ground_and_stays_in_its_cell() {
        let engine = boundary_engine();
        let mut body = body_at(Vec3::new(100.0, 10.0, 53.0), TransientState::ACTIVE);
        body.state = PhysicsState::GRAVITY;
        body.set_velocity(Vec3::new(1.0, 0.0, 0.0));
        let shape = OrdinaryShape {
            radius: 0.3,
            height: 0.6,
            sphere_scale: 1.0,
            step_up_height: 0.4,
            step_down_height: 0.4,
            ..OrdinaryShape::default()
        };
        for _ in 0..40 {
            advance_quantum(
                &engine,
                &mut body,
                SOURCE_CELL,
                &shape,
                Vec3::ZERO,
                Quat::IDENTITY,
                1.0,
                0.1,
                1,
                false,
            );
        }
        let at = body.position();
        assert!(at.x > 100.5, "it moved along: {at:?}");
        assert!((at.z - 50.0).abs() < 0.1, "it rests on the ground: {at:?}");
        assert!(body.in_contact());
    }
}
