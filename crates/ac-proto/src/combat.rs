//! Combat: attacks, combat mode, health queries, the attack and defense
//! notices, and deaths.
//!
//! The evasion and death notices are a single string each; read them with
//! [`crate::chat::parse_string_event`]. Game-action builders take the
//! session's next action sequence number and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, bare_action, read_string, u32_action};
use crate::{GameActionType, Opcode, ProtoError, Result};

/// Combat mode bits.
pub mod combat_mode {
    /// Not set.
    pub const UNDEF: u32 = 0;
    /// Peace.
    pub const NON_COMBAT: u32 = 0x01;
    /// Melee.
    pub const MELEE: u32 = 0x02;
    /// Missile.
    pub const MISSILE: u32 = 0x04;
    /// Magic.
    pub const MAGIC: u32 = 0x08;
    /// Every valid mode.
    pub const VALID_COMBAT: u32 = NON_COMBAT | MELEE | MISSILE | MAGIC;
    /// Every fighting mode.
    pub const COMBAT_COMBAT: u32 = MELEE | MISSILE | MAGIC;
}

/// Change combat mode (see [`combat_mode`]).
#[must_use]
pub fn build_change_combat_mode(sequence: u32, mode: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::CHANGE_COMBAT_MODE, mode)
}

/// Melee attack: target, attack height, power (0 to 1).
#[must_use]
pub fn build_targeted_melee_attack(
    sequence: u32,
    target_guid: u32,
    attack_height: u32,
    power_level: f32,
) -> Vec<u8> {
    attack(
        sequence,
        GameActionType::TARGETED_MELEE_ATTACK,
        target_guid,
        attack_height,
        power_level,
    )
}

/// Missile attack: target, attack height, accuracy (0 to 1).
#[must_use]
pub fn build_targeted_missile_attack(
    sequence: u32,
    target_guid: u32,
    attack_height: u32,
    accuracy_level: f32,
) -> Vec<u8> {
    attack(
        sequence,
        GameActionType::TARGETED_MISSILE_ATTACK,
        target_guid,
        attack_height,
        accuracy_level,
    )
}

fn attack(
    sequence: u32,
    action_type: GameActionType,
    target: u32,
    height: u32,
    level: f32,
) -> Vec<u8> {
    let mut w = action(sequence, action_type);
    w.u32(target);
    w.u32(height);
    w.f32(level);
    w.into_vec()
}

/// Stop the current attack.
#[must_use]
pub fn build_cancel_attack(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::CANCEL_ATTACK)
}

/// Ask for a target's health; the server answers with
/// [`UpdateHealth`].
#[must_use]
pub fn build_query_health(sequence: u32, target_guid: u32) -> Vec<u8> {
    u32_action(sequence, GameActionType::QUERY_HEALTH, target_guid)
}

/// A target's health ([`crate::GameEventType::UPDATE_HEALTH`]).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct UpdateHealth {
    /// The target.
    pub target_guid: u32,
    /// Health as a fraction, 0 to 1.
    pub health_percent: f32,
}

impl UpdateHealth {
    /// Parse the event payload: target, fraction.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            target_guid: r.u32()?,
            health_percent: r.f32()?,
        })
    }
}

/// The player hit something ([`crate::GameEventType::ATTACKER_NOTIFICATION`]).
#[derive(Debug, Clone, PartialEq)]
pub struct AttackerNotification {
    /// Who was hit.
    pub defender_name: String,
    /// Damage type bits.
    pub damage_type: u32,
    /// The defender's health afterwards, 0 to 1.
    pub health_percent: f64,
    /// Damage dealt.
    pub damage: u32,
    /// Non-zero for a critical hit.
    pub critical: u32,
    /// Attack condition bits.
    pub attack_conditions: u64,
}

impl AttackerNotification {
    /// Parse the event payload: name, damage type, health fraction,
    /// damage, critical word, condition bits.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            defender_name: read_string(&mut r)?,
            damage_type: r.u32()?,
            health_percent: r.f64()?,
            damage: r.u32()?,
            critical: r.u32()?,
            attack_conditions: r.u64()?,
        })
    }
}

