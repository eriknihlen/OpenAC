//! What kind of object a collision target is, and which movers pass
//! through which targets.

use crate::state::ObjectInfoState;

/// Facts about a collision target the movement sweep needs beyond its
/// physics state: is it a player, a creature, a player killer, ...
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct EntityCollisionFlags(pub u8);

impl EntityCollisionFlags {
    /// No flags.
    pub const NONE: u8 = 0x00;
    /// A player (public weenie bit `0x8`).
    pub const IS_PLAYER: u8 = 0x01;
    /// A creature.
    pub const IS_CREATURE: u8 = 0x02;
    /// A player killer (public weenie bit `0x20`).
    pub const IS_PK: u8 = 0x04;
    /// A player killer lite (public weenie bit `0x2000000`).
    pub const IS_PK_LITE: u8 = 0x08;
    /// Cannot be walked through (public weenie bit `0x200000`).
    pub const IS_IMPENETRABLE: u8 = 0x10;
    /// Has a game object behind it (not a bare static).
    pub const HAS_WEENIE: u8 = 0x20;
    /// May enter restricted places (public weenie bits `0x100000` and
    /// `0x400000` together).
    pub const CAN_BYPASS_MOVE_RESTRICTIONS: u8 = 0x40;

    /// The flags that come from the public weenie bitfield.
    pub const PWD_BITFIELD_DERIVED_MASK: u8 = Self::IS_PLAYER
        | Self::IS_PK
        | Self::IS_PK_LITE
        | Self::IS_IMPENETRABLE
        | Self::CAN_BYPASS_MOVE_RESTRICTIONS;

    /// True when every bit of `bits` is set.
    #[must_use]
    pub fn contains(self, bits: u8) -> bool {
        self.0 & bits == bits
    }

    /// The flags a public weenie bitfield gives.
    #[must_use]
    pub fn from_pwd_bitfield(bitfield: u32) -> Self {
        let mut flags = 0u8;
        if bitfield & 0x8 != 0 {
            flags |= Self::IS_PLAYER;
        }
        if bitfield & 0x20 != 0 {
            flags |= Self::IS_PK;
        }
        if bitfield & 0x20_0000 != 0 {
            flags |= Self::IS_IMPENETRABLE;
        }
        if bitfield & 0x200_0000 != 0 {
            flags |= Self::IS_PK_LITE;
        }
        if bitfield & 0x10_0000 != 0 && bitfield & 0x40_0000 != 0 {
            flags |= Self::CAN_BYPASS_MOVE_RESTRICTIONS;
        }
        Self(flags)
    }

    /// The mover-state bits these flags give a moving object of this kind.
    #[must_use]
    pub fn to_mover_state(self) -> ObjectInfoState {
        let mut state = ObjectInfoState::NONE;
        if self.contains(Self::IS_PK) {
            state |= ObjectInfoState::IS_PK;
        }
        if self.contains(Self::IS_PK_LITE) {
            state |= ObjectInfoState::IS_PK_LITE;
        }
        if self.contains(Self::IS_IMPENETRABLE) {
            state |= ObjectInfoState::IS_IMPENETRABLE;
        }
        if self.contains(Self::CAN_BYPASS_MOVE_RESTRICTIONS) {
            state |= ObjectInfoState::CAN_BYPASS_MOVE_RESTRICTIONS;
        }
        ObjectInfoState(state)
    }
}

/// The player-killer mover bits of an object from its public weenie
/// bitfield, when the client knows one (OpenAC
/// `EntityCollisionFlagsExt.ResolveMoverPvpState` after its table lookup).
#[must_use]
pub fn resolve_mover_pvp_state(public_weenie_bitfield: Option<u32>) -> ObjectInfoState {
    match public_weenie_bitfield {
        Some(bitfield) => EntityCollisionFlags::from_pwd_bitfield(bitfield).to_mover_state(),
        None => ObjectInfoState(ObjectInfoState::NONE),
    }
}

const ETHEREAL_PS: u32 = 0x4;
const IGNORE_COLLISIONS_PS: u32 = 0x10;

/// True when the mover passes through the target without testing it: an
/// ethereal target that ignores collisions; a creature for the camera or a
/// mover that ignores creatures; another player, unless either is
/// impenetrable or both are player killers (or both player killers lite).
#[must_use]
pub fn should_skip(
    target_state: u32,
    target_flags: EntityCollisionFlags,
    mover_state: ObjectInfoState,
) -> bool {
    if target_state & ETHEREAL_PS != 0 && target_state & IGNORE_COLLISIONS_PS != 0 {
        return true;
    }

    let mover_is_viewer = mover_state.contains(ObjectInfoState::IS_VIEWER);
    let target_is_creature = target_flags.contains(EntityCollisionFlags::IS_CREATURE);
    if mover_is_viewer && target_is_creature {
        return true;
    }

    let mover_ignores_creatures = mover_state.contains(ObjectInfoState::IGNORE_CREATURES);
    if mover_ignores_creatures && target_is_creature {
        return true;
    }

    let mover_is_player = mover_state.contains(ObjectInfoState::IS_PLAYER);
    let target_is_player = target_flags.contains(EntityCollisionFlags::IS_PLAYER);
    if mover_is_player && target_is_player {
        let mut collide = false;
        if mover_state.contains(ObjectInfoState::IS_IMPENETRABLE) {
            collide = true;
        }
        if !collide && target_flags.contains(EntityCollisionFlags::IS_IMPENETRABLE) {
            collide = true;
        }
        if !collide
            && mover_state.contains(ObjectInfoState::IS_PK)
            && target_flags.contains(EntityCollisionFlags::IS_PK)
        {
            collide = true;
        }
        if !collide
            && mover_state.contains(ObjectInfoState::IS_PK_LITE)
            && target_flags.contains(EntityCollisionFlags::IS_PK_LITE)
        {
            collide = true;
        }
        if !collide {
            return true;
        }
    }

    false
}
