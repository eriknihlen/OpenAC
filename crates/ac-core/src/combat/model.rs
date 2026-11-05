//! Combat modes, attack heights and types, damage types and body parts,
//! and the input rules around them: the default mode for what the
//! character holds, the combat-mode toggle, and whether the character is
//! in position to attack or change mode.

use super::animation::motion_command;
use crate::items::{CombatUse, EquipMask, ItemType};
use crate::wire_enum::wire_enum;

wire_enum! {
    flags
    /// Combat mode.
    CombatMode {
        UNDEF = 0 => "Undef",
        NON_COMBAT = 0x01 => "NonCombat",
        MELEE = 0x02 => "Melee",
        MISSILE = 0x04 => "Missile",
        MAGIC = 0x08 => "Magic",
        VALID_COMBAT = 0x0F => "ValidCombat",
        COMBAT_COMBAT = 0x0E => "CombatCombat",
    }
}

wire_enum! {
    values
    /// Attack height.
    AttackHeight {
        UNDEF = 0 => "Undef",
        HIGH = 1 => "High",
        MEDIUM = 2 => "Medium",
        LOW = 3 => "Low",
    }
}

wire_enum! {
    flags
    /// Attack type bits.
    AttackType {
        NONE = 0 => "None",
        PUNCH = 0x0001 => "Punch",
        THRUST = 0x0002 => "Thrust",
        SLASH = 0x0004 => "Slash",
        KICK = 0x0008 => "Kick",
        OFFHAND_PUNCH = 0x0010 => "OffhandPunch",
        DOUBLE_SLASH = 0x0020 => "DoubleSlash",
        TRIPLE_SLASH = 0x0040 => "TripleSlash",
        DOUBLE_THRUST = 0x0080 => "DoubleThrust",
        TRIPLE_THRUST = 0x0100 => "TripleThrust",
        OFFHAND_THRUST = 0x0200 => "OffhandThrust",
        OFFHAND_SLASH = 0x0400 => "OffhandSlash",
        OFFHAND_DOUBLE_SLASH = 0x0800 => "OffhandDoubleSlash",
        OFFHAND_TRIPLE_SLASH = 0x1000 => "OffhandTripleSlash",
        OFFHAND_DOUBLE_THRUST = 0x2000 => "OffhandDoubleThrust",
        OFFHAND_TRIPLE_THRUST = 0x4000 => "OffhandTripleThrust",
        UNARMED = 0x0019 => "Unarmed",
        MULTI_STRIKE = 0x79E0 => "MultiStrike",
    }
}

wire_enum! {
    flags
    /// Damage type bits.
    DamageType {
        UNDEF = 0 => "Undef",
        SLASH = 0x0001 => "Slash",
        PIERCE = 0x0002 => "Pierce",
        BLUDGEON = 0x0004 => "Bludgeon",
        COLD = 0x0008 => "Cold",
        FIRE = 0x0010 => "Fire",
        ACID = 0x0020 => "Acid",
        ELECTRIC = 0x0040 => "Electric",
        HEALTH = 0x0080 => "Health",
        STAMINA = 0x0100 => "Stamina",
        MANA = 0x0200 => "Mana",
        NETHER = 0x0400 => "Nether",
        BASE = 0x1000_0000 => "Base",
    }
}

wire_enum! {
    values
    /// Body part (hit location).
    BodyPart {
        HEAD = 0 => "Head",
        CHEST = 1 => "Chest",
        ABDOMEN = 2 => "Abdomen",
        UPPER_ARM = 3 => "UpperArm",
        LOWER_ARM = 4 => "LowerArm",
        HAND = 5 => "Hand",
        UPPER_LEG = 6 => "UpperLeg",
        LOWER_LEG = 7 => "LowerLeg",
        FOOT = 8 => "Foot",
    }
}

/// The three attack keys.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CombatAttackAction {
    /// Low attack.
    Low,
    /// Medium attack.
    Medium,
    /// High attack.
    High,
}

/// The dual-wield combat style.
pub const DUAL_WIELD_COMBAT_STYLE: u32 = motion_command::DUAL_WIELD_COMBAT;
/// The forward command a missile stance must hold to be ready.
pub const READY_FORWARD_COMMAND: u32 = motion_command::READY;
/// Seconds for the power bar to fill.
pub const ATTACK_POWER_UP_SECONDS: f64 = 1.0;
/// Seconds for the power bar to fill when dual wielding.
pub const DUAL_WIELD_POWER_UP_SECONDS: f64 = 0.8;

