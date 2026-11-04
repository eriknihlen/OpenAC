//! The grid, the router and leaps over shapes built in code.
//!
//! Ported from OpenAC `tests/AcDream.Core.Tests/Navigation/NavGridTests.cs`;
//! the expected values are that file's.

use ac_nav::geometry::{self, LANDBLOCK_SIZE};
use ac_nav::grid::DEFAULT_CELL_SIZE;
use ac_nav::route::{self, START_HEIGHT_TOLERANCE, START_RADIUS};
use ac_nav::{
    NavAvoidance, NavBody, NavCylinder, NavGeometry, NavGrid, NavLeapAbility, NavLeapFinder,
    NavLeapLimits, NavLeapPhysics, NavRoute, NavRouteOutcome, NavRouteRequest, NavSurfaces,
    NavTerrain, NavTriangle,
};
use ac_physics::{
    CellSurface, PhysicsBody, PhysicsEngine, PhysicsState, Quat, ShadowCollisionType, ShadowEntry,
    TerrainSurface, TransientState, Vec3,
};
use glam::Vec2;
use std::collections::HashMap;

const BODY: NavBody = NavBody::new(0.48, 1.835, 0.4, 0.4);

/// Physics that moves a body in steps of 1/20 s, throws it back up at 5% of
/// the speed it lands at, and takes 95% of its speed along the ground away
/// each second.
const ORDINARY_GROUND: NavLeapPhysics = NavLeapPhysics {
    step_seconds: 0.05,
    elasticity: 0.05,
    friction: 0.95,
};

/// A body that walks at 3.12 m/s, runs at 7.3 m/s, jumps 4.2 m high at full
/// power and may drop 5 m.
const LEAPER: NavLeapAbility = NavLeapAbility {
    walk_speed: 3.12,
    run_speed: 7.3,
    full_jump_height: 4.2,
    maximum_drop: 5.0,
    physics: NavLeapPhysics {
        step_seconds: 0.0,
        elasticity: 0.0,
        friction: 0.0,
    },
};

fn sliding() -> NavLeapAbility {
    NavLeapAbility {
        physics: ORDINARY_GROUND,
        ..LEAPER
    }
}

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

fn flat_terrain() -> TerrainSurface {
    TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, None)
}

fn quad(a: Vec3, b: Vec3, c: Vec3, d: Vec3) -> Vec<NavTriangle> {
    vec![NavTriangle::new(a, b, c), NavTriangle::new(a, c, d)]
}

fn floor(x0: f32, y0: f32, x1: f32, y1: f32, z: f32) -> Vec<NavTriangle> {
    quad(v(x0, y0, z), v(x1, y0, z), v(x1, y1, z), v(x0, y1, z))
}

fn ramp(x0: f32, y0: f32, x1: f32, y1: f32, z_at_y0: f32, z_at_y1: f32) -> Vec<NavTriangle> {
    quad(
        v(x0, y0, z_at_y0),
        v(x1, y0, z_at_y0),
        v(x1, y1, z_at_y1),
        v(x0, y1, z_at_y1),
    )
}

fn wall(x0: f32, y0: f32, x1: f32, y1: f32, bottom: f32, top: f32) -> Vec<NavTriangle> {
    quad(
        v(x0, y0, bottom),
        v(x1, y1, bottom),
        v(x1, y1, top),
        v(x0, y0, top),
    )
}

fn stairs(x0: f32, x1: f32, y_start: f32, depth: f32, rise: f32, steps: i32) -> Vec<NavTriangle> {
    let mut triangles = Vec::new();
    for step in 0..steps {
        let y = y_start + step as f32 * depth;
        triangles.extend(wall(
            x0,
            y,
            x1,
            y,
            step as f32 * rise,
            (step + 1) as f32 * rise,
        ));
        triangles.extend(floor(x0, y, x1, y + depth, (step + 1) as f32 * rise));
    }
    triangles
}

fn join(parts: &[Vec<NavTriangle>]) -> Vec<NavTriangle> {
    parts.concat()
}

fn build_with(
    triangles: Vec<NavTriangle>,
    cylinders: Vec<NavCylinder>,
    size: f32,
    shells: Vec<NavTriangle>,
) -> NavGrid {
    let geometry = NavGeometry::new(
        0.0,
        0.0,
        size,
        Vec::new(),
        triangles,
        Vec::new(),
        cylinders,
        shells,
    )
    .unwrap();
    NavGrid::build(&geometry, BODY, DEFAULT_CELL_SIZE).unwrap()
}

fn build(triangles: Vec<NavTriangle>) -> NavGrid {
    build_with(triangles, Vec::new(), 32.0, Vec::new())
}

fn build_geometry(geometry: &NavGeometry) -> NavGrid {
    NavGrid::build(geometry, BODY, DEFAULT_CELL_SIZE).unwrap()
}

fn plain(radius: f32) -> (f32, NavRouteRequest<'static>) {
    (radius, NavRouteRequest::default())
}

fn find(grid: &NavGrid, from: Vec3, to: Vec3, arrival: f32) -> NavRoute {
    let (radius, request) = plain(arrival);
    route::find(grid, from, to, radius, &request)
}

fn find_with(
    grid: &NavGrid,
    from: Vec3,
    to: Vec3,
    arrival: f32,
    request: NavRouteRequest<'_>,
) -> NavRoute {
    route::find(grid, from, to, arrival, &request)
}

fn leaping(ability: NavLeapAbility) -> NavRouteRequest<'static> {
    NavRouteRequest {
        leaps: Some(ability),
        ..NavRouteRequest::default()
    }
}

/// A route's reason and leaps, for a failed assertion to show.
fn described(route: &NavRoute) -> String {
    let leaps: Vec<String> = route
        .leaps
        .iter()
        .map(|leap| {
            format!(
                "{:?} to {:?} at power {:.2} {}",
                route.legs[(leap.leg_index - 1) as usize],
                route.legs[leap.leg_index as usize],
                leap.power,
                if leap.run { "run" } else { "walk" }
            )
        })
        .collect();
    format!("{}; leaps: {}", route.reason, leaps.join(", "))
}

/// How long a jump launched upward at `launch` stays in the air before it
/// comes back down to its takeoff's height.
fn seconds_aloft(physics: NavLeapPhysics, launch: f32) -> f32 {
    physics.step_seconds
        + (launch + (launch * launch + 2.0 * 9.8 * launch * physics.step_seconds).sqrt()) / 9.8
}

fn distance_to_segment(point: Vec2, from: Vec3, to: Vec3) -> f32 {
    let start = from.truncate();
    let along = to.truncate() - start;
    let length_squared = along.length_squared();
    let t = if length_squared > 0.0 {
        ((point - start).dot(along) / length_squared).clamp(0.0, 1.0)
    } else {
        0.0
    };
    point.distance(start + along * t)
}

fn close(actual: f32, expected: f32, decimals: i32) {
    let tolerance = 0.5 * 10f32.powi(-decimals);
    assert!(
        (actual - expected).abs() <= tolerance,
        "{actual} is not {expected} to {decimals} decimals"
    );
}

fn in_range(value: f32, low: f32, high: f32) {
    assert!(
        value >= low && value <= high,
        "{value} is not within {low}..{high}"
    );
}

fn single_leap(route: &NavRoute) -> ac_nav::NavRouteLeap {
    assert_eq!(route.leaps.len(), 1, "{}", described(route));
    route.leaps[0]
}

fn leg(route: &NavRoute, index: i32) -> Vec3 {
    route.legs[index as usize]
}

#[test]
fn a_flat_floor_is_stood_on_and_its_edge_is_too_tight_to_walk() {
    let grid = build(floor(0.0, 0.0, 10.0, 10.0, 0.0));

    let middle = grid.find_node(v(5.125, 5.125, 0.0), 0.1, 0.5);

    assert!(middle >= 0);
    close(grid.position(middle).z, 0.0, 3);
    assert_eq!(grid.find_node(v(0.125, 5.125, 0.0), 0.1, 0.5), -1);
}

#[test]
fn the_open_sea_is_no_floor_and_the_shore_beside_it_is_tight() {
    let water_everywhere = [0x10u8 << 2; 81];
    let sea = TerrainSurface::new(&[0; 81], &[0.0; 256], 0, 0, Some(&water_everywhere));
    let land = flat_terrain();
    assert!(sea.is_entirely_water());

    let grid = build_geometry(
        &NavGeometry::new(
            176.0,
            0.0,
            32.0,
            vec![
                NavTerrain::new(land, 0.0, 0.0),
                NavTerrain::new(sea, 192.0, 0.0),
            ],
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );

    let (inland, inland_nodes) = grid.nodes_in_column(32, 64);
    let (shore, shore_nodes) = grid.nodes_in_column(63, 64);
    let (_, sea_nodes) = grid.nodes_in_column(64, 64);
    assert_eq!(inland_nodes, 1);
    assert!(grid.is_clear(inland));
    assert_eq!(shore_nodes, 1);
    assert!(!grid.is_clear(shore));
    assert_eq!(sea_nodes, 0);
}

#[test]
fn ground_over_a_cellar_is_still_floor() {
    let ground = TerrainSurface::new(&[0; 81], &[10.0; 256], 0, 0, None);

    let grid = build_geometry(
        &NavGeometry::new(
            0.0,
            0.0,
            32.0,
            vec![NavTerrain::new(ground, 0.0, 0.0)],
            floor(8.0, 8.0, 24.0, 24.0, 5.0),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );

    assert!(grid.find_node(v(16.0, 16.0, 10.0), 0.2, 0.5) >= 0);
    assert!(grid.find_node(v(16.0, 16.0, 5.0), 0.2, 0.5) >= 0);
}

#[test]
fn a_route_across_open_floor_is_one_straight_leg() {
    let grid = build(floor(0.0, 0.0, 20.0, 20.0, 0.0));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(15.0, 12.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert_eq!(route.legs.len(), 2);
}

#[test]
fn a_wall_is_passed_through_its_doorway() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 9.0, 10.0, 0.0, 3.0),
        wall(11.0, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(3.0, 5.0, 0.0), v(17.0, 15.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(
        route
            .path
            .iter()
            .any(|p| p.y > 9.9 && p.y < 10.4 && p.x > 9.0 && p.x < 11.0)
    );
}

#[test]
fn a_doorway_just_wider_than_the_body_is_passed() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 9.4, 10.0, 0.0, 3.0),
        wall(10.6, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(3.0, 5.0, 0.0), v(17.0, 15.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
}

#[test]
fn a_doorway_narrower_than_the_body_is_not_passed_and_the_route_ends_where_the_goal_can_be_seen_through_it()
 {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 9.75, 10.0, 0.0, 3.0),
        wall(10.25, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(3.0, 5.0, 0.0), v(17.0, 15.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(route.path.iter().all(|p| p.y < 10.0));
    assert!(
        route.reason.contains("nearest spot that can"),
        "{}",
        route.reason
    );
    assert!(route.ends_in_sight);
}

#[test]
fn a_ramp_climbs_from_the_ground_onto_a_deck() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 10.0, 0.0),
        ramp(6.0, 10.0, 10.0, 16.0, 0.0, 2.5),
        floor(0.0, 16.0, 20.0, 20.0, 2.5),
    ]));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(16.0, 18.0, 2.5), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    close(route.path.last().unwrap().z, 2.5, 1);
    assert!(
        route
            .path
            .iter()
            .any(|p| p.y > 11.0 && p.y < 15.0 && p.z > 0.3 && p.z < 2.2)
    );
}

