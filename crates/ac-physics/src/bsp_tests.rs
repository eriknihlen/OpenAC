//! Tests of the BSP walks and the collision dispatch.
//!
//! Ported cases come from OpenAC `tests/AcDream.Core.Tests/Physics/BSPQueryTests.cs`,
//! `SphereIntersectsCellBspTests.cs` and `BoxIntersectsCellBspDifferentialTests.cs`.
//! The differential tests check every stack-based walk against a literal
//! recursive transcription of OpenAC `src/AcDream.Core/Physics/BSPQuery.cs`
//! on random trees.

use std::collections::HashMap;

use glam::{Quat, Vec3};

use crate::bsp::{CellBsp, CellBspNode, PhysicsBsp, PhysicsBspNode};
use crate::bsp_collisions::{BspFrame, BspTransition, LocalSpheres, WalkState};
use crate::geometry::{Plane, Sphere};
use crate::globals::{EPSILON, FLOOR_Z, LANDING_Z};
use crate::polygon::{
    ResolvedPolygon, adjust_sphere_to_plane, check_walkable, hits_sphere, pos_hits_sphere,
    walkable_hits_sphere,
};
use crate::state::{InsertType, ObjectInfoState, TransitionState};

// ---------------------------------------------------------------------------
// Builders

fn square(id: u16, half: f32, z: f32) -> ResolvedPolygon {
    ResolvedPolygon::new(
        id,
        vec![
            Vec3::new(-half, -half, z),
            Vec3::new(half, -half, z),
            Vec3::new(half, half, z),
            Vec3::new(-half, half, z),
        ],
        Plane::new(Vec3::Z, -z),
    )
}

fn unit_square(id: u16, z: f32) -> ResolvedPolygon {
    ResolvedPolygon::new(
        id,
        vec![
            Vec3::new(0.0, 0.0, z),
            Vec3::new(1.0, 0.0, z),
            Vec3::new(1.0, 1.0, z),
            Vec3::new(0.0, 1.0, z),
        ],
        Plane::new(Vec3::Z, -z),
    )
}

fn leaf(center: Vec3, radius: f32, polygons: Vec<u16>) -> PhysicsBspNode {
    PhysicsBspNode::Leaf {
        sphere: Sphere::new(center, radius),
        solid: false,
        polygons,
    }
}

fn one_leaf_tree(center: Vec3, radius: f32, polys: Vec<ResolvedPolygon>) -> PhysicsBsp {
    let ids = polys.iter().map(|p| p.id).collect();
    let map = polys.into_iter().map(|p| (p.id, p)).collect();
    PhysicsBsp::new(vec![leaf(center, radius, ids)], map).unwrap()
}

/// A wall on the plane y = 0 facing +y, 4 m square.
fn wall_tree() -> PhysicsBsp {
    let wall = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-2.0, 0.0, -2.0),
            Vec3::new(-2.0, 0.0, 2.0),
            Vec3::new(2.0, 0.0, 2.0),
            Vec3::new(2.0, 0.0, -2.0),
        ],
        Plane::new(Vec3::Y, 0.0),
    );
    one_leaf_tree(Vec3::ZERO, 4.0, vec![wall])
}

// ---------------------------------------------------------------------------
// A recording transition

#[derive(Debug, Clone, PartialEq)]
enum Call {
    Offset(Vec3),
    ContactPlane(Plane, u32, bool),
    Walkable(Plane, usize, Vec3),
    CollisionNormal(Vec3),
    Collide(Vec3),
    NegPolyHit(bool, Vec3),
    StepSphereUp(Vec3),
    SlideSphere(Vec3),
}

#[derive(Debug)]
struct Recorder {
    insert_type: InsertType,
    obstruction_ethereal: bool,
    bldg_check: bool,
    hits_interior_cell: bool,
    check_walkable: bool,
    step_down: bool,
    step_down_amt: f32,
    step_up: bool,
    collide: bool,
    state: ObjectInfoState,
    walk: WalkState,
    calls: Vec<Call>,
}

impl Default for Recorder {
    fn default() -> Self {
        Self {
            insert_type: InsertType::Transition,
            obstruction_ethereal: false,
            bldg_check: false,
            hits_interior_cell: false,
            check_walkable: false,
            step_down: false,
            step_down_amt: 0.0,
            step_up: false,
            collide: false,
            state: ObjectInfoState(0),
            walk: WalkState::default(),
            calls: Vec::new(),
        }
    }
}

