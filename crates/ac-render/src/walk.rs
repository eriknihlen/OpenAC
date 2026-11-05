//! The game's frame walk: which interior cells, landblocks and buildings a
//! frame draws, and through which screen-space openings.
//!
//! The frame is rooted at the viewer's (camera's) cell. Outdoors the whole
//! landscape is drawn through one full-screen view. Inside a cell the walk
//! floods outward through the cell's portals (doorways, windows, stair
//! openings): each portal polygon is projected to the screen and clipped by
//! the view it was reached through, and the clipped polygon becomes a view
//! of the cell behind it. Portals that lead outdoors collect the views the
//! landscape is drawn through. Outdoors, each building whose doorway faces
//! the viewer floods its interior through that doorway the same way (a
//! "look-in").
//!
//! A view is a convex screen polygon (pixels, y down) with a world plane
//! per edge; the renderer draws everything reached through a view clipped
//! to that polygon. Pure CPU logic in the drawing frame; the caller turns
//! the walk's calls on [`WalkSink`] into draws.

use std::collections::HashMap;

use glam::{Mat4, Vec2, Vec3, Vec4};

/// The plane and side epsilon of every visibility test.
pub const EPSILON: f32 = f32::from_bits(0x3951_B717);
/// A view keeps at most this many vertices.
pub const MAX_VIEW_VERTICES: usize = 31;
/// Screen points closer than this (pixels) on both axes are one point.
const DEDUP_THRESHOLD: f32 = 1.0;
/// The near distance of the camera plane the walk culls with, metres.
pub const Z_NEAR: f32 = 0.1;
/// The portal-side sentinel for "no entry portal".
const NO_PORTAL: usize = 0xFFFF;
/// A portal whose other cell is this leads outdoors.
pub const OUTSIDE: u32 = 0xFFFF_FFFF;
/// Height the landscape culling treats as the sky, metres.
const SKY_HEIGHT: f32 = 1000.0;
const INSIDE_COLUMN: f32 = 0.0;
const OUTSIDE_COLUMN: f32 = 1001.0;
/// Side of a landblock and of a land cell, metres.
const BLOCK_LENGTH: f32 = 192.0;
const CELL_LENGTH: f32 = 24.0;

/// A plane: points `p` with `normal . p + d = 0`; positive in front.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Plane {
    /// Unit normal.
    pub normal: Vec3,
    /// Offset.
    pub d: f32,
}

impl Plane {
    /// Signed distance of a point.
    #[must_use]
    pub fn distance(&self, p: Vec3) -> f32 {
        self.normal.dot(p) + self.d
    }
}

/// A portal polygon in its owner's local frame.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Polygon {
    /// Vertices in order.
    pub vertices: Vec<Vec3>,
    /// Its plane, from the first three vertices.
    pub plane: Plane,
}

impl Polygon {
    /// A polygon from its vertices; `None` with fewer than three.
    #[must_use]
    pub fn new(vertices: Vec<Vec3>) -> Option<Self> {
        if vertices.len() < 3 {
            return None;
        }
        let normal = (vertices[1] - vertices[0])
            .cross(vertices[2] - vertices[0])
            .normalize_or_zero();
        let d = -normal.dot(vertices[0]);
        Some(Self {
            vertices,
            plane: Plane { normal, d },
        })
    }
}

/// A portal of an interior cell.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CellPortal {
    /// The cell on the other side; [`OUTSIDE`] for the outdoors.
    pub other_cell_id: u32,
    /// Index of the portal's polygon in [`WalkCell::polygons`].
    pub polygon: usize,
    /// The side of the polygon the cell lies on: 0 front, 1 back.
    pub portal_side: u8,
    /// The matching portal's index in the other cell; negative for none.
    pub other_portal_id: i32,
    /// The portal matches its partner exactly (no second clip).
    pub exact_match: bool,
}

/// An interior cell as the walk sees it.
#[derive(Debug, Clone, PartialEq)]
pub struct WalkCell {
    /// Full cell id.
    pub cell_id: u32,
    /// Portals in file order.
    pub portals: Vec<CellPortal>,
    /// Portal polygons, cell-local (empty when the dat polygon is missing).
    pub polygons: Vec<Polygon>,
    /// Cells visible from this one.
    pub stab_list: Vec<u32>,
    /// Cell-local to drawing frame.
    pub world: Mat4,
    /// Drawing frame to cell-local.
    pub inverse: Mat4,
}

/// A portal of a building's outside into one of its cells.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuildingPortal {
    /// The side of the doorway the outside lies on: 0 front, 1 back.
    pub portal_side: u8,
    /// The cell behind it; [`OUTSIDE`] for none.
    pub other_cell_id: u32,
    /// The matching portal's index in that cell; negative for none.
    pub other_portal_id: i32,
    /// Exact match.
    pub exact_match: bool,
    /// Cells visible through it.
    pub stab_list: Vec<u32>,
}

/// One node of a building's drawing tree, reduced to what the portal walk
/// reads.
#[derive(Debug, Clone, PartialEq)]
pub enum BspNode {
    /// A leaf: the walk stops here.
    Leaf,
    /// A splitting plane with children (indices into the tree).
    Split {
        /// The plane.
        plane: Plane,
        /// Front child.
        positive: Option<usize>,
        /// Back child.
        negative: Option<usize>,
    },
    /// A plane holding doorways.
    Portal {
        /// The plane.
        plane: Plane,
        /// Front child.
        positive: Option<usize>,
        /// Back child.
        negative: Option<usize>,
        /// The doorways on it: (index into the building's portals, polygon).
        portals: Vec<(usize, Polygon)>,
    },
}

/// A building's portal-only drawing tree, root first.
pub type BspTree = Vec<BspNode>;

/// A building as one frame's walk uses it: its doorways and the doorway
/// tree of the model its level of detail chose (`None`: no look-ins).
#[derive(Debug, Clone, Copy)]
pub struct BuildingRef<'a> {
    /// The building.
    pub building: &'a WalkBuilding,
    /// The chosen model's doorway tree.
    pub tree: Option<&'a BspTree>,
}

/// A building as the walk sees it (part 0 of its model).
#[derive(Debug, Clone, PartialEq)]
pub struct WalkBuilding {
    /// Doorways into its cells.
    pub portals: Vec<BuildingPortal>,
    /// Part-0 local to drawing frame.
    pub world: Mat4,
    /// Drawing frame to part-0 local.
    pub inverse: Mat4,
}

/// What lies in view: outside, partly or entirely inside.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum Bounding {
    /// Not in view.
    #[default]
    Outside,
    /// Partly in view.
    Partial,
    /// Entirely in view.
    Entire,
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct ScreenPoint {
    x: f32,
    y: f32,
    z: f32,
    w: f32,
}

/// One vertex of a view: its screen point and the world plane of the edge
/// it starts.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct ViewVertex {
    /// Pixels, y down.
    pub point: Vec2,
    /// World plane through the eye and this edge; positive inside.
    pub plane: Plane,
}

/// One view polygon: a range of [`ViewSet::vertices`].
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ViewPoly {
    /// Vertex count.
    pub count: usize,
    /// First vertex.
    pub base: usize,
    /// Screen bounds.
    pub min: Vec2,
    /// Screen bounds.
    pub max: Vec2,
}

/// The views of one portal view slot.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ViewSet {
    /// Polygons.
    pub polys: Vec<ViewPoly>,
    /// Their vertices (each polygon's first vertex repeated after it).
    pub vertices: Vec<ViewVertex>,
}

impl ViewSet {
    /// The screen points of view `i`.
    #[must_use]
    pub fn points(&self, i: usize) -> Vec<Vec2> {
        let p = self.polys[i];
        self.vertices[p.base..p.base + p.count]
            .iter()
            .map(|v| v.point)
            .collect()
    }

    /// The edge planes of view `i`.
    #[must_use]
    pub fn planes(&self, i: usize) -> Vec<Plane> {
        let p = self.polys[i];
        self.vertices[p.base..p.base + p.count]
            .iter()
            .map(|v| v.plane)
            .collect()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
struct PortalFlags {
    seen: bool,
    in_view: bool,
}

/// One view slot of a cell: the views reaching it this flood.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct PortalView {
    flags: Vec<PortalFlags>,
    /// The views.
    pub view: ViewSet,
    max_in_dist_sq: f32,
    /// Views in use.
    pub view_count: usize,
    cell_view_done: bool,
    update_count: usize,
}

impl PortalView {
    fn reset_for_push(&mut self) {
        self.view_count = 0;
        self.update_count = 0;
    }
}

/// A cell's stack of view slots (a look-in nested in another pushes one).
#[derive(Debug, Clone, Default)]
struct CellViews {
    slots: Vec<PortalView>,
    num_view: usize,
}

impl CellViews {
    fn push(&mut self) {
        while self.slots.len() <= self.num_view {
            self.slots.push(PortalView::default());
        }
        self.slots[self.num_view].reset_for_push();
        self.num_view += 1;
    }

    fn pop(&mut self) {
        self.num_view = self.num_view.saturating_sub(1);
    }

    fn top(&self) -> Option<&PortalView> {
        self.num_view.checked_sub(1).map(|i| &self.slots[i])
    }

    fn top_mut(&mut self) -> Option<&mut PortalView> {
        self.num_view.checked_sub(1).map(|i| &mut self.slots[i])
    }
}

/// The camera a frame is walked for.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WalkCamera {
    /// Drawing frame to clip space.
    pub view_proj: Mat4,
    /// Its inverse.
    pub inverse_view_proj: Mat4,
    /// Eye, drawing frame.
    pub eye: Vec3,
    /// Look direction (unit).
    pub forward: Vec3,
    /// Viewport, pixels.
    pub width: f32,
    /// Viewport, pixels.
    pub height: f32,
}

impl WalkCamera {
    /// The near plane, positive in front of it.
    #[must_use]
    pub fn cy_plane(&self) -> Plane {
        Plane {
            normal: self.forward,
            d: -self.eye.dot(self.forward) - Z_NEAR,
        }
    }

    /// The direction from the eye through a screen point.
    fn ray_through(&self, sx: f32, sy: f32) -> Vec3 {
        let ndc_x = sx / self.width * 2.0 - 1.0;
        let ndc_y = 1.0 - sy / self.height * 2.0;
        let near = self.inverse_view_proj * Vec4::new(ndc_x, ndc_y, 0.0, 1.0);
        let far = self.inverse_view_proj * Vec4::new(ndc_x, ndc_y, 1.0, 1.0);
        far.truncate() / far.w - near.truncate() / near.w
    }

    fn screen_point(&self, p: Vec3, object_to_clip: &Mat4) -> ScreenPoint {
        let c = *object_to_clip * p.extend(1.0);
        ScreenPoint {
            x: c.x * self.width * 0.5 + c.w * self.width * 0.5,
            y: c.w * self.height * 0.5 - c.y * self.height * 0.5,
            z: c.z,
            w: c.w,
        }
    }
}