/// A place on top of a raised block is arrived at on the block. Without
/// leaps nothing walks up onto it, so the route says it does not reach the
/// goal's floor rather than ending on the ground beside it; with leaps the
/// route jumps up and ends on top.
#[test]
fn a_place_on_a_raised_top_is_arrived_at_on_the_top_and_never_on_the_ground_beside_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        floor(9.0, 9.0, 13.0, 13.0, 1.0),
        wall(9.0, 9.0, 13.0, 9.0, 0.0, 1.0),
        wall(13.0, 9.0, 13.0, 13.0, 0.0, 1.0),
        wall(13.0, 13.0, 9.0, 13.0, 0.0, 1.0),
        wall(9.0, 13.0, 9.0, 9.0, 0.0, 1.0),
    ]));
    let top = v(11.0, 11.0, 1.0);

    let any_floor = find(&grid, v(3.0, 3.0, 0.0), top, 2.5);
    let walked = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        top,
        2.5,
        NavRouteRequest {
            arrive_on_goal_floor: true,
            ..NavRouteRequest::default()
        },
    );
    let leapt = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        top,
        2.5,
        NavRouteRequest {
            leaps: Some(LEAPER),
            arrive_on_goal_floor: true,
            ..NavRouteRequest::default()
        },
    );

    assert!(
        any_floor.outcome == NavRouteOutcome::Routed && any_floor.path.last().unwrap().z < 0.5,
        "{}",
        described(&any_floor)
    );
    assert!(
        walked.outcome != NavRouteOutcome::Routed,
        "{}",
        described(&walked)
    );
    assert!(walked.reason.contains("floor"));
    assert!(
        leapt.outcome == NavRouteOutcome::Routed,
        "{}",
        described(&leapt)
    );
    in_range(leapt.path.last().unwrap().z, 0.9, 1.1);
}

/// An object whose own collision is in the grid, such as a life stone,
/// stands with its middle inside itself, so no spot sees that point through
/// its walls. Told how wide the object is, the route sees its side instead
/// and arrives beside it in sight, even with spots to keep out of.
#[test]
fn an_object_whose_collision_is_in_the_grid_is_arrived_at_beside_it_in_sight() {
    let grid = build(join(&[
        floor(0.0, 0.0, 24.0, 24.0, 0.0),
        floor(10.0, 10.0, 13.0, 13.0, 3.0),
        wall(10.0, 10.0, 13.0, 10.0, 0.0, 3.0),
        wall(13.0, 10.0, 13.0, 13.0, 0.0, 3.0),
        wall(13.0, 13.0, 10.0, 13.0, 0.0, 3.0),
        wall(10.0, 13.0, 10.0, 10.0, 0.0, 3.0),
    ]));
    let stone = v(11.5, 11.5, 0.0);
    let portal = [NavAvoidance::new(v(3.0, 20.0, 0.0), 1.5)];

    let point_only = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        stone,
        2.5,
        NavRouteRequest {
            avoid: &portal,
            ..NavRouteRequest::default()
        },
    );
    let beside = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        stone,
        2.5,
        NavRouteRequest {
            avoid: &portal,
            goal_radius: 2.2,
            ..NavRouteRequest::default()
        },
    );

    assert!(
        point_only.outcome != NavRouteOutcome::Routed,
        "{}",
        described(&point_only)
    );
    assert!(
        beside.outcome == NavRouteOutcome::Routed && beside.reason == "routed",
        "{}",
        described(&beside)
    );
    assert!(beside.ends_in_sight);
    in_range(
        beside
            .path
            .last()
            .unwrap()
            .truncate()
            .distance(stone.truncate()),
        1.5,
        4.7,
    );
}

/// An object whose collision is one tall cylinder, such as a town sign, is
/// seen at its side.
#[test]
fn a_tall_cylinder_object_is_arrived_at_beside_it_in_sight() {
    let sign = NavCylinder::new(v(12.0, 12.0, 0.0), 0.9, 6.7);
    let grid = build_with(
        floor(0.0, 0.0, 24.0, 24.0, 0.0),
        vec![sign],
        32.0,
        Vec::new(),
    );
    let portal = [NavAvoidance::new(v(3.0, 20.0, 0.0), 1.5)];

    let route = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        sign.base,
        2.0,
        NavRouteRequest {
            avoid: &portal,
            goal_radius: sign.radius,
            ..NavRouteRequest::default()
        },
    );

    assert!(
        route.outcome == NavRouteOutcome::Routed && route.reason == "routed",
        "{}",
        described(&route)
    );
    assert!(route.ends_in_sight);
}

/// The goal's floor is all the floor a body walks across from it, so a
/// place on a deck is reached up a ramp.
#[test]
fn a_place_on_a_deck_is_still_reached_up_the_ramp_that_joins_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 10.0, 0.0),
        ramp(6.0, 10.0, 10.0, 16.0, 0.0, 2.5),
        floor(0.0, 16.0, 20.0, 20.0, 2.5),
    ]));

    let route = find_with(
        &grid,
        v(3.0, 3.0, 0.0),
        v(16.0, 18.0, 2.5),
        1.0,
        NavRouteRequest {
            arrive_on_goal_floor: true,
            ..NavRouteRequest::default()
        },
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    close(route.path.last().unwrap().z, 2.5, 1);
}

fn block_triangles() -> Vec<NavTriangle> {
    join(&[
        floor(9.0, 9.0, 13.0, 13.0, 1.0),
        wall(9.0, 9.0, 13.0, 9.0, 0.0, 1.0),
        wall(13.0, 9.0, 13.0, 13.0, 0.0, 1.0),
        wall(13.0, 13.0, 9.0, 13.0, 0.0, 1.0),
        wall(9.0, 13.0, 9.0, 9.0, 0.0, 1.0),
    ])
}

/// A route onto an object ends on the highest floor standing on the
/// object's own surfaces, never on the ground around it.
#[test]
fn a_route_onto_an_object_ends_on_its_top_and_never_on_the_ground_beside_it() {
    let block = block_triangles();
    let grid = build(join(&[floor(0.0, 0.0, 20.0, 20.0, 0.0), block.clone()]));
    let surfaces = NavSurfaces {
        triangles: block,
        cylinders: Vec::new(),
    };

    let walked = route::find_onto(
        &grid,
        v(3.0, 3.0, 0.0),
        &surfaces,
        2.5,
        &NavRouteRequest::default(),
    );
    let leapt = route::find_onto(&grid, v(3.0, 3.0, 0.0), &surfaces, 2.5, &leaping(LEAPER));

    assert!(
        walked.outcome != NavRouteOutcome::Routed,
        "{}",
        described(&walked)
    );
    assert!(
        leapt.outcome == NavRouteOutcome::Routed,
        "{}",
        described(&leapt)
    );
    let end = *leapt.path.last().unwrap();
    in_range(end.z, 0.9, 1.1);
    in_range(end.x, 9.0, 13.0);
    in_range(end.y, 9.0, 13.0);
}

/// An object with a ramp up to a deck is stood on at the deck, its highest
/// floor, reached up the ramp.
#[test]
fn an_object_with_a_ramp_is_stood_on_at_its_deck() {
    let stage = join(&[
        ramp(6.0, 10.0, 10.0, 16.0, 0.0, 2.5),
        floor(4.0, 16.0, 12.0, 20.0, 2.5),
    ]);
    let grid = build(join(&[floor(0.0, 0.0, 20.0, 10.0, 0.0), stage.clone()]));

    let route = route::find_onto(
        &grid,
        v(3.0, 3.0, 0.0),
        &NavSurfaces {
            triangles: stage,
            cylinders: Vec::new(),
        },
        1.0,
        &NavRouteRequest::default(),
    );

    assert!(
        route.outcome == NavRouteOutcome::Routed,
        "{}",
        described(&route)
    );
    close(route.path.last().unwrap().z, 2.5, 1);
}

/// The top of a post is a perch a body stands on, so a route onto the post
/// ends on it.
#[test]
fn a_route_onto_a_post_ends_on_its_top() {
    let post = NavCylinder::new(v(10.0, 10.0, 0.0), 0.9, 1.2);
    let grid = build_with(
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        vec![post],
        32.0,
        Vec::new(),
    );

    let route = route::find_onto(
        &grid,
        v(4.0, 4.0, 0.0),
        &NavSurfaces {
            triangles: Vec::new(),
            cylinders: vec![post],
        },
        2.5,
        &leaping(LEAPER),
    );

    assert!(
        route.outcome == NavRouteOutcome::Routed,
        "{}",
        described(&route)
    );
    in_range(route.path.last().unwrap().z, 1.15, 1.25);
}

/// An object with nothing a body stands on, such as a thin wall, has no top
/// to route onto.
#[test]
fn an_object_with_nothing_to_stand_on_has_no_top() {
    let thin = wall(8.0, 12.0, 16.0, 12.0, 0.0, 3.0);
    let grid = build(join(&[floor(0.0, 0.0, 20.0, 20.0, 0.0), thin.clone()]));

    let route = route::find_onto(
        &grid,
        v(3.0, 3.0, 0.0),
        &NavSurfaces {
            triangles: thin,
            cylinders: Vec::new(),
        },
        2.5,
        &leaping(LEAPER),
    );

    assert_eq!(route.outcome, NavRouteOutcome::NoGoal);
    assert!(route.reason.contains("nothing on top"));
}

#[test]
fn a_ramp_too_steep_to_stand_on_is_not_climbed() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 10.0, 0.0),
        ramp(6.0, 10.0, 10.0, 12.0, 0.0, 4.0),
        floor(0.0, 12.0, 20.0, 20.0, 4.0),
    ]));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(16.0, 16.0, 4.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::NoPath);
}

