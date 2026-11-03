//! How long navigation questions take in installed dungeons: path previews
//! to the middle of every cell, asked the way a plugin asks them, with the
//! time spent on the asking thread and on the worker printed apart. A
//! diagnostic: it prints its counts and runs only when asked for.

mod support;

use std::time::Instant;

use ac_nav::motor::{MoveChannel, MoveRequest, ScriptedMoveSnapshot};
use ac_nav::sources::on_the_cell_floor;
use ac_nav::walk::{
    self, NavGoalSource, PlanMode, PlanStatus, PreviewTarget, WalkBody, WalkBodySample,
    WalkController, WalkWorld,
};
use ac_nav::{NavBody, geometry};
use ac_physics::PhysicsEngine;
use glam::Vec3;

/// A character standing still where a test puts it.
struct Standing {
    position: Vec3,
    cell_id: u32,
    body: NavBody,
}

impl WalkBody for Standing {
    fn try_sample(&self) -> Option<WalkBodySample> {
        Some(WalkBodySample {
            position: self.position,
            heading_degrees: 0.0,
            body: self.body,
            moves: ScriptedMoveSnapshot::default(),
            in_portal_space: false,
            cell_id: self.cell_id,
            leaps: None,
            airborne: false,
            turning: None,
            walk_stop_meters: 0.0,
            run_stop_meters: 0.0,
            player_movement_input_frames: 0,
        })
    }

    fn begin_move(&mut self, _request: MoveRequest) -> bool {
        false
    }

    fn stop_move(&mut self, _channel: MoveChannel) -> bool {
        false
    }
}

/// Places stand on the floor of their cell, as the client's goal source
/// stands them.
struct Floors<'a>(&'a PhysicsEngine);

impl NavGoalSource for Floors<'_> {
    fn try_locate(&self, _object_id: u32) -> Option<Vec3> {
        None
    }

    fn try_locate_place(&self, cell_id: u32, local: Vec3) -> Option<Vec3> {
        Some(on_the_cell_floor(self.0, cell_id, local, None))
    }

    fn try_global_of(&self, world: Vec3) -> Option<Vec3> {
        Some(world)
    }
}

/// The middle of each cell's bounds, the place a dungeon map gives for it.
fn cell_middles(engine: &PhysicsEngine, dungeon: u32) -> Vec<(u32, Vec3)> {
    let cells = &engine
        .landblock(dungeon)
        .expect("the dungeon is resident")
        .cells;
    cells
        .iter()
        .filter_map(|cell| {
            let mut low = Vec3::splat(f32::INFINITY);
            let mut high = Vec3::splat(f32::NEG_INFINITY);
            for [a, b, c] in cell.triangles() {
                low = low.min(a.min(b.min(*c)));
                high = high.max(a.max(b.max(*c)));
            }
            (!low.x.is_infinite()).then(|| (cell.cell_id(), (low + high) * 0.5))
        })
        .collect()
}

fn median(values: &mut [f64]) -> f64 {
    if values.is_empty() {
        return f64::NAN;
    }
    values.sort_by(f64::total_cmp);
    values[values.len() / 2]
}

fn milliseconds(since: Instant) -> f64 {
    since.elapsed().as_secs_f64() * 1000.0
}

