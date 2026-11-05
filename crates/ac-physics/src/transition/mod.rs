//! The movement sweep ("transition"): moving a body's spheres from where
//! they are toward where they want to go, one step at a time, colliding
//! with the cells, terrain, buildings and objects on the way, stepping up
//! and down and sliding as retail does.
//!
//! A step is at most one sphere radius long. Each step's offset is first
//! bent along the surface the body stands on and the wall it slides along
//! ([`Transition::adjust_offset`]), then the body is inserted at the new
//! spot ([`Transition::transitional_insert`]): every collision test of the
//! cells it reaches runs, and the step is accepted, adjusted and retried,
//! or refused ([`Transition::validate_transition`]).

pub(crate) mod bsp_bridge;
mod cells;
mod collision_info;
mod object_info;
mod objects;
mod sphere_path;

use glam::Vec3;

use crate::engine::PhysicsEngine;
use crate::geometry::Plane;
use crate::globals::{EPSILON, EPSILON_SQ, FLOOR_Z, LANDING_Z};
use crate::polygon::find_crossed_edge;
use crate::state::{InsertType, ObjectInfoState, TransitionState};

pub use collision_info::{CollisionInfo, ContactPlane};
pub use object_info::{HouseRestrictions, ObjectInfo, ObjectRestrictions, RestrictionObject};
pub use sphere_path::{CollisionSphere, SpherePath};

/// Which collision phase of a primary cell a hook observes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum TransitionCellCollisionPhase {
    /// The cell's own geometry or terrain.
    Environment,
    /// The building standing in the cell.
    Building,
    /// The objects listed in the cell.
    Objects,
}

/// The walkable allowance a placement's step down uses (cos 85°).
const PLACEMENT_WALKABLE_ALLOWANCE: f32 = 0.087_155_7;

/// The pi the placement search uses (retail `3.14159989f`; `3.1416` is the
/// same `f32`).
#[allow(clippy::approx_constant)]
const RETAIL_PI: f32 = 3.1416;

/// One movement sweep.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Transition {
    /// The moving object.
    pub object_info: ObjectInfo,
    /// Its spheres and positions.
    pub sphere_path: SpherePath,
    /// What it found.
    pub collision_info: CollisionInfo,
}

/// True when an object with these physics state bits collides with the
/// collision trees of its parts only (never its cylinders or spheres).
#[must_use]
pub fn bsp_only_dispatch(entity_state: u32) -> bool {
    entity_state & 0x1_0000 != 0
}

/// The compass offset of a placement search sample: `distance` metres
/// toward `heading_degrees` (0 north, 90 east).
#[must_use]
pub fn get_placement_compass_offset(heading_degrees: f32, distance: f32) -> Vec3 {
    const DEGREES_TO_RADIANS: f64 = 0.017_453_292_519_943_295;
    let radians = f64::from(heading_degrees) * DEGREES_TO_RADIANS;
    Vec3::new(
        radians.sin() as f32 * distance,
        radians.cos() as f32 * distance,
        0.0,
    )
}

/// How far a step down probes and how many times: the requested height,
/// but half a radius for a one-sphere body that the height would sink
/// through, and two half-height probes when the body is no taller than
/// the height.
#[must_use]
pub fn get_step_down_probe_plan(
    num_spheres: usize,
    sphere_radius: f32,
    requested_height: f32,
) -> (f32, i32) {
    let diameter = sphere_radius * 2.0;
    let mut probe_height = requested_height;
    if num_spheres < 2 && diameter <= probe_height {
        probe_height = sphere_radius * 0.5;
    }
    if diameter > probe_height {
        return (probe_height, 1);
    }
    (probe_height * 0.5, 2)
}

