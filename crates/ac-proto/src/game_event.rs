use ac_bits::Reader;

use crate::{Opcode, ProtoError, Result, expect_opcode};

/// Bytes before an event's payload: opcode, player guid, sequence, type.
const HEADER_SIZE: usize = 16;

/// A server game event: the [`Opcode::GameEvent`] envelope around one event
/// addressed to the player. The payload is parsed by the event's own type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GameEventEnvelope<'a> {
    /// The player the event is for.
    pub player_guid: u32,
    /// The server's game-event sequence number.
    pub sequence: u32,
    /// Which event.
    pub event_type: GameEventType,
    /// The event's payload, borrowed from the body.
    pub payload: &'a [u8],
}

impl<'a> GameEventEnvelope<'a> {
    /// Split a game-event body into its header fields and payload.
    pub fn parse(body: &'a [u8]) -> Result<Self> {
        if body.len() < HEADER_SIZE {
            return Err(ProtoError::InvalidLength {
                what: "game event",
                actual: body.len(),
            });
        }
        let mut r = Reader::new(body);
        expect_opcode(&mut r, Opcode::GameEvent)?;
        let player_guid = r.u32()?;
        let sequence = r.u32()?;
        let event_type = GameEventType(r.u32()?);
        Ok(Self {
            player_guid,
            sequence,
            event_type,
            payload: r.peek_rest(),
        })
    }
}

macro_rules! game_event_types {
    ($($name:ident = $value:literal,)*) => {
        impl GameEventType {
            $(
                #[doc = concat!("`", stringify!($value), "`")]
                pub const $name: Self = Self($value);
            )*

            /// The event's name, or `None` for a type outside the table.
            #[must_use]
            pub fn name(self) -> Option<&'static str> {
                match self {
                    $(Self::$name => Some(stringify!($name)),)*
                    _ => None,
                }
            }
        }
    };
}

/// The event type inside a [`Opcode::GameEvent`] envelope.
///
/// An open set: any `u32` is a valid event type (unknown ones are counted
/// and skipped by the dispatcher); the constants name the known ones.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct GameEventType(pub u32);

