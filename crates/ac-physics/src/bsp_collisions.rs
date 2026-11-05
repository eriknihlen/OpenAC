//! How a BSP tree answers one step of a moving sphere.
//!
//! The movement sweep (the "transition") moves a body in small steps. At
//! each step it asks every nearby tree whether the body's spheres collide,
//! in the tree's own frame. The tree answers with a [`TransitionState`] and
//! reports what it found back to the sweep through [`BspTransition`]: a
//! surface to stand on, a collision normal, a push-out offset, a "near
//! miss". Which question is asked depends on the sweep's state:
//!
//! 1. placement (or passing through an ethereal obstruction): any overlap
//!    with solid matter is a collision;
//! 2. "check walkable": is there ground under the sphere;
//! 3. stepping down: lower the sphere onto the ground;
//! 4. after a collision was flagged: settle onto walkable ground;
//! 5. standing on something: step up over, or slide along, what is hit;
//! 6. in the air: flag the collision, or (clipped movers) stop at it.

use glam::{Quat, Vec3};

use crate::bsp::PhysicsBsp;
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON_SQ, LANDING_Z};
use crate::polygon::{ResolvedPolygon, adjust_sphere_to_poly, adjust_to_placement_poly};
use crate::state::{InsertType, ObjectInfoState, TransitionState};

/// The part of the sweep's state the walkable searches read and adjust.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WalkState {
    /// Fraction of the current step still unused (1 at the start of a
    /// step); shrinks as the sphere is moved back onto surfaces.
    pub walk_interp: f32,
    /// Smallest up-component of a normal that counts as walkable here.
    pub walkable_allowance: f32,
}

impl Default for WalkState {
    fn default() -> Self {
        Self {
            walk_interp: 1.0,
            walkable_allowance: crate::globals::FLOOR_Z,
        }
    }
}

/// Where a tree sits in the world: its rotation, scale and origin. Offsets
/// and normals found in the tree's frame are carried out with it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct BspFrame {
    /// Rotation from the tree's frame to the world.
    pub local_to_world: Quat,
    /// Scale of the tree (the query spheres are already divided by it).
    pub scale: f32,
    /// World position of the tree's origin.
    pub world_origin: Vec3,
}

impl Default for BspFrame {
    fn default() -> Self {
        Self {
            local_to_world: Quat::IDENTITY,
            scale: 1.0,
            world_origin: Vec3::ZERO,
        }
    }
}

impl BspFrame {
    fn rotate(&self, v: Vec3) -> Vec3 {
        self.local_to_world * v
    }

    /// A normal carried to the world, renormalised (straight up when it
    /// degenerates).
    fn normal_to_world(&self, normal: Vec3) -> Vec3 {
        let n = self.local_to_world * normal;
        if n.length_squared() > EPSILON_SQ {
            crate::geometry::normalize(n)
        } else {
            Vec3::Z
        }
    }

    /// The world plane with `world_normal` through the polygon's first
    /// vertex.
    fn world_plane(&self, world_normal: Vec3, local_vertices: &[Vec3]) -> Plane {
        let d = match local_vertices.first() {
            Some(v0) => {
                -world_normal.dot(self.local_to_world * (*v0 * self.scale) + self.world_origin)
            }
            None => 0.0,
        };
        Plane::new(world_normal, d)
    }
}

/// The moving body's spheres in the tree's frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LocalSpheres {
    /// The body (foot) sphere where the step would put it.
    pub sphere0: Sphere,
    /// The head sphere, when the body has two.
    pub sphere1: Option<Sphere>,
    /// The body sphere's centre where the step starts.
    pub curr_center: Vec3,
    /// "Up" in the tree's frame.
    pub space_z: Vec3,
}

/// What a BSP collision query reads from, and reports to, the sweep in
/// progress. The sweep implements it; the queries never keep it.
pub trait BspTransition {
    /// Why the body is being inserted.
    fn insert_type(&self) -> InsertType;
    /// Passing through an ethereal obstruction (tested like placement).
    fn obstruction_ethereal(&self) -> bool;
    /// The sweep is testing against a building's shell.
    fn bldg_check(&self) -> bool;
    /// The body also overlaps a building's interior cell.
    fn hits_interior_cell(&self) -> bool;
    /// The sweep only asks whether there is ground under the sphere.
    fn check_walkable(&self) -> bool;
    /// The sweep is stepping the body down.
    fn step_down(&self) -> bool;
    /// How far the step-down may lower the body, metres.
    fn step_down_amt(&self) -> f32;
    /// The sweep is stepping the body up.
    fn step_up(&self) -> bool;
    /// A collision was flagged earlier in this step.
    fn collide(&self) -> bool;
    /// The moving body's state bits.
    fn object_state(&self) -> ObjectInfoState;
    /// The cell the step is being checked in.
    fn check_cell_id(&self) -> u32;
    /// The walkable search state.
    fn walk(&mut self) -> &mut WalkState;

