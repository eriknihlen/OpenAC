//! Stepping a missile (or any single-sphere object without a motion
//! interpreter) through the world: integrate one quantum, then sweep the
//! displacement and commit what the sweep allowed.

use ac_core::physics::{PhysicsState, TransientState};
use glam::{Quat, Vec3};

use crate::body::{CellPosition, PhysicsBody};
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::frame_math::set_vector_heading;
use crate::globals::{DEFAULT_STEP_HEIGHT, EPSILON};
use crate::obj_update::{apply_set_position_contact, handle_all_collisions};
use crate::state::ObjectInfoState;

/// A projectile's collision sphere as its setup gives it, and the object's
/// scale.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProjectileCollisionSphere {
    /// Centre in the setup's frame (unscaled).
    pub setup_local_origin: Vec3,
    /// Radius (unscaled).
    pub setup_radius: f32,
    /// The object's scale.
    pub object_scale: f32,
}

impl ProjectileCollisionSphere {
    /// A sphere at scale 1.
    #[must_use]
    pub fn new(setup_local_origin: Vec3, setup_radius: f32) -> Self {
        Self {
            setup_local_origin,
            setup_radius,
            object_scale: 1.0,
        }
    }

    /// The scaled centre.
    #[must_use]
    pub fn local_origin(&self) -> Vec3 {
        self.setup_local_origin * self.object_scale
    }

    /// The scaled radius.
    #[must_use]
    pub fn radius(&self) -> f32 {
        self.setup_radius * self.object_scale
    }

    /// Finite, positive and larger than the collision tolerance.
    #[must_use]
    pub fn is_valid(&self) -> bool {
        let origin = self.local_origin();
        let radius = self.radius();
        self.setup_radius.is_finite()
            && self.setup_radius > 0.0
            && self.object_scale.is_finite()
            && self.object_scale > 0.0
            && radius.is_finite()
            && radius > EPSILON
            && origin.x.is_finite()
            && origin.y.is_finite()
            && origin.z.is_finite()
    }
}

/// What advancing a projectile did.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ProjectileAdvanceResult {
    /// The cell it is in.
    pub cell_id: u32,
    /// How many quanta ran.
    pub quantum_count: i32,
    /// Anything ran.
    pub simulated: bool,
    /// It hit something.
    pub collision_normal_valid: bool,
    /// The last hit's normal.
    pub collision_normal: Vec3,
    /// Every sweep succeeded.
    pub transition_ok: bool,
}

/// The integrator's state at one moment.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ProjectileQuantumDynamics {
    /// Velocity.
    pub velocity: Vec3,
    /// Cached velocity.
    pub cached_velocity: Vec3,
    /// Acceleration.
    pub acceleration: Vec3,
    /// Angular velocity.
    pub omega: Vec3,
    /// Transient bits.
    pub transient_state: TransientState,
    /// Last update time.
    pub last_update_time: f64,
}

/// A quantum integrated but not yet swept.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ProjectileQuantumPreparation {
    /// The cell.
    pub cell_id: u32,
    /// The quantum, seconds.
    pub quantum: f32,
    /// It was integrated.
    pub simulated: bool,
    /// It moved, so it needs a sweep.
    pub requires_transition: bool,
    /// Position before.
    pub begin_position: Vec3,
    /// Orientation before.
    pub begin_orientation: Quat,
    /// Cell position before.
    pub begin_cell_position: CellPosition,
    /// In the world before.
    pub begin_in_world: bool,
    /// Integrator state before.
    pub begin_dynamics: ProjectileQuantumDynamics,
    /// Where integration put it.
    pub candidate_position: Vec3,
    /// How integration turned it.
    pub candidate_orientation: Quat,
    /// Integrator state after.
    pub candidate_dynamics: ProjectileQuantumDynamics,
    /// It touched a surface before.
    pub previous_contact: bool,
    /// It stood on walkable ground before.
    pub previous_on_walkable: bool,
}

