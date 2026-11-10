//! Projectile paths (OpenAC `IProjectileAutomation` on
//! `RuntimeAutomationSurface`): a probe sphere flown from the character to a
//! target through the session's own collision world, the one its bodies
//! move through, so every client answers from the same place.

use ac_physics::globals::{EPSILON, MAX_VELOCITY};
use ac_physics::{ObjectInfoState, PhysicsBody, PhysicsEngine, PhysicsState, ResolveRequest};
use ac_plugin::abi::combat::AttackHeight;
use ac_plugin::abi::projectile::{
    ProjectilePathKind, ProjectilePathQuery, ProjectilePathResult, ProjectilePathStatus,
};
use ac_plugin::abi::ui::{MapPoint, ProjectileSample};
use glam::{Vec2, Vec3};

use super::PluginBinding;

/// The flight, once the query is checked and both ends are known.
#[derive(Debug, Clone, Copy)]
pub struct Flight {
    /// The shooter's id in the collision world (it never blocks itself).
    pub shooter_id: u32,
    /// The shooter's body position, frame metres.
    pub shooter: Vec3,
    /// The cell the shooter stands in.
    pub cell_id: u32,
    /// The target's id in the collision world.
    pub target_id: u32,
    /// The target's base, frame metres.
    pub target: Vec3,
}

fn result(status: ProjectilePathStatus) -> ProjectilePathResult {
    ProjectilePathResult {
        status,
        ..ProjectilePathResult::default()
    }
}

/// The most a launch speed can be, metres per second: no object moves
/// faster.
const MAXIMUM_LAUNCH_SPEED: f32 = MAX_VELOCITY;

/// Gravity's pull on a curved flight, metres per second squared. The
/// physics constant points down (negative); the arc formulas here take its
/// size.
const GRAVITY_PULL: f32 = -ac_physics::globals::GRAVITY;

/// The launch of a missile or arc at a total `speed`, as a server
/// launches one: the lower of the two arcs under [`GRAVITY_PULL`] that reach a
/// point `across` metres away along the ground and `rise` metres higher.
/// The velocity across and up, and the launch angle in radians; `None`
/// when no arc reaches the point at this speed.
pub(super) fn ballistic_launch(speed: f32, across: f32, rise: f32) -> Option<(f32, f32, f32)> {
    let speed2 = speed * speed;
    let root =
        speed2 * speed2 - GRAVITY_PULL * (GRAVITY_PULL * across * across + 2.0 * rise * speed2);
    if !root.is_finite() || root < 0.0 {
        return None;
    }
    let angle = (speed2 - root.sqrt()).atan2(GRAVITY_PULL * across);
    Some((speed * angle.cos(), speed * angle.sin(), angle))
}

/// The velocity a flight starts with.
struct Launch {
    /// Along the ground towards the aim point, metres per second.
    across: f32,
    /// Upwards, metres per second.
    up: f32,
    /// The launch speed used and its angle in degrees, when the query gave
    /// a speed.
    asked: Option<(f32, f32)>,
}

/// OpenAC `EvaluateProjectilePath`: the shot leaves 1.2 m up (1.8 m for
/// an arc) a little ahead of the shooter, aims at 0.3, 0.9 or 1.5 m above
/// the target's base, flies at 100, 46 or 37.5185 m/s across (straight,
/// missile, arc), the curved kinds lofted to land under 9.8 m/s² gravity,
/// and is swept one step at a time; a step that ends on the target clears
/// it, anything else that stops it blocks. A missile or arc query with a
/// launch speed flies instead at that total speed (capped at 50 m/s) on the
/// lower arc that reaches the aim point, as a server launches a missile,
/// and the notice says the speed and angle used; a target no arc reaches at
/// that speed is invalid. `samples` collects one marker per step (in frame
/// metres) when given.
pub fn evaluate(
    engine: &PhysicsEngine,
    flight: &Flight,
    query: &ProjectilePathQuery,
    samples: Option<&mut Vec<(Vec3, bool)>>,
) -> ProjectilePathResult {
    let mut answer = fly(engine, flight, query, samples);
    if let Some((speed, degrees)) = answer.1 {
        let launched = format!("Launched at {speed:.1} m/s, {degrees:.1}° up.");
        answer.0.notice = Some(match answer.0.notice.take() {
            Some(notice) => format!("{notice} {launched}"),
            None => launched,
        });
    }
    answer.0
}

