//! Where a request goes: the proxy .NET's `HttpClient.DefaultProxy` names
//! for its address. The environment variables when they name one
//! (`HttpEnvironmentProxy`), else on Windows the user's proxy settings
//! (`HttpWindowsProxy`), else direct.

use std::fmt;
use std::sync::Arc;

mod bypass;
mod list;
#[cfg(windows)]
mod windows;

pub use list::Proxies;

/// How a proxy is spoken to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProxyKind {
    /// HTTP over plain TCP: `CONNECT` for a tunnel, or the request itself.
    Http,
    /// HTTP over TLS to the proxy.
    Https,
    /// SOCKS 4: the server's IPv4 address, resolved here.
    Socks4,
    /// SOCKS 4a: the server's name, resolved by the proxy.
    Socks4a,
    /// SOCKS 5 (RFC 1928), with a user name and password by RFC 1929.
    Socks5,
}

/// The credentials a proxy's address carries (.NET builds a
/// `NetworkCredential` from the address's user information).
#[derive(Clone, PartialEq, Eq)]
pub enum Credentials {
    /// A user name, its domain (`DOMAIN\user`) and a password.
    Named {
        /// The domain before a backslash in the user name.
        domain: Option<String>,
        /// The user name.
        user: String,
        /// The password (empty when the address has none).
        password: String,
    },
    /// `:` alone (`http://:@proxy:8080`): the Windows logon (.NET's
    /// `CredentialCache.DefaultNetworkCredentials`), whose user name and
    /// password read as empty.
    Logon,
}

impl Credentials {
    /// The user name (empty for the logon).
    #[must_use]
    pub fn user(&self) -> &str {
        match self {
            Self::Named { user, .. } => user,
            Self::Logon => "",
        }
    }

    /// The password (empty for the logon).
    #[must_use]
    pub fn password(&self) -> &str {
        match self {
            Self::Named { password, .. } => password,
            Self::Logon => "",
        }
    }

    /// The domain, if one was named.
    #[must_use]
    pub fn domain(&self) -> Option<&str> {
        match self {
            Self::Named { domain, .. } => domain.as_deref().filter(|d| !d.is_empty()),
            Self::Logon => None,
        }
    }
}

impl fmt::Debug for Credentials {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Named { .. } => f.write_str("Named(..)"),
            Self::Logon => f.write_str("Logon"),
        }
    }
}

/// A proxy to go through.
#[derive(Clone, PartialEq, Eq)]
pub struct ProxyServer {
    /// How it is spoken to.
    pub kind: ProxyKind,
    /// Host name or address (an IPv6 address in brackets).
    pub host: String,
    /// TCP port.
    pub port: u16,
    /// What answers its `407` (and a SOCKS proxy's login).
    pub credentials: Option<Credentials>,
}

impl ProxyServer {
    /// The host without the brackets of an IPv6 address, for connecting.
    #[must_use]
    pub fn bare_host(&self) -> &str {
        self.host.trim_start_matches('[').trim_end_matches(']')
    }
}

impl fmt::Debug for ProxyServer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ProxyServer")
            .field("kind", &self.kind)
            .field("host", &self.host)
            .field("port", &self.port)
            .field("credentials", &self.credentials)
            .finish()
    }
}

impl fmt::Display for ProxyServer {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}:{}", self.host, self.port)
    }
}

/// Where a request goes: the proxies it tries (none: direct). `Err` names
/// a setting that cannot be followed.
pub type Route = Result<Proxies, String>;

/// The address a request or connection is for.
#[derive(Debug, Clone, Copy)]
pub struct Target<'a> {
    scheme: &'a str,
    host: &'a str,
    /// Read by the Windows settings (a configuration script is given it).
    #[cfg_attr(not(windows), allow(dead_code))]
    url: &'a str,
}

impl<'a> Target<'a> {
    /// `scheme` is the request's (`http`, `https`, `ws` or `wss`), `host`
    /// its host (an IPv6 address with or without brackets), `url` the whole
    /// address (a configuration script is given it).
    #[must_use]
    pub fn new(scheme: &'a str, host: &'a str, url: &'a str) -> Self {
        Self { scheme, host, url }
    }

