//! Where the panels keep the player's own files (the key bindings, the key
//! map profiles, the unmapped keys, the style sheet), by key.
//!
//! The panels read and write bytes through [`UiStorage`] and never touch a
//! file system themselves. Natively [`FileUiStorage`] maps each key to the
//! file the client has always used, so nothing on disk moves; a host
//! without a file system (the browser) fills a [`MemoryUiStorage`] with the
//! values it keeps and takes the writes back out to keep them.

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::io;
use std::rc::Rc;

#[cfg(not(target_arch = "wasm32"))]
mod file;
#[cfg(not(target_arch = "wasm32"))]
pub use file::{FileUiStorage, WriteMode};

/// The keys the panels use.
pub mod keys {
    /// The player's key bindings (JSON).
    pub const KEY_BINDINGS: &str = "keybinds.json";
    /// The bindings of retail actions the client has no action for (JSON).
    pub const UNMAPPED_KEY_BINDINGS: &str = "keybinds.unmapped.json";
    /// The active key map profile's file name (one line of text).
    pub const ACTIVE_KEYMAP: &str = "active-keymap.txt";
    /// The key map profiles: one key per profile, this prefix followed by
    /// the profile's file name (`keymaps/openac.keymap`).
    pub const KEYMAPS: &str = "keymaps/";
    /// The player's style sheet in the game folder.
    pub const CONTROLS_INI: &str = "controls/controls.ini";
}

/// What is stored under a key, when something is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Presence {
    /// A value (a regular file), not something else of that name (a
    /// folder).
    pub file: bool,
    /// It may not be written.
    pub read_only: bool,
}

/// Bytes by key. A key is `/`-separated text ([`keys`]).
///
/// Reads and writes report failures as [`io::Error`]s, so a file store's
/// messages are the file system's own; a key with nothing stored reads as
/// [`io::ErrorKind::NotFound`].
pub trait UiStorage {
    /// The bytes stored under `key`.
    ///
    /// # Errors
    /// [`io::ErrorKind::NotFound`] when nothing is stored there; any other
    /// error when it cannot be read.
    fn get(&self, key: &str) -> io::Result<Vec<u8>>;

    /// Store `bytes` under `key`, replacing what was there.
    ///
    /// # Errors
    /// It cannot be written (read only: [`io::ErrorKind::PermissionDenied`]).
    fn put(&mut self, key: &str, bytes: &[u8]) -> io::Result<()>;

    /// Remove what is stored under `key` (nothing stored is no error).
    ///
    /// # Errors
    /// It cannot be removed.
    fn remove(&mut self, key: &str) -> io::Result<()>;

    /// Every key starting with `prefix` that holds a value, sorted.
    ///
    /// # Errors
    /// The keys cannot be listed.
    fn list(&self, prefix: &str) -> io::Result<Vec<String>>;

    /// Whether something is stored under `key`, without reading it.
    fn presence(&self, key: &str) -> Option<Presence>;
}

/// A storage shared with its host: the host keeps one handle to take the
/// writes and hands the window another.
impl<T: UiStorage + ?Sized> UiStorage for Rc<RefCell<T>> {
    fn get(&self, key: &str) -> io::Result<Vec<u8>> {
        self.borrow().get(key)
    }

    fn put(&mut self, key: &str, bytes: &[u8]) -> io::Result<()> {
        self.borrow_mut().put(key, bytes)
    }

    fn remove(&mut self, key: &str) -> io::Result<()> {
        self.borrow_mut().remove(key)
    }

    fn list(&self, prefix: &str) -> io::Result<Vec<String>> {
        self.borrow().list(prefix)
    }

    fn presence(&self, key: &str) -> Option<Presence> {
        self.borrow().presence(key)
    }
}

/// One change to a [`MemoryUiStorage`], for its host to keep.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StorageWrite {
    /// `bytes` were stored under `key`.
    Put {
        /// The key.
        key: String,
        /// Its new bytes.
        bytes: Vec<u8>,
    },
    /// What was stored under `key` was removed.
    Remove {
        /// The key.
        key: String,
    },
}

/// Values in memory, with a journal of the writes since the host last took
/// them ([`MemoryUiStorage::take_writes`]).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MemoryUiStorage {
    values: BTreeMap<String, Vec<u8>>,
    writes: Vec<StorageWrite>,
}

impl MemoryUiStorage {
    /// Nothing stored.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// A value the host holds (it is not journaled: the host has it).
    pub fn insert(&mut self, key: impl Into<String>, bytes: impl Into<Vec<u8>>) {
        self.values.insert(key.into(), bytes.into());
    }

    /// The bytes under `key`.
    #[must_use]
    pub fn value(&self, key: &str) -> Option<&[u8]> {
        self.values.get(key).map(Vec::as_slice)
    }

