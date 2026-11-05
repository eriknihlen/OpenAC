//! Navigation over Holtburg from the installed game files: a walk across
//! town, a walk through a cottage's door to a room inside, and the running
//! jump from a porch onto a roof that OpenAC's
//! `HoltburgRoofJumpInstalledDatTests` plans.

mod support;

use ac_nav::grid::DEFAULT_CELL_SIZE;
use ac_nav::{
    NavGrid, NavLeapAbility, NavLeapPhysics, NavRouteOutcome, NavRouteRequest, geometry, route,
};
use glam::Vec3;

/// Holtburg and the landblocks east and south of it, as OpenAC's test loads
/// them.
const HOLTBURG: [u32; 4] = [0xA9B4_FFFF, 0xAAB4_FFFF, 0xA9B3_FFFF, 0xAAB3_FFFF];

fn holtburg_grid(world: &support::PublishedLandblocks) -> NavGrid {
    let geometry =
        geometry::capture(&world.engine, 0.0, 0.0, 192.0, None).expect("Holtburg is resident");
    NavGrid::build(&geometry, world.body, DEFAULT_CELL_SIZE).expect("the grid builds")
}

fn ground(world: &support::PublishedLandblocks, x: f32, y: f32) -> Vec3 {
    Vec3::new(
        x,
        y,
        world
            .engine
            .sample_terrain_z(x, y)
            .expect("the point is on resident terrain"),
    )
}

/// A walk from the south-west of Holtburg to the north-east, past the
/// town's buildings, over the ground the client walks.
#[test]
#[ignore = "needs the retail dat files"]
fn a_walk_crosses_holtburg() {
    let dats = support::dats();
    let world = support::load(&dats, &HOLTBURG, &[]);
    let grid = holtburg_grid(&world);
    let from = ground(&world, 40.0, 40.0);
    let to = ground(&world, 170.0, 170.0);

    let route = route::find(&grid, from, to, 1.0, &NavRouteRequest::default());

    assert_eq!(route.outcome, NavRouteOutcome::Routed, "{}", route.reason);
    assert_eq!(route.reason, "routed");
    let end = *route.legs.last().unwrap();
    assert!(
        end.truncate().distance(to.truncate()) <= 1.0,
        "the route ends {end:?}, short of {to:?}"
    );
    assert!(route.length >= from.truncate().distance(to.truncate()) - 1.0);
    assert!(route.leaps.is_empty());
}

/// A walk from the ground outside to a room of the cottage south of the
/// town's middle: through its door, onto the room's own floor under its
/// ceiling.
#[test]
#[ignore = "needs the retail dat files"]
fn a_walk_goes_through_a_door_into_a_cottage() {
    // The cottage's cells start at 0xA9B4013F; this is the first one's
    // origin, on its floor at 94 m.
    const COTTAGE_ROOM: u32 = 0xA9B4_013F;
    let dats = support::dats();
    let world = support::load(&dats, &HOLTBURG, &[COTTAGE_ROOM]);
    let grid = holtburg_grid(&world);
    let room = world.cell_origins[&COTTAGE_ROOM];
    let from = ground(&world, 120.0, 60.0);

    let route = route::find(
        &grid,
        from,
        room,
        1.0,
        &NavRouteRequest {
            arrive_on_goal_floor: true,
            ..NavRouteRequest::default()
        },
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed, "{}", route.reason);
    assert_eq!(route.reason, "routed");
    let end = *route.path.last().unwrap();
    assert!(
        end.truncate().distance(room.truncate()) <= 1.0,
        "the route ends {end:?}, short of the room at {room:?}"
    );
    assert!((end.z - room.z).abs() < 0.5, "the route ends at {end:?}");
    let node = grid.find_standing_node(end, 0.2, 0.2);
    assert!(node >= 0);
    assert!(
        grid.ceiling(node).is_finite(),
        "the route's end stands under no ceiling, so it is not inside"
    );
    assert!(
        grid.ceiling(grid.find_standing_node(from, 1.5, 1.0))
            .is_infinite(),
        "the walk starts outdoors"
    );
}

