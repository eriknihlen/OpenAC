//! The native storage: each key is a file at the path the client has always
//! used for it.

use std::io;
use std::path::{Path, PathBuf};

use super::{Presence, UiStorage, keys};

/// How a file is written.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WriteMode {
    /// Create its folder and write the file in place.
    InPlace,
    /// Create its folder, write a temporary file beside it and rename that
    /// over the file, so a reader never sees half of it.
    Replace,
}

#[derive(Debug, Clone)]
struct Place {
    /// The key, or for a folder the prefix its keys start with.
    key: String,
    path: PathBuf,
    folder: bool,
    mode: WriteMode,
}

/// Keys mapped to files: single files by their exact key, and folders whose
/// keys are a prefix followed by a plain file name. A key mapped to nothing
/// reads as not found and cannot be written.
#[derive(Debug, Clone, Default)]
pub struct FileUiStorage {
    places: Vec<Place>,
}

impl FileUiStorage {
    /// No keys mapped.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// `key` is the file at `path`.
    #[must_use]
    pub fn with_file(mut self, key: &str, path: impl Into<PathBuf>, mode: WriteMode) -> Self {
        self.places.push(Place {
            key: key.to_owned(),
            path: path.into(),
            folder: false,
            mode,
        });
        self
    }

    /// Each key `prefix` + a plain file name is that file in `directory`.
    #[must_use]
    pub fn with_folder(
        mut self,
        prefix: &str,
        directory: impl Into<PathBuf>,
        mode: WriteMode,
    ) -> Self {
        self.places.push(Place {
            key: prefix.to_owned(),
            path: directory.into(),
            folder: true,
            mode,
        });
        self
    }

    /// The key configuration's files where the client keeps them: the
    /// bindings at `bindings_file`, the unmapped keys and the active
    /// profile's name beside it, and the `.keymap` profiles in
    /// `keymap_directory`.
    #[must_use]
    pub fn for_key_bindings(bindings_file: &Path, keymap_directory: &Path) -> Self {
        let config = bindings_file.parent().unwrap_or(Path::new("."));
        Self::new()
            .with_file(keys::KEY_BINDINGS, bindings_file, WriteMode::InPlace)
            .with_file(
                keys::UNMAPPED_KEY_BINDINGS,
                crate::input::unmapped::unmapped_path(bindings_file),
                WriteMode::InPlace,
            )
            .with_file(
                keys::ACTIVE_KEYMAP,
                config.join("active-keymap.txt"),
                WriteMode::Replace,
            )
            .with_folder(keys::KEYMAPS, keymap_directory, WriteMode::Replace)
    }

    /// The style sheet in the game folder `game_directory` too.
    #[must_use]
    pub fn with_game_folder(self, game_directory: &Path) -> Self {
        self.with_file(
            keys::CONTROLS_INI,
            game_directory.join("controls").join("controls.ini"),
            WriteMode::InPlace,
        )
    }

    /// The file `key` maps to, and how it is written.
    #[must_use]
    pub fn path_of(&self, key: &str) -> Option<(PathBuf, WriteMode)> {
        if let Some(place) = self.places.iter().find(|p| !p.folder && p.key == key) {
            return Some((place.path.clone(), place.mode));
        }
        self.places.iter().filter(|p| p.folder).find_map(|place| {
            let name = key.strip_prefix(place.key.as_str())?;
            plain_name(name).then(|| (place.path.join(name), place.mode))
        })
    }

    fn unmapped(key: &str) -> io::Error {
        io::Error::new(
            io::ErrorKind::Unsupported,
            format!("no file is kept for '{key}'"),
        )
    }
}

/// One file name: not empty, no folder separator, not a folder reference.
fn plain_name(name: &str) -> bool {
    !name.is_empty() && name != "." && name != ".." && !name.contains(['/', '\\'])
}

impl UiStorage for FileUiStorage {
    fn get(&self, key: &str) -> io::Result<Vec<u8>> {
        match self.path_of(key) {
            Some((path, _)) => std::fs::read(path),
            None => Err(io::Error::new(
                io::ErrorKind::NotFound,
                format!("no file is kept for '{key}'"),
            )),
        }
    }

