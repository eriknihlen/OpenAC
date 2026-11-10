//! Projectile paths. Cases from OpenAC
//! `tests/AcDream.HostParity.Tests/Scenarios/ProjectilePathParityTests.cs`
//! (the flight through the session's collision world: a creature in the
//! open is clear, one under the ground is blocked by it, an unknown id is
//! an invalid target) and `RuntimeAutomationSurface.EvaluateProjectile
//! PathRequest`'s guards. The flights need the retail dats: set
//! `OPENAC_DAT_DIR` or install them under `Documents\Asheron's Call`; run
//! with `cargo test -- --include-ignored`.

use ac_physics::content::publish_landblock;
use ac_physics::{CollisionWorld, PhysicsEngine};
use ac_plugin::abi::calls;
use ac_plugin::abi::combat::AttackHeight;
use ac_plugin::abi::projectile::{ProjectilePathKind, ProjectilePathQuery, ProjectilePathStatus};
use glam::Vec3;

use super::projectile::{Flight, ballistic_launch, evaluate};
use super::tests::{call, in_world, with_binding};
use crate::session::Outbox;

/// OpenAC's parity query: 0.4 m probe, 0.7 m steps, 500 checks.
fn query(target_id: u32, kind: ProjectilePathKind) -> ProjectilePathQuery {
    ProjectilePathQuery {
        target_id,
        kind,
        target_height: AttackHeight::Medium,
        projectile_radius: 0.4,
        step_distance: 0.7,
        maximum_collision_checks: 500,
        diagnostics: false,
        launch_speed: None,
    }
}

// OpenAC `EvaluateProjectilePathRequest`: a zero id, a radius or step that
// is not a positive finite number, or no budget is an invalid target; an
// object the client does not know is too; with no character placed there
// is nothing to shoot from.
#[test]
fn unusable_queries_and_unknown_targets_are_invalid() {
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        assert!(call::<calls::ProjectilesAvailable>(b, &()));
        let ask = |b: &mut _, q: ProjectilePathQuery| call::<calls::ProjectilesEvaluatePath>(b, &q);
        let invalid = |q: ProjectilePathQuery| q;
        for q in [
            query(0, ProjectilePathKind::Straight),
            ProjectilePathQuery {
                projectile_radius: 0.0,
                ..query(0x5000_FFFF, ProjectilePathKind::Straight)
            },
            ProjectilePathQuery {
                step_distance: f32::NAN,
                ..query(0x5000_FFFF, ProjectilePathKind::Straight)
            },
            ProjectilePathQuery {
                maximum_collision_checks: 0,
                ..query(0x5000_FFFF, ProjectilePathKind::Straight)
            },
            query(0x5000_FFFF, ProjectilePathKind::Straight),
        ] {
            assert_eq!(
                ask(b, invalid(q)).status,
                ProjectilePathStatus::InvalidTarget
            );
        }
    });
    // Out of the world: unavailable.
    let mut state = in_world();
    let mut outbox = Outbox::new();
    with_binding(&mut state, &mut outbox, |b| {
        b.facts.in_world = false;
        assert!(!call::<calls::ProjectilesAvailable>(b, &()));
        assert_eq!(
            call::<calls::ProjectilesEvaluatePath>(b, &query(1, ProjectilePathKind::Straight))
                .status,
            ProjectilePathStatus::Unavailable
        );
    });
}

fn holtburg() -> PhysicsEngine {
    let dir = std::env::var_os("OPENAC_DAT_DIR").map(std::path::PathBuf::from);
    let content = crate::movement::WorldContent::open(dir.as_deref()).expect("the retail dats");
    let mut engine = PhysicsEngine::new();
    engine.attach_world(CollisionWorld::new());
    publish_landblock(
        &mut engine,
        content.dats(),
        content.height_table(),
        0xA9B4_FFFF,
        Vec3::ZERO,
    )
    .unwrap();
    engine
}

/// Flat open ground in Holtburg (the movement tests' spot).
const SPOT: (f32, f32) = (60.0, 150.0);

fn outdoor_cell(x: f32, y: f32) -> u32 {
    0xA9B4_0000 | (1 + (x / 24.0) as u32 * 8 + (y / 24.0) as u32)
}