/// Jump skill 443 with nothing carried, running about 10.6 m/s.
fn skill_443() -> NavLeapAbility {
    NavLeapAbility {
        walk_speed: 3.12,
        run_speed: 10.6,
        full_jump_height: 443.0 / (443.0 + 1300.0) * 22.2 + 0.05,
        maximum_drop: 12.0,
        physics: NavLeapPhysics {
            step_seconds: 1.0 / 30.0,
            elasticity: 0.05,
            friction: 0.95,
        },
    }
}

/// A running jump in Holtburg from a porch onto the flat roof of a building
/// with a room under the roof's edge, as a player with jump skill 443 makes
/// it. Ported from OpenAC
/// `tests/AcDream.App.Tests/Navigation/HoltburgRoofJumpInstalledDatTests.cs`.
#[test]
#[ignore = "needs the retail dat files"]
// The porch and roof points are OpenAC's literals, kept as written.
#[allow(clippy::excessive_precision)]
fn the_porch_jump_onto_the_roof_is_planned() {
    let porch = Vec3::new(116.8918, 26.095654, 97.505005);
    let on_roof = Vec3::new(136.324829, 8.420344, 102.005005);
    let dats = support::dats();
    let world = support::load(&dats, &HOLTBURG, &[]);
    let geometry =
        geometry::capture(&world.engine, 88.0, -40.0, 96.0, None).expect("Holtburg is resident");
    let grid = NavGrid::build(&geometry, world.body, DEFAULT_CELL_SIZE).unwrap();

    let route = route::find(
        &grid,
        porch,
        on_roof,
        2.5,
        &NavRouteRequest {
            leaps: Some(skill_443()),
            arrive_on_goal_floor: true,
            goal_radius: 0.5,
            ..NavRouteRequest::default()
        },
    );

    assert_eq!(route.outcome, NavRouteOutcome::Routed, "{}", route.reason);
    assert_eq!(route.reason, "routed");
    assert_eq!(route.leaps.len(), 1);
    let leap = route.leaps[0];
    let takeoff = route.legs[(leap.leg_index - 1) as usize];
    let landing = route.legs[leap.leg_index as usize];
    assert!((takeoff.z - 97.5).abs() <= 0.05, "takeoff {takeoff:?}");
    assert!((landing.z - 102.0).abs() <= 0.05, "landing {landing:?}");
}

/// A cottage's rooms see outside; an outdoor cell is no dungeon; nor is a
/// landblock's own record. OpenAC `SealedDungeonCells.IsSealedDungeon`.
#[test]
#[ignore = "needs the retail dat files"]
fn only_indoor_cells_that_see_nothing_outside_are_a_sealed_dungeon() {
    let dats = support::dats();

    assert!(!ac_nav::dungeon::is_sealed_dungeon(&dats, 0xA9B4_013F));
    assert!(!ac_nav::dungeon::is_sealed_dungeon(&dats, 0xA9B4_0021));
    assert!(!ac_nav::dungeon::is_sealed_dungeon(&dats, 0xA9B4_FFFE));
    // No room in Holtburg is sealed: every one of its cells sees outside.
    assert!(
        (0x0100..0x0180)
            .map(|low| 0xA9B4_0000 | low)
            .all(|cell| !ac_nav::dungeon::is_sealed_dungeon(&dats, cell))
    );
    // The landblocks along x 0x01 are dungeons: their first cell sees
    // nothing outside.
    assert!(ac_nav::dungeon::is_sealed_dungeon(&dats, 0x0100_0100));
    assert!(ac_nav::dungeon::is_sealed_dungeon(&dats, 0x0119_0100));
}

/// Real dungeons flatten to a plan with floor and walls. OpenAC
/// `DungeonFloorplanSpikeInstalledDatTests.RealDungeonsAreDrawnToFiles`
/// (its three default landblocks; the drawing to files is left out).
#[test]
#[ignore = "needs the retail dat files"]
fn real_dungeons_flatten_to_floors_and_walls() {
    let dats = support::dats();
    let mut builder = ac_nav::floorplan::DungeonFloorplanBuilder::new();
    for landblock in [0x8A02_0000, 0x0125_0000, 0x0007_0000] {
        let plan = builder
            .try_build(&dats, landblock)
            .expect("the cells read")
            .unwrap_or_else(|| panic!("0x{landblock:08X} has a plan"));
        println!(
            "0x{landblock:08X}: cells={} layers={} {}",
            plan.cells.len(),
            plan.layers.len(),
            plan.counts
        );
        assert!(plan.counts.floors > 0, "no floor at all");
        assert!(plan.counts.walls_after_merge > 0, "no walls at all");
    }
}