/// Something hit the player ([`crate::GameEventType::DEFENDER_NOTIFICATION`]).
#[derive(Debug, Clone, PartialEq)]
pub struct DefenderNotification {
    /// Who hit.
    pub attacker_name: String,
    /// Damage type bits.
    pub damage_type: u32,
    /// The player's health afterwards, 0 to 1.
    pub health_percent: f64,
    /// Damage taken.
    pub damage: u32,
    /// Body part hit.
    pub hit_quadrant: u32,
    /// Non-zero for a critical hit.
    pub critical: u32,
    /// Attack condition bits.
    pub attack_conditions: u64,
}

impl DefenderNotification {
    /// Parse the event payload: name, damage type, health fraction,
    /// damage, hit quadrant, critical word, condition bits.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            attacker_name: read_string(&mut r)?,
            damage_type: r.u32()?,
            health_percent: r.f64()?,
            damage: r.u32()?,
            hit_quadrant: r.u32()?,
            critical: r.u32()?,
            attack_conditions: r.u64()?,
        })
    }
}

/// The error code of an attack-done event
/// ([`crate::GameEventType::ATTACK_DONE`]); 0 when the attack finished.
pub fn parse_attack_done(payload: &[u8]) -> Result<u32> {
    Ok(Reader::new(payload).u32()?)
}

/// Check a commence-attack event
/// ([`crate::GameEventType::COMBAT_COMMENCE_ATTACK`]): it has no payload.
pub fn parse_combat_commence_attack(payload: &[u8]) -> Result<()> {
    if payload.is_empty() {
        Ok(())
    } else {
        Err(ProtoError::InvalidLength {
            what: "commence attack",
            actual: payload.len(),
        })
    }
}

/// A player died nearby ([`Opcode::PlayerKilled`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlayerKilled {
    /// The death message.
    pub death_message: String,
    /// Who died.
    pub victim_guid: u32,
    /// Who killed them.
    pub killer_guid: u32,
}

impl PlayerKilled {
    /// Parse the body: opcode, message, victim, killer.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::PlayerKilled)?;
        Ok(Self {
            death_message: read_string(&mut r)?,
            victim_guid: r.u32()?,
            killer_guid: r.u32()?,
        })
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `CombatEventTests.cs` (captured event fixtures),
    //! `PlayerKilledTests.cs`, `CharacterActionsTests.cs` (combat mode) and
    //! the body shapes of `WorldSessionCombatTests.cs`.

    use ac_bits::Writer;

    use super::*;
    use crate::chat::parse_string_event;
    use crate::test_util::{assert_every_prefix_fails, words};
    use crate::{GameEventEnvelope, GameEventType};

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    #[test]
    fn attack_requests() {
        let melee = build_targeted_melee_attack(3, 0x1234_5678, 2, 0.75);
        assert_eq!(melee[..20], words(&[0xF7B1, 3, 0x08, 0x1234_5678, 2]));
        assert_eq!(melee[20..], 0.75f32.to_le_bytes());
        let missile = build_targeted_missile_attack(4, 0x8765_4321, 1, 0.5);
        assert_eq!(missile[..20], words(&[0xF7B1, 4, 0x0A, 0x8765_4321, 1]));
        assert_eq!(missile[20..], 0.5f32.to_le_bytes());
        assert_eq!(build_cancel_attack(5), words(&[0xF7B1, 5, 0x01B7]));
        assert_eq!(
            build_change_combat_mode(1, combat_mode::MELEE),
            words(&[0xF7B1, 1, 0x53, 2])
        );
        assert_eq!(
            [
                combat_mode::NON_COMBAT,
                combat_mode::MELEE,
                combat_mode::MISSILE,
                combat_mode::MAGIC
            ],
            [1, 2, 4, 8]
        );
        assert_eq!(
            build_query_health(3, 0xCAFE),
            words(&[0xF7B1, 3, 0x01BF, 0xCAFE])
        );
    }

