//! The local combat state: the combat mode (the client's and the
//! server's), the last health percent reported for each object, and the
//! combat notifications turned into typed events.

use std::collections::HashMap;

use super::model::CombatMode;

/// Something hit the player.
#[derive(Debug, Clone, PartialEq)]
pub struct DamageIncoming {
    /// Attacker's name.
    pub attacker_name: String,
    /// Attacker's id.
    pub attacker_guid: u32,
    /// Damage type bits.
    pub damage_type: u32,
    /// Damage dealt.
    pub damage: u32,
    /// Hit quadrant (body part) bits.
    pub hit_quadrant: u32,
    /// A critical hit.
    pub critical: bool,
    /// Attack type bits (zero for a defender notification).
    pub attack_type: u32,
    /// Damage as a fraction of maximum health.
    pub damage_percent: f64,
    /// Attack-condition bits.
    pub attack_conditions: u64,
}

/// The player hit something.
#[derive(Debug, Clone, PartialEq)]
pub struct DamageDealt {
    /// Defender's name.
    pub defender_name: String,
    /// Damage type bits.
    pub damage_type: u32,
    /// Damage dealt.
    pub damage: u32,
    /// Damage as a fraction of the defender's maximum health.
    pub damage_percent: f64,
    /// A critical hit.
    pub critical: bool,
    /// Attack-condition bits.
    pub attack_conditions: u64,
}

/// A combat event for the owner to publish.
#[derive(Debug, Clone, PartialEq)]
pub enum CombatEvent {
    /// An object's health percent was reported.
    HealthChanged {
        /// The object.
        guid: u32,
        /// Health as a fraction of its maximum.
        percent: f32,
    },
    /// The player was hit.
    DamageTaken(DamageIncoming),
    /// The player hit something.
    DamageDealt(DamageDealt),
    /// The player evaded an attack from this attacker.
    EvadedIncoming(String),
    /// This defender evaded the player's attack.
    MissedOutgoing(String),
    /// An attack finished.
    AttackDone {
        /// Attack sequence.
        sequence: u32,
        /// Error code (zero on success).
        weenie_error: u32,
    },
    /// The server accepted an attack; the power bar and animation begin.
    AttackCommenced,
    /// The combat mode changed.
    CombatModeChanged(CombatMode),
    /// The player killed something.
    KillLanded {
        /// The victim's name.
        victim_name: String,
        /// The victim's id.
        victim_guid: u32,
    },
}

/// The local player's combat state.
#[derive(Debug, Clone, PartialEq)]
pub struct CombatState {
    health_by_guid: HashMap<u32, f32>,
    current_mode: CombatMode,
    server_mode: Option<CombatMode>,
}

impl Default for CombatState {
    fn default() -> Self {
        Self::new()
    }
}

impl CombatState {
    /// Peace mode, nothing tracked.
    #[must_use]
    pub fn new() -> Self {
        Self {
            health_by_guid: HashMap::new(),
            current_mode: CombatMode::NON_COMBAT,
            server_mode: None,
        }
    }

    /// The current combat mode.
    #[must_use]
    pub fn current_mode(&self) -> CombatMode {
        self.current_mode
    }

    /// The mode the server last reported, or `None` before it has reported
    /// one this session. The client follows it, so it is also the current
    /// mode from then on; it lags a change the client sent until the server
    /// takes it.
    #[must_use]
    pub fn server_mode(&self) -> Option<CombatMode> {
        self.server_mode
    }

    /// The last health percent reported for an object, 1.0 when unknown.
    #[must_use]
    pub fn health_percent(&self, guid: u32) -> f32 {
        self.health_by_guid.get(&guid).copied().unwrap_or(1.0)
    }

    /// True when a health percent was reported for the object.
    #[must_use]
    pub fn has_health(&self, guid: u32) -> bool {
        self.health_by_guid.contains_key(&guid)
    }

