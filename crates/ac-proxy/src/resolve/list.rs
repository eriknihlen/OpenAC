//! A Windows proxy list and the proxies a request tries from it, as .NET's
//! `MultiProxy` and `FailedProxyCache` do: the entries that serve the
//! request's scheme, in their order; a proxy that could not be connected
//! to is skipped by later requests for 30 minutes.

use std::collections::HashMap;
use std::net::{Ipv4Addr, Ipv6Addr};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use super::{ProxyKind, ProxyServer};

/// How long a proxy that could not be connected to is skipped.
const FAILURE_TIMEOUT: Duration = Duration::from_secs(30 * 60);
/// How often expired failures are swept out of a large memory.
const FLUSH_EVERY: Duration = Duration::from_secs(5 * 60);
/// The number of remembered failures above which they are swept.
const LARGE_MEMORY: usize = 8;

/// The characters between a list's entries.
const DELIMITERS: &[char] = &[';', ' ', '\n', '\r', '\t'];

/// One proxy server's identity in the failure memory.
type Key = (String, u16);

fn key(server: &ProxyServer) -> Key {
    (server.host.clone(), server.port)
}

/// The proxies that could not be connected to, and until when they are
/// skipped (.NET `FailedProxyCache`). Shared by every request of one
/// [`super::SystemProxy`].
#[derive(Debug)]
pub(crate) struct FailedProxies {
    state: Mutex<FailedState>,
}

#[derive(Debug)]
struct FailedState {
    renew_at: HashMap<Key, Instant>,
    next_flush: Instant,
}

impl Default for FailedProxies {
    fn default() -> Self {
        Self {
            state: Mutex::new(FailedState {
                renew_at: HashMap::new(),
                next_flush: Instant::now() + FLUSH_EVERY,
            }),
        }
    }
}

impl FailedState {
    /// Sweeps out expired failures once there are many of them, at most
    /// every five minutes.
    fn cleanup(&mut self, now: Instant) {
        if self.renew_at.len() > LARGE_MEMORY && now >= self.next_flush {
            self.renew_at.retain(|_, at| now < *at);
            self.next_flush = now + FLUSH_EVERY;
        }
    }
}

impl FailedProxies {
    fn with_state<T>(&self, job: impl FnOnce(&mut FailedState) -> T) -> T {
        // A poisoned memory is still a memory: a panic elsewhere left it
        // consistent (every change is one map operation).
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        job(&mut state)
    }

    /// When `server` may be used again: `None` now (it has not failed, or
    /// its time is up and it is forgotten).
    fn renew_at(&self, server: &ProxyServer, now: Instant) -> Option<Instant> {
        self.with_state(|state| {
            state.cleanup(now);
            let key = key(server);
            let at = *state.renew_at.get(&key)?;
            if now < at {
                return Some(at);
            }
            state.renew_at.remove(&key);
            None
        })
    }

    /// `server` could not be connected to: skip it for 30 minutes.
    fn set_failed(&self, server: &ProxyServer, now: Instant) {
        self.with_state(|state| {
            state.renew_at.insert(key(server), now + FAILURE_TIMEOUT);
            state.cleanup(now);
        });
    }

    /// Forgets `server`'s failure, if it is still the one recorded at `at`.
    fn renew(&self, server: &ProxyServer, at: Instant) {
        self.with_state(|state| {
            let key = key(server);
            if state.renew_at.get(&key) == Some(&at) {
                state.renew_at.remove(&key);
            }
        });
    }
}

/// Where the proxies come from.
#[derive(Debug, Clone)]
enum Entries {
    /// Parsed in advance (the manual settings, or a single proxy).
    Listed(Vec<ProxyServer>),
    /// Parsed one entry at a time (an automatic configuration's answer,
    /// which only the Windows settings give).
    #[cfg(any(windows, test))]
    Lazy { list: String, secure: bool },
}

