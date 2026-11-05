//! Process level: the console stop (Ctrl+Break on Windows, which a Ctrl+C
//! keypress also raises in a console; SIGINT on Unix) on `openac run
//! --config` logs every session off before the process exits, with no
//! console and with the reader of its standard output gone, as when a
//! Ctrl+C in a shell ends both the client and the program its output is
//! piped into. Each session runs against a loopback stand-in for the
//! server that reports whether the log off and the transport disconnect
//! arrived. On Unix SIGTERM and SIGHUP are the same stop. OpenAC:
//! `AcDream.Headless/Program.cs` (the console cancel and SIGTERM end every
//! session gracefully).

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::io::{BufRead, BufReader, ErrorKind, Read};
use std::net::{SocketAddr, UdpSocket};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

use ac_bits::Writer;
use ac_net::crypto::Isaac;
use ac_net::packet::{PacketFlags, PacketHeader, encode, parse_packet, write_single_fragment};

const SERVER_SEED: u32 = 0xAABB_CCDD;
const CLIENT_SEED: u32 = 0x0102_0304;
const PLAYER: u32 = 0x5000_000A;
/// The client's log off request and the server's confirmation.
const LOG_OFF: u32 = 0xF653;

// The stand-ins' ports are bound and read as the session tests' stand-ins
// in `ac-runtime` do (its `session/runner_test_server.rs`, which is private
// to that crate and so not reachable from this test):
//
// - never with a socket receive timeout: on Windows a UDP read whose
//   timeout fires as a datagram arrives can lose that datagram,
//   and the client sends its login request once. The ports are
//   non-blocking and polled.
// - on every address, below the system's client range: on Windows a port
//   bound only on the loopback address can coexist with another client
//   bound on every address on the same port, and then takes its datagrams.
// - a reused port still gets an earlier client's late packets, so only a
//   login request is taken as the login, and after it only that client's
//   datagrams.

/// The pause between polls of an empty port.
const POLL: Duration = Duration::from_millis(1);

/// Two adjacent non-blocking ports and the login address a client is given:
/// logins arrive on the first, the connect response on the second.
fn bind_pair() -> (UdpSocket, UdpSocket, SocketAddr) {
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
            return (login, connect, SocketAddr::from(([127, 0, 0, 1], port)));
        }
    }
    panic!("no two adjacent ports");
}

/// The next login request on the login port, waiting until `deadline`:
/// who sent it. Anything else there is not one.
fn recv_login(login: &UdpSocket, deadline: Instant) -> Option<SocketAddr> {
    let mut buf = [0u8; 2048];
    while let Some((len, from)) = recv_until(login, &mut buf, deadline) {
        let packet = parse_packet(&buf[..len]);
        if packet.is_ok_and(|p| p.header.flags.has(PacketFlags::LOGIN_REQUEST)) {
            return Some(from);
        }
    }
    None
}

/// The next datagram from `client` on `socket` into `buf`, waiting until
/// `deadline`: its length. Datagrams from anyone else are passed over.
fn recv_from_client(
    socket: &UdpSocket,
    buf: &mut [u8],
    client: SocketAddr,
    deadline: Instant,
) -> Option<usize> {
    while let Some((len, from)) = recv_until(socket, buf, deadline) {
        if from == client {
            return Some(len);
        }
    }
    None
}

/// The next datagram on `socket` into `buf`, waiting until `deadline`;
/// `None` once it passed with nothing.
fn recv_until(
    socket: &UdpSocket,
    buf: &mut [u8],
    deadline: Instant,
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
                if Instant::now() >= deadline {
                    return None;
                }
                thread::sleep(POLL);
            }
            Err(e) => panic!("the stand-in server's socket failed: {e}"),
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

