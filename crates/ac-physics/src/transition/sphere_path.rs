//! The moving body's spheres and positions through one sweep.

use glam::{Quat, Vec3};

use crate::bsp_collisions::WalkState;
use crate::cell_array::CellArray;
use crate::geometry::{Plane, Sphere};
use crate::globals::{DUMMY_SPHERE_RADIUS, FLOOR_Z};
use crate::polygon::check_walkable_support;
use crate::state::InsertType;

/// A sphere of a body's collision shape, in the body's frame (scaled).
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct CollisionSphere {
    /// Centre in the body's frame.
    pub origin: Vec3,
    /// Radius.
    pub radius: f32,
}

impl CollisionSphere {
    /// A sphere at `origin` with `radius`.
    #[must_use]
    pub const fn new(origin: Vec3, radius: f32) -> Self {
        Self { origin, radius }
    }
}

/// The body's spheres and the positions of one sweep.
#[derive(Debug, Clone, PartialEq)]
pub struct SpherePath {
    /// How many spheres the body has (1 or 2: foot and head).
    pub num_sphere: usize,
    /// The spheres in the body's frame.
    pub local_sphere: [Sphere; 2],
    /// The spheres where the step being checked would put them.
    pub global_sphere: [Sphere; 2],
    /// The spheres where the body is now.
    pub global_curr_center: [Sphere; 2],

    /// Start of the move.
    pub begin_pos: Vec3,
    /// The cell the move started in (0: none).
    pub begin_cell_id: u32,
    /// End of the move.
    pub end_pos: Vec3,
    /// Where the body is now.
    pub cur_pos: Vec3,
    /// Where the step being checked puts it.
    pub check_pos: Vec3,
    /// Orientation at the start.
    pub begin_orientation: Quat,
    /// Orientation at the end.
    pub end_orientation: Quat,
    /// Orientation now.
    pub cur_orientation: Quat,
    /// Orientation of the step being checked.
    pub check_orientation: Quat,

    /// The cell the body is in.
    pub cur_cell_id: u32,
    /// The cell of the step being checked.
    pub check_cell_id: u32,

    /// The world corner of the landblock outdoor cells are measured from,
    /// carried with the body.
    pub carried_block_origin: Option<Vec3>,

    /// The offset of the step being checked.
    pub global_offset: Vec3,

    /// A step up is under way.
    pub step_up: bool,
    /// The normal that started the step up.
    pub step_up_normal: Vec3,
    /// A collision was flagged in this step.
    pub collide: bool,

    /// A step down is under way.
    pub step_down: bool,
    /// How far the step down may lower the body.
    pub step_down_amt: f32,
    /// The unused fraction of the step (`walk_interp`) and the smallest
    /// up-component of a walkable normal here (`walkable_allowance`).
    pub walk: WalkState,

    /// The walkable polygon under the body is known.
    pub walkable_valid: bool,
    /// Its plane.
    pub walkable_plane: Plane,
    /// Its vertices (world frame), when known.
    pub walkable_vertices: Option<Vec<Vec3>>,
    /// The up direction it was found with.
    pub walkable_up: Vec3,

    /// The last walkable polygon found (kept across clears).
    pub last_walkable_valid: bool,
    /// Its plane.
    pub last_walkable_plane: Plane,
    /// Its vertices.
    pub last_walkable_vertices: Option<Vec<Vec3>>,
    /// The up direction.
    pub last_walkable_up: Vec3,

    /// The saved check position.
    pub backup_check_pos: Vec3,
    /// The saved check cell.
    pub backup_check_cell_id: u32,

    /// A near miss was recorded.
    pub neg_poly_hit: bool,
    /// The near miss asks for a step up (else a slide).
    pub neg_step_up: bool,
    /// The near miss's normal.
    pub neg_collision_normal: Vec3,
    /// Only asking whether there is ground under the body.
    pub check_walkable: bool,
    /// Why the body is being inserted.
    pub insert_type: InsertType,
    /// Placement may slide the body to a free spot.
    pub placement_allows_sliding: bool,

    /// Passing through an ethereal obstruction.
    pub obstruction_ethereal: bool,
    /// Testing a building's shell.
    pub bldg_check: bool,
    /// The body also overlaps a building's interior cell.
    pub hits_interior_cell: bool,

    /// The cells the step reaches.
    pub cell_candidates: CellArray,
    /// Every cell a set-position looked at.
    pub set_position_query_footprint: CellArray,
    /// Record into [`Self::set_position_query_footprint`].
    pub footprint_active: bool,
}

