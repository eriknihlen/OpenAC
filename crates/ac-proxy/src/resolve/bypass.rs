//! A Windows bypass list (the proxy settings' "don't use the proxy server
//! for addresses beginning with", `ProxyOverride`) as .NET's
//! `HttpWindowsProxy` reads and applies it to the manual proxy.
//!
//! Each entry runs to a space, `;` or `]` (anything after it up to the next
//! `;` is dropped), after leading spaces, an `http://` or `https://` prefix
//! and a `[` are taken off; `*` matches any run of characters, case
//! ignored. `<local>` matches a name with no dot in it, and this machine's
//! own addresses: the caller hands over this machine's interface addresses
//! when they are needed (a host that is an address or has no dot). Only the
//! Windows settings carry such a list, so only the Windows route asks for
//! them (`GetAdaptersAddresses`); on other systems nothing does.

// Read only by the Windows settings; the tests run everywhere.
#![cfg_attr(not(windows), allow(dead_code))]

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};

/// One of this machine's interface addresses, with an IPv6 address's scope
/// (its interface index; 0 for none). .NET compares both.
pub(crate) type InterfaceAddress = (IpAddr, u32);

/// A bypass list, read.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct BypassList {
    /// The wildcard entries, in order.
    entries: Vec<String>,
    /// Whether `<local>` was named.
    local: bool,
}

/// Whether `text` at `at` begins with `prefix`, ASCII case ignored (.NET
/// `string.Compare(..., StringComparison.OrdinalIgnoreCase) == 0` over the
/// prefix's length).
fn starts_with_at(text: &[u8], at: usize, prefix: &str) -> bool {
    text.get(at..at + prefix.len())
        .is_some_and(|head| head.eq_ignore_ascii_case(prefix.as_bytes()))
}

impl BypassList {
    /// .NET `HttpWindowsProxy.UpdateConfiguration`'s reading of the list.
    pub(crate) fn parse(list: &str) -> Self {
        let text = list.as_bytes();
        let length = text.len();
        let mut at = 0;
        let mut out = Self::default();
        while at < length {
            while at < length && text[at] == b' ' {
                at += 1;
            }
            if starts_with_at(text, at, "http://") {
                at += 7;
            } else if starts_with_at(text, at, "https://") {
                at += 8;
            }
            if at < length && text[at] == b'[' {
                at += 1;
            }
            let start = at;
            while at < length && !matches!(text[at], b' ' | b';' | b']') {
                at += 1;
            }
            let entry = if at == start {
                None
            } else if starts_with_at(text, start, "<local>") {
                out.local = true;
                None
            } else {
                // `start` and `at` sit beside ASCII bytes (or the ends), so
                // they are character boundaries.
                Some(list[start..at].to_owned())
            };
            while at < length && text[at] != b';' {
                at += 1;
            }
            if at < length {
                at += 1;
            }
            out.entries.extend(entry);
        }
        out
    }

    /// Whether a request for `host` goes direct (.NET
    /// `HttpWindowsProxy.GetMultiProxy`, manual settings). `<local>` only
    /// counts when this machine has an address; `interfaces` gives them
    /// and is called only when they decide the answer.
    pub(crate) fn is_bypassed<E>(
        &self,
        host: &str,
        interfaces: impl FnOnce() -> Result<Vec<InterfaceAddress>, E>,
    ) -> Result<bool, E> {
        let host = Host::read(host);
        if self.local && (host.address.is_some() || !host.text.contains('.')) {
            let own = interfaces()?;
            if !own.is_empty() {
                if host.is_loopback() {
                    return Ok(true);
                }
                if host.address.is_some_and(|address| own.contains(&address)) {
                    return Ok(true);
                }
                if !host.is_ipv6() && !host.text.contains('.') {
                    return Ok(true);
                }
            }
        }
        Ok(self
            .entries
            .iter()
            .any(|entry| star_match(&host.text, entry)))
    }
}