impl BspTransition for Recorder {
    fn insert_type(&self) -> InsertType {
        self.insert_type
    }
    fn obstruction_ethereal(&self) -> bool {
        self.obstruction_ethereal
    }
    fn bldg_check(&self) -> bool {
        self.bldg_check
    }
    fn hits_interior_cell(&self) -> bool {
        self.hits_interior_cell
    }
    fn check_walkable(&self) -> bool {
        self.check_walkable
    }
    fn step_down(&self) -> bool {
        self.step_down
    }
    fn step_down_amt(&self) -> f32 {
        self.step_down_amt
    }
    fn step_up(&self) -> bool {
        self.step_up
    }
    fn collide(&self) -> bool {
        self.collide
    }
    fn object_state(&self) -> ObjectInfoState {
        self.state
    }
    fn check_cell_id(&self) -> u32 {
        0xA9B4_0100
    }
    fn walk(&mut self) -> &mut WalkState {
        &mut self.walk
    }
    fn add_offset_to_check_pos(&mut self, offset: Vec3) {
        self.calls.push(Call::Offset(offset));
    }
    fn set_contact_plane(&mut self, plane: Plane, cell_id: u32, is_water: bool) {
        self.calls
            .push(Call::ContactPlane(plane, cell_id, is_water));
    }
    fn set_walkable(&mut self, plane: Plane, local_vertices: &[Vec3], _frame: &BspFrame, up: Vec3) {
        self.calls
            .push(Call::Walkable(plane, local_vertices.len(), up));
    }
    fn set_collision_normal(&mut self, normal: Vec3) {
        self.calls.push(Call::CollisionNormal(normal));
    }
    fn set_collide(&mut self, normal: Vec3) {
        self.calls.push(Call::Collide(normal));
    }
    fn set_neg_poly_hit(&mut self, step_up: bool, normal: Vec3) {
        self.calls.push(Call::NegPolyHit(step_up, normal));
    }
    fn step_sphere_up(&mut self, normal: Vec3) -> TransitionState {
        self.calls.push(Call::StepSphereUp(normal));
        TransitionState::Ok
    }
    fn slide_sphere(&mut self, normal: Vec3) -> TransitionState {
        self.calls.push(Call::SlideSphere(normal));
        TransitionState::Slid
    }
}

impl Recorder {
    fn contact_plane(&self) -> Option<Plane> {
        self.calls.iter().find_map(|c| match c {
            Call::ContactPlane(p, ..) => Some(*p),
            _ => None,
        })
    }
}

fn spheres(sphere0: Sphere, sphere1: Option<Sphere>, curr_center: Vec3) -> LocalSpheres {
    LocalSpheres {
        sphere0,
        sphere1,
        curr_center,
        space_z: Vec3::Z,
    }
}

// ---------------------------------------------------------------------------
// BSPQueryTests.cs

#[test]
fn static_query_on_an_empty_tree_finds_nothing() {
    let bsp = PhysicsBsp::default();
    assert!(
        bsp.sphere_intersects_poly(Vec3::new(0.5, 0.5, 0.1), 0.2)
            .is_none()
    );
}

#[test]
fn static_query_rejects_outside_the_bounding_sphere() {
    let bsp = one_leaf_tree(Vec3::ZERO, 1.0, vec![unit_square(0, 0.0)]);
    assert!(
        bsp.sphere_intersects_poly(Vec3::splat(100.0), 0.5)
            .is_none()
    );
}

#[test]
fn static_query_hits_the_leaf_polygon() {
    let bsp = one_leaf_tree(Vec3::new(0.5, 0.5, 0.0), 2.0, vec![unit_square(0, 0.0)]);
    let hit = bsp
        .sphere_intersects_poly(Vec3::new(0.5, 0.5, 0.3), 0.5)
        .unwrap();
    assert_eq!(hit.poly_id, 0);
    assert!(hit.normal.z > 0.9);
}

#[test]
fn static_query_misses_a_sphere_too_high() {
    let bsp = one_leaf_tree(Vec3::new(0.5, 0.5, 0.0), 2.0, vec![unit_square(0, 0.0)]);
    assert!(
        bsp.sphere_intersects_poly(Vec3::new(0.5, 0.5, 5.0), 0.3)
            .is_none()
    );
}

#[test]
fn static_query_descends_the_positive_side() {
    let nodes = vec![
        PhysicsBspNode::Split {
            plane: Plane::new(Vec3::Z, 0.0),
            sphere: Sphere::new(Vec3::new(0.5, 0.5, 0.0), 5.0),
            positive: Some(1),
            negative: None,
        },
        leaf(Vec3::new(0.5, 0.5, 0.0), 2.0, vec![0]),
    ];
    let bsp = PhysicsBsp::new(nodes, [(0, unit_square(0, 0.0))].into_iter().collect()).unwrap();
    assert_eq!(
        bsp.sphere_intersects_poly(Vec3::new(0.5, 0.5, 0.3), 0.5)
            .unwrap()
            .poly_id,
        0
    );
}

fn two_floors(lower: f32, upper: f32) -> PhysicsBsp {
    let center = Vec3::new(0.5, 0.5, (lower + upper) * 0.5);
    let half_height = (upper - lower).abs() * 0.5 + 1.0;
    let radius = (0.5f32 * 0.5 + 0.5 * 0.5 + half_height * half_height).sqrt();
    one_leaf_tree(
        center,
        radius,
        vec![unit_square(0, lower), unit_square(1, upper)],
    )
}

fn floor_z_walk() -> WalkState {
    WalkState {
        walk_interp: 1.0,
        walkable_allowance: FLOOR_Z,
    }
}

#[test]
fn find_walkable_picks_the_lower_floor_under_the_foot() {
    let bsp = two_floors(0.0, 3.0);
    let hit = bsp
        .find_walkable_sphere(
            &mut floor_z_walk(),
            Sphere::new(Vec3::new(0.5, 0.5, 0.4), 0.48),
            0.5,
            Vec3::Z,
        )
        .unwrap();
    assert_eq!(hit.poly_id, 0);
    assert!((hit.adjusted_center.z - 0.48).abs() < 0.005);
}

