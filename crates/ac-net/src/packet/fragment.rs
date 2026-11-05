use crate::crypto::hash32;

/// The 16-byte header in front of each message fragment.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct FragmentHeader {
    /// Message sequence: all fragments of one message share it.
    pub sequence: u32,
    /// Fragment id (the client always sends [`FragmentHeader::OUTBOUND_ID`]).
    pub id: u32,
    /// How many fragments the message has.
    pub count: u16,
    /// Bytes of this fragment on the wire, header included.
    pub total_size: u16,
    /// This fragment's position in the message.
    pub index: u16,
    /// The message queue (see [`crate::GameMessageGroup`]).
    pub queue: u16,
}

impl FragmentHeader {
    /// Wire size of the header.
    pub const SIZE: usize = 16;
    /// Largest fragment on the wire, header included.
    pub const MAX_FRAGMENT_SIZE: usize = 464;
    /// Largest payload in one fragment.
    pub const MAX_PAYLOAD: usize = Self::MAX_FRAGMENT_SIZE - Self::SIZE;
    /// The id every client-sent fragment carries.
    pub const OUTBOUND_ID: u32 = 0x8000_0000;

    /// The header as its 16 wire bytes.
    #[must_use]
    pub fn to_bytes(&self) -> [u8; Self::SIZE] {
        let mut out = [0; Self::SIZE];
        out[0..4].copy_from_slice(&self.sequence.to_le_bytes());
        out[4..8].copy_from_slice(&self.id.to_le_bytes());
        out[8..10].copy_from_slice(&self.count.to_le_bytes());
        out[10..12].copy_from_slice(&self.total_size.to_le_bytes());
        out[12..14].copy_from_slice(&self.index.to_le_bytes());
        out[14..16].copy_from_slice(&self.queue.to_le_bytes());
        out
    }

    /// Read a header from the first 16 bytes; `None` when fewer remain.
    #[must_use]
    pub fn from_bytes(data: &[u8]) -> Option<Self> {
        let b: &[u8; Self::SIZE] = data.get(..Self::SIZE)?.try_into().ok()?;
        let u16_at = |i: usize| u16::from_le_bytes([b[i], b[i + 1]]);
        let u32_at = |i: usize| u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]);
        Some(Self {
            sequence: u32_at(0),
            id: u32_at(4),
            count: u16_at(8),
            total_size: u16_at(10),
            index: u16_at(12),
            queue: u16_at(14),
        })
    }
}

/// One fragment borrowed from a datagram.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Fragment<'a> {
    /// The fragment header.
    pub header: FragmentHeader,
    /// The payload bytes after the header.
    pub payload: &'a [u8],
}

impl Fragment<'_> {
    /// This fragment's share of the packet checksum: the hash of its header
    /// bytes plus the hash of its payload.
    #[must_use]
    pub fn hash(&self) -> u32 {
        hash32(&self.header.to_bytes()).wrapping_add(hash32(self.payload))
    }
}

/// Parse one fragment from the front of `source`, returning it and the bytes
/// it occupies. `None` when the header is short or inconsistent (size out of
/// range, zero count, index past count) or the payload is truncated.
#[must_use]
pub fn parse_fragment(source: &[u8]) -> Option<(Fragment<'_>, usize)> {
    let header = FragmentHeader::from_bytes(source)?;
    let total = usize::from(header.total_size);
    if !(FragmentHeader::SIZE..=FragmentHeader::MAX_FRAGMENT_SIZE).contains(&total)
        || header.count == 0
        || header.index >= header.count
        || source.len() < total
    {
        return None;
    }
    Some((
        Fragment {
            header,
            payload: &source[FragmentHeader::SIZE..total],
        },
        total,
    ))
}

/// Iterates the fragments of a packet whose fragment area was already
/// validated by [`crate::packet::parse_packet`].
#[derive(Debug, Clone)]
pub struct Fragments<'a> {
    rest: &'a [u8],
}

impl<'a> Fragments<'a> {
    pub(crate) fn new(bytes: &'a [u8]) -> Self {
        Self { rest: bytes }
    }
}

impl<'a> Iterator for Fragments<'a> {
    type Item = Fragment<'a>;

    fn next(&mut self) -> Option<Fragment<'a>> {
        // A malformed tail cannot occur after validation; stop rather than
        // panic if it ever does.
        let (fragment, used) = parse_fragment(self.rest)?;
        self.rest = &self.rest[used..];
        Some(fragment)
    }
}

/// Append a whole message as one fragment (header then body) and return the
/// bytes written. `None` when the body exceeds [`FragmentHeader::MAX_PAYLOAD`].
pub fn write_single_fragment(
    out: &mut Vec<u8>,
    sequence: u32,
    queue: u16,
    body: &[u8],
) -> Option<usize> {
    write_fragment(out, sequence, queue, 1, 0, body)
}

/// Append fragment `index` of a `count`-fragment message (header then its
/// piece of the body) and return the bytes written. Every fragment of one
/// message carries the same sequence, id, count and queue. `None` when the
/// piece exceeds [`FragmentHeader::MAX_PAYLOAD`] or `index` is not below
/// `count`.
pub fn write_fragment(
    out: &mut Vec<u8>,
    sequence: u32,
    queue: u16,
    count: u16,
    index: u16,
    payload: &[u8],
) -> Option<usize> {
    if payload.len() > FragmentHeader::MAX_PAYLOAD || index >= count {
        return None;
    }
    let total = FragmentHeader::SIZE + payload.len();
    let header = FragmentHeader {
        sequence,
        id: FragmentHeader::OUTBOUND_ID,
        count,
        // Bounded by MAX_FRAGMENT_SIZE above.
        total_size: total as u16,
        index,
        queue,
    };
    out.extend_from_slice(&header.to_bytes());
    out.extend_from_slice(payload);
    Some(total)
}