/// Every cell of an installed dungeon routed to by the middle of its bounds,
/// the place a dungeon map gives for it, as it is given and as it stands on
/// the floor under it. A diagnostic: it prints its counts. OpenAC PR #222
/// `DungeonPlaceFloorInstalledDatTests.TheMiddleOfEveryCellRoutesOnceItStandsOnTheFloor`:
/// 0x5B48's wings are joined by more than its cells, so from its largest
/// wing about 265 of its cells route on the floor and few as given; 0x5948
/// routes nearly every cell.
#[test]
#[ignore = "needs the retail dat files and OPENAC_DIAGNOSTIC_TESTS=1"]
fn the_middle_of_every_dungeon_cell_routes_once_it_stands_on_the_floor() {
    // A diagnostic (it prints its counts, about three minutes in a debug
    // build): `scripts/check.*` runs ignored tests when the dats are
    // present, so it runs only when asked for.
    if std::env::var("OPENAC_DIAGNOSTIC_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_DIAGNOSTIC_TESTS=1 to run diagnostic tests");
        return;
    }
    use std::collections::HashMap;

    use ac_nav::grid::DEFAULT_CELL_SIZE;
    use ac_nav::route::GOAL_HEIGHT_TOLERANCE;
    use ac_nav::sources::on_the_cell_floor;
    use ac_nav::walk::planning_radius;

    let dats = support::dats();
    for (dungeon, start_cell) in [(0x5B48_FFFFu32, 0x5B48_013Cu32), (0x5948_FFFF, 0x5948_0100)] {
        let cell_ids: Vec<u32> = (0x100..0x500)
            .map(|low| (dungeon & 0xFFFF_0000) | low)
            .collect();
        let world = support::load(&dats, &[dungeon], &cell_ids);
        let cells = &world
            .engine
            .landblock(dungeon)
            .expect("the dungeon is resident")
            .cells;
        let (minimum, maximum) =
            geometry::measure_cells(&world.engine, dungeon).expect("the dungeon has cells");
        let size =
            (((maximum.x - minimum.x).max(maximum.y - minimum.y) + 48.0) / 16.0).ceil() * 16.0;
        let origin_x = (((minimum.x + maximum.x) * 0.5 - size * 0.5) / DEFAULT_CELL_SIZE).floor()
            * DEFAULT_CELL_SIZE;
        let origin_y = (((minimum.y + maximum.y) * 0.5 - size * 0.5) / DEFAULT_CELL_SIZE).floor()
            * DEFAULT_CELL_SIZE;
        let captured =
            geometry::capture_dungeon(&world.engine, dungeon, origin_x, origin_y, size, None)
                .expect("the dungeon is resident");
        let grid =
            NavGrid::build(&captured, world.body, DEFAULT_CELL_SIZE).expect("the grid builds");

        let start_node = grid.find_walkable_node(world.cell_origins[&start_cell], 1.0, 1.5);
        assert!(start_node >= 0);
        let start = grid.position(start_node);
        let arrival = planning_radius(2.5);
        let request = NavRouteRequest {
            arrive_on_goal_floor: true,
            ..NavRouteRequest::default()
        };
        let (mut as_given, mut on_the_floor, mut tall, mut floorless, mut face_up_floor) =
            (0, 0, 0, 0, 0);
        let mut reasons: HashMap<String, i32> = HashMap::new();
        let mut floored_reasons: HashMap<String, i32> = HashMap::new();
        for cell in cells {
            let mut low = Vec3::splat(f32::INFINITY);
            let mut high = Vec3::splat(f32::NEG_INFINITY);
            for [a, b, c] in cell.triangles() {
                low = low.min(a.min(b.min(*c)));
                high = high.max(a.max(b.max(*c)));
            }
            if low.x.is_infinite() {
                continue;
            }
            let middle = (low + high) * 0.5;
            let placed = on_the_cell_floor(&world.engine, cell.cell_id(), middle, None);
            if middle.z - placed.z > GOAL_HEIGHT_TOLERANCE {
                tall += 1;
            }
            if geometry::highest_floor_under(
                &world.engine,
                dungeon,
                middle.x,
                middle.y,
                f32::NEG_INFINITY,
                middle.z,
                None,
            )
            .is_some()
            {
                face_up_floor += 1;
            }
            if cell
                .sample_level_z_at_or_below(middle.x, middle.y, middle.z)
                .is_none()
            {
                floorless += 1;
            }
            let raw = route::find(&grid, start, middle, arrival, &request);
            if raw.outcome == NavRouteOutcome::Routed {
                as_given += 1;
            } else {
                *reasons.entry(raw.reason).or_default() += 1;
            }
            let floored = route::find(&grid, start, placed, arrival, &request);
            if floored.outcome == NavRouteOutcome::Routed {
                on_the_floor += 1;
            } else {
                *floored_reasons.entry(floored.reason).or_default() += 1;
            }
        }

        println!(
            "0x{:04X}: {} cells, {tall} with their middle higher over the floor than a walk arrives, {floorless} with no floor of their own under it",
            dungeon >> 16,
            cells.len()
        );
        println!("  routed as given: {as_given}; routed on the floor: {on_the_floor}");
        println!("  {face_up_floor} with a face-up floor somewhere under their middle");
        let top = |counts: HashMap<String, i32>| {
            let mut counts: Vec<(String, i32)> = counts.into_iter().collect();
            counts.sort_by_key(|count| std::cmp::Reverse(count.1));
            counts.truncate(5);
            counts
        };
        for (reason, count) in top(reasons) {
            println!("  {count} as given: {reason}");
        }
        for (reason, count) in top(floored_reasons) {
            println!("  {count} on the floor: {reason}");
        }
        assert!(on_the_floor > as_given);
    }
}

