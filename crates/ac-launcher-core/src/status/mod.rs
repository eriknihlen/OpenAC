//! A running session's status: the status file it appends to, read as it
//! grows; the reachability and population of servers; and the public
//! servers TreeStats lists ([`known_servers`]).

mod event;
mod health;
pub mod known_servers;

pub use event::{
    StatusCharacterEntry, StatusEnvelope, StatusEvent, StatusEventKind, parse_status_line,
};
pub use health::{
    POPULATION_URI, ReachabilityProbe, ServerHealthService, ServerHealthSnapshot,
    find_player_count, is_status_reply, parse_player_counts, status_request_datagram,
};
pub use known_servers::{KnownServer, KnownServerCatalog, KnownServerList, SERVERS_URI};

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

/// Something that yields the status events written since the last call.
pub trait StatusEventSource: Send {
    /// Events completed since the last call; never fails (an unreadable
    /// file reads as nothing new).
    fn read_new_events(&mut self) -> Vec<StatusEvent>;
}

/// Creates the event source for a status file.
pub trait StatusEventSourceFactory: Send + Sync {
    /// A source reading `path`.
    fn create(&self, path: &Path) -> Box<dyn StatusEventSource>;
}

/// Creates [`StatusFileTailer`]s.
#[derive(Debug, Clone, Copy, Default)]
pub struct StatusFileTailerFactory;

impl StatusEventSourceFactory for StatusFileTailerFactory {
    fn create(&self, path: &Path) -> Box<dyn StatusEventSource> {
        Box::new(StatusFileTailer::new(path))
    }
}

/// Follows a status file another process appends to: returns each complete
/// line once, keeps a partial last line for the next call, and starts over
/// when the file shrinks (replaced or truncated).
#[derive(Debug, Clone)]
pub struct StatusFileTailer {
    path: PathBuf,
    position: u64,
}

impl StatusFileTailer {
    /// A tailer at the start of `path` (which need not exist yet).
    pub fn new(path: &Path) -> Self {
        Self {
            path: path.to_path_buf(),
            position: 0,
        }
    }

    fn read_core(&mut self) -> std::io::Result<Vec<StatusEvent>> {
        if !self.path.exists() {
            return Ok(Vec::new());
        }
        // The standard library opens files shared for reading, writing and
        // deletion on Windows, so a host appending to the file is not
        // disturbed.
        let mut file = File::open(&self.path)?;
        let length = file.metadata()?.len();
        if length < self.position {
            self.position = 0;
        }
        if length == self.position {
            return Ok(Vec::new());
        }
        file.seek(SeekFrom::Start(self.position))?;
        let mut buffer = Vec::new();
        file.take(length - self.position).read_to_end(&mut buffer)?;
        let mut events = Vec::new();
        let mut line_start = 0;
        let mut consumed = 0;
        for index in 0..buffer.len() {
            if buffer[index] != b'\n' {
                continue;
            }
            let mut line_end = index;
            if line_end > line_start && buffer[line_end - 1] == b'\r' {
                line_end -= 1;
            }
            if line_end > line_start {
                let line = String::from_utf8_lossy(&buffer[line_start..line_end]);
                events.push(parse_status_line(&line));
            }
            line_start = index + 1;
            consumed = line_start;
        }
        self.position += consumed as u64;
        Ok(events)
    }
}

impl StatusEventSource for StatusFileTailer {
    fn read_new_events(&mut self) -> Vec<StatusEvent> {
        self.read_core().unwrap_or_default()
    }
}
