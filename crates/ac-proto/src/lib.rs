//! Typed game messages: parse functions that turn a message body into a
//! struct, and build functions that turn values into a body. No I/O.
//!
//! A game message body is what the transport hands over after reassembling
//! fragments: a little-endian `u32` opcode followed by the message fields.
//! Every parser checks the opcode, returns [`ProtoError`] on malformed input
//! and never panics. Builders return the body as `Vec<u8>`; which message
//! queue a body travels on is the transport's concern, and each builder's
//! documentation names the queue the session sends it on.
//!
//! This crate covers the login flow: character list, enter world, the
//! game-data check, login complete, log off. [`Inbound::parse`] classifies
//! any inbound body into the messages that flow cares about.
//!
//! It also reads the world-object messages: object descriptions (create and
//! update), delete, pickup, parent, physics state, position, velocity,
//! motion, appearance changes and the player's teleport notice.
//!
//! The gameplay messages live in public modules by subject: [`chat`],
//! [`turbine_chat`], [`client_commands`], [`social`], [`allegiance`],
//! [`fellowship`], [`house`], [`trade`], [`book`], [`chess`], [`vendor`],
//! [`inventory`], [`combat`], [`magic`], [`appraise`], [`character`],
//! [`contract`], [`effects`] and [`movement_out`]. Each holds the server
//! messages and event payloads of its subject (`parse`) and the client
//! requests (`build_*`). Game-action builders take the session's next
//! action sequence number, see [`GameActionSequence`].

mod character_error;
mod character_list;
mod create_object;
mod ddd;
mod enter_world;
mod game_action;
mod game_event;
mod geometry;
mod inbound;
mod logoff;
mod opcode;
mod server_name;

mod enchantment;
mod game_event_dispatch;
mod inventory_update;
mod model_data;
mod movement;
mod obj_desc_event;
mod object_events;
mod physics_desc;
mod player_description;
mod quality_update;
mod update_motion;
mod update_position;
mod weenie_desc;

mod wire;

pub mod allegiance;
pub mod appraise;
pub mod book;
pub mod character;
pub mod chat;
pub mod chess;
pub mod client_commands;
pub mod combat;
pub mod contract;
pub mod effects;
pub mod fellowship;
pub mod house;
pub mod inventory;
pub mod magic;
pub mod movement_out;
pub mod social;
pub mod trade;
pub mod turbine_chat;
pub mod vendor;

#[cfg(test)]
mod sequence_tests;
#[cfg(test)]
mod test_util;

pub use ac_bits::BitsError;
pub use character_error::{CharacterError, CharacterErrorCode};
pub use character_list::{Character, CharacterList, EnterWorldSelection, SelectionError};
pub use create_object::CreateObject;
pub use ddd::{
    DddBegin, DddDataVersions, MAX_DATA_VERSION_COUNT, build_data_check_complete,
    build_interrogation_response,
};
pub use enter_world::{build_enter_world, build_enter_world_request};
pub use game_action::{
    GameAction, GameActionSequence, GameActionType, build_game_action, build_house_query,
    build_login_complete,
};
pub use game_event::{GameEventEnvelope, GameEventType};
pub use geometry::{Position, Quaternion, Vector3};
pub use inbound::Inbound;
pub use logoff::{build_logoff_request, is_logoff_confirmation};
pub use opcode::{Opcode, read_opcode};
pub use server_name::ServerName;

