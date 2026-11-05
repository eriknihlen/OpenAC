use std::collections::VecDeque;

use super::TransportStats;
use super::sent_store::{CachedPacket, SentPacketStore};
use super::seq::{is_newer, newest};
use crate::crypto::Isaac;
use crate::packet::{
    EncodeError, FragmentHeader, MAX_MESSAGE_BODY, PacketFlags, PacketHeader, SequenceList,
    seal_in_place, split_message, write_fragment,
};

/// Why a game message could not be sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum SendError {
    /// The body needs more fragments than the header's 16-bit count holds.
    #[error("message body of {0} bytes exceeds the {MAX_MESSAGE_BODY}-byte message limit")]
    MessageTooLarge(usize),
    /// The connection was closed.
    #[error("the connection is closed")]
    Closed,
    /// The server has not confirmed the handshake yet; a message sent now
    /// would spend a sequence number and key the server is not ready for.
    #[error("the handshake is not confirmed yet")]
    NotConfirmed,
    /// Building the packet failed (an internal invariant broke).
    #[error(transparent)]
    Encode(#[from] EncodeError),
}

/// Where one fragment sits in its message.
#[derive(Debug, Clone, Copy)]
struct FragmentPlace {
    /// The message's fragment sequence, shared by all its fragments.
    message_sequence: u32,
    queue: u16,
    count: u16,
    index: u16,
}

pub(crate) fn emit(out: &mut VecDeque<Vec<u8>>, stats: &mut TransportStats, datagram: Vec<u8>) {
    stats.packets_sent += 1;
    out.push_back(datagram);
}

/// Outbound reliable delivery: sequence and fragment numbering, the
/// outbound keystream, the sent-packet cache, and resends on request.
///
/// There is no timer-based resend: a packet is resent only when the server
/// names it in a NAK, and every resend reuses the packet's original
/// keystream word (the server tolerates only a bounded number of skipped
/// words, so drawing a new one would slowly kill the session).
#[derive(Debug)]
pub struct OutboundFlowQueue {
    keystream: Isaac,
    client_id: u16,
    iteration: u16,
    store: SentPacketStore,
    pending_resends: Vec<u32>,
    highest_id_sent: u32,
    fragment_sequence: u32,
    ack_watermark: u32,
}

impl OutboundFlowQueue {
    /// Sequence 1 belongs to the handshake's connect response, so the first
    /// reliable packet is 2; fragment sequences start at 1.
    pub fn new(keystream: Isaac, client_id: u16, iteration: u16) -> Self {
        Self {
            keystream,
            client_id,
            iteration,
            store: SentPacketStore::default(),
            pending_resends: Vec::new(),
            highest_id_sent: 1,
            fragment_sequence: 1,
            ack_watermark: 0,
        }
    }

    /// The last sequence sent (control packets borrow it).
    pub fn highest_id_sent(&self) -> u32 {
        self.highest_id_sent
    }

    /// The server's acknowledgement watermark as we know it.
    pub fn ack_watermark(&self) -> u32 {
        self.ack_watermark
    }

    /// Packets held for resending.
    pub fn cache_depth(&self) -> usize {
        self.store.len()
    }

    /// Resends queued for the next sweep.
    pub fn pending_resend_count(&self) -> usize {
        self.pending_resends.len()
    }

    fn next_sequence(&self) -> u32 {
        // Wraps past the top to 1, never 0.
        if self.highest_id_sent == u32::MAX {
            1
        } else {
            self.highest_id_sent + 1
        }
    }