impl Default for SpherePath {
    fn default() -> Self {
        Self {
            num_sphere: 1,
            local_sphere: [Sphere::default(); 2],
            global_sphere: [Sphere::default(); 2],
            global_curr_center: [Sphere::default(); 2],
            begin_pos: Vec3::ZERO,
            begin_cell_id: 0,
            end_pos: Vec3::ZERO,
            cur_pos: Vec3::ZERO,
            check_pos: Vec3::ZERO,
            begin_orientation: Quat::IDENTITY,
            end_orientation: Quat::IDENTITY,
            cur_orientation: Quat::IDENTITY,
            check_orientation: Quat::IDENTITY,
            cur_cell_id: 0,
            check_cell_id: 0,
            carried_block_origin: None,
            global_offset: Vec3::ZERO,
            step_up: false,
            step_up_normal: Vec3::ZERO,
            collide: false,
            step_down: false,
            step_down_amt: 0.0,
            walkable_valid: false,
            walkable_plane: Plane::default(),
            walkable_vertices: None,
            walkable_up: Vec3::Z,
            walk: WalkState::default(),
            last_walkable_valid: false,
            last_walkable_plane: Plane::default(),
            last_walkable_vertices: None,
            last_walkable_up: Vec3::Z,
            backup_check_pos: Vec3::ZERO,
            backup_check_cell_id: 0,
            neg_poly_hit: false,
            neg_step_up: false,
            neg_collision_normal: Vec3::ZERO,
            check_walkable: false,
            insert_type: InsertType::Transition,
            placement_allows_sliding: true,
            obstruction_ethereal: false,
            bldg_check: false,
            hits_interior_cell: false,
            cell_candidates: CellArray::new(),
            set_position_query_footprint: CellArray::new(),
            footprint_active: false,
        }
    }
}

impl SpherePath {
    /// True when a walkable polygon with at least three vertices is known.
    #[must_use]
    pub fn has_walkable_polygon(&self) -> bool {
        self.walkable_valid
            && self
                .walkable_vertices
                .as_ref()
                .is_some_and(|v| v.len() >= 3)
    }

    /// True when a last walkable polygon with at least three vertices is
    /// known.
    #[must_use]
    pub fn has_last_walkable_polygon(&self) -> bool {
        self.last_walkable_valid
            && self
                .last_walkable_vertices
                .as_ref()
                .is_some_and(|v| v.len() >= 3)
    }

    /// Moves the step being checked to `pos` in `cell_id` (the spheres
    /// follow, with the check orientation).
    pub fn set_check_pos(&mut self, pos: Vec3, cell_id: u32) {
        self.check_pos = pos;
        self.check_cell_id = cell_id;
        for i in 0..self.num_sphere {
            self.global_sphere[i].center =
                self.check_orientation * self.local_sphere[i].center + pos;
            self.global_sphere[i].radius = self.local_sphere[i].radius;
        }
    }

    /// Moves the step being checked (and its spheres) by `offset`.
    pub fn add_offset_to_check_pos(&mut self, offset: Vec3) {
        self.check_pos += offset;
        for i in 0..self.num_sphere {
            self.global_sphere[i].center += offset;
        }
    }

    /// Saves the check position and cell.
    pub fn save_check_pos(&mut self) {
        self.backup_check_pos = self.check_pos;
        self.backup_check_cell_id = self.check_cell_id;
    }

    /// Restores the saved check position and cell.
    pub fn restore_check_pos(&mut self) {
        self.set_check_pos(self.backup_check_pos, self.backup_check_cell_id);
    }

    /// Flags a collision: remembers the check position, the normal, and
    /// resets the walk fraction.
    pub fn set_collide(&mut self, collision_normal: Vec3) {
        self.collide = true;
        self.backup_check_pos = self.check_pos;
        self.backup_check_cell_id = self.check_cell_id;
        self.step_up_normal = collision_normal;
        self.walk.walk_interp = 1.0;
    }

    /// Records the walkable polygon (world vertices) and remembers it as
    /// the last one too.
    pub fn set_walkable(&mut self, plane: Plane, vertices: &[Vec3], up: Vec3) {
        self.walkable_valid = true;
        self.walkable_plane = plane;
        self.walkable_vertices = Some(vertices.to_vec());
        self.walkable_up = up;
        self.walk.walkable_allowance = FLOOR_Z;

        self.last_walkable_valid = true;
        self.last_walkable_plane = plane;
        self.last_walkable_vertices = Some(vertices.to_vec());
        self.last_walkable_up = up;
    }

    /// Records the walkable polygon from vertices in a tree's frame,
    /// carried into the world by `local_to_world`, `scale` and
    /// `world_origin`.
    pub fn set_walkable_transformed(
        &mut self,
        plane: Plane,
        local_vertices: &[Vec3],
        local_to_world: Quat,
        scale: f32,
        world_origin: Vec3,
        up: Vec3,
    ) {
        let world: Vec<Vec3> = local_vertices
            .iter()
            .map(|v| local_to_world * (*v * scale) + world_origin)
            .collect();
        self.set_walkable(plane, &world, up);
    }

    /// Forgets the walkable polygon (the last one is kept).
    pub fn clear_walkable(&mut self) {
        self.walkable_valid = false;
        self.walkable_vertices = None;
    }

    /// Makes the last walkable polygon the current one again; false when
    /// there is none.
    pub fn restore_last_walkable(&mut self) -> bool {
        if !self.has_last_walkable_polygon() {
            return false;
        }
        self.walkable_valid = true;
        self.walkable_plane = self.last_walkable_plane;
        self.walkable_vertices = self.last_walkable_vertices.clone();
        self.walkable_up = self.last_walkable_up;
        true
    }

