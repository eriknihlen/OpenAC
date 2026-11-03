//! A session's status file: one JSON line per session event, appended as it
//! happens, for a launcher, a supervisor or a person to follow.
//!
//! Every line is an object whose first members are `v` (vocabulary version,
//! 1), `e` (event name), `t` (UTC time, ISO 8601 with seven fractional digits
//! and `+00:00`) and `sessionId`, followed by the event's own members in a
//! fixed order. The connection edges are kept consistent: a second
//! `connected` first writes `disconnected` with reason `reconnect`, and
//! `exited` first closes an open connection with reason `process-exit`.
//! After `exited`, or after [`StatusWriter::close`], nothing more is written.
//!
//! A line that cannot be written is reported once through the diagnostic
//! callback and the writer stays on: the next event tries again, and a
//! successful write re-arms the report.

use std::fmt::{self, Write as _};
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::json::write_string;
use crate::paths::full_path;

/// The vocabulary version every line carries as `v`.
pub const STATUS_VOCABULARY_VERSION: u32 = 1;

/// How often a session that makes progress writes `heartbeat`.
pub const HEARTBEAT_INTERVAL: std::time::Duration = std::time::Duration::from_secs(10);

/// The environment variable a launcher sets to `1` to ask for `heartbeat`
/// lines. Without it none are written: a launcher that does not know the
/// event would report every one as unknown.
pub const HEARTBEAT_VARIABLE: &str = "OPENAC_STATUS_HEARTBEAT";

/// Whether the launcher asked for `heartbeat` lines (`read` looks up an
/// environment variable).
pub fn heartbeat_requested(read: impl Fn(&str) -> Option<String>) -> bool {
    read(HEARTBEAT_VARIABLE).is_some_and(|value| value.trim() == "1")
}

/// The server's refusal an `exited` line names: its words, and whether it
/// only says the account is still logged on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ServerRefusalNote<'a> {
    /// What the game says for it (`serverMessage`).
    pub message: &'a str,
    /// The account, or one of its characters, is still logged on
    /// (`accountInUse`).
    pub account_in_use: bool,
}

/// One character in a `characterList` event.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterEntry {
    /// The character's object id.
    pub id: u32,
    /// The character's name.
    pub name: String,
    /// Seconds until a character pending deletion is gone (0 when it is not).
    pub seconds_greyed_out: u32,
}

/// The character list the server sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterReport {
    /// The account name the server reported.
    pub account_name: String,
    /// How many character slots the account has.
    pub slot_count: i32,
    /// The characters, in server order.
    pub entries: Vec<RosterEntry>,
}

/// One status event.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(missing_docs)]
pub enum StatusEvent<'a> {
    Started,
    Connected,
    CharacterList(&'a RosterReport),
    EnteredWorld {
        character_id: u32,
        character_name: &'a str,
    },
    CharacterCreated {
        guid: u32,
        name: &'a str,
    },
    CreationFailed {
        code: u32,
        reason: &'a str,
        name: &'a str,
    },
    PluginLoaded {
        plugin: &'a str,
    },
    PluginFailed {
        plugin: &'a str,
        error: &'a str,
    },
    LoginCommandFailed {
        command_index: i32,
        command: &'a str,
        error: &'a str,
    },
    Disconnected {
        reason: &'a str,
    },
    Exited {
        code: i32,
        reason: &'a str,
        /// The server's own words, when a refusal from it ended the
        /// session (`serverMessage`; an addition to OpenAC's vocabulary).
        server_message: Option<&'a str>,
        /// That refusal only says the account, or one of its characters,
        /// is still logged on (`accountInUse`, written only when true).
        account_in_use: bool,
    },
    /// The session is alive and its loop makes progress (an addition to
    /// OpenAC's vocabulary): written every [`HEARTBEAT_INTERVAL`] while it
    /// does, so a launcher can tell a client that stopped responding.
    Heartbeat,
    /// A game window opened on the session (an addition to OpenAC's
    /// vocabulary: the launcher follows it to show the row's mode).
    WindowOpened,
    /// The session's game window closed; `reason` is `requested`,
    /// `closedByUser`, `renderFailed` or `shutdown`.
    WindowClosed {
        reason: &'a str,
    },
    /// A game window was asked for and could not be opened.
    WindowFailed {
        error: &'a str,
    },
}

