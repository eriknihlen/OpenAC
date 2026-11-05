//! Chat rules: the transcript, text classes and channels, the wording of
//! channel, combat, inventory-refusal and server-failure lines, the chat
//! windows' filters, the language filter, line markup, reply targets, the
//! status box and chat rooms.
//!
//! Everything here is pure: inputs are plain values (the runtime adapts the
//! network messages) and time comes in from the caller.

mod channel;
pub mod client_text;
pub mod combat_text;
pub mod inventory_failure;
pub mod language_filter;
mod log;
mod log_text_type;
pub mod session_log;
mod spew_box;
pub mod tag_markup;
mod target_state;
mod turbine;
pub mod weenie_errors;
mod window_state;

pub use channel::{
    CHANNEL_ALLEGIANCE_BROADCAST, CHANNEL_CO_VASSALS, CHANNEL_FELLOW, CHANNEL_FELLOW_BROADCAST,
    CHANNEL_MONARCH, CHANNEL_PATRON, CHANNEL_VASSALS, ChatChannelInfo, ChatChannelSource,
    legacy_channel_chat_type, legacy_channel_heard, legacy_channel_name, legacy_channel_sent,
};
pub use combat_text::{CombatLineKind, CombatNotice};
pub use log::{
    ChatEntry, ChatFilter, ChatFilterId, ChatKind, ChatLog, ChatSuppressionFilters,
    DEFAULT_MAX_ENTRIES, format_timestamp_prefix,
};
pub use log_text_type::LogTextType;
pub use spew_box::{DEFAULT_LIFETIME_SECONDS, MAX_CONCURRENT_ITEMS, SpewBoxEntry, SpewBoxState};
pub use tag_markup::{ChatSpanRole, ChatTextSpan, ChatTextTag};
pub use target_state::ChatCommandTargetState;
pub use turbine::{TurbineChannel, TurbineChatRooms, TurbineChatState};
pub use window_state::{
    BROADCAST_TARGET_WINDOW, ChatWindowId, ChatWindowState, FLOATY1_DEFAULT_FILTER,
    FLOATY2_DEFAULT_FILTER, FLOATY3_DEFAULT_FILTER, FLOATY4_DEFAULT_FILTER,
    MAIN_WINDOW_DEFAULT_FILTER, MAX_FLOATING_WINDOW_ID, MIN_FLOATING_WINDOW_ID,
};

/// The lowest object id the server gives a player character.
pub const FIRST_PLAYER_OBJECT_ID: u32 = 0x5000_0001;
/// The highest object id the server gives a player character.
pub const LAST_PLAYER_OBJECT_ID: u32 = 0x6FFF_FFFF;

/// Whether `object_id` is a player character's: chat uses it to tell
/// another player from a creature or an item that speaks, since only a
/// player is someone to answer or to click to send a tell.
#[must_use]
pub fn is_player_object_id(object_id: u32) -> bool {
    (FIRST_PLAYER_OBJECT_ID..=LAST_PLAYER_OBJECT_ID).contains(&object_id)
}