impl Transition {
    /// A fresh sweep.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Moves the body from the path's start toward its end. True when the
    /// sweep succeeded (the body is at `sphere_path.cur_pos`).
    pub fn find_transitional_position(&mut self, engine: &PhysicsEngine) -> bool {
        self.object_info.velocity_killed = false;

        if self.sphere_path.cur_cell_id == 0 {
            return false;
        }

        let offset = self.sphere_path.end_pos - self.sphere_path.begin_pos;
        let dist = offset.length();
        let radius = self.sphere_path.local_sphere[0].radius;

        if radius <= EPSILON {
            return false;
        }

        let num_steps: i32;
        let mut offset_per_step: Vec3;

        if self.object_info.is_viewer() {
            if dist > EPSILON {
                offset_per_step = offset * (radius / dist);
                num_steps = (dist / radius).floor() as i32 + 1;
            } else {
                num_steps = 0;
                offset_per_step = Vec3::ZERO;
            }
        } else {
            let step = dist / radius;
            if step > 1.0 {
                num_steps = step.ceil() as i32;
                offset_per_step = offset * (1.0 / num_steps as f32);
            } else if offset != Vec3::ZERO {
                num_steps = 1;
                offset_per_step = offset;
            } else {
                num_steps = 0;
                offset_per_step = Vec3::ZERO;
            }
        }

        if self.object_info.free_rotate() {
            self.sphere_path.cur_orientation = self.sphere_path.end_orientation;
        }

        let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
        self.sphere_path.set_check_pos(cur_pos, cur_cell);

        if num_steps <= 0 {
            if !self.object_info.free_rotate() {
                self.sphere_path.cur_orientation = self.sphere_path.end_orientation;
            }
            return true;
        }

        let mut transition_state = TransitionState::Ok;

        for i in 0..num_steps {
            if self.object_info.is_viewer() && i == num_steps - 1 && dist > EPSILON {
                offset_per_step = offset * ((dist - (num_steps - 1) as f32 * radius) / dist);
            }

            let requested_offset = offset_per_step;
            self.sphere_path.global_offset = self.adjust_offset(requested_offset);

            if !self.object_info.is_viewer()
                && self.sphere_path.global_offset.length_squared() < EPSILON_SQ
            {
                return i != 0 && transition_state == TransitionState::Ok;
            }

            if !self.object_info.free_rotate() {
                let delta = (i as f32 + 1.0) / num_steps as f32;
                self.sphere_path.check_orientation = self
                    .sphere_path
                    .begin_orientation
                    .slerp(self.sphere_path.end_orientation, delta);
            }

            self.collision_info.sliding_normal_valid = false;
            self.collision_info.clear_contact_plane();

            let global_offset = self.sphere_path.global_offset;
            self.sphere_path.add_offset_to_check_pos(global_offset);

            let result = self.transitional_insert(3, engine);

            transition_state = self.validate_transition(result);

            if self.collision_info.frames_stationary_fall != 0
                || (self.collision_info.collision_normal_valid && self.object_info.path_clipped())
            {
                break;
            }
        }

        transition_state == TransitionState::Ok
    }

    /// The sweep for the path's insert type: an ordinary move, or a
    /// placement.
    pub fn find_valid_position(&mut self, engine: &PhysicsEngine) -> bool {
        if self.sphere_path.insert_type == InsertType::Transition {
            self.find_transitional_position(engine)
        } else {
            self.find_placement_position(engine)
        }
    }

    /// Places the body at (or near) the path's current position: an initial
    /// placement against cells, terrain and buildings, then a placement
    /// search (sliding and ringing out to a free spot when allowed), then a
    /// step down onto the ground.
    pub fn find_placement_position(&mut self, engine: &PhysicsEngine) -> bool {
        let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
        self.sphere_path.set_check_pos(cur_pos, cur_cell);
        self.sphere_path.insert_type = InsertType::InitialPlacement;

        let initial_state = self.initial_placement_insert(engine);
        let initial = self.validate_placement(engine, initial_state, true);
        if initial != TransitionState::Ok {
            return false;
        }

        self.sphere_path.insert_type = InsertType::Placement;
        if !self.find_placement_pos(engine) {
            return false;
        }

        if self.object_info.step_down {
            let step_down_height = self.object_info.step_down_height;
            self.sphere_path.walk.walkable_allowance = PLACEMENT_WALKABLE_ALLOWANCE;
            self.sphere_path.save_check_pos();
            let saved_insert = self.sphere_path.insert_type;
            self.sphere_path.insert_type = InsertType::Transition;

            let (probe_height, probe_count) = get_step_down_probe_plan(
                self.sphere_path.num_sphere,
                self.sphere_path.global_sphere[0].radius,
                step_down_height,
            );
            let mut stepped = self.do_step_down(probe_height, PLACEMENT_WALKABLE_ALLOWANCE, engine);
            if !stepped && probe_count > 1 {
                stepped = self.do_step_down(probe_height, PLACEMENT_WALKABLE_ALLOWANCE, engine);
            }
            if !stepped {
                self.sphere_path.restore_check_pos();
                self.collision_info.clear_contact_plane();
            }
            self.sphere_path.insert_type = saved_insert;
            self.sphere_path.clear_walkable();
        }

        self.validate_placement(engine, TransitionState::Ok, true) == TransitionState::Ok
    }