    /// `https` and `wss` take the HTTPS proxy, `http` and `ws` the HTTP one
    /// (.NET `IsSupportedNonSecureScheme`).
    #[must_use]
    pub fn is_secure(&self) -> bool {
        self.scheme.eq_ignore_ascii_case("https") || self.scheme.eq_ignore_ascii_case("wss")
    }

    fn bare_host(&self) -> &str {
        self.host.trim_start_matches('[').trim_end_matches(']')
    }

    /// The host as .NET's `Uri.Host` gives it: an IPv6 address in brackets.
    fn uri_host(&self) -> String {
        let bare = self.bare_host();
        if bare.contains(':') {
            format!("[{bare}]")
        } else {
            bare.to_owned()
        }
    }

    /// `localhost`, `127.0.0.0/8` or `::1`.
    fn is_loopback(&self) -> bool {
        let bare = self.bare_host();
        bare.eq_ignore_ascii_case("localhost")
            || bare
                .parse::<std::net::IpAddr>()
                .is_ok_and(|address| address.is_loopback())
    }
}

/// Percent-decoding (.NET `Uri.UnescapeDataString`): `%XX` becomes the
/// byte, anything else is kept.
fn unescape(text: &str) -> String {
    let bytes = text.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut at = 0;
    while at < bytes.len() {
        if bytes[at] == b'%'
            && let Some(hex) = text.get(at + 1..at + 3)
            && let Ok(value) = u8::from_str_radix(hex, 16)
        {
            out.push(value);
            at += 3;
            continue;
        }
        out.push(bytes[at]);
        at += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// A proxy as .NET's `HttpEnvironmentProxy.GetUriFromString` reads it:
/// `[scheme://][user[:password]@]host[:port]`. The schemes are `http` (also
/// when none is written), `https`, `socks4`, `socks4a` and `socks5`; the
/// port defaults to 443 for `https` and to 80 for every other (SOCKS
/// included); the user information is percent-decoded and split at its
/// first `:`. `None` for text that is not a proxy address.
#[must_use]
pub fn parse_proxy(text: &str) -> Option<ProxyServer> {
    let text = text.trim();
    if text.is_empty() {
        return None;
    }
    let prefixes: [(&str, ProxyKind, u16); 5] = [
        ("http://", ProxyKind::Http, 80),
        ("https://", ProxyKind::Https, 443),
        ("socks4://", ProxyKind::Socks4, 80),
        ("socks5://", ProxyKind::Socks5, 80),
        ("socks4a://", ProxyKind::Socks4a, 80),
    ];
    let (kind, default_port, mut rest) = prefixes
        .iter()
        .find_map(|(prefix, kind, port)| {
            text.get(..prefix.len())
                .filter(|head| head.eq_ignore_ascii_case(prefix))
                .map(|_| (*kind, *port, &text[prefix.len()..]))
        })
        .unwrap_or((ProxyKind::Http, 80, text));
    rest = rest.strip_suffix('/').unwrap_or(rest);
    let (user_info, authority) = match rest.rsplit_once('@') {
        Some((user, authority)) => (Some(unescape(user)), authority),
        None => (None, rest),
    };
    let bracket_end = authority.find(']');
    let (host, port) = match authority.rfind(':') {
        Some(colon) if bracket_end.is_none_or(|end| colon > end) => {
            let digits: String = authority[colon + 1..]
                .chars()
                .take_while(char::is_ascii_digit)
                .collect();
            (&authority[..colon], digits.parse::<u16>().ok()?)
        }
        _ => (authority, default_port),
    };
    if host.is_empty()
        || host.contains(char::is_whitespace)
        || host.contains(['/', '?', '#', '@'])
        || (host.contains(':') && !host.starts_with('['))
    {
        return None;
    }
    let credentials = user_info.and_then(|info| {
        let (user, password) = match info.split_once(':') {
            Some((user, password)) => (user.to_owned(), Some(password.to_owned())),
            None => (info, None),
        };
        match password {
            // `:@`: the address's user information reads `:`, the logon.
            Some(password) if user.is_empty() && password.is_empty() => Some(Credentials::Logon),
            // `@` alone: no user information at all.
            None if user.is_empty() => None,
            password => {
                let (domain, user) = match user.split_once('\\') {
                    Some((domain, user)) => (Some(domain.to_owned()), user.to_owned()),
                    None => (None, user),
                };
                Some(Credentials::Named {
                    domain,
                    user,
                    password: password.unwrap_or_default(),
                })
            }
        }
    });
    Some(ProxyServer {
        kind,
        host: host.to_owned(),
        port,
        credentials,
    })
}

/// The proxy the environment names (.NET `HttpEnvironmentProxy`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnvironmentProxy {
    http: Option<ProxyServer>,
    https: Option<ProxyServer>,
    bypass: Vec<String>,
}

impl EnvironmentProxy {
    /// From the variables `lookup` reads: none when neither an HTTP nor an
    /// HTTPS proxy is named. Where names are case sensitive (not Windows)
    /// the lower-case name is read first; `HTTP_PROXY` is ignored when
    /// `GATEWAY_INTERFACE` is set (a CGI script's request header);
    /// `ALL_PROXY` stands in for whichever of the two is missing.
    pub fn from_lookup(
        lookup: impl Fn(&str) -> Option<String>,
        case_sensitive_names: bool,
    ) -> Option<Self> {
        let get = |name: &str| lookup(name).and_then(|v| parse_proxy(&v));
        let lower = |name: &str| case_sensitive_names.then(|| get(name)).flatten();
        let cgi = lookup("GATEWAY_INTERFACE").is_some();
        let mut http = lower("http_proxy");
        if http.is_none() && !cgi {
            http = get("HTTP_PROXY");
        }
        let mut https = lower("https_proxy").or_else(|| get("HTTPS_PROXY"));
        if http.is_none() || https.is_none() {
            let all = lower("all_proxy").or_else(|| get("ALL_PROXY"));
            http = http.or_else(|| all.clone());
            https = https.or(all);
        }
        if http.is_none() && https.is_none() {
            return None;
        }
        let bypass = case_sensitive_names
            .then(|| lookup("no_proxy"))
            .flatten()
            .or_else(|| lookup("NO_PROXY"))
            .map(|list| {
                list.split(',')
                    .map(str::trim)
                    .filter(|entry| !entry.is_empty())
                    .map(str::to_owned)
                    .collect()
            })
            .unwrap_or_default();
        Some(Self {
            http,
            https,
            bypass,
        })
    }

    /// From this process's environment.
    #[must_use]
    pub fn from_process() -> Option<Self> {
        Self::from_lookup(
            |name| std::env::var(name).ok().filter(|v| !v.is_empty()),
            cfg!(not(windows)),
        )
    }

    /// Whether `host` (as `Uri.Host` gives it) is in `NO_PROXY`: an entry
    /// with a leading dot matches that domain itself and every name under
    /// it, any other entry only itself, ignoring case; no wildcards.
    fn is_bypassed(&self, host: &str) -> bool {
        self.bypass.iter().any(|entry| {
            if let Some(domain) = entry.strip_prefix('.') {
                domain.eq_ignore_ascii_case(host)
                    || host
                        .len()
                        .checked_sub(entry.len())
                        .and_then(|at| host.get(at..))
                        .is_some_and(|tail| tail.eq_ignore_ascii_case(entry))
            } else {
                entry.eq_ignore_ascii_case(host)
            }
        })
    }

    fn route(&self, target: &Target<'_>) -> Route {
        let entry = if target.is_secure() {
            self.https.as_ref()
        } else {
            self.http.as_ref()
        };
        match entry {
            Some(server) if !self.is_bypassed(&target.uri_host()) => {
                Ok(Proxies::single(server.clone()))
            }
            _ => Ok(Proxies::direct()),
        }
    }
}

/// The proxy this machine's settings name for a request: the environment
/// when it names one, else (Windows) the user's proxy settings. Cheap to
/// clone; clones share what they remember.
#[derive(Clone)]
pub struct SystemProxy {
    inner: Arc<Source>,
}

enum Source {
    /// No proxy.
    Direct,
    Environment(EnvironmentProxy),
    #[cfg(windows)]
    Windows(windows::WindowsProxy),
    Fixed(ProxyServer),
    Listed(String, Arc<list::FailedProxies>),
}

impl fmt::Debug for SystemProxy {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let kind = match &*self.inner {
            Source::Direct => "direct",
            Source::Environment(_) => "environment",
            #[cfg(windows)]
            Source::Windows(_) => "windows settings",
            Source::Fixed(_) => "fixed",
            Source::Listed(..) => "listed",
        };
        f.debug_struct("SystemProxy")
            .field("source", &kind)
            .finish()
    }
}

