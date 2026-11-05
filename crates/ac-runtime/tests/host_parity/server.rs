//! The server, as far as an arm is concerned (OpenAC `ParityServer` and
//! `ParitySessionOperations`): a scripted loopback stand-in that logs the
//! client in, says what a scenario tells it to say, and records every game
//! message the client sends. What it says goes through the client's whole
//! inbound path (the transport, the parser, the session, the plugin host),
//! the same on both arms.
//!
//! Like a real server it keeps what it sent and sends it again when the
//! client asks, and asks the client again for a packet that never came: a
//! datagram can be lost even on the loopback (a Windows socket read with a
//! timeout drops one now and then). It also sends again, unasked, what the
//! client has not been seen to have, so a scenario's wait never hangs on
//! the client's own resend request, which comes only after 600 ms.
//!
//! Its ports are bound and read as the session tests' stand-ins in this
//! crate are (`src/session/runner_test_server.rs`, private to the library
//! and so not reachable from an integration test): non-blocking and
//! polled, never with a socket receive timeout (on Windows a read whose
//! timeout fires as a datagram arrives can lose it, and the client
//! sends its login request and connect response once); bound on every
//! address below the system's client range; and taking only a login
//! request as the login, then only its own client's datagrams, since a
//! reused port still gets an earlier client's late packets.

use std::collections::hash_map::RandomState;
use std::collections::{BTreeMap, BTreeSet};
use std::hash::{BuildHasher, Hasher};
use std::io::ErrorKind;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

use ac_bits::Writer;
use ac_net::crypto::Isaac;
use ac_net::packet::{
    FragmentHeader, PacketFlags, PacketHeader, encode, parse_packet, write_fragment,
};

const SERVER_SEED: u32 = 0x1357_9BDF;
const CLIENT_SEED: u32 = 0x2468_ACE0;

/// The client action envelope.
pub const GAME_ACTION: u32 = 0xF7B1;
/// The ping request action (the outbound barrier, never recorded).
const PING_ACTION: u32 = 0x01E9;

/// How long a gap in the client's packets waits before the server asks
/// for it again.
const NAK_AFTER: Duration = Duration::from_millis(30);

/// One game message the client sent.
#[derive(Clone, PartialEq, Eq)]
pub struct Outbound {
    pub opcode: u32,
    pub body: Vec<u8>,
}

impl Outbound {
    /// The client action this carries, when it is one (OpenAC
    /// `ParityOutbound.GameAction`).
    pub fn game_action(&self) -> Option<u32> {
        (self.opcode == GAME_ACTION && self.body.len() >= 12).then(|| self.u32_at(8))
    }

    pub fn u32_at(&self, at: usize) -> u32 {
        u32::from_le_bytes(self.body[at..at + 4].try_into().unwrap())
    }

    pub fn u16_at(&self, at: usize) -> u16 {
        u16::from_le_bytes(self.body[at..at + 2].try_into().unwrap())
    }

    pub fn f32_at(&self, at: usize) -> f32 {
        f32::from_le_bytes(self.body[at..at + 4].try_into().unwrap())
    }

    /// A length-prefixed line of text at `at` (two bytes of length, then
    /// the characters).
    pub fn text_at(&self, at: usize) -> String {
        let len = usize::from(self.u16_at(at));
        ac_bits::cp1252::decode(&self.body[at + 2..at + 2 + len])
    }
}

impl std::fmt::Debug for Outbound {
    /// The message without the action sequence number, which counts every
    /// action sent (barriers included) and so says nothing about this one.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let hex = |bytes: &[u8]| -> String { bytes.iter().map(|b| format!("{b:02X}")).collect() };
        match self.game_action() {
            Some(action) => write!(f, "0xF7B1 action 0x{action:04X} {}", hex(&self.body[12..])),
            None => write!(f, "0x{:04X} {}", self.opcode, hex(&self.body[4..])),
        }
    }
}

