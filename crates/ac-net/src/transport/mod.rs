//! Reliable delivery over the packet layer: outbound cache and resend on
//! request, sequence-aligned inbound keystream with parked words, the
//! ack/NAK cadence, the interval clock, and the loss figure.

mod clock;
mod inbound;
mod loss;
mod outbound;
mod scheduler;
mod sent_store;
pub(crate) mod seq;
mod stats;

use std::collections::VecDeque;
use std::time::Duration;

pub use clock::INTERVAL;
pub(crate) use inbound::Admission;
pub use inbound::{INITIAL_WATERMARK, SANITY_WINDOW};
pub use outbound::SendError;
pub(crate) use outbound::TimeStamps;
pub use scheduler::{ACK_GATE, MAX_NAK_IDS, NAK_GATE};
pub use stats::TransportStats;

use crate::crypto::Isaac;
use crate::{FragmentAssembler, NetTime};
use clock::IntervalClock;
use inbound::InboundSequenceTracker;
use loss::PacketLossAverager;
use outbound::OutboundFlowQueue;
use scheduler::{AckNakScheduler, ControlHeader};

/// How often incomplete partial messages are checked for eviction.
pub const ASSEMBLER_SWEEP_PERIOD: Duration = Duration::from_secs(5);

/// The transport state of one negotiated connection. Datagrams it emits
/// collect in an internal queue the connection drains.
#[derive(Debug)]
pub(crate) struct ReliableTransport {
    pub clock: IntervalClock,
    pub stats: TransportStats,
    pub outbound: OutboundFlowQueue,
    pub inbound: InboundSequenceTracker,
    pub assembler: FragmentAssembler,
    scheduler: AckNakScheduler,
    loss: PacketLossAverager,
    assembler_sweep_at: NetTime,
    client_id: u16,
    iteration: u16,
    /// The time of the last sweep: the clock the time pair is stamped from.
    now: NetTime,
    /// The server's clock as last reported, and when.
    server_time: f64,
    server_time_at: NetTime,
    out: VecDeque<Vec<u8>>,
}

impl ReliableTransport {
    /// Born when the server's connect request is accepted, carrying the
    /// server's clock.
    pub fn new(
        now: NetTime,
        outbound_keystream: Isaac,
        inbound_keystream: Isaac,
        client_id: u16,
        iteration: u16,
        server_time: f64,
    ) -> Self {
        let stats = TransportStats::default();
        Self {
            now,
            server_time,
            server_time_at: now,
            clock: IntervalClock::new(now),
            loss: PacketLossAverager::new(now, &stats),
            stats,
            outbound: OutboundFlowQueue::new(outbound_keystream, client_id, iteration),
            inbound: InboundSequenceTracker::new(inbound_keystream, INITIAL_WATERMARK),
            assembler: FragmentAssembler::new(),
            scheduler: AckNakScheduler::new(now),
            assembler_sweep_at: now,
            client_id,
            iteration,
            out: VecDeque::new(),
        }
    }

    /// Queue one game message.
    pub fn send_game_message(&mut self, body: &[u8], queue: u16) -> Result<(), SendError> {
        let stamps = self.stamps();
        self.outbound.send_game_message(
            body,
            queue,
            self.clock.interval_id(),
            stamps,
            &mut self.stats,
            &mut self.out,
        )
    }

    /// The server reported its clock (a time sync) at `at`.
    pub fn note_server_time(&mut self, server_time: f64, at: NetTime) {
        self.server_time = server_time;
        self.server_time_at = at;
    }

    /// The clocks a time pair carries now: the server's clock carried
    /// forward by the time since it was reported, and our own clock.
    pub fn stamps(&self) -> TimeStamps {
        TimeStamps {
            server_time: self.server_time + self.now.since(self.server_time_at).as_secs_f64(),
            local_time: self.now.as_duration().as_secs_f32(),
        }
    }

