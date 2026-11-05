//! One negotiated connection: the handshake's tail (the connect response
//! and its retries) plus the reliable transport.

use std::collections::VecDeque;
use std::time::Duration;

use crate::crypto::Isaac;
use crate::packet::{
    ConnectRequest, PacketFlags, PacketHeader, encode, parse_packet, transport_disconnect,
};
use crate::transport::{Admission, ReliableTransport, SendError, TransportStats};
use crate::{GameMessageGroup, InboundMessage, NetTime};

/// The connect response goes out this long after the connect request
/// arrives.
pub const CONNECT_RESPONSE_DELAY: Duration = Duration::from_millis(200);

/// Until the server answers, the connect response is resent whenever
/// strictly more than this has passed since the last copy.
pub const CONNECT_RESPONSE_RETRY: Duration = Duration::from_nanos(333_333_333);

/// The connection is lost when strictly more than this passes without a
/// valid packet from the server. A server that dies, or drops the session
/// without a word, is only ever seen as silence.
pub const SERVER_SILENCE_TIMEOUT: Duration = Duration::from_secs(140);

/// Where a datagram goes. The connect response goes to the port after the
/// login port; everything else goes to the login port.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Destination {
    /// The server's login port.
    Login,
    /// The login port plus one.
    Connect,
}

/// A datagram to send.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Transmit {
    /// Which server port.
    pub destination: Destination,
    /// The bytes.
    pub datagram: Vec<u8>,
}

/// A point-in-time view of the transport, for diagnostics.
#[derive(Debug, Clone, PartialEq)]
pub struct LinkSnapshot {
    /// Running counters.
    pub stats: TransportStats,
    /// Missing inbound sequences awaiting resend.
    pub nak_set: usize,
    /// Reclaimed inbound words not yet used.
    pub reclaimed_words: usize,
    /// Sent packets still cached for resend.
    pub cache_depth: usize,
    /// Resends queued for the next tick.
    pub pending_resends: usize,
    /// Highest inbound sequence seen.
    pub highest_id_received: u32,
    /// Last outbound sequence used.
    pub highest_id_sent: u32,
    /// The server's acknowledgement of our packets, as far as we know.
    pub ack_watermark: u32,
    /// Incomplete inbound messages.
    pub partial_messages: usize,
    /// The link-status loss percentage.
    pub packet_loss_percent: f64,
    /// Whether the server has answered the connect response.
    pub handshake_confirmed: bool,
}

/// A connection after the server's connect request was accepted.
///
/// Feed it every received datagram ([`Connection::handle_datagram`]), call
/// [`Connection::tick`] regularly (every few milliseconds to a frame), and
/// send what [`Connection::poll_transmit`] yields. Send game messages only
/// once [`Connection::is_handshake_confirmed`] is true.
#[derive(Debug)]
pub struct Connection {
    transport: ReliableTransport,
    client_id: u16,
    iteration: u16,
    connect_response: Vec<u8>,
    response_not_before: NetTime,
    response_sent_at: Option<NetTime>,
    handshake_confirmed: bool,
    server_time: f64,
    last_packet_at: NetTime,
    /// When the server-silence rule last ran (the previous tick).
    previous_silence_check: Option<NetTime>,
    /// How long the server may stay silent ([`SERVER_SILENCE_TIMEOUT`]).
    silence_timeout: Duration,
    /// The server fell silent for longer than [`SERVER_SILENCE_TIMEOUT`].
    lost: bool,
    closed: bool,
    outbox: VecDeque<Transmit>,
}

impl Connection {
    /// Accept a verified connect request: seed both keystreams, prepare
    /// the connect response.
    pub(crate) fn new(now: NetTime, header: &PacketHeader, request: &ConnectRequest) -> Self {
        // The id is carried in 32 bits but stamped as 16.
        let client_id = request.client_id as u16;
        let iteration = header.iteration;
        let response = PacketHeader {
            sequence: 1,
            flags: PacketFlags::CONNECT_RESPONSE,
            id: 0,
            ..PacketHeader::default()
        };
        // An 8-byte cleartext optional section always encodes.
        let connect_response = encode(response, &request.cookie.to_le_bytes(), None)
            .expect("an 8-byte cleartext optional section always encodes");
        Self {
            transport: ReliableTransport::new(
                now,
                Isaac::new(request.client_seed),
                Isaac::new(request.server_seed),
                client_id,
                iteration,
            ),
            client_id,
            iteration,
            connect_response,
            response_not_before: now + CONNECT_RESPONSE_DELAY,
            response_sent_at: None,
            handshake_confirmed: false,
            server_time: request.server_time,
            last_packet_at: now,
            previous_silence_check: None,
            silence_timeout: SERVER_SILENCE_TIMEOUT,
            lost: false,
            closed: false,
            outbox: VecDeque::new(),
        }
    }

