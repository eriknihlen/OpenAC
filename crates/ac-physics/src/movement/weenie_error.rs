//! Movement failure codes ("weenie errors"): the game's numbered reasons a
//! movement request failed. The client shows text for some of them (see
//! `ac_core::chat::weenie_errors`).

use std::fmt;

/// A movement result code; [`WeenieError::NONE`] is success. Codes are the
/// game's; any `u32` can appear (a queued motion carries its jump-refusal
/// code, which is replayed as-is).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct WeenieError(pub u32);

impl WeenieError {
    /// Success.
    pub const NONE: Self = Self(0x00);
    /// The object has no motion table.
    pub const NO_ANIMATION_TABLE: Self = Self(0x07);
    /// The object has no physics body.
    pub const NO_PHYSICS_OBJECT: Self = Self(0x08);
    /// Not standing on the ground ("You can't jump while in the air").
    pub const NOT_GROUNDED: Self = Self(0x24);
    /// The movement was cancelled by another.
    pub const ACTION_CANCELLED: Self = Self(0x36);
    /// The target left.
    pub const OBJECT_GONE: Self = Self(0x37);
    /// The target does not exist.
    pub const NO_OBJECT: Self = Self(0x38);
    /// No way to get there.
    pub const CANT_GET_THERE: Self = Self(0x39);
    /// The object teleported.
    pub const I_TELEPORTED: Self = Self(0x3C);
    /// Gave up past the fail distance.
    pub const YOU_CHARGED_TOO_FAR: Self = Self(0x3D);
    /// Too tired.
    pub const YOU_ARE_TOO_TIRED_TO_DO_THAT: Self = Self(0x3E);
    /// Crouching is refused outside non-combat stance.
    pub const CROUCH_IN_COMBAT_STANCE: Self = Self(0x3F);
    /// Sitting is refused outside non-combat stance.
    pub const SIT_IN_COMBAT_STANCE: Self = Self(0x40);
    /// Lying down is refused outside non-combat stance.
    pub const SLEEP_IN_COMBAT_STANCE: Self = Self(0x41);
    /// Chat emotes are refused outside non-combat stance.
    pub const CHAT_EMOTE_OUTSIDE_NON_COMBAT: Self = Self(0x42);
    /// The motion table has no data for the motion.
    pub const NO_MTABLE_DATA: Self = Self(0x43);
    /// Six actions are already queued.
    pub const ACTION_DEPTH_EXCEEDED: Self = Self(0x45);
    /// Generic movement failure.
    pub const GENERAL_MOVEMENT_FAILURE: Self = Self(0x47);
    /// "You can't jump from this position."
    pub const YOU_CANT_JUMP_FROM_THIS_POSITION: Self = Self(0x48);
    /// "You are too loaded down to jump."
    pub const CANT_JUMP_LOADED_DOWN: Self = Self(0x49);

    /// The numeric code.
    #[must_use]
    pub fn code(self) -> u32 {
        self.0
    }

    /// True for success.
    #[must_use]
    pub fn is_none(self) -> bool {
        self == Self::NONE
    }
}

impl fmt::Display for WeenieError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "movement error 0x{:02X}", self.0)
    }
}
