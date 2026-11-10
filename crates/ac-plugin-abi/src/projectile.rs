//! Projectile paths (OpenAC `IProjectileAutomation`): whether a shot at a
//! target would get there, by flying a probe sphere through the session's
//! collision world.

use crate::combat::AttackHeight;
use crate::ui::ProjectileSample;
use crate::{wire_enum, wire_record};

wire_enum! {
    /// How a projectile flies (OpenAC `PluginProjectilePathKind`).
    pub enum ProjectilePathKind: "projectile path kind" {
        /// Fast and flat: keeps its height.
        Straight = 0,
        /// Slow, lobbed high, pulled down by gravity.
        Arc = 1,
        /// Faster, still dropping under gravity.
        Missile = 2,
    }
}

wire_enum! {
    /// Why a path query did or did not admit the shot (OpenAC
    /// `PluginProjectilePathStatus`).
    pub enum ProjectilePathStatus: "projectile path status" {
        /// No session in the world, or the place is not loaded.
        Unavailable = 0,
        /// Nothing stopped the shot.
        Clear = 1,
        /// Something stood in the way.
        Blocked = 2,
        /// An unusable query or a target the client does not know.
        InvalidTarget = 3,
        /// The collision-check budget ran out first.
        BudgetExceeded = 4,
        /// The flight stopped being a usable number.
        Error = 5,
    }
}

wire_record! {
    /// One path query (OpenAC `EvaluatePath`).
    pub struct ProjectilePathQuery {
        /// The object shot at.
        pub target_id: u32,
        /// The flight model.
        pub kind: ProjectilePathKind,
        /// Where on the target to aim (0.3, 0.9 or 1.5 m above its base).
        pub target_height: AttackHeight,
        /// The probe sphere's radius, metres (above 0).
        pub projectile_radius: f32,
        /// How far the probe moves between checks, metres (above 0).
        pub step_distance: f32,
        /// The most collision checks to spend.
        pub maximum_collision_checks: i32,
        /// Keep a marker per step (OpenAC `EvaluatePathWithDiagnostics`).
        pub diagnostics: bool,
        /// The launch speed, metres per second, for a missile or arc: the
        /// launcher's maximum velocity. With it the probe flies as a server
        /// launches a missile: at this total speed, capped at 50, on the
        /// lower of the two arcs that reach the aim point under 9.8 m/s²
        /// gravity. A speed that is not a finite number above 0, or a
        /// target no arc reaches at the (capped) speed, is an invalid
        /// target, and the notice says which. Otherwise the answer's notice
        /// ends with the speed and launch angle used. Without it, or for a
        /// straight shot, the probe flies as before: 46 m/s across for a
        /// missile, 37.5185 m/s for an arc, lofted to land on the aim
        /// point. The probe flies toward where the target is now; the
        /// server aims ahead of a moving target, so the real shot can take
        /// a different arc. Added in API 1.12.
        since (1, 12) pub launch_speed: Option<f32>,
    }
}

wire_record! {
    /// The answer (OpenAC `PluginProjectilePathResult`).
    pub struct ProjectilePathResult {
        /// Admitted, and if not, why.
        pub status: ProjectilePathStatus,
        /// How many checks were made.
        pub collision_checks: i32,
        /// The object that stopped the shot (the target itself when the
        /// flight ended on it); 0 for the landscape or nothing.
        pub blocking_object_id: u32,
        /// Why a budget, error or out-of-reach result came out, and, when
        /// the query gave a launch speed, the speed and launch angle the
        /// flight used.
        pub notice: Option<String>,
        /// One marker per step, with diagnostics.
        pub debug_samples: Vec<ProjectileSample>,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ApiVersion, Wire};

    // API 1.12 added the launch speed: what a plugin built for 1.11 sends is
    // the query without it, byte for byte as before, and reads as no launch
    // speed; one built for 1.12 sends it.
    #[test]
    fn a_query_from_a_plugin_built_for_api_1_11_has_no_launch_speed() {
        let v1_11 = ApiVersion::new(1, 11);
        let asked = ProjectilePathQuery {
            target_id: 0x5000_0001,
            kind: ProjectilePathKind::Missile,
            target_height: AttackHeight::Medium,
            projectile_radius: 0.4,
            step_distance: 0.7,
            maximum_collision_checks: 500,
            diagnostics: true,
            launch_speed: Some(20.0),
        };
        let without = ProjectilePathQuery {
            launch_speed: None,
            ..asked.clone()
        };
        let old = asked.to_bytes_at(v1_11);
        assert_eq!(old, without.to_bytes_at(v1_11));
        assert_eq!(old.len() + 5, asked.to_bytes().len());
        assert_eq!(
            ProjectilePathQuery::from_bytes_at(&old, v1_11).unwrap(),
            without
        );
        let now = ProjectilePathQuery::from_bytes(&asked.to_bytes()).unwrap();
        assert_eq!(now, asked);
    }
}
