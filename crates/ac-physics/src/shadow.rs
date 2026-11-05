//! The collision shapes of objects in the world ("shadows") and the rows
//! the per-cell object lists hold.
//!
//! An object collides as one of three shapes per part: a collision tree
//! (BSP) of a model, an upright cylinder, or a sphere. Each part also has
//! a box, used to work out which cells the object reaches into.

use glam::{Quat, Vec3};

use crate::collision_flags::EntityCollisionFlags;
use crate::geometry::Sphere;
use crate::world::{Placement, VisualBounds};

/// Which kind of shape a shadow row collides as.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum ShadowCollisionType {
    /// A model's collision tree.
    #[default]
    Bsp,
    /// An upright cylinder.
    Cylinder,
    /// A sphere.
    Sphere,
}

/// The bounds of one model part: its collision sphere and its box, in the
/// part's own frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowPartGeometry {
    /// The collision tree's root sphere.
    pub sphere: Sphere,
    /// Smallest box corner.
    pub box_min: Vec3,
    /// Largest box corner.
    pub box_max: Vec3,
}

impl ShadowPartGeometry {
    /// The geometry from a sphere and, when known, the model's drawn
    /// bounds (the box); without them the box is the sphere's cube.
    #[must_use]
    pub fn new(sphere: Sphere, visual_bounds: Option<&VisualBounds>) -> Self {
        if let Some(bounds) = visual_bounds {
            return Self {
                sphere,
                box_min: bounds.min,
                box_max: bounds.max,
            };
        }
        let extent = Vec3::splat(sphere.radius);
        Self {
            sphere,
            box_min: sphere.center - extent,
            box_max: sphere.center + extent,
        }
    }
}

/// One collision part of an object, in the object's own frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowShape {
    /// The model id (for a collision tree), else 0.
    pub gfx_obj_id: u32,
    /// Part position in the object's frame (already scaled).
    pub local_position: Vec3,
    /// Part rotation in the object's frame.
    pub local_rotation: Quat,
    /// The scale already applied to the geometry.
    pub scale: f32,
    /// The kind of shape.
    pub collision_type: ShadowCollisionType,
    /// Radius (scaled), metres.
    pub radius: f32,
    /// Cylinder height (scaled), metres; 0 for other shapes.
    pub cyl_height: f32,
    /// Centre of the bounding sphere in the part's frame (scaled).
    pub bounds_center: Vec3,
    /// Smallest box corner in the part's frame (scaled).
    pub local_bounds_min: Vec3,
    /// Largest box corner in the part's frame (scaled).
    pub local_bounds_max: Vec3,
}

impl ShadowShape {
    /// A model part colliding with its collision tree.
    #[must_use]
    pub fn bsp(
        gfx_obj_id: u32,
        local_position: Vec3,
        local_rotation: Quat,
        scale: f32,
        local_geometry: ShadowPartGeometry,
    ) -> Self {
        Self {
            gfx_obj_id,
            local_position,
            local_rotation,
            scale,
            collision_type: ShadowCollisionType::Bsp,
            radius: local_geometry.sphere.radius * scale,
            cyl_height: 0.0,
            bounds_center: local_geometry.sphere.center * scale,
            local_bounds_min: local_geometry.box_min * scale,
            local_bounds_max: local_geometry.box_max * scale,
        }
    }

    /// An upright cylinder with its base at the part position.
    #[must_use]
    pub fn cylinder(
        gfx_obj_id: u32,
        local_position: Vec3,
        local_rotation: Quat,
        scale: f32,
        radius: f32,
        cyl_height: f32,
    ) -> Self {
        Self {
            gfx_obj_id,
            local_position,
            local_rotation,
            scale,
            collision_type: ShadowCollisionType::Cylinder,
            radius,
            cyl_height,
            bounds_center: Vec3::ZERO,
            local_bounds_min: Vec3::new(-radius, -radius, 0.0),
            local_bounds_max: Vec3::new(radius, radius, cyl_height),
        }
    }

    /// A sphere centred on the part position.
    #[must_use]
    pub fn sphere(
        gfx_obj_id: u32,
        local_position: Vec3,
        local_rotation: Quat,
        scale: f32,
        radius: f32,
    ) -> Self {
        Self {
            gfx_obj_id,
            local_position,
            local_rotation,
            scale,
            collision_type: ShadowCollisionType::Sphere,
            radius,
            cyl_height: 0.0,
            bounds_center: Vec3::ZERO,
            local_bounds_min: Vec3::splat(-radius),
            local_bounds_max: Vec3::splat(radius),
        }
    }
}