#[test]
fn find_walkable_picks_the_upper_floor_above_it() {
    let bsp = two_floors(0.0, 3.0);
    let hit = bsp
        .find_walkable_sphere(
            &mut floor_z_walk(),
            Sphere::new(Vec3::new(0.5, 0.5, 3.4), 0.48),
            0.5,
            Vec3::Z,
        )
        .unwrap();
    assert_eq!(hit.poly_id, 1);
    assert!((hit.adjusted_center.z - 3.48).abs() < 0.005);
}

#[test]
fn find_walkable_finds_nothing_out_of_reach() {
    let bsp = two_floors(0.0, 3.0);
    let mut walk = floor_z_walk();
    assert!(
        bsp.find_walkable_sphere(
            &mut walk,
            Sphere::new(Vec3::new(0.5, 0.5, 10.0), 0.48),
            0.5,
            Vec3::Z
        )
        .is_none()
    );
    assert_eq!(walk, floor_z_walk());
}

#[test]
fn find_walkable_rejects_a_steep_polygon() {
    // A 60° slope: normal z = 0.5 < FLOOR_Z.
    let normal = Vec3::new(0.0, 0.75f32.sqrt(), 0.5).normalize();
    let rise = 0.75f32.sqrt() / 0.5;
    let verts = vec![
        Vec3::ZERO,
        Vec3::X,
        Vec3::new(1.0, 1.0, rise),
        Vec3::new(0.0, 1.0, rise),
    ];
    let poly = ResolvedPolygon::new(0, verts, Plane::new(normal, 0.0));
    let bsp = one_leaf_tree(Vec3::new(0.5, 0.5, 0.0), 2.0, vec![poly]);
    assert!(
        bsp.find_walkable_sphere(
            &mut floor_z_walk(),
            Sphere::new(Vec3::new(0.5, 0.5, 0.3), 0.48),
            0.5,
            Vec3::Z
        )
        .is_none()
    );
}

#[test]
fn step_down_writes_a_world_space_contact_plane() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let mut t = Recorder {
        walk: floor_z_walk(),
        step_down: true,
        step_down_amt: 0.5,
        ..Recorder::default()
    };
    let s = Sphere::new(Vec3::new(0.0, 0.0, 0.4), 0.48);
    let frame = BspFrame {
        world_origin: Vec3::new(0.0, 0.0, 94.0),
        ..BspFrame::default()
    };
    let state = bsp.find_collisions(&mut t, &spheres(s, None, s.center), &frame);
    assert_eq!(state, TransitionState::Adjusted);
    let plane = t.contact_plane().unwrap();
    assert!((plane.normal.z - 1.0).abs() < 1e-3);
    assert!((plane.d - -94.0).abs() < 0.01);
    // The sphere was lifted onto the floor and the walkable recorded with
    // world up.
    assert!(matches!(t.calls[0], Call::Offset(o) if (o.z - 0.08).abs() < 1e-5));
    assert!(t.calls.contains(&Call::Walkable(plane, 4, Vec3::Z)));
}

fn contact_recorder() -> Recorder {
    Recorder {
        state: ObjectInfoState(ObjectInfoState::CONTACT),
        ..Recorder::default()
    }
}

#[test]
fn contact_near_miss_moving_parallel_records_a_neg_poly_hit() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    let s1 = Sphere::new(Vec3::new(0.0, 0.3, 1.0), 0.48);
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, Some(s1), s0.center - Vec3::new(0.05, 0.0, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Ok);
    // The head touches too, so the head's near miss (slide) is recorded.
    assert_eq!(t.calls, vec![Call::NegPolyHit(false, Vec3::Y)]);
}

#[test]
fn contact_near_miss_moving_away_records_a_neg_poly_hit() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    let s1 = Sphere::new(Vec3::new(0.0, 0.3, 1.0), 0.48);
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, Some(s1), s0.center - Vec3::new(0.0, 0.05, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Ok);
    assert_eq!(t.calls, vec![Call::NegPolyHit(false, Vec3::Y)]);
}

#[test]
fn contact_foot_only_near_miss_asks_for_a_step_up() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    // The head clears the wall's top.
    let s1 = Sphere::new(Vec3::new(0.0, 0.3, 3.0), 0.48);
    bsp.find_collisions(
        &mut t,
        &spheres(s0, Some(s1), s0.center - Vec3::new(0.05, 0.0, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(t.calls, vec![Call::NegPolyHit(true, Vec3::Y)]);
}

#[test]
fn contact_single_sphere_near_miss_records_nothing() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, None, s0.center - Vec3::new(0.05, 0.0, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Ok);
    assert!(t.calls.is_empty());
}

#[test]
fn contact_full_hit_steps_up_and_never_records_a_near_miss() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    // Moving -y, into the wall's front.
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, None, s0.center + Vec3::new(0.0, 0.05, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Ok);
    assert_eq!(t.calls, vec![Call::StepSphereUp(Vec3::Y)]);

    // While a step is under way the same hit slides instead.
    let mut t = Recorder {
        step_up: true,
        ..contact_recorder()
    };
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, None, s0.center + Vec3::new(0.0, 0.05, 0.0)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Slid);
    assert_eq!(t.calls, vec![Call::SlideSphere(Vec3::Y)]);
}

