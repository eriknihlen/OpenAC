//! Portals: the openings between cells.

use glam::Vec3;

/// A cell's portal to another cell, as the cell's dat entry lists it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PortalInfo {
    /// The low 16 bits of the cell on the other side.
    pub other_cell_id: u16,
    /// The portal polygon's id in the cell's structure.
    pub polygon_id: u16,
    /// Portal flags.
    pub flags: u16,
}

impl PortalInfo {
    /// A portal entry.
    #[must_use]
    pub const fn new(other_cell_id: u16, polygon_id: u16, flags: u16) -> Self {
        Self {
            other_cell_id,
            polygon_id,
            flags,
        }
    }

    /// Which side of the portal polygon faces into this cell: true (front)
    /// unless flag bit `0x2` is set.
    #[must_use]
    pub fn portal_side(&self) -> bool {
        self.flags & 2 == 0
    }
}

/// A portal polygon in world space, reduced to a plane and a bounding disc.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PortalPlane {
    /// Unit normal (from the first three vertices' winding).
    pub normal: Vec3,
    /// Plane offset: `normal · p + d = 0` on the plane.
    pub d: f32,
    /// The cell on the other side.
    pub target_cell_id: u32,
    /// The cell that owns the portal.
    pub owner_cell_id: u32,
    /// Portal flags.
    pub flags: u16,
    /// Mean of the vertices.
    pub centroid: Vec3,
    /// Largest distance from the centroid to a vertex, metres.
    pub radius: f32,
}

impl PortalPlane {
    /// The plane through a portal polygon's vertices; `None` with fewer
    /// than three.
    #[must_use]
    pub fn from_vertices(
        vertices: &[Vec3],
        target_cell_id: u32,
        owner_cell_id: u32,
        flags: u16,
    ) -> Option<Self> {
        if vertices.len() < 3 {
            return None;
        }
        let normal = crate::geometry::normalize(
            (vertices[1] - vertices[0]).cross(vertices[2] - vertices[0]),
        );
        let d = -normal.dot(vertices[0]);
        let centroid = vertices.iter().copied().sum::<Vec3>() / vertices.len() as f32;
        let radius = vertices
            .iter()
            .map(|v| centroid.distance(*v))
            .fold(0.0f32, |m, r| if r > m { r } else { m });
        Some(Self {
            normal,
            d,
            target_cell_id,
            owner_cell_id,
            flags,
            centroid,
            radius,
        })
    }

    /// Whether moving from `old_pos` to `new_pos` crosses the plane near
    /// the portal: the nearer of the two positions is within the portal's
    /// radius (measured across the ground, per axis), and the two lie
    /// strictly on opposite sides.
    #[must_use]
    pub fn is_crossing(&self, old_pos: Vec3, new_pos: Vec3) -> bool {
        let dx = (old_pos.x - self.centroid.x)
            .abs()
            .min((new_pos.x - self.centroid.x).abs());
        let dy = (old_pos.y - self.centroid.y)
            .abs()
            .min((new_pos.y - self.centroid.y).abs());
        if (dx * dx + dy * dy).sqrt() > self.radius {
            return false;
        }
        let old_dist = self.normal.dot(old_pos) + self.d;
        let new_dist = self.normal.dot(new_pos) + self.d;
        old_dist * new_dist < 0.0
    }
}

#[cfg(test)]
mod tests {
    //! Ported from OpenAC `tests/AcDream.Core.Tests/Physics/PortalPlaneTests.cs`
    //! and `PortalInfoTests.cs`.

    use super::*;

    fn xy_plane() -> PortalPlane {
        PortalPlane::from_vertices(&[Vec3::ZERO, Vec3::X, Vec3::Y], 42, 7, 0).unwrap()
    }

    #[test]
    fn normal_from_the_vertices() {
        let p = xy_plane();
        assert!(p.normal.x.abs() < 1e-5 && p.normal.y.abs() < 1e-5);
        assert!((p.normal.z.abs() - 1.0).abs() < 1e-5);
        assert!(PortalPlane::from_vertices(&[Vec3::ZERO, Vec3::X], 0, 0, 0).is_none());
    }

    #[test]
    fn crossing_opposite_sides() {
        let p = xy_plane();
        let above = Vec3::new(0.0, 0.0, 1.0);
        let below = Vec3::new(0.0, 0.0, -1.0);
        assert!(p.is_crossing(above, below));
        assert!(p.is_crossing(below, above));
    }

    #[test]
    fn not_crossing_same_side_or_from_the_plane() {
        let p = xy_plane();
        assert!(!p.is_crossing(Vec3::new(0.0, 0.0, 1.0), Vec3::new(0.0, 0.0, 2.0)));
        assert!(!p.is_crossing(Vec3::new(0.5, 0.5, 0.0), Vec3::new(0.5, 0.5, 1.0)));
    }

    #[test]
    fn portal_side_follows_bit_2_only() {
        assert!(PortalInfo::new(0x0101, 5, 0).portal_side());
        assert!(!PortalInfo::new(0x0101, 5, 2).portal_side());
        assert!(PortalInfo::new(0x0101, 5, 0xFF & !2).portal_side());
        assert_eq!(PortalInfo::new(0xFFFF, 5, 0).other_cell_id, 0xFFFF);
    }
}
