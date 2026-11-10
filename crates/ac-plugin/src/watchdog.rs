//! How long a call into a plugin may take, and what the client says about
//! one that takes long.
//!
//! A call may take as long as it needs: one over [`SLOW_CALL`] goes on and
//! is reported as slow, so lag can be traced to a plugin; only one past its
//! deadline ([`CALL_DEADLINE`], [`START_DEADLINE`]) is taken for stuck (an
//! endless loop) and stopped. The deadline is wasmtime's epoch
//! interruption: a [`Ticker`] thread moves the engine's epoch on every
//! [`EPOCH_TICK`], and each call sets its store's deadline that many
//! epochs ahead. The ticker touches nothing but the engine's epoch
//! counter; the plugins and their state stay on the session's thread.
//!
//! The ticker counts the time it actually slept, at most
//! [`EPOCH_STEP_MAX`] per wake: a process stopped in a debugger, or a
//! computer gone to sleep, stops the ticker too and adds at most that much
//! to a running call's clock when it goes on, so a breakpoint does not get
//! the plugin it interrupted stopped.
//!
//! A call's time is its own. When it asks another plugin something (a loot
//! classifier, a command's completions), that plugin's answer runs as a
//! call of its own, with its own deadline and slow-call time, and the
//! asking call's clock stops meanwhile: a slow answer warns, or a stuck one
//! stops, the plugin that gave it, never the one that asked.
//! One entry call lends other plugins at most one [`CALL_DEADLINE`] in
//! all: past that it asks no other plugin anything (a service call answers
//! that it is out of time), so a plugin asking in a loop cannot hold its
//! session however fast each answer is.
//!
//! Every report names the entry call ([`Entry`]) and the client functions
//! it called on the way ([`ImportTally`]): the fault of a stuck plugin, the
//! slow-call warning, and the opt-in slow log
//! ([`crate::PluginSessionOptions::slow_log`], every call over its
//! threshold).

use std::fmt;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{self, RecvTimeoutError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use ac_plugin_abi::callback::Callback;
use ac_plugin_abi::ui::UiEvent;
use ac_plugin_abi::world::ObjectChangeKind;
use ac_plugin_abi::{Event, Import};

/// A tick, event, command or callback that takes longer than this, wall
/// time (the client's work on its imports included, what other plugins took
/// to answer it left out), is reported as slow:
/// a warning in the client log and one chat line per plugin per session.
/// It keeps running.
pub const SLOW_CALL: Duration = Duration::from_millis(100);

/// A tick, event, command or callback still running after this long, wall
/// time, is taken for stuck and stopped: the plugin is unloaded with a
/// fault.
pub const CALL_DEADLINE: Duration = Duration::from_secs(10);

/// The deadline of starting (instantiation and `enable`) and stopping
/// (`disable`), which may read files and build tables.
pub const START_DEADLINE: Duration = Duration::from_secs(30);

/// How often the [`Ticker`] moves the engine's epoch on: the resolution of
/// the deadlines.
pub const EPOCH_TICK: Duration = Duration::from_millis(10);

/// The most one wake of the [`Ticker`] counts, however long it slept: what
/// a debugger stop or a computer's sleep adds to a running call's clock.
pub const EPOCH_STEP_MAX: Duration = Duration::from_millis(100);

/// The environment variable that turns the slow log on: the threshold in
/// milliseconds (`content/docs/launch-options.md`).
pub const SLOW_LOG_VARIABLE: &str = "OPENAC_PLUGIN_SLOW_LOG";

/// The threshold [`SLOW_LOG_VARIABLE`] holds: a whole number of
/// milliseconds; `None` (off) when it is unset, blank or not a number.
#[must_use]
pub fn slow_log_threshold(value: Option<&str>) -> Option<Duration> {
    value
        .and_then(|text| text.trim().replace('_', "").parse().ok())
        .map(Duration::from_millis)
}

/// The time limits a [`crate::PluginEngine`] gives every call: the
/// constants above, or shorter ones for tests
/// (`PluginEngine::with_time_limits`, feature `test-fixtures`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TimeLimits {
    /// A call over this is reported as slow ([`SLOW_CALL`]).
    pub slow_call: Duration,
    /// Where a tick, event, command or callback is stopped
    /// ([`CALL_DEADLINE`]).
    pub call: Duration,
    /// Where starting or stopping is stopped ([`START_DEADLINE`]).
    pub start: Duration,
}

