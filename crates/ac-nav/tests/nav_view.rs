//! The navigation view's segments. Ported from OpenAC
//! `tests/AcDream.App.Tests/Rendering/NavMeshDebugOverlayTests.cs` (the grid
//! marks); the route cases follow `NavMeshDebugOverlay.DrawRoute` and
//! `DrawLeg`.

use ac_nav::grid::DEFAULT_CELL_SIZE;
use ac_nav::route::{NavAvoidance, NavRoute, NavRouteOutcome};
use ac_nav::view::{
    self, CYLINDER_SEGMENTS, DRAW_STRIDE, LEAP_ARC_HEIGHT, LEAP_ARC_SEGMENTS, NavDrawState,
    NavLineKind, ROUTE_LIFT,
};
use ac_nav::{NavBody, NavGeometry, NavGrid, NavRouteLeap, NavTriangle};
use glam::Vec3;

const BODY: NavBody = NavBody::new(0.48, 1.835, 0.4, 0.4);

fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3::new(x, y, z)
}

fn floor(x0: f32, y0: f32, x1: f32, y1: f32, z: f32) -> Vec<NavTriangle> {
    vec![
        NavTriangle::new(v(x0, y0, z), v(x1, y0, z), v(x1, y1, z)),
        NavTriangle::new(v(x0, y0, z), v(x1, y1, z), v(x0, y1, z)),
    ]
}

fn build(triangles: Vec<NavTriangle>) -> NavGrid {
    let geometry = NavGeometry::new(
        0.0,
        0.0,
        32.0,
        Vec::new(),
        triangles,
        Vec::new(),
        Vec::new(),
        Vec::new(),
    )
    .unwrap();
    NavGrid::build(&geometry, BODY, DEFAULT_CELL_SIZE).unwrap()
}

fn on_stride(grid: &NavGrid, at: Vec3) -> bool {
    ((at.x - grid.origin_x()) / grid.cell_size()).floor() as i32 % DRAW_STRIDE == 0
        && ((at.y - grid.origin_y()) / grid.cell_size()).floor() as i32 % DRAW_STRIDE == 0
}

#[test]
fn every_tight_point_is_drawn_and_clear_points_only_on_every_second_column_and_row() {
    // The floor's edges fall on odd columns and rows, which the stride skips.
    let grid = build(floor(0.25, 0.25, 4.5, 4.5, 0.0));

    let marks = view::grid_marks(&grid, v(2.4, 2.4, 0.0));

    let tight = (0..grid.node_count())
        .filter(|&n| !grid.is_clear(n))
        .count();
    let clear_on_stride = (0..grid.node_count())
        .filter(|&n| grid.is_clear(n) && on_stride(&grid, grid.position(n)))
        .count();
    assert!(tight > 0);
    assert_eq!(tight, marks.iter().filter(|m| !m.1).count());
    assert!(marks.iter().any(|m| !m.1 && !on_stride(&grid, m.0)));
    assert_eq!(clear_on_stride, marks.iter().filter(|m| m.1).count());
    assert!(marks.iter().filter(|m| m.1).all(|m| on_stride(&grid, m.0)));
}

#[test]
fn floors_above_and_below_are_marked_and_only_distance_across_leaves_points_out() {
    let grid = build(
        [
            floor(1.0, 1.0, 7.0, 7.0, 0.0),
            floor(9.0, 1.0, 15.0, 7.0, 5.0),
            floor(1.0, 9.0, 7.0, 15.0, -5.0),
            floor(26.0, 26.0, 31.0, 31.0, 0.0),
        ]
        .concat(),
    );

    let marks = view::grid_marks(&grid, v(4.0, 4.0, 0.0));

    assert!(marks.iter().any(|m| m.0.z > 4.0));
    assert!(marks.iter().any(|m| m.0.z < -4.0));
    assert!(!marks.iter().any(|m| m.0.x > 26.0 && m.0.y > 26.0));
}

