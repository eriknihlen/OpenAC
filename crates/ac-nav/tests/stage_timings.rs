//! How long one stage of a long outdoor walk takes to search, with leaps,
//! for a body with an ordinary jump skill and one with a test character's
//! very high one, over the ground the client loads (its trees and rocks
//! with it): the stage east of Eastham whose far landblocks the client had
//! not loaded yet, and a stage a wall no walk or leap crosses; and walks
//! planned over one region: toward a goal behind such a wall, onto a raised
//! spot (a rock, a roof) near the start, and the reach of every spot a
//! route of 60 m gets to. A diagnostic: it prints its timings and runs only
//! when asked for.

mod support;

use std::time::Instant;

use ac_nav::grid::DEFAULT_CELL_SIZE;
use ac_nav::walk::{REGION_MARGIN, choose_stage_region, try_choose_region};
use ac_nav::{
    NavGrid, NavLeapAbility, NavLeapFinder, NavLeapPhysics, NavRouteRequest, NavTriangle, geometry,
    route,
};
use ac_physics::PhysicsEngine;
use glam::{Vec2, Vec3};

/// A body's leaps at a jump skill, running as fast as the same run skill.
fn leaper(skill: f32) -> NavLeapAbility {
    NavLeapAbility {
        walk_speed: 3.12,
        run_speed: skill / (skill + 200.0) * 11.0 + 4.0,
        full_jump_height: skill / (skill + 1300.0) * 22.2 + 0.05,
        maximum_drop: 12.0,
        physics: NavLeapPhysics {
            step_seconds: 1.0 / 30.0,
            elasticity: 0.05,
            friction: 0.95,
        },
    }
}

/// Landblocks published by the client's own loader, scenery and all, the
/// first's south-west corner the origin.
fn publish(dats: &ac_dat::DatCollection, landblocks: &[u32]) -> PhysicsEngine {
    let region: ac_dat::objects::Region = dats
        .get(0x1300_0000)
        .expect("read the region")
        .expect("the region");
    let heights = region.land_defs.land_height_table;
    let mut engine = PhysicsEngine::new();
    engine.attach_world(ac_physics::CollisionWorld::new());
    for &id in landblocks {
        ac_physics::content::publish_landblock(
            &mut engine,
            dats,
            &heights,
            id,
            ac_physics::content::landblock_origin(landblocks[0], id),
        )
        .expect("the landblock publishes")
        .expect("the landblock exists");
    }
    engine
}

/// The landblocks around Eastham from 0xCD93 north to row 0x96 and east up
/// to, not including, column `east_end`.
fn around_eastham(east_end: u32) -> Vec<u32> {
    let mut blocks = Vec::new();
    for x in 0xCDu32..east_end {
        for y in 0x93u32..=0x96 {
            blocks.push((x << 24) | (y << 16) | 0xFFFF);
        }
    }
    blocks
}

