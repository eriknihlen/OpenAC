//! Chat rooms: the name and text class a room's lines are shown with, and
//! the check before the player speaks in one.
//!
//! The client refuses locally, without sending anything, when rooms are off,
//! the room was never assigned, or the player is not listening to it. The
//! "hear" character options are room membership, not a display setting.

use ac_core::chat::{LogTextType, TurbineChannel, TurbineChatState, client_text, weenie_errors};
use ac_proto::turbine_chat::chat_type;

/// Character option word 1: hear the allegiance room.
pub const HEAR_ALLEGIANCE_CHAT: u32 = 0x4000_0000;
/// Character option word 2: hear General.
pub const HEAR_GENERAL_CHAT: u32 = 0x0000_0100;
/// Character option word 2: hear Trade.
pub const HEAR_TRADE_CHAT: u32 = 0x0000_0200;
/// Character option word 2: hear LFG.
pub const HEAR_LFG_CHAT: u32 = 0x0000_0400;
/// Character option word 2: hear Roleplay.
pub const HEAR_ROLEPLAY_CHAT: u32 = 0x0000_0800;
/// Character option word 2: hear the society room.
pub const HEAR_SOCIETY_CHAT: u32 = 0x0008_0000;

/// Option word 1 before the server's description arrives.
pub const DEFAULT_OPTIONS1: u32 = 0x50C4_A54A;
/// Option word 2 before the server's description arrives.
pub const DEFAULT_OPTIONS2: u32 = 0x0094_8700;

/// The name a room's lines are shown under.
#[must_use]
pub fn display_name(room_id: u32, chat_type: u32) -> String {
    match chat_type {
        chat_type::ALLEGIANCE => "Allegiance".into(),
        chat_type::GENERAL => "General".into(),
        chat_type::TRADE => "Trade".into(),
        chat_type::LFG => "LFG".into(),
        chat_type::ROLEPLAY => "Roleplay".into(),
        chat_type::SOCIETY => "Society".into(),
        chat_type::SOCIETY_CELESTIAL_HAND => "Celestial Hand".into(),
        chat_type::SOCIETY_ELDRYTCH_WEB => "Eldrytch Web".into(),
        chat_type::SOCIETY_RADIANT_BLOOD => "Radiant Blood".into(),
        chat_type::OLTHOI => "Olthoi".into(),
        _ => format!("Room 0x{room_id:08X}"),
    }
}

/// The text class a room's lines are shown with.
#[must_use]
pub fn log_text_type(chat_type: u32) -> u32 {
    match chat_type {
        chat_type::ALLEGIANCE | chat_type::OLTHOI => 0x12,
        chat_type::GENERAL => 0x1B,
        chat_type::TRADE => 0x1C,
        chat_type::LFG => 0x1D,
        chat_type::ROLEPLAY => 0x1E,
        chat_type::SOCIETY
        | chat_type::SOCIETY_CELESTIAL_HAND
        | chat_type::SOCIETY_ELDRYTCH_WEB
        | chat_type::SOCIETY_RADIANT_BLOOD => 0x20,
        _ => 0x00,
    }
}

/// Whether the player may speak in a room.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GateStatus {
    /// The line may go out.
    Allowed,
    /// Rooms are off or the room was never assigned.
    Unavailable,
    /// The player is not listening to the room.
    NotListening,
}

/// The check's answer, with the room it resolved.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GateResult {
    /// The verdict.
    pub status: GateStatus,
    /// The room id (0 when none).
    pub room_id: u32,
    /// The room's chat type.
    pub chat_type: u32,
    /// The room's display name.
    pub display_name: String,
}

/// The player's facts the check reads: the two character option words and
/// whether the character is an Olthoi (the Olthoi room has no option).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Membership {
    /// Character option word 1.
    pub options1: u32,
    /// Character option word 2.
    pub options2: u32,
    /// The character is an Olthoi.
    pub is_olthoi: bool,
}

impl Default for Membership {
    fn default() -> Self {
        Self {
            options1: DEFAULT_OPTIONS1,
            options2: DEFAULT_OPTIONS2,
            is_olthoi: false,
        }
    }
}