/// The loopback server one arm runs against.
pub struct Server {
    socket: UdpSocket,
    connect: UdpSocket,
    client: Option<SocketAddr>,
    keystream: Isaac,
    sequence: u32,
    fragment_sequence: u32,
    game_event_sequence: u32,
    /// Every packet sent, by sequence, for the client's resend requests.
    sent: BTreeMap<u32, Vec<u8>>,
    /// Every packet below this sequence is known to have reached the
    /// client; the rest is sent again now and then.
    confirmed: u32,
    last_repair: Instant,
    /// The client's packet sequences seen, and since when a gap below the
    /// highest has been open.
    client_sequences: BTreeSet<u32>,
    gap_since: Option<Instant>,
    /// Every message the client sent, in order, barriers removed.
    outbound: Vec<Outbound>,
    /// How many of [`Self::outbound`] were taken.
    taken: usize,
    /// Fragment sequences already recorded (a resend is not a new message).
    seen: BTreeSet<u32>,
    /// Pings seen (the outbound barriers).
    pings: u32,
    /// How many times a character was let into the world.
    entries: usize,
    /// Fragments of messages not yet whole: (sequence) -> pieces.
    partial: BTreeMap<u32, Vec<Option<Vec<u8>>>>,
}

/// Two adjacent non-blocking ports, bound on every address (a port bound
/// only on the loopback address can coexist, on Windows, with a client
/// bound on every address, and would take that client's datagrams), below
/// the range the system hands out to clients, so no client is ever given
/// one.
fn bind_pair() -> (UdpSocket, UdpSocket) {
    // Even ports from 20,000 to 48,998 (the connect port one above), below
    // the system's client range (49,152 up), starting at a random one so
    // that stand-ins starting at once mostly look in different places; a
    // port in use is skipped.
    const PAIRS: u64 = 14_500;
    let start = RandomState::new().build_hasher().finish() % PAIRS;
    for step in 0..PAIRS {
        let port = 20_000 + 2 * ((start + step) % PAIRS) as u16;
        let Ok(login) = UdpSocket::bind(("0.0.0.0", port)) else {
            continue;
        };
        if let Ok(connect) = UdpSocket::bind(("0.0.0.0", port + 1)) {
            login.set_nonblocking(true).unwrap();
            connect.set_nonblocking(true).unwrap();
            return (login, connect);
        }
    }
    panic!("no two adjacent ports");
}

/// The pause between polls of an empty port.
const POLL: Duration = Duration::from_millis(1);

/// The next datagram on `socket` into `buf`, waiting until `deadline`
/// (`None` passes at once); `None` once it passed with nothing.
fn recv_until(
    socket: &UdpSocket,
    buf: &mut [u8],
    deadline: Option<Instant>,
) -> Option<(usize, SocketAddr)> {
    loop {
        match socket.recv_from(buf) {
            Ok(received) => return Some(received),
            // On Windows an ICMP "port unreachable" for an earlier send
            // (to a client port already closed) surfaces on a later
            // receive; it says nothing about this one.
            Err(e)
                if matches!(
                    e.kind(),
                    ErrorKind::ConnectionReset | ErrorKind::Interrupted
                ) => {}
            Err(e) if e.kind() == ErrorKind::WouldBlock => {
                if deadline.is_none_or(|deadline| Instant::now() >= deadline) {
                    return None;
                }
                std::thread::sleep(POLL);
            }
            Err(e) => panic!("the stand-in server's socket failed: {e}"),
        }
    }
}

/// Whether a datagram is a login request.
fn is_login_request(datagram: &[u8]) -> bool {
    parse_packet(datagram).is_ok_and(|p| p.header.flags.has(PacketFlags::LOGIN_REQUEST))
}

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

impl Server {
    pub fn bind() -> Self {
        // Polled: the arm reads them between its other waits.
        let (socket, connect) = bind_pair();
        Self {
            socket,
            connect,
            client: None,
            keystream: Isaac::new(SERVER_SEED),
            sequence: 2,
            fragment_sequence: 1,
            game_event_sequence: 0,
            sent: BTreeMap::new(),
            confirmed: 2,
            last_repair: Instant::now(),
            client_sequences: BTreeSet::new(),
            gap_since: None,
            outbound: Vec::new(),
            taken: 0,
            seen: BTreeSet::new(),
            pings: 0,
            entries: 0,
            partial: BTreeMap::new(),
        }
    }

    pub fn address(&self) -> SocketAddr {
        let port = self.socket.local_addr().unwrap().port();
        SocketAddr::from(([127, 0, 0, 1], port))
    }

