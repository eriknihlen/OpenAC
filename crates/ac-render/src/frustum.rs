//! View-frustum culling: skip what the camera cannot see.
//!
//! The frustum is the truncated pyramid the camera sees, bounded by six
//! planes (left, right, bottom, top, near, far). Each plane is stored as
//! `(nx, ny, nz, d)` with a unit normal pointing into the frustum: a point
//! `p` is on the visible side when `n . p + d >= 0`.

use glam::{Mat4, Vec3, Vec4};

/// The six planes of a view-projection matrix (depth 0..1).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FrustumPlanes {
    /// Left plane.
    pub left: Vec4,
    /// Right plane.
    pub right: Vec4,
    /// Bottom plane.
    pub bottom: Vec4,
    /// Top plane.
    pub top: Vec4,
    /// Near plane.
    pub near: Vec4,
    /// Far plane.
    pub far: Vec4,
}

impl FrustumPlanes {
    /// Extract the planes from `projection * view`. With depth running 0..1
    /// the near plane is the third row alone.
    #[must_use]
    pub fn from_view_projection(vp: Mat4) -> Self {
        let r0 = vp.row(0);
        let r1 = vp.row(1);
        let r2 = vp.row(2);
        let r3 = vp.row(3);
        Self {
            left: normalize_plane(r3 + r0),
            right: normalize_plane(r3 - r0),
            bottom: normalize_plane(r3 + r1),
            top: normalize_plane(r3 - r1),
            near: normalize_plane(r2),
            far: normalize_plane(r3 - r2),
        }
    }

    fn all(&self) -> [Vec4; 6] {
        [
            self.left,
            self.right,
            self.bottom,
            self.top,
            self.near,
            self.far,
        ]
    }

    /// True unless the box lies wholly outside one plane. A box that only
    /// crosses a frustum corner can pass (the test is conservative).
    #[must_use]
    pub fn is_aabb_visible(&self, min: Vec3, max: Vec3) -> bool {
        self.all().iter().all(|plane| {
            // The box corner farthest along the plane normal.
            let p = Vec3::new(
                if plane.x >= 0.0 { max.x } else { min.x },
                if plane.y >= 0.0 { max.y } else { min.y },
                if plane.z >= 0.0 { max.z } else { min.z },
            );
            plane.x * p.x + plane.y * p.y + plane.z * p.z + plane.w >= 0.0
        })
    }
}

fn normalize_plane(plane: Vec4) -> Vec4 {
    plane / plane.truncate().length()
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden cases: OpenAC tests/AcDream.Core.Tests/Rendering/FrustumCullerTests.cs
    // (System.Numerics CreateLookAt / CreatePerspectiveFieldOfView are the
    // right-handed, depth 0..1 matrices glam builds with `_rh`).

    fn looking_down_minus_z(fov: f32, aspect: f32, near: f32, far: f32) -> FrustumPlanes {
        let view = glam::camera::rh::view::look_at_mat4(Vec3::ZERO, Vec3::NEG_Z, Vec3::Y);
        let proj = glam::camera::rh::proj::directx::perspective(fov, aspect, near, far);
        FrustumPlanes::from_view_projection(proj * view)
    }

    #[test]
    fn orthographic_box_inside_is_visible() {
        let p = FrustumPlanes::from_view_projection(glam::camera::rh::proj::directx::orthographic(
            -1.0, 1.0, -1.0, 1.0, 0.1, 100.0,
        ));
        assert!(p.is_aabb_visible(Vec3::new(-0.5, -0.5, -50.0), Vec3::new(0.5, 0.5, -1.0)));
    }

    #[test]
    fn box_in_front_is_visible_behind_is_not() {
        let p = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 1.0, 1.0, 1000.0);
        assert!(p.is_aabb_visible(
            Vec3::new(-10.0, -10.0, -100.0),
            Vec3::new(10.0, 10.0, -10.0)
        ));
        assert!(!p.is_aabb_visible(Vec3::new(-10.0, -10.0, 10.0), Vec3::new(10.0, 10.0, 100.0)));
    }

    #[test]
    fn box_far_left_is_not_visible() {
        let p = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 1.0, 1.0, 1000.0);
        assert!(!p.is_aabb_visible(
            Vec3::new(-1000.0, -10.0, -50.0),
            Vec3::new(-500.0, 10.0, -10.0)
        ));
    }

    #[test]
    fn box_straddling_the_near_plane_is_visible() {
        let p = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 1.0, 1.0, 1000.0);
        assert!(p.is_aabb_visible(Vec3::new(-1.0, -1.0, -2.0), Vec3::new(1.0, 1.0, 0.5)));
    }

    #[test]
    fn the_near_plane_sits_at_the_camera_near_distance() {
        for (near, far) in [(0.1, 1000.0), (1.0, 1000.0), (1.0, 100.0), (2.5, 5000.0)] {
            let p = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 16.0 / 9.0, near, far);
            let n = p.near;
            assert!((n.truncate().length() - 1.0).abs() < 1e-4);
            assert!(n.x.abs() < 1e-4 && n.y.abs() < 1e-4 && (n.z + 1.0).abs() < 1e-4);
            assert!((-n.w - near).abs() < 1e-3, "{near}: {}", n.w);
            let eps = near * 0.01;
            let at =
                |z: f32| p.is_aabb_visible(Vec3::new(-0.01, -0.01, z), Vec3::new(0.01, 0.01, z));
            assert!(at(-(near + eps)));
            assert!(!at(-(near - eps)));
        }
    }

    #[test]
    fn the_far_plane_sits_at_the_far_distance() {
        let p = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 16.0 / 9.0, 1.0, 100.0);
        assert!((p.far.truncate().length() - 1.0).abs() < 1e-4);
        assert!((p.far.z - 1.0).abs() < 1e-4);
        assert!((p.far.w - 100.0).abs() < 1e-3);
        let q = looking_down_minus_z(std::f32::consts::FRAC_PI_3, 1.0, 1.0, 100.0);
        assert!(!q.is_aabb_visible(
            Vec3::new(-10.0, -10.0, -500.0),
            Vec3::new(10.0, 10.0, -200.0)
        ));
    }

    #[test]
    fn landblock_ahead_is_visible_and_behind_is_not() {
        let view = glam::camera::rh::view::look_at_mat4(
            Vec3::new(96.0, 96.0, 150.0),
            Vec3::new(96.0, 288.0, 100.0),
            Vec3::Z,
        );
        let proj = glam::camera::rh::proj::directx::perspective(
            std::f32::consts::FRAC_PI_3,
            16.0 / 9.0,
            1.0,
            5000.0,
        );
        let p = FrustumPlanes::from_view_projection(proj * view);
        assert!(p.is_aabb_visible(Vec3::new(0.0, 192.0, 50.0), Vec3::new(192.0, 384.0, 200.0)));
        assert!(!p.is_aabb_visible(
            Vec3::new(0.0, -384.0, 50.0),
            Vec3::new(192.0, -192.0, 200.0)
        ));
    }
}
