use ac_bits::Reader;

use super::fragment::{Fragments, parse_fragment};
use super::{PacketFlags, PacketHeader};
use crate::crypto::{Isaac, hash32};

/// Why a datagram could not be read as a packet.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum PacketError {
    /// Shorter than the 20-byte header.
    #[error("datagram shorter than the packet header")]
    TooShort,
    /// The header's body length runs past the end of the datagram.
    #[error("header data size exceeds the datagram")]
    BodyExceedsDatagram,
    /// An optional section the flags announce is truncated or malformed.
    #[error("malformed optional header section")]
    InvalidOptionalHeader,
    /// A fragment header is inconsistent or its payload is truncated.
    #[error("malformed message fragment")]
    InvalidFragment,
    /// The checksum does not match.
    #[error("checksum mismatch")]
    ChecksumMismatch,
}

/// Why a packet could not be built.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum EncodeError {
    /// The body does not fit the header's 16-bit size field.
    #[error("body of {0} bytes exceeds the 65535-byte limit")]
    BodyTooLarge(usize),
    /// The buffer is shorter than header plus body.
    #[error("buffer too small for the packet")]
    BufferTooSmall,
    /// The optional section is longer than the body.
    #[error("optional section longer than the body")]
    OptionalLengthOutOfRange,
    /// The optional section the flags announce is malformed.
    #[error("malformed optional header section")]
    MalformedOptional,
    /// A packet without fragments has bytes after its optional section.
    #[error("non-fragment body has bytes outside the optional section")]
    StrayBodyBytes,
    /// The fragment area holds a malformed fragment.
    #[error("body contains a malformed fragment")]
    MalformedFragment,
    /// The flags ask for a sealed checksum but no keystream was given.
    #[error("encrypted checksum requested without a keystream")]
    MissingKeystream,
}

/// The server's connect request: the clock, the cookie to echo, our client
/// id and the two keystream seeds.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ConnectRequest {
    /// Server time in seconds.
    pub server_time: f64,
    /// Cookie the client echoes in its connect response.
    pub cookie: u64,
    /// The client id to stamp on every packet (low 16 bits are used).
    pub client_id: u32,
    /// Seed of the server's keystream (seals packets we receive).
    pub server_seed: u32,
    /// Seed of our keystream (seals packets we send).
    pub client_seed: u32,
}

/// A list of 32-bit sequence numbers borrowed from a packet body.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SequenceList<'a> {
    bytes: &'a [u8],
}

impl<'a> SequenceList<'a> {
    /// A list over raw little-endian id bytes (a trailing partial id is
    /// ignored).
    #[must_use]
    pub fn from_raw(bytes: &'a [u8]) -> Self {
        Self {
            bytes: &bytes[..bytes.len() - bytes.len() % 4],
        }
    }

    /// Number of ids.
    #[must_use]
    pub fn len(&self) -> usize {
        self.bytes.len() / 4
    }

    /// True when there are no ids.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.bytes.is_empty()
    }

    /// The ids in wire order.
    pub fn iter(&self) -> impl Iterator<Item = u32> + 'a {
        self.bytes
            .chunks_exact(4)
            .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
    }
}

/// The optional sections between the header and the fragments. Fields the
/// flags do not announce hold zero.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct OptionalHeader<'a> {
    /// The raw optional bytes (hashed into the checksum as one block).
    pub raw: &'a [u8],
    /// Cumulative acknowledgement.
    pub ack_sequence: u32,
    /// Sequences the peer asks us to resend.
    pub retransmit_requests: SequenceList<'a>,
    /// Sequences the peer will never resend.
    pub reject_retransmits: SequenceList<'a>,
    /// The peer's clock.
    pub time_sync: f64,
    /// Echo request time.
    pub echo_request_time: f32,
    /// Flow report: bytes.
    pub flow_bytes: u32,
    /// Flow report: interval.
    pub flow_interval: u16,
    /// The connect request, when the flags carry one.
    pub connect_request: Option<ConnectRequest>,
}