    /// The writes since the last call, in order.
    pub fn take_writes(&mut self) -> Vec<StorageWrite> {
        std::mem::take(&mut self.writes)
    }
}

impl UiStorage for MemoryUiStorage {
    fn get(&self, key: &str) -> io::Result<Vec<u8>> {
        self.values
            .get(key)
            .cloned()
            .ok_or_else(|| io::ErrorKind::NotFound.into())
    }

    fn put(&mut self, key: &str, bytes: &[u8]) -> io::Result<()> {
        self.values.insert(key.to_owned(), bytes.to_vec());
        self.writes.push(StorageWrite::Put {
            key: key.to_owned(),
            bytes: bytes.to_vec(),
        });
        Ok(())
    }

    fn remove(&mut self, key: &str) -> io::Result<()> {
        if self.values.remove(key).is_some() {
            self.writes.push(StorageWrite::Remove {
                key: key.to_owned(),
            });
        }
        Ok(())
    }

    fn list(&self, prefix: &str) -> io::Result<Vec<String>> {
        Ok(self
            .values
            .range::<str, _>((
                std::ops::Bound::Included(prefix),
                std::ops::Bound::Unbounded,
            ))
            .map(|(key, _)| key)
            .take_while(|key| key.starts_with(prefix))
            .cloned()
            .collect())
    }

    fn presence(&self, key: &str) -> Option<Presence> {
        self.values.contains_key(key).then_some(Presence {
            file: true,
            read_only: false,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_memory_store_gets_puts_removes_and_lists() {
        let mut s = MemoryUiStorage::new();
        assert_eq!(
            s.get("keybinds.json").unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
        assert_eq!(s.presence("keybinds.json"), None);
        s.put("keybinds.json", b"{}").unwrap();
        s.put("keymaps/b.keymap", b"B").unwrap();
        s.put("keymaps/a.keymap", b"A").unwrap();
        s.put("keymapsx", b"not under the prefix").unwrap();
        assert_eq!(s.get("keybinds.json").unwrap(), b"{}");
        assert_eq!(
            s.presence("keymaps/a.keymap"),
            Some(Presence {
                file: true,
                read_only: false
            })
        );
        assert_eq!(
            s.list("keymaps/").unwrap(),
            ["keymaps/a.keymap", "keymaps/b.keymap"]
        );
        s.put("keymaps/a.keymap", b"A2").unwrap();
        assert_eq!(s.value("keymaps/a.keymap"), Some(&b"A2"[..]));
        s.remove("keymaps/a.keymap").unwrap();
        s.remove("keymaps/never.keymap").unwrap();
        assert_eq!(s.list("keymaps/").unwrap(), ["keymaps/b.keymap"]);
        assert_eq!(
            s.get("keymaps/a.keymap").unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
    }

    #[test]
    fn a_memory_store_journals_its_writes_but_not_what_the_host_put_in() {
        let mut s = MemoryUiStorage::new();
        s.insert("controls/controls.ini", b"[title]\n".to_vec());
        s.insert("keymaps/old.keymap", b"x".to_vec());
        assert!(s.take_writes().is_empty());
        assert_eq!(s.get("controls/controls.ini").unwrap(), b"[title]\n");
        s.put("active-keymap.txt", b"a.keymap\n").unwrap();
        s.remove("keymaps/old.keymap").unwrap();
        // Removing nothing changes nothing, so there is nothing to keep.
        s.remove("keymaps/old.keymap").unwrap();
        s.put("active-keymap.txt", b"b.keymap\n").unwrap();
        assert_eq!(
            s.take_writes(),
            [
                StorageWrite::Put {
                    key: "active-keymap.txt".into(),
                    bytes: b"a.keymap\n".to_vec()
                },
                StorageWrite::Remove {
                    key: "keymaps/old.keymap".into()
                },
                StorageWrite::Put {
                    key: "active-keymap.txt".into(),
                    bytes: b"b.keymap\n".to_vec()
                },
            ]
        );
        assert!(s.take_writes().is_empty());
    }

    #[test]
    fn a_shared_store_journals_what_the_window_writes_for_its_host() {
        let host = Rc::new(RefCell::new(MemoryUiStorage::new()));
        let mut window: Box<dyn UiStorage> = Box::new(Rc::clone(&host));
        window.put("keybinds.json", b"{}").unwrap();
        assert_eq!(window.get("keybinds.json").unwrap(), b"{}");
        assert_eq!(
            host.borrow_mut().take_writes(),
            [StorageWrite::Put {
                key: "keybinds.json".into(),
                bytes: b"{}".to_vec()
            }]
        );
    }
}