    /// The login handshake, the data check and the character list, up to
    /// the client's request to enter the world with `character`, which the
    /// server allows; the character's own object is still to come.
    pub fn log_in(&mut self, characters: &[(u32, &str)]) -> Result<(), String> {
        let mut buf = [0u8; 2048];
        let deadline = Some(Instant::now() + Duration::from_secs(20));
        // Only a login request is one; anything else there is a stray.
        let client = loop {
            let Some((len, from)) = recv_until(&self.socket, &mut buf, deadline) else {
                return Err("the client never asked to log in".to_owned());
            };
            if is_login_request(&buf[..len]) {
                break from;
            }
        };
        self.client = Some(client);
        self.socket.send_to(&connect_request(), client).unwrap();
        let within = Some(Instant::now() + Duration::from_secs(5));
        loop {
            let Some((_, from)) = recv_until(&self.connect, &mut buf, within) else {
                return Err("no connect response".to_owned());
            };
            if from == client {
                break;
            }
        }
        self.send(&words(&[0xF7E5]));
        self.send(&character_list(characters));
        self.send(&server_name());
        self.send(&words(&[0xF7EA]));
        self.allow_entering()
    }

    /// The client asks to enter the world; the server allows it and the
    /// client enters (the character's own object is still to come).
    pub fn allow_entering(&mut self) -> Result<(), String> {
        self.entries += 1;
        self.wait_for_count(0xF7C8, self.entries)?;
        self.send(&words(&[0xF7DF]));
        self.wait_for_count(0xF657, self.entries)?;
        // The client answered the last of it.
        self.confirm();
        Ok(())
    }

    /// Says one game message.
    pub fn send(&mut self, body: &[u8]) {
        let client = self.client.expect("logged in");
        let pieces: Vec<&[u8]> = body.chunks(FragmentHeader::MAX_PAYLOAD).collect();
        let count = u16::try_from(pieces.len()).unwrap();
        for (index, piece) in pieces.iter().enumerate() {
            let mut fragment = Vec::new();
            write_fragment(
                &mut fragment,
                self.fragment_sequence,
                9,
                count,
                u16::try_from(index).unwrap(),
                piece,
            )
            .unwrap();
            let header = PacketHeader {
                sequence: self.sequence,
                flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
                ..PacketHeader::default()
            };
            let datagram = encode(header, &fragment, Some(&mut self.keystream)).unwrap();
            // Twice: a lost first copy is made good by the second before the
            // next message, so the client still reads them in the order said
            // (it drops the copy it does not need).
            self.socket.send_to(&datagram, client).unwrap();
            self.socket.send_to(&datagram, client).unwrap();
            self.sent.insert(self.sequence, datagram);
            self.sequence += 1;
        }
        self.fragment_sequence += 1;
    }

    /// Says a game event to the character (the `0xF7B0` envelope).
    pub fn game_event(&mut self, player: u32, event_type: u32, payload: &[u8]) {
        self.game_event_sequence += 1;
        let mut w = Writer::new();
        w.u32(0xF7B0);
        w.u32(player);
        w.u32(self.game_event_sequence);
        w.u32(event_type);
        w.bytes(payload);
        self.send(w.as_slice());
    }

    /// Everything sent so far reached the client.
    pub fn confirm(&mut self) {
        self.confirmed = self.sequence;
    }

    /// Sends again every packet not known to have reached the client (the
    /// client drops a copy it already has).
    pub fn resend_unconfirmed(&mut self) {
        let client = self.client.expect("logged in");
        for datagram in self.sent.range(self.confirmed..).map(|(_, d)| d) {
            self.socket.send_to(datagram, client).unwrap();
        }
        self.last_repair = Instant::now();
    }

    /// [`Self::resend_unconfirmed`], every tenth of a second while a wait
    /// goes on.
    pub fn repair(&mut self) {
        if self.client.is_some() && self.last_repair.elapsed() > Duration::from_millis(100) {
            self.resend_unconfirmed();
        }
    }