impl SystemProxy {
    /// Read once, as .NET reads `DefaultProxy` once per process: the
    /// environment, else (Windows) the user's settings (read again for each
    /// request), else direct (.NET's default on Linux and macOS).
    #[must_use]
    pub fn detect() -> Self {
        let source = match EnvironmentProxy::from_process() {
            Some(environment) => Source::Environment(environment),
            #[cfg(windows)]
            None => Source::Windows(windows::WindowsProxy::default()),
            #[cfg(not(windows))]
            None => Source::Direct,
        };
        Self {
            inner: Arc::new(source),
        }
    }

    /// Never a proxy.
    #[must_use]
    pub fn direct() -> Self {
        Self {
            inner: Arc::new(Source::Direct),
        }
    }

    /// Always `server`, except for this machine (tests and diagnostics).
    #[must_use]
    pub fn fixed(server: ProxyServer) -> Self {
        Self {
            inner: Arc::new(Source::Fixed(server)),
        }
    }

    /// The Windows proxy list `list` as the manual settings name it, on
    /// every system, with no bypass list and no automatic configuration
    /// (tests and diagnostics): its proxies in order, one that could not be
    /// connected to skipped by later requests, as with the user's settings.
    #[must_use]
    pub fn listed(list: &str) -> Self {
        Self {
            inner: Arc::new(Source::Listed(list.to_owned(), Arc::default())),
        }
    }

