//! The launcher's network, kept out of `ac-launcher-core`:
//!
//! - [`OsHttpTransport`]: the core's `HttpTransport`, an HTTP/1.1 `GET`
//!   over the operating system's TLS (SChannel on Windows, the Security
//!   framework on macOS, the system OpenSSL on Linux), plus plain HTTP to
//!   this machine only. It never follows a redirect, never decompresses,
//!   streams the body (lengths, chunked transfer, or until close), and
//!   honours the request's timeout and the caller's cancellation.
//! - [`UdpReachabilityProbe`]: the core's `ReachabilityProbe`, the
//!   passwordless status datagram sent to a game server and timed until a
//!   valid reply.
//!
//! The TLS stack is the operating system's, so certificates and security
//! updates come with the system.

mod bounded;
mod http1;
mod probe;
mod transport;

pub use probe::UdpReachabilityProbe;
pub use transport::OsHttpTransport;
