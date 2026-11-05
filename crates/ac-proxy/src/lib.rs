//! The system proxy, as OpenAC gets it from .NET, shared by the launcher's
//! HTTP (`ac-launcher-net`) and the plugins' network (`ac-plugin-net`).
//!
//! OpenAC's launcher and its plugins make their requests with `HttpClient`
//! and `ClientWebSocket` and name no proxy of their own, so every request
//! goes through .NET's `HttpClient.DefaultProxy`:
//!
//! - [`SystemProxy`] finds the proxy for an address: the environment
//!   variables `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY` and `NO_PROXY`,
//!   else on Windows the user's proxy settings (the manual proxy, its bypass
//!   list, and automatic configuration by script or detection). A Windows
//!   list is tried in order ([`Proxies`]): the next proxy when one cannot be
//!   connected to, which later requests then skip for 30 minutes.
//! - [`open_tunnel`] opens a byte stream to a server through the proxy:
//!   `CONNECT` through an `http` or `https` proxy, or SOCKS 4, 4a or 5.
//! - [`ProxyAuth`] answers the proxy's `407`: Negotiate, NTLM, Digest or
//!   Basic, in .NET's order, with the credentials the proxy's address
//!   carries (`user:password@`, `DOMAIN\user`, or `:@` for the Windows
//!   logon). NTLM and Negotiate run through the Windows security packages
//!   (SSPI); elsewhere they are refused with a clear error.

mod auth;
mod base64;
mod digest;
mod md5;
mod random;
mod resolve;
#[cfg(windows)]
mod sspi;
mod tunnel;

pub use auth::{ProxyAuth, Step, split_challenges};
pub use base64::{decode as base64_decode, encode as base64_encode};
pub use resolve::{
    Credentials, EnvironmentProxy, Proxies, ProxyKind, ProxyServer, Route, SystemProxy, Target,
    parse_proxy,
};
pub use tunnel::{Dialer, ProxyStream, TunnelError, open_tunnel};