/// The largest message body the client can send: as many full fragments as
/// the header's 16-bit count allows.
pub const MAX_MESSAGE_BODY: usize = FragmentHeader::MAX_PAYLOAD * u16::MAX as usize;

/// Split a message body into its fragment payloads, the way the client
/// fragments every outgoing message: full [`FragmentHeader::MAX_PAYLOAD`]-byte
/// pieces in order, the last one holding the remainder. An empty body stays
/// one empty fragment. `None` when the count would not fit the header's
/// 16-bit field.
#[must_use]
pub fn split_message(body: &[u8]) -> Option<Vec<&[u8]>> {
    if body.is_empty() {
        return Some(vec![body]);
    }
    let pieces: Vec<&[u8]> = body.chunks(FragmentHeader::MAX_PAYLOAD).collect();
    u16::try_from(pieces.len()).ok()?;
    Some(pieces)
}

#[cfg(test)]
mod tests {
    use super::*;

    // Source: OpenAC tests `Packets/MessageFragmentHeaderTests.cs`.
    #[test]
    fn header_round_trip_and_layout() {
        let header = FragmentHeader {
            sequence: 0x0403_0201,
            id: 0x0807_0605,
            count: 0x0A09,
            total_size: 0x0C0B,
            index: 0x0E0D,
            queue: 0x100F,
        };
        let bytes = header.to_bytes();
        assert_eq!(bytes.to_vec(), (1..=16).collect::<Vec<u8>>());
        assert_eq!(FragmentHeader::from_bytes(&bytes), Some(header));
        assert_eq!(FragmentHeader::from_bytes(&bytes[..15]), None);
        assert_eq!(FragmentHeader::SIZE, 16);
        assert_eq!(FragmentHeader::MAX_FRAGMENT_SIZE, 464);
        assert_eq!(FragmentHeader::MAX_PAYLOAD, 448);
    }

    #[test]
    fn parse_rejects_inconsistent_headers() {
        let mut wire = Vec::new();
        write_single_fragment(&mut wire, 7, 9, &[1, 2, 3]).unwrap();
        let (fragment, used) = parse_fragment(&wire).unwrap();
        assert_eq!(used, 19);
        assert_eq!(fragment.payload, &[1, 2, 3]);
        assert_eq!(fragment.header.queue, 9);
        assert_eq!(fragment.header.id, FragmentHeader::OUTBOUND_ID);

        // Truncated payload.
        assert!(parse_fragment(&wire[..18]).is_none());
        // Zero count, index past count, size below the header.
        for patch in [
            FragmentHeader {
                count: 0,
                ..fragment.header
            },
            FragmentHeader {
                index: 1,
                ..fragment.header
            },
            FragmentHeader {
                total_size: 15,
                ..fragment.header
            },
            FragmentHeader {
                total_size: 465,
                ..fragment.header
            },
        ] {
            let mut bad = wire.clone();
            bad[..16].copy_from_slice(&patch.to_bytes());
            assert!(parse_fragment(&bad).is_none(), "{patch:?}");
        }
    }

    // The game splits a message into 448-byte pieces, the last one the
    // remainder; a 448-multiple has no empty tail piece.
    #[test]
    fn messages_split_into_448_byte_pieces() {
        let sizes = |len: usize| -> Vec<usize> {
            split_message(&vec![0; len])
                .unwrap()
                .iter()
                .map(|p| p.len())
                .collect()
        };
        assert_eq!(sizes(0), [0]);
        assert_eq!(sizes(1), [1]);
        assert_eq!(sizes(448), [448]);
        assert_eq!(sizes(449), [448, 1]);
        assert_eq!(sizes(480), [448, 32]);
        assert_eq!(sizes(896), [448, 448]);
        assert_eq!(sizes(MAX_MESSAGE_BODY).len(), usize::from(u16::MAX));
        assert!(split_message(&vec![0; MAX_MESSAGE_BODY + 1]).is_none());
    }

    #[test]
    fn write_fragment_stamps_count_and_index() {
        let mut wire = Vec::new();
        assert_eq!(write_fragment(&mut wire, 5, 9, 3, 2, &[7; 10]), Some(26));
        let (fragment, used) = parse_fragment(&wire).unwrap();
        assert_eq!(used, 26);
        assert_eq!(
            fragment.header,
            FragmentHeader {
                sequence: 5,
                id: FragmentHeader::OUTBOUND_ID,
                count: 3,
                total_size: 26,
                index: 2,
                queue: 9,
            }
        );
        assert_eq!(write_fragment(&mut wire, 5, 9, 3, 3, &[7; 10]), None);
        assert_eq!(write_fragment(&mut wire, 5, 9, 3, 0, &[7; 449]), None);
    }

    #[test]
    fn single_fragment_capacity_is_448() {
        let mut out = Vec::new();
        assert_eq!(write_single_fragment(&mut out, 1, 9, &[0; 448]), Some(464));
        assert_eq!(write_single_fragment(&mut out, 1, 9, &[0; 449]), None);
    }
}
