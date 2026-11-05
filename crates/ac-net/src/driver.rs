//! A thin blocking driver: runs a [`Handshake`] and a [`Connection`] over a
//! `std::net::UdpSocket`, optionally through the [`LossyFilter`].
//!
//! It owns the socket and the clock; the protocol logic stays in the core.
//!
//! The socket is read by a small receive thread that blocks in `recv_from`
//! with no timeout and hands each datagram over a channel; the session's
//! thread waits on that channel with its timeouts. The receive thread only
//! moves bytes: every datagram is still handled, and every send made, on the
//! session's thread. A socket read with a timeout is not used: on Windows a
//! UDP read whose timeout fires just as a datagram arrives can lose that
//! datagram (about 1 in 1,500 at the pump's 1 ms waits on loopback), and
//! each loss costs a retransmit request and a stall of 600 ms or more. A
//! wait on the channel never loses what it was handed. A non-blocking
//! socket polled between short sleeps would not lose datagrams either, but
//! would wake about a thousand times a second while idle and add up to a
//! sleep's length to every arrival.

use std::io::{self, ErrorKind};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, UdpSocket};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, SyncSender};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::lossy::{InvalidDropPercent, LossCounters, LossOptions, LossyFilter};
use crate::packet::LoginBuildError;
use crate::{Connection, Destination, Handshake, InboundMessage, NetTime, Transmit};

/// Largest datagram the driver reads.
const MAX_DATAGRAM: usize = 65_535;

/// How long `pump` waits for the next datagram of a burst before it ends
/// the pass.
const DRAIN_WAIT: Duration = Duration::from_millis(1);

/// Datagrams the receive thread may hold for the session before it stops
/// reading (the socket's own buffer then fills, as it would with no
/// thread).
const QUEUE: usize = 4_096;

/// Driver settings.
#[derive(Debug, Clone)]
pub struct DriverOptions {
    /// The server's login address (its connect port is the next port).
    pub server: SocketAddr,
    /// Local address to bind; `None` binds the unspecified address of the
    /// server's family on an ephemeral port.
    pub bind: Option<SocketAddr>,
    /// Seeded packet loss (diagnostic; 0 % = off).
    pub loss: LossOptions,
}

impl DriverOptions {
    /// Defaults for `server`: ephemeral local port, no loss.
    #[must_use]
    pub fn new(server: SocketAddr) -> Self {
        Self {
            server,
            bind: None,
            loss: LossOptions::default(),
        }
    }
}

