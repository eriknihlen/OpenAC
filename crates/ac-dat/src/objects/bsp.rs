//! Binary space partition trees, as stored inside models and cell
//! structures.
//!
//! A BSP tree splits space with planes: each splitting node has a plane and
//! up to two children, one for the space in front of the plane (the
//! "positive" side) and one behind it. Collision walks the physics tree;
//! drawing walks the drawing tree; interiors use the cell tree to find which
//! cell a point is in.
//!
//! The file stores the tree depth first: a node's four-character tag, its
//! own fields, then its children (positive first), then the fields that
//! follow the children. Which fields a node carries depends on the kind of
//! tree, so the reader is told the kind. Nodes are kept in a flat list in
//! file order (the root is index 0) with children referenced by index, and
//! the reader walks the file with an explicit stack, so a deep tree cannot
//! exhaust the call stack.

use ac_bits::Reader;

use crate::error::DecodeError;
use crate::types::{Sphere, Vector3};

type Result<T> = core::result::Result<T, DecodeError>;

/// Which tree is being read; the stored fields differ by kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BspTreeKind {
    /// Collision tree of a model: leaves hold polygons and a solid flag,
    /// every node holds a bounding sphere.
    Physics,
    /// Drawing tree of a model: splitting and portal nodes hold polygons
    /// and a bounding sphere; leaves hold only their index.
    Drawing,
    /// Cell tree of an interior structure: planes, children and leaf
    /// indices only.
    Cell,
}

/// A plane: the points `p` with `normal · p + distance = 0`.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Plane {
    /// Unit normal; it points to the positive side.
    pub normal: Vector3,
    /// Signed offset along the normal.
    pub distance: f32,
}

impl Plane {
    pub(crate) fn unpack(r: &mut Reader<'_>) -> Result<Self> {
        Ok(Self {
            normal: Vector3::unpack(r)?,
            distance: r.f32()?,
        })
    }
}

/// A node's four-character tag, stored as a little-endian `u32` whose
/// big-endian bytes spell the tag (`LEAF`, `PORT`, `BPIN`, ...).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct BspTag(pub u32);

impl BspTag {
    /// A leaf.
    pub const LEAF: Self = Self::from_text(*b"LEAF");
    /// A portal node. Any tree may hold one; the retail dats have them only
    /// in the drawing trees of models (buildings).
    pub const PORTAL: Self = Self::from_text(*b"PORT");

    /// The tag whose big-endian bytes are `text`.
    #[must_use]
    pub const fn from_text(text: [u8; 4]) -> Self {
        Self(u32::from_be_bytes(text))
    }

    /// The four characters of the tag.
    #[must_use]
    pub const fn text(self) -> [u8; 4] {
        self.0.to_be_bytes()
    }

    /// True when a splitting node with this tag stores a positive child.
    /// Only `BPnn`, `BPIn`, `BPIN` and `BPnN` do.
    #[must_use]
    pub fn has_positive(self) -> bool {
        matches!(&self.text(), b"BPnn" | b"BPIn" | b"BPIN" | b"BPnN")
    }

    /// True when a splitting node with this tag stores a negative child.
    /// Only `BpIN`, `BpnN`, `BPIN` and `BPnN` do.
    #[must_use]
    pub fn has_negative(self) -> bool {
        matches!(&self.text(), b"BpIN" | b"BpnN" | b"BPIN" | b"BPnN")
    }
}

/// One side of a portal node: a portal polygon and its index in the
/// owner's portal list.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PortalRef {
    /// Polygon id of the portal.
    pub poly_id: u16,
    /// Index of the portal in the owner's portal list.
    pub portal_index: u16,
}

/// A node of a [`BspTree`]. Child links are indices into [`BspTree::nodes`].
#[derive(Debug, Clone, PartialEq)]
pub enum BspNode {
    /// A splitting node. Any tag other than `LEAF` and `PORT` is one; the
    /// tag decides which children are stored (tags such as `BPOL` and
    /// `BpIn` store none).
    Split(BspSplit),
    /// A portal node: a splitting node that also lists portals.
    Portal(BspPortal),
    /// A leaf.
    Leaf(BspLeaf),
}

impl BspNode {
    /// Indices of the node's children, positive side first.
    pub fn children(&self) -> impl Iterator<Item = usize> {
        let (positive, negative) = match self {
            Self::Split(s) => (s.positive, s.negative),
            Self::Portal(p) => (Some(p.positive), Some(p.negative)),
            Self::Leaf(_) => (None, None),
        };
        positive.into_iter().chain(negative)
    }
}

