use crate::{
    CharacterError, CharacterList, CreateObject, DddBegin, DeleteObject, GameEventEnvelope,
    ObjDescEvent, Opcode, ParentEvent, PickupEvent, PlayerTeleport, ProtoError, Result, ServerName,
    SetState, UpdateMotion, UpdatePosition, VectorUpdate, is_logoff_confirmation, read_opcode,
};
use crate::{
    InventoryRemoveObject, PrivateUpdateAttribute, PrivateUpdatePosition,
    PrivateUpdatePropertyBool, PrivateUpdatePropertyDataId, PrivateUpdatePropertyInstanceId,
    PrivateUpdatePropertyInt, PrivateUpdatePropertyInt64, PrivateUpdatePropertyString,
    PrivateUpdateSkill, PrivateUpdateVital, PrivateUpdateVitalCurrent, PublicUpdatePropertyBool,
    PublicUpdatePropertyDataId, PublicUpdatePropertyInstanceId, PublicUpdatePropertyInt,
    PublicUpdatePropertyString, SetStackSize,
};

/// An inbound game message, classified.
///
/// Messages no parser here handles come back as [`Inbound::Other`] with
/// their opcode, so a caller can route them elsewhere.
#[derive(Debug, Clone, PartialEq)]
pub enum Inbound<'a> {
    /// The account's characters.
    CharacterList(CharacterList),
    /// The world's name and population.
    ServerName(ServerName),
    /// A login or character-selection rejection. Check
    /// [`CharacterError::is_sentinel`] before treating it as one.
    CharacterError(CharacterError),
    /// The server confirmed the character log-off.
    LogOffConfirmed,
    /// The server is ready for the enter-world message.
    ServerReady,
    /// The server asks which game-data versions the client has; answer with
    /// [`crate::build_interrogation_response`].
    DddInterrogation,
    /// The server announces a game-data update.
    DddBegin(DddBegin),
    /// The server pushes game-data file contents, which this client cannot
    /// apply.
    DddDataMessage,
    /// The server ends the game-data check.
    DddEnd,
    /// The server created an object.
    CreateObject(Box<CreateObject>),
    /// A game event addressed to the player.
    GameEvent(GameEventEnvelope<'a>),
    /// An integer property changed on a visible object.
    PublicUpdatePropertyInt(PublicUpdatePropertyInt),
    /// An integer property changed on the player.
    PrivateUpdatePropertyInt(PrivateUpdatePropertyInt),
    /// A 64-bit integer property changed on the player.
    PrivateUpdatePropertyInt64(PrivateUpdatePropertyInt64),
    /// A true/false property changed on the player.
    PrivateUpdatePropertyBool(PrivateUpdatePropertyBool),
    /// A true/false property changed on a visible object.
    PublicUpdatePropertyBool(PublicUpdatePropertyBool),
    /// A string property changed on a visible object.
    PublicUpdatePropertyString(PublicUpdatePropertyString),
    /// A string property changed on the player.
    PrivateUpdatePropertyString(PrivateUpdatePropertyString),
    /// A data-id property changed on a visible object.
    PublicUpdatePropertyDataId(PublicUpdatePropertyDataId),
    /// A data-id property changed on the player.
    PrivateUpdatePropertyDataId(PrivateUpdatePropertyDataId),
    /// An object-id property changed on a visible object.
    PublicUpdatePropertyInstanceId(PublicUpdatePropertyInstanceId),
    /// An object-id property changed on the player.
    PrivateUpdatePropertyInstanceId(PrivateUpdatePropertyInstanceId),
    /// A saved-position slot changed on the player.
    PrivateUpdatePosition(PrivateUpdatePosition),
    /// A vital restated in full.
    PrivateUpdateVital(PrivateUpdateVital),
    /// A vital's current value changed.
    PrivateUpdateVitalCurrent(PrivateUpdateVitalCurrent),
    /// A primary attribute restated.
    PrivateUpdateAttribute(PrivateUpdateAttribute),
    /// A skill restated.
    PrivateUpdateSkill(PrivateUpdateSkill),
    /// A stack's count and value changed.
    SetStackSize(SetStackSize),
    /// An object left the player's inventory view.
    InventoryRemoveObject(InventoryRemoveObject),
    /// The server re-sent the description of an existing object.
    UpdateObject(Box<CreateObject>),
    /// The server destroyed an object.
    DeleteObject(DeleteObject),
    /// An object left the world into a container.
    PickupEvent(PickupEvent),
    /// An object was attached to a parent.
    ParentEvent(ParentEvent),
    /// An object's physics state changed.
    SetState(SetState),
    /// An object moved.
    UpdatePosition(UpdatePosition),
    /// An object's velocity changed.
    VectorUpdate(VectorUpdate),
    /// An object's motion changed.
    UpdateMotion(Box<UpdateMotion>),
    /// An object's appearance changed.
    ObjDescEvent(ObjDescEvent),
    /// The player is being teleported.
    PlayerTeleport(PlayerTeleport),
    /// Someone spoke, nearby or ranged.
    HearSpeech(crate::chat::HearSpeech),
    /// Someone emoted.
    EmoteText(crate::chat::Emote),
    /// Someone emoted with an animation.
    SoulEmote(crate::chat::Emote),
    /// Server text for the chat window.
    ServerMessage(crate::chat::ServerMessage),
    /// Turbine chat room traffic.
    TurbineChat(Box<crate::turbine_chat::TurbineChat>),
    /// A player died nearby.
    PlayerKilled(crate::combat::PlayerKilled),
    /// The server deleted the character asked for.
    CharacterDeleteAcknowledged,
    /// The answer to a character create or restore.
    CharGenVerification(crate::character::CharGenVerificationResponse),
    /// An object plays a sound.
    SoundEvent(crate::effects::SoundEvent),
    /// An object plays a physics script by id.
    PlayScript(crate::effects::PlayScript),
    /// An object plays a physics script by type.
    PlayScriptType(crate::effects::PlayScriptType),
    /// The environment changed (fog or sound cue).
    AdminEnvirons(crate::effects::AdminEnvirons),
    /// Any other message.
    Other {
        /// The message opcode.
        opcode: u32,
        /// The whole body, opcode included.
        body: &'a [u8],
    },
}