#[test]
fn stairs_with_low_risers_are_climbed() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 10.0, 0.0),
        stairs(6.0, 10.0, 10.0, 0.5, 0.2, 10),
        floor(0.0, 15.0, 20.0, 20.0, 2.0),
    ]));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(16.0, 18.0, 2.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    close(route.path.last().unwrap().z, 2.0, 1);
}

#[test]
fn stairs_with_risers_taller_than_a_step_are_not_climbed() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 10.0, 0.0),
        stairs(6.0, 10.0, 10.0, 0.5, 0.8, 3),
        floor(0.0, 11.5, 20.0, 20.0, 2.4),
    ]));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(16.0, 16.0, 2.4), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::NoPath);
}

#[test]
fn ground_under_a_deck_and_the_deck_are_separate_standing_points() {
    let grid = build(join(&[
        floor(0.0, 0.0, 10.0, 10.0, 0.0),
        floor(3.0, 3.0, 7.0, 7.0, 3.0),
    ]));

    let ground = grid.find_node(v(5.125, 5.125, 0.0), 0.1, 0.5);
    let deck = grid.find_node(v(5.125, 5.125, 3.0), 0.1, 0.5);

    assert!(ground >= 0 && deck >= 0);
    close(grid.position(ground).z, 0.0, 3);
    close(grid.position(deck).z, 3.0, 3);
    assert_eq!(grid.column_of(ground), grid.column_of(deck));
}

#[test]
fn a_low_ceiling_leaves_no_room_to_stand_under_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 10.0, 10.0, 0.0),
        floor(2.0, 2.0, 8.0, 8.0, 1.2),
    ]));

    assert_eq!(grid.find_node(v(5.125, 5.125, 0.0), 0.1, 0.5), -1);
    assert!(grid.find_node(v(5.125, 5.125, 1.2), 0.1, 0.5) >= 0);
}

#[test]
fn a_cylinder_is_walked_around() {
    let grid = build_with(
        floor(0.0, 0.0, 20.0, 8.0, 0.0),
        vec![NavCylinder::new(v(10.0, 4.0, 0.0), 1.0, 2.0)],
        32.0,
        Vec::new(),
    );

    let route = find(&grid, v(2.0, 4.0, 0.0), v(18.0, 4.0, 0.0), 0.5);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(route.legs.len() > 2);
    assert!(
        route
            .path
            .iter()
            .all(|p| p.truncate().distance(Vec2::new(10.0, 4.0)) >= 1.0)
    );
}

#[test]
fn terrain_is_stood_on_except_where_a_cell_covers_it() {
    let grid = build_geometry(
        &NavGeometry::new(
            0.0,
            0.0,
            LANDBLOCK_SIZE,
            vec![NavTerrain::new(flat_terrain(), 0.0, 0.0)],
            floor(100.0, 100.0, 110.0, 110.0, 1.0),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );

    assert!(grid.find_node(v(50.125, 50.125, 0.0), 0.1, 0.5) >= 0);
    assert_eq!(grid.find_node(v(105.125, 105.125, 0.0), 0.1, 0.5), -1);
    assert!(grid.find_node(v(105.125, 105.125, 1.0), 0.1, 0.5) >= 0);
    assert_eq!(
        find(&grid, v(40.0, 40.0, 0.0), v(60.0, 70.0, 0.0), 1.0).outcome,
        NavRouteOutcome::Routed
    );
}

#[test]
fn a_region_spanning_two_landblocks_is_walked_across_their_seam() {
    let grid = build_geometry(
        &NavGeometry::new(
            96.0,
            0.0,
            LANDBLOCK_SIZE,
            vec![
                NavTerrain::new(flat_terrain(), 0.0, 0.0),
                NavTerrain::new(flat_terrain(), 192.0, 0.0),
            ],
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );

    assert!(grid.find_node(v(191.875, 50.125, 0.0), 0.1, 0.5) >= 0);
    assert!(grid.find_node(v(192.125, 50.125, 0.0), 0.1, 0.5) >= 0);
    let route = find(&grid, v(150.0, 50.0, 0.0), v(250.0, 60.0, 0.0), 1.0);
    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert_eq!(route.legs.len(), 2);
}

#[test]
fn a_region_is_captured_from_every_resident_landblock_it_overlaps() {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        0xA9B4_FFFF,
        flat_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    engine.add_landblock(
        0xAAB4_FFFF,
        flat_terrain(),
        Vec::new(),
        Vec::new(),
        192.0,
        0.0,
    );

    let both = geometry::capture(&engine, 150.0, 10.0, 96.0, None).unwrap();
    let west = geometry::capture(&engine, 10.0, 10.0, 96.0, None).unwrap();
    let east = geometry::capture_landblock(&engine, 0xAAB4_FFFF).unwrap();

    let mut both_ids = both.landblock_ids.clone();
    both_ids.sort_unstable();
    assert_eq!(both_ids, [0xA9B4_FFFF, 0xAAB4_FFFF]);
    assert_eq!(west.landblock_ids, [0xA9B4_FFFF]);
    assert_eq!(east.origin_x, 192.0);
    assert_eq!(east.landblock_ids, [0xAAB4_FFFF]);
    assert!(geometry::capture(&engine, 1000.0, 1000.0, 96.0, None).is_none());
}

#[test]
fn a_goal_behind_a_thin_wall_is_reached_from_the_side_that_can_see_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(10.0, 0.0, 10.0, 16.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(6.0, 4.0, 0.0), v(11.5, 4.0, 0.0), 2.5);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(route.path.last().unwrap().x > 10.0);
    assert!(route.path.iter().any(|p| p.y > 16.0));
}

#[test]
fn a_goal_behind_a_counter_window_is_reached_at_the_nearest_spot_that_can_see_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(12.0, 8.0, 16.0, 8.0, 0.0, 3.0),
        wall(16.0, 8.0, 16.0, 12.0, 0.0, 3.0),
        wall(16.0, 12.0, 12.0, 12.0, 0.0, 3.0),
        wall(12.0, 12.0, 12.0, 8.0, 0.0, 1.0),
        wall(12.0, 12.0, 12.0, 8.0, 2.0, 3.0),
    ]));
    let goal = v(14.5, 10.0, 0.0);

    let route = find(&grid, v(4.0, 10.0, 0.0), goal, 2.5);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    let end = *route.path.last().unwrap();
    assert!(end.x < 12.0);
    in_range(end.truncate().distance(goal.truncate()), 2.5, 4.0);
    assert!(
        route.reason.contains("nearest spot that can"),
        "{}",
        route.reason
    );
}

#[test]
fn a_goal_shut_in_a_cupboard_is_reached_at_the_nearest_spot_without_a_line_of_sight() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(9.5, 9.5, 10.5, 9.5, 0.0, 3.0),
        wall(10.5, 9.5, 10.5, 10.5, 0.0, 3.0),
        wall(10.5, 10.5, 9.5, 10.5, 0.0, 3.0),
        wall(9.5, 10.5, 9.5, 9.5, 0.0, 3.0),
    ]));

    let route = find(&grid, v(3.0, 3.0, 0.0), v(10.0, 10.0, 0.0), 2.5);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(
        route.reason.contains("without a line of sight"),
        "{}",
        route.reason
    );
    assert!(!route.ends_in_sight);
    let end = *route.path.last().unwrap();
    assert!(!(end.x > 9.5 && end.x < 10.5 && end.y > 9.5 && end.y < 10.5));
    in_range(end.truncate().distance(Vec2::new(10.0, 10.0)), 0.5, 2.5);
}

#[test]
fn a_start_inside_a_passage_too_narrow_to_stand_in_is_not_taken_from_beyond_its_walls() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 12.0, 10.0, 0.0, 3.0),
        wall(0.0, 10.7, 12.0, 10.7, 0.0, 3.0),
    ]));
    let start = v(6.0, 10.35, 0.0);

    assert!(grid.find_node(start, START_RADIUS, START_HEIGHT_TOLERANCE) >= 0);
    assert_eq!(
        grid.find_walkable_node(start, START_RADIUS, START_HEIGHT_TOLERANCE),
        -1
    );
    assert_eq!(
        find(&grid, start, v(16.0, 16.0, 0.0), 1.0).outcome,
        NavRouteOutcome::NoStart
    );
}

#[test]
fn legs_through_a_doorway_keep_the_body_clear_of_its_frame() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 9.4, 10.0, 0.0, 3.0),
        wall(10.6, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(2.0, 4.0, 0.0), v(15.0, 17.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    for leg_index in 1..route.legs.len() {
        for frame in [Vec2::new(9.4, 10.0), Vec2::new(10.6, 10.0)] {
            let distance =
                distance_to_segment(frame, route.legs[leg_index - 1], route.legs[leg_index]);
            assert!(
                distance >= grid.nearest_wall() - 0.05,
                "leg {leg_index} passes within {distance:.2} m of a door frame"
            );
        }
    }
}

#[test]
fn legs_around_a_corner_keep_the_room_the_route_kept() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(10.0, 0.0, 10.0, 10.0, 0.0, 3.0),
        wall(10.0, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));

    let route = find(&grid, v(16.0, 15.0, 0.0), v(4.0, 4.0, 0.0), 1.0);

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    assert!(
        route.legs.len() <= 3,
        "the route turns the corner in {} legs",
        route.legs.len() - 1
    );
    let corner = Vec2::new(10.0, 10.0);
    for leg_index in 1..route.legs.len() {
        let distance =
            distance_to_segment(corner, route.legs[leg_index - 1], route.legs[leg_index]);
        assert!(
            distance >= 0.85,
            "leg {leg_index} passes within {distance:.2} m of the corner"
        );
    }
}

#[test]
fn a_body_may_brush_a_wall_along_a_path_but_not_cross_one_or_leave_its_floor() {
    let grid = build(join(&[
        floor(0.0, 0.0, 10.0, 10.0, 0.0),
        wall(5.0, 0.0, 5.0, 4.0, 0.0, 3.0),
    ]));
    let west = grid.find_node(v(2.125, 4.375, 0.0), 0.1, 0.5);
    let east = grid.find_node(v(8.125, 4.375, 0.0), 0.1, 0.5);

    assert!(
        !grid.can_walk_straight(west, east),
        "a walked leg keeps farther from the wall's end"
    );
    assert!(grid.can_brush_along(&[grid.position(west), v(5.0, 4.375, 0.0), grid.position(east)]));
    assert!(
        !grid.can_brush_along(&[v(2.125, 2.125, 0.0), v(8.125, 2.125, 0.0)]),
        "through the wall"
    );
    assert!(
        !grid.can_brush_along(&[v(5.125, 1.0, 0.0), v(5.125, 3.0, 0.0)]),
        "pressed against the wall"
    );
    assert!(
        !grid.can_brush_along(&[v(8.125, 8.125, 0.0), v(11.0, 8.125, 0.0)]),
        "off the floor"
    );
    assert!(
        !grid.can_brush_along(&[v(2.125, 8.125, 5.0), v(4.125, 8.125, 5.0)]),
        "far above the floor"
    );
}

