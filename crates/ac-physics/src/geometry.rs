//! Spheres and planes.

use glam::{Quat, Vec3, Vec4};

/// A vector divided by its length. The reference port divides (rather
/// than multiplying by the reciprocal, as `Vec3::normalize` does); the two
/// differ in the last bit often enough to move exact results.
#[must_use]
pub fn normalize(v: Vec3) -> Vec3 {
    v / v.length()
}

/// A quaternion divided by its length (see [`normalize`]).
#[must_use]
pub fn normalize_quat(q: Quat) -> Quat {
    Quat::from_vec4(Vec4::from(q) / q.length())
}

/// A sphere: centre and radius, metres.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Sphere {
    /// Centre.
    pub center: Vec3,
    /// Radius.
    pub radius: f32,
}

impl Sphere {
    /// A sphere at `center` with `radius`.
    #[must_use]
    pub const fn new(center: Vec3, radius: f32) -> Self {
        Self { center, radius }
    }

    /// True when the two spheres overlap (touching does not count).
    #[must_use]
    pub fn intersects(&self, other: &Sphere) -> bool {
        let d = other.center - self.center;
        let r = other.radius + self.radius;
        d.length_squared() < r * r
    }
}

/// A plane: the points `p` with `normal · p + d = 0`. The normal points to
/// the plane's front.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Plane {
    /// Normal; unit length for every plane built from geometry.
    pub normal: Vec3,
    /// Offset term.
    pub d: f32,
}

impl Plane {
    /// A plane from its normal and offset term.
    #[must_use]
    pub const fn new(normal: Vec3, d: f32) -> Self {
        Self { normal, d }
    }

    /// Signed distance of `point` from the plane (positive in front).
    #[must_use]
    pub fn distance(&self, point: Vec3) -> f32 {
        self.normal.dot(point) + self.d
    }
}