impl ProjectileQuantumPreparation {
    fn not_simulated(cell_id: u32, quantum: f32) -> Self {
        Self {
            cell_id,
            quantum,
            simulated: false,
            requires_transition: false,
            begin_position: Vec3::ZERO,
            begin_orientation: Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            begin_cell_position: CellPosition::default(),
            begin_in_world: false,
            begin_dynamics: ProjectileQuantumDynamics::default(),
            candidate_position: Vec3::ZERO,
            candidate_orientation: Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            candidate_dynamics: ProjectileQuantumDynamics::default(),
            previous_contact: false,
            previous_on_walkable: false,
        }
    }
}

/// Why a projectile step was refused.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum ProjectileError {
    /// The game clock is not finite.
    ClockNotFinite,
    /// The body's last update time is not finite.
    LastUpdateNotFinite,
    /// The quantum is not finite, positive and at most 0.2 s.
    BadQuantum(f32),
}

impl std::fmt::Display for ProjectileError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::ClockNotFinite => f.write_str("the game clock must be finite"),
            Self::LastUpdateNotFinite => {
                f.write_str("the projectile's prior update timestamp must be finite")
            }
            Self::BadQuantum(q) => write!(
                f,
                "an object quantum must be finite and no larger than the maximum quantum, got {q}"
            ),
        }
    }
}

impl std::error::Error for ProjectileError {}

fn capture_dynamics(body: &PhysicsBody) -> ProjectileQuantumDynamics {
    ProjectileQuantumDynamics {
        velocity: body.velocity,
        cached_velocity: body.cached_velocity,
        acceleration: body.acceleration,
        omega: body.omega,
        transient_state: body.transient_state,
        last_update_time: body.last_update_time,
    }
}

fn apply_dynamics(body: &mut PhysicsBody, d: &ProjectileQuantumDynamics) {
    body.velocity = d.velocity;
    body.cached_velocity = d.cached_velocity;
    body.acceleration = d.acceleration;
    body.omega = d.omega;
    body.transient_state = d.transient_state;
    body.last_update_time = d.last_update_time;
}

fn restore_begin_frame(
    body: &mut PhysicsBody,
    begin_position: Vec3,
    begin_orientation: Quat,
    begin_cell_position: &CellPosition,
    begin_in_world: bool,
) {
    body.orientation = begin_orientation;
    if begin_cell_position.obj_cell_id != 0 {
        body.snap_to_cell(
            begin_cell_position.obj_cell_id,
            begin_position,
            begin_cell_position.frame.origin,
        );
    } else {
        body.set_frame_in_current_cell(begin_position, begin_orientation);
    }
    body.in_world = begin_in_world;
}

fn suspended(body: &PhysicsBody, cell_id: u32, is_parented: bool) -> bool {
    is_parented
        || !body.in_world
        || body.state.contains(PhysicsState::FROZEN)
        || body.state.contains(PhysicsState::STATIC)
        || cell_id == 0
}

