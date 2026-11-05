//! Picking the swing animation from the combat maneuver table: the
//! entries for a stance, height and attack type, and the power level that
//! chooses between the first and second of them.

use super::model::{AttackHeight, AttackType};

/// Below this power the second maneuver is used.
pub const DEFAULT_SUBDIVISION: f32 = 0.33;
/// The subdivision for a weapon that both thrusts and slashes.
pub const THRUST_SLASH_SUBDIVISION: f32 = 0.66;

/// One entry of a combat maneuver table (read from the portal dat by the
/// runtime).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CombatManeuver {
    /// Combat stance (motion style).
    pub style: u32,
    /// Attack height.
    pub attack_height: AttackHeight,
    /// Attack type.
    pub attack_type: AttackType,
    /// Minimum skill level.
    pub min_skill_level: u32,
    /// Motion command.
    pub motion: u32,
}

/// The chosen maneuver.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CombatManeuverSelection {
    /// A maneuver was found.
    pub found: bool,
    /// Its motion command (0 when none).
    pub motion: u32,
    /// Every matching motion, in table order.
    pub candidates: Vec<u32>,
    /// The attack type used.
    pub effective_attack_type: AttackType,
    /// The power subdivision used.
    pub subdivision: f32,
}

/// Picks the maneuver for an attack: the first matching entry, or the
/// second when there are several and the power is below the subdivision.
#[must_use]
pub fn select_motion(
    maneuvers: &[CombatManeuver],
    stance: u32,
    attack_height: AttackHeight,
    attack_type: AttackType,
    power_level: f32,
    is_thrust_slash_weapon: bool,
) -> CombatManeuverSelection {
    let motions = find_motions(maneuvers, stance, attack_height, attack_type);
    let Some(&first) = motions.first() else {
        return CombatManeuverSelection::default();
    };
    let subdivision = if is_thrust_slash_weapon {
        THRUST_SLASH_SUBDIVISION
    } else {
        DEFAULT_SUBDIVISION
    };
    let motion = match motions.get(1) {
        Some(&second) if power_level < subdivision => second,
        _ => first,
    };
    CombatManeuverSelection {
        found: true,
        motion,
        candidates: motions,
        effective_attack_type: attack_type,
        subdivision,
    }
}

/// The motions of every entry matching stance, height and type, in table
/// order.
#[must_use]
pub fn find_motions(
    maneuvers: &[CombatManeuver],
    stance: u32,
    attack_height: AttackHeight,
    attack_type: AttackType,
) -> Vec<u32> {
    maneuvers
        .iter()
        .filter(|m| {
            m.style == stance && m.attack_height == attack_height && m.attack_type == attack_type
        })
        .map(|m| m.motion)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::combat::animation::motion_command as mc;

    // Golden behavior from OpenAC
    // `tests/AcDream.Core.Tests/Combat/CombatManeuverSelectorTests.cs`.

    const SLASH_LOW: u32 = 0x1000_005D;
    const BACKHAND_LOW: u32 = 0x1000_0060;
    const ATTACK_LOW2: u32 = 0x1000_0067;

    fn entry(
        style: u32,
        height: AttackHeight,
        attack_type: AttackType,
        motion: u32,
    ) -> CombatManeuver {
        CombatManeuver {
            style,
            attack_height: height,
            attack_type,
            min_skill_level: 0,
            motion,
        }
    }

    #[test]
    fn first_entry_at_or_above_subdivision() {
        let table = [
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::MEDIUM,
                AttackType::SLASH,
                mc::SLASH_MED,
            ),
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::MEDIUM,
                AttackType::SLASH,
                mc::BACKHAND_MED,
            ),
        ];
        let at = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::MEDIUM,
            AttackType::SLASH,
            DEFAULT_SUBDIVISION,
            false,
        );
        let high = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::MEDIUM,
            AttackType::SLASH,
            1.0,
            false,
        );
        assert_eq!(at.motion, mc::SLASH_MED);
        assert_eq!(high.motion, mc::SLASH_MED);
    }

    #[test]
    fn second_entry_below_subdivision() {
        let table = [
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::MEDIUM,
                AttackType::SLASH,
                mc::SLASH_MED,
            ),
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::MEDIUM,
                AttackType::SLASH,
                mc::BACKHAND_MED,
            ),
        ];
        let selection = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::MEDIUM,
            AttackType::SLASH,
            0.2,
            false,
        );
        assert!(selection.found);
        assert_eq!(selection.motion, mc::BACKHAND_MED);
        assert_eq!(selection.effective_attack_type, AttackType::SLASH);
        assert_eq!(selection.candidates.len(), 2);
    }

    #[test]
    fn thrust_slash_weapon_uses_two_thirds() {
        let table = [
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::HIGH,
                AttackType::SLASH,
                mc::SLASH_HIGH,
            ),
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::HIGH,
                AttackType::SLASH,
                mc::BACKHAND_HIGH,
            ),
        ];
        let normal = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::HIGH,
            AttackType::SLASH,
            0.5,
            false,
        );
        let thrust_slash = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::HIGH,
            AttackType::SLASH,
            0.5,
            true,
        );
        assert_eq!(normal.motion, mc::SLASH_HIGH);
        assert_eq!(thrust_slash.motion, mc::BACKHAND_HIGH);
        assert!(thrust_slash.subdivision == THRUST_SLASH_SUBDIVISION);
    }

    #[test]
    fn missing_lookup_is_none() {
        let table = [entry(
            mc::BOW_COMBAT,
            AttackHeight::HIGH,
            AttackType::PUNCH,
            mc::SHOOT,
        )];
        let selection = select_motion(
            &table,
            mc::SWORD_COMBAT,
            AttackHeight::HIGH,
            AttackType::PUNCH,
            0.5,
            false,
        );
        assert_eq!(selection, CombatManeuverSelection::default());
        assert!(!selection.found);
    }

    #[test]
    fn find_motions_keeps_table_order() {
        let table = [
            entry(
                mc::HAND_COMBAT,
                AttackHeight::LOW,
                AttackType::KICK,
                mc::ATTACK_LOW1,
            ),
            entry(
                mc::HAND_COMBAT,
                AttackHeight::LOW,
                AttackType::KICK,
                mc::ATTACK_LOW6,
            ),
            entry(
                mc::HAND_COMBAT,
                AttackHeight::LOW,
                AttackType::PUNCH,
                ATTACK_LOW2,
            ),
            entry(
                mc::HAND_COMBAT,
                AttackHeight::HIGH,
                AttackType::KICK,
                SLASH_LOW,
            ),
            entry(
                mc::SWORD_COMBAT,
                AttackHeight::LOW,
                AttackType::KICK,
                BACKHAND_LOW,
            ),
        ];
        assert_eq!(
            find_motions(&table, mc::HAND_COMBAT, AttackHeight::LOW, AttackType::KICK),
            [mc::ATTACK_LOW1, mc::ATTACK_LOW6]
        );
    }
}
