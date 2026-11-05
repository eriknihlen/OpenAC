//! Orientation helpers.

use glam::{Mat3, Quat, Vec3};

use crate::globals::EPSILON_SQ;

/// The orientation facing `direction` (its local +y along the direction,
/// +x to the right, +z up the cross product); the current orientation when
/// the direction is too short or not finite.
#[must_use]
pub fn set_vector_heading(current_orientation: Quat, direction: Vec3) -> Quat {
    let length_squared = direction.length_squared();
    if length_squared < EPSILON_SQ || !length_squared.is_finite() {
        return current_orientation;
    }
    let forward = direction / length_squared.sqrt();

    let right = if forward.x == 0.0 && forward.y == 0.0 {
        Vec3::X
    } else {
        let scale = forward.x.abs().max(forward.y.abs());
        let x = forward.x / scale;
        let y = forward.y / scale;
        let horizontal_length = (x * x + y * y).sqrt();
        Vec3::new(y / horizontal_length, -x / horizontal_length, 0.0)
    };

    let up = crate::geometry::normalize(right.cross(forward));

    // Columns are the world directions of the local axes: +x right, +y
    // heading, +z up.
    let rotation = Mat3::from_cols(right, forward, up);
    crate::geometry::normalize_quat(Quat::from_mat3(&rotation))
}