impl Default for TimeLimits {
    fn default() -> Self {
        Self {
            slow_call: SLOW_CALL,
            call: CALL_DEADLINE,
            start: START_DEADLINE,
        }
    }
}

impl TimeLimits {
    /// The deadline of entry call `entry`.
    #[must_use]
    pub fn deadline(&self, entry: Entry) -> Duration {
        match entry {
            Entry::Start | Entry::Stop => self.start,
            Entry::Tick | Entry::Event(_) | Entry::Callback(_) => self.call,
        }
    }
}

/// `deadline` in epochs from now: rounded up, and one more for the epoch
/// already under way, so a call is never stopped before its deadline.
#[must_use]
pub(crate) fn epochs(deadline: Duration) -> u64 {
    let ticks = deadline.as_nanos().div_ceil(EPOCH_TICK.as_nanos());
    u64::try_from(ticks)
        .unwrap_or(u64::MAX / 2)
        .saturating_add(1)
}

/// A duration in words: `10 s` when it is whole seconds, else `250 ms`.
#[must_use]
pub fn duration_words(duration: Duration) -> String {
    if duration >= Duration::from_secs(1) && duration.subsec_nanos() == 0 {
        format!("{} s", duration.as_secs())
    } else {
        format!("{} ms", duration.as_millis())
    }
}

/// The epochs a [`Ticker`] has moved its engine on, counted beside the
/// engine's own epoch (which wasmtime keeps to itself), so a call can
/// measure in epochs, as its deadline does, how long another plugin took
/// to answer it. Counted after the engine's epoch moves, so it is never
/// ahead of it: a deadline worked out from it is never short.
#[derive(Debug, Clone, Default)]
pub(crate) struct EpochCount(Arc<AtomicU64>);

impl EpochCount {
    /// The epochs counted so far.
    pub(crate) fn now(&self) -> u64 {
        self.0.load(Ordering::Acquire)
    }

    /// `ticks` more epochs.
    pub(crate) fn advance(&self, ticks: u32) {
        self.0.fetch_add(u64::from(ticks), Ordering::Release);
    }
}

/// The thread that moves an engine's epoch on every [`EPOCH_TICK`] while
/// it lives (its only work); dropping it ends the thread.
pub(crate) struct Ticker {
    stop: Option<mpsc::Sender<()>>,
    thread: Option<JoinHandle<()>>,
}

impl fmt::Debug for Ticker {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Ticker").finish_non_exhaustive()
    }
}

impl Ticker {
    /// Starts the thread for `engine`, counting its epochs in `count`.
    ///
    /// # Errors
    /// The thread could not be started, in words.
    pub(crate) fn start(engine: &wasmtime::Engine, count: EpochCount) -> Result<Self, String> {
        let (stop, stopped) = mpsc::channel::<()>();
        let engine = engine.clone();
        let thread = std::thread::Builder::new()
            .name("plugin-watchdog".to_owned())
            .spawn(move || {
                let mut last = Instant::now();
                let mut carry = Duration::ZERO;
                // Woken by the timeout every tick; the sender's drop ends it.
                while let Err(RecvTimeoutError::Timeout) = stopped.recv_timeout(EPOCH_TICK) {
                    let now = Instant::now();
                    let slept = (now - last + carry).min(EPOCH_STEP_MAX);
                    last = now;
                    // At most EPOCH_STEP_MAX / EPOCH_TICK ticks: it fits.
                    let ticks =
                        u32::try_from(slept.as_nanos() / EPOCH_TICK.as_nanos()).unwrap_or(0);
                    carry = slept - EPOCH_TICK * ticks;
                    for _ in 0..ticks {
                        engine.increment_epoch();
                    }
                    count.advance(ticks);
                }
            })
            .map_err(|error| format!("the plugin watchdog could not start: {error}"))?;
        Ok(Self {
            stop: Some(stop),
            thread: Some(thread),
        })
    }
}