#[test]
fn a_route_keeps_out_of_avoided_spots() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(0.0, 10.0, 4.0, 10.0, 0.0, 3.0),
        wall(6.0, 10.0, 14.0, 10.0, 0.0, 3.0),
        wall(16.0, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));
    let from = v(5.0, 5.0, 0.0);
    let to = v(5.0, 15.0, 0.0);
    let left_door = NavAvoidance::new(v(5.0, 10.0, 0.0), 1.2);
    let right_door = NavAvoidance::new(v(15.0, 10.0, 0.0), 1.2);
    let only_left = [left_door];
    let both = [left_door, right_door];

    let direct = find(&grid, from, to, 1.0);
    let around = find_with(
        &grid,
        from,
        to,
        1.0,
        NavRouteRequest {
            avoid: &only_left,
            ..NavRouteRequest::default()
        },
    );
    let nowhere = find_with(
        &grid,
        from,
        to,
        1.0,
        NavRouteRequest {
            avoid: &both,
            ..NavRouteRequest::default()
        },
    );

    assert_eq!(direct.outcome, NavRouteOutcome::Routed);
    assert!(!direct.path.iter().any(|p| p.x > 13.0));
    assert_eq!(around.outcome, NavRouteOutcome::Routed);
    assert!(
        around
            .path
            .iter()
            .any(|p| p.x > 13.0 && (p.y - 10.0).abs() < 0.5)
    );
    assert!(
        around
            .path
            .iter()
            .all(|p| p.truncate().distance(Vec2::new(5.0, 10.0)) > 1.2)
    );
    assert_eq!(nowhere.outcome, NavRouteOutcome::NoPath);
}

fn crowded(grid: &NavGrid, from: Vec3, to: Vec3, creature: NavAvoidance) -> NavRoute {
    let crowd = [creature];
    find_with(
        grid,
        from,
        to,
        1.0,
        NavRouteRequest {
            crowd: &crowd,
            ..NavRouteRequest::default()
        },
    )
}

#[test]
fn a_route_goes_around_a_creature_where_there_is_room_and_keeps_as_clear_of_walls() {
    let grid = build(floor(0.0, 0.0, 20.0, 20.0, 0.0));
    let from = v(10.0, 2.0, 0.0);
    let to = v(10.0, 18.0, 0.0);
    let creature = NavAvoidance::new(v(10.0, 10.0, 0.0), 1.6);

    let plain_route = find(&grid, from, to, 1.0);
    let around = crowded(&grid, from, to, creature);

    assert_eq!(around.reason, "routed");
    assert!(
        plain_route.crowding == 0.0,
        "a route not asked about the creature measures no crowding"
    );
    for leg_index in 1..around.legs.len() {
        let distance = distance_to_segment(
            Vec2::new(10.0, 10.0),
            around.legs[leg_index - 1],
            around.legs[leg_index],
        );
        assert!(
            distance >= creature.radius - 0.05,
            "leg {leg_index} passes {distance:.2} m from the creature"
        );
    }
    assert!(
        around.crowding < 0.05,
        "the route grazes the creature by {:.3} m",
        around.crowding
    );
    assert!(
        around.scrape <= plain_route.scrape + 0.25,
        "around the creature scrapes {:.2} m², straight on {:.2} m²",
        around.scrape,
        plain_route.scrape
    );
}

#[test]
fn a_route_goes_through_a_creature_filling_a_corridor_rather_than_scraping_along_its_walls() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(8.5, 0.0, 8.5, 20.0, 0.0, 3.0),
        wall(11.5, 0.0, 11.5, 20.0, 0.0, 3.0),
    ]));
    let from = v(10.0, 2.0, 0.0);
    let to = v(10.0, 18.0, 0.0);
    let creature = NavAvoidance::new(v(10.0, 10.0, 0.0), 1.4);

    let plain_route = find(&grid, from, to, 1.0);
    let route = crowded(&grid, from, to, creature);

    assert_eq!(route.reason, "routed");
    assert!(route.crowding > 0.0, "the route goes through the creature");
    assert!(
        route.scrape <= plain_route.scrape + 0.25,
        "the route scrapes {:.2} m², straight on {:.2} m²",
        route.scrape,
        plain_route.scrape
    );
    for point in &route.legs {
        in_range(point.x, 9.5, 10.5);
    }
}

#[test]
fn a_route_sidesteps_a_creature_on_the_side_with_room_and_keeps_clear_of_the_wall_there() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(6.0, 0.0, 6.0, 20.0, 0.0, 3.0),
        wall(14.0, 0.0, 14.0, 20.0, 0.0, 3.0),
    ]));
    let from = v(9.0, 2.0, 0.0);
    let to = v(9.0, 18.0, 0.0);
    let creature = NavAvoidance::new(v(8.0, 10.0, 0.0), 1.4);

    let plain_route = find(&grid, from, to, 1.0);
    let route = crowded(&grid, from, to, creature);

    assert_eq!(route.reason, "routed");
    assert!(
        route.crowding < 0.05,
        "the route grazes the creature by {:.3} m",
        route.crowding
    );
    for leg_index in 1..route.legs.len() {
        let distance = distance_to_segment(
            Vec2::new(8.0, 10.0),
            route.legs[leg_index - 1],
            route.legs[leg_index],
        );
        assert!(
            distance >= creature.radius - 0.05,
            "leg {leg_index} passes {distance:.2} m from the creature"
        );
    }
    for point in &route.path {
        assert!(
            point.x <= 13.0,
            "the route comes within {:.2} m of the far wall",
            14.0 - point.x
        );
    }
    assert!(
        route.scrape <= plain_route.scrape + 0.25,
        "the sidestep scrapes {:.2} m², straight on {:.2} m²",
        route.scrape,
        plain_route.scrape
    );
}

#[test]
fn a_creature_where_a_route_rounds_a_corner_does_not_pull_the_route_into_the_corner() {
    let grid = build(join(&[
        floor(0.0, 0.0, 20.0, 20.0, 0.0),
        wall(10.0, 0.0, 10.0, 10.0, 0.0, 3.0),
        wall(10.0, 10.0, 20.0, 10.0, 0.0, 3.0),
    ]));
    let from = v(16.0, 15.0, 0.0);
    let to = v(4.0, 4.0, 0.0);
    let plain_route = find(&grid, from, to, 1.0);
    assert!(plain_route.legs.len() > 2);
    let creature = NavAvoidance::new(plain_route.legs[1], 1.2);

    let route = crowded(&grid, from, to, creature);

    assert_eq!(route.reason, "routed");
    let corner = Vec2::new(10.0, 10.0);
    for leg_index in 1..route.legs.len() {
        let distance =
            distance_to_segment(corner, route.legs[leg_index - 1], route.legs[leg_index]);
        assert!(
            distance >= 0.85,
            "leg {leg_index} passes within {distance:.2} m of the corner"
        );
    }
    assert!(
        route.scrape <= plain_route.scrape + 0.25,
        "the route scrapes {:.2} m², without the creature {:.2} m²",
        route.scrape,
        plain_route.scrape
    );
}

#[test]
fn a_route_starting_inside_an_avoided_spot_leaves_it_without_going_deeper() {
    let grid = build(floor(0.0, 0.0, 20.0, 20.0, 0.0));
    let spot = [NavAvoidance::new(v(10.0, 10.0, 0.0), 2.0)];
    let request = || NavRouteRequest {
        avoid: &spot,
        ..NavRouteRequest::default()
    };

    let away = find_with(
        &grid,
        v(10.0, 11.0, 0.0),
        v(10.0, 18.0, 0.0),
        1.0,
        request(),
    );
    let past = find_with(&grid, v(10.0, 11.0, 0.0), v(10.0, 2.0, 0.0), 1.0, request());

    assert_eq!(away.reason, "routed");
    assert_eq!(past.reason, "routed");
    assert!(
        past.path
            .iter()
            .all(|p| p.truncate().distance(Vec2::new(10.0, 10.0)) >= 0.85)
    );
}

