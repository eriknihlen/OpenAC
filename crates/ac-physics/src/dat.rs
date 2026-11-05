//! Building the prepared collision data from dat objects.
//!
//! The only module that reads `ac-dat` types: trees keep the dat's node
//! order and indices, polygons are resolved once.

use std::collections::HashMap;

use ac_dat::objects::{BspNode, BspTree, CellStruct, LandBlock, Polygon, VertexArray};
use ac_dat::types::{Quaternion, Sphere as DatSphere, Vector3};
use glam::{Quat, Vec3};

use crate::bsp::{BspError, CellBsp, CellBspNode, PhysicsBsp, PhysicsBspNode};
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::terrain::TerrainSurface;

/// A dat vector.
#[must_use]
pub fn vec3(v: Vector3) -> Vec3 {
    Vec3::new(v.x, v.y, v.z)
}

/// A dat quaternion.
#[must_use]
pub fn quat(q: Quaternion) -> Quat {
    Quat::from_xyzw(q.x, q.y, q.z, q.w)
}

fn sphere(s: &DatSphere) -> Sphere {
    Sphere::new(vec3(s.origin), s.radius)
}

fn plane(p: &ac_dat::objects::Plane) -> Plane {
    Plane::new(vec3(p.normal), p.distance)
}

/// Vertex positions by id.
#[must_use]
pub fn vertex_positions(vertices: &VertexArray) -> HashMap<u16, Vec3> {
    vertices
        .vertices
        .iter()
        .map(|(id, v)| (*id, vec3(v.origin)))
        .collect()
}

/// Resolves every polygon that takes part in collision (see
/// [`ResolvedPolygon::resolve`]).
#[must_use]
pub fn resolve_polygons(
    polygons: &[(u16, Polygon)],
    vertices: &VertexArray,
) -> HashMap<u16, ResolvedPolygon> {
    let positions = vertex_positions(vertices);
    polygons
        .iter()
        .filter_map(|(id, p)| {
            ResolvedPolygon::resolve(
                *id,
                p.vertex_ids.iter().copied(),
                &positions,
                p.sides_type.0,
            )
            .map(|r| (*id, r))
        })
        .collect()
}

impl PhysicsBsp {
    /// A physics tree and its polygons from the dat. A portal node (never
    /// stored in a physics tree) becomes a split with both children.
    pub fn from_dat(
        tree: &BspTree,
        polygons: &[(u16, Polygon)],
        vertices: &VertexArray,
    ) -> Result<Self, BspError> {
        let nodes = tree
            .nodes
            .iter()
            .map(|n| match n {
                BspNode::Leaf(l) => PhysicsBspNode::Leaf {
                    sphere: sphere(&l.sphere),
                    solid: l.solid != 0,
                    polygons: l.polygons.clone(),
                },
                BspNode::Split(s) => PhysicsBspNode::Split {
                    plane: plane(&s.plane),
                    sphere: sphere(&s.sphere),
                    positive: s.positive,
                    negative: s.negative,
                },
                BspNode::Portal(p) => PhysicsBspNode::Split {
                    plane: plane(&p.plane),
                    sphere: sphere(&p.sphere),
                    positive: Some(p.positive),
                    negative: Some(p.negative),
                },
            })
            .collect();
        Self::new(nodes, resolve_polygons(polygons, vertices))
    }

    /// The physics tree of an interior cell structure.
    pub fn from_cell_struct(cell: &CellStruct) -> Result<Self, BspError> {
        Self::from_dat(
            &cell.physics_bsp,
            &cell.physics_polygons,
            &cell.vertex_array,
        )
    }
}

impl CellBsp {
    /// A containment tree from the dat.
    pub fn from_dat(tree: &BspTree) -> Result<Self, BspError> {
        let nodes = tree
            .nodes
            .iter()
            .map(|n| match n {
                BspNode::Leaf(_) => CellBspNode::Leaf,
                BspNode::Split(s) => CellBspNode::Split {
                    plane: plane(&s.plane),
                    positive: s.positive,
                    negative: s.negative,
                },
                BspNode::Portal(p) => CellBspNode::Split {
                    plane: plane(&p.plane),
                    positive: Some(p.positive),
                    negative: Some(p.negative),
                },
            })
            .collect();
        Self::new(nodes)
    }
}

impl TerrainSurface {
    /// The ground of a landblock, with the region's height table. The
    /// terrain type is the low byte of each corner's terrain word.
    #[must_use]
    pub fn from_landblock(block: &LandBlock, height_table: &[f32; 256]) -> Self {
        let terrain: [u8; 81] = std::array::from_fn(|i| (block.terrain[i].0 & 0xFF) as u8);
        Self::new(
            &block.height,
            height_table,
            (block.id >> 24) & 0xFF,
            (block.id >> 16) & 0xFF,
            Some(&terrain),
        )
    }
}