/// [`evaluate`]'s flight, with the launch it modelled when the query gave
/// a speed.
#[allow(clippy::too_many_lines)]
fn fly(
    engine: &PhysicsEngine,
    flight: &Flight,
    query: &ProjectilePathQuery,
    mut samples: Option<&mut Vec<(Vec3, bool)>>,
) -> (ProjectilePathResult, Option<(f32, f32)>) {
    let radius = query.projectile_radius;
    let base_delta = flight.target - flight.shooter;
    let mut horizontal = Vec2::new(base_delta.x, base_delta.y);
    let mut horizontal_distance = horizontal.length();
    if !horizontal_distance.is_finite() || horizontal_distance <= EPSILON {
        return (result(ProjectilePathStatus::InvalidTarget), None);
    }
    let mut direction = horizontal / horizontal_distance;
    let source_forward = match query.kind {
        ProjectilePathKind::Arc => 0.44,
        ProjectilePathKind::Missile => 0.61,
        ProjectilePathKind::Straight => 0.66,
    };
    let source_height = if query.kind == ProjectilePathKind::Arc {
        1.8
    } else {
        1.2
    };
    let target_height = match query.target_height {
        AttackHeight::Low => 0.3,
        AttackHeight::High => 1.5,
        AttackHeight::Medium => 0.9,
    };
    let mut current = flight.shooter
        + Vec3::new(
            direction.x * source_forward,
            direction.y * source_forward,
            source_height,
        );
    let destination = flight.target + Vec3::new(0.0, 0.0, target_height);
    let delta = destination - current;
    horizontal = Vec2::new(delta.x, delta.y);
    horizontal_distance = horizontal.length();
    if horizontal_distance <= EPSILON {
        // The aim point is straight above or below the start: nothing to
        // fly through. A launch at a given speed must still be usable and
        // reach that high.
        if query.launch_speed.is_none() || query.kind == ProjectilePathKind::Straight {
            return (result(ProjectilePathStatus::Clear), None);
        }
        return match launch(query, horizontal_distance, delta.z) {
            Ok(launch) => (result(ProjectilePathStatus::Clear), launch.asked),
            Err(refused) => (refused, None),
        };
    }
    direction = horizontal / horizontal_distance;
    let launch = match launch(query, horizontal_distance, delta.z) {
        Ok(launch) => launch,
        Err(refused) => return (refused, None),
    };
    let asked = launch.asked;
    let speed = launch.across;
    let total_time = horizontal_distance / speed;
    let mut velocity = Vec3::new(direction.x * speed, direction.y * speed, launch.up);
    let mut elapsed = 0.0f32;
    let mut cell_id = flight.cell_id;
    let mut probe = PhysicsBody::new();
    probe.state = PhysicsState::MISSILE | PhysicsState::INELASTIC | PhysicsState::REPORT_COLLISIONS;
    let maximum = query.maximum_collision_checks;
    for check in 1..=maximum {
        let remaining = (total_time - elapsed).max(0.0);
        if remaining <= EPSILON {
            return (
                ProjectilePathResult {
                    collision_checks: check - 1,
                    ..result(ProjectilePathStatus::Clear)
                },
                asked,
            );
        }
        let velocity_magnitude = velocity.length();
        if !velocity_magnitude.is_finite() || velocity_magnitude <= EPSILON {
            return (
                ProjectilePathResult {
                    collision_checks: check - 1,
                    notice: Some("The projectile trajectory became invalid.".to_owned()),
                    ..result(ProjectilePathStatus::Error)
                },
                asked,
            );
        }
        let quantum = remaining.min(query.step_distance / velocity_magnitude);
        let mut next = current + velocity * quantum;
        if quantum >= remaining - EPSILON {
            next = destination;
        }
        let resolved = engine.resolve_with_transition(
            &ResolveRequest {
                current_pos: current,
                target_pos: next,
                cell_id,
                sphere_radius: radius,
                sphere_height: 0.0,
                step_up_height: 0.0,
                step_down_height: 0.0,
                is_on_ground: false,
                mover_flags: ObjectInfoState(ObjectInfoState::PATH_CLIPPED),
                moving_entity_id: flight.shooter_id,
                local_sphere_origin: Some(Vec3::ZERO),
                designated_target_id: flight.target_id,
                ..ResolveRequest::default()
            },
            Some(&mut probe),
        );
        let requested = current.distance(next);
        let delivered = current.distance(resolved.position);
        let stopped = !resolved.ok
            || resolved.collided_with_environment
            || resolved.last_collided_object_id != 0
            || resolved.collision_normal_valid
            || delivered + 0.01 < requested;
        let target_hit = resolved.last_collided_object_id == flight.target_id;
        if let Some(samples) = samples.as_deref_mut() {
            samples.push((resolved.position, target_hit || !stopped));
        }
        if target_hit {
            return (
                ProjectilePathResult {
                    collision_checks: check,
                    blocking_object_id: flight.target_id,
                    ..result(ProjectilePathStatus::Clear)
                },
                asked,
            );
        }
        if stopped {
            return (
                ProjectilePathResult {
                    collision_checks: check,
                    blocking_object_id: resolved.last_collided_object_id,
                    ..result(ProjectilePathStatus::Blocked)
                },
                asked,
            );
        }
        current = resolved.position;
        cell_id = resolved.cell_id;
        elapsed += quantum;
        if query.kind != ProjectilePathKind::Straight {
            velocity.z -= GRAVITY_PULL * quantum;
        }
    }
    (
        ProjectilePathResult {
            collision_checks: maximum,
            notice: Some("The projectile collision-check budget was exhausted.".to_owned()),
            ..result(ProjectilePathStatus::BudgetExceeded)
        },
        asked,
    )
}

