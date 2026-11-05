//! Which kind of combat animation a motion command is: stance, melee
//! swing, missile shot, creature attack, spell cast, hit reaction, death.
//!
//! Turning a 16-bit wire command into a full motion command and routing it
//! to the animation sequencer belong to the motion code; this module only
//! classifies full commands.

/// Full motion command values (the numbering the server and the game data
/// use) for the combat stances and combat actions.
pub mod motion_command {
    /// Bare-handed stance.
    pub const HAND_COMBAT: u32 = 0x8000_003C;
    /// Peace stance.
    pub const NON_COMBAT: u32 = 0x8000_003D;
    /// Sword stance.
    pub const SWORD_COMBAT: u32 = 0x8000_003E;
    /// Bow stance.
    pub const BOW_COMBAT: u32 = 0x8000_003F;
    /// Sword-and-shield stance.
    pub const SWORD_SHIELD_COMBAT: u32 = 0x8000_0040;
    /// Crossbow stance.
    pub const CROSSBOW_COMBAT: u32 = 0x8000_0041;
    /// Sling stance.
    pub const SLING_COMBAT: u32 = 0x8000_0043;
    /// Two-handed sword stance.
    pub const TWO_HANDED_SWORD_COMBAT: u32 = 0x8000_0044;
    /// Two-handed staff stance.
    pub const TWO_HANDED_STAFF_COMBAT: u32 = 0x8000_0045;
    /// Dual-wield stance.
    pub const DUAL_WIELD_COMBAT: u32 = 0x8000_0046;
    /// Thrown-weapon stance.
    pub const THROWN_WEAPON_COMBAT: u32 = 0x8000_0047;
    /// Magic stance.
    pub const MAGIC: u32 = 0x8000_0049;
    /// Atlatl stance.
    pub const ATLATL_COMBAT: u32 = 0x8000_013B;
    /// Thrown-weapon-and-shield stance.
    pub const THROWN_SHIELD_COMBAT: u32 = 0x8000_013C;

    /// The "ready" forward command.
    pub const READY: u32 = 0x4100_0003;
    /// Death.
    pub const DEAD: u32 = 0x4000_0011;
    /// Missile reload.
    pub const RELOAD: u32 = 0x4000_0016;
    /// Spell cast.
    pub const CAST_SPELL: u32 = 0x4000_00D3;
    /// Cast with a staff.
    pub const USE_MAGIC_STAFF: u32 = 0x4000_00E0;
    /// Cast with a wand.
    pub const USE_MAGIC_WAND: u32 = 0x4000_00E1;

    /// Fall down.
    pub const FALL_DOWN: u32 = 0x1000_0050;
    /// Twitches 1-4 (0x10000051..=0x10000054).
    pub const TWITCH1: u32 = 0x1000_0051;
    /// Twitch 4.
    pub const TWITCH4: u32 = 0x1000_0054;
    /// Stagger backward.
    pub const STAGGER_BACKWARD: u32 = 0x1000_0055;
    /// Stagger forward.
    pub const STAGGER_FORWARD: u32 = 0x1000_0056;
    /// Sanctuary.
    pub const SANCTUARY: u32 = 0x1000_0057;
    /// Thrust (medium).
    pub const THRUST_MED: u32 = 0x1000_0058;
    /// Slash (high).
    pub const SLASH_HIGH: u32 = 0x1000_005B;
    /// Slash (medium).
    pub const SLASH_MED: u32 = 0x1000_005C;
    /// Backhand (high).
    pub const BACKHAND_HIGH: u32 = 0x1000_005E;
    /// Backhand (medium).
    pub const BACKHAND_MED: u32 = 0x1000_005F;
    /// Backhand (low), the last of the swing block 0x10000058..=0x10000060.
    pub const BACKHAND_LOW: u32 = 0x1000_0060;
    /// Missile shot.
    pub const SHOOT: u32 = 0x1000_0061;
    /// Creature attacks, high/med/low for sets 1-3 (0x10000062..=0x1000006A).
    pub const ATTACK_HIGH1: u32 = 0x1000_0062;
    /// Low attack, set 1.
    pub const ATTACK_LOW1: u32 = 0x1000_0064;
    /// Low attack, set 2.
    pub const ATTACK_LOW2: u32 = 0x1000_0067;
    /// Low attack, set 3.
    pub const ATTACK_LOW3: u32 = 0x1000_006A;
    /// Missile attacks 1-3 (0x100000D0..=0x100000D2).
    pub const MISSILE_ATTACK1: u32 = 0x1000_00D0;
    /// Missile attack 3.
    pub const MISSILE_ATTACK3: u32 = 0x1000_00D2;
    /// Double/triple slash and thrust, low/med/high (0x1000011F..=0x1000012A).
    pub const DOUBLE_SLASH_LOW: u32 = 0x1000_011F;
    /// Triple thrust (high).
    pub const TRIPLE_THRUST_HIGH: u32 = 0x1000_012A;
    /// Off-hand swings and the off-hand kick (0x10000173..=0x10000185).
    pub const OFFHAND_SLASH_HIGH: u32 = 0x1000_0173;
    /// Off-hand kick.
    pub const OFFHAND_KICK: u32 = 0x1000_0185;
    /// Creature attacks, sets 4-6 (0x10000186..=0x1000018E).
    pub const ATTACK_HIGH4: u32 = 0x1000_0186;
    /// Low attack, set 6.
    pub const ATTACK_LOW6: u32 = 0x1000_018E;
    /// Punches, main and off hand (0x1000018F..=0x1000019A).
    pub const PUNCH_FAST_HIGH: u32 = 0x1000_018F;
    /// Off-hand slow punch (low).
    pub const OFFHAND_PUNCH_SLOW_LOW: u32 = 0x1000_019A;
}

