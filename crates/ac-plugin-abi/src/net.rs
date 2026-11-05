//! The network a plugin may use (API group "net"): HTTP requests and TCP
//! sockets, behind the `network` and `listen` permissions and the hosts the
//! manifest lists or the player approved. Nothing blocks: a call returns a
//! handle, and what happens arrives as [`crate::Event::Net`].

use crate::{wire_enum, wire_record, wire_union};

wire_record! {
    /// An HTTP request.
    pub struct HttpRequest {
        /// `GET`, `POST`, ...
        pub method: String,
        /// `https://host[:port]/path?query` (plain `http` too).
        pub url: String,
        /// Extra headers (`Host`, `Content-Length` and `Connection` are the
        /// client's).
        pub headers: Vec<(String, String)>,
        /// The body.
        pub body: Vec<u8>,
        /// Give up after this long, milliseconds (0: 30 s).
        pub timeout_ms: u32,
        /// The largest body accepted, bytes (0: 16 MiB).
        pub max_bytes: u32,
        /// Follow redirects (each hop's host must be allowed too).
        pub follow_redirects: bool,
    }
}

wire_enum! {
    /// Why a network operation failed.
    pub enum NetError: "net error" {
        /// It did not fail.
        None = 0,
        /// Not allowed: the permission, or a host not approved.
        Refused = 1,
        /// The address or request is not one the client can send.
        Invalid = 2,
        /// Out of time.
        Timeout = 3,
        /// The connection could not be made, or broke.
        Connect = 4,
        /// The secure connection failed.
        Tls = 5,
        /// The reply was larger than allowed.
        TooLarge = 6,
        /// The reply was not HTTP the client can read.
        Protocol = 7,
    }
}

wire_record! {
    /// An HTTP reply (or why there is none).
    pub struct HttpReply {
        /// The request's handle.
        pub handle: u32,
        /// The status code, 0 with an error.
        pub status: u32,
        /// The reason phrase.
        pub reason: String,
        /// The headers, in order.
        pub headers: Vec<(String, String)>,
        /// The body.
        pub body: Vec<u8>,
        /// Why it failed, [`NetError::None`] when it did not.
        pub error: NetError,
        /// The failure in words.
        pub message: String,
    }
}

wire_record! {
    /// Open a TCP connection.
    pub struct TcpConnect {
        /// The host name or address.
        pub host: String,
        /// The port.
        pub port: u32,
        /// Wrap it in TLS (the host name is checked against the
        /// certificate).
        pub tls: bool,
        /// Give up connecting after this long, milliseconds (0: 30 s).
        pub timeout_ms: u32,
    }
}

wire_union! {
    /// Something happened on the network ([`crate::Event::Net`]).
    pub enum NetEvent: "net event" {
        /// An HTTP request finished.
        0 => HttpCompleted(HttpReply),
        /// A socket connected (its handle).
        1 => Connected(u32),
        /// Bytes are waiting on a socket (read them with a receive).
        2 => Readable(u32),
        /// A socket closed: (handle, why; empty when the other end closed).
        3 => Closed((u32, String)),
        /// A listener accepted a connection: (listener, new socket).
        4 => Accepted((u32, u32)),
        /// A connect failed: (handle, why, words).
        5 => Failed((u32, NetError, String)),
    }
}