/// A request's host as .NET's `Uri` gives it to the list.
struct Host {
    /// `Uri.IdnHost`: an IPv6 address without brackets, compressed in
    /// lower case, its zone as written; `loopback` read as `localhost`.
    text: String,
    /// The host as an address (`IPAddress.TryParse(IdnHost)`).
    address: Option<InterfaceAddress>,
}

impl Host {
    fn read(host: &str) -> Self {
        let bare = host.trim_start_matches('[').trim_end_matches(']');
        let (part, zone) = match bare.split_once('%') {
            Some((part, zone)) => (part, Some(zone)),
            None => (bare, None),
        };
        if let Ok(v6) = part.parse::<Ipv6Addr>() {
            // A zone of decimal digits that fits is the scope; any other
            // reads as none (.NET 10.0.8, by reflection).
            let scope = zone
                .filter(|zone| !zone.is_empty() && zone.bytes().all(|b| b.is_ascii_digit()))
                .and_then(|zone| zone.parse::<u32>().ok())
                .unwrap_or(0);
            let text = match zone {
                Some(zone) => format!("{v6}%{zone}"),
                None => v6.to_string(),
            };
            return Self {
                text,
                address: Some((IpAddr::V6(v6), scope)),
            };
        }
        if zone.is_none()
            && let Ok(v4) = part.parse::<Ipv4Addr>()
        {
            return Self {
                text: v4.to_string(),
                address: Some((IpAddr::V4(v4), 0)),
            };
        }
        let text = if bare.eq_ignore_ascii_case("loopback") {
            "localhost".to_owned()
        } else {
            bare.to_owned()
        };
        Self {
            text,
            address: None,
        }
    }

    fn is_ipv6(&self) -> bool {
        matches!(self.address, Some((IpAddr::V6(_), _)))
    }

    /// .NET `Uri.IsLoopback`: `localhost` (and `loopback`), `127.0.0.0/8`,
    /// `::1`, and `127.0.0.0/8` mapped into IPv6.
    fn is_loopback(&self) -> bool {
        match self.address {
            Some((IpAddr::V4(v4), _)) => v4.is_loopback(),
            Some((IpAddr::V6(v6), _)) => {
                v6.is_loopback() || v6.to_ipv4_mapped().is_some_and(|v4| v4.is_loopback())
            }
            None => self.text.eq_ignore_ascii_case("localhost"),
        }
    }
}

/// .NET's invariant upper case of one character: the single-character
/// upper case, except that `ı` (dotless i) stays itself. (Rust's mapping
/// also differs for a few Greek and Unicode 16 letters, which a .NET host,
/// in its ASCII IDN form, never holds.)
fn upper_invariant(c: char) -> char {
    if c == '\u{131}' {
        return c;
    }
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(single), None) => single,
        _ => c,
    }
}

