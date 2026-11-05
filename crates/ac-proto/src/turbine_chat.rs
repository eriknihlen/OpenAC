//! Turbine chat: the room-based General, Trade, LFG, Roleplay, Society,
//! Olthoi and allegiance channels ([`Opcode::TurbineChat`]), and the event
//! that tells the client which room id each channel uses.
//!
//! A turbine chat message is a header of seven `u32` fields framed by two
//! size words, then a payload whose shape depends on the blob and dispatch
//! types. Its strings are UTF-16, not Windows-1252.

use ac_bits::{Reader, Writer};

use crate::{Opcode, ProtoError, Result};

/// Bytes between the opcode and the payload.
pub const HEADER_SIZE: usize = 36;

/// The response and method ids a send-to-room-by-id request carries.
const SEND_TO_ROOM_BY_ID: u32 = 2;

/// Extra-data size the client writes in its own requests.
const REQUEST_EXTRA_DATA_SIZE: u32 = 0x0C;

/// How the payload is encoded.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum BlobType {
    /// Not set.
    Unknown = 0,
    /// A server event, binary.
    EventBinary = 1,
    /// A server event, XML-RPC.
    EventXmlRpc = 2,
    /// A request, binary.
    RequestBinary = 3,
    /// A request, XML-RPC.
    RequestXmlRpc = 4,
    /// A response, binary.
    ResponseBinary = 5,
    /// A response, XML-RPC.
    ResponseXmlRpc = 6,
}

impl BlobType {
    fn from_u32(value: u32) -> Result<Self> {
        Ok(match value {
            0 => Self::Unknown,
            1 => Self::EventBinary,
            2 => Self::EventXmlRpc,
            3 => Self::RequestBinary,
            4 => Self::RequestXmlRpc,
            5 => Self::ResponseBinary,
            6 => Self::ResponseXmlRpc,
            _ => {
                return Err(ProtoError::InvalidValue {
                    what: "turbine chat blob type",
                    value,
                });
            }
        })
    }
}

/// What the message asks for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DispatchType {
    /// Not set.
    Unknown = 0,
    /// Send to a room named by string.
    SendToRoomByName = 1,
    /// Send to a room named by id.
    SendToRoomById = 2,
    /// Create a room.
    CreateRoom = 3,
    /// Invite a client into a room.
    InviteClientToRoomById = 4,
    /// Eject a client from a room.
    EjectClientFromRoomById = 5,
}

impl DispatchType {
    fn from_u32(value: u32) -> Result<Self> {
        Ok(match value {
            0 => Self::Unknown,
            1 => Self::SendToRoomByName,
            2 => Self::SendToRoomById,
            3 => Self::CreateRoom,
            4 => Self::InviteClientToRoomById,
            5 => Self::EjectClientFromRoomById,
            _ => {
                return Err(ProtoError::InvalidValue {
                    what: "turbine chat dispatch type",
                    value,
                });
            }
        })
    }
}

/// The turbine chat channels, as the chat type of a line.
pub mod chat_type {
    /// Not set.
    pub const UNDEF: u32 = 0;
    /// Allegiance.
    pub const ALLEGIANCE: u32 = 1;
    /// General.
    pub const GENERAL: u32 = 2;
    /// Trade.
    pub const TRADE: u32 = 3;
    /// Looking for group.
    pub const LFG: u32 = 4;
    /// Roleplay.
    pub const ROLEPLAY: u32 = 5;
    /// Society.
    pub const SOCIETY: u32 = 6;
    /// Celestial Hand.
    pub const SOCIETY_CELESTIAL_HAND: u32 = 7;
    /// Eldrytch Web.
    pub const SOCIETY_ELDRYTCH_WEB: u32 = 8;
    /// Radiant Blood.
    pub const SOCIETY_RADIANT_BLOOD: u32 = 9;
    /// Olthoi.
    pub const OLTHOI: u32 = 10;
}