/// A part's box placed in the world.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowPartBox {
    /// Smallest box corner in the part's frame (scaled).
    pub local_min: Vec3,
    /// Largest box corner in the part's frame (scaled).
    pub local_max: Vec3,
    /// World position of the part.
    pub world_position: Vec3,
    /// World rotation of the part.
    pub world_rotation: Quat,
}

impl ShadowPartBox {
    /// The box of a shape on an object placed in the world.
    #[must_use]
    pub fn from_shape(shape: &ShadowShape, entity_position: Vec3, entity_rotation: Quat) -> Self {
        Self {
            local_min: shape.local_bounds_min,
            local_max: shape.local_bounds_max,
            world_position: entity_position + entity_rotation * shape.local_position,
            world_rotation: entity_rotation * shape.local_rotation,
        }
    }

    fn corner(&self, index: usize) -> Vec3 {
        Vec3::new(
            if index & 1 == 0 {
                self.local_min.x
            } else {
                self.local_max.x
            },
            if index & 2 == 0 {
                self.local_min.y
            } else {
                self.local_max.y
            },
            if index & 4 == 0 {
                self.local_min.z
            } else {
                self.local_max.z
            },
        )
    }

    /// The axis-aligned box around all eight corners, relative to
    /// `frame_origin`.
    #[must_use]
    pub fn refit_to(&self, frame_origin: Vec3) -> (Vec3, Vec3) {
        let offset = self.world_position - frame_origin;
        let mut min = Vec3::splat(f32::MAX);
        let mut max = Vec3::splat(f32::MIN);
        for corner in 0..8 {
            let world = self.world_rotation * self.corner(corner) + offset;
            min = min.min(world);
            max = max.max(world);
        }
        (min, max)
    }

    /// The axis-aligned box around all eight corners in a cell's frame.
    #[must_use]
    pub fn refit_to_local(&self, cell: &Placement) -> (Vec3, Vec3) {
        let mut min = Vec3::splat(f32::MAX);
        let mut max = Vec3::splat(f32::MIN);
        for corner in 0..8 {
            let world = self.world_rotation * self.corner(corner) + self.world_position;
            let dest = cell.to_local(world);
            min = min.min(dest);
            max = max.max(dest);
        }
        (min, max)
    }
}

/// One collision part of one object, as a cell's object list holds it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShadowEntry {
    /// The object's id.
    pub entity_id: u32,
    /// The model id (collision tree parts), else 0.
    pub gfx_obj_id: u32,
    /// World position of the part.
    pub position: Vec3,
    /// World rotation of the part.
    pub rotation: Quat,
    /// Radius, metres.
    pub radius: f32,
    /// The kind of shape.
    pub collision_type: ShadowCollisionType,
    /// Cylinder height, metres.
    pub cyl_height: f32,
    /// Scale of the part's geometry.
    pub scale: f32,
    /// The object's physics state bits.
    pub state: u32,
    /// What kind of object it is.
    pub flags: EntityCollisionFlags,
    /// Part position in the object's frame.
    pub local_position: Vec3,
    /// Part rotation in the object's frame (all zero for a single-shape
    /// registration, as OpenAC's default quaternion).
    pub local_rotation: Quat,
}

impl ShadowEntry {
    /// A row for a single-shape object (no part offset).
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn single(
        entity_id: u32,
        gfx_obj_id: u32,
        position: Vec3,
        rotation: Quat,
        radius: f32,
        collision_type: ShadowCollisionType,
        cyl_height: f32,
        scale: f32,
        state: u32,
        flags: EntityCollisionFlags,
    ) -> Self {
        Self {
            entity_id,
            gfx_obj_id,
            position,
            rotation,
            radius,
            collision_type,
            cyl_height,
            scale,
            state,
            flags,
            local_position: Vec3::ZERO,
            local_rotation: Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
        }
    }
}

/// Which algorithm worked out an object's cell array.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum RetailCellArrayRoute {
    /// Not worked out.
    #[default]
    None,
    /// From the object's cylinders.
    Cylsphere,
    /// From its parts' boxes.
    BoundingBox,
}

/// One drawn part of one object in one cell's draw list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RetailPartEntry {
    /// The object's id.
    pub entity_id: u32,
    /// The part's index in the object's part array.
    pub part_index: usize,
    /// The part's model.
    pub gfx_obj_id: u32,
    /// The cell.
    pub cell_id: u32,
    /// The object spans more than one cell, so drawing it needs clip planes.
    pub clip_planes_required: bool,
}