    /// How many objects have a reported health percent.
    #[must_use]
    pub fn tracked_target_count(&self) -> usize {
        self.health_by_guid.len()
    }

    /// An object's health was reported.
    pub fn on_update_health(&mut self, target_guid: u32, health_percent: f32) -> CombatEvent {
        self.health_by_guid.insert(target_guid, health_percent);
        CombatEvent::HealthChanged {
            guid: target_guid,
            percent: health_percent,
        }
    }

    /// Sets the combat mode; the event when it changed.
    pub fn set_combat_mode(&mut self, mode: CombatMode) -> Option<CombatEvent> {
        if self.current_mode == mode {
            return None;
        }
        self.current_mode = mode;
        Some(CombatEvent::CombatModeChanged(mode))
    }

    /// The server's word on the mode: recorded, then followed.
    pub fn set_server_combat_mode(&mut self, mode: CombatMode) -> Option<CombatEvent> {
        self.server_mode = Some(mode);
        self.set_combat_mode(mode)
    }

    /// The player was the victim of an attack.
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn on_victim_notification(
        &self,
        attacker_name: &str,
        attacker_guid: u32,
        damage_type: u32,
        damage: u32,
        hit_quadrant: u32,
        critical: u32,
        attack_type: u32,
        damage_percent: f64,
        attack_conditions: u64,
    ) -> CombatEvent {
        CombatEvent::DamageTaken(DamageIncoming {
            attacker_name: attacker_name.to_owned(),
            attacker_guid,
            damage_type,
            damage,
            hit_quadrant,
            critical: critical != 0,
            attack_type,
            damage_percent,
            attack_conditions,
        })
    }

    /// The player was hit (defender notification; no attack type).
    #[allow(clippy::too_many_arguments)]
    #[must_use]
    pub fn on_defender_notification(
        &self,
        attacker_name: &str,
        attacker_guid: u32,
        damage_type: u32,
        damage: u32,
        hit_quadrant: u32,
        critical: u32,
        damage_percent: f64,
        attack_conditions: u64,
    ) -> CombatEvent {
        self.on_victim_notification(
            attacker_name,
            attacker_guid,
            damage_type,
            damage,
            hit_quadrant,
            critical,
            0,
            damage_percent,
            attack_conditions,
        )
    }

    /// The player hit something.
    #[must_use]
    pub fn on_attacker_notification(
        &self,
        defender_name: &str,
        damage_type: u32,
        damage: u32,
        damage_percent: f64,
        critical: u32,
        attack_conditions: u64,
    ) -> CombatEvent {
        CombatEvent::DamageDealt(DamageDealt {
            defender_name: defender_name.to_owned(),
            damage_type,
            damage,
            damage_percent,
            critical: critical != 0,
            attack_conditions,
        })
    }

    /// The defender evaded the player's attack.
    #[must_use]
    pub fn on_evasion_attacker_notification(&self, defender_name: &str) -> CombatEvent {
        CombatEvent::MissedOutgoing(defender_name.to_owned())
    }

    /// The player evaded an attack.
    #[must_use]
    pub fn on_evasion_defender_notification(&self, attacker_name: &str) -> CombatEvent {
        CombatEvent::EvadedIncoming(attacker_name.to_owned())
    }

    /// The player killed something.
    #[must_use]
    pub fn on_killer_notification(&self, victim_name: &str, victim_guid: u32) -> CombatEvent {
        CombatEvent::KillLanded {
            victim_name: victim_name.to_owned(),
            victim_guid,
        }
    }

    /// An attack finished.
    #[must_use]
    pub fn on_attack_done(&self, attack_sequence: u32, weenie_error: u32) -> CombatEvent {
        CombatEvent::AttackDone {
            sequence: attack_sequence,
            weenie_error,
        }
    }

    /// The server accepted an attack.
    #[must_use]
    pub fn on_combat_commence_attack(&self) -> CombatEvent {
        CombatEvent::AttackCommenced
    }