#[test]
#[ignore = "needs the retail dat files"]
fn a_shot_in_the_open_is_clear_and_one_under_the_ground_is_blocked() {
    let engine = holtburg();
    let (x, y) = SPOT;
    let ground = engine.sample_terrain_z(x, y).unwrap();
    let shooter = Vec3::new(x, y, ground);
    let flight = |target: Vec3| Flight {
        shooter_id: 1,
        shooter,
        cell_id: outdoor_cell(x, y),
        target_id: 2,
        target,
    };
    let near = flight(Vec3::new(
        x + 12.0,
        y,
        engine.sample_terrain_z(x + 12.0, y).unwrap(),
    ));
    for kind in [
        ProjectilePathKind::Straight,
        ProjectilePathKind::Arc,
        ProjectilePathKind::Missile,
    ] {
        let answer = evaluate(&engine, &near, &query(2, kind), None);
        assert_ne!(answer.status, ProjectilePathStatus::Unavailable, "{kind:?}");
        assert!(answer.collision_checks > 0, "{kind:?}");
    }
    let straight = evaluate(
        &engine,
        &near,
        &query(2, ProjectilePathKind::Straight),
        None,
    );
    assert_eq!(straight.status, ProjectilePathStatus::Clear);
    // 12 m in 0.7 m steps: the flight ends on its last step.
    assert!(
        (15..=20).contains(&straight.collision_checks),
        "{straight:?}"
    );
    // The ground is in the way of a creature 6 m under it.
    let buried = flight(Vec3::new(x + 12.0, y, ground - 6.0));
    let blocked = evaluate(
        &engine,
        &buried,
        &query(2, ProjectilePathKind::Straight),
        None,
    );
    assert_eq!(blocked.status, ProjectilePathStatus::Blocked, "{blocked:?}");
    assert_eq!(blocked.blocking_object_id, 0, "the landscape stopped it");
    // A budget smaller than the flight runs out; diagnostics keep a marker
    // per step.
    let mut samples = Vec::new();
    let short = evaluate(
        &engine,
        &near,
        &ProjectilePathQuery {
            maximum_collision_checks: 3,
            ..query(2, ProjectilePathKind::Straight)
        },
        Some(&mut samples),
    );
    assert_eq!(short.status, ProjectilePathStatus::BudgetExceeded);
    assert_eq!(short.collision_checks, 3);
    assert_eq!(samples.len(), 3);
    assert!(samples.iter().all(|(_, clear)| *clear));
}

// ── A launch speed (API 1.12): a missile or arc flies at that total speed
// on the lower arc that reaches the aim point, as a server launches one.

fn launched_at(speed: f32, target_id: u32, kind: ProjectilePathKind) -> ProjectilePathQuery {
    ProjectilePathQuery {
        launch_speed: Some(speed),
        ..query(target_id, kind)
    }
}

/// A flight over nothing: the shot starts 1.2 m up and aims 0.9 m up, so
/// the aim point is `across` metres out (less the 0.61 m a missile starts
/// ahead) and 0.3 m lower.
fn over_nothing(across: f32) -> Flight {
    Flight {
        shooter_id: 1,
        shooter: Vec3::ZERO,
        cell_id: 0,
        target_id: 2,
        target: Vec3::new(across, 0.0, 0.0),
    }
}

// A target no arc reaches at the launch speed is an invalid target, before
// any collision check: at 20 m/s the farthest reach on level ground is
// 20² / 9.8, about 40.8 m. A speed that is not a finite number above 0 is
// unusable.
#[test]
fn a_target_out_of_reach_at_the_launch_speed_is_invalid() {
    let engine = PhysicsEngine::new();
    for kind in [ProjectilePathKind::Missile, ProjectilePathKind::Arc] {
        let far = evaluate(
            &engine,
            &over_nothing(45.0),
            &launched_at(20.0, 2, kind),
            None,
        );
        assert_eq!(far.status, ProjectilePathStatus::InvalidTarget, "{kind:?}");
        assert_eq!(far.collision_checks, 0);
        assert_eq!(
            far.notice.as_deref(),
            Some("The target is out of reach at 20.0 m/s."),
            "{kind:?}"
        );
        for unusable in [0.0, -5.0, f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            let answer = evaluate(
                &engine,
                &over_nothing(10.0),
                &launched_at(unusable, 2, kind),
                None,
            );
            assert_eq!(answer.status, ProjectilePathStatus::InvalidTarget);
            assert_eq!(
                answer.notice.as_deref(),
                Some("The launch speed must be a finite number above 0 m/s.")
            );
        }
    }
    // At most 50 m/s: 300 m is beyond 50² / 9.8 (255 m) however fast the
    // query says.
    let clamped = evaluate(
        &engine,
        &over_nothing(300.0),
        &launched_at(400.0, 2, ProjectilePathKind::Missile),
        None,
    );
    assert_eq!(
        clamped.notice.as_deref(),
        Some("The target is out of reach at 50.0 m/s.")
    );
    // A straight shot has no launch speed: it is not out of reach, and
    // flies as before (here into a world with nothing loaded).
    let straight = evaluate(
        &engine,
        &over_nothing(45.0),
        &launched_at(20.0, 2, ProjectilePathKind::Straight),
        None,
    );
    let before = evaluate(
        &engine,
        &over_nothing(45.0),
        &query(2, ProjectilePathKind::Straight),
        None,
    );
    assert_eq!(straight, before);
    assert_ne!(straight.status, ProjectilePathStatus::InvalidTarget);
}