    fn put(&mut self, key: &str, bytes: &[u8]) -> io::Result<()> {
        let (path, mode) = self.path_of(key).ok_or_else(|| Self::unmapped(key))?;
        if let Some(dir) = path.parent().filter(|d| !d.as_os_str().is_empty()) {
            std::fs::create_dir_all(dir)?;
        }
        match mode {
            WriteMode::InPlace => std::fs::write(&path, bytes),
            WriteMode::Replace => replace(&path, bytes),
        }
    }

    fn remove(&mut self, key: &str) -> io::Result<()> {
        let (path, _) = self.path_of(key).ok_or_else(|| Self::unmapped(key))?;
        match std::fs::remove_file(path) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
            other => other,
        }
    }

    fn list(&self, prefix: &str) -> io::Result<Vec<String>> {
        let mut found = Vec::new();
        for place in &self.places {
            if !place.folder {
                if place.key.starts_with(prefix) && place.path.is_file() {
                    found.push(place.key.clone());
                }
                continue;
            }
            if !(place.key.starts_with(prefix) || prefix.starts_with(place.key.as_str())) {
                continue;
            }
            if !place.path.is_dir() {
                continue;
            }
            for entry in std::fs::read_dir(&place.path)? {
                let entry = entry?;
                let name = entry.file_name().to_string_lossy().into_owned();
                let key = format!("{}{name}", place.key);
                if entry.file_type()?.is_file() && key.starts_with(prefix) {
                    found.push(key);
                }
            }
        }
        found.sort();
        found.dedup();
        Ok(found)
    }

    fn presence(&self, key: &str) -> Option<Presence> {
        let (path, _) = self.path_of(key)?;
        let meta = std::fs::metadata(path).ok()?;
        Some(Presence {
            file: meta.is_file(),
            read_only: meta.permissions().readonly(),
        })
    }
}