#[test]
fn each_grid_point_is_a_small_cross_in_its_colour_lifted_off_the_floor() {
    let grid = build(floor(0.25, 0.25, 4.5, 4.5, 0.0));
    let state = NavDrawState {
        grid: Some(&grid),
        route: None,
        goal: None,
        leg_index: None,
        staged: false,
    };
    let marks = view::grid_marks(&grid, v(2.4, 2.4, 0.0));

    let lines = view::lines(&state, Some(v(2.4, 2.4, 0.0)), &[], true);

    assert_eq!(lines.len(), marks.len() * 2);
    let (at, clear) = marks[0];
    assert!((at.z - 0.04).abs() < 1e-5);
    assert_eq!(lines[0].from, at - v(0.08, 0.0, 0.0));
    assert_eq!(lines[0].to, at + v(0.08, 0.0, 0.0));
    assert_eq!(lines[1].from, at - v(0.0, 0.08, 0.0));
    assert_eq!(
        lines[0].kind,
        if clear {
            NavLineKind::Clear
        } else {
            NavLineKind::Tight
        }
    );
    // Hidden, the grid draws nothing.
    assert!(view::lines(&state, Some(v(2.4, 2.4, 0.0)), &[], false).is_empty());
}

fn route(legs: Vec<Vec3>, leaps: Vec<NavRouteLeap>) -> NavRoute {
    NavRoute {
        outcome: NavRouteOutcome::Routed,
        reason: "routed".to_owned(),
        path: legs.clone(),
        legs,
        length: 0.0,
        expansions: 0,
        milliseconds: 0.0,
        leaps,
        ends_in_sight: true,
        scrape: 0.0,
        crowding: 0.0,
    }
}

fn route_only(lines: &[view::NavLine]) -> Vec<view::NavLine> {
    lines
        .iter()
        .copied()
        .filter(|l| l.kind == NavLineKind::Route)
        .collect()
}

#[test]
fn a_route_found_alone_is_a_lifted_magenta_line_along_its_legs_and_a_ring_at_the_goal() {
    let r = route(
        vec![v(0.0, 0.0, 0.0), v(4.0, 0.0, 0.0), v(4.0, 6.0, 1.0)],
        vec![],
    );
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: Some((v(4.0, 8.0, 1.0), 2.5)),
        leg_index: None,
        staged: false,
    };

    let lines = view::lines(&state, None, &[], false);

    // The goal's cylinder: two rings and four uprights.
    let ring = 2 * CYLINDER_SEGMENTS + 4;
    assert_eq!(lines.len(), ring + 2);
    assert!((lines[0].from - v(6.5, 8.0, 1.0)).length() < 1e-5);
    let legs = &lines[ring..];
    let lift = v(0.0, 0.0, ROUTE_LIFT);
    assert_eq!(legs[0].from, v(0.0, 0.0, 0.0) + lift);
    assert_eq!(legs[0].to, v(4.0, 0.0, 0.0) + lift);
    assert_eq!(legs[1].to, v(4.0, 6.0, 1.0) + lift);
    assert!(lines.iter().all(|l| l.kind == NavLineKind::Route));
}

#[test]
fn while_walking_the_line_starts_at_the_character_and_skips_the_legs_behind() {
    let r = route(
        vec![
            v(0.0, 0.0, 0.0),
            v(4.0, 0.0, 0.0),
            v(4.0, 6.0, 0.0),
            v(8.0, 6.0, 0.0),
        ],
        vec![],
    );
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: None,
        leg_index: Some(2),
        staged: false,
    };

    let lines = view::lines(&state, Some(v(4.0, 3.0, 0.0)), &[], false);

    let lift = v(0.0, 0.0, ROUTE_LIFT);
    assert_eq!(lines.len(), 2);
    assert_eq!(lines[0].from, v(4.0, 3.0, 0.0) + lift);
    assert_eq!(lines[0].to, v(4.0, 6.0, 0.0) + lift);
    assert_eq!(lines[1].from, v(4.0, 6.0, 0.0) + lift);
    assert_eq!(lines[1].to, v(8.0, 6.0, 0.0) + lift);
}

