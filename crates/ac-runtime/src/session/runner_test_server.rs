//! What the session tests' loopback stand-ins for a server share: their two
//! ports, and reading them so that no datagram is lost and none is taken
//! from the wrong client. Many sessions and their stand-ins run at once,
//! in one test process and across several, and under load.
//!
//! A stand-in never reads with a socket receive timeout. On Windows a UDP
//! read whose timeout fires just as a datagram arrives can lose that
//! datagram (`ac-net/tests/udp_loss.rs`), and the client sends its
//! login request once: a stand-in that lost it never answered, and the
//! session's first connection ended at its connect wait. The ports are
//! non-blocking and polled instead, which loses nothing.
//!
//! A stand-in's ports are bound on every address, below the range the
//! system hands out to clients. On Windows a port bound only on the
//! loopback address can coexist with another session's client bound on
//! every address on the same port, and then takes that client's datagrams.
//! (The `host_parity` stand-in binds the same way.)
//!
//! A port is reused as soon as it is free, so a client of an earlier
//! connection can still send to it (its disconnect, say). A stand-in takes
//! only a login request as a login, and after that only its own client's
//! datagrams: one that took a stray datagram for the login answered the
//! wrong address and left its own client's request unread.

use std::collections::hash_map::RandomState;
use std::hash::{BuildHasher, Hasher};
use std::io::ErrorKind;
use std::net::{SocketAddr, UdpSocket};
use std::thread;
use std::time::{Duration, Instant};

use ac_net::packet::{PacketFlags, parse_packet};

/// The pause between polls of an empty port.
const POLL: Duration = Duration::from_millis(1);

/// Two adjacent non-blocking ports and the login address a client is given:
/// logins arrive on the first, the connect response on the second.
pub(super) fn bind_pair() -> (UdpSocket, UdpSocket, SocketAddr) {
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

/// Whether a datagram is a login request.
pub(super) fn is_login_request(datagram: &[u8]) -> bool {
    parse_packet(datagram).is_ok_and(|p| p.header.flags.has(PacketFlags::LOGIN_REQUEST))
}

/// The next login request on the login port, waiting until `deadline`:
/// who sent it. Anything else there is not one.
pub(super) fn recv_login(login: &UdpSocket, deadline: Instant) -> Option<SocketAddr> {
    let mut buf = [0u8; 2048];
    while let Some((len, from)) = recv_until(login, &mut buf, deadline) {
        if is_login_request(&buf[..len]) {
            return Some(from);
        }
    }
    None
}

/// The next datagram from `client` on `socket` into `buf`, waiting until
/// `deadline`: its length. Datagrams from anyone else are passed over.
pub(super) fn recv_from_client(
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
pub(super) fn recv_until(
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