impl StatusEvent<'_> {
    /// The event's name, the line's `e`.
    pub const fn name(&self) -> &'static str {
        match self {
            Self::Started => "started",
            Self::Connected => "connected",
            Self::CharacterList(_) => "characterList",
            Self::EnteredWorld { .. } => "enteredWorld",
            Self::CharacterCreated { .. } => "characterCreated",
            Self::CreationFailed { .. } => "creationFailed",
            Self::PluginLoaded { .. } => "pluginLoaded",
            Self::PluginFailed { .. } => "pluginFailed",
            Self::LoginCommandFailed { .. } => "loginCommandFailed",
            Self::Disconnected { .. } => "disconnected",
            Self::Exited { .. } => "exited",
            Self::Heartbeat => "heartbeat",
            Self::WindowOpened => "windowOpened",
            Self::WindowClosed { .. } => "windowClosed",
            Self::WindowFailed { .. } => "windowFailed",
        }
    }
}

/// The JSON line for one event, without the line ending.
pub fn format_status_line(session_id: &str, event: &StatusEvent<'_>, at: SystemTime) -> String {
    let mut line = String::with_capacity(96);
    let _ = write!(
        line,
        "{{\"v\":{STATUS_VOCABULARY_VERSION},\"e\":\"{}\",\"t\":\"{}\",\"sessionId\":",
        event.name(),
        Timestamp(at)
    );
    write_string(&mut line, session_id);
    let text = |line: &mut String, name: &str, value: &str| {
        let _ = write!(line, ",\"{name}\":");
        write_string(line, value);
    };
    match *event {
        StatusEvent::Started
        | StatusEvent::Connected
        | StatusEvent::WindowOpened
        | StatusEvent::Heartbeat => {}
        StatusEvent::CharacterList(roster) => {
            text(&mut line, "accountName", &roster.account_name);
            let _ = write!(
                line,
                ",\"slotCount\":{},\"characters\":[",
                roster.slot_count
            );
            for (index, entry) in roster.entries.iter().enumerate() {
                if index > 0 {
                    line.push(',');
                }
                let _ = write!(line, "{{\"id\":{},\"name\":", entry.id);
                write_string(&mut line, &entry.name);
                let _ = write!(line, ",\"secondsGreyedOut\":{}}}", entry.seconds_greyed_out);
            }
            line.push(']');
        }
        StatusEvent::EnteredWorld {
            character_id,
            character_name,
        } => {
            let _ = write!(line, ",\"characterId\":{character_id}");
            text(&mut line, "characterName", character_name);
        }
        StatusEvent::CharacterCreated { guid, name } => {
            let _ = write!(line, ",\"guid\":{guid}");
            text(&mut line, "name", name);
        }
        StatusEvent::CreationFailed { code, reason, name } => {
            let _ = write!(line, ",\"code\":{code}");
            text(&mut line, "reason", reason);
            text(&mut line, "name", name);
        }
        StatusEvent::PluginLoaded { plugin } => text(&mut line, "plugin", plugin),
        StatusEvent::PluginFailed { plugin, error } => {
            text(&mut line, "plugin", plugin);
            text(&mut line, "error", error);
        }
        StatusEvent::LoginCommandFailed {
            command_index,
            command,
            error,
        } => {
            let _ = write!(line, ",\"commandIndex\":{command_index}");
            text(&mut line, "command", command);
            text(&mut line, "error", error);
        }
        StatusEvent::Disconnected { reason } => text(&mut line, "reason", reason),
        StatusEvent::Exited {
            code,
            reason,
            server_message,
            account_in_use,
        } => {
            let _ = write!(line, ",\"code\":{code}");
            text(&mut line, "reason", reason);
            if let Some(message) = server_message {
                text(&mut line, "serverMessage", message);
            }
            if account_in_use {
                line.push_str(",\"accountInUse\":true");
            }
        }
        StatusEvent::WindowClosed { reason } => text(&mut line, "reason", reason),
        StatusEvent::WindowFailed { error } => text(&mut line, "error", error),
    }
    line.push('}');
    line
}

/// A UTC time written as `yyyy-MM-ddTHH:mm:ss.fffffff+00:00`.
#[derive(Debug, Clone, Copy)]
pub struct Timestamp(pub SystemTime);

