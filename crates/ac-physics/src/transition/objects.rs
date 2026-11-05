//! The transition against the objects listed in a cell: collision trees,
//! spheres and upright cylinders.

use glam::Vec3;

use super::{Transition, bsp_only_dispatch};
use crate::collision_flags::{EntityCollisionFlags, should_skip};
use crate::engine::PhysicsEngine;
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON, LANDING_Z};
use crate::shadow::{ShadowCollisionType, ShadowEntry};
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::world::Placement;

fn sphere_collides_with_sphere(disp: Vec3, radsum: f32) -> bool {
    disp.length_squared() <= radsum * radsum
}

fn cyl_collides_with_sphere(disp: Vec3, radsum: f32, cyl_height: f32, sphere_radius: f32) -> bool {
    if disp.x * disp.x + disp.y * disp.y <= radsum * radsum {
        let half_h = cyl_height * 0.5;
        if sphere_radius - EPSILON + half_h >= (half_h - disp.z).abs() {
            return true;
        }
    }
    false
}

/// Normalises `v`; true (and `v` unchanged) when it is too short.
fn normalize_check_small(v: &mut Vec3) -> bool {
    let mag = v.length();
    if mag < EPSILON {
        return true;
    }
    *v /= mag;
    false
}

fn find_sphere_time_of_collision(movement: Vec3, sphere_pos: Vec3, rad_sum: f32) -> f64 {
    let dist_sq = movement.length_squared();
    if dist_sq < EPSILON {
        return -1.0;
    }
    let non_collide = sphere_pos.length_squared() - rad_sum * rad_sum;
    if non_collide < EPSILON {
        return -1.0;
    }
    let similar = -sphere_pos.dot(movement);
    let non_collide_b =
        f64::from(similar) * f64::from(similar) - f64::from(non_collide) * f64::from(dist_sq);
    if non_collide_b < 0.0 {
        return -1.0;
    }
    // The fraction of the movement at which the spheres first touch: the
    // nearer root, or the farther one when the nearer lies behind.
    let c_dist = non_collide_b.sqrt();
    if f64::from(similar) - c_dist < 0.0 {
        return (c_dist + f64::from(similar)) / f64::from(dist_sq);
    }
    (f64::from(similar) - c_dist) / f64::from(dist_sq)
}

impl Transition {
    /// The objects listed in a cell, in list order: skipped are the mover
    /// itself, missile-ignored targets, exempt targets (see
    /// [`should_skip`]) and, while stepping down, ethereal ones. The first
    /// object that is not OK ends the search. The initial placement tests no
    /// objects (the placement search that follows does).
    pub fn find_obj_collisions_in_cell(
        &mut self,
        engine: &PhysicsEngine,
        cell_id: u32,
    ) -> TransitionState {
        let Some(world) = engine.world() else {
            return TransitionState::Ok;
        };
        if self.sphere_path.insert_type == InsertType::InitialPlacement {
            return TransitionState::Ok;
        }

        let objs = engine.shadows.objects_in_cell(cell_id);
        if objs.is_empty() {
            return TransitionState::Ok;
        }

        for obj in objs {
            if self.object_info.self_entity_id != 0
                && obj.entity_id == self.object_info.self_entity_id
            {
                continue;
            }
            if self
                .object_info
                .missile_ignore(obj.entity_id, obj.state, obj.flags)
            {
                continue;
            }
            if should_skip(obj.state, obj.flags, self.object_info.state) {
                continue;
            }

            let ethereal_for_test =
                obj.state & 0x4 != 0 || (self.object_info.ethereal && obj.state & 0x1 == 0);
            if ethereal_for_test && self.sphere_path.step_down {
                continue;
            }
            self.sphere_path.obstruction_ethereal = ethereal_for_test;

            let collision_was_valid_pre = self.collision_info.collision_normal_valid;

            let mut result = match obj.collision_type {
                ShadowCollisionType::Bsp => {
                    let physics = world.gfx_obj(obj.gfx_obj_id).filter(|p| p.has_physics());
                    let Some(physics) = physics else {
                        self.sphere_path.obstruction_ethereal = false;
                        continue;
                    };
                    self.bsp_find_collisions(
                        engine,
                        &physics.bsp,
                        Placement::new(obj.rotation, obj.position),
                        obj.scale,
                    )
                }
                ShadowCollisionType::Sphere => {
                    if bsp_only_dispatch(obj.state) {
                        continue;
                    }
                    let is_creature = obj.state & 0x40 != 0
                        || obj.flags.contains(EntityCollisionFlags::IS_CREATURE);
                    self.sphere_collision(obj, engine, is_creature)
                }
                ShadowCollisionType::Cylinder => {
                    if bsp_only_dispatch(obj.state) {
                        continue;
                    }
                    self.cylinder_collision(obj, engine)
                }
            };

            if result != TransitionState::Ok
                && self.sphere_path.obstruction_ethereal
                && !self.sphere_path.step_down
                && obj.state & 0x1 == 0
            {
                result = TransitionState::Ok;
                self.collision_info.collision_normal_valid = false;
            }

            let attributed = result != TransitionState::Ok
                || (!collision_was_valid_pre && self.collision_info.collision_normal_valid);
            if attributed {
                self.collision_info.collide_object_guids.push(obj.entity_id);
                self.collision_info.last_collided_object_guid = Some(obj.entity_id);
            }

            self.sphere_path.obstruction_ethereal = false;

            if result != TransitionState::Ok {
                return result;
            }
        }
        TransitionState::Ok
    }