/// The velocity the flight starts with. Without a launch speed, or for a
/// straight shot: the kind's speed across, and the climb that lands on the
/// aim point (lofted against gravity for the curved kinds). With one, for
/// a missile or arc: the lower arc at that total speed (above 50 m/s,
/// 50), or an invalid target when the speed is not a finite number above 0
/// or no arc reaches.
fn launch(
    query: &ProjectilePathQuery,
    across: f32,
    rise: f32,
) -> Result<Launch, ProjectilePathResult> {
    let curved = query.kind != ProjectilePathKind::Straight;
    if let Some(asked) = query.launch_speed.filter(|_| curved) {
        if !asked.is_finite() || asked <= 0.0 {
            return Err(ProjectilePathResult {
                notice: Some("The launch speed must be a finite number above 0 m/s.".to_owned()),
                ..result(ProjectilePathStatus::InvalidTarget)
            });
        }
        let speed = asked.min(MAXIMUM_LAUNCH_SPEED);
        let Some((across_speed, up, angle)) = ballistic_launch(speed, across, rise) else {
            return Err(ProjectilePathResult {
                notice: Some(format!("The target is out of reach at {speed:.1} m/s.")),
                ..result(ProjectilePathStatus::InvalidTarget)
            });
        };
        return Ok(Launch {
            across: across_speed,
            up,
            asked: Some((speed, angle.to_degrees())),
        });
    }
    let speed = match query.kind {
        ProjectilePathKind::Arc => 37.5185,
        ProjectilePathKind::Missile => 46.0,
        ProjectilePathKind::Straight => 100.0,
    };
    let total_time = across / speed;
    let up = if curved {
        (rise + 0.5 * GRAVITY_PULL * total_time * total_time) / total_time
    } else {
        rise / total_time
    };
    Ok(Launch {
        across: speed,
        up,
        asked: None,
    })
}