/// A splitting node.
#[derive(Debug, Clone, PartialEq)]
pub struct BspSplit {
    /// The node's tag.
    pub tag: BspTag,
    /// The splitting plane.
    pub plane: Plane,
    /// Child on the positive side of the plane.
    pub positive: Option<usize>,
    /// Child on the negative side.
    pub negative: Option<usize>,
    /// Bounding sphere (physics and drawing trees; zero in cell trees).
    pub sphere: Sphere,
    /// Polygon ids (drawing trees only).
    pub polygons: Vec<u16>,
}

/// A portal node. It always has both children.
#[derive(Debug, Clone, PartialEq)]
pub struct BspPortal {
    /// The splitting plane.
    pub plane: Plane,
    /// Child on the positive side of the plane.
    pub positive: usize,
    /// Child on the negative side.
    pub negative: usize,
    /// Bounding sphere (drawing trees only).
    pub sphere: Sphere,
    /// Polygon ids (drawing trees only).
    pub polygons: Vec<u16>,
    /// Portals (drawing trees only).
    pub portals: Vec<PortalRef>,
}

/// A leaf.
#[derive(Debug, Clone, PartialEq)]
pub struct BspLeaf {
    /// The leaf's index.
    pub index: i32,
    /// Non-zero when the leaf's space is inside the solid (physics only).
    pub solid: i32,
    /// Bounding sphere (physics only).
    pub sphere: Sphere,
    /// Polygon ids (physics only).
    pub polygons: Vec<u16>,
}

/// A BSP tree: nodes in file order, the root first.
#[derive(Debug, Clone, PartialEq)]
pub struct BspTree {
    /// The kind of tree, which decides the fields each node carries.
    pub kind: BspTreeKind,
    /// Every node; index 0 is the root.
    pub nodes: Vec<BspNode>,
}

/// Work left for the reader: a node still to read (and the link to point
/// at it), or the trailing fields of a node whose children are done.
enum Task {
    Node(Option<(usize, Side)>),
    Finish(usize),
}

#[derive(Clone, Copy)]
enum Side {
    Positive,
    Negative,
}

impl BspTree {
    /// Read a tree of the given kind.
    pub fn unpack(r: &mut Reader<'_>, kind: BspTreeKind) -> Result<Self> {
        let mut nodes = Vec::new();
        let mut tasks = vec![Task::Node(None)];
        while let Some(task) = tasks.pop() {
            match task {
                Task::Node(parent) => {
                    let index = nodes.len();
                    if let Some((parent, side)) = parent {
                        link(&mut nodes, parent, side, index);
                    }
                    let tag = BspTag(r.u32()?);
                    if tag == BspTag::LEAF {
                        nodes.push(BspNode::Leaf(read_leaf(r, kind)?));
                    } else if tag == BspTag::PORTAL {
                        nodes.push(BspNode::Portal(BspPortal {
                            plane: Plane::unpack(r)?,
                            positive: 0,
                            negative: 0,
                            sphere: Sphere::default(),
                            polygons: Vec::new(),
                            portals: Vec::new(),
                        }));
                        tasks.push(Task::Finish(index));
                        tasks.push(Task::Node(Some((index, Side::Negative))));
                        tasks.push(Task::Node(Some((index, Side::Positive))));
                    } else {
                        nodes.push(BspNode::Split(BspSplit {
                            tag,
                            plane: Plane::unpack(r)?,
                            positive: None,
                            negative: None,
                            sphere: Sphere::default(),
                            polygons: Vec::new(),
                        }));
                        tasks.push(Task::Finish(index));
                        if tag.has_negative() {
                            tasks.push(Task::Node(Some((index, Side::Negative))));
                        }
                        if tag.has_positive() {
                            tasks.push(Task::Node(Some((index, Side::Positive))));
                        }
                    }
                }
                Task::Finish(index) => {
                    if let Some(node) = nodes.get_mut(index) {
                        finish(r, kind, node)?;
                    }
                }
            }
        }
        Ok(Self { kind, nodes })
    }

    /// The root node.
    #[must_use]
    pub fn root(&self) -> Option<&BspNode> {
        self.nodes.first()
    }
}