/// May the player speak in this room?
#[must_use]
pub fn evaluate(
    channel: TurbineChannel,
    state: &TurbineChatState,
    membership: Membership,
) -> GateResult {
    let chat_type = match channel {
        TurbineChannel::Allegiance => chat_type::ALLEGIANCE,
        TurbineChannel::General => chat_type::GENERAL,
        TurbineChannel::Trade => chat_type::TRADE,
        TurbineChannel::Lfg => chat_type::LFG,
        TurbineChannel::Roleplay => chat_type::ROLEPLAY,
        TurbineChannel::Society => chat_type::SOCIETY,
        TurbineChannel::Olthoi => chat_type::OLTHOI,
        TurbineChannel::SocietyCelestialHand
        | TurbineChannel::SocietyEldrytchWeb
        | TurbineChannel::SocietyRadiantBlood => chat_type::UNDEF,
    };
    let room_id = if chat_type == chat_type::UNDEF {
        0
    } else {
        state.room_for(channel)
    };
    let display_name = display_name(room_id, chat_type);
    let result = |status| GateResult {
        status,
        room_id,
        chat_type,
        display_name: display_name.clone(),
    };
    if !state.enabled() || room_id == 0 {
        return result(GateStatus::Unavailable);
    }
    let hearing = match channel {
        TurbineChannel::Allegiance => membership.options1 & HEAR_ALLEGIANCE_CHAT != 0,
        TurbineChannel::General => membership.options2 & HEAR_GENERAL_CHAT != 0,
        TurbineChannel::Trade => membership.options2 & HEAR_TRADE_CHAT != 0,
        TurbineChannel::Lfg => membership.options2 & HEAR_LFG_CHAT != 0,
        TurbineChannel::Roleplay => membership.options2 & HEAR_ROLEPLAY_CHAT != 0,
        TurbineChannel::Society => membership.options2 & HEAR_SOCIETY_CHAT != 0,
        TurbineChannel::Olthoi => membership.is_olthoi,
        _ => true,
    };
    result(if hearing {
        GateStatus::Allowed
    } else {
        GateStatus::NotListening
    })
}

