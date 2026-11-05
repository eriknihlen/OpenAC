//! Walks of the prepared BSP trees.
//!
//! Every walk visits nodes in the same order as a depth-first recursion
//! that tries the positive child before the negative one, using an explicit
//! stack. A node's bounding sphere is tested before anything below it; a
//! splitting node whose plane the sphere clears (by its radius less
//! [`EPSILON`]) sends the walk down one side only.

use glam::Vec3;

use crate::bsp::{CellBsp, CellBspNode, PhysicsBsp, PhysicsBspNode};
use crate::bsp_collisions::WalkState;
use crate::geometry::{Plane, Sphere};
use crate::globals::EPSILON;
use crate::polygon::{
    ResolvedPolygon, adjust_sphere_to_plane, check_walkable, hits_sphere,
    polygon_hits_sphere_precise, pos_hits_sphere, walkable_hits_sphere,
};
use crate::stack::NodeStack;

/// A polygon a sphere touched.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PolyHit {
    /// The polygon's id.
    pub poly_id: u16,
    /// Its plane normal, in the tree's own frame.
    pub normal: Vec3,
}

/// A walkable polygon found under a sphere.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WalkableHit {
    /// The polygon's id.
    pub poly_id: u16,
    /// The sphere's centre once it rests on the polygon.
    pub adjusted_center: Vec3,
}

/// Tolerance of the box-against-plane classification.
pub const BOX_PLANE_EPSILON: f32 = 0.0002;

/// Where a point or box lies relative to a plane.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlaneSide {
    /// In front.
    Positive = 0,
    /// Behind.
    Negative = 1,
    /// On the plane, or (for a box) on both sides.
    Straddle = 2,
}

/// Which side of `plane` the point is on, with a band of `eps` either side
/// counting as on the plane (the front edge of the band counts as front).
#[must_use]
pub fn which_side(plane: &Plane, point: Vec3, eps: f32) -> PlaneSide {
    let dist = plane.normal.dot(point) + plane.d;
    if dist >= eps {
        PlaneSide::Positive
    } else if dist < -eps {
        PlaneSide::Negative
    } else {
        PlaneSide::Straddle
    }
}

/// Which side of `plane` the axis-aligned box `min..max` is on: one side
/// only when all eight corners are on it.
#[must_use]
pub fn classify_box(plane: &Plane, min: Vec3, max: Vec3) -> PlaneSide {
    let corners = [
        Vec3::new(min.x, min.y, min.z),
        Vec3::new(max.x, max.y, max.z),
        Vec3::new(min.x, min.y, max.z),
        Vec3::new(min.x, max.y, min.z),
        Vec3::new(max.x, min.y, min.z),
        Vec3::new(max.x, min.y, max.z),
        Vec3::new(min.x, max.y, max.z),
        Vec3::new(max.x, max.y, min.z),
    ];
    let side0 = which_side(plane, corners[0], BOX_PLANE_EPSILON);
    if side0 == PlaneSide::Straddle {
        return PlaneSide::Straddle;
    }
    for corner in &corners[1..] {
        if which_side(plane, *corner, BOX_PLANE_EPSILON) != side0 {
            return PlaneSide::Straddle;
        }
    }
    side0
}

/// Push the children a split sends a sphere to: the positive side when it
/// is clear in front, the negative side when clear behind, otherwise both
/// (positive first).
fn push_sides(
    stack: &mut NodeStack<usize>,
    dist: f32,
    reach: f32,
    positive: Option<usize>,
    negative: Option<usize>,
) {
    if dist >= reach {
        stack.push_opt(positive);
    } else if dist <= -reach {
        stack.push_opt(negative);
    } else {
        stack.push_opt(negative);
        stack.push_opt(positive);
    }
}

impl NodeStack<usize> {
    fn push_opt(&mut self, node: Option<usize>) {
        if let Some(node) = node {
            self.push(node);
        }
    }
}

impl PhysicsBsp {
    fn walk_start(&self) -> NodeStack<usize> {
        let mut stack = NodeStack::new();
        if !self.nodes.is_empty() {
            stack.push(0);
        }
        stack
    }

    fn leaf_polygons<'a>(&'a self, ids: &'a [u16]) -> impl Iterator<Item = &'a ResolvedPolygon> {
        ids.iter().filter_map(|id| self.polygons.get(id))
    }