/// Point a parent's child link at `child`.
fn link(nodes: &mut [BspNode], parent: usize, side: Side, child: usize) {
    match (nodes.get_mut(parent), side) {
        (Some(BspNode::Split(s)), Side::Positive) => s.positive = Some(child),
        (Some(BspNode::Split(s)), Side::Negative) => s.negative = Some(child),
        (Some(BspNode::Portal(p)), Side::Positive) => p.positive = child,
        (Some(BspNode::Portal(p)), Side::Negative) => p.negative = child,
        _ => {}
    }
}

fn read_leaf(r: &mut Reader<'_>, kind: BspTreeKind) -> Result<BspLeaf> {
    let index = r.i32()?;
    let mut leaf = BspLeaf {
        index,
        solid: 0,
        sphere: Sphere::default(),
        polygons: Vec::new(),
    };
    if kind == BspTreeKind::Physics {
        leaf.solid = r.i32()?;
        leaf.sphere = Sphere::unpack(r)?;
        let count = r.u32()?;
        leaf.polygons = read_poly_ids(r, count)?;
    }
    Ok(leaf)
}

/// The fields stored after a node's children.
fn finish(r: &mut Reader<'_>, kind: BspTreeKind, node: &mut BspNode) -> Result<()> {
    match (node, kind) {
        (BspNode::Split(s), BspTreeKind::Drawing) => {
            s.sphere = Sphere::unpack(r)?;
            let count = r.u32()?;
            s.polygons = read_poly_ids(r, count)?;
        }
        (BspNode::Split(s), BspTreeKind::Physics) => s.sphere = Sphere::unpack(r)?,
        (BspNode::Portal(p), BspTreeKind::Drawing) => {
            p.sphere = Sphere::unpack(r)?;
            let num_polys = r.u32()?;
            let num_portals = r.u32()?;
            p.polygons = read_poly_ids(r, num_polys)?;
            for _ in 0..num_portals {
                p.portals.push(PortalRef {
                    poly_id: r.u16()?,
                    portal_index: r.u16()?,
                });
            }
        }
        _ => {}
    }
    Ok(())
}

