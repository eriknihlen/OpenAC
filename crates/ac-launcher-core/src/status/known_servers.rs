//! The public servers TreeStats lists, with the player counts it reports,
//! for Add server. Each successful fetch is saved in the cache folder, so
//! the list still opens offline; with nothing saved and no network there is
//! no list. Odd values in the documents are read or left out, never fatal.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use ac_host_core::json::{self as host_json, Member, Node, Value};

use super::health::POPULATION_URI;
use crate::cancel::{CancelToken, Cancelled};
use crate::net::{self, HttpError, HttpRequest, HttpTransport};
use crate::url::WebUrl;

/// The server list TreeStats publishes.
pub const SERVERS_URI: &str = "https://treestats.net/servers.json";

/// The largest document read.
const MAXIMUM_BYTES: u64 = 1_048_576;
/// How long one download may take.
const FETCH_TIMEOUT: Duration = Duration::from_secs(8);
/// A list fetched this recently is reused without another fetch.
const REUSE_FOR: Duration = Duration::from_secs(10 * 60);
/// The saved server list's file in the cache folder.
const SERVERS_CACHE_FILE: &str = "known-servers.json";
/// The saved player counts' file in the cache folder.
const COUNTS_CACHE_FILE: &str = "known-servers-players.json";

/// A public server from the TreeStats list, with its latest reported
/// player count.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KnownServer {
    /// Its name.
    pub name: String,
    /// Its host.
    pub host: String,
    /// Its port, 1 to 65535.
    pub port: u16,
    /// PvE, PvP, ..., when given.
    pub server_type: Option<String>,
    /// The server software, when given.
    pub software: Option<String>,
    /// Its description, when given.
    pub description: Option<String>,
    /// Its website: an absolute `http` or `https` address.
    pub website: Option<WebUrl>,
    /// Its Discord: an absolute `http` or `https` address.
    pub discord: Option<WebUrl>,
    /// Its latest reported player count.
    pub player_count: Option<i32>,
}

/// The known servers, and whether they came from the network or from the
/// copy saved last time.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KnownServerList {
    /// The servers, sorted by name ignoring case.
    pub servers: Vec<KnownServer>,
    /// The saved copy (the network failed).
    pub is_from_cache: bool,
    /// When it was fetched, or saved for the copy.
    pub saved_at: SystemTime,
}

/// The current time.
pub type KnownServerClock = Arc<dyn Fn() -> SystemTime + Send + Sync>;

/// Fetches and saves the known server list.
pub struct KnownServerCatalog {
    transport: Arc<dyn HttpTransport>,
    cache_directory: PathBuf,
    clock: KnownServerClock,
    last: Mutex<Option<KnownServerList>>,
}

impl std::fmt::Debug for KnownServerCatalog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("KnownServerCatalog")
            .field("cache_directory", &self.cache_directory)
            .finish_non_exhaustive()
    }
}

impl KnownServerCatalog {
    /// A catalog saving its copies in `cache_directory`.
    pub fn new(transport: Arc<dyn HttpTransport>, cache_directory: &Path) -> Self {
        Self {
            transport,
            cache_directory: cache_directory.to_path_buf(),
            clock: Arc::new(SystemTime::now),
            last: Mutex::new(None),
        }
    }

    /// The same catalog on another clock (tests).
    #[must_use]
    pub fn with_clock(mut self, clock: KnownServerClock) -> Self {
        self.clock = clock;
        self
    }

    fn servers_cache_path(&self) -> PathBuf {
        self.cache_directory.join(SERVERS_CACHE_FILE)
    }

    fn counts_cache_path(&self) -> PathBuf {
        self.cache_directory.join(COUNTS_CACHE_FILE)
    }

