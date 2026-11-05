//! Projectile paths (OpenAC `IProjectileAutomation` on
//! `RuntimeAutomationSurface`): a probe sphere flown from the character to a
//! target through the session's own collision world, the one its bodies
//! move through, so every client answers from the same place.

use ac_physics::globals::EPSILON;
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

/// OpenAC `EvaluateProjectilePath`: the shot leaves 1.2 m up (1.8 m for
/// an arc) a little ahead of the shooter, aims at 0.3, 0.9 or 1.5 m above
/// the target's base, flies at 100, 46 or 37.5185 m/s (straight, missile,
/// arc), the curved kinds lofted to land under 9.8 m/s² gravity, and is
/// swept one step at a time; a step that ends on the target clears it,
/// anything else that stops it blocks. `samples` collects one marker per
/// step (in frame metres) when given.
#[allow(clippy::too_many_lines)]
pub fn evaluate(
    engine: &PhysicsEngine,
    flight: &Flight,
    query: &ProjectilePathQuery,
    mut samples: Option<&mut Vec<(Vec3, bool)>>,
) -> ProjectilePathResult {
    let radius = query.projectile_radius;
    let base_delta = flight.target - flight.shooter;
    let mut horizontal = Vec2::new(base_delta.x, base_delta.y);
    let mut horizontal_distance = horizontal.length();
    if !horizontal_distance.is_finite() || horizontal_distance <= EPSILON {
        return result(ProjectilePathStatus::InvalidTarget);
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
        return result(ProjectilePathStatus::Clear);
    }
    direction = horizontal / horizontal_distance;
    let speed = match query.kind {
        ProjectilePathKind::Arc => 37.5185,
        ProjectilePathKind::Missile => 46.0,
        ProjectilePathKind::Straight => 100.0,
    };
    let total_time = horizontal_distance / speed;
    let vertical_speed = if query.kind == ProjectilePathKind::Straight {
        delta.z / total_time
    } else {
        (delta.z + 4.9 * total_time * total_time) / total_time
    };
    let mut velocity = Vec3::new(direction.x * speed, direction.y * speed, vertical_speed);
    let mut elapsed = 0.0f32;
    let mut cell_id = flight.cell_id;
    let mut probe = PhysicsBody::new();
    probe.state = PhysicsState::MISSILE | PhysicsState::INELASTIC | PhysicsState::REPORT_COLLISIONS;
    let maximum = query.maximum_collision_checks;
    for check in 1..=maximum {
        let remaining = (total_time - elapsed).max(0.0);
        if remaining <= EPSILON {
            return ProjectilePathResult {
                collision_checks: check - 1,
                ..result(ProjectilePathStatus::Clear)
            };
        }
        let velocity_magnitude = velocity.length();
        if !velocity_magnitude.is_finite() || velocity_magnitude <= EPSILON {
            return ProjectilePathResult {
                collision_checks: check - 1,
                notice: Some("The projectile trajectory became invalid.".to_owned()),
                ..result(ProjectilePathStatus::Error)
            };
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
            return ProjectilePathResult {
                collision_checks: check,
                blocking_object_id: flight.target_id,
                ..result(ProjectilePathStatus::Clear)
            };
        }
        if stopped {
            return ProjectilePathResult {
                collision_checks: check,
                blocking_object_id: resolved.last_collided_object_id,
                ..result(ProjectilePathStatus::Blocked)
            };
        }
        current = resolved.position;
        cell_id = resolved.cell_id;
        elapsed += quantum;
        if query.kind != ProjectilePathKind::Straight {
            velocity.z -= 9.8 * quantum;
        }
    }
    ProjectilePathResult {
        collision_checks: maximum,
        notice: Some("The projectile collision-check budget was exhausted.".to_owned()),
        ..result(ProjectilePathStatus::BudgetExceeded)
    }
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