    // ── Spheres ────────────────────────────────────────────────────────

    fn sphere_collision(
        &mut self,
        obj: &ShadowEntry,
        engine: &PhysicsEngine,
        is_creature: bool,
    ) -> TransitionState {
        if self.sphere_path.obstruction_ethereal {
            return TransitionState::Ok;
        }
        let sp = &self.sphere_path;
        let s0 = sp.global_sphere[0];
        let disp0 = s0.center - obj.position;
        let radsum = s0.radius + obj.radius - EPSILON;

        let has_head = sp.num_sphere > 1;
        let disp1 = if has_head {
            sp.global_sphere[1].center - obj.position
        } else {
            Vec3::ZERO
        };

        if sp.insert_type == InsertType::Placement {
            if sphere_collides_with_sphere(disp0, radsum) {
                return TransitionState::Collided;
            }
            if has_head && sphere_collides_with_sphere(disp1, radsum) {
                return TransitionState::Collided;
            }
            return TransitionState::Ok;
        }

        if sp.step_down {
            if is_creature {
                return TransitionState::Ok;
            }
            return self.sphere_step_sphere_down(obj, disp0, radsum);
        }

        if sp.check_walkable {
            if sphere_collides_with_sphere(disp0, radsum) {
                return TransitionState::Collided;
            }
            if has_head && sphere_collides_with_sphere(disp1, radsum) {
                return TransitionState::Collided;
            }
            return TransitionState::Ok;
        }

        if !sp.collide {
            let state = self.object_info.state;
            if state.intersects(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE) {
                // Grounded: the foot steps over or slides; the head slides.
                if sphere_collides_with_sphere(disp0, radsum) {
                    return self.sphere_step_sphere_up(obj, engine, disp0, radsum);
                }
                if has_head && sphere_collides_with_sphere(disp1, radsum) {
                    return self.sphere_slide_sphere(obj, 1);
                }
            } else if state.contains(ObjectInfoState::PATH_CLIPPED) {
                if sphere_collides_with_sphere(disp0, radsum) {
                    return self.sphere_collide_with_point(obj, s0, radsum, 0);
                }
            } else {
                // Airborne: the foot lands on the sphere; the head hits a
                // point.
                if sphere_collides_with_sphere(disp0, radsum) {
                    return self.sphere_land_on_sphere(obj);
                }
                if has_head && sphere_collides_with_sphere(disp1, radsum) {
                    let s1 = sp.global_sphere[1];
                    return self.sphere_collide_with_point(obj, s1, radsum, 1);
                }
            }
            return TransitionState::Ok;
        }

        if is_creature {
            return TransitionState::Ok;
        }

        let hit0 = sphere_collides_with_sphere(disp0, radsum);
        if !(hit0 || has_head && sphere_collides_with_sphere(disp1, radsum)) {
            return TransitionState::Ok;
        }

        let mut movement = sp.global_curr_center[0].center - s0.center;
        let radsum_eps = radsum + EPSILON;
        let len_sq = movement.length_squared();
        if len_sq.abs() < EPSILON {
            return TransitionState::Collided;
        }
        let diff = -movement.dot(disp0);
        let disc = diff * diff - (disp0.length_squared() - radsum_eps * radsum_eps) * len_sq;
        if disc < 0.0 {
            return TransitionState::Collided;
        }
        let mut tt = disc.sqrt() + diff;
        if tt > 1.0 {
            tt = diff * 2.0 - tt;
        }
        let time = tt / len_sq;
        let timecheck = (1.0 - time) * sp.walk.walk_interp;
        if timecheck >= sp.walk.walk_interp || timecheck < -0.1 {
            return TransitionState::Collided;
        }
        movement *= time;
        let disp_n = (disp0 + movement) / radsum_eps;
        if disp_n.z <= sp.walk.walkable_allowance {
            // The sphere's top is too steep to rest on.
            return TransitionState::Ok;
        }
        let rest_pt = s0.center - disp_n * s0.radius;
        let contact_plane = Plane::new(disp_n, -disp_n.dot(rest_pt));
        let check_cell = sp.check_cell_id;
        self.collision_info
            .set_contact_plane(contact_plane, check_cell, true);
        self.sphere_path.walk.walk_interp = timecheck;
        self.sphere_path.add_offset_to_check_pos(movement);
        TransitionState::Adjusted
    }