pub use enchantment::{Enchantment, MAX_ENCHANTMENT_LIST_COUNT};
pub use game_event_dispatch::{
    DispatchOutcome, GameEventDispatcher, GameEventHandler, OwnedRegistration, SubscriptionSet,
};
pub use inventory_update::{InventoryRemoveObject, SetStackSize};
pub use model_data::{
    AnimPartChange, GFX_OBJ_ID_TYPE, ModelData, PALETTE_ID_TYPE, SURFACE_TEXTURE_ID_TYPE,
    SubPalette, TextureChange,
};
pub use movement::*;
pub use obj_desc_event::ObjDescEvent;
pub use object_events::{
    DeleteObject, ParentEvent, PickupEvent, PlayerTeleport, SetState, VectorUpdate,
};
pub use physics_desc::*;
pub use player_description::{
    AttributeEntry, CharacterOptionDataFlags, DEFAULT_SPELLBOOK_FILTERS, DescriptionPropertyFlags,
    DescriptionVectorFlags, DesiredComponent, EnchantmentBucket, EquippedEntry, InventoryEntry,
    MAX_TRAILER_COUNT, PlayerDescription, PlayerEnchantment, PropertyTables, Shortcut, SkillEntry,
};
pub use quality_update::{
    PrivateUpdateAttribute, PrivateUpdatePosition, PrivateUpdatePropertyBool,
    PrivateUpdatePropertyDataId, PrivateUpdatePropertyInstanceId, PrivateUpdatePropertyInt,
    PrivateUpdatePropertyInt64, PrivateUpdatePropertyString, PrivateUpdateSkill,
    PrivateUpdateVital, PrivateUpdateVitalCurrent, PublicUpdatePropertyBool,
    PublicUpdatePropertyDataId, PublicUpdatePropertyInstanceId, PublicUpdatePropertyInt,
    PublicUpdatePropertyString,
};
pub use update_motion::UpdateMotion;
pub use update_position::*;
pub use weenie_desc::*;

/// Errors from parsing or building a message body.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum ProtoError {
    /// The body ended early, or a value did not fit its wire encoding.
    #[error(transparent)]
    Bits(#[from] BitsError),
    /// The body starts with a different opcode than the parser handles.
    #[error("expected opcode {expected:#06x}, got {actual:#010x}")]
    WrongOpcode {
        /// Opcode the parser handles.
        expected: u32,
        /// Opcode found in the body.
        actual: u32,
    },
    /// A collection count is larger than the rest of the body could hold.
    #[error("{what} count {count} exceeds the remaining payload")]
    CountExceedsPayload {
        /// Which collection.
        what: &'static str,
        /// The count read from the body.
        count: u32,
    },
    /// The body has the wrong total length for its message.
    #[error("{what}: invalid body length {actual}")]
    InvalidLength {
        /// Which message.
        what: &'static str,
        /// The body length found.
        actual: usize,
    },
    /// A collection count is outside the range the message allows.
    #[error("{what} count {count} is outside 0..={max}")]
    CountOutOfRange {
        /// Which collection.
        what: &'static str,
        /// The count read from the body (a negative count as its bits).
        count: u32,
        /// The largest count allowed.
        max: u32,
    },
    /// A data-file version count is above the limit the response allows.
    #[error("data version count {count} is above the limit {max}")]
    DataVersionOutOfRange {
        /// The count given.
        count: u32,
        /// The largest count allowed.
        max: u32,
    },
    /// A collection count is above the limit its parser accepts.
    #[error("{what} count {count} is above the limit {max}")]
    CountTooLarge {
        /// Which collection.
        what: &'static str,
        /// The count read from the body.
        count: u32,
        /// The largest count accepted.
        max: u32,
    },
    /// A floating-point field holds NaN or an infinity.
    #[error("{what}: non-finite value")]
    NonFinite {
        /// Which record.
        what: &'static str,
    },
    /// A field holds a value outside the set its message allows.
    #[error("{what}: value {value:#x} is not allowed")]
    InvalidValue {
        /// Which field.
        what: &'static str,
        /// The value read from the body.
        value: u32,
    },
    /// The fields are each well formed but contradict one another (a record
    /// names a parent that is not there, an id appears twice).
    #[error("{what}: inconsistent message")]
    Inconsistent {
        /// Which message or record.
        what: &'static str,
    },
    /// A builder was given an argument its message cannot carry.
    #[error("{what}: invalid argument")]
    InvalidArgument {
        /// Which argument.
        what: &'static str,
    },
}

/// Shorthand for results in this crate.
pub type Result<T> = core::result::Result<T, ProtoError>;

/// Read the opcode and check it is `expected`.
fn expect_opcode(r: &mut ac_bits::Reader<'_>, expected: Opcode) -> Result<()> {
    let actual = r.u32()?;
    if actual == expected.value() {
        Ok(())
    } else {
        Err(ProtoError::WrongOpcode {
            expected: expected.value(),
            actual,
        })
    }
}