impl Drop for Ticker {
    fn drop(&mut self) {
        drop(self.stop.take());
        if let Some(thread) = self.thread.take() {
            // The thread only counts; a panic there leaves nothing to report.
            let _ = thread.join();
        }
    }
}

/// Which of a plugin's entry calls ran.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Entry {
    /// Instantiation and `enable`.
    Start,
    /// `disable`.
    Stop,
    /// The 15 ms tick.
    Tick,
    /// An event, by kind.
    Event(&'static str),
    /// A callback (a question the client or another plugin asks), by kind.
    Callback(&'static str),
}

impl Entry {
    /// The entry call that delivers `event`.
    #[must_use]
    pub fn of_event(event: &Event) -> Self {
        Self::Event(match event {
            Event::WindowAttached(_) => "window attached",
            Event::Command(_) => "command",
            Event::ChatReceived(_) => "chat line",
            Event::LoginComplete => "login complete",
            Event::Logoff => "logoff",
            Event::LocalPlayerDied(_) => "player died",
            Event::ObjectChanged(change) => match change.kind {
                ObjectChangeKind::Created => "object created",
                ObjectChangeKind::Updated => "object updated",
                ObjectChangeKind::IdentReceived => "object appraised",
                ObjectChangeKind::Moved => "object moved",
                ObjectChangeKind::Released => "object released",
            },
            Event::PortalTransition(_) => "portal transition",
            Event::ConfirmationRequested(_) => "confirmation requested",
            Event::ContainerOpened(_) => "container opened",
            Event::ContainerClosed(_) => "container closed",
            Event::SelectionChanged(_) => "selection changed",
            Event::ActivationCompleted(_) => "activation completed",
            Event::ItemUseCompleted(_) => "item use completed",
            Event::EquipmentPlacement(_) => "equipment placement",
            Event::Trade(_) => "trade",
            Event::Vendor(_) => "vendor",
            Event::NavigationChanged(_) => "navigation changed",
            Event::NavigationSnapshotChanged(_) => "navigation snapshot",
            Event::ChatLinkClicked(_) => "chat link clicked",
            Event::Net(_) => "network",
            Event::EntitySpawned(_) => "entity spawned",
            Event::ExperienceChanged(_) => "experience changed",
            Event::ThemeChanged(_) => "theme changed",
            Event::NavigationPlan(_) => "navigation plan",
            Event::NavigationReach(_) => "navigation reach",
            Event::ServiceChanged(_) => "service changed",
            Event::StatusTextReceived(_) => "status text",
            Event::Ui(UiEvent::Action(_)) => "ui action",
            Event::Ui(UiEvent::Pointer(_)) => "ui pointer",
            Event::Ui(UiEvent::Hotkey(_)) => "ui hotkey",
            Event::Ui(UiEvent::InputAction(..)) => "ui input action",
            // `Event` is non-exhaustive: a kind added later reads as this
            // until it is named here.
            _ => "(unnamed kind)",
        })
    }

    /// The entry call that asks `callback`.
    #[must_use]
    pub fn of_callback(callback: &Callback) -> Self {
        Self::Callback(match callback {
            Callback::ChatFilter(_) => "chat filter",
            Callback::ChatIntercept(_) => "chat intercept",
            Callback::LootClassify(_) => "loot classify",
            Callback::LootNeedsIdentification(_) => "loot needs identification",
            Callback::LootLooted(_) => "loot looted",
            Callback::LootRemoved(_) => "loot removed",
            Callback::CommandComplete(_) => "command completion",
            Callback::ServiceRequest(_) => "service request",
            // `Callback` is non-exhaustive: a kind added later reads as this
            // until it is named here.
            _ => "(unnamed kind)",
        })
    }
}

impl fmt::Display for Entry {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Start => f.write_str("start"),
            Self::Stop => f.write_str("stop"),
            Self::Tick => f.write_str("tick"),
            Self::Event(kind) => write!(f, "event {kind}"),
            Self::Callback(kind) => write!(f, "callback {kind}"),
        }
    }
}