fn read_list<'a>(r: &mut Reader<'a>) -> ac_bits::Result<Option<SequenceList<'a>>> {
    let count = r.u32()? as usize;
    if count > 1024 || r.remaining() < count * 4 {
        return Ok(None);
    }
    Ok(Some(SequenceList {
        bytes: r.bytes(count * 4)?,
    }))
}

/// Parse the optional sections announced by `flags` from the front of
/// `body`; returns them and the bytes they occupy.
fn parse_optional(body: &[u8], flags: PacketFlags) -> Option<(OptionalHeader<'_>, usize)> {
    parse_optional_inner(body, flags).ok().flatten()
}

fn parse_optional_inner(
    body: &[u8],
    flags: PacketFlags,
) -> ac_bits::Result<Option<(OptionalHeader<'_>, usize)>> {
    let mut r = Reader::new(body);
    let mut o = OptionalHeader::default();
    if flags.has(PacketFlags::SERVER_SWITCH) {
        r.skip(8)?;
    }
    if flags.has(PacketFlags::REQUEST_RETRANSMIT) {
        let Some(list) = read_list(&mut r)? else {
            return Ok(None);
        };
        o.retransmit_requests = list;
    }
    if flags.has(PacketFlags::REJECT_RETRANSMIT) {
        let Some(list) = read_list(&mut r)? else {
            return Ok(None);
        };
        o.reject_retransmits = list;
    }
    if flags.has(PacketFlags::ACK_SEQUENCE) {
        o.ack_sequence = r.u32()?;
    }
    if flags.has(PacketFlags::LOGIN_REQUEST) {
        // The login payload is the rest of the body.
        o.raw = body;
        return Ok(Some((o, body.len())));
    }
    if flags.has(PacketFlags::WORLD_LOGIN_REQUEST) {
        r.skip(8)?;
    }
    if flags.has(PacketFlags::CONNECT_REQUEST) {
        let server_time = r.f64()?;
        let cookie = r.u64()?;
        let client_id = r.u32()?;
        let server_seed = r.u32()?;
        let client_seed = r.u32()?;
        r.skip(4)?; // trailing padding word
        o.connect_request = Some(ConnectRequest {
            server_time,
            cookie,
            client_id,
            server_seed,
            client_seed,
        });
    }
    if flags.has(PacketFlags::CONNECT_RESPONSE) {
        r.skip(8)?;
    }
    if flags.has(PacketFlags::CICMD_COMMAND) {
        r.skip(8)?;
    }
    if flags.has(PacketFlags::TIME_SYNC) {
        o.time_sync = r.f64()?;
    }
    if flags.has(PacketFlags::ECHO_REQUEST) {
        o.echo_request_time = r.f32()?;
    }
    if flags.has(PacketFlags::FLOW) {
        o.flow_bytes = r.u32()?;
        o.flow_interval = r.u16()?;
    }
    let used = r.position();
    o.raw = &body[..used];
    Ok(Some((o, used)))
}

/// A packet parsed in place from a datagram. Parsing never touches a
/// keystream; the caller decides which key (if any) to check against.
#[derive(Debug, Clone, Copy)]
pub struct ParsedPacket<'a> {
    /// The header.
    pub header: PacketHeader,
    /// The optional sections.
    pub optional: OptionalHeader<'a>,
    /// The whole body (`data_size` bytes after the header).
    pub body: &'a [u8],
    /// Number of fragments.
    pub fragment_count: usize,
    /// The header's share of the checksum.
    pub header_hash: u32,
    /// The optional section's and fragments' share of the checksum.
    pub payload_hash: u32,
    fragment_bytes: &'a [u8],
}

impl<'a> ParsedPacket<'a> {
    /// The message fragments, in wire order.
    #[must_use]
    pub fn fragments(&self) -> Fragments<'a> {
        Fragments::new(self.fragment_bytes)
    }

    /// Whether the checksum holds: `header + payload` in the clear, or
    /// `header + (key XOR payload)` when sealed with keystream word `key`.
    #[must_use]
    pub fn verify(&self, key: Option<u32>) -> bool {
        verify_checksum(&self.header, self.header_hash, self.payload_hash, key)
    }
}