impl PluginBinding<'_> {
    /// OpenAC `IProjectileAutomation.IsAvailable`: a session in the world.
    pub(super) fn projectiles_available(&self) -> bool {
        self.in_world()
    }

    /// OpenAC `EvaluateProjectilePathRequest`: the query checked, both
    /// ends found (the target's body, else where the server last put it),
    /// the start's collision loaded, then the flight. Ids in the answer are
    /// the server's (a static object's is its own).
    pub(super) fn projectiles_evaluate_path(
        &mut self,
        query: &ProjectilePathQuery,
    ) -> ProjectilePathResult {
        if !self.in_world() {
            return result(ProjectilePathStatus::Unavailable);
        }
        if query.target_id == 0
            || !query.projectile_radius.is_finite()
            || query.projectile_radius <= 0.0
            || !query.step_distance.is_finite()
            || query.step_distance <= 0.0
            || query.maximum_collision_checks <= 0
        {
            return result(ProjectilePathStatus::InvalidTarget);
        }
        let world = &self.state.world;
        let (Some(player), Some(target)) = (
            world.player_guid().and_then(|g| world.object(g)),
            world.object(query.target_id),
        ) else {
            return result(ProjectilePathStatus::InvalidTarget);
        };
        let shooter_id = player.key().local_id;
        let target_id = target.key().local_id;
        let target_place = target.accepted_position().copied();
        let parts = self.state.movement.navigation_parts();
        let Some(controller) = parts.controller else {
            return result(ProjectilePathStatus::InvalidTarget);
        };
        let cell_id = controller.current_cell_position().obj_cell_id;
        let target = match parts.remote_positions.get(&target_id) {
            Some(at) => Some(*at),
            None => target_place.and_then(|p| {
                parts
                    .residency
                    .origin_of(p.cell_id)
                    .map(|o| o + Vec3::new(p.origin.x, p.origin.y, p.origin.z))
            }),
        };
        let Some(target) = target.filter(|_| cell_id != 0) else {
            return result(ProjectilePathStatus::InvalidTarget);
        };
        // A flight through a place whose collision is not loaded would meet
        // nothing and read as clear: no answer, and it says so.
        let loaded = if cell_id & 0xFFFF >= 0x0100 {
            parts.engine.is_spawn_cell_ready(cell_id)
        } else {
            parts.engine.is_landblock_terrain_resident(cell_id)
        };
        if !loaded {
            return result(ProjectilePathStatus::Unavailable);
        }
        let flight = Flight {
            shooter_id,
            shooter: controller.position(),
            cell_id,
            target_id,
            target,
        };
        let mut samples = Vec::new();
        let mut answer = evaluate(
            parts.engine,
            &flight,
            query,
            query.diagnostics.then_some(&mut samples),
        );
        let center = parts.residency.frame_center();
        answer.debug_samples = samples
            .into_iter()
            .filter_map(|(at, is_clear)| {
                Some(ProjectileSample {
                    position: map_point(center?, at),
                    is_clear,
                    radius: query.projectile_radius,
                })
            })
            .collect();
        if answer.blocking_object_id != 0 {
            answer.blocking_object_id = self
                .state
                .world
                .object_by_local_id(answer.blocking_object_id)
                .map_or(answer.blocking_object_id, |o| o.guid());
        }
        answer
    }
}

/// A frame position (metres from the frame centre landblock's corner) on
/// the map grid.
fn map_point(center: u32, at: Vec3) -> MapPoint {
    let corner_x = f64::from((center >> 24) & 0xFF) * 192.0;
    let corner_y = f64::from((center >> 16) & 0xFF) * 192.0;
    let unit = ac_plugin_ui::MapPoint::UNIT_METRES;
    let origin = ac_plugin_ui::MapPoint::ORIGIN_METRES;
    MapPoint {
        east_west: (corner_x + f64::from(at.x) - origin) / unit,
        north_south: (corner_y + f64::from(at.y) - origin) / unit,
        elevation: f64::from(at.z) / unit,
    }
}
