//! A placement (position plus rotation) and the few ways the animation code
//! moves one.
//!
//! Positions are metres, x east, y north, z up. A rotation is a unit
//! quaternion: think of it as "turn this far about this axis", stored in a
//! form that composes without gimbal problems. Angular velocities ("omega")
//! are vectors whose direction is the turning axis and whose length is the
//! speed in radians per second.

use glam::{Quat, Vec3};

/// The game's general small-number tolerance: 0.0002 as an `f32`
/// (0.000199999995).
pub const F_EPSILON: f32 = 0.000_2;

/// A position and an orientation.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Frame {
    /// Position, metres.
    pub origin: Vec3,
    /// Orientation.
    pub orientation: Quat,
}

impl Default for Frame {
    fn default() -> Self {
        Self::IDENTITY
    }
}

impl Frame {
    /// At the origin, not rotated.
    pub const IDENTITY: Self = Self {
        origin: Vec3::ZERO,
        orientation: Quat::IDENTITY,
    };

    /// A frame at `origin` with `orientation`.
    #[must_use]
    pub fn new(origin: Vec3, orientation: Quat) -> Self {
        Self {
            origin,
            orientation,
        }
    }

    /// Convert a frame as stored in the dat files.
    #[must_use]
    pub fn from_dat(f: &ac_dat::types::Frame) -> Self {
        Self {
            origin: vec3_from_dat(f.origin),
            orientation: quat_from_dat(f.orientation),
        }
    }

    /// Rotate by `rotation_global` (axis times angle, radians) about the
    /// world axes: the new turn is applied after the current orientation.
    /// Rotations shorter than [`F_EPSILON`] are ignored.
    pub fn grotate(&mut self, rotation_global: Vec3) {
        let mag_sq = rotation_global.length_squared();
        if mag_sq < F_EPSILON * F_EPSILON {
            return;
        }
        let angle = mag_sq.sqrt();
        let inv_mag = 1.0 / angle;
        let half = angle * 0.5;
        let s = half.sin();
        let c = half.cos();
        let r = Quat::from_xyzw(
            rotation_global.x * s * inv_mag,
            rotation_global.y * s * inv_mag,
            rotation_global.z * s * inv_mag,
            c,
        );
        self.orientation = set_rotate(self.origin, self.orientation, r * self.orientation);
    }

    /// Rotate by `rotation_local` (axis times angle, radians) about the
    /// frame's own axes.
    pub fn rotate(&mut self, rotation_local: Vec3) {
        self.grotate(self.orientation * rotation_local);
    }

    /// Append the relative placement `pos`: move by `pos.origin` measured in
    /// this frame's axes, then turn by `pos.orientation`.
    pub fn combine(&mut self, pos: &Frame) {
        self.origin += self.orientation * pos.origin;
        self.orientation = set_rotate(
            self.origin,
            self.orientation,
            self.orientation * pos.orientation,
        );
    }

    /// Undo [`Frame::combine`] with the same `pos`: turn back first, then
    /// move back along the restored axes.
    pub fn subtract1(&mut self, pos: &Frame) {
        self.orientation = set_rotate(
            self.origin,
            self.orientation,
            self.orientation * pos.orientation.conjugate(),
        );
        self.origin -= self.orientation * pos.origin;
    }
}

/// Accept `candidate` as the new orientation only if it normalises to a
/// valid unit rotation and `frame_origin` is not NaN; otherwise keep
/// `previous`. Normalisation is done in double precision, so even a tiny
/// non-zero quaternion normalises.
#[must_use]
pub fn set_rotate(frame_origin: Vec3, previous: Quat, candidate: Quat) -> Quat {
    let (x, y, z, w) = (
        f64::from(candidate.x),
        f64::from(candidate.y),
        f64::from(candidate.z),
        f64::from(candidate.w),
    );
    let length_squared = w * w + x * x + y * y + z * z;
    let inverse_length = 1.0 / length_squared.sqrt();
    let normalized = Quat::from_xyzw(
        (x * inverse_length) as f32,
        (y * inverse_length) as f32,
        (z * inverse_length) as f32,
        (w * inverse_length) as f32,
    );
    if frame_origin.is_nan() || normalized.is_nan() {
        return previous;
    }
    let normalized_length_squared = normalized.length_squared();
    if !normalized_length_squared.is_nan()
        && (normalized_length_squared - 1.0).abs() < F_EPSILON * 5.0
    {
        normalized
    } else {
        previous
    }
}