/// Deewain's rock jump puzzle, built from the installed game files with the
/// rocks the server places in it, and climbed: every rock top is floor the
/// grid knows, and a route leaves one rock for the next. Ported from OpenAC
/// `tests/AcDream.App.Tests/Navigation/RockJumpPuzzleInstalledDatTests.cs`
/// (its rocks are the set a live server placed, as recorded there).
mod rock_jump {
    use super::*;
    use ac_dat::objects::{GfxObj, Setup};
    use ac_physics::shadow_builder::from_setup;
    use ac_physics::{
        EntityCollisionFlags, PhysicsState, RegistrationSite, ShadowCollisionType,
        ShadowPartGeometry,
    };
    use glam::{Quat, Vec2};

    const LANDBLOCK: u32 = 0x7E03_FFFF;
    const SMALL_ROCK: u32 = 0x0200_1A44;
    const WIDE_ROCK: u32 = 0x0200_1A43;
    const TALL_ROCK: u32 = 0x0200_1A45;
    const REGION_ORIGIN_X: f32 = 76.0;
    const REGION_ORIGIN_Y: f32 = -288.0;
    const REGION_SIZE: f32 = 64.0;

    fn rocks() -> [(u32, u32, Vec3); 17] {
        [
            (
                SMALL_ROCK,
                0x7E03_0142,
                Vec3::new(101.230, -274.100, -2.100),
            ),
            (SMALL_ROCK, 0x7E03_0124, Vec3::new(94.870, -273.130, -0.360)),
            (
                SMALL_ROCK,
                0x7E03_0142,
                Vec3::new(103.420, -269.500, -0.230),
            ),
            (TALL_ROCK, 0x7E03_0407, Vec3::new(92.590, -265.000, 1.130)),
            (TALL_ROCK, 0x7E03_040E, Vec3::new(97.890, -268.780, 2.130)),
            (TALL_ROCK, 0x7E03_0407, Vec3::new(94.870, -263.130, 5.640)),
            (TALL_ROCK, 0x7E03_061F, Vec3::new(113.300, -258.000, 6.450)),
            (WIDE_ROCK, 0x7E03_0610, Vec3::new(92.590, -255.000, 7.130)),
            (WIDE_ROCK, 0x7E03_0618, Vec3::new(98.770, -254.100, 9.900)),
            (WIDE_ROCK, 0x7E03_08C6, Vec3::new(86.700, -248.000, 12.450)),
            (
                SMALL_ROCK,
                0x7E03_08CC,
                Vec3::new(100.630, -245.900, 12.790),
            ),
            (WIDE_ROCK, 0x7E03_08D0, Vec3::new(107.410, -245.000, 13.130)),
            (SMALL_ROCK, 0x7E03_08C6, Vec3::new(93.780, -246.800, 13.600)),
            (
                SMALL_ROCK,
                0x7E03_08CB,
                Vec3::new(103.420, -240.500, 16.770),
            ),
            (WIDE_ROCK, 0x7E03_096A, Vec3::new(101.230, -236.100, 18.220)),
            (WIDE_ROCK, 0x7E03_0966, Vec3::new(88.770, -241.130, 18.760)),
            (TALL_ROCK, 0x7E03_096E, Vec3::new(106.220, -243.200, 21.600)),
        ]
    }