    /// The client id the server assigned.
    #[must_use]
    pub fn client_id(&self) -> u16 {
        self.client_id
    }

    /// The connection iteration the server assigned.
    #[must_use]
    pub fn iteration(&self) -> u16 {
        self.iteration
    }

    /// True once the server has sent a valid packet other than its connect
    /// request (it accepted our connect response).
    #[must_use]
    pub fn is_handshake_confirmed(&self) -> bool {
        self.handshake_confirmed
    }

    /// True after [`Connection::disconnect`].
    #[must_use]
    pub fn is_closed(&self) -> bool {
        self.closed
    }

    /// The server's clock in seconds, from the connect request and then
    /// every positive time sync.
    #[must_use]
    pub fn server_time(&self) -> f64 {
        self.server_time
    }

    /// When the last valid packet arrived (the connect request at first).
    #[must_use]
    pub fn last_packet_at(&self) -> NetTime {
        self.last_packet_at
    }

    /// True once the server has been silent for strictly more than
    /// [`SERVER_SILENCE_TIMEOUT`] (judged in [`Connection::tick`]): the
    /// connection is lost and the session ends. There is nothing to log
    /// off from; only the transport disconnect goes out.
    #[must_use]
    pub fn is_lost(&self) -> bool {
        self.lost
    }

    /// Judge the server's silence against `timeout` instead of
    /// [`SERVER_SILENCE_TIMEOUT`] (the stall guard uses it too). Only tests
    /// shorten it: a whole session cannot wait 140 s for its server to be
    /// found gone.
    pub fn set_server_silence_timeout(&mut self, timeout: Duration) {
        self.silence_timeout = timeout;
    }

    /// The link-status loss percentage (what the link panel shows).
    #[must_use]
    pub fn packet_loss_percent(&self) -> f64 {
        self.transport.packet_loss_percentage()
    }

    /// Running transport counters.
    #[must_use]
    pub fn stats(&self) -> &TransportStats {
        &self.transport.stats
    }

    /// A diagnostic view of the transport.
    #[must_use]
    pub fn snapshot(&self) -> LinkSnapshot {
        let t = &self.transport;
        LinkSnapshot {
            stats: t.stats.clone(),
            nak_set: t.inbound.nak_count(),
            reclaimed_words: t.inbound.reclaimed_word_count(),
            cache_depth: t.outbound.cache_depth(),
            pending_resends: t.outbound.pending_resend_count(),
            highest_id_received: t.inbound.highest_id_received(),
            highest_id_sent: t.outbound.highest_id_sent(),
            ack_watermark: t.outbound.ack_watermark(),
            partial_messages: t.assembler.partial_count(),
            packet_loss_percent: t.packet_loss_percentage(),
            handshake_confirmed: self.handshake_confirmed,
        }
    }

    /// The next datagram to send.
    pub fn poll_transmit(&mut self) -> Option<Transmit> {
        self.outbox.pop_front()
    }

    /// Queue a game message on `group`; a body over 448 bytes goes out as
    /// several fragments of one message, one packet each.
    pub fn send_message(&mut self, body: &[u8], group: GameMessageGroup) -> Result<(), SendError> {
        if self.closed {
            return Err(SendError::Closed);
        }
        if !self.handshake_confirmed {
            return Err(SendError::NotConfirmed);
        }
        let result = self.transport.send_game_message(body, group.to_u16());
        self.drain_transport();
        result
    }

    /// Process one received datagram; returns the game messages it
    /// completed, in the order their fragments arrived.
    pub fn handle_datagram(&mut self, now: NetTime, datagram: &[u8]) -> Vec<InboundMessage> {
        let mut messages = Vec::new();
        if !self.closed {
            self.process(now, datagram, &mut messages);
            self.drain_transport();
        }
        messages
    }

    fn process(&mut self, now: NetTime, datagram: &[u8], messages: &mut Vec<InboundMessage>) {
        let Ok(packet) = parse_packet(datagram) else {
            return;
        };
        let header = packet.header;
        let encrypted = header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM);
        let t = &mut self.transport;

