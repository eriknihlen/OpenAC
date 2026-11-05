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

use super::projectile::{Flight, evaluate};
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
