//! Keeping checked accounts' clients running: after a crash or an exit,
//! once the process and its start-up work have ended and the delay has
//! passed, the account is launched again with its current choice. A Stop
//! keeps the client down until it is launched again by hand.
//!
//! A checked account with a character chosen and no client is started at
//! once: when the launcher opens, when it is checked, and when keeping
//! clients running is turned on. One whose last client was stopped stays
//! down.
//!
//! The controller owns no process and runs no launch itself: [`poll`]
//! hands out at most one due attempt, the caller runs the ordinary launch,
//! and [`finish`] records how it went.
//!
//! A relaunched client the server refuses because the account is still
//! logged on is tried again as soon as the server's hold ends, for as long
//! as the server keeps answering: a refusal means the server is up. A
//! client that cannot reach the server ends like any other and waits the
//! full delay before the next try.
//!
//! [`poll`]: LauncherRelaunchController::poll
//! [`finish`]: LauncherRelaunchController::finish

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime};

use super::capabilities::LauncherCapability;
use super::orchestrator::OrchestratorError;
use super::snapshot::{LauncherActivityKind, LauncherSessionSnapshot, LauncherStateSnapshot};
use crate::cancel::CancelToken;
use crate::profiles::{LaunchMode, same_account};

/// The default delay before a relaunch, in seconds.
pub const DEFAULT_RELAUNCH_DELAY_SECONDS: u32 = 30;
/// The shortest delay allowed, in seconds.
pub const MINIMUM_RELAUNCH_DELAY_SECONDS: u32 = 5;
/// The longest delay allowed, in seconds.
pub const MAXIMUM_RELAUNCH_DELAY_SECONDS: u32 = 3600;

/// A monotonic clock: the time since some fixed start.
pub type MonotonicClock = Arc<dyn Fn() -> Duration + Send + Sync>;

/// A checked account's current saved launch choice, whatever the visual
/// filters show.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LauncherRelaunchSelection {
    /// The server.
    pub server: String,
    /// The account.
    pub account: String,
    /// The character, or `None` for character select.
    pub character: Option<String>,
    /// The launch mode.
    pub mode: LaunchMode,
}

impl LauncherRelaunchSelection {
    /// A choice for `account` on `server`.
    pub fn new(server: &str, account: &str, character: Option<&str>, mode: LaunchMode) -> Self {
        Self {
            server: server.to_owned(),
            account: account.to_owned(),
            character: character.map(str::to_owned),
            mode,
        }
    }
}

/// A delay outside 5..=3600 seconds.
#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
#[error("The relaunch delay must be a whole number from 5 to 3600 seconds.")]
pub struct RelaunchDelayOutOfRange;

/// One due relaunch, handed to the caller to run.
#[derive(Debug, Clone)]
pub struct RelaunchAttempt {
    /// What to launch.
    pub selection: LauncherRelaunchSelection,
    /// Cancelled when the account's relaunch is no longer wanted.
    pub cancel: CancelToken,
    entry: u64,
}

/// A checked account: its server and its name.
type Key = (String, String);

/// The same account: the server exactly, the name in any case.
fn same_key(a: &Key, b: &Key) -> bool {
    a.0 == b.0 && same_account(&a.1, &b.1)
}

#[derive(Debug)]
struct Entry {
    id: u64,
    selection: LauncherRelaunchSelection,
    seen_session: Option<String>,
    seen_created_at: Option<SystemTime>,
    tracked_session: Option<String>,
    terminal_since: Option<Duration>,
    cancellation: Option<CancelToken>,
    /// The server refused the tracked session because the account was still
    /// logged on: the next attempt waits only for the server's hold, not the
    /// full delay.
    retry_after_hold: bool,
    /// The account has no client and none was stopped: it starts without
    /// waiting the delay, once a character is chosen.
    start_now: bool,
}

impl Entry {
    /// The client ended; it is relaunched after the delay unless it was
    /// stopped.
    fn ended(&mut self, now: Duration) {
        if self.terminal_since.is_none() {
            self.terminal_since = Some(now);
            // An end waits the full delay unless it shows a refusal.
            self.retry_after_hold = false;
        }
    }

    /// The client was stopped: it stays down.
    fn stopped(&mut self) {
        self.tracked_session = None;
        self.terminal_since = None;
        self.retry_after_hold = false;
        self.start_now = false;
    }
}

/// Schedules ordinary launches for the play sessions of checked accounts.
pub struct LauncherRelaunchController {
    clock: MonotonicClock,
    entries: Vec<(Key, Entry)>,
    next_entry: u64,
    enabled: bool,
    disposed: bool,
    launching: bool,
    delay_seconds: u32,
}

impl std::fmt::Debug for LauncherRelaunchController {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LauncherRelaunchController")
            .field("enabled", &self.enabled)
            .field("launching", &self.launching)
            .field("delay_seconds", &self.delay_seconds)
            .finish_non_exhaustive()
    }
}

impl Default for LauncherRelaunchController {
    fn default() -> Self {
        let start = Instant::now();
        Self::new(Arc::new(move || start.elapsed()))
    }
}

