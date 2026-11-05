//! A whole session against a loopback stand-in for the server that boots it
//! once the character is in the world, the way the server does when the
//! account logs in again elsewhere: `CharacterError(Logon)`, then silence.
//! The session must end at once with the reason, not
//! run until a timeout.

use std::net::UdpSocket;

use ac_bits::Writer;
use ac_net::crypto::Isaac;
use ac_net::packet::{PacketFlags, PacketHeader, encode, parse_packet, write_single_fragment};

use super::test_server::{bind_pair, recv_from_client, recv_login};
use super::*;

const SERVER_SEED: u32 = 0xAABB_CCDD;
const CLIENT_SEED: u32 = 0x0102_0304;
const PLAYER: u32 = 0x5000_000A;

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

fn character_list() -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(1);
    w.u32(PLAYER);
    w.string16l("+Booted").unwrap();
    w.u32(0);
    w.u32(0);
    w.i32(11);
    w.string16l("testaccount2").unwrap();
    w.u32(1);
    w.u32(1);
    w.into_vec()
}

fn words(words: &[u32]) -> Vec<u8> {
    words.iter().flat_map(|w| w.to_le_bytes()).collect()
}

/// Sealed, sequenced server packets, one message each.
struct Server {
    socket: UdpSocket,
    client: std::net::SocketAddr,
    keystream: Isaac,
    sequence: u32,
    fragment_sequence: u32,
}

impl Server {
    fn send(&mut self, body: &[u8]) {
        let mut fragment = Vec::new();
        write_single_fragment(&mut fragment, self.fragment_sequence, 9, body).unwrap();
        self.fragment_sequence += 1;
        let header = PacketHeader {
            sequence: self.sequence,
            flags: PacketFlags::BLOB_FRAGMENTS | PacketFlags::ENCRYPTED_CHECKSUM,
            ..PacketHeader::default()
        };
        self.sequence += 1;
        let datagram = encode(header, &fragment, Some(&mut self.keystream)).unwrap();
        self.socket.send_to(&datagram, self.client).unwrap();
    }

    /// Read client packets until one carries a message with `opcode`.
    fn wait_for(&self, opcode: u32) {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut buf = [0u8; 2048];
        while let Some(len) = recv_from_client(&self.socket, &mut buf, self.client, deadline) {
            let Ok(packet) = parse_packet(&buf[..len]) else {
                continue;
            };
            if packet
                .fragments()
                .any(|f| f.header.index == 0 && f.payload.get(..4) == Some(&opcode.to_le_bytes()))
            {
                return;
            }
        }
        panic!("the client never sent {opcode:#06x}");
    }
}

/// The handshake, the character list and the world entry, up to the
/// client's request to enter as +Booted.
fn into_world(login: UdpSocket, connect: &UdpSocket) -> Server {
    let mut buf = [0u8; 2048];
    let client =
        recv_login(&login, Instant::now() + Duration::from_secs(20)).expect("the login request");
    login.send_to(&connect_request(), client).unwrap();
    let within = Instant::now() + Duration::from_secs(5);
    recv_from_client(connect, &mut buf, client, within).expect("the connect response");
    let mut server = Server {
        socket: login,
        client,
        keystream: Isaac::new(SERVER_SEED),
        sequence: 2,
        fragment_sequence: 1,
    };
    server.send(&words(&[0xF7E5]));
    server.send(&character_list());
    server.send(&words(&[0xF7EA]));
    server.wait_for(0xF7C8);
    server.send(&words(&[0xF7DF]));
    server.wait_for(0xF657);
    server
}

fn booted_config(address: std::net::SocketAddr) -> SessionConfig {
    SessionConfig {
        server: address,
        account: "testaccount2".into(),
        password: "x".into(),
        character: CharacterSelector::Name("+Booted".into()),
        loss: LossOptions::default(),
        enter_world: true,
        await_character_selection: false,
        data_directory: None,
        tables: None,
        content: None,
        diagnostics: NetDiagnostics::default(),
        login: LoginAutomation::default(),
        plugins: None,
    }
}

#[test]
fn a_dropped_handle_logs_its_session_off_and_waits_for_it() {
    // A host thread that panics (or returns early) drops its handle while
    // the character is in the world; the drop logs it off first, else the
    // server keeps the character in the world after the process ends.
    let (login, connect, address) = bind_pair();
    let server = thread::spawn(move || {
        let mut server = into_world(login, &connect);
        server.wait_for(0xF653);
        server.send(&words(&[0xF653]));
    });

    let handle = spawn(booted_config(address)).unwrap();
    loop {
        match handle.next_event(Duration::from_secs(10)) {
            Some(HostEvent::Session(SessionEvent::EnteredWorld(_))) => break,
            Some(HostEvent::Ended(summary)) => panic!("ended before the world: {summary:?}"),
            Some(_) => {}
            None => panic!("never entered the world"),
        }
    }
    let commands = handle.commands();
    drop(handle);
    // The log off reached the server, and the session thread is gone (its
    // command channel is closed).
    server.join().expect("the server saw the log off");
    assert!(commands.send(SessionCommand::Shutdown).is_err());
}

#[test]
fn a_session_the_server_boots_in_the_world_ends_at_once_with_the_reason() {
    let (login, connect, address) = bind_pair();
    let server = thread::spawn(move || {
        let mut server = into_world(login, &connect);
        thread::sleep(Duration::from_millis(200));
        server.send(&words(&[0xF659, 0x01]));
        Instant::now()
    });

    let handle = spawn(booted_config(address)).unwrap();

    let mut entered = false;
    let mut failed = None;
    let summary = loop {
        match handle.next_event(Duration::from_secs(60)) {
            Some(HostEvent::Session(SessionEvent::EnteredWorld(character))) => {
                assert_eq!(character.name, "+Booted");
                entered = true;
            }
            Some(HostEvent::Session(SessionEvent::Failed(error))) => failed = Some(error),
            Some(HostEvent::Ended(summary)) => break summary,
            Some(_) => {}
            None => panic!("the session never ended"),
        }
    };
    let ended = Instant::now();
    let booted = server.join().unwrap();
    handle.join();

    assert!(entered, "the character was in the world");
    assert_eq!(
        failed,
        Some(SessionError::ServerEnded {
            code: 1,
            reason: "Another account is already logged on from this client.".into(),
        })
    );
    assert_eq!(
        summary.error.as_deref(),
        Some(
            "the server ended the session: Another account is already logged on from this client. (error 0x1)"
        )
    );
    // At once: well inside the shortest timeout that would otherwise end it.
    let took = ended.saturating_duration_since(booted);
    assert!(took < Duration::from_secs(3), "took {took:?}");
}