/// Why the driver failed.
#[derive(Debug, thiserror::Error)]
pub enum DriverError {
    /// A socket call failed.
    #[error("socket error: {0}")]
    Io(#[from] io::Error),
    /// The server did not answer in time.
    #[error("timed out waiting for {0}")]
    Timeout(&'static str),
    /// The login request could not be built.
    #[error(transparent)]
    Login(#[from] LoginBuildError),
    /// The loss options are invalid.
    #[error(transparent)]
    Loss(#[from] InvalidDropPercent),
    /// The login port is 65535, so there is no connect port after it.
    #[error("login port {0} leaves no room for the connect port")]
    NoConnectPort(u16),
}

/// Counters the driver keeps about the socket.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct DriverCounters {
    /// Datagrams sent.
    pub sent: u64,
    /// Datagrams received (before any loss filter).
    pub received: u64,
    /// Receives that reported a reset: on Windows an ICMP "port
    /// unreachable" for an earlier send surfaces on a later receive. It
    /// says nothing about this receive, so the driver counts it and reads on.
    pub unreachable_reports: u64,
}

/// What the receive thread hands to the session's thread.
#[derive(Debug)]
enum Received {
    /// One datagram.
    Datagram(Vec<u8>),
    /// A receive that reported a reset (see
    /// [`DriverCounters::unreachable_reports`]); the thread reads on.
    Unreachable,
    /// The socket failed; the thread has stopped.
    Failed(io::Error),
}

/// Reads `socket` until it fails or the session's end of the channel is
/// gone (the driver then wakes it with an empty datagram to itself).
fn receive_loop(socket: &UdpSocket, incoming: &SyncSender<Received>) {
    let mut buffer = vec![0u8; MAX_DATAGRAM];
    loop {
        let received = match socket.recv_from(&mut buffer) {
            Ok((len, _from)) => Received::Datagram(buffer[..len].to_vec()),
            Err(e) if e.kind() == ErrorKind::ConnectionReset => Received::Unreachable,
            Err(e) if e.kind() == ErrorKind::Interrupted => continue,
            Err(e) => {
                let _ = incoming.send(Received::Failed(e));
                return;
            }
        };
        if incoming.send(received).is_err() {
            return;
        }
    }
}

/// Where a datagram from the socket to itself goes: its own address, with
/// an unspecified address read as the loopback address of its family.
fn own_address(local: SocketAddr) -> SocketAddr {
    let ip = match local.ip() {
        IpAddr::V4(ip) if ip.is_unspecified() => IpAddr::V4(Ipv4Addr::LOCALHOST),
        IpAddr::V6(ip) if ip.is_unspecified() => IpAddr::V6(Ipv6Addr::LOCALHOST),
        ip => ip,
    };
    SocketAddr::new(ip, local.port())
}

/// A UDP socket bound for one connection, with the thread that reads it.
#[derive(Debug)]
pub struct UdpDriver {
    socket: UdpSocket,
    login: SocketAddr,
    connect: SocketAddr,
    origin: Instant,
    lossy: Option<LossyFilter>,
    counters: DriverCounters,
    incoming: Receiver<Received>,
    receiver: Option<JoinHandle<()>>,
}

impl UdpDriver {
    /// Bind a socket for talking to `options.server`.
    pub fn bind(options: &DriverOptions) -> Result<Self, DriverError> {
        let login = options.server;
        let connect_port = login
            .port()
            .checked_add(1)
            .ok_or(DriverError::NoConnectPort(login.port()))?;
        let connect = SocketAddr::new(login.ip(), connect_port);
        let bind = options.bind.unwrap_or_else(|| {
            let ip = match login.ip() {
                IpAddr::V4(_) => IpAddr::V4(Ipv4Addr::UNSPECIFIED),
                IpAddr::V6(_) => IpAddr::V6(Ipv6Addr::UNSPECIFIED),
            };
            SocketAddr::new(ip, 0)
        });
        let lossy = LossyFilter::new(options.loss)?;
        let socket = UdpSocket::bind(bind)?;
        let reader = socket.try_clone()?;
        let (sender, incoming) = mpsc::sync_channel(QUEUE);
        let receiver = thread::Builder::new()
            .name("ac-net-receive".to_owned())
            .spawn(move || receive_loop(&reader, &sender))?;
        Ok(Self {
            socket,
            login,
            connect,
            origin: Instant::now(),
            lossy,
            counters: DriverCounters::default(),
            incoming,
            receiver: Some(receiver),
        })
    }

    /// The local address.
    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.socket.local_addr()
    }

    /// Time since the driver was created, for the core's `now`.
    #[must_use]
    pub fn now(&self) -> NetTime {
        NetTime::from_duration(self.origin.elapsed())
    }

    /// The instant [`now`](Self::now) counts from, so another thread can
    /// read the same clock.
    #[must_use]
    pub fn origin(&self) -> Instant {
        self.origin
    }

    /// Socket counters.
    #[must_use]
    pub fn counters(&self) -> DriverCounters {
        self.counters
    }

    /// The loss filter's counters, when one is active.
    #[must_use]
    pub fn loss_counters(&self) -> Option<LossCounters> {
        self.lossy.as_ref().map(LossyFilter::counters)
    }

    /// Send one datagram to its destination port (unless the loss filter
    /// eats it).
    pub fn send(&mut self, transmit: &Transmit) -> io::Result<()> {
        if let Some(lossy) = &mut self.lossy
            && !lossy.pass_outbound(&transmit.datagram)
        {
            return Ok(());
        }
        let to = match transmit.destination {
            Destination::Login => self.login,
            Destination::Connect => self.connect,
        };
        self.socket.send_to(&transmit.datagram, to)?;
        self.counters.sent += 1;
        Ok(())
    }

    /// Wait up to `timeout` for one datagram (that survives the loss
    /// filter). `None` when the time ran out; a zero `timeout` only takes
    /// what has already arrived.
    pub fn recv(&mut self, timeout: Duration) -> io::Result<Option<Vec<u8>>> {
        let deadline = Instant::now() + timeout;
        loop {
            let wait = deadline.saturating_duration_since(Instant::now());
            let received = match self.incoming.recv_timeout(wait) {
                Ok(received) => received,
                Err(RecvTimeoutError::Timeout) => return Ok(None),
                Err(RecvTimeoutError::Disconnected) => {
                    return Err(io::Error::new(
                        ErrorKind::BrokenPipe,
                        "the socket's receive thread has stopped",
                    ));
                }
            };
            match received {
                Received::Datagram(datagram) => {
                    self.counters.received += 1;
                    let pass = self.lossy.as_mut().is_none_or(LossyFilter::pass_inbound);
                    if pass {
                        return Ok(Some(datagram));
                    }
                }
                Received::Unreachable => self.counters.unreachable_reports += 1,
                Received::Failed(e) => return Err(e),
            }
        }
    }

    /// Log in: send the login request and wait up to `timeout` for the
    /// server's connect request.
    pub fn connect(
        &mut self,
        account: &str,
        password: &str,
        timeout: Duration,
    ) -> Result<Connection, DriverError> {
        let mut handshake = self.begin_connect(account, password)?;
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(DriverError::Timeout("the connect request"));
            }
            if let Some(connection) = self.poll_connect(&mut handshake, remaining)? {
                return Ok(connection);
            }
        }
    }

