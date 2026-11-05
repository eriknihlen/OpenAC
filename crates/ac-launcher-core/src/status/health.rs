//! Server health: whether the game endpoint answers a status request, and
//! how many players a public population list reports for it.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime};

use ac_host_core::json::{self as host_json, Value};

use crate::cancel::{CancelToken, Cancelled};
use crate::net::{self, HttpRequest, HttpTransport};
use crate::url::WebUrl;

/// The public population list.
pub const POPULATION_URI: &str = "https://treestats.net/player_counts-latest.json";

/// A status request a server answers without a password: the login packet
/// header with the `acservertracker` identity, which never continues the
/// handshake.
const STATUS_REQUEST_HEX: &str = "00000000000001009300D005000000004000000004003138303200003400000001000000000000003EB8A8581C006163736572766572747261636B65723A6A6A3968323668637367676300000000000000000000";

/// The datagram a reachability probe sends.
pub fn status_request_datagram() -> Vec<u8> {
    (0..STATUS_REQUEST_HEX.len())
        .step_by(2)
        .map(|index| u8::from_str_radix(&STATUS_REQUEST_HEX[index..index + 2], 16).unwrap_or(0))
        .collect()
}

/// Whether a datagram is the server's answer to the status request: the
/// connect challenge (flags `0x00040000`, 32-byte body) or an explicit
/// network rejection (`0x00100000` or `0x00200000`, 4-byte body), with the
/// header's body size matching the datagram.
pub fn is_status_reply(packet: &[u8]) -> bool {
    if packet.len() < 20 {
        return false;
    }
    let flags = u32::from_le_bytes([packet[4], packet[5], packet[6], packet[7]]);
    let body_size = usize::from(u16::from_le_bytes([packet[16], packet[17]]));
    if body_size != packet.len() - 20 {
        return false;
    }
    match flags {
        0x0004_0000 => body_size == 32,
        0x0010_0000 | 0x0020_0000 => body_size == 4,
        _ => false,
    }
}

/// Sends the status request and waits for a valid reply. Implemented by the
/// host (a UDP socket); this crate opens none.
pub trait ReachabilityProbe: Send + Sync {
    /// Milliseconds until a valid reply ([`is_status_reply`]), or `None`
    /// when none came within `timeout` or the host could not be reached.
    fn probe(&self, host: &str, port: u16, timeout: Duration, cancel: &CancelToken) -> Option<f64>;
}

/// One server's health.
#[derive(Debug, Clone, PartialEq)]
pub struct ServerHealthSnapshot {
    /// Whether the endpoint answered.
    pub is_reachable: bool,
    /// Round trip in milliseconds, when it answered.
    pub latency_milliseconds: Option<f64>,
    /// Players the population list reports, when it names this server.
    pub player_count: Option<i32>,
    /// The count comes from an older successful fetch.
    pub is_player_count_stale: bool,
    /// When the check finished.
    pub checked_at: SystemTime,
}

/// Population counts keyed by server name, compared ignoring case.
pub type PlayerCounts = BTreeMap<String, (String, i32)>;

/// Reads the population list: an array of `{server, count}`; entries with
/// a blank name or a count that is not a non-negative 32-bit integer are
/// skipped. `None` when the text is not a JSON array.
pub fn parse_player_counts(json: &str) -> Option<PlayerCounts> {
    let root = host_json::parse(json.as_bytes()).ok()?;
    let Value::Array(items) = &root.value else {
        return None;
    };
    let mut counts = PlayerCounts::new();
    for item in items {
        let Value::Object(members) = &item.value else {
            continue;
        };
        let find = |name: &str| {
            members
                .iter()
                .find(|member| member.name == name)
                .map(|member| &member.value)
        };
        let Some(server) = find("server").and_then(|node| match &node.value {
            Value::String(text) if !text.trim().is_empty() => Some(text.trim().to_owned()),
            _ => None,
        }) else {
            continue;
        };
        let Some(count) = find("count")
            .and_then(|node| host_json::int32(node).ok())
            .filter(|&count| count >= 0)
        else {
            continue;
        };
        counts.insert(server.to_lowercase(), (server, count));
    }
    Some(counts)
}

