//! A Setup's parts in their resting pose: each part's model and where it
//! sits in the object. Pure data in, pure data out; the renderer draws the
//! parts, the UI measures them (a label's height over a model).
//!
//! Matrices here are glam's column-vector convention: `a * b` applies `b`
//! first.

use ac_dat::objects::{AnimationFrame, Setup};
use ac_dat::types::{Frame, Quaternion, Vector3};
use glam::{Mat4, Quat, Vec3};

/// Placement id of the resting pose.
pub const PLACEMENT_RESTING: u32 = 101;
/// Placement id of the default pose.
pub const PLACEMENT_DEFAULT: u32 = 0;

/// One part of a Setup: its model and where it sits in the object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct MeshRef {
    /// The part's GfxObj id.
    pub gfx_obj_id: u32,
    /// Object-local transform of the part (scale, then rotation, then
    /// translation).
    pub transform: Mat4,
    /// The part's surface substitutions from its object's appearance
    /// (texture and palette swaps); 0 for none.
    pub variant: u32,
}

/// The pose a Setup rests in: an explicit frame, else the resting
/// placement, else the default placement, else the first one listed.
#[must_use]
pub fn resting_frame<'a>(
    setup: &'a Setup,
    override_frame: Option<&'a AnimationFrame>,
) -> Option<&'a AnimationFrame> {
    override_frame
        .or_else(|| placement(setup, PLACEMENT_RESTING))
        .or_else(|| placement(setup, PLACEMENT_DEFAULT))
        .or_else(|| setup.placement_frames.first().map(|(_, f)| f))
}

/// The frame a Setup is posed in for placement `id`: that placement's
/// frame, else the default one, else none.
#[must_use]
pub fn placement_frame(setup: &Setup, id: u32) -> Option<&AnimationFrame> {
    placement(setup, id).or_else(|| placement(setup, PLACEMENT_DEFAULT))
}

fn placement(setup: &Setup, id: u32) -> Option<&AnimationFrame> {
    setup
        .placement_frames
        .iter()
        .find(|(k, _)| *k == id)
        .map(|(_, f)| f)
}

/// The parts of a Setup in its resting pose, each with its default scale.
/// A part with no frame sits at the object's origin unrotated.
#[must_use]
pub fn flatten_setup(setup: &Setup, override_frame: Option<&AnimationFrame>) -> Vec<MeshRef> {
    let pose = resting_frame(setup, override_frame);
    setup
        .parts
        .iter()
        .enumerate()
        .map(|(i, &gfx_obj_id)| {
            let frame = pose.and_then(|p| p.frames.get(i));
            let scale = setup.default_scale.get(i).map_or(Vec3::ONE, |s| vec3(*s));
            let (rotation, translation) = frame.map_or((Quat::IDENTITY, Vec3::ZERO), split);
            MeshRef {
                gfx_obj_id,
                transform: Mat4::from_translation(translation)
                    * Mat4::from_quat(rotation)
                    * Mat4::from_scale(scale),
                variant: 0,
            }
        })
        .collect()
}

/// The rigid (unscaled) transform of each part in the resting pose, with
/// the part's offset multiplied by `object_scale`. Empty when the Setup
/// lists no placement at all.
#[must_use]
pub fn setup_part_transforms(
    setup: &Setup,
    override_frame: Option<&AnimationFrame>,
    object_scale: f32,
) -> Vec<Mat4> {
    let Some(pose) = resting_frame(setup, override_frame) else {
        return Vec::new();
    };
    (0..setup.parts.len())
        .map(|i| {
            let (rotation, translation) = pose
                .frames
                .get(i)
                .map_or((Quat::IDENTITY, Vec3::ZERO), split);
            Mat4::from_translation(translation * object_scale) * Mat4::from_quat(rotation)
        })
        .collect()
}

/// A dat vector as glam's.
#[must_use]
pub fn vec3(v: Vector3) -> Vec3 {
    Vec3::new(v.x, v.y, v.z)
}

/// A dat quaternion (w, x, y, z) as glam's.
#[must_use]
pub fn quat(q: Quaternion) -> Quat {
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

fn split(frame: &Frame) -> (Quat, Vec3) {
    (quat(frame.orientation), vec3(frame.origin))
}