/// .NET `SimpleRegex.IsMatchWithStarWildcard`: `*` matches any run of
/// characters, the rest one character each, case ignored.
fn star_match(input: &str, pattern: &str) -> bool {
    let input: Vec<char> = input.chars().collect();
    let pattern: Vec<char> = pattern.chars().collect();
    let (mut at, mut star_at, mut p, mut after_star) = (0, None::<usize>, 0, None::<usize>);
    while at < input.len() {
        if pattern.get(p) == Some(&'*') {
            star_at = Some(at);
            p += 1;
            after_star = Some(p);
            continue;
        }
        if let Some(&c) = pattern.get(p)
            && (c == input[at] || upper_invariant(c) == upper_invariant(input[at]))
        {
            at += 1;
            p += 1;
            continue;
        }
        let (Some(star), Some(resume)) = (star_at, after_star) else {
            return false;
        };
        at = star + 1;
        star_at = Some(at);
        p = resume;
    }
    while pattern.get(p) == Some(&'*') {
        p += 1;
    }
    p == pattern.len()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A machine with a LAN address, a link-local IPv6 address on
    /// interface 4, and the loopback interface.
    fn machine() -> Result<Vec<InterfaceAddress>, ()> {
        Ok(vec![
            ("192.168.0.10".parse().unwrap(), 0),
            ("fe80::1:2:3:4".parse().unwrap(), 4),
            ("::1".parse().unwrap(), 0),
            ("127.0.0.1".parse().unwrap(), 0),
        ])
    }

    fn bypassed(list: &str, host: &str) -> bool {
        BypassList::parse(list).is_bypassed(host, machine).unwrap()
    }

    fn entries(list: &str) -> Vec<String> {
        BypassList::parse(list).entries
    }

    // The entries .NET 10.0.8's `HttpWindowsProxy` reads (its `_bypass`
    // and whether `<local>` was named, by reflection, 2026-10-15): spaces
    // before an entry and `http://`, `https://` and `[` prefixes dropped;
    // an entry ends at a space, `;` or `]`, the rest to the next `;`
    // dropped; a tab is part of an entry; a port is kept.
    #[test]
    fn a_list_is_read_as_dotnet_reads_it() {
        assert_eq!(
            entries("http://www.example.com;https://secure.example.com"),
            ["www.example.com", "secure.example.com"]
        );
        assert_eq!(
            entries("[::1];[fe80::1]:8080;[2001:db8::5]"),
            ["::1", "fe80::1", "2001:db8::5"]
        );
        assert_eq!(entries("a b;c"), ["a", "c"]);
        assert_eq!(
            entries("  spaced.example  ;\ttab.example"),
            ["spaced.example", "\ttab.example"]
        );
        assert_eq!(entries("host:8080"), ["host:8080"]);
        assert_eq!(entries("HTTP://Upper.Example"), ["Upper.Example"]);
        assert_eq!(entries("http://[2001:db8::7]"), ["2001:db8::7"]);
        assert!(entries(";;;").is_empty());
        assert!(entries("] x.example").is_empty());
        assert_eq!(entries("x.example]y;z.example"), ["x.example", "z.example"]);
        assert_eq!(
            entries("http://https://double.example"),
            ["https://double.example"]
        );
        assert_eq!(
            entries("[http://bracket.example"),
            ["http://bracket.example"]
        );
        assert_eq!(entries("a,b.example"), ["a,b.example"]);
        for list in ["<LOCAL>", "<local>junk", "*.corp.example;10.*;<local>"] {
            assert!(BypassList::parse(list).local, "{list}");
        }
        assert_eq!(
            entries("*.corp.example;10.*;<local>"),
            ["*.corp.example", "10.*"]
        );
        assert!(!BypassList::parse("<loc").local);
    }

    // Which requests .NET 10.0.8's `HttpWindowsProxy.GetMultiProxy` sends
    // direct for each list (by reflection, 2026-10-15).
    #[test]
    fn requests_go_direct_as_dotnet_sends_them() {
        let list = "*.corp.example;10.*;<local>";
        for host in [
            "intranet",
            "build.corp.example",
            "10.1.2.3",
            "10.9.9.9",
            "a",
            "abxc",
        ] {
            assert!(bypassed(list, host), "{host}");
        }
        for host in ["corp.example", "github.com", "[fe80::1]", "[2001:db8::5]"] {
            assert!(!bypassed(list, host), "{host}");
        }
        assert!(bypassed("http://www.example.com", "www.example.com"));
        assert!(bypassed("[fe80::1]:8080", "[fe80::1]"));
        assert!(bypassed("[::1];[2001:db8::5]", "[2001:db8::5]"));
        assert!(bypassed("[2001:db8::5]", "2001:DB8:0:0:0:0:0:5"));
        assert!(!bypassed("[2001:db8::5]", "[2001:db8::9]"));
        assert!(bypassed("a b;c", "a") && bypassed("a b;c", "c"));
        assert!(!bypassed("a b;c", "b"));
        assert!(!bypassed("  spaced.example  ;\ttab.example", "tab.example"));
        assert!(!bypassed("host:8080", "host"));
        assert!(bypassed("HTTP://Upper.Example", "upper.example"));
        assert!(!bypassed("http://https://double.example", "double.example"));
        assert!(!bypassed("[http://bracket.example", "bracket.example"));
        assert!(!bypassed(";;;", "a"));
        for host in ["github.com", "[2001:db8::7]", "intranet", "10.1.2.3"] {
            assert!(bypassed("*", host), "{host}");
        }
        assert!(bypassed("a*b*c", "abxc"));
        assert!(!bypassed("a*b*c", "ac"));
        assert!(bypassed("*.example.*", "www.example.org"));
        assert!(!bypassed("*.example.*", "github.com"));
        // `ı` (dotless i) is not `I` in .NET's invariant casing.
        assert!(!bypassed("\u{131}ntranet", "intranet"));
        assert!(bypassed("\u{17f}ecure.example.com", "secure.example.com"));
        assert!(!bypassed("", "a"));
    }

    // `<local>` (.NET 10.0.8, this machine's own addresses by reflection,
    // 2026-10-15): a name without a dot; this machine's IPv4 addresses and
    // loopback; a link-local IPv6 address only with its interface's scope,
    // which a URL's `%25` zone never gives (`%254` reads as scope 254).
    #[test]
    fn local_names_this_machine_and_names_without_a_dot() {
        for host in [
            "intranet",
            "junk",
            "192.168.0.10",
            "127.0.0.1",
            "127.0.0.2",
            "[::1]",
            "::ffff:127.0.0.1",
            "localhost",
            "LOOPBACK",
            "fe80::1:2:3:4%4",
        ] {
            assert!(bypassed("<local>", host), "{host}");
        }
        for host in [
            "github.com",
            "192.168.1.99",
            "10.9.9.9",
            "[fe80::1:2:3:4]",
            "fe80::1:2:3:4%254",
            "fe80::1:2:3:4%4a",
            "[2001:db8::9]",
            "localhost.",
        ] {
            assert!(!bypassed("<local>", host), "{host}");
        }
    }

    // .NET keeps `_localIp` only when the machine has an address: with
    // none, `<local>` matches nothing. The addresses are asked for only
    // when they decide the answer, and a failure to read them is passed on.
    #[test]
    fn this_machines_addresses_are_read_only_when_needed() {
        let list = BypassList::parse("<local>;*.example");
        assert_eq!(
            list.is_bypassed("intranet", || Ok::<_, ()>(Vec::new())),
            Ok(false)
        );
        assert_eq!(
            list.is_bypassed("www.example", || -> Result<_, ()> {
                panic!("a dotted name needs no addresses")
            }),
            Ok(true)
        );
        assert_eq!(
            list.is_bypassed("github.com", || -> Result<_, ()> {
                panic!("a dotted name needs no addresses")
            }),
            Ok(false)
        );
        assert_eq!(list.is_bypassed("intranet", || Err(5)), Err(5));
        assert_eq!(
            BypassList::parse("*")
                .is_bypassed("intranet", || -> Result<_, ()> { panic!("no <local>") }),
            Ok(true)
        );
    }

    // .NET 10.0.8 `IPAddress.TryParse` of a zone, and `Uri.IdnHost`'s IPv6
    // form, by reflection (2026-10-15).
    #[test]
    fn a_host_reads_as_uri_gives_it() {
        let read = |host: &str| {
            let host = Host::read(host);
            (host.text, host.address.map(|(_, scope)| scope))
        };
        assert_eq!(read("[::1]"), ("::1".to_owned(), Some(0)));
        assert_eq!(read("2001:DB8::5"), ("2001:db8::5".to_owned(), Some(0)));
        assert_eq!(
            read("[1:0:0:2:0:0:0:3]"),
            ("1:0:0:2::3".to_owned(), Some(0))
        );
        assert_eq!(read("::ffff:1.2.3.4").0, "::ffff:1.2.3.4");
        assert_eq!(
            read("fe80::1%2512"),
            ("fe80::1%2512".to_owned(), Some(2512))
        );
        assert_eq!(read("fe80::1%4294967295").1, Some(4_294_967_295));
        for zone in ["4294967296", "12a", "-1", "+1", ""] {
            assert_eq!(read(&format!("fe80::1%{zone}")).1, Some(0), "{zone}");
        }
        assert_eq!(read("fe80::1%012").1, Some(12));
        assert_eq!(read("Intra"), ("Intra".to_owned(), None));
        assert_eq!(read("loopback"), ("localhost".to_owned(), None));
    }
}
