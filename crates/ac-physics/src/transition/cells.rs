//! The transition against the environment: the primary cell's geometry or
//! terrain, the building in an outdoor cell, and the other cells the body
//! reaches.

use ac_core::physics::PhysicsState;
use glam::Vec3;

use super::Transition;
use super::bsp_bridge::BspContext;
use crate::bsp::PhysicsBsp;
use crate::bsp_collisions::WalkState;
use crate::bsp_collisions::{BspFrame, LocalSpheres};
use crate::cell_transit::{self, CellCollector};
use crate::engine::{PhysicsEngine, TerrainWalkableSample};
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON, FLOOR_Z};
use crate::state::{InsertType, ObjectInfoState, TransitionState};
use crate::world::{CellPhysics, Placement};

/// How far below the foot the indoor walkable search looks, metres.
const INDOOR_WALKABLE_PROBE_DISTANCE: f32 = 0.5;

/// Side of a landblock, metres.
const BLOCK_LENGTH: f32 = 192.0;

impl Transition {
    /// Runs a BSP tree's collision test of the body's spheres in the tree's
    /// frame (`placement` and `scale` place the tree in the world; `up` is
    /// world up in the tree's frame).
    pub(crate) fn bsp_find_collisions(
        &mut self,
        engine: &PhysicsEngine,
        bsp: &PhysicsBsp,
        placement: Placement,
        scale: f32,
    ) -> TransitionState {
        let inv_rot = placement.rotation.inverse();
        let inv_scale = if scale > 0.0 { 1.0 / scale } else { 1.0 };
        let sp = &self.sphere_path;
        let to_local = |p: Vec3| (inv_rot * (p - placement.origin)) * inv_scale;
        let sphere0 = Sphere::new(
            to_local(sp.global_sphere[0].center),
            sp.global_sphere[0].radius * inv_scale,
        );
        let sphere1 = (sp.num_sphere > 1).then(|| {
            Sphere::new(
                to_local(sp.global_sphere[1].center),
                sp.global_sphere[1].radius * inv_scale,
            )
        });
        let spheres = LocalSpheres {
            sphere0,
            sphere1,
            curr_center: to_local(sp.global_curr_center[0].center),
            space_z: inv_rot * Vec3::Z,
        };
        let frame = BspFrame {
            local_to_world: placement.rotation,
            scale,
            world_origin: placement.origin,
        };
        let mut ctx = BspContext {
            transition: self,
            engine: Some(engine),
        };
        bsp.find_collisions(&mut ctx, &spheres, &frame)
    }

    /// The cell's (or a room's) collision tree: spheres carried into the
    /// room's frame unscaled, world up in the room is `+z`.
    fn cell_find_collisions(
        &mut self,
        engine: &PhysicsEngine,
        cell: &CellPhysics,
    ) -> TransitionState {
        let placement = cell.placement;
        let sp = &self.sphere_path;
        let sphere0 = Sphere::new(
            placement.to_local(sp.global_sphere[0].center),
            sp.global_sphere[0].radius,
        );
        let sphere1 = (sp.num_sphere > 1).then(|| {
            Sphere::new(
                placement.to_local(sp.global_sphere[1].center),
                sp.global_sphere[1].radius,
            )
        });
        let spheres = LocalSpheres {
            sphere0,
            sphere1,
            curr_center: placement.to_local(sp.global_curr_center[0].center),
            space_z: Vec3::Z,
        };
        let frame = BspFrame {
            local_to_world: placement.rotation,
            scale: 1.0,
            world_origin: placement.origin,
        };
        let mut ctx = BspContext {
            transition: self,
            engine: Some(engine),
        };
        cell.physics_bsp.find_collisions(&mut ctx, &spheres, &frame)
    }