    /// Start logging in: the login request goes out. Feed the handshake to
    /// [`Self::poll_connect`] until the server's connect request arrives (a
    /// caller that has other work, such as its host's commands, polls in
    /// short waits).
    pub fn begin_connect(
        &mut self,
        account: &str,
        password: &str,
    ) -> Result<Handshake, DriverError> {
        let unix_time = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| d.as_secs() as u32);
        let mut handshake = Handshake::start(self.now(), account, password, unix_time)?;
        while let Some(t) = handshake.poll_transmit() {
            self.send(&t)?;
        }
        Ok(handshake)
    }

    /// Wait up to `wait` for one datagram and hand it to `handshake`: the
    /// connection once the server's connect request arrived, else `None`.
    pub fn poll_connect(
        &mut self,
        handshake: &mut Handshake,
        wait: Duration,
    ) -> Result<Option<Connection>, DriverError> {
        let Some(datagram) = self.recv(wait)? else {
            return Ok(None);
        };
        Ok(handshake.handle_datagram(self.now(), &datagram))
    }

    /// Send everything the connection has queued.
    pub fn flush(&mut self, connection: &mut Connection) -> io::Result<()> {
        while let Some(t) = connection.poll_transmit() {
            self.send(&t)?;
        }
        Ok(())
    }

    /// Receive for up to `wait` (returning early once the socket is idle
    /// after at least one datagram), then tick and flush. Returns the game
    /// messages completed meanwhile.
    pub fn pump(
        &mut self,
        connection: &mut Connection,
        wait: Duration,
    ) -> Result<Vec<InboundMessage>, DriverError> {
        let deadline = Instant::now() + wait;
        let mut messages = Vec::new();
        let mut timeout = wait;
        while let Some(datagram) = self.recv(timeout)? {
            messages.extend(connection.handle_datagram(self.now(), &datagram));
            if Instant::now() >= deadline {
                break;
            }
            // Take the rest of a burst, waiting only briefly for each.
            timeout = DRAIN_WAIT;
        }
        connection.tick(self.now());
        self.flush(connection)?;
        Ok(messages)
    }
}