/// Advances a projectile body to `current_time` in quanta.
#[allow(clippy::too_many_arguments)]
pub fn advance(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    current_time: f64,
    mut cell_id: u32,
    sphere: ProjectileCollisionSphere,
    moving_entity_id: u32,
    designated_target_id: u32,
    is_parented: bool,
) -> Result<ProjectileAdvanceResult, ProjectileError> {
    if !current_time.is_finite() {
        return Err(ProjectileError::ClockNotFinite);
    }
    if !body.last_update_time.is_finite() {
        return Err(ProjectileError::LastUpdateNotFinite);
    }
    let idle = |cell_id: u32, ok: bool| ProjectileAdvanceResult {
        cell_id,
        transition_ok: ok,
        ..ProjectileAdvanceResult::default()
    };

    if !sphere.is_valid() {
        return Ok(idle(cell_id, false));
    }
    if suspended(body, cell_id, is_parented) {
        body.transient_state.remove(TransientState::ACTIVE);
        return Ok(idle(cell_id, true));
    }
    if !body.is_active() {
        return Ok(idle(cell_id, true));
    }

    let mut elapsed = current_time - body.last_update_time;
    if elapsed <= f64::from(EPSILON) || elapsed > f64::from(PhysicsBody::HUGE_QUANTUM) {
        body.last_update_time = current_time;
        return Ok(idle(cell_id, true));
    }

    let mut quantum_count = 0;
    let mut collision_normal_valid = false;
    let mut collision_normal = Vec3::ZERO;
    let mut transition_ok = true;
    let mut physics_time = body.last_update_time;

    let mut step =
        |body: &mut PhysicsBody, dt: f32, cell_id: &mut u32| -> Result<(), ProjectileError> {
            let preparation = begin_quantum(body, dt, *cell_id, sphere, false)?;
            let result = complete_quantum(
                engine,
                body,
                &preparation,
                sphere,
                moving_entity_id,
                designated_target_id,
            );
            if result.cell_id != 0 {
                *cell_id = result.cell_id;
            }
            collision_normal_valid |= result.collision_normal_valid;
            if result.collision_normal_valid {
                collision_normal = result.collision_normal;
            }
            transition_ok &= result.transition_ok;
            Ok(())
        };

    while elapsed > f64::from(PhysicsBody::MAX_QUANTUM) {
        physics_time += f64::from(PhysicsBody::MAX_QUANTUM);
        step(body, PhysicsBody::MAX_QUANTUM, &mut cell_id)?;
        quantum_count += 1;
        elapsed -= f64::from(PhysicsBody::MAX_QUANTUM);
    }
    if elapsed > f64::from(PhysicsBody::MIN_QUANTUM) {
        let quantum = elapsed as f32;
        physics_time += elapsed;
        step(body, quantum, &mut cell_id)?;
        quantum_count += 1;
    }

    body.last_update_time = physics_time;

    Ok(ProjectileAdvanceResult {
        cell_id,
        quantum_count,
        simulated: quantum_count != 0,
        collision_normal_valid,
        collision_normal,
        transition_ok,
    })
}

/// Advances a projectile by one admitted quantum.
#[allow(clippy::too_many_arguments)]
pub fn advance_quantum(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    quantum: f32,
    cell_id: u32,
    sphere: ProjectileCollisionSphere,
    moving_entity_id: u32,
    designated_target_id: u32,
    is_parented: bool,
) -> Result<ProjectileAdvanceResult, ProjectileError> {
    let preparation = begin_quantum(body, quantum, cell_id, sphere, is_parented)?;
    Ok(complete_quantum(
        engine,
        body,
        &preparation,
        sphere,
        moving_entity_id,
        designated_target_id,
    ))
}

/// Integrates one quantum and records where it would move the body; the
/// body itself is left as it was.
pub fn begin_quantum(
    body: &mut PhysicsBody,
    quantum: f32,
    cell_id: u32,
    sphere: ProjectileCollisionSphere,
    is_parented: bool,
) -> Result<ProjectileQuantumPreparation, ProjectileError> {
    if !quantum.is_finite() || quantum <= EPSILON || quantum > PhysicsBody::MAX_QUANTUM {
        return Err(ProjectileError::BadQuantum(quantum));
    }
    if !sphere.is_valid() {
        return Ok(ProjectileQuantumPreparation::not_simulated(
            cell_id, quantum,
        ));
    }
    if suspended(body, cell_id, is_parented) {
        body.transient_state.remove(TransientState::ACTIVE);
        return Ok(ProjectileQuantumPreparation::not_simulated(
            cell_id, quantum,
        ));
    }
    if !body.is_active() {
        return Ok(ProjectileQuantumPreparation::not_simulated(
            cell_id, quantum,
        ));
    }

    let begin_position = body.position();
    let begin_orientation = body.orientation;
    let begin_cell_position = body.cell_position();
    let begin_in_world = body.in_world;
    let begin_dynamics = capture_dynamics(body);
    let previous_contact = body.in_contact();
    let previous_on_walkable = body.on_walkable();

    body.calc_acceleration();
    body.update_physics_internal(quantum);

    let candidate_position = body.position();
    let mut candidate_orientation = body.orientation;
    let displacement = candidate_position - begin_position;
    let candidate_moved = displacement != Vec3::ZERO;
    if candidate_moved && body.state.contains(PhysicsState::ALIGN_PATH) {
        candidate_orientation = set_vector_heading(candidate_orientation, displacement);
    }

    if !candidate_moved {
        body.cached_velocity = Vec3::ZERO;
        body.orientation = candidate_orientation;
        body.last_update_time += f64::from(quantum);
    } else {
        body.set_position(begin_position);
        body.orientation = begin_orientation;
    }

    let candidate_dynamics = capture_dynamics(body);
    restore_begin_frame(
        body,
        begin_position,
        begin_orientation,
        &begin_cell_position,
        begin_in_world,
    );
    apply_dynamics(body, &begin_dynamics);

    Ok(ProjectileQuantumPreparation {
        cell_id,
        quantum,
        simulated: true,
        requires_transition: candidate_moved,
        begin_position,
        begin_orientation,
        begin_cell_position,
        begin_in_world,
        begin_dynamics,
        candidate_position,
        candidate_orientation,
        candidate_dynamics,
        previous_contact,
        previous_on_walkable,
    })
}

