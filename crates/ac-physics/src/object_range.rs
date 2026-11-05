//! Whether two objects are within a range of each other, the test behind
//! "close enough to use" (a vendor's window, an opened container).

use ac_motion::move_to_math::cylinder_distance;
use glam::Vec3;

/// One object for a range test: where it stands (its base), its girth and
/// its height, metres.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct RangeBody {
    /// Base position, metres.
    pub position: Vec3,
    /// Cylinder radius, metres.
    pub radius: f32,
    /// Cylinder height, metres.
    pub height: f32,
}

/// True when `first` and `second` are within `range` metres (inclusive).
/// `ignore_z_delta` measures the flat distance between centres and wins
/// over `use_radii`; `use_radii` measures the gap between the two upright
/// cylinders (a step between them adds its height); otherwise the straight
/// distance between centres.
#[must_use]
pub fn objects_in_range(
    first: RangeBody,
    second: RangeBody,
    range: f64,
    use_radii: bool,
    ignore_z_delta: bool,
) -> bool {
    let distance = if ignore_z_delta {
        let dx = f64::from(second.position.x - first.position.x);
        let dy = f64::from(second.position.y - first.position.y);
        (dx * dx + dy * dy).sqrt()
    } else if use_radii {
        f64::from(cylinder_distance(
            first.radius,
            first.height,
            first.position,
            second.radius,
            second.height,
            second.position,
        ))
    } else {
        f64::from(first.position.distance(second.position))
    };
    distance <= range
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/ObjectRangeMathTests.cs`.

    use super::*;

    fn body(x: f32, y: f32, z: f32, radius: f32, height: f32) -> RangeBody {
        RangeBody {
            position: Vec3::new(x, y, z),
            radius,
            height,
        }
    }

    #[test]
    fn external_container_range_uses_the_surface_gap_not_the_centre_distance() {
        let player = body(7.01, 98.06, -0.45, 0.48, 1.835);
        let corpse = body(3.73, 98.03, -0.44, 0.80, 0.50);
        assert!(objects_in_range(player, corpse, 2.01, true, false));
    }

    #[test]
    fn centre_distance_mode_does_not_subtract_the_radii() {
        let a = body(0.0, 0.0, 0.0, 10.0, 10.0);
        let b = body(3.0, 0.0, 0.0, 10.0, 10.0);
        assert!(!objects_in_range(a, b, 2.0, false, false));
    }

    #[test]
    fn ignore_z_delta_uses_the_flat_centre_distance_over_the_radii() {
        let a = body(0.0, 0.0, 0.0, 100.0, 100.0);
        let b = body(3.0, 4.0, 1000.0, 100.0, 100.0);
        assert!(objects_in_range(a, b, 5.0, true, true));
    }

    #[test]
    fn the_range_boundary_is_inclusive() {
        let a = body(0.0, 0.0, 0.0, 0.0, 0.0);
        let b = body(3.0, 4.0, 0.0, 0.0, 0.0);
        assert!(objects_in_range(a, b, 5.0, false, false));
    }
}
