//! Test doubles: a model of the server's session, a scripted link, and a
//! harness that runs a real [`Connection`] against them on virtual time.

pub mod crypto;
pub mod link;
pub mod server;

use std::collections::VecDeque;
use std::time::Duration;

use crate::lossy::LossyFilter;
use crate::packet::{PacketFlags, PacketHeader};
use crate::{
    Connection, Destination, GameMessageGroup, Handshake, InboundMessage, NetTime, Transmit,
};
use link::{Dir, ScriptedLink};
use server::ServerModel;

pub const CLIENT_SEED: u32 = 0x2B6D_6F87;
pub const SERVER_SEED: u32 = 0x9A3C_51E4;
pub const CLIENT_ID: u32 = 0x1234;
pub const COOKIE: u64 = 0xFEED_FACE_CAFE_BABE;

/// A game-message body: a 4-byte opcode then the text.
pub fn message(opcode: u32, text: &str) -> Vec<u8> {
    let mut body = opcode.to_le_bytes().to_vec();
    body.extend_from_slice(text.as_bytes());
    body
}

/// The text after the opcode.
pub fn text_of(body: &[u8]) -> String {
    String::from_utf8_lossy(&body[4..]).into_owned()
}

pub fn is_connect_response(datagram: &[u8]) -> bool {
    PacketHeader::from_bytes(datagram).is_some_and(|h| h.flags.has(PacketFlags::CONNECT_RESPONSE))
}

/// A client [`Connection`] wired to a [`ServerModel`] through a scripted
/// link (and optionally the lossy filter, client side), on virtual time.
/// Sends reach the server immediately; the server's replies wait in a
/// queue until [`Harness::deliver`].
#[derive(Debug)]
pub struct Harness {
    pub now: NetTime,
    pub server: ServerModel,
    pub link: ScriptedLink,
    pub lossy: Option<LossyFilter>,
    pub conn: Option<Connection>,
    pub received: Vec<InboundMessage>,
    pub connect_port_sends: usize,
    to_client: VecDeque<Vec<u8>>,
}

impl Harness {
    pub fn new() -> Self {
        let now = NetTime::ZERO;
        let mut server = ServerModel::new(now, CLIENT_SEED, SERVER_SEED, CLIENT_ID, COOKIE);
        server.welcome = vec![
            (message(0xF658, "character list"), GameMessageGroup::UiQueue),
            (message(0xF7E5, ""), GameMessageGroup::DatabaseQueue),
        ];
        Self {
            now,
            server,
            link: ScriptedLink::default(),
            lossy: None,
            conn: None,
            received: Vec::new(),
            connect_port_sends: 0,
            to_client: VecDeque::new(),
        }
    }

    /// A harness whose handshake has completed.
    pub fn connected() -> Self {
        let mut h = Self::new();
        h.connect();
        h
    }

    pub fn conn(&mut self) -> &mut Connection {
        self.conn.as_mut().expect("not connected")
    }

    /// Login request out, connect request back: the connection exists but
    /// has not sent its connect response yet.
    pub fn handshake(&mut self) {
        let mut handshake =
            Handshake::start(self.now, "testaccount", "testpassword", 1234).expect("login request");
        while let Some(t) = handshake.poll_transmit() {
            self.send_to_server(t);
        }
        while let Some(datagram) = self.to_client.pop_front() {
            if let Some(conn) = handshake.handle_datagram(self.now, &datagram) {
                self.conn = Some(conn);
                break;
            }
        }
        assert!(self.conn.is_some(), "no connect request arrived");
    }

    /// Handshake, then step 50 ms at a time until the server has answered.
    pub fn connect(&mut self) {
        self.handshake();
        for _ in 0..400 {
            if self.conn().is_handshake_confirmed() {
                return;
            }
            self.step(Duration::from_millis(50));
        }
        panic!("handshake never confirmed");
    }

    fn send_to_server(&mut self, t: Transmit) {
        let connect_response = is_connect_response(&t.datagram);
        assert_eq!(
            t.destination == Destination::Connect,
            connect_response,
            "only the connect response goes to the connect port"
        );
        if connect_response {
            self.connect_port_sends += 1;
        }
        let pass = self
            .lossy
            .as_mut()
            .is_none_or(|l| l.pass_outbound(&t.datagram));
        if pass && self.link.transmit(Dir::ClientToServer, &t.datagram) {
            self.server.receive(self.now, &t.datagram);
        }
        self.pump_server();
    }

    /// Run the server's update and put what it sends on the link.
    pub fn pump_server(&mut self) {
        self.server.update(self.now);
        for datagram in self.server.take_pending() {
            if self.link.transmit(Dir::ServerToClient, &datagram) {
                self.to_client.push_back(datagram);
            }
        }
    }

    /// Hand every queued server datagram to the client.
    pub fn deliver(&mut self) {
        while let Some(datagram) = self.to_client.pop_front() {
            if !self.lossy.as_mut().is_none_or(|l| l.pass_inbound()) {
                continue;
            }
            let now = self.now;
            let messages = self.conn().handle_datagram(now, &datagram);
            self.received.extend(messages);
            self.flush_client();
        }
    }

    pub fn flush_client(&mut self) {
        while let Some(t) = self.conn().poll_transmit() {
            self.send_to_server(t);
        }
    }

    pub fn tick(&mut self) {
        let now = self.now;
        self.conn().tick(now);
        self.flush_client();
    }

    /// Advance time, let the server run, deliver, tick.
    pub fn step(&mut self, dt: Duration) {
        self.now += dt;
        self.pump_server();
        self.deliver();
        self.tick();
    }

    pub fn send(&mut self, body: &[u8]) {
        self.conn()
            .send_message(body, GameMessageGroup::UiQueue)
            .expect("send");
        self.flush_client();
    }

    pub fn server_send(&mut self, body: Vec<u8>) {
        self.server
            .enqueue_game_message(body, GameMessageGroup::UiQueue);
        self.pump_server();
    }

    /// Put a datagram straight into the client's queue.
    pub fn inject_to_client(&mut self, datagram: Vec<u8>) {
        self.to_client.push_back(datagram);
    }

    /// Texts of the messages the client received.
    pub fn received_texts(&self) -> Vec<String> {
        self.received.iter().map(|m| text_of(&m.body)).collect()
    }

    pub fn dispatched_texts(&self) -> Vec<String> {
        self.server.dispatched.iter().map(|m| text_of(m)).collect()
    }
}
