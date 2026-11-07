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
/// about 48.381° from level. Retail computes it once at startup as
/// `cos(3437.7467707849391)` (the argument in radians) stored as an `f32`;
/// this is that `f32`, bit for bit (`0x3F2A_0751`).
pub const FLOOR_Z: f32 = 0.664_174_14;

/// Step-up height a body gets when it has none of its own, metres.
pub const DEFAULT_STEP_HEIGHT: f32 = 0.01;

/// Acceleration of gravity, metres per second squared (down).
pub const GRAVITY: f32 = -9.8;

/// Fastest a body may move, metres per second.
pub const MAX_VELOCITY: f32 = 50.0;

/// Radius of the stand-in sphere used when an object has none, metres.
pub const DUMMY_SPHERE_RADIUS: f32 = 0.1;

#[cfg(test)]
mod tests {
    use super::*;

    /// Retail walkable slope: the `f32` of `cos(3437.7467707849391)`.
    #[test]
    fn floor_z_is_the_retail_cosine_as_an_f32() {
        #[allow(clippy::cast_possible_truncation)]
        let derived = 3_437.746_770_784_939_f64.cos() as f32;
        assert_eq!(FLOOR_Z.to_bits(), derived.to_bits());
        assert_eq!(FLOOR_Z.to_bits(), 0x3F2A_0751);
    }

    /// A normal between the retail limit and the old rounded 0.6642 (a
    /// slope of about 48.38° from level) is walkable.
    #[test]
    fn a_normal_just_under_the_rounded_limit_is_walkable() {
        let z = 0.664_18_f32;
        assert!(z < 0.6642);
        assert!(crate::engine::is_walkable_contact(
            true,
            glam::Vec3::new(0.0, 0.0, z)
        ));
    }
}
