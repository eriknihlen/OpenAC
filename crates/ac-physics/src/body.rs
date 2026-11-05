//! A physics object's body: where it is, how it moves, what it touches.
//!
//! Velocity is metres per second, acceleration metres per second squared,
//! angular velocity ("omega") radians per second about its axis. Time steps
//! ("quanta") are seconds: the body never integrates more than 0.2 s at a
//! time and drops updates more than 2 s late.

use ac_core::physics::{PhysicsState, TransientState, adjust_to_outside};
use ac_motion::Frame;
use glam::{Quat, Vec3};

use crate::geometry::Plane;
use crate::globals::{EPSILON, EPSILON_SQ};

/// cos 10°: sleds glide on ground flatter than this.
#[allow(clippy::excessive_precision)]
const COS_10_DEGREES: f32 = 0.984_807_75;

/// A position in the world as the game stores it: a cell and a frame
/// relative to that cell's landblock corner.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CellPosition {
    /// The cell.
    pub obj_cell_id: u32,
    /// Origin (landblock-relative) and orientation.
    pub frame: Frame,
}

impl CellPosition {
    /// A position from its parts.
    #[must_use]
    pub fn new(obj_cell_id: u32, origin: Vec3, orientation: Quat) -> Self {
        Self {
            obj_cell_id,
            frame: Frame::new(origin, orientation),
        }
    }
}

fn is_outdoor(cell: u32) -> bool {
    (1..=0x40).contains(&(cell & 0xFFFF))
}

fn adjust(cell: &mut u32, local: &mut Vec3) -> bool {
    let mut p = local.to_array();
    let ok = adjust_to_outside(cell, &mut p);
    *local = Vec3::from_array(p);
    ok
}

/// A physics object's body.
#[derive(Debug, Clone, PartialEq)]
pub struct PhysicsBody {
    position: Vec3,
    cell_position: CellPosition,
    /// In the world (placed).
    pub in_world: bool,
    /// Orientation.
    pub orientation: Quat,
    /// Velocity.
    pub velocity: Vec3,
    /// The velocity the last update actually moved at (reported, never
    /// integrated).
    pub cached_velocity: Vec3,
    /// How many updates the body has hung in the air without moving.
    pub frames_stationary_fall: i32,
    /// Acceleration.
    pub acceleration: Vec3,
    /// Angular velocity.
    pub omega: Vec3,
    /// The ground normal friction uses.
    pub ground_normal: Vec3,
    /// The wall normal the body slides along (horizontal).
    pub sliding_normal: Vec3,
    /// The surface the body stands on is known.
    pub contact_plane_valid: bool,
    /// That surface.
    pub contact_plane: Plane,
    /// Its cell.
    pub contact_plane_cell_id: u32,
    /// It is water.
    pub contact_plane_is_water: bool,
    /// The previous walkable polygon is known (for edge slides).
    pub walkable_polygon_valid: bool,
    /// Its plane.
    pub walkable_plane: Plane,
    /// Its vertices (world frame).
    pub walkable_vertices: Option<Vec<Vec3>>,
    /// The up direction it was found with.
    pub walkable_up: Vec3,
    /// Bounce: the fraction of speed into a surface given back.
    pub elasticity: f32,
    /// Friction.
    pub friction: f32,
    /// Persistent physics state bits.
    pub state: PhysicsState,
    /// Per-update contact state bits.
    pub transient_state: TransientState,
    /// Game time of the last update, seconds.
    pub last_update_time: f64,
    /// Held fully in place by the server's leash.
    pub is_fully_constrained: bool,
    /// The last velocity set came from the player's own input.
    pub last_move_was_autonomous: bool,
}

impl Default for PhysicsBody {
    fn default() -> Self {
        Self {
            position: Vec3::ZERO,
            cell_position: CellPosition::default(),
            in_world: false,
            orientation: Quat::IDENTITY,
            velocity: Vec3::ZERO,
            cached_velocity: Vec3::ZERO,
            frames_stationary_fall: 0,
            acceleration: Vec3::ZERO,
            omega: Vec3::ZERO,
            ground_normal: Vec3::Z,
            sliding_normal: Vec3::ZERO,
            contact_plane_valid: false,
            contact_plane: Plane::default(),
            contact_plane_cell_id: 0,
            contact_plane_is_water: false,
            walkable_polygon_valid: false,
            walkable_plane: Plane::default(),
            walkable_vertices: None,
            walkable_up: Vec3::Z,
            elasticity: 0.05,
            friction: PhysicsBody::DEFAULT_FRICTION,
            state: PhysicsState::GRAVITY | PhysicsState::REPORT_COLLISIONS,
            transient_state: TransientState::NONE,
            last_update_time: 0.0,
            is_fully_constrained: false,
            last_move_was_autonomous: false,
        }
    }
}