/// The proxies a request tries, in order (.NET `MultiProxy`): none (go
/// direct), one, or a Windows list, whose proxies that could not be
/// connected to recently are skipped.
#[derive(Debug, Clone)]
pub struct Proxies {
    entries: Entries,
    index: usize,
    current: Option<ProxyServer>,
    failed: Option<Arc<FailedProxies>>,
}

impl Proxies {
    /// No proxy: the request goes direct.
    #[must_use]
    pub fn direct() -> Self {
        Self::listed(Vec::new(), None)
    }

    /// Always `server` (the environment's proxy, or a fixed one).
    #[must_use]
    pub fn single(server: ProxyServer) -> Self {
        Self::listed(vec![server], None)
    }

    fn listed(servers: Vec<ProxyServer>, failed: Option<Arc<FailedProxies>>) -> Self {
        Self {
            entries: Entries::Listed(servers),
            index: 0,
            current: None,
            failed,
        }
    }

    /// The manual settings' list (.NET `MultiProxy.ParseManualSettings`):
    /// every entry for the scheme, read now.
    pub(crate) fn manual(list: &str, secure: bool, failed: &Arc<FailedProxies>) -> Self {
        let mut servers = Vec::new();
        let mut rest = list;
        while let (Some(server), consumed) = parse_part(rest, secure, true) {
            servers.push(server);
            rest = &rest[consumed..];
        }
        Self::listed(servers, Some(Arc::clone(failed)))
    }

    /// An automatic configuration's list (.NET `MultiProxy.CreateLazy`):
    /// read an entry at a time as the request goes on.
    #[cfg(any(windows, test))]
    pub(crate) fn lazy(list: &str, secure: bool, failed: &Arc<FailedProxies>) -> Self {
        if list.is_empty() {
            return Self::direct();
        }
        Self {
            entries: Entries::Lazy {
                list: list.to_owned(),
                secure,
            },
            index: 0,
            current: None,
            failed: Some(Arc::clone(failed)),
        }
    }

    /// The next entry and whether it is the last (.NET
    /// `MultiProxy.ReadNextHelper`).
    fn read_entry(&mut self) -> (Option<ProxyServer>, bool) {
        match &self.entries {
            Entries::Listed(servers) => match servers.get(self.index) {
                Some(server) => {
                    self.index += 1;
                    (Some(server.clone()), self.index == servers.len())
                }
                None => (None, false),
            },
            #[cfg(any(windows, test))]
            Entries::Lazy { list, secure } => {
                if self.index >= list.len() {
                    return (None, false);
                }
                let (server, consumed) = parse_part(&list[self.index..], *secure, false);
                self.index += consumed;
                (server, self.index == list.len())
            }
        }
    }

    /// The next proxy to try and whether it is the last (.NET
    /// `MultiProxy.ReadNext`). Asking again means the one given before
    /// could not be connected to: it is skipped for 30 minutes. Proxies
    /// skipped that way are passed over; when every one is, the first ask
    /// gets the one whose time is up soonest.
    pub fn read_next(&mut self) -> Option<(ProxyServer, bool)> {
        let now = Instant::now();
        if let (Some(current), Some(failed)) = (&self.current, &self.failed) {
            failed.set_failed(current, now);
        }
        let (Some(mut server), mut last) = self.read_entry() else {
            self.current = None;
            return None;
        };
        let Some(failed) = self.failed.clone() else {
            self.current = Some(server.clone());
            return Some((server, last));
        };
        let mut soonest: Option<(ProxyServer, Instant)> = None;
        loop {
            let Some(at) = failed.renew_at(&server, now) else {
                self.current = Some(server.clone());
                return Some((server, last));
            };
            if soonest.as_ref().is_none_or(|(_, soonest)| at < *soonest) {
                soonest = Some((server, at));
            }
            match self.read_entry() {
                (Some(next), next_last) => {
                    server = next;
                    last = next_last;
                }
                (None, next_last) => {
                    last = next_last;
                    break;
                }
            }
        }
        if self.current.is_some() {
            return None;
        }
        let (server, at) = soonest?;
        failed.renew(&server, at);
        self.current = Some(server.clone());
        Some((server, last))
    }