impl fmt::Display for Timestamp {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        // Times before 1970 do not occur; they print as the epoch.
        let since = self.0.duration_since(UNIX_EPOCH).unwrap_or_default();
        let seconds = i64::try_from(since.as_secs()).unwrap_or(i64::MAX);
        let ticks = since.subsec_nanos() / 100;
        let days = seconds.div_euclid(86_400);
        let second_of_day = seconds.rem_euclid(86_400);
        let (year, month, day) = civil_from_days(days);
        write!(
            f,
            "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{ticks:07}+00:00",
            second_of_day / 3600,
            second_of_day / 60 % 60,
            second_of_day % 60
        )
    }
}

/// Days since 1970-01-01 to a proleptic Gregorian (year, month, day).
#[must_use]
pub fn civil_from_days(days: i64) -> (i64, i64, i64) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let day_of_era = z.rem_euclid(146_097);
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = if month_index < 10 {
        month_index + 3
    } else {
        month_index - 9
    };
    let year = year_of_era + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

/// Where status lines go.
pub trait StatusSink {
    /// Writes one line (the text has no line ending) and flushes it.
    fn write_line(&mut self, line: &str) -> io::Result<()>;
    /// Lets go of whatever the sink holds after a failure; the next line
    /// starts afresh.
    fn reset(&mut self) {}
    /// Names the destination for a diagnostic.
    fn describe(&self) -> String;
}

/// A status file. Opened on the first line for appending and held for the
/// session's lifetime; the parent directory is created if missing. Others
/// may read, write, rename or delete the file while it is held, so a watcher
/// can never take the session's ability to write away from it.
#[derive(Debug)]
pub struct FileSink {
    path: PathBuf,
    file: Option<File>,
    directory_ensured: bool,
}

impl FileSink {
    /// A sink for `path`, made absolute against the working directory now.
    pub fn new(path: &Path) -> Self {
        let base = std::env::current_dir().unwrap_or_default();
        Self {
            path: full_path(path, &base),
            file: None,
            directory_ensured: false,
        }
    }

    /// The absolute path written to.
    pub fn path(&self) -> &Path {
        &self.path
    }

    fn open(&mut self) -> io::Result<&mut File> {
        if self.file.is_none() {
            if !self.directory_ensured {
                if let Some(parent) = self.path.parent() {
                    std::fs::create_dir_all(parent)?;
                }
                self.directory_ensured = true;
            }
            self.file = Some(open_shared_append(&self.path)?);
        }
        match self.file.as_mut() {
            Some(file) => Ok(file),
            None => Err(io::Error::other("status file not open")),
        }
    }
}

#[cfg(windows)]
fn open_shared_append(path: &Path) -> io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    const FILE_SHARE_READ: u32 = 0x1;
    const FILE_SHARE_WRITE: u32 = 0x2;
    const FILE_SHARE_DELETE: u32 = 0x4;
    OpenOptions::new()
        .append(true)
        .create(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .open(path)
}

#[cfg(not(windows))]
fn open_shared_append(path: &Path) -> io::Result<File> {
    OpenOptions::new().append(true).create(true).open(path)
}

impl StatusSink for FileSink {
    fn write_line(&mut self, line: &str) -> io::Result<()> {
        let file = self.open()?;
        let mut bytes = Vec::with_capacity(line.len() + 1);
        bytes.extend_from_slice(line.as_bytes());
        bytes.push(b'\n');
        // One write per line, flushed: a watcher wants the event as it
        // happens and never half a line.
        file.write_all(&bytes)?;
        file.flush()
    }

    fn reset(&mut self) {
        self.file = None;
    }

    fn describe(&self) -> String {
        self.path.display().to_string()
    }
}

/// Status lines into any writer (a test buffer, a pipe).
#[derive(Debug)]
pub struct WriterSink<W>(pub W);

impl<W: Write> StatusSink for WriterSink<W> {
    fn write_line(&mut self, line: &str) -> io::Result<()> {
        self.0.write_all(line.as_bytes())?;
        self.0.write_all(b"\n")?;
        self.0.flush()
    }

    fn describe(&self) -> String {
        "status writer".to_owned()
    }
}

type Clock = Box<dyn Fn() -> SystemTime + Send>;
type Diagnostic = Box<dyn FnMut(&str) + Send>;