    /// Where a request for `target` goes. This machine's own addresses are
    /// always reached directly. On Windows the user's automatic
    /// configuration may look up and run a script, so this can take a
    /// while: call it where the request's deadline is watched.
    pub fn route(&self, target: &Target<'_>) -> Route {
        if target.is_loopback() {
            return Ok(Proxies::direct());
        }
        match &*self.inner {
            Source::Direct => Ok(Proxies::direct()),
            Source::Environment(environment) => environment.route(target),
            #[cfg(windows)]
            Source::Windows(settings) => settings.route(target),
            Source::Fixed(server) => Ok(Proxies::single(server.clone())),
            Source::Listed(list, failed) => Ok(Proxies::manual(list, target.is_secure(), failed)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn proxy(kind: ProxyKind, host: &str, port: u16) -> Option<ProxyServer> {
        Some(ProxyServer {
            kind,
            host: host.to_owned(),
            port,
            credentials: None,
        })
    }

    fn http(host: &str, port: u16) -> Option<ProxyServer> {
        proxy(ProxyKind::Http, host, port)
    }

    /// The first proxy a route tries.
    fn first(route: Route) -> Result<Option<ProxyServer>, String> {
        route.map(|proxies| proxies.servers().into_iter().next())
    }

    fn https_target(host: &str) -> Target<'_> {
        Target::new("https", host, "https://example/")
    }

    fn named(domain: Option<&str>, user: &str, password: &str) -> Option<Credentials> {
        Some(Credentials::Named {
            domain: domain.map(str::to_owned),
            user: user.to_owned(),
            password: password.to_owned(),
        })
    }

    // .NET 10 `HttpEnvironmentProxy.GetUriFromString`: a host, a host and
    // port, an `http` address, IPv6, `https` (port 443) and SOCKS (port 80,
    // as every scheme but `https`); a port that is not a number, or a scheme
    // .NET does not know, is no proxy.
    #[test]
    fn proxy_addresses_parse_as_dotnet_reads_them() {
        assert_eq!(parse_proxy("proxy.local"), http("proxy.local", 80));
        assert_eq!(parse_proxy("proxy.local:3128"), http("proxy.local", 3128));
        assert_eq!(
            parse_proxy("http://proxy.local:8080/"),
            http("proxy.local", 8080)
        );
        assert_eq!(parse_proxy("HTTP://[::1]:8080"), http("[::1]", 8080));
        assert_eq!(parse_proxy("http://[::1]"), http("[::1]", 80));
        assert_eq!(
            parse_proxy("socks5://proxy.local"),
            proxy(ProxyKind::Socks5, "proxy.local", 80)
        );
        assert_eq!(
            parse_proxy("SOCKS4A://proxy.local:9050"),
            proxy(ProxyKind::Socks4a, "proxy.local", 9050)
        );
        assert_eq!(
            parse_proxy("socks4://proxy.local:1080"),
            proxy(ProxyKind::Socks4, "proxy.local", 1080)
        );
        assert_eq!(
            parse_proxy("https://proxy.local"),
            proxy(ProxyKind::Https, "proxy.local", 443)
        );
        assert_eq!(parse_proxy("ftp://proxy.local"), None);
        assert_eq!(parse_proxy("socks5h://proxy.local:1080"), None);
        assert_eq!(parse_proxy("http://proxy.local/path"), None);
        assert_eq!(parse_proxy("proxy.local:port"), None);
        assert_eq!(parse_proxy("proxy.local:99999"), None);
        assert_eq!(parse_proxy(""), None);
    }

    // .NET `GetCredentialsFromString`: decoded, split at the first `:`, a
    // domain before a backslash; `:@` (user information `:`) is the logon;
    // a user alone has an empty password.
    #[test]
    fn credentials_come_from_the_addresses_user_information() {
        let creds = |text: &str| parse_proxy(text).unwrap().credentials;
        assert_eq!(
            creds("http://me%40home:p%3Ass@proxy.local:8080"),
            named(None, "me@home", "p:ss")
        );
        assert_eq!(
            creds("http://CORP%5Cme:secret@proxy:8080"),
            named(Some("CORP"), "me", "secret")
        );
        assert_eq!(
            creds("CORP\\me:se:cret@proxy:8080"),
            named(Some("CORP"), "me", "se:cret")
        );
        assert_eq!(creds("http://me@proxy:8080"), named(None, "me", ""));
        assert_eq!(creds("http://:@proxy:8080"), Some(Credentials::Logon));
        assert_eq!(creds("http://@proxy:8080"), None);
        assert_eq!(creds("http://proxy:8080"), None);
    }

    fn environment(pairs: &[(&str, &str)], case_sensitive: bool) -> Option<EnvironmentProxy> {
        let pairs: Vec<(String, String)> = pairs
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        EnvironmentProxy::from_lookup(
            move |name| {
                pairs
                    .iter()
                    .find(|(k, _)| k == name)
                    .map(|(_, v)| v.clone())
            },
            case_sensitive,
        )
    }

    // .NET (`HttpEnvironmentProxy.Unix`): lower case first; ALL_PROXY
    // fills a missing scheme; nothing set is no environment proxy (the
    // system settings are read instead).
    #[test]
    fn the_environment_names_a_proxy_per_scheme() {
        assert_eq!(environment(&[], true), None);
        assert_eq!(environment(&[("NO_PROXY", "x")], true), None);
        let env = environment(
            &[
                ("https_proxy", "lower:1"),
                ("HTTPS_PROXY", "upper:2"),
                ("ALL_PROXY", "all:3"),
            ],
            true,
        )
        .unwrap();
        assert_eq!(
            first(env.route(&https_target("example.com"))),
            Ok(http("lower", 1))
        );
        assert_eq!(env.http, http("all", 3));
        let only_http = environment(&[("HTTP_PROXY", "plain:8080")], true).unwrap();
        assert_eq!(
            first(only_http.route(&https_target("example.com"))),
            Ok(None)
        );
        // Windows reads the upper-case names only (its names ignore case).
        let windows = environment(&[("https_proxy", "lower:1")], false);
        assert_eq!(windows, None);
    }

    // .NET: `HTTP_PROXY` is not read while `GATEWAY_INTERFACE` is set (in a
    // CGI script it is a request header); `HTTPS_PROXY` still is.
    #[test]
    fn a_cgi_environment_ignores_http_proxy() {
        let env = environment(
            &[
                ("GATEWAY_INTERFACE", "CGI/1.1"),
                ("HTTP_PROXY", "plain:8080"),
                ("HTTPS_PROXY", "secure:8443"),
            ],
            false,
        )
        .unwrap();
        assert_eq!(env.http, None);
        assert_eq!(env.https, http("secure", 8443));
    }

    // .NET 10 `IsMatchInBypassList`: a leading dot matches the domain itself
    // and the names under it; no dot is an exact match; case is ignored.
    #[test]
    fn no_proxy_follows_the_dot_rule() {
        let env = environment(
            &[
                ("HTTPS_PROXY", "proxy:8080"),
                ("NO_PROXY", " .example.com , Exact.org,"),
            ],
            false,
        )
        .unwrap();
        assert_eq!(first(env.route(&https_target("www.example.com"))), Ok(None));
        assert_eq!(first(env.route(&https_target("example.com"))), Ok(None));
        assert!(
            first(env.route(&https_target("badexample.com")))
                .unwrap()
                .is_some()
        );
        assert_eq!(first(env.route(&https_target("exact.ORG"))), Ok(None));
        assert!(
            first(env.route(&https_target("www.exact.org")))
                .unwrap()
                .is_some()
        );
    }

    // `ws` takes the HTTP proxy and `wss` the HTTPS one, as `http` and
    // `https` do (.NET `IsSupportedNonSecureScheme`).
    #[test]
    fn web_socket_addresses_take_their_schemes_proxy() {
        let env = environment(
            &[("HTTP_PROXY", "plain:1"), ("HTTPS_PROXY", "secure:2")],
            false,
        )
        .unwrap();
        assert_eq!(
            first(env.route(&Target::new("ws", "example.com", "ws://example.com/"))),
            Ok(http("plain", 1))
        );
        assert_eq!(
            first(env.route(&Target::new("wss", "example.com", "wss://example.com/"))),
            Ok(http("secure", 2))
        );
    }

    #[test]
    fn all_proxy_may_name_a_socks_proxy() {
        let env = environment(&[("ALL_PROXY", "socks5://proxy:1080")], false).unwrap();
        assert_eq!(
            first(env.route(&https_target("example.com"))),
            Ok(proxy(ProxyKind::Socks5, "proxy", 1080))
        );
    }

    #[test]
    fn this_machine_is_never_proxied() {
        let proxy = SystemProxy::fixed(ProxyServer {
            kind: ProxyKind::Http,
            host: "proxy".into(),
            port: 1,
            credentials: None,
        });
        for host in ["127.0.0.1", "LOCALHOST", "[::1]", "::1"] {
            assert_eq!(first(proxy.route(&https_target(host))), Ok(None), "{host}");
        }
        assert!(
            first(proxy.route(&https_target("example.com")))
                .unwrap()
                .is_some()
        );
        assert_eq!(
            first(SystemProxy::direct().route(&https_target("example.com"))),
            Ok(None)
        );
    }

    // A manual Windows list: every entry for the scheme, in order, as HTTP
    // proxies; never for this machine.
    #[test]
    fn a_listed_proxy_gives_every_entry_for_the_scheme() {
        let proxy = SystemProxy::listed("http=web:80;socks=sock:1080;one:1;https=two:2");
        let servers = |target: Target<'_>| {
            proxy
                .route(&target)
                .unwrap()
                .servers()
                .iter()
                .map(ToString::to_string)
                .collect::<Vec<_>>()
        };
        assert_eq!(servers(https_target("example.com")), ["one:1", "two:2"]);
        assert_eq!(
            servers(Target::new("ws", "example.com", "ws://example.com/")),
            ["web:80", "one:1"]
        );
        assert!(servers(https_target("127.0.0.1")).is_empty());
    }
}
