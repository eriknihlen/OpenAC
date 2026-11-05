//! Prepared BSP trees for collision.
//!
//! A binary space partition tree cuts space with planes: each splitting
//! node has a plane and up to two children, one for the space in front of
//! the plane ("positive") and one behind it. A *physics* tree (of a model or
//! a room) keeps, at each leaf, the collision polygons in that piece of
//! space, and at every node a bounding sphere of everything below it, so a
//! query can skip whole branches. A *cell* tree (of a room) only answers
//! "is this point inside the room".
//!
//! The trees keep the dat's layout: nodes in a flat list, the root first,
//! children by index. Every child index points further down the list, which
//! [`PhysicsBsp::new`] and [`CellBsp::new`] check, so a walk always ends.

use std::collections::HashMap;
use std::fmt;

use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;

/// Why a tree was refused.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BspError {
    /// Node `node` names child `child`, which is not after it in the list or
    /// not in the list at all.
    BadChild {
        /// The parent node.
        node: usize,
        /// The child index it names.
        child: usize,
    },
}

impl fmt::Display for BspError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BadChild { node, child } => {
                write!(
                    f,
                    "BSP node {node} names child {child}, which is not below it"
                )
            }
        }
    }
}

impl std::error::Error for BspError {}

/// A node of a [`PhysicsBsp`].
#[derive(Debug, Clone, PartialEq)]
pub enum PhysicsBspNode {
    /// A splitting node.
    Split {
        /// The splitting plane.
        plane: Plane,
        /// Bounds of everything below.
        sphere: Sphere,
        /// Child in front of the plane.
        positive: Option<usize>,
        /// Child behind the plane.
        negative: Option<usize>,
    },
    /// A leaf.
    Leaf {
        /// Bounds of the leaf's polygons.
        sphere: Sphere,
        /// True when the leaf's space is inside solid matter.
        solid: bool,
        /// Ids of the collision polygons in the leaf.
        polygons: Vec<u16>,
    },
}

impl PhysicsBspNode {
    pub(crate) fn sphere(&self) -> &Sphere {
        match self {
            Self::Split { sphere, .. } | Self::Leaf { sphere, .. } => sphere,
        }
    }

    fn children(&self) -> impl Iterator<Item = usize> {
        match self {
            Self::Split {
                positive, negative, ..
            } => [*positive, *negative],
            Self::Leaf { .. } => [None, None],
        }
        .into_iter()
        .flatten()
    }
}

/// The collision tree of a model or a room, with its polygons.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PhysicsBsp {
    pub(crate) nodes: Vec<PhysicsBspNode>,
    pub(crate) polygons: HashMap<u16, ResolvedPolygon>,
}

impl PhysicsBsp {
    /// A tree from its nodes (root first) and polygons. A leaf may name a
    /// polygon that is missing; queries skip it.
    pub fn new(
        nodes: Vec<PhysicsBspNode>,
        polygons: HashMap<u16, ResolvedPolygon>,
    ) -> Result<Self, BspError> {
        check_children(nodes.iter().map(|n| n.children()))?;
        Ok(Self { nodes, polygons })
    }

    /// The nodes, root first.
    #[must_use]
    pub fn nodes(&self) -> &[PhysicsBspNode] {
        &self.nodes
    }

    /// The polygons by id.
    #[must_use]
    pub fn polygons(&self) -> &HashMap<u16, ResolvedPolygon> {
        &self.polygons
    }

    /// The polygon with `id`.
    #[must_use]
    pub fn polygon(&self, id: u16) -> Option<&ResolvedPolygon> {
        self.polygons.get(&id)
    }

    /// True when the tree has no nodes (every query then finds nothing).
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }
}

/// A node of a [`CellBsp`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum CellBspNode {
    /// A splitting node.
    Split {
        /// The splitting plane.
        plane: Plane,
        /// Child in front of the plane.
        positive: Option<usize>,
        /// Child behind the plane.
        negative: Option<usize>,
    },
    /// A leaf.
    Leaf,
}

/// The containment tree of a room: the room is the space in front of every
/// plane on the path down the positive children.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CellBsp {
    pub(crate) nodes: Vec<CellBspNode>,
}

impl CellBsp {
    /// A tree from its nodes, root first.
    pub fn new(nodes: Vec<CellBspNode>) -> Result<Self, BspError> {
        check_children(nodes.iter().map(|n| {
            match *n {
                CellBspNode::Split {
                    positive, negative, ..
                } => [positive, negative],
                CellBspNode::Leaf => [None, None],
            }
            .into_iter()
            .flatten()
        }))?;
        Ok(Self { nodes })
    }

    /// The nodes, root first.
    #[must_use]
    pub fn nodes(&self) -> &[CellBspNode] {
        &self.nodes
    }
}

/// Every child must come after its parent and be in the list.
fn check_children<I: Iterator<Item = usize>>(
    children: impl ExactSizeIterator<Item = I>,
) -> Result<(), BspError> {
    let len = children.len();
    for (node, kids) in children.enumerate() {
        for child in kids {
            if child <= node || child >= len {
                return Err(BspError::BadChild { node, child });
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use glam::Vec3;

    #[test]
    fn children_must_point_down_the_list() {
        let leaf = PhysicsBspNode::Leaf {
            sphere: Sphere::default(),
            solid: false,
            polygons: vec![],
        };
        let split = |positive, negative| PhysicsBspNode::Split {
            plane: Plane::new(Vec3::Z, 0.0),
            sphere: Sphere::default(),
            positive,
            negative,
        };
        assert!(PhysicsBsp::new(vec![split(Some(1), None), leaf.clone()], HashMap::new()).is_ok());
        assert_eq!(
            PhysicsBsp::new(vec![split(Some(0), None)], HashMap::new()),
            Err(BspError::BadChild { node: 0, child: 0 })
        );
        assert_eq!(
            PhysicsBsp::new(vec![split(None, Some(5)), leaf], HashMap::new()),
            Err(BspError::BadChild { node: 0, child: 5 })
        );
        assert!(
            CellBsp::new(vec![
                CellBspNode::Split {
                    plane: Plane::default(),
                    positive: Some(1),
                    negative: None
                },
                CellBspNode::Leaf
            ])
            .is_ok()
        );
        assert!(
            CellBsp::new(vec![CellBspNode::Split {
                plane: Plane::default(),
                positive: Some(1),
                negative: None
            }])
            .is_err()
        );
    }
}