fn character_list(name: &str) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF658);
    w.u32(0);
    w.u32(1);
    w.u32(PLAYER);
    w.string16l(name).unwrap();
    w.u32(0);
    w.u32(0);
    w.i32(11);
    w.string16l("stopaccount").unwrap();
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
    client: SocketAddr,
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

    /// Reads its client's packets until one passes `wanted`; false at
    /// `limit`.
    fn wait(
        &self,
        limit: Duration,
        wanted: impl Fn(&ac_net::packet::ParsedPacket<'_>) -> bool,
    ) -> bool {
        let deadline = Instant::now() + limit;
        let mut buf = [0u8; 2048];
        while let Some(len) = recv_from_client(&self.socket, &mut buf, self.client, deadline) {
            if parse_packet(&buf[..len]).is_ok_and(|packet| wanted(&packet)) {
                return true;
            }
        }
        false
    }

    /// Reads client packets until one carries a message with `opcode`.
    fn wait_for(&self, opcode: u32, limit: Duration) -> bool {
        self.wait(limit, |packet| {
            packet
                .fragments()
                .any(|f| f.header.index == 0 && f.payload.get(..4) == Some(&opcode.to_le_bytes()))
        })
    }
}

/// What a stand-in server saw of its session's end.
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
struct Ending {
    logged_off: bool,
    disconnected: bool,
}

/// A server that takes one login into the world as `name`, then waits for
/// the log off (confirming it) and the transport disconnect.
fn stand_in(name: &'static str) -> (SocketAddr, thread::JoinHandle<Ending>) {
    let (login, connect, address) = bind_pair();
    let server = thread::spawn(move || {
        let mut buf = [0u8; 2048];
        let Some(client) = recv_login(&login, Instant::now() + Duration::from_secs(60)) else {
            return Ending::default();
        };
        login.send_to(&connect_request(), client).unwrap();
        let within = Instant::now() + Duration::from_secs(30);
        recv_from_client(&connect, &mut buf, client, within).expect("the connect response");
        let mut server = Server {
            socket: login,
            client,
            keystream: Isaac::new(SERVER_SEED),
            sequence: 2,
            fragment_sequence: 1,
        };
        server.send(&words(&[0xF7E5]));
        server.send(&character_list(name));
        server.send(&words(&[0xF7EA]));
        assert!(server.wait_for(0xF7C8, Duration::from_secs(30)));
        server.send(&words(&[0xF7DF]));
        assert!(server.wait_for(0xF657, Duration::from_secs(30)));
        let logged_off = server.wait_for(LOG_OFF, Duration::from_secs(60));
        if !logged_off {
            return Ending::default();
        }
        server.send(&words(&[LOG_OFF]));
        let disconnected = server.wait(Duration::from_secs(20), |packet| {
            packet.header.flags.has(PacketFlags::DISCONNECT)
        });
        Ending {
            logged_off,
            disconnected,
        }
    });
    (address, server)
}

struct TempRoot(PathBuf);

impl Drop for TempRoot {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn document(root: &Path, sessions: &[(&str, SocketAddr, &str)]) -> PathBuf {
    let entries: Vec<String> = sessions
        .iter()
        .map(|(id, address, name)| {
            format!(
                r#"{{ "id": "{id}", "endpoint": {{ "host": "127.0.0.1", "port": {port} }},
                   "account": "stop{id}", "character": {{ "name": "{name}" }},
                   "policy": {{ "id": "idle" }},
                   "credential": {{ "provider": "environment", "reference": "STOP_TEST_PASSWORD_{upper}" }} }}"#,
                port = address.port(),
                upper = id.to_uppercase(),
            )
        })
        .collect();
    let path = root.join("two.json");
    std::fs::write(
        &path,
        format!(r#"{{ "version": 1, "sessions": [{}] }}"#, entries.join(",")),
    )
    .unwrap();
    path
}

#[test]
fn a_console_stop_logs_every_session_off_even_after_the_output_reader_is_gone() {
    every_session_logs_off_after(|pid| {
        assert!(ac_launcher_core::launching::request_console_stop(pid));
    });
}

