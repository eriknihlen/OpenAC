//! The world as the panels see it, and what they hand the host to draw in
//! it.
//!
//! The host draws the world; [`WorldFrame`] is what the panels ask of the
//! world as it was last drawn (where a point lands in the drawing frame,
//! the camera, a held object's pose, the land's height, which icon anchors
//! the world hides). What they place in the world for the host to draw
//! (plugin lines, triangles and ground shapes, the icon anchors to test)
//! are the plain types below, in the drawing frame.

use ac_plugin_ui::GroundShape;
use glam::{Mat4, Quat, Vec3};

/// The world as the host last drew it.
pub trait WorldFrame {
    /// Where `local` (metres in the cell `cell_id`'s landblock) lies in the
    /// drawing frame; `None` while that landblock is not placed.
    fn frame_position(&self, cell_id: u32, local: Vec3) -> Option<Vec3>;

    /// The root pose last published for the live object `guid` (a held
    /// object's hangs from its holder's hand), drawing frame.
    fn published_root_pose(&self, guid: u32) -> Option<Mat4>;

    /// The camera's view and projection, drawing frame, this frame.
    fn view_matrices(&self) -> Option<(Mat4, Mat4)>;

    /// The world is hidden behind portal space.
    fn portal_hides_world(&self) -> bool;

    /// Whether the anchor of `key` was in sight when last tested; `None`
    /// before an answer.
    fn icon_probe_visible(&self, key: IconProbeKey) -> Option<bool>;

    /// The ground's height under a drawing-frame point, where its
    /// landblock's terrain is known.
    fn sample_terrain_z(&self, x: f32, y: f32) -> Option<f32>;
}

/// The direction an orientation faces in the ground plane, as an angle from
/// +x (east) toward +y (north), radians in -pi..=pi. An object's forward
/// axis is its local +y.
#[must_use]
pub fn yaw_of(orientation: Quat) -> f32 {
    let forward = orientation * Vec3::Y;
    forward.y.atan2(forward.x)
}
/// A plugin's line placed in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedLine {
    /// One end, drawing frame.
    pub start: Vec3,
    /// The other end, drawing frame.
    pub end: Vec3,
    /// `0xRRGGBB`.
    pub color_rgb: u32,
    /// Kept just above the ground along its length.
    pub follow_terrain: bool,
    /// Thickness, metres.
    pub width_metres: f32,
}

/// A plugin's triangle placed in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedTriangle {
    /// The corners, drawing frame.
    pub corners: [Vec3; 3],
    /// Each corner's colour (0..1, alpha last), blended across the face.
    pub colors: [[f32; 4]; 3],
}

/// How far above the land its anchor may be for a shape to follow it, metres
/// (at or below the land counts).
pub const ON_THE_LAND_METRES: f32 = 1.0;

/// Where a shape's anchor is this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ShapeAnchor {
    /// Its base, drawing frame.
    pub centre: Vec3,
    /// The way it faces, as a compass bearing in degrees.
    pub heading_degrees: f32,
    /// On the landscape, not inside a building or dungeon.
    pub is_outdoor: bool,
}

/// A plugin's ground shape placed in the drawing frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedShape {
    /// Its centre, drawing frame.
    pub centre: Vec3,
    /// The bearing its arc starts at, the object's heading already added.
    pub start_degrees: f32,
    /// Each point lies on the land under it, rather than flat at the
    /// centre's height.
    pub follow_terrain: bool,
    /// The land's height at the centre when it follows the land, else the
    /// centre's height.
    pub ground_z: f32,
    /// The outer radius, metres.
    pub radius: f32,
    /// How wide a band is, metres, inward from the radius.
    pub width: f32,
    /// How far round it goes, clockwise from its start, degrees.
    pub sweep_degrees: f32,
    /// Filled from the centre, rather than a band.
    pub filled: bool,
    /// Red, green, blue, alpha, 0..1.
    pub color: [f32; 4],
}

/// Where `shape` lies around `at`, or `None` when the anchor is not a finite
/// position. `ground` is the land's height at a drawing-frame (x, y), where
/// it is known.
#[must_use]
pub fn place_shape(
    shape: &GroundShape,
    at: ShapeAnchor,
    ground: impl Fn(f32, f32) -> Option<f32>,
) -> Option<PlacedShape> {
    if !at.centre.is_finite() {
        return None;
    }
    let land = ground(at.centre.x, at.centre.y)
        .filter(|z| at.is_outdoor && at.centre.z - z <= ON_THE_LAND_METRES);
    let start_degrees = if shape.faces_object {
        shape.start_degrees + at.heading_degrees
    } else {
        shape.start_degrees
    };
    Some(PlacedShape {
        centre: at.centre,
        start_degrees,
        follow_terrain: land.is_some(),
        ground_z: land.unwrap_or(at.centre.z),
        radius: shape.radius,
        width: shape.width,
        sweep_degrees: shape.sweep_degrees,
        filled: shape.filled,
        color: shape
            .color_rgba
            .to_be_bytes()
            .map(|channel| f32::from(channel) / 255.0),
    })
}

/// Which anchor a probe answers for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum IconProbeKey {
    /// An object, whichever of its icons asked.
    Object(u32),
    /// A fixed position, by the bits of its map coordinates (east-west,
    /// north-south, elevation): every icon there shares the answer, and an
    /// icon put somewhere else, even in the place of one taken away, starts
    /// with none.
    Position([u64; 3]),
}

/// An anchor to test this frame.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct IconProbe {
    /// What the answer is kept under.
    pub key: IconProbeKey,
    /// The anchor, drawing frame.
    pub anchor: Vec3,
}