/// See [`ParsedPacket::verify`].
#[must_use]
pub fn verify_checksum(
    header: &PacketHeader,
    header_hash: u32,
    payload_hash: u32,
    key: Option<u32>,
) -> bool {
    let payload = match key {
        Some(key) => key ^ payload_hash,
        None => payload_hash,
    };
    header.checksum == header_hash.wrapping_add(payload)
}

/// Parse a datagram: header, optional sections, fragment layout, and both
/// checksum shares. The checksum itself is not checked.
pub fn parse_packet(datagram: &[u8]) -> Result<ParsedPacket<'_>, PacketError> {
    let header = PacketHeader::from_bytes(datagram).ok_or(PacketError::TooShort)?;
    let body = datagram[PacketHeader::SIZE..]
        .get(..usize::from(header.data_size))
        .ok_or(PacketError::BodyExceedsDatagram)?;
    let (optional, optional_len) =
        parse_optional(body, header.flags).ok_or(PacketError::InvalidOptionalHeader)?;

    let mut fragment_bytes: &[u8] = &[];
    let mut fragment_count = 0;
    let mut fragment_hash = 0u32;
    if header.flags.has(PacketFlags::BLOB_FRAGMENTS) {
        fragment_bytes = &body[optional_len..];
        let mut rest = fragment_bytes;
        while !rest.is_empty() {
            let (fragment, used) = parse_fragment(rest).ok_or(PacketError::InvalidFragment)?;
            fragment_hash = fragment_hash.wrapping_add(fragment.hash());
            fragment_count += 1;
            rest = &rest[used..];
        }
    }

    Ok(ParsedPacket {
        header,
        optional,
        body,
        fragment_count,
        header_hash: header.hash(),
        payload_hash: hash32(optional.raw).wrapping_add(fragment_hash),
        fragment_bytes,
    })
}

/// Parse and check the checksum, drawing the next word from `keystream`
/// for a sealed packet. The word is consumed whether or not it matches, so
/// this is for tests and simple peers only; a client connection keeps its
/// inbound keystream aligned to sequence order instead.
pub fn decode_with_keystream<'a>(
    datagram: &'a [u8],
    keystream: Option<&mut Isaac>,
) -> Result<ParsedPacket<'a>, PacketError> {
    let packet = parse_packet(datagram)?;
    let key = if packet.header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM) {
        Some(keystream.ok_or(PacketError::ChecksumMismatch)?.next_word())
    } else {
        None
    };
    if packet.verify(key) {
        Ok(packet)
    } else {
        Err(PacketError::ChecksumMismatch)
    }
}

/// What sealing a packet produced.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Sealed {
    /// Datagram length (header plus body).
    pub len: usize,
    /// The keystream word used (0 for a cleartext checksum).
    pub key: u32,
    /// The payload share as written: `key XOR payload_hash` when sealed,
    /// else `payload_hash`. A resend adds a fresh header hash to it.
    pub sealed_payload: u32,
}

fn payload_hash(body: &[u8], flags: PacketFlags, optional_len: usize) -> Result<u32, EncodeError> {
    let optional_hash = hash32(&body[..optional_len]);
    if !flags.has(PacketFlags::BLOB_FRAGMENTS) {
        if optional_len != body.len() {
            return Err(EncodeError::StrayBodyBytes);
        }
        return Ok(optional_hash);
    }
    let mut hash = optional_hash;
    let mut rest = &body[optional_len..];
    while !rest.is_empty() {
        let (fragment, used) = parse_fragment(rest).ok_or(EncodeError::MalformedFragment)?;
        hash = hash.wrapping_add(fragment.hash());
        rest = &rest[used..];
    }
    Ok(hash)
}