    /// The placement search: the spot itself, then (when sliding is
    /// allowed) rings of compass samples out to 4 m (2 m for a body smaller
    /// than 0.125 m).
    pub fn find_placement_pos(&mut self, engine: &PhysicsEngine) -> bool {
        let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
        self.sphere_path.set_check_pos(cur_pos, cur_cell);
        self.collision_info.sliding_normal_valid = false;
        self.collision_info.clear_contact_plane();

        let inserted = self.transitional_insert(3, engine);
        let mut transition_state = self.validate_placement_transition(inserted);
        if transition_state == TransitionState::Ok {
            return true;
        }

        if !self.sphere_path.placement_allows_sliding {
            return false;
        }

        let adjust_distance = 4.0f32;
        let mut adjust_radius = adjust_distance;
        let mut sphere_radius = self.sphere_path.local_sphere[0].radius;
        let mut fake_sphere = false;

        if sphere_radius < 0.125 {
            fake_sphere = true;
            adjust_radius = 2.0;
        } else if sphere_radius < 0.48 {
            sphere_radius = 0.48;
        }

        let mut step_count_exact = 4.0f64 / f64::from(sphere_radius);
        if fake_sphere {
            step_count_exact *= 0.5;
        }
        if step_count_exact <= 1.0 {
            return false;
        }

        let step_count = step_count_exact.ceil() as i32;
        let distance_per_step = (f64::from(adjust_radius) / f64::from(step_count)) as f32;
        let radians_per_step = ((f64::from(distance_per_step) / f64::from(sphere_radius))
            * f64::from(RETAIL_PI)) as f32;
        let mut total_distance = 0.0f32;
        let mut total_radians = 0.0f32;

        for _ring in 0..step_count {
            total_distance += distance_per_step;
            total_radians += radians_per_step;

            let sample_count = (f64::from(total_radians).ceil() as i32) * 2;
            let heading_step = (360.0f64 / f64::from(sample_count)) as f32;

            for sample in 0..sample_count {
                let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
                self.sphere_path.set_check_pos(cur_pos, cur_cell);

                let heading = heading_step * sample as f32;
                let offset = get_placement_compass_offset(heading, total_distance);
                self.sphere_path.global_offset = self.adjust_offset(offset);

                if self.sphere_path.global_offset.length_squared() < EPSILON_SQ {
                    continue;
                }

                let global_offset = self.sphere_path.global_offset;
                self.sphere_path.add_offset_to_check_pos(global_offset);
                self.collision_info.sliding_normal_valid = false;
                self.collision_info.clear_contact_plane();

                let inserted = self.transitional_insert(3, engine);
                transition_state = self.validate_placement_transition(inserted);
                if transition_state == TransitionState::Ok {
                    return true;
                }
            }
        }

        false
    }

    /// Accepts an OK placement step (the body moves there); a refused one
    /// that may slide clears the findings.
    pub fn validate_placement_transition(
        &mut self,
        transition_state: TransitionState,
    ) -> TransitionState {
        if self.sphere_path.check_cell_id == 0 {
            return TransitionState::Collided;
        }
        if transition_state == TransitionState::Ok {
            self.accept_check_position();
        } else if matches!(
            transition_state,
            TransitionState::Collided | TransitionState::Adjusted | TransitionState::Slid
        ) && self.sphere_path.placement_allows_sliding
        {
            self.collision_info = CollisionInfo::default();
        }
        transition_state
    }

    fn accept_check_position(&mut self) {
        let sp = &mut self.sphere_path;
        sp.cur_pos = sp.check_pos;
        sp.cur_cell_id = sp.check_cell_id;
        sp.cur_orientation = sp.check_orientation;
        sp.refresh_curr_centers();
    }

    /// Accepts an OK placement; an adjusted or slid one gets one retry with
    /// a full placement insert.
    pub fn validate_placement(
        &mut self,
        engine: &PhysicsEngine,
        transition_state: TransitionState,
        retry_placement: bool,
    ) -> TransitionState {
        if self.sphere_path.check_cell_id == 0 {
            return TransitionState::Collided;
        }
        if transition_state == TransitionState::Ok {
            self.accept_check_position();
        } else if matches!(
            transition_state,
            TransitionState::Adjusted | TransitionState::Slid
        ) && retry_placement
        {
            let state = self.placement_insert(engine);
            return self.validate_placement(engine, state, false);
        }
        transition_state
    }