    /// The periodic pump: interval clock, loss sample, the NAK-or-ack
    /// decision, requested resends (then the cache prune), the time-sync
    /// pair when the interval clock reaches a time-sync point (once
    /// `confirmed`: the server has accepted the connection, so a numbered
    /// packet may go), and every five seconds the partial-message eviction.
    pub fn sweep(&mut self, now: NetTime, confirmed: bool) {
        let time_sync_point = self.clock.update(now);
        self.now = now;
        self.loss.sweep(now, &self.stats);
        let control = ControlHeader {
            sequence: self.outbound.highest_id_sent(),
            client_id: self.client_id,
            interval_id: self.clock.interval_id(),
            iteration: self.iteration,
        };
        self.scheduler
            .sweep(now, &self.inbound, control, &mut self.stats, &mut self.out);
        let stamps = self.stamps();
        self.outbound.transmit_pending_resends(
            self.clock.interval_id(),
            stamps,
            &mut self.stats,
            &mut self.out,
        );
        if time_sync_point && confirmed {
            self.outbound.queue_time_pair(
                self.clock.interval_id(),
                stamps,
                &mut self.stats,
                &mut self.out,
            );
        }
        if now.since(self.assembler_sweep_at) >= ASSEMBLER_SWEEP_PERIOD {
            self.assembler_sweep_at = now;
            self.assembler.sweep_expired(now);
        }
    }

    /// The link-status packet-loss percentage.
    pub fn packet_loss_percentage(&self) -> f64 {
        self.loss.percentage()
    }