/// Something overhead, `id`: a sphere of `radius` whose lowest point is
/// `clearance` metres above the ground at (`x`, `y`).
fn overhead(engine: &mut PhysicsEngine, id: u32, x: f32, y: f32, clearance: f32, radius: f32) {
    use ac_physics::{
        EntityCollisionFlags, Quat, RegistrationSite, ShadowCollisionType, SingleShape,
    };
    let ground = engine.sample_terrain_z(x, y).unwrap();
    let cell = outdoor_cell(x, y);
    let (shadows, world) = engine.shadows_and_world();
    shadows
        .register(
            world,
            id,
            &SingleShape {
                gfx_obj_id: 0,
                radius,
                collision_type: ShadowCollisionType::Sphere,
                cyl_height: 0.0,
                scale: 1.0,
            },
            RegistrationSite {
                world_pos: Vec3::new(x, y, ground + clearance + radius),
                rotation: Quat::IDENTITY,
                world_offset_x: 0.0,
                world_offset_y: 0.0,
                landblock_id: (cell & 0xFFFF_0000) | 0xFFFF,
                seed_cell_id: cell,
            },
            0,
            EntityCollisionFlags(0),
            false,
            None,
        )
        .expect("register");
}

// A launcher's arrow at 20 m/s climbs about 5 m on its way to a creature
// 35 m out, where the probe at 46 m/s across stays under 2 m: with
// something 3.2 m over the middle of the flight, the slower arc hits it and
// the default one passes under.
#[test]
#[ignore = "needs the retail dat files"]
fn a_slower_launch_lobs_into_a_low_ceiling_the_default_probe_passes_under() {
    const CEILING: u32 = 0x8000_7101;
    let mut engine = holtburg();
    let (x, y) = SPOT;
    let ground = engine.sample_terrain_z(x, y).unwrap();
    let across = 35.0;
    let target = Vec3::new(
        x + across,
        y,
        engine.sample_terrain_z(x + across, y).unwrap(),
    );
    let flight = Flight {
        shooter_id: 1,
        shooter: Vec3::new(x, y, ground),
        cell_id: outdoor_cell(x, y),
        target_id: 2,
        target,
    };
    // Nothing overhead yet: both clear, and the launch is in the notice.
    let default = evaluate(
        &engine,
        &flight,
        &query(2, ProjectilePathKind::Missile),
        None,
    );
    assert_eq!(default.status, ProjectilePathStatus::Clear, "{default:?}");
    assert_eq!(default.notice, None);
    let slow = evaluate(
        &engine,
        &flight,
        &launched_at(20.0, 2, ProjectilePathKind::Missile),
        None,
    );
    assert_eq!(slow.status, ProjectilePathStatus::Clear, "{slow:?}");
    let notice = slow.notice.as_deref().unwrap();
    assert!(notice.starts_with("Launched at 20.0 m/s, "), "{notice}");
    // The low arc to a point 34.4 m out and 0.3 m lower at 20 m/s leaves
    // about 28 degrees up.
    let degrees: f32 = notice["Launched at 20.0 m/s, ".len()..]
        .trim_end_matches("° up.")
        .parse()
        .unwrap();
    assert!((27.0..30.0).contains(&degrees), "{notice}");
    // A slower flight takes more steps: 0.7 m of its longer, slower path
    // each.
    assert!(slow.collision_checks > default.collision_checks);

    overhead(&mut engine, CEILING, x + across / 2.0, y, 3.2, 6.0);
    let default = evaluate(
        &engine,
        &flight,
        &query(2, ProjectilePathKind::Missile),
        None,
    );
    assert_eq!(default.status, ProjectilePathStatus::Clear, "{default:?}");
    let mut samples = Vec::new();
    let slow = evaluate(
        &engine,
        &flight,
        &launched_at(20.0, 2, ProjectilePathKind::Missile),
        Some(&mut samples),
    );
    assert_eq!(slow.status, ProjectilePathStatus::Blocked, "{slow:?}");
    assert_eq!(slow.blocking_object_id, CEILING);
    assert!(
        slow.notice
            .as_deref()
            .unwrap()
            .starts_with("Launched at 20.0 m/s, ")
    );
    // It went up past the ceiling's lowest point before it hit.
    let highest = samples.iter().map(|(at, _)| at.z).fold(f32::MIN, f32::max);
    assert!(highest > ground + 3.2, "{highest} over {ground}");
    // Fast enough, the arc stays under it again.
    let fast = evaluate(
        &engine,
        &flight,
        &launched_at(45.0, 2, ProjectilePathKind::Missile),
        None,
    );
    assert_eq!(fast.status, ProjectilePathStatus::Clear, "{fast:?}");
}