    /// Inserts the body at the check position, retrying up to
    /// `num_attempts` times as collisions adjust it.
    pub fn transitional_insert(
        &mut self,
        num_attempts: i32,
        engine: &PhysicsEngine,
    ) -> TransitionState {
        if self.sphere_path.check_cell_id == 0 {
            return TransitionState::Ok;
        }
        if num_attempts <= 0 {
            return TransitionState::Invalid;
        }

        let mut transit_state = TransitionState::Ok;

        let mut attempt = 0;
        while attempt < num_attempts {
            attempt += 1;
            let check_cell = self.sphere_path.check_cell_id;
            transit_state = self.insert_into_cell(engine, check_cell, num_attempts);

            if transit_state == TransitionState::Collided {
                self.sphere_path.neg_poly_hit = false;
                return TransitionState::Collided;
            }

            if transit_state == TransitionState::Slid {
                self.collision_info.clear_contact_plane();
                self.sphere_path.neg_poly_hit = false;
                continue;
            }

            if transit_state == TransitionState::Adjusted {
                // insert_into_cell used up its own retries; keep every
                // sphere field but the near miss and try again.
                self.sphere_path.neg_poly_hit = false;
                continue;
            }

            if transit_state != TransitionState::Ok {
                continue;
            }

            let other_state = self.run_check_other_cells_and_advance(engine);

            if other_state != TransitionState::Ok {
                self.sphere_path.neg_poly_hit = false;
            }
            if other_state == TransitionState::Collided {
                return TransitionState::Collided;
            }
            if other_state != TransitionState::Ok {
                transit_state = other_state;
                continue;
            }

            if self.sphere_path.collide {
                self.sphere_path.collide = false;

                // Land where the collision put the body when there is
                // walkable ground there and it can be placed; otherwise go
                // back to where the collision was flagged.
                if self.collision_info.contact_plane_valid
                    && self.do_check_walkable(LANDING_Z, engine)
                {
                    let saved_insert = self.sphere_path.insert_type;
                    self.sphere_path.insert_type = InsertType::Placement;
                    let place_state = self.transitional_insert(num_attempts, engine);
                    self.sphere_path.insert_type = saved_insert;

                    if place_state == TransitionState::Ok {
                        self.sphere_path.clear_walkable();
                        return place_state;
                    }
                }

                self.sphere_path.clear_walkable();

                {
                    self.sphere_path.restore_check_pos();
                    self.collision_info.clear_contact_plane();

                    if self.collision_info.last_known_contact_plane_valid {
                        self.collision_info.last_known_contact_plane_valid = false;
                        self.object_info.stop_velocity();
                    } else {
                        let normal = self.sphere_path.step_up_normal;
                        self.collision_info.set_collision_normal(normal);
                    }

                    return TransitionState::Collided;
                }
            }

            if self.sphere_path.neg_poly_hit
                && !self.sphere_path.step_down
                && !self.sphere_path.step_up
            {
                self.sphere_path.neg_poly_hit = false;

                if self.sphere_path.neg_step_up {
                    let normal = self.sphere_path.neg_collision_normal;
                    if !self.do_step_up(normal, engine) {
                        let step_up_slide = self.step_up_slide();
                        if step_up_slide == TransitionState::Slid {
                            transit_state = step_up_slide;
                            self.collision_info.clear_contact_plane();
                            continue;
                        }
                        if step_up_slide != TransitionState::Ok {
                            return step_up_slide;
                        }
                    }
                } else {
                    let normal = self.sphere_path.neg_collision_normal;
                    let curr = self.sphere_path.global_curr_center[0].center;
                    let slide = self.slide_sphere(normal, curr, 0);
                    if slide == TransitionState::Collided {
                        return TransitionState::Collided;
                    }
                    transit_state = slide;
                    continue;
                }
            }

            if self.collision_info.contact_plane_valid {
                return TransitionState::Ok;
            }

            if self.object_info.contact()
                && !self.sphere_path.step_down
                && self.sphere_path.check_cell_id != 0
                && self.object_info.step_down
            {
                let z_val = self.object_info.get_walkable_z();
                let step_down_height = self.object_info.step_down_height;
                self.sphere_path.walk.walkable_allowance = z_val;
                self.sphere_path.save_check_pos();

                let (probe_height, probe_count) = get_step_down_probe_plan(
                    self.sphere_path.num_sphere,
                    self.sphere_path.global_sphere[0].radius,
                    step_down_height,
                );
                let step_down_height = probe_height;

                let mut stepped_down = false;
                for _ in 0..probe_count {
                    if self.do_step_down(step_down_height, z_val, engine) {
                        stepped_down = true;
                        break;
                    }
                }

                if stepped_down {
                    self.sphere_path.clear_walkable();
                    return TransitionState::Ok;
                }

                let (stop, edge_state) =
                    self.edge_slide_after_step_down_failed(engine, step_down_height, z_val);
                if stop {
                    return edge_state;
                }

                if edge_state == TransitionState::Slid {
                    transit_state = edge_state;
                    self.collision_info.clear_contact_plane();
                    self.sphere_path.neg_poly_hit = false;
                    continue;
                }

                if edge_state == TransitionState::Adjusted {
                    transit_state = edge_state;
                    self.sphere_path.neg_poly_hit = false;
                    continue;
                }

                transit_state = edge_state;
                continue;
            }

            return TransitionState::Ok;
        }

        transit_state
    }

    fn insert_into_cell(
        &mut self,
        engine: &PhysicsEngine,
        cell_id: u32,
        num_attempts: i32,
    ) -> TransitionState {
        if cell_id == 0 {
            return TransitionState::Collided;
        }
        let mut state = TransitionState::Ok;
        for _ in 0..num_attempts {
            state = self.find_primary_cell_collisions(engine, cell_id);
            if matches!(state, TransitionState::Ok | TransitionState::Collided) {
                return state;
            }
            if state == TransitionState::Slid {
                self.collision_info.clear_contact_plane();
            }
        }
        state
    }

