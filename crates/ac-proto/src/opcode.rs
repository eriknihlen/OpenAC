use crate::Result;

/// Game message opcodes this crate knows: the first `u32` of a body.
///
/// Any other opcode is still a valid message; it is just not one the login
/// flow handles, so callers keep it as a plain `u32`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum Opcode {
    /// Client asks to log the character off; the server echoes the bare
    /// opcode back as confirmation.
    CharacterLogOff = 0xF653,
    /// Client enters the world with the chosen character.
    CharacterEnterWorld = 0xF657,
    /// Server sends the account's characters.
    CharacterList = 0xF658,
    /// Server rejects a login or character-selection step.
    CharacterError = 0xF659,
    /// Server describes a new object.
    CreateObject = 0xF745,
    /// Server event addressed to the player, wrapped in an envelope.
    GameEvent = 0xF7B0,
    /// Client action, wrapped in an envelope with a sequence number.
    GameAction = 0xF7B1,
    /// Client asks to enter the world; the server answers with
    /// [`Opcode::ServerReady`].
    CharacterEnterWorldRequest = 0xF7C8,
    /// Server is ready for the enter-world message. The body is the bare
    /// opcode.
    ServerReady = 0xF7DF,
    /// Server name and population.
    ServerName = 0xF7E1,
    /// Server pushes game-data file contents; this client cannot apply them.
    DddDataMessage = 0xF7E2,
    /// Server asks which game-data versions the client has.
    DddInterrogation = 0xF7E5,
    /// Client reports its game-data versions.
    DddInterrogationResponse = 0xF7E6,
    /// Server announces the game-data update it is about to send.
    DddBegin = 0xF7E7,
    /// Server ends the game-data check; the client echoes the bare opcode.
    DddEnd = 0xF7EA,
    /// Server sets a stack's count and value.
    SetStackSize = 0x0197,
    /// Server removes an object from the player's inventory view.
    InventoryRemoveObject = 0x0024,
    /// Server changes one integer property on the player.
    PrivateUpdatePropertyInt = 0x02CD,
    /// Server changes one integer property on a visible object.
    PublicUpdatePropertyInt = 0x02CE,
    /// Server changes one 64-bit integer property on the player.
    PrivateUpdatePropertyInt64 = 0x02CF,
    /// Server changes one true/false property on the player.
    PrivateUpdatePropertyBool = 0x02D1,
    /// Server changes one true/false property on a visible object.
    PublicUpdatePropertyBool = 0x02D2,
    /// Server changes one string property on the player.
    PrivateUpdatePropertyString = 0x02D5,
    /// Server changes one string property on a visible object.
    PublicUpdatePropertyString = 0x02D6,
    /// Server changes one data-id property on the player.
    PrivateUpdatePropertyDataId = 0x02D7,
    /// Server changes one data-id property on a visible object.
    PublicUpdatePropertyDataId = 0x02D8,
    /// Server changes one object-id property on the player.
    PrivateUpdatePropertyInstanceId = 0x02D9,
    /// Server changes one object-id property on a visible object.
    PublicUpdatePropertyInstanceId = 0x02DA,
    /// Server changes one saved-position slot on the player.
    PrivateUpdatePosition = 0x02DB,
    /// Server restates one of the player's skills.
    PrivateUpdateSkill = 0x02DD,
    /// Server restates one of the player's primary attributes.
    PrivateUpdateAttribute = 0x02E3,
    /// Server restates one of the player's vitals.
    PrivateUpdateVital = 0x02E7,
    /// Server changes only a vital's current value.
    PrivateUpdateVitalCurrent = 0x02E9,
    /// An object's appearance changed.
    ObjDescEvent = 0xF625,
    /// Server destroys an object.
    DeleteObject = 0xF747,
    /// An object moved.
    UpdatePosition = 0xF748,
    /// An object was attached to a parent.
    ParentEvent = 0xF749,
    /// An object left the world into a container.
    PickupEvent = 0xF74A,
    /// An object's physics state changed.
    SetState = 0xF74B,
    /// An object's motion changed.
    UpdateMotion = 0xF74C,
    /// An object's velocity changed.
    VectorUpdate = 0xF74E,
    /// The player is being teleported.
    PlayerTeleport = 0xF751,
    /// Server re-sends the full description of an existing object.
    UpdateObject = 0xF7DB,
    /// Someone nearby spoke.
    HearSpeech = 0x02BB,
    /// Someone spoke loudly enough to carry a set distance.
    HearRangedSpeech = 0x02BC,
    /// Someone emoted.
    EmoteText = 0x01E0,
    /// Someone emoted with an animation.
    SoulEmote = 0x01E2,
    /// Server text for the chat window.
    ServerMessage = 0xF7E0,
    /// Turbine chat room traffic, both directions.
    TurbineChat = 0xF7DE,
    /// A player died; everyone nearby hears the death message.
    PlayerKilled = 0x019E,
    /// An object plays a sound.
    SoundEvent = 0xF750,
    /// An object plays a physics script by id.
    PlayScript = 0xF754,
    /// An object plays a physics script by type and intensity.
    PlayScriptType = 0xF755,
    /// Server changes the environment (fog preset or sound cue).
    AdminEnvirons = 0xEA60,
    /// Client deletes a character; the server echoes the bare opcode.
    CharacterDelete = 0xF655,
    /// Client creates a character.
    CharacterCreate = 0xF656,
    /// Client restores a deleted character.
    CharacterRestore = 0xF7D9,
    /// Server answers a character create or restore.
    CharGenVerificationResponse = 0xF643,
    /// Client sends a legacy friends-list command.
    FriendsOld = 0xF7CD,
}