    /// Take the next datagram the transport wants sent.
    pub fn pop_outgoing(&mut self) -> Option<Vec<u8>> {
        self.out.pop_front()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::packet::{FragmentHeader, PacketFlags, PacketHeader, decode_with_keystream};

    const CLIENT_SEED: u32 = 0x11AA_22BB;
    const SERVER_SEED: u32 = 0x33CC_44DD;
    const CLIENT_ID: u16 = 0x1234;
    const ITERATION: u16 = 0x0007;
    const SERVER_TIME: f64 = 5000.0;

    struct Fixture {
        t: ReliableTransport,
        now: NetTime,
        sent: Vec<Vec<u8>>,
        confirmed: bool,
    }

    impl Fixture {
        fn new() -> Self {
            Self {
                t: ReliableTransport::new(
                    NetTime::ZERO,
                    Isaac::new(CLIENT_SEED),
                    Isaac::new(SERVER_SEED),
                    CLIENT_ID,
                    ITERATION,
                    SERVER_TIME,
                ),
                now: NetTime::ZERO,
                sent: Vec::new(),
                confirmed: true,
            }
        }

        fn admit(&mut self, sequence: u32) {
            let admission = self.t.inbound.admit(sequence, true, &mut self.t.stats);
            assert_ne!(admission, Admission::Drop, "sequence {sequence}");
        }

        fn advance_ms(&mut self, millis: u64) {
            self.now += Duration::from_millis(millis);
        }

        fn sweep(&mut self) {
            self.t.sweep(self.now, self.confirmed);
            while let Some(d) = self.t.pop_outgoing() {
                self.sent.push(d);
            }
        }

        fn send(&mut self, marker: u8) {
            self.t
                .send_game_message(&[marker, 0x11, 0x22, 0x33, 0, 0, 0, 0], 9)
                .unwrap();
            while let Some(d) = self.t.pop_outgoing() {
                self.sent.push(d);
            }
        }
    }

    fn assert_ack(datagram: &[u8], sequence: u32, value: u32, time: u16) {
        assert_eq!(datagram.len(), PacketHeader::SIZE + 4);
        let h = PacketHeader::from_bytes(datagram).unwrap();
        assert_eq!(h.flags, PacketFlags::ACK_SEQUENCE);
        assert_eq!(h.sequence, sequence);
        assert_eq!(h.id, CLIENT_ID);
        assert_eq!(h.time, time);
        assert_eq!(h.iteration, ITERATION);
        assert_eq!(h.data_size, 4);
        let packet = decode_with_keystream(datagram, None).unwrap();
        assert_eq!(packet.optional.ack_sequence, value);
    }

    fn flags_of(datagram: &[u8]) -> PacketFlags {
        PacketHeader::from_bytes(datagram).unwrap().flags
    }

    // Source: OpenAC tests `AckNakSchedulerTests.CumulativeAck_TwoSecondGate_CarriesWatermarkAtEmission`.
    #[test]
    fn cumulative_ack_two_second_gate() {
        let mut f = Fixture::new();
        f.admit(2);
        f.admit(3);
        f.sweep();
        f.advance_ms(1990);
        f.sweep();
        assert!(f.sent.is_empty());
        f.advance_ms(10);
        f.sweep();
        assert_eq!(f.t.clock.interval_id(), 5);
        assert_eq!(f.sent.len(), 1);
        assert_ack(&f.sent[0], 1, 3, 5);
        assert_eq!(f.t.stats.acks_sent, 1);

        f.sweep();
        f.advance_ms(1990);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        f.admit(4);
        f.admit(5);
        f.advance_ms(10);
        f.sweep();
        assert_eq!(f.sent.len(), 2);
        assert_ack(&f.sent[1], 1, 5, f.t.clock.interval_id());
        assert_eq!(f.t.stats.acks_sent, 2);
    }

    // Source: OpenAC tests `AckNakSchedulerTests.ParkedNak_SuppressesTheAck_AckResumesWhenTheGapClears`.
    #[test]
    fn a_parked_nak_suppresses_the_ack_until_the_gap_clears() {
        let mut f = Fixture::new();
        f.admit(2);
        f.admit(4);
        assert_eq!(f.t.inbound.nak_count(), 1);
        f.advance_ms(2500);
        f.sweep();
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_eq!(flags_of(&f.sent[0]), PacketFlags::REQUEST_RETRANSMIT);
        assert_eq!(f.t.stats.acks_sent, 0);
        assert_eq!(f.t.stats.naks_sent, 1);
        f.sent.clear();

        f.admit(3);
        assert_eq!(f.t.inbound.nak_count(), 0);
        f.sweep();
        assert!(f.sent.is_empty());
        f.advance_ms(2000);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_ack(&f.sent[0], 1, 4, f.t.clock.interval_id());
    }

    // Source: OpenAC tests `AckNakSchedulerTests.CreateObjectFlood_InsideOneWindow_CollapsesToOneAck`.
    #[test]
    fn a_flood_inside_one_window_collapses_to_one_ack() {
        let mut f = Fixture::new();
        for sequence in 2..52 {
            f.admit(sequence);
            f.advance_ms(20);
            f.sweep();
        }
        assert!(f.sent.is_empty());
        f.advance_ms(1000);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_ack(&f.sent[0], 1, 51, f.t.clock.interval_id());
    }

    // Source: OpenAC tests `NakEmissionTests.NakGate_ClosedAtExactly600ms_OpensJustPastIt`.
    #[test]
    fn nak_gate_is_strictly_past_600ms() {
        let mut f = Fixture::new();
        f.admit(2);
        f.admit(4);
        f.advance_ms(600);
        f.sweep();
        assert!(f.sent.is_empty());
        f.advance_ms(1);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        f.sweep();
        f.advance_ms(600);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        f.advance_ms(1);
        f.sweep();
        assert_eq!(f.sent.len(), 2);
        assert_eq!(f.t.stats.naks_sent, 2);
    }

    // Source: OpenAC tests `NakEmissionTests.SharedTimestamp_AckDelaysNak_NakDelaysAck`.
    #[test]
    fn ack_and_nak_share_one_timestamp() {
        let mut f = Fixture::new();
        f.admit(2);
        f.advance_ms(2000);
        f.sweep();
        assert_eq!(f.t.stats.acks_sent, 1);
        f.sent.clear();

        f.admit(4);
        f.advance_ms(600);
        f.sweep();
        assert!(f.sent.is_empty());
        f.advance_ms(1);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_eq!(flags_of(&f.sent[0]), PacketFlags::REQUEST_RETRANSMIT);
        f.sent.clear();

        f.admit(3);
        f.advance_ms(1999);
        f.sweep();
        assert!(f.sent.is_empty());
        f.advance_ms(1);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_eq!(f.t.stats.acks_sent, 2);
    }

    // Source: OpenAC tests `NakEmissionTests.NakSweep_BothGatesOpen_EmitsTheNakAndNeverTheAck`.
    #[test]
    fn with_both_gates_open_only_the_nak_goes() {
        let mut f = Fixture::new();
        f.admit(2);
        f.admit(4);
        f.advance_ms(5000);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        assert_eq!(flags_of(&f.sent[0]), PacketFlags::REQUEST_RETRANSMIT);
        assert_eq!(f.t.stats.naks_sent, 1);
        assert_eq!(f.t.stats.acks_sent, 0);
    }

    // Source: OpenAC tests `NakEmissionTests.NakShape_CleartextExactFlags_BorrowedSequence_AscendingIds`.
    #[test]
    fn nak_shape() {
        let mut f = Fixture::new();
        f.send(1);
        f.send(2);
        assert_eq!(f.t.outbound.highest_id_sent(), 3);
        f.sent.clear();
        f.admit(2);
        f.admit(6);
        f.advance_ms(700);
        f.sweep();
        assert_eq!(f.sent.len(), 1);
        let nak = &f.sent[0];
        let h = PacketHeader::from_bytes(nak).unwrap();
        assert_eq!(h.flags, PacketFlags::REQUEST_RETRANSMIT);
        assert_eq!(h.sequence, 3);
        assert_eq!(h.id, CLIENT_ID);
        assert_eq!(h.time, 2);
        assert_eq!(h.iteration, ITERATION);
        assert_eq!(h.data_size, 16);
        assert_eq!(nak.len(), 36);
        let words: Vec<u32> = nak[20..]
            .chunks_exact(4)
            .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
            .collect();
        assert_eq!(words, vec![3, 3, 4, 5]);
        let packet = decode_with_keystream(nak, None).unwrap();
        assert_eq!(
            packet
                .optional
                .retransmit_requests
                .iter()
                .collect::<Vec<_>>(),
            vec![3, 4, 5]
        );
        assert_eq!(f.t.inbound.nak_count(), 3);
    }

    // Source: OpenAC tests `NakEmissionTests.NakList_CapsAt114LowestAscending_SetUntouched`.
    #[test]
    fn nak_list_caps_at_114_lowest() {
        let mut f = Fixture::new();
        f.admit(2);
        f.admit(203);
        assert_eq!(f.t.inbound.nak_count(), 200);
        f.advance_ms(700);
        f.sweep();
        let nak = &f.sent[0];
        let h = PacketHeader::from_bytes(nak).unwrap();
        assert_eq!(usize::from(h.data_size), 4 + 114 * 4);
        let words: Vec<u32> = nak[20..]
            .chunks_exact(4)
            .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
            .collect();
        assert_eq!(words[0], 114);
        assert_eq!(words[1..], (3..117).collect::<Vec<u32>>()[..]);
        assert_eq!(f.t.inbound.nak_count(), 200);
    }

    // Source: OpenAC tests `ConnectResponseRetransmitTests.cs` (`ReliableTransportAssemblerSweepTests`).
    #[test]
    fn the_sweep_evicts_aged_partials_on_a_five_second_cadence() {
        let mut f = Fixture::new();
        let partial = |seq: u32| {
            let header = FragmentHeader {
                sequence: seq,
                id: FragmentHeader::OUTBOUND_ID,
                count: 2,
                total_size: 17,
                index: 0,
                queue: 7,
            };
            (header, [0x42u8])
        };
        let (h, p) = partial(10);
        let frag = crate::packet::Fragment {
            header: h,
            payload: &p,
        };
        assert!(f.t.assembler.ingest(f.now, &frag).is_none());
        f.advance_ms(30_000);
        f.sweep();
        assert_eq!(f.t.assembler.partial_count(), 1);
        f.advance_ms(28_000);
        let (h, p) = partial(11);
        let frag = crate::packet::Fragment {
            header: h,
            payload: &p,
        };
        assert!(f.t.assembler.ingest(f.now, &frag).is_none());
        f.advance_ms(3_500); // t = 61.5 s
        f.sweep();
        assert_eq!(f.t.assembler.partial_count(), 1);
    }

    /// Sweep every 100 ms up to `until_ms`; the time-pair packets sent,
    /// with when they went out.
    fn idle_time_pairs(f: &mut Fixture, until_ms: u64) -> Vec<(u64, Vec<u8>)> {
        let mut pairs = Vec::new();
        while f.now.as_duration() < Duration::from_millis(until_ms) {
            f.advance_ms(100);
            let before = f.sent.len();
            f.sweep();
            for d in &f.sent[before..] {
                if flags_of(d).has(PacketFlags::TIME_SYNC) {
                    pairs.push((f.now.as_duration().as_millis() as u64, d.clone()));
                }
            }
        }
        pairs
    }

    // Idle: a pair every 3 s (the interval clock starts at 1 and reaches
    // 7, 13, 19, ...), each held until the next one is queued, so each goes
    // out one period late, numbered in turn and stamped as it goes.
    #[test]
    fn an_idle_connection_sends_one_numbered_time_pair_every_three_seconds() {
        let mut f = Fixture::new();
        let pairs = idle_time_pairs(&mut f, 12_000);
        let times: Vec<u64> = pairs.iter().map(|(t, _)| *t).collect();
        assert_eq!(times, vec![6_000, 9_000, 12_000]);
        let mut shadow = Isaac::new(CLIENT_SEED);
        for (i, (t, d)) in pairs.iter().enumerate() {
            let packet = decode_with_keystream(d, Some(&mut shadow)).unwrap();
            assert_eq!(packet.header.sequence, 2 + i as u32);
            assert_eq!(
                packet.header.flags,
                PacketFlags::TIME_SYNC
                    | PacketFlags::ECHO_REQUEST
                    | PacketFlags::ENCRYPTED_CHECKSUM
            );
            let seconds = *t as f64 / 1000.0;
            assert_eq!(packet.optional.time_sync, SERVER_TIME + seconds);
            assert_eq!(packet.optional.echo_request_time, seconds as f32);
        }
        assert_eq!(f.t.outbound.highest_id_sent(), 4);
        assert_eq!(f.t.outbound.cache_depth(), 3);
        assert_eq!(f.t.stats.time_pairs_sent, 3);
        // The idle ack borrows the last numbered sequence: at 12 s the ack
        // goes out before that sweep's pair, so it carries 3.
        let last_ack = f
            .sent
            .iter()
            .rev()
            .find(|d| flags_of(d) == PacketFlags::ACK_SEQUENCE)
            .unwrap();
        assert_eq!(PacketHeader::from_bytes(last_ack).unwrap().sequence, 3);
    }

    // The server's clock is carried forward from its last report.
    #[test]
    fn the_time_sync_carries_the_server_clock_forward() {
        let mut f = Fixture::new();
        f.advance_ms(1_000);
        f.t.note_server_time(9_000.0, f.now);
        let pairs = idle_time_pairs(&mut f, 6_000);
        let packet = crate::packet::parse_packet(&pairs[0].1).unwrap();
        assert_eq!(packet.optional.time_sync, 9_005.0);
    }

    // Nothing numbered goes before the server has accepted the connection.
    #[test]
    fn no_time_pair_before_the_handshake_is_confirmed() {
        let mut f = Fixture::new();
        f.confirmed = false;
        assert!(idle_time_pairs(&mut f, 12_000).is_empty());
        assert!(!f.t.outbound.time_pair_held());
        assert_eq!(f.t.outbound.highest_id_sent(), 1);
    }
}