/// What the player reads when the check refuses; `None` when it allows.
#[must_use]
pub fn refusal_text(gate: &GateResult) -> Option<(String, LogTextType)> {
    match gate.status {
        GateStatus::Allowed => None,
        GateStatus::Unavailable => Some((
            client_text::TURBINE_CHAT_UNAVAILABLE.to_owned(),
            LogTextType::Default,
        )),
        GateStatus::NotListening => weenie_errors::resolve(0x0551, Some(&gate.display_name)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use ac_core::chat::TurbineChatRooms;

    fn received() -> TurbineChatState {
        let mut state = TurbineChatState::new();
        state.on_channels_received(TurbineChatRooms {
            allegiance: 0x10,
            general: 0x11,
            trade: 0x12,
            lfg: 0x13,
            roleplay: 0x14,
            olthoi: 0x17,
            society: 0x16,
            ..TurbineChatRooms::default()
        });
        state
    }

    fn with(options1: u32, options2: u32) -> Membership {
        Membership {
            options1,
            options2,
            is_olthoi: false,
        }
    }

    // Golden values: OpenAC tests/AcDream.Runtime.Tests/Session/TurbineChatDisplayNamesTests.cs

    #[test]
    fn log_text_types_match_the_retail_chat_format() {
        for (chat, expected) in [
            (chat_type::ALLEGIANCE, 0x12),
            (chat_type::GENERAL, 0x1B),
            (chat_type::TRADE, 0x1C),
            (chat_type::LFG, 0x1D),
            (chat_type::ROLEPLAY, 0x1E),
            (chat_type::SOCIETY, 0x20),
            (chat_type::SOCIETY_CELESTIAL_HAND, 0x20),
            (chat_type::SOCIETY_ELDRYTCH_WEB, 0x20),
            (chat_type::SOCIETY_RADIANT_BLOOD, 0x20),
            (chat_type::OLTHOI, 0x12),
            (chat_type::UNDEF, 0x00),
        ] {
            assert_eq!(log_text_type(chat), expected, "{chat}");
        }
        assert_eq!(display_name(0xAB, 99), "Room 0x000000AB");
    }

    // Golden values: OpenAC tests/AcDream.Runtime.Tests/Gameplay/TurbineChatMembershipGateTests.cs

    #[test]
    fn rooms_off_or_unassigned_are_unavailable() {
        let off = evaluate(
            TurbineChannel::General,
            &TurbineChatState::new(),
            Membership::default(),
        );
        assert_eq!((off.status, off.room_id), (GateStatus::Unavailable, 0));
        assert_eq!(
            refusal_text(&off).map(|(t, _)| t).as_deref(),
            Some(client_text::TURBINE_CHAT_UNAVAILABLE)
        );
        let mut state = TurbineChatState::new();
        state.on_channels_received(TurbineChatRooms {
            general: 0x11,
            ..TurbineChatRooms::default()
        });
        assert_eq!(
            evaluate(TurbineChannel::Allegiance, &state, Membership::default()).status,
            GateStatus::Unavailable
        );
    }

    #[test]
    fn a_hear_option_off_is_not_listening() {
        for channel in [
            TurbineChannel::General,
            TurbineChannel::Trade,
            TurbineChannel::Lfg,
            TurbineChannel::Roleplay,
            TurbineChannel::Society,
        ] {
            let gate = evaluate(channel, &received(), with(DEFAULT_OPTIONS1, 0));
            assert_eq!(gate.status, GateStatus::NotListening, "{channel:?}");
            assert_ne!(gate.room_id, 0);
            assert!(!gate.display_name.is_empty());
        }
        let gate = evaluate(
            TurbineChannel::Allegiance,
            &received(),
            with(0, DEFAULT_OPTIONS2),
        );
        assert_eq!(gate.status, GateStatus::NotListening);
        let gate = evaluate(
            TurbineChannel::General,
            &received(),
            with(DEFAULT_OPTIONS1, 0),
        );
        assert_eq!(
            refusal_text(&gate),
            weenie_errors::resolve(0x0551, Some(&gate.display_name))
        );
    }

    #[test]
    fn the_olthoi_room_gates_on_heritage() {
        let not = evaluate(TurbineChannel::Olthoi, &received(), Membership::default());
        let olthoi = evaluate(
            TurbineChannel::Olthoi,
            &received(),
            Membership {
                is_olthoi: true,
                ..Membership::default()
            },
        );
        assert_eq!(not.status, GateStatus::NotListening);
        assert_eq!(olthoi.status, GateStatus::Allowed);
    }

    #[test]
    fn an_allowed_result_names_its_room() {
        let membership = Membership {
            options1: DEFAULT_OPTIONS1,
            options2: DEFAULT_OPTIONS2 | HEAR_SOCIETY_CHAT,
            is_olthoi: true,
        };
        for (channel, room, chat, name) in [
            (
                TurbineChannel::Allegiance,
                0x10,
                chat_type::ALLEGIANCE,
                "Allegiance",
            ),
            (TurbineChannel::General, 0x11, chat_type::GENERAL, "General"),
            (TurbineChannel::Trade, 0x12, chat_type::TRADE, "Trade"),
            (TurbineChannel::Lfg, 0x13, chat_type::LFG, "LFG"),
            (TurbineChannel::Society, 0x16, chat_type::SOCIETY, "Society"),
            (TurbineChannel::Olthoi, 0x17, chat_type::OLTHOI, "Olthoi"),
        ] {
            let gate = evaluate(channel, &received(), membership);
            assert_eq!(
                gate,
                GateResult {
                    status: GateStatus::Allowed,
                    room_id: room,
                    chat_type: chat,
                    display_name: name.into(),
                }
            );
            assert_eq!(refusal_text(&gate), None);
        }
    }

    #[test]
    fn default_options_hear_the_usual_rooms_but_not_roleplay_or_society() {
        for (channel, status) in [
            (TurbineChannel::Allegiance, GateStatus::Allowed),
            (TurbineChannel::General, GateStatus::Allowed),
            (TurbineChannel::Trade, GateStatus::Allowed),
            (TurbineChannel::Lfg, GateStatus::Allowed),
            (TurbineChannel::Roleplay, GateStatus::NotListening),
            (TurbineChannel::Society, GateStatus::NotListening),
        ] {
            assert_eq!(
                evaluate(channel, &received(), Membership::default()).status,
                status,
                "{channel:?}"
            );
        }
    }
}