    /// The walkable polygon under a foot centre in a room (searching up to
    /// half a metre down, walking slopes only): its world plane, world
    /// vertices and id.
    pub fn try_find_indoor_walkable_plane(
        &mut self,
        cell: &CellPhysics,
        local_foot_center: Vec3,
        sphere_radius: f32,
    ) -> Option<(Plane, Vec<Vec3>, u16)> {
        if cell.physics_bsp.is_empty() {
            return None;
        }
        // The search runs on this sweep's walk fraction (which it may
        // shrink) with the walking allowance; the allowance is put back.
        let mut walk = WalkState {
            walk_interp: self.sphere_path.walk.walk_interp,
            walkable_allowance: FLOOR_Z,
        };
        let found = cell.physics_bsp.find_walkable_sphere(
            &mut walk,
            Sphere::new(local_foot_center, sphere_radius),
            INDOOR_WALKABLE_PROBE_DISTANCE,
            Vec3::Z,
        );
        self.sphere_path.walk.walk_interp = walk.walk_interp;
        let hit = found?;
        let poly = cell.physics_bsp.polygon(hit.poly_id)?;
        let world_normal = crate::geometry::normalize(cell.placement.rotation * poly.plane.normal);
        let world_v0 = cell.placement.to_world(*poly.vertices.first()?);
        let world_d = -world_normal.dot(world_v0);
        let world_vertices = poly
            .vertices
            .iter()
            .map(|v| cell.placement.to_world(*v))
            .collect();
        Some((
            Plane::new(world_normal, world_d),
            world_vertices,
            hit.poly_id,
        ))
    }

    /// Records the result of one of the other cells: collided or adjusted
    /// stops the search (and counts as an environment hit when airborne);
    /// slid stops it and clears the contact plane. True to stop.
    pub fn apply_other_cell_result(&mut self, result: TransitionState) -> bool {
        match result {
            TransitionState::Collided | TransitionState::Adjusted => {
                if !self.object_info.state.contains(ObjectInfoState::CONTACT) {
                    self.collision_info.collided_with_environment = true;
                }
                true
            }
            TransitionState::Slid => {
                self.collision_info.clear_contact_plane();
                true
            }
            _ => false,
        }
    }

    /// The other cells the body reaches (sorted by id, the check cell
    /// skipped): an outdoor cell's terrain, building and objects; a room's
    /// geometry and objects. The first one that is not OK ends the search.
    /// Terrain is tested with `sphere_radius`.
    pub fn check_other_cells(
        &mut self,
        engine: &PhysicsEngine,
        sphere_radius: f32,
        cell_set: &[u32],
    ) -> TransitionState {
        let Some(world) = engine.world() else {
            return TransitionState::Ok;
        };
        let mut ordered = cell_set.to_vec();
        ordered.sort_unstable();

        for cell_id in ordered {
            if cell_id == self.sphere_path.check_cell_id {
                continue;
            }

            // The foot centre is read live per cell: an earlier cell may
            // have moved the sphere (a step up).
            let foot_center = self.sphere_path.global_sphere[0].center;

            if cell_id & 0xFFFF < 0x0100 {
                if !world.is_visible(cell_id) {
                    continue;
                }
                if let Some(terrain) =
                    engine.sample_terrain_walkable_in_cell(cell_id, foot_center.x, foot_center.y)
                {
                    let terrain_state = if self.open_sea_blocks(&terrain) {
                        TransitionState::Collided
                    } else {
                        self.validate_walkable(
                            foot_center,
                            sphere_radius,
                            terrain.plane,
                            terrain.is_water,
                            terrain.water_depth,
                            terrain.cell_id,
                            Some(terrain.vertices),
                        )
                    };
                    if self.apply_other_cell_result(terrain_state) {
                        return terrain_state;
                    }
                }

                let bldg = self.find_building_collisions(engine, cell_id);
                if self.apply_other_cell_result(bldg) {
                    return bldg;
                }
                let obj = self.find_obj_collisions_in_cell(engine, cell_id);
                if self.apply_other_cell_result(obj) {
                    return obj;
                }
                continue;
            }

            let Some(cell) = world.cell(cell_id) else {
                continue;
            };
            if !cell.has_physics() {
                continue;
            }

            let result = self.cell_find_collisions(engine, cell);
            if self.apply_other_cell_result(result) {
                return result;
            }
            let obj = self.find_obj_collisions_in_cell(engine, cell_id);
            if self.apply_other_cell_result(obj) {
                return obj;
            }
        }
        TransitionState::Ok
    }