impl PhysicsBody {
    /// Fastest a body may move, metres per second.
    pub const MAX_VELOCITY: f32 = 50.0;
    /// [`Self::MAX_VELOCITY`] squared.
    pub const MAX_VELOCITY_SQUARED: f32 = Self::MAX_VELOCITY * Self::MAX_VELOCITY;
    /// Gravity, metres per second squared.
    pub const GRAVITY: f32 = -9.8;
    /// Below this speed a body stops, metres per second.
    pub const SMALL_VELOCITY: f32 = 0.25;
    /// [`Self::SMALL_VELOCITY`] squared.
    pub const SMALL_VELOCITY_SQUARED: f32 = Self::SMALL_VELOCITY * Self::SMALL_VELOCITY;
    /// Friction of ordinary ground.
    pub const DEFAULT_FRICTION: f32 = 0.95;
    /// Shortest step worth simulating, seconds.
    pub const MIN_QUANTUM: f32 = 1.0 / 30.0;
    /// Longest single step, seconds.
    pub const MAX_QUANTUM: f32 = 0.2;
    /// Updates later than this are dropped, seconds.
    pub const HUGE_QUANTUM: f32 = 2.0;

    /// A body with the default state (gravity, reports collisions).
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// World position.
    #[must_use]
    pub fn position(&self) -> Vec3 {
        self.position
    }

    /// Moves the body; the cell position follows by the same amount (an
    /// outdoor cell is re-seated on the cell under it).
    pub fn set_position(&mut self, value: Vec3) {
        let delta = value - self.position;
        self.position = value;
        self.sync_cell_position_delta(delta);
    }

    /// The cell position.
    #[must_use]
    pub fn cell_position(&self) -> CellPosition {
        self.cell_position
    }

    /// Places the body in a cell and the world.
    pub fn snap_to_cell(&mut self, cell_id: u32, world_pos: Vec3, cell_local: Vec3) {
        self.stage_dormant_cell_frame(cell_id, world_pos, cell_local);
        self.in_world = true;
    }

    /// Sets the body's cell frame without putting it in the world.
    pub fn stage_dormant_cell_frame(&mut self, cell_id: u32, world_pos: Vec3, cell_local: Vec3) {
        self.position = world_pos;
        let mut cell = cell_id;
        let mut local = cell_local;
        if is_outdoor(cell_id) {
            adjust(&mut cell, &mut local);
        }
        self.cell_position = CellPosition {
            obj_cell_id: cell,
            frame: Frame::new(local, self.orientation),
        };
    }

    /// Moves and turns the body within its current cell.
    pub fn set_frame_in_current_cell(&mut self, world_position: Vec3, orientation: Quat) {
        let delta = world_position - self.position;
        self.position = world_position;
        self.orientation = orientation;
        if self.cell_position.obj_cell_id != 0 {
            self.cell_position = CellPosition {
                obj_cell_id: self.cell_position.obj_cell_id,
                frame: Frame::new(self.cell_position.frame.origin + delta, orientation),
            };
        }
    }

    /// Commits a transition's result: the new position, and the cell it
    /// found.
    pub fn commit_transition_position(&mut self, resolved_cell_id: u32, world_position: Vec3) {
        self.set_position(world_position);
        if self.cell_position.obj_cell_id == 0 || resolved_cell_id == 0 {
            return;
        }
        let mut cell = resolved_cell_id;
        let mut local = self.cell_position.frame.origin;
        if is_outdoor(cell) && !adjust(&mut cell, &mut local) {
            return;
        }
        self.cell_position = CellPosition {
            obj_cell_id: cell,
            frame: Frame::new(local, self.cell_position.frame.orientation),
        };
    }

    fn sync_cell_position_delta(&mut self, delta: Vec3) {
        let cell = self.cell_position.obj_cell_id;
        if cell == 0 {
            return;
        }
        let mut local = self.cell_position.frame.origin + delta;
        let orientation = self.cell_position.frame.orientation;
        if !is_outdoor(cell) {
            self.cell_position = CellPosition {
                obj_cell_id: cell,
                frame: Frame::new(local, orientation),
            };
            return;
        }
        let mut adjusted = cell;
        if adjust(&mut adjusted, &mut local) {
            self.cell_position = CellPosition {
                obj_cell_id: adjusted,
                frame: Frame::new(local, orientation),
            };
        }
    }

    /// Stores the walkable polygon's vertices.
    pub fn set_walkable_vertices_exact(&mut self, source: &[Vec3]) {
        self.walkable_vertices = Some(source.to_vec());
    }

    /// Gravity acts on the body.
    #[must_use]
    pub fn has_gravity(&self) -> bool {
        self.state.contains(PhysicsState::GRAVITY)
    }

    /// Standing on walkable ground.
    #[must_use]
    pub fn on_walkable(&self) -> bool {
        self.transient_state.contains(TransientState::ON_WALKABLE)
    }

    /// Needs per-update simulation.
    #[must_use]
    pub fn is_active(&self) -> bool {
        self.transient_state.contains(TransientState::ACTIVE)
    }

    /// Touching a surface.
    #[must_use]
    pub fn in_contact(&self) -> bool {
        self.transient_state.contains(TransientState::CONTACT)
    }

    /// Touching water.
    #[must_use]
    pub fn is_water_contact(&self) -> bool {
        self.transient_state.contains(TransientState::WATER_CONTACT)
    }