/// The payload shapes the client understands.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Payload {
    /// A line said in a room (binary event, send to room by name).
    EventSendToRoom {
        /// The room.
        room_id: u32,
        /// Who said it.
        sender_name: String,
        /// What was said.
        message: String,
        /// Size of the trailing extra data the server describes.
        extra_data_size: u32,
        /// The speaker's id.
        sender_id: u32,
        /// Result code.
        hresult: i32,
        /// Chat type (see [`chat_type`]).
        chat_type: u32,
    },
    /// The client's line for a room (binary request, send to room by id).
    RequestSendToRoomById {
        /// The client's request number, echoed in the response.
        context_id: u32,
        /// The room.
        room_id: u32,
        /// What to say.
        message: String,
        /// Size of the trailing extra data.
        extra_data_size: u32,
        /// The speaker's id.
        sender_id: u32,
        /// Result code.
        hresult: i32,
        /// Chat type (see [`chat_type`]).
        chat_type: u32,
    },
    /// The answer to a request (binary response, any dispatch type).
    Response {
        /// The request it answers.
        context_id: u32,
        /// Response id.
        response_id: u32,
        /// Method id.
        method_id: u32,
        /// Result code; zero is success.
        hresult: i32,
    },
    /// Any other combination, kept as its raw bytes.
    Unknown(Vec<u8>),
}

/// A turbine chat message ([`Opcode::TurbineChat`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TurbineChat {
    /// Payload encoding.
    pub blob_type: BlobType,
    /// What the message asks for.
    pub dispatch_type: DispatchType,
    /// Target type.
    pub target_type: u32,
    /// Target id.
    pub target_id: u32,
    /// Transport type.
    pub transport_type: u32,
    /// Transport id.
    pub transport_id: u32,
    /// Cookie.
    pub cookie: u32,
    /// The payload.
    pub payload: Payload,
}

impl TurbineChat {
    /// Parse a body: opcode, total size, blob type, dispatch type, target
    /// type and id, transport type and id, cookie, payload size plus 8, then
    /// the payload. Bytes the payload does not use up to its stated size are
    /// skipped.
    pub fn parse(body: &[u8]) -> Result<Self> {
        let mut r = Reader::new(body);
        crate::expect_opcode(&mut r, Opcode::TurbineChat)?;
        let _total_size = r.u32()?;
        let blob_type = r.u32()?;
        let dispatch_type = r.u32()?;
        let target_type = r.u32()?;
        let target_id = r.u32()?;
        let transport_type = r.u32()?;
        let transport_id = r.u32()?;
        let cookie = r.u32()?;
        let size = r.u32()?;
        let Some(payload_size) = size.checked_sub(8) else {
            return Err(ProtoError::InvalidValue {
                what: "turbine chat payload size",
                value: size,
            });
        };
        if payload_size as usize > r.remaining() {
            return Err(ProtoError::CountExceedsPayload {
                what: "turbine chat payload bytes",
                count: payload_size,
            });
        }
        let blob_type = BlobType::from_u32(blob_type)?;
        let dispatch_type = DispatchType::from_u32(dispatch_type)?;
        let start = r.position();
        let payload = match (blob_type, dispatch_type) {
            (BlobType::EventBinary, DispatchType::SendToRoomByName) => Payload::EventSendToRoom {
                room_id: r.u32()?,
                sender_name: read_turbine_string(&mut r)?,
                message: read_turbine_string(&mut r)?,
                extra_data_size: r.u32()?,
                sender_id: r.u32()?,
                hresult: r.i32()?,
                chat_type: r.u32()?,
            },
            (BlobType::RequestBinary, DispatchType::SendToRoomById) => {
                let context_id = r.u32()?;
                let response_id = r.u32()?;
                let method_id = r.u32()?;
                let room_id = r.u32()?;
                for (what, value) in [
                    ("turbine chat response id", response_id),
                    ("turbine chat method id", method_id),
                ] {
                    if value != SEND_TO_ROOM_BY_ID {
                        return Err(ProtoError::InvalidValue { what, value });
                    }
                }
                Payload::RequestSendToRoomById {
                    context_id,
                    room_id,
                    message: read_turbine_string(&mut r)?,
                    extra_data_size: r.u32()?,
                    sender_id: r.u32()?,
                    hresult: r.i32()?,
                    chat_type: r.u32()?,
                }
            }
            (BlobType::ResponseBinary, _) => Payload::Response {
                context_id: r.u32()?,
                response_id: r.u32()?,
                method_id: r.u32()?,
                hresult: r.i32()?,
            },
            _ => Payload::Unknown(r.bytes(payload_size as usize)?.to_vec()),
        };
        let consumed = r.position() - start;
        if consumed < payload_size as usize {
            r.skip(payload_size as usize - consumed)?;
        }
        Ok(Self {
            blob_type,
            dispatch_type,
            target_type,
            target_id,
            transport_type,
            transport_id,
            cookie,
            payload,
        })
    }