    /// True when the foot sphere is still supported by the walkable polygon
    /// (or none is known).
    #[must_use]
    pub fn check_walkables(&self) -> bool {
        if !self.has_walkable_polygon() {
            return true;
        }
        let Some(vertices) = &self.walkable_vertices else {
            return true;
        };
        let foot = self.global_sphere[0];
        check_walkable_support(
            &self.walkable_plane,
            vertices,
            foot.center,
            foot.radius * 0.5,
            self.walkable_up,
        )
    }

    /// Sets up a sweep from `begin` to `end` in `cell_id` for a body of one
    /// sphere (`sphere_height` 0) or two (foot and head, the head
    /// `sphere_height − radius` above the foot). The foot sits at
    /// `local_sphere_origin`, default one radius up.
    #[allow(clippy::too_many_arguments)]
    pub fn init_path(
        &mut self,
        begin: Vec3,
        end: Vec3,
        cell_id: u32,
        sphere_radius: f32,
        sphere_height: f32,
        local_sphere_origin: Option<Vec3>,
        begin_orientation: Option<Quat>,
        end_orientation: Option<Quat>,
    ) {
        let origin0 = local_sphere_origin.unwrap_or(Vec3::new(0.0, 0.0, sphere_radius));
        if sphere_height > 0.0 {
            self.init_path_core(
                begin,
                end,
                cell_id,
                2,
                origin0,
                sphere_radius,
                Vec3::new(0.0, 0.0, sphere_height - sphere_radius),
                sphere_radius,
                begin_orientation,
                end_orientation,
            );
        } else {
            self.init_path_core(
                begin,
                end,
                cell_id,
                1,
                origin0,
                sphere_radius,
                Vec3::ZERO,
                0.0,
                begin_orientation,
                end_orientation,
            );
        }
    }

    /// [`Self::init_path`] with one sphere of `sphere_radius`, default
    /// placement and no rotation.
    pub fn init_path_simple(&mut self, begin: Vec3, end: Vec3, cell_id: u32, sphere_radius: f32) {
        self.init_path(begin, end, cell_id, sphere_radius, 0.0, None, None, None);
    }

    /// Sets up a sweep with the body's own sphere list (at most two, scaled;
    /// none gives a stand-in sphere).
    #[allow(clippy::too_many_arguments)]
    pub fn init_path_spheres(
        &mut self,
        begin: Vec3,
        end: Vec3,
        cell_id: u32,
        spheres: &[CollisionSphere],
        scale: f32,
        begin_orientation: Option<Quat>,
        end_orientation: Option<Quat>,
    ) {
        if spheres.is_empty() {
            self.init_path_core(
                begin,
                end,
                cell_id,
                1,
                Vec3::new(0.0, 0.0, DUMMY_SPHERE_RADIUS),
                DUMMY_SPHERE_RADIUS,
                Vec3::ZERO,
                0.0,
                begin_orientation,
                end_orientation,
            );
            return;
        }
        let s0 = spheres[0];
        if spheres.len() > 1 {
            let s1 = spheres[1];
            self.init_path_core(
                begin,
                end,
                cell_id,
                2,
                s0.origin * scale,
                s0.radius * scale,
                s1.origin * scale,
                s1.radius * scale,
                begin_orientation,
                end_orientation,
            );
        } else {
            self.init_path_core(
                begin,
                end,
                cell_id,
                1,
                s0.origin * scale,
                s0.radius * scale,
                Vec3::ZERO,
                0.0,
                begin_orientation,
                end_orientation,
            );
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn init_path_core(
        &mut self,
        begin: Vec3,
        end: Vec3,
        cell_id: u32,
        num_sphere: usize,
        origin0: Vec3,
        radius0: f32,
        origin1: Vec3,
        radius1: f32,
        begin_orientation: Option<Quat>,
        end_orientation: Option<Quat>,
    ) {
        self.begin_pos = begin;
        self.begin_cell_id = cell_id;
        self.end_pos = end;
        self.cur_pos = begin;
        self.cur_cell_id = cell_id;

        self.begin_orientation = begin_orientation.unwrap_or(Quat::IDENTITY);
        self.end_orientation = end_orientation.unwrap_or(self.begin_orientation);
        self.cur_orientation = self.begin_orientation;
        self.check_orientation = self.begin_orientation;

        self.num_sphere = num_sphere;
        self.local_sphere[0] = Sphere::new(origin0, radius0);
        if num_sphere > 1 {
            self.local_sphere[1] = Sphere::new(origin1, radius1);
        }

        self.set_check_pos(begin, cell_id);

        for i in 0..self.num_sphere {
            self.global_curr_center[i].center =
                self.cur_orientation * self.local_sphere[i].center + begin;
            self.global_curr_center[i].radius = self.local_sphere[i].radius;
        }
    }

    /// Re-derives the current sphere centres from the current position and
    /// orientation.
    pub(crate) fn refresh_curr_centers(&mut self) {
        for i in 0..self.num_sphere {
            self.global_curr_center[i].center =
                self.cur_orientation * self.local_sphere[i].center + self.cur_pos;
            self.global_curr_center[i].radius = self.local_sphere[i].radius;
        }
    }
}
