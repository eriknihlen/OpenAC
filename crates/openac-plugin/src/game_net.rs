//! Typed [`Host`] methods for the network: HTTP requests and TCP sockets.
//! Nothing blocks; what happens arrives as [`crate::Event::Net`] (only to
//! this plugin). Hosts must be in the manifest's `network` permission or
//! approved by the player (`/plugin allow <id> <host>`).

use ac_plugin_abi::calls;
use ac_plugin_abi::net::{HttpRequest, TcpConnect};

use crate::Host;

/// Net.
impl Host<'_> {
    /// Send an HTTP request: its handle; the reply arrives as
    /// [`crate::net::NetEvent::HttpCompleted`]. Needs the `network`
    /// permission.
    ///
    /// # Errors
    /// The host is not allowed, or the request cannot be sent.
    pub fn http_request(&mut self, request: HttpRequest) -> Result<u32, String> {
        self.call::<calls::NetHttpRequest>(&request)
    }

    /// `GET url`, following redirects.
    ///
    /// # Errors
    /// As [`Host::http_request`].
    pub fn http_get(&mut self, url: &str) -> Result<u32, String> {
        self.http_request(HttpRequest {
            method: "GET".into(),
            url: url.to_owned(),
            follow_redirects: true,
            ..HttpRequest::default()
        })
    }

    /// Open a TCP connection (TLS optional): its handle. Needs `network`.
    ///
    /// # Errors
    /// The host is not allowed, or the port is not one.
    pub fn tcp_connect(&mut self, host: &str, port: u16, tls: bool) -> Result<u32, String> {
        self.call::<calls::NetTcpConnect>(&TcpConnect {
            host: host.to_owned(),
            port: u32::from(port),
            tls,
            timeout_ms: 0,
        })
    }

    /// Open a TCP connection as a WebSocket's is opened (.NET
    /// `ClientWebSocket`): through the player's system proxy when one
    /// applies (`tls` for `wss`, without for `ws`), else direct. Its handle;
    /// needs `network`. [`Host::tcp_connect`] always goes direct, as a .NET
    /// socket does.
    ///
    /// # Errors
    /// The host is not allowed, or the port is not one.
    pub fn tcp_connect_proxied(&mut self, host: &str, port: u16, tls: bool) -> Result<u32, String> {
        self.call::<calls::NetTcpConnectProxied>(&TcpConnect {
            host: host.to_owned(),
            port: u32::from(port),
            tls,
            timeout_ms: 0,
        })
    }

    /// Listen on a port of this computer (loopback): the listener's
    /// handle. Needs `listen`.
    ///
    /// # Errors
    /// The port is taken.
    pub fn tcp_listen(&mut self, port: u16) -> Result<u32, String> {
        self.call::<calls::NetTcpListen>(&u32::from(port))
    }

    /// Queue bytes on a socket.
    ///
    /// # Errors
    /// No such socket, or it closed.
    pub fn net_send(&mut self, socket: u32, bytes: &[u8]) -> Result<u32, String> {
        self.call::<calls::NetSend>(&(socket, bytes.to_vec()))
    }

    /// Take up to `max` waiting bytes from a socket.
    pub fn net_receive(&mut self, socket: u32, max: u32) -> Vec<u8> {
        self.call::<calls::NetReceive>(&(socket, max))
    }

    /// Close a socket or listener, or forget a request.
    pub fn net_close(&mut self, handle: u32) -> bool {
        self.call::<calls::NetClose>(&handle)
    }
}