    /// The message body, opcode first. The two size words are computed from
    /// the payload.
    pub fn build(&self) -> Result<Vec<u8>> {
        let mut payload = Writer::new();
        match &self.payload {
            Payload::EventSendToRoom {
                room_id,
                sender_name,
                message,
                extra_data_size,
                sender_id,
                hresult,
                chat_type,
            } => {
                payload.u32(*room_id);
                write_turbine_string(&mut payload, sender_name)?;
                write_turbine_string(&mut payload, message)?;
                payload.u32(*extra_data_size);
                payload.u32(*sender_id);
                payload.i32(*hresult);
                payload.u32(*chat_type);
            }
            Payload::RequestSendToRoomById {
                context_id,
                room_id,
                message,
                extra_data_size,
                sender_id,
                hresult,
                chat_type,
            } => {
                payload.u32(*context_id);
                payload.u32(SEND_TO_ROOM_BY_ID);
                payload.u32(SEND_TO_ROOM_BY_ID);
                payload.u32(*room_id);
                write_turbine_string(&mut payload, message)?;
                payload.u32(*extra_data_size);
                payload.u32(*sender_id);
                payload.i32(*hresult);
                payload.u32(*chat_type);
            }
            Payload::Response {
                context_id,
                response_id,
                method_id,
                hresult,
            } => {
                payload.u32(*context_id);
                payload.u32(*response_id);
                payload.u32(*method_id);
                payload.i32(*hresult);
            }
            Payload::Unknown(bytes) => payload.bytes(bytes),
        }
        let payload = payload.into_vec();
        let payload_len = u32::try_from(payload.len())
            .ok()
            .filter(|len| *len <= u32::MAX - 40)
            .ok_or(ProtoError::InvalidArgument {
                what: "turbine chat payload",
            })?;
        let mut w = Writer::with_capacity(4 + HEADER_SIZE + payload.len());
        w.u32(Opcode::TurbineChat.value());
        w.u32(40 + payload_len);
        w.u32(self.blob_type as u32);
        w.u32(self.dispatch_type as u32);
        w.u32(self.target_type);
        w.u32(self.target_id);
        w.u32(self.transport_type);
        w.u32(self.transport_id);
        w.u32(self.cookie);
        w.u32(8 + payload_len);
        w.bytes(&payload);
        Ok(w.into_vec())
    }
}

/// Say `text` in turbine chat room `room_id`. `cookie` is the client's
/// request number (the response echoes it); the message's own header
/// cookie is zero. Sent as a plain message on the UI queue; it does not
/// take a game-action sequence number.
pub fn build_turbine_chat_to_room(
    room_id: u32,
    chat_type: u32,
    sender_guid: u32,
    text: &str,
    cookie: u32,
) -> Result<Vec<u8>> {
    TurbineChat {
        blob_type: BlobType::RequestBinary,
        dispatch_type: DispatchType::SendToRoomById,
        target_type: 1,
        target_id: 0,
        transport_type: 0,
        transport_id: 0,
        cookie: 0,
        payload: Payload::RequestSendToRoomById {
            context_id: cookie,
            room_id,
            message: text.to_owned(),
            extra_data_size: REQUEST_EXTRA_DATA_SIZE,
            sender_id: sender_guid,
            hresult: 0,
            chat_type,
        },
    }
    .build()
}

/// A turbine string: a length in UTF-16 code units (one byte below 0x80,
/// else two bytes, high byte first with its top bit set), then that many
/// UTF-16LE units. Unpaired surrogates decode as U+FFFD.
pub fn read_turbine_string(r: &mut Reader<'_>) -> Result<String> {
    let first = r.u8()?;
    let units = if first & 0x80 != 0 {
        (usize::from(first & 0x7F) << 8) | usize::from(r.u8()?)
    } else {
        usize::from(first)
    };
    let bytes = r.bytes(units * 2)?;
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
        .collect();
    Ok(String::from_utf16_lossy(&units))
}