fn read_poly_ids(r: &mut Reader<'_>, count: u32) -> Result<Vec<u16>> {
    let mut ids = Vec::new();
    for _ in 0..count {
        ids.push(r.u16()?);
    }
    Ok(ids)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use ac_bits::Writer;

    fn tag(w: &mut Writer, text: &[u8; 4]) {
        w.u32(BspTag::from_text(*text).0);
    }

    fn plane(w: &mut Writer, d: f32) {
        for v in [0.0, 0.0, 1.0, d] {
            w.f32(v);
        }
    }

    fn sphere(w: &mut Writer, r: f32) {
        for v in [0.0, 0.0, 0.0, r] {
            w.f32(v);
        }
    }

    /// A tree of the given kind: a `BPIN` split (plane distance -1, sphere
    /// radius 3, drawing polygon 7) over leaves 0 and 1; physics leaves are
    /// solid with radius 2 and polygons 4 and 5.
    pub(crate) fn small_tree(w: &mut Writer, kind: BspTreeKind) {
        tag(w, b"BPIN");
        plane(w, -1.0);
        for index in [0, 1] {
            tag(w, b"LEAF");
            w.i32(index);
            if kind == BspTreeKind::Physics {
                w.i32(1);
                sphere(w, 2.0);
                w.u32(2);
                w.u16(4);
                w.u16(5);
            }
        }
        match kind {
            BspTreeKind::Drawing => {
                sphere(w, 3.0);
                w.u32(1);
                w.u16(7);
            }
            BspTreeKind::Physics => sphere(w, 3.0),
            BspTreeKind::Cell => {}
        }
    }

    #[test]
    fn each_kind_reads_its_own_fields() {
        for kind in [
            BspTreeKind::Drawing,
            BspTreeKind::Physics,
            BspTreeKind::Cell,
        ] {
            let mut w = Writer::new();
            small_tree(&mut w, kind);
            let bytes = w.into_vec();
            let r = &mut Reader::new(&bytes);
            let t = BspTree::unpack(r, kind).unwrap();
            assert_eq!(r.remaining(), 0, "{kind:?}");
            assert_eq!(t.kind, kind);
            let Some(BspNode::Split(s)) = t.root() else {
                panic!("root is a split")
            };
            assert_eq!(s.plane.distance, -1.0);
            assert_eq!((s.positive, s.negative), (Some(1), Some(2)));
            let BspNode::Leaf(back) = &t.nodes[2] else {
                panic!("back leaf")
            };
            assert_eq!(back.index, 1);
            match kind {
                BspTreeKind::Drawing => {
                    assert_eq!(s.sphere.radius, 3.0);
                    assert_eq!(s.polygons, vec![7]);
                    assert!(back.polygons.is_empty());
                }
                BspTreeKind::Physics => {
                    assert_eq!(s.sphere.radius, 3.0);
                    assert_eq!((back.solid, back.sphere.radius), (1, 2.0));
                    assert_eq!(back.polygons, vec![4, 5]);
                }
                BspTreeKind::Cell => assert_eq!(s.sphere, Sphere::default()),
            }
            for len in 0..bytes.len() {
                assert!(BspTree::unpack(&mut Reader::new(&bytes[..len]), kind).is_err());
            }
        }
    }

    /// Physics: BPnN(BPnn(leaf 0), leaf 1).
    fn physics_sample() -> Vec<u8> {
        let mut w = Writer::new();
        tag(&mut w, b"BPnN");
        plane(&mut w, 1.0);
        tag(&mut w, b"BPnn");
        plane(&mut w, 2.0);
        tag(&mut w, b"LEAF");
        w.i32(0);
        w.i32(1);
        sphere(&mut w, 0.5);
        w.u32(2);
        w.u16(3);
        w.u16(4);
        sphere(&mut w, 1.5); // BPnn's sphere
        tag(&mut w, b"LEAF");
        w.i32(1);
        w.i32(0);
        sphere(&mut w, 0.25);
        w.u32(0);
        sphere(&mut w, 2.5); // BPnN's sphere
        w.into_vec()
    }

    #[test]
    fn physics_tree_reads_depth_first() {
        let bytes = physics_sample();
        let mut r = Reader::new(&bytes);
        let t = BspTree::unpack(&mut r, BspTreeKind::Physics).unwrap();
        assert_eq!(r.remaining(), 0);
        assert_eq!(t.nodes.len(), 4);
        let BspNode::Split(root) = &t.nodes[0] else {
            panic!("root is not a split")
        };
        assert_eq!(&root.tag.text(), b"BPnN");
        assert_eq!((root.positive, root.negative), (Some(1), Some(3)));
        assert_eq!(root.plane.distance, 1.0);
        assert_eq!(root.sphere.radius, 2.5);
        let BspNode::Split(inner) = &t.nodes[1] else {
            panic!("node 1 is not a split")
        };
        assert_eq!((inner.positive, inner.negative), (Some(2), None));
        assert_eq!(inner.sphere.radius, 1.5);
        let BspNode::Leaf(leaf) = &t.nodes[2] else {
            panic!("node 2 is not a leaf")
        };
        assert_eq!((leaf.index, leaf.solid), (0, 1));
        assert_eq!(leaf.polygons, vec![3, 4]);
        let BspNode::Leaf(leaf) = &t.nodes[3] else {
            panic!("node 3 is not a leaf")
        };
        assert_eq!(leaf.index, 1);
        assert_eq!(leaf.sphere.radius, 0.25);
    }

    #[test]
    fn drawing_tree_with_portal_and_childless_tags() {
        let mut w = Writer::new();
        tag(&mut w, b"PORT");
        plane(&mut w, 1.0);
        tag(&mut w, b"BPOL"); // no children
        plane(&mut w, 2.0);
        sphere(&mut w, 3.0);
        w.u32(1);
        w.u16(7);
        tag(&mut w, b"LEAF");
        w.i32(5);
        sphere(&mut w, 4.0); // portal's sphere
        w.u32(2);
        w.u32(1);
        w.u16(8);
        w.u16(9);
        w.u16(10);
        w.u16(0);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let t = BspTree::unpack(&mut r, BspTreeKind::Drawing).unwrap();
        assert_eq!(r.remaining(), 0);
        let BspNode::Portal(p) = &t.nodes[0] else {
            panic!("root is not a portal")
        };
        assert_eq!((p.positive, p.negative), (1, 2));
        assert_eq!(p.sphere.radius, 4.0);
        assert_eq!(p.polygons, vec![8, 9]);
        assert_eq!(
            p.portals,
            vec![PortalRef {
                poly_id: 10,
                portal_index: 0
            }]
        );
        let BspNode::Split(s) = &t.nodes[1] else {
            panic!("node 1 is not a split")
        };
        assert_eq!((s.positive, s.negative), (None, None));
        assert_eq!(s.polygons, vec![7]);
        let BspNode::Leaf(leaf) = &t.nodes[2] else {
            panic!("node 2 is not a leaf")
        };
        assert_eq!(leaf.index, 5);
        assert!(leaf.polygons.is_empty());
    }

    #[test]
    fn portal_nodes_in_cell_and_physics_trees_store_no_trailing_fields() {
        for kind in [BspTreeKind::Cell, BspTreeKind::Physics] {
            let mut w = Writer::new();
            tag(&mut w, b"PORT");
            plane(&mut w, 2.0);
            for index in [0, 1] {
                tag(&mut w, b"LEAF");
                w.i32(index);
                if kind == BspTreeKind::Physics {
                    w.i32(0);
                    sphere(&mut w, 1.0);
                    w.u32(0);
                }
            }
            let bytes = w.into_vec();
            let mut r = Reader::new(&bytes);
            let t = BspTree::unpack(&mut r, kind).unwrap();
            assert_eq!(r.remaining(), 0, "{kind:?}");
            let BspNode::Portal(p) = &t.nodes[0] else {
                panic!("root is not a portal")
            };
            assert_eq!((p.positive, p.negative), (1, 2));
            assert!(p.polygons.is_empty() && p.portals.is_empty());
            assert_eq!(t.nodes[0].children().collect::<Vec<_>>(), vec![1, 2]);
        }
    }

    #[test]
    fn children_follow_the_links() {
        let bytes = physics_sample();
        let t = BspTree::unpack(&mut Reader::new(&bytes), BspTreeKind::Physics).unwrap();
        let children: Vec<Vec<usize>> = t.nodes.iter().map(|n| n.children().collect()).collect();
        assert_eq!(children, vec![vec![1, 3], vec![2], vec![], vec![]]);
    }

    #[test]
    fn cell_tree_has_no_spheres() {
        let mut w = Writer::new();
        tag(&mut w, b"BpIN");
        plane(&mut w, 1.0);
        tag(&mut w, b"LEAF");
        w.i32(3);
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let t = BspTree::unpack(&mut r, BspTreeKind::Cell).unwrap();
        assert_eq!(r.remaining(), 0);
        let BspNode::Split(s) = &t.nodes[0] else {
            panic!("root is not a split")
        };
        assert_eq!((s.positive, s.negative), (None, Some(1)));
    }

    #[test]
    fn child_flags_follow_the_tag() {
        for (text, pos, neg) in [
            (b"BPnn", true, false),
            (b"BPIn", true, false),
            (b"BpIN", false, true),
            (b"BpnN", false, true),
            (b"BPIN", true, true),
            (b"BPnN", true, true),
            (b"BPOL", false, false),
            (b"BpIn", false, false),
        ] {
            let t = BspTag::from_text(*text);
            assert_eq!((t.has_positive(), t.has_negative()), (pos, neg));
        }
        assert_eq!(BspTag::LEAF.0, 0x4C45_4146);
        assert_eq!(BspTag::PORTAL.0, 0x504F_5254);
    }

    #[test]
    fn a_very_deep_tree_does_not_exhaust_the_stack() {
        let depth = 200_000;
        let mut w = Writer::new();
        for _ in 0..depth {
            tag(&mut w, b"BPnn");
            plane(&mut w, 0.0);
        }
        tag(&mut w, b"LEAF");
        w.i32(0);
        for _ in 0..depth {
            sphere(&mut w, 1.0);
        }
        let bytes = w.into_vec();
        let mut r = Reader::new(&bytes);
        let t = BspTree::unpack(&mut r, BspTreeKind::Physics);
        // The leaf is a physics leaf, so it needs more fields than given.
        assert!(t.is_err());

        let mut w = Writer::new();
        for _ in 0..depth {
            tag(&mut w, b"BPnn");
            plane(&mut w, 0.0);
        }
        tag(&mut w, b"LEAF");
        w.i32(0);
        let bytes = w.into_vec();
        let t = BspTree::unpack(&mut Reader::new(&bytes), BspTreeKind::Cell).unwrap();
        assert_eq!(t.nodes.len(), depth + 1);
    }

    #[test]
    fn truncated_tree_is_an_error_at_every_length() {
        let bytes = physics_sample();
        for len in 0..bytes.len() {
            assert!(
                BspTree::unpack(&mut Reader::new(&bytes[..len]), BspTreeKind::Physics).is_err(),
                "len {len}"
            );
        }
    }
}
