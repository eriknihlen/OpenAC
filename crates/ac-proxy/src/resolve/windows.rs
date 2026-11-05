//! The user's proxy settings on Windows (Settings > Network > Proxy, the
//! same the Internet Options dialog edits), as .NET's `HttpWindowsProxy`
//! reads them through WinHTTP: automatic configuration first (detection,
//! a script address, or both), then the manual proxy with its bypass list
//! (whose `<local>` takes in this machine's own addresses, read through
//! `GetAdaptersAddresses`).
//! WinHTTP runs the configuration script itself; after a failed detection
//! automatic configuration is not tried for two minutes, as in .NET, so a
//! network without a script does not cost every request a search. A list
//! of proxies is tried in order, one that could not be connected to skipped
//! by later requests for 30 minutes ([`Proxies`]).

#![allow(unsafe_code)]

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{
    ERROR_BUFFER_OVERFLOW, ERROR_INVALID_PARAMETER, ERROR_NO_DATA, ERROR_SUCCESS, GetLastError,
    GlobalFree,
};
use windows_sys::Win32::NetworkManagement::IpHelper::{
    GAA_FLAG_INCLUDE_ALL_INTERFACES, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER,
    GAA_FLAG_SKIP_FRIENDLY_NAME, GAA_FLAG_SKIP_MULTICAST, GetAdaptersAddresses,
    IP_ADAPTER_ADDRESSES_LH,
};
use windows_sys::Win32::Networking::WinHttp::{
    ERROR_WINHTTP_AUTODETECTION_FAILED, WINHTTP_ACCESS_TYPE_NAMED_PROXY,
    WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_AUTO_DETECT_TYPE_DHCP, WINHTTP_AUTO_DETECT_TYPE_DNS_A,
    WINHTTP_AUTOPROXY_AUTO_DETECT, WINHTTP_AUTOPROXY_CONFIG_URL, WINHTTP_AUTOPROXY_OPTIONS,
    WINHTTP_CURRENT_USER_IE_PROXY_CONFIG, WINHTTP_PROXY_INFO, WinHttpCloseHandle,
    WinHttpGetIEProxyConfigForCurrentUser, WinHttpGetProxyForUrl, WinHttpOpen,
};
use windows_sys::Win32::Networking::WinSock::{
    AF_INET, AF_INET6, AF_UNSPEC, SOCKADDR_IN, SOCKADDR_IN6,
};
use windows_sys::core::PWSTR;

use super::bypass::{BypassList, InterfaceAddress};
use super::list::FailedProxies;
use super::{Proxies, Route, Target};

/// How long automatic configuration is not tried after a failed detection.
const RECENT_DETECTION_FAILURE: Duration = Duration::from_secs(120);

/// The user's settings, read again for every request (they can change
/// while the program runs).
#[derive(Debug, Default)]
pub(crate) struct WindowsProxy {
    detection_failed_at: Mutex<Option<Instant>>,
    failed: Arc<FailedProxies>,
}

/// What automatic configuration answered.
enum Answer {
    /// Go direct.
    Direct,
    /// A proxy list, with or without a bypass list beside it.
    Proxies { list: String, bypass: bool },
}