/// Writes one session's status events.
pub struct StatusWriter<S> {
    sink: Option<S>,
    clock: Clock,
    diagnostic: Diagnostic,
    failure_reported: bool,
    connected: bool,
    exited: bool,
    closed: bool,
}

impl<S> fmt::Debug for StatusWriter<S> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("StatusWriter")
            .field("enabled", &self.sink.is_some())
            .field("connected", &self.connected)
            .field("exited", &self.exited)
            .field("closed", &self.closed)
            .finish_non_exhaustive()
    }
}

impl StatusWriter<FileSink> {
    /// A writer for a session's `statusFile`; absent or blank makes a writer
    /// that writes nothing. Failures are reported on standard error.
    pub fn for_file(path: Option<&Path>) -> Self {
        let path = path.filter(|path| !path.to_string_lossy().trim().is_empty());
        Self::new(path.map(FileSink::new))
    }
}

impl<S: StatusSink> StatusWriter<S> {
    /// A writer into `sink`; `None` writes nothing. The system clock stamps
    /// lines; failures are reported on standard error.
    pub fn new(sink: Option<S>) -> Self {
        Self {
            sink,
            clock: Box::new(SystemTime::now),
            diagnostic: Box::new(|message| eprintln!("{message}")),
            failure_reported: false,
            connected: false,
            exited: false,
            closed: false,
        }
    }

    /// Replaces the clock that stamps each line.
    #[must_use]
    pub fn with_clock(mut self, clock: impl Fn() -> SystemTime + Send + 'static) -> Self {
        self.clock = Box::new(clock);
        self
    }

    /// Replaces where a write failure is reported (the host's own log).
    #[must_use]
    pub fn with_diagnostic(mut self, diagnostic: impl FnMut(&str) + Send + 'static) -> Self {
        self.diagnostic = Box::new(diagnostic);
        self
    }

    /// Whether events are written. A failed write does not turn this off.
    pub fn is_enabled(&self) -> bool {
        self.sink.is_some() && !self.closed
    }

    /// The sink, for inspection.
    pub fn sink(&self) -> Option<&S> {
        self.sink.as_ref()
    }

    /// The session started.
    pub fn started(&mut self, session_id: &str) {
        self.write(session_id, &StatusEvent::Started);
    }

    /// The session connected. A second connect first closes the first.
    pub fn connected(&mut self, session_id: &str) {
        if !self.accepting() {
            return;
        }
        if self.connected {
            if !self.try_write(
                session_id,
                &StatusEvent::Disconnected {
                    reason: "reconnect",
                },
            ) {
                return;
            }
            self.connected = false;
        }
        if self.try_write(session_id, &StatusEvent::Connected) {
            self.connected = true;
        }
    }

    /// The server sent the character list.
    pub fn character_list(&mut self, session_id: &str, roster: &RosterReport) {
        self.write(session_id, &StatusEvent::CharacterList(roster));
    }

    /// The session entered the world.
    pub fn entered_world(&mut self, session_id: &str, character_id: u32, character_name: &str) {
        self.write(
            session_id,
            &StatusEvent::EnteredWorld {
                character_id,
                character_name,
            },
        );
    }

    /// The server created a character.
    pub fn character_created(&mut self, session_id: &str, guid: u32, name: &str) {
        self.write(session_id, &StatusEvent::CharacterCreated { guid, name });
    }

    /// The server refused to create a character.
    pub fn creation_failed(&mut self, session_id: &str, code: u32, reason: &str, name: &str) {
        self.write(
            session_id,
            &StatusEvent::CreationFailed { code, reason, name },
        );
    }

    /// A plugin loaded.
    pub fn plugin_loaded(&mut self, session_id: &str, plugin: &str) {
        self.write(session_id, &StatusEvent::PluginLoaded { plugin });
    }

    /// A plugin failed to load.
    pub fn plugin_failed(&mut self, session_id: &str, plugin: &str, error: &str) {
        self.write(session_id, &StatusEvent::PluginFailed { plugin, error });
    }

    /// A login command failed; the session carries on.
    pub fn login_command_failed(
        &mut self,
        session_id: &str,
        command_index: i32,
        command: &str,
        error: &str,
    ) {
        self.write(
            session_id,
            &StatusEvent::LoginCommandFailed {
                command_index,
                command,
                error,
            },
        );
    }

    /// A game window opened on the session.
    pub fn window_opened(&mut self, session_id: &str) {
        self.write(session_id, &StatusEvent::WindowOpened);
    }