/// Finish a packet whose body already sits at `datagram[20..20 + body_len]`:
/// set the data size, compute the checksum (drawing one keystream word if
/// the flags ask for a sealed checksum) and write the header in front.
///
/// Every check runs before the keystream is touched, so a failure never
/// costs a word.
pub fn seal_in_place(
    mut header: PacketHeader,
    datagram: &mut [u8],
    body_len: usize,
    optional_len: usize,
    keystream: Option<&mut Isaac>,
) -> Result<Sealed, EncodeError> {
    let data_size = u16::try_from(body_len).map_err(|_| EncodeError::BodyTooLarge(body_len))?;
    let len = PacketHeader::SIZE + body_len;
    if datagram.len() < len {
        return Err(EncodeError::BufferTooSmall);
    }
    if optional_len > body_len {
        return Err(EncodeError::OptionalLengthOutOfRange);
    }
    let payload = payload_hash(
        &datagram[PacketHeader::SIZE..len],
        header.flags,
        optional_len,
    )?;

    header.data_size = data_size;
    let header_hash = header.hash();
    let (key, sealed_payload) = if header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM) {
        let key = keystream.ok_or(EncodeError::MissingKeystream)?.next_word();
        (key, key ^ payload)
    } else {
        (0, payload)
    };
    header.checksum = header_hash.wrapping_add(sealed_payload);
    datagram[..PacketHeader::SIZE].copy_from_slice(&header.to_bytes());
    Ok(Sealed {
        len,
        key,
        sealed_payload,
    })
}

/// Build a datagram from a header and a body. The optional-section length
/// is found by parsing the body against the header's flags.
pub fn encode(
    header: PacketHeader,
    body: &[u8],
    keystream: Option<&mut Isaac>,
) -> Result<Vec<u8>, EncodeError> {
    let (_, optional_len) =
        parse_optional(body, header.flags).ok_or(EncodeError::MalformedOptional)?;
    let mut datagram = vec![0; PacketHeader::SIZE + body.len()];
    datagram[PacketHeader::SIZE..].copy_from_slice(body);
    seal_in_place(header, &mut datagram, body.len(), optional_len, keystream)?;
    Ok(datagram)
}

