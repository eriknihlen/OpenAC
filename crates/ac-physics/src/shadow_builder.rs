//! Building an object's collision parts from its dat setup or its placed
//! models.
//!
//! An object collides either with the collision trees of its parts (when
//! any part has one) or with its setup's cylinders, else its setup's
//! spheres; never both.

use ac_dat::objects::{AnimationFrame, Setup};
use ac_motion::Frame;
use glam::{Quat, Vec3};

use crate::dat::vec3;
use crate::geometry::Sphere;
use crate::shadow::{ShadowPartGeometry, ShadowShape};
use crate::world::{GfxObjPhysics, VisualBounds};

/// Placement id of the resting pose.
const PLACEMENT_RESTING: u32 = 0x65;
/// Placement id of the default pose.
const PLACEMENT_DEFAULT: u32 = 0;

/// One placed model of a static object: which model, and where it sits in
/// the object's frame (decomposed from the part transform).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PartRef {
    /// The model id.
    pub gfx_obj_id: u32,
    /// Part position in the object's frame.
    pub position: Vec3,
    /// Part rotation in the object's frame.
    pub rotation: Quat,
    /// Part scale (the x scale is used; objects scale uniformly).
    pub scale: Vec3,
}

fn effective_part_gfx_obj_id(setup: &Setup, effective: Option<&[u32]>, index: usize) -> u32 {
    match effective {
        Some(ids) if index < ids.len() => ids[index],
        _ => setup.parts[index],
    }
}

/// The frame the parts rest in. A server object's parts collide in the
/// placement they are drawn in: `placement`'s frame, else the default one,
/// else none (every part at the object's origin). Without a placement (a
/// static object), the resting frame, else the default one, else the first.
fn resolve_placement_frame(setup: &Setup, placement: Option<u32>) -> Option<&AnimationFrame> {
    let find = |id: u32| {
        setup
            .placement_frames
            .iter()
            .find(|(k, _)| *k == id)
            .map(|(_, f)| f)
    };
    if let Some(id) = placement {
        return find(id).or_else(|| find(PLACEMENT_DEFAULT));
    }
    find(PLACEMENT_RESTING)
        .or_else(|| find(PLACEMENT_DEFAULT))
        .or_else(|| setup.placement_frames.first().map(|(_, f)| f))
}

fn part_pose(
    pose_override: Option<&[Frame]>,
    placement_frame: Option<&AnimationFrame>,
    index: usize,
) -> Frame {
    if let Some(poses) = pose_override
        && index < poses.len()
    {
        return poses[index];
    }
    if let Some(frame) = placement_frame
        && index < frame.frames.len()
    {
        return Frame::from_dat(&frame.frames[index]);
    }
    Frame {
        origin: Vec3::ZERO,
        orientation: Quat::IDENTITY,
    }
}

/// The collision parts of a setup at scale `ent_scale`.
///
/// When no part has a collision tree, the setup's cylinders (a zero height
/// becomes four radii), else its spheres. Then one tree shape per part that
/// has a collision tree, posed by `part_pose_override`, else the frame of
/// `placement` (a server object, the placement it is drawn in), else the
/// setup's resting (or default, or first) frame. `effective_part_ids`
/// replaces part models (appearance swaps). `physics_bsp_bounds` gives a
/// part's geometry; without it a part is a 2 m sphere at its origin.
#[must_use]
pub fn from_setup(
    setup: &Setup,
    ent_scale: f32,
    has_physics_bsp: &dyn Fn(u32) -> bool,
    part_pose_override: Option<&[Frame]>,
    effective_part_ids: Option<&[u32]>,
    physics_bsp_bounds: Option<&dyn Fn(u32) -> Option<ShadowPartGeometry>>,
    placement: Option<u32>,
) -> Vec<ShadowShape> {
    let mut result = Vec::new();

    let any_physics_bsp_part = (0..setup.parts.len())
        .any(|i| has_physics_bsp(effective_part_gfx_obj_id(setup, effective_part_ids, i)));

    // Cylinders and spheres only for an object with no collision-tree part.
    if !any_physics_bsp_part {
        for cyl in &setup.cyl_spheres {
            if cyl.radius <= 0.0 {
                continue;
            }
            let base_height = if cyl.height > 0.0 {
                cyl.height
            } else {
                cyl.radius * 4.0
            };
            result.push(ShadowShape::cylinder(
                0,
                vec3(cyl.origin) * ent_scale,
                Quat::IDENTITY,
                ent_scale,
                cyl.radius * ent_scale,
                base_height * ent_scale,
            ));
        }

        if setup.cyl_spheres.is_empty() {
            for sph in &setup.spheres {
                if sph.radius <= 0.0 {
                    continue;
                }
                result.push(ShadowShape::sphere(
                    0,
                    vec3(sph.origin) * ent_scale,
                    Quat::IDENTITY,
                    ent_scale,
                    sph.radius * ent_scale,
                ));
            }
        }
    }

    let placement_frame = resolve_placement_frame(setup, placement);
    for i in 0..setup.parts.len() {
        let gfx_id = effective_part_gfx_obj_id(setup, effective_part_ids, i);
        if !has_physics_bsp(gfx_id) {
            continue;
        }
        let pose = part_pose(part_pose_override, placement_frame, i);
        let geometry = physics_bsp_bounds
            .and_then(|f| f(gfx_id))
            .unwrap_or_else(|| ShadowPartGeometry::new(Sphere::new(Vec3::ZERO, 2.0), None));
        result.push(ShadowShape::bsp(
            gfx_id,
            pose.origin * ent_scale,
            pose.orientation,
            ent_scale,
            geometry,
        ));
    }

    result
}