    /// Runs `attempt` through the proxies as .NET's `HttpClient` sends a
    /// request (`HttpConnectionPoolManager.SendAsync`): direct (`None`)
    /// when there is none; once through the only or last one; otherwise
    /// through each in turn while `unreachable` says the proxy could not be
    /// connected to, the last such failure being the result when none is
    /// left. Any other failure is the result at once.
    ///
    /// # Errors
    /// The failure of the last attempt.
    pub fn try_each<T, E>(
        mut self,
        mut attempt: impl FnMut(Option<&ProxyServer>) -> Result<T, E>,
        unreachable: impl Fn(&E) -> bool,
    ) -> Result<T, E> {
        let Some((mut server, last)) = self.read_next() else {
            return attempt(None);
        };
        if last {
            return attempt(Some(&server));
        }
        loop {
            match attempt(Some(&server)) {
                Err(error) if unreachable(&error) => match self.read_next() {
                    Some((next, _)) => server = next,
                    None => return Err(error),
                },
                outcome => return outcome,
            }
        }
    }

    /// Every proxy, in order, without trying any (tests and diagnostics).
    #[must_use]
    pub fn servers(&self) -> Vec<ProxyServer> {
        let mut copy = Self {
            failed: None,
            current: None,
            ..self.clone()
        };
        let mut servers = Vec::new();
        while let Some((server, _)) = copy.read_next() {
            servers.push(server);
        }
        servers
    }
}

/// The first entry of `list` that serves the scheme, and how many bytes
/// were read to find it (.NET `MultiProxy.TryParseProxyConfigPart`).
/// Entries are split by `;` or white space, each `[scheme=][scheme://]
/// address`; `http=` serves `http` only and `https=` `https` only (exact
/// lower case), an entry without one serves both. In an automatic
/// configuration's answer an `http://` or `https://` prefix also picks the
/// scheme; in the manual settings it is only dropped. Every proxy is spoken
/// to as an HTTP proxy, and an entry that is not an address (a `socks=` or
/// `ftp=` entry, a bad port) is passed over.
fn parse_part(list: &str, secure: bool, manual: bool) -> (Option<ProxyServer>, usize) {
    const SECURE: u8 = 1;
    const INSECURE: u8 = 2;
    let wanted = if secure { SECURE } else { INSECURE };
    let mut rest = list;
    loop {
        rest = rest.trim_start_matches(DELIMITERS);
        if rest.is_empty() {
            return (None, list.len());
        }
        let mut serves = SECURE | INSECURE;
        if let Some(after) = rest.strip_prefix("http=") {
            serves = INSECURE;
            rest = after;
        } else if let Some(after) = rest.strip_prefix("https=") {
            serves = SECURE;
            rest = after;
        }
        if let Some(after) = rest.strip_prefix("http://") {
            if !manual {
                serves = INSECURE;
            }
            rest = after;
        } else if let Some(after) = rest.strip_prefix("https://") {
            if !manual {
                serves = SECURE;
            }
            rest = after;
        }
        let end = rest.find(DELIMITERS).unwrap_or(rest.len());
        if serves & wanted != 0
            && let Some(server) = http_address(&rest[..end])
        {
            let consumed = list.len() - rest.len() + end;
            return (Some(server), consumed);
        }
        rest = &rest[end..];
    }
}

