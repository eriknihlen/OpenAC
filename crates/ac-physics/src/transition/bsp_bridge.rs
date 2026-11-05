//! How a BSP tree's collision test reports back to the sweep.

use glam::Vec3;

use super::Transition;
use crate::bsp_collisions::{BspFrame, BspTransition, WalkState};
use crate::engine::PhysicsEngine;
use crate::geometry::Plane;
use crate::state::{InsertType, ObjectInfoState, TransitionState};

/// A sweep in progress, with the engine its step-ups recurse into.
pub(crate) struct BspContext<'a> {
    pub(crate) transition: &'a mut Transition,
    /// The engine step-ups run in. Without one (OpenAC's engine-less
    /// query, used by tests) a full foot hit slides instead.
    pub(crate) engine: Option<&'a PhysicsEngine>,
}

impl BspTransition for BspContext<'_> {
    fn insert_type(&self) -> InsertType {
        self.transition.sphere_path.insert_type
    }

    fn obstruction_ethereal(&self) -> bool {
        self.transition.sphere_path.obstruction_ethereal
    }

    fn bldg_check(&self) -> bool {
        self.transition.sphere_path.bldg_check
    }

    fn hits_interior_cell(&self) -> bool {
        self.transition.sphere_path.hits_interior_cell
    }

    fn check_walkable(&self) -> bool {
        self.transition.sphere_path.check_walkable
    }

    fn step_down(&self) -> bool {
        self.transition.sphere_path.step_down
    }

    fn step_down_amt(&self) -> f32 {
        self.transition.sphere_path.step_down_amt
    }

    fn step_up(&self) -> bool {
        self.transition.sphere_path.step_up
    }

    fn collide(&self) -> bool {
        self.transition.sphere_path.collide
    }

    fn object_state(&self) -> ObjectInfoState {
        self.transition.object_info.state
    }

    fn check_cell_id(&self) -> u32 {
        self.transition.sphere_path.check_cell_id
    }

    fn walk(&mut self) -> &mut WalkState {
        &mut self.transition.sphere_path.walk
    }

    fn add_offset_to_check_pos(&mut self, offset: Vec3) {
        self.transition.sphere_path.add_offset_to_check_pos(offset);
    }

    fn set_contact_plane(&mut self, plane: Plane, cell_id: u32, is_water: bool) {
        self.transition
            .collision_info
            .set_contact_plane(plane, cell_id, is_water);
    }

    fn set_walkable(&mut self, plane: Plane, local_vertices: &[Vec3], frame: &BspFrame, up: Vec3) {
        self.transition.sphere_path.set_walkable_transformed(
            plane,
            local_vertices,
            frame.local_to_world,
            frame.scale,
            frame.world_origin,
            up,
        );
    }

    fn set_collision_normal(&mut self, normal: Vec3) {
        self.transition.collision_info.set_collision_normal(normal);
    }

    fn set_collide(&mut self, normal: Vec3) {
        self.transition.sphere_path.set_collide(normal);
    }

    fn set_neg_poly_hit(&mut self, step_up: bool, normal: Vec3) {
        // Kept as found, as OpenAC does; retail stores it negated.
        let sp = &mut self.transition.sphere_path;
        sp.neg_poly_hit = true;
        sp.neg_step_up = step_up;
        sp.neg_collision_normal = normal;
    }

    fn step_sphere_up(&mut self, normal: Vec3) -> TransitionState {
        let Some(engine) = self.engine else {
            return self.slide_sphere(normal);
        };
        if self.transition.do_step_up(normal, engine) {
            return TransitionState::Ok;
        }
        self.transition.step_up_slide()
    }

    fn slide_sphere(&mut self, normal: Vec3) -> TransitionState {
        let curr = self.transition.sphere_path.global_curr_center[0].center;
        self.transition.slide_sphere(normal, curr, 0)
    }
}