    /// The primary cell's environment: a room's entry restriction and
    /// geometry, else the terrain under the foot (open sea blocks).
    pub fn find_env_collisions(
        &mut self,
        engine: &PhysicsEngine,
        primary_cell_id: u32,
    ) -> TransitionState {
        self.sphere_path.obstruction_ethereal = false;

        let foot_center = self.sphere_path.global_sphere[0].center;
        let sphere_radius = self.sphere_path.global_sphere[0].radius;

        let cell_low = primary_cell_id & 0xFFFF;
        if cell_low >= 0x0100
            && let Some(world) = engine.world()
        {
            let cell = world.cell(primary_cell_id);
            let restriction_state = self
                .object_info
                .check_entry_restrictions(cell.map_or(0, |c| c.restriction_obj), engine.objects());
            if restriction_state != TransitionState::Ok {
                if !self.object_info.state.contains(ObjectInfoState::CONTACT) {
                    self.collision_info.collided_with_environment = true;
                }
                return restriction_state;
            }

            if let Some(cell) = cell
                && cell.has_physics()
            {
                let cell_state = self.cell_find_collisions(engine, cell);
                if cell_state != TransitionState::Ok {
                    if !self.object_info.state.contains(ObjectInfoState::CONTACT) {
                        self.collision_info.collided_with_environment = true;
                    }
                    return cell_state;
                }
                return TransitionState::Ok;
            }
        }

        if let Some(terrain) =
            engine.sample_terrain_walkable_in_cell(primary_cell_id, foot_center.x, foot_center.y)
        {
            if self.open_sea_blocks(&terrain) {
                if !self.object_info.state.contains(ObjectInfoState::CONTACT) {
                    self.collision_info.collided_with_environment = true;
                }
                return TransitionState::Collided;
            }
            let terrain_state = self.validate_walkable(
                foot_center,
                sphere_radius,
                terrain.plane,
                terrain.is_water,
                terrain.water_depth,
                terrain.cell_id,
                Some(terrain.vertices),
            );
            if terrain_state != TransitionState::Ok {
                return terrain_state;
            }
        }
        // No terrain resident here: pass through.
        TransitionState::Ok
    }

    /// Open sea is a wall: a landblock entirely under water refuses every
    /// mover but the camera and missiles.
    fn open_sea_blocks(&self, terrain: &TerrainWalkableSample) -> bool {
        terrain.block_entirely_water
            && !self.object_info.is_viewer()
            && !self
                .object_info
                .mover_physics_state
                .contains(PhysicsState::MISSILE)
    }