    fn initial_placement_insert(&mut self, engine: &PhysicsEngine) -> TransitionState {
        if self.sphere_path.check_cell_id == 0 {
            return TransitionState::Collided;
        }
        let cell = self.sphere_path.check_cell_id;
        let mut result = self.insert_into_cell(engine, cell, 3);
        if result == TransitionState::Ok {
            result = self.run_check_other_cells_and_advance(engine);
        }
        result
    }

    fn placement_insert(&mut self, engine: &PhysicsEngine) -> TransitionState {
        if self.sphere_path.check_cell_id == 0 {
            return TransitionState::Collided;
        }
        let cell = self.sphere_path.check_cell_id;
        let result = self.insert_into_cell(engine, cell, 3);
        if result == TransitionState::Ok {
            self.run_check_other_cells_and_advance(engine)
        } else {
            result
        }
    }

    fn observe_phase(
        &mut self,
        engine: &PhysicsEngine,
        phase: TransitionCellCollisionPhase,
        cell_id: u32,
        actual: TransitionState,
    ) -> TransitionState {
        match &engine.cell_collision_hook {
            Some(hook) => hook(self, phase, cell_id, actual),
            None => actual,
        }
    }

    fn find_primary_cell_collisions(
        &mut self,
        engine: &PhysicsEngine,
        cell_id: u32,
    ) -> TransitionState {
        let env = self.find_env_collisions(engine, cell_id);
        let environment = self.observe_phase(
            engine,
            TransitionCellCollisionPhase::Environment,
            cell_id,
            env,
        );
        if environment != TransitionState::Ok {
            return environment;
        }
        let bldg = self.find_building_collisions(engine, cell_id);
        let building = self.observe_phase(
            engine,
            TransitionCellCollisionPhase::Building,
            cell_id,
            bldg,
        );
        if building != TransitionState::Ok {
            return building;
        }
        let objs = self.find_obj_collisions_in_cell(engine, cell_id);
        self.observe_phase(engine, TransitionCellCollisionPhase::Objects, cell_id, objs)
    }

    /// After a failed step down from walkable ground: edge sliders slide
    /// along the edge (a cliff, the walkable polygon's edge, or a probe
    /// back from the current position); others stay put. Returns whether
    /// to stop with the state, else the state to retry with.
    pub(crate) fn edge_slide_after_step_down_failed(
        &mut self,
        engine: &PhysicsEngine,
        step_down_height: f32,
        z_val: f32,
    ) -> (bool, TransitionState) {
        if !self.object_info.on_walkable() || !self.object_info.edge_slide() {
            self.sphere_path.clear_walkable();
            self.sphere_path.restore_check_pos();
            self.collision_info.clear_contact_plane();
            return (true, TransitionState::Ok);
        }

        if self.collision_info.contact_plane_valid
            && self.collision_info.contact_plane.normal.z < z_val
        {
            let cliff_plane = self.collision_info.contact_plane;
            self.sphere_path.clear_walkable();
            self.sphere_path.restore_check_pos();
            self.collision_info.clear_contact_plane();
            return (false, self.cliff_slide(cliff_plane));
        }

        if self.sphere_path.has_walkable_polygon() {
            self.sphere_path.restore_check_pos();
            self.collision_info.clear_contact_plane();
            let result = self.precipice_slide();
            return (result == TransitionState::Collided, result);
        }

        if self.collision_info.contact_plane_valid {
            self.sphere_path.clear_walkable();
            self.sphere_path.restore_check_pos();
            self.collision_info.clear_contact_plane();
            return (true, TransitionState::Ok);
        }

        let back_to_current = self.sphere_path.global_curr_center[0].center
            - self.sphere_path.global_sphere[0].center;
        self.sphere_path.add_offset_to_check_pos(back_to_current);

        let _ = self.do_step_down(step_down_height, z_val, engine);

        self.collision_info.clear_contact_plane();
        self.sphere_path.restore_check_pos();

        if self.sphere_path.has_walkable_polygon() {
            let result = self.precipice_slide();
            return (result == TransitionState::Collided, result);
        }

        self.sphere_path.clear_walkable();
        (true, TransitionState::Collided)
    }

    /// Slides along a cliff: along the crease of the too-steep plane and the
    /// last walkable plane, back to the near side.
    pub(crate) fn cliff_slide(&mut self, contact_plane: Plane) -> TransitionState {
        let reference_normal = self.collision_info.last_known_contact_plane.normal;
        let mut contact_normal = contact_plane.normal.cross(reference_normal);
        contact_normal.z = 0.0;

        let mut collide_normal = Vec3::new(-contact_normal.y, contact_normal.x, 0.0);
        if collide_normal.length_squared() < EPSILON_SQ {
            return TransitionState::Ok;
        }
        collide_normal = crate::geometry::normalize(collide_normal);

        let offset = self.sphere_path.global_sphere[0].center
            - self.sphere_path.global_curr_center[0].center;
        let angle = collide_normal.dot(offset);

        if angle <= 0.0 {
            self.sphere_path
                .add_offset_to_check_pos(collide_normal * angle);
            self.collision_info.set_collision_normal(collide_normal);
        } else {
            self.sphere_path
                .add_offset_to_check_pos(collide_normal * -angle);
            self.collision_info.set_collision_normal(-collide_normal);
        }
        TransitionState::Adjusted
    }

