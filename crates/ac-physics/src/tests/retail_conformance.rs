//! Cell membership against retail, ported from OpenAC's
//! `tests/AcDream.Core.Tests/Conformance/`: `FindCellListConformanceTests.cs`,
//! `RetailTraceTests.cs` (with the `RetailTrace` parser),
//! `CottageDoorwayCharacterizationTests.cs`, `DoorwayCellMembershipTests.cs`,
//! `ThresholdPortalCrossingReplayTests.cs` and
//! `SpawnCellRecoveryDiagnosticTests.cs`.
//!
//! `tests/fixtures/conformance/find-cell-list-threshold.log` is a retail
//! client trace (debugger breakpoint on the cell search): seed cell,
//! position and the cell retail picked. The dat-backed cases are ignored
//! without the retail dats. Diagnostic dumps are not ported.

use ac_dat::DatCollection;
use ac_dat::objects::{EnvCell, LandBlockInfo};
use glam::{Quat, Vec3};

use super::fixtures::fixture_path;
use super::retail_replays::{dats, load_env_cell};
use crate::cell_array::CellArray;
use crate::cell_transit::{
    CellCollector, find_cell_set, find_transit_cells_sphere, find_visible_child_cell,
};
use crate::dat::{quat, vec3};
use crate::engine::{PhysicsEngine, ResolveRequest};
use crate::geometry::{Plane, Sphere};
use crate::state::ObjectInfoState;
use crate::terrain::TerrainSurface;
use crate::world::{BldPortalInfo, BuildingPhysics, CollisionWorld, Placement};
use crate::{PhysicsBody, TransientState};

const HOLTBURG: u32 = 0xA9B4_0000;
const VESTIBULE_0170: u32 = 0xA9B4_0170;
const ROOM_0171: u32 = 0xA9B4_0171;
const INTERIOR_0170_LOCAL: Vec3 = Vec3::new(5.865, -8.449, 0.417);
const INTERIOR_0171_LOCAL: Vec3 = Vec3::new(6.55, -3.25, 4.60);

// ── RetailTrace ────────────────────────────────────────────────────────

/// One retail cell-search pick.
#[derive(Debug, Clone, Copy, PartialEq)]
struct RetailCellPick {
    seed: u32,
    position: Vec3,
    picked: u32,
}

/// Parses `[fcl] seed=0x.. px=.. py=.. pz=.. picked=0x..`; anything else is
/// not a pick.
fn parse_find_cell_list(line: &str) -> Option<RetailCellPick> {
    let rest = line.strip_prefix("[fcl]")?;
    let mut fields = rest.split_whitespace();
    let mut field = |name: &str| fields.next()?.strip_prefix(name).map(str::to_owned);
    let hex = |s: &str| u32::from_str_radix(s.strip_prefix("0x")?, 16).ok();
    let num = |s: &str| -> Option<f32> {
        let ok = !s.is_empty()
            && s.trim_start_matches('-')
                .split('.')
                .all(|part| !part.is_empty() && part.bytes().all(|b| b.is_ascii_digit()));
        ok.then(|| s.parse().ok()).flatten()
    };
    let seed = hex(&field("seed=")?)?;
    let px = num(&field("px=")?)?;
    let py = num(&field("py=")?)?;
    let pz = num(&field("pz=")?)?;
    let picked = hex(&field("picked=")?)?;
    if fields.next().is_some() {
        return None;
    }
    Some(RetailCellPick {
        seed,
        position: Vec3::new(px, py, pz),
        picked,
    })
}

fn parse_all<'a>(lines: impl IntoIterator<Item = &'a str>) -> Vec<RetailCellPick> {
    lines.into_iter().filter_map(parse_find_cell_list).collect()
}

fn threshold_picks() -> Vec<RetailCellPick> {
    let text = std::fs::read_to_string(fixture_path("conformance", "find-cell-list-threshold.log"))
        .expect("trace");
    parse_all(text.lines())
}