#[test]
fn normals_leave_the_tree_rotated() {
    let bsp = wall_tree();
    let mut t = contact_recorder();
    let s0 = Sphere::new(Vec3::new(0.0, 0.3, 0.0), 0.48);
    let frame = BspFrame {
        local_to_world: Quat::from_rotation_z(std::f32::consts::FRAC_PI_2),
        ..BspFrame::default()
    };
    bsp.find_collisions(
        &mut t,
        &spheres(s0, None, s0.center + Vec3::new(0.0, 0.05, 0.0)),
        &frame,
    );
    let Call::StepSphereUp(n) = t.calls[0] else {
        panic!("{:?}", t.calls)
    };
    assert!((n - -Vec3::X).length() < 1e-6, "{n}");
}

#[test]
fn airborne_hit_flags_a_collision_with_landing_allowance() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let mut t = Recorder::default();
    let s0 = Sphere::new(Vec3::new(0.0, 0.0, 0.3), 0.48);
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, None, s0.center + Vec3::new(0.0, 0.0, 0.2)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Adjusted);
    assert_eq!(t.calls, vec![Call::Collide(Vec3::Z)]);
    assert_eq!(t.walk.walkable_allowance, LANDING_Z);
}

#[test]
fn airborne_head_hit_collides() {
    // A ceiling at z = 2 facing down; the foot is clear, the head hits.
    let ceiling = ResolvedPolygon::new(
        0,
        vec![
            Vec3::new(-2.0, -2.0, 2.0),
            Vec3::new(-2.0, 2.0, 2.0),
            Vec3::new(2.0, 2.0, 2.0),
            Vec3::new(2.0, -2.0, 2.0),
        ],
        Plane::new(-Vec3::Z, 2.0),
    );
    let bsp = one_leaf_tree(Vec3::ZERO, 4.0, vec![ceiling]);
    let mut t = Recorder::default();
    let s0 = Sphere::new(Vec3::new(0.0, 0.0, 0.5), 0.48);
    let s1 = Sphere::new(Vec3::new(0.0, 0.0, 1.7), 0.48);
    let state = bsp.find_collisions(
        &mut t,
        &spheres(s0, Some(s1), s0.center - Vec3::new(0.0, 0.0, 0.1)),
        &BspFrame::default(),
    );
    assert_eq!(state, TransitionState::Collided);
    assert_eq!(t.calls, vec![Call::CollisionNormal(-Vec3::Z)]);
}

#[test]
fn path_clipped_movers_stop_at_the_touching_point() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let s0 = Sphere::new(Vec3::new(0.0, 0.0, 0.3), 0.48);
    let curr = Vec3::new(0.0, 0.0, 1.3);

    let mut t = Recorder {
        state: ObjectInfoState(ObjectInfoState::PATH_CLIPPED),
        ..Recorder::default()
    };
    assert_eq!(
        bsp.find_collisions(&mut t, &spheres(s0, None, curr), &BspFrame::default()),
        TransitionState::Collided
    );
    assert_eq!(t.calls, vec![Call::CollisionNormal(Vec3::Z)]);

    let mut t = Recorder {
        state: ObjectInfoState(ObjectInfoState::PATH_CLIPPED | ObjectInfoState::PERFECT_CLIP),
        ..Recorder::default()
    };
    assert_eq!(
        bsp.find_collisions(&mut t, &spheres(s0, None, curr), &BspFrame::default()),
        TransitionState::Adjusted
    );
    let Call::Offset(o) = t.calls[1] else {
        panic!("{:?}", t.calls)
    };
    // The sphere ends clear of the floor, within the bisection tolerance
    // (2% of the 1 m move) of the touching height 0.48.
    let z = s0.center.z + o.z;
    assert!((0.48..0.48 + 0.02).contains(&z), "z = {z}");
}

#[test]
fn check_walkable_asks_for_ground_under_the_sphere() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let mut t = Recorder {
        check_walkable: true,
        ..Recorder::default()
    };
    let on = Sphere::new(Vec3::new(0.0, 0.0, 0.3), 0.48);
    assert_eq!(
        bsp.find_collisions(&mut t, &spheres(on, None, on.center), &BspFrame::default()),
        TransitionState::Collided
    );
    let off = Sphere::new(Vec3::new(0.0, 0.0, 3.0), 0.48);
    assert_eq!(
        bsp.find_collisions(
            &mut t,
            &spheres(off, None, off.center),
            &BspFrame::default()
        ),
        TransitionState::Ok
    );
}