/// How many of the most recently used imports a report names.
const NAMED: usize = 5;

/// The client functions one call made, each with how often, in the order
/// last used, and how long the client took over them.
#[derive(Debug, Default, Clone)]
pub(crate) struct ImportTally {
    /// (import, calls, when last called, the client's time on it).
    counts: Vec<(Import, u32, u64, Duration)>,
    calls: u64,
}

impl ImportTally {
    /// Forgets the last call's imports, keeping the room.
    pub(crate) fn clear(&mut self) {
        self.counts.clear();
        self.calls = 0;
    }

    /// One more call of `import`, which took the client `time`.
    pub(crate) fn record(&mut self, import: Import, time: Duration) {
        self.calls += 1;
        let now = self.calls;
        if let Some(entry) = self.counts.iter_mut().find(|(i, ..)| *i == import) {
            entry.1 = entry.1.saturating_add(1);
            entry.2 = now;
            entry.3 = entry.3.saturating_add(time);
        } else {
            self.counts.push((import, 1, now, time));
        }
    }

    /// The client's time over all the imports; the rest of a call's wall
    /// time is the plugin's own.
    pub(crate) fn client_time(&self) -> Duration {
        self.counts.iter().map(|(.., time)| *time).sum()
    }

    /// `client 1.2 ms, most in items_state ×25 (0.9 ms)`: the client's
    /// share of a call and the import it spent longest on (the slow log).
    pub(crate) fn client_share(&self) -> String {
        let total = millis(self.client_time());
        match self.counts.iter().max_by_key(|(.., time)| *time) {
            Some((import, count, _, time)) => format!(
                "client {total:.1} ms, most in {} \u{d7}{count} ({:.1} ms)",
                import.name(),
                millis(*time)
            ),
            None => format!("client {total:.1} ms"),
        }
    }
}

/// A duration in milliseconds, for the logs.
fn millis(time: Duration) -> f64 {
    time.as_secs_f64() * 1000.0
}