        if header.sequence == 0 {
            // Unsequenced packets never touch the keystream or the watermark.
            if encrypted || !packet.verify(None) {
                return;
            }
        } else {
            match t.inbound.admit(header.sequence, encrypted, &mut t.stats) {
                Admission::Drop => return,
                Admission::Process { key, draw_order } => {
                    if !packet.verify(key) {
                        t.stats.checksum_failures += 1;
                        if let Some(key) = key {
                            t.inbound
                                .repark(header.sequence, key, draw_order, &mut t.stats);
                        }
                        return;
                    }
                }
            }
        }

        self.last_packet_at = now;
        t.stats.packets_received += 1;
        if !self.handshake_confirmed && !header.flags.has(PacketFlags::CONNECT_REQUEST) {
            self.handshake_confirmed = true;
        }

        let optional = &packet.optional;
        if header.flags.has(PacketFlags::REQUEST_RETRANSMIT) {
            t.outbound
                .on_retransmit_request(optional.retransmit_requests, &mut t.stats);
        }
        if header.flags.has(PacketFlags::REJECT_RETRANSMIT) {
            t.stats.rejects_received += 1;
            t.inbound.on_reject_retransmit(optional.reject_retransmits);
            if header.sequence != 0 && !encrypted {
                t.inbound
                    .on_cleartext_reject_sequence(header.sequence, &mut t.stats);
            }
        }
        if header.flags.has(PacketFlags::ACK_SEQUENCE) {
            t.outbound
                .on_ack_sequence(optional.ack_sequence, &mut t.stats);
        }
        if header.flags.has(PacketFlags::TIME_SYNC) && optional.time_sync > 0.0 {
            self.server_time = optional.time_sync;
        }