/// Previews to the middle of every cell of a few installed dungeons, each
/// asked on this thread and searched on a worker as the client does.
#[test]
#[ignore = "needs the retail dat files and OPENAC_DIAGNOSTIC_TESTS=1"]
fn previews_in_installed_dungeons_are_timed() {
    if std::env::var("OPENAC_DIAGNOSTIC_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_DIAGNOSTIC_TESTS=1 to run diagnostic tests");
        return;
    }
    let dats = support::dats();
    let sealed = |cell: u32| ac_nav::dungeon::is_sealed_dungeon(&dats, cell);
    for dungeon in [0x0032_FFFFu32, 0x5B48_FFFF] {
        let cell_ids: Vec<u32> = (0x100..0x500)
            .map(|low| (dungeon & 0xFFFF_0000) | low)
            .collect();
        let world = support::load(&dats, &[dungeon], &cell_ids);
        let middles = cell_middles(&world.engine, dungeon);
        let Some(&(start_cell, start_middle)) = middles.first() else {
            println!("0x{:04X}: no cells", dungeon >> 16);
            continue;
        };
        let start = on_the_cell_floor(&world.engine, start_cell, start_middle, None);
        let (minimum, maximum) =
            geometry::measure_cells(&world.engine, dungeon).expect("the dungeon has cells");
        println!(
            "0x{:04X}: {} cells over {:.0} by {:.0} m, starting in 0x{start_cell:08X}",
            dungeon >> 16,
            middles.len(),
            maximum.x - minimum.x,
            maximum.y - minimum.y,
        );

        let goals = Floors(&world.engine);
        let mut body = Standing {
            position: start,
            cell_id: start_cell,
            body: world.body,
        };
        let mut controller = WalkController::new(PlanMode::Threads, None);
        let (mut asking, mut working, mut placing) = (Vec::new(), Vec::new(), Vec::new());
        let (mut routed, mut reused) = (0, 0);
        let mut lengths = Vec::new();
        for &(cell_id, middle) in &middles {
            let placed = Instant::now();
            let _ = on_the_cell_floor(&world.engine, cell_id, middle, None);
            placing.push(milliseconds(placed));

            let world_view = WalkWorld {
                physics: &world.engine,
                body: &mut body,
                goals: &goals,
                doors: None,
                paused_by: None,
                is_sealed_dungeon: Some(&sealed),
            };
            let asked = Instant::now();
            let preview = controller.preview_path(
                &world_view,
                PreviewTarget::Place {
                    cell_id,
                    local: middle,
                },
                2.5,
            );
            asking.push(milliseconds(asked));
            let waited = Instant::now();
            let plan: walk::NavPlan = preview.take();
            working.push(milliseconds(waited));
            if plan.status == PlanStatus::Routed {
                routed += 1;
                lengths.push((cell_id, plan.length_meters));
            }
            if plan.grid_reused {
                reused += 1;
            }
            let mut world_view = WalkWorld {
                physics: &world.engine,
                body: &mut body,
                goals: &goals,
                doors: None,
                paused_by: None,
                is_sealed_dungeon: Some(&sealed),
            };
            controller.tick(&mut world_view, 0.05);
        }
        let first_working = working.first().copied().unwrap_or(f64::NAN);
        println!(
            "  previews: {routed} of {} routed, {reused} on a grid already built",
            middles.len()
        );
        println!(
            "  asking (this thread): median {:.2} ms, worst {:.2} ms; placing the goal alone: median {:.2} ms, worst {:.2} ms",
            median(&mut asking.clone()),
            asking.iter().copied().fold(0.0, f64::max),
            median(&mut placing.clone()),
            placing.iter().copied().fold(0.0, f64::max),
        );
        println!(
            "  worker: first {first_working:.1} ms, median {:.1} ms, worst {:.1} ms",
            median(&mut working.clone()),
            working.iter().copied().fold(0.0, f64::max),
        );
    }
}

/// The first read of a dungeon's indoor cells and floorplan, which a
/// plugin's call does on the session thread, and the kept answer after.
#[test]
#[ignore = "needs the retail dat files and OPENAC_DIAGNOSTIC_TESTS=1"]
fn dungeon_reads_are_timed() {
    if std::env::var("OPENAC_DIAGNOSTIC_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_DIAGNOSTIC_TESTS=1 to run diagnostic tests");
        return;
    }
    let dats = support::dats();
    for landblock in [0x0032_0000u32, 0x5B48_0000, 0x0125_0000] {
        // What the read cost before it took in the doorways: the cells
        // alone, from files not yet read.
        let fresh = support::dats();
        let read = Instant::now();
        let info: ac_dat::objects::LandBlockInfo = fresh
            .get(landblock | 0xFFFE)
            .expect("read the landblock")
            .expect("the landblock");
        for offset in 0..info.num_cells {
            let _: Option<ac_dat::objects::EnvCell> = fresh
                .get(landblock | (0x0100 + offset))
                .expect("read a cell");
        }
        let cells_alone = milliseconds(read);

        let mut indoor = ac_nav::indoor::IndoorCellCache::new();
        let read = Instant::now();
        let cells = indoor.capture(&dats, landblock).expect("the cells read");
        let first = milliseconds(read);
        let again = Instant::now();
        let _ = indoor.capture(&dats, landblock).expect("the cells read");
        let kept = milliseconds(again);
        let doorways: usize = cells.iter().map(|c| c.doorways.len()).sum();
        let seen: usize = cells.iter().map(|c| c.visible_cells.len()).sum();

        let mut builder = ac_nav::floorplan::DungeonFloorplanBuilder::new();
        let read = Instant::now();
        let plan = builder.try_build(&dats, landblock).expect("the cells read");
        let plan_first = milliseconds(read);
        println!(
            "0x{:04X}: cells alone {cells_alone:.2} ms; indoor cells first {first:.2} ms, kept {kept:.3} ms ({} cells, {doorways} doorways, {seen} seen); floorplan first {plan_first:.2} ms ({} cells)",
            landblock >> 16,
            cells.len(),
            plan.map_or(0, |p| p.cells.len()),
        );
    }
}

