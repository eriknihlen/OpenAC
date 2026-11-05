//! What reloads a plugin besides `/plugin reload`: a change to its code,
//! markup or `plugin.json`, once those files have gone
//! [`QUIET_PERIOD_SECONDS`] without another change (an update writes several
//! files; the plugin reloads once, after the last of them).
//!
//! The plugin folders are looked at every [`POLL_INTERVAL_SECONDS`] of
//! session time. Only what makes up the plugin counts, so a plugin writing
//! its own logs or data beside its code does not hold its reload back
//! forever, and the player's folders (`data`, and MossTank's `vtank`) never
//! count.

use std::path::{Path, PathBuf};
use std::time::SystemTime;

use ac_host::paths::is_player_folder;

use crate::discovery::MANIFEST_FILE;

/// How long a plugin's code, markup and `plugin.json` must go without a
/// change, after one of them changed, before the plugin is reloaded.
pub const QUIET_PERIOD_SECONDS: f64 = 1.0;

/// How often the watched plugin folders are looked at, in seconds of
/// session time.
pub const POLL_INTERVAL_SECONDS: f64 = 0.25;

/// One package file as last seen: its path, length and write time.
type Seen = (PathBuf, u64, Option<SystemTime>);

/// Whether a file is part of what a plugin is: its manifest, its code or
/// its markup.
fn is_package_file(path: &Path) -> bool {
    let Some(name) = path.file_name().map(|n| n.to_string_lossy()) else {
        return false;
    };
    if name.eq_ignore_ascii_case(MANIFEST_FILE) {
        return true;
    }
    path.extension().is_some_and(|extension| {
        let extension = extension.to_string_lossy();
        extension.eq_ignore_ascii_case("wasm") || extension.eq_ignore_ascii_case("xml")
    })
}

/// The package files of the plugin folder `directory`, sorted, or `None`
/// when the folder is not there.
fn package_files(directory: &Path) -> Option<Vec<Seen>> {
    if !directory.is_dir() {
        return None;
    }
    let mut files = Vec::new();
    let mut pending = vec![directory.to_path_buf()];
    while let Some(folder) = pending.pop() {
        let Ok(entries) = std::fs::read_dir(&folder) else {
            continue;
        };
        for entry in entries.filter_map(Result::ok) {
            let path = entry.path();
            let Ok(metadata) = entry.metadata() else {
                continue;
            };
            if metadata.is_dir() {
                let players =
                    folder == directory && is_player_folder(&entry.file_name().to_string_lossy());
                if !players {
                    pending.push(path);
                }
            } else if is_package_file(&path) {
                files.push((path, metadata.len(), metadata.modified().ok()));
            }
        }
    }
    files.sort();
    Some(files)
}

/// The same folder (compared ignoring case on Windows, as OpenAC compares
/// paths).
fn same_folder(a: &Path, b: &Path) -> bool {
    if cfg!(windows) {
        a.to_string_lossy()
            .eq_ignore_ascii_case(&b.to_string_lossy())
    } else {
        a == b
    }
}

#[derive(Debug)]
struct Watched {
    directory: PathBuf,
    id: String,
    seen: Option<Vec<Seen>>,
}

/// The plugin folders a session watches and the changes waiting for quiet.
#[derive(Debug, Default)]
pub(crate) struct FolderWatch {
    watched: Vec<Watched>,
    /// Per plugin id, the session time its files were last seen changing.
    last_change: Vec<(String, f64)>,
    now: f64,
    next_poll: f64,
}

impl FolderWatch {
    /// Records that changes to `directory` belong to plugin `id`. A folder
    /// already watched keeps what was last seen of it.
    pub(crate) fn watch(&mut self, directory: &Path, id: &str) {
        let directory = std::path::absolute(directory).unwrap_or_else(|_| directory.to_path_buf());
        if let Some(watched) = self
            .watched
            .iter_mut()
            .find(|w| same_folder(&w.directory, &directory))
        {
            id.clone_into(&mut watched.id);
            return;
        }
        let seen = package_files(&directory);
        self.watched.push(Watched {
            directory,
            id: id.to_owned(),
            seen,
        });
    }

    /// Whether a change to plugin `id`'s files was seen and is waiting for
    /// them to go quiet.
    pub(crate) fn has_pending(&self, id: &str) -> bool {
        self.last_change
            .iter()
            .any(|(pending, _)| pending.eq_ignore_ascii_case(id))
    }

    /// `delta_seconds` more of session time: looks at the folders when a
    /// look is due and returns the plugins whose files have now been quiet
    /// for the quiet period since they changed.
    pub(crate) fn advance(&mut self, delta_seconds: f64) -> Vec<String> {
        if delta_seconds.is_finite() && delta_seconds > 0.0 {
            self.now += delta_seconds;
        }
        if self.watched.is_empty() {
            return Vec::new();
        }
        if self.now >= self.next_poll {
            self.next_poll = self.now + POLL_INTERVAL_SECONDS;
            self.poll();
        }
        let now = self.now;
        let mut due = Vec::new();
        self.last_change.retain(|(id, at)| {
            if now - *at < QUIET_PERIOD_SECONDS {
                return true;
            }
            due.push(id.clone());
            false
        });
        due
    }