    /// The list, fetched at most every ten minutes; the saved copy when
    /// the network fails; `None` when neither is available. The list and
    /// the player counts download together.
    pub fn load(&self, cancel: &CancelToken) -> Result<Option<KnownServerList>, Cancelled> {
        let mut last = self
            .last
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let now = (self.clock)();
        if let Some(recent) = last.as_ref().filter(|recent| !recent.is_from_cache)
            && now.duration_since(recent.saved_at).unwrap_or_default() < REUSE_FOR
        {
            return Ok(Some(recent.clone()));
        }
        let (servers, counts) = std::thread::scope(|scope| {
            let counts = scope.spawn(|| {
                self.try_fetch(POPULATION_URI, &self.counts_cache_path(), is_array, cancel)
            });
            let servers = self.try_fetch(
                SERVERS_URI,
                &self.servers_cache_path(),
                is_server_list,
                cancel,
            );
            // A panic in the download thread counts as a failed download.
            let counts = counts.join().unwrap_or(Ok(None));
            (servers, counts)
        });
        let (servers, counts) = (servers?, counts?);
        let from_cache = servers.is_none();
        let servers = servers.or_else(|| read_cache(&self.servers_cache_path()));
        let counts = counts.or_else(|| read_cache(&self.counts_cache_path()));
        let Some(servers) = servers.filter(|servers| is_server_list(servers)) else {
            return Ok(last.clone());
        };
        let saved_at = if from_cache {
            cache_time(&self.servers_cache_path()).unwrap_or(now)
        } else {
            now
        };
        let list = KnownServerList {
            servers: parse(&servers, counts.as_deref()),
            is_from_cache: from_cache,
            saved_at,
        };
        *last = Some(list.clone());
        Ok(Some(list))
    }

    /// Downloads a document and saves it for offline use, but only when
    /// `usable` accepts it; `None` when the download fails or is refused.
    fn try_fetch(
        &self,
        uri: &str,
        cache: &Path,
        usable: fn(&str) -> bool,
        cancel: &CancelToken,
    ) -> Result<Option<String>, Cancelled> {
        let Some(url) = WebUrl::parse(uri) else {
            return Ok(None);
        };
        let mut response = match self.transport.get(
            &HttpRequest {
                url,
                timeout: Some(FETCH_TIMEOUT),
            },
            cancel,
        ) {
            Ok(response) => response,
            Err(HttpError::Cancelled) => return Err(Cancelled),
            Err(_) if cancel.is_cancelled() => return Err(Cancelled),
            Err(_) => return Ok(None),
        };
        if !(200..300).contains(&response.status) {
            return Ok(None);
        }
        let body = match net::read_bounded(&mut response, MAXIMUM_BYTES, "too large", cancel) {
            Ok(body) => body,
            Err(_) if cancel.is_cancelled() => return Err(Cancelled),
            Err(_) => return Ok(None),
        };
        let text = String::from_utf8_lossy(&body).into_owned();
        if !usable(&text) {
            return Ok(None);
        }
        write_cache(cache, &text);
        Ok(Some(text))
    }
}

/// The servers in a `servers.json`, sorted by name ignoring case. An entry
/// without a name, a host or a usable port, or that is not an object, is
/// left out; a field of an unexpected kind is read as missing. Player counts
/// come from a `player_counts` document when it names the server, and from
/// the server's own entry otherwise. A document that is not a JSON array
/// gives no servers.
pub fn parse(servers_json: &str, player_counts_json: Option<&str>) -> Vec<KnownServer> {
    let counts = player_counts_json
        .map(parse_player_counts)
        .unwrap_or_default();
    let mut servers = Vec::new();
    for item in array_items(servers_json) {
        let Value::Object(members) = &item.value else {
            continue;
        };
        let (Some(name), Some(host), Some(port)) = (
            text(members, "name"),
            text(members, "host"),
            whole(members, "port").and_then(|port| u16::try_from(port).ok()),
        ) else {
            continue;
        };
        if port == 0 {
            continue;
        }
        let players = match counts.iter().find(|(server, _)| same_name(server, &name)) {
            Some((_, count)) => Some(*count),
            None => match find(members, "players").map(|node| &node.value) {
                Some(Value::Object(reported)) => {
                    whole(reported, "count").filter(|&count| count >= 0)
                }
                _ => None,
            },
        };
        servers.push(KnownServer {
            server_type: text(members, "type"),
            software: text(members, "software"),
            description: text(members, "description"),
            website: link(members, "website_url"),
            discord: link(members, "discord_url"),
            player_count: players,
            name,
            host,
            port,
        });
    }
    // Ordinal, ignoring case: both sides upper-cased.
    servers.sort_by_key(|server| server.name.to_uppercase());
    servers
}