/// Where a launch of (`across`, `up`) metres per second is after `t`
/// seconds under 9.8 m/s² gravity: across and up from the start.
fn after(across: f32, up: f32, t: f32) -> (f32, f32) {
    (across * t, up * t - 4.9 * t * t)
}

// The launch keeps the total speed asked for, takes the lower of the two
// arcs (below 45 degrees on level ground, the other being its complement),
// and lands on the aim point whether it is level, above or below.
#[test]
fn a_launch_at_a_speed_is_the_lower_arc_that_lands_on_the_aim_point() {
    for (speed, across, rise) in [
        (20.0f32, 30.0f32, 0.0f32),
        (20.0, 30.0, 3.0),
        (20.0, 30.0, -3.0),
        (20.0, 10.0, 8.0),
        (50.0, 5.0, -3.0),
        (50.0, 250.0, 0.0),
        (28.0, 35.0, -0.3),
    ] {
        let (h, v, angle) = ballistic_launch(speed, across, rise).unwrap();
        let case = format!("{speed} m/s, {across} m across, {rise} m up");
        assert!((h.hypot(v) - speed).abs() < 1e-3 * speed, "{case}");
        assert!(h > 0.0, "{case}");
        assert!((angle.tan() - v / h).abs() < 1e-4, "{case}");
        let (x, z) = after(h, v, across / h);
        assert!((x - across).abs() < 1e-3, "{case}");
        assert!(
            (z - rise).abs() < 1e-2 * across.max(1.0),
            "{case}: lands {z}"
        );
        // The other arc that lands there leaves steeper.
        let high = (speed * speed
            + (speed.powi(4) - 9.8 * (9.8 * across * across + 2.0 * rise * speed * speed)).sqrt())
        .atan2(9.8 * across);
        assert!(angle < high, "{case}");
    }
    // Level ground: the two arcs are complements, the lower below 45°.
    let (_, _, low) = ballistic_launch(20.0, 30.0, 0.0).unwrap();
    let expected = 0.5 * (9.8f32 * 30.0 / 400.0).asin();
    assert!(
        (low - expected).abs() < 1e-4,
        "{} vs {}",
        low.to_degrees(),
        expected.to_degrees()
    );
    // Downhill and close at speed, it leaves below level.
    let (_, _, down) = ballistic_launch(50.0, 5.0, -3.0).unwrap();
    assert!(down < 0.0);
    // The edge of reach on level ground, 20² / 9.8 m: just inside, one arc
    // at about 45°; just outside, none.
    let edge = 400.0 / 9.8;
    let (_, _, steepest) = ballistic_launch(20.0, edge * 0.9999, 0.0).unwrap();
    assert!(
        (steepest.to_degrees() - 45.0).abs() < 1.5,
        "{}",
        steepest.to_degrees()
    );
    assert_eq!(ballistic_launch(20.0, edge * 1.0001, 0.0), None);
    // At 50 m/s the reach is 255 m.
    assert!(ballistic_launch(50.0, 254.0, 0.0).is_some());
    assert_eq!(ballistic_launch(50.0, 256.0, 0.0), None);
    // Straight up: reachable up to v² / 2g (20.4 m at 20 m/s), at 90°;
    // straight down at -90°.
    let (_, up, angle) = ballistic_launch(20.0, 0.0, 10.0).unwrap();
    assert!((angle.to_degrees() - 90.0).abs() < 1e-3);
    assert!((up - 20.0).abs() < 1e-3);
    assert_eq!(ballistic_launch(20.0, 0.0, 21.0), None);
    let (_, _, angle) = ballistic_launch(20.0, 0.0, -10.0).unwrap();
    assert!((angle.to_degrees() + 90.0).abs() < 1e-3);
}