#[test]
#[ignore = "needs the retail dat files and OPENAC_DIAGNOSTIC_TESTS=1"]
fn stages_with_leaps_are_timed() {
    if std::env::var("OPENAC_DIAGNOSTIC_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_DIAGNOSTIC_TESTS=1 to run diagnostic tests");
        return;
    }
    let dats = support::dats();
    let body = support::load(&dats, &around_eastham(0xCE), &[]).body;
    // Points from 0xCD93's corner: where the Eastham portal lets out, where
    // a live walk's first stage from there ended, and the Seaside Lair.
    let ground = |engine: &PhysicsEngine, x: f32, y: f32| {
        Vec3::new(x, y, engine.sample_terrain_z(x, y).unwrap_or(8.0))
    };

    // The live walk's second stage, the 0xD1 landblocks not loaded yet.
    let unloaded = publish(&dats, &around_eastham(0xD1));
    let second = ground(&unloaded, 192.0 + 383.4, 192.0 + 179.8);
    let lair = ground(&unloaded, 4.0 * 192.0 + 150.58, 192.0 + 182.8);
    let (x, y, size) = choose_stage_region(second, lair);
    let captured = geometry::capture(&unloaded, x, y, size, None).expect("resident");
    let grid = NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).expect("the grid builds");
    time(
        "second stage, far landblocks not loaded",
        &grid,
        second,
        lair,
    );

    // From the portal's arrival toward the lair, a wall 30 m high across
    // the whole region 40 m on: leaps are sought and none found.
    let loaded = publish(&dats, &around_eastham(0xD3));
    let arrival = ground(&loaded, 192.0 + 151.05, 192.0 + 112.61);
    let (x, y, size) = choose_stage_region(arrival, lair);
    let mut captured = geometry::capture(&loaded, x, y, size, None).expect("resident");
    let toward = (lair - arrival).truncate().normalize();
    let across = Vec2::new(-toward.y, toward.x) * 1000.0;
    let middle = arrival.truncate() + toward * 40.0;
    let corner = |p: Vec2, z: f32| Vec3::new(p.x, p.y, z);
    let (a, b) = (middle - across, middle + across);
    let (low, high) = (arrival.z - 50.0, arrival.z + 30.0);
    captured.object_triangles.extend([
        NavTriangle::new(corner(a, low), corner(b, low), corner(b, high)),
        NavTriangle::new(corner(a, low), corner(b, high), corner(a, high)),
    ]);
    let grid = NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).expect("the grid builds");
    time("first stage, walled", &grid, arrival, lair);

    // A leap aimed onward, as a walk aims one it planned that will not aim
    // from where the body stands: from the portal's arrival onto the
    // nearest spot standing 1.5 m or more above the terrain under it (a
    // rock, a roof).
    let captured = geometry::capture(&loaded, x, y, size, None).expect("resident");
    let grid = NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).expect("the grid builds");
    let raised = (0..grid.node_count())
        .map(|node| grid.position(node))
        .filter(|at| {
            at.distance(arrival) < 30.0
                && loaded
                    .sample_terrain_z(at.x, at.y)
                    .is_some_and(|ground| at.z > ground + 1.5)
        })
        .min_by(|a, b| a.distance(arrival).total_cmp(&b.distance(arrival)))
        .expect("a raised spot near the arrival");
    // One region's walk toward a goal behind a wall 30 m high across the
    // whole region halfway there, 80 m and 250 m away: no walk arrives, so
    // the route is planned again with leaps, and none crosses.
    for meters in [80.0, 250.0] {
        let goal = arrival + (toward * meters).extend(0.0);
        let goal = ground(&loaded, goal.x, goal.y);
        let (x, y, size) = try_choose_region(arrival, goal).expect("one region holds both");
        let mut captured = geometry::capture(&loaded, x, y, size, None).expect("resident");
        let middle = arrival.truncate() + toward * (meters * 0.5);
        let (a, b) = (middle - across, middle + across);
        captured.object_triangles.extend([
            NavTriangle::new(corner(a, low), corner(b, low), corner(b, high)),
            NavTriangle::new(corner(a, low), corner(b, high), corner(a, high)),
        ]);
        let walled = NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).expect("the grid builds");
        time_find(
            &format!("one {size} m region, goal {meters} m behind a wall"),
            &walled,
            arrival,
            goal,
            false,
        );
    }

    for skill in [443.0, 15000.0] {
        let finder = NavLeapFinder::new(&grid, leaper(skill));
        let clock = Instant::now();
        let aimed = finder.aim_onward(arrival, raised, true);
        eprintln!(
            "onward leap onto {raised}, jump skill {skill}: {}, {:.0} ms",
            if aimed.is_some() { "aimed" } else { "none" },
            clock.elapsed().as_secs_f64() * 1000.0
        );
    }

    // One region's walk onto that raised spot, as a follow onto its floor
    // and as a plain walk to it.
    let (x, y, size) = try_choose_region(arrival, raised).expect("one region holds both");
    let captured = geometry::capture(&loaded, x, y, size, None).expect("resident");
    let near = NavGrid::build(&captured, body, DEFAULT_CELL_SIZE).expect("the grid builds");
    let name = format!("one {size} m region, onto the raised spot");
    time_find(&format!("{name}, its floor"), &near, arrival, raised, true);
    time_find(&name, &near, arrival, raised, false);

    // Every spot a route of at most 60 m gets to, leaping, over the stage's
    // grid (in the client only a sealed dungeon's reach is searched).
    for skill in [443.0, 15000.0] {
        let clock = Instant::now();
        let flood = route::flood(&grid, arrival, 60.0, &[], Some(leaper(skill)))
            .expect("the arrival stands on the grid");
        eprintln!(
            "reach within 60 m, jump skill {skill}: {} expansions{}, {:.0} ms",
            flood.expansions,
            if flood.gave_up { " (gave up)" } else { "" },
            clock.elapsed().as_secs_f64() * 1000.0
        );
    }
}

/// Plans one region's walk at jump skills 443 and 15000 and prints what it
/// took.
fn time_find(name: &str, grid: &NavGrid, from: Vec3, to: Vec3, on_goal_floor: bool) {
    for skill in [443.0, 15000.0] {
        let clock = Instant::now();
        let found = route::find(
            grid,
            from,
            to,
            1.0,
            &NavRouteRequest {
                leaps: Some(leaper(skill)),
                arrive_on_goal_floor: on_goal_floor,
                ..NavRouteRequest::default()
            },
        );
        eprintln!(
            "{name}, jump skill {skill}: {:?}, '{}', {} leaps, {} expansions, {:.0} ms",
            found.outcome,
            found.reason,
            found.leaps.len(),
            found.expansions,
            clock.elapsed().as_secs_f64() * 1000.0
        );
    }
}

/// Searches a stage at jump skills 443 and 15000 and prints what it took.
fn time(name: &str, grid: &NavGrid, from: Vec3, to: Vec3) {
    for skill in [443.0, 15000.0] {
        let clock = Instant::now();
        let found = route::find_toward(
            grid,
            from,
            to,
            REGION_MARGIN,
            &NavRouteRequest {
                leaps: Some(leaper(skill)),
                ..NavRouteRequest::default()
            },
        );
        eprintln!(
            "{name}, jump skill {skill}: {:?}, '{}', {} leaps, {} expansions, {:.0} ms",
            found.outcome,
            found.reason,
            found.leaps.len(),
            found.expansions,
            clock.elapsed().as_secs_f64() * 1000.0
        );
    }
}