#[test]
fn a_leap_is_an_arc_a_metre_high_at_its_middle() {
    let r = route(
        vec![v(0.0, 0.0, 0.0), v(4.0, 0.0, 0.0)],
        vec![NavRouteLeap {
            leg_index: 1,
            power: 1.0,
            run: true,
        }],
    );
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: None,
        leg_index: None,
        staged: false,
    };

    let lines = view::lines(&state, None, &[], false);

    assert_eq!(lines.len(), LEAP_ARC_SEGMENTS as usize);
    let middle = lines[LEAP_ARC_SEGMENTS as usize / 2 - 1].to;
    assert!((middle - v(2.0, 0.0, ROUTE_LIFT + LEAP_ARC_HEIGHT)).length() < 1e-5);
    assert_eq!(lines.last().unwrap().to, v(4.0, 0.0, ROUTE_LIFT));
}

#[test]
fn a_stage_adds_a_dimmer_line_on_to_the_goal_beyond_it() {
    let r = route(vec![v(0.0, 0.0, 0.0), v(4.0, 0.0, 0.0)], vec![]);
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: Some((v(40.0, 0.0, 0.0), 2.5)),
        leg_index: None,
        staged: true,
    };

    let lines = view::lines(&state, None, &[], false);

    let beyond: Vec<_> = lines
        .iter()
        .filter(|l| l.kind == NavLineKind::Beyond)
        .collect();
    assert_eq!(beyond.len(), 1);
    assert_eq!(beyond[0].from, v(4.0, 0.0, ROUTE_LIFT));
    assert_eq!(beyond[0].to, v(40.0, 0.0, ROUTE_LIFT));
}

#[test]
fn with_the_grid_shown_the_path_leg_posts_and_portals_are_added() {
    let r = route(vec![v(0.0, 0.0, 0.0), v(4.0, 0.0, 0.0)], vec![]);
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: None,
        leg_index: None,
        staged: false,
    };
    let portals = [NavAvoidance::new(v(10.0, 0.0, 0.0), 1.5)];

    let lines = view::lines(&state, Some(v(0.0, 0.0, 0.0)), &portals, true);

    let portal = lines
        .iter()
        .filter(|l| l.kind == NavLineKind::Portal)
        .count();
    assert_eq!(portal, 2 * CYLINDER_SEGMENTS + 4);
    let path: Vec<_> = lines
        .iter()
        .filter(|l| l.kind == NavLineKind::Path)
        .collect();
    assert_eq!(path.len(), 1);
    assert!((path[0].from.z - 0.15).abs() < 1e-6);
    let routes = route_only(&lines);
    // Two posts a metre high, then the leg.
    assert_eq!(routes.len(), 3);
    assert_eq!(routes[0].to, v(0.0, 0.0, 1.0));
    assert_eq!(routes[1].to, v(4.0, 0.0, 1.0));
    // Without the character there is no grid and no portal.
    assert!(
        !view::lines(&state, None, &portals, true)
            .iter()
            .any(|l| l.kind == NavLineKind::Portal)
    );
}

#[test]
fn a_route_that_found_nothing_draws_nothing() {
    let mut r = route(vec![v(0.0, 0.0, 0.0), v(4.0, 0.0, 0.0)], vec![]);
    r.outcome = NavRouteOutcome::NoGoal;
    let state = NavDrawState {
        grid: None,
        route: Some(&r),
        goal: None,
        leg_index: None,
        staged: false,
    };

    assert!(view::lines(&state, None, &[], true).is_empty());
}

#[test]
fn the_colours_are_openacs() {
    assert_eq!(NavLineKind::Clear.rgb(), [0.2, 0.95, 0.35]);
    assert_eq!(NavLineKind::Tight.rgb(), [0.9, 0.25, 0.15]);
    assert_eq!(NavLineKind::Path.rgb(), [0.85, 0.85, 0.85]);
    assert_eq!(NavLineKind::Route.rgb(), [1.0, 0.0, 1.0]);
    assert_eq!(NavLineKind::Beyond.rgb(), [0.5, 0.0, 0.5]);
    assert_eq!(NavLineKind::Portal.rgb(), [0.8, 0.3, 1.0]);
}