    /// A character who jumps as high as the one that walked the puzzle.
    fn climber() -> NavLeapAbility {
        NavLeapAbility {
            walk_speed: 3.12,
            run_speed: 7.3,
            full_jump_height: 6.42,
            maximum_drop: 12.0,
            physics: NavLeapPhysics {
                step_seconds: 1.0 / 30.0,
                elasticity: 0.05,
                friction: 0.95,
            },
        }
    }

    /// Places the rocks into the world the way the client places an object
    /// it is told about: its parts' collision, at its own position.
    fn place_rocks(
        dats: &ac_dat::DatCollection,
        world: &mut support::PublishedLandblocks,
    ) -> Vec<u32> {
        let mut ids = Vec::new();
        for (index, (setup_id, cell, at)) in rocks().into_iter().enumerate() {
            let setup: Setup = dats
                .get(setup_id)
                .expect("read the rock")
                .expect("the rock");
            {
                let collision = world.engine.world_mut().expect("a collision world");
                for &part in &setup.parts {
                    if collision.gfx_obj(part).is_none()
                        && let Ok(Some(gfx)) = dats.get::<GfxObj>(part)
                    {
                        collision
                            .cache_gfx_obj(&gfx)
                            .expect("the rock's tree prepares");
                    }
                }
            }
            let (shadows, collision) = world.engine.shadows_and_world();
            let has_bsp = |id: u32| collision.gfx_obj(id).is_some_and(|g| g.has_physics());
            let bounds = |id: u32| {
                collision
                    .gfx_obj(id)
                    .filter(|g| g.has_physics())
                    .map(|g| ShadowPartGeometry::new(g.bounding_sphere, g.visual_bounds.as_ref()))
            };
            let shapes = from_setup(&setup, 1.0, &has_bsp, None, None, Some(&bounds), None);
            if index == 0 {
                let cylinders = shapes
                    .iter()
                    .filter(|s| s.collision_type == ShadowCollisionType::Cylinder)
                    .count();
                println!(
                    "rock setup 0x{setup_id:08X}: {} parts, {} shapes ({cylinders} cylinders)",
                    setup.parts.len(),
                    shapes.len()
                );
            }
            let entity_id = 0x8005_0690 + index as u32;
            shadows
                .register_multi_part(
                    collision,
                    entity_id,
                    &shapes,
                    RegistrationSite {
                        world_pos: at,
                        rotation: Quat::IDENTITY,
                        world_offset_x: 0.0,
                        world_offset_y: 0.0,
                        landblock_id: LANDBLOCK,
                        seed_cell_id: cell,
                    },
                    (PhysicsState::REPORT_COLLISIONS | PhysicsState::IGNORE_COLLISIONS).bits(),
                    EntityCollisionFlags(EntityCollisionFlags::HAS_WEENIE),
                    false,
                    None,
                )
                .expect("the rock registers");
            ids.push(entity_id);
        }
        ids
    }

    /// The floor the grid put on top of a rock: the highest node just above
    /// it.
    fn rock_top(grid: &NavGrid, at: Vec3) -> i32 {
        let mut best = -1;
        let mut height = f32::NEG_INFINITY;
        for node in 0..grid.node_count() {
            let position = grid.position(node);
            if position.truncate().distance_squared(at.truncate()) > 1.2 * 1.2 {
                continue;
            }
            if position.z <= height || position.z < at.z - 0.3 || position.z > at.z + 1.5 {
                continue;
            }
            height = position.z;
            best = node;
        }
        best
    }

