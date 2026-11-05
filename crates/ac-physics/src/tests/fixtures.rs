//! Readers for OpenAC's captured fixtures (`tests/fixtures/`, copied from
//! OpenAC's `tests/AcDream.Core.Tests/Fixtures/`): cell and model geometry
//! dumps and live `resolve` captures (one JSON record per line).
//!
//! A cell dump carries no trees; like OpenAC's replay harnesses the cells
//! get a single synthetic collision leaf over every polygon (centre at the
//! cell origin, 15 m) and no containment tree. A model dump becomes one
//! leaf bounded by its dumped sphere.

#![allow(dead_code)]

use std::path::PathBuf;

use ac_host::json::{self, Node, Value};
use glam::{Mat3, Quat, Vec3};

use crate::engine::{PhysicsEngine, ResolveRequest, ResolveResult};
use crate::geometry::{Plane, Sphere};
use crate::polygon::ResolvedPolygon;
use crate::portal::PortalInfo;
use crate::state::ObjectInfoState;
use crate::world::{CellPhysics, GfxObjPhysics, Placement};
use crate::{PhysicsBody, PhysicsState, TransientState};

use super::support::single_leaf;

/// A fixture file's path.
pub fn fixture_path(dir: &str, file: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("tests")
        .join("fixtures")
        .join(dir)
        .join(file)
}

fn read_text(dir: &str, file: &str) -> String {
    let path = fixture_path(dir, file);
    std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()))
}

fn parse(text: &str) -> Node {
    json::parse(text.trim_start_matches('\u{feff}').as_bytes()).expect("fixture JSON")
}

/// The member called `name` (matched ignoring case: dumps are PascalCase,
/// captures camelCase).
pub fn member<'a>(node: &'a Node, name: &str) -> &'a Node {
    opt_member(node, name).unwrap_or_else(|| panic!("fixture member {name} missing"))
}

/// The member called `name`, when present and not null.
pub fn opt_member<'a>(node: &'a Node, name: &str) -> Option<&'a Node> {
    json::object_members(node)
        .expect("fixture object")
        .iter()
        .find(|m| m.name.eq_ignore_ascii_case(name))
        .map(|m| &m.value)
        .filter(|n| !json::is_null(n))
}

fn number_text(node: &Node) -> &str {
    match &node.value {
        Value::Number(text) | Value::String(text) => text,
        other => panic!("fixture number expected, got {other:?}"),
    }
}

/// A float as .NET wrote it (named literals allowed).
pub fn f32_of(node: &Node) -> f32 {
    match number_text(node) {
        "NaN" => f32::NAN,
        "Infinity" => f32::INFINITY,
        "-Infinity" => f32::NEG_INFINITY,
        text => text.parse().expect("fixture float"),
    }
}

/// A double as .NET wrote it.
pub fn f64_of(node: &Node) -> f64 {
    match number_text(node) {
        "NaN" => f64::NAN,
        text => text.parse().expect("fixture double"),
    }
}

/// An unsigned integer.
pub fn u32_of(node: &Node) -> u32 {
    number_text(node).parse().expect("fixture u32")
}

/// A signed integer.
pub fn i64_of(node: &Node) -> i64 {
    number_text(node).parse().expect("fixture i64")
}

/// A boolean.
pub fn bool_of(node: &Node) -> bool {
    json::boolean(node).expect("fixture bool")
}

/// A vector `{x, y, z}`.
pub fn vec3_of(node: &Node) -> Vec3 {
    Vec3::new(
        f32_of(member(node, "x")),
        f32_of(member(node, "y")),
        f32_of(member(node, "z")),
    )
}

/// A quaternion `{x, y, z, w}`.
pub fn quat_of(node: &Node) -> Quat {
    Quat::from_xyzw(
        f32_of(member(node, "x")),
        f32_of(member(node, "y")),
        f32_of(member(node, "z")),
        f32_of(member(node, "w")),
    )
}

/// A plane `{normal, d}`.
pub fn plane_of(node: &Node) -> Plane {
    Plane::new(vec3_of(member(node, "normal")), f32_of(member(node, "d")))
}