#[test]
fn a_goal_beyond_the_grid_is_routed_to_the_grids_edge_toward_it() {
    let grid = build(floor(0.0, 0.0, 32.0, 32.0, 0.0));

    let route = route::find_toward(
        &grid,
        v(4.0, 16.0, 0.0),
        v(200.0, 16.0, 0.0),
        4.0,
        &NavRouteRequest::default(),
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    in_range(route.legs.last().unwrap().x, 27.5, 32.0);
    assert!(
        route.reason.contains("its edge toward the goal"),
        "{}",
        route.reason
    );
}

#[test]
fn a_goal_beyond_a_wall_across_the_grid_is_routed_to_the_reachable_spot_nearest_it() {
    let grid = build(join(&[
        floor(0.0, 0.0, 32.0, 32.0, 0.0),
        wall(20.0, 0.0, 20.0, 32.0, 0.0, 3.0),
    ]));

    let route = route::find_toward(
        &grid,
        v(4.0, 16.0, 0.0),
        v(200.0, 16.0, 0.0),
        4.0,
        &NavRouteRequest::default(),
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    in_range(route.legs.last().unwrap().x, 17.0, 20.0);
    assert!(
        route.reason.contains("the reachable spot nearest it"),
        "{}",
        route.reason
    );
}

/// A region half over a landblock whose ground is loaded and half over one
/// that is not yet: a stage toward a goal beyond the unloaded half ends at
/// the edge of the loaded ground, as at the edge of the grid.
#[test]
fn a_goal_beyond_the_loaded_ground_is_routed_to_its_edge_toward_it() {
    let grid = build_geometry(
        &NavGeometry::new(
            96.0,
            0.0,
            LANDBLOCK_SIZE,
            vec![NavTerrain::new(flat_terrain(), 0.0, 0.0)],
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );

    let route = route::find_toward(
        &grid,
        v(120.0, 96.0, 0.0),
        v(1000.0, 96.0, 0.0),
        4.0,
        &leaping(LEAPER),
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed);
    in_range(route.legs.last().unwrap().x, 186.0, 192.0);
    assert!(
        route.reason.contains("its edge toward the goal"),
        "{}",
        route.reason
    );
    assert!(route.leaps.is_empty());
}

/// Two floors 3 m apart, the goal far beyond the second: a stage's walk
/// stops at the gap and its leap crosses it there.
#[test]
fn a_stage_leaps_the_gap_its_walk_stops_at() {
    let grid = build_with(
        join(&[
            floor(0.0, 0.0, 30.0, 64.0, 0.0),
            floor(33.0, 0.0, 64.0, 64.0, 0.0),
        ]),
        Vec::new(),
        64.0,
        Vec::new(),
    );

    let walked = route::find_toward(
        &grid,
        v(5.0, 32.0, 0.0),
        v(500.0, 32.0, 0.0),
        4.0,
        &NavRouteRequest::default(),
    );
    let leapt = route::find_toward(
        &grid,
        v(5.0, 32.0, 0.0),
        v(500.0, 32.0, 0.0),
        4.0,
        &leaping(LEAPER),
    );

    assert!(walked.reason.contains("the reachable spot nearest it"));
    assert!(
        leapt.reason.contains("its edge toward the goal") && !leapt.leaps.is_empty(),
        "{}",
        described(&leapt)
    );
}

/// The gap is 20 m wide where the walk comes nearest the goal and 3 m wide
/// 90 m north of there: a stage's leaps take off only near where its walk
/// came nearest, so it keeps the walk.
#[test]
fn a_stage_takes_no_leap_far_from_where_its_walk_came_nearest() {
    let grid = build_with(
        join(&[
            floor(0.0, 0.0, 30.0, 128.0, 0.0),
            floor(50.0, 0.0, 128.0, 90.0, 0.0),
            floor(33.0, 95.0, 128.0, 128.0, 0.0),
        ]),
        Vec::new(),
        128.0,
        Vec::new(),
    );

    let route = route::find_toward(
        &grid,
        v(5.0, 5.0, 0.0),
        v(500.0, 5.0, 0.0),
        4.0,
        &leaping(LEAPER),
    );

    assert!(
        route.reason.contains("the reachable spot nearest it") && route.leaps.is_empty(),
        "{}",
        described(&route)
    );
    in_range(route.legs.last().unwrap().x, 26.0, 30.0);
    in_range(route.legs.last().unwrap().y, 0.0, 10.0);
}

/// A floor with a platform east and one west of the middle: a finder
/// limited to landings nearer a goal far east than the middle leaps onto the
/// east platform only, where an unlimited one leaps onto both.
#[test]
fn a_limited_finder_leaps_only_onto_floor_nearer_the_goal() {
    let grid = build(join(&[
        floor(0.0, 0.0, 32.0, 32.0, 0.0),
        box_top(22.0, 14.0, 26.0, 18.0, 1.0),
        box_top(6.0, 14.0, 10.0, 18.0, 1.0),
    ]));
    let landings = |finder: &NavLeapFinder<&NavGrid>| {
        let mut east = false;
        let mut west = false;
        for node in 0..grid.node_count() {
            for leap in finder.from(node).iter() {
                let at = grid.position(leap.to);
                if at.z > 0.5 {
                    east |= at.x > 16.0;
                    west |= at.x < 16.0;
                }
            }
        }
        (east, west)
    };

    let unlimited = NavLeapFinder::new(&grid, LEAPER);
    let limited = NavLeapFinder::limited(
        &grid,
        LEAPER,
        NavLeapLimits {
            takeoff_near: v(16.0, 16.0, 0.0),
            takeoff_radius: 48.0,
            toward: v(500.0, 16.0, 0.0),
            landing_within: 484.0,
        },
    );

    assert_eq!(landings(&unlimited), (true, true));
    assert_eq!(landings(&limited), (true, false));
}

/// The top and sides of a box standing on the floor.
fn box_top(x0: f32, y0: f32, x1: f32, y1: f32, height: f32) -> Vec<NavTriangle> {
    join(&[
        floor(x0, y0, x1, y1, height),
        wall(x0, y0, x1, y0, 0.0, height),
        wall(x1, y0, x1, y1, 0.0, height),
        wall(x1, y1, x0, y1, 0.0, height),
        wall(x0, y1, x0, y0, 0.0, height),
    ])
}

fn square_cell(id: u32, corners: [Vec3; 4]) -> CellSurface {
    let vertices: HashMap<u16, Vec3> = corners
        .iter()
        .enumerate()
        .map(|(i, &c)| (i as u16, c))
        .collect();
    CellSurface::new(id, &vertices, &[vec![0, 1, 2, 3]])
}

#[test]
fn a_landblocks_cells_are_measured_across_every_polygon() {
    let mut engine = PhysicsEngine::new();
    let room = square_cell(
        0xA9B4_0100,
        [
            v(10.0, 20.0, -30.0),
            v(40.0, 20.0, -30.0),
            v(40.0, 50.0, -30.0),
            v(10.0, 50.0, -30.0),
        ],
    );
    let corridor = square_cell(
        0xA9B4_0101,
        [
            v(40.0, 30.0, -30.0),
            v(250.0, 30.0, -30.0),
            v(250.0, 34.0, -30.0),
            v(40.0, 34.0, -30.0),
        ],
    );
    engine.add_landblock(
        0xA9B4_FFFF,
        flat_terrain(),
        vec![room, corridor],
        Vec::new(),
        0.0,
        0.0,
    );
    engine.add_landblock(
        0xAAB4_FFFF,
        flat_terrain(),
        Vec::new(),
        Vec::new(),
        192.0,
        0.0,
    );

    let (minimum, maximum) = geometry::measure_cells(&engine, 0xA9B4_FFFF).unwrap();
    assert_eq!(minimum, Vec2::new(10.0, 20.0));
    assert_eq!(maximum, Vec2::new(250.0, 50.0));
    assert!(geometry::measure_cells(&engine, 0xAAB4_FFFF).is_none());
    assert!(geometry::measure_cells(&engine, 0x1234_FFFF).is_none());
}

fn dungeon_hall() -> CellSurface {
    square_cell(
        0x0019_0100,
        [
            v(70.0, -330.0, 6.0),
            v(110.0, -330.0, 6.0),
            v(110.0, -290.0, 6.0),
            v(70.0, -290.0, 6.0),
        ],
    )
}

#[test]
fn a_region_over_dungeon_cells_outside_their_landblocks_square_is_captured() {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        0x0019_FFFF,
        flat_terrain(),
        vec![dungeon_hall()],
        Vec::new(),
        0.0,
        0.0,
    );

    let geometry = geometry::capture(&engine, 60.0, -340.0, 64.0, None).unwrap();

    assert_eq!(geometry.landblock_ids, [0x0019_FFFF]);
    assert_eq!(geometry.cell_triangles.len(), 2);
    assert!(geometry::capture(&engine, 400.0, -340.0, 64.0, None).is_none());
}

#[test]
fn a_dungeon_is_captured_from_its_own_landblock_without_terrain() {
    let mut engine = PhysicsEngine::new();
    engine.add_landblock(
        0x0019_FFFF,
        flat_terrain(),
        vec![dungeon_hall()],
        Vec::new(),
        0.0,
        0.0,
    );
    engine.add_landblock(
        0x0018_FFFF,
        flat_terrain(),
        Vec::new(),
        Vec::new(),
        0.0,
        -384.0,
    );

    let around = geometry::capture(&engine, 60.0, -340.0, 64.0, None).unwrap();
    let dungeon =
        geometry::capture_dungeon(&engine, 0x0019_0100, 60.0, -340.0, 64.0, None).unwrap();

    let mut around_ids = around.landblock_ids.clone();
    around_ids.sort_unstable();
    assert_eq!(around_ids, [0x0018_FFFF, 0x0019_FFFF]);
    assert!(!around.terrains.is_empty());
    assert_eq!(dungeon.landblock_ids, [0x0019_FFFF]);
    assert!(dungeon.terrains.is_empty());
    assert_eq!(dungeon.cell_triangles.len(), 2);
    assert!(geometry::capture_dungeon(&engine, 0x1234_0100, 60.0, -340.0, 64.0, None).is_none());
}

#[test]
fn an_objects_footprint_is_its_cylinder_or_sphere_or_else_where_it_was_registered() {
    let entry =
        |id: u32, gfx: u32, at: Vec3, radius: f32, kind: ShadowCollisionType, height: f32| {
            ShadowEntry::single(
                id,
                gfx,
                at,
                Quat::IDENTITY,
                radius,
                kind,
                height,
                1.0,
                0,
                Default::default(),
            )
        };
    let cylinder = entry(
        1,
        0,
        v(10.0, 20.0, 1.0),
        0.6,
        ShadowCollisionType::Cylinder,
        2.0,
    );
    let sphere = entry(
        2,
        0,
        v(5.0, 6.0, 7.0),
        0.4,
        ShadowCollisionType::Sphere,
        0.0,
    );
    let unloaded_model = entry(
        3,
        0x0100_1234,
        v(1.0, 2.0, 3.0),
        2.5,
        ShadowCollisionType::Bsp,
        0.0,
    );

    assert_eq!(
        geometry::footprint_of(&cylinder, None),
        NavAvoidance::new(v(10.0, 20.0, 1.0), 0.6)
    );
    assert_eq!(
        geometry::footprint_of(&sphere, None),
        NavAvoidance::new(v(5.0, 6.0, 7.0), 0.4)
    );
    assert_eq!(
        geometry::footprint_of(&unloaded_model, None),
        NavAvoidance::new(v(1.0, 2.0, 3.0), 2.5)
    );
}

#[test]
fn a_hop_takes_a_route_off_a_deck_down_to_the_ground_below() {
    let grid = build(join(&[
        floor(2.0, 2.0, 12.0, 12.0, 3.0),
        wall(12.0, 2.0, 12.0, 12.0, 0.0, 3.0),
        floor(12.0, 2.0, 24.0, 12.0, 0.0),
    ]));
    let from = v(6.0, 7.0, 3.0);
    let to = v(18.0, 7.0, 0.0);

    let walked = find(&grid, from, to, 1.0);
    let leapt = find_with(&grid, from, to, 1.0, leaping(LEAPER));

    assert_ne!(walked.reason, "routed");
    assert_eq!(leapt.reason, "routed");
    let leap = single_leap(&leapt);
    assert!(!leap.run);
    assert!(
        leap.power <= 0.3,
        "the hop took {:.1} of full power",
        leap.power
    );
    close(leg(&leapt, leap.leg_index - 1).z, 3.0, 1);
    close(leg(&leapt, leap.leg_index).z, 0.0, 1);
}

#[test]
fn a_jump_takes_a_route_across_a_gap_no_walk_crosses() {
    let grid = build(join(&[
        floor(2.0, 2.0, 10.0, 12.0, 0.0),
        floor(13.0, 2.0, 24.0, 12.0, 0.0),
    ]));
    let from = v(6.0, 7.0, 0.0);
    let to = v(18.0, 7.0, 0.0);

    let walked = find(&grid, from, to, 1.0);
    let leapt = find_with(&grid, from, to, 1.0, leaping(LEAPER));

    assert_ne!(walked.reason, "routed");
    assert_eq!(leapt.reason, "routed");
    let leap = single_leap(&leapt);
    assert!(leg(&leapt, leap.leg_index - 1).x < 10.0 && leg(&leapt, leap.leg_index).x > 13.0);
}

#[test]
fn a_jump_at_a_run_takes_a_route_across_a_gap_no_walk_jump_clears() {
    let grid = build(join(&[
        floor(2.0, 2.0, 10.0, 12.0, 0.0),
        floor(17.0, 2.0, 28.0, 12.0, 0.0),
    ]));

    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(22.0, 7.0, 0.0),
        1.0,
        leaping(LEAPER),
    );

    assert_eq!(leapt.reason, "routed");
    let leap = single_leap(&leapt);
    assert!(leap.run);
    assert!(leg(&leapt, leap.leg_index - 1).x < 10.0 && leg(&leapt, leap.leg_index).x > 17.0);
}

#[test]
fn a_standing_jump_takes_a_route_up_onto_a_ledge() {
    let grid = build(join(&[
        floor(2.0, 2.0, 12.0, 12.0, 0.0),
        wall(12.0, 2.0, 12.0, 12.0, 0.0, 1.5),
        floor(12.0, 2.0, 24.0, 12.0, 1.5),
    ]));
    let from = v(6.0, 7.0, 0.0);
    let to = v(18.0, 7.0, 1.5);

    let walked = find(&grid, from, to, 1.0);
    let leapt = find_with(&grid, from, to, 1.0, leaping(LEAPER));

    assert_ne!(walked.reason, "routed");
    assert_eq!(leapt.reason, "routed");
    let leap = single_leap(&leapt);
    close(leg(&leapt, leap.leg_index - 1).z, 0.0, 1);
    close(leg(&leapt, leap.leg_index).z, 1.5, 1);
}

fn gap_grid() -> NavGrid {
    build(join(&[
        floor(2.0, 2.0, 10.0, 12.0, 0.0),
        floor(13.0, 2.0, 28.0, 12.0, 0.0),
    ]))
}

/// A leap aimed again from its own takeoff is the leap the route planned:
/// the same pace, and about the same power.
#[test]
fn a_leap_aimed_again_from_its_takeoff_is_the_leap_the_route_planned() {
    let grid = gap_grid();
    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(22.0, 7.0, 0.0),
        1.0,
        leaping(sliding()),
    );
    let leap = single_leap(&leapt);

    let aim = NavLeapFinder::new(&grid, sliding()).aim_from(
        leg(&leapt, leap.leg_index - 1),
        leg(&leapt, leap.leg_index),
        leap.run,
    );

    let aim = aim.expect("a leap aimed again from its takeoff");
    assert_eq!(aim.run, leap.run);
    in_range(aim.power, leap.power - 0.1, leap.power + 0.1);
}