    /// Reads what the client sent so far into the record, sends again what
    /// the client asks for, and asks again for what it missed. Datagrams
    /// from anyone else are passed over.
    pub fn poll(&mut self) {
        let mut buf = [0u8; 2048];
        while let Some((len, from)) = recv_until(&self.socket, &mut buf, None) {
            if Some(from) != self.client {
                continue;
            }
            let Ok(packet) = parse_packet(&buf[..len]) else {
                continue;
            };
            if packet.header.sequence != 0 {
                self.client_sequences.insert(packet.header.sequence);
            }
            if packet.header.flags.has(PacketFlags::REQUEST_RETRANSMIT) {
                let client = self.client.expect("logged in");
                for id in packet.optional.retransmit_requests.iter() {
                    if let Some(datagram) = self.sent.get(&id) {
                        self.socket.send_to(datagram, client).unwrap();
                    }
                }
            }
            for fragment in packet.fragments() {
                let header = fragment.header;
                if self.seen.contains(&header.sequence) {
                    continue;
                }
                let body = if header.count == 1 {
                    Some(fragment.payload.to_vec())
                } else {
                    let pieces = self
                        .partial
                        .entry(header.sequence)
                        .or_insert_with(|| vec![None; usize::from(header.count)]);
                    pieces[usize::from(header.index)] = Some(fragment.payload.to_vec());
                    if pieces.iter().all(Option::is_some) {
                        let whole = pieces.iter().flatten().flatten().copied().collect();
                        self.partial.remove(&header.sequence);
                        Some(whole)
                    } else {
                        None
                    }
                };
                let Some(body) = body else { continue };
                self.seen.insert(header.sequence);
                if body.len() < 4 {
                    continue;
                }
                let message = Outbound {
                    opcode: u32::from_le_bytes(body[..4].try_into().unwrap()),
                    body,
                };
                if message.game_action() == Some(PING_ACTION) {
                    self.pings += 1;
                } else {
                    self.outbound.push(message);
                }
            }
        }
        self.ask_for_missing();
    }

    /// A gap below the client's highest packet open for a while: ask for
    /// the missing packets, as a server does.
    fn ask_for_missing(&mut self) {
        let (Some(&first), Some(&last)) =
            (self.client_sequences.first(), self.client_sequences.last())
        else {
            return;
        };
        let missing: Vec<u32> = (first..last)
            .filter(|s| !self.client_sequences.contains(s))
            .collect();
        if missing.is_empty() {
            self.gap_since = None;
            return;
        }
        let since = *self.gap_since.get_or_insert_with(Instant::now);
        if since.elapsed() < NAK_AFTER {
            return;
        }
        self.gap_since = Some(Instant::now());
        let mut w = Writer::new();
        w.u32(u32::try_from(missing.len()).unwrap());
        for id in &missing {
            w.u32(*id);
        }
        let header = PacketHeader {
            flags: PacketFlags::REQUEST_RETRANSMIT,
            ..PacketHeader::default()
        };
        let datagram = encode(header, w.as_slice(), None).unwrap();
        self.socket
            .send_to(&datagram, self.client.expect("logged in"))
            .unwrap();
    }

    /// Pings seen so far.
    pub fn pings(&self) -> u32 {
        self.pings
    }

    /// Everything recorded since the last take.
    pub fn take(&mut self) -> Vec<Outbound> {
        let taken = self.outbound[self.taken..].to_vec();
        self.taken = self.outbound.len();
        taken
    }

    /// What was recorded since the last take, without taking it.
    pub fn pending(&self) -> &[Outbound] {
        &self.outbound[self.taken..]
    }

    /// Waits until the client has sent `count` messages with this opcode
    /// (recorded like any other).
    pub fn wait_for_count(&mut self, opcode: u32, count: usize) -> Result<(), String> {
        let deadline = Instant::now() + Duration::from_secs(20);
        loop {
            self.poll();
            if self.outbound.iter().filter(|m| m.opcode == opcode).count() >= count {
                return Ok(());
            }
            self.repair();
            std::thread::sleep(Duration::from_millis(1));
            if Instant::now() > deadline {
                return Err(format!(
                    "the client never sent {opcode:#06x}; it sent {:?}",
                    self.outbound
                ));
            }
        }
    }
}

fn connect_request() -> Vec<u8> {
    let mut w = Writer::new();
    w.f64(1000.0);
    w.u64(0xFEED_FACE_CAFE_BABE);
    w.u32(0x1234);
    w.u32(SERVER_SEED);
    w.u32(CLIENT_SEED);
    w.u32(0);
    let header = PacketHeader {
        flags: PacketFlags::CONNECT_REQUEST,
        iteration: 7,
        ..PacketHeader::default()
    };
    encode(header, w.as_slice(), None).unwrap()
}

fn character_list(characters: &[(u32, &str)]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(u32::try_from(characters.len()).unwrap());
    for (character, name) in characters {
        w.u32(*character);
        w.string16l(name).unwrap();
        w.u32(0);
    }
    w.u32(0);
    w.i32(11);
    w.string16l(super::world::ACCOUNT_NAME).unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

/// The world's name and how many are on it (`ac-proto` `ServerName`), as
/// OpenAC's harness states them.
fn server_name() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF7E1);
    w.i32(super::world::SERVER_POPULATION);
    w.i32(800);
    w.string16l(super::world::WORLD_NAME).unwrap();
    w.into_vec()
}