    /// First polygon the moving sphere runs into (touches while moving into
    /// its front). Polygons touched while moving away are left in
    /// `hit_poly` as a near miss.
    pub(crate) fn sphere_intersects_poly_internal<'a>(
        &'a self,
        sphere: &Sphere,
        movement: Vec3,
        hit_poly: &mut Option<&'a ResolvedPolygon>,
        contact: &mut Vec3,
    ) -> bool {
        let mut stack = self.walk_start();
        while let Some(i) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            if !sphere.intersects(node.sphere()) {
                continue;
            }
            match node {
                PhysicsBspNode::Leaf { polygons, .. } => {
                    for poly in self.leaf_polygons(polygons) {
                        if pos_hits_sphere(poly, sphere, movement, contact, hit_poly) {
                            return true;
                        }
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    positive,
                    negative,
                    ..
                } => {
                    let dist = plane.distance(sphere.center);
                    push_sides(
                        &mut stack,
                        dist,
                        sphere.radius - EPSILON,
                        *positive,
                        *negative,
                    );
                }
            }
        }
        false
    }

    /// Lowers `valid_pos` onto every walkable polygon it touches, moving
    /// back along `movement`; the last polygon that took it is the hit.
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn find_walkable_internal<'a>(
        &'a self,
        walk: &mut WalkState,
        valid_pos: &mut Sphere,
        movement: Vec3,
        up: Vec3,
        hit_poly: &mut Option<&'a ResolvedPolygon>,
        changed: &mut bool,
    ) {
        let mut stack = self.walk_start();
        while let Some(i) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            // The sphere may have moved since this node was queued; test
            // where it is now, as the recursion would.
            if !valid_pos.intersects(node.sphere()) {
                continue;
            }
            match node {
                PhysicsBspNode::Leaf { polygons, .. } => {
                    for poly in self.leaf_polygons(polygons) {
                        let walkable =
                            walkable_hits_sphere(poly, walk.walkable_allowance, valid_pos, up);
                        let adjusted = walkable
                            && adjust_sphere_to_plane(
                                poly,
                                &mut walk.walk_interp,
                                valid_pos,
                                movement,
                            );
                        if walkable && adjusted {
                            *changed = true;
                            *hit_poly = Some(poly);
                        }
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    positive,
                    negative,
                    ..
                } => {
                    let dist = plane.distance(valid_pos.center);
                    push_sides(
                        &mut stack,
                        dist,
                        valid_pos.radius - EPSILON,
                        *positive,
                        *negative,
                    );
                }
            }
        }
    }

    /// Whether the sphere stands on a walkable polygon (touches it and is
    /// supported within half its radius).
    pub(crate) fn hits_walkable_internal(
        &self,
        walk: &WalkState,
        sphere: &Sphere,
        up: Vec3,
    ) -> bool {
        let mut stack = self.walk_start();
        while let Some(i) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            if !sphere.intersects(node.sphere()) {
                continue;
            }
            match node {
                PhysicsBspNode::Leaf { polygons, .. } => {
                    for poly in self.leaf_polygons(polygons) {
                        if walkable_hits_sphere(poly, walk.walkable_allowance, sphere, up)
                            && check_walkable(poly, sphere, up, true)
                        {
                            return true;
                        }
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    positive,
                    negative,
                    ..
                } => {
                    let dist = plane.distance(sphere.center);
                    push_sides(
                        &mut stack,
                        dist,
                        sphere.radius - EPSILON,
                        *positive,
                        *negative,
                    );
                }
            }
        }
        false
    }

    /// Whether the sphere overlaps solid matter: touches a polygon, or (with
    /// `center_check`) has its centre in a solid leaf. The centre check is
    /// passed only to the side of a straddled plane the centre is on.
    #[must_use]
    pub fn sphere_intersects_solid(&self, sphere: &Sphere, center_check: bool) -> bool {
        let mut stack = NodeStack::<(usize, bool)>::new();
        if !self.nodes.is_empty() {
            stack.push((0, center_check));
        }
        while let Some((i, center_check)) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            match node {
                PhysicsBspNode::Leaf {
                    sphere: bounds,
                    solid,
                    polygons,
                } => {
                    if polygons.is_empty() {
                        continue;
                    }
                    if center_check && *solid {
                        return true;
                    }
                    if !sphere.intersects(bounds) {
                        continue;
                    }
                    if self.leaf_polygons(polygons).any(|p| hits_sphere(p, sphere)) {
                        return true;
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    sphere: bounds,
                    positive,
                    negative,
                } => {
                    if !sphere.intersects(bounds) {
                        continue;
                    }
                    let dist = plane.distance(sphere.center);
                    let reach = sphere.radius - EPSILON;
                    let mut push = |node: Option<usize>, cc: bool| {
                        if let Some(node) = node {
                            stack.push((node, cc));
                        }
                    };
                    if dist >= reach {
                        push(*positive, center_check);
                    } else if dist <= -reach {
                        push(*negative, center_check);
                    } else if dist < 0.0 {
                        // Positive side first, without the centre check.
                        push(*negative, center_check);
                        push(*positive, false);
                    } else {
                        push(*negative, false);
                        push(*positive, center_check);
                    }
                }
            }
        }
        false
    }

    /// The placement test: whether the sphere (of `radius` for the plane
    /// tests) overlaps a polygon or, with `center_check`, has its centre in
    /// solid space. Sets `center_solid` when the centre is found in a solid
    /// leaf and `hit_poly` to the first polygon touched.
    ///
    /// The return value follows the tree walk exactly: a touched polygon
    /// returns true only when reached through the second-visited side of
    /// every straddled plane above it; otherwise the walk unwinds returning
    /// `center_solid`. With no polygon touched it is the answer of the last
    /// node visited along that second-side chain (an empty leaf answers
    /// false, anything else `center_solid`).
    pub(crate) fn sphere_intersects_solid_poly_internal<'a>(
        &'a self,
        sphere: &Sphere,
        radius: f32,
        center_solid: &mut bool,
        hit_poly: &mut Option<&'a ResolvedPolygon>,
        center_check: bool,
    ) -> bool {
        /// A queued node: index (None for a missing child), centre check,
        /// and whether its answer is the walk's answer.
        #[derive(Clone, Copy, Default)]
        struct Item {
            node: Option<usize>,
            center_check: bool,
            tail: bool,
        }

        if self.nodes.is_empty() {
            return *center_solid;
        }
        let mut stack = NodeStack::with(Item {
            node: Some(0),
            center_check,
            tail: true,
        });
        let mut answer = *center_solid;
        while let Some(item) = stack.pop() {
            let Some(node) = item.node.and_then(|i| self.nodes.get(i)) else {
                if item.tail {
                    answer = *center_solid;
                }
                continue;
            };
            match node {
                PhysicsBspNode::Leaf {
                    sphere: bounds,
                    solid,
                    polygons,
                } => {
                    if polygons.is_empty() {
                        if item.tail {
                            answer = false;
                        }
                        continue;
                    }
                    if item.center_check && *solid {
                        *center_solid = true;
                    }
                    if sphere.intersects(bounds)
                        && let Some(poly) = self
                            .leaf_polygons(polygons)
                            .find(|p| hits_sphere(p, sphere))
                    {
                        *hit_poly = Some(poly);
                        return item.tail || *center_solid;
                    }
                    if item.tail {
                        answer = *center_solid;
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    sphere: bounds,
                    positive,
                    negative,
                } => {
                    if !sphere.intersects(bounds) {
                        if item.tail {
                            answer = *center_solid;
                        }
                        continue;
                    }
                    let dist = plane.distance(sphere.center);
                    let reach = radius - EPSILON;
                    if dist >= reach {
                        stack.push(Item {
                            node: *positive,
                            ..item
                        });
                    } else if dist <= -reach {
                        stack.push(Item {
                            node: *negative,
                            ..item
                        });
                    } else {
                        let (first, second) = if dist <= 0.0 {
                            (*negative, *positive)
                        } else {
                            (*positive, *negative)
                        };
                        stack.push(Item {
                            node: second,
                            center_check: false,
                            tail: item.tail,
                        });
                        stack.push(Item {
                            node: first,
                            center_check: item.center_check,
                            tail: false,
                        });
                    }
                }
            }
        }
        answer
    }

    /// Whether a sphere touches any polygon of the tree (no movement test):
    /// the first polygon found.
    #[must_use]
    pub fn sphere_intersects_poly(&self, center: Vec3, radius: f32) -> Option<PolyHit> {
        let sphere = Sphere::new(center, radius);
        let mut stack = self.walk_start();
        while let Some(i) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            if !sphere.intersects(node.sphere()) {
                continue;
            }
            match node {
                PhysicsBspNode::Leaf { polygons, .. } => {
                    for poly in self.leaf_polygons(polygons) {
                        let mut contact = Vec3::ZERO;
                        if polygon_hits_sphere_precise(
                            &poly.plane,
                            &poly.vertices,
                            center,
                            radius,
                            &mut contact,
                        ) {
                            return Some(PolyHit {
                                poly_id: poly.id,
                                normal: poly.plane.normal,
                            });
                        }
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    positive,
                    negative,
                    ..
                } => {
                    let dist = plane.distance(center);
                    push_sides(&mut stack, dist, radius - EPSILON, *positive, *negative);
                }
            }
        }
        None
    }

    /// The earliest polygon a sphere moving by `movement` touches, tested
    /// at the start (time 0) and the end (time 1) of the move only; only
    /// polygons it moves into the front of count.
    #[must_use]
    pub fn sphere_intersects_poly_with_time(
        &self,
        center: Vec3,
        radius: f32,
        movement: Vec3,
    ) -> Option<(PolyHit, f32)> {
        let move_len = movement.length();
        let mut best: Option<(PolyHit, f32)> = None;
        let mut best_time = f32::MAX;
        let mut stack = self.walk_start();
        while let Some(i) = stack.pop() {
            let Some(node) = self.nodes.get(i) else {
                continue;
            };
            let bounds = node.sphere();
            let d = center - bounds.center;
            let r = radius + bounds.radius + move_len + 0.1;
            if d.length_squared() >= r * r {
                continue;
            }
            match node {
                PhysicsBspNode::Leaf { polygons, .. } => {
                    for poly in self.leaf_polygons(polygons) {
                        if movement.dot(poly.plane.normal) >= 0.0 {
                            continue;
                        }
                        let hit = PolyHit {
                            poly_id: poly.id,
                            normal: poly.plane.normal,
                        };
                        let mut contact = Vec3::ZERO;
                        if polygon_hits_sphere_precise(
                            &poly.plane,
                            &poly.vertices,
                            center,
                            radius,
                            &mut contact,
                        ) {
                            if 0.0 < best_time {
                                best_time = 0.0;
                                best = Some((hit, 0.0));
                            }
                            continue;
                        }
                        let end = center + movement;
                        if polygon_hits_sphere_precise(
                            &poly.plane,
                            &poly.vertices,
                            end,
                            radius,
                            &mut contact,
                        ) && 1.0 < best_time
                        {
                            best_time = 1.0;
                            best = Some((hit, 1.0));
                        }
                    }
                }
                PhysicsBspNode::Split {
                    plane,
                    positive,
                    negative,
                    ..
                } => {
                    let dist = plane.distance(center);
                    let reach = radius + move_len;
                    if dist >= reach {
                        stack.push_opt(*positive);
                    } else if dist <= -reach {
                        stack.push_opt(*negative);
                    } else {
                        stack.push_opt(*negative);
                        stack.push_opt(*positive);
                    }
                }
            }
        }
        best
    }

    /// Lowers a sphere by up to `probe_distance` along `-up` onto the
    /// walkable polygons it touches (polygons whose normal is more than
    /// `walk.walkable_allowance` up). `walk.walk_interp` shrinks as the
    /// sphere is lowered. `None` when nothing walkable took the sphere.
    pub fn find_walkable_sphere(
        &self,
        walk: &mut WalkState,
        sphere: Sphere,
        probe_distance: f32,
        up: Vec3,
    ) -> Option<WalkableHit> {
        if self.nodes.is_empty() {
            return None;
        }
        let movement = -up * probe_distance;
        let mut valid_pos = sphere;
        let mut changed = false;
        let mut poly = None;
        self.find_walkable_internal(walk, &mut valid_pos, movement, up, &mut poly, &mut changed);
        match poly {
            Some(poly) if changed => Some(WalkableHit {
                poly_id: poly.id,
                adjusted_center: valid_pos.center,
            }),
            _ => None,
        }
    }
}