/// A reach search over every cell of a few installed dungeons, timed on
/// the asking thread and the worker, then a preview to each cell it
/// reached: how a reach length compares with the preview's straightened
/// length to the same place, and which cells with a floor it missed.
#[test]
#[ignore = "needs the retail dat files and OPENAC_DIAGNOSTIC_TESTS=1"]
fn reach_searches_in_installed_dungeons_are_timed() {
    if std::env::var("OPENAC_DIAGNOSTIC_TESTS").as_deref() != Ok("1") {
        eprintln!("skipped: set OPENAC_DIAGNOSTIC_TESTS=1 to run diagnostic tests");
        return;
    }
    let dats = support::dats();
    let sealed = |cell: u32| ac_nav::dungeon::is_sealed_dungeon(&dats, cell);
    for dungeon in [0x0032_FFFFu32, 0x5B48_FFFF] {
        let cell_ids: Vec<u32> = (0x100..0x500)
            .map(|low| (dungeon & 0xFFFF_0000) | low)
            .collect();
        let world = support::load(&dats, &[dungeon], &cell_ids);
        let middles = cell_middles(&world.engine, dungeon);
        let (start_cell, start_middle) = middles[0];
        let start = on_the_cell_floor(&world.engine, start_cell, start_middle, None);
        let goals = Floors(&world.engine);
        let mut body = Standing {
            position: start,
            cell_id: start_cell,
            body: world.body,
        };
        let mut controller = WalkController::new(PlanMode::Threads, None);
        let mut times = Vec::new();
        let mut reach = None;
        for _ in 0..3 {
            let mut view = WalkWorld {
                physics: &world.engine,
                body: &mut body,
                goals: &goals,
                doors: None,
                paused_by: None,
                is_sealed_dungeon: Some(&sealed),
            };
            let asked = Instant::now();
            let search = controller
                .reachable_cells(&view, f32::INFINITY)
                .expect("a reach search");
            let asking = milliseconds(asked);
            let found = search.take();
            times.push((asking, found.took_ms, found.grid_reused));
            controller.tick(&mut view, 0.05);
            reach = Some(found);
        }
        let reach = reach.unwrap();
        println!(
            "0x{:04X}: {} of {} cells reached ({}); asking {:.2} ms, worker {:.1} ms building the grid, then {:.1} and {:.1} ms on it",
            dungeon >> 16,
            reach.cells.len(),
            middles.len(),
            reach.reason,
            times[0].0,
            times[0].1,
            times[1].1,
            times[2].1,
        );
        assert!(!times[0].2 && times[1].2 && times[2].2);

        let mut ratios = Vec::new();
        let mut unrouted = 0;
        for cell in &reach.cells {
            let view = WalkWorld {
                physics: &world.engine,
                body: &mut body,
                goals: &goals,
                doors: None,
                paused_by: None,
                is_sealed_dungeon: Some(&sealed),
            };
            let plan = controller
                .preview_path(
                    &view,
                    PreviewTarget::Place {
                        cell_id: cell.cell_id,
                        local: cell.place,
                    },
                    0.5,
                )
                .take();
            if plan.status == PlanStatus::Routed && plan.length_meters > 1.0 {
                ratios.push(f64::from(cell.route_meters / plan.length_meters));
            } else if plan.status != PlanStatus::Routed {
                unrouted += 1;
            }
        }
        ratios.sort_by(f64::total_cmp);
        let at = |share: f64| ratios[((ratios.len() - 1) as f64 * share) as usize];
        println!(
            "  reach length / preview length over {} cells: min {:.3}, median {:.3}, 90th {:.3}, max {:.3}; {unrouted} reached cells a preview did not route",
            ratios.len(),
            at(0.0),
            at(0.5),
            at(0.9),
            at(1.0),
        );
        let drops = reach.cells.iter().filter(|c| c.by_drop).count();
        let plan = ac_nav::floorplan::DungeonFloorplanBuilder::new()
            .try_build(&dats, dungeon)
            .unwrap()
            .unwrap();
        let reached: std::collections::HashSet<u32> =
            reach.cells.iter().map(|c| c.cell_id).collect();
        let missed: Vec<u32> = plan
            .cells
            .iter()
            .filter(|c| c.floor_z.is_some() && !reached.contains(&c.cell_id))
            .map(|c| c.cell_id)
            .collect();
        let floorless = plan.cells.iter().filter(|c| c.floor_z.is_none()).count();
        // A preview to the middle of each cell with a floor the reach
        // missed: one that routes is a cell whose floor the reach could
        // not find a node on.
        let mut routed_anyway = Vec::new();
        for &cell_id in missed.iter().take(40) {
            let Some(&(_, middle)) = middles.iter().find(|(id, _)| *id == cell_id) else {
                continue;
            };
            let view = WalkWorld {
                physics: &world.engine,
                body: &mut body,
                goals: &goals,
                doors: None,
                paused_by: None,
                is_sealed_dungeon: Some(&sealed),
            };
            let plan = controller
                .preview_path(
                    &view,
                    PreviewTarget::Place {
                        cell_id,
                        local: middle,
                    },
                    2.5,
                )
                .take();
            if plan.status == PlanStatus::Routed {
                let end = plan.path.last().map(|p| p.cell_id).unwrap_or(0);
                routed_anyway.push(format!("0x{cell_id:08X} (ends in 0x{end:08X})"));
            }
        }
        println!(
            "  {drops} reached by a drop; {floorless} with no floor of their own; {} with a floor not reached, of which a preview to the middle routes: {routed_anyway:?}",
            missed.len()
        );
    }
}