/// A body that stopped a step short of its takeoff is aimed from where it
/// stands: charged harder, and kept by the same checks the route's own
/// leaps pass.
#[test]
fn a_leap_aimed_from_a_step_short_of_its_takeoff_is_charged_harder() {
    let grid = gap_grid();
    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(22.0, 7.0, 0.0),
        1.0,
        leaping(sliding()),
    );
    let leap = single_leap(&leapt);
    let takeoff = leg(&leapt, leap.leg_index - 1);
    let landing = leg(&leapt, leap.leg_index);
    let away = v(takeoff.x - landing.x, takeoff.y - landing.y, 0.0).normalize();

    let aim =
        NavLeapFinder::new(&grid, sliding()).aim_from(takeoff + away * 0.8, landing, leap.run);

    let aim = aim.expect("a leap aimed from a step short");
    assert!(
        aim.power > leap.power || aim.run != leap.run,
        "aimed at {:.2} power against the planned {:.2}",
        aim.power,
        leap.power
    );
}

/// Where no leap at the landing a route planned is kept from the spot a body
/// came to rest on, a leap onto another spot of that same floor is taken
/// from where it stands.
#[test]
fn a_leap_onto_another_spot_of_the_landings_floor_is_aimed_from_where_the_body_stands() {
    let grid = gap_grid();
    let finder = NavLeapFinder::new(&grid, sliding());
    let standing = v(6.0, 7.0, 0.0);
    // Farther across the far floor than a leap from here reaches.
    let planned = v(27.0, 7.0, 0.0);

    assert!(finder.aim_from(standing, planned, true).is_none());
    let (_, spot) = finder
        .aim_onward(standing, planned, true)
        .expect("a leap aimed onward");

    assert!(
        spot.x < planned.x,
        "the leap aimed onward at ({:.1}, {:.1}), no nearer than the landing it replaced",
        spot.x,
        spot.y
    );
    assert!(finder.aim_from(standing, spot, true).is_some());
}

/// A landing smaller than the body keeps no room to spare: a leap aimed
/// again from the takeoff it was planned from is kept, and one aimed from
/// farther off is not.
#[test]
fn a_leap_onto_a_landing_smaller_than_the_body_is_kept_only_from_its_own_takeoff() {
    let grid = build(join(&[
        floor(2.0, 12.0, 8.0, 18.0, 0.0),
        floor(17.0, 14.75, 25.0, 15.25, 0.0),
    ]));
    let finder = NavLeapFinder::new(&grid, sliding());
    let takeoff = v(7.0, 15.0, 0.0);
    let landing = v(21.0, 15.0, 0.0);

    assert!(
        finder
            .aim_from_explained(takeoff, landing, true, true)
            .is_ok()
    );
    assert!(
        finder
            .aim_from_explained(takeoff, landing, true, false)
            .is_err()
    );
}

/// A leap aimed again is kept only as the route's own leaps are: one that
/// comes down on a strip only when flown exactly is not aimed from where the
/// body stands, while the same leap onto a wider strip is.
#[test]
fn a_leap_aimed_again_must_still_land_when_flown_a_little_off() {
    let aim_onto = |width: f32| {
        let grid = build(join(&[
            floor(2.0, 12.0, 8.0, 18.0, 0.0),
            floor(17.0, 15.0 - width / 2.0, 25.0, 15.0 + width / 2.0, 0.0),
        ]));
        NavLeapFinder::new(&grid, sliding()).aim_from(v(7.0, 15.0, 0.0), v(21.0, 15.0, 0.0), true)
    };

    assert!(aim_onto(2.0).is_none());
    assert!(aim_onto(3.0).is_some());
}

/// A body sliding into a ledge at a slant is pushed along the edge and
/// slides on; one sliding into it head on stops there.
#[test]
fn a_slide_into_a_ledge_at_a_slant_goes_on_along_its_edge() {
    let grid = build(floor(2.0, 2.0, 24.0, 6.0, 0.0));
    let finder = NavLeapFinder::new(&grid, sliding());
    let from = grid.find_node(v(8.0, 4.5, 0.0), 0.5, 0.5);

    let slanted = grid.position(finder.slides_to(from, Vec2::new(1.0, 1.0), 4.0));
    let head_on = grid.position(finder.slides_to(from, Vec2::new(0.0, 1.0), 4.0));

    assert!(
        slanted.x > 9.5,
        "the slanted slide came to rest at {:.2}, {:.2}",
        slanted.x,
        slanted.y
    );
    assert!(slanted.y < 6.0);
    in_range(head_on.x, 7.5, 8.5);
    assert!(head_on.y < 6.0);
}

/// A landing slide spends its speed climbing: at a run of 7.3 m/s it rises
/// no more than its speed squared over twice gravity, 2.7 m.
#[test]
fn a_landing_slide_climbs_no_higher_than_its_speed_carries_it() {
    let grid = build(join(&[
        floor(2.0, 2.0, 20.0, 6.0, 0.0),
        ramp(2.0, 6.0, 20.0, 12.0, 0.0, 5.04),
    ]));
    let finder = NavLeapFinder::new(&grid, sliding());
    let below_rise = grid.find_node(v(10.0, 5.5, 0.0), 0.5, 0.5);
    let on_flat = grid.find_node(v(10.0, 3.0, 0.0), 0.5, 0.5);

    let flat = grid.position(finder.slides_to(on_flat, Vec2::new(1.0, 0.0), 6.0));
    let climbed = grid.position(finder.slides_to(below_rise, Vec2::new(0.0, 1.0), 6.0));

    assert!(flat.x - grid.position(on_flat).x > 5.0);
    let most = 7.3 * 7.3 / (2.0 * 9.8);
    assert!(
        climbed.z > 1.0 && climbed.z <= most + 0.1,
        "the slide climbed to {:.2} m",
        climbed.z
    );
}

/// A leap coming down on terrain is thrown back up a little and skims on
/// through the hop; one coming down on an object's floor slides on with no
/// hop.
#[test]
fn a_leap_hops_on_terrain_but_not_on_an_objects_floor() {
    let sliding = sliding();
    let on_terrain = build_geometry(
        &NavGeometry::new(
            0.0,
            0.0,
            32.0,
            vec![NavTerrain::new(flat_terrain(), 0.0, 0.0)],
            Vec::new(),
            Vec::new(),
            Vec::new(),
            Vec::new(),
        )
        .unwrap(),
    );
    let on_object = build(floor(0.0, 0.0, 32.0, 32.0, 0.0));
    let terrain_node = on_terrain.find_node(v(10.0, 16.0, 0.0), 0.5, 0.5);
    let object_node = on_object.find_node(v(10.0, 16.0, 0.0), 0.5, 0.5);

    assert!(on_terrain.is_terrain(terrain_node));
    assert!(!on_object.is_terrain(object_node));
    let (terrain_down, terrain_rest) = NavLeapFinder::new(&on_terrain, sliding)
        .follow(v(4.0, 16.0, 0.0), v(20.0, 16.0, 0.0), 0.9, true)
        .unwrap();
    let (object_down, object_rest) = NavLeapFinder::new(&on_object, sliding)
        .follow(v(4.0, 16.0, 0.0), v(20.0, 16.0, 0.0), 0.9, true)
        .unwrap();
    let terrain_slide = terrain_rest.x - terrain_down.x;
    let object_slide = object_rest.x - object_down.x;
    assert!(
        terrain_slide > object_slide + 0.5,
        "slid {terrain_slide:.2} m on terrain and {object_slide:.2} m on the object"
    );
    let ground = sliding.physics.ground_slide(sliding.run_speed);
    in_range(object_slide, ground - 0.3, ground + 0.4);
}