    /// Slides along the step-up normal after a failed step up.
    pub(crate) fn step_up_slide(&mut self) -> TransitionState {
        self.collision_info.clear_contact_plane();
        let normal = self.sphere_path.step_up_normal;
        let curr = self.sphere_path.global_curr_center[0].center;
        self.slide_sphere(normal, curr, 0)
    }

    /// Slides along the edge of the walkable polygon the body is leaving.
    pub(crate) fn precipice_slide(&mut self) -> TransitionState {
        let sp = &mut self.sphere_path;
        if !sp.has_walkable_polygon() {
            sp.clear_walkable();
            return TransitionState::Collided;
        }
        let crossed = sp.walkable_vertices.as_ref().and_then(|verts| {
            find_crossed_edge(
                &sp.walkable_plane,
                verts,
                sp.global_sphere[0].center,
                sp.walkable_up,
            )
        });
        let Some(mut collision_normal) = crossed else {
            sp.clear_walkable();
            return TransitionState::Collided;
        };

        sp.clear_walkable();
        sp.step_up = false;

        let offset = sp.global_sphere[0].center - sp.global_curr_center[0].center;
        if collision_normal.dot(offset) > 0.0 {
            collision_normal = -collision_normal;
        }
        let curr = sp.global_curr_center[0].center;
        self.slide_sphere(collision_normal, curr, 0)
    }

    /// Slides sphere `sphere_num` along a wall with `collision_normal`: along
    /// the crease of the wall and the ground (contact plane, else the last
    /// known one), or straight off the wall when no ground is known. A
    /// degenerate normal halves the step back toward `curr_pos`; sliding
    /// into an opposing pair of surfaces is a collision.
    pub(crate) fn slide_sphere(
        &mut self,
        collision_normal: Vec3,
        curr_pos: Vec3,
        sphere_num: usize,
    ) -> TransitionState {
        if collision_normal.length_squared() < EPSILON_SQ {
            let half_offset = (curr_pos - self.sphere_path.global_sphere[sphere_num].center) * 0.5;
            self.sphere_path.add_offset_to_check_pos(half_offset);
            return TransitionState::Adjusted;
        }

        self.collision_info.set_collision_normal(collision_normal);

        let g_delta = self.sphere_path.global_sphere[sphere_num].center - curr_pos;

        let contact_plane = if self.collision_info.contact_plane_valid {
            self.collision_info.contact_plane
        } else if self.collision_info.last_known_contact_plane_valid {
            self.collision_info.last_known_contact_plane
        } else {
            let diff = collision_normal.dot(g_delta);
            let offset = -collision_normal * diff;
            self.sphere_path.add_offset_to_check_pos(offset);
            return TransitionState::Slid;
        };

        let direction = collision_normal.cross(contact_plane.normal);
        let dir_len_sq = direction.length_squared();

        if dir_len_sq >= EPSILON {
            let diff = direction.dot(g_delta);
            let inv_dir_len_sq = 1.0 / dir_len_sq;
            let mut offset = direction * diff * inv_dir_len_sq;
            if offset.length_squared() < EPSILON {
                return TransitionState::Collided;
            }
            offset -= g_delta;
            self.sphere_path.add_offset_to_check_pos(offset);
            return TransitionState::Slid;
        }

        if collision_normal.dot(contact_plane.normal) >= 0.0 {
            let diff = collision_normal.dot(g_delta);
            let offset = -collision_normal * diff;
            self.sphere_path.add_offset_to_check_pos(offset);
            return TransitionState::Slid;
        }

        let reversed = -g_delta;
        if reversed.length_squared() > EPSILON_SQ {
            self.collision_info
                .set_collision_normal(crate::geometry::normalize(reversed));
        }
        TransitionState::Collided
    }