        for fragment in packet.fragments() {
            if let Some(message) = t.assembler.ingest(now, &fragment)
                && message.body.len() >= 4
            {
                messages.push(message);
            }
        }
    }

    /// Advance time: send or resend the connect response when due, then run
    /// the transport sweep (acks, NAKs, requested resends, evictions).
    pub fn tick(&mut self, now: NetTime) {
        if self.closed {
            return;
        }
        match self.response_sent_at {
            None if now >= self.response_not_before => self.send_connect_response(now),
            Some(at) if !self.handshake_confirmed && now.since(at) > CONNECT_RESPONSE_RETRY => {
                self.send_connect_response(now);
            }
            _ => {}
        }
        if self.response_sent_at.is_some() {
            self.transport.sweep(now);
            self.drain_transport();
        }
        self.check_server_silence(now);
    }

    /// The server-silence rule, once per tick after the received packets
    /// were handled: strictly more than [`SERVER_SILENCE_TIMEOUT`] since
    /// the last valid packet loses the connection. The stall guard: when
    /// the client itself did not tick for that long (hung, a debugger), the
    /// packets waiting on the socket were only read this tick, so it does
    /// not judge the server; the next tick does. The first tick only
    /// starts the clock.
    fn check_server_silence(&mut self, now: NetTime) {
        if self.lost {
            return;
        }
        let Some(previous) = self.previous_silence_check.replace(now) else {
            return;
        };
        if now.since(previous) < self.silence_timeout
            && now.since(self.last_packet_at) > self.silence_timeout
        {
            self.lost = true;
        }
    }

    fn send_connect_response(&mut self, now: NetTime) {
        // Every copy is the identical datagram.
        self.outbox.push_back(Transmit {
            destination: Destination::Connect,
            datagram: self.connect_response.clone(),
        });
        self.response_sent_at = Some(now);
    }

    /// Queue the transport disconnect and close: later sends fail, later
    /// datagrams and ticks are ignored. (A graceful logout sends the game's
    /// logoff message and waits for its confirmation first.)
    pub fn disconnect(&mut self) {
        if self.closed {
            return;
        }
        self.outbox.push_back(Transmit {
            destination: Destination::Login,
            datagram: transport_disconnect(self.client_id, self.iteration),
        });
        self.closed = true;
    }

    fn drain_transport(&mut self) {
        while let Some(datagram) = self.transport.pop_outgoing() {
            self.outbox.push_back(Transmit {
                destination: Destination::Login,
                datagram,
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Handshake;
    use crate::packet::{decode_with_keystream, parse_login_request, write_single_fragment};

    const CLIENT_SEED: u32 = 0x0102_0304;
    const SERVER_SEED: u32 = 0xAABB_CCDD;
    const COOKIE: u64 = 0xFEED_FACE_CAFE_BABE;

    fn connect_request(client_id: u32, iteration: u16, server_time: f64) -> Vec<u8> {
        let mut w = ac_bits::Writer::new();
        w.f64(server_time);
        w.u64(COOKIE);
        w.u32(client_id);
        w.u32(SERVER_SEED);
        w.u32(CLIENT_SEED);
        w.u32(0);
        let header = PacketHeader {
            flags: PacketFlags::CONNECT_REQUEST,
            iteration,
            ..PacketHeader::default()
        };
        encode(header, w.as_slice(), None).unwrap()
    }

    fn connected() -> Connection {
        let mut hs = Handshake::start(NetTime::ZERO, "acct", "pw", 1).unwrap();
        hs.poll_transmit().unwrap();
        hs.handle_datagram(NetTime::ZERO, &connect_request(0x1234, 7, 12345.5))
            .unwrap()
    }

    /// Builds sealed server packets the way the server does.
    struct Server {
        keystream: Isaac,
        fragment_sequence: u32,
    }

    impl Server {
        fn new() -> Self {
            Self {
                keystream: Isaac::new(SERVER_SEED),
                fragment_sequence: 0,
            }
        }

        fn message(&mut self, sequence: u32, body: &[u8]) -> Vec<u8> {
            let mut fragment = Vec::new();
            write_single_fragment(&mut fragment, self.fragment_sequence, 9, body).unwrap();
            self.fragment_sequence += 1;
            let header = PacketHeader {
                sequence,
                flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
                ..PacketHeader::default()
            };
            encode(header, &fragment, Some(&mut self.keystream)).unwrap()
        }
    }

    fn drain(conn: &mut Connection) -> Vec<Transmit> {
        std::iter::from_fn(|| conn.poll_transmit()).collect()
    }

    fn ms(millis: u64) -> NetTime {
        NetTime::from_millis(millis)
    }

    #[test]
    fn handshake_sends_one_login_and_accepts_only_a_valid_connect_request() {
        let mut hs = Handshake::start(NetTime::ZERO, "testaccount", "testpassword", 42).unwrap();
        let login = hs.poll_transmit().unwrap();
        assert_eq!(login.destination, Destination::Login);
        assert!(hs.poll_transmit().is_none());
        let packet = decode_with_keystream(&login.datagram, None).unwrap();
        assert_eq!(packet.header.flags, PacketFlags::LOGIN_REQUEST);
        assert_eq!(packet.header.sequence, 0);
        let fields = parse_login_request(packet.body).unwrap();
        assert_eq!(fields.account, "testaccount");
        assert_eq!(fields.timestamp, 42);

        // Noise, a bad checksum, a sealed packet: all ignored.
        assert!(hs.handle_datagram(ms(1), &[0; 10]).is_none());
        let mut corrupt = connect_request(0x1234, 7, 1.0);
        corrupt[8] ^= 1;
        assert!(hs.handle_datagram(ms(1), &corrupt).is_none());
        assert!(
            hs.handle_datagram(ms(1), &Server::new().message(2, &[1, 2, 3, 4]))
                .is_none()
        );

        let conn = hs
            .handle_datagram(ms(5), &connect_request(0x0001_1234, 7, 12345.5))
            .unwrap();
        assert_eq!(conn.client_id(), 0x1234);
        assert_eq!(conn.iteration(), 7);
        assert_eq!(conn.server_time(), 12345.5);
        assert!(!conn.is_handshake_confirmed());
        assert!(
            hs.handle_datagram(ms(6), &connect_request(1, 1, 1.0))
                .is_none()
        );
    }

    // Source: OpenAC `WorldSession.Connection.cs` (200 ms delay, 0.333 s retry until
    // confirmed) and tests `ConnectResponseRetransmitTests.cs`.
    #[test]
    fn connect_response_waits_200ms_and_retries_until_confirmed() {
        let mut conn = connected();
        conn.tick(ms(199));
        assert!(drain(&mut conn).is_empty());
        conn.tick(ms(200));
        let sent = drain(&mut conn);
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].destination, Destination::Connect);
        let packet = decode_with_keystream(&sent[0].datagram, None).unwrap();
        assert_eq!(packet.header.sequence, 1);
        assert_eq!(packet.header.flags, PacketFlags::CONNECT_RESPONSE);
        assert_eq!(packet.header.id, 0);
        assert_eq!(packet.body, &COOKIE.to_le_bytes()[..]);

        // Strictly greater than 0.333333333 s.
        let first = NetTime::from_millis(200);
        conn.tick(first + CONNECT_RESPONSE_RETRY);
        assert!(drain(&mut conn).is_empty());
        let retry_at = first + CONNECT_RESPONSE_RETRY + Duration::from_nanos(1);
        conn.tick(retry_at);
        let retry = drain(&mut conn);
        assert_eq!(retry.len(), 1);
        assert_eq!(retry[0], sent[0]);

        // Any valid server packet confirms; retries stop.
        let mut server = Server::new();
        let messages = conn.handle_datagram(ms(700), &server.message(2, &[0xE0, 0xF7, 0, 0]));
        assert_eq!(messages.len(), 1);
        assert!(conn.is_handshake_confirmed());
        conn.tick(ms(5_000));
        assert!(
            drain(&mut conn)
                .iter()
                .all(|t| t.destination == Destination::Login)
        );
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.SequenceZero_CleartextBypassesTracker_EncryptedDrops`.
    #[test]
    fn sequence_zero_bypasses_the_tracker() {
        let mut conn = connected();
        let before = conn.snapshot();
        let time_sync = |keystream: Option<&mut Isaac>, flags: PacketFlags| {
            encode(
                PacketHeader {
                    flags,
                    ..PacketHeader::default()
                },
                &777.5f64.to_le_bytes(),
                keystream,
            )
            .unwrap()
        };
        conn.handle_datagram(ms(1), &time_sync(None, PacketFlags::TIME_SYNC));
        assert_eq!(conn.server_time(), 777.5);
        let after = conn.snapshot();
        assert_eq!(after.highest_id_received, before.highest_id_received);
        assert_eq!(after.nak_set, 0);
        assert_eq!(after.stats.keys_parked, 0);

        // A sealed sequence-zero packet is dropped and costs no word.
        let mut throwaway = Isaac::new(0xDEAD_BEEF);
        conn.handle_datagram(
            ms(2),
            &time_sync(
                Some(&mut throwaway),
                PacketFlags::TIME_SYNC | PacketFlags::ENCRYPTED_CHECKSUM,
            ),
        );
        let mut server = Server::new();
        let messages = conn.handle_datagram(ms(3), &server.message(2, b"\xE0\xF7\0\0intact"));
        assert_eq!(messages.len(), 1);
        assert_eq!(conn.stats().checksum_failures, 0);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.DuplicateServerPacket_DropsBeforeDispatch`.
    #[test]
    fn a_duplicate_server_packet_is_dropped_before_dispatch() {
        let mut conn = connected();
        let mut server = Server::new();
        let once = server.message(2, b"\xE0\xF7\0\0once");
        assert_eq!(conn.handle_datagram(ms(1), &once).len(), 1);
        assert!(conn.handle_datagram(ms(2), &once).is_empty());
        assert_eq!(conn.stats().inbound_dups_dropped, 1);
        let next = server.message(3, b"\xE0\xF7\0\0aligned");
        assert_eq!(conn.handle_datagram(ms(3), &next).len(), 1);
        assert_eq!(conn.stats().checksum_failures, 0);
    }

    // Source: OpenAC tests `InboundSequenceTrackerTests.S2CLoss_LaterPacketsStillDecode_LateRedeliveryCompletesTheMessage`.
    #[test]
    fn a_lost_packet_parks_its_word_and_its_late_arrival_decodes() {
        let mut conn = connected();
        let mut server = Server::new();
        let lost = server.message(2, b"\xE0\xF7\0\0lost");
        let after = server.message(3, b"\xE0\xF7\0\0after");
        let got = conn.handle_datagram(ms(1), &after);
        assert_eq!(got[0].body, b"\xE0\xF7\0\0after");
        assert_eq!(conn.snapshot().nak_set, 1);
        assert_eq!(conn.stats().keys_parked, 1);
        let got = conn.handle_datagram(ms(2), &lost);
        assert_eq!(got[0].body, b"\xE0\xF7\0\0lost");
        assert_eq!(conn.snapshot().nak_set, 0);
        assert_eq!(conn.stats().checksum_failures, 0);
        assert_eq!(conn.stats().inbound_dups_dropped, 0);
    }

    #[test]
    fn a_corrupt_packet_reparks_its_word_for_the_resend() {
        let mut conn = connected();
        let mut server = Server::new();
        let good = server.message(2, b"\xE0\xF7\0\0good");
        let mut corrupt = good.clone();
        let last = corrupt.len() - 1;
        corrupt[last] ^= 0xFF;
        assert!(conn.handle_datagram(ms(1), &corrupt).is_empty());
        assert_eq!(conn.stats().checksum_failures, 1);
        assert_eq!(conn.snapshot().nak_set, 1);
        assert_eq!(conn.handle_datagram(ms(2), &good).len(), 1);
        assert_eq!(conn.snapshot().nak_set, 0);
    }

    // Source: OpenAC tests `WorldSessionNegotiationShutdownTests.cs` and
    // `Packets/TransportDisconnectTests.cs`.
    #[test]
    fn disconnect_sends_the_negotiated_identity_and_closes() {
        let mut conn = connected();
        conn.disconnect();
        let sent = drain(&mut conn);
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].destination, Destination::Login);
        let packet = decode_with_keystream(&sent[0].datagram, None).unwrap();
        assert!(packet.header.flags.has(PacketFlags::DISCONNECT));
        assert_eq!(packet.header.id, 0x1234);
        assert_eq!(packet.header.iteration, 7);
        assert!(conn.is_closed());
        assert_eq!(
            conn.send_message(&[1, 2, 3, 4], GameMessageGroup::UiQueue),
            Err(SendError::Closed)
        );
        conn.tick(ms(10_000));
        conn.disconnect();
        assert!(drain(&mut conn).is_empty());
    }

    #[test]
    fn messages_go_to_the_login_port_sealed_from_sequence_two() {
        let mut conn = connected();
        // Before the server confirms the handshake, a send is refused.
        assert_eq!(
            conn.send_message(&[0x58, 0xF6, 0, 0], GameMessageGroup::UiQueue),
            Err(SendError::NotConfirmed)
        );
        conn.tick(ms(200));
        conn.handle_datagram(ms(250), &Server::new().message(2, &[0xDF, 0xF7, 0, 0]));
        assert!(conn.is_handshake_confirmed());
        drain(&mut conn);
        conn.send_message(&[0x58, 0xF6, 0, 0], GameMessageGroup::UiQueue)
            .unwrap();
        let sent = drain(&mut conn);
        assert_eq!(sent.len(), 1);
        assert_eq!(sent[0].destination, Destination::Login);
        let mut client = Isaac::new(CLIENT_SEED);
        let packet = decode_with_keystream(&sent[0].datagram, Some(&mut client)).unwrap();
        assert_eq!(packet.header.sequence, 2);
        assert_eq!(packet.header.id, 0x1234);
        assert_eq!(packet.header.iteration, 7);
        let fragment = packet.fragments().next().unwrap();
        assert_eq!(fragment.header.sequence, 1);
        assert_eq!(fragment.header.queue, GameMessageGroup::UiQueue.to_u16());
        // A body over one fragment goes out as one packet per fragment.
        conn.send_message(&[0; 449], GameMessageGroup::UiQueue)
            .unwrap();
        let sent = drain(&mut conn);
        assert_eq!(sent.len(), 2);
        for (datagram, (sequence, index)) in sent.iter().zip([(3, 0), (4, 1)]) {
            let packet = decode_with_keystream(&datagram.datagram, Some(&mut client)).unwrap();
            assert_eq!(packet.header.sequence, sequence);
            let fragment = packet.fragments().next().unwrap();
            assert_eq!(fragment.header.sequence, 2);
            assert_eq!((fragment.header.count, fragment.header.index), (2, index));
        }
    }

    /// Tick once a second from `from` to `to` seconds (inclusive), on top
    /// of `offset`.
    fn tick_seconds(conn: &mut Connection, offset: NetTime, from: u64, to: u64) {
        for s in from..=to {
            conn.tick(offset + Duration::from_secs(s));
        }
    }

    // OpenAC's server-silence rule (`ServerSilenceTests`: the exact 140 s
    // edge, strictly greater).
    #[test]
    fn a_server_silent_for_more_than_140_s_loses_the_connection() {
        let mut conn = connected();
        conn.tick(ms(200));
        let mut server = Server::new();
        let last = ms(250);
        conn.handle_datagram(last, &server.message(2, &[0xE0, 0xF7, 0, 0]));
        assert!(conn.is_handshake_confirmed());
        tick_seconds(&mut conn, last, 1, 139);
        assert!(!conn.is_lost());
        // Exactly 140 s of silence is not yet a loss.
        conn.tick(last + SERVER_SILENCE_TIMEOUT);
        assert!(!conn.is_lost());
        conn.tick(last + SERVER_SILENCE_TIMEOUT + Duration::from_nanos(1));
        assert!(conn.is_lost());
        // Lost for good; nothing to log off from, the transport disconnect
        // still goes out.
        conn.handle_datagram(
            last + Duration::from_secs(141),
            &server.message(3, &[0xE0, 0xF7, 0, 0]),
        );
        conn.tick(last + Duration::from_secs(142));
        assert!(conn.is_lost());
        drain(&mut conn);
        conn.disconnect();
        let sent = drain(&mut conn);
        assert_eq!(sent.len(), 1);
        let packet = decode_with_keystream(&sent[0].datagram, None).unwrap();
        assert!(packet.header.flags.has(PacketFlags::DISCONNECT));
    }

    // What counts as the server's traffic: every packet that passes the
    // sequence and checksum checks, whatever it carries; noise and a
    // corrupt packet do not.
    #[test]
    fn only_a_valid_server_packet_restarts_the_silence() {
        let mut conn = connected();
        let mut server = Server::new();
        let start = NetTime::ZERO;
        tick_seconds(&mut conn, start, 1, 100);
        let ack_only = server.message(2, &[0xE0, 0xF7, 0, 0]);
        conn.handle_datagram(start + Duration::from_secs(100), &ack_only);
        tick_seconds(&mut conn, start, 101, 230);
        assert!(!conn.is_lost(), "a packet at 100 s holds until 240 s");
        let mut corrupt = server.message(3, b"\xE0\xF7\0\0late");
        let end = corrupt.len() - 1;
        corrupt[end] ^= 0xFF;
        conn.handle_datagram(start + Duration::from_secs(231), &corrupt);
        conn.handle_datagram(start + Duration::from_secs(232), &[0; 10]);
        tick_seconds(&mut conn, start, 231, 240);
        assert!(!conn.is_lost());
        conn.tick(start + Duration::from_millis(240_001));
        assert!(conn.is_lost());
    }

    // The stall guard: a client that did not tick for 140 s reads what
    // waited on its socket first, so that tick does not judge the server;
    // the first tick only starts the clock.
    #[test]
    fn a_tick_after_a_client_stall_does_not_judge_the_server() {
        let mut conn = connected();
        conn.tick(ms(150_000));
        assert!(!conn.is_lost(), "the first tick only starts the clock");
        conn.tick(ms(151_000));
        assert!(conn.is_lost(), "the next one judges");

        let mut conn = connected();
        conn.tick(ms(1_000));
        conn.tick(ms(200_000));
        assert!(!conn.is_lost(), "199 s without a tick: not judged");
        // What waited on the socket arrives on that turn.
        let mut server = Server::new();
        conn.handle_datagram(ms(200_000), &server.message(2, &[0xE0, 0xF7, 0, 0]));
        conn.tick(ms(201_000));
        assert!(!conn.is_lost());

        let mut conn = connected();
        conn.tick(ms(1_000));
        conn.tick(ms(141_000));
        assert!(!conn.is_lost(), "exactly 140 s between ticks is a stall");
        conn.tick(ms(142_000));
        assert!(conn.is_lost(), "nothing waited: the next tick judges");
    }

    // The same edges on a shortened timeout (the session tests' seam).
    #[test]
    fn a_shortened_silence_timeout_keeps_the_edges() {
        let mut conn = connected();
        conn.set_server_silence_timeout(Duration::from_secs(2));
        conn.tick(ms(0));
        conn.tick(ms(1_000));
        conn.tick(ms(2_000));
        assert!(!conn.is_lost(), "exactly the timeout is not yet a loss");
        conn.tick(ms(2_001));
        assert!(conn.is_lost());

        let mut conn = connected();
        conn.set_server_silence_timeout(Duration::from_secs(2));
        conn.tick(ms(0));
        conn.tick(ms(3_000));
        assert!(!conn.is_lost(), "a stall of the timeout is not judged");
        conn.tick(ms(3_001));
        assert!(conn.is_lost());
    }
}