/// Write `content` to a temporary file beside `path` and rename it over
/// `path`; the temporary file is removed when that fails.
fn replace(path: &Path, content: &[u8]) -> io::Result<()> {
    let temp = path.with_extension(format!(
        "tmp-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_nanos())
    ));
    let written = std::fs::write(&temp, content).and_then(|()| std::fs::rename(&temp, path));
    if written.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    written
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new(name: &str) -> Self {
            let path = std::env::temp_dir().join(format!(
                "openac-ui-storage-{name}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_or(0, |d| d.as_nanos())
            ));
            Self(path)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn the_key_configuration_keys_are_the_files_the_client_always_used() {
        let config = Path::new("C:/config");
        let game = Path::new("C:/game");
        let s = FileUiStorage::for_key_bindings(&config.join("keybinds.json"), game)
            .with_game_folder(game);
        let path = |key: &str| s.path_of(key);
        assert_eq!(
            path(keys::KEY_BINDINGS),
            Some((config.join("keybinds.json"), WriteMode::InPlace))
        );
        assert_eq!(
            path(keys::UNMAPPED_KEY_BINDINGS),
            Some((config.join("keybinds.unmapped.json"), WriteMode::InPlace))
        );
        assert_eq!(
            path(keys::ACTIVE_KEYMAP),
            Some((config.join("active-keymap.txt"), WriteMode::Replace))
        );
        assert_eq!(
            path("keymaps/friends.keymap"),
            Some((game.join("friends.keymap"), WriteMode::Replace))
        );
        assert_eq!(
            path(keys::CONTROLS_INI),
            Some((
                game.join("controls").join("controls.ini"),
                WriteMode::InPlace
            ))
        );
        // A key under the folder names one plain file in it.
        for bad in [
            "keymaps/",
            "keymaps/a/b",
            "keymaps/..",
            "keymaps/.",
            "keymaps/a\\b",
        ] {
            assert_eq!(path(bad), None, "{bad}");
        }
        assert_eq!(path("settings.json"), None);
    }

    #[test]
    fn a_bindings_file_of_another_name_keeps_its_unmapped_file_beside_it() {
        let s = FileUiStorage::for_key_bindings(Path::new("cfg/mine.json"), Path::new("k"));
        assert_eq!(
            s.path_of(keys::UNMAPPED_KEY_BINDINGS).map(|p| p.0),
            Some(Path::new("cfg").join("mine.unmapped.json"))
        );
        // A bare file name: the active profile is remembered in the
        // current folder, as before.
        let s = FileUiStorage::for_key_bindings(Path::new("keybinds.json"), Path::new("k"));
        assert_eq!(
            s.path_of(keys::ACTIVE_KEYMAP).map(|p| p.0),
            Some(Path::new("").join("active-keymap.txt"))
        );
    }

    #[test]
    fn files_are_read_written_listed_and_removed_where_the_keys_map() {
        let root = TempDir::new("files");
        let config = root.0.join("config");
        let profiles = root.0.join("profiles");
        let mut s = FileUiStorage::for_key_bindings(&config.join("keybinds.json"), &profiles);
        assert_eq!(
            s.get(keys::KEY_BINDINGS).unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
        assert_eq!(s.list(keys::KEYMAPS).unwrap(), Vec::<String>::new());
        // Both folders are created on the first write.
        s.put(keys::KEY_BINDINGS, b"{ }\n").unwrap();
        s.put("keymaps/b.keymap", b"B").unwrap();
        s.put("keymaps/A.keymap", b"A").unwrap();
        s.put(keys::ACTIVE_KEYMAP, b"A.keymap\n").unwrap();
        assert_eq!(
            std::fs::read(config.join("keybinds.json")).unwrap(),
            b"{ }\n"
        );
        assert_eq!(std::fs::read(profiles.join("b.keymap")).unwrap(), b"B");
        assert_eq!(
            std::fs::read(config.join("active-keymap.txt")).unwrap(),
            b"A.keymap\n"
        );
        assert_eq!(s.get("keymaps/A.keymap").unwrap(), b"A");
        // Folders and files of other folders are not listed; a replaced
        // file leaves no temporary file behind.
        std::fs::create_dir_all(profiles.join("sub.keymap")).unwrap();
        assert_eq!(
            s.list(keys::KEYMAPS).unwrap(),
            ["keymaps/A.keymap", "keymaps/b.keymap"]
        );
        let mut on_disk: Vec<_> = std::fs::read_dir(&profiles)
            .unwrap()
            .map(|e| e.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        on_disk.sort();
        assert_eq!(on_disk, ["A.keymap", "b.keymap", "sub.keymap"]);
        assert_eq!(
            s.list("").unwrap(),
            [
                "active-keymap.txt",
                "keybinds.json",
                "keymaps/A.keymap",
                "keymaps/b.keymap"
            ]
        );
        assert_eq!(
            s.presence("keymaps/b.keymap"),
            Some(Presence {
                file: true,
                read_only: false
            })
        );
        assert_eq!(
            s.presence("keymaps/sub.keymap"),
            Some(Presence {
                file: false,
                read_only: false
            })
        );
        assert_eq!(s.presence("keymaps/none.keymap"), None);
        s.remove("keymaps/b.keymap").unwrap();
        s.remove("keymaps/b.keymap").unwrap();
        assert!(!profiles.join("b.keymap").exists());
        assert_eq!(
            s.put("settings.json", b"{}").unwrap_err().kind(),
            io::ErrorKind::Unsupported
        );
        assert_eq!(
            s.get("settings.json").unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
    }

    #[test]
    fn a_read_only_file_says_so() {
        let root = TempDir::new("read-only");
        let mut s = FileUiStorage::new().with_folder(keys::KEYMAPS, &root.0, WriteMode::Replace);
        s.put("keymaps/locked.keymap", b"x").unwrap();
        let path = root.0.join("locked.keymap");
        let mut permissions = std::fs::metadata(&path).unwrap().permissions();
        permissions.set_readonly(true);
        std::fs::set_permissions(&path, permissions.clone()).unwrap();
        assert_eq!(
            s.presence("keymaps/locked.keymap"),
            Some(Presence {
                file: true,
                read_only: true
            })
        );
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
        std::fs::set_permissions(&path, permissions).unwrap();
    }
}