game_event_types! {
    ALLEGIANCE_UPDATE_ABORTED = 0x0003,
    POPUP_STRING = 0x0004,
    PLAYER_DESCRIPTION = 0x0013,
    ALLEGIANCE_UPDATE = 0x0020,
    FRIENDS_LIST_UPDATE = 0x0021,
    INVENTORY_PUT_OBJ_IN_CONTAINER = 0x0022,
    WIELD_OBJECT = 0x0023,
    CHARACTER_TITLE = 0x0029,
    UPDATE_TITLE = 0x002B,
    CLOSE_GROUND_CONTAINER = 0x0052,
    APPROACH_VENDOR = 0x0062,
    START_BARBER = 0x0075,
    INVENTORY_SERVER_SAVE_FAILED = 0x00A0,
    FELLOWSHIP_QUIT = 0x00A3,
    FELLOWSHIP_DISMISS = 0x00A4,
    BOOK_DATA_RESPONSE = 0x00B4,
    BOOK_MODIFY_PAGE_RESPONSE = 0x00B5,
    BOOK_ADD_PAGE_RESPONSE = 0x00B6,
    BOOK_DELETE_PAGE_RESPONSE = 0x00B7,
    BOOK_PAGE_DATA_RESPONSE = 0x00B8,
    GET_INSCRIPTION_RESPONSE = 0x00C3,
    IDENTIFY_OBJECT_RESPONSE = 0x00C9,
    CHANNEL_BROADCAST = 0x0147,
    CHANNEL_LIST = 0x0148,
    CHANNEL_INDEX = 0x0149,
    VIEW_CONTENTS = 0x0196,
    INVENTORY_PUT_OBJECT_IN_3D = 0x019A,
    ATTACK_DONE = 0x01A7,
    MAGIC_REMOVE_SPELL = 0x01A8,
    VICTIM_NOTIFICATION = 0x01AC,
    KILLER_NOTIFICATION = 0x01AD,
    ATTACKER_NOTIFICATION = 0x01B1,
    DEFENDER_NOTIFICATION = 0x01B2,
    EVASION_ATTACKER_NOTIFICATION = 0x01B3,
    EVASION_DEFENDER_NOTIFICATION = 0x01B4,
    COMBAT_COMMENCE_ATTACK = 0x01B8,
    UPDATE_HEALTH = 0x01C0,
    QUERY_AGE_RESPONSE = 0x01C3,
    USE_DONE = 0x01C7,
    ALLEGIANCE_UPDATE_DONE = 0x01C8,
    FELLOWSHIP_FELLOW_UPDATE_DONE = 0x01C9,
    FELLOWSHIP_FELLOW_STATS_DONE = 0x01CA,
    ITEM_APPRAISE_DONE = 0x01CB,
    EMOTE = 0x01E2,
    PING_RESPONSE = 0x01EA,
    SET_SQUELCH_DB = 0x01F4,
    REGISTER_TRADE = 0x01FD,
    OPEN_TRADE = 0x01FE,
    CLOSE_TRADE = 0x01FF,
    ADD_TO_TRADE = 0x0200,
    REMOVE_FROM_TRADE = 0x0201,
    ACCEPT_TRADE = 0x0202,
    DECLINE_TRADE = 0x0203,
    RESET_TRADE = 0x0205,
    TRADE_FAILURE = 0x0207,
    CLEAR_TRADE_ACCEPTANCE = 0x0208,
    HOUSE_PROFILE = 0x021D,
    HOUSE_DATA = 0x0225,
    HOUSE_STATUS = 0x0226,
    UPDATE_RENT_TIME = 0x0227,
    UPDATE_RENT_PAYMENT = 0x0228,
    HOUSE_UPDATE_RESTRICTIONS = 0x0248,
    UPDATE_HAR = 0x0257,
    HOUSE_TRANSACTION = 0x0259,
    QUERY_ITEM_MANA_RESPONSE = 0x0264,
    AVAILABLE_HOUSES = 0x0271,
    CHARACTER_CONFIRMATION_REQUEST = 0x0274,
    CHARACTER_CONFIRMATION_DONE = 0x0276,
    ALLEGIANCE_LOGIN_NOTIFICATION = 0x027A,
    ALLEGIANCE_INFO_RESPONSE = 0x027C,
    JOIN_GAME_RESPONSE = 0x0281,
    START_GAME = 0x0282,
    MOVE_RESPONSE = 0x0283,
    OPPONENT_TURN = 0x0284,
    OPPONENT_STALEMATE = 0x0285,
    WEENIE_ERROR = 0x028A,
    WEENIE_ERROR_WITH_STRING = 0x028B,
    GAME_OVER = 0x028C,
    SET_TURBINE_CHAT_CHANNELS = 0x0295,
    ADMIN_QUERY_PLUGIN_LIST = 0x02AE,
    ADMIN_QUERY_PLUGIN = 0x02B1,
    ADMIN_QUERY_PLUGIN_RESPONSE = 0x02B3,
    SALVAGE_OPERATIONS_RESULT = 0x02B4,
    TELL = 0x02BD,
    FELLOWSHIP_FULL_UPDATE = 0x02BE,
    FELLOWSHIP_DISBAND = 0x02BF,
    FELLOWSHIP_UPDATE_FELLOW = 0x02C0,
    MAGIC_UPDATE_SPELL = 0x02C1,
    MAGIC_UPDATE_ENCHANTMENT = 0x02C2,
    MAGIC_REMOVE_ENCHANTMENT = 0x02C3,
    MAGIC_UPDATE_MULTIPLE_ENCHANTMENTS = 0x02C4,
    MAGIC_REMOVE_MULTIPLE_ENCHANTMENTS = 0x02C5,
    MAGIC_PURGE_ENCHANTMENTS = 0x02C6,
    MAGIC_DISPEL_ENCHANTMENT = 0x02C7,
    MAGIC_DISPEL_MULTIPLE_ENCHANTMENTS = 0x02C8,
    PORTAL_STORM_BREWING = 0x02C9,
    PORTAL_STORM_IMMINENT = 0x02CA,
    PORTAL_STORM = 0x02CB,
    PORTAL_STORM_SUBSIDED = 0x02CC,
    COMMUNICATION_TRANSIENT_STRING = 0x02EB,
    MAGIC_PURGE_BAD_ENCHANTMENTS = 0x0312,
    SEND_CLIENT_CONTRACT_TRACKER_TABLE = 0x0314,
    SEND_CLIENT_CONTRACT_TRACKER = 0x0315,
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/GameEventDispatcherTests.cs`
    //! (envelope parsing).

    use super::*;
    use crate::test_util::words;

    fn envelope(event: GameEventType, payload: &[u8], guid: u32, sequence: u32) -> Vec<u8> {
        let mut body = words(&[0xF7B0, guid, sequence, event.0]);
        body.extend_from_slice(payload);
        body
    }

    #[test]
    fn valid_envelope_returns_fields() {
        let body = envelope(GameEventType::PLAYER_DESCRIPTION, &[0; 8], 0xAABB_CCDD, 42);
        let env = GameEventEnvelope::parse(&body).unwrap();
        assert_eq!(env.player_guid, 0xAABB_CCDD);
        assert_eq!(env.sequence, 42);
        assert_eq!(env.event_type, GameEventType::PLAYER_DESCRIPTION);
        assert_eq!(env.event_type.0, 0x0013);
        assert_eq!(env.payload.len(), 8);
    }

    #[test]
    fn empty_payload_is_valid() {
        let body = envelope(GameEventType::HOUSE_PROFILE, &[], 0x1234_5678, 0);
        assert!(GameEventEnvelope::parse(&body).unwrap().payload.is_empty());
    }

    #[test]
    fn wrong_outer_opcode_is_an_error() {
        let body = words(&[0xDEAD_BEEF, 0, 0, 0]);
        assert!(matches!(
            GameEventEnvelope::parse(&body),
            Err(ProtoError::WrongOpcode { .. })
        ));
    }

    #[test]
    fn too_short_is_an_error() {
        let body = envelope(GameEventType::TELL, &[], 1, 2);
        for len in 0..HEADER_SIZE {
            assert!(GameEventEnvelope::parse(&body[..len]).is_err());
        }
    }

    #[test]
    fn unknown_types_are_kept_and_unnamed() {
        let body = envelope(GameEventType(0xFFFF), &[1], 1, 2);
        let env = GameEventEnvelope::parse(&body).unwrap();
        assert_eq!(env.event_type, GameEventType(0xFFFF));
        assert_eq!(env.event_type.name(), None);
        assert_eq!(GameEventType::TELL.name(), Some("TELL"));
    }
}