    #[test]
    fn captured_attack_done_and_commence() {
        let body = hex("B0F700000000000000000000A701000036000000");
        let env = GameEventEnvelope::parse(&body).unwrap();
        assert_eq!(env.event_type, GameEventType::ATTACK_DONE);
        assert_eq!(parse_attack_done(env.payload), Ok(0x36));
        let body = hex("B0F700000000000005000000B8010000");
        let env = GameEventEnvelope::parse(&body).unwrap();
        assert_eq!(env.event_type, GameEventType::COMBAT_COMMENCE_ATTACK);
        assert_eq!(parse_combat_commence_attack(env.payload), Ok(()));
        assert!(parse_combat_commence_attack(&[0]).is_err());
    }

    #[test]
    fn captured_attacker_notification() {
        let body = hex(concat!(
            "B0F700000000000001000000B10100000E0044727564676520526176656E6572",
            "01000000000000000000D03F25000000010000000600000000000000"
        ));
        let env = GameEventEnvelope::parse(&body).unwrap();
        let parsed = AttackerNotification::parse(env.payload).unwrap();
        assert_eq!(parsed.defender_name, "Drudge Ravener");
        assert_eq!(parsed.damage_type, 1);
        assert_eq!(parsed.health_percent, 0.25);
        assert_eq!(parsed.damage, 37);
        assert_eq!(parsed.critical, 1);
        assert_eq!(parsed.attack_conditions, 6);
        assert_every_prefix_fails(env.payload, AttackerNotification::parse);
    }

    #[test]
    fn captured_defender_notification() {
        let body = hex(concat!(
            "B0F700000000000002000000B20100000A0042616E6465726C696E6710000000",
            "000000000000C03F1200000001000000000000000800000000000000"
        ));
        let env = GameEventEnvelope::parse(&body).unwrap();
        let parsed = DefenderNotification::parse(env.payload).unwrap();
        assert_eq!(parsed.attacker_name, "Banderling");
        assert_eq!(parsed.damage_type, 0x10);
        assert_eq!(parsed.health_percent, 0.125);
        assert_eq!(parsed.damage, 18);
        assert_eq!(parsed.hit_quadrant, 1);
        assert_eq!(parsed.critical, 0);
        assert_eq!(parsed.attack_conditions, 8);
        assert_every_prefix_fails(env.payload, DefenderNotification::parse);
    }

    #[test]
    fn captured_evasion_and_death_notices() {
        for (fixture, text) in [
            (
                "B0F700000000000003000000B301000008004D6F7373776172740000",
                "Mosswart",
            ),
            ("B0F700000000000004000000B401000004004D6974650000", "Mite"),
            (
                "B0F700000000000006000000AC0100000E00596F752068617665206469656421",
                "You have died!",
            ),
            (
                "B0F700000000000007000000AD0100001600596F75206B696C6C6564207468652064727564676521",
                "You killed the drudge!",
            ),
        ] {
            let body = hex(fixture);
            let env = GameEventEnvelope::parse(&body).unwrap();
            assert_eq!(parse_string_event(env.payload).as_deref(), Ok(text));
        }
    }

    #[test]
    fn update_health() {
        let mut w = Writer::new();
        w.u32(0xC0DE);
        w.f32(0.42);
        let parsed = UpdateHealth::parse(w.as_slice()).unwrap();
        assert_eq!(parsed.target_guid, 0xC0DE);
        assert_eq!(parsed.health_percent, 0.42);
        assert_every_prefix_fails(w.as_slice(), UpdateHealth::parse);
    }

    #[test]
    fn player_killed() {
        let mut w = Writer::new();
        w.u32(0x019E);
        w.string16l("Test").unwrap();
        w.u32(0x1234_5678);
        w.u32(0x90AB_CDEF);
        assert_eq!(
            PlayerKilled::parse(w.as_slice()),
            Ok(PlayerKilled {
                death_message: "Test".into(),
                victim_guid: 0x1234_5678,
                killer_guid: 0x90AB_CDEF
            })
        );
        assert_every_prefix_fails(w.as_slice(), PlayerKilled::parse);
        assert!(matches!(
            PlayerKilled::parse(&words(&[0xDEAD_BEEF, 0, 0, 0])),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }
}