/// A body standing against the wall of a ledge no jump from there clears is
/// not aimed from there.
#[test]
fn no_leap_is_aimed_from_where_it_cannot_clear_the_ledge_it_jumps_up_onto() {
    let grid = build(join(&[
        floor(2.0, 2.0, 12.0, 12.0, 0.0),
        wall(12.0, 2.0, 12.0, 12.0, 0.0, 1.5),
        floor(12.0, 2.0, 24.0, 12.0, 1.5),
    ]));
    let finder = NavLeapFinder::new(&grid, LEAPER);

    assert!(
        finder
            .aim_from(v(11.4, 7.0, 0.0), v(18.0, 7.0, 1.5), true)
            .is_none()
    );
    assert!(
        finder
            .aim_from(v(6.0, 7.0, 0.0), v(15.0, 7.0, 1.5), true)
            .is_some()
    );
}

/// How far a leap may carry comes from the body itself: a body that runs
/// 10.6 m/s and jumps 5.7 m high clears a gap of 18 m onto floor at the same
/// height, and one that jumps 1.5 m does not.
#[test]
fn a_leap_carries_as_far_as_the_bodys_own_run_and_jump_take_it() {
    let grid = build_with(
        join(&[
            floor(2.0, 10.0, 8.0, 16.0, 5.0),
            floor(26.0, 6.0, 38.0, 20.0, 5.0),
        ]),
        Vec::new(),
        40.0,
        Vec::new(),
    );
    let from = v(4.0, 13.0, 5.0);
    let to = v(32.0, 13.0, 5.0);
    let far = NavLeapAbility {
        walk_speed: 3.12,
        run_speed: 10.6,
        full_jump_height: 5.69,
        maximum_drop: 12.0,
        physics: ORDINARY_GROUND,
    };
    let low = NavLeapAbility {
        full_jump_height: 1.5,
        ..far
    };
    let goal_floor = |ability| NavRouteRequest {
        leaps: Some(ability),
        arrive_on_goal_floor: true,
        ..NavRouteRequest::default()
    };

    let long_jump = find_with(&grid, from, to, 1.5, goal_floor(far));
    let low_jump = find_with(&grid, from, to, 1.5, goal_floor(low));

    assert!(
        long_jump.reason == "routed" && long_jump.leaps.len() == 1,
        "{}",
        described(&long_jump)
    );
    assert_ne!(low_jump.reason, "routed");
}

/// A room under a roof too steep to stand on has that roof as its ceiling.
/// A body flying high over the roof is in open air; a body in the room
/// whose head reaches the roof is not.
#[test]
fn a_body_above_the_ceiling_of_a_room_is_in_open_air_but_one_whose_head_meets_it_is_not() {
    let grid = build_with(
        join(&[
            floor(0.0, 0.0, 24.0, 24.0, 0.0),
            ramp(8.0, 4.0, 12.0, 20.0, 4.0, 4.0),
            quad(
                v(8.0, 4.0, 4.0),
                v(8.0, 20.0, 4.0),
                v(10.0, 20.0, 9.0),
                v(10.0, 4.0, 9.0),
            ),
        ]),
        Vec::new(),
        24.0,
        Vec::new(),
    );

    assert!(
        grid.is_open_air(v(9.0, 12.0, 10.0)),
        "a body flying over the steep roof is in open air"
    );
    assert!(
        !grid.is_open_air(v(9.0, 12.0, 5.0)),
        "a body whose head meets the roof over the room is not"
    );
}

#[test]
fn a_drop_deeper_than_the_body_may_fall_is_not_taken() {
    let grid = build(join(&[
        floor(2.0, 2.0, 12.0, 12.0, 8.0),
        wall(12.0, 2.0, 12.0, 12.0, 0.0, 8.0),
        floor(12.0, 2.0, 24.0, 12.0, 0.0),
    ]));

    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 8.0),
        v(18.0, 7.0, 0.0),
        1.0,
        leaping(LEAPER),
    );

    assert_ne!(leapt.reason, "routed");
    assert!(leapt.leaps.is_empty());
}