#[test]
fn parse_find_cell_list_line_yields_seed_pos_and_picked() {
    let p = parse_find_cell_list(
        "[fcl] seed=0xA9B40170 px=141.5000 py=7.2200 pz=92.7400 picked=0xA9B40171",
    )
    .unwrap();
    assert_eq!(p.seed, 0xA9B4_0170);
    assert_eq!(p.position, Vec3::new(141.5, 7.22, 92.74));
    assert_eq!(p.picked, 0xA9B4_0171);
}

#[test]
fn parse_negative_coordinates_and_lowercase_hex() {
    let p = parse_find_cell_list("[fcl] seed=0xa9b40031 px=-12.5 py=0 pz=-3.25 picked=0xa9b40170")
        .unwrap();
    assert_eq!(p.seed, 0xA9B4_0031);
    assert_eq!(p.position, Vec3::new(-12.5, 0.0, -3.25));
    assert_eq!(p.picked, 0xA9B4_0170);
}

#[test]
fn parse_non_matching_line_returns_none() {
    assert!(parse_find_cell_list("[BP4] find_collisions hit#10170 collide=0").is_none());
    assert!(parse_find_cell_list("").is_none());
    assert!(parse_find_cell_list("[fcl] seed=0xA9B40170 px=1 py=2").is_none());
}

#[test]
fn parse_file_skips_noise_and_yields_only_picks() {
    let picks = parse_all([
        "armed; walk now",
        "[fcl] seed=0xA9B40031 px=160.0 py=10.0 pz=94.0 picked=0xA9B40170",
        "[BP] noise",
        "[fcl] seed=0xA9B40170 px=158.0 py=12.0 pz=95.0 picked=0xA9B40171",
    ]);
    assert_eq!(picks.len(), 2);
    assert_eq!(picks[0].picked, 0xA9B4_0170);
    assert_eq!(picks[1].picked, 0xA9B4_0171);
}

// ── helpers ────────────────────────────────────────────────────────────

fn world_with(
    d: &DatCollection,
    landblock: u32,
    lows: std::ops::RangeInclusive<u32>,
) -> CollisionWorld {
    let mut world = CollisionWorld::new();
    for low in lows {
        load_env_cell(d, &mut world, landblock | low);
    }
    world
}

fn pick(world: &CollisionWorld, center: Vec3, radius: f32, seed: u32) -> u32 {
    let mut cells = CellArray::new();
    find_cell_set(world, center, radius, seed, &mut cells, None)
}

fn point_in_cell(world: &CollisionWorld, id: u32, p: Vec3) -> bool {
    world.cell(id).is_some_and(|c| {
        c.has_containment() && c.containment_bsp.point_inside(c.placement.to_local(p))
    })
}

fn register_buildings(d: &DatCollection, world: &mut CollisionWorld, landblock: u32) {
    let info: LandBlockInfo = d.get(landblock | 0xFFFE).unwrap().expect("landblock info");
    for b in &info.buildings {
        if b.portals.is_empty() {
            continue;
        }
        let origin = vec3(b.frame.origin);
        let low = (origin.x / 24.0) as u32 * 8 + (origin.y / 24.0) as u32 + 1;
        world.cache_building(
            landblock | low,
            BuildingPhysics {
                placement: Placement::new(quat(b.frame.orientation), origin),
                portals: b
                    .portals
                    .iter()
                    .map(|p| BldPortalInfo {
                        other_cell_id: landblock | u32::from(p.other_cell_id),
                        other_portal_id: p.other_portal_id as i16,
                        flags: p.flags,
                    })
                    .collect(),
                model_id: 0,
            },
        );
    }
}

// ── CottageDoorwayCharacterizationTests ────────────────────────────────

#[test]
#[ignore = "needs the retail dat files"]
fn doorway_topology_is_pinned() {
    let d = dats();
    let world = world_with(&d, HOLTBURG, 0x0170..=0x0171);
    let v = world.cell(VESTIBULE_0170).unwrap();
    let r = world.cell(ROOM_0171).unwrap();
    assert!(v.has_containment());
    assert!(r.has_containment());
    assert!(v.seen_outside);
    assert!(r.seen_outside);
    assert!(v.portals.iter().any(|p| p.other_cell_id == 0xFFFF));
    assert!(v.portals.iter().any(|p| p.other_cell_id == 0x0171));
    assert!(r.portals.iter().any(|p| p.other_cell_id == 0x0170));
    assert!(!r.portals.iter().any(|p| p.other_cell_id == 0xFFFF));
}