/// `text` as .NET's `Uri` reads `http://` followed by it: `[user[:password]
/// @]host[:port][/path][?query][#fragment]`, the host a name (letters,
/// digits, `-` and `_`, dot-separated, an optional final dot), an IPv4
/// address, or an IPv6 address in brackets; an empty port is 80. The user
/// information and anything after the address are dropped (the Windows
/// settings carry no credentials). `None` when it is not an address.
fn http_address(text: &str) -> Option<ProxyServer> {
    let authority_end = text.find(['/', '?', '#']).unwrap_or(text.len());
    let authority = &text[..authority_end];
    if authority.contains('\\') {
        return None;
    }
    let host_port = match authority.split_once('@') {
        Some((_, host_port)) if host_port.contains('@') => return None,
        Some((_, host_port)) => host_port,
        None => authority,
    };
    let (host, port) = if let Some(inner) = host_port.strip_prefix('[') {
        let (address, after) = inner.split_once(']')?;
        let bare = address.split_once('%').map_or(address, |(bare, _)| bare);
        bare.parse::<Ipv6Addr>().ok()?;
        let port = match after.strip_prefix(':') {
            Some(digits) => port(digits)?,
            // Anything else after the brackets starts the path.
            None => 80,
        };
        (format!("[{}]", address.to_ascii_lowercase()), port)
    } else {
        let (host, port) = match host_port.split_once(':') {
            Some((host, digits)) => (host, port(digits)?),
            None => (host_port, 80),
        };
        if host.parse::<Ipv4Addr>().is_err() && !is_name(host) {
            return None;
        }
        (host.to_lowercase(), port)
    };
    Some(ProxyServer {
        kind: ProxyKind::Http,
        host,
        port,
        credentials: None,
    })
}

/// A port as `Uri` reads it: digits (leading zeros allowed) up to 65535;
/// none at all is the scheme's default, 80.
fn port(digits: &str) -> Option<u16> {
    if digits.is_empty() {
        return Some(80);
    }
    if !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let value = digits
        .trim_start_matches('0')
        .parse::<u32>()
        .ok()
        .or_else(|| digits.bytes().all(|b| b == b'0').then_some(0))?;
    u16::try_from(value).ok()
}