fn polygon_of(node: &Node) -> ResolvedPolygon {
    let id = u16::try_from(u32_of(member(node, "id"))).expect("polygon id");
    let vertices = json::array_items(member(node, "vertices"))
        .expect("vertices")
        .iter()
        .map(vec3_of)
        .collect();
    ResolvedPolygon::new(id, vertices, plane_of(member(node, "plane")))
}

fn polygons_of(node: Option<&Node>) -> Vec<ResolvedPolygon> {
    node.map(|n| {
        json::array_items(n)
            .expect("polygons")
            .iter()
            .map(polygon_of)
            .collect()
    })
    .unwrap_or_default()
}

/// A .NET row-vector matrix as a placement: the rows are the images of the
/// axes (the columns of the column-vector rotation), the fourth row the
/// origin.
fn placement_of(m: &Node) -> Placement {
    let f = |name: &str| f32_of(member(m, name));
    let rotation = Mat3::from_cols(
        Vec3::new(f("M11"), f("M12"), f("M13")),
        Vec3::new(f("M21"), f("M22"), f("M23")),
        Vec3::new(f("M31"), f("M32"), f("M33")),
    );
    Placement::new(
        Quat::from_mat3(&rotation),
        Vec3::new(f("M41"), f("M42"), f("M43")),
    )
}

/// A cell dump's id, placement, polygons (file order), portal polygons,
/// portals and visible cells.
pub struct CellDump {
    pub id: u32,
    pub placement: Placement,
    pub polygons: Vec<ResolvedPolygon>,
    pub portal_polygons: Vec<ResolvedPolygon>,
    pub portals: Vec<PortalInfo>,
    pub visible_cells: Vec<u32>,
}

/// Reads `0xXXXXXXXX.json` from a fixture directory.
pub fn read_cell_dump(dir: &str, cell_id: u32) -> CellDump {
    let root = parse(&read_text(dir, &format!("0x{cell_id:08X}.json")));
    let portals = opt_member(&root, "Portals")
        .map(|n| {
            json::array_items(n)
                .expect("portals")
                .iter()
                .map(|p| {
                    let u = |name: &str| u16::try_from(u32_of(member(p, name))).expect("u16");
                    PortalInfo::new(u("OtherCellId"), u("PolygonId"), u("Flags"))
                })
                .collect()
        })
        .unwrap_or_default();
    let visible_cells = opt_member(&root, "VisibleCellIds")
        .map(|n| {
            json::array_items(n)
                .expect("visible")
                .iter()
                .map(u32_of)
                .collect()
        })
        .unwrap_or_default();
    CellDump {
        id: u32_of(member(&root, "CellId")),
        placement: placement_of(member(&root, "WorldTransform")),
        polygons: polygons_of(opt_member(&root, "ResolvedPolygons")),
        portal_polygons: polygons_of(opt_member(&root, "PortalPolygons")),
        portals,
        visible_cells,
    }
}

/// A dumped cell with the replay harnesses' synthetic collision leaf.
pub fn load_cell(dir: &str, cell_id: u32) -> CellPhysics {
    let dump = read_cell_dump(dir, cell_id);
    let mut cell = CellPhysics {
        id: cell_id,
        physics_bsp: single_leaf(Vec3::ZERO, 15.0, dump.polygons),
        placement: dump.placement,
        portals: dump.portals,
        ..CellPhysics::default()
    };
    for p in dump.portal_polygons {
        cell.portal_polygons.insert(p.id, p);
    }
    for id in dump.visible_cells {
        cell.visible_cells.add(id);
    }
    cell
}

/// A dumped model (`0xXXXXXXXX.gfxobj.json`) as one collision leaf.
pub fn load_gfx_obj(dir: &str, gfx_id: u32) -> GfxObjPhysics {
    let root = parse(&read_text(dir, &format!("0x{gfx_id:08X}.gfxobj.json")));
    let polygons = polygons_of(opt_member(&root, "ResolvedPolygons"));
    let mut center = vec3_of(member(&root, "BoundingSphereOrigin"));
    let mut radius = f32_of(member(&root, "BoundingSphereRadius"));
    if radius <= 0.0 && !polygons.is_empty() {
        (center, radius) = covering_sphere(&polygons);
    }
    GfxObjPhysics {
        id: gfx_id,
        bsp: single_leaf(center, radius, polygons),
        bounding_sphere: Sphere::new(center, radius),
        visual_bounds: None,
    }
}

