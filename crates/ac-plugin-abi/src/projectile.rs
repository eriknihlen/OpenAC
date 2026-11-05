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
        /// Why a budget or error result came out.
        pub notice: Option<String>,
        /// One marker per step, with diagnostics.
        pub debug_samples: Vec<ProjectileSample>,
    }
}