impl LauncherRelaunchController {
    /// A controller timing its delays on `clock`.
    pub fn new(clock: MonotonicClock) -> Self {
        Self {
            clock,
            entries: Vec::new(),
            next_entry: 0,
            enabled: false,
            disposed: false,
            launching: false,
            delay_seconds: DEFAULT_RELAUNCH_DELAY_SECONDS,
        }
    }

    /// An attempt is running.
    pub fn is_launching(&self) -> bool {
        self.launching
    }

    /// The delay, in seconds.
    pub fn delay_seconds(&self) -> u32 {
        self.delay_seconds
    }

    /// Sets the delay; refused outside 5..=3600 seconds.
    pub fn set_delay_seconds(&mut self, seconds: u32) -> Result<(), RelaunchDelayOutOfRange> {
        if !(MINIMUM_RELAUNCH_DELAY_SECONDS..=MAXIMUM_RELAUNCH_DELAY_SECONDS).contains(&seconds) {
            return Err(RelaunchDelayOutOfRange);
        }
        self.delay_seconds = seconds;
        Ok(())
    }

    /// Updates the checked accounts and follows only their active or newly
    /// seen play sessions; an account that has only old history is not
    /// launched just by being checked.
    pub fn observe(
        &mut self,
        snapshot: &LauncherStateSnapshot,
        checked_accounts: &[LauncherRelaunchSelection],
        enabled: bool,
    ) {
        if self.disposed {
            return;
        }
        if !enabled {
            self.cancel_entries();
            self.enabled = false;
            return;
        }
        self.enabled = true;
        let configured = |server_name: &str, account_name: &str| {
            snapshot.servers.iter().any(|server| {
                server.name == server_name
                    && server
                        .accounts
                        .iter()
                        .any(|account| same_account(&account.account_name, account_name))
            })
        };
        let mut selected: Vec<(Key, LauncherRelaunchSelection)> = Vec::new();
        for selection in checked_accounts {
            if !configured(&selection.server, &selection.account) {
                continue;
            }
            // Two checked spellings of one account are one entry: the first
            // is kept.
            let key = (selection.server.clone(), selection.account.clone());
            if !selected.iter().any(|(known, _)| same_key(known, &key)) {
                selected.push((key, selection.clone()));
            }
        }
        let mut sessions: HashMap<&str, &LauncherSessionSnapshot> = HashMap::new();
        let mut latest_plays: Vec<&LauncherSessionSnapshot> = Vec::new();
        for session in &snapshot.sessions {
            sessions.insert(session.session_id.as_str(), session);
            if session.kind != LauncherActivityKind::Play {
                continue;
            }
            match latest_plays.iter_mut().find(|previous| {
                previous.server_name == session.server_name
                    && same_account(&previous.account_name, &session.account_name)
            }) {
                Some(previous) if session.created_at > previous.created_at => *previous = session,
                Some(_) => {}
                None => latest_plays.push(session),
            }
        }
        self.entries.retain(|(key, entry)| {
            let keep = selected.iter().any(|(wanted, _)| same_key(wanted, key));
            if !keep && let Some(cancel) = &entry.cancellation {
                cancel.cancel();
            }
            keep
        });
        let now = (self.clock)();
        for (key, choice) in selected {
            let latest = latest_plays
                .iter()
                .find(|session| {
                    session.server_name == key.0 && same_account(&session.account_name, &key.1)
                })
                .copied();
            let index = match self
                .entries
                .iter()
                .position(|(known, _)| same_key(known, &key))
            {
                Some(index) => {
                    let entry = &mut self.entries[index].1;
                    entry.selection = choice;
                    if let Some(latest) = latest
                        && entry.seen_session.as_deref() != Some(latest.session_id.as_str())
                        && entry
                            .seen_created_at
                            .is_none_or(|seen| latest.created_at >= seen)
                    {
                        entry.seen_session = Some(latest.session_id.clone());
                        entry.seen_created_at = Some(latest.created_at);
                        entry.tracked_session = Some(latest.session_id.clone());
                        entry.terminal_since = None;
                        entry.start_now = false;
                        // A session someone else started waits the full
                        // delay after it ends. One that appears while an
                        // attempt runs is the attempt's own (its launch
                        // reports the state change before the attempt is
                        // finished).
                        if entry.cancellation.is_none() {
                            entry.retry_after_hold = false;
                        }
                    }
                    index
                }
                None => {
                    let id = self.next_entry;
                    self.next_entry += 1;
                    let running = latest.filter(|session| session.is_process_or_start_active());
                    // No client: start one now, unless the last was stopped.
                    let start_now =
                        running.is_none() && latest.is_none_or(|session| session.stop.is_none());
                    self.entries.push((
                        key,
                        Entry {
                            id,
                            selection: choice,
                            seen_session: latest.map(|session| session.session_id.clone()),
                            seen_created_at: latest.map(|session| session.created_at),
                            tracked_session: running.map(|session| session.session_id.clone()),
                            terminal_since: start_now.then_some(now),
                            cancellation: None,
                            retry_after_hold: false,
                            start_now,
                        },
                    ));
                    self.entries.len() - 1
                }
            };
            let entry = &mut self.entries[index].1;
            let Some(tracked) = &entry.tracked_session else {
                continue;
            };
            let session = sessions.get(tracked.as_str()).copied();
            match session {
                Some(session) if session.is_process_or_start_active() => {
                    entry.terminal_since = None;
                }
                Some(session) if session.stop.is_some() => {
                    // The player's Stop, graceful or one that had to kill
                    // the client: it stays down until launched again.
                    entry.stopped();
                }
                ended => {
                    entry.ended(now);
                    // The client's exit report, which says the server still
                    // held the account, is often read after the end itself
                    // was seen: whenever it shows, the next try waits only
                    // for the hold. The server answered, so it is up: there
                    // is no limit on these tries.
                    if ended.is_some_and(|session| session.account_in_use) {
                        entry.retry_after_hold = true;
                    }
                }
            }
        }
    }