impl fmt::Display for ImportTally {
    /// `items_owned ×1, spells_known ×3, world_object ×412`: the last
    /// [`NAMED`] imports used, the most recent last, then how many others.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.counts.is_empty() {
            return f.write_str("none");
        }
        let mut recent: Vec<&(Import, u32, u64, Duration)> = self.counts.iter().collect();
        recent.sort_by_key(|(_, _, last, _)| std::cmp::Reverse(*last));
        let others = recent.len().saturating_sub(NAMED);
        recent.truncate(NAMED);
        for (n, (import, count, ..)) in recent.iter().rev().enumerate() {
            if n > 0 {
                f.write_str(", ")?;
            }
            write!(f, "{} \u{d7}{count}", import.name())?;
        }
        match others {
            0 => Ok(()),
            1 => f.write_str(" (and 1 other)"),
            _ => write!(f, " (and {others} others)"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_tally_names_the_last_imports_used_with_their_counts() {
        let mut tally = ImportTally::default();
        assert_eq!(tally.to_string(), "none");
        tally.record(Import::HostSetting, Duration::ZERO);
        for _ in 0..3 {
            tally.record(Import::HostWindowAttached, Duration::ZERO);
        }
        tally.record(Import::HostClock, Duration::ZERO);
        tally.record(Import::HostClock, Duration::ZERO);
        assert_eq!(
            tally.to_string(),
            "host_setting \u{d7}1, host_window_attached \u{d7}3, host_clock \u{d7}2"
        );
        tally.clear();
        assert_eq!(tally.to_string(), "none");
    }

    #[test]
    fn the_tally_adds_up_the_clients_time_and_names_its_heaviest_import() {
        let mut tally = ImportTally::default();
        assert_eq!(tally.client_share(), "client 0.0 ms");
        tally.record(Import::HostClock, Duration::from_micros(100));
        for _ in 0..25 {
            tally.record(Import::HostSetting, Duration::from_micros(100));
        }
        tally.record(Import::HostClock, Duration::from_micros(200));
        assert_eq!(tally.client_time(), Duration::from_micros(2800));
        assert_eq!(
            tally.client_share(),
            "client 2.8 ms, most in host_setting \u{d7}25 (2.5 ms)"
        );
    }

    #[test]
    fn the_tally_names_five_and_counts_the_rest() {
        let mut tally = ImportTally::default();
        for import in Import::ALL.iter().take(7) {
            tally.record(*import, Duration::ZERO);
        }
        let text = tally.to_string();
        assert!(text.ends_with(" (and 2 others)"), "{text}");
        assert_eq!(text.matches('\u{d7}').count(), 5, "{text}");
        assert!(text.contains(Import::ALL[6].name()), "{text}");
        assert!(!text.contains(Import::ALL[0].name()), "{text}");
    }

    #[test]
    fn the_threshold_is_whole_milliseconds_or_off() {
        assert_eq!(
            slow_log_threshold(Some(" 20 ")),
            Some(Duration::from_millis(20))
        );
        assert_eq!(
            slow_log_threshold(Some("1_000")),
            Some(Duration::from_secs(1))
        );
        assert_eq!(slow_log_threshold(Some("")), None);
        assert_eq!(slow_log_threshold(Some("lots")), None);
        assert_eq!(slow_log_threshold(Some("2.5")), None);
        assert_eq!(slow_log_threshold(None), None);
    }

    #[test]
    fn the_limits_are_the_owners() {
        // Warn past 100 ms, stop past 10 s (30 s to
        // start or stop).
        assert_eq!(SLOW_CALL, Duration::from_millis(100));
        assert_eq!(CALL_DEADLINE, Duration::from_secs(10));
        assert_eq!(START_DEADLINE, Duration::from_secs(30));
        let limits = TimeLimits::default();
        assert_eq!(limits.slow_call, SLOW_CALL);
        assert_eq!(limits.deadline(Entry::Tick), CALL_DEADLINE);
        assert_eq!(limits.deadline(Entry::Event("chat line")), CALL_DEADLINE);
        assert_eq!(
            limits.deadline(Entry::Callback("chat filter")),
            CALL_DEADLINE
        );
        assert_eq!(limits.deadline(Entry::Start), START_DEADLINE);
        assert_eq!(limits.deadline(Entry::Stop), START_DEADLINE);
    }

    #[test]
    fn a_deadline_in_epochs_is_never_short_of_its_time() {
        assert_eq!(epochs(CALL_DEADLINE), 1001);
        assert_eq!(epochs(Duration::from_millis(15)), 3);
        assert_eq!(epochs(Duration::ZERO), 1);
    }

    #[test]
    fn durations_read_in_seconds_when_whole() {
        assert_eq!(duration_words(CALL_DEADLINE), "10 s");
        assert_eq!(duration_words(START_DEADLINE), "30 s");
        assert_eq!(duration_words(Duration::from_millis(250)), "250 ms");
        assert_eq!(duration_words(Duration::from_millis(1500)), "1500 ms");
    }

    #[test]
    fn entries_read_as_the_call_they_were() {
        assert_eq!(Entry::Tick.to_string(), "tick");
        assert_eq!(Entry::Start.to_string(), "start");
        assert_eq!(
            Entry::of_event(&Event::LoginComplete).to_string(),
            "event login complete"
        );
        assert_eq!(
            Entry::of_callback(&Callback::LootRemoved((1, 2))).to_string(),
            "callback loot removed"
        );
    }
}