fn part_scale(scale: Vec3) -> f32 {
    if scale.x > 0.0 { scale.x } else { 1.0 }
}

/// The collision-tree parts of a landblock static (none for a building
/// shell, which collides through its building record).
#[must_use]
pub fn from_landblock_bsp_parts<'a>(
    parts: &[PartRef],
    is_building_shell: bool,
    get_gfx_obj: &dyn Fn(u32) -> Option<&'a GfxObjPhysics>,
) -> Vec<ShadowShape> {
    let mut shapes = Vec::new();
    if is_building_shell {
        return shapes;
    }
    for part in parts {
        let Some(phys) = get_gfx_obj(part.gfx_obj_id) else {
            continue;
        };
        if !phys.has_physics() {
            continue;
        }
        let geometry = ShadowPartGeometry::new(phys.bounding_sphere, phys.visual_bounds.as_ref());
        shapes.push(ShadowShape::bsp(
            part.gfx_obj_id,
            part.position,
            part.rotation,
            part_scale(part.scale),
            geometry,
        ));
    }
    shapes
}

/// The drawn parts of a landblock static (its part array): one shape per
/// part whose drawn bounds are known, with its collision sphere when it has
/// a collision tree, else its drawn bounds' sphere. Also says whether any
/// part has a collision tree.
#[must_use]
pub fn from_static_render_parts<'a>(
    parts: &[PartRef],
    get_gfx_obj: &dyn Fn(u32) -> Option<&'a GfxObjPhysics>,
    get_visual_bounds: &dyn Fn(u32) -> Option<VisualBounds>,
) -> (Vec<ShadowShape>, bool) {
    let mut has_physics_bsp = false;
    let mut shapes = Vec::with_capacity(parts.len());
    for part in parts {
        let physics = get_gfx_obj(part.gfx_obj_id);
        let part_has_physics_bsp = physics.is_some_and(GfxObjPhysics::has_physics);
        has_physics_bsp |= part_has_physics_bsp;

        let bounds = physics
            .and_then(|p| p.visual_bounds)
            .or_else(|| get_visual_bounds(part.gfx_obj_id));
        let Some(bounds) = bounds else {
            continue;
        };

        let sphere = match physics {
            Some(p) if part_has_physics_bsp => p.bounding_sphere,
            _ => Sphere::new(bounds.center, bounds.radius),
        };
        shapes.push(ShadowShape::bsp(
            part.gfx_obj_id,
            part.position,
            part.rotation,
            part_scale(part.scale),
            ShadowPartGeometry::new(sphere, Some(&bounds)),
        ));
    }
    (shapes, has_physics_bsp)
}

/// The drawn parts of a setup (its part array), posed like
/// [`from_setup`], one per part whose drawn bounds are known.
#[must_use]
pub fn from_setup_render_parts<'a>(
    setup: &Setup,
    ent_scale: f32,
    effective_part_ids: Option<&[u32]>,
    part_pose_override: Option<&[Frame]>,
    get_gfx_obj: &dyn Fn(u32) -> Option<&'a GfxObjPhysics>,
    get_visual_bounds: &dyn Fn(u32) -> Option<VisualBounds>,
    placement: Option<u32>,
) -> Vec<ShadowShape> {
    let mut shapes = Vec::with_capacity(setup.parts.len());
    let placement_frame = resolve_placement_frame(setup, placement);
    for i in 0..setup.parts.len() {
        let gfx_id = effective_part_gfx_obj_id(setup, effective_part_ids, i);
        let physics = get_gfx_obj(gfx_id);
        let part_has_physics_bsp = physics.is_some_and(GfxObjPhysics::has_physics);

        let bounds = physics
            .and_then(|p| p.visual_bounds)
            .or_else(|| get_visual_bounds(gfx_id));
        let Some(bounds) = bounds else {
            continue;
        };

        let pose = part_pose(part_pose_override, placement_frame, i);
        let sphere = match physics {
            Some(p) if part_has_physics_bsp => p.bounding_sphere,
            _ => Sphere::new(bounds.center, bounds.radius),
        };
        shapes.push(ShadowShape::bsp(
            gfx_id,
            pose.origin * ent_scale,
            pose.orientation,
            ent_scale,
            ShadowPartGeometry::new(sphere, Some(&bounds)),
        ));
    }
    shapes
}