/// Server name to player count from a `player_counts` document, with the
/// same tolerance; a later entry for a name replaces an earlier one.
fn parse_player_counts(json: &str) -> Vec<(String, i32)> {
    let mut counts: Vec<(String, i32)> = Vec::new();
    for item in array_items(json) {
        let Value::Object(members) = &item.value else {
            continue;
        };
        let (Some(server), Some(count)) = (
            text(members, "server"),
            whole(members, "count").filter(|&count| count >= 0),
        ) else {
            continue;
        };
        match counts
            .iter_mut()
            .find(|(known, _)| same_name(known, &server))
        {
            Some(entry) => entry.1 = count,
            None => counts.push((server, count)),
        }
    }
    counts
}

/// Names compared ordinally, ignoring case.
fn same_name(left: &str, right: &str) -> bool {
    left.to_uppercase() == right.to_uppercase()
}

/// The items of a JSON array, or none when the text is not one.
fn array_items(json: &str) -> Vec<Node> {
    match host_json::parse(json.as_bytes()) {
        Ok(Node {
            value: Value::Array(items),
            ..
        }) => items,
        _ => Vec::new(),
    }
}

/// Only a list with at least one usable server replaces the saved copy.
fn is_server_list(json: &str) -> bool {
    !parse(json, None).is_empty()
}

fn is_array(json: &str) -> bool {
    matches!(
        host_json::parse(json.as_bytes()),
        Ok(Node {
            value: Value::Array(_),
            ..
        })
    )
}

/// The member called `name`; of repeated members, the last (as .NET reads them).
fn find<'a>(members: &'a [Member], name: &str) -> Option<&'a Node> {
    members
        .iter()
        .rev()
        .find(|member| member.name == name)
        .map(|member| &member.value)
}

/// A non-blank string, trimmed.
fn text(members: &[Member], name: &str) -> Option<String> {
    match find(members, name).map(|node| &node.value) {
        Some(Value::String(text)) if !text.trim().is_empty() => Some(text.trim().to_owned()),
        _ => None,
    }
}

/// A whole number written as a number or as a string (the list writes the
/// port as a string); `None` for anything else.
fn whole(members: &[Member], name: &str) -> Option<i32> {
    let node = find(members, name)?;
    match &node.value {
        Value::Number(_) => host_json::int32(node).ok(),
        Value::String(text) => {
            let text = text.trim();
            // A sign may lead; nothing else but digits.
            let digits = text.strip_prefix(['+', '-']).unwrap_or(text);
            if digits.is_empty() || !digits.bytes().all(|byte| byte.is_ascii_digit()) {
                return None;
            }
            text.parse::<i32>().ok()
        }
        _ => None,
    }
}

/// A website or Discord link, kept only when it is an absolute `http` or
/// `https` address.
fn link(members: &[Member], name: &str) -> Option<WebUrl> {
    text(members, name)
        .and_then(|text| WebUrl::parse(&text))
        .filter(|url| url.is_http() || url.is_https())
}

fn read_cache(path: &Path) -> Option<String> {
    std::fs::read_to_string(path).ok()
}

fn cache_time(path: &Path) -> Option<SystemTime> {
    std::fs::metadata(path)
        .and_then(|meta| meta.modified())
        .ok()
}

/// Saves a fetched document; a list that cannot be saved still shows this
/// time, it is only not there offline next time.
fn write_cache(path: &Path, text: &str) {
    let Some(directory) = path.parent() else {
        return;
    };
    let mut temporary = path.as_os_str().to_owned();
    temporary.push(".tmp");
    let temporary = PathBuf::from(temporary);
    let _ = std::fs::create_dir_all(directory)
        .and_then(|()| std::fs::write(&temporary, text))
        .and_then(|()| std::fs::rename(&temporary, path));
}
