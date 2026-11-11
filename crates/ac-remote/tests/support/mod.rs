//! A tiny relay for the link's tests: a plain `ws://127.0.0.1:<port>`
//! listener with the server side of the WebSocket handshake and frames.
//! Every wait has a deadline; nothing sleeps as an assertion.

#![allow(dead_code)]

use std::io::{ErrorKind, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::time::{Duration, Instant};

use ac_remote::protocol::{Capability, Hello, SessionState};
use ac_remote::{Inbound, Link, LinkConfig, RelayUrl, Secret, Timing};

/// How long a test waits for anything before it fails.
pub const PATIENCE: Duration = Duration::from_secs(5);

/// The token the tests use.
pub const TOKEN: &str = "test-token-5f1e";

pub fn hello() -> Hello {
    Hello {
        shape: "abcd".into(),
        ui_interface: 1,
        client_version: "0.0.0".into(),
        client_commit: None,
        instance_id: "instance".into(),
        config_session_id: "main".into(),
        generation: 1,
        character_name: "First".into(),
        character_guid: 0x5000_0001,
        server: "127.0.0.1:9000".into(),
        state: SessionState::InWorld,
        capabilities: vec![Capability::View],
    }
}

/// Short timings, so a test runs in milliseconds.
pub fn fast_timing() -> Timing {
    Timing {
        silence: Duration::from_secs(3),
        hello_timeout: Duration::from_secs(3),
        connect_timeout: Duration::from_secs(3),
        backoff_first: Duration::from_millis(20),
        backoff_max: Duration::from_millis(80),
    }
}

pub struct TestRelay {
    listener: TcpListener,
    port: u16,
}

impl TestRelay {
    pub fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
        listener.set_nonblocking(true).expect("non-blocking");
        let port = listener.local_addr().expect("address").port();
        Self { listener, port }
    }

    pub fn url(&self) -> RelayUrl {
        RelayUrl::parse(&format!("ws://127.0.0.1:{}/client/ws", self.port)).expect("url")
    }

    pub fn config(&self) -> LinkConfig {
        let mut config = LinkConfig::new(self.url(), Secret::new(TOKEN), hello());
        config.timing = fast_timing();
        config
    }

    pub fn link(&self) -> Link {
        Link::start(self.config()).expect("link")
    }

    /// The next connection, or `None` within `wait`.
    pub fn accept_within(&self, wait: Duration) -> Option<RelayConn> {
        let deadline = Instant::now() + wait;
        loop {
            match self.listener.accept() {
                Ok((stream, _)) => {
                    stream.set_nonblocking(false).expect("blocking");
                    stream
                        .set_read_timeout(Some(Duration::from_millis(50)))
                        .expect("timeout");
                    return Some(RelayConn {
                        stream,
                        buffer: Vec::new(),
                    });
                }
                Err(e) if e.kind() == ErrorKind::WouldBlock => {
                    if Instant::now() >= deadline {
                        return None;
                    }
                    std::thread::sleep(Duration::from_millis(2));
                }
                Err(e) => panic!("accept: {e}"),
            }
        }
    }

    pub fn accept(&self) -> RelayConn {
        self.accept_within(PATIENCE).expect("the link connects")
    }
}

/// The upgrade request as read.
#[derive(Debug)]
pub struct Request {
    pub line: String,
    pub headers: Vec<(String, String)>,
}

impl Request {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(n, _)| n.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }
}

/// A frame the link sent, unmasked.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub fin: bool,
    pub opcode: u8,
    pub payload: Vec<u8>,
}

pub struct RelayConn {
    stream: TcpStream,
    buffer: Vec<u8>,
}

/// Reading reached the end of the connection.
#[derive(Debug)]
pub struct Ended;

