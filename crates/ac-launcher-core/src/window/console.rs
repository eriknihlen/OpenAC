//! One windowless session's console: the end of what the session has
//! written (its log file, read again whenever it grew) and a line to send.

use std::io::{Read, Seek, SeekFrom};
use std::time::SystemTime;

use super::Orchestration;
use crate::launching::{SessionConsoleRun, read_line};

/// How much of the end of the log is shown.
pub const TAIL_BYTES: u64 = 256 * 1024;

/// How many of the newest lines are drawn; the file keeps more.
pub const MOST_LINES_SHOWN: usize = 1500;

/// A console window's state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionConsole {
    /// The session.
    pub session_id: String,
    /// The window's title: whose console this is.
    pub title: String,
    /// The end of the log.
    pub output: String,
    /// The line being typed.
    pub input: String,
    /// Why the last line was not sent, or empty.
    pub notice: String,
    /// Open (closing the window clears it).
    pub is_open: bool,
    shown_length: Option<u64>,
    shown_written_at: Option<SystemTime>,
}

impl SessionConsole {
    /// A console for `session_id`.
    pub fn new(session_id: impl Into<String>, title: impl Into<String>) -> Self {
        Self {
            session_id: session_id.into(),
            title: title.into(),
            output: String::new(),
            input: String::new(),
            notice: String::new(),
            is_open: true,
            shown_length: None,
            shown_written_at: None,
        }
    }

    /// Reads the log again if it changed; cheap when it has not. True when
    /// the output changed.
    pub fn refresh(&mut self, orchestrator: &dyn Orchestration) -> bool {
        let Some(path) = orchestrator.session_log_path(&self.session_id) else {
            return false;
        };
        let Ok(metadata) = std::fs::metadata(&path) else {
            return false;
        };
        let written = metadata.modified().ok();
        if self.shown_length == Some(metadata.len()) && self.shown_written_at == written {
            return false;
        }
        // The session is writing or rotating the file when this fails; the next poll reads it.
        let Ok(mut file) = std::fs::File::open(&path) else {
            return false;
        };
        let length = file.metadata().map_or(metadata.len(), |m| m.len());
        let start = length.saturating_sub(TAIL_BYTES);
        let mut bytes = Vec::new();
        if file.seek(SeekFrom::Start(start)).is_err() || file.read_to_end(&mut bytes).is_err() {
            return false;
        }
        let mut text = String::from_utf8_lossy(&bytes).into_owned();
        if start > 0 {
            // The cut fell inside a line; show whole lines only.
            if let Some(first_break) = text.find('\n') {
                text = text[first_break + 1..].to_owned();
            }
        }
        self.shown_length = Some(metadata.len());
        self.shown_written_at = written;
        self.output = text;
        true
    }

    /// The newest lines, split into coloured runs: with `verbose` every
    /// line the session wrote, otherwise only what a player would see (the
    /// lines the session marked: chat, the status box, what was typed and
    /// the console's answers).
    pub fn lines(&self, verbose: bool) -> Vec<Vec<SessionConsoleRun>> {
        let mut lines: Vec<&str> = self.output.split('\n').collect();
        // Nothing follows the line break that ends the log.
        if lines.last().is_some_and(|line| line.is_empty()) {
            lines.pop();
        }
        let mut out: Vec<Vec<SessionConsoleRun>> = lines
            .iter()
            .rev()
            .map(|line| read_line(line.trim_end_matches('\r')))
            .filter(|line| verbose || line.kind.is_some())
            .take(MOST_LINES_SHOWN)
            .map(|line| line.runs)
            .collect();
        out.reverse();
        out
    }

    /// Whether Send may run.
    pub fn can_send(&self) -> bool {
        !self.input.trim().is_empty()
    }

    /// Sends the typed line.
    pub fn send(&mut self, orchestrator: &dyn Orchestration) {
        let line = self.input.trim().to_owned();
        if line.is_empty() {
            return;
        }
        if orchestrator.try_send_console_line(&self.session_id, &line) {
            self.input.clear();
            self.notice.clear();
        } else {
            self.notice = "Not sent: the session is not running.".into();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// What a client asked for marks writes: its log unmarked, the lines a
    /// player would see behind their kind's mark.
    const OUTPUT: &str = "-- lifecycle: generation 1 InWorld\n\
        \u{1e}c[say] Bob says, \"hi\"\n\
        \u{1e}c[system] Welcome to the server\n\
        \u{1e}c  powered by ACEmulator\n\
        [s1] SessionEvent(EnteredWorld)\n\
        \u{1e}s[client] You are too fatigued to attack!\n\
        -- plugin acdream.mosstank: loaded\n\
        \u{1e}t/objects\n\
        \u{1e}r[s1] 12 objects known\n\
        \u{1e}r-- quitting (graceful logout)\n\
        window: opened\r\n";

    fn texts(console: &SessionConsole, verbose: bool) -> Vec<String> {
        console
            .lines(verbose)
            .iter()
            .map(|runs| runs.iter().map(|run| run.text.as_str()).collect())
            .collect()
    }

    fn console() -> SessionConsole {
        let mut console = SessionConsole::new("s1", "Console");
        console.output = OUTPUT.into();
        console
    }

    #[test]
    fn by_default_the_console_shows_chat_the_status_box_and_what_was_typed_and_answered() {
        assert_eq!(
            texts(&console(), false),
            [
                "[say] Bob says, \"hi\"",
                "[system] Welcome to the server",
                "  powered by ACEmulator",
                "[client] You are too fatigued to attack!",
                "> /objects",
                "[s1] 12 objects known",
                "-- quitting (graceful logout)",
            ]
        );
    }

    #[test]
    fn verbose_shows_every_line_without_its_mark() {
        let shown = texts(&console(), true);
        assert_eq!(shown.len(), 11);
        assert_eq!(shown[0], "-- lifecycle: generation 1 InWorld");
        assert_eq!(shown[1], "[say] Bob says, \"hi\"");
        assert_eq!(shown[10], "window: opened");
        assert!(shown.iter().all(|text| !text.contains('\u{1e}')));
    }

    #[test]
    fn the_newest_shown_lines_are_kept_when_there_are_too_many() {
        let mut console = SessionConsole::new("s1", "Console");
        console.output = (0..MOST_LINES_SHOWN + 10)
            .map(|n| format!("\u{1e}c[say] line {n}\nlog line {n}\n"))
            .collect();
        let shown = texts(&console, false);
        assert_eq!(shown.len(), MOST_LINES_SHOWN);
        assert_eq!(shown[0], "[say] line 10");
        assert_eq!(
            shown.last().map(String::as_str),
            Some(format!("[say] line {}", MOST_LINES_SHOWN + 9).as_str())
        );
    }
}
