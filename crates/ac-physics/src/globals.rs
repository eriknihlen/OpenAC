//! Physics constants shared by every collision query.

/// The general tolerance of collision tests, metres (and unitless for dot
/// products).
pub const EPSILON: f32 = 0.0002;

/// [`EPSILON`] squared.
pub const EPSILON_SQ: f32 = EPSILON * EPSILON;

/// Smallest up-component of a surface normal a falling body may land on:
/// cos 85°, so anything up to 85° from level.
pub const LANDING_Z: f32 = 0.087_155_7;

/// Smallest up-component of a surface normal a body can stand and walk on:
/// about 48° from level.
pub const FLOOR_Z: f32 = 0.6642;

/// Step-up height a body gets when it has none of its own, metres.
pub const DEFAULT_STEP_HEIGHT: f32 = 0.01;

/// Acceleration of gravity, metres per second squared (down).
pub const GRAVITY: f32 = -9.8;

/// Fastest a body may move, metres per second.
pub const MAX_VELOCITY: f32 = 50.0;

/// Radius of the stand-in sphere used when an object has none, metres.
pub const DUMMY_SPHERE_RADIUS: f32 = 0.1;