    /// Sets the acceleration from the state: none while standing on walkable
    /// ground (unless sledding), else gravity when it applies.
    pub fn calc_acceleration(&mut self) {
        if self.transient_state.contains(TransientState::CONTACT)
            && self.transient_state.contains(TransientState::ON_WALKABLE)
            && !self.state.contains(PhysicsState::SLEDDING)
        {
            self.acceleration = Vec3::ZERO;
            self.omega = Vec3::ZERO;
            return;
        }
        self.acceleration = if self.state.contains(PhysicsState::GRAVITY) {
            Vec3::new(0.0, 0.0, Self::GRAVITY)
        } else {
            Vec3::ZERO
        };
    }

    /// Sets the velocity (capped at 50 m/s) and wakes the body.
    pub fn set_velocity(&mut self, new_velocity: Vec3) {
        self.velocity = new_velocity;
        if self.velocity.length_squared() > Self::MAX_VELOCITY_SQUARED {
            self.velocity = crate::geometry::normalize(self.velocity) * Self::MAX_VELOCITY;
        }
        self.transient_state.insert(TransientState::ACTIVE);
    }

    /// Sets the velocity from the body's own axes.
    pub fn set_local_velocity(&mut self, local_velocity: Vec3, autonomous: bool) {
        let world = self.orientation * local_velocity;
        self.last_move_was_autonomous = autonomous;
        self.set_velocity(world);
    }

    /// Sets or clears the on-walkable bit and recomputes acceleration.
    pub fn set_on_walkable(&mut self, is_on_walkable: bool) {
        self.transient_state
            .set(TransientState::ON_WALKABLE, is_on_walkable);
        self.calc_acceleration();
    }

    /// Ground friction over `dt` seconds: on walkable ground, unless moving
    /// away from it (0.25 m/s or more), removes the into-ground part of the
    /// velocity and decays it by `(1 − friction)^dt`. Sleds slide freely
    /// below 1.25 m/s and with little friction fast on near-level ground.
    pub fn calc_friction(&mut self, dt: f32, velocity_mag2: f32) {
        if !self.transient_state.contains(TransientState::ON_WALKABLE) {
            return;
        }
        let angle = self.velocity.dot(self.ground_normal);
        if angle >= 0.25 {
            return;
        }
        self.velocity -= angle * self.ground_normal;

        let mut friction = self.friction;
        if self.state.contains(PhysicsState::SLEDDING) {
            if velocity_mag2 < 1.5625 {
                friction = 1.0;
            } else if velocity_mag2 >= 6.25 && self.ground_normal.z > COS_10_DEGREES {
                friction = 0.2;
            }
        }
        let scalar = (1.0 - friction).powf(dt);
        self.velocity *= scalar;
    }

    /// Integrates one step of `dt` seconds.
    pub fn update_physics_internal(&mut self, dt: f32) {
        let mut velocity_mag2 = self.velocity.length_squared();
        if velocity_mag2 <= 0.0 {
            if self.transient_state.contains(TransientState::ON_WALKABLE) {
                self.transient_state.remove(TransientState::ACTIVE);
            }
        } else {
            if velocity_mag2 > Self::MAX_VELOCITY_SQUARED {
                self.velocity = crate::geometry::normalize(self.velocity) * Self::MAX_VELOCITY;
                velocity_mag2 = Self::MAX_VELOCITY_SQUARED;
            }
            self.calc_friction(dt, velocity_mag2);
            if velocity_mag2 - Self::SMALL_VELOCITY_SQUARED < 0.0002 {
                self.velocity = Vec3::ZERO;
            }
            let step = self.velocity * dt + self.acceleration * (0.5 * dt * dt);
            self.set_position(self.position + step);
        }

        self.velocity += self.acceleration * dt;

        let rotation = self.omega * dt;
        let rotation_len_sq = rotation.length_squared();
        if rotation_len_sq >= EPSILON_SQ {
            let angle = rotation_len_sq.sqrt();
            let delta = Quat::from_axis_angle(rotation / angle, angle);
            self.orientation = crate::geometry::normalize_quat(delta * self.orientation);
        }
    }

    /// Advances the body to `current_time` (seconds) in quanta of at most
    /// 0.2 s; a remainder shorter than 1/30 s waits for the next update.
    pub fn update_object(&mut self, current_time: f64) {
        let mut delta_time = current_time - self.last_update_time;
        if delta_time <= f64::from(EPSILON) {
            self.last_update_time = current_time;
            return;
        }
        if delta_time > f64::from(Self::HUGE_QUANTUM) {
            self.last_update_time = current_time;
            return;
        }
        let mut physics_time = self.last_update_time;
        while delta_time > f64::from(Self::MAX_QUANTUM) {
            physics_time += f64::from(Self::MAX_QUANTUM);
            self.calc_acceleration();
            self.update_physics_internal(Self::MAX_QUANTUM);
            delta_time -= f64::from(Self::MAX_QUANTUM);
        }
        if delta_time > f64::from(Self::MIN_QUANTUM) {
            physics_time += delta_time;
            self.calc_acceleration();
            self.update_physics_internal(delta_time as f32);
        }
        self.last_update_time = physics_time;
    }
}