    /// Bends a step's offset: along the sliding wall when moving into it,
    /// along the ground (into the plane: projected; away from it: kept on
    /// it), then pushes a sphere sunk into the ground back up to tangent.
    pub fn adjust_offset(&mut self, offset: Vec3) -> Vec3 {
        let ci = &mut self.collision_info;
        let sp = &mut self.sphere_path;

        let mut result = offset;
        let mut check_slide = false;

        let sliding_angle = result.dot(ci.sliding_normal);
        if ci.sliding_normal_valid {
            if sliding_angle < 0.0 {
                check_slide = true;
            } else {
                ci.sliding_normal_valid = false;
            }
        }

        if !ci.contact_plane_valid {
            if check_slide {
                result -= ci.sliding_normal * sliding_angle;
            }
            return result;
        }

        let collision_angle = result.dot(ci.contact_plane.normal);
        let mut slide_offset = ci.contact_plane.normal.cross(ci.sliding_normal);

        if check_slide {
            let slide_len = slide_offset.length();
            if slide_len < EPSILON {
                result = Vec3::ZERO;
            } else {
                slide_offset /= slide_len;
                result = slide_offset.dot(result) * slide_offset;
            }
        } else if collision_angle <= 0.0 {
            result -= ci.contact_plane.normal * collision_angle;
        } else {
            let n = ci.contact_plane.normal;
            if n.z.abs() > EPSILON {
                result.z = -(result.x * n.x + result.y * n.y) / n.z;
            }
        }

        if ci.contact_plane_cell_id != 0 && !ci.contact_plane_is_water {
            let glob_center = sp.global_sphere[0].center;
            let radius = sp.global_sphere[0].radius;
            let dist = glob_center.dot(ci.contact_plane.normal) + ci.contact_plane.d;
            if dist < radius - EPSILON {
                // Sunk below tangent: push up along +z back to tangent.
                let z_dist = (radius - dist) / ci.contact_plane.normal.z;
                if radius > z_dist.abs() {
                    sp.add_offset_to_check_pos(Vec3::new(0.0, 0.0, z_dist));
                }
            }
        }

        result
    }

    /// Lowers the body by `step_down_height` onto walkable ground (normal at
    /// least `walkable_z` up); true when it found ground it can be placed
    /// on.
    pub(crate) fn do_step_down(
        &mut self,
        step_down_height: f32,
        walkable_z: f32,
        engine: &PhysicsEngine,
    ) -> bool {
        let sp = &mut self.sphere_path;
        sp.neg_poly_hit = false;
        sp.step_down = true;
        sp.step_down_amt = step_down_height;
        sp.walk.walk_interp = 1.0;

        if !sp.step_up {
            sp.add_offset_to_check_pos(Vec3::new(0.0, 0.0, -step_down_height));
        }

        let transit_state = self.transitional_insert(5, engine);

        self.sphere_path.step_down = false;

        if transit_state == TransitionState::Ok
            && self.collision_info.contact_plane_valid
            && self.collision_info.contact_plane.normal.z >= walkable_z
        {
            if self.object_info.edge_slide()
                && !self.sphere_path.step_up
                && !self.do_check_walkable(walkable_z, engine)
            {
                return false;
            }

            let saved_insert = self.sphere_path.insert_type;
            self.sphere_path.insert_type = InsertType::Placement;
            let place_state = self.transitional_insert(1, engine);
            self.sphere_path.insert_type = saved_insert;

            return place_state == TransitionState::Ok;
        }

        false
    }

    /// Steps the body up over what it hit (by stepping it down from above,
    /// up to its step-up height); restores it when that fails.
    pub fn do_step_up(&mut self, collision_normal: Vec3, engine: &PhysicsEngine) -> bool {
        let saved_cp = self.collision_info.contact();

        self.collision_info.clear_contact_plane();

        self.sphere_path.step_up = true;
        self.sphere_path.step_up_normal = collision_normal;

        let mut step_down_height = 0.04f32;
        let mut z_landing_value = LANDING_Z;

        if self
            .object_info
            .state
            .contains(ObjectInfoState::ON_WALKABLE)
        {
            z_landing_value = self.object_info.get_walkable_z();
            step_down_height = self.object_info.step_up_height;
        }

        self.sphere_path.walk.walkable_allowance = z_landing_value;
        self.sphere_path.save_check_pos();

        let step_down = self.do_step_down(step_down_height, z_landing_value, engine);

        self.sphere_path.step_up = false;
        self.sphere_path.clear_walkable();

        if !step_down {
            self.sphere_path.restore_check_pos();
            if saved_cp.valid {
                let ci = &mut self.collision_info;
                ci.contact_plane = saved_cp.plane;
                ci.contact_plane_valid = true;
                ci.contact_plane_cell_id = saved_cp.cell_id;
                ci.contact_plane_is_water = saved_cp.is_water;
            }
        }

        step_down
    }

