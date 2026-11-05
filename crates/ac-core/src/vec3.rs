//! The few `[f32; 3]` vector operations the pure rules need: x, y, z in
//! meters (z up). Each is computed in single precision, component by
//! component, in the order written.

pub(crate) type Vec3 = [f32; 3];

pub(crate) fn add(a: Vec3, b: Vec3) -> Vec3 {
    [a[0] + b[0], a[1] + b[1], a[2] + b[2]]
}

pub(crate) fn sub(a: Vec3, b: Vec3) -> Vec3 {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}

pub(crate) fn neg(a: Vec3) -> Vec3 {
    [-a[0], -a[1], -a[2]]
}

/// The vector perpendicular to `a` and `b` (right-handed).
pub(crate) fn cross(a: Vec3, b: Vec3) -> Vec3 {
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]
}

pub(crate) fn length_squared(a: Vec3) -> f32 {
    a[0] * a[0] + a[1] * a[1] + a[2] * a[2]
}

/// `a` scaled to length 1. A zero vector gives NaN components, so callers
/// check the length first where zero can occur.
pub(crate) fn normalize(a: Vec3) -> Vec3 {
    let len = length_squared(a).sqrt();
    [a[0] / len, a[1] / len, a[2] / len]
}