#[test]
fn placement_collides_on_any_overlap() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let mut t = Recorder {
        insert_type: InsertType::Placement,
        ..Recorder::default()
    };
    let overlapping = Sphere::new(Vec3::new(0.0, 0.0, 0.3), 0.48);
    let clear = Sphere::new(Vec3::new(0.0, 0.0, 0.6), 0.48);
    let frame = BspFrame::default();
    assert_eq!(
        bsp.find_collisions(
            &mut t,
            &spheres(overlapping, None, overlapping.center),
            &frame
        ),
        TransitionState::Collided
    );
    assert_eq!(
        bsp.find_collisions(&mut t, &spheres(clear, None, clear.center), &frame),
        TransitionState::Ok
    );
    assert_eq!(
        bsp.find_collisions(
            &mut t,
            &spheres(clear, Some(overlapping), clear.center),
            &frame
        ),
        TransitionState::Collided
    );
}

#[test]
fn placement_insert_pushes_out_of_the_floor() {
    let bsp = one_leaf_tree(Vec3::ZERO, 3.0, vec![square(0, 2.0, 0.0)]);
    let frame = BspFrame::default();
    let mut t = Recorder::default();
    let clear = Sphere::new(Vec3::new(0.0, 0.0, 0.6), 0.48);
    assert_eq!(
        bsp.placement_insert(&mut t, clear, None, &frame),
        TransitionState::Ok
    );
    assert!(t.calls.is_empty());

    let mut t = Recorder::default();
    let sunk = Sphere::new(Vec3::new(0.0, 0.0, 0.3), 0.48);
    assert_eq!(
        bsp.placement_insert(&mut t, sunk, None, &frame),
        TransitionState::Adjusted
    );
    let Call::Offset(o) = t.calls[0] else {
        panic!("{:?}", t.calls)
    };
    assert!((o - Vec3::new(0.0, 0.0, 0.18)).length() < 1e-5, "{o}");

    // The offset is rotated to the world.
    let mut t = Recorder::default();
    let frame = BspFrame {
        local_to_world: Quat::from_rotation_x(std::f32::consts::PI),
        ..BspFrame::default()
    };
    bsp.placement_insert(&mut t, sunk, None, &frame);
    let Call::Offset(o) = t.calls[0] else {
        panic!("{:?}", t.calls)
    };
    assert!((o - Vec3::new(0.0, 0.0, -0.18)).length() < 1e-5, "{o}");
}

#[test]
fn placement_walk_on_a_plane_through_the_centre_tries_the_back_first() {
    // A split through the sphere's centre (distance exactly 0) with a
    // touched polygon on each side. OpenAC visits the negative side first
    // on a tie (retail visits the positive side first), so the back polygon is found, through the first-visited side,
    // and the walk answers with the centre flag rather than true.
    let floor = |id: u16, z: f32| square(id, 2.0, z);
    let nodes = vec![
        PhysicsBspNode::Split {
            plane: Plane::new(Vec3::Z, 0.0),
            sphere: Sphere::new(Vec3::ZERO, 5.0),
            positive: Some(1),
            negative: Some(2),
        },
        leaf(Vec3::ZERO, 3.0, vec![2]),
        leaf(Vec3::ZERO, 3.0, vec![1]),
    ];
    let polygons = [(1, floor(1, -0.2)), (2, floor(2, 0.2))]
        .into_iter()
        .collect();
    let bsp = PhysicsBsp::new(nodes, polygons).unwrap();
    let sphere = Sphere::new(Vec3::ZERO, 0.5);
    let (mut cs, mut hit) = (false, None);
    let answer = bsp.sphere_intersects_solid_poly_internal(&sphere, 0.5, &mut cs, &mut hit, true);
    assert_eq!(hit.map(|p| p.id), Some(1));
    assert!(!answer);
}

// ---------------------------------------------------------------------------
// Cell trees: SphereIntersectsCellBspTests.cs, BoxIntersectsCellBspDifferentialTests.cs

/// Inside is x >= 0.
fn single_plane_cell_tree() -> CellBsp {
    CellBsp::new(vec![
        CellBspNode::Split {
            plane: Plane::new(Vec3::X, 0.0),
            positive: Some(1),
            negative: None,
        },
        CellBspNode::Leaf,
    ])
    .unwrap()
}

#[test]
fn cell_tree_sphere_queries() {
    let empty = CellBsp::default();
    assert!(empty.sphere_intersects(Vec3::ZERO, 0.5));
    let leaf = CellBsp::new(vec![CellBspNode::Leaf]).unwrap();
    assert!(leaf.sphere_intersects(Vec3::ZERO, 0.5));
    let t = single_plane_cell_tree();
    assert!(t.sphere_intersects(Vec3::new(0.5, 0.0, 0.0), 0.5));
    assert!(t.sphere_intersects(Vec3::ZERO, 0.5));
    assert!(t.sphere_intersects(Vec3::new(-0.3, 0.0, 0.0), 0.5));
    assert!(!t.sphere_intersects(Vec3::new(-1.0, 0.0, 0.0), 0.5));
    assert!(t.sphere_intersects(Vec3::new(-0.5, 0.0, 0.0), 0.5));
    assert!(!t.point_inside(Vec3::new(-0.3, 0.0, 0.0)));
    assert!(t.point_inside(Vec3::ZERO));
}