    /// Moves the step's end position (and the body's spheres) by `offset`
    /// (world frame).
    fn add_offset_to_check_pos(&mut self, offset: Vec3);
    /// Records the surface the body stands on.
    fn set_contact_plane(&mut self, plane: Plane, cell_id: u32, is_water: bool);
    /// Records the walkable polygon the body stands on: `plane` in the
    /// world, the polygon's vertices in the tree's frame (carried out with
    /// `frame`), and `up`.
    fn set_walkable(&mut self, plane: Plane, local_vertices: &[Vec3], frame: &BspFrame, up: Vec3);
    /// Records the normal of what was hit (world frame).
    fn set_collision_normal(&mut self, normal: Vec3);
    /// Flags a collision with `normal` for the sweep to resolve (remember
    /// the position, reset the walk fraction).
    fn set_collide(&mut self, normal: Vec3);
    /// Records a near miss: a polygon the body touches but moves along or
    /// away from. `step_up` tells the sweep to try stepping up (foot sphere)
    /// rather than sliding (head sphere); `normal` is the polygon's normal
    /// in the world as found, not negated. (Whether the sweep keeps it as
    /// given or negated is an open question.)
    fn set_neg_poly_hit(&mut self, step_up: bool, normal: Vec3);
    /// Tries to step the body up over what it hit (world normal); slides
    /// along it when that fails.
    fn step_sphere_up(&mut self, normal: Vec3) -> TransitionState;
    /// Slides the body along a surface with this world normal.
    fn slide_sphere(&mut self, normal: Vec3) -> TransitionState;
}

impl PhysicsBsp {
    /// One collision test of a moving body against this tree. See the
    /// module documentation for the six questions it can answer; an empty
    /// tree answers [`TransitionState::Ok`].
    pub fn find_collisions<T: BspTransition + ?Sized>(
        &self,
        t: &mut T,
        spheres: &LocalSpheres,
        frame: &BspFrame,
    ) -> TransitionState {
        if self.is_empty() {
            return TransitionState::Ok;
        }
        let sphere0 = spheres.sphere0;
        let up = spheres.space_z;
        let movement = sphere0.center - spheres.curr_center;

        // 1. Placement: any solid overlap is a collision.
        if t.insert_type() == InsertType::Placement || t.obstruction_ethereal() {
            let clear_cell = !(t.bldg_check() && t.hits_interior_cell());
            if self.sphere_intersects_solid(&sphere0, clear_cell) {
                return TransitionState::Collided;
            }
            if let Some(sphere1) = spheres.sphere1
                && self.sphere_intersects_solid(&sphere1, clear_cell)
            {
                return TransitionState::Collided;
            }
            return TransitionState::Ok;
        }

        // 2. Is there ground under the sphere?
        if t.check_walkable() {
            return if self.hits_walkable_internal(t.walk(), &sphere0, up) {
                TransitionState::Collided
            } else {
                TransitionState::Ok
            };
        }

        // 3. Step down onto the ground.
        if t.step_down() {
            return self.step_sphere_down(t, &sphere0, up, frame);
        }

        // 4. A collision was flagged: settle onto walkable ground.
        if t.collide() {
            let mut valid_pos = sphere0;
            let mut hit_poly = None;
            let mut changed = false;
            self.find_walkable_internal(
                t.walk(),
                &mut valid_pos,
                movement,
                up,
                &mut hit_poly,
                &mut changed,
            );
            if let (true, Some(poly)) = (changed, hit_poly) {
                self.settle_on(t, poly, valid_pos.center - sphere0.center, frame);
                return TransitionState::Adjusted;
            }
            return TransitionState::Ok;
        }

        // 5. Standing on something.
        if t.object_state().contains(ObjectInfoState::CONTACT) {
            let mut hit_poly0 = None;
            let mut contact0 = Vec3::ZERO;
            let hit0 = self.sphere_intersects_poly_internal(
                &sphere0,
                movement,
                &mut hit_poly0,
                &mut contact0,
            );
            if hit0 && let Some(poly) = hit_poly0 {
                // The foot runs into something: step up over it, unless a
                // step is already under way.
                let world_normal = frame.rotate(poly.plane.normal);
                if !t.step_up() && !t.step_down() {
                    return t.step_sphere_up(world_normal);
                }
                return t.slide_sphere(world_normal);
            }
            if let Some(sphere1) = spheres.sphere1 {
                let mut hit_poly1 = None;
                let mut contact1 = Vec3::ZERO;
                let hit1 = self.sphere_intersects_poly_internal(
                    &sphere1,
                    movement,
                    &mut hit_poly1,
                    &mut contact1,
                );
                if let Some(poly) = hit_poly1 {
                    if hit1 {
                        // The head runs into something: slide.
                        return t.slide_sphere(frame.rotate(poly.plane.normal));
                    }
                    // The head only touches: a near miss to slide along.
                    t.set_neg_poly_hit(false, frame.rotate(poly.plane.normal));
                    return TransitionState::Ok;
                }
                if let Some(poly) = hit_poly0 {
                    // The foot only touches: a near miss to step up over.
                    t.set_neg_poly_hit(true, frame.rotate(poly.plane.normal));
                    return TransitionState::Ok;
                }
            }
            return TransitionState::Ok;
        }

        // 6. In the air.
        // A polygon the sphere runs into, or only touches, counts here.
        let mut hit_poly0 = None;
        let mut contact0 = Vec3::ZERO;
        self.sphere_intersects_poly_internal(&sphere0, movement, &mut hit_poly0, &mut contact0);
        if let Some(poly) = hit_poly0 {
            if t.object_state().contains(ObjectInfoState::PATH_CLIPPED) {
                return self.collide_with_pt(t, &sphere0, spheres.curr_center, poly, frame);
            }
            t.set_collide(frame.rotate(poly.plane.normal));
            t.walk().walkable_allowance = LANDING_Z;
            return TransitionState::Adjusted;
        }
        if let Some(sphere1) = spheres.sphere1 {
            let mut hit_poly1 = None;
            let mut contact1 = Vec3::ZERO;
            self.sphere_intersects_poly_internal(&sphere1, movement, &mut hit_poly1, &mut contact1);
            if let Some(poly) = hit_poly1 {
                t.set_collision_normal(frame.rotate(poly.plane.normal));
                return TransitionState::Collided;
            }
        }
        TransitionState::Ok
    }

