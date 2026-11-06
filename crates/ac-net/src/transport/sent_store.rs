use std::collections::{HashMap, VecDeque};

use super::seq::is_newer;

/// A reliable packet kept for resending.
#[derive(Debug, Clone)]
pub struct CachedPacket {
    /// Packet sequence.
    pub sequence: u32,
    /// The whole datagram: header then body. A resend rewrites the header
    /// (and the time pair, when the body starts with one).
    pub datagram: Vec<u8>,
    /// The keystream word the packet was sealed with; a resend that changes
    /// the body seals it again with this word, never a new one.
    pub key: u32,
    /// The payload share of the checksum as last sealed (key XOR payload
    /// hash); a resend adds a fresh header hash to it.
    pub sealed_payload: u32,
    /// Whether the body carries fragments.
    pub has_fragments: bool,
    /// Whether the body starts with a cumulative ack (as sent; a resend
    /// keeps its value).
    pub has_ack: bool,
    /// Whether the body carries the time-sync and echo pair (after the
    /// ack, if any).
    pub has_time_pair: bool,
}

/// Sent reliable packets in send order, until the server acknowledges them.
///
/// The only optional sections a reliable packet carries are the time-sync
/// and echo pair and, when it rode with the pair, the cumulative ack; they
/// travel with it on every resend. Other acks and every NAK go in
/// standalone packets.
#[derive(Debug, Default)]
pub struct SentPacketStore {
    order: VecDeque<u32>,
    by_sequence: HashMap<u32, CachedPacket>,
}

impl SentPacketStore {
    /// Packets held.
    #[must_use]
    pub fn len(&self) -> usize {
        self.order.len()
    }

    /// Keep a packet. Sequences are added in increasing order.
    pub fn add(&mut self, packet: CachedPacket) {
        self.order.push_back(packet.sequence);
        self.by_sequence.insert(packet.sequence, packet);
    }

    /// Whether `sequence` is held.
    #[must_use]
    pub fn contains(&self, sequence: u32) -> bool {
        self.by_sequence.contains_key(&sequence)
    }

    /// The packet for `sequence`, mutable so a resend can rewrite its header.
    pub fn get_mut(&mut self, sequence: u32) -> Option<&mut CachedPacket> {
        self.by_sequence.get_mut(&sequence)
    }

    /// Drop packets strictly older than `watermark` (wrap-safe), oldest first.
    pub fn flush_older_than(&mut self, watermark: u32) {
        while let Some(&head) = self.order.front() {
            if !is_newer(watermark, head) {
                break;
            }
            self.order.pop_front();
            self.by_sequence.remove(&head);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(sequence: u32) -> CachedPacket {
        CachedPacket {
            sequence,
            datagram: vec![0; 44],
            key: 0,
            sealed_payload: 0,
            has_fragments: true,
            has_ack: false,
            has_time_pair: false,
        }
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.SentPacketStore_FifoContainsAndStrictFlush`.
    #[test]
    fn flush_is_strictly_below_the_watermark() {
        let mut store = SentPacketStore::default();
        for s in 2..=4 {
            store.add(entry(s));
        }
        assert_eq!(store.len(), 3);
        assert!(store.contains(3));
        assert!(!store.contains(5));
        assert_eq!(store.get_mut(2).unwrap().sequence, 2);

        store.flush_older_than(3);
        assert_eq!(store.len(), 2);
        assert!(!store.contains(2));
        assert!(store.contains(3) && store.contains(4));

        store.flush_older_than(5);
        assert_eq!(store.len(), 0);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.SentPacketStore_FlushIsWrapSafe_AcrossTheSequenceWrap`.
    #[test]
    fn flush_is_wrap_safe() {
        let mut store = SentPacketStore::default();
        for s in [0xFFFF_FFFE, 0xFFFF_FFFF, 1, 2] {
            store.add(entry(s));
        }
        store.flush_older_than(1);
        assert_eq!(store.len(), 2);
        assert!(!store.contains(0xFFFF_FFFE) && !store.contains(0xFFFF_FFFF));
        assert!(store.contains(1) && store.contains(2));
        store.flush_older_than(3);
        assert_eq!(store.len(), 0);
    }
}