fn normalize(name: &str) -> String {
    name.chars()
        .filter(|c| c.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect()
}

/// The count for a server: by its name (ignoring case), else the one entry
/// whose letters and digits match the name or one of the host's labels
/// (`coldeve` in `play.coldeve.ac`). `None` when nothing or more than one
/// entry matches.
pub fn find_player_count(counts: &PlayerCounts, server_name: &str, host: &str) -> Option<i32> {
    if let Some((_, count)) = counts.get(&server_name.trim().to_lowercase()) {
        return Some(*count);
    }
    let label = normalize(server_name);
    let host_parts: Vec<String> = host
        .trim_end_matches('.')
        .split('.')
        .map(normalize)
        .collect();
    let matches: Vec<i32> = counts
        .values()
        .filter(|(name, _)| {
            let key = normalize(name);
            key == label || host_parts.contains(&key)
        })
        .map(|(_, count)| *count)
        .collect();
    if matches.len() == 1 {
        Some(matches[0])
    } else {
        None
    }
}

#[derive(Debug, Default)]
struct PopulationState {
    counts: PlayerCounts,
    last_attempt: Option<SystemTime>,
    last_success: Option<SystemTime>,
}

type Clock = Arc<dyn Fn() -> SystemTime + Send + Sync>;

/// Checks reachability (3 s timeout) and population (fetched at most once a
/// minute, 5 s timeout, shared by every server; a count is stale when the
/// last fetch failed or is more than five minutes old).
pub struct ServerHealthService {
    transport: Arc<dyn HttpTransport>,
    probe: Arc<dyn ReachabilityProbe>,
    clock: Clock,
    population: Mutex<PopulationState>,
}

impl std::fmt::Debug for ServerHealthService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("ServerHealthService")
    }
}

impl ServerHealthService {
    /// A service on the system clock.
    pub fn new(transport: Arc<dyn HttpTransport>, probe: Arc<dyn ReachabilityProbe>) -> Self {
        Self::with_clock(transport, probe, SystemTime::now)
    }

    /// A service on `clock` (tests move time).
    pub fn with_clock(
        transport: Arc<dyn HttpTransport>,
        probe: Arc<dyn ReachabilityProbe>,
        clock: impl Fn() -> SystemTime + Send + Sync + 'static,
    ) -> Self {
        Self {
            transport,
            probe,
            clock: Arc::new(clock),
            population: Mutex::new(PopulationState::default()),
        }
    }

    /// Checks one server. Only the caller's cancellation is an error.
    pub fn check(
        &self,
        host: &str,
        port: u16,
        server_name: &str,
        cancel: &CancelToken,
    ) -> Result<ServerHealthSnapshot, Cancelled> {
        cancel.check()?;
        let probe = Arc::clone(&self.probe);
        let (latency, population) = std::thread::scope(|scope| {
            let reachability =
                scope.spawn(|| probe.probe(host, port, Duration::from_secs(3), cancel));
            let population = self.population(server_name, host, cancel);
            (reachability.join().ok().flatten(), population)
        });
        cancel.check()?;
        let (player_count, stale) = population?;
        Ok(ServerHealthSnapshot {
            is_reachable: latency.is_some(),
            latency_milliseconds: latency,
            player_count,
            is_player_count_stale: stale,
            checked_at: (self.clock)(),
        })
    }

    fn population(
        &self,
        server_name: &str,
        host: &str,
        cancel: &CancelToken,
    ) -> Result<(Option<i32>, bool), Cancelled> {
        let mut state = self
            .population
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        cancel.check()?;
        let now = (self.clock)();
        let due = state.last_attempt.is_none_or(|last| {
            now.duration_since(last).unwrap_or_default() >= Duration::from_secs(60)
        });
        if due {
            state.last_attempt = Some(now);
            if let Some(counts) = self.fetch_counts(cancel) {
                state.counts = counts;
                state.last_success = Some((self.clock)());
            }
            cancel.check()?;
        }
        let count = find_player_count(&state.counts, server_name, host);
        let stale = count.is_some()
            && match (state.last_success, state.last_attempt) {
                (None, _) => true,
                (Some(success), Some(attempt)) if attempt > success => true,
                (Some(success), _) => {
                    (self.clock)().duration_since(success).unwrap_or_default()
                        > Duration::from_secs(300)
                }
            };
        Ok((count, stale))
    }

    fn fetch_counts(&self, cancel: &CancelToken) -> Option<PlayerCounts> {
        let url = WebUrl::parse(POPULATION_URI)?;
        let mut response = self
            .transport
            .get(
                &HttpRequest {
                    url,
                    timeout: Some(Duration::from_secs(5)),
                },
                cancel,
            )
            .ok()?;
        if !(200..300).contains(&response.status) {
            return None;
        }
        let body = net::read_bounded(&mut response, 1_048_576, "too large", cancel).ok()?;
        parse_player_counts(&String::from_utf8_lossy(&body))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_status_request_carries_the_passwordless_identity() {
        let request = status_request_datagram();
        // OpenAC ServerHealthServiceTests.UdpProbeUsesPasswordlessStatusIdentityAndWaitsForValidReply.
        assert_eq!(
            u32::from_le_bytes([request[32], request[33], request[34], request[35]]),
            1
        );
        assert_eq!(&request[46..74], b"acservertracker:jj9h26hcsggc");
    }
}