// An aim point straight above or below the start has nothing to fly
// through: clear, as before, without a speed. With one, the speed must
// still be usable and reach that high, and the notice says the launch.
#[test]
fn an_aim_point_straight_above_still_needs_a_usable_speed_that_reaches() {
    let engine = PhysicsEngine::new();
    // A missile starts 0.61 m ahead, so a target 0.61 m out is straight
    // above or below it.
    let above = |height: f32| Flight {
        target: Vec3::new(0.61, 0.0, height),
        ..over_nothing(0.61)
    };
    let missile = ProjectilePathKind::Missile;
    let before = evaluate(&engine, &above(5.0), &query(2, missile), None);
    assert_eq!(before.status, ProjectilePathStatus::Clear);
    assert_eq!((before.collision_checks, before.notice), (0, None));
    let reached = evaluate(&engine, &above(5.0), &launched_at(20.0, 2, missile), None);
    assert_eq!(reached.status, ProjectilePathStatus::Clear);
    assert_eq!(
        reached.notice.as_deref(),
        Some("Launched at 20.0 m/s, 90.0° up.")
    );
    let too_high = evaluate(&engine, &above(30.0), &launched_at(20.0, 2, missile), None);
    assert_eq!(too_high.status, ProjectilePathStatus::InvalidTarget);
    assert_eq!(
        too_high.notice.as_deref(),
        Some("The target is out of reach at 20.0 m/s.")
    );
    let unusable = evaluate(
        &engine,
        &above(5.0),
        &launched_at(f32::NAN, 2, missile),
        None,
    );
    assert_eq!(unusable.status, ProjectilePathStatus::InvalidTarget);
    // A straight shot ignores the speed here too.
    let straight = ProjectilePathKind::Straight;
    let probe = Flight {
        target: Vec3::new(0.66, 0.0, 30.0),
        ..over_nothing(0.66)
    };
    assert_eq!(
        evaluate(&engine, &probe, &launched_at(f32::NAN, 2, straight), None),
        evaluate(&engine, &probe, &query(2, straight), None)
    );
}

// Without a launch speed every kind flies exactly as before API 1.12:
// these answers were recorded from the flight as it was, over open ground
// 12 and 35 m out (the step count, its highest point over the shooter's
// ground and where the middle step reached).
#[test]
#[ignore = "needs the retail dat files"]
fn without_a_launch_speed_every_kind_flies_as_before() {
    let engine = holtburg();
    let (x, y) = SPOT;
    let ground = engine.sample_terrain_z(x, y).unwrap();
    let recorded = [
        (
            12.0,
            ProjectilePathKind::Straight,
            17,
            1.1815,
            (6.9578, 1.0334),
        ),
        (12.0, ProjectilePathKind::Arc, 17, 1.7737, (6.7293, 1.4411)),
        (
            12.0,
            ProjectilePathKind::Missile,
            17,
            1.2000,
            (6.9093, 1.1186),
        ),
        (
            35.0,
            ProjectilePathKind::Straight,
            50,
            1.1939,
            (18.8595, 1.0409),
        ),
        (35.0, ProjectilePathKind::Arc, 50, 2.4715, (18.6178, 2.4075)),
        (
            35.0,
            ProjectilePathKind::Missile,
            50,
            1.7678,
            (18.7962, 1.7533),
        ),
    ];
    for (across, kind, checks, peak, (mid_x, mid_z)) in recorded {
        let flight = Flight {
            shooter_id: 1,
            shooter: Vec3::new(x, y, ground),
            cell_id: outdoor_cell(x, y),
            target_id: 2,
            target: Vec3::new(
                x + across,
                y,
                engine.sample_terrain_z(x + across, y).unwrap(),
            ),
        };
        let mut samples = Vec::new();
        let answer = evaluate(&engine, &flight, &query(2, kind), Some(&mut samples));
        let case = format!("{kind:?} at {across} m");
        assert_eq!(answer.status, ProjectilePathStatus::Clear, "{case}");
        assert_eq!(answer.collision_checks, checks, "{case}");
        assert_eq!(answer.notice, None, "{case}");
        assert_eq!(samples.len(), checks as usize, "{case}");
        let highest = samples
            .iter()
            .map(|(at, _)| at.z - ground)
            .fold(f32::MIN, f32::max);
        assert!((highest - peak).abs() < 1e-3, "{case}: peak {highest}");
        let mid = samples[samples.len() / 2].0 - flight.shooter;
        assert!((mid.x - mid_x).abs() < 1e-3, "{case}: middle {mid}");
        assert!(mid.y.abs() < 1e-3, "{case}: middle {mid}");
        assert!((mid.z - mid_z).abs() < 1e-3, "{case}: middle {mid}");
    }
}
