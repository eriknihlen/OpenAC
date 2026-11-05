//! Server reachability: send the passwordless status datagram to a game
//! server and time the first valid reply.
//!
//! The socket is non-blocking and looked at every [`PAUSE`]: a UDP read
//! with a timeout can lose a datagram on Windows when the timeout fires
//! just as it arrives, and the probe's one reply must not be lost. The
//! pause also bounds how late the reply's time is taken.

use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant};

use ac_launcher_core::cancel::CancelToken;
use ac_launcher_core::status::{ReachabilityProbe, is_status_reply, status_request_datagram};

use crate::bounded;

/// Large enough for any datagram, so a long unrelated one is read whole
/// (and ignored) rather than failing the receive.
const RECEIVE_BUFFER: usize = 64 * 1024;

/// The pause between looks at the socket while no reply has come.
const PAUSE: Duration = Duration::from_millis(1);

/// The launcher core's [`ReachabilityProbe`] over a UDP socket. The host
/// name is resolved once per probe (an IPv4 address preferred); every
/// datagram that is not a status reply is ignored; an error from the socket
/// (an ICMP "port unreachable" on Windows, say) means unreachable.
#[derive(Debug, Clone, Copy, Default)]
pub struct UdpReachabilityProbe;

impl UdpReachabilityProbe {
    /// A probe.
    pub const fn new() -> Self {
        Self
    }
}

fn resolve(host: &str, port: u16, deadline: Instant, cancel: &CancelToken) -> Option<SocketAddr> {
    let target = host.to_owned();
    let job = move || -> io::Result<Vec<SocketAddr>> {
        Ok((target.as_str(), port).to_socket_addrs()?.collect())
    };
    let addresses = bounded::run("udp-probe-resolve", job, Some(deadline), cancel)
        .ok()?
        .ok()?;
    addresses
        .iter()
        .find(|address| address.is_ipv4())
        .or_else(|| addresses.first())
        .copied()
}

fn probe_core(
    host: &str,
    port: u16,
    timeout: Duration,
    cancel: &CancelToken,
) -> io::Result<Option<f64>> {
    let deadline = Instant::now() + timeout;
    let Some(address) = resolve(host, port, deadline, cancel) else {
        return Ok(None);
    };
    let local: IpAddr = if address.is_ipv4() {
        Ipv4Addr::UNSPECIFIED.into()
    } else {
        Ipv6Addr::UNSPECIFIED.into()
    };
    let socket = UdpSocket::bind((local, 0))?;
    socket.connect(address)?;
    socket.set_nonblocking(true)?;
    let started = Instant::now();
    socket.send(&status_request_datagram())?;
    let mut buffer = vec![0u8; RECEIVE_BUFFER];
    loop {
        let idle = match socket.recv(&mut buffer) {
            Ok(length) if is_status_reply(&buffer[..length]) => {
                return Ok(Some(started.elapsed().as_secs_f64() * 1000.0));
            }
            // Something else was read: look again at once.
            Ok(_) => false,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => false,
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => true,
            Err(error) => return Err(error),
        };
        if bounded::interrupted(cancel, Some(deadline)).is_some() {
            return Ok(None);
        }
        if idle {
            std::thread::sleep(PAUSE);
        }
    }
}

impl ReachabilityProbe for UdpReachabilityProbe {
    fn probe(&self, host: &str, port: u16, timeout: Duration, cancel: &CancelToken) -> Option<f64> {
        probe_core(host, port, timeout, cancel).ok().flatten()
    }
}