/// Write a turbine string; at most 0x7FFF UTF-16 code units.
pub fn write_turbine_string(w: &mut Writer, text: &str) -> Result<()> {
    let units: Vec<u16> = text.encode_utf16().collect();
    let count = units.len();
    if count >= 0x8000 {
        return Err(ProtoError::InvalidArgument {
            what: "turbine string length",
        });
    }
    if count < 0x80 {
        w.u8(count as u8);
    } else {
        w.u8(0x80 | (count >> 8) as u8);
        w.u8(count as u8);
    }
    for unit in units {
        w.u16(unit);
    }
    Ok(())
}

/// Which turbine chat room each channel uses (game event
/// [`crate::GameEventType::SET_TURBINE_CHAT_CHANNELS`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct TurbineChatChannels {
    /// Allegiance room.
    pub allegiance: u32,
    /// General room.
    pub general: u32,
    /// Trade room.
    pub trade: u32,
    /// Looking-for-group room.
    pub lfg: u32,
    /// Roleplay room.
    pub roleplay: u32,
    /// Olthoi room.
    pub olthoi: u32,
    /// The player's society room.
    pub society: u32,
    /// Celestial Hand room.
    pub society_celestial_hand: u32,
    /// Eldrytch Web room.
    pub society_eldrytch_web: u32,
    /// Radiant Blood room.
    pub society_radiant_blood: u32,
}

impl TurbineChatChannels {
    /// Payload size: ten room ids.
    pub const PAYLOAD_SIZE: usize = 40;

