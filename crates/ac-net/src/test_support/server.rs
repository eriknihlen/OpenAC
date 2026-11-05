//! A behavioural model of the emulator server's network session, faithful
//! to the rules a client must survive: arrival-driven NAKs at expected+2
//! with a 1 s limit, cleartext-only NAK intake, the exact-flags ack rule,
//! the 256-word key search, out-of-order packet and fragment buffering,
//! the 2 s ack / 20 s time-sync bundles, the 120 s resend cache, and
//! termination on disconnect or 60 s of silence.

use std::collections::{HashMap, VecDeque};
use std::time::Duration;

use super::crypto::{MAXIMUM_EFFORT_LEVEL, ServerCrypto};
use crate::crypto::{Isaac, hash32};
use crate::packet::{FragmentHeader, PacketFlags, PacketHeader, parse_packet};
use crate::{GameMessageGroup, NetTime};

const MAX_NAK_IDS: u32 = 115;
const MAX_PACKET_BODY: usize = 464;
const QUEUE_MAX: usize = 0x0C;
const NAK_RATE_LIMIT: Duration = Duration::from_secs(1);
const ACK_INTERVAL: Duration = Duration::from_secs(2);
const TIME_SYNC_INTERVAL: Duration = Duration::from_secs(20);
const SESSION_TIMEOUT: Duration = Duration::from_secs(60);
const CACHE_PRUNE_INTERVAL: Duration = Duration::from_secs(5);
const CACHE_RETENTION_SECONDS: i64 = 120;
const TERMINATION_WINDOW: Duration = Duration::from_secs(2);
const SERVER_ID: u16 = 0x000C;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TerminationReason {
    PacketHeaderDisconnect,
    ClientSentNetworkErrorDisconnect,
    AbnormalSequenceReceived,
    NetworkTimeout,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionState {
    AwaitingLogin,
    AwaitingConnectResponse,
    Connected,
}

#[derive(Debug, Clone)]
struct OwnedPacket {
    header: PacketHeader,
    optional_raw: Vec<u8>,
    ack_sequence: u32,
    retransmit_requests: Vec<u32>,
    fragments: Vec<(FragmentHeader, Vec<u8>)>,
    payload_hash: u32,
}

#[derive(Debug, Clone)]
struct Draft {
    flags: PacketFlags,
    body: Vec<u8>,
    optional_len: usize,
}

#[derive(Debug, Clone)]
struct CachedS2C {
    sequence: u32,
    flags: PacketFlags,
    time: u16,
    body: Vec<u8>,
    optional_len: usize,
    xor: u32,
}

#[derive(Debug, Default)]
struct Bundle {
    messages: VecDeque<Vec<u8>>,
    prop_changed: bool,
    time_sync: bool,
    send_ack: bool,
    encrypted: bool,
}

impl Bundle {
    fn needs_sending(&self) -> bool {
        self.prop_changed || !self.messages.is_empty()
    }
}

/// A message being split into fragments for sending.
#[derive(Debug)]
struct OutboundMessage {
    data: Vec<u8>,
    queue: u16,
    sequence: u32,
    count: u16,
    index: u16,
    remaining: usize,
    tail_sent: bool,
}

impl OutboundMessage {
    fn new(data: Vec<u8>, sequence: u32, queue: u16) -> Self {
        let count = data.len().div_ceil(FragmentHeader::MAX_PAYLOAD) as u16;
        let remaining = data.len();
        Self {
            data,
            queue,
            sequence,
            count,
            index: 0,
            remaining,
            tail_sent: count == 1,
        }
    }

    fn next_size(&self) -> usize {
        FragmentHeader::SIZE + self.remaining.min(FragmentHeader::MAX_PAYLOAD)
    }

    fn tail_size(&self) -> usize {
        FragmentHeader::SIZE + self.data.len() % FragmentHeader::MAX_PAYLOAD
    }

    fn tail_fragment(&mut self) -> Vec<u8> {
        self.tail_sent = true;
        self.fragment(self.count - 1)
    }

    fn next_fragment(&mut self) -> Vec<u8> {
        let index = self.index;
        self.index += 1;
        self.fragment(index)
    }

    fn fragment(&mut self, index: u16) -> Vec<u8> {
        let position = usize::from(index) * FragmentHeader::MAX_PAYLOAD;
        let size = (self.data.len() - position).min(FragmentHeader::MAX_PAYLOAD);
        self.remaining -= size;
        let header = FragmentHeader {
            sequence: self.sequence,
            id: FragmentHeader::OUTBOUND_ID,
            count: self.count,
            total_size: (FragmentHeader::SIZE + size) as u16,
            index,
            queue: self.queue,
        };
        let mut out = header.to_bytes().to_vec();
        out.extend_from_slice(&self.data[position..position + size]);
        out
    }
}

#[derive(Debug)]
struct PartialC2S {
    total: usize,
    fragments: Vec<(u16, Vec<u8>)>,
}

impl PartialC2S {
    fn complete(&self) -> bool {
        self.fragments.len() == self.total
    }

    fn add(&mut self, index: u16, payload: Vec<u8>) {
        if self.complete() || self.fragments.iter().any(|(i, _)| *i == index) {
            return;
        }
        self.fragments.push((index, payload));
    }

    fn message(&mut self) -> Option<Vec<u8>> {
        self.fragments.sort_by_key(|(i, _)| *i);
        let body: Vec<u8> = self.fragments.iter().flat_map(|(_, p)| p.clone()).collect();
        (body.len() >= 4).then_some(body)
    }
}

/// The server side of one session.
#[derive(Debug)]
pub struct ServerModel {
    client_id: u32,
    cookie: u64,
    client_seed: u32,
    server_seed: u32,
    pub crypto: ServerCrypto,
    s2c_keystream: Isaac,

    pub state: SessionState,
    last_received_packet_sequence: u32,
    last_received_fragment_sequence: u32,
    out_of_order_packets: HashMap<u32, OwnedPacket>,
    partial_fragments: HashMap<u32, PartialC2S>,
    out_of_order_fragments: HashMap<u32, Vec<u8>>,
    last_nak_at: Option<NetTime>,

    packet_sequence: u32,
    s2c_fragment_sequence: u32,
    cached: HashMap<u32, CachedS2C>,
    last_prune_at: Option<NetTime>,
    next_ack_at: NetTime,
    next_resync_at: Option<NetTime>,
    send_resync: bool,
    bundles: Vec<Bundle>,
    flush_queue: VecDeque<Draft>,

    termination: Option<(TerminationReason, NetTime)>,
    pub released: bool,
    pub timeout_deadline: NetTime,

    /// Every game message the session dispatched, in dispatch order.
    pub dispatched: Vec<Vec<u8>>,
    /// Every datagram the server ever sent.
    pub sent: Vec<Vec<u8>>,
    pending_out: VecDeque<Vec<u8>>,

    pub crc_drops: usize,
    pub duplicate_drops: usize,
    pub state_drops: usize,
    pub retransmits_served: usize,
    pub login_requests: usize,
    pub connect_responses_accepted: usize,
    /// Messages queued when the connect response is accepted.
    pub welcome: Vec<(Vec<u8>, GameMessageGroup)>,
}

fn owned(datagram: &[u8]) -> Option<OwnedPacket> {
    let packet = parse_packet(datagram).ok()?;
    Some(OwnedPacket {
        header: packet.header,
        optional_raw: packet.optional.raw.to_vec(),
        ack_sequence: packet.optional.ack_sequence,
        retransmit_requests: packet.optional.retransmit_requests.iter().collect(),
        fragments: packet
            .fragments()
            .map(|f| (f.header, f.payload.to_vec()))
            .collect(),
        payload_hash: packet.payload_hash,
    })
}

fn secs(now: NetTime) -> f64 {
    now.as_duration().as_secs_f64()
}

impl ServerModel {
    pub fn new(
        now: NetTime,
        client_seed: u32,
        server_seed: u32,
        client_id: u32,
        cookie: u64,
    ) -> Self {
        Self {
            client_id,
            cookie,
            client_seed,
            server_seed,
            crypto: ServerCrypto::new(client_seed),
            s2c_keystream: Isaac::new(server_seed),
            state: SessionState::AwaitingLogin,
            last_received_packet_sequence: 1,
            last_received_fragment_sequence: 0,
            out_of_order_packets: HashMap::new(),
            partial_fragments: HashMap::new(),
            out_of_order_fragments: HashMap::new(),
            last_nak_at: None,
            packet_sequence: u32::MAX,
            s2c_fragment_sequence: 0,
            cached: HashMap::new(),
            last_prune_at: None,
            next_ack_at: now + ACK_INTERVAL,
            next_resync_at: None,
            send_resync: false,
            bundles: (0..QUEUE_MAX).map(|_| Bundle::default()).collect(),
            flush_queue: VecDeque::new(),
            termination: None,
            released: false,
            timeout_deadline: now + SESSION_TIMEOUT,
            dispatched: Vec::new(),
            sent: Vec::new(),
            pending_out: VecDeque::new(),
            crc_drops: 0,
            duplicate_drops: 0,
            state_drops: 0,
            retransmits_served: 0,
            login_requests: 0,
            connect_responses_accepted: 0,
            welcome: Vec::new(),
        }
    }

    pub fn last_received_packet_sequence(&self) -> u32 {
        self.last_received_packet_sequence
    }

    pub fn out_of_order_packet_count(&self) -> usize {
        self.out_of_order_packets.len()
    }

    pub fn fragment_gate_buffer_count(&self) -> usize {
        self.out_of_order_fragments.len()
    }

    pub fn termination_reason(&self) -> Option<TerminationReason> {
        self.termination.map(|(reason, _)| reason)
    }

    pub fn is_terminated(&self) -> bool {
        self.termination.is_some()
    }

    pub fn take_pending(&mut self) -> Vec<Vec<u8>> {
        self.pending_out.drain(..).collect()
    }

    pub fn receive(&mut self, now: NetTime, datagram: &[u8]) {
        if self.released {
            return;
        }
        let Some(packet) = owned(datagram) else {
            return;
        };
        let flags = packet.header.flags;
        if !self.check_state(flags) {
            self.state_drops += 1;
            return;
        }
        if flags.has(PacketFlags::CONNECT_RESPONSE) {
            self.handle_connect_response(now, &packet);
            return;
        }
        if !self.verify_crc(&packet) {
            self.crc_drops += 1;
            return;
        }
        if flags.has(PacketFlags::REQUEST_RETRANSMIT) && !flags.has(PacketFlags::ENCRYPTED_CHECKSUM)
        {
            let uncached: Vec<u32> = packet
                .retransmit_requests
                .iter()
                .copied()
                .filter(|&s| !self.try_retransmit(s))
                .collect();
            if !uncached.is_empty() {
                self.enqueue_reject(&uncached);
            }
            return;
        }
        if flags.has(PacketFlags::DISCONNECT) {
            self.terminate(now, TerminationReason::PacketHeaderDisconnect);
            return;
        }
        if flags.has(PacketFlags::NET_ERROR_DISCONNECT) {
            self.terminate(now, TerminationReason::ClientSentNetworkErrorDisconnect);
            return;
        }
        self.timeout_deadline = now + SESSION_TIMEOUT;

        let sequence = packet.header.sequence;
        // Ack-only packets borrow the current sequence and are exempt by
        // exact flag equality.
        if sequence <= self.last_received_packet_sequence
            && sequence != 0
            && !(flags == PacketFlags::ACK_SEQUENCE
                && sequence == self.last_received_packet_sequence)
        {
            self.duplicate_drops += 1;
            return;
        }

        let desired = self.last_received_packet_sequence + 1;
        if sequence > desired {
            self.out_of_order_packets.entry(sequence).or_insert(packet);
            let rate_open = self
                .last_nak_at
                .is_none_or(|at| now.since(at) > NAK_RATE_LIMIT);
            if desired + 2 <= sequence && rate_open {
                self.request_retransmission(now, sequence);
            }
            return;
        }

        self.handle_ordered(now, packet);
        while let Some(next) = self
            .out_of_order_packets
            .remove(&(self.last_received_packet_sequence + 1))
        {
            self.handle_ordered(now, next);
        }
        while let Some(message) = self
            .out_of_order_fragments
            .remove(&(self.last_received_fragment_sequence + 1))
        {
            self.dispatch(message);
        }
    }

    fn check_state(&self, flags: PacketFlags) -> bool {
        if flags.has(PacketFlags::LOGIN_REQUEST) && self.state != SessionState::AwaitingLogin {
            return false;
        }
        if flags.has(PacketFlags::CONNECT_RESPONSE)
            && self.state != SessionState::AwaitingConnectResponse
        {
            return false;
        }
        let control = PacketFlags::ACK_SEQUENCE
            | PacketFlags::TIME_SYNC
            | PacketFlags::ECHO_REQUEST
            | PacketFlags::FLOW;
        !(flags.has(control) && self.state == SessionState::AwaitingLogin)
    }

    fn verify_crc(&mut self, packet: &OwnedPacket) -> bool {
        let header_hash = packet.header.hash();
        if packet.header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM) {
            let key = packet.header.checksum.wrapping_sub(header_hash) ^ packet.payload_hash;
            if self.crypto.search(key) {
                self.crypto.consume_key(key);
                return true;
            }
            return false;
        }
        header_hash.wrapping_add(packet.payload_hash) == packet.header.checksum
    }

    fn handle_connect_response(&mut self, now: NetTime, packet: &OwnedPacket) {
        if packet.header.flags != PacketFlags::CONNECT_RESPONSE || packet.optional_raw.len() < 8 {
            return;
        }
        let mut cookie = [0; 8];
        cookie.copy_from_slice(&packet.optional_raw[..8]);
        if u64::from_le_bytes(cookie) != self.cookie {
            return;
        }
        self.state = SessionState::Connected;
        self.send_resync = true;
        self.timeout_deadline = now + SESSION_TIMEOUT;
        self.connect_responses_accepted += 1;
        for (body, group) in std::mem::take(&mut self.welcome) {
            self.enqueue_game_message(body, group);
        }
    }

    fn handle_ordered(&mut self, now: NetTime, packet: OwnedPacket) {
        let flags = packet.header.flags;
        if flags.has(PacketFlags::ACK_SEQUENCE) {
            let ack = packet.ack_sequence;
            self.cached.retain(|&sequence, _| sequence >= ack);
        }
        if flags.has(PacketFlags::LOGIN_REQUEST) {
            self.login_requests += 1;
            self.send_connect_request(now);
            return;
        }
        for (header, payload) in packet.fragments {
            self.process_fragment(header, payload);
        }
        if packet.header.sequence != 0 && flags != PacketFlags::ACK_SEQUENCE {
            self.last_received_packet_sequence = packet.header.sequence;
        }
    }

    fn process_fragment(&mut self, header: FragmentHeader, payload: Vec<u8>) {
        let mut message = None;
        if header.count != 1 {
            if let Some(buffer) = self.partial_fragments.get_mut(&header.sequence) {
                buffer.add(header.index, payload);
                if buffer.complete() {
                    message = buffer.message();
                    self.partial_fragments.remove(&header.sequence);
                }
            } else {
                let mut buffer = PartialC2S {
                    total: usize::from(header.count),
                    fragments: Vec::new(),
                };
                buffer.add(header.index, payload);
                self.partial_fragments.insert(header.sequence, buffer);
            }
        } else if payload.len() >= 4 {
            message = Some(payload);
        }
        let Some(message) = message else {
            return;
        };
        if header.sequence == self.last_received_fragment_sequence + 1 {
            self.dispatch(message);
        } else {
            self.out_of_order_fragments
                .entry(header.sequence)
                .or_insert(message);
        }
    }

    fn dispatch(&mut self, message: Vec<u8>) {
        self.dispatched.push(message);
        self.last_received_fragment_sequence += 1;
    }

    fn request_retransmission(&mut self, now: NetTime, received: u32) {
        let desired = self.last_received_packet_sequence + 1;
        let mut need = vec![desired];
        let bottom = desired + 1;
        if received < bottom || (received - bottom) as usize > MAXIMUM_EFFORT_LEVEL {
            self.terminate(now, TerminationReason::AbnormalSequenceReceived);
            return;
        }
        let mut count = 1;
        for id in bottom..received {
            if self.out_of_order_packets.contains_key(&id) {
                continue;
            }
            need.push(id);
            count += 1;
            if count >= MAX_NAK_IDS {
                break;
            }
        }
        let body = id_list(&need);
        self.flush_queue.push_back(Draft {
            flags: PacketFlags::REQUEST_RETRANSMIT,
            optional_len: body.len(),
            body,
        });
        self.last_nak_at = Some(now);
    }

    fn try_retransmit(&mut self, sequence: u32) -> bool {
        let Some(cached) = self.cached.get_mut(&sequence) else {
            return false;
        };
        cached.flags |= PacketFlags::RETRANSMISSION;
        let c = cached.clone();
        self.emit(c.sequence, c.flags, c.time, &c.body, c.optional_len, c.xor);
        self.retransmits_served += 1;
        true
    }

    fn enqueue_reject(&mut self, ids: &[u32]) {
        let body = id_list(ids);
        self.flush_queue.push_back(Draft {
            flags: PacketFlags::REJECT_RETRANSMIT,
            optional_len: body.len(),
            body,
        });
    }

    pub fn update(&mut self, now: NetTime) {
        if self.released {
            return;
        }
        if let Some((_, end)) = self.termination {
            self.run_network_update(now);
            if now > end {
                self.release();
            }
            return;
        }
        if now >= self.timeout_deadline {
            self.terminate(now, TerminationReason::NetworkTimeout);
            return;
        }
        self.run_network_update(now);
    }

    fn run_network_update(&mut self, now: NetTime) {
        if self
            .last_prune_at
            .is_none_or(|at| now.since(at) > CACHE_PRUNE_INTERVAL)
        {
            self.prune(now);
        }
        if self.state == SessionState::Connected {
            for group in 0..QUEUE_MAX {
                if group == 0 {
                    let resync_due = self.next_resync_at.is_none_or(|at| now > at);
                    let bundle = &mut self.bundles[0];
                    if self.send_resync && !bundle.time_sync && resync_due {
                        bundle.time_sync = true;
                        bundle.prop_changed = true;
                        bundle.encrypted = true;
                        self.next_resync_at = Some(now + TIME_SYNC_INTERVAL);
                    }
                    let bundle = &mut self.bundles[0];
                    if !bundle.send_ack && now > self.next_ack_at {
                        bundle.send_ack = true;
                        bundle.prop_changed = true;
                        self.next_ack_at = now + ACK_INTERVAL;
                    }
                }
                if !self.bundles[group].needs_sending() {
                    continue;
                }
                let bundle = std::mem::take(&mut self.bundles[group]);
                self.send_bundle(now, bundle, group as u16);
            }
        }
        while let Some(draft) = self.flush_queue.pop_front() {
            self.flush_one(now, draft);
        }
    }

    pub fn enqueue_game_message(&mut self, body: Vec<u8>, group: GameMessageGroup) {
        let bundle = &mut self.bundles[usize::from(group.to_u16())];
        bundle.encrypted = true;
        bundle.messages.push_back(body);
    }

    pub fn send_connect_request(&mut self, now: NetTime) {
        let mut w = ac_bits::Writer::new();
        w.f64(secs(now));
        w.u64(self.cookie);
        w.u32(self.client_id);
        w.u32(self.server_seed);
        w.u32(self.client_seed);
        w.u32(0);
        let body = w.into_vec();
        self.flush_queue.push_back(Draft {
            flags: PacketFlags::CONNECT_REQUEST,
            optional_len: body.len(),
            body,
        });
        self.state = SessionState::AwaitingConnectResponse;
    }

    fn send_bundle(&mut self, now: NetTime, mut bundle: Bundle, group: u16) {
        let mut write_optional = true;
        let mut messages: Vec<OutboundMessage> = Vec::new();
        while let Some(body) = bundle.messages.pop_front() {
            let sequence = self.s2c_fragment_sequence;
            self.s2c_fragment_sequence += 1;
            messages.push(OutboundMessage::new(body, sequence, group));
        }

        while !messages.is_empty() || write_optional {
            let mut flags = PacketFlags::NONE;
            let mut fragments: Vec<Vec<u8>> = Vec::new();
            let mut optional = Vec::new();
            if !messages.is_empty() {
                flags |= PacketFlags::BLOB_FRAGMENTS;
            }
            if bundle.encrypted {
                flags |= PacketFlags::ENCRYPTED_CHECKSUM;
            }
            let mut available = MAX_PACKET_BODY;

            if let Some(first) = messages.first_mut() {
                if first.remaining >= available {
                    // A large message fills the packet alone.
                    fragments.push(first.next_fragment());
                    if first.remaining == 0 {
                        messages.remove(0);
                    }
                } else {
                    if write_optional {
                        write_optional = false;
                        optional = self.optional_headers(now, &bundle, &mut flags);
                        available -= optional.len();
                    }
                    let mut index = 0;
                    while index < messages.len() {
                        let message = &mut messages[index];
                        let mut skipped = false;
                        if !message.tail_sent && available >= message.tail_size() {
                            let fragment = message.tail_fragment();
                            available -= fragment.len();
                            fragments.push(fragment);
                        } else if available >= message.next_size() {
                            let fragment = message.next_fragment();
                            available -= fragment.len();
                            fragments.push(fragment);
                        } else {
                            skipped = true;
                        }
                        if message.remaining == 0 {
                            messages.remove(index);
                        } else {
                            index += 1;
                        }
                        // The UI queue stays strictly ordered.
                        if skipped && group == GameMessageGroup::UiQueue.to_u16() {
                            break;
                        }
                    }
                }
            } else if write_optional {
                write_optional = false;
                optional = self.optional_headers(now, &bundle, &mut flags);
            }

            let optional_len = optional.len();
            let mut body = optional;
            for fragment in fragments {
                body.extend_from_slice(&fragment);
            }
            self.flush_queue.push_back(Draft {
                flags,
                body,
                optional_len,
            });
        }
    }

    fn optional_headers(&self, now: NetTime, bundle: &Bundle, flags: &mut PacketFlags) -> Vec<u8> {
        let mut w = ac_bits::Writer::new();
        if bundle.send_ack {
            *flags |= PacketFlags::ACK_SEQUENCE;
            w.u32(self.last_received_packet_sequence);
        }
        if bundle.time_sync {
            *flags |= PacketFlags::TIME_SYNC;
            w.f64(secs(now));
        }
        w.into_vec()
    }

    fn flush_one(&mut self, now: NetTime, draft: Draft) {
        let encrypted = draft.flags.has(PacketFlags::ENCRYPTED_CHECKSUM);
        if encrypted && self.packet_sequence == 0 {
            self.packet_sequence = 1;
        }
        let is_nak = draft.flags.has(PacketFlags::REQUEST_RETRANSMIT);
        let sequence = if draft.flags == PacketFlags::ACK_SEQUENCE || is_nak {
            self.packet_sequence
        } else {
            self.packet_sequence = self.packet_sequence.wrapping_add(1);
            self.packet_sequence
        };
        let time = now.as_duration().as_secs() as u16;
        let xor = if encrypted {
            self.s2c_keystream.next_word()
        } else {
            0
        };
        if sequence >= 2 && !is_nak {
            self.cached.entry(sequence).or_insert(CachedS2C {
                sequence,
                flags: draft.flags,
                time,
                body: draft.body.clone(),
                optional_len: draft.optional_len,
                xor,
            });
        }
        self.emit(
            sequence,
            draft.flags,
            time,
            &draft.body,
            draft.optional_len,
            xor,
        );
    }

    fn emit(
        &mut self,
        sequence: u32,
        flags: PacketFlags,
        time: u16,
        body: &[u8],
        optional_len: usize,
        xor: u32,
    ) {
        let mut header = PacketHeader {
            sequence,
            flags,
            id: SERVER_ID,
            iteration: 1,
            time,
            data_size: body.len() as u16,
            checksum: 0,
        };
        let mut payload = hash32(&body[..optional_len]);
        if flags.has(PacketFlags::BLOB_FRAGMENTS) {
            let mut rest = &body[optional_len..];
            while let Some((fragment, used)) = crate::packet::parse_fragment(rest) {
                payload = payload.wrapping_add(fragment.hash());
                rest = &rest[used..];
            }
        }
        header.checksum = header.hash().wrapping_add(payload ^ xor);
        let mut datagram = header.to_bytes().to_vec();
        datagram.extend_from_slice(body);
        self.sent.push(datagram.clone());
        self.pending_out.push_back(datagram);
    }

    fn prune(&mut self, now: NetTime) {
        self.last_prune_at = Some(now);
        let current = i64::from(now.as_duration().as_secs() as u16);
        self.cached.retain(|_, packet| {
            let time = i64::from(packet.time);
            let current = if current >= time {
                current
            } else {
                current + 65_535
            };
            current - time <= CACHE_RETENTION_SECONDS
        });
    }

    fn terminate(&mut self, now: NetTime, reason: TerminationReason) {
        self.termination = Some((reason, now + TERMINATION_WINDOW));
    }

    fn release(&mut self) {
        self.released = true;
        self.out_of_order_packets.clear();
        self.partial_fragments.clear();
        self.out_of_order_fragments.clear();
        self.cached.clear();
        self.flush_queue.clear();
        for bundle in &mut self.bundles {
            *bundle = Bundle::default();
        }
    }
}

fn id_list(ids: &[u32]) -> Vec<u8> {
    let mut body = (ids.len() as u32).to_le_bytes().to_vec();
    for id in ids {
        body.extend_from_slice(&id.to_le_bytes());
    }
    body
}