fn covering_sphere(polygons: &[ResolvedPolygon]) -> (Vec3, f32) {
    let vertices: Vec<Vec3> = polygons
        .iter()
        .flat_map(|p| p.vertices.iter().copied())
        .collect();
    if vertices.is_empty() {
        return (Vec3::ZERO, 0.0);
    }
    let centroid = vertices.iter().copied().sum::<Vec3>() / vertices.len() as f32;
    let max_sq = vertices
        .iter()
        .map(|v| centroid.distance_squared(*v))
        .fold(0.0f32, f32::max);
    (centroid, max_sq.sqrt())
}

// ── Live resolve captures ──────────────────────────────────────────────

/// The inputs of one captured `resolve` call.
#[derive(Debug, Clone, Copy)]
pub struct CaptureInput {
    pub current_pos: Vec3,
    pub target_pos: Vec3,
    pub cell_id: u32,
    pub sphere_radius: f32,
    pub sphere_height: f32,
    pub step_up_height: f32,
    pub step_down_height: f32,
    pub is_on_ground: bool,
    pub mover_flags: u32,
    pub moving_entity_id: u32,
}

/// What the live call returned.
#[derive(Debug, Clone, Copy)]
pub struct CaptureResult {
    pub position: Vec3,
    pub cell_id: u32,
    pub is_on_ground: bool,
    pub collision_normal_valid: bool,
    pub collision_normal: Vec3,
}

/// A body before or after the call.
#[derive(Debug, Clone)]
pub struct BodySnapshot {
    pub position: Vec3,
    pub orientation: Quat,
    pub velocity: Vec3,
    pub acceleration: Vec3,
    pub omega: Vec3,
    pub ground_normal: Vec3,
    pub sliding_normal: Vec3,
    pub contact_plane_valid: bool,
    pub contact_plane: Plane,
    pub contact_plane_cell_id: u32,
    pub contact_plane_is_water: bool,
    pub walkable_polygon_valid: bool,
    pub walkable_plane: Plane,
    pub walkable_vertices: Option<Vec<Vec3>>,
    pub walkable_up: Vec3,
    pub elasticity: f32,
    pub friction: f32,
    pub state: u32,
    pub transient_state: u32,
    pub last_update_time: f64,
}

/// One captured call.
#[derive(Debug, Clone)]
pub struct CaptureRecord {
    pub tick: i64,
    pub input: CaptureInput,
    pub body_before: Option<BodySnapshot>,
    pub result: CaptureResult,
    pub body_after: Option<BodySnapshot>,
}

fn snapshot_of(n: &Node) -> BodySnapshot {
    BodySnapshot {
        position: vec3_of(member(n, "position")),
        orientation: quat_of(member(n, "orientation")),
        velocity: vec3_of(member(n, "velocity")),
        acceleration: vec3_of(member(n, "acceleration")),
        omega: vec3_of(member(n, "omega")),
        ground_normal: vec3_of(member(n, "groundNormal")),
        sliding_normal: vec3_of(member(n, "slidingNormal")),
        contact_plane_valid: bool_of(member(n, "contactPlaneValid")),
        contact_plane: plane_of(member(n, "contactPlane")),
        contact_plane_cell_id: u32_of(member(n, "contactPlaneCellId")),
        contact_plane_is_water: bool_of(member(n, "contactPlaneIsWater")),
        walkable_polygon_valid: bool_of(member(n, "walkablePolygonValid")),
        walkable_plane: plane_of(member(n, "walkablePlane")),
        walkable_vertices: opt_member(n, "walkableVertices").map(|v| {
            json::array_items(v)
                .expect("vertices")
                .iter()
                .map(vec3_of)
                .collect()
        }),
        walkable_up: vec3_of(member(n, "walkableUp")),
        elasticity: f32_of(member(n, "elasticity")),
        friction: f32_of(member(n, "friction")),
        state: u32_of(member(n, "state")),
        transient_state: u32_of(member(n, "transientState")),
        last_update_time: f64_of(member(n, "lastUpdateTime")),
    }
}