    /// After the primary cell is clear: works out every cell the body
    /// reaches and the cell holding it, tests the others, and moves the
    /// check position into the holding cell.
    pub(crate) fn run_check_other_cells_and_advance(
        &mut self,
        engine: &PhysicsEngine,
    ) -> TransitionState {
        let sphere_radius = self.sphere_path.global_sphere[0].radius;
        let Some(world) = engine.world() else {
            let resolved = engine.resolve_cell_id(
                self.sphere_path.global_sphere[0].center,
                sphere_radius,
                self.sphere_path.check_cell_id,
            );
            if resolved != self.sphere_path.check_cell_id {
                let check_pos = self.sphere_path.check_pos;
                self.sphere_path.set_check_pos(check_pos, resolved);
            }
            return TransitionState::Ok;
        };

        self.sphere_path.hits_interior_cell = false;

        let sp = &mut self.sphere_path;
        let spheres = sp.global_sphere;
        let footprint = if sp.footprint_active {
            Some(&mut sp.set_position_query_footprint)
        } else {
            None
        };
        let mut collector = CellCollector::with_footprint(&mut sp.cell_candidates, footprint);
        let (containing_cell_id, containing_cell_found) =
            cell_transit::build_cell_set_and_pick_containing(
                world,
                &spheres,
                sp.num_sphere,
                sp.check_cell_id,
                sp.carried_block_origin,
                &mut collector,
            );
        let cell_set: Vec<u32> = sp.cell_candidates.ordered_ids().to_vec();

        // A placement candidate no resident cell holds is refused outright;
        // an ordinary move keeps its seed cell.
        if !containing_cell_found
            && matches!(
                sp.insert_type,
                InsertType::InitialPlacement | InsertType::Placement
            )
        {
            return TransitionState::Collided;
        }

        if sp.check_cell_id & 0xFFFF >= 0x0100
            || containing_cell_id & 0xFFFF >= 0x0100
            || cell_set.iter().any(|id| id & 0xFFFF >= 0x0100)
        {
            sp.hits_interior_cell = true;
        }

        let other_cells_state = self.check_other_cells(engine, sphere_radius, &cell_set);
        if other_cells_state != TransitionState::Ok {
            return other_cells_state;
        }

        let sp = &mut self.sphere_path;
        if containing_cell_id != sp.check_cell_id {
            let old_low = sp.check_cell_id & 0xFFFF;
            let new_low = containing_cell_id & 0xFFFF;
            if let Some(carried) = sp.carried_block_origin
                && (1..=0x40).contains(&old_low)
                && (1..=0x40).contains(&new_low)
            {
                let old_x = ((sp.check_cell_id >> 24) & 0xFF) as i32;
                let old_y = ((sp.check_cell_id >> 16) & 0xFF) as i32;
                let new_x = ((containing_cell_id >> 24) & 0xFF) as i32;
                let new_y = ((containing_cell_id >> 16) & 0xFF) as i32;
                sp.carried_block_origin = Some(
                    carried
                        + Vec3::new(
                            (new_x - old_x) as f32 * BLOCK_LENGTH,
                            (new_y - old_y) as f32 * BLOCK_LENGTH,
                            0.0,
                        ),
                );
            }
            let check_pos = sp.check_pos;
            sp.set_check_pos(check_pos, containing_cell_id);
        }
        TransitionState::Ok
    }