/// Whether the character is in position to act in its combat mode: asked
/// before an attack is built, before the combat mode changes, and each
/// frame while a mode change waits.
///
/// Peace and magic are ready once no motion is queued. Melee needs the
/// combat maneuver table; a missile stance must be a bow-family stance
/// held at the ready command. `lenient` (the attack builders) accepts a
/// held melee or missile stance at once; otherwise the queued motions must
/// also have run out.
#[must_use]
pub fn player_in_ready_position(
    mode: CombatMode,
    current_style: u32,
    forward_command: u32,
    has_combat_table: bool,
    motions_pending: bool,
    lenient: bool,
) -> bool {
    match mode {
        CombatMode::NON_COMBAT | CombatMode::MAGIC => !motions_pending,
        CombatMode::MELEE => has_combat_table && (lenient || !motions_pending),
        CombatMode::MISSILE => {
            // The missile stances in the numbering the server and the game
            // data use: atlatl and thrown-weapon-with-shield sit three
            // higher there than in the older client's own table.
            let missile_stance = matches!(
                current_style,
                motion_command::BOW_COMBAT
                    | motion_command::CROSSBOW_COMBAT
                    | motion_command::SLING_COMBAT
                    | motion_command::THROWN_WEAPON_COMBAT
                    | motion_command::ATLATL_COMBAT
                    | motion_command::THROWN_SHIELD_COMBAT
            );
            forward_command == READY_FORWARD_COMMAND
                && missile_stance
                && (lenient || !motions_pending)
        }
        _ => false,
    }
}

/// The lenient form the attack builders use, for a character whose combat
/// table is known. Attacks are only built in melee or missile mode.
#[must_use]
pub fn player_in_ready_position_for_attack(
    mode: CombatMode,
    current_style: u32,
    forward_command: u32,
) -> bool {
    matches!(mode, CombatMode::MELEE | CombatMode::MISSILE)
        && player_in_ready_position(mode, current_style, forward_command, true, false, true)
}

/// What the default-mode rule reads from one item the player carries.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct CarriedItem {
    /// Where it is equipped (empty when not equipped).
    pub equipped_location: EquipMask,
    /// Combat use.
    pub combat_use: CombatUse,
    /// Item type.
    pub item_type: ItemType,
}

/// The default combat mode and, when that is peace because of something
/// held, the index of the held item that blocks combat.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DefaultCombatModeDecision {
    /// The mode.
    pub mode: CombatMode,
    /// Index (into the contents given) of a held item that is not a caster.
    pub incompatible_held_item: Option<usize>,
}

/// The mode combat starts in, from the player's contents in inventory
/// order: the first weapon (melee, missile or two-handed slot) gives
/// missile for a missile weapon and melee otherwise; else a held caster
/// gives magic and any other held item blocks combat; else unarmed melee.
#[must_use]
pub fn default_combat_mode_decision(ordered_contents: &[CarriedItem]) -> DefaultCombatModeDecision {
    let primary = EquipMask::MELEE_WEAPON | EquipMask::MISSILE_WEAPON | EquipMask::TWO_HANDED;
    if let Some(weapon) = ordered_contents
        .iter()
        .find(|item| item.equipped_location.intersects(primary))
    {
        let mode = if weapon.combat_use == CombatUse::MISSILE {
            CombatMode::MISSILE
        } else {
            CombatMode::MELEE
        };
        return DefaultCombatModeDecision {
            mode,
            incompatible_held_item: None,
        };
    }

    match ordered_contents
        .iter()
        .position(|item| item.equipped_location.intersects(EquipMask::HELD))
    {
        None => DefaultCombatModeDecision {
            mode: CombatMode::MELEE,
            incompatible_held_item: None,
        },
        Some(index)
            if ordered_contents[index]
                .item_type
                .intersects(ItemType::CASTER) =>
        {
            DefaultCombatModeDecision {
                mode: CombatMode::MAGIC,
                incompatible_held_item: None,
            }
        }
        Some(index) => DefaultCombatModeDecision {
            mode: CombatMode::NON_COMBAT,
            incompatible_held_item: Some(index),
        },
    }
}

/// [`default_combat_mode_decision`]'s mode.
#[must_use]
pub fn default_combat_mode(ordered_contents: &[CarriedItem]) -> CombatMode {
    default_combat_mode_decision(ordered_contents).mode
}