    /// Whether the body walking on walkable ground still has ground under
    /// it (`z_check` up at least) within its step-down height; true when not
    /// on walkable ground or the walkable polygon still supports it.
    pub(crate) fn do_check_walkable(&mut self, z_check: f32, engine: &PhysicsEngine) -> bool {
        if !self
            .object_info
            .state
            .contains(ObjectInfoState::ON_WALKABLE)
        {
            return true;
        }
        if self.sphere_path.check_walkables() {
            return true;
        }

        let sp = &mut self.sphere_path;
        let saved_check_pos = sp.check_pos;
        let saved_check_cell_id = sp.check_cell_id;
        let saved_backup_check_pos = sp.backup_check_pos;
        let saved_backup_check_cell_id = sp.backup_check_cell_id;

        let mut step_height = self.object_info.step_down_height;
        let glob_sphere = sp.global_sphere[0];

        if sp.num_sphere < 2 && step_height > glob_sphere.radius * 2.0 {
            step_height = glob_sphere.radius * 0.5;
        }
        if step_height > glob_sphere.radius * 2.0 {
            step_height *= 0.5;
        }

        sp.walk.walkable_allowance = z_check;
        sp.check_walkable = true;
        sp.add_offset_to_check_pos(Vec3::new(0.0, 0.0, -step_height));

        let transit_state = self.transitional_insert(1, engine);

        let sp = &mut self.sphere_path;
        sp.check_walkable = false;
        sp.set_check_pos(saved_check_pos, saved_check_cell_id);
        sp.backup_check_pos = saved_backup_check_pos;
        sp.backup_check_cell_id = saved_backup_check_cell_id;

        transit_state != TransitionState::Ok
    }

    /// Settles one step: an OK step that moved is accepted; a refused one is
    /// undone (the last known ground is kept when the body still touches it,
    /// and the velocity is killed) and counts as OK. Then the findings
    /// become the body's contact state, and a body hanging in the air
    /// without moving counts stationary frames.
    pub fn validate_transition(
        &mut self,
        mut transition_state: TransitionState,
    ) -> TransitionState {
        let clean_advance = transition_state == TransitionState::Ok
            && self.sphere_path.check_pos != self.sphere_path.cur_pos;

        if clean_advance {
            self.accept_check_position();
            let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
            self.sphere_path.set_check_pos(cur_pos, cur_cell);
        } else if transition_state == TransitionState::Ok {
            let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
            self.sphere_path.set_check_pos(cur_pos, cur_cell);
        } else if transition_state != TransitionState::Invalid {
            let ci = &mut self.collision_info;
            if ci.last_known_contact_plane_valid {
                self.object_info.stop_velocity();
                let sphere_center = self.sphere_path.global_curr_center[0].center;
                let radius = self.sphere_path.global_sphere[0].radius;
                let angle = ci.last_known_contact_plane.normal.dot(sphere_center)
                    + ci.last_known_contact_plane.d;
                if radius + EPSILON > angle.abs() {
                    ci.set_contact_plane(
                        ci.last_known_contact_plane,
                        ci.last_known_contact_plane_cell_id,
                        ci.last_known_contact_plane_is_water,
                    );
                }
            }
            if !ci.collision_normal_valid {
                ci.set_collision_normal(Vec3::Z);
            }
            let (cur_pos, cur_cell) = (self.sphere_path.cur_pos, self.sphere_path.cur_cell_id);
            self.sphere_path.set_check_pos(cur_pos, cur_cell);
            transition_state = TransitionState::Ok;
        }

        let ci = &mut self.collision_info;
        let oi = &mut self.object_info;
        if ci.collision_normal_valid {
            let normal = ci.collision_normal;
            ci.set_sliding_normal(normal);
        }

        if ci.contact_plane_valid {
            ci.last_known_contact_plane_valid = true;
            ci.last_known_contact_plane = ci.contact_plane;
            ci.last_known_contact_plane_cell_id = ci.contact_plane_cell_id;
            ci.last_known_contact_plane_is_water = ci.contact_plane_is_water;

            oi.state.insert(ObjectInfoState::CONTACT);
            if ci.contact_plane.normal.z >= FLOOR_Z {
                oi.state.insert(ObjectInfoState::ON_WALKABLE);
            } else {
                oi.state.remove(ObjectInfoState::ON_WALKABLE);
            }
        } else {
            ci.last_known_contact_plane_valid = false;
            oi.state
                .remove(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
        }

        if !oi.is_viewer() && oi.mover_has_gravity {
            let redo = clean_advance || oi.on_walkable();
            if redo {
                ci.frames_stationary_fall = 0;
            } else {
                let fsf = ci.frames_stationary_fall;
                if fsf > 0 {
                    if fsf > 1 {
                        ci.frames_stationary_fall = 3;
                        let up = Vec3::Z;
                        let sphere = self.sphere_path.global_sphere[0];
                        let d = sphere.radius - sphere.center.z;
                        ci.set_contact_plane(
                            Plane::new(up, d),
                            self.sphere_path.check_cell_id,
                            false,
                        );
                        if !oi.contact() {
                            ci.set_collision_normal(up);
                            ci.collided_with_environment = true;
                        }
                        oi.state
                            .insert(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE);
                        ci.last_known_contact_plane_valid = true;
                        ci.last_known_contact_plane = ci.contact_plane;
                        ci.last_known_contact_plane_cell_id = ci.contact_plane_cell_id;
                        ci.last_known_contact_plane_is_water = ci.contact_plane_is_water;
                    } else {
                        ci.frames_stationary_fall = 2;
                    }
                } else {
                    ci.frames_stationary_fall = 1;
                }
            }
        }

        transition_state
    }
}