/// A host name `Uri` takes for `http`: dot-separated labels of letters,
/// digits, `-` and `_`, none empty but an optional last one.
fn is_name(host: &str) -> bool {
    let labels = host.strip_suffix('.').unwrap_or(host);
    !labels.is_empty()
        && labels.split('.').all(|label| {
            !label.is_empty()
                && label
                    .chars()
                    .all(|c| c.is_alphanumeric() || c == '-' || c == '_')
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn http(host: &str, port: u16) -> ProxyServer {
        ProxyServer {
            kind: ProxyKind::Http,
            host: host.to_owned(),
            port,
            credentials: None,
        }
    }

    fn names(servers: &[ProxyServer]) -> Vec<String> {
        servers.iter().map(ToString::to_string).collect()
    }

    fn manual(list: &str, secure: bool) -> Vec<String> {
        let failed = Arc::new(FailedProxies::default());
        names(&Proxies::manual(list, secure, &failed).servers())
    }

    fn automatic(list: &str, secure: bool) -> Vec<String> {
        let failed = Arc::new(FailedProxies::default());
        names(&Proxies::lazy(list, secure, &failed).servers())
    }

    // Golden values: .NET 10.0.8's `MultiProxy.ParseManualSettings` and
    // `CreateLazy` read through reflection (2026-10-15). Every entry for
    // the scheme in the list's order, a general entry where it stands;
    // `socks=`, `ftp=`, upper-case `HTTPS=` and bad ports are passed over.
    #[test]
    fn a_list_gives_the_schemes_entries_in_order() {
        assert_eq!(manual("proxy:8080", true), ["proxy:8080"]);
        assert_eq!(manual("a:1;b:2;c:3", true), ["a:1", "b:2", "c:3"]);
        let list = "http=web:80;https=secure:443 ftp=files:21";
        assert_eq!(manual(list, true), ["secure:443"]);
        assert_eq!(manual(list, false), ["web:80"]);
        assert_eq!(
            manual("general:1;https=secure:2", true),
            ["general:1", "secure:2"]
        );
        assert_eq!(manual("ftp=files:21;general:1", true), ["general:1"]);
        assert_eq!(manual("HTTPS=sec:1", true), Vec::<String>::new());
        assert_eq!(manual("p:99999;q:2", true), ["q:2"]);
        assert_eq!(manual("p:port;q:2", true), ["q:2"]);
        assert_eq!(manual("user:pw@p:8080", true), ["p:8080"]);
        assert_eq!(manual("[::1]:8080", true), ["[::1]:8080"]);
        assert_eq!(manual("p", true), ["p:80"]);
        assert_eq!(manual("p:", true), ["p:80"]);
        assert_eq!(manual("PROXY p:1", true), ["proxy:80", "p:1"]);
        // `socks5://` is not a prefix .NET knows: `socks5` is read as the
        // host (port 80) and the rest as a path.
        assert_eq!(manual("socks5://s5:1080", true), ["socks5:80"]);
    }

    // .NET 10.0.8: a `socks=` entry of the Windows settings is not a proxy
    // .NET uses (`socks=host` is no host name), for either scheme.
    #[test]
    fn socks_entries_are_passed_over() {
        for secure in [true, false] {
            assert_eq!(manual("socks=sock:1080", secure), Vec::<String>::new());
            assert_eq!(automatic("socks=sock:1080", secure), Vec::<String>::new());
            assert_eq!(
                manual("socks=sock:1080 general:3128", secure),
                ["general:3128"]
            );
        }
        assert_eq!(manual("socks=sock:1080;https=sec:443", true), ["sec:443"]);
        assert_eq!(
            manual("socks=sock:1080;https=sec:443", false),
            Vec::<String>::new()
        );
    }

    // .NET 10.0.8: an `http://` or `https://` prefix picks the scheme in an
    // automatic configuration's answer and is only dropped in the manual
    // settings; the proxy is an HTTP proxy either way.
    #[test]
    fn a_url_prefix_picks_the_scheme_only_in_an_automatic_answer() {
        let list = "http://web:80;https://sec:443";
        assert_eq!(manual(list, true), ["web:80", "sec:443"]);
        assert_eq!(automatic(list, true), ["sec:443"]);
        assert_eq!(automatic(list, false), ["web:80"]);
        assert_eq!(manual("https=https://sec:443", true), ["sec:443"]);
        assert_eq!(manual("https=https://sec:443", false), Vec::<String>::new());
        assert_eq!(manual("http=http://web:80", false), ["web:80"]);
    }

    // .NET 10.0.8 `Uri.TryCreate("http://" + entry)`.
    #[test]
    fn an_entry_is_an_address_as_uri_reads_it() {
        let ok = |text: &str| http_address(text).map(|s| s.to_string());
        assert_eq!(ok("H.Example.COM"), Some("h.example.com:80".into()));
        assert_eq!(ok("a_b"), Some("a_b:80".into()));
        assert_eq!(ok("-a"), Some("-a:80".into()));
        assert_eq!(ok("a."), Some("a.:80".into()));
        assert_eq!(ok("h:0"), Some("h:0".into()));
        assert_eq!(ok("h:65535"), Some("h:65535".into()));
        assert_eq!(ok("h:08080"), Some("h:8080".into()));
        assert_eq!(ok("u:p@h:1"), Some("h:1".into()));
        assert_eq!(ok("@h"), Some("h:80".into()));
        assert_eq!(ok("h:1/x?y#z"), Some("h:1".into()));
        assert_eq!(ok("[::1]:"), Some("[::1]:80".into()));
        assert_eq!(ok("[::1]x"), Some("[::1]:80".into()));
        assert_eq!(ok("1.2.3.4:5"), Some("1.2.3.4:5".into()));
        assert_eq!(ok("999.1.1.1"), Some("999.1.1.1:80".into()));
        for bad in [
            "a=b",
            "a~b",
            "a!b",
            "a*b",
            "a%41b",
            "a..b",
            ".a",
            "a\\b",
            "h:65536",
            "h:+1",
            "h:1a",
            "h::1",
            "a@b@h",
            "h\\x",
            "::1",
            "[zz]",
            "[1.2.3.4]",
            "",
            ":1",
            "/x",
            "a:b:c",
        ] {
            assert_eq!(ok(bad), None, "{bad}");
        }
    }

    // .NET `MultiProxy.ReadNext`: asking for the next proxy marks the one
    // before as failed, and later requests pass over it.
    #[test]
    fn a_proxy_that_failed_is_passed_over_by_later_requests() {
        let failed = Arc::new(FailedProxies::default());
        let mut first = Proxies::manual("a:1;b:2;c:3", true, &failed);
        assert_eq!(first.read_next(), Some((http("a", 1), false)));
        assert_eq!(first.read_next(), Some((http("b", 2), false)));
        let mut second = Proxies::manual("a:1;b:2;c:3", true, &failed);
        assert_eq!(second.read_next(), Some((http("b", 2), false)));
        // Asking past the last one marks it too.
        let mut third = Proxies::manual("a:1;b:2;c:3", true, &failed);
        assert_eq!(third.read_next(), Some((http("b", 2), false)));
        assert_eq!(third.read_next(), Some((http("c", 3), true)));
        assert_eq!(third.read_next(), None);
        // Every one failed: the first ask gets the one whose time is up
        // soonest (the first to fail), and its failure is forgotten.
        let mut fourth = Proxies::manual("a:1;b:2;c:3", true, &failed);
        assert_eq!(fourth.read_next(), Some((http("a", 1), false)));
        let mut fifth = Proxies::manual("a:1;b:2;c:3", true, &failed);
        assert_eq!(fifth.read_next(), Some((http("a", 1), false)));
    }

    #[test]
    fn a_failure_is_forgotten_after_thirty_minutes() {
        let failed = FailedProxies::default();
        let now = Instant::now();
        failed.set_failed(&http("a", 1), now);
        assert_eq!(
            failed.renew_at(&http("a", 1), now),
            Some(now + FAILURE_TIMEOUT)
        );
        assert_eq!(failed.renew_at(&http("b", 1), now), None);
        assert_eq!(failed.renew_at(&http("a", 1), now + FAILURE_TIMEOUT), None);
        assert_eq!(failed.with_state(|state| state.renew_at.len()), 0);
    }

    // .NET `HttpConnectionPoolManager.SendAsyncMultiProxy`: the next proxy
    // after one that could not be reached; any other failure ends there.
    #[test]
    fn a_request_tries_the_next_proxy_only_when_one_cannot_be_reached() {
        let failed = Arc::new(FailedProxies::default());
        let mut tried = Vec::new();
        let outcome: Result<(), &str> = Proxies::manual("a:1;b:2;c:3", true, &failed).try_each(
            |server| {
                tried.push(server.unwrap().to_string());
                Err(if tried.len() == 1 {
                    "unreachable"
                } else {
                    "refused"
                })
            },
            |error| *error == "unreachable",
        );
        assert_eq!(outcome, Err("refused"));
        assert_eq!(tried, ["a:1", "b:2"]);

        let mut tried = Vec::new();
        let outcome: Result<(), &str> = Proxies::manual("x:1;y:2", true, &failed).try_each(
            |server| {
                tried.push(server.unwrap().to_string());
                Err("unreachable")
            },
            |_| true,
        );
        assert_eq!(outcome, Err("unreachable"));
        assert_eq!(tried, ["x:1", "y:2"]);

        let direct: Result<bool, ()> = Proxies::direct().try_each(|s| Ok(s.is_none()), |_| true);
        assert_eq!(direct, Ok(true));
    }
}