    /// Tests the sphere against a ground plane (terrain, sunk by the water
    /// depth). Resting on it records the contact; below it pushes the sphere
    /// back up (refused when it is only checking for ground, or when a step
    /// down would overshoot). A body that is not standing records the
    /// collision.
    ///
    /// The camera's eye is not pushed up: a sphere sunk into the ground is
    /// moved back along its own step to where it first touches the plane,
    /// and the collision is recorded, so a clipped sweep stops there. The
    /// water depth plays no part for it.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn validate_walkable(
        &mut self,
        sphere_center: Vec3,
        sphere_radius: f32,
        contact_plane: Plane,
        is_water: bool,
        water_depth: f32,
        cell_id: u32,
        walkable_vertices: Option<[Vec3; 3]>,
    ) -> TransitionState {
        if self.object_info.is_viewer() {
            self.validate_walkable_viewer(sphere_center, sphere_radius, contact_plane);
            return TransitionState::Ok;
        }

        let low_point = sphere_center - Vec3::new(0.0, 0.0, sphere_radius);
        let dist = low_point.dot(contact_plane.normal) + contact_plane.d + water_depth;

        if dist >= -EPSILON {
            if dist <= EPSILON {
                let walkable_normal =
                    contact_plane.normal.z >= self.sphere_path.walk.walkable_allowance;
                if self.sphere_path.step_down || !self.object_info.on_walkable() || walkable_normal
                {
                    self.collision_info
                        .set_contact_plane(contact_plane, cell_id, is_water);
                    self.cache_walkable_context(contact_plane, walkable_vertices);
                }
                if !self.object_info.contact() && !self.sphere_path.step_down {
                    self.collision_info
                        .set_collision_normal(contact_plane.normal);
                    self.collision_info.collided_with_environment = true;
                }
            }
            return TransitionState::Ok;
        }

        if self.sphere_path.check_walkable {
            return TransitionState::Collided;
        }

        let z_dist = dist / contact_plane.normal.z;

        let mut result = TransitionState::Ok;
        let walkable = contact_plane.normal.z >= self.sphere_path.walk.walkable_allowance;
        if self.sphere_path.step_down || !self.object_info.on_walkable() || walkable {
            self.collision_info
                .set_contact_plane(contact_plane, cell_id, is_water);
            self.cache_walkable_context(contact_plane, walkable_vertices);

            if self.sphere_path.step_down {
                let sp = &self.sphere_path;
                let interp = (1.0 - (-1.0 / (sp.step_down_amt * sp.walk.walk_interp)) * z_dist)
                    * sp.walk.walk_interp;
                if interp >= sp.walk.walk_interp || interp < -0.1 {
                    return TransitionState::Collided;
                }
                self.sphere_path.walk.walk_interp = interp;
            }

            self.sphere_path
                .add_offset_to_check_pos(Vec3::new(0.0, 0.0, -z_dist));
            result = TransitionState::Adjusted;
        }

        if !self.object_info.contact() && !self.sphere_path.step_down {
            self.collision_info
                .set_collision_normal(contact_plane.normal);
            self.collision_info.collided_with_environment = true;
        }

        result
    }

    /// The camera's arm of [`Transition::validate_walkable`]. The gap is
    /// measured from the sphere's surface along the plane's normal (not
    /// from its lowest point). Sunk in by more than the tolerance, the
    /// sphere goes back the fraction `t` of its step from the current
    /// centre that puts its surface on the plane. A sweep that began
    /// outdoors always goes back; one that began indoors only when the
    /// touch lies within this step (`0 < t <= 1`).
    fn validate_walkable_viewer(
        &mut self,
        sphere_center: Vec3,
        sphere_radius: f32,
        contact_plane: Plane,
    ) {
        let dist = sphere_center.dot(contact_plane.normal) + contact_plane.d - sphere_radius;
        if dist > -EPSILON {
            return;
        }
        let offset = sphere_center - self.sphere_path.global_curr_center[0].center;
        let t = dist / offset.dot(contact_plane.normal);
        let within_step = t > 0.0 && t <= 1.0;
        let began_outdoors = self.sphere_path.begin_cell_id & 0xFFFF < 0x0100;
        if within_step || began_outdoors {
            self.sphere_path.add_offset_to_check_pos(offset * -t);
            self.collision_info
                .set_collision_normal(contact_plane.normal);
            self.collision_info.collided_with_environment = true;
        }
    }

    fn cache_walkable_context(
        &mut self,
        contact_plane: Plane,
        walkable_vertices: Option<[Vec3; 3]>,
    ) {
        if let Some(vertices) = walkable_vertices
            && contact_plane.normal.z >= FLOOR_Z
        {
            self.sphere_path
                .set_walkable(contact_plane, &vertices, Vec3::Z);
        }
    }

    /// The building standing in an outdoor cell: its shell model's collision
    /// tree, tested as a building check.
    pub(crate) fn find_building_collisions(
        &mut self,
        engine: &PhysicsEngine,
        cell_id: u32,
    ) -> TransitionState {
        if cell_id & 0xFFFF >= 0x0100 {
            return TransitionState::Ok;
        }
        let Some(world) = engine.world() else {
            return TransitionState::Ok;
        };
        let Some(building) = world.building(cell_id) else {
            return TransitionState::Ok;
        };
        if building.model_id == 0 {
            return TransitionState::Ok;
        }
        let Some(physics) = world.gfx_obj(building.model_id) else {
            return TransitionState::Ok;
        };
        if !physics.has_physics() {
            return TransitionState::Ok;
        }

        self.sphere_path.bldg_check = true;
        let result = self.bsp_find_collisions(engine, &physics.bsp, building.placement, 1.0);
        self.sphere_path.bldg_check = false;

        if result != TransitionState::Ok && !self.object_info.contact() {
            self.collision_info.collided_with_environment = true;
        }
        result
    }
}