/// What kind of combat animation a command is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum CombatAnimationKind {
    /// Not a combat animation.
    #[default]
    None,
    /// A combat stance.
    CombatStance,
    /// A melee swing, punch or kick.
    MeleeSwing,
    /// A missile shot or reload.
    MissileAttack,
    /// A creature's attack.
    CreatureAttack,
    /// A spell cast.
    SpellCast,
    /// A hit reaction.
    HitReaction,
    /// Death.
    Death,
}

/// The combat notifications the server sends.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum CombatAnimationEvent {
    /// The server accepted an attack.
    CombatCommenceAttack,
    /// An attack finished.
    AttackDone,
    /// You hit something.
    AttackerNotification,
    /// Something hit you.
    DefenderNotification,
    /// Your target evaded.
    EvasionAttackerNotification,
    /// You evaded.
    EvasionDefenderNotification,
    /// You were the victim of a kill.
    VictimNotification,
    /// You killed something.
    KillerNotification,
}

/// The animation a combat notification plays: none. The game animates
/// combat only from the motion commands the server sends; notifications
/// carry no animation of their own.
#[must_use]
pub fn animation_for_event(_event: CombatAnimationEvent) -> CombatAnimationKind {
    CombatAnimationKind::None
}

/// Classifies a full motion command.
#[must_use]
pub fn classify_motion_command(full_command: u32) -> CombatAnimationKind {
    use motion_command as m;
    match full_command {
        m::HAND_COMBAT
        | m::SWORD_COMBAT
        | m::SWORD_SHIELD_COMBAT
        | m::TWO_HANDED_SWORD_COMBAT
        | m::TWO_HANDED_STAFF_COMBAT
        | m::BOW_COMBAT
        | m::CROSSBOW_COMBAT
        | m::SLING_COMBAT
        | m::DUAL_WIELD_COMBAT
        | m::THROWN_WEAPON_COMBAT
        | m::ATLATL_COMBAT
        | m::THROWN_SHIELD_COMBAT
        | m::MAGIC => CombatAnimationKind::CombatStance,

        m::THRUST_MED..=m::BACKHAND_LOW
        | m::DOUBLE_SLASH_LOW..=m::TRIPLE_THRUST_HIGH
        | m::OFFHAND_SLASH_HIGH..=m::OFFHAND_KICK
        | m::PUNCH_FAST_HIGH..=m::OFFHAND_PUNCH_SLOW_LOW => CombatAnimationKind::MeleeSwing,

        m::SHOOT | m::MISSILE_ATTACK1..=m::MISSILE_ATTACK3 | m::RELOAD => {
            CombatAnimationKind::MissileAttack
        }

        m::ATTACK_HIGH1..=m::ATTACK_LOW3 | m::ATTACK_HIGH4..=m::ATTACK_LOW6 => {
            CombatAnimationKind::CreatureAttack
        }

        m::CAST_SPELL | m::USE_MAGIC_STAFF | m::USE_MAGIC_WAND => CombatAnimationKind::SpellCast,

        m::FALL_DOWN..=m::SANCTUARY => CombatAnimationKind::HitReaction,

        m::DEAD => CombatAnimationKind::Death,

        _ => CombatAnimationKind::None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Golden values from OpenAC
    // `tests/AcDream.Core.Tests/Combat/CombatAnimationPlannerTests.cs`.

    #[test]
    fn classifies_combat_commands() {
        for (command, expected) in [
            (0x1000_0058, CombatAnimationKind::MeleeSwing),
            (0x1000_005B, CombatAnimationKind::MeleeSwing),
            (0x1000_017D, CombatAnimationKind::MeleeSwing),
            (0x1000_018E, CombatAnimationKind::CreatureAttack),
            (0x1000_0061, CombatAnimationKind::MissileAttack),
            (0x4000_0016, CombatAnimationKind::MissileAttack),
            (0x1000_0062, CombatAnimationKind::CreatureAttack),
            (0x1000_018B, CombatAnimationKind::CreatureAttack),
            (0x4000_00D3, CombatAnimationKind::SpellCast),
            (0x4000_00E0, CombatAnimationKind::SpellCast),
            (0x1000_0051, CombatAnimationKind::HitReaction),
            (0x1000_0055, CombatAnimationKind::HitReaction),
            (0x4000_0011, CombatAnimationKind::Death),
            (0x8000_003E, CombatAnimationKind::CombatStance),
            (0x8000_0043, CombatAnimationKind::CombatStance),
            (0x8000_0044, CombatAnimationKind::CombatStance),
        ] {
            assert_eq!(
                classify_motion_command(command),
                expected,
                "{command:#010X}"
            );
        }
    }

    #[test]
    fn late_combat_block_uses_server_numbering() {
        for (command, expected) in [
            (0x1000_0173, CombatAnimationKind::MeleeSwing),
            (0x1000_0175, CombatAnimationKind::MeleeSwing),
            (0x1000_0176, CombatAnimationKind::MeleeSwing),
            (0x1000_017E, CombatAnimationKind::MeleeSwing),
            (0x1000_0182, CombatAnimationKind::MeleeSwing),
            (0x1000_0185, CombatAnimationKind::MeleeSwing),
            (0x1000_0186, CombatAnimationKind::CreatureAttack),
            (0x1000_0188, CombatAnimationKind::CreatureAttack),
            (0x1000_018C, CombatAnimationKind::CreatureAttack),
            (0x1000_018E, CombatAnimationKind::CreatureAttack),
            (0x1000_018F, CombatAnimationKind::MeleeSwing),
            (0x1000_0191, CombatAnimationKind::MeleeSwing),
            (0x1000_0197, CombatAnimationKind::MeleeSwing),
            (0x1000_019A, CombatAnimationKind::MeleeSwing),
        ] {
            assert_eq!(
                classify_motion_command(command),
                expected,
                "{command:#010X}"
            );
        }
    }

    #[test]
    fn non_combat_commands_are_none() {
        for command in [
            0,
            0xFFFF,
            0x0900_0170,
            0x8000_003D,
            0x1000_0057 + 0x200,
            0x4100_0003,
        ] {
            assert_eq!(classify_motion_command(command), CombatAnimationKind::None);
        }
        // The ranges stop exactly where the named blocks stop.
        assert_eq!(
            classify_motion_command(0x1000_006B),
            CombatAnimationKind::None
        );
        assert_eq!(
            classify_motion_command(0x1000_011E),
            CombatAnimationKind::None
        );
        assert_eq!(
            classify_motion_command(0x1000_012B),
            CombatAnimationKind::None
        );
        assert_eq!(
            classify_motion_command(0x1000_0172),
            CombatAnimationKind::None
        );
        assert_eq!(
            classify_motion_command(0x1000_019B),
            CombatAnimationKind::None
        );
        assert_eq!(
            classify_motion_command(0x1000_004F),
            CombatAnimationKind::None
        );
    }

    #[test]
    fn notifications_carry_no_animation() {
        for event in [
            CombatAnimationEvent::CombatCommenceAttack,
            CombatAnimationEvent::AttackDone,
            CombatAnimationEvent::AttackerNotification,
            CombatAnimationEvent::DefenderNotification,
            CombatAnimationEvent::EvasionAttackerNotification,
            CombatAnimationEvent::EvasionDefenderNotification,
            CombatAnimationEvent::VictimNotification,
            CombatAnimationEvent::KillerNotification,
        ] {
            assert_eq!(animation_for_event(event), CombatAnimationKind::None);
        }
    }
}