    /// Send one game message: split into fragments of at most 448 bytes
    /// that share the message's fragment sequence, each fragment in its own
    /// keystream-sealed packet with its own packet sequence, every packet
    /// kept for resending. A full fragment fills a packet, so two pieces of
    /// one message never share a packet.
    pub fn send_game_message(
        &mut self,
        body: &[u8],
        queue: u16,
        interval_id: u16,
        stats: &mut TransportStats,
        out: &mut VecDeque<Vec<u8>>,
    ) -> Result<(), SendError> {
        let pieces = split_message(body).ok_or(SendError::MessageTooLarge(body.len()))?;
        // `split_message` keeps the count within 16 bits.
        let count = pieces.len() as u16;
        // Every fragment shares one message identity, taken before the
        // first goes out, so a send that fails part way never hands the
        // same identity to the next message.
        let message_sequence = self.fragment_sequence;
        self.fragment_sequence = self.fragment_sequence.wrapping_add(1);
        for (index, piece) in (0..count).zip(pieces) {
            let fragment = FragmentPlace {
                message_sequence,
                queue,
                count,
                index,
            };
            self.send_fragment(piece, fragment, interval_id, stats, out)?;
        }
        Ok(())
    }

    /// The fragment sequence the next message will carry, for tests.
    #[cfg(test)]
    pub(crate) fn fragment_sequence(&self) -> u32 {
        self.fragment_sequence
    }

    fn send_fragment(
        &mut self,
        piece: &[u8],
        place: FragmentPlace,
        interval_id: u16,
        stats: &mut TransportStats,
        out: &mut VecDeque<Vec<u8>>,
    ) -> Result<(), SendError> {
        let mut datagram =
            Vec::with_capacity(PacketHeader::SIZE + FragmentHeader::SIZE + piece.len());
        datagram.resize(PacketHeader::SIZE, 0);
        let fragment_len = write_fragment(
            &mut datagram,
            place.message_sequence,
            place.queue,
            place.count,
            place.index,
            piece,
        )
        .ok_or(SendError::MessageTooLarge(piece.len()))?;
        let header = PacketHeader {
            sequence: self.next_sequence(),
            flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
            id: self.client_id,
            time: interval_id,
            iteration: self.iteration,
            ..PacketHeader::default()
        };
        let sealed = seal_in_place(
            header,
            &mut datagram,
            fragment_len,
            0,
            Some(&mut self.keystream),
        )?;

        self.highest_id_sent = header.sequence;
        emit(out, stats, datagram.clone());
        self.store.add(CachedPacket {
            sequence: header.sequence,
            datagram,
            sealed_payload: sealed.sealed_payload,
            has_fragments: true,
        });
        Ok(())
    }

    /// Fold an acknowledgement into the watermark (wrap-safe, never backwards).
    pub fn on_ack_sequence(&mut self, ack: u32, stats: &mut TransportStats) {
        stats.acks_consumed += 1;
        self.ack_watermark = newest(self.ack_watermark, ack);
    }

    /// A server NAK: its first id doubles as an acknowledgement; each cached
    /// id is queued (sorted, deduplicated) for the next sweep.
    pub fn on_retransmit_request(&mut self, ids: SequenceList<'_>, stats: &mut TransportStats) {
        if ids.is_empty() {
            return;
        }
        stats.nak_requests_received += 1;
        for (i, id) in ids.iter().enumerate() {
            if i == 0 {
                self.on_ack_sequence(id, stats);
            }
            if self.store.contains(id) {
                self.merge_insert_pending(id);
            } else {
                stats.uncached_nak_ids += 1;
            }
        }
    }

    fn merge_insert_pending(&mut self, id: u32) {
        let index = self
            .pending_resends
            .iter()
            .position(|&pending| !is_newer(id, pending))
            .unwrap_or(self.pending_resends.len());
        if self.pending_resends.get(index) == Some(&id) {
            return;
        }
        self.pending_resends.insert(index, id);
    }

    /// Resend every queued id the server has not since acknowledged, then
    /// drop cached packets strictly below the acknowledgement watermark.
    pub fn transmit_pending_resends(
        &mut self,
        interval_id: u16,
        stats: &mut TransportStats,
        out: &mut VecDeque<Vec<u8>>,
    ) {
        for &id in &self.pending_resends {
            if self.ack_watermark != 0 && is_newer(self.ack_watermark, id) {
                continue;
            }
            let Some(cached) = self.store.get_mut(id) else {
                continue;
            };
            if let Some(header) = resend_header(cached, interval_id) {
                cached.datagram[..PacketHeader::SIZE].copy_from_slice(&header.to_bytes());
                stats.resends_sent += 1;
                emit(out, stats, cached.datagram.clone());
            }
        }
        self.pending_resends.clear();
        self.store.flush_older_than(self.ack_watermark);
    }
}