/// Append one view to `dest` from screen points (homogeneous or divided):
/// near-duplicate and collinear points are dropped; fewer than three left
/// is no view. Each edge gets the world plane through the eye.
fn copy_view(dest: &mut PortalView, points: &mut [ScreenPoint], camera: &WalkCamera) -> bool {
    let npts = points.len();
    if npts == 0 {
        return false;
    }
    let mut keep = vec![false; npts];
    keep[0] = true;
    let mut n = 1usize;
    let mut last = 0usize;
    let mut second_to_last = 0usize;
    let mut second = 0usize;
    for i in 0..npts {
        let p = &mut points[i];
        if p.w != 1.0 {
            p.x /= p.w;
            p.y /= p.w;
            p.w = 1.0;
        }
        if i == 0 {
            continue;
        }
        let distinct = (points[i].x - points[last].x).abs() > DEDUP_THRESHOLD
            || (points[i].y - points[last].y).abs() > DEDUP_THRESHOLD;
        keep[i] = distinct;
        if !distinct {
            continue;
        }
        if n == 1 {
            n += 1;
            second = i;
        } else {
            let pp = points[second_to_last];
            let prev = points[last];
            let cur = points[i];
            let span = (pp.x - cur.x).abs().max((pp.y - cur.y).abs());
            let cross = (pp.x - prev.x) * (prev.y - cur.y) - (pp.y - prev.y) * (prev.x - cur.x);
            if cross.abs() >= span {
                n += 1;
                second_to_last = last;
            } else {
                keep[last] = false;
                if second == last {
                    second = i;
                }
            }
        }
        last = i;
    }
    let first = points[0];
    let last_distinct = (first.x - points[last].x).abs() > DEDUP_THRESHOLD
        || (first.y - points[last].y).abs() > DEDUP_THRESHOLD;
    keep[last] = last_distinct;
    if last_distinct {
        let span = (points[second_to_last].x - first.x)
            .abs()
            .max((points[second_to_last].y - first.y).abs());
        let cross = (points[second_to_last].x - points[last].x) * (points[last].y - first.y)
            - (points[last].x - first.x) * (points[second_to_last].y - points[last].y);
        if cross.abs() < span {
            keep[last] = false;
            n -= 1;
            last = second_to_last;
        }
    } else {
        n -= 1;
        last = second_to_last;
    }
    second_to_last = last;
    if second > 0 {
        let span = (points[second_to_last].x - points[second].x)
            .abs()
            .max((points[second_to_last].y - points[second].y).abs());
        let cross = (first.y - points[second].y) * (points[second_to_last].x - first.x)
            - (first.x - points[second].x) * (points[second_to_last].y - first.y);
        if cross.abs() < span {
            n -= 1;
            keep[0] = false;
        }
    }
    if n < 3 {
        return false;
    }
    let n = n.min(MAX_VIEW_VERTICES);

    let view = &mut dest.view;
    if dest.view_count == 0 {
        view.polys.clear();
        view.vertices.clear();
    }
    let base = view.vertices.len();
    let mut written = 0;
    for i in 0..npts {
        if written >= n {
            break;
        }
        if !keep[i] {
            continue;
        }
        view.vertices.push(ViewVertex {
            point: Vec2::new(points[i].x.abs(), points[i].y.abs()),
            plane: Plane::default(),
        });
        written += 1;
    }
    if written < 3 {
        view.vertices.truncate(base);
        return false;
    }
    let n = written;
    let first_point = view.vertices[base].point;
    view.vertices.push(ViewVertex {
        point: first_point,
        plane: Plane::default(),
    });
    let mut min = view.vertices[base + n - 1].point;
    let mut max = min;
    for k in (0..n - 1).rev() {
        let pt = view.vertices[base + k].point;
        min = min.min(pt);
        max = max.max(pt);
    }
    view.polys.push(ViewPoly {
        count: n,
        base,
        min,
        max,
    });
    let mut rays: Vec<Vec3> = (0..n)
        .map(|k| {
            let pt = view.vertices[base + k].point;
            camera.ray_through(pt.x, pt.y)
        })
        .collect();
    rays.push(rays[0]);
    for k in (0..n).rev() {
        let mut normal = rays[k + 1].cross(rays[k]);
        if normal.x.abs() >= EPSILON || normal.y.abs() >= EPSILON || normal.z.abs() >= EPSILON {
            normal /= normal.length();
        }
        view.vertices[base + k].plane = Plane {
            normal,
            d: -normal.dot(camera.eye),
        };
    }
    dest.view_count += 1;
    true
}

/// Append the full-viewport view.
fn copy_full_view(dest: &mut PortalView, camera: &WalkCamera) -> bool {
    let (w, h) = (camera.width, camera.height);
    let mut quad = [
        ScreenPoint {
            x: 0.0,
            y: h,
            z: 0.0,
            w: 1.0,
        },
        ScreenPoint {
            x: w,
            y: h,
            z: 0.0,
            w: 1.0,
        },
        ScreenPoint {
            x: w,
            y: 0.0,
            z: 0.0,
            w: 1.0,
        },
        ScreenPoint {
            x: 0.0,
            y: 0.0,
            z: 0.0,
            w: 1.0,
        },
    ];
    copy_view(dest, &mut quad, camera)
}

/// Clip a projected polygon (homogeneous screen points) by a convex view
/// (its screen vertices): first against `w >= EPSILON`, then each edge.
/// The result keeps the input's winding; empty when fewer than three
/// points survive.
fn clip_against_view(input: &[ScreenPoint], view: &[Vec2]) -> Vec<ScreenPoint> {
    let mut current: Vec<ScreenPoint> = input.to_vec();
    let mut reversals = 0;
    if current.iter().any(|p| p.w < EPSILON) {
        current = clip_pass_w(&current);
        if current.len() < 3 {
            return Vec::new();
        }
        reversals += 1;
    }
    let n = view.len();
    for e in (0..n).rev() {
        let a = view[if e == n - 1 { 0 } else { e + 1 }];
        let b = view[e];
        current = clip_pass_edge(&current, a, b);
        if current.len() < 3 {
            return Vec::new();
        }
        reversals += 1;
    }
    if reversals % 2 == 1 {
        current.reverse();
    }
    current
}

fn lerp(p: ScreenPoint, q: ScreenPoint, t: f32) -> ScreenPoint {
    ScreenPoint {
        x: p.x + (q.x - p.x) * t,
        y: p.y + (q.y - p.y) * t,
        z: p.z + (q.z - p.z) * t,
        w: p.w + (q.w - p.w) * t,
    }
}

fn clip_pass_w(pts: &[ScreenPoint]) -> Vec<ScreenPoint> {
    let mut out = Vec::with_capacity(pts.len() + 1);
    let mut prev = pts[0];
    let mut s_prev = prev.w - EPSILON;
    let mut in_prev = s_prev >= 0.0;
    for i in (0..pts.len()).rev() {
        let cur = pts[i];
        let s = cur.w - EPSILON;
        let in_cur = s >= 0.0;
        if in_prev != in_cur {
            out.push(lerp(prev, cur, s_prev / (s_prev - s)));
        }
        if in_cur {
            out.push(cur);
        }
        prev = cur;
        s_prev = s;
        in_prev = in_cur;
    }
    out
}

fn clip_pass_edge(pts: &[ScreenPoint], a: Vec2, b: Vec2) -> Vec<ScreenPoint> {
    let ex = b.x - a.x;
    let ey = b.y - a.y;
    let side = |p: &ScreenPoint| (p.x - a.x * p.w) * ey - (p.y - a.y * p.w) * ex;
    let mut out = Vec::with_capacity(pts.len() + 1);
    let mut prev = pts[0];
    let s0 = side(&prev);
    let mut s_prev = s0;
    let mut in_prev = s0 <= 0.0;
    for i in (0..pts.len()).rev() {
        let cur = pts[i];
        let s = if i != 0 { side(&cur) } else { s0 };
        let in_cur = s <= 0.0;
        if in_prev != in_cur {
            out.push(lerp(prev, cur, s_prev / (s_prev - s)));
        }
        if in_cur {
            out.push(cur);
        }
        prev = cur;
        s_prev = s;
        in_prev = in_cur;
    }
    out
}

/// Whether a sphere is in a view: outside when behind the near plane or
/// any edge plane by more than its radius.
#[must_use]
pub fn viewcone_check(center: Vec3, radius: f32, cy: &Plane, edges: &[Plane]) -> Bounding {
    let d = cy.distance(center);
    if d < -radius {
        return Bounding::Outside;
    }
    let mut partial = d <= radius;
    for plane in edges {
        let d = plane.distance(center);
        if d < -radius {
            return Bounding::Outside;
        }
        if d <= radius {
            partial = true;
        }
    }
    if partial {
        Bounding::Partial
    } else {
        Bounding::Entire
    }
}

/// The height a plane crosses the vertical through (x, y), coded for the
/// column test: `OUTSIDE_COLUMN` when the whole column is outside,
/// `INSIDE_COLUMN` when it is all inside, `-h` when inside above `h`, `h`
/// when inside below `h`.
fn point_limit(x: f32, y: f32, plane: &Plane) -> f32 {
    let n = plane.normal;
    if n.z > EPSILON {
        let h = -((x * n.x + y * n.y + plane.d) / n.z);
        if h >= SKY_HEIGHT {
            return OUTSIDE_COLUMN;
        }
        return if h > 0.0 { -h } else { INSIDE_COLUMN };
    }
    if n.z < -EPSILON {
        let h = -((x * n.x + y * n.y + plane.d) / n.z);
        if h <= 0.0 {
            return OUTSIDE_COLUMN;
        }
        return if h >= SKY_HEIGHT { INSIDE_COLUMN } else { h };
    }
    let d = x * n.x + y * n.y + plane.d;
    if d < -EPSILON {
        OUTSIDE_COLUMN
    } else {
        INSIDE_COLUMN
    }
}

fn corner_check(bound: f32, min_z: f32, max_z: f32) -> Bounding {
    if bound == OUTSIDE_COLUMN {
        return Bounding::Outside;
    }
    if bound != INSIDE_COLUMN {
        if bound <= 0.0 {
            let h = -bound;
            if h > min_z {
                if max_z <= h {
                    return Bounding::Outside;
                }
                return Bounding::Partial;
            }
        } else if bound < max_z {
            if bound <= min_z {
                return Bounding::Outside;
            }
            return Bounding::Partial;
        }
    }
    Bounding::Entire
}

fn plane_check(corners: [f32; 4], min_z: f32, max_z: f32) -> Bounding {
    let c = corners.map(|b| corner_check(b, min_z, max_z));
    if c.iter().all(|&k| k == Bounding::Outside) {
        return Bounding::Outside;
    }
    if c.iter().all(|&k| k == Bounding::Entire) {
        return Bounding::Entire;
    }
    Bounding::Partial
}

/// Whether the box over the square with corners `(x0, y0)`..`(x1, y1)`
/// and heights `min_z..max_z` is in the view bounded by `cy` and `edges`.
#[must_use]
#[allow(clippy::too_many_arguments)]
pub fn column_check(
    x0: f32,
    y0: f32,
    x1: f32,
    y1: f32,
    min_z: f32,
    max_z: f32,
    cy: &Plane,
    edges: &[Plane],
) -> Bounding {
    let corners = |plane: &Plane| {
        [
            point_limit(x0, y0, plane),
            point_limit(x0, y1, plane),
            point_limit(x1, y0, plane),
            point_limit(x1, y1, plane),
        ]
    };
    let mut result = plane_check(corners(cy), min_z, max_z);
    if result == Bounding::Outside {
        return Bounding::Outside;
    }
    for plane in edges {
        let r = plane_check(corners(plane), min_z, max_z);
        if r == Bounding::Outside {
            return Bounding::Outside;
        }
        if r == Bounding::Partial {
            result = Bounding::Partial;
        }
    }
    result
}

