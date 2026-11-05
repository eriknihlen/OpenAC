//! What the movement sweep found: the surface under the body, the normal of
//! what it hit, the objects it hit.

use glam::Vec3;

use crate::geometry::Plane;
use crate::globals::EPSILON_SQ;

/// A contact plane and where it came from.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ContactPlane {
    /// The plane is set.
    pub valid: bool,
    /// The plane (world frame).
    pub plane: Plane,
    /// The cell it belongs to.
    pub cell_id: u32,
    /// It is a water surface.
    pub is_water: bool,
}

/// The sweep's findings.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CollisionInfo {
    /// The surface the body stands on in this step.
    pub contact_plane_valid: bool,
    /// See [`Self::contact_plane_valid`].
    pub contact_plane: Plane,
    /// See [`Self::contact_plane_valid`].
    pub contact_plane_cell_id: u32,
    /// See [`Self::contact_plane_valid`].
    pub contact_plane_is_water: bool,

    /// The last surface the body stood on.
    pub last_known_contact_plane_valid: bool,
    /// See [`Self::last_known_contact_plane_valid`].
    pub last_known_contact_plane: Plane,
    /// See [`Self::last_known_contact_plane_valid`].
    pub last_known_contact_plane_cell_id: u32,
    /// See [`Self::last_known_contact_plane_valid`].
    pub last_known_contact_plane_is_water: bool,

    /// A horizontal normal of a wall the body slides along.
    pub sliding_normal_valid: bool,
    /// See [`Self::sliding_normal_valid`] (z is always 0).
    pub sliding_normal: Vec3,

    /// The normal of what the body hit.
    pub collision_normal_valid: bool,
    /// See [`Self::collision_normal_valid`].
    pub collision_normal: Vec3,

    /// The body hit the environment (terrain, cells, buildings) while not
    /// standing on anything.
    pub collided_with_environment: bool,
    /// How many steps the body has been stuck in the air (0..=3).
    pub frames_stationary_fall: i32,

    /// Offset applied by adjustments (kept for the caller).
    pub adjust_offset: Vec3,
    /// The objects hit, in order (an object may repeat).
    pub collide_object_guids: Vec<u32>,
    /// The last object hit.
    pub last_collided_object_guid: Option<u32>,

    /// How many times the contact plane actually changed (a counter for
    /// tests).
    pub contact_plane_write_count: u32,
}

impl CollisionInfo {
    /// Records the surface the body stands on (the contact plane only; the
    /// last-known plane is left alone).
    pub fn set_contact_plane(&mut self, plane: Plane, cell_id: u32, is_water: bool) {
        if self.contact_plane_valid
            && self.contact_plane_cell_id == cell_id
            && self.contact_plane_is_water == is_water
            && self.contact_plane == plane
        {
            return;
        }
        self.contact_plane_write_count += 1;
        self.contact_plane_valid = true;
        self.contact_plane = plane;
        self.contact_plane_cell_id = cell_id;
        self.contact_plane_is_water = is_water;
    }

    /// Seeds both the contact plane and the last-known plane.
    pub fn init_contact_plane(&mut self, plane: Plane, cell_id: u32, is_water: bool) {
        self.set_contact_plane(plane, cell_id, is_water);
        self.last_known_contact_plane_valid = true;
        self.last_known_contact_plane = plane;
        self.last_known_contact_plane_cell_id = cell_id;
        self.last_known_contact_plane_is_water = is_water;
    }

    /// Clears the contact plane (valid and water flags only, as retail).
    pub(crate) fn clear_contact_plane(&mut self) {
        self.contact_plane_valid = false;
        self.contact_plane_is_water = false;
    }

    /// Records a sliding normal: the horizontal part of `normal`,
    /// normalised when long enough.
    pub fn set_sliding_normal(&mut self, normal: Vec3) {
        self.sliding_normal_valid = true;
        self.sliding_normal = Vec3::new(normal.x, normal.y, 0.0);
        if self.sliding_normal.length_squared() > EPSILON_SQ {
            self.sliding_normal = crate::geometry::normalize(self.sliding_normal);
        }
    }

    /// Records the normal of what was hit.
    pub fn set_collision_normal(&mut self, normal: Vec3) {
        self.collision_normal_valid = true;
        self.collision_normal = normal;
    }

    /// The contact plane as one value.
    #[must_use]
    pub fn contact(&self) -> ContactPlane {
        ContactPlane {
            valid: self.contact_plane_valid,
            plane: self.contact_plane,
            cell_id: self.contact_plane_cell_id,
            is_water: self.contact_plane_is_water,
        }
    }

    /// The last-known contact plane as one value.
    #[must_use]
    pub fn last_known_contact(&self) -> ContactPlane {
        ContactPlane {
            valid: self.last_known_contact_plane_valid,
            plane: self.last_known_contact_plane,
            cell_id: self.last_known_contact_plane_cell_id,
            is_water: self.last_known_contact_plane_is_water,
        }
    }
}