#[test]
fn cell_tree_box_queries() {
    assert!(CellBsp::default().box_intersects(Vec3::splat(-1.0), Vec3::splat(1.0)));
    let t = single_plane_cell_tree();
    assert!(t.box_intersects(Vec3::new(1.0, -1.0, -1.0), Vec3::new(2.0, 1.0, 1.0)));
    assert!(!t.box_intersects(Vec3::new(-2.0, -1.0, -1.0), Vec3::new(-1.0, 1.0, 1.0)));
    assert!(t.box_intersects(Vec3::new(-0.5, -1.0, -1.0), Vec3::new(0.5, 1.0, 1.0)));
    // The epsilon band: wholly negative by the tiniest margin, or not.
    let eps = crate::BOX_PLANE_EPSILON;
    assert!(!t.box_intersects(Vec3::splat(-1.0), Vec3::new(-eps - 0.0001, 1.0, 1.0)));
    assert!(t.box_intersects(Vec3::splat(-1.0), Vec3::new(-eps + 0.0001, 1.0, 1.0)));
}

#[test]
fn deep_cell_chain() {
    let normals = [Vec3::X, Vec3::Y, Vec3::Z, Vec3::ONE.normalize()];
    const DEPTH: usize = 200;
    let mut nodes: Vec<CellBspNode> = (0..DEPTH)
        .map(|i| CellBspNode::Split {
            plane: Plane::new(normals[(DEPTH - 1 - i) % 4], 1000.0),
            positive: Some(i + 1),
            negative: None,
        })
        .collect();
    nodes.push(CellBspNode::Leaf);
    let t = CellBsp::new(nodes).unwrap();
    assert!(t.box_intersects(Vec3::splat(-1.0), Vec3::splat(1.0)));
    assert!(!t.box_intersects(Vec3::splat(-2000.0), Vec3::splat(-1900.0)));
}

// ---------------------------------------------------------------------------
// Differential: stack walks against literal recursions

struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
    fn unit(&mut self) -> f32 {
        (self.next() >> 40) as f32 / (1u64 << 24) as f32
    }
    fn range(&mut self, lo: f32, hi: f32) -> f32 {
        lo + self.unit() * (hi - lo)
    }
    fn vec(&mut self, r: f32) -> Vec3 {
        Vec3::new(self.range(-r, r), self.range(-r, r), self.range(-r, r))
    }
    fn chance(&mut self, p: f32) -> bool {
        self.unit() < p
    }
}

fn random_tree(rng: &mut Rng) -> PhysicsBsp {
    fn node(rng: &mut Rng, depth: u32, nodes: &mut Vec<PhysicsBspNode>) -> usize {
        let index = nodes.len();
        let sphere = Sphere::new(rng.vec(3.0), rng.range(0.5, 5.0));
        if depth == 0 || rng.chance(0.25) {
            let polygons = (0..rng.next() % 4)
                .map(|_| (rng.next() % 14) as u16)
                .collect();
            nodes.push(PhysicsBspNode::Leaf {
                sphere,
                solid: rng.chance(0.3),
                polygons,
            });
            return index;
        }
        nodes.push(PhysicsBspNode::Leaf {
            sphere,
            solid: false,
            polygons: vec![],
        });
        let plane = Plane::new(rng.vec(1.0).normalize_or(Vec3::Z), rng.range(-2.0, 2.0));
        let positive = rng.chance(0.85).then(|| node(rng, depth - 1, nodes));
        let negative = rng.chance(0.85).then(|| node(rng, depth - 1, nodes));
        nodes[index] = PhysicsBspNode::Split {
            plane,
            sphere,
            positive,
            negative,
        };
        index
    }
    let mut nodes = Vec::new();
    node(rng, 5, &mut nodes);
    // Twelve polygons (ids 0..12; 12 and 13 are missing on purpose), some
    // flat floors, some walls, some slopes.
    let mut polygons = HashMap::new();
    for id in 0..12u16 {
        let center = rng.vec(2.0);
        let normal = if id % 3 == 0 {
            Vec3::Z
        } else {
            rng.vec(1.0).normalize_or(Vec3::Z)
        };
        let u = normal.any_orthonormal_vector() * rng.range(0.3, 2.0);
        let v = normal.cross(u).normalize() * rng.range(0.3, 2.0);
        let vertices = [
            center - u - v,
            center + u - v,
            center + u + v,
            center - u + v,
        ];
        let positions: HashMap<u16, Vec3> = vertices
            .iter()
            .enumerate()
            .map(|(i, v)| (i as u16, *v))
            .collect();
        if let Some(p) = ResolvedPolygon::resolve(id, 0..4, &positions, 1) {
            polygons.insert(id, p);
        }
    }
    PhysicsBsp::new(nodes, polygons).unwrap()
}

fn get(bsp: &PhysicsBsp, node: Option<usize>) -> Option<&PhysicsBspNode> {
    node.and_then(|i| bsp.nodes.get(i))
}

fn polys<'a>(bsp: &'a PhysicsBsp, ids: &'a [u16]) -> impl Iterator<Item = &'a ResolvedPolygon> {
    ids.iter().filter_map(|id| bsp.polygons.get(id))
}