impl RelayConn {
    /// Reads until `have` says the buffer holds enough, or the deadline.
    fn fill_until(&mut self, mut have: impl FnMut(&[u8]) -> bool) -> Result<(), Ended> {
        let deadline = Instant::now() + PATIENCE;
        while !have(&self.buffer) {
            let mut chunk = [0u8; 4096];
            match self.stream.read(&mut chunk) {
                Ok(0) => return Err(Ended),
                Ok(n) => self.buffer.extend_from_slice(&chunk[..n]),
                Err(e) if matches!(e.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => {
                    assert!(Instant::now() < deadline, "the link sent nothing in time");
                }
                Err(e)
                    if matches!(
                        e.kind(),
                        ErrorKind::ConnectionReset | ErrorKind::ConnectionAborted
                    ) =>
                {
                    return Err(Ended);
                }
                Err(e) => panic!("read: {e}"),
            }
        }
        Ok(())
    }

    pub fn read_request(&mut self) -> Request {
        self.fill_until(|b| b.windows(4).any(|w| w == b"\r\n\r\n"))
            .expect("a request");
        let end = self
            .buffer
            .windows(4)
            .position(|w| w == b"\r\n\r\n")
            .expect("end")
            + 4;
        let head: Vec<u8> = self.buffer.drain(..end).collect();
        let head = String::from_utf8(head).expect("utf-8");
        let mut lines = head.split("\r\n");
        let line = lines.next().unwrap_or_default().to_owned();
        let headers = lines
            .filter(|l| !l.is_empty())
            .map(|l| {
                let (n, v) = l.split_once(':').expect("header");
                (n.trim().to_owned(), v.trim().to_owned())
            })
            .collect();
        Request { line, headers }
    }

    pub fn answer_upgrade(&mut self, request: &Request) {
        let key = request.header("Sec-WebSocket-Key").expect("key");
        let answer = format!(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {}\r\n\r\n",
            ac_remote::ws::accept_key(key)
        );
        self.send_raw(answer.as_bytes());
    }

    /// Reads the request and answers the upgrade.
    pub fn upgrade(&mut self) -> Request {
        let request = self.read_request();
        self.answer_upgrade(&request);
        request
    }

    /// Upgrades, reads the hello, answers `hello_ok`; the hello's text.
    pub fn accept_session(&mut self, link: &Link) -> String {
        self.upgrade();
        let hello = self.read_text();
        self.send_text(r#"{"type":"hello_ok","protocol":1,"ping_interval_ms":5000}"#);
        expect_connected(link);
        hello
    }

    pub fn send_raw(&mut self, bytes: &[u8]) {
        // The link may already have gone; a test that cares reads after.
        let _ = self.stream.write_all(bytes);
    }

    pub fn send_frame(&mut self, fin: bool, opcode: u8, payload: &[u8]) {
        self.send_raw(&frame(fin, opcode, payload));
    }

    pub fn send_text(&mut self, text: &str) {
        self.send_frame(true, 1, text.as_bytes());
    }

    pub fn send_binary(&mut self, bytes: &[u8]) {
        self.send_frame(true, 2, bytes);
    }

    pub fn send_close(&mut self, code: u16) {
        self.send_frame(true, 8, &code.to_be_bytes());
    }

    /// The next frame the link sent; every one must be masked.
    pub fn read_frame(&mut self) -> Result<Frame, Ended> {
        self.fill_until(|b| frame_length(b).is_some_and(|n| b.len() >= n))?;
        let total = frame_length(&self.buffer).expect("length");
        let bytes: Vec<u8> = self.buffer.drain(..total).collect();
        let fin = bytes[0] & 0x80 != 0;
        let opcode = bytes[0] & 0x0F;
        assert!(bytes[1] & 0x80 != 0, "client frames are masked");
        let (length, at) = header_length(&bytes).expect("header");
        let mask = [bytes[at], bytes[at + 1], bytes[at + 2], bytes[at + 3]];
        let payload = bytes[at + 4..at + 4 + length]
            .iter()
            .enumerate()
            .map(|(i, b)| b ^ mask[i % 4])
            .collect();
        Ok(Frame {
            fin,
            opcode,
            payload,
        })
    }

    /// The next text frame's text (pongs are skipped).
    pub fn read_text(&mut self) -> String {
        loop {
            let frame = self.read_frame().expect("a frame");
            match frame.opcode {
                10 => {}
                1 => return String::from_utf8(frame.payload).expect("utf-8"),
                other => panic!("expected text, got opcode {other}"),
            }
        }
    }

    /// Reads until the connection ends; the frames on the way.
    pub fn read_to_end(&mut self) -> Vec<Frame> {
        let mut frames = Vec::new();
        while let Ok(frame) = self.read_frame() {
            frames.push(frame);
        }
        frames
    }
}

/// A server frame (unmasked).
pub fn frame(fin: bool, opcode: u8, payload: &[u8]) -> Vec<u8> {
    let mut out = vec![if fin { 0x80 } else { 0 } | opcode];
    match payload.len() {
        n if n < 126 => out.push(n as u8),
        n if n <= 0xFFFF => {
            out.push(126);
            out.extend((n as u16).to_be_bytes());
        }
        n => {
            out.push(127);
            out.extend((n as u64).to_be_bytes());
        }
    }
    out.extend_from_slice(payload);
    out
}

/// A masked client frame's payload length and where its mask starts.
fn header_length(bytes: &[u8]) -> Option<(usize, usize)> {
    let second = *bytes.get(1)?;
    match second & 0x7F {
        126 => Some((
            usize::from(u16::from_be_bytes([*bytes.get(2)?, *bytes.get(3)?])),
            4,
        )),
        127 => {
            let raw: [u8; 8] = bytes.get(2..10)?.try_into().ok()?;
            Some((usize::try_from(u64::from_be_bytes(raw)).ok()?, 10))
        }
        n => Some((usize::from(n), 2)),
    }
}

fn frame_length(bytes: &[u8]) -> Option<usize> {
    let (length, at) = header_length(bytes)?;
    Some(at + 4 + length)
}

/// The next event within the patience.
pub fn next_event(link: &Link) -> Inbound {
    link.events()
        .recv_timeout(PATIENCE)
        .expect("an event in time")
}

pub fn expect_connected(link: &Link) {
    match next_event(link) {
        Inbound::Connected { .. } => {}
        other => panic!("expected Connected, got {other:?}"),
    }
}

/// The value of a top-level JSON member, as written (strings with quotes).
pub fn member<'a>(json: &'a str, name: &str) -> Option<&'a str> {
    let key = format!("\"{name}\":");
    let start = json.find(&key)? + key.len();
    let rest = &json[start..];
    let end = if let Some(quoted) = rest.strip_prefix('"') {
        quoted.find('"')? + 2
    } else {
        rest.find([',', '}'])?
    };
    Some(&rest[..end])
}