impl Drop for UdpDriver {
    /// Stops the receive thread and waits for it, so the socket is closed
    /// once the driver is gone (a driver bound to a fixed port can then be
    /// bound again at once).
    fn drop(&mut self) {
        // With the session's end of the channel gone, the thread stops at
        // the next datagram it reads (or at once, if it waits for room in a
        // full queue); the empty datagram to the socket itself is that
        // datagram.
        let (_, closed) = mpsc::sync_channel(0);
        drop(std::mem::replace(&mut self.incoming, closed));
        let Some(receiver) = self.receiver.take() else {
            return;
        };
        let woken = self
            .socket
            .local_addr()
            .and_then(|local| self.socket.send_to(&[], own_address(local)));
        // Unwoken, the thread ends at the next datagram that arrives; it is
        // not waited for then, so a drop never hangs.
        if woken.is_ok() {
            let _ = receiver.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::crypto::hash32;
    use crate::packet::{PacketFlags, PacketHeader, decode_with_keystream, parse_login_request};
    use std::net::UdpSocket;
    use std::thread;

    fn connect_request(client_id: u32, iteration: u16, server_time: f64) -> Vec<u8> {
        let mut w = ac_bits::Writer::new();
        w.f64(server_time);
        w.u64(0xFEED_FACE_CAFE_BABE);
        w.u32(client_id);
        w.u32(0xAABB_CCDD);
        w.u32(0x0102_0304);
        w.u32(0);
        let body = w.into_vec();
        let mut header = PacketHeader {
            flags: PacketFlags::CONNECT_REQUEST,
            data_size: body.len() as u16,
            iteration,
            ..PacketHeader::default()
        };
        header.checksum = header.hash().wrapping_add(hash32(&body));
        let mut datagram = header.to_bytes().to_vec();
        datagram.extend_from_slice(&body);
        datagram
    }

    /// A stand-in server's two adjacent ports (logins on the first, the
    /// connect response on the second) and the login address, bound and
    /// read as the session tests' stand-ins in `ac-runtime` are (its
    /// `session/runner_test_server.rs`, not reachable from this crate): on
    /// every address below the system's client range (on Windows a port
    /// bound only on the loopback address can coexist with a client bound
    /// on every address on it, and takes its datagrams), non-blocking and
    /// polled (on Windows a read whose timeout fires as a datagram arrives
    /// can lose it).
    fn stand_in_ports() -> (UdpSocket, UdpSocket, SocketAddr) {
        use std::collections::hash_map::RandomState;
        use std::hash::{BuildHasher, Hasher};
        // Even ports from 20,000 to 48,998 (the connect port one above),
        // starting at a random one; a port in use is skipped.
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

    /// The next datagram on `socket` that `wanted` takes, within 5 s, into
    /// `buf`: its length and sender. A reused port still gets an earlier
    /// client's late packets, which are passed over.
    fn recv_wanted(
        socket: &UdpSocket,
        buf: &mut [u8],
        wanted: impl Fn(&[u8], SocketAddr) -> bool,
    ) -> (usize, SocketAddr) {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            match socket.recv_from(buf) {
                Ok((len, from)) if wanted(&buf[..len], from) => return (len, from),
                Ok(_) => {}
                // On Windows an ICMP "port unreachable" for an earlier
                // send surfaces on a later receive.
                Err(e)
                    if matches!(
                        e.kind(),
                        ErrorKind::ConnectionReset | ErrorKind::Interrupted
                    ) => {}
                Err(e) if e.kind() == ErrorKind::WouldBlock => {
                    assert!(Instant::now() < deadline, "nothing wanted arrived");
                    thread::sleep(Duration::from_millis(1));
                }
                Err(e) => panic!("the stand-in server's socket failed: {e}"),
            }
        }
    }

    // Source: OpenAC tests `WorldSessionNegotiationShutdownTests.ThrowingServerTimeSubscriber_StillDisposesWithNegotiatedDisconnect`
    // (a loopback socket stands in for the server; nothing leaves the machine).
    #[test]
    fn loopback_login_then_disconnect_carries_the_negotiated_identity() {
        let (server, _connect, server_addr) = stand_in_ports();
        let flow = thread::spawn(move || {
            let mut buf = [0u8; 2048];
            // Only a login request is the login; after it, only that
            // client's datagrams are its.
            let (len, client) = recv_wanted(&server, &mut buf, |datagram, _| {
                decode_with_keystream(datagram, None)
                    .is_ok_and(|p| p.header.flags.has(PacketFlags::LOGIN_REQUEST))
            });
            let login = buf[..len].to_vec();
            server
                .send_to(&connect_request(0x1234, 7, 12345.5), client)
                .unwrap();
            let (len, _) = recv_wanted(&server, &mut buf, |_, from| from == client);
            (login, buf[..len].to_vec())
        });

        let mut driver = UdpDriver::bind(&DriverOptions::new(server_addr)).unwrap();
        let mut connection = driver
            .connect("typed-account", "password", Duration::from_secs(3))
            .unwrap();
        assert_eq!(connection.server_time(), 12345.5);
        connection.disconnect();
        driver.flush(&mut connection).unwrap();

        let (login, disconnect) = flow.join().unwrap();
        let login = decode_with_keystream(&login, None).unwrap();
        assert_eq!(login.header.flags, PacketFlags::LOGIN_REQUEST);
        assert_eq!(
            parse_login_request(login.body).unwrap().account,
            "typed-account"
        );
        let disconnect = decode_with_keystream(&disconnect, None).unwrap();
        assert!(disconnect.header.flags.has(PacketFlags::DISCONNECT));
        assert_eq!(disconnect.header.id, 0x1234);
        assert_eq!(disconnect.header.iteration, 7);
        assert!(driver.counters().sent >= 2);
    }

    #[test]
    fn connect_times_out_when_nothing_answers() {
        let silent = UdpSocket::bind("127.0.0.1:0").unwrap();
        let mut driver =
            UdpDriver::bind(&DriverOptions::new(silent.local_addr().unwrap())).unwrap();
        let err = driver
            .connect("a", "b", Duration::from_millis(50))
            .unwrap_err();
        assert!(matches!(err, DriverError::Timeout(_)), "{err}");
    }

    #[test]
    fn a_dropped_driver_stops_its_receive_thread_and_frees_its_port() {
        let mut options = DriverOptions::new("127.0.0.1:9000".parse().unwrap());
        options.bind = Some("127.0.0.1:0".parse().unwrap());
        let driver = UdpDriver::bind(&options).unwrap();
        let port = driver.local_addr().unwrap();
        drop(driver);
        // The receive thread held a handle to the socket; once it has
        // stopped, the port is free for a new driver.
        options.bind = Some(port);
        let again = UdpDriver::bind(&options).unwrap();
        assert_eq!(again.local_addr().unwrap(), port);
    }

    #[test]
    fn the_unspecified_address_wakes_through_loopback() {
        assert_eq!(
            own_address("0.0.0.0:4000".parse().unwrap()),
            "127.0.0.1:4000".parse::<SocketAddr>().unwrap()
        );
        assert_eq!(
            own_address("[::]:4000".parse().unwrap()),
            "[::1]:4000".parse::<SocketAddr>().unwrap()
        );
        assert_eq!(
            own_address("192.168.1.5:4000".parse().unwrap()),
            "192.168.1.5:4000".parse::<SocketAddr>().unwrap()
        );
    }

    #[test]
    fn a_zero_wait_takes_only_what_has_arrived() {
        let mut options = DriverOptions::new("127.0.0.1:9000".parse().unwrap());
        options.bind = Some("127.0.0.1:0".parse().unwrap());
        let mut driver = UdpDriver::bind(&options).unwrap();
        assert_eq!(driver.recv(Duration::ZERO).unwrap(), None);
        let peer = UdpSocket::bind("127.0.0.1:0").unwrap();
        peer.send_to(b"abc", driver.local_addr().unwrap()).unwrap();
        assert_eq!(
            driver.recv(Duration::from_secs(5)).unwrap().as_deref(),
            Some(&b"abc"[..])
        );
        assert_eq!(driver.counters().received, 1);
    }

    #[test]
    fn the_top_port_has_no_connect_port() {
        let options = DriverOptions::new("127.0.0.1:65535".parse().unwrap());
        assert!(matches!(
            UdpDriver::bind(&options),
            Err(DriverError::NoConnectPort(65535))
        ));
    }
}
