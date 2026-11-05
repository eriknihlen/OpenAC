//! The UDP reachability probe against a loopback socket the test answers
//! from. The first test is OpenAC `ServerHealthServiceTests`
//! `UdpProbeUsesPasswordlessStatusIdentityAndWaitsForValidReply`.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::io::ErrorKind;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::status::ReachabilityProbe;
use ac_launcher_net::UdpReachabilityProbe;

// The test's server port is bound and read as the session tests' stand-ins
// in `ac-runtime` do (its `session/runner_test_server.rs`, private to that
// crate): on every address below the system's client range (on Windows a
// port bound only on the loopback address can coexist with a client bound
// on every address on it, and takes its datagrams; and a port the system
// hands out may still get a closed client's late packets); non-blocking
// and polled, never with a socket receive timeout (on Windows a read whose
// timeout fires as a datagram arrives can lose it); and only a
// status request is taken as the probe's.

/// A non-blocking port and its number.
fn server() -> (UdpSocket, u16) {
    // Ports from 20,000 to 48,999, below the system's client range (49,152
    // up), starting at a random one; a port in use is skipped.
    const PORTS: u64 = 29_000;
    let start = RandomState::new().build_hasher().finish() % PORTS;
    for step in 0..PORTS {
        let port = 20_000 + ((start + step) % PORTS) as u16;
        if let Ok(socket) = UdpSocket::bind(("0.0.0.0", port)) {
            socket.set_nonblocking(true).unwrap();
            return (socket, port);
        }
    }
    panic!("no free port");
}

/// The next status request (flags `0x00010000`, the login request's) on
/// `server` within 5 s, into `buf`: its length and sender. Anything else
/// there is not one.
fn recv_request(server: &UdpSocket, buf: &mut [u8]) -> (usize, SocketAddr) {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match server.recv_from(buf) {
            Ok((length, from)) if length >= 8 && buf[4..8] == 0x0001_0000u32.to_le_bytes() => {
                return (length, from);
            }
            Ok(_) => {}
            // On Windows an ICMP "port unreachable" for an earlier send
            // surfaces on a later receive; it says nothing about this one.
            Err(e)
                if matches!(
                    e.kind(),
                    ErrorKind::ConnectionReset | ErrorKind::Interrupted
                ) => {}
            Err(e) if e.kind() == ErrorKind::WouldBlock => {
                assert!(Instant::now() < deadline, "no status request arrived");
                std::thread::sleep(Duration::from_millis(1));
            }
            Err(e) => panic!("the test server's socket failed: {e}"),
        }
    }
}

/// A reply header: `flags` at offset 4, the body size at offset 16.
fn reply(flags: u32, body_size: u16) -> Vec<u8> {
    let mut packet = vec![0u8; 20 + usize::from(body_size)];
    packet[4..8].copy_from_slice(&flags.to_le_bytes());
    packet[16..18].copy_from_slice(&body_size.to_le_bytes());
    packet
}

fn probe_in_background(
    port: u16,
    timeout: Duration,
    cancel: CancelToken,
) -> std::thread::JoinHandle<Option<f64>> {
    std::thread::spawn(move || UdpReachabilityProbe.probe("127.0.0.1", port, timeout, &cancel))
}

#[test]
fn udp_probe_uses_passwordless_status_identity_and_waits_for_valid_reply() {
    let (server, port) = server();
    let pending = probe_in_background(port, Duration::from_secs(5), CancelToken::new());
    let mut request = [0u8; 2048];
    let (length, from) = recv_request(&server, &mut request);
    let request = &request[..length];
    assert_eq!(
        u32::from_le_bytes([request[32], request[33], request[34], request[35]]),
        1
    );
    assert_eq!(&request[46..74], b"acservertracker:jj9h26hcsggc");
    server.send_to(&[1, 2, 3], from).unwrap();
    server.send_to(&reply(0x0004_0000, 32), from).unwrap();
    let latency = pending.join().unwrap();
    assert!(latency.is_some_and(|ms| ms >= 0.0), "{latency:?}");
}

#[test]
fn an_explicit_rejection_also_means_reachable() {
    let (server, port) = server();
    let pending = probe_in_background(port, Duration::from_secs(5), CancelToken::new());
    let mut request = [0u8; 2048];
    let (_, from) = recv_request(&server, &mut request);
    server.send_to(&reply(0x0010_0000, 4), from).unwrap();
    assert!(pending.join().unwrap().is_some());
}

#[test]
fn a_silent_server_is_unreachable_at_the_timeout() {
    let (_server, port) = server();
    let started = Instant::now();
    let latency = UdpReachabilityProbe.probe(
        "127.0.0.1",
        port,
        Duration::from_millis(300),
        &CancelToken::new(),
    );
    assert_eq!(latency, None);
    let elapsed = started.elapsed();
    assert!(
        elapsed >= Duration::from_millis(250) && elapsed < Duration::from_secs(2),
        "{elapsed:?}"
    );
}

#[test]
fn cancelling_ends_the_probe_early() {
    let (_server, port) = server();
    let cancel = CancelToken::new();
    let pending = probe_in_background(port, Duration::from_secs(10), cancel.clone());
    std::thread::sleep(Duration::from_millis(100));
    let started = Instant::now();
    cancel.cancel();
    assert_eq!(pending.join().unwrap(), None);
    assert!(started.elapsed() < Duration::from_secs(2));
}

#[test]
fn a_name_that_does_not_resolve_is_unreachable() {
    let latency = UdpReachabilityProbe.probe(
        "no-such-host.invalid",
        9000,
        Duration::from_secs(3),
        &CancelToken::new(),
    );
    assert_eq!(latency, None);
}