    /// Carries a settle offset found in the tree's frame out to the world
    /// and records the polygon as the contact plane and the walkable.
    fn settle_on<T: BspTransition + ?Sized>(
        &self,
        t: &mut T,
        poly: &ResolvedPolygon,
        local_offset: Vec3,
        frame: &BspFrame,
    ) {
        t.add_offset_to_check_pos(frame.rotate(local_offset) * frame.scale);
        let world_normal = frame.normal_to_world(poly.plane.normal);
        let world_plane = frame.world_plane(world_normal, &poly.vertices);
        let cell = t.check_cell_id();
        t.set_contact_plane(world_plane, cell, false);
        t.set_walkable(world_plane, &poly.vertices, frame, Vec3::Z);
    }

    /// Lowers the sphere by the step-down distance (scaled into the tree's
    /// frame) onto walkable ground.
    fn step_sphere_down<T: BspTransition + ?Sized>(
        &self,
        t: &mut T,
        check_pos: &Sphere,
        up: Vec3,
        frame: &BspFrame,
    ) -> TransitionState {
        let amount = -(t.step_down_amt() * t.walk().walk_interp);
        let movement = up * amount * (1.0 / frame.scale);
        let mut valid_pos = *check_pos;
        let mut changed = false;
        let mut hit_poly = None;
        self.find_walkable_internal(
            t.walk(),
            &mut valid_pos,
            movement,
            up,
            &mut hit_poly,
            &mut changed,
        );
        if let (true, Some(poly)) = (changed, hit_poly) {
            self.settle_on(t, poly, valid_pos.center - check_pos.center, frame);
            return TransitionState::Adjusted;
        }
        TransitionState::Ok
    }

