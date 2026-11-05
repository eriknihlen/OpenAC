//! Rejecting impossible positions before they reach the world.
//!
//! A frame is an origin (x, y, z in meters) plus a rotation quaternion
//! (w, x, y, z). It is valid when nothing is NaN and the quaternion has
//! length 1 within a small tolerance. A position adds the cell id, which
//! must name a real place on the map.
//!
//! Only NaN is rejected in the origin; an infinite coordinate passes, as in
//! the game client.

use super::land_defs::inbound_valid_cell_id;

/// How far the squared length of the rotation may stray from 1: five times
/// the physics epsilon (0.0002 as an `f32`), computed at higher precision.
const SQUARED_NORM_TOLERANCE: f64 = 0.0002_f32 as f64 * 5.0;

/// True if `origin` and `rotation_wxyz` hold no NaN and the rotation's
/// squared length is within 0.001 of 1.
#[must_use]
pub fn is_valid_frame(origin: [f32; 3], rotation_wxyz: [f32; 4]) -> bool {
    if origin
        .iter()
        .chain(rotation_wxyz.iter())
        .any(|v| v.is_nan())
    {
        return false;
    }
    let [w, x, y, z] = rotation_wxyz.map(f64::from);
    // Summed at higher precision, then rounded to f32 once.
    let norm_squared = (z * z + y * y + x * x + w * w) as f32;
    !norm_squared.is_nan() && (f64::from(norm_squared) - 1.0).abs() <= SQUARED_NORM_TOLERANCE
}

/// True if `cell_id` names a place on the map and the frame is valid.
#[must_use]
pub fn is_valid_position(cell_id: u32, origin: [f32; 3], rotation_wxyz: [f32; 4]) -> bool {
    inbound_valid_cell_id(cell_id) && is_valid_frame(origin, rotation_wxyz)
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC
    //! `tests/AcDream.Core.Tests/Physics/PositionFrameValidationTests.cs`.

    use super::*;

    const IDENTITY: [f32; 4] = [1.0, 0.0, 0.0, 0.0];

    #[test]
    fn valid_position_requires_inbound_cell_and_frame_validity() {
        assert!(is_valid_position(0x0101_FFFF, [1.0, 2.0, 3.0], IDENTITY));
        assert!(!is_valid_position(0, [1.0, 2.0, 3.0], IDENTITY));
        assert!(!is_valid_position(
            0x0101_FFFF,
            [f32::NAN, 2.0, 3.0],
            IDENTITY
        ));
        assert!(!is_valid_position(
            0x0101_FFFF,
            [1.0, 2.0, 3.0],
            [0.9, 0.0, 0.0, 0.0]
        ));
    }

    #[test]
    fn quaternion_squared_norm_uses_the_tolerance() {
        for (squared_norm, expected) in [(1.001_f32, true), (1.0011_f32, false)] {
            let rotation = [squared_norm.sqrt(), 0.0, 0.0, 0.0];
            assert_eq!(
                is_valid_position(0x0101_FFFF, [0.0; 3], rotation),
                expected,
                "{squared_norm}"
            );
        }
    }

    #[test]
    fn nan_anywhere_in_the_rotation_is_invalid() {
        for i in 0..4 {
            let mut rotation = IDENTITY;
            rotation[i] = f32::NAN;
            assert!(!is_valid_frame([0.0; 3], rotation), "component {i}");
        }
    }

    #[test]
    fn infinite_origin_passes_infinite_rotation_fails() {
        assert!(is_valid_frame([f32::INFINITY, 0.0, 0.0], IDENTITY));
        assert!(!is_valid_frame([0.0; 3], [f32::INFINITY, 0.0, 0.0, 0.0]));
    }

    #[test]
    fn tolerance_on_the_low_side() {
        // A squared length of 0.9992 is inside; 0.9988 is outside.
        assert!(is_valid_frame([0.0; 3], [0.9992_f32.sqrt(), 0.0, 0.0, 0.0]));
        assert!(!is_valid_frame(
            [0.0; 3],
            [0.9988_f32.sqrt(), 0.0, 0.0, 0.0]
        ));
    }
}
