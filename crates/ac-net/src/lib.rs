//! The game's UDP transport.
//!
//! The core is sans-I/O: it takes datagrams and a caller-supplied time
//! ([`NetTime`]) and produces datagrams to send. It never touches a socket or
//! reads a clock. One thread owns a connection; nothing here is shared.
//!
//! The life of a connection:
//!
//! 1. [`Handshake::start`] builds the login request; send what
//!    [`Handshake::poll_transmit`] yields and offer every received datagram
//!    to [`Handshake::handle_datagram`] until it returns a [`Connection`].
//! 2. On the [`Connection`]: feed every datagram to
//!    [`Connection::handle_datagram`] (it returns completed game messages),
//!    call [`Connection::tick`] every few milliseconds, send what
//!    [`Connection::poll_transmit`] yields to its [`Destination`], and queue
//!    game messages with [`Connection::send_message`] once the handshake is
//!    confirmed.
//! 3. [`Connection::disconnect`] queues the transport disconnect.
//!
//! [`UdpDriver`] does all of that over a `std::net::UdpSocket`, optionally
//! through the seeded [`LossyFilter`].

mod assembler;
mod connection;
pub mod crypto;
mod driver;
mod group;
mod handshake;
mod lossy;
pub mod packet;
mod time;
mod transport;

#[cfg(test)]
mod scenario_tests;
#[cfg(test)]
mod test_support;

pub use assembler::{COMPLETED_RING_SIZE, FragmentAssembler, InboundMessage, PARTIAL_TTL};
pub use connection::{
    CONNECT_RESPONSE_DELAY, CONNECT_RESPONSE_RETRY, Connection, Destination, LinkSnapshot,
    SERVER_SILENCE_TIMEOUT, Transmit,
};
pub use driver::{DriverCounters, DriverError, DriverOptions, UdpDriver};
pub use group::GameMessageGroup;
pub use handshake::Handshake;
pub use lossy::{InvalidDropPercent, LossCounters, LossDirection, LossOptions, LossyFilter};
pub use time::NetTime;
pub use transport::{
    ACK_GATE, ASSEMBLER_SWEEP_PERIOD, INITIAL_WATERMARK, INTERVAL, MAX_NAK_IDS, NAK_GATE,
    SANITY_WINDOW, SendError, TransportStats,
};