    /// Ends the character session: no tracked health, peace mode, no
    /// server mode. Always reports peace mode, even when already in it.
    pub fn clear(&mut self) -> CombatEvent {
        self.health_by_guid.clear();
        self.current_mode = CombatMode::NON_COMBAT;
        self.server_mode = None;
        CombatEvent::CombatModeChanged(CombatMode::NON_COMBAT)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/Combat/CombatStateTests.cs`.

    #[test]
    fn update_health_caches_percent() {
        let mut state = CombatState::new();
        assert_eq!(
            state.on_update_health(0xBEEF, 0.33),
            CombatEvent::HealthChanged {
                guid: 0xBEEF,
                percent: 0.33
            }
        );
        assert!((state.health_percent(0xBEEF) - 0.33).abs() < 1e-6);
    }

    #[test]
    fn unknown_health_is_full() {
        assert!(CombatState::new().health_percent(0xDEAD) == 1.0);
    }

    #[test]
    fn set_combat_mode_tracks_and_reports_change() {
        let mut state = CombatState::new();
        assert_eq!(
            state.set_combat_mode(CombatMode::MISSILE),
            Some(CombatEvent::CombatModeChanged(CombatMode::MISSILE))
        );
        assert_eq!(state.current_mode(), CombatMode::MISSILE);
        assert_eq!(state.set_combat_mode(CombatMode::MISSILE), None);
    }

    #[test]
    fn server_mode_is_recorded_then_followed() {
        let mut state = CombatState::new();
        assert_eq!(state.server_mode(), None);
        state.set_server_combat_mode(CombatMode::MELEE);
        assert_eq!(state.server_mode(), Some(CombatMode::MELEE));
        assert_eq!(state.current_mode(), CombatMode::MELEE);
    }

    #[test]
    fn notifications_become_events() {
        let state = CombatState::new();
        let CombatEvent::DamageTaken(taken) =
            state.on_victim_notification("Drudge", 0xAA, 1, 42, 3, 1, 8, 0.0, 0)
        else {
            panic!("expected damage taken");
        };
        assert_eq!(taken.attacker_name, "Drudge");
        assert_eq!(taken.damage, 42);
        assert!(taken.critical);

        let CombatEvent::DamageDealt(dealt) =
            state.on_attacker_notification("Drudge", 1, 30, 0.15, 0, 0)
        else {
            panic!("expected damage dealt");
        };
        assert_eq!(dealt.defender_name, "Drudge");
        assert_eq!(dealt.damage, 30);
        assert!((dealt.damage_percent - 0.15).abs() < 1e-9);

        assert_eq!(
            state.on_evasion_defender_notification("Rat"),
            CombatEvent::EvadedIncoming("Rat".to_owned())
        );
        assert_eq!(
            state.on_evasion_attacker_notification("Tusker"),
            CombatEvent::MissedOutgoing("Tusker".to_owned())
        );
        assert_eq!(
            state.on_attack_done(5, 0),
            CombatEvent::AttackDone {
                sequence: 5,
                weenie_error: 0
            }
        );
        assert_eq!(
            state.on_combat_commence_attack(),
            CombatEvent::AttackCommenced
        );
    }

    #[test]
    fn clear_resets_to_peace() {
        let mut state = CombatState::new();
        state.on_update_health(1, 0.5);
        state.on_update_health(2, 0.8);
        state.set_server_combat_mode(CombatMode::MELEE);
        assert_eq!(
            state.clear(),
            CombatEvent::CombatModeChanged(CombatMode::NON_COMBAT)
        );
        assert!(state.health_percent(1) == 1.0);
        assert_eq!(state.tracked_target_count(), 0);
        assert_eq!(state.current_mode(), CombatMode::NON_COMBAT);
        assert_eq!(state.server_mode(), None);
        // A second clear reports peace again.
        assert_eq!(
            state.clear(),
            CombatEvent::CombatModeChanged(CombatMode::NON_COMBAT)
        );
    }
}