#[test]
#[ignore = "needs the retail dat files"]
fn doorway_interior_points_are_pinned() {
    let d = dats();
    let world = world_with(&d, HOLTBURG, 0x0170..=0x0171);
    let v_world = world
        .cell(VESTIBULE_0170)
        .unwrap()
        .placement
        .to_world(INTERIOR_0170_LOCAL);
    let r_world = world
        .cell(ROOM_0171)
        .unwrap()
        .placement
        .to_world(INTERIOR_0171_LOCAL);
    assert!(point_in_cell(&world, VESTIBULE_0170, v_world));
    assert!(point_in_cell(&world, ROOM_0171, r_world));
    assert!(!point_in_cell(&world, VESTIBULE_0170, r_world));
    assert!(!point_in_cell(&world, ROOM_0171, Vec3::splat(10_000.0)));
}

// ── FindCellListConformanceTests ───────────────────────────────────────

const FIND_RADIUS: f32 = 0.4;

fn threshold_building(d: &DatCollection) -> CollisionWorld {
    world_with(d, HOLTBURG, 0x016F..=0x0175)
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_cell_list_deep_inside_room_0171_returns_0171() {
    let world = threshold_building(&dats());
    let at = world
        .cell(ROOM_0171)
        .unwrap()
        .placement
        .to_world(INTERIOR_0171_LOCAL);
    assert_eq!(pick(&world, at, FIND_RADIUS, ROOM_0171), ROOM_0171);
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_cell_list_deep_inside_vestibule_0170_returns_0170() {
    let world = threshold_building(&dats());
    let at = world
        .cell(VESTIBULE_0170)
        .unwrap()
        .placement
        .to_world(INTERIOR_0170_LOCAL);
    assert_eq!(
        pick(&world, at, FIND_RADIUS, VESTIBULE_0170),
        VESTIBULE_0170
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_cell_list_in_vestibule_seeded_from_room_returns_0170() {
    let world = threshold_building(&dats());
    let at = world
        .cell(VESTIBULE_0170)
        .unwrap()
        .placement
        .to_world(INTERIOR_0170_LOCAL);
    assert_eq!(pick(&world, at, FIND_RADIUS, ROOM_0171), VESTIBULE_0170);
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_cell_list_doorway_threshold_indoor_picks_match_retail() {
    let world = threshold_building(&dats());
    let indoor: Vec<RetailCellPick> = threshold_picks()
        .into_iter()
        .filter(|p| p.seed & 0xFFFF >= 0x100 && p.picked & 0xFFFF >= 0x100)
        .collect();
    assert!(!indoor.is_empty());
    for p in indoor {
        assert_eq!(
            pick(&world, p.position, FIND_RADIUS, p.seed),
            p.picked,
            "{p:?}"
        );
    }
}

// ── DoorwayCellMembershipTests ─────────────────────────────────────────

const A9B3: u32 = 0xA9B3_0000;
const FOOT: f32 = 0.48;

fn interiors_with_terrain(d: &DatCollection, landblock: u32) -> CollisionWorld {
    let mut world = world_with(d, landblock, 0x0100..=0x01FF);
    world.register_terrain(landblock, Vec3::ZERO);
    world
}

#[test]
#[ignore = "needs the retail dat files"]
fn a9b3_cottage_gap_at_doorway_straddles_exit_plane_demotes() {
    let world = interiors_with_terrain(&dats(), A9B3);
    assert_eq!(
        pick(
            &world,
            Vec3::new(184.915, 82.464, 116.48),
            FOOT,
            A9B3 | 0x104
        ),
        A9B3 | 0x3C
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a9b3_cottage_gap_beyond_straddle_distance_keeps_current_cell() {
    let world = interiors_with_terrain(&dats(), A9B3);
    assert_eq!(
        pick(
            &world,
            Vec3::new(185.345, 82.464, 116.48),
            FOOT,
            A9B3 | 0x104
        ),
        A9B3 | 0x104
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn find_transit_cells_sphere_exit_portal_straddle_gate_matches_retail() {
    let world = interiors_with_terrain(&dats(), A9B3);
    let cell = world.cell(A9B3 | 0x102).unwrap();
    let straddles = |center: Vec3| {
        let mut cells = CellArray::new();
        find_transit_cells_sphere(
            &world,
            cell,
            A9B3 | 0x102,
            &[Sphere::new(center, FOOT)],
            1,
            &mut CellCollector::new(&mut cells),
        )
    };
    assert!(!straddles(Vec3::new(183.0, 86.5, 117.0)));
    assert!(straddles(Vec3::new(185.70, 85.5, 117.0)));
}

#[test]
#[ignore = "needs the retail dat files"]
fn a9b3_cottage_outdoor_seed_tick_skipped_threshold_recovers_via_growing_walk() {
    let d = dats();
    let mut world = interiors_with_terrain(&d, A9B3);
    register_buildings(&d, &mut world, A9B3);
    assert_eq!(
        pick(&world, Vec3::new(184.30, 82.20, 116.48), FOOT, A9B3 | 0x3C),
        A9B3 | 0x100
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn a9b3_cottage_run_speed_entry_never_strands_outdoor() {
    let d = dats();
    let mut world = interiors_with_terrain(&d, A9B3);
    register_buildings(&d, &mut world, A9B3);
    let mut phase = 0.0f32;
    while phase < 0.16 {
        let mut curr = A9B3 | 0x3C;
        let mut x = 191.5 - phase;
        while x > 181.0 {
            let t = (191.5 - x) / 10.5;
            curr = pick(&world, Vec3::new(x, 82.7 - t * 0.7, 116.48), FOOT, curr);
            x -= 0.16;
        }
        assert!(
            curr & 0xFFFF >= 0x0100,
            "phase {phase}: stranded outdoor in {curr:#010X}"
        );
        phase += 0.04;
    }
}

#[test]
#[ignore = "needs the retail dat files"]
fn a9b3_cottage_outdoor_seed_at_threshold_gap_keeps_outdoor() {
    let d = dats();
    let mut world = interiors_with_terrain(&d, A9B3);
    register_buildings(&d, &mut world, A9B3);
    assert_eq!(
        pick(
            &world,
            Vec3::new(184.915, 82.464, 116.48),
            FOOT,
            A9B3 | 0x3C
        ),
        A9B3 | 0x3C
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn threshold_cottage_adjacent_claim_laterally_recovers_via_stab_graph() {
    let world = interiors_with_terrain(&dats(), HOLTBURG);
    assert_eq!(
        pick(
            &world,
            Vec3::new(155.390, 11.020, 94.0 + FOOT),
            FOOT,
            0xA9B4_0172
        ),
        ROOM_0171
    );
}

// ── ThresholdPortalCrossingReplayTests ─────────────────────────────────

#[test]
#[ignore = "needs the retail dat files"]
fn production_path_indoor_crossings_match_retail() {
    const FLOOR_Z: f32 = 94.005;
    let world = threshold_building(&dats());
    let mut engine = PhysicsEngine::new();
    engine.attach_world(world);
    engine.add_landblock(
        HOLTBURG,
        TerrainSurface::new(&[0u8; 81], &[-1000.0; 256], 0, 0, None),
        Vec::new(),
        Vec::new(),
        0.0,
        0.0,
    );
    let picks = threshold_picks();
    let (mut total, mut failures) = (0, Vec::new());
    for pair in picks.windows(2) {
        let (from, to) = (pair[0].picked, pair[1].picked);
        if from & 0xFFFF < 0x100 || to & 0xFFFF < 0x100 {
            continue;
        }
        let floor = Plane::new(Vec3::Z, -FLOOR_Z);
        let mut body = PhysicsBody::new();
        body.set_position(pair[0].position);
        body.orientation = Quat::IDENTITY;
        body.contact_plane_valid = true;
        body.contact_plane = floor;
        body.contact_plane_cell_id = from;
        body.walkable_polygon_valid = true;
        body.walkable_plane = floor;
        body.walkable_vertices = Some(vec![
            Vec3::new(150.0, 5.0, FLOOR_Z),
            Vec3::new(150.0, 20.0, FLOOR_Z),
            Vec3::new(165.0, 20.0, FLOOR_Z),
            Vec3::new(165.0, 5.0, FLOOR_Z),
        ]);
        body.walkable_up = Vec3::Z;
        body.transient_state = TransientState::CONTACT | TransientState::ON_WALKABLE;
        let r = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: pair[0].position,
                target_pos: pair[1].position,
                cell_id: from,
                sphere_radius: 0.4,
                sphere_height: 1.2,
                step_up_height: 0.4,
                step_down_height: 0.1,
                is_on_ground: true,
                mover_flags: ObjectInfoState(
                    ObjectInfoState::IS_PLAYER | ObjectInfoState::EDGE_SLIDE,
                ),
                ..ResolveRequest::default()
            },
            Some(&mut body),
        );
        total += 1;
        if r.cell_id != to {
            failures.push(format!(
                "{:#06X}->{:#06X}: got {:#06X}",
                from & 0xFFFF,
                to & 0xFFFF,
                r.cell_id & 0xFFFF
            ));
        }
    }
    assert!(total > 0);
    assert!(
        failures.is_empty(),
        "{} of {total} diverged: {}",
        failures.len(),
        failures.join("; ")
    );
}

// ── SpawnCellRecoveryDiagnosticTests ───────────────────────────────────

const POISONED: u32 = 0xA9B4_0162;
const SPAWN: Vec3 = Vec3::new(156.533_14, 11.775_104, 96.5);

#[test]
#[ignore = "needs the retail dat files"]
fn spawn_position_is_inside_0171_not_0162_and_pick_recovers_from_good_seed() {
    let world = world_with(&dats(), HOLTBURG, 0x0100..=0x01FF);
    let foot = SPAWN + Vec3::new(0.0, 0.0, FOOT);
    let containing: Vec<u32> = (0x0100..=0x01FF)
        .map(|low| HOLTBURG | low)
        .filter(|id| point_in_cell(&world, *id, foot))
        .collect();
    assert!(containing.contains(&ROOM_0171));
    assert!(!containing.contains(&POISONED));
    assert_eq!(pick(&world, foot, FOOT, ROOM_0171), ROOM_0171);
    assert_eq!(pick(&world, foot, FOOT, POISONED), POISONED);
}

#[test]
#[ignore = "needs the retail dat files"]
fn seen_outside_is_populated_for_ground_floor_interiors() {
    let d = dats();
    let mut world = CollisionWorld::new();
    assert!(load_env_cell(&d, &mut world, ROOM_0171));
    assert!(load_env_cell(&d, &mut world, POISONED));
    assert!(world.cell(ROOM_0171).unwrap().seen_outside);
    assert!(world.cell(POISONED).is_some());
}

#[test]
#[ignore = "needs the retail dat files"]
fn adjacent_room_claim_corrects_to_containing_room_via_stab_list() {
    let world = world_with(&dats(), HOLTBURG, 0x0100..=0x01FF);
    let foot = Vec3::new(155.390, 11.020, 94.0 + FOOT);
    assert_eq!(
        find_visible_child_cell(&world, 0xA9B4_0172, foot, true, None),
        ROOM_0171
    );
}

#[test]
#[ignore = "needs the retail dat files"]
fn poisoned_claim_is_a_different_building_far_away() {
    let d = dats();
    let claim: EnvCell = d.get(POISONED).unwrap().unwrap();
    let actual: EnvCell = d.get(ROOM_0171).unwrap().unwrap();
    let dist = vec3(claim.position.origin).distance(vec3(actual.position.origin));
    assert!(dist > 20.0, "distance {dist}");
}
