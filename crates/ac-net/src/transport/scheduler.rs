use std::collections::VecDeque;
use std::time::Duration;

use super::TransportStats;
use super::inbound::InboundSequenceTracker;
use super::outbound::emit;
use crate::NetTime;
use crate::packet::{PacketFlags, PacketHeader, seal_in_place};

/// A cumulative ack goes out once at least this long has passed.
pub const ACK_GATE: Duration = Duration::from_secs(2);
/// A NAK goes out once strictly more than this has passed.
pub const NAK_GATE: Duration = Duration::from_millis(600);
/// At most this many ids per NAK packet (the lowest ones).
pub const MAX_NAK_IDS: usize = 114;

/// Header fields every control packet copies from the connection.
#[derive(Debug, Clone, Copy)]
pub struct ControlHeader {
    /// Sequence to borrow (the last one we sent).
    pub sequence: u32,
    /// Our client id.
    pub client_id: u16,
    /// Current interval.
    pub interval_id: u16,
    /// Connection iteration.
    pub iteration: u16,
}

/// Decides, once per sweep, between asking for missing packets (NAK) and
/// acknowledging everything received (cumulative ack). The two share one
/// timestamp, so a NAK delays the next ack and vice versa, and a sweep
/// never sends both. Both are standalone cleartext packets with exact
/// flags: the server ignores sealed NAKs and treats an ack by equality.
#[derive(Debug)]
pub struct AckNakScheduler {
    shared_timestamp: NetTime,
}

impl AckNakScheduler {
    /// Armed at connection birth.
    pub fn new(now: NetTime) -> Self {
        Self {
            shared_timestamp: now,
        }
    }

    /// Run one sweep.
    pub fn sweep(
        &mut self,
        now: NetTime,
        inbound: &InboundSequenceTracker,
        control: ControlHeader,
        stats: &mut TransportStats,
        out: &mut VecDeque<Vec<u8>>,
    ) {
        let elapsed = now.since(self.shared_timestamp);
        if inbound.nak_count() > 0 {
            if elapsed <= NAK_GATE {
                return;
            }
            emit_request_retransmit(inbound, control, stats, out);
            self.shared_timestamp = now;
            return;
        }
        if elapsed < ACK_GATE {
            return;
        }
        emit_cumulative_ack(inbound.highest_id_received(), control, stats, out);
        self.shared_timestamp = now;
    }
}

fn control_packet(flags: PacketFlags, body: &[u8], control: ControlHeader) -> Vec<u8> {
    let header = PacketHeader {
        sequence: control.sequence,
        flags,
        id: control.client_id,
        time: control.interval_id,
        iteration: control.iteration,
        ..PacketHeader::default()
    };
    let mut datagram = vec![0; PacketHeader::SIZE + body.len()];
    datagram[PacketHeader::SIZE..].copy_from_slice(body);
    let sealed = seal_in_place(header, &mut datagram, body.len(), body.len(), None)
        .expect("a cleartext all-optional body always seals");
    datagram.truncate(sealed.len);
    datagram
}

fn emit_cumulative_ack(
    highest_received: u32,
    control: ControlHeader,
    stats: &mut TransportStats,
    out: &mut VecDeque<Vec<u8>>,
) {
    let datagram = control_packet(
        PacketFlags::ACK_SEQUENCE,
        &highest_received.to_le_bytes(),
        control,
    );
    emit(out, stats, datagram);
    stats.acks_sent += 1;
}

fn emit_request_retransmit(
    inbound: &InboundSequenceTracker,
    control: ControlHeader,
    stats: &mut TransportStats,
    out: &mut VecDeque<Vec<u8>>,
) {
    let ids = inbound.nak_ids_ascending(MAX_NAK_IDS);
    let mut body = Vec::with_capacity(4 + ids.len() * 4);
    // At most MAX_NAK_IDS ids.
    body.extend_from_slice(&(ids.len() as u32).to_le_bytes());
    for id in &ids {
        body.extend_from_slice(&id.to_le_bytes());
    }
    let datagram = control_packet(PacketFlags::REQUEST_RETRANSMIT, &body, control);
    emit(out, stats, datagram);
    stats.naks_sent += 1;
    stats.nak_ids_sent += ids.len() as u64;
}