#[cfg(unix)]
fn send_signal(pid: u32, signal: nix::sys::signal::Signal) {
    let pid = nix::unistd::Pid::from_raw(i32::try_from(pid).unwrap());
    nix::sys::signal::kill(pid, signal).unwrap();
}

#[cfg(unix)]
#[test]
fn sigterm_logs_every_session_off() {
    every_session_logs_off_after(|pid| send_signal(pid, nix::sys::signal::Signal::SIGTERM));
}

#[cfg(unix)]
#[test]
fn sighup_logs_every_session_off() {
    every_session_logs_off_after(|pid| send_signal(pid, nix::sys::signal::Signal::SIGHUP));
}

/// Starts `openac run --config` with two sessions, waits until both are in
/// the world and the reader of its output is gone, calls `stop` with the
/// client's process id, and checks that both sessions logged off and
/// disconnected and the process exited 0.
fn every_session_logs_off_after(stop: impl FnOnce(u32)) {
    let root = TempRoot(std::env::temp_dir().join(format!(
        "openac-console-stop-{}-{}",
        std::process::id(),
        ac_launcher_core::ids::unique_hex32()
    )));
    std::fs::create_dir_all(&root.0).unwrap();
    let (address_a, server_a) = stand_in("+Alpha");
    let (address_b, server_b) = stand_in("+Beta");
    let config = document(
        &root.0,
        &[("a", address_a, "+Alpha"), ("b", address_b, "+Beta")],
    );

    let mut command = Command::new(env!("CARGO_BIN_EXE_openac"));
    command
        .arg("run")
        .arg("--config")
        .arg(&config)
        .arg("--config-dir")
        .arg(root.0.join("config"))
        .arg("--data-dir")
        .arg(root.0.join("data"))
        .arg("--cache-dir")
        .arg(root.0.join("cache"))
        .env("OPENAC_HEADLESS", "1")
        .env("OPENAC_HEADLESS_CONSOLE", "0")
        .env("OPENAC_NO_AUDIO", "1")
        .env("STOP_TEST_PASSWORD_A", "x")
        .env("STOP_TEST_PASSWORD_B", "x")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = ac_launcher_core::launching::spawn_stoppable(&mut command).unwrap();

    // The output's reader reads until both characters are in the world,
    // then goes away, closing the pipe.
    let stdout = child.stdout.take().unwrap();
    let (in_world, both_in_world) = mpsc::channel();
    let reader = thread::spawn(move || {
        let mut waiting = vec!["[a] EnteredWorld", "[b] EnteredWorld"];
        for line in BufReader::new(stdout).lines() {
            let Ok(line) = line else { break };
            waiting.retain(|prefix| !line.starts_with(prefix));
            if waiting.is_empty() {
                let _ = in_world.send(());
                break;
            }
        }
    });
    let mut stderr = child.stderr.take().unwrap();
    let errors = thread::spawn(move || {
        let mut text = String::new();
        let _ = stderr.read_to_string(&mut text);
        text
    });

    let entered = both_in_world.recv_timeout(Duration::from_secs(60)).is_ok();
    reader.join().unwrap();
    if !entered {
        let _ = child.kill();
        let _ = child.wait();
        panic!(
            "the sessions never entered the world:\n{}",
            errors.join().unwrap()
        );
    }
    // Let the client write to the closed pipe before the stop.
    thread::sleep(Duration::from_millis(500));
    stop(child.id());

    let deadline = Instant::now() + Duration::from_secs(30);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break Some(status);
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            let _ = child.wait();
            break None;
        }
        thread::sleep(Duration::from_millis(20));
    };
    let errors = errors.join().unwrap();
    let endings = [server_a.join().unwrap(), server_b.join().unwrap()];
    let status = status.unwrap_or_else(|| panic!("never exited after the stop:\n{errors}"));
    for ending in endings {
        assert_eq!(
            ending,
            Ending {
                logged_off: true,
                disconnected: true
            },
            "{errors}"
        );
    }
    assert_eq!(status.code(), Some(0), "{errors}");
}