/// The header a resend carries: flags become exactly retransmission +
/// sealed checksum (+ fragments), time becomes the current interval, and
/// the checksum is a fresh header hash plus the original sealed payload
/// share. Sequence, size, id and iteration stay; the body is untouched.
pub(crate) fn resend_header(cached: &CachedPacket, interval_id: u16) -> Option<PacketHeader> {
    let mut header = PacketHeader::from_bytes(&cached.datagram)?;
    let mut flags = PacketFlags::RETRANSMISSION | PacketFlags::ENCRYPTED_CHECKSUM;
    if cached.has_fragments {
        flags |= PacketFlags::BLOB_FRAGMENTS;
    }
    header.flags = flags;
    header.time = interval_id;
    header.checksum = header.hash().wrapping_add(cached.sealed_payload);
    Some(header)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::parse_packet;

    const CLIENT_SEED: u32 = 0x11AA_22BB;
    const CLIENT_ID: u16 = 0x1234;
    const ITERATION: u16 = 0x0007;

    struct Fixture {
        queue: OutboundFlowQueue,
        stats: TransportStats,
        out: VecDeque<Vec<u8>>,
    }

    impl Fixture {
        fn new() -> Self {
            Self {
                queue: OutboundFlowQueue::new(Isaac::new(CLIENT_SEED), CLIENT_ID, ITERATION),
                stats: TransportStats::default(),
                out: VecDeque::new(),
            }
        }

        fn send(&mut self, marker: u8, interval: u16) {
            self.queue
                .send_game_message(
                    &message(marker),
                    9,
                    interval,
                    &mut self.stats,
                    &mut self.out,
                )
                .unwrap();
        }

        fn nak(&mut self, ids: &[u32]) {
            let bytes: Vec<u8> = ids.iter().flat_map(|id| id.to_le_bytes()).collect();
            let list = list_of(&bytes);
            self.queue.on_retransmit_request(list, &mut self.stats);
        }

        fn sweep(&mut self, interval: u16) {
            self.queue
                .transmit_pending_resends(interval, &mut self.stats, &mut self.out);
        }

        fn take(&mut self) -> Vec<Vec<u8>> {
            self.out.drain(..).collect()
        }
    }

    fn list_of(bytes: &[u8]) -> SequenceList<'_> {
        SequenceList::from_raw(bytes)
    }

    fn message(marker: u8) -> Vec<u8> {
        vec![marker, 0x11, 0x22, 0x33, 0, 0, 0, 0]
    }

    fn key_of(datagram: &[u8]) -> u32 {
        let packet = parse_packet(datagram).unwrap();
        packet.header.checksum.wrapping_sub(packet.header_hash) ^ packet.payload_hash
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.Resend_RebuildsHeaderOnly_FlagsTimeChecksum_BodyBitIdentical`.
    #[test]
    fn resend_rebuilds_only_the_header() {
        let mut f = Fixture::new();
        f.send(0xA1, 1);
        let original = f.take().pop().unwrap();
        let original_header = PacketHeader::from_bytes(&original).unwrap();
        assert_eq!(original_header.sequence, 2);
        assert_eq!(
            original_header.flags,
            PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM
        );
        assert_eq!(original_header.time, 1);
        assert_eq!(original_header.iteration, ITERATION);

        // Later (interval 3) the server NAKs sequence 2.
        f.nak(&[2]);
        f.sweep(3);
        let resent = f.take().pop().unwrap();
        let h = PacketHeader::from_bytes(&resent).unwrap();
        assert_eq!(h.flags.bits(), 7);
        assert_eq!(h.time, 3);
        assert_eq!(h.sequence, original_header.sequence);
        assert_eq!(h.data_size, original_header.data_size);
        assert_eq!(h.id, original_header.id);
        assert_eq!(h.iteration, original_header.iteration);
        let sealed = original_header
            .checksum
            .wrapping_sub(original_header.hash());
        assert_eq!(h.checksum, h.hash().wrapping_add(sealed));
        assert_eq!(resent[20..], original[20..]);
        assert_eq!(f.stats.resends_sent, 1);
        assert_eq!(f.stats.nak_requests_received, 1);
        assert_eq!(f.stats.uncached_nak_ids, 0);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.BuildResendHeader_WithoutFragments_FlagsAreExactlyThree`.
    #[test]
    fn fragmentless_resend_flags_are_exactly_three() {
        let header = PacketHeader {
            sequence: 9,
            flags: PacketFlags::ENCRYPTED_CHECKSUM,
            id: 0x1234,
            ..PacketHeader::default()
        };
        let cached = CachedPacket {
            sequence: 9,
            datagram: header.to_bytes().to_vec(),
            sealed_payload: 0xDEAD_BEEF,
            has_fragments: false,
        };
        let rebuilt = resend_header(&cached, 42).unwrap();
        assert_eq!(rebuilt.flags.bits(), 3);
        assert_eq!(rebuilt.time, 42);
        assert_eq!(rebuilt.sequence, 9);
        assert_eq!(rebuilt.checksum, rebuilt.hash().wrapping_add(0xDEAD_BEEF));
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.Resend_ConsumesNoOutboundIsaacWord`.
    #[test]
    fn resend_reuses_the_key_and_draws_no_new_word() {
        let mut shadow = Isaac::new(CLIENT_SEED);
        let (w1, w2, w3) = (shadow.next_word(), shadow.next_word(), shadow.next_word());
        let mut f = Fixture::new();
        f.send(0xA1, 1);
        f.send(0xB2, 1);
        let sent = f.take();
        assert_eq!(key_of(&sent[0]), w1);
        assert_eq!(key_of(&sent[1]), w2);

        f.nak(&[2]);
        f.sweep(1);
        assert_eq!(key_of(&f.take().pop().unwrap()), w1);

        f.send(0xC3, 1);
        assert_eq!(key_of(&f.take().pop().unwrap()), w3);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.Nak_ForUncachedId_SendsNothing_AndCounts`.
    #[test]
    fn nak_for_an_uncached_id_sends_nothing() {
        let mut f = Fixture::new();
        f.send(0xA1, 1);
        f.take();
        f.nak(&[40]);
        f.sweep(1);
        assert!(f.take().is_empty());
        assert_eq!(f.stats.uncached_nak_ids, 1);
        assert_eq!(f.stats.resends_sent, 0);
        assert_eq!(f.queue.pending_resend_count(), 0);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.NakFirstId_FoldsTheAckWatermark_AndSweepPrunesStrictlyBelow`.
    #[test]
    fn nak_first_id_acks_and_the_sweep_prunes_strictly_below() {
        let mut f = Fixture::new();
        for marker in [0xA1, 0xB2, 0xC3] {
            f.send(marker, 1);
        }
        assert_eq!(f.queue.cache_depth(), 3);
        f.take();
        f.nak(&[4]);
        assert_eq!(f.queue.ack_watermark(), 4);
        f.sweep(1);
        let resent = f.take();
        assert_eq!(resent.len(), 1);
        assert_eq!(PacketHeader::from_bytes(&resent[0]).unwrap().sequence, 4);
        assert_eq!(f.queue.cache_depth(), 1);
        assert!(f.stats.acks_consumed >= 1);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.FreshSend_StampsCurrentIntervalId_ResendRestampsNewer`.
    #[test]
    fn fresh_sends_and_resends_stamp_the_current_interval() {
        let mut f = Fixture::new();
        f.send(0xA1, 6);
        let fresh = PacketHeader::from_bytes(&f.take()[0]).unwrap();
        assert_eq!(fresh.time, 6);
        assert_eq!(fresh.iteration, ITERATION);
        f.nak(&[2]);
        f.sweep(8);
        let resent = PacketHeader::from_bytes(&f.take()[0]).unwrap();
        assert_eq!(resent.time, 8);
        assert_eq!(resent.iteration, ITERATION);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.OnAckSequence_IsWrapSafeMax_AndNeverRegresses`.
    #[test]
    fn ack_watermark_is_wrap_safe_and_never_regresses() {
        let mut f = Fixture::new();
        f.queue.on_ack_sequence(10, &mut f.stats);
        f.queue.on_ack_sequence(3, &mut f.stats);
        assert_eq!(f.queue.ack_watermark(), 10);

        let mut w = Fixture::new();
        for ack in [0x6000_0000, 0xC000_0000, 0xFFFF_FFF6] {
            w.queue.on_ack_sequence(ack, &mut w.stats);
        }
        assert_eq!(w.queue.ack_watermark(), 0xFFFF_FFF6);
        w.queue.on_ack_sequence(5, &mut w.stats);
        assert_eq!(w.queue.ack_watermark(), 5);
        w.queue.on_ack_sequence(0xFFFF_FFF6, &mut w.stats);
        assert_eq!(w.queue.ack_watermark(), 5);
    }

    // Source: OpenAC tests `OutboundReliableTransportTests.NewerAckOvertakingQueuedNak_SuppressesStaleEncryptedResend`.
    #[test]
    fn a_newer_ack_suppresses_a_stale_queued_resend() {
        let mut f = Fixture::new();
        for marker in [2, 3, 4] {
            f.send(marker, 1);
        }
        f.nak(&[2]);
        f.queue.on_ack_sequence(3, &mut f.stats);
        f.take();
        f.sweep(1);
        assert!(f.take().is_empty());
        assert_eq!(f.stats.resends_sent, 0);
        assert_eq!(f.queue.cache_depth(), 2);
        assert_eq!(f.queue.pending_resend_count(), 0);
    }

    fn send_body(f: &mut Fixture, body: &[u8]) -> Vec<Vec<u8>> {
        f.queue
            .send_game_message(body, 9, 1, &mut f.stats, &mut f.out)
            .unwrap();
        f.take()
    }

    // The game's message splitting (448-byte pieces, every piece stamped
    // with the message's id, piece count, piece index, piece size + 16 and
    // queue) and packet coalescing (a piece joins a waiting packet only
    // while the packet stays within 464 data bytes, so a full piece always
    // starts its own packet); the server reassembles the pieces by fragment
    // sequence, complete at `count` distinct indices.
    #[test]
    fn a_480_byte_message_goes_out_as_two_fragments_in_two_packets() {
        let body: Vec<u8> = (0..480u32).map(|i| i as u8).collect();
        let mut shadow = Isaac::new(CLIENT_SEED);
        let (w1, w2, w3) = (shadow.next_word(), shadow.next_word(), shadow.next_word());
        let mut f = Fixture::new();
        let sent = send_body(&mut f, &body);
        assert_eq!(sent.len(), 2);

        type Golden = (u32, u16, [u8; 16], std::ops::Range<usize>, u32);
        let golden: [Golden; 2] = [
            (
                2,
                464,
                [
                    0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x02, 0x00, 0xD0, 0x01, 0x00,
                    0x00, 0x09, 0x00,
                ],
                0..448,
                w1,
            ),
            (
                3,
                48,
                [
                    0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x02, 0x00, 0x30, 0x00, 0x01,
                    0x00, 0x09, 0x00,
                ],
                448..480,
                w2,
            ),
        ];
        for (datagram, (sequence, data_size, fragment_header, piece, key)) in
            sent.iter().zip(golden)
        {
            let header = PacketHeader::from_bytes(datagram).unwrap();
            assert_eq!(header.sequence, sequence);
            assert_eq!(
                header.flags,
                PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM
            );
            assert_eq!(header.id, CLIENT_ID);
            assert_eq!(header.iteration, ITERATION);
            assert_eq!(header.data_size, data_size);
            assert_eq!(datagram.len(), PacketHeader::SIZE + usize::from(data_size));
            assert_eq!(&datagram[20..36], &fragment_header);
            assert_eq!(&datagram[36..], &body[piece]);
            assert_eq!(key_of(datagram), key);
        }
        assert_eq!(f.queue.cache_depth(), 2);
        assert_eq!(f.stats.packets_sent, 2);

        // The next message takes the next fragment sequence and packet.
        f.send(0xA1, 1);
        let next = f.take().pop().unwrap();
        let packet = parse_packet(&next).unwrap();
        assert_eq!(packet.header.sequence, 4);
        assert_eq!(packet.fragments().next().unwrap().header.sequence, 2);
        assert_eq!(key_of(&next), w3);
    }

    // The server's reassembly (the pieces in index order, concatenated) is
    // what our own inbound assembler does; a split message must come back out byte for byte.
    #[test]
    fn split_messages_round_trip_through_the_inbound_assembler() {
        use crate::FragmentAssembler;
        use crate::NetTime;
        for len in [4usize, 447, 448, 449, 480, 896, 897, 2000] {
            let body: Vec<u8> = (0..len).map(|i| (i * 7) as u8).collect();
            let mut f = Fixture::new();
            let sent = send_body(&mut f, &body);
            assert_eq!(sent.len(), len.div_ceil(448), "len {len}");
            let mut assembler = FragmentAssembler::default();
            let mut done = Vec::new();
            // Deliver the packets in reverse to exercise out-of-order assembly.
            for datagram in sent.iter().rev() {
                let packet = parse_packet(datagram).unwrap();
                for fragment in packet.fragments() {
                    assert_eq!(usize::from(fragment.header.count), sent.len());
                    assert!(fragment.payload.len() <= 448);
                    done.extend(assembler.ingest(NetTime::ZERO, &fragment));
                }
            }
            assert_eq!(done.len(), 1, "len {len}");
            assert_eq!(done[0].body, body);
            assert_eq!(done[0].queue, 9);
        }
    }

    #[test]
    fn a_nak_for_one_fragment_resends_only_that_packet() {
        let mut f = Fixture::new();
        let sent = send_body(&mut f, &[0x5A; 480]);
        f.nak(&[3]);
        f.sweep(2);
        let resent = f.take();
        assert_eq!(resent.len(), 1);
        assert_eq!(resent[0][20..], sent[1][20..]);
        assert_eq!(PacketHeader::from_bytes(&resent[0]).unwrap().sequence, 3);
    }

    #[test]
    fn a_body_past_the_fragment_count_limit_is_refused_without_spending_a_sequence() {
        let mut f = Fixture::new();
        let too_big = vec![0; MAX_MESSAGE_BODY + 1];
        let err = f
            .queue
            .send_game_message(&too_big, 9, 1, &mut f.stats, &mut f.out)
            .unwrap_err();
        assert_eq!(err, SendError::MessageTooLarge(MAX_MESSAGE_BODY + 1));
        assert_eq!(f.queue.highest_id_sent(), 1);
        assert!(f.take().is_empty());
        f.send(1, 1);
        let next = f.take().pop().unwrap();
        assert_eq!(PacketHeader::from_bytes(&next).unwrap().sequence, 2);
        let packet = parse_packet(&next).unwrap();
        assert_eq!(packet.fragments().next().unwrap().header.sequence, 1);
    }

    /// Sends `body`, then reads every packet back: one per 448-byte piece,
    /// each sealed with its own keystream word, all carrying one message
    /// identity, reassembled (last piece first) into the body; the next
    /// message takes the next identity (OpenAC
    /// `OutboundFragmentationTests.VerifyRoundTrip`).
    fn verify_round_trip(body: &[u8]) {
        let mut f = Fixture::new();
        let sent = send_body(&mut f, body);
        let count = body.len().div_ceil(FragmentHeader::MAX_PAYLOAD).max(1);
        assert_eq!(sent.len(), count);
        assert_eq!(f.queue.fragment_sequence(), 2);
        assert_eq!(f.queue.cache_depth(), count);
        let mut shadow = Isaac::new(CLIENT_SEED);
        for datagram in &sent {
            assert!(
                parse_packet(datagram)
                    .unwrap()
                    .verify(Some(shadow.next_word()))
            );
        }
        let mut assembler = crate::FragmentAssembler::new();
        let mut assembled = None;
        for (i, datagram) in sent.iter().enumerate().rev() {
            let packet = parse_packet(datagram).unwrap();
            assert_eq!(packet.header.sequence, 2 + i as u32);
            assert!(
                (PacketHeader::SIZE + FragmentHeader::SIZE..=484).contains(&datagram.len()),
                "{}",
                datagram.len()
            );
            let fragment = packet.fragments().next().unwrap();
            let h = fragment.header;
            assert_eq!(h.sequence, 1);
            assert_eq!(h.id, FragmentHeader::OUTBOUND_ID);
            assert_eq!(usize::from(h.count), count);
            assert_eq!(usize::from(h.index), i);
            assert_eq!(h.queue, 9);
            assert_eq!(h.total_size, packet.header.data_size);
            if let Some(message) = assembler.ingest(crate::NetTime::ZERO, &fragment) {
                assembled = Some(message.body);
            }
        }
        assert_eq!(assembled.as_deref(), Some(body));
        let next = send_body(&mut f, &[1, 2, 3]).pop().unwrap();
        let packet = parse_packet(&next).unwrap();
        let h = packet.fragments().next().unwrap().header;
        assert_eq!((h.sequence, h.count, h.index), (2, 1, 0));
        assert!(packet.verify(Some(shadow.next_word())));
    }

    // Source: OpenAC tests `OutboundFragmentationTests.PayloadBoundaries_RoundTripWithOneMessageIdentity`
    // with the two long channel messages of
    // `LongChannelMessages_AreTransmittedWhole` (450 and 456 bytes; their
    // built sizes are pinned in `ac-proto` `turbine_chat::tests`).
    #[test]
    fn every_body_size_round_trips_with_one_message_identity() {
        for size in [0usize, 447, 448, 449, 450, 456, 896, 897, 65536] {
            let body: Vec<u8> = (0..size).map(|i| i as u8).collect();
            verify_round_trip(&body);
        }
    }

    // Source: OpenAC tests `OutboundFragmentationTests.LostMiddleFragment_IsRecoveredByNakWithoutAdvancingCipher`.
    // (`FailedLaterSend_KeepsEarlierCacheAndDoesNotReuseMessageIdentity`
    // has no counterpart: this queue only builds datagrams, so a send
    // cannot fail once the body is split; the identity is still taken
    // before the first fragment, as OpenAC takes it.)
    #[test]
    fn a_lost_middle_fragment_is_recovered_by_a_nak_without_a_new_key() {
        let body: Vec<u8> = (0..897u32).map(|i| i as u8).collect();
        let mut f = Fixture::new();
        let sent = send_body(&mut f, &body);
        assert_eq!(sent.len(), 3);
        let mut shadow = Isaac::new(CLIENT_SEED);
        let packets: Vec<_> = sent.iter().map(|d| parse_packet(d).unwrap()).collect();
        for packet in &packets {
            assert!(packet.verify(Some(shadow.next_word())));
        }
        let mut assembler = crate::FragmentAssembler::new();
        let now = crate::NetTime::ZERO;
        let first = packets[0].fragments().next().unwrap();
        assert!(assembler.ingest(now, &first).is_none());
        let last = packets[2].fragments().next().unwrap();
        assert!(assembler.ingest(now, &last).is_none());
        f.nak(&[3]);
        f.sweep(1);
        let resent = f.take().pop().unwrap();
        let header = PacketHeader::from_bytes(&resent).unwrap();
        assert_eq!(header.sequence, 3);
        assert!(header.flags.has(PacketFlags::RETRANSMISSION));
        assert_eq!(resent[PacketHeader::SIZE..], sent[1][PacketHeader::SIZE..]);
        let sealed = packets[1]
            .header
            .checksum
            .wrapping_sub(packets[1].header_hash);
        assert_eq!(header.checksum, header.hash().wrapping_add(sealed));
        let middle = parse_packet(&resent).unwrap();
        let message = assembler
            .ingest(now, &middle.fragments().next().unwrap())
            .expect("complete");
        assert_eq!(message.body, body);
        assert_eq!(f.stats.resends_sent, 1);
        let after = send_body(&mut f, &[4]).pop().unwrap();
        assert!(
            parse_packet(&after)
                .unwrap()
                .verify(Some(shadow.next_word()))
        );
    }
}