    /// A clipped mover stops at what it hit: exactly at the touching point
    /// when it clips perfectly, otherwise where it is.
    fn collide_with_pt<'a, T: BspTransition + ?Sized>(
        &'a self,
        t: &mut T,
        check_pos: &Sphere,
        cur_pos: Vec3,
        hit_poly: &'a ResolvedPolygon,
        frame: &BspFrame,
    ) -> TransitionState {
        let collision_normal = frame.rotate(hit_poly.plane.normal);
        if !t.object_state().contains(ObjectInfoState::PERFECT_CLIP) {
            t.set_collision_normal(collision_normal);
            return TransitionState::Collided;
        }
        let mut valid_pos = *check_pos;
        if !self.adjust_to_plane(&mut valid_pos, cur_pos, hit_poly) {
            return TransitionState::Collided;
        }
        t.set_collision_normal(collision_normal);
        t.add_offset_to_check_pos(frame.rotate(valid_pos.center - check_pos.center) * frame.scale);
        TransitionState::Adjusted
    }

    /// Finds, by bisection, the furthest point along the move from
    /// `cur_pos` to `check_pos` where the sphere touches nothing, and puts
    /// the sphere there. False when the first search does not clear within
    /// 15 tries.
    fn adjust_to_plane<'a>(
        &'a self,
        check_pos: &mut Sphere,
        cur_pos: Vec3,
        hit_poly: &'a ResolvedPolygon,
    ) -> bool {
        const MAX_ITER: i32 = 15;
        let movement = check_pos.center - cur_pos;
        let mut hit_poly = hit_poly;
        let mut clear_time = 0.0f64;
        let mut hit_time = 1.0f64;
        let mut i = 0;
        loop {
            let touch_time = adjust_sphere_to_poly(hit_poly, check_pos, cur_pos, movement);
            if touch_time == 1.0 {
                break;
            }
            check_pos.center = cur_pos + movement * touch_time;
            let mut hp2 = None;
            let mut cp2 = Vec3::ZERO;
            if !self.sphere_intersects_poly_internal(check_pos, movement, &mut hp2, &mut cp2) {
                clear_time = f64::from(touch_time);
                break;
            }
            if let Some(p) = hp2 {
                hit_poly = p;
            }
            i += 1;
            hit_time = f64::from(touch_time);
            if i >= MAX_ITER {
                return false;
            }
        }
        while i < MAX_ITER {
            let avg = (clear_time + hit_time) * 0.5;
            check_pos.center = cur_pos + movement * avg as f32;
            let mut hp2 = None;
            let mut cp2 = Vec3::ZERO;
            if self.sphere_intersects_poly_internal(check_pos, movement, &mut hp2, &mut cp2) {
                hit_time = avg;
            } else {
                clear_time = avg;
            }
            if hit_time - clear_time < 0.02 {
                break;
            }
            i += 1;
        }
        check_pos.center = cur_pos + movement * clear_time as f32;
        true
    }

    /// Places a body without overlap: pushes its spheres out of every
    /// polygon they overlap, up to 20 tries, doubling the test radius when
    /// the centre is in solid space with no polygon to push from.
    /// [`TransitionState::Ok`] when the first try is clear,
    /// [`TransitionState::Adjusted`] (offset applied) when a later one is,
    /// [`TransitionState::Collided`] when none is.
    ///
    /// This is the test the first placement of a body (login, teleport
    /// arrival) runs against rooms and models.
    pub fn placement_insert<T: BspTransition + ?Sized>(
        &self,
        t: &mut T,
        sphere0: Sphere,
        sphere1: Option<Sphere>,
        frame: &BspFrame,
    ) -> TransitionState {
        const MAX_ITER: i32 = 20;
        let original = sphere0;
        let original_radius = sphere0.radius;
        let mut s0 = sphere0;
        let mut s1 = sphere1.unwrap_or_default();
        let has_s1 = sphere1.is_some();
        let clear_cell = !(t.bldg_check() && t.hits_interior_cell());
        let mut radius = original_radius;
        let mut hit_poly: Option<&ResolvedPolygon> = None;
        let mut iteration = 0;
        while iteration < MAX_ITER {
            let mut center_solid = false;
            if self.sphere_intersects_solid_poly_internal(
                &s0,
                radius,
                &mut center_solid,
                &mut hit_poly,
                clear_cell,
            ) {
                match hit_poly.take() {
                    Some(poly) => {
                        adjust_to_placement_poly(
                            poly,
                            &mut s0,
                            &mut s1,
                            radius,
                            center_solid,
                            clear_cell,
                        );
                        radius = original_radius;
                    }
                    None => radius *= 2.0,
                }
            } else if !has_s1
                // The head sphere is tested with the foot's centre-solid
                // flag and near-miss polygon still set.
                || !self.sphere_intersects_solid_poly_internal(
                    &s1,
                    radius,
                    &mut center_solid,
                    &mut hit_poly,
                    clear_cell,
                )
            {
                return finish_placement(t, iteration, s0, original, frame);
            } else {
                match hit_poly.take() {
                    Some(poly) => {
                        adjust_to_placement_poly(
                            poly,
                            &mut s1,
                            &mut s0,
                            radius,
                            center_solid,
                            clear_cell,
                        );
                        radius = original_radius;
                    }
                    None => radius *= 2.0,
                }
            }
            iteration += 1;
        }
        TransitionState::Collided
    }
}

/// A clear placement: unchanged on the first try, otherwise the body moves
/// by how far the foot sphere was pushed (rotated to the world).
fn finish_placement<T: BspTransition + ?Sized>(
    t: &mut T,
    iteration: i32,
    s0: Sphere,
    original: Sphere,
    frame: &BspFrame,
) -> TransitionState {
    if iteration == 0 {
        return TransitionState::Ok;
    }
    t.add_offset_to_check_pos(frame.rotate(s0.center - original.center));
    TransitionState::Adjusted
}