    fn poll(&mut self) {
        for index in 0..self.watched.len() {
            let now = package_files(&self.watched[index].directory);
            let watched = &mut self.watched[index];
            if now == watched.seen {
                continue;
            }
            // A folder going away is not a new copy; one appearing is.
            let counts = now.is_some();
            watched.seen = now;
            if counts {
                let id = watched.id.clone();
                self.note_change(&id);
            }
        }
    }

    fn note_change(&mut self, id: &str) {
        let now = self.now;
        if let Some(entry) = self
            .last_change
            .iter_mut()
            .find(|(pending, _)| pending.eq_ignore_ascii_case(id))
        {
            entry.1 = now;
        } else {
            self.last_change.push((id.to_owned(), now));
        }
    }
}

#[cfg(test)]
mod tests {
    //! OpenAC `PluginReloadTests` (the quiet period, the player's files)
    //! against the folder watch alone.

    use super::*;

    struct Folder(PathBuf);

    impl Folder {
        fn new(tag: &str) -> Self {
            let path = std::env::temp_dir().join(format!(
                "ac-plugin-reload-watch-{tag}-{}",
                std::process::id()
            ));
            let _ = std::fs::remove_dir_all(&path);
            std::fs::create_dir_all(&path).unwrap();
            std::fs::write(path.join("plugin.json"), "{}").unwrap();
            std::fs::write(path.join("hello.wasm"), "one").unwrap();
            Self(path)
        }
    }

    impl Drop for Folder {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    // OpenAC ChangingTheEntryAssemblyReloadsThePluginOnceItsFolderIsQuiet.
    #[test]
    fn a_code_change_is_due_once_the_folder_has_been_quiet() {
        let folder = Folder::new("quiet");
        let mut watch = FolderWatch::default();
        watch.watch(&folder.0, "openac.hello");
        assert!(watch.advance(0.0).is_empty());
        std::fs::write(folder.0.join("hello.wasm"), "two, longer").unwrap();
        assert!(watch.advance(0.25).is_empty());
        assert!(watch.has_pending("OPENAC.HELLO"));
        assert!(watch.advance(0.5).is_empty());
        assert_eq!(watch.advance(0.5), ["openac.hello"]);
        assert!(!watch.has_pending("openac.hello"));
        assert!(watch.advance(2.0).is_empty());
    }

    // OpenAC WritingThePlayersFilesNeitherMarksNorHoldsBackAReload.
    #[test]
    fn the_players_files_and_data_beside_the_code_never_count() {
        let folder = Folder::new("players");
        let mut watch = FolderWatch::default();
        watch.watch(&folder.0, "openac.hello");
        let files = folder.0.join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME);
        std::fs::create_dir_all(&files).unwrap();
        std::fs::write(files.join("plugin.json"), "{}").unwrap();
        std::fs::write(files.join("hello.wasm"), "not the plugin").unwrap();
        let vtank = folder.0.join(ac_host::paths::VTANK_FOLDER_NAME);
        std::fs::create_dir_all(&vtank).unwrap();
        std::fs::write(vtank.join("loot.xml"), "<shared/>").unwrap();
        std::fs::write(folder.0.join("plugin.log"), "a line").unwrap();
        assert!(watch.advance(0.3).is_empty());
        assert!(!watch.has_pending("openac.hello"));

        std::fs::write(folder.0.join("plugin.json"), "{ }").unwrap();
        assert!(watch.advance(0.3).is_empty());
        assert!(watch.has_pending("openac.hello"));
        assert!(watch.advance(0.6).is_empty());
        std::fs::write(files.join("state.json"), "{\"saved\":true}").unwrap();
        std::fs::write(folder.0.join("plugin.log"), "another line").unwrap();
        assert_eq!(watch.advance(0.5), ["openac.hello"]);
    }

    #[test]
    fn a_folder_going_away_is_not_a_change_and_coming_back_is() {
        let folder = Folder::new("away");
        let mut watch = FolderWatch::default();
        watch.watch(&folder.0, "openac.hello");
        std::fs::remove_dir_all(&folder.0).unwrap();
        assert!(watch.advance(0.3).is_empty());
        assert!(!watch.has_pending("openac.hello"));
        std::fs::create_dir_all(&folder.0).unwrap();
        std::fs::write(folder.0.join("plugin.json"), "{}").unwrap();
        assert!(watch.advance(0.3).is_empty());
        assert!(watch.has_pending("openac.hello"));
    }
}