/// `BSPQuery.SphereIntersectsPolyInternal`.
fn ref_sphere_intersects_poly<'a>(
    bsp: &'a PhysicsBsp,
    node: Option<usize>,
    sphere: &Sphere,
    movement: Vec3,
    hit: &mut Option<&'a ResolvedPolygon>,
    contact: &mut Vec3,
) -> bool {
    let Some(n) = get(bsp, node) else {
        return false;
    };
    if !sphere.intersects(n.sphere()) {
        return false;
    }
    match n {
        PhysicsBspNode::Leaf { polygons, .. } => {
            polys(bsp, polygons).any(|p| pos_hits_sphere(p, sphere, movement, contact, hit))
        }
        PhysicsBspNode::Split {
            plane,
            positive,
            negative,
            ..
        } => {
            let dist = plane.distance(sphere.center);
            let reach = sphere.radius - EPSILON;
            if dist >= reach {
                return ref_sphere_intersects_poly(bsp, *positive, sphere, movement, hit, contact);
            }
            if dist <= -reach {
                return ref_sphere_intersects_poly(bsp, *negative, sphere, movement, hit, contact);
            }
            ref_sphere_intersects_poly(bsp, *positive, sphere, movement, hit, contact)
                || ref_sphere_intersects_poly(bsp, *negative, sphere, movement, hit, contact)
        }
    }
}

