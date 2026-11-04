//! Combat (API group F): the combat state, the creatures worth attacking,
//! combat modes and physical attacks.

use crate::{wire_enum, wire_record};

wire_enum! {
    /// A combat mode.
    pub enum CombatMode: "combat mode" {
        /// Not known yet.
        Unknown = 0,
        /// Peace.
        Peace = 1,
        /// Melee.
        Melee = 2,
        /// Missile.
        Missile = 3,
        /// Magic.
        Magic = 4,
    }
}

wire_enum! {
    /// Where a swing goes.
    pub enum AttackHeight: "attack height" {
        /// High.
        High = 1,
        /// Medium.
        Medium = 2,
        /// Low.
        Low = 3,
    }
}

wire_record! {
    /// A creature the character could attack.
    pub struct CombatTarget {
        /// Its id.
        pub object_id: u32,
        /// Its name.
        pub name: String,
        /// Its weenie class.
        pub weenie_class_id: u32,
        /// Straight-line distance, metres (height included).
        pub distance: f32,
        /// Where it stands relative to the character's facing, degrees
        /// (-180..180, positive clockwise).
        pub relative_angle_degrees: f32,
        /// A health report arrived for it.
        pub is_health_known: bool,
        /// Its health, 0..=1 (1 when not known).
        pub health_fraction: f32,
        /// Its creature type.
        pub species_id: i32,
        /// Its creature type's name.
        pub species_name: String,
        /// Its health at full, 0 when not known.
        pub maximum_health: i32,
        /// It wields armor (a shield).
        pub has_shield: bool,
        /// The server's instance counter.
        pub incarnation: u16,
        /// Counts its health reports.
        pub health_revision: i64,
        /// Seconds since its last health report (infinite for none).
        pub seconds_since_health_update: f64,
        /// How far above the character it is, metres.
        pub height_difference: f32,
        /// It died (this incarnation).
        pub is_dead: bool,
        /// The last position the server sent for it. `distance`, the angle
        /// and the height difference are measured to where the client has
        /// it now (its simulated body), which can stand apart from this.
        /// A change to it alone raises no object change, so a plugin that
        /// follows it asks again. Added in API 1.7.
        since (1, 7) pub server_position: Option<crate::world::Position>,
    }
}

wire_record! {
    /// The combat state now.
    pub struct CombatSnapshot {
        /// The selection.
        pub selected_id: u32,
        /// The mode.
        pub mode: CombatMode,
        /// Where the next swing goes.
        pub attack_height: AttackHeight,
        /// The power bar setting, 0..=1.
        pub desired_power: f32,
        /// The bar's level now.
        pub power_bar_level: f32,
        /// The bar is building.
        pub build_in_progress: bool,
        /// An attack key is held.
        pub request_in_progress: bool,
        /// A swing awaits the server.
        pub server_response_pending: bool,
        /// Swings repeat.
        pub repeat_attack_in_progress: bool,
        /// Counts finished swings.
        pub completion_revision: i64,
        /// The last finished swing's sequence.
        pub completion_sequence: u32,
        /// Its error, 0 for none.
        pub completion_weenie_error: u32,
        /// Counts the character's qualified motions.
        pub qualified_self_motion_revision: i64,
        /// Seconds since the last.
        pub qualified_self_motion_age_seconds: f64,
        /// The mode the server last set.
        pub server_mode: CombatMode,
    }
}

wire_enum! {
    /// The answer to a combat command.
    pub enum CombatStatus: "combat status" {
        /// Not in the world.
        Unavailable = 0,
        /// The target cannot be attacked.
        InvalidTarget = 1,
        /// Not in melee or missile combat.
        WrongMode = 2,
        /// A swing at that target is in flight.
        Busy = 3,
        /// Already in that mode.
        AlreadyReady = 4,
        /// The mode change went out (or waits for the body).
        ModeChangeSent = 5,
        /// The swing started.
        Started = 6,
        /// The swing was released.
        Released = 7,
        /// The attack stopped.
        Stopped = 8,
        /// The client declined.
        Refused = 9,
    }
}

wire_record! {
    /// The answer to a combat command.
    pub struct CombatResult {
        /// The verdict.
        pub status: CombatStatus,
        /// Why, in words.
        pub notice: Option<String>,
    }
}

impl CombatResult {
    /// An answer with no words.
    #[must_use]
    pub fn of(status: CombatStatus) -> Self {
        Self {
            status,
            notice: None,
        }
    }

    /// The command was taken.
    #[must_use]
    pub fn accepted(&self) -> bool {
        matches!(
            self.status,
            CombatStatus::AlreadyReady
                | CombatStatus::ModeChangeSent
                | CombatStatus::Started
                | CombatStatus::Released
                | CombatStatus::Stopped
        )
    }
}

wire_record! {
    /// Start a swing at a creature.
    pub struct AttackRequest {
        /// The creature (selected first).
        pub target_id: u32,
        /// Where.
        pub height: AttackHeight,
        /// The power, 0..=1.
        pub power: f32,
    }
}