impl CellBsp {
    /// Whether the point is inside the room: in front of (or on) every
    /// plane down the positive children. A tree with no nodes holds
    /// everything.
    #[must_use]
    pub fn point_inside(&self, point: Vec3) -> bool {
        self.walk_positive(|plane| plane.distance(point) >= 0.0)
    }

    /// Whether the sphere reaches into the room: never more than its radius
    /// (plus 1 cm) behind a plane down the positive children.
    #[must_use]
    pub fn sphere_intersects(&self, center: Vec3, radius: f32) -> bool {
        let rad = radius + 0.01;
        self.walk_positive(|plane| plane.distance(center) >= -rad)
    }

    /// Whether the box reaches into the room: never wholly behind a plane
    /// down the positive children.
    #[must_use]
    pub fn box_intersects(&self, min: Vec3, max: Vec3) -> bool {
        self.walk_positive(|plane| classify_box(plane, min, max) != PlaneSide::Negative)
    }

    /// Follows the positive children while `keep` accepts each plane; a
    /// leaf or a missing positive child ends the walk inside.
    fn walk_positive(&self, mut keep: impl FnMut(&Plane) -> bool) -> bool {
        let mut index = 0;
        loop {
            match self.nodes.get(index) {
                None | Some(CellBspNode::Leaf) => return true,
                Some(CellBspNode::Split {
                    plane, positive, ..
                }) => {
                    if !keep(plane) {
                        return false;
                    }
                    match positive {
                        Some(next) => index = *next,
                        None => return true,
                    }
                }
            }
        }
    }
}
