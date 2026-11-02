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
