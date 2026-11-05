//! Logging in: the login request out, the server's connect request back.

use std::collections::VecDeque;

use crate::NetTime;
use crate::connection::{Connection, Destination, Transmit};
use crate::packet::{
    LoginBuildError, PacketFlags, PacketHeader, build_login_request, encode, parse_packet,
};

/// The first phase of a connection: one cleartext login request to the
/// login port, then waiting for the server's connect request.
///
/// The login request is sent once; giving up after a deadline is the
/// caller's decision (see [`Handshake::started_at`]).
#[derive(Debug)]
pub struct Handshake {
    outbox: VecDeque<Transmit>,
    started_at: NetTime,
    established: bool,
}

impl Handshake {
    /// Build the login request for `account`/`password`, stamped with
    /// `unix_time` (seconds since 1970; the core reads no clock).
    pub fn start(
        now: NetTime,
        account: &str,
        password: &str,
        unix_time: u32,
    ) -> Result<Self, LoginBuildError> {
        let body = build_login_request(account, password, unix_time)?;
        let header = PacketHeader {
            flags: PacketFlags::LOGIN_REQUEST,
            ..PacketHeader::default()
        };
        // A cleartext login body is all optional section and always encodes.
        let datagram = encode(header, &body, None).expect("a cleartext login body always encodes");
        let mut outbox = VecDeque::new();
        outbox.push_back(Transmit {
            destination: Destination::Login,
            datagram,
        });
        Ok(Self {
            outbox,
            started_at: now,
            established: false,
        })
    }

    /// When the attempt started.
    #[must_use]
    pub fn started_at(&self) -> NetTime {
        self.started_at
    }

    /// The next datagram to send.
    pub fn poll_transmit(&mut self) -> Option<Transmit> {
        self.outbox.pop_front()
    }

    /// Offer a received datagram. Anything other than a valid cleartext
    /// connect request is ignored. On the connect request, returns the
    /// [`Connection`] (once; later datagrams are ignored).
    pub fn handle_datagram(&mut self, now: NetTime, datagram: &[u8]) -> Option<Connection> {
        if self.established {
            return None;
        }
        let packet = parse_packet(datagram).ok()?;
        let header = packet.header;
        if header.flags.has(PacketFlags::ENCRYPTED_CHECKSUM)
            || !packet.verify(None)
            || !header.flags.has(PacketFlags::CONNECT_REQUEST)
        {
            return None;
        }
        let request = packet.optional.connect_request?;
        self.established = true;
        Some(Connection::new(now, &header, &request))
    }
}