    /// The session's game window closed, for `reason`.
    pub fn window_closed(&mut self, session_id: &str, reason: &str) {
        self.write(session_id, &StatusEvent::WindowClosed { reason });
    }

    /// A game window could not be opened on the session.
    pub fn window_failed(&mut self, session_id: &str, error: &str) {
        self.write(session_id, &StatusEvent::WindowFailed { error });
    }

    /// The session is alive and making progress (see
    /// [`StatusEvent::Heartbeat`]).
    pub fn heartbeat(&mut self, session_id: &str) {
        self.write(session_id, &StatusEvent::Heartbeat);
    }

    /// The connection ended.
    pub fn disconnected(&mut self, session_id: &str, reason: &str) {
        if !self.accepting() {
            return;
        }
        if self.try_write(session_id, &StatusEvent::Disconnected { reason }) {
            self.connected = false;
        }
    }

    /// The session ended. Only the first call writes; an open connection is
    /// closed first with reason `process-exit`.
    pub fn exited(&mut self, session_id: &str, code: i32, reason: &str) {
        self.exited_by(session_id, code, reason, None);
    }

    /// As [`exited`](Self::exited), naming the server's refusal that ended
    /// the session, when one did.
    pub fn exited_by(
        &mut self,
        session_id: &str,
        code: i32,
        reason: &str,
        refusal: Option<ServerRefusalNote<'_>>,
    ) {
        if !self.accepting() {
            return;
        }
        if self.connected {
            if !self.try_write(
                session_id,
                &StatusEvent::Disconnected {
                    reason: "process-exit",
                },
            ) {
                return;
            }
            self.connected = false;
        }
        let event = StatusEvent::Exited {
            code,
            reason,
            server_message: refusal.map(|refusal| refusal.message),
            account_in_use: refusal.is_some_and(|refusal| refusal.account_in_use),
        };
        if self.try_write(session_id, &event) {
            self.exited = true;
        }
    }

    /// Lets go of the destination; nothing is written afterwards.
    pub fn close(&mut self) {
        if self.closed {
            return;
        }
        self.closed = true;
        if let Some(sink) = self.sink.as_mut() {
            sink.reset();
        }
    }

    fn accepting(&self) -> bool {
        self.is_enabled() && !self.exited
    }

    fn write(&mut self, session_id: &str, event: &StatusEvent<'_>) {
        if self.accepting() {
            self.try_write(session_id, event);
        }
    }

    fn try_write(&mut self, session_id: &str, event: &StatusEvent<'_>) -> bool {
        let line = format_status_line(session_id, event, (self.clock)());
        let Some(sink) = self.sink.as_mut() else {
            return false;
        };
        match sink.write_line(&line) {
            Ok(()) => {
                self.failure_reported = false;
                true
            }
            Err(error) => {
                // The handle may be the broken part: let it go, and let the
                // next line open a fresh one. No retry loop.
                sink.reset();
                if !self.failure_reported {
                    self.failure_reported = true;
                    let message = format!(
                        "[status-writer] could not write to '{}' ({error}); the next event will \
                         try again.",
                        sink.describe()
                    );
                    (self.diagnostic)(&message);
                }
                false
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    // Not an OpenAC test: heartbeats are written only when a launcher asks.
    #[test]
    fn heartbeats_are_written_only_when_asked() {
        assert!(heartbeat_requested(
            |name| (name == HEARTBEAT_VARIABLE).then(|| "1".into())
        ));
        assert!(!heartbeat_requested(|_| None));
        assert!(!heartbeat_requested(|_| Some("0".into())));
    }

    #[test]
    fn timestamps_use_seven_fraction_digits_and_utc_offset() {
        let at = UNIX_EPOCH + Duration::new(1_791_800_430, 123_456_789);
        assert_eq!(
            Timestamp(at).to_string(),
            "2026-10-12T10:20:30.1234567+00:00"
        );
        assert_eq!(
            Timestamp(UNIX_EPOCH).to_string(),
            "1970-01-01T00:00:00.0000000+00:00"
        );
        // A leap day.
        let leap = UNIX_EPOCH + Duration::from_secs(951_782_400);
        assert_eq!(
            Timestamp(leap).to_string(),
            "2000-02-29T00:00:00.0000000+00:00"
        );
    }
}