    /// Parse the event payload: the ten room ids in field order.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            allegiance: r.u32()?,
            general: r.u32()?,
            trade: r.u32()?,
            lfg: r.u32()?,
            roleplay: r.u32()?,
            olthoi: r.u32()?,
            society: r.u32()?,
            society_celestial_hand: r.u32()?,
            society_eldrytch_web: r.u32()?,
            society_radiant_blood: r.u32()?,
        })
    }

    /// The event payload.
    #[must_use]
    pub fn to_payload(&self) -> Vec<u8> {
        let mut w = Writer::with_capacity(Self::PAYLOAD_SIZE);
        for room in [
            self.allegiance,
            self.general,
            self.trade,
            self.lfg,
            self.roleplay,
            self.olthoi,
            self.society,
            self.society_celestial_hand,
            self.society_eldrytch_web,
            self.society_radiant_blood,
        ] {
            w.u32(room);
        }
        w.into_vec()
    }
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC `tests/AcDream.Core.Net.Tests/Messages/`
    //! `TurbineChatTests.cs` and `SetTurbineChatChannelsTests.cs` (golden
    //! fixtures from captured traffic), and the body shape of
    //! `WorldSession.SendTurbineChatTo`.

    use super::*;
    use crate::GameEventEnvelope;
    use crate::GameEventType;
    use crate::test_util::assert_every_prefix_fails;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    const EVENT_FIXTURE: &str = concat!(
        "DEF700005E000000010000000100000001000000",
        "B5000B0001000000B5000B00000000003E000000",
        "020000000541006C006900630065000B68006500",
        "6C006C006F00200077006F0072006C0064000C00",
        "0000010000500000000002000000"
    );

    const REQUEST_FIXTURE: &str = concat!(
        "DEF700005D000000030000000200000001000000",
        "00000000000000000000000000000000",
        "3D000000",
        "07000000020000000200000002000000",
        "0A7400720061006400650020007300700061006D00",
        "0C000000010000500000000003000000"
    );

    #[test]
    fn event_send_to_room_golden_fixture() {
        let parsed = TurbineChat::parse(&hex(EVENT_FIXTURE)).unwrap();
        assert_eq!(parsed.blob_type, BlobType::EventBinary);
        assert_eq!(parsed.dispatch_type, DispatchType::SendToRoomByName);
        assert_eq!(parsed.target_type, 1);
        assert_eq!(parsed.target_id, 0x000B_00B5);
        assert_eq!(parsed.transport_type, 1);
        assert_eq!(parsed.transport_id, 0x000B_00B5);
        assert_eq!(parsed.cookie, 0);
        assert_eq!(
            parsed.payload,
            Payload::EventSendToRoom {
                room_id: 2,
                sender_name: "Alice".into(),
                message: "hello world".into(),
                extra_data_size: 0x0C,
                sender_id: 0x5000_0001,
                hresult: 0,
                chat_type: chat_type::GENERAL,
            }
        );
        // The builder reproduces the capture byte for byte.
        assert_eq!(parsed.build().unwrap(), hex(EVENT_FIXTURE));
        assert_every_prefix_fails(&hex(EVENT_FIXTURE), TurbineChat::parse);
    }

    #[test]
    fn response_golden_fixture() {
        let fixture = hex(concat!(
            "DEF7000038000000050000000100000001000000",
            "B5000B0001000000B5000B00000000001800000007000000",
            "020000000200000000000000"
        ));
        let parsed = TurbineChat::parse(&fixture).unwrap();
        assert_eq!(parsed.blob_type, BlobType::ResponseBinary);
        assert_eq!(parsed.dispatch_type, DispatchType::SendToRoomByName);
        assert_eq!(
            parsed.payload,
            Payload::Response {
                context_id: 7,
                response_id: 2,
                method_id: 2,
                hresult: 0
            }
        );
        assert_eq!(parsed.build().unwrap(), fixture);
    }

    #[test]
    fn request_send_to_room_by_id_golden_fixture() {
        let parsed = TurbineChat::parse(&hex(REQUEST_FIXTURE)).unwrap();
        assert_eq!(parsed.blob_type, BlobType::RequestBinary);
        assert_eq!(parsed.dispatch_type, DispatchType::SendToRoomById);
        assert_eq!(
            parsed.payload,
            Payload::RequestSendToRoomById {
                context_id: 7,
                room_id: 2,
                message: "trade spam".into(),
                extra_data_size: 0x0C,
                sender_id: 0x5000_0001,
                hresult: 0,
                chat_type: chat_type::TRADE,
            }
        );
        assert_eq!(parsed.build().unwrap(), hex(REQUEST_FIXTURE));
    }

    #[test]
    fn request_rejects_other_response_ids() {
        let mut fixture = hex(REQUEST_FIXTURE);
        fixture[44..48].copy_from_slice(&3u32.to_le_bytes());
        assert!(matches!(
            TurbineChat::parse(&fixture),
            Err(ProtoError::InvalidValue { value: 3, .. })
        ));
    }

    #[test]
    fn unknown_types_sizes_and_slack() {
        // Blob type 7 is outside the set.
        let mut fixture = hex(REQUEST_FIXTURE);
        fixture[8..12].copy_from_slice(&7u32.to_le_bytes());
        assert!(TurbineChat::parse(&fixture).is_err());
        // An XML event keeps its payload as raw bytes.
        let raw = TurbineChat {
            blob_type: BlobType::EventXmlRpc,
            dispatch_type: DispatchType::Unknown,
            target_type: 0,
            target_id: 0,
            transport_type: 0,
            transport_id: 0,
            cookie: 0,
            payload: Payload::Unknown(vec![1, 2, 3]),
        };
        assert_eq!(TurbineChat::parse(&raw.build().unwrap()), Ok(raw));
        // A stated payload larger than what follows is an error, and so is
        // a size word below its own 8 bytes.
        let mut short = hex(REQUEST_FIXTURE);
        short[36..40].copy_from_slice(&0x100u32.to_le_bytes());
        assert!(TurbineChat::parse(&short).is_err());
        let mut tiny = hex(REQUEST_FIXTURE);
        tiny[36..40].copy_from_slice(&7u32.to_le_bytes());
        assert!(TurbineChat::parse(&tiny).is_err());
        // A stated payload smaller than the fields use is read through.
        let mut under = hex(REQUEST_FIXTURE);
        under[36..40].copy_from_slice(&8u32.to_le_bytes());
        assert_eq!(
            TurbineChat::parse(&under).unwrap().payload,
            TurbineChat::parse(&hex(REQUEST_FIXTURE)).unwrap().payload
        );
        // Unused bytes inside the stated payload are skipped.
        let mut slack = hex(REQUEST_FIXTURE);
        slack.extend_from_slice(&[0xEE; 3]);
        slack[36..40].copy_from_slice(&(0x3D + 3u32).to_le_bytes());
        assert!(TurbineChat::parse(&slack).is_ok());
    }

    #[test]
    fn send_to_room_body_matches_the_session_wrapper() {
        // OpenAC `WorldSession.SendTurbineChatTo`: request blob, send by
        // id, target type 1, extra data 0x0C, the cookie as context id.
        let body =
            build_turbine_chat_to_room(2, chat_type::TRADE, 0x5000_0001, "trade spam", 7).unwrap();
        assert_eq!(body, hex(REQUEST_FIXTURE));
    }

    fn string_bytes(text: &str) -> Vec<u8> {
        let mut w = Writer::new();
        write_turbine_string(&mut w, text).unwrap();
        w.into_vec()
    }

    #[test]
    fn turbine_strings() {
        let short = string_bytes("Hello");
        assert_eq!(short.len(), 11);
        assert_eq!(short[0], 5);
        let long_text = "a".repeat(200);
        let long = string_bytes(&long_text);
        assert_eq!(long[..2], [0x80, 200]);
        assert_eq!(long.len(), 2 + 400);
        let accented = string_bytes("Café");
        assert_eq!(accented.len(), 9);
        assert_eq!(accented[7..9], [0xE9, 0x00]);
        assert_eq!(string_bytes(""), [0]);
        for (bytes, text) in [
            (short, "Hello"),
            (long, long_text.as_str()),
            (accented, "Café"),
        ] {
            let mut r = Reader::new(&bytes);
            assert_eq!(read_turbine_string(&mut r).as_deref(), Ok(text));
            assert_eq!(r.remaining(), 0);
        }
        let mut w = Writer::new();
        assert!(write_turbine_string(&mut w, &"a".repeat(0x8000)).is_err());
        assert!(read_turbine_string(&mut Reader::new(&[2, b'a', 0])).is_err());
    }

    #[test]
    fn channel_assignment_reads_all_rooms() {
        let full = hex(concat!(
            "B0F7000000000000210000009502000078563412",
            "020000000300000004000000050000000A000000",
            "09000000070000000800000009000000"
        ));
        let env = GameEventEnvelope::parse(&full).unwrap();
        assert_eq!(env.event_type, GameEventType::SET_TURBINE_CHAT_CHANNELS);
        let rooms = TurbineChatChannels::parse(env.payload).unwrap();
        assert_eq!(
            rooms,
            TurbineChatChannels {
                allegiance: 0x1234_5678,
                general: 2,
                trade: 3,
                lfg: 4,
                roleplay: 5,
                olthoi: 10,
                society: 9,
                society_celestial_hand: 7,
                society_eldrytch_web: 8,
                society_radiant_blood: 9,
            }
        );
        let payload = rooms.to_payload();
        assert_eq!(payload.len(), TurbineChatChannels::PAYLOAD_SIZE);
        assert_eq!(TurbineChatChannels::parse(&payload), Ok(rooms));
        assert!(TurbineChatChannels::parse(&[0; 39]).is_err());
    }

    // OpenAC `OutboundFragmentationTests.LongChannelMessages_AreTransmittedWhole`:
    // two real channel messages build bodies past
    // one fragment's 448 bytes (the transport splits them; `ac-net`
    // `transport::outbound::tests::every_body_size_round_trips_with_one_message_identity`).
    #[test]
    fn long_channel_messages_build_bodies_past_one_fragment() {
        for (text, size) in [
            (
                "Keep heading north until you get to a long room with a throne. This is the first checkpoint. The wall behind the throne will only open if all the Thugs prior to this point have been killed",
                450,
            ),
            (
                "From the drop stick to the middle until you come to a room with an acid pit in the middle, go north to a small room where you'll go east, continue east until you come to a large L shaped room",
                456,
            ),
        ] {
            let body = build_turbine_chat_to_room(2, 0, 3, text, 1).unwrap();
            assert_eq!(body.len(), size);
        }
    }
}