fn record_of(n: &Node) -> CaptureRecord {
    let i = member(n, "input");
    let r = member(n, "result");
    CaptureRecord {
        tick: i64_of(member(n, "tick")),
        input: CaptureInput {
            current_pos: vec3_of(member(i, "currentPos")),
            target_pos: vec3_of(member(i, "targetPos")),
            cell_id: u32_of(member(i, "cellId")),
            sphere_radius: f32_of(member(i, "sphereRadius")),
            sphere_height: f32_of(member(i, "sphereHeight")),
            step_up_height: f32_of(member(i, "stepUpHeight")),
            step_down_height: f32_of(member(i, "stepDownHeight")),
            is_on_ground: bool_of(member(i, "isOnGround")),
            mover_flags: u32_of(member(i, "moverFlags")),
            moving_entity_id: u32_of(member(i, "movingEntityId")),
        },
        body_before: opt_member(n, "bodyBefore").map(snapshot_of),
        result: CaptureResult {
            position: vec3_of(member(r, "position")),
            cell_id: u32_of(member(r, "cellId")),
            is_on_ground: bool_of(member(r, "isOnGround")),
            collision_normal_valid: bool_of(member(r, "collisionNormalValid")),
            collision_normal: vec3_of(member(r, "collisionNormal")),
        },
        body_after: opt_member(n, "bodyAfter").map(snapshot_of),
    }
}

/// Every record of a `.jsonl` capture, in file order.
pub fn load_captures(dir: &str, file: &str) -> Vec<CaptureRecord> {
    read_text(dir, file)
        .lines()
        .map(|l| l.trim_start_matches('\u{feff}'))
        .filter(|l| !l.trim().is_empty())
        .map(|l| record_of(&parse(l)))
        .collect()
}

/// A body seeded from a snapshot, as OpenAC's harnesses do (position set,
/// no cell snap).
pub fn seed_body(s: &BodySnapshot) -> PhysicsBody {
    let mut b = PhysicsBody::new();
    b.set_position(s.position);
    b.orientation = s.orientation;
    b.velocity = s.velocity;
    b.acceleration = s.acceleration;
    b.omega = s.omega;
    b.ground_normal = s.ground_normal;
    b.sliding_normal = s.sliding_normal;
    b.contact_plane_valid = s.contact_plane_valid;
    b.contact_plane = s.contact_plane;
    b.contact_plane_cell_id = s.contact_plane_cell_id;
    b.contact_plane_is_water = s.contact_plane_is_water;
    b.walkable_polygon_valid = s.walkable_polygon_valid;
    b.walkable_plane = s.walkable_plane;
    b.walkable_vertices = s.walkable_vertices.clone();
    b.walkable_up = s.walkable_up;
    b.elasticity = s.elasticity;
    b.friction = s.friction;
    b.state = PhysicsState(s.state);
    b.transient_state = TransientState(s.transient_state);
    b.last_update_time = s.last_update_time;
    b
}

/// Replays a record's call against `engine` with `body`.
pub fn replay(
    engine: &PhysicsEngine,
    input: &CaptureInput,
    body: Option<&mut PhysicsBody>,
) -> ResolveResult {
    engine.resolve_with_transition(
        &ResolveRequest {
            current_pos: input.current_pos,
            target_pos: input.target_pos,
            cell_id: input.cell_id,
            sphere_radius: input.sphere_radius,
            sphere_height: input.sphere_height,
            step_up_height: input.step_up_height,
            step_down_height: input.step_down_height,
            is_on_ground: input.is_on_ground,
            mover_flags: ObjectInfoState(input.mover_flags),
            moving_entity_id: input.moving_entity_id,
            ..ResolveRequest::default()
        },
        body,
    )
}