/// The cleartext, unsequenced packet that tells the server we are closing
/// the connection.
#[must_use]
pub fn transport_disconnect(client_id: u16, iteration: u16) -> Vec<u8> {
    let header = PacketHeader {
        sequence: 0,
        flags: PacketFlags::DISCONNECT,
        id: client_id,
        iteration,
        ..PacketHeader::default()
    };
    let mut datagram = header.to_bytes().to_vec();
    let sealed = seal_in_place(header, &mut datagram, 0, 0, None)
        .expect("an empty cleartext body always seals");
    datagram.truncate(sealed.len);
    datagram
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::fragment::{FragmentHeader, write_single_fragment};

    fn raw_packet(header: PacketHeader, body: &[u8]) -> Vec<u8> {
        let mut out = header.to_bytes().to_vec();
        out.extend_from_slice(body);
        out
    }

    // Source: OpenAC tests `Packets/PacketCodecTests.cs` (minimal valid packet).
    #[test]
    fn cleartext_ack_packet_is_accepted() {
        let body = [0x78, 0x56, 0x34, 0x12];
        let mut header = PacketHeader {
            sequence: 1,
            flags: PacketFlags::ACK_SEQUENCE,
            id: 42,
            time: 100,
            data_size: 4,
            iteration: 0,
            checksum: 0,
        };
        header.checksum = header.hash().wrapping_add(hash32(&body));
        let datagram = raw_packet(header, &body);
        let packet = decode_with_keystream(&datagram, None).unwrap();
        assert_eq!(packet.optional.ack_sequence, 0x1234_5678);
    }

    // Source: OpenAC tests `Packets/PacketCodecTests.cs` (error cases).
    #[test]
    fn malformed_datagrams_are_errors() {
        let bad_sum = raw_packet(
            PacketHeader {
                flags: PacketFlags::ACK_SEQUENCE,
                data_size: 4,
                checksum: 0xDEAD_BEEF,
                ..PacketHeader::default()
            },
            &[1, 2, 3, 4],
        );
        assert_eq!(
            decode_with_keystream(&bad_sum, None).unwrap_err(),
            PacketError::ChecksumMismatch
        );
        assert_eq!(parse_packet(&[0; 19]).unwrap_err(), PacketError::TooShort);
        let oversized = raw_packet(
            PacketHeader {
                data_size: 1000,
                ..PacketHeader::default()
            },
            &[0; 10],
        );
        assert_eq!(
            parse_packet(&oversized).unwrap_err(),
            PacketError::BodyExceedsDatagram
        );
        let short_optional = raw_packet(
            PacketHeader {
                flags: PacketFlags::TIME_SYNC,
                data_size: 4,
                ..PacketHeader::default()
            },
            &[1, 2, 3, 4],
        );
        assert_eq!(
            parse_packet(&short_optional).unwrap_err(),
            PacketError::InvalidOptionalHeader
        );
        let mut zero_count = Vec::new();
        write_single_fragment(&mut zero_count, 1, 0, &[1]).unwrap();
        zero_count[8] = 0;
        let bad_fragment = raw_packet(
            PacketHeader {
                flags: PacketFlags::BLOB_FRAGMENTS,
                data_size: zero_count.len() as u16,
                ..PacketHeader::default()
            },
            &zero_count,
        );
        assert_eq!(
            parse_packet(&bad_fragment).unwrap_err(),
            PacketError::InvalidFragment
        );
    }

    // Source: OpenAC tests `Packets/PacketCodecTests.cs`
    // (`TryDecode_PacketWithOneFragment_ParsesAndValidatesCRC`).
    #[test]
    fn one_fragment_packet_checksum_is_header_plus_fragment_hashes() {
        let fragment = FragmentHeader {
            sequence: 1,
            id: 0x8000_0001,
            count: 1,
            total_size: 19,
            index: 0,
            queue: 5,
        };
        let mut body = fragment.to_bytes().to_vec();
        body.extend_from_slice(&[0xAA, 0xBB, 0xCC]);
        let mut header = PacketHeader {
            flags: PacketFlags::BLOB_FRAGMENTS,
            data_size: 19,
            ..PacketHeader::default()
        };
        header.checksum = header
            .hash()
            .wrapping_add(hash32(&fragment.to_bytes()))
            .wrapping_add(hash32(&[0xAA, 0xBB, 0xCC]));
        let datagram = raw_packet(header, &body);
        let packet = decode_with_keystream(&datagram, None).unwrap();
        let fragments: Vec<_> = packet.fragments().collect();
        assert_eq!(fragments.len(), 1);
        assert_eq!(fragments[0].payload, &[0xAA, 0xBB, 0xCC]);
        assert_eq!(fragments[0].header, fragment);
    }

    // Source: OpenAC tests `Packets/PacketCodecTests.cs` and
    // `Packets/BorrowedPacketCodecTests.cs` (`VerifyChecksum_EncryptedForm`).
    #[test]
    fn sealed_checksum_needs_the_matching_keystream_word() {
        let body = [1, 2, 3, 4];
        let header = PacketHeader {
            flags: PacketFlags::ACK_SEQUENCE | PacketFlags::ENCRYPTED_CHECKSUM,
            ..PacketHeader::default()
        };
        let seed = u32::from_le_bytes([0x44, 0x33, 0x22, 0x11]);
        let datagram = encode(header, &body, Some(&mut Isaac::new(seed))).unwrap();
        assert!(decode_with_keystream(&datagram, Some(&mut Isaac::new(seed))).is_ok());
        assert_eq!(
            decode_with_keystream(&datagram, Some(&mut Isaac::new(1))).unwrap_err(),
            PacketError::ChecksumMismatch
        );
        assert_eq!(
            decode_with_keystream(&datagram, None).unwrap_err(),
            PacketError::ChecksumMismatch
        );

        let packet = parse_packet(&datagram).unwrap();
        let key = Isaac::new(seed).next_word();
        assert!(packet.verify(Some(key)));
        assert!(!packet.verify(Some(key ^ 1)));
        assert!(!packet.verify(None));
    }

    // Source: OpenAC tests `Packets/PacketCodecEncodeTests.cs`.
    #[test]
    fn encode_sets_data_size_and_round_trips() {
        let header = PacketHeader {
            sequence: 7,
            flags: PacketFlags::ACK_SEQUENCE,
            id: 1,
            time: 100,
            data_size: 9999,
            ..PacketHeader::default()
        };
        let datagram = encode(header, &[0x78, 0x56, 0x34, 0x12], None).unwrap();
        assert_eq!(PacketHeader::from_bytes(&datagram).unwrap().data_size, 4);
        let packet = decode_with_keystream(&datagram, None).unwrap();
        assert_eq!(packet.optional.ack_sequence, 0x1234_5678);

        let encrypted_without_keystream = PacketHeader {
            flags: PacketFlags::ENCRYPTED_CHECKSUM,
            ..PacketHeader::default()
        };
        assert_eq!(
            encode(encrypted_without_keystream, &[], None).unwrap_err(),
            EncodeError::MissingKeystream
        );
    }

    // Source: OpenAC tests `Packets/PacketCodecEncodeTests.cs`
    // (`FinalizeInPlace_MalformedFragment_DoesNotConsumeIsaac`).
    #[test]
    fn a_failed_seal_does_not_consume_a_keystream_word() {
        let seed = u32::from_le_bytes([9, 8, 7, 6]);
        let header = PacketHeader {
            sequence: 33,
            flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
            id: 5,
            ..PacketHeader::default()
        };
        let mut malformed = vec![0; PacketHeader::SIZE + FragmentHeader::SIZE];
        let bad = FragmentHeader {
            count: 0,
            total_size: 16,
            ..FragmentHeader::default()
        };
        malformed[PacketHeader::SIZE..].copy_from_slice(&bad.to_bytes());
        let mut after_failure = Isaac::new(seed);
        assert_eq!(
            seal_in_place(header, &mut malformed, 16, 0, Some(&mut after_failure)).unwrap_err(),
            EncodeError::MalformedFragment
        );

        let mut body = Vec::new();
        write_single_fragment(&mut body, 1, 9, &[1, 2, 3, 4]).unwrap();
        let actual = encode(header, &body, Some(&mut after_failure)).unwrap();
        let expected = encode(header, &body, Some(&mut Isaac::new(seed))).unwrap();
        assert_eq!(actual, expected);
    }

    // Source: OpenAC tests `Packets/BorrowedPacketCodecTests.cs`
    // (`TryParseBorrowed_AllOptionalFieldsAndFragments_MatchesOwnedDecoder`).
    #[test]
    fn every_optional_section_parses_in_order() {
        let flags = PacketFlags::SERVER_SWITCH
            | PacketFlags::REQUEST_RETRANSMIT
            | PacketFlags::REJECT_RETRANSMIT
            | PacketFlags::ACK_SEQUENCE
            | PacketFlags::WORLD_LOGIN_REQUEST
            | PacketFlags::CONNECT_REQUEST
            | PacketFlags::CONNECT_RESPONSE
            | PacketFlags::CICMD_COMMAND
            | PacketFlags::TIME_SYNC
            | PacketFlags::ECHO_REQUEST
            | PacketFlags::FLOW
            | PacketFlags::BLOB_FRAGMENTS;
        let mut w = ac_bits::Writer::new();
        w.bytes(&[0x11; 8]);
        w.u32(2);
        w.u32(0x1011_1213);
        w.u32(0x2021_2223);
        w.u32(1);
        w.u32(0x3031_3233);
        w.u32(0x4041_4243);
        w.bytes(&[0x44; 8]);
        w.f64(12345.6789);
        w.u64(0xFEED_FACE_CAFE_BABE);
        w.u32(0x5051_5253);
        w.u32(0x6061_6263);
        w.u32(0x7071_7273);
        w.u32(0);
        w.bytes(&[0x81; 8]);
        w.bytes(&[0x91; 8]);
        w.f64(9876.54321);
        w.f32(42.25);
        w.u32(0xA0A1_A2A3);
        w.u16(0xB0B1);
        let optional_len = w.len();
        let mut body = w.into_vec();
        for (index, payload) in [(0u16, &[0xC1, 0xC2][..]), (1, &[0xD1, 0xD2, 0xD3][..])] {
            let header = FragmentHeader {
                sequence: 99,
                id: 0x8000_0000,
                count: 2,
                total_size: (16 + payload.len()) as u16,
                index,
                queue: 7,
            };
            body.extend_from_slice(&header.to_bytes());
            body.extend_from_slice(payload);
        }
        let header = PacketHeader {
            sequence: 0x0102_0304,
            flags,
            id: 0x0506,
            time: 0x0708,
            iteration: 0x090A,
            ..PacketHeader::default()
        };
        let datagram = encode(header, &body, None).unwrap();
        let packet = decode_with_keystream(&datagram, None).unwrap();
        let o = packet.optional;
        assert_eq!(o.raw.len(), optional_len);
        assert_eq!(
            o.retransmit_requests.iter().collect::<Vec<_>>(),
            vec![0x1011_1213, 0x2021_2223]
        );
        assert_eq!(
            o.reject_retransmits.iter().collect::<Vec<_>>(),
            vec![0x3031_3233]
        );
        assert_eq!(o.ack_sequence, 0x4041_4243);
        assert_eq!(
            o.connect_request,
            Some(ConnectRequest {
                server_time: 12345.6789,
                cookie: 0xFEED_FACE_CAFE_BABE,
                client_id: 0x5051_5253,
                server_seed: 0x6061_6263,
                client_seed: 0x7071_7273,
            })
        );
        assert_eq!(o.time_sync, 9876.54321);
        assert_eq!(o.echo_request_time, 42.25);
        assert_eq!(o.flow_bytes, 0xA0A1_A2A3);
        assert_eq!(o.flow_interval, 0xB0B1);
        assert_eq!(packet.fragment_count, 2);
        let payloads: Vec<_> = packet.fragments().map(|f| f.payload).collect();
        assert_eq!(payloads, vec![&[0xC1, 0xC2][..], &[0xD1, 0xD2, 0xD3][..]]);
    }

    // Source: OpenAC tests `Packets/ConnectRequestTests.cs`.
    #[test]
    fn connect_request_fields_decode() {
        let mut w = ac_bits::Writer::new();
        w.f64(12345.6789);
        w.u64(0xFEED_FACE_CAFE_BABE);
        w.u32(0x1122_3344);
        w.u32(0xAABB_CCDD);
        w.u32(0x0102_0304);
        w.u32(0);
        let header = PacketHeader {
            flags: PacketFlags::CONNECT_REQUEST,
            ..PacketHeader::default()
        };
        let datagram = encode(header, w.as_slice(), None).unwrap();
        let request = decode_with_keystream(&datagram, None)
            .unwrap()
            .optional
            .connect_request
            .unwrap();
        assert_eq!(request.cookie, 0xFEED_FACE_CAFE_BABE);
        assert_eq!(request.client_id, 0x1122_3344);
        assert_eq!(request.server_seed, 0xAABB_CCDD);
        assert_eq!(request.client_seed, 0x0102_0304);
        assert_ne!(
            Isaac::new(request.server_seed).next_word(),
            Isaac::new(request.client_seed).next_word()
        );
    }

    // Source: OpenAC tests `Packets/TransportDisconnectTests.cs`.
    #[test]
    fn transport_disconnect_shape() {
        let datagram = transport_disconnect(0x1234, 0x0001);
        assert_eq!(datagram.len(), PacketHeader::SIZE);
        let header = PacketHeader::from_bytes(&datagram).unwrap();
        assert_eq!(header.sequence, 0);
        assert_eq!(header.flags, PacketFlags::DISCONNECT);
        assert_eq!(header.id, 0x1234);
        assert_eq!(header.iteration, 1);
        assert_eq!(header.data_size, 0);
        assert!(decode_with_keystream(&datagram, None).is_ok());
    }
}