#[test]
fn a_leap_is_aimed_onto_a_small_block_that_no_set_power_comes_down_on() {
    let grid = build(join(&[
        floor(2.0, 2.0, 10.0, 12.0, 0.0),
        floor(13.25, 6.5, 14.5, 7.75, 1.5),
        wall(13.25, 6.5, 14.5, 6.5, -5.0, 1.5),
        wall(14.5, 6.5, 14.5, 7.75, -5.0, 1.5),
        wall(14.5, 7.75, 13.25, 7.75, -5.0, 1.5),
        wall(13.25, 7.75, 13.25, 6.5, -5.0, 1.5),
    ]));

    let leapt = find_with(
        &grid,
        v(6.0, 7.125, 0.0),
        v(13.875, 7.125, 1.5),
        0.5,
        leaping(LEAPER),
    );

    assert_eq!(leapt.reason, "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    let landing = leg(&leapt, leap.leg_index);
    close(landing.z, 1.5, 1);
    in_range(landing.x, 13.5, 14.25);
    in_range(landing.y, 6.75, 7.5);
}

fn jump_up_nearly_as_high_as_the_body_can(slides: bool) {
    let grid = build(join(&[
        floor(2.0, 2.0, 14.0, 12.0, 0.0),
        wall(14.0, 2.0, 14.0, 12.0, 0.0, 3.8),
        floor(14.0, 2.0, 26.0, 12.0, 3.8),
    ]));
    let ability = if slides { sliding() } else { LEAPER };

    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(20.0, 7.0, 3.8),
        1.0,
        leaping(ability),
    );

    assert_eq!(leapt.reason, "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    let takeoff = leg(&leapt, leap.leg_index - 1);
    close(takeoff.z, 0.0, 1);
    close(leg(&leapt, leap.leg_index).z, 3.8, 1);
    assert!(
        takeoff.x < 12.0,
        "the leap left from {:.2} m before the edge",
        14.0 - takeoff.x
    );
}

#[test]
fn a_jump_up_nearly_as_high_as_the_body_can_jump_leaves_from_far_enough_back_to_clear_the_edge() {
    jump_up_nearly_as_high_as_the_body_can(false);
}

#[test]
fn a_jump_up_nearly_as_high_as_the_body_can_jump_leaves_from_far_enough_back_when_it_slides() {
    jump_up_nearly_as_high_as_the_body_can(true);
}

#[test]
fn a_leap_comes_down_in_the_middle_of_a_small_platform_rather_than_near_its_edge() {
    let grid = build(join(&[
        floor(2.0, 2.0, 10.0, 12.0, 0.0),
        floor(13.0, 5.0, 17.0, 9.0, 1.0),
        wall(13.0, 5.0, 17.0, 5.0, -5.0, 1.0),
        wall(17.0, 5.0, 17.0, 9.0, -5.0, 1.0),
        wall(17.0, 9.0, 13.0, 9.0, -5.0, 1.0),
        wall(13.0, 9.0, 13.0, 5.0, -5.0, 1.0),
    ]));

    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(15.0, 7.0, 1.0),
        1.0,
        leaping(LEAPER),
    );

    assert_eq!(leapt.reason, "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    let landing = leg(&leapt, leap.leg_index);
    close(landing.z, 1.0, 1);
    let from_centre = landing.truncate().distance(Vec2::new(15.0, 7.0));
    assert!(
        from_centre <= 0.75,
        "the leap came down {from_centre:.2} m from the platform's centre"
    );
}

#[test]
fn a_leap_ends_where_the_body_comes_to_rest_after_its_landing_hop_and_slide() {
    let grid = gap_grid();
    let sliding = sliding();

    let leapt = find_with(
        &grid,
        v(6.0, 7.0, 0.0),
        v(22.0, 7.0, 0.0),
        1.0,
        leaping(sliding),
    );

    assert_eq!(leapt.reason, "routed");
    let leap = single_leap(&leapt);
    let takeoff = leg(&leapt, leap.leg_index - 1);
    let rest = leg(&leapt, leap.leg_index);
    let speed = if leap.run {
        sliding.run_speed
    } else {
        sliding.walk_speed
    };
    let launch = (2.0 * 9.8 * sliding.jump_height(leap.power)).sqrt();
    let aloft = seconds_aloft(ORDINARY_GROUND, launch);
    let comes_to_rest =
        speed * aloft + ORDINARY_GROUND.slide(speed, ORDINARY_GROUND.fall_speed(launch, aloft));
    let travelled = takeoff.truncate().distance(rest.truncate());
    assert!(
        (travelled - comes_to_rest).abs() <= 0.55,
        "the leap came to rest {travelled:.2} m on, where its physics puts it {comes_to_rest:.2} m on; {}",
        described(&leapt)
    );
}

#[test]
fn a_leap_onto_a_platform_too_short_to_come_to_rest_in_its_middle_comes_down_there_and_slides_on() {
    let grid = build(join(&[
        floor(2.0, 2.0, 8.0, 12.0, 0.0),
        floor(11.0, 6.0, 13.5, 8.0, 0.0),
        wall(11.0, 6.0, 13.5, 6.0, -5.0, 0.0),
        wall(13.5, 6.0, 13.5, 8.0, -5.0, 0.0),
        wall(13.5, 8.0, 11.0, 8.0, -5.0, 0.0),
        wall(11.0, 8.0, 11.0, 6.0, -5.0, 0.0),
    ]));

    let leapt = find_with(
        &grid,
        v(5.0, 7.0, 0.0),
        v(12.25, 7.0, 0.0),
        1.0,
        leaping(sliding()),
    );

    assert!(leapt.reason == "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    // Past the platform's middle at 12.25: coming down on a platform rather
    // than terrain, the body slides on without a landing hop.
    in_range(leg(&leapt, leap.leg_index).x, 12.3, 13.5);
}

#[test]
fn a_leap_onto_a_platform_it_would_slide_across_comes_down_in_its_middle_and_slides_to_its_far_edge()
 {
    let grid = build(join(&[
        floor(2.0, 2.0, 8.0, 12.0, 0.0),
        floor(13.0, 6.0, 16.0, 8.0, 1.0),
        wall(13.0, 6.0, 16.0, 6.0, -5.0, 1.0),
        wall(16.0, 6.0, 16.0, 8.0, -5.0, 1.0),
        wall(16.0, 8.0, 13.0, 8.0, -5.0, 1.0),
        wall(13.0, 8.0, 13.0, 6.0, -5.0, 1.0),
    ]));
    let on_ice = NavLeapAbility {
        full_jump_height: 8.0,
        physics: NavLeapPhysics {
            friction: 0.5,
            ..ORDINARY_GROUND
        },
        ..LEAPER
    };

    let leapt = find_with(
        &grid,
        v(5.0, 7.0, 0.0),
        v(14.5, 7.0, 1.0),
        1.0,
        leaping(on_ice),
    );

    assert!(leapt.reason == "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    let rest = leg(&leapt, leap.leg_index);
    close(rest.z, 1.0, 1);
    assert!(
        rest.x > 15.25,
        "the leap came to rest {:.2} m past the platform's middle",
        rest.x - 14.5
    );
}

#[test]
fn a_leap_comes_down_on_a_steep_roof_its_side_meets_before_its_feet() {
    let grid = build(join(&[
        floor(2.0, 2.0, 20.0, 10.0, 0.0),
        wall(2.0, 10.0, 20.0, 10.0, 0.0, 4.94),
        ramp(2.0, 10.0, 20.0, 12.5, 4.94, 7.4),
        floor(2.0, 12.5, 20.0, 14.5, 7.4),
        ramp(2.0, 14.5, 20.0, 17.0, 7.4, 4.94),
        wall(2.0, 17.0, 20.0, 17.0, 0.0, 4.94),
    ]));
    let climber = NavLeapAbility {
        full_jump_height: 6.42,
        physics: NavLeapPhysics {
            step_seconds: 0.035,
            ..ORDINARY_GROUND
        },
        ..LEAPER
    };

    let leapt = find_with(
        &grid,
        v(11.0, 5.0, 0.0),
        v(11.0, 11.0, 5.92),
        1.0,
        leaping(climber),
    );

    assert!(leapt.reason == "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    in_range(leg(&leapt, leap.leg_index).z, 4.9, 7.5);
}

fn a_jump_comes_to_rest_where_the_physics_body_comes_to_rest(speed: f32, height: f32, step: f32) {
    let physics = NavLeapPhysics {
        step_seconds: step,
        ..ORDINARY_GROUND
    };
    let launch = (2.0 * 9.8 * height).sqrt();
    let mut body = PhysicsBody::new();
    body.state = PhysicsState::GRAVITY;
    body.transient_state = TransientState::CONTACT;
    body.transient_state.insert(TransientState::ON_WALKABLE);
    body.elasticity = physics.elasticity;
    body.friction = physics.friction;
    body.velocity = Vec3::new(speed, 0.0, launch);

    let mut steps = 0;
    while steps < 2000 && body.velocity != Vec3::ZERO {
        let was_in_contact = body.in_contact();
        let was_on_walkable = body.on_walkable();
        body.calc_acceleration();
        body.update_physics_internal(step);
        let down = body.position().z <= 0.0;
        if down {
            let position = body.position();
            body.set_position(position.with_z(0.0));
            body.transient_state.insert(TransientState::CONTACT);
            body.transient_state.insert(TransientState::ON_WALKABLE);
        } else {
            body.transient_state.remove(TransientState::CONTACT);
            body.transient_state.remove(TransientState::ON_WALKABLE);
        }
        body.calc_acceleration();
        ac_physics::obj_update::handle_all_collisions(
            &mut body,
            down,
            Vec3::Z,
            was_in_contact,
            was_on_walkable,
            down,
        );
        steps += 1;
    }

    let aloft = seconds_aloft(physics, launch);
    let comes_to_rest = speed * aloft + physics.slide(speed, physics.fall_speed(launch, aloft));
    assert_eq!(body.velocity, Vec3::ZERO);
    in_range(
        body.position().x,
        comes_to_rest - speed * step,
        comes_to_rest + speed * step,
    );
}

#[test]
fn a_walking_jump_comes_to_rest_where_the_physics_body_comes_to_rest() {
    a_jump_comes_to_rest_where_the_physics_body_comes_to_rest(3.12, 2.5, 0.05);
}

#[test]
fn a_high_walking_jump_at_a_short_step_comes_to_rest_where_the_physics_body_comes_to_rest() {
    a_jump_comes_to_rest_where_the_physics_body_comes_to_rest(3.12, 4.0, 0.035);
}

#[test]
fn a_running_jump_comes_to_rest_where_the_physics_body_comes_to_rest() {
    a_jump_comes_to_rest_where_the_physics_body_comes_to_rest(9.5, 3.8, 0.0667);
}

#[test]
fn a_body_stands_on_top_of_a_post() {
    let grid = build_with(
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        vec![NavCylinder::new(v(10.0, 10.0, 0.0), 0.9, 1.2)],
        32.0,
        Vec::new(),
    );

    let top = grid.find_node(v(10.0, 10.0, 1.2), 0.5, 0.2);

    assert!(top >= 0, "no node stands on the post");
    in_range(grid.position(top).z, 1.15, 1.25);
}

/// Small platforms with gaps between them are climbed by jumping from one
/// to the next, the way a jump puzzle is built.
#[test]
fn a_stack_of_small_platforms_is_climbed_by_jumping() {
    let grid = build(join(&[
        floor(2.0, 2.0, 8.0, 20.0, 0.0),
        floor(10.0, 8.0, 12.5, 10.5, 1.4),
        floor(14.0, 8.0, 16.5, 10.5, 2.8),
        floor(18.0, 8.0, 20.5, 10.5, 4.2),
    ]));
    let climber = NavLeapAbility {
        full_jump_height: 6.42,
        physics: NavLeapPhysics {
            step_seconds: 0.035,
            ..ORDINARY_GROUND
        },
        ..LEAPER
    };

    let leapt = find_with(
        &grid,
        v(5.0, 9.0, 0.0),
        v(19.25, 9.25, 4.2),
        1.0,
        leaping(climber),
    );

    assert!(leapt.reason == "routed", "{}", described(&leapt));
    // A running leap onto a platform, rather than terrain, comes down with no
    // hop to carry it off again, so it may take the top in one leap rather
    // than hopping up the stack.
    assert!(!leapt.leaps.is_empty());
    in_range(leapt.legs.last().unwrap().z, 4.1, 4.3);
}

/// A candelabra is a hand's width across and taller than a body. Its cap is
/// floor a body perches on, and a jump aimed at it lands there.
#[test]
fn a_leap_reaches_a_cap_no_wider_than_a_hand() {
    let grid = build_with(
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        vec![NavCylinder::new(v(14.0, 10.0, 0.0), 0.2, 2.2)],
        32.0,
        Vec::new(),
    );
    let cap = v(14.0, 10.0, 2.2);
    let climber = NavLeapAbility {
        full_jump_height: 6.42,
        physics: NavLeapPhysics {
            step_seconds: 0.035,
            ..ORDINARY_GROUND
        },
        ..LEAPER
    };

    let walked = find(&grid, v(6.0, 10.0, 0.0), cap, 0.5);
    let leapt = find_with(&grid, v(6.0, 10.0, 0.0), cap, 0.5, leaping(climber));

    assert!(
        walked.outcome != NavRouteOutcome::Routed,
        "{}",
        described(&walked)
    );
    assert!(leapt.reason == "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    in_range(leg(&leapt, leap.leg_index).z, 2.1, 2.3);
}

/// The top of a post is floor a body perches on, too small to walk clear
/// of. A jump reaches it and a route may end there; a walk never crosses
/// it.
#[test]
fn a_leap_reaches_a_post_top_that_a_walk_cannot() {
    let grid = build_with(
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        vec![NavCylinder::new(v(14.0, 10.0, 0.0), 0.35, 1.4)],
        32.0,
        Vec::new(),
    );
    let post = v(14.0, 10.0, 1.4);

    let walked = find(&grid, v(6.0, 10.0, 0.0), post, 0.5);
    let leapt = find_with(&grid, v(6.0, 10.0, 0.0), post, 0.5, leaping(LEAPER));

    assert!(
        walked.outcome != NavRouteOutcome::Routed,
        "{}",
        described(&walked)
    );
    assert!(leapt.reason == "routed", "{}", described(&leapt));
    let leap = single_leap(&leapt);
    in_range(leg(&leapt, leap.leg_index).z, 1.3, 1.5);
}

/// A dome's top curves away, so a body stands over its middle and not out on
/// its flank.
#[test]
fn a_body_stands_on_a_domes_middle_and_not_its_flank() {
    let grid = build_with(
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        vec![NavCylinder::dome(v(10.0, 10.0, 0.0), 1.6, 3.2)],
        32.0,
        Vec::new(),
    );

    let middle = grid.find_node(v(10.0, 10.0, 3.2), 0.3, 0.2);
    let flank = grid.find_node(v(11.5, 10.0, 2.2), 0.2, 0.4);

    assert!(middle >= 0, "no node stands on the dome's middle");
    in_range(grid.position(middle).z, 3.0, 3.25);
    assert!(flank < 0, "a node stands out on the dome's flank");
}

/// A building's shell carries a plate where its ground floor is, and that
/// plate runs on over the stairwell down to its cellar. Inside the building
/// the shell is no floor and no ceiling: the cells' own floors, stairs and
/// ceilings are what a body meets.
#[test]
fn a_buildings_shell_neither_floors_nor_roofs_its_own_rooms() {
    let cells = join(&[
        floor(2.0, 2.0, 20.0, 20.0, 0.0),
        floor(2.0, 2.0, 20.0, 20.0, 8.0),
        ramp(6.0, 6.0, 14.0, 12.0, 0.0, 2.6),
    ]);
    let grid = build_with(
        cells,
        Vec::new(),
        32.0,
        join(&[
            floor(2.0, 2.0, 20.0, 20.0, 3.0),
            floor(2.0, 2.0, 20.0, 20.0, 12.0),
        ]),
    );

    let on_the_stairs = grid.find_node(v(10.0, 9.0, 1.3), 0.5, 0.5);
    let on_the_plate = grid.find_node(v(10.0, 9.0, 3.0), 0.5, 0.2);
    let on_the_roof = grid.find_node(v(10.0, 9.0, 12.0), 0.5, 0.2);

    assert!(
        on_the_stairs >= 0,
        "no node stands on the stairs under the shell's plate"
    );
    assert!(
        on_the_plate < 0,
        "a node stands on the shell's plate inside the building"
    );
    assert!(
        on_the_roof >= 0,
        "no node stands on the shell above the building's rooms"
    );
}