/// Convert a dat vector.
#[must_use]
pub fn vec3_from_dat(v: ac_dat::types::Vector3) -> Vec3 {
    Vec3::new(v.x, v.y, v.z)
}

/// Convert a dat quaternion (stored w first).
#[must_use]
pub fn quat_from_dat(q: ac_dat::types::Quaternion) -> Quat {
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

#[cfg(test)]
mod tests {
    //! Golden cases from OpenAC `CSequencePhysicsTests` (frame half) and
    //! `MotionDeltaFrameTests` (set_rotate edge cases).
    use super::*;
    use std::f32::consts::PI;

    fn assert_vec(expected: Vec3, actual: Vec3) {
        assert!(
            (expected - actual).length() < 1e-5,
            "expected {expected}, got {actual}"
        );
    }

    #[test]
    fn grotate_composes_in_global_space() {
        // Already turned 90° about z; a global 90° turn about x then maps
        // local +x (now +y) to +z.
        let mut frame = Frame::new(Vec3::ZERO, Quat::from_axis_angle(Vec3::Z, PI / 2.0));
        frame.grotate(Vec3::new(PI / 2.0, 0.0, 0.0));
        assert_vec(Vec3::Z, frame.orientation * Vec3::X);
    }

    #[test]
    fn rotate_maps_the_local_vector_through_the_orientation() {
        let q0 = Quat::from_axis_angle(Vec3::Z, PI / 2.0);
        let mut via_local = Frame::new(Vec3::ZERO, q0);
        via_local.rotate(Vec3::new(PI / 2.0, 0.0, 0.0));
        let mut via_global = Frame::new(Vec3::ZERO, q0);
        via_global.grotate(Vec3::new(0.0, PI / 2.0, 0.0));
        assert_vec(
            via_global.orientation * Vec3::X,
            via_local.orientation * Vec3::X,
        );
        assert_vec(
            via_global.orientation * Vec3::Z,
            via_local.orientation * Vec3::Z,
        );
    }

    #[test]
    fn epsilon_is_the_retail_float() {
        // 0.000199999995f in the original source.
        assert_eq!(F_EPSILON.to_bits(), 0x3951_B717);
    }

    #[test]
    fn tiny_rotation_is_skipped() {
        let mut frame = Frame::IDENTITY;
        frame.grotate(Vec3::new(1e-5, 0.0, 0.0));
        assert_eq!(frame.orientation, Quat::IDENTITY);
    }

    #[test]
    fn combine_then_subtract_round_trips() {
        let mut frame = Frame::new(
            Vec3::new(10.0, 20.0, 30.0),
            Quat::from_axis_angle(Vec3::Z, PI / 2.0),
        );
        let start = frame;
        let step = Frame::new(
            Vec3::new(2.0, 0.0, 1.0),
            Quat::from_axis_angle(Vec3::Z, PI / 4.0),
        );
        frame.combine(&step);
        // Translation goes through the old orientation: local +x is world +y.
        assert_vec(Vec3::new(10.0, 22.0, 31.0), frame.origin);
        frame.subtract1(&step);
        assert_vec(start.origin, frame.origin);
        assert!(frame.orientation.dot(start.orientation).abs() > 0.99999);
    }

    #[test]
    fn invalid_candidates_keep_the_previous_rotation() {
        let previous = Quat::from_axis_angle(Vec3::Y, 0.35);
        for invalid in [
            Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            Quat::from_xyzw(f32::NAN, 0.0, 0.0, 1.0),
            Quat::from_xyzw(f32::INFINITY, 0.0, 0.0, 1.0),
        ] {
            assert_eq!(set_rotate(Vec3::ZERO, previous, invalid), previous);
        }
        assert_eq!(
            set_rotate(Vec3::NAN, previous, Quat::IDENTITY),
            previous,
            "a NaN origin also refuses the rotation"
        );
    }

    #[test]
    fn near_zero_quaternion_normalizes_in_double_precision() {
        let q = set_rotate(
            Vec3::ZERO,
            Quat::IDENTITY,
            Quat::from_xyzw(1e-30, 0.0, 0.0, 0.0),
        );
        let expected = Quat::from_axis_angle(Vec3::X, PI);
        assert!(q.dot(expected).abs() > 0.99999);
    }
}