    /// Hands out at most one due relaunch. `may_launch` and each account's
    /// `capability` (a reconnect hold, a running session) are checked again
    /// on every poll; nothing is handed out while an attempt runs.
    pub fn poll(
        &mut self,
        may_launch: bool,
        capability: &mut dyn FnMut(
            &LauncherRelaunchSelection,
        ) -> Result<LauncherCapability, OrchestratorError>,
    ) -> Result<Option<RelaunchAttempt>, OrchestratorError> {
        if self.disposed || !self.enabled || self.launching || !may_launch {
            return Ok(None);
        }
        let now = (self.clock)();
        let delay = Duration::from_secs(u64::from(self.delay_seconds));
        for (_, entry) in &mut self.entries {
            let Some(since) = entry.terminal_since else {
                continue;
            };
            // After a refusal the server's hold, checked through `capability`,
            // is the only wait; an account with no client does not wait.
            if !entry.retry_after_hold && !entry.start_now && now.saturating_sub(since) < delay {
                continue;
            }
            let choice = &entry.selection;
            if choice.mode == LaunchMode::Headless && choice.character.is_none() {
                continue;
            }
            // Started without a client to replace: only with a character.
            if entry.start_now && choice.character.is_none() {
                continue;
            }
            if !capability(choice)?.is_available {
                continue;
            }
            let cancel = CancelToken::new();
            entry.cancellation = Some(cancel.clone());
            // A failed start waits the full delay like any other.
            entry.start_now = false;
            self.launching = true;
            return Ok(Some(RelaunchAttempt {
                selection: choice.clone(),
                cancel,
                entry: entry.id,
            }));
        }
        Ok(None)
    }

    /// Records how `attempt` went. A failure (not a cancellation) is
    /// returned for the caller to show; the next attempt waits a full delay.
    pub fn finish(
        &mut self,
        attempt: &RelaunchAttempt,
        result: Result<LauncherSessionSnapshot, OrchestratorError>,
    ) -> Option<OrchestratorError> {
        let now = (self.clock)();
        let mut failure = None;
        match result {
            Ok(launched) => {
                if let Some(entry) = self.live_entry(attempt) {
                    entry.tracked_session = Some(launched.session_id.clone());
                    entry.seen_session = Some(launched.session_id.clone());
                    entry.seen_created_at = Some(launched.created_at);
                    entry.terminal_since = (!launched.is_process_or_start_active()).then_some(now);
                }
            }
            Err(OrchestratorError::Cancelled) if attempt.cancel.is_cancelled() => {}
            Err(error) => {
                if !self.disposed {
                    failure = Some(error);
                }
                // A launch that failed outright waits the full delay.
                if let Some(entry) = self.live_entry(attempt) {
                    entry.retry_after_hold = false;
                }
            }
        }
        if let Some(entry) = self.live_entry(attempt)
            && entry.terminal_since.is_some()
        {
            entry.terminal_since = Some(now);
        }
        if let Some((_, entry)) = self
            .entries
            .iter_mut()
            .find(|(_, entry)| entry.id == attempt.entry)
        {
            entry.cancellation = None;
        }
        self.launching = false;
        failure
    }

    /// Every account is stopped (Stop all): pending relaunches and starts
    /// are dropped and an attempt in flight is cancelled; each stays down
    /// until it is launched again, as after its own Stop.
    pub fn stop_all(&mut self) {
        for (_, entry) in &mut self.entries {
            if let Some(cancel) = &entry.cancellation {
                cancel.cancel();
            }
            entry.stopped();
        }
    }

    /// Cancels every pending relaunch and stops scheduling.
    pub fn dispose(&mut self) {
        if self.disposed {
            return;
        }
        self.disposed = true;
        self.enabled = false;
        self.cancel_entries();
    }

    fn live_entry(&mut self, attempt: &RelaunchAttempt) -> Option<&mut Entry> {
        if self.disposed || !self.enabled {
            return None;
        }
        self.entries
            .iter_mut()
            .map(|(_, entry)| entry)
            .find(|entry| entry.id == attempt.entry)
    }

    fn cancel_entries(&mut self) {
        for (_, entry) in &self.entries {
            if let Some(cancel) = &entry.cancellation {
                cancel.cancel();
            }
        }
        self.entries.clear();
    }
}