impl<'a> Inbound<'a> {
    /// Classify and parse an inbound body. A body too short for an opcode,
    /// or a malformed message of a handled kind, is an error; the session
    /// drops such a message and carries on with the next one.
    pub fn parse(body: &'a [u8]) -> Result<Self> {
        let opcode = read_opcode(body)?;
        // The confirmation is checked first: it is the log-off opcode with
        // nothing after it, unlike the request shape.
        if is_logoff_confirmation(body) {
            return Ok(Self::LogOffConfirmed);
        }
        Ok(match Opcode::from_u32(opcode) {
            Some(Opcode::CharacterList) => Self::CharacterList(CharacterList::parse(body)?),
            Some(Opcode::ServerName) => Self::ServerName(ServerName::parse(body)?),
            Some(Opcode::CharacterError) => Self::CharacterError(CharacterError::parse(body)?),
            Some(Opcode::ServerReady) => Self::ServerReady,
            Some(Opcode::DddInterrogation) => Self::DddInterrogation,
            Some(Opcode::DddBegin) => Self::DddBegin(DddBegin::parse(body)?),
            Some(Opcode::DddDataMessage) => Self::DddDataMessage,
            Some(Opcode::DddEnd) => {
                if body.len() != 4 {
                    return Err(ProtoError::InvalidLength {
                        what: "game-data check end",
                        actual: body.len(),
                    });
                }
                Self::DddEnd
            }
            Some(Opcode::CreateObject) => Self::CreateObject(Box::new(CreateObject::parse(body)?)),
            Some(Opcode::GameEvent) => Self::GameEvent(GameEventEnvelope::parse(body)?),
            Some(Opcode::PublicUpdatePropertyInt) => {
                Self::PublicUpdatePropertyInt(PublicUpdatePropertyInt::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyInt) => {
                Self::PrivateUpdatePropertyInt(PrivateUpdatePropertyInt::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyInt64) => {
                Self::PrivateUpdatePropertyInt64(PrivateUpdatePropertyInt64::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyBool) => {
                Self::PrivateUpdatePropertyBool(PrivateUpdatePropertyBool::parse(body)?)
            }
            Some(Opcode::PublicUpdatePropertyBool) => {
                Self::PublicUpdatePropertyBool(PublicUpdatePropertyBool::parse(body)?)
            }
            Some(Opcode::PublicUpdatePropertyString) => {
                Self::PublicUpdatePropertyString(PublicUpdatePropertyString::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyString) => {
                Self::PrivateUpdatePropertyString(PrivateUpdatePropertyString::parse(body)?)
            }
            Some(Opcode::PublicUpdatePropertyDataId) => {
                Self::PublicUpdatePropertyDataId(PublicUpdatePropertyDataId::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyDataId) => {
                Self::PrivateUpdatePropertyDataId(PrivateUpdatePropertyDataId::parse(body)?)
            }
            Some(Opcode::PublicUpdatePropertyInstanceId) => {
                Self::PublicUpdatePropertyInstanceId(PublicUpdatePropertyInstanceId::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePropertyInstanceId) => {
                Self::PrivateUpdatePropertyInstanceId(PrivateUpdatePropertyInstanceId::parse(body)?)
            }
            Some(Opcode::PrivateUpdatePosition) => {
                Self::PrivateUpdatePosition(PrivateUpdatePosition::parse(body)?)
            }
            Some(Opcode::PrivateUpdateVital) => {
                Self::PrivateUpdateVital(PrivateUpdateVital::parse(body)?)
            }
            Some(Opcode::PrivateUpdateVitalCurrent) => {
                Self::PrivateUpdateVitalCurrent(PrivateUpdateVitalCurrent::parse(body)?)
            }
            Some(Opcode::PrivateUpdateAttribute) => {
                Self::PrivateUpdateAttribute(PrivateUpdateAttribute::parse(body)?)
            }
            Some(Opcode::PrivateUpdateSkill) => {
                Self::PrivateUpdateSkill(PrivateUpdateSkill::parse(body)?)
            }
            Some(Opcode::SetStackSize) => Self::SetStackSize(SetStackSize::parse(body)?),
            Some(Opcode::InventoryRemoveObject) => {
                Self::InventoryRemoveObject(InventoryRemoveObject::parse(body)?)
            }
            Some(Opcode::UpdateObject) => {
                Self::UpdateObject(Box::new(CreateObject::parse_update(body)?))
            }
            Some(Opcode::DeleteObject) => Self::DeleteObject(DeleteObject::parse(body)?),
            Some(Opcode::PickupEvent) => Self::PickupEvent(PickupEvent::parse(body)?),
            Some(Opcode::ParentEvent) => Self::ParentEvent(ParentEvent::parse(body)?),
            Some(Opcode::SetState) => Self::SetState(SetState::parse(body)?),
            Some(Opcode::UpdatePosition) => Self::UpdatePosition(UpdatePosition::parse(body)?),
            Some(Opcode::VectorUpdate) => Self::VectorUpdate(VectorUpdate::parse(body)?),
            Some(Opcode::UpdateMotion) => Self::UpdateMotion(Box::new(UpdateMotion::parse(body)?)),
            Some(Opcode::ObjDescEvent) => Self::ObjDescEvent(ObjDescEvent::parse(body)?),
            Some(Opcode::PlayerTeleport) => Self::PlayerTeleport(PlayerTeleport::parse(body)?),
            Some(Opcode::HearSpeech | Opcode::HearRangedSpeech) => {
                Self::HearSpeech(crate::chat::HearSpeech::parse(body)?)
            }
            Some(Opcode::EmoteText) => Self::EmoteText(crate::chat::Emote::parse_emote_text(body)?),
            Some(Opcode::SoulEmote) => Self::SoulEmote(crate::chat::Emote::parse_soul_emote(body)?),
            Some(Opcode::ServerMessage) => {
                Self::ServerMessage(crate::chat::ServerMessage::parse(body)?)
            }
            Some(Opcode::TurbineChat) => {
                Self::TurbineChat(Box::new(crate::turbine_chat::TurbineChat::parse(body)?))
            }
            Some(Opcode::PlayerKilled) => {
                Self::PlayerKilled(crate::combat::PlayerKilled::parse(body)?)
            }
            Some(Opcode::CharacterDelete)
                if crate::character::is_character_delete_acknowledgement(body) =>
            {
                Self::CharacterDeleteAcknowledged
            }
            Some(Opcode::CharGenVerificationResponse) => Self::CharGenVerification(
                crate::character::CharGenVerificationResponse::parse(body)?,
            ),
            Some(Opcode::SoundEvent) => Self::SoundEvent(crate::effects::SoundEvent::parse(body)?),
            Some(Opcode::PlayScript) => Self::PlayScript(crate::effects::PlayScript::parse(body)?),
            Some(Opcode::PlayScriptType) => {
                Self::PlayScriptType(crate::effects::PlayScriptType::parse(body)?)
            }
            Some(Opcode::AdminEnvirons) => {
                Self::AdminEnvirons(crate::effects::AdminEnvirons::parse(body)?)
            }
            _ => Self::Other { opcode, body },
        })
    }
}

#[cfg(test)]
mod tests {
    //! Message-level parts of OpenAC
    //! `tests/AcDream.Core.Net.Tests/WorldSessionCharacterSelectionTests.cs` and
    //! `DataCheckTests.cs`: what each inbound body is recognised as.

    use ac_bits::Writer;

    use super::*;
    use crate::test_util::words;
    use crate::{CharacterErrorCode, GameEventType};

    fn roster(seconds_greyed_out: u32) -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF658);
        w.u32(0);
        w.u32(1);
        w.u32(0x5000_0001);
        w.string16l("Character").unwrap();
        w.u32(seconds_greyed_out);
        w.u32(0);
        w.u32(11);
        w.string16l("Canonical").unwrap();
        w.u32(1);
        w.u32(1);
        w.into_vec()
    }

    fn server_name() -> Vec<u8> {
        let mut w = Writer::new();
        w.u32(0xF7E1);
        w.i32(3);
        w.i32(100);
        w.string16l("sawato").unwrap();
        w.into_vec()
    }

    #[test]
    fn character_selection_replies_classify_in_wire_order() {
        let delete_ack = words(&[0xF655]);
        let error = words(&[0xF659, CharacterErrorCode::Delete.value()]);
        let bodies = [roster(0), delete_ack, error, server_name(), roster(1)];
        let parsed: Vec<Inbound<'_>> = bodies.iter().map(|b| Inbound::parse(b).unwrap()).collect();

        assert!(
            matches!(&parsed[0], Inbound::CharacterList(l) if l.characters[0].seconds_greyed_out == 0)
        );
        assert_eq!(parsed[1], Inbound::CharacterDeleteAcknowledged);
        // The request shape is not an acknowledgement.
        let request = crate::character::build_character_delete("acct", 1).unwrap();
        assert!(matches!(
            Inbound::parse(&request),
            Ok(Inbound::Other { opcode: 0xF655, .. })
        ));
        let created = words(&[0xF643, 3]);
        assert!(matches!(
            Inbound::parse(&created),
            Ok(Inbound::CharGenVerification(v)) if v.code == 3
        ));
        assert!(
            matches!(parsed[2], Inbound::CharacterError(e) if e.code() == Some(CharacterErrorCode::Delete))
        );
        assert!(
            matches!(&parsed[3], Inbound::ServerName(s) if s.world_name == "sawato" && s.current_connections == 3 && s.max_connections == 100)
        );
        assert!(
            matches!(&parsed[4], Inbound::CharacterList(l) if l.characters[0].seconds_greyed_out == 1)
        );
    }

    #[test]
    fn enter_world_replies() {
        let sentinel = words(&[0xF659, CharacterErrorCode::NumErrors.value()]);
        assert!(matches!(
            Inbound::parse(&sentinel),
            Ok(Inbound::CharacterError(e)) if e.is_sentinel()
        ));
        assert_eq!(Inbound::parse(&words(&[0xF7DF])), Ok(Inbound::ServerReady));
        assert!(matches!(
            Inbound::parse(&minimal_description(0xF745, 0x5000_0001)),
            Ok(Inbound::CreateObject(o)) if o.guid == 0x5000_0001
        ));
        // A body cut short of the physics block is dropped, as OpenAC
        // drops a CreateObject it cannot parse.
        assert!(Inbound::parse(&words(&[0xF745, 0x5000_0001, 0])).is_err());
        let event = words(&[0xF7B0, 0x5000_0001, 1, GameEventType::PLAYER_DESCRIPTION.0]);
        assert!(matches!(
            Inbound::parse(&event),
            Ok(Inbound::GameEvent(e)) if e.event_type == GameEventType::PLAYER_DESCRIPTION
        ));
    }

    /// Opcode, guid, empty appearance, empty physics block with zero
    /// counters, no description.
    fn minimal_description(opcode: u32, guid: u32) -> Vec<u8> {
        words(&[opcode, guid, 0x11, 0, 0, 0, 0, 0, 0, 0])
    }

    #[test]
    fn world_object_messages() {
        // Dispatch by opcode as OpenAC src/AcDream.Core.Net/WorldSession.cs
        // :: ProcessDatagram does.
        assert!(matches!(
            Inbound::parse(&minimal_description(0xF7DB, 7)),
            Ok(Inbound::UpdateObject(o)) if o.guid == 7
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF747, 7, 0x0003])),
            Ok(Inbound::DeleteObject(d)) if d.guid == 7 && d.instance_sequence == 3
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF74A, 7, 0x0004_0003])),
            Ok(Inbound::PickupEvent(p)) if p.position_sequence == 4
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF749, 1, 2, 3, 4, 0x0006_0005])),
            Ok(Inbound::ParentEvent(p)) if p.child_guid == 2 && p.child_position_sequence == 6
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF74B, 7, 0x14, 0x0002_0001])),
            Ok(Inbound::SetState(s)) if s.physics_state == 0x14 && s.state_sequence == 2
        ));
        let position = words(&[
            0xF748,
            7,
            0x78,
            0xA9B4_0001,
            0,
            0,
            0,
            0x0002_0001,
            0x0004_0003,
        ]);
        assert!(matches!(
            Inbound::parse(&position),
            Ok(Inbound::UpdatePosition(p)) if p.position.cell_id == 0xA9B4_0001 && p.teleport_sequence == 3
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF74E, 7, 0, 0, 0, 0, 0, 0, 0x0009_0008])),
            Ok(Inbound::VectorUpdate(v)) if v.vector_sequence == 9
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF74C, 7, 0, 0, 0x003D_0000, 0])),
            Ok(Inbound::UpdateMotion(m)) if m.movement.stance() == 0x3D
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF625, 7, 0x11, 0x0002_0001])),
            Ok(Inbound::ObjDescEvent(e)) if e.obj_desc_sequence == 2
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF751, 0x0000_0042])),
            Ok(Inbound::PlayerTeleport(t)) if t.teleport_sequence == 0x42
        ));
        assert!(Inbound::parse(&words(&[0xF625, 7, 0x11])).is_err());
    }

    #[test]
    fn logoff_confirmation_is_only_the_bare_opcode() {
        assert_eq!(
            Inbound::parse(&words(&[0xF653])),
            Ok(Inbound::LogOffConfirmed)
        );
        let request = words(&[0xF653, 0x5000_000A]);
        assert_eq!(
            Inbound::parse(&request),
            Ok(Inbound::Other {
                opcode: 0xF653,
                body: &request
            })
        );
    }

    #[test]
    fn data_check_messages() {
        assert_eq!(
            Inbound::parse(&words(&[0xF7E5, 1, 2, 3])),
            Ok(Inbound::DddInterrogation)
        );
        assert_eq!(
            Inbound::parse(&words(&[0xF7E7, 0, 0])),
            Ok(Inbound::DddBegin(DddBegin {
                expected_bytes: 0,
                iteration_count: 0
            }))
        );
        assert!(Inbound::parse(&words(&[0xF7E7, 1, 1])).is_err());
        assert_eq!(
            Inbound::parse(&words(&[0xF7E2, 9])),
            Ok(Inbound::DddDataMessage)
        );
        assert_eq!(Inbound::parse(&words(&[0xF7EA])), Ok(Inbound::DddEnd));
        assert_eq!(
            Inbound::parse(&words(&[0xF7EA, 0])),
            Err(ProtoError::InvalidLength {
                what: "game-data check end",
                actual: 8
            })
        );
    }

    #[test]
    fn quality_and_inventory_updates_classify() {
        // Opcode, one-byte sequence, then the fields; see each parser's tests.
        fn with_sequence(opcode: u32, rest: &[u32]) -> Vec<u8> {
            let mut w = Writer::new();
            w.u32(opcode);
            w.u8(3);
            for &word in rest {
                w.u32(word);
            }
            w.into_vec()
        }
        type Check = fn(&Inbound<'_>) -> bool;
        let cases: [(Vec<u8>, Check); 14] = [
            (
                with_sequence(0x02CE, &[1, 18, 9]),
                |m| matches!(m, Inbound::PublicUpdatePropertyInt(p) if p.guid == 1 && p.value == 9),
            ),
            (
                with_sequence(0x02CD, &[5, 1500]),
                |m| matches!(m, Inbound::PrivateUpdatePropertyInt(p) if p.value == 1500),
            ),
            (
                with_sequence(0x02CF, &[1, 7, 0]),
                |m| matches!(m, Inbound::PrivateUpdatePropertyInt64(p) if p.value == 7),
            ),
            (
                with_sequence(0x02D8, &[1, 52, 0x0600_1234]),
                |m| matches!(m, Inbound::PublicUpdatePropertyDataId(p) if p.value == 0x0600_1234),
            ),
            (
                with_sequence(0x02D7, &[8, 0x0600_5678]),
                |m| matches!(m, Inbound::PrivateUpdatePropertyDataId(p) if p.property == 8),
            ),
            (
                with_sequence(0x02DA, &[9, 3, 1]),
                |m| matches!(m, Inbound::PublicUpdatePropertyInstanceId(p) if p.guid == 9),
            ),
            (
                with_sequence(0x02D9, &[2, 2]),
                |m| matches!(m, Inbound::PrivateUpdatePropertyInstanceId(p) if p.property == 2),
            ),
            (
                with_sequence(0x02DB, &[0x0E, 0xC6A9_001F, 0, 0, 0, 0, 0, 0, 0]),
                |m| matches!(m, Inbound::PrivateUpdatePosition(p) if p.position.cell_id == 0xC6A9_001F),
            ),
            (
                with_sequence(0x02E7, &[2, 100, 12345, 67890, 100]),
                |m| matches!(m, Inbound::PrivateUpdateVital(p) if p.start == 12345),
            ),
            (
                with_sequence(0x02E9, &[2, 100]),
                |m| matches!(m, Inbound::PrivateUpdateVitalCurrent(p) if p.current == 100),
            ),
            (
                with_sequence(0x02E3, &[3, 41, 100, 1_010_895]),
                |m| matches!(m, Inbound::PrivateUpdateAttribute(p) if p.xp == 1_010_895),
            ),
            (
                with_sequence(0x02DD, &[6, 0x0001_0032, 3, 1000, 10, 0, 0, 0]),
                |m| matches!(m, Inbound::PrivateUpdateSkill(p) if p.ranks == 50 && p.adjust_pp == 1),
            ),
            (
                with_sequence(0x0197, &[0x5000_0A01, 25, 125]),
                |m| matches!(m, Inbound::SetStackSize(p) if p.stack_size == 25),
            ),
            (
                words(&[0x0024, 0x5000_0A07]),
                |m| matches!(m, Inbound::InventoryRemoveObject(p) if p.guid == 0x5000_0A07),
            ),
        ];
        for (body, check) in &cases {
            let parsed = Inbound::parse(body).unwrap();
            assert!(check(&parsed), "{parsed:?}");
            // A truncated message of a handled kind is an error, not Other.
            assert!(Inbound::parse(&body[..body.len() - 1]).is_err());
        }
    }

    #[test]
    fn string_updates_classify() {
        // Opcode, sequence, (object,) property, padding to 4, u16 length, text.
        let public = [
            0xD6, 0x02, 0, 0, 1, 0x2A, 0, 0, 0x50, 1, 0, 0, 0, 0, 0, 0, 2, 0, b'O', b'k',
        ];
        assert!(matches!(
            Inbound::parse(&public).unwrap(),
            Inbound::PublicUpdatePropertyString(p) if p.guid == 0x5000_002A && p.value == "Ok"
        ));
        let private = [0xD5, 0x02, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 2, 0, b'O', b'k'];
        assert!(matches!(
            Inbound::parse(&private).unwrap(),
            Inbound::PrivateUpdatePropertyString(p) if p.property == 1 && p.value == "Ok"
        ));
        assert!(Inbound::parse(&public[..public.len() - 1]).is_err());
        assert!(Inbound::parse(&private[..private.len() - 1]).is_err());
    }

    #[test]
    fn chat_messages_classify() {
        // Dispatch by opcode as OpenAC `WorldSession.ProcessDatagram` does
        // for 0x02BB/0x02BC, 0x01E0, 0x01E2, 0xF7E0 and 0xF7DE.
        fn strings(opcode: u32, head: &[u32], texts: &[&str], tail: &[u32]) -> Vec<u8> {
            let mut w = Writer::new();
            w.u32(opcode);
            for &word in head {
                w.u32(word);
            }
            for text in texts {
                w.string16l(text).unwrap();
            }
            for &word in tail {
                w.u32(word);
            }
            w.into_vec()
        }
        type Check = fn(&Inbound<'_>) -> bool;
        let cases: [(Vec<u8>, Check); 7] = [
            (
                strings(0x02BB, &[], &["hi", "Al"], &[7, 0x0B]),
                |m| matches!(m, Inbound::HearSpeech(s) if s.text == "hi" && !s.is_ranged()),
            ),
            (
                strings(0x02BC, &[], &["hi", "Al"], &[7, 60f32.to_bits(), 0x0B]),
                |m| matches!(m, Inbound::HearSpeech(s) if s.range == Some(60.0)),
            ),
            (
                strings(0x01E0, &[7], &["Al", "waves"], &[]),
                |m| matches!(m, Inbound::EmoteText(e) if e.text == "waves"),
            ),
            (
                strings(0x01E2, &[7], &["Al", "bows"], &[]),
                |m| matches!(m, Inbound::SoulEmote(e) if e.text == "bows"),
            ),
            (
                strings(0xF7E0, &[], &["hi"], &[5]),
                |m| matches!(m, Inbound::ServerMessage(s) if s.chat_type == 5),
            ),
            (
                {
                    // Response blob with an empty-but-sized payload of four words.
                    let mut body = words(&[0xF7DE, 56, 5, 1, 1, 0, 1, 0, 0, 24]);
                    body.extend(words(&[7, 2, 2, 0]));
                    body
                },
                |m| matches!(m, Inbound::TurbineChat(t) if t.cookie == 0),
            ),
            (
                strings(0x019E, &[], &["Al died"], &[1, 2]),
                |m| matches!(m, Inbound::PlayerKilled(k) if k.killer_guid == 2),
            ),
        ];
        for (body, check) in &cases {
            let parsed = Inbound::parse(body).unwrap();
            assert!(check(&parsed), "{parsed:?}");
            assert!(Inbound::parse(&body[..body.len() - 4]).is_err());
        }
    }

    #[test]
    fn effect_messages_classify() {
        // OpenAC `WorldSession.ProcessDatagram` branches for 0xF750,
        // 0xF754, 0xF755 and 0xEA60.
        assert!(matches!(
            Inbound::parse(&words(&[0xF750, 7, 3, 0])),
            Ok(Inbound::SoundEvent(s)) if s.sound_type == 3
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF754, 7, 0x3300_0001])),
            Ok(Inbound::PlayScript(s)) if s.script_id == 0x3300_0001
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xF755, 7, 0x21, 0])),
            Ok(Inbound::PlayScriptType(s)) if s.script_type == 0x21
        ));
        assert!(matches!(
            Inbound::parse(&words(&[0xEA60, 0x65])),
            Ok(Inbound::AdminEnvirons(e)) if e.environ_type == 0x65
        ));
        assert!(Inbound::parse(&words(&[0xF754, 7, 1, 0])).is_err());
        assert!(Inbound::parse(&words(&[0xEA60])).is_err());
    }

    #[test]
    fn short_and_malformed_bodies_are_errors() {
        for len in 0..4 {
            assert!(Inbound::parse(&[0x58, 0xF6, 0, 0][..len]).is_err());
        }
        let list = roster(0);
        for len in 4..list.len() {
            assert!(Inbound::parse(&list[..len]).is_err());
        }
    }
}