/// The order a landblock grid of `width` blocks is visited, viewer block
/// first, then rings outward (indices `x * width + y`).
#[must_use]
pub fn block_order(viewer_x: i32, viewer_y: i32, width: i32) -> Vec<(i32, i32)> {
    const X_CONST: [i32; 8] = [0, 0, 0, 0, 1, 0, -1, 0];
    const X_RING: [i32; 8] = [0, -1, 0, 1, 0, -1, 0, 1];
    const X_STEP: [i32; 8] = [-1, 0, 1, 0, 1, 0, -1, 0];
    const Y_CONST: [i32; 8] = [0, 0, 0, 0, 0, 1, 0, -1];
    const Y_STEP: [i32; 8] = [0, -1, 0, 1, 0, 1, 0, -1];
    const Y_RING: [i32; 8] = [1, 0, -1, 0, 1, 0, -1, 0];
    let mut out = vec![(viewer_x, viewer_y)];
    let max_ring = viewer_x
        .max(viewer_y)
        .max(width - 1 - viewer_x)
        .max(width - 1 - viewer_y);
    for ring in 1..=max_ring {
        for step in 0..ring {
            for slot in 0..8 {
                let x = X_STEP[slot] * step + X_RING[slot] * ring + X_CONST[slot] + viewer_x;
                let y = Y_STEP[slot] * step + Y_RING[slot] * ring + Y_CONST[slot] + viewer_y;
                if x >= 0 && x < width && y >= 0 && y < width {
                    out.push((x, y));
                }
            }
        }
    }
    out
}

/// The cell order of an 8 x 8 block, far to near from the cell nearest the
/// viewer (`closest`), as indices `x * 8 + y`.
#[must_use]
pub fn cell_order_far_to_near(closest: (i32, i32)) -> [usize; 64] {
    let mut order = [0usize; 64];
    let near = block_order(closest.0, closest.1, 8);
    for (k, (x, y)) in near.iter().enumerate() {
        order[63 - k] = (*x * 8 + *y) as usize;
    }
    order
}

/// The cell of a block nearest the viewer, by the block's direction from
/// the viewer's block (`viewer_cell`: the viewer's land cell in its block).
#[must_use]
pub fn closest_cell(dx: i32, dy: i32, viewer_cell: (i32, i32)) -> (i32, i32) {
    let x = match dx.signum() {
        -1 => 7,
        1 => 0,
        _ => viewer_cell.0,
    };
    let y = match dy.signum() {
        -1 => 7,
        1 => 0,
        _ => viewer_cell.1,
    };
    (x, y)
}

/// A landblock the landscape walk can draw.
#[derive(Debug, Clone, PartialEq)]
pub struct WalkBlock {
    /// Grid coordinates.
    pub coord: (u8, u8),
    /// South-west corner in the drawing frame.
    pub origin: Vec3,
    /// Terrain heights.
    pub min_z: f32,
    /// Terrain heights.
    pub max_z: f32,
    /// Its land cells are walked and its objects and buildings loaded (the
    /// near tier); they are drawn within the walk's object range.
    pub near: bool,
    /// Buildings by land cell (`x * 8 + y`): indices into the caller's
    /// building list.
    pub buildings: Vec<(usize, usize)>,
}

/// What the walk asks the renderer to draw, in order.
pub trait WalkSink {
    /// The landscape is drawn through `views` (the full screen when
    /// `full_screen`).
    fn landscape_begin(&mut self, views: &ViewSet, view_count: usize, full_screen: bool);
    /// A far landblock (one whose land cells are not walked) is in view of
    /// some view: its whole terrain (told once a landscape, however many
    /// views see it, before any near block's cells).
    fn land_block(&mut self, block: usize);
    /// A near land cell's terrain turn (far to near), before its buildings
    /// and objects: terrain drawn after a farther building's doorway punch
    /// restores the depth the punch opened.
    fn land_cell_terrain(&mut self, block: usize, cell: usize);
    /// A land cell's objects turn (far to near): draw them in each view.
    /// Made only in blocks within the walk's object range.
    fn land_cell(&mut self, block: usize, cell: usize);
    /// A near land cell is done (every walked cell, whether or not its
    /// objects were drawn): end its sort cell.
    fn sort_cell_exit(&mut self, block: usize, cell: usize);
    /// A building is reached: decide its level; `false` skips it.
    fn building_drawable(&mut self, building: usize) -> bool;
    /// A doorway's far-depth punch, a polygon in the drawing frame, in
    /// view `view` of the current landscape views.
    fn punch(&mut self, polygon: &[Vec3], view: usize);
    /// Cells reached through a flood, far to near, each with its views.
    fn draw_cells(&mut self, cells: &[(u32, ViewSet, usize)]);
    /// A building's shell (after its look-ins).
    fn building_shell(&mut self, building: usize);
    /// The landscape of an interior frame is done: clear depth when the
    /// counter says so, then seal each exit portal (drawing frame polygon)
    /// at its own depth in each view of its cell.
    fn interior_landscape_done(&mut self, seals: &[(Vec<Vec3>, ViewSet, usize)]);
}

/// The object range a walk starts with, in landblock rings.
pub const DEFAULT_OBJECT_RING_LIMIT: u32 = 4;

/// Land cells per side of a landblock's landscape grid at `ring` from the
/// viewer's block: 8 for the 3x3 around the viewer, 4 at ring 2, 2 at
/// rings 3 and 4, 1 beyond.
#[must_use]
pub fn side_cell_count_for_ring(ring: u32) -> u32 {
    match ring {
        0 | 1 => 8,
        2 => 4,
        3 | 4 => 2,
        _ => 1,
    }
}

/// A near block at `ring` draws its objects and buildings: always on the
/// full grid, on a coarse grid within the object range, never on a
/// one-cell grid (it carries no objects).
#[must_use]
pub fn draws_objects(ring: u32, object_ring_limit: u32) -> bool {
    match side_cell_count_for_ring(ring) {
        8 => true,
        1 => false,
        _ => ring <= object_ring_limit,
    }
}

/// The walk: the cells it can reach and their view state.
#[derive(Debug)]
pub struct FrameWalk {
    cells: HashMap<u32, WalkCell>,
    views: HashMap<u32, CellViews>,
    /// Objects and buildings of a near block are drawn when its ring from
    /// the viewer's block (the larger of the x and y differences) is at
    /// most this, or at most 1; beyond it only its terrain is. The renderer
    /// sets it to the near radius each frame, so a near landblock kept a
    /// little past the edge while the player walks shows no objects. A
    /// block on a one-cell grid (ring 5 and out) draws none whatever the
    /// limit ([`side_cell_count_for_ring`]).
    pub object_ring_limit: u32,
}

impl Default for FrameWalk {
    fn default() -> Self {
        Self {
            cells: HashMap::new(),
            views: HashMap::new(),
            object_ring_limit: DEFAULT_OBJECT_RING_LIMIT,
        }
    }
}

/// One flood's state.
#[derive(Debug, Default)]
struct PView {
    outside_view: PortalView,
    cell_draw_list: Vec<u32>,
    todo: Vec<(u32, f32)>,
    draw_landscape: bool,
    active_view: Vec<Vec2>,
}

impl FrameWalk {
    /// An empty walk.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Add (or replace) a cell.
    pub fn insert_cell(&mut self, cell: WalkCell) {
        self.cells.insert(cell.cell_id, cell);
    }

    /// Remove every cell of a landblock (`id >> 16`).
    pub fn remove_landblock(&mut self, landblock: u32) {
        self.cells.retain(|id, _| id >> 16 != landblock);
        self.views.retain(|id, _| id >> 16 != landblock);
    }

    /// A cell, when present.
    #[must_use]
    pub fn cell(&self, id: u32) -> Option<&WalkCell> {
        self.cells.get(&id)
    }

    /// How many cells.
    #[must_use]
    pub fn len(&self) -> usize {
        self.cells.len()
    }