/// The combat-mode toggle: from any combat mode to peace, from peace to the
/// default combat mode.
#[must_use]
pub fn toggle_mode(current_mode: CombatMode, default_combat_mode: CombatMode) -> CombatMode {
    if current_mode == CombatMode::NON_COMBAT {
        default_combat_mode
    } else {
        CombatMode::NON_COMBAT
    }
}

/// True for the modes that attack a target (melee and missile).
#[must_use]
pub fn supports_targeted_attack(mode: CombatMode) -> bool {
    matches!(mode, CombatMode::MELEE | CombatMode::MISSILE)
}

/// The attack height an attack key asks for.
#[must_use]
pub fn height_for(action: CombatAttackAction) -> AttackHeight {
    match action {
        CombatAttackAction::Low => AttackHeight::LOW,
        CombatAttackAction::Medium => AttackHeight::MEDIUM,
        CombatAttackAction::High => AttackHeight::HIGH,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Combat/CombatInputPlannerTests.cs` and
    // `CombatStateTests.cs`.

    fn equipped(location: EquipMask, item_type: ItemType, combat_use: CombatUse) -> CarriedItem {
        CarriedItem {
            equipped_location: location,
            combat_use,
            item_type,
        }
    }

    #[test]
    fn wire_values() {
        assert_eq!(CombatMode::NON_COMBAT.bits(), 1);
        assert_eq!(CombatMode::MELEE.bits(), 2);
        assert_eq!(CombatMode::MISSILE.bits(), 4);
        assert_eq!(CombatMode::MAGIC.bits(), 8);
        assert_eq!(AttackType::PUNCH.bits(), 0x1);
        assert_eq!(AttackType::THRUST.bits(), 0x2);
        assert_eq!(AttackType::SLASH.bits(), 0x4);
        assert_eq!(AttackType::KICK.bits(), 0x8);
        assert_eq!(AttackType::OFFHAND_PUNCH.bits(), 0x10);
        assert_eq!(AttackType::MULTI_STRIKE.bits(), 0x79E0);
        assert_eq!(
            AttackType::UNARMED,
            AttackType::PUNCH | AttackType::KICK | AttackType::OFFHAND_PUNCH
        );
    }

    #[test]
    fn missile_combat_use_selects_missile() {
        let bow = equipped(
            EquipMask::MISSILE_WEAPON,
            ItemType::MISSILE_WEAPON,
            CombatUse::MISSILE,
        );
        assert_eq!(default_combat_mode(&[bow]), CombatMode::MISSILE);
    }

    #[test]
    fn primary_weapon_order_follows_inventory_placement() {
        let bow = equipped(
            EquipMask::MISSILE_WEAPON,
            ItemType::MISSILE_WEAPON,
            CombatUse::MISSILE,
        );
        let sword = equipped(
            EquipMask::MELEE_WEAPON,
            ItemType::MELEE_WEAPON,
            CombatUse::MELEE,
        );
        assert_eq!(default_combat_mode(&[bow, sword]), CombatMode::MISSILE);
        assert_eq!(default_combat_mode(&[sword, bow]), CombatMode::MELEE);
    }

    #[test]
    fn held_caster_selects_magic() {
        let wand = equipped(EquipMask::HELD, ItemType::CASTER, CombatUse::NONE);
        assert_eq!(default_combat_mode(&[wand]), CombatMode::MAGIC);
    }

    #[test]
    fn held_non_caster_cannot_enter_combat() {
        let held = equipped(EquipMask::HELD, ItemType::MISC, CombatUse::NONE);
        let decision = default_combat_mode_decision(&[CarriedItem::default(), held]);
        assert_eq!(decision.mode, CombatMode::NON_COMBAT);
        assert_eq!(decision.incompatible_held_item, Some(1));
    }

    #[test]
    fn no_weapon_defaults_to_unarmed_melee() {
        assert_eq!(default_combat_mode(&[]), CombatMode::MELEE);
    }

    #[test]
    fn toggle_from_peace_uses_default_mode() {
        assert_eq!(
            toggle_mode(CombatMode::NON_COMBAT, CombatMode::MELEE),
            CombatMode::MELEE
        );
        assert_eq!(
            toggle_mode(CombatMode::NON_COMBAT, CombatMode::MISSILE),
            CombatMode::MISSILE
        );
        assert_eq!(
            toggle_mode(CombatMode::NON_COMBAT, CombatMode::NON_COMBAT),
            CombatMode::NON_COMBAT
        );
    }

    #[test]
    fn toggle_from_combat_returns_peace() {
        for mode in [CombatMode::MELEE, CombatMode::MAGIC, CombatMode::UNDEF] {
            assert_eq!(toggle_mode(mode, CombatMode::MELEE), CombatMode::NON_COMBAT);
        }
    }

    #[test]
    fn attack_keys_map_to_heights() {
        assert_eq!(height_for(CombatAttackAction::Low), AttackHeight::LOW);
        assert_eq!(height_for(CombatAttackAction::Medium), AttackHeight::MEDIUM);
        assert_eq!(height_for(CombatAttackAction::High), AttackHeight::HIGH);
    }

    #[test]
    fn targeted_attack_modes() {
        assert!(supports_targeted_attack(CombatMode::MELEE));
        assert!(supports_targeted_attack(CombatMode::MISSILE));
        assert!(!supports_targeted_attack(CombatMode::NON_COMBAT));
        assert!(!supports_targeted_attack(CombatMode::MAGIC));
    }

    #[test]
    fn ready_position_every_arm() {
        const R: u32 = READY_FORWARD_COMMAND;
        let cases = [
            (CombatMode::MAGIC, 0x8000_003D, 0, true, false, false, true),
            (CombatMode::MAGIC, 0x8000_003D, 0, true, true, false, false),
            (CombatMode::MAGIC, 0x8000_003D, 0, true, true, true, false),
            (
                CombatMode::NON_COMBAT,
                0x8000_003D,
                0,
                true,
                false,
                false,
                true,
            ),
            (
                CombatMode::NON_COMBAT,
                0x8000_003D,
                0,
                true,
                true,
                true,
                false,
            ),
            (CombatMode::MELEE, 0x8000_003D, 0, false, false, true, false),
            (CombatMode::MELEE, 0x8000_003D, 0, true, true, true, true),
            (CombatMode::MELEE, 0x8000_003D, 0, true, true, false, false),
            (CombatMode::MELEE, 0x8000_003D, 0, true, false, false, true),
            (CombatMode::MISSILE, 0x8000_003F, R, true, true, true, true),
            (
                CombatMode::MISSILE,
                0x8000_003F,
                R,
                true,
                true,
                false,
                false,
            ),
            (
                CombatMode::MISSILE,
                0x8000_003F,
                R,
                true,
                false,
                false,
                true,
            ),
            (
                CombatMode::MISSILE,
                0x8000_003D,
                R,
                true,
                false,
                true,
                false,
            ),
            (
                CombatMode::MISSILE,
                0x8000_003F,
                0x4100_0006,
                true,
                false,
                true,
                false,
            ),
        ];
        for (mode, style, forward, table, pending, lenient, expected) in cases {
            assert_eq!(
                player_in_ready_position(mode, style, forward, table, pending, lenient),
                expected,
                "{mode:?} {style:#X} {forward:#X} {table} {pending} {lenient}"
            );
        }
    }

    #[test]
    fn every_missile_stance_in_server_numbering_is_ready() {
        for (style, expected) in [
            (0x8000_003F, true),
            (0x8000_0041, true),
            (0x8000_0043, true),
            (0x8000_0047, true),
            (0x8000_013B, true),
            (0x8000_013C, true),
            (0x8000_0138, false),
            (0x8000_0139, false),
        ] {
            assert_eq!(
                player_in_ready_position(
                    CombatMode::MISSILE,
                    style,
                    READY_FORWARD_COMMAND,
                    true,
                    false,
                    false
                ),
                expected,
                "{style:#X}"
            );
        }
    }

    #[test]
    fn ready_for_attack_branch() {
        const R: u32 = READY_FORWARD_COMMAND;
        for (mode, style, forward, expected) in [
            (CombatMode::MELEE, 0x8000_003D, 0, true),
            (CombatMode::MISSILE, 0x8000_003F, R, true),
            (CombatMode::MISSILE, 0x8000_0041, R, true),
            (CombatMode::MISSILE, 0x8000_013C, R, true),
            (CombatMode::MISSILE, 0x8000_013B, R, true),
            (CombatMode::MISSILE, 0x8000_003D, R, false),
            (CombatMode::MISSILE, 0x8000_003F, 0x4100_0006, false),
            (CombatMode::NON_COMBAT, 0x8000_003D, R, false),
        ] {
            assert_eq!(
                player_in_ready_position_for_attack(mode, style, forward),
                expected,
                "{mode:?} {style:#X}"
            );
        }
    }
}
