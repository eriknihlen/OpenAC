//! Logging in: the login request out, the server's connect request back.

use std::collections::VecDeque;
use std::time::Duration;

use crate::NetTime;
use crate::connection::{Connection, Destination, Transmit};
use crate::packet::{
    LoginBuildError, PacketFlags, PacketHeader, build_login_request, encode, parse_packet,
};

/// How long the client waits for the server's connect request before it
/// sends the login request again.
pub const LOGIN_RESEND_INTERVAL: Duration = Duration::from_secs(2);

/// How many login requests the client sends before it gives up. With
/// [`LOGIN_RESEND_INTERVAL`] that is 40 seconds from the first one.
pub const LOGIN_REQUEST_LIMIT: u32 = 20;

/// The first phase of a connection: a cleartext login request to the login
/// port, then waiting for the server's connect request.
///
/// The login request goes out when the attempt starts and again every
/// [`LOGIN_RESEND_INTERVAL`] without an answer, the same bytes each time,
/// so one lost datagram does not fail the login. When the interval after
/// the [`LOGIN_REQUEST_LIMIT`]th request passes without an answer, the
/// attempt has timed out ([`Handshake::is_timed_out`]). Feed it the time
/// with [`Handshake::tick`].
#[derive(Debug)]
pub struct Handshake {
    datagram: Vec<u8>,
    outbox: VecDeque<Transmit>,
    started_at: NetTime,
    last_sent_at: NetTime,
    requests_sent: u32,
    established: bool,
    timed_out: bool,
}

impl Handshake {
    /// Build the login request for `account`/`password`, stamped with
    /// `unix_time` (seconds since 1970; the core reads no clock), and queue
    /// the first copy.
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
        let mut handshake = Self {
            datagram,
            outbox: VecDeque::new(),
            started_at: now,
            last_sent_at: now,
            requests_sent: 0,
            established: false,
            timed_out: false,
        };
        handshake.send_request(now);
        Ok(handshake)
    }

    /// When the attempt started.
    #[must_use]
    pub fn started_at(&self) -> NetTime {
        self.started_at
    }

    /// How many login requests have been queued so far.
    #[must_use]
    pub fn requests_sent(&self) -> u32 {
        self.requests_sent
    }

    /// The server never answered: the last request's interval ran out.
    #[must_use]
    pub fn is_timed_out(&self) -> bool {
        self.timed_out
    }

    /// When [`Self::tick`] next has something to do: the next resend, or
    /// the time out after the last one.
    #[must_use]
    pub fn next_deadline(&self) -> NetTime {
        self.last_sent_at + LOGIN_RESEND_INTERVAL
    }

    /// Advance to `now`: once [`LOGIN_RESEND_INTERVAL`] has passed since the
    /// last request without an answer, queue another, or time out when
    /// [`LOGIN_REQUEST_LIMIT`] have gone out.
    pub fn tick(&mut self, now: NetTime) {
        if self.established || self.timed_out {
            return;
        }
        if now.since(self.last_sent_at) < LOGIN_RESEND_INTERVAL {
            return;
        }
        if self.requests_sent >= LOGIN_REQUEST_LIMIT {
            self.timed_out = true;
            self.outbox.clear();
            return;
        }
        self.send_request(now);
    }

    /// The next datagram to send.
    pub fn poll_transmit(&mut self) -> Option<Transmit> {
        self.outbox.pop_front()
    }

    /// Offer a received datagram. Anything other than a valid cleartext
    /// connect request is ignored, as is everything once the attempt has
    /// timed out. On the connect request, returns the [`Connection`] (once;
    /// later datagrams are ignored) and stops the resends.
    pub fn handle_datagram(&mut self, now: NetTime, datagram: &[u8]) -> Option<Connection> {
        if self.established || self.timed_out {
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
        self.outbox.clear();
        Some(Connection::new(now, &header, &request))
    }

    fn send_request(&mut self, now: NetTime) {
        self.requests_sent += 1;
        self.last_sent_at = now;
        self.outbox.push_back(Transmit {
            destination: Destination::Login,
            datagram: self.datagram.clone(),
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn drain(hs: &mut Handshake) -> Vec<Transmit> {
        std::iter::from_fn(|| hs.poll_transmit()).collect()
    }

    fn start() -> Handshake {
        Handshake::start(NetTime::ZERO, "acct", "pw", 1).unwrap()
    }

    // The game client sends the login request again every two seconds while
    // the server has not answered.
    #[test]
    fn the_login_request_is_sent_again_every_two_seconds() {
        let mut hs = start();
        let first = drain(&mut hs);
        assert_eq!(first.len(), 1);

        hs.tick(NetTime::from_millis(1_999));
        assert!(drain(&mut hs).is_empty(), "not before two seconds");

        hs.tick(NetTime::from_millis(2_000));
        let again = drain(&mut hs);
        assert_eq!(again.len(), 1);
        assert_eq!(again[0].destination, Destination::Login);
        assert_eq!(again[0].datagram, first[0].datagram, "the same request");
        assert_eq!(hs.requests_sent(), 2);

        // The interval runs from the last request, not from the start.
        hs.tick(NetTime::from_millis(3_999));
        assert!(drain(&mut hs).is_empty());
        hs.tick(NetTime::from_millis(4_000));
        assert_eq!(drain(&mut hs).len(), 1);
        assert_eq!(hs.next_deadline(), NetTime::from_millis(6_000));
    }

    // The game client sends twenty login requests and gives up two seconds
    // after the last one: forty seconds after the first.
    #[test]
    fn the_attempt_times_out_two_seconds_after_the_twentieth_request() {
        let mut hs = start();
        let mut sent = drain(&mut hs).len();
        let mut now = NetTime::ZERO;
        for _ in 0..19 {
            now += LOGIN_RESEND_INTERVAL;
            hs.tick(now);
            sent += drain(&mut hs).len();
        }
        assert_eq!(sent, 20);
        assert_eq!(hs.requests_sent(), LOGIN_REQUEST_LIMIT);
        assert!(!hs.is_timed_out());

        hs.tick(NetTime::from_millis(39_999));
        assert!(!hs.is_timed_out());
        hs.tick(NetTime::from_millis(40_000));
        assert!(hs.is_timed_out());
        assert!(drain(&mut hs).is_empty(), "no twenty-first request");

        hs.tick(NetTime::from_millis(60_000));
        assert!(drain(&mut hs).is_empty());
    }
}