/// `BSPQuery.FindWalkableInternal`.
#[allow(clippy::too_many_arguments)]
fn ref_find_walkable<'a>(
    bsp: &'a PhysicsBsp,
    node: Option<usize>,
    walk: &mut WalkState,
    valid_pos: &mut Sphere,
    movement: Vec3,
    up: Vec3,
    hit: &mut Option<&'a ResolvedPolygon>,
    changed: &mut bool,
) {
    let Some(n) = get(bsp, node) else {
        return;
    };
    if !valid_pos.intersects(n.sphere()) {
        return;
    }
    match n {
        PhysicsBspNode::Leaf { polygons, .. } => {
            for p in polys(bsp, polygons) {
                let walkable = walkable_hits_sphere(p, walk.walkable_allowance, valid_pos, up);
                let adjusted = walkable
                    && adjust_sphere_to_plane(p, &mut walk.walk_interp, valid_pos, movement);
                if walkable && adjusted {
                    *changed = true;
                    *hit = Some(p);
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
            let reach = valid_pos.radius - EPSILON;
            if dist >= reach {
                ref_find_walkable(bsp, *positive, walk, valid_pos, movement, up, hit, changed);
                return;
            }
            if dist <= -reach {
                ref_find_walkable(bsp, *negative, walk, valid_pos, movement, up, hit, changed);
                return;
            }
            ref_find_walkable(bsp, *positive, walk, valid_pos, movement, up, hit, changed);
            ref_find_walkable(bsp, *negative, walk, valid_pos, movement, up, hit, changed);
        }
    }
}

/// `BSPQuery.HitsWalkableInternal`.
fn ref_hits_walkable(
    bsp: &PhysicsBsp,
    node: Option<usize>,
    walk: &WalkState,
    sphere: &Sphere,
    up: Vec3,
) -> bool {
    let Some(n) = get(bsp, node) else {
        return false;
    };
    if !sphere.intersects(n.sphere()) {
        return false;
    }
    match n {
        PhysicsBspNode::Leaf { polygons, .. } => polys(bsp, polygons).any(|p| {
            walkable_hits_sphere(p, walk.walkable_allowance, sphere, up)
                && check_walkable(p, sphere, up, true)
        }),
        PhysicsBspNode::Split {
            plane,
            positive,
            negative,
            ..
        } => {
            let dist = plane.distance(sphere.center);
            let reach = sphere.radius - EPSILON;
            if dist >= reach {
                return ref_hits_walkable(bsp, *positive, walk, sphere, up);
            }
            if dist <= -reach {
                return ref_hits_walkable(bsp, *negative, walk, sphere, up);
            }
            ref_hits_walkable(bsp, *positive, walk, sphere, up)
                || ref_hits_walkable(bsp, *negative, walk, sphere, up)
        }
    }
}

/// `BSPQuery.SphereIntersectsSolidInternal`.
fn ref_solid(bsp: &PhysicsBsp, node: Option<usize>, sphere: &Sphere, cc: bool) -> bool {
    let Some(n) = get(bsp, node) else {
        return false;
    };
    match n {
        PhysicsBspNode::Leaf {
            sphere: bounds,
            solid,
            polygons,
        } => {
            if polygons.is_empty() {
                return false;
            }
            if cc && *solid {
                return true;
            }
            if !sphere.intersects(bounds) {
                return false;
            }
            polys(bsp, polygons).any(|p| hits_sphere(p, sphere))
        }
        PhysicsBspNode::Split {
            plane,
            sphere: bounds,
            positive,
            negative,
        } => {
            if !sphere.intersects(bounds) {
                return false;
            }
            let dist = plane.distance(sphere.center);
            let reach = sphere.radius - EPSILON;
            if dist >= reach {
                return ref_solid(bsp, *positive, sphere, cc);
            }
            if dist <= -reach {
                return ref_solid(bsp, *negative, sphere, cc);
            }
            if dist < 0.0 {
                ref_solid(bsp, *positive, sphere, false) || ref_solid(bsp, *negative, sphere, cc)
            } else {
                ref_solid(bsp, *positive, sphere, cc) || ref_solid(bsp, *negative, sphere, false)
            }
        }
    }
}

/// `BSPQuery.SphereIntersectsSolidPolyInternal`.
fn ref_solid_poly<'a>(
    bsp: &'a PhysicsBsp,
    node: Option<usize>,
    sphere: &Sphere,
    radius: f32,
    cs: &mut bool,
    hit: &mut Option<&'a ResolvedPolygon>,
    cc: bool,
) -> bool {
    let Some(n) = get(bsp, node) else {
        return *cs;
    };
    match n {
        PhysicsBspNode::Leaf {
            sphere: bounds,
            solid,
            polygons,
        } => {
            if polygons.is_empty() {
                return false;
            }
            if cc && *solid {
                *cs = true;
            }
            if !sphere.intersects(bounds) {
                return *cs;
            }
            if let Some(p) = polys(bsp, polygons).find(|p| hits_sphere(p, sphere)) {
                *hit = Some(p);
                return true;
            }
            *cs
        }
        PhysicsBspNode::Split {
            plane,
            sphere: bounds,
            positive,
            negative,
        } => {
            if !sphere.intersects(bounds) {
                return *cs;
            }
            let dist = plane.distance(sphere.center);
            let reach = radius - EPSILON;
            if dist >= reach {
                return ref_solid_poly(bsp, *positive, sphere, radius, cs, hit, cc);
            }
            if dist <= -reach {
                return ref_solid_poly(bsp, *negative, sphere, radius, cs, hit, cc);
            }
            let (first, second) = if dist <= 0.0 {
                (*negative, *positive)
            } else {
                (*positive, *negative)
            };
            ref_solid_poly(bsp, first, sphere, radius, cs, hit, cc);
            if hit.is_some() {
                return *cs;
            }
            ref_solid_poly(bsp, second, sphere, radius, cs, hit, false)
        }
    }
}

#[test]
fn stack_walks_match_the_recursion_on_random_trees() {
    let mut rng = Rng(0x4150_3135_0BAD_F00D);
    let id = |p: Option<&ResolvedPolygon>| p.map(|p| p.id);
    let mut hits = [0usize; 6];
    for _ in 0..3000 {
        let bsp = random_tree(&mut rng);
        for _ in 0..20 {
            let sphere = Sphere::new(rng.vec(3.0), rng.range(0.2, 2.0));
            let up = if rng.chance(0.5) {
                Vec3::Z
            } else {
                rng.vec(1.0).normalize_or(Vec3::Z)
            };
            // Half the moves go down, as the walkable searches do.
            let movement = if rng.chance(0.5) {
                -up * rng.range(0.1, 1.0)
            } else {
                rng.vec(1.0)
            };
            let cc = rng.chance(0.5);

            let (mut h1, mut c1, mut h2, mut c2) = (None, Vec3::ZERO, None, Vec3::ZERO);
            let a = bsp.sphere_intersects_poly_internal(&sphere, movement, &mut h1, &mut c1);
            let b = ref_sphere_intersects_poly(&bsp, Some(0), &sphere, movement, &mut h2, &mut c2);
            assert_eq!((a, id(h1), c1), (b, id(h2), c2));
            hits[0] += usize::from(a);

            let mut w1 = WalkState {
                walk_interp: 1.0,
                walkable_allowance: rng.range(0.0, 0.9),
            };
            let mut w2 = w1;
            let (mut s1, mut s2) = (sphere, sphere);
            let (mut ch1, mut ch2) = (false, false);
            let (mut h1, mut h2) = (None, None);
            bsp.find_walkable_internal(&mut w1, &mut s1, movement, up, &mut h1, &mut ch1);
            ref_find_walkable(
                &bsp,
                Some(0),
                &mut w2,
                &mut s2,
                movement,
                up,
                &mut h2,
                &mut ch2,
            );
            assert_eq!((w1, s1, ch1, id(h1)), (w2, s2, ch2, id(h2)));
            hits[1] += usize::from(ch1);

            let a = bsp.hits_walkable_internal(&w1, &sphere, up);
            assert_eq!(a, ref_hits_walkable(&bsp, Some(0), &w1, &sphere, up));
            hits[2] += usize::from(a);

            let a = bsp.sphere_intersects_solid(&sphere, cc);
            assert_eq!(a, ref_solid(&bsp, Some(0), &sphere, cc));
            hits[3] += usize::from(a);

            let radius = sphere.radius * rng.range(0.5, 2.0);
            let start_cs = rng.chance(0.2);
            let (mut cs1, mut cs2) = (start_cs, start_cs);
            let (mut h1, mut h2) = (None, None);
            let a =
                bsp.sphere_intersects_solid_poly_internal(&sphere, radius, &mut cs1, &mut h1, cc);
            let b = ref_solid_poly(&bsp, Some(0), &sphere, radius, &mut cs2, &mut h2, cc);
            assert_eq!((a, cs1, id(h1)), (b, cs2, id(h2)));
            hits[4] += usize::from(h1.is_some());
            // A touched polygon reached through a first-visited side answers
            // with the centre flag, not true.
            hits[5] += usize::from(h1.is_some() && !a);
        }
    }
    // Every walk found something often enough for the comparison to mean
    // something.
    assert!(hits.iter().all(|h| *h > 100), "{hits:?}");
}