    /// True with no cells.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.cells.is_empty()
    }

    /// Move every cell by `shift` (the drawing frame's origin moved).
    pub fn translate(&mut self, shift: Vec3) {
        let t = Mat4::from_translation(shift);
        for cell in self.cells.values_mut() {
            cell.world = t * cell.world;
            cell.inverse = cell.world.inverse();
        }
    }

    fn push_view(&mut self, id: u32) {
        if self.cells.contains_key(&id) {
            self.views.entry(id).or_default().push();
        }
    }

    fn pop_view(&mut self, id: u32) {
        if let Some(v) = self.views.get_mut(&id) {
            v.pop();
        }
    }

    fn top(&self, id: u32) -> Option<&PortalView> {
        self.views.get(&id).and_then(CellViews::top)
    }

    fn top_mut(&mut self, id: u32) -> Option<&mut PortalView> {
        self.views.get_mut(&id).and_then(CellViews::top_mut)
    }

    fn num_view(&self, id: u32) -> usize {
        self.views.get(&id).map_or(0, |v| v.num_view)
    }

    /// Walk one frame rooted at `viewer_cell` and tell `sink` what to draw.
    pub fn walk_frame(
        &mut self,
        viewer_cell: u32,
        camera: &WalkCamera,
        blocks: &[WalkBlock],
        buildings: &[BuildingRef<'_>],
        sink: &mut impl WalkSink,
    ) {
        for v in self.views.values_mut() {
            v.num_view = 0;
        }
        if viewer_cell & 0xFFFF < 0x0100 || !self.cells.contains_key(&viewer_cell) {
            let mut root = PortalView::default();
            copy_full_view(&mut root, camera);
            self.draw_landscape(camera, blocks, buildings, &root, true, sink);
        } else {
            self.draw_inside(viewer_cell, camera, blocks, buildings, sink);
        }
    }

    fn draw_inside(
        &mut self,
        cell_id: u32,
        camera: &WalkCamera,
        blocks: &[WalkBlock],
        buildings: &[BuildingRef<'_>],
        sink: &mut impl WalkSink,
    ) {
        self.push_view(cell_id);
        let stabs = self.cells[&cell_id].stab_list.clone();
        for &s in &stabs {
            self.push_view(s);
        }
        if let Some(top) = self.top_mut(cell_id) {
            copy_full_view(top, camera);
        }
        let mut pview = PView {
            draw_landscape: true,
            ..PView::default()
        };
        self.construct_view(&mut pview, cell_id, NO_PORTAL, camera);
        if pview.outside_view.view_count > 0 {
            let outside = pview.outside_view.clone();
            self.draw_landscape(camera, blocks, buildings, &outside, false, sink);
            let seals = self.exit_seals(&pview);
            sink.interior_landscape_done(&seals);
        }
        let cells = self.cells_with_views(&pview);
        sink.draw_cells(&cells);
        for &s in &stabs {
            self.pop_view(s);
        }
        self.pop_view(cell_id);
    }

    /// Each flooded cell's exit portals (in the drawing frame) with the
    /// cell's views, far to near.
    fn exit_seals(&self, pview: &PView) -> Vec<(Vec<Vec3>, ViewSet, usize)> {
        let mut out = Vec::new();
        for &id in pview.cell_draw_list.iter().rev() {
            let (Some(cell), Some(top)) = (self.cells.get(&id), self.top(id)) else {
                continue;
            };
            for portal in &cell.portals {
                if portal.other_cell_id != OUTSIDE {
                    continue;
                }
                let Some(poly) = cell.polygons.get(portal.polygon) else {
                    continue;
                };
                if poly.vertices.is_empty() || rejected_by_boundary_guard(&poly.vertices) {
                    continue;
                }
                let world: Vec<Vec3> = poly
                    .vertices
                    .iter()
                    .map(|v| cell.world.transform_point3(*v))
                    .collect();
                out.push((world, top.view.clone(), top.view_count));
            }
        }
        out
    }

    /// The flood's cells far to near, each with its current views.
    fn cells_with_views(&self, pview: &PView) -> Vec<(u32, ViewSet, usize)> {
        pview
            .cell_draw_list
            .iter()
            .rev()
            .filter_map(|&id| {
                self.top(id)
                    .map(|top| (id, top.view.clone(), top.view_count))
            })
            .collect()
    }

    fn draw_landscape(
        &mut self,
        camera: &WalkCamera,
        blocks: &[WalkBlock],
        buildings: &[BuildingRef<'_>],
        views: &PortalView,
        full_screen: bool,
        sink: &mut impl WalkSink,
    ) {
        sink.landscape_begin(&views.view, views.view_count, full_screen);
        let cy = camera.cy_plane();
        // Per block: in view of any view (a union that never downgrades).
        // Objects are drawn in every cell of an in-view block (each part
        // is tested against the views on its own), so the per-cell test
        // only the terrain drawing would need is not made.
        let mut block_in_view = vec![Bounding::Outside; blocks.len()];
        for v in 0..views.view_count.max(1) {
            let edges = if views.view_count == 0 {
                Vec::new()
            } else {
                views.view.planes(v)
            };
            for (b, block) in blocks.iter().enumerate() {
                let o = block.origin;
                let bt = column_check(
                    o.x,
                    o.y,
                    o.x + BLOCK_LENGTH,
                    o.y + BLOCK_LENGTH,
                    block.min_z,
                    block.max_z,
                    &cy,
                    &edges,
                );
                if bt == Bounding::Outside {
                    continue;
                }
                block_in_view[b] = block_in_view[b].max(bt);
            }
        }
        // A far block's terrain is drawn whole, once however many views see
        // it; a near block's terrain is drawn cell by cell in the walk below.
        // Far blocks lie beyond the near ones, so drawing them first keeps
        // the far-to-near order.
        for (b, in_view) in block_in_view.iter().enumerate() {
            if *in_view != Bounding::Outside && !blocks[b].near {
                sink.land_block(b);
            }
        }
        // Far to near: blocks by ring from the viewer's block, cells far
        // to near within each.
        let viewer = (camera.eye.x / BLOCK_LENGTH).floor() as i32;
        let viewer_y = (camera.eye.y / BLOCK_LENGTH).floor() as i32;
        let viewer_cell = (
            ((camera.eye.x - viewer as f32 * BLOCK_LENGTH) / CELL_LENGTH).floor() as i32,
            ((camera.eye.y - viewer_y as f32 * BLOCK_LENGTH) / CELL_LENGTH).floor() as i32,
        );
        let viewer_cell = (viewer_cell.0.clamp(0, 7), viewer_cell.1.clamp(0, 7));
        // The game's block order (viewer's block, then rings), walked
        // backwards.
        let relative = |b: usize| {
            (
                (blocks[b].origin.x / BLOCK_LENGTH).round() as i32 - viewer,
                (blocks[b].origin.y / BLOCK_LENGTH).round() as i32 - viewer_y,
            )
        };
        let reach = (0..blocks.len())
            .map(|b| {
                let (x, y) = relative(b);
                x.abs().max(y.abs())
            })
            .max()
            .unwrap_or(0);
        let by_place: HashMap<(i32, i32), usize> =
            (0..blocks.len()).map(|b| (relative(b), b)).collect();
        let order: Vec<usize> = block_order(reach, reach, 2 * reach + 1)
            .into_iter()
            .rev()
            .filter_map(|(x, y)| by_place.get(&(x - reach, y - reach)).copied())
            .collect();
        for b in order {
            if block_in_view[b] == Bounding::Outside || !blocks[b].near {
                continue;
            }
            let bx = (blocks[b].origin.x / BLOCK_LENGTH).round() as i32 - viewer;
            let by = (blocks[b].origin.y / BLOCK_LENGTH).round() as i32 - viewer_y;
            let closest = closest_cell(bx, by, viewer_cell);
            let ring = bx.unsigned_abs().max(by.unsigned_abs());
            let objects = draws_objects(ring, self.object_ring_limit);
            for cell in cell_order_far_to_near(closest) {
                // The cell's terrain, then its buildings (a doorway punch
                // opens the depth of everything drawn so far inside the
                // doorway), then its objects. A nearer cell's terrain comes
                // later and covers a punch it stands in front of.
                sink.land_cell_terrain(b, cell);
                if objects {
                    for &(building_cell, building) in &blocks[b].buildings {
                        if building_cell == cell {
                            self.draw_building(camera, buildings, building, views, sink);
                        }
                    }
                    sink.land_cell(b, cell);
                }
                sink.sort_cell_exit(b, cell);
            }
        }
    }

    fn draw_building(
        &mut self,
        camera: &WalkCamera,
        buildings: &[BuildingRef<'_>],
        index: usize,
        views: &PortalView,
        sink: &mut impl WalkSink,
    ) {
        if !sink.building_drawable(index) {
            return;
        }
        let BuildingRef { building, tree } = buildings[index];
        if let Some(tree) = tree {
            let viewpoint = building.inverse.transform_point3(camera.eye);
            let view_count = views.view_count.max(1);
            for v in 0..view_count {
                let active: Vec<Vec2> = if views.view_count == 0 {
                    Vec::new()
                } else {
                    views.view.points(v)
                };
                for pass in [1u8, 2] {
                    let mut portals = Vec::new();
                    collect_portals(tree, 0, viewpoint, &mut portals);
                    for (portal_index, polygon) in portals {
                        self.draw_portal(
                            camera,
                            building,
                            portal_index,
                            &polygon,
                            pass,
                            &active,
                            v,
                            sink,
                        );
                    }
                }
            }
        }
        sink.building_shell(index);
    }

    #[allow(clippy::too_many_arguments)]
    fn draw_portal(
        &mut self,
        camera: &WalkCamera,
        building: &WalkBuilding,
        portal_index: usize,
        polygon: &Polygon,
        pass: u8,
        active: &[Vec2],
        view: usize,
        sink: &mut impl WalkSink,
    ) {
        let Some(portal) = building.portals.get(portal_index) else {
            return;
        };
        for &s in &portal.stab_list {
            self.push_view(s);
        }
        let viewpoint = building.inverse.transform_point3(camera.eye);
        let d = polygon.plane.distance(viewpoint);
        let side = if d > EPSILON {
            0
        } else if d < -EPSILON {
            1
        } else {
            2
        };
        let facing = if portal.portal_side != 0 {
            side == 1
        } else {
            side == 0
        };
        if facing && self.cells.contains_key(&portal.other_cell_id) {
            let to_clip = camera.view_proj * building.world;
            let mut projected: Vec<ScreenPoint> = polygon
                .vertices
                .iter()
                .map(|v| camera.screen_point(*v, &to_clip))
                .collect();
            if side != 0 {
                projected.reverse();
            }
            let mut clipped = if active.is_empty() {
                clip_against_view(&projected, &full_screen(camera))
            } else {
                clip_against_view(&projected, active)
            };
            if !clipped.is_empty()
                && self
                    .top_mut(portal.other_cell_id)
                    .is_some_and(|top| copy_view(top, &mut clipped, camera))
            {
                if pass != 2 && !rejected_by_boundary_guard(&polygon.vertices) {
                    let world: Vec<Vec3> = polygon
                        .vertices
                        .iter()
                        .map(|v| building.world.transform_point3(*v))
                        .collect();
                    sink.punch(&world, view);
                }
                if pass != 1 {
                    let mut pview = PView::default();
                    let entry = usize::try_from(portal.other_portal_id).unwrap_or(NO_PORTAL);
                    self.construct_view(&mut pview, portal.other_cell_id, entry, camera);
                    let cells = self.cells_with_views(&pview);
                    sink.draw_cells(&cells);
                }
            }
        }
        for &s in &portal.stab_list {
            self.pop_view(s);
        }
    }

    fn construct_view(&mut self, pview: &mut PView, seed: u32, entry: usize, camera: &WalkCamera) {
        pview.outside_view.reset_for_push();
        pview.todo.clear();
        pview.cell_draw_list.clear();
        self.init_cell(seed, entry, camera);
        ins_cell_todo(&mut pview.todo, seed, 0.0);
        while let Some((cell, _)) = pview.todo.pop() {
            pview.cell_draw_list.push(cell);
            if let Some(top) = self.top_mut(cell) {
                top.cell_view_done = true;
            }
            if self.clip_portals(pview, cell, 0, camera) {
                self.add_view_to_portals(pview, cell, camera);
            }
        }
    }

    fn init_cell(&mut self, cell_id: u32, entry: usize, camera: &WalkCamera) -> bool {
        let Some(cell) = self.cells.get(&cell_id) else {
            return false;
        };
        let Some(top) = self.views.get_mut(&cell_id).and_then(CellViews::top_mut) else {
            return false;
        };
        if top.view_count == 0 {
            return false;
        }
        let viewpoint = cell.inverse.transform_point3(camera.eye);
        top.cell_view_done = false;
        if top.flags.len() < cell.portals.len() {
            top.flags.resize(cell.portals.len(), PortalFlags::default());
        }
        let mut max_dist_sq = 0.0f32;
        let mut any_look_through = false;
        for (i, portal) in cell.portals.iter().enumerate() {
            let poly = cell.polygons.get(portal.polygon);
            let flags = &mut top.flags[i];
            if i == entry && !flags.in_view {
                flags.in_view = true;
                flags.seen = true;
            } else {
                flags.seen = false;
                let d = poly.map_or(0.0, |p| p.plane.distance(viewpoint));
                if (-EPSILON..=EPSILON).contains(&d) {
                    flags.in_view = false;
                    any_look_through = true;
                } else {
                    let side = u8::from(d <= EPSILON);
                    if side == portal.portal_side {
                        flags.in_view = false;
                        any_look_through = true;
                    } else {
                        flags.in_view = true;
                    }
                }
            }
            if flags.in_view
                && let Some(poly) = poly
            {
                for v in &poly.vertices {
                    max_dist_sq = max_dist_sq.max((viewpoint - *v).length_squared());
                }
            }
        }
        top.max_in_dist_sq = max_dist_sq;
        if any_look_through && top.view_count > 0 {
            for flags in top.flags.iter_mut().take(cell.portals.len()) {
                if !flags.in_view && !flags.seen {
                    flags.seen = true;
                }
            }
        }
        top.update_count = top.view_count;
        true
    }

    fn clip_portals(
        &mut self,
        pview: &mut PView,
        cell_id: u32,
        start_view: usize,
        camera: &WalkCamera,
    ) -> bool {
        let Some(cell) = self.cells.get(&cell_id) else {
            return false;
        };
        let Some(top) = self.top(cell_id) else {
            return false;
        };
        if cell.portals.is_empty() {
            return false;
        }
        let live = cell.portals.iter().enumerate().any(|(j, p)| {
            let f = top.flags.get(j).copied().unwrap_or_default();
            f.seen
                && !f.in_view
                && (p.other_cell_id == OUTSIDE || self.cells.contains_key(&p.other_cell_id))
        });
        if !live {
            return false;
        }
        let to_clip = camera.view_proj * cell.world;
        let view_count = top.view_count;
        let top_view = top.view.clone();
        let flags = top.flags.clone();
        let portals = cell.portals.clone();
        for i in start_view..view_count {
            pview.active_view = top_view.points(i);
            for (j, portal) in portals.iter().enumerate() {
                let f = flags.get(j).copied().unwrap_or_default();
                if !f.seen || f.in_view {
                    continue;
                }
                let neighbor_known = portal.other_cell_id != OUTSIDE
                    && self.cells.contains_key(&portal.other_cell_id);
                if portal.other_cell_id != OUTSIDE && !neighbor_known {
                    continue;
                }
                let Some(poly) = self.cells[&cell_id].polygons.get(portal.polygon) else {
                    continue;
                };
                let mut clipped = get_clip(
                    poly,
                    portal.portal_side,
                    &to_clip,
                    camera,
                    &pview.active_view,
                );
                if clipped.is_empty() {
                    continue;
                }
                if portal.other_cell_id == OUTSIDE {
                    if pview.draw_landscape {
                        copy_view(&mut pview.outside_view, &mut clipped, camera);
                    }
                } else {
                    if !portal.exact_match && portal.other_portal_id >= 0 {
                        clipped = self.other_portal_clip(&clipped, portal, camera);
                        pview.active_view = top_view.points(i);
                        if clipped.is_empty() {
                            continue;
                        }
                    }
                    if self.num_view(portal.other_cell_id) != 0
                        && let Some(top) = self.top_mut(portal.other_cell_id)
                    {
                        copy_view(top, &mut clipped, camera);
                    }
                }
            }
        }
        true
    }

    fn other_portal_clip(
        &self,
        clipped: &[ScreenPoint],
        portal: &CellPortal,
        camera: &WalkCamera,
    ) -> Vec<ScreenPoint> {
        let mut scratch = PortalView::default();
        let mut pts = clipped.to_vec();
        if !copy_view(&mut scratch, &mut pts, camera) {
            return Vec::new();
        }
        let Some(far) = self.cells.get(&portal.other_cell_id) else {
            return Vec::new();
        };
        let Some(far_portal) = usize::try_from(portal.other_portal_id)
            .ok()
            .and_then(|i| far.portals.get(i))
        else {
            return Vec::new();
        };
        let Some(poly) = far.polygons.get(far_portal.polygon) else {
            return Vec::new();
        };
        let active = scratch.view.points(0);
        let side = if far_portal.portal_side == 0 { 1 } else { 0 };
        get_clip(poly, side, &(camera.view_proj * far.world), camera, &active)
    }

    fn add_view_to_portals(&mut self, pview: &mut PView, cell_id: u32, camera: &WalkCamera) {
        let Some(cell) = self.cells.get(&cell_id) else {
            return;
        };
        let portals = cell.portals.clone();
        for (j, portal) in portals.iter().enumerate() {
            let neighbor = portal.other_cell_id;
            if neighbor == OUTSIDE || !self.cells.contains_key(&neighbor) {
                continue;
            }
            let f = self
                .top(cell_id)
                .and_then(|t| t.flags.get(j).copied())
                .unwrap_or_default();
            if f.in_view || !f.seen || self.num_view(neighbor) == 0 {
                continue;
            }
            let Some(ntop) = self.top(neighbor) else {
                continue;
            };
            if ntop.view_count == 0 {
                continue;
            }
            let entry = usize::try_from(portal.other_portal_id).unwrap_or(NO_PORTAL);
            if ntop.update_count == 0 {
                if self.init_cell(neighbor, entry, camera) {
                    let dist = self.top(neighbor).map_or(0.0, |t| t.max_in_dist_sq);
                    ins_cell_todo(&mut pview.todo, neighbor, dist);
                }
            } else if ntop.update_count != ntop.view_count {
                self.add_to_cell(neighbor, entry);
                if self.top(neighbor).is_some_and(|t| t.cell_view_done) {
                    self.fix_cell_list(pview, neighbor, cell_id, camera);
                }
                if let Some(t) = self.top_mut(neighbor) {
                    t.update_count = t.view_count;
                }
            } else {
                continue;
            }
            if portal.other_portal_id >= 0 {
                self.set_other_seen(neighbor, portal.other_portal_id as usize);
            }
        }
    }

    fn add_to_cell(&mut self, cell_id: u32, entry: usize) {
        let portal_count = self.cells.get(&cell_id).map_or(0, |c| c.portals.len());
        let Some(top) = self.top_mut(cell_id) else {
            return;
        };
        for _ in top.update_count..top.view_count {
            for j in 0..portal_count.min(top.flags.len()) {
                let flags = &mut top.flags[j];
                if j == entry && !flags.in_view {
                    flags.in_view = true;
                }
                if !flags.in_view && !flags.seen {
                    flags.seen = true;
                }
            }
        }
    }

    fn set_other_seen(&mut self, neighbor: u32, back_index: usize) {
        if let Some(top) = self.top_mut(neighbor)
            && let Some(flags) = top.flags.get_mut(back_index)
            && flags.in_view
        {
            flags.seen = true;
        }
    }

    fn fix_cell_list(
        &mut self,
        pview: &mut PView,
        moved: u32,
        reached_through: u32,
        camera: &WalkCamera,
    ) {
        self.adjust_cell_place(pview, moved, reached_through, 0);
        let start = self.top(moved).map_or(0, |t| t.update_count);
        if self.clip_portals(pview, moved, start, camera) {
            self.add_view_to_portals(pview, moved, camera);
        }
    }

    fn adjust_cell_place(&self, pview: &mut PView, moved: u32, reached_through: u32, depth: u32) {
        if depth > 64 || !adjust_draw_list(&mut pview.cell_draw_list, moved, reached_through) {
            return;
        }
        let (Some(cell), Some(top)) = (self.cells.get(&reached_through), self.top(reached_through))
        else {
            return;
        };
        for (i, portal) in cell.portals.iter().enumerate() {
            let f = top.flags.get(i).copied().unwrap_or_default();
            if f.seen && f.in_view && self.cells.contains_key(&portal.other_cell_id) {
                self.adjust_cell_place(pview, reached_through, portal.other_cell_id, depth + 1);
            }
        }
    }
}

fn adjust_draw_list(list: &mut Vec<u32>, moved: u32, reached_through: u32) -> bool {
    for i in 0..list.len() {
        let id = list[i];
        if id == reached_through {
            break;
        }
        if id != moved {
            continue;
        }
        let mut at = i;
        while at < list.len() && list[at] != reached_through {
            at += 1;
        }
        if at == list.len() {
            list.push(reached_through);
        }
        for k in (i + 1..=at).rev() {
            list[k] = list[k - 1];
        }
        list[i] = reached_through;
        return true;
    }
    false
}

fn ins_cell_todo(todo: &mut Vec<(u32, f32)>, cell: u32, dist: f32) {
    let mut pos = todo.len();
    while pos > 0 && dist.partial_cmp(&todo[pos - 1].1) != Some(std::cmp::Ordering::Less) {
        pos -= 1;
    }
    todo.insert(pos, (cell, dist));
}

fn get_clip(
    polygon: &Polygon,
    side: u8,
    object_to_clip: &Mat4,
    camera: &WalkCamera,
    active: &[Vec2],
) -> Vec<ScreenPoint> {
    if polygon.vertices.len() < 3 {
        return Vec::new();
    }
    let mut projected: Vec<ScreenPoint> = polygon
        .vertices
        .iter()
        .map(|v| camera.screen_point(*v, object_to_clip))
        .collect();
    if side != 0 {
        projected.reverse();
    }
    clip_against_view(&projected, active)
}

fn full_screen(camera: &WalkCamera) -> Vec<Vec2> {
    vec![
        Vec2::new(0.0, camera.height),
        Vec2::new(camera.width, camera.height),
        Vec2::new(camera.width, 0.0),
        Vec2::new(0.0, 0.0),
    ]
}

/// Walk a building's drawing tree from the viewer's side, collecting its
/// doorways far to near.
fn collect_portals(tree: &BspTree, node: usize, viewpoint: Vec3, out: &mut Vec<(usize, Polygon)>) {
    let mut current = Some(node);
    let mut guard = 0;
    while let Some(index) = current {
        guard += 1;
        if guard > 4096 {
            return;
        }
        let Some(n) = tree.get(index) else {
            return;
        };
        let (plane, positive, negative, portals) = match n {
            BspNode::Leaf => return,
            BspNode::Split {
                plane,
                positive,
                negative,
            } => (*plane, *positive, *negative, None),
            BspNode::Portal {
                plane,
                positive,
                negative,
                portals,
            } => (*plane, *positive, *negative, Some(portals)),
        };
        let d = plane.distance(viewpoint);
        let side = if d > EPSILON {
            0
        } else if d < -EPSILON {
            1
        } else {
            2
        };
        match portals {
            Some(portals) => {
                if side == 0 {
                    visit(tree, negative, viewpoint, out);
                    out.extend(portals.iter().cloned());
                    current = positive;
                } else if side == 1 {
                    visit(tree, positive, viewpoint, out);
                    out.extend(portals.iter().cloned());
                    current = negative;
                } else {
                    visit(tree, positive, viewpoint, out);
                    current = negative;
                }
            }
            None => {
                if side == 0 {
                    visit(tree, negative, viewpoint, out);
                    current = positive;
                } else {
                    visit(tree, positive, viewpoint, out);
                    current = negative;
                }
            }
        }
    }
}

fn visit(tree: &BspTree, child: Option<usize>, viewpoint: Vec3, out: &mut Vec<(usize, Polygon)>) {
    if let Some(c) = child
        && !matches!(tree.get(c), None | Some(BspNode::Leaf))
    {
        collect_portals(tree, c, viewpoint, out);
    }
}

/// The trivial reject of a depth-only portal polygon (punch or seal): every
/// vertex on the same edge of the ±12 m local square of a land cell, x or
/// y. An empty polygon is rejected.
#[must_use]
pub fn rejected_by_boundary_guard(local: &[Vec3]) -> bool {
    let all = |f: &dyn Fn(&Vec3) -> bool| local.iter().all(f);
    all(&|v| v.x == 12.0)
        || all(&|v| v.x == -12.0)
        || all(&|v| v.y == 12.0)
        || all(&|v| v.y == -12.0)
}

/// The edge half-planes of a view polygon for the GPU: `(a, b, c)` with a
/// pixel `(x, y)` inside when `a x + b y + c <= 0` for every edge (the
/// same side test the walk clips with).
#[must_use]
pub fn view_edges(points: &[Vec2]) -> Vec<[f32; 3]> {
    let n = points.len();
    (0..n)
        .map(|e| {
            let a = points[if e == n - 1 { 0 } else { e + 1 }];
            let b = points[e];
            let ex = b.x - a.x;
            let ey = b.y - a.y;
            // side(p) = (p.x - a.x) ey - (p.y - a.y) ex
            [ey, -ex, -(a.x * ey) + a.y * ex]
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use glam::camera::rh::{proj, view};

    fn camera(eye: Vec3, target: Vec3) -> WalkCamera {
        let v = view::look_at_mat4(eye, target, Vec3::Z);
        let p = proj::directx::perspective(std::f32::consts::FRAC_PI_2, 1.0, 0.1, 5000.0);
        let vp = p * v;
        WalkCamera {
            view_proj: vp,
            inverse_view_proj: vp.inverse(),
            eye,
            forward: (target - eye).normalize(),
            width: 800.0,
            height: 800.0,
        }
    }

    #[test]
    fn the_full_view_faces_inward() {
        let cam = camera(Vec3::ZERO, Vec3::Y);
        let mut v = PortalView::default();
        assert!(copy_full_view(&mut v, &cam));
        let planes = v.view.planes(0);
        assert_eq!(planes.len(), 4);
        let ahead = Vec3::new(0.0, 10.0, 0.0);
        assert_eq!(
            viewcone_check(ahead, 0.1, &cam.cy_plane(), &planes),
            Bounding::Entire
        );
        let behind = Vec3::new(0.0, -10.0, 0.0);
        assert_eq!(
            viewcone_check(behind, 0.1, &cam.cy_plane(), &planes),
            Bounding::Outside
        );
        let left = Vec3::new(-30.0, 10.0, 0.0);
        assert_eq!(
            viewcone_check(left, 0.1, &cam.cy_plane(), &planes),
            Bounding::Outside
        );
    }

    #[test]
    fn clipping_a_polygon_by_a_view_keeps_the_overlap() {
        let view = vec![
            Vec2::new(0.0, 100.0),
            Vec2::new(100.0, 100.0),
            Vec2::new(100.0, 0.0),
            Vec2::new(0.0, 0.0),
        ];
        let p = |x: f32, y: f32| ScreenPoint {
            x,
            y,
            z: 0.0,
            w: 1.0,
        };
        let input = [
            p(50.0, 150.0),
            p(150.0, 150.0),
            p(150.0, 50.0),
            p(50.0, 50.0),
        ];
        let out = clip_against_view(&input, &view);
        assert_eq!(out.len(), 4);
        for q in &out {
            assert!((50.0..=100.0).contains(&q.x) && (50.0..=100.0).contains(&q.y));
        }
        let outside = [
            p(150.0, 150.0),
            p(250.0, 150.0),
            p(250.0, 120.0),
            p(150.0, 120.0),
        ];
        assert!(clip_against_view(&outside, &view).is_empty());
    }

    #[test]
    fn view_edges_agree_with_the_clip_side_test() {
        let pts = vec![
            Vec2::new(0.0, 100.0),
            Vec2::new(100.0, 100.0),
            Vec2::new(100.0, 0.0),
            Vec2::new(0.0, 0.0),
        ];
        let edges = view_edges(&pts);
        let inside = |x: f32, y: f32| edges.iter().all(|e| e[0] * x + e[1] * y + e[2] <= 0.0);
        assert!(inside(50.0, 50.0));
        assert!(!inside(150.0, 50.0));
        assert!(!inside(50.0, -1.0));
    }

    #[test]
    fn todo_pops_the_nearest_first() {
        let mut todo = Vec::new();
        ins_cell_todo(&mut todo, 1, 5.0);
        ins_cell_todo(&mut todo, 2, 3.0);
        ins_cell_todo(&mut todo, 3, 7.0);
        assert_eq!(todo.iter().map(|t| t.0).collect::<Vec<_>>(), [3, 1, 2]);
        assert_eq!(todo.pop().map(|t| t.0), Some(2));
    }

    #[test]
    fn block_order_rings_out_from_the_viewer() {
        let order = block_order(1, 1, 3);
        assert_eq!(order.len(), 9);
        assert_eq!(order[0], (1, 1));
        let far_to_near = cell_order_far_to_near((0, 0));
        assert_eq!(far_to_near[63], 0);
        assert_eq!(closest_cell(1, 0, (3, 4)), (0, 4));
        assert_eq!(closest_cell(-1, -1, (3, 4)), (7, 7));
    }

    /// Two rooms joined by a doorway; the second has a window to the
    /// outdoors.
    fn two_rooms() -> FrameWalk {
        let square = |y: f32| {
            Polygon::new(vec![
                Vec3::new(-1.0, y, 0.0),
                Vec3::new(1.0, y, 0.0),
                Vec3::new(1.0, y, 2.0),
                Vec3::new(-1.0, y, 2.0),
            ])
            .unwrap()
        };
        let mut walk = FrameWalk::new();
        // Room A (y 0..4): doorway at y = 4 into B. Its polygon's normal is
        // -y (front faces the room... the side bit says which side the cell
        // lies on).
        let a_door = square(4.0);
        let a_side = u8::from(a_door.plane.distance(Vec3::new(0.0, 2.0, 1.0)) < 0.0);
        walk.insert_cell(WalkCell {
            cell_id: 0x0101_0100,
            portals: vec![CellPortal {
                other_cell_id: 0x0101_0101,
                polygon: 0,
                portal_side: a_side,
                other_portal_id: 0,
                exact_match: true,
            }],
            polygons: vec![a_door],
            stab_list: vec![0x0101_0101],
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        });
        let b_door = square(4.0);
        let b_side = u8::from(b_door.plane.distance(Vec3::new(0.0, 6.0, 1.0)) < 0.0);
        let b_window = square(8.0);
        let w_side = u8::from(b_window.plane.distance(Vec3::new(0.0, 6.0, 1.0)) < 0.0);
        walk.insert_cell(WalkCell {
            cell_id: 0x0101_0101,
            portals: vec![
                CellPortal {
                    other_cell_id: 0x0101_0100,
                    polygon: 0,
                    portal_side: b_side,
                    other_portal_id: 0,
                    exact_match: true,
                },
                CellPortal {
                    other_cell_id: OUTSIDE,
                    polygon: 1,
                    portal_side: w_side,
                    other_portal_id: -1,
                    exact_match: true,
                },
            ],
            polygons: vec![b_door, b_window],
            stab_list: vec![0x0101_0100],
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        });
        walk
    }

    #[derive(Default)]
    struct Recorder {
        landscape_views: Vec<usize>,
        cells: Vec<Vec<u32>>,
        seals: usize,
    }

    impl WalkSink for Recorder {
        fn landscape_begin(&mut self, _views: &ViewSet, count: usize, _full: bool) {
            self.landscape_views.push(count);
        }
        fn land_block(&mut self, _b: usize) {}
        fn land_cell_terrain(&mut self, _b: usize, _c: usize) {}
        fn land_cell(&mut self, _b: usize, _c: usize) {}
        fn sort_cell_exit(&mut self, _b: usize, _c: usize) {}
        fn building_drawable(&mut self, _b: usize) -> bool {
            true
        }
        fn punch(&mut self, _p: &[Vec3], _v: usize) {}
        fn draw_cells(&mut self, cells: &[(u32, ViewSet, usize)]) {
            self.cells.push(cells.iter().map(|c| c.0).collect());
        }
        fn building_shell(&mut self, _b: usize) {}
        fn interior_landscape_done(&mut self, seals: &[(Vec<Vec3>, ViewSet, usize)]) {
            self.seals += seals.len();
        }
    }

    #[test]
    fn the_flood_reaches_through_doorways_and_out_of_windows() {
        let mut walk = two_rooms();
        let cam = camera(Vec3::new(0.0, 1.0, 1.0), Vec3::new(0.0, 10.0, 1.0));
        let mut rec = Recorder::default();
        walk.walk_frame(0x0101_0100, &cam, &[], &[], &mut rec);
        // Far to near: B then A.
        assert_eq!(rec.cells, vec![vec![0x0101_0101, 0x0101_0100]]);
        // B's window leads outside: one landscape view and one seal.
        assert_eq!(rec.landscape_views, vec![1]);
        assert_eq!(rec.seals, 1);
    }

    #[test]
    fn looking_away_from_the_doorway_floods_nothing_beyond() {
        let mut walk = two_rooms();
        let cam = camera(Vec3::new(0.0, 3.0, 1.0), Vec3::new(0.0, -10.0, 1.0));
        let mut rec = Recorder::default();
        walk.walk_frame(0x0101_0100, &cam, &[], &[], &mut rec);
        assert_eq!(rec.cells, vec![vec![0x0101_0100]]);
        assert!(rec.landscape_views.is_empty());
    }

    #[test]
    fn an_outdoor_root_draws_the_landscape_full_screen() {
        let mut walk = two_rooms();
        let cam = camera(Vec3::new(0.0, -10.0, 1.0), Vec3::new(0.0, 10.0, 1.0));
        let mut rec = Recorder::default();
        walk.walk_frame(0x0101_0001, &cam, &[], &[], &mut rec);
        assert_eq!(rec.landscape_views, vec![1]);
        assert!(rec.cells.is_empty());
    }

    /// The landscape's turns, in order.
    #[derive(Default)]
    struct Turns(Vec<String>);

    impl WalkSink for Turns {
        fn landscape_begin(&mut self, _views: &ViewSet, _count: usize, _full: bool) {}
        fn land_block(&mut self, b: usize) {
            self.0.push(format!("block {b}"));
        }
        fn land_cell_terrain(&mut self, b: usize, c: usize) {
            self.0.push(format!("terrain {b}:{c}"));
        }
        fn land_cell(&mut self, b: usize, c: usize) {
            self.0.push(format!("objects {b}:{c}"));
        }
        fn sort_cell_exit(&mut self, _b: usize, _c: usize) {}
        fn building_drawable(&mut self, b: usize) -> bool {
            self.0.push(format!("building {b}"));
            true
        }
        fn punch(&mut self, _p: &[Vec3], _v: usize) {}
        fn draw_cells(&mut self, _cells: &[(u32, ViewSet, usize)]) {}
        fn building_shell(&mut self, _b: usize) {}
        fn interior_landscape_done(&mut self, _seals: &[(Vec<Vec3>, ViewSet, usize)]) {}
    }

    /// OpenAC `RetailFrameWalkTests.DrawLandscape_InterleavesLandCellTurnsWithBuildingAndObjectTurns_FarToNear`:
    /// a near block's terrain is drawn cell by cell in the walk, each
    /// cell's terrain before its building and its objects, so a nearer
    /// cell's terrain comes after a farther building (and its doorway
    /// punch); a far block's terrain is drawn whole, before the near
    /// block's cells.
    #[test]
    fn land_cell_terrain_interleaves_with_buildings_far_to_near() {
        let block = |x: f32, near: bool, buildings: Vec<(usize, usize)>| WalkBlock {
            coord: (0, 0),
            origin: Vec3::new(x, 0.0, 0.0),
            min_z: 0.0,
            max_z: 10.0,
            near,
            buildings,
        };
        // The building stands in land cell (3, 0), east of the viewer in
        // cell (0, 0); a far block lies two blocks east.
        let blocks = [
            block(0.0, true, vec![(3 * 8, 0)]),
            block(2.0 * BLOCK_LENGTH, false, Vec::new()),
        ];
        let house = WalkBuilding {
            portals: Vec::new(),
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        };
        let buildings = [BuildingRef {
            building: &house,
            tree: None,
        }];
        let cam = camera(Vec3::new(12.0, 12.0, 5.0), Vec3::new(100.0, 12.0, 5.0));
        let mut turns = Turns::default();
        FrameWalk::new().walk_frame(0x0000_0001, &cam, &blocks, &buildings, &mut turns);
        let at = |turn: &str| {
            turns
                .0
                .iter()
                .position(|t| t == turn)
                .unwrap_or_else(|| panic!("no {turn} in {:?}", turns.0))
        };
        assert_eq!(turns.0[0], "block 1");
        assert!(!turns.0.contains(&"block 0".to_owned()));
        for cell in 0..64 {
            let terrain = format!("terrain 0:{cell}");
            assert_eq!(turns.0.iter().filter(|t| **t == terrain).count(), 1);
            assert!(at(&terrain) < at(&format!("objects 0:{cell}")));
        }
        assert!(at("terrain 0:24") < at("building 0"));
        assert!(at("building 0") < at("objects 0:24"));
        // The viewer's own cell is the nearest: its terrain comes after the
        // building.
        assert!(at("building 0") < at("terrain 0:0"));
        assert_eq!(turns.0.last().map(String::as_str), Some("objects 0:0"));
    }

    /// Shells, objects turns and sort-cell exits of one walk.
    #[derive(Default)]
    struct Shells {
        shells: Vec<usize>,
        object_turns: usize,
        exits: usize,
    }

    impl WalkSink for Shells {
        fn landscape_begin(&mut self, _views: &ViewSet, _count: usize, _full: bool) {}
        fn land_block(&mut self, _b: usize) {}
        fn land_cell_terrain(&mut self, _b: usize, _c: usize) {}
        fn land_cell(&mut self, _b: usize, _c: usize) {
            self.object_turns += 1;
        }
        fn sort_cell_exit(&mut self, _b: usize, _c: usize) {
            self.exits += 1;
        }
        fn building_drawable(&mut self, _b: usize) -> bool {
            true
        }
        fn punch(&mut self, _p: &[Vec3], _v: usize) {}
        fn draw_cells(&mut self, _cells: &[(u32, ViewSet, usize)]) {}
        fn building_shell(&mut self, b: usize) {
            self.shells.push(b);
        }
        fn interior_landscape_done(&mut self, _seals: &[(Vec<Vec3>, ViewSet, usize)]) {}
    }

    /// OpenAC `RetailFrameWalkTests.CoarseTerrainDrawsAllBuildingShellsWithinObjectRangeAndUpdatesOnRecenter`
    /// (rings 2, 3 and 4): a near block's buildings, three of them in land
    /// cells 1, 2 and 64, are all drawn once while its ring from the
    /// viewer's block is within the object range, none when the range is
    /// one ring short (its terrain is still walked), all again when the
    /// viewer stands in the block or comes back, and none once the block
    /// has no buildings.
    #[test]
    fn near_buildings_are_drawn_within_the_object_range_and_follow_the_viewer() {
        let house = WalkBuilding {
            portals: Vec::new(),
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        };
        let buildings: Vec<BuildingRef<'_>> = (0..3)
            .map(|_| BuildingRef {
                building: &house,
                tree: None,
            })
            .collect();
        for ring in 2u32..=4 {
            let x = ring as f32 * BLOCK_LENGTH;
            let block = |buildings: Vec<(usize, usize)>| WalkBlock {
                coord: (0, 0),
                origin: Vec3::new(x, 0.0, 0.0),
                min_z: 0.0,
                max_z: 10.0,
                near: true,
                buildings,
            };
            let with = [block(vec![(0, 0), (1, 1), (63, 2)])];
            // The viewer in the first land cell of block (0, 0), looking
            // east at the block `ring` landblocks away.
            let from_home = camera(Vec3::new(12.0, 96.0, 50.0), Vec3::new(x, 96.0, 0.0));
            let from_inside = camera(
                Vec3::new(x + 12.0, 96.0, 50.0),
                Vec3::new(x + 180.0, 96.0, 0.0),
            );
            let shells = |walk: &mut FrameWalk, cam: &WalkCamera, blocks: &[WalkBlock]| {
                let mut s = Shells::default();
                walk.walk_frame(0x0000_0001, cam, blocks, &buildings, &mut s);
                assert_eq!(s.exits, 64, "every walked cell ends its sort cell");
                let mut distinct = s.shells.clone();
                distinct.sort_unstable();
                distinct.dedup();
                assert_eq!(distinct.len(), s.shells.len(), "each shell once");
                (s.shells.len(), s.object_turns)
            };
            let mut walk = FrameWalk::new();
            walk.object_ring_limit = ring;
            assert_eq!(shells(&mut walk, &from_home, &with), (3, 64), "ring {ring}");
            walk.object_ring_limit = ring - 1;
            assert_eq!(shells(&mut walk, &from_home, &with), (0, 0), "ring {ring}");
            walk.object_ring_limit = ring;
            assert_eq!(shells(&mut walk, &from_inside, &with), (3, 64));
            assert_eq!(shells(&mut walk, &from_home, &with), (3, 64));
            assert_eq!(shells(&mut walk, &from_home, &[block(Vec::new())]), (0, 64));
        }
    }

    /// OpenAC `WalkLandscapeAssembler.SideCellCountForRing` and
    /// `RetailFrameWalk.DrawLandscape`: the grid per ring, and which rings
    /// draw objects. A one-cell block (ring 5 and out) draws none, even
    /// with the object range past it (a near radius above 4).
    #[test]
    fn rings_past_four_draw_no_objects_whatever_the_range() {
        let sides: Vec<u32> = (0..7).map(side_cell_count_for_ring).collect();
        assert_eq!(sides, [8, 8, 4, 2, 2, 1, 1]);
        assert!(draws_objects(1, 0));
        assert!(draws_objects(4, 4));
        assert!(!draws_objects(4, 3));
        assert!(!draws_objects(5, 5));
        assert!(!draws_objects(6, 8));

        let house = WalkBuilding {
            portals: Vec::new(),
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        };
        let buildings = [BuildingRef {
            building: &house,
            tree: None,
        }];
        let x = 5.0 * BLOCK_LENGTH;
        let blocks = [WalkBlock {
            coord: (0, 0),
            origin: Vec3::new(x, 0.0, 0.0),
            min_z: 0.0,
            max_z: 10.0,
            near: true,
            buildings: vec![(0, 0)],
        }];
        let cam = camera(Vec3::new(12.0, 96.0, 50.0), Vec3::new(x, 96.0, 0.0));
        let mut walk = FrameWalk::new();
        walk.object_ring_limit = 5;
        let mut s = Shells::default();
        walk.walk_frame(0x0000_0001, &cam, &blocks, &buildings, &mut s);
        assert_eq!((s.shells.len(), s.object_turns, s.exits), (0, 0, 64));
    }

    /// OpenAC `RetailFrameWalk.DrawLandscape`: the viewer's block and its
    /// eight neighbours draw their objects and buildings whatever the
    /// object range.
    #[test]
    fn the_blocks_around_the_viewer_draw_their_objects_at_any_range() {
        let house = WalkBuilding {
            portals: Vec::new(),
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        };
        let buildings = [BuildingRef {
            building: &house,
            tree: None,
        }];
        let blocks = [WalkBlock {
            coord: (0, 0),
            origin: Vec3::new(BLOCK_LENGTH, 0.0, 0.0),
            min_z: 0.0,
            max_z: 10.0,
            near: true,
            buildings: vec![(9, 0)],
        }];
        let cam = camera(Vec3::new(12.0, 96.0, 50.0), Vec3::new(300.0, 96.0, 0.0));
        let mut walk = FrameWalk::new();
        walk.object_ring_limit = 0;
        let mut s = Shells::default();
        walk.walk_frame(0x0000_0001, &cam, &blocks, &buildings, &mut s);
        assert_eq!((s.shells.len(), s.object_turns, s.exits), (1, 64, 64));
    }

    // Ported from OpenAC tests/AcDream.App.Tests/Rendering/Walk/WalkVisibilityMathTests.cs.

    fn plane(normal: Vec3, d: f32) -> Plane {
        Plane { normal, d }
    }

    #[test]
    fn point_limits_encode_the_column_test() {
        assert_eq!(point_limit(0.0, 0.0, &plane(Vec3::Z, -5.0)), -5.0);
        assert_eq!(
            point_limit(0.0, 0.0, &plane(Vec3::Z, -1000.0)),
            OUTSIDE_COLUMN
        );
        assert_eq!(point_limit(0.0, 0.0, &plane(Vec3::Z, 3.0)), INSIDE_COLUMN);
        assert_eq!(point_limit(0.0, 0.0, &plane(Vec3::NEG_Z, 7.0)), 7.0);
        assert_eq!(
            point_limit(0.0, 0.0, &plane(Vec3::NEG_Z, -2.0)),
            OUTSIDE_COLUMN
        );
        let x = plane(Vec3::X, -10.0);
        assert_eq!(point_limit(5.0, 0.0, &x), OUTSIDE_COLUMN);
        assert_eq!(point_limit(15.0, 0.0, &x), INSIDE_COLUMN);
        assert_eq!(point_limit(10.0, 0.0, &x), INSIDE_COLUMN);
    }

    #[test]
    fn corner_checks_classify_the_slab() {
        use Bounding::{Entire, Outside, Partial};
        for (bound, min, max, expected) in [
            (1001.0, 0.0, 10.0, Outside),
            (0.0, 0.0, 10.0, Entire),
            (-5.0, 6.0, 10.0, Entire),
            (-5.0, 2.0, 10.0, Partial),
            (-5.0, 2.0, 4.0, Outside),
            (7.0, 2.0, 6.0, Entire),
            (7.0, 2.0, 10.0, Partial),
            (7.0, 8.0, 10.0, Outside),
            (-5.0, 2.0, 5.0, Outside),
            (-5.0, 5.0, 10.0, Entire),
            (5.0, 5.0, 10.0, Outside),
            (5.0, 2.0, 5.0, Entire),
        ] {
            assert_eq!(
                corner_check(bound, min, max),
                expected,
                "{bound} {min} {max}"
            );
        }
        assert_eq!(plane_check([1001.0; 4], 0.0, 10.0), Outside);
        assert_eq!(plane_check([0.0; 4], 0.0, 10.0), Entire);
        assert_eq!(
            plane_check([1001.0, 1001.0, 1001.0, 0.0], 0.0, 10.0),
            Partial
        );
        assert_eq!(plane_check([0.0, 0.0, 0.0, 1001.0], 0.0, 10.0), Partial);
    }

    #[test]
    fn viewcone_culls_strictly_and_is_partial_inclusively() {
        let cy = plane(Vec3::Y, 0.0);
        let check = |c: Vec3, edges: &[Plane]| viewcone_check(c, 1.0, &cy, edges);
        assert_eq!(check(Vec3::new(0.0, -5.0, 0.0), &[]), Bounding::Outside);
        assert_eq!(check(Vec3::new(0.0, -1.0, 0.0), &[]), Bounding::Partial);
        assert_eq!(check(Vec3::new(0.0, 1.0, 0.0), &[]), Bounding::Partial);
        assert_eq!(check(Vec3::new(0.0, 1.01, 0.0), &[]), Bounding::Entire);
        let edges = [plane(Vec3::X, 0.0)];
        assert_eq!(check(Vec3::new(-3.0, 5.0, 0.0), &edges), Bounding::Outside);
        assert_eq!(check(Vec3::new(0.5, 5.0, 0.0), &edges), Bounding::Partial);
        assert_eq!(check(Vec3::new(3.0, 5.0, 0.0), &edges), Bounding::Entire);
    }

    #[test]
    fn the_boundary_guard_rejects_polygons_on_one_cell_edge() {
        let v = |x: f32, y: f32| Vec3::new(x, y, 3.0);
        assert!(rejected_by_boundary_guard(&[
            v(12.0, -3.0),
            v(12.0, 0.0),
            v(12.0, 5.0)
        ]));
        assert!(rejected_by_boundary_guard(&[
            v(-3.0, -12.0),
            v(0.0, -12.0),
            v(5.0, -12.0)
        ]));
        assert!(!rejected_by_boundary_guard(&[
            v(0.0, 0.0),
            v(12.0, 0.0),
            v(5.0, 5.0)
        ]));
        assert!(!rejected_by_boundary_guard(&[
            v(11.999, 0.0),
            v(11.999, 5.0),
            v(11.999, -5.0)
        ]));
        assert!(!rejected_by_boundary_guard(&[
            v(12.0, 0.0),
            v(0.0, 12.0),
            v(12.0, 5.0)
        ]));
        assert!(!rejected_by_boundary_guard(&[
            Vec3::new(0.0, 0.0, 12.0),
            Vec3::new(1.0, 1.0, -12.0),
            Vec3::new(2.0, 0.0, 0.0)
        ]));
        assert!(rejected_by_boundary_guard(&[]));
    }

    // Ported from OpenAC tests/AcDream.App.Tests/Rendering/Walk/LandWalkOrderTests.cs.

    #[test]
    fn block_order_covers_every_slot_once_and_rings_outward() {
        for (vx, vy, width) in [(5, 5, 11), (0, 0, 11), (10, 3, 11), (1, 1, 3)] {
            let order = block_order(vx, vy, width);
            assert_eq!(order.len(), (width * width) as usize);
            assert_eq!(order[0], (vx, vy));
            let unique: std::collections::HashSet<_> = order.iter().collect();
            assert_eq!(unique.len(), order.len());
        }
        let mut previous = 0;
        for (x, y) in block_order(5, 5, 11) {
            let ring = (x - 5).abs().max((y - 5).abs());
            assert!(ring >= previous);
            previous = ring;
        }
        let decoded: Vec<i32> = block_order(1, 1, 3)
            .into_iter()
            .map(|(x, y)| x * 3 + y)
            .collect();
        assert_eq!(decoded, [4, 5, 1, 3, 7, 8, 2, 0, 6]);
        for (cx, cy) in [(0, 0), (7, 7), (3, 5), (2, 6)] {
            let order = cell_order_far_to_near((cx, cy));
            assert_eq!(order[63], (cx * 8 + cy) as usize);
            let unique: std::collections::HashSet<_> = order.iter().collect();
            assert_eq!(unique.len(), 64);
            let mut previous = i32::MAX;
            for slot in order {
                let ring = (slot as i32 / 8 - cx)
                    .abs()
                    .max((slot as i32 % 8 - cy).abs());
                assert!(ring <= previous);
                previous = ring;
            }
        }
    }

    /// OpenAC `LandWalkOrderTests.Closest_cell_matches_the_direction_switch`
    /// (8 x 8 blocks): a block's edge facing the viewer, or the viewer's own
    /// cell in its block.
    #[test]
    fn the_closest_cell_faces_the_viewer() {
        for ((dx, dy), expected) in [
            ((0, 0), (5, 3)),
            ((0, 3), (5, 0)),
            ((0, -1), (5, 7)),
            ((2, 0), (0, 3)),
            ((-4, 0), (7, 3)),
            ((-1, 1), (7, 0)),
            ((-2, -2), (7, 7)),
            ((3, 1), (0, 0)),
            ((1, -5), (0, 7)),
        ] {
            assert_eq!(closest_cell(dx, dy, (5, 3)), expected, "{dx},{dy}");
        }
    }

    // Ported from OpenAC tests/AcDream.App.Tests/Rendering/Walk/WalkPViewFloodTests.cs.

    fn flood_camera() -> WalkCamera {
        let v = view::look_at_mat4(Vec3::ZERO, Vec3::NEG_Z, Vec3::Y);
        let p = proj::directx::perspective(1.2, 1.0, 0.1, 1000.0);
        let vp = p * v;
        WalkCamera {
            view_proj: vp,
            inverse_view_proj: vp.inverse(),
            eye: Vec3::ZERO,
            forward: Vec3::NEG_Z,
            width: 640.0,
            height: 480.0,
        }
    }

    fn quad(z: f32, half: f32, facing_viewer: bool) -> Polygon {
        Polygon {
            vertices: vec![
                Vec3::new(-half, -half, z),
                Vec3::new(half, -half, z),
                Vec3::new(half, half, z),
                Vec3::new(-half, half, z),
            ],
            plane: plane(
                Vec3::new(0.0, 0.0, if facing_viewer { 1.0 } else { -1.0 }),
                if facing_viewer { -z } else { z },
            ),
        }
    }

    fn portal(other: u32, polygon: usize, side: u8, other_portal: i32) -> CellPortal {
        CellPortal {
            other_cell_id: other,
            polygon,
            portal_side: side,
            other_portal_id: other_portal,
            exact_match: false,
        }
    }

    fn add_cell(walk: &mut FrameWalk, id: u32, portals: Vec<(CellPortal, Polygon)>) {
        walk.insert_cell(WalkCell {
            cell_id: id,
            portals: portals.iter().map(|p| p.0).collect(),
            polygons: portals.into_iter().map(|p| p.1).collect(),
            stab_list: Vec::new(),
            world: Mat4::IDENTITY,
            inverse: Mat4::IDENTITY,
        });
        walk.push_view(id);
    }

    fn flood(walk: &mut FrameWalk, seed: u32, landscape: bool) -> PView {
        let cam = flood_camera();
        copy_full_view(walk.top_mut(seed).unwrap(), &cam);
        let mut pview = PView {
            draw_landscape: landscape,
            ..PView::default()
        };
        walk.construct_view(&mut pview, seed, NO_PORTAL, &cam);
        pview
    }

    #[test]
    fn the_flood_traverses_an_opening_into_the_neighbour() {
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(0x101, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        add_cell(
            &mut w,
            0x101,
            vec![(portal(0x100, 0, 1, 0), quad(-2.0, 0.5, true))],
        );
        let p = flood(&mut w, 0x100, true);
        assert_eq!(p.cell_draw_list, [0x100, 0x101]);
        assert_eq!(w.top(0x101).unwrap().view_count, 1);
        assert!(w.top(0x101).unwrap().cell_view_done);
    }

    #[test]
    fn a_facing_portal_is_not_traversed_but_sets_the_distance_key() {
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(0x101, 0, 1, 0), quad(-2.0, 0.5, true))],
        );
        add_cell(
            &mut w,
            0x101,
            vec![(portal(0x100, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        let p = flood(&mut w, 0x100, true);
        assert_eq!(p.cell_draw_list, [0x100]);
        assert!((w.top(0x100).unwrap().max_in_dist_sq - 4.5).abs() < 1e-3);
    }

    #[test]
    fn an_exit_portal_raises_the_outside_view_only_with_the_landscape() {
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(OUTSIDE, 0, 0, -1), quad(-2.0, 0.5, true))],
        );
        assert_eq!(flood(&mut w, 0x100, true).outside_view.view_count, 1);
        w.pop_view(0x100);
        w.push_view(0x100);
        assert_eq!(flood(&mut w, 0x100, false).outside_view.view_count, 0);
    }

    #[test]
    fn the_flood_never_walks_back_and_deeper_chains_pop_nearest_first() {
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(0x101, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        add_cell(
            &mut w,
            0x101,
            vec![(portal(0x100, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        let p = flood(&mut w, 0x100, true);
        assert_eq!(p.cell_draw_list.len(), 2);
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(0x101, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        add_cell(
            &mut w,
            0x101,
            vec![
                (portal(0x100, 0, 1, 0), quad(-2.0, 0.5, true)),
                (portal(0x102, 1, 0, 0), quad(-4.0, 0.4, true)),
            ],
        );
        add_cell(
            &mut w,
            0x102,
            vec![(portal(0x101, 0, 1, 1), quad(-4.0, 0.4, true))],
        );
        let p = flood(&mut w, 0x100, true);
        assert_eq!(p.cell_draw_list, [0x100, 0x101, 0x102]);
        assert_eq!(w.top(0x102).unwrap().view_count, 1);
    }

    #[test]
    fn an_unloaded_neighbour_is_skipped() {
        let mut w = FrameWalk::new();
        add_cell(
            &mut w,
            0x100,
            vec![(portal(0xDEAD, 0, 0, 0), quad(-2.0, 0.5, true))],
        );
        assert_eq!(flood(&mut w, 0x100, true).cell_draw_list, [0x100]);
    }

    // Ported from OpenAC tests/AcDream.App.Tests/Rendering/Walk/WalkScreenClipTests.cs.

    fn root_quad() -> Vec<Vec2> {
        vec![
            Vec2::new(0.0, 480.0),
            Vec2::new(640.0, 480.0),
            Vec2::new(640.0, 0.0),
            Vec2::new(0.0, 0.0),
        ]
    }

    fn pt(x: f32, y: f32) -> ScreenPoint {
        ScreenPoint {
            x,
            y,
            z: 0.0,
            w: 1.0,
        }
    }

    #[test]
    fn screen_transform_maps_the_clip_centre_to_the_screen_centre_with_y_down() {
        let cam = WalkCamera {
            view_proj: Mat4::IDENTITY,
            inverse_view_proj: Mat4::IDENTITY,
            eye: Vec3::ZERO,
            forward: Vec3::Y,
            width: 640.0,
            height: 480.0,
        };
        let c = cam.screen_point(Vec3::ZERO, &Mat4::IDENTITY);
        assert_eq!((c.x, c.y, c.w), (320.0, 240.0, 1.0));
        assert_eq!(cam.screen_point(Vec3::Y, &Mat4::IDENTITY).y, 0.0);
    }

    #[test]
    fn screen_clipping_keeps_inside_polygons_and_cuts_at_the_edges() {
        let inside = [pt(100.0, 100.0), pt(300.0, 120.0), pt(200.0, 300.0)];
        let out = clip_against_view(&inside, &root_quad());
        assert_eq!(
            out.iter().map(|p| p.x).collect::<Vec<_>>(),
            [100.0, 300.0, 200.0]
        );
        let straddling = [pt(-100.0, 100.0), pt(100.0, 100.0), pt(100.0, 300.0)];
        let out = clip_against_view(&straddling, &root_quad());
        assert!(out.len() >= 3);
        assert!(out.iter().all(|p| p.x / p.w >= -0.001));
        assert!(out.iter().any(|p| (p.x / p.w).abs() < 0.001));
        let outside = [pt(-300.0, 100.0), pt(-100.0, 100.0), pt(-200.0, 300.0)];
        assert!(clip_against_view(&outside, &root_quad()).is_empty());
        let behind = [
            pt(100.0, 100.0),
            pt(300.0, 100.0),
            ScreenPoint {
                x: 200.0,
                y: 200.0,
                z: 0.0,
                w: -0.5,
            },
        ];
        let out = clip_against_view(&behind, &root_quad());
        assert!(out.len() >= 3);
        assert!(out.iter().all(|p| p.w >= EPSILON - 1e-6));
    }
}