    fn sphere_step_sphere_up(
        &mut self,
        obj: &ShadowEntry,
        engine: &PhysicsEngine,
        disp0: Vec3,
        radsum: f32,
    ) -> TransitionState {
        let radsum_eps = radsum + EPSILON;
        if self.object_info.step_up_height < radsum_eps - disp0.z {
            return self.sphere_slide_sphere(obj, 0);
        }
        let n = self.sphere_path.global_curr_center[0].center - obj.position;
        if self.do_step_up(n, engine) {
            return TransitionState::Ok;
        }
        self.step_up_slide()
    }

    fn sphere_slide_sphere(&mut self, obj: &ShadowEntry, sphere_num: usize) -> TransitionState {
        let curr = self.sphere_path.global_curr_center[sphere_num].center;
        let mut n = curr - obj.position;
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }
        self.slide_sphere(n, curr, sphere_num)
    }

    fn sphere_land_on_sphere(&mut self, obj: &ShadowEntry) -> TransitionState {
        let mut n = self.sphere_path.global_curr_center[0].center - obj.position;
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }
        self.sphere_path.set_collide(n);
        self.sphere_path.walk.walkable_allowance = LANDING_Z;
        TransitionState::Adjusted
    }

    fn sphere_collide_with_point(
        &mut self,
        obj: &ShadowEntry,
        check_sphere: Sphere,
        radsum: f32,
        sphere_num: usize,
    ) -> TransitionState {
        let g_center = self.sphere_path.global_curr_center[sphere_num].center;
        let mut global_offset = g_center - obj.position;

        if !self
            .object_info
            .state
            .contains(ObjectInfoState::PERFECT_CLIP)
        {
            if !normalize_check_small(&mut global_offset) {
                self.collision_info.set_collision_normal(global_offset);
            }
            return TransitionState::Collided;
        }

        // Perfect clip: back off to the exact time of impact.
        let radsum_eps = radsum + EPSILON;
        let check_offset = check_sphere.center - g_center;
        let toi = find_sphere_time_of_collision(check_offset, global_offset, radsum_eps);
        if toi < f64::from(EPSILON) || toi > 1.0 {
            return TransitionState::Collided;
        }
        // Pull the check sphere back along its own movement to the touching
        // point; the contact direction is only the collision normal.
        let collision_offset = check_offset * toi as f32 - check_offset;
        let old_disp = collision_offset + check_sphere.center - obj.position;
        self.collision_info
            .set_collision_normal(old_disp / radsum_eps);
        self.sphere_path.add_offset_to_check_pos(collision_offset);
        TransitionState::Adjusted
    }

    fn sphere_step_sphere_down(
        &mut self,
        obj: &ShadowEntry,
        disp0: Vec3,
        radsum: f32,
    ) -> TransitionState {
        let sp = &self.sphere_path;
        let mut hit = sphere_collides_with_sphere(disp0, radsum);
        if !hit && sp.num_sphere > 1 {
            let disp1 = sp.global_sphere[1].center - obj.position;
            hit = sphere_collides_with_sphere(disp1, radsum);
        }
        if !hit {
            return TransitionState::Ok;
        }

        let step_down = sp.step_down_amt * sp.walk.walk_interp;
        if step_down.abs() < EPSILON {
            return TransitionState::Collided;
        }

        let radsum_eps = radsum + EPSILON;
        let under_root = radsum_eps * radsum_eps - (disp0.x * disp0.x + disp0.y * disp0.y);
        if under_root < 0.0 {
            return TransitionState::Collided;
        }
        let val = under_root.sqrt();
        let scaled_step = (val - disp0.z) / step_down;
        let timecheck = (1.0 - scaled_step) * sp.walk.walk_interp;
        if timecheck >= sp.walk.walk_interp || timecheck < -0.1 {
            return TransitionState::Collided;
        }

        let interp = step_down * scaled_step;
        let disp_n = Vec3::new(disp0.x, disp0.y, disp0.z + interp) / radsum_eps;
        if disp_n.z <= sp.walk.walkable_allowance {
            return TransitionState::Ok;
        }

        let rest_pt = obj.position + disp_n * obj.radius;
        let rest_plane = Plane::new(disp_n, -disp_n.dot(rest_pt));
        let check_cell = sp.check_cell_id;
        self.collision_info
            .set_contact_plane(rest_plane, check_cell, true);
        self.sphere_path.walk.walk_interp = timecheck;
        self.sphere_path
            .add_offset_to_check_pos(Vec3::new(0.0, 0.0, interp));
        TransitionState::Adjusted
    }

    // ── Cylinders ──────────────────────────────────────────────────────

    fn cylinder_collision(&mut self, obj: &ShadowEntry, engine: &PhysicsEngine) -> TransitionState {
        // Degenerate heights: registration applies the same fallback.
        let cyl_height = if obj.cyl_height > 0.0 {
            obj.cyl_height
        } else {
            obj.radius * 4.0
        };

        let sp = &self.sphere_path;
        let s0 = sp.global_sphere[0];
        let disp0 = s0.center - obj.position;
        let radsum = obj.radius - EPSILON + s0.radius;

        let has_head = sp.num_sphere > 1;
        let (disp1, head_radius) = if has_head {
            (
                sp.global_sphere[1].center - obj.position,
                sp.global_sphere[1].radius,
            )
        } else {
            (Vec3::ZERO, 0.0)
        };

        if sp.insert_type == InsertType::Placement || sp.obstruction_ethereal {
            if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius) {
                return TransitionState::Collided;
            }
            if has_head && cyl_collides_with_sphere(disp1, radsum, cyl_height, head_radius) {
                return TransitionState::Collided;
            }
            return TransitionState::Ok;
        }

        if sp.step_down {
            return self.cyl_step_sphere_down(obj, cyl_height, disp0, radsum);
        }

        if sp.check_walkable {
            if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius) {
                return TransitionState::Collided;
            }
            if has_head && cyl_collides_with_sphere(disp1, radsum, cyl_height, head_radius) {
                return TransitionState::Collided;
            }
            return TransitionState::Ok;
        }

        if !sp.collide {
            let state = self.object_info.state;
            if state.intersects(ObjectInfoState::CONTACT | ObjectInfoState::ON_WALKABLE) {
                // Grounded: the foot steps over or onto; the head slides.
                if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius) {
                    return self.cyl_step_sphere_up(obj, engine, cyl_height, disp0, radsum);
                }
                if has_head && cyl_collides_with_sphere(disp1, radsum, cyl_height, head_radius) {
                    return self.cyl_slide_sphere(obj, cyl_height, disp1, radsum, 1);
                }
            } else if state.contains(ObjectInfoState::PATH_CLIPPED) {
                if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius) {
                    return self.cyl_collide_with_point(obj, cyl_height, s0, disp0, radsum, 0);
                }
            } else {
                // Airborne: the foot lands on the top; the head hits a point.
                if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius) {
                    return self.cyl_land_on_cylinder(obj, cyl_height, disp0, radsum);
                }
                if has_head && cyl_collides_with_sphere(disp1, radsum, cyl_height, head_radius) {
                    let s1 = sp.global_sphere[1];
                    return self.cyl_collide_with_point(obj, cyl_height, s1, disp1, radsum, 1);
                }
            }
            return TransitionState::Ok;
        }

        if cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius)
            || (has_head && cyl_collides_with_sphere(disp1, radsum, cyl_height, head_radius))
        {
            let movement = sp.global_curr_center[0].center - s0.center;
            if movement.z.abs() < EPSILON {
                return TransitionState::Collided;
            }
            let timecheck = (cyl_height + s0.radius - disp0.z) / movement.z;
            let offset = movement * timecheck;

            let sum = offset + disp0;
            if radsum * radsum < sum.x * sum.x + sum.y * sum.y {
                // The rewound point is off the top: not a top landing.
                return TransitionState::Ok;
            }

            let t = (1.0 - timecheck) * sp.walk.walk_interp;
            if t >= sp.walk.walk_interp || t < -0.1 {
                return TransitionState::Collided;
            }

            let mut pt = s0.center + offset;
            pt.z -= s0.radius;
            let contact_plane = Plane::new(Vec3::Z, -pt.z);
            let check_cell = sp.check_cell_id;
            self.collision_info
                .set_contact_plane(contact_plane, check_cell, true);
            self.sphere_path.walk.walk_interp = t;
            self.sphere_path.add_offset_to_check_pos(offset);
            return TransitionState::Adjusted;
        }

        TransitionState::Ok
    }

    /// The normal of a cylinder hit: horizontal (from the axis) when the
    /// current centre is beside the cylinder, else up or down. True when the
    /// side normal is certain (the current centre overlaps the cylinder's
    /// height band, or did not move vertically) or the normal is vertical.
    #[allow(clippy::too_many_arguments)]
    fn cyl_normal_of_collision(
        &self,
        obj: &ShadowEntry,
        cyl_height: f32,
        disp_check: Vec3,
        radsum: f32,
        sphere_radius: f32,
        sphere_num: usize,
    ) -> (bool, Vec3) {
        let disp_curr = self.sphere_path.global_curr_center[sphere_num].center - obj.position;
        if radsum * radsum < disp_curr.x * disp_curr.x + disp_curr.y * disp_curr.y {
            let normal = Vec3::new(disp_curr.x, disp_curr.y, 0.0);
            let half_h = cyl_height * 0.5;
            let z_band_overlap_at_curr =
                sphere_radius - EPSILON + half_h >= (half_h - disp_curr.z).abs();
            let no_z_movement = (disp_curr.z - disp_check.z).abs() <= EPSILON;
            return (z_band_overlap_at_curr || no_z_movement, normal);
        }
        let normal = Vec3::new(
            0.0,
            0.0,
            if disp_check.z - disp_curr.z <= 0.0 {
                1.0
            } else {
                -1.0
            },
        );
        (true, normal)
    }

    fn cyl_step_sphere_up(
        &mut self,
        obj: &ShadowEntry,
        engine: &PhysicsEngine,
        cyl_height: f32,
        disp0: Vec3,
        radsum: f32,
    ) -> TransitionState {
        let s0 = self.sphere_path.global_sphere[0];
        if self.object_info.step_up_height < s0.radius + cyl_height - disp0.z {
            return self.cyl_slide_sphere(obj, cyl_height, disp0, radsum, 0);
        }
        let (_, mut n) = self.cyl_normal_of_collision(obj, cyl_height, disp0, radsum, s0.radius, 0);
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }
        let n_world = obj.rotation * n;
        if self.do_step_up(n_world, engine) {
            return TransitionState::Ok;
        }
        self.step_up_slide()
    }

    fn cyl_step_sphere_down(
        &mut self,
        obj: &ShadowEntry,
        cyl_height: f32,
        disp0: Vec3,
        radsum: f32,
    ) -> TransitionState {
        let sp = &self.sphere_path;
        let s0 = sp.global_sphere[0];
        let mut hit = cyl_collides_with_sphere(disp0, radsum, cyl_height, s0.radius);
        if !hit && sp.num_sphere > 1 {
            let disp1 = sp.global_sphere[1].center - obj.position;
            hit = cyl_collides_with_sphere(disp1, radsum, cyl_height, sp.global_sphere[1].radius);
        }
        if !hit {
            return TransitionState::Ok;
        }

        let step_scale = sp.step_down_amt * sp.walk.walk_interp;
        if step_scale.abs() < EPSILON {
            return TransitionState::Collided;
        }

        let delta_z = cyl_height + s0.radius - disp0.z;
        let interp = (1.0 - delta_z / step_scale) * sp.walk.walk_interp;
        if interp >= sp.walk.walk_interp || interp < -0.1 {
            return TransitionState::Collided;
        }

        let top_z = s0.center.z + delta_z - s0.radius;
        let contact_plane = Plane::new(Vec3::Z, -top_z);
        let check_cell = sp.check_cell_id;
        self.collision_info
            .set_contact_plane(contact_plane, check_cell, true);
        self.sphere_path.walk.walk_interp = interp;
        self.sphere_path
            .add_offset_to_check_pos(Vec3::new(0.0, 0.0, delta_z));
        TransitionState::Adjusted
    }

    fn cyl_slide_sphere(
        &mut self,
        obj: &ShadowEntry,
        cyl_height: f32,
        disp: Vec3,
        radsum: f32,
        sphere_num: usize,
    ) -> TransitionState {
        let radius = self.sphere_path.global_sphere[sphere_num].radius;
        let (_, mut n) =
            self.cyl_normal_of_collision(obj, cyl_height, disp, radsum, radius, sphere_num);
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }
        let curr = self.sphere_path.global_curr_center[sphere_num].center;
        self.slide_sphere(n, curr, sphere_num)
    }

    fn cyl_land_on_cylinder(
        &mut self,
        obj: &ShadowEntry,
        cyl_height: f32,
        disp0: Vec3,
        radsum: f32,
    ) -> TransitionState {
        let radius = self.sphere_path.global_sphere[0].radius;
        let (_, mut n) = self.cyl_normal_of_collision(obj, cyl_height, disp0, radsum, radius, 0);
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }
        self.sphere_path.set_collide(n);
        self.sphere_path.walk.walkable_allowance = LANDING_Z;
        TransitionState::Adjusted
    }

    // The time checks are written out so a NaN time passes as it does in
    // OpenAC (a range check would refuse it).
    #[allow(clippy::too_many_arguments, clippy::manual_range_contains)]
    fn cyl_collide_with_point(
        &mut self,
        obj: &ShadowEntry,
        cyl_height: f32,
        check_sphere: Sphere,
        disp: Vec3,
        radsum: f32,
        sphere_num: usize,
    ) -> TransitionState {
        let (definite, mut n) = self.cyl_normal_of_collision(
            obj,
            cyl_height,
            disp,
            radsum,
            check_sphere.radius,
            sphere_num,
        );
        if normalize_check_small(&mut n) {
            return TransitionState::Collided;
        }

        if !self
            .object_info
            .state
            .contains(ObjectInfoState::PERFECT_CLIP)
        {
            self.collision_info.set_collision_normal(n);
            return TransitionState::Collided;
        }

        let glob_center = self.sphere_path.global_curr_center[0].center;
        let movement = check_sphere.center - glob_center;
        let old_disp = glob_center - obj.position;
        let radsum_eps = radsum + EPSILON;

        let xy_move_len_sq = movement.x * movement.x + movement.y * movement.y;
        let dot2d = movement.x * old_disp.x + movement.y * old_disp.y;
        let xy_diff = -dot2d;
        let old_disp_xy_sq = old_disp.x * old_disp.x + old_disp.y * old_disp.y;
        let diff_sq =
            xy_diff * xy_diff - (old_disp_xy_sq - radsum_eps * radsum_eps) * xy_move_len_sq;

        let mut time: f32;
        let mut scaled_movement: Vec3;

        if !definite {
            if movement.z.abs() < EPSILON {
                return TransitionState::Collided;
            }
            if movement.z > 0.0 {
                n = Vec3::new(0.0, 0.0, -1.0);
                time = -((movement.z + check_sphere.radius) / movement.z);
            } else {
                n = Vec3::new(0.0, 0.0, 1.0);
                time = (check_sphere.radius + cyl_height - movement.z) / movement.z;
            }
            scaled_movement = movement * time;

            let landed = scaled_movement + old_disp;
            if landed.x * landed.x + landed.y * landed.y >= radsum_eps * radsum_eps {
                if xy_move_len_sq.abs() < EPSILON {
                    return TransitionState::Collided;
                }
                if diff_sq >= 0.0 && xy_move_len_sq > EPSILON {
                    let diff = diff_sq.sqrt();
                    time = if xy_diff - diff < 0.0 {
                        (diff - dot2d) / xy_move_len_sq
                    } else {
                        (xy_diff - diff) / xy_move_len_sq
                    };
                    scaled_movement = movement * time;
                }
                n = (scaled_movement + glob_center - obj.position) / radsum_eps;
                n.z = 0.0;
            }

            if time < 0.0 || time > 1.0 {
                return TransitionState::Collided;
            }

            let offset_out = glob_center - scaled_movement - check_sphere.center;
            self.sphere_path.add_offset_to_check_pos(offset_out);
            self.collision_info.set_collision_normal(n);
            return TransitionState::Adjusted;
        }

        if n.z != 0.0 {
            if movement.z.abs() < EPSILON {
                return TransitionState::Collided;
            }
            time = if movement.z > 0.0 {
                -((old_disp.z + check_sphere.radius) / movement.z)
            } else {
                (check_sphere.radius + cyl_height - old_disp.z) / movement.z
            };
            scaled_movement = movement * time;

            if time < 0.0 || time > 1.0 {
                return TransitionState::Collided;
            }

            let offset_out = glob_center + scaled_movement - check_sphere.center;
            self.sphere_path.add_offset_to_check_pos(offset_out);
            self.collision_info.set_collision_normal(n);
            return TransitionState::Adjusted;
        }

        if diff_sq < 0.0 || xy_move_len_sq < EPSILON {
            return TransitionState::Collided;
        }

        let diff = diff_sq.sqrt();
        time = if xy_diff - diff < 0.0 {
            (diff - dot2d) / xy_move_len_sq
        } else {
            (xy_diff - diff) / xy_move_len_sq
        };
        scaled_movement = movement * time;

        if time < 0.0 || time > 1.0 {
            return TransitionState::Collided;
        }

        n = (scaled_movement + glob_center - obj.position) / radsum_eps;
        n.z = 0.0;

        let offset_out = glob_center + scaled_movement - check_sphere.center;
        self.sphere_path.add_offset_to_check_pos(offset_out);
        self.collision_info.set_collision_normal(n);
        TransitionState::Adjusted
    }
}