impl Opcode {
    /// Every known opcode, for lookups and tests.
    pub const ALL: [Opcode; 59] = [
        Opcode::CharacterLogOff,
        Opcode::CharacterEnterWorld,
        Opcode::CharacterList,
        Opcode::CharacterError,
        Opcode::CreateObject,
        Opcode::GameEvent,
        Opcode::GameAction,
        Opcode::CharacterEnterWorldRequest,
        Opcode::ServerReady,
        Opcode::ServerName,
        Opcode::DddDataMessage,
        Opcode::DddInterrogation,
        Opcode::DddInterrogationResponse,
        Opcode::DddBegin,
        Opcode::DddEnd,
        Opcode::SetStackSize,
        Opcode::InventoryRemoveObject,
        Opcode::PrivateUpdatePropertyInt,
        Opcode::PublicUpdatePropertyInt,
        Opcode::PrivateUpdatePropertyInt64,
        Opcode::PrivateUpdatePropertyBool,
        Opcode::PublicUpdatePropertyBool,
        Opcode::PrivateUpdatePropertyString,
        Opcode::PublicUpdatePropertyString,
        Opcode::PrivateUpdatePropertyDataId,
        Opcode::PublicUpdatePropertyDataId,
        Opcode::PrivateUpdatePropertyInstanceId,
        Opcode::PublicUpdatePropertyInstanceId,
        Opcode::PrivateUpdatePosition,
        Opcode::PrivateUpdateSkill,
        Opcode::PrivateUpdateAttribute,
        Opcode::PrivateUpdateVital,
        Opcode::PrivateUpdateVitalCurrent,
        Opcode::ObjDescEvent,
        Opcode::DeleteObject,
        Opcode::UpdatePosition,
        Opcode::ParentEvent,
        Opcode::PickupEvent,
        Opcode::SetState,
        Opcode::UpdateMotion,
        Opcode::VectorUpdate,
        Opcode::PlayerTeleport,
        Opcode::UpdateObject,
        Opcode::HearSpeech,
        Opcode::HearRangedSpeech,
        Opcode::EmoteText,
        Opcode::SoulEmote,
        Opcode::ServerMessage,
        Opcode::TurbineChat,
        Opcode::PlayerKilled,
        Opcode::SoundEvent,
        Opcode::PlayScript,
        Opcode::PlayScriptType,
        Opcode::AdminEnvirons,
        Opcode::CharacterDelete,
        Opcode::CharacterCreate,
        Opcode::CharacterRestore,
        Opcode::CharGenVerificationResponse,
        Opcode::FriendsOld,
    ];

    /// The wire value.
    #[must_use]
    pub const fn value(self) -> u32 {
        self as u32
    }

    /// The known opcode with this wire value, if any.
    #[must_use]
    pub fn from_u32(value: u32) -> Option<Self> {
        Self::ALL.into_iter().find(|op| op.value() == value)
    }

    /// The bare four-byte body that is only this opcode.
    #[must_use]
    pub fn bare_body(self) -> Vec<u8> {
        self.value().to_le_bytes().to_vec()
    }
}

/// The opcode at the start of a message body.
pub fn read_opcode(body: &[u8]) -> Result<u32> {
    Ok(ac_bits::Reader::new(body).u32()?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn from_u32_round_trips_every_known_opcode() {
        for op in Opcode::ALL {
            assert_eq!(Opcode::from_u32(op.value()), Some(op));
        }
        assert_eq!(Opcode::from_u32(0xDEAD_BEEF), None);
    }

    #[test]
    fn read_opcode_is_little_endian_and_rejects_short_bodies() {
        assert_eq!(read_opcode(&[0x58, 0xF6, 0, 0, 9]), Ok(0xF658));
        for len in 0..4 {
            assert!(read_opcode(&[0x58, 0xF6, 0, 0][..len]).is_err());
        }
    }
}