    /// The floor under the lowest rock, where a climb begins.
    fn floor_below_first_rock(grid: &NavGrid) -> Vec3 {
        let at = rocks()[0].2;
        let mut best = -1;
        let mut height = f32::NEG_INFINITY;
        for node in 0..grid.node_count() {
            let position = grid.position(node);
            if position.truncate().distance(at.truncate()) > 6.0 {
                continue;
            }
            if position.z > at.z || position.z <= height || !grid.is_clear(node) {
                continue;
            }
            height = position.z;
            best = node;
        }
        if best >= 0 {
            grid.position(best)
        } else {
            Vec3::new(at.x, at.y, at.z - 2.0)
        }
    }

    fn routed(grid: &NavGrid, from: Vec3, to: Vec3) -> ac_nav::NavRoute {
        route::find(
            grid,
            from,
            to,
            0.6,
            &NavRouteRequest {
                leaps: Some(climber()),
                ..NavRouteRequest::default()
            },
        )
    }

    #[test]
    #[ignore = "needs the retail dat files"]
    fn the_rock_jump_puzzle_is_climbed_rock_by_rock() {
        let dats = support::dats();
        let mut world = support::load(&dats, &[LANDBLOCK], &[]);
        let placed: std::collections::HashSet<u32> =
            place_rocks(&dats, &mut world).into_iter().collect();

        let bare_geometry = geometry::capture_dungeon(
            &world.engine,
            LANDBLOCK,
            REGION_ORIGIN_X,
            REGION_ORIGIN_Y,
            REGION_SIZE,
            None,
        )
        .expect("the landblock is resident");
        let stands_on = |id: u32| placed.contains(&id);
        let geometry = geometry::capture_dungeon(
            &world.engine,
            LANDBLOCK,
            REGION_ORIGIN_X,
            REGION_ORIGIN_Y,
            REGION_SIZE,
            Some(&stands_on),
        )
        .expect("the landblock is resident");
        let bare = NavGrid::build(&bare_geometry, world.body, DEFAULT_CELL_SIZE).unwrap();
        let grid = NavGrid::build(&geometry, world.body, DEFAULT_CELL_SIZE).unwrap();
        println!(
            "{} rocks in the geometry; {} nodes without them, {} with",
            geometry.object_ids.len(),
            bare.report().nodes,
            grid.report().nodes
        );

        let mut tops = Vec::new();
        for (index, (_, _, at)) in rocks().into_iter().enumerate() {
            let node = rock_top(&grid, at);
            assert!(node >= 0, "the grid put no floor on rock {}", index + 1);
            tops.push(grid.position(node));
        }

        // Every hop the puzzle asks for: each rock to a rock near it, no
        // higher than a jump.
        let flat = |p: Vec3| Vec2::new(p.x, p.y);
        let (mut near, mut reached) = (0, 0);
        for from in 0..tops.len() {
            for to in 0..tops.len() {
                if to == from {
                    continue;
                }
                let along = flat(tops[from]).distance(flat(tops[to]));
                let rise = tops[to].z - tops[from].z;
                if along > 12.0 || !(-11.0..=6.4).contains(&rise) {
                    continue;
                }
                near += 1;
                if routed(&grid, tops[from], tops[to]).reason == "routed" {
                    reached += 1;
                }
            }
        }
        println!("{reached} of {near} near-rock hops routed");

        let up = routed(&grid, floor_below_first_rock(&grid), tops[tops.len() - 1]);
        println!(
            "the floor to the highest rock: {}, {} leaps",
            up.reason,
            up.leaps.len()
        );
        // Rock 12 to rock 11 is level and a jump apart.
        let across = routed(&grid, tops[11], tops[10]);

        assert!(!geometry.object_ids.is_empty());
        assert_eq!(
            across.reason, "routed",
            "no route leaves rock 12 for rock 11"
        );
        assert_eq!(up.reason, "routed", "no route climbs the puzzle");
        assert!(
            reached >= 55,
            "only {reached} of {near} near-rock hops routed"
        );
    }
}