/// The settings as WinHTTP reports them.
#[derive(Debug, Default)]
struct UserSettings {
    auto_detect: bool,
    script: Option<String>,
    proxy: Option<String>,
    bypass: Option<String>,
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

/// Takes a string WinHTTP allocated: its text, and the memory freed.
fn take(text: PWSTR) -> Option<String> {
    if text.is_null() {
        return None;
    }
    // SAFETY: WinHTTP hands back a NUL-terminated wide string it allocated
    // with GlobalAlloc; it is read up to its terminator and freed once.
    let value = unsafe {
        let mut length = 0;
        while *text.add(length) != 0 {
            length += 1;
        }
        let value = String::from_utf16_lossy(std::slice::from_raw_parts(text, length));
        GlobalFree(text.cast());
        value
    };
    Some(value).filter(|v| !v.trim().is_empty())
}

fn user_settings() -> Option<UserSettings> {
    let mut config = WINHTTP_CURRENT_USER_IE_PROXY_CONFIG::default();
    // SAFETY: `config` is a valid, zeroed structure the call fills in; its
    // strings are taken (and freed) below whatever the result.
    let ok = unsafe { WinHttpGetIEProxyConfigForCurrentUser(&raw mut config) } != 0;
    let settings = UserSettings {
        auto_detect: config.fAutoDetect != 0,
        script: take(config.lpszAutoConfigUrl),
        proxy: take(config.lpszProxy),
        bypass: take(config.lpszProxyBypass),
    };
    ok.then_some(settings)
}

/// What automatic configuration says for `url`; `Err` carries WinHTTP's
/// error when it could not be found or run.
fn automatic(url: &str, detect: bool, script: Option<&str>) -> Result<Answer, u32> {
    let agent = wide("OpenAC");
    // SAFETY: a session with no proxy of its own, used only to resolve one;
    // closed below.
    let session = unsafe {
        WinHttpOpen(
            agent.as_ptr(),
            WINHTTP_ACCESS_TYPE_NO_PROXY,
            std::ptr::null(),
            std::ptr::null(),
            0,
        )
    };
    if session.is_null() {
        // SAFETY: reads this thread's last error, set by the failed call.
        return Err(unsafe { GetLastError() });
    }
    let script = script.map(wide);
    let mut options = WINHTTP_AUTOPROXY_OPTIONS::default();
    if detect {
        options.dwFlags |= WINHTTP_AUTOPROXY_AUTO_DETECT;
        options.dwAutoDetectFlags = WINHTTP_AUTO_DETECT_TYPE_DHCP | WINHTTP_AUTO_DETECT_TYPE_DNS_A;
    }
    if let Some(script) = &script {
        options.dwFlags |= WINHTTP_AUTOPROXY_CONFIG_URL;
        options.lpszAutoConfigUrl = script.as_ptr();
    }
    options.fAutoLogonIfChallenged = 1;
    let target = wide(url);
    let mut info = WINHTTP_PROXY_INFO::default();
    // SAFETY: the session is open, the strings live until after the call,
    // and `info` is a zeroed structure the call fills in.
    let ok =
        unsafe { WinHttpGetProxyForUrl(session, target.as_ptr(), &raw mut options, &raw mut info) }
            != 0;
    // SAFETY: reads this thread's last error, set by the call just made.
    let error = unsafe { GetLastError() };
    let proxy = take(info.lpszProxy);
    let bypass = take(info.lpszProxyBypass).is_some();
    // SAFETY: the session handle was opened above and is closed once.
    unsafe {
        WinHttpCloseHandle(session);
    }
    if !ok {
        return Err(error);
    }
    match (info.dwAccessType, proxy) {
        (WINHTTP_ACCESS_TYPE_NAMED_PROXY, Some(list)) => Ok(Answer::Proxies { list, bypass }),
        _ => Ok(Answer::Direct),
    }
}

/// This machine's addresses for a bypass list's `<local>`: every
/// interface's unicast addresses, IPv6 ones with their scope (.NET
/// `NetworkInterface.GetAllNetworkInterfaces`, each one's
/// `UnicastAddresses`). `Err` carries `GetAdaptersAddresses`'s error, where
/// .NET throws `NetworkInformationException`; none are read as none.
fn interface_addresses() -> Result<Vec<InterfaceAddress>, u32> {
    let flags = GAA_FLAG_INCLUDE_ALL_INTERFACES
        | GAA_FLAG_SKIP_ANYCAST
        | GAA_FLAG_SKIP_MULTICAST
        | GAA_FLAG_SKIP_DNS_SERVER
        | GAA_FLAG_SKIP_FRIENDLY_NAME;
    let family = u32::from(AF_UNSPEC);
    let mut size = 0u32;
    // SAFETY: with no buffer the call only reports the size it needs.
    let mut result = unsafe {
        GetAdaptersAddresses(
            family,
            flags,
            std::ptr::null(),
            std::ptr::null_mut(),
            &raw mut size,
        )
    };
    loop {
        match result {
            ERROR_BUFFER_OVERFLOW => {
                // Eight-byte words: the structures need that alignment.
                let mut buffer = vec![0u64; (size as usize).div_ceil(8)];
                let first = buffer.as_mut_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
                // SAFETY: `buffer` holds `size` bytes, suitably aligned.
                result = unsafe {
                    GetAdaptersAddresses(family, flags, std::ptr::null(), first, &raw mut size)
                };
                if result == ERROR_SUCCESS {
                    return Ok(read_adapters(first));
                }
            }
            ERROR_SUCCESS | ERROR_INVALID_PARAMETER | ERROR_NO_DATA => return Ok(Vec::new()),
            error => return Err(error),
        }
    }
}

/// The unicast addresses of the adapters `GetAdaptersAddresses` wrote,
/// starting at `first`.
fn read_adapters(first: *const IP_ADAPTER_ADDRESSES_LH) -> Vec<InterfaceAddress> {
    let mut out = Vec::new();
    let mut adapter = first;
    while !adapter.is_null() {
        // SAFETY: `adapter` is in the list GetAdaptersAddresses wrote into
        // the caller's live buffer; its links stay inside that buffer.
        let (mut unicast, next) = unsafe { ((*adapter).FirstUnicastAddress, (*adapter).Next) };
        while !unicast.is_null() {
            // SAFETY: as above: the unicast list and each socket address
            // live in the same buffer, the address's family naming its type.
            unsafe {
                let address = (*unicast).Address.lpSockaddr;
                if !address.is_null() {
                    match (*address).sa_family {
                        AF_INET => {
                            let v4 = &*address.cast::<SOCKADDR_IN>();
                            let octets = v4.sin_addr.S_un.S_addr.to_ne_bytes();
                            out.push((IpAddr::V4(Ipv4Addr::from(octets)), 0));
                        }
                        AF_INET6 => {
                            let v6 = &*address.cast::<SOCKADDR_IN6>();
                            let octets = v6.sin6_addr.u.Byte;
                            let scope = v6.Anonymous.sin6_scope_id;
                            out.push((IpAddr::V6(Ipv6Addr::from(octets)), scope));
                        }
                        _ => {}
                    }
                }
                unicast = (*unicast).Next;
            }
        }
        adapter = next;
    }
    out
}

/// The address WinHTTP is asked about: a WebSocket's as its HTTP form
/// (`ws` as `http`, `wss` as `https`), as .NET passes it.
fn http_form(url: &str) -> String {
    for (from, to) in [("wss:", "https:"), ("ws:", "http:")] {
        if let Some(rest) = url
            .get(..from.len())
            .filter(|head| head.eq_ignore_ascii_case(from))
            .map(|_| &url[from.len()..])
        {
            return format!("{to}{rest}");
        }
    }
    url.to_owned()
}

impl WindowsProxy {
    /// .NET `HttpWindowsProxy.GetMultiProxy`.
    pub(super) fn route(&self, target: &Target<'_>) -> Route {
        let Some(settings) = user_settings() else {
            return Ok(Proxies::direct());
        };
        let https = target.is_secure();
        let failed_at = || {
            self.detection_failed_at
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
        };
        let recently_failed =
            failed_at().is_some_and(|at| at.elapsed() <= RECENT_DETECTION_FAILURE);
        if (settings.auto_detect || settings.script.is_some()) && !recently_failed {
            let answer = automatic(
                &http_form(target.url),
                settings.auto_detect,
                settings.script.as_deref(),
            );
            *failed_at() =
                matches!(answer, Err(ERROR_WINHTTP_AUTODETECTION_FAILED)).then(Instant::now);
            match answer {
                Ok(Answer::Direct) => return Ok(Proxies::direct()),
                Ok(Answer::Proxies {
                    list,
                    bypass: false,
                }) => return Ok(Proxies::lazy(&list, https, &self.failed)),
                // WinHTTP fell back to the manual settings, with their
                // bypass list: they are read below.
                Ok(Answer::Proxies { bypass: true, .. }) => {}
                // .NET falls back to the manual proxy; without a bypass
                // list it is read as an automatic answer.
                Err(_) => match (&settings.proxy, &settings.bypass) {
                    (None, _) => return Ok(Proxies::direct()),
                    (Some(list), None) => return Ok(Proxies::lazy(list, https, &self.failed)),
                    (Some(_), Some(_)) => {}
                },
            }
        }
        let Some(list) = settings.proxy else {
            return Ok(Proxies::direct());
        };
        if let Some(bypass) = &settings.bypass {
            let bypassed = BypassList::parse(bypass)
                .is_bypassed(target.host, interface_addresses)
                .map_err(|error| {
                    format!("this machine's network addresses could not be read (error {error})")
                })?;
            if bypassed {
                return Ok(Proxies::direct());
            }
        }
        Ok(Proxies::manual(&list, https, &self.failed))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // WinHTTP reads the current user's settings (whatever this machine has)
    // and the strings it allocated are taken and freed without fault.
    #[test]
    fn the_users_settings_are_read_and_freed() {
        assert!(
            user_settings().is_some(),
            "WinHTTP reads the current user's settings"
        );
    }

    // Every machine has the loopback interface, whose 127.0.0.1 .NET lists
    // among its unicast addresses ("Loopback Pseudo-Interface 1"). On the
    // development machine the whole list, scopes and order, matched .NET
    // 10.0.8's `GetAllNetworkInterfaces` (2026-10-15).
    #[test]
    fn this_machines_addresses_are_read() {
        let addresses = interface_addresses().expect("GetAdaptersAddresses answers");
        assert!(
            addresses.contains(&(IpAddr::V4(Ipv4Addr::LOCALHOST), 0)),
            "{addresses:?}"
        );
    }

    // .NET `WinInetProxyHelper.GetProxyForUrl` asks WinHTTP about a
    // WebSocket's address in its HTTP form.
    #[test]
    fn web_socket_addresses_are_asked_about_in_their_http_form() {
        assert_eq!(http_form("wss://example.com/"), "https://example.com/");
        assert_eq!(http_form("ws://example.com:80/"), "http://example.com:80/");
        assert_eq!(http_form("https://example.com/"), "https://example.com/");
    }

    // This machine's route and how long finding it takes, a failed
    // detection then held for two minutes:
    // `cargo test -p ac-proxy --lib -- --ignored --nocapture route_on_this_machine`.
    #[test]
    #[ignore = "reads this machine's settings and may search the network"]
    fn route_on_this_machine() {
        let proxy = WindowsProxy::default();
        let url = Target::new("https", "github.com", "https://github.com/");
        for attempt in 0..2 {
            let started = Instant::now();
            let route = proxy.route(&url);
            println!("attempt {attempt}: {route:?} in {:?}", started.elapsed());
        }
    }
}
