//! The `@log` file: every chat line, worded as the chat box shows it,
//! appended to a file the player names until they run `@log` again.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};

use ac_core::chat::session_log::ensure_extension;

/// What a `@log` command did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatLogResult {
    /// A file is now open.
    pub opened: bool,
    /// A file that was open was closed.
    pub closed: bool,
    /// The name asked for, with `.txt` added when it had no extension.
    pub name: String,
    /// The name of the file that was closed.
    pub closed_name: Option<String>,
}

/// The open log file, if any. Relative names resolve against the base
/// directory; it and any directories in the name are created.
#[derive(Debug)]
pub struct ChatSessionLog {
    base_directory: PathBuf,
    file: Option<File>,
    current_name: Option<String>,
}

impl ChatSessionLog {
    /// Nothing open; names resolve against `base_directory`.
    #[must_use]
    pub fn new(base_directory: impl Into<PathBuf>) -> Self {
        Self {
            base_directory: base_directory.into(),
            file: None,
            current_name: None,
        }
    }

    /// The name of the open file.
    #[must_use]
    pub fn current_name(&self) -> Option<&str> {
        self.current_name.as_deref()
    }

    /// True while a file is open.
    #[must_use]
    pub fn is_open(&self) -> bool {
        self.file.is_some()
    }

    /// The directory relative names resolve against.
    #[must_use]
    pub fn base_directory(&self) -> &Path {
        &self.base_directory
    }

    /// Close what is open, then open `name` for appending. Returns the
    /// resolved name and whether it opened; a blank name opens nothing.
    pub fn open(&mut self, name: &str) -> (bool, String) {
        self.close();
        if name.trim().is_empty() {
            return (false, String::new());
        }
        let resolved = ensure_extension(name.trim());
        let given = Path::new(&resolved);
        let path = if given.is_absolute() {
            given.to_path_buf()
        } else {
            self.base_directory.join(given)
        };
        let opened = path
            .parent()
            .map_or(Ok(()), std::fs::create_dir_all)
            .and_then(|()| OpenOptions::new().create(true).append(true).open(&path));
        match opened {
            Ok(file) => {
                self.file = Some(file);
                self.current_name = Some(resolved.clone());
                (true, resolved)
            }
            Err(_) => (false, resolved),
        }
    }

    /// Close the open file; false when none was open.
    pub fn close(&mut self) -> bool {
        self.current_name = None;
        self.file.take().is_some()
    }

    /// Append one line: the optional timestamp, the text, a newline.
    ///
    /// A write that fails is dropped: the log is a copy of what the player
    /// already sees, and a full disk must not end the session.
    pub fn write(&mut self, timestamp_prefix: Option<&str>, text: &str) {
        if let Some(file) = &mut self.file {
            let line = format!("{}{text}\n", timestamp_prefix.unwrap_or_default());
            let _ = file.write_all(line.as_bytes());
        }
    }

    /// The `@log [name]` command: close what is open, then open `name`
    /// unless it is blank.
    pub fn set(&mut self, name: &str) -> ChatLogResult {
        let closed_name = self.current_name.clone();
        let closed = self.close();
        if name.trim().is_empty() {
            return ChatLogResult {
                opened: false,
                closed,
                name: String::new(),
                closed_name,
            };
        }
        let (opened, resolved) = self.open(name);
        ChatLogResult {
            opened,
            closed,
            name: resolved,
            closed_name,
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// A fresh directory under the system temp directory, removed on drop.
    pub(crate) struct TempDir(pub PathBuf);

    impl TempDir {
        pub(crate) fn new(tag: &str) -> Self {
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos());
            let path =
                std::env::temp_dir().join(format!("openac-{tag}-{}-{nanos}", std::process::id()));
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    // Golden behaviour: OpenAC src/AcDream.Core/Chat/ChatSessionLog.cs and
    // tests/AcDream.Core.Tests/Chat/ChatSessionLogTests.cs.

    #[test]
    fn a_bare_name_gains_txt_and_appends_across_opens() {
        let dir = TempDir::new("chatlog");
        let mut log = ChatSessionLog::new(&dir.0);
        let result = log.set("session");
        assert_eq!(
            result,
            ChatLogResult {
                opened: true,
                closed: false,
                name: "session.txt".into(),
                closed_name: None
            }
        );
        log.write(None, "first");
        let result = log.set("");
        assert!(result.closed && !result.opened);
        assert_eq!(result.closed_name.as_deref(), Some("session.txt"));
        log.write(None, "dropped while closed");
        log.set("session.txt");
        log.write(Some("1:02:03 "), "second");
        log.close();
        assert_eq!(
            std::fs::read_to_string(dir.0.join("session.txt")).unwrap(),
            "first\n1:02:03 second\n"
        );
    }

    #[test]
    fn subdirectories_in_the_name_are_created() {
        let dir = TempDir::new("chatlog-sub");
        let mut log = ChatSessionLog::new(&dir.0);
        let (opened, name) = log.open("logs.d/aclog");
        assert!(opened);
        assert_eq!(name, "logs.d/aclog.txt");
        log.write(None, "x");
        log.close();
        assert!(dir.0.join("logs.d").join("aclog.txt").exists());
    }

    #[test]
    fn opening_a_second_log_closes_the_first() {
        let dir = TempDir::new("chatlog-two");
        let mut log = ChatSessionLog::new(&dir.0);
        log.set("old");
        let result = log.set("new.log");
        assert!(result.opened && result.closed);
        assert_eq!(result.closed_name.as_deref(), Some("old.txt"));
        assert_eq!(log.current_name(), Some("new.log"));
    }

    #[test]
    fn an_unopenable_path_reports_failure() {
        let dir = TempDir::new("chatlog-bad");
        // A file where the directory should be.
        std::fs::write(dir.0.join("blocker"), b"").unwrap();
        let mut log = ChatSessionLog::new(&dir.0);
        let result = log.set("blocker/inner");
        assert!(!result.opened);
        assert_eq!(result.name, "blocker/inner.txt");
        assert!(!log.is_open());
    }
}