/// Sweeps a prepared quantum's displacement and commits the result.
pub fn complete_quantum(
    engine: &PhysicsEngine,
    body: &mut PhysicsBody,
    preparation: &ProjectileQuantumPreparation,
    sphere: ProjectileCollisionSphere,
    moving_entity_id: u32,
    designated_target_id: u32,
) -> ProjectileAdvanceResult {
    if !preparation.simulated {
        return ProjectileAdvanceResult {
            cell_id: preparation.cell_id,
            transition_ok: true,
            ..ProjectileAdvanceResult::default()
        };
    }
    apply_dynamics(body, &preparation.candidate_dynamics);
    if !preparation.requires_transition {
        body.set_frame_in_current_cell(
            preparation.candidate_position,
            preparation.candidate_orientation,
        );
        return ProjectileAdvanceResult {
            cell_id: preparation.cell_id,
            quantum_count: 1,
            simulated: true,
            transition_ok: true,
            ..ProjectileAdvanceResult::default()
        };
    }

    let mut cell_id = preparation.cell_id;
    let mover_flags = if body.state.contains(PhysicsState::PATH_CLIPPED) {
        ObjectInfoState(ObjectInfoState::PATH_CLIPPED)
    } else {
        ObjectInfoState(ObjectInfoState::NONE)
    };
    let resolved = engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: preparation.begin_position,
            target_pos: preparation.candidate_position,
            cell_id,
            sphere_radius: sphere.radius(),
            sphere_height: 0.0,
            step_up_height: DEFAULT_STEP_HEIGHT,
            step_down_height: 0.0,
            is_on_ground: preparation.previous_on_walkable,
            mover_flags,
            moving_entity_id,
            local_sphere_origin: Some(sphere.local_origin()),
            begin_orientation: Some(preparation.begin_orientation),
            end_orientation: Some(preparation.candidate_orientation),
            designated_target_id,
            ..ResolveRequest::default()
        },
        Some(body),
    );

    let mut collision_normal_valid = false;
    let mut collision_normal = Vec3::ZERO;
    let transition_ok = resolved.ok;
    if !resolved.ok {
        body.set_frame_in_current_cell(
            preparation.candidate_position,
            preparation.candidate_orientation,
        );
        body.cached_velocity = Vec3::ZERO;
    } else {
        body.cached_velocity =
            (resolved.position - preparation.begin_position) / preparation.quantum;
        body.set_position(resolved.position);
        body.orientation = if resolved.orientation == Quat::from_xyzw(0.0, 0.0, 0.0, 0.0) {
            preparation.candidate_orientation
        } else {
            resolved.orientation
        };
        if resolved.cell_id != 0 {
            cell_id = resolved.cell_id;
        }
        apply_set_position_contact(body, resolved.in_contact, resolved.on_walkable);
        let now_on_walkable = body.on_walkable();
        handle_all_collisions(
            body,
            resolved.collision_normal_valid,
            resolved.collision_normal,
            preparation.previous_contact,
            preparation.previous_on_walkable,
            now_on_walkable,
        );
        if resolved.collision_normal_valid {
            collision_normal_valid = true;
            collision_normal = resolved.collision_normal;
        }
    }

    body.last_update_time += f64::from(preparation.quantum);
    ProjectileAdvanceResult {
        cell_id,
        quantum_count: 1,
        simulated: true,
        collision_normal_valid,
        collision_normal,
        transition_ok,
    }
}
