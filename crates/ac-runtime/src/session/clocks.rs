//! The clocks the session's owners read, apart from the session state that
//! keeps them, so the owners build without the session runner.

use std::time::Duration;

/// The two clocks the owners read.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Clocks {
    /// Wall clock, UTC milliseconds since the Unix epoch (chat lines, the
    /// journal, the options' auto-save).
    pub wall_unix_ms: i64,
    /// Session time (use pacing, appraisal limits, appraisal stamps).
    pub session: Duration,
}
