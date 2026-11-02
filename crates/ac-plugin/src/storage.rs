//! Plugin storage: files under roots the client chooses, reached only by
//! relative keys (OpenAC `FilePluginStorage` behind `ScopedPluginHost`'s
//! per-plugin scoping, and the shared `VtankProfiles` storage).
//!
//! - `plugin-data` is `<plugin storage directory>/<plugin id>/data`, the
//!   same folder on every host; with the storage directory at the plugins
//!   folder it is inside the plugin's own folder, where installs, updates
//!   and code removal leave it alone;
//! - `plugin-files` is the plugin's own installed folder, read only;
//! - `profiles` is the shared VTank folder (`vtank` in MossTank's plugin
//!   folder), for plugins with the `profiles` permission.
//!
//! A scope is a sub-folder (`character/<name>`, `world/<name>`). Keys are
//! `/`-separated, never absolute, never `..`, never `\`. Writes are atomic
//! (a sibling temporary file moved over the old one) and text is UTF-8
//! without a byte order mark; text reads honour a byte order mark as .NET's
//! `File.ReadAllText` does. Locks are lock files other clients on the
//! computer honour, released when the plugin lets go or stops.

use std::collections::{HashMap, VecDeque};
use std::fs::{self, File, OpenOptions};
use std::path::{Path, PathBuf};

use ac_plugin_abi::Permission;
use ac_plugin_abi::storage::{ScopeKind, StorageKey, StoragePlace, StorageRoot, StorageWrite};
use openac_plugin_compat::text::ordinal;

use crate::manifest::PluginManifest;

/// Where the storage roots are on this computer; `None` for a root this
/// host has nowhere to keep (every call on it then fails).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct StorageRoots {
    /// The folder whose sub-folders are each plugin's own data.
    pub plugin_data: Option<PathBuf>,
    /// The shared VTank profile folder.
    pub profiles: Option<PathBuf>,
}

/// What storage keeps for the session's plugins: the locks they hold, and
/// a counter naming temporary files.
#[derive(Debug, Default)]
pub(crate) struct StorageService {
    held: HashMap<u32, (String, File)>,
    next: u32,
    temporary: u64,
}

impl StorageService {
    /// Everything `owner` holds is released.
    pub(crate) fn release(&mut self, owner: &str) {
        self.held
            .retain(|_, (holder, _)| !holder.eq_ignore_ascii_case(owner));
    }
}

/// The plugin a storage call is for.
pub(crate) struct Caller<'a> {
    pub(crate) manifest: &'a PluginManifest,
    pub(crate) directory: &'a Path,
}

/// A key checked as OpenAC's `ScopedPluginStorage.ValidateKey` does: not
/// blank, not absolute, no `..`, no `\`. Its parts.
fn key_parts(key: &str) -> Result<Vec<&str>, String> {
    if key.trim().is_empty() {
        return Err("a storage key is blank".to_owned());
    }
    if key.starts_with('/') || key.contains(':') || key.contains("..") || key.contains('\\') {
        return Err(format!("'{key}' is not a relative storage key"));
    }
    Ok(key.split('/').filter(|p| !p.is_empty()).collect())
}

/// A scope's name as a folder name: characters a file name cannot hold
/// become `_`.
fn scope_folder(name: &str) -> Result<String, String> {
    let name = name.trim();
    if name.is_empty() || name == "." || name.contains("..") {
        return Err(format!("'{name}' is not a scope name"));
    }
    Ok(name
        .chars()
        .map(|c| match c {
            '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => '_',
            c if c.is_control() => '_',
            c => c,
        })
        .collect())
}

/// The folder a place is.
fn base(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    place: &StoragePlace,
) -> Result<PathBuf, String> {
    let root = match place.root {
        StorageRoot::PluginData => roots
            .plugin_data
            .as_ref()
            .map(|r| {
                r.join(&caller.manifest.id)
                    .join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME)
            })
            .ok_or("this client keeps no plugin data")?,
        StorageRoot::PluginFiles => caller.directory.to_path_buf(),
        StorageRoot::Profiles => {
            if !caller.manifest.allows(Permission::Profiles) {
                return Err("the profiles root needs the 'profiles' permission".to_owned());
            }
            roots
                .profiles
                .clone()
                .ok_or("this client has no profile folder")?
        }
    };
    Ok(match place.scope {
        ScopeKind::Global => root,
        ScopeKind::Character => root
            .join("character")
            .join(scope_folder(&place.scope_name)?),
        ScopeKind::World => root.join("world").join(scope_folder(&place.scope_name)?),
    })
}

fn path_of(roots: &StorageRoots, caller: &Caller<'_>, key: &StorageKey) -> Result<PathBuf, String> {
    let mut path = base(roots, caller, &key.place)?;
    for part in key_parts(&key.key)? {
        path.push(part);
    }
    Ok(path)
}

fn io(error: &std::io::Error) -> String {
    error.to_string()
}

/// The bytes under a key.
pub(crate) fn read(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    key: &StorageKey,
) -> Result<Option<Vec<u8>>, String> {
    let path = path_of(roots, caller, key)?;
    if !path.is_file() {
        return Ok(None);
    }
    fs::read(&path).map(Some).map_err(|e| io(&e))
}

/// Text as .NET's `File.ReadAllText(path, Encoding.UTF8)` reads it: a byte
/// order mark (UTF-8, UTF-16 either way, UTF-32 either way) chooses the
/// encoding and is dropped; otherwise UTF-8, bad bytes as U+FFFD.
#[must_use]
pub fn decode_text(bytes: &[u8]) -> String {
    fn units16(bytes: &[u8], big: bool) -> String {
        let units: Vec<u16> = bytes
            .chunks(2)
            .map(|c| {
                let pair = [c[0], c.get(1).copied().unwrap_or(0)];
                if big {
                    u16::from_be_bytes(pair)
                } else {
                    u16::from_le_bytes(pair)
                }
            })
            .collect();
        String::from_utf16_lossy(&units)
    }
    fn units32(bytes: &[u8], big: bool) -> String {
        bytes
            .chunks(4)
            .map(|c| {
                let mut quad = [0u8; 4];
                quad[..c.len()].copy_from_slice(c);
                let value = if big {
                    u32::from_be_bytes(quad)
                } else {
                    u32::from_le_bytes(quad)
                };
                char::from_u32(value).unwrap_or(char::REPLACEMENT_CHARACTER)
            })
            .collect()
    }
    match bytes {
        [0xFF, 0xFE, 0, 0, rest @ ..] => units32(rest, false),
        [0, 0, 0xFE, 0xFF, rest @ ..] => units32(rest, true),
        [0xEF, 0xBB, 0xBF, rest @ ..] => String::from_utf8_lossy(rest).into_owned(),
        [0xFF, 0xFE, rest @ ..] => units16(rest, false),
        [0xFE, 0xFF, rest @ ..] => units16(rest, true),
        _ => String::from_utf8_lossy(bytes).into_owned(),
    }
}

/// Replaces what is under a key: written to a sibling temporary file, then
/// moved over it (OpenAC `FilePluginStorage.WriteText`).
pub(crate) fn write(
    service: &mut StorageService,
    roots: &StorageRoots,
    caller: &Caller<'_>,
    request: &StorageWrite,
) -> Result<(), String> {
    if request.place.root == StorageRoot::PluginFiles {
        return Err("the plugin's own files are read only".to_owned());
    }
    let key = StorageKey {
        place: request.place.clone(),
        key: request.key.clone(),
    };
    let path = path_of(roots, caller, &key)?;
    let directory = path.parent().ok_or("a storage key names no file")?;
    fs::create_dir_all(directory).map_err(|e| io(&e))?;
    let name = path
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .ok_or("a storage key names no file")?;
    let temporary = directory.join(format!(".{name}.{}-{}.tmp", std::process::id(), {
        service.temporary += 1;
        service.temporary
    }));
    let result = fs::write(&temporary, &request.bytes).and_then(|()| fs::rename(&temporary, &path));
    if result.is_err() {
        // The temporary file is ours; the old content stands.
        let _ = fs::remove_file(&temporary);
    }
    result.map_err(|e| io(&e))
}

/// The keys of the files beneath a prefix, sorted ignoring case (OpenAC
/// `FilePluginStorage.List`: `Directory.EnumerateFiles(directory, "*",
/// SearchOption.AllDirectories)`, ordered by `StringComparer.OrdinalIgnoreCase`).
///
/// As .NET walks: a folder's entries in the order the file system gives
/// them, then its sub-folders one after another in the order they were met;
/// hidden and system files are listed; a sub-folder that is gone (or a link
/// to nothing) is passed over; any other error fails the listing.
pub(crate) fn list(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    prefix: &StorageKey,
) -> Result<Vec<String>, String> {
    let base = base(roots, caller, &prefix.place)?;
    let directory = if prefix.key.is_empty() {
        base.clone()
    } else {
        path_of(roots, caller, prefix)?
    };
    if !folder_exists(&directory) {
        return Ok(Vec::new());
    }
    let mut keys = Vec::new();
    let mut pending = VecDeque::from([directory]);
    let mut first = true;
    while let Some(folder) = pending.pop_front() {
        let entries = match fs::read_dir(&folder) {
            Ok(entries) => entries,
            Err(error) if !first && gone(&error) => continue,
            Err(error) => return Err(io(&error)),
        };
        first = false;
        for entry in entries {
            let entry = entry.map_err(|e| io(&e))?;
            let path = entry.path();
            if is_folder(&entry, &path) {
                pending.push_back(path);
            } else if let Ok(relative) = path.strip_prefix(&base) {
                let key: Vec<String> = relative
                    .components()
                    .map(|c| c.as_os_str().to_string_lossy().into_owned())
                    .collect();
                keys.push(key.join("/"));
            }
        }
    }
    sort_ignoring_case(&mut keys);
    Ok(keys)
}

/// Sorts as `OrderBy(key => key, StringComparer.OrdinalIgnoreCase)` does:
/// stable, each character compared by its upper case, so `a.nav` comes
/// before `_x.nav` (`A` is below `_`; `a` is above it).
fn sort_ignoring_case(keys: &mut [String]) {
    keys.sort_by(|a, b| ordinal::compare_ignore_case(a, b));
}

/// Whether a sub-folder could not be opened because it is not there (or is
/// not a folder): .NET passes over such a folder and fails on any other
/// error.
fn gone(error: &std::io::Error) -> bool {
    matches!(
        error.kind(),
        std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory
    )
}

/// Whether a listed entry is a folder to descend into, decided as .NET
/// decides it. The kind comes with the listing itself (on Windows from the
/// same `FindNextFile` record, on Linux from the entry's type), so most
/// entries cost no further call; asking the file system about every path
/// again (`Path::is_dir`) was the whole cost of a listing.
///
/// On Windows an entry is a folder when its folder attribute is set, so a
/// link to a folder (a directory symbolic link or a junction) is a folder
/// and is walked into, through the link; a link whose folder is gone is
/// still a folder, passed over when it cannot be opened, never a file.
#[cfg(windows)]
fn is_folder(entry: &fs::DirEntry, _path: &Path) -> bool {
    use std::os::windows::fs::FileTypeExt;
    entry
        .file_type()
        .is_ok_and(|kind| kind.is_dir() || kind.is_symlink_dir())
}

/// Elsewhere a link is followed to what it names: a link to a folder is a
/// folder, and a link to nothing is a file (as .NET's listing on Unix).
/// An entry whose kind the listing did not give is asked about.
#[cfg(not(windows))]
fn is_folder(entry: &fs::DirEntry, path: &Path) -> bool {
    match entry.file_type() {
        Ok(kind) if !kind.is_symlink() => kind.is_dir(),
        _ => path.is_dir(),
    }
}

/// `Directory.Exists`: on Windows the path's own folder attribute (a link
/// to a folder exists even when its folder is gone, and listing it then
/// fails, as in .NET).
#[cfg(windows)]
fn folder_exists(path: &Path) -> bool {
    use std::os::windows::fs::FileTypeExt;
    fs::symlink_metadata(path).is_ok_and(|m| {
        let kind = m.file_type();
        kind.is_dir() || kind.is_symlink_dir()
    })
}

/// `Directory.Exists` elsewhere: the path, followed through links, is a
/// folder.
#[cfg(not(windows))]
fn folder_exists(path: &Path) -> bool {
    path.is_dir()
}

/// The data files under `relative` in every layer (OpenAC
/// `FilePluginResourceCatalog.ListDataFiles`): the player's own (the
/// plugin's data folder, which overrides) and the package's (the plugin's
/// folder), each relative path once, ignoring case, sorted ignoring case.
/// A client that keeps no plugin data has the package layer only.
pub(crate) fn list_data_files(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    relative: &str,
) -> Result<Vec<String>, String> {
    let mut all: Vec<String> = Vec::new();
    for root in [StorageRoot::PluginData, StorageRoot::PluginFiles] {
        let prefix = StorageKey {
            place: StoragePlace::global(root),
            key: relative.to_owned(),
        };
        let keys = match list(roots, caller, &prefix) {
            Ok(keys) => keys,
            Err(_) if root == StorageRoot::PluginData && roots.plugin_data.is_none() => continue,
            Err(error) => return Err(error),
        };
        for key in keys {
            if !all.iter().any(|k| ordinal::equals_ignore_case(k, &key)) {
                all.push(key);
            }
        }
    }
    sort_ignoring_case(&mut all);
    Ok(all)
}

/// Removes what is under a key; false when nothing was.
pub(crate) fn delete(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    key: &StorageKey,
) -> Result<bool, String> {
    if key.place.root == StorageRoot::PluginFiles {
        return Err("the plugin's own files are read only".to_owned());
    }
    let path = path_of(roots, caller, key)?;
    if !path.is_file() {
        return Ok(false);
    }
    fs::remove_file(&path).map(|()| true).map_err(|e| io(&e))
}

/// Creates the folder a prefix names (a trailing `/` allowed), with its
/// parents (OpenAC `FilePluginStorage.EnsureDirectory`).
pub(crate) fn ensure_folder(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    prefix: &StorageKey,
) -> Result<bool, String> {
    if prefix.place.root == StorageRoot::PluginFiles {
        return Err("the plugin's own files are read only".to_owned());
    }
    let key = StorageKey {
        place: prefix.place.clone(),
        key: prefix.key.trim_end_matches('/').to_owned(),
    };
    let path = path_of(roots, caller, &key)?;
    fs::create_dir_all(&path).map_err(|e| io(&e))?;
    Ok(path.is_dir())
}

/// Where a place is, for the player to read.
pub(crate) fn describe(
    roots: &StorageRoots,
    caller: &Caller<'_>,
    place: &StoragePlace,
) -> Option<String> {
    base(roots, caller, place)
        .ok()
        .map(|p| p.display().to_string())
}

impl StorageService {
    /// Takes the lock a key names for `owner`: a `.lock` file beside where
    /// the key would be, locked so other clients' attempts fail while it is
    /// held. Its handle.
    pub(crate) fn lock(
        &mut self,
        roots: &StorageRoots,
        caller: &Caller<'_>,
        key: &StorageKey,
    ) -> Result<u32, String> {
        let mut path = path_of(roots, caller, key)?;
        let name = path
            .file_name()
            .map(|n| format!("{}.lock", n.to_string_lossy()))
            .ok_or("a lock key names no file")?;
        path.set_file_name(name);
        if let Some(directory) = path.parent() {
            fs::create_dir_all(directory).map_err(|e| io(&e))?;
        }
        let file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(&path)
            .map_err(|e| io(&e))?;
        file.try_lock().map_err(|e| match e {
            fs::TryLockError::WouldBlock => format!("'{}' is held by another client", key.key),
            fs::TryLockError::Error(e) => io(&e),
        })?;
        self.next = self.next.wrapping_add(1).max(1);
        self.held
            .insert(self.next, (caller.manifest.id.clone(), file));
        Ok(self.next)
    }

    /// Releases one of `owner`'s locks; false for a handle it does not hold.
    pub(crate) fn unlock(&mut self, owner: &str, handle: u32) -> bool {
        let held = self
            .held
            .get(&handle)
            .is_some_and(|(holder, _)| holder.eq_ignore_ascii_case(owner));
        held && self.held.remove(&handle).is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // .NET `File.ReadAllText(path, Encoding.UTF8)`: every byte order mark
    // is honoured and dropped; bare bytes are UTF-8 with bad bytes as
    // U+FFFD (the golden strings are what .NET returns for these bytes).
    #[test]
    fn text_reads_as_dotnet_reads_a_file() {
        assert_eq!(decode_text(b"\xEF\xBB\xBFmeta"), "meta");
        assert_eq!(decode_text(b"\xFF\xFEm\0e\0"), "me");
        assert_eq!(decode_text(b"\xFE\xFF\0m\0e"), "me");
        assert_eq!(decode_text(b"\xFF\xFE\0\0m\0\0\0"), "m");
        assert_eq!(decode_text(b"\0\0\xFE\xFF\0\0\0m"), "m");
        assert_eq!(decode_text(b"plain"), "plain");
        assert_eq!(decode_text(b"a\xFFb"), "a\u{FFFD}b");
    }

    // Register G14 (MossTank's `GlobalVariableStore` mutex): a lock one
    // client holds refuses another until released; a plugin that stops
    // releases its own.
    #[test]
    fn a_lock_is_held_against_another_client_until_released() {
        let folder = std::env::temp_dir().join(format!("openac-lock-{}", std::process::id()));
        let roots = StorageRoots {
            plugin_data: Some(folder.clone()),
            profiles: None,
        };
        let manifest = PluginManifest::parse(
            r#"{"id":"openac.locker","displayName":"L","version":"0.1.0","entry":"l.wasm","apiVersion":"1.0"}"#,
        )
        .expect("a manifest");
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        let key = StorageKey {
            place: StoragePlace::global(StorageRoot::PluginData),
            key: "globals".into(),
        };
        let (mut one, mut other) = (StorageService::default(), StorageService::default());
        let held = one.lock(&roots, &caller, &key).expect("free");
        assert!(other.lock(&roots, &caller, &key).is_err());
        assert!(!one.unlock("someone.else", held));
        assert!(one.unlock("openac.locker", held));
        let again = other.lock(&roots, &caller, &key).expect("released");
        other.release("openac.locker");
        assert!(!other.unlock("openac.locker", again));
        assert!(one.lock(&roots, &caller, &key).is_ok());
        let _ = fs::remove_dir_all(&folder);
    }

    // OpenAC `ScopedPluginStorageTests.AnEmptyPrefixListsThePluginsWholeFolderAndNothingElse`:
    // an empty prefix is the plugin's whole folder,
    // and another plugin's file beside it is not listed.
    #[test]
    fn an_empty_prefix_lists_the_plugins_whole_folder_and_nothing_else() {
        let folder =
            std::env::temp_dir().join(format!("openac-storage-list-{}", std::process::id()));
        let roots = StorageRoots {
            plugin_data: Some(folder.clone()),
            profiles: None,
        };
        let other = folder
            .join("openac.other")
            .join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME);
        fs::create_dir_all(&other).unwrap();
        fs::write(other.join("secret.txt"), "not yours").unwrap();
        let manifest = PluginManifest::parse(
            r#"{"id":"openac.example","displayName":"Example Plugin","version":"0.1.0","entry":"e.wasm","apiVersion":"1.0"}"#,
        )
        .expect("a manifest");
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        let place = StoragePlace::global(StorageRoot::PluginData);
        let mut service = StorageService::default();
        for (key, text) in [("settings.json", "{}"), ("profiles/a.utl", "x")] {
            let request = StorageWrite {
                place: place.clone(),
                key: key.into(),
                bytes: text.as_bytes().to_vec(),
            };
            write(&mut service, &roots, &caller, &request).expect("written");
        }
        let everything = StorageKey {
            place,
            key: String::new(),
        };
        assert_eq!(
            list(&roots, &caller, &everything).unwrap(),
            ["profiles/a.utl", "settings.json"]
        );
        let _ = fs::remove_dir_all(&folder);
    }

    // OpenAC `FilePluginStorage.List`: every file beneath the prefix's
    // folder at any depth (`Directory.EnumerateFiles(..., AllDirectories)`),
    // as its path from the root with `/`, sorted ignoring case; folders,
    // empty ones included, are not keys; a prefix with no folder is empty
    // (with OpenAC `FilePluginStorageTests`: a nested file listed under its
    // folder's prefix, and "a created but empty folder holds no keys").
    #[test]
    fn a_listing_is_every_file_beneath_sorted_ignoring_case() {
        let folder =
            std::env::temp_dir().join(format!("openac-storage-golden-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        let (roots, manifest) = listing_root(&folder);
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        for file in [
            "metas/Bore.met",
            "metas/arwic.met",
            "navs/Holtburg/loop2.nav",
            "navs/holtburg/Loop1.nav",
            "navs/z.nav",
            "Loot.utl",
            "loot/LootFest.utl",
            "loot/a.utl",
            "ub/profiles/deep/er/x.json",
        ] {
            let path = file.split('/').fold(folder.clone(), |p, part| p.join(part));
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, file).unwrap();
        }
        fs::create_dir_all(folder.join("empty").join("inner")).unwrap();
        let listing = |prefix: &str| {
            let key = StorageKey {
                place: StoragePlace::global(StorageRoot::PluginFiles),
                key: prefix.into(),
            };
            list(&roots, &caller, &key).unwrap()
        };
        // On a case-insensitive file system the two Holtburg spellings are
        // one folder named as first created.
        let holtburg = if folder.join("navs").join("HOLTBURG").is_dir() {
            ["navs/Holtburg/Loop1.nav", "navs/Holtburg/loop2.nav"]
        } else {
            ["navs/holtburg/Loop1.nav", "navs/Holtburg/loop2.nav"]
        };
        let mut whole = vec![
            "Loot.utl",
            "loot/a.utl",
            "loot/LootFest.utl",
            "metas/arwic.met",
            "metas/Bore.met",
        ];
        whole.extend(holtburg);
        whole.extend(["navs/z.nav", "ub/profiles/deep/er/x.json"]);
        assert_eq!(listing(""), whole);
        let mut navs = holtburg.to_vec();
        navs.push("navs/z.nav");
        assert_eq!(listing("navs"), navs);
        assert_eq!(listing("navs/"), navs);
        assert_eq!(listing("ub/profiles"), ["ub/profiles/deep/er/x.json"]);
        assert!(listing("empty").is_empty());
        assert!(listing("nowhere").is_empty());
        // A file named as the prefix is not a folder to list.
        assert!(listing("Loot.utl").is_empty());
        let _ = fs::remove_dir_all(&folder);
    }

    // Each listing reads the folder as it is now: a file added, removed or
    // renamed between two listings shows in the second.
    #[test]
    fn a_listing_sees_files_added_removed_and_renamed_since_the_last() {
        let folder =
            std::env::temp_dir().join(format!("openac-storage-fresh-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        let (roots, manifest) = listing_root(&folder);
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        let navs = folder.join("navs");
        fs::create_dir_all(&navs).unwrap();
        fs::write(navs.join("a.nav"), "a").unwrap();
        let listing = || {
            let key = StorageKey {
                place: StoragePlace::global(StorageRoot::PluginFiles),
                key: "navs".into(),
            };
            list(&roots, &caller, &key).unwrap()
        };
        assert_eq!(listing(), ["navs/a.nav"]);
        fs::write(navs.join("b.nav"), "b").unwrap();
        assert_eq!(listing(), ["navs/a.nav", "navs/b.nav"]);
        fs::rename(navs.join("a.nav"), navs.join("c.nav")).unwrap();
        assert_eq!(listing(), ["navs/b.nav", "navs/c.nav"]);
        fs::create_dir_all(navs.join("sub")).unwrap();
        fs::write(navs.join("sub").join("d.nav"), "d").unwrap();
        assert_eq!(listing(), ["navs/b.nav", "navs/c.nav", "navs/sub/d.nav"]);
        fs::remove_file(navs.join("b.nav")).unwrap();
        fs::remove_dir_all(navs.join("sub")).unwrap();
        assert_eq!(listing(), ["navs/c.nav"]);
        let _ = fs::remove_dir_all(&folder);
    }

    /// The names of the ordering probe, in its input order.
    const PROBE_NAMES: [&str; 21] = [
        "_x.nav",
        "a.nav",
        "[b].nav",
        "]g.nav",
        "\\h.nav",
        "^c.nav",
        "`d.nav",
        "Z.nav",
        "{e}.nav",
        "~f.nav",
        "0.nav",
        "\u{b5}.nav",
        "\u{e0}.nav",
        "\u{131}.nav",
        "\u{17f}.nav",
        "\u{17e}.nav",
        "\u{ff01}.nav",
        "\u{1F600}.nav",
        "\u{df}.nav",
        "\u{1fb3}.nav",
        "\u{1fbc}.nav",
    ];

    /// What .NET 10.0.8 (Windows) printed for `PROBE_NAMES.OrderBy(k => k,
    /// StringComparer.OrdinalIgnoreCase)`: each character compared
    /// by its upper case, so the letters sort below `[ \ ] ^ _` and the
    /// backtick; `µ` sorts as Greek `Μ`; `ı`, `ſ` and `ß` have no upper case
    /// here; `ᾳ` and `ᾼ` are equal and keep their order; `！` (U+FF01) sorts
    /// before the emoji.
    const PROBE_SORTED: [&str; 21] = [
        "0.nav",
        "a.nav",
        "Z.nav",
        "[b].nav",
        "\\h.nav",
        "]g.nav",
        "^c.nav",
        "_x.nav",
        "`d.nav",
        "{e}.nav",
        "~f.nav",
        "\u{e0}.nav",
        "\u{df}.nav",
        "\u{131}.nav",
        "\u{17e}.nav",
        "\u{17f}.nav",
        "\u{b5}.nav",
        "\u{1fb3}.nav",
        "\u{1fbc}.nav",
        "\u{ff01}.nav",
        "\u{1F600}.nav",
    ];

    #[test]
    fn keys_sort_as_ordinal_ignore_case() {
        let mut keys: Vec<String> = PROBE_NAMES.iter().map(|k| (*k).to_owned()).collect();
        sort_ignoring_case(&mut keys);
        assert_eq!(keys, PROBE_SORTED);
        // Lower-casing would put `_x.nav` first.
        assert_eq!(
            ordinal::compare_ignore_case("a.nav", "_x.nav"),
            std::cmp::Ordering::Less
        );
    }

    // The probe's names as files (all but `\h.nav`, which no Windows file
    // can be named, and `ᾼ.nav`, which is `ᾳ.nav`'s name on a file system
    // that ignores case), listed in .NET's order.
    #[test]
    fn a_listing_sorts_symbols_and_letters_as_ordinal_ignore_case() {
        let folder =
            std::env::temp_dir().join(format!("openac-storage-order-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        let (roots, manifest) = listing_root(&folder);
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        let kept = |name: &&str| *name != "\\h.nav" && *name != "\u{1fbc}.nav";
        for name in PROBE_NAMES.into_iter().filter(kept) {
            fs::write(folder.join(name), name).unwrap();
        }
        let key = StorageKey {
            place: StoragePlace::global(StorageRoot::PluginFiles),
            key: String::new(),
        };
        let expected: Vec<&str> = PROBE_SORTED.into_iter().filter(kept).collect();
        assert_eq!(list(&roots, &caller, &key).unwrap(), expected);
        let _ = fs::remove_dir_all(&folder);
    }

    /// Links `link` to the folder `target`: a junction on Windows (which
    /// needs no administrator), a symbolic link elsewhere.
    fn link_folder(link: &Path, target: &Path) {
        #[cfg(unix)]
        std::os::unix::fs::symlink(target, link).unwrap();
        #[cfg(windows)]
        {
            let status = std::process::Command::new("cmd.exe")
                .args(["/d", "/c", "mklink", "/J"])
                .arg(link)
                .arg(target)
                .stdout(std::process::Stdio::null())
                .status()
                .unwrap();
            assert!(status.success(), "could not create the test junction");
        }
    }

    // .NET's `Directory.EnumerateFiles(..., AllDirectories)` walks through a
    // link to a folder (a junction on Windows: the folder attribute is set
    // and the folder is opened through the link), and .NET 10.0.8 printed
    // `list\top.nav` and `list\linked\deep\r.nav` for this layout, passing
    // over the link whose folder is gone; listing that link itself threw
    // `DirectoryNotFoundException`.
    // On Unix a link to nothing is not a folder, so .NET lists it as a file.
    #[test]
    fn a_listing_walks_through_folder_links_as_dotnet_does() {
        let folder =
            std::env::temp_dir().join(format!("openac-storage-links-{}", std::process::id()));
        let _ = fs::remove_dir_all(&folder);
        let (roots, manifest) = listing_root(&folder);
        let caller = Caller {
            manifest: &manifest,
            directory: &folder,
        };
        fs::create_dir_all(folder.join("real").join("deep")).unwrap();
        fs::write(folder.join("real").join("deep").join("r.nav"), "r").unwrap();
        fs::create_dir_all(folder.join("gone")).unwrap();
        fs::create_dir_all(folder.join("list")).unwrap();
        fs::write(folder.join("list").join("top.nav"), "t").unwrap();
        link_folder(&folder.join("list").join("linked"), &folder.join("real"));
        link_folder(&folder.join("list").join("dangling"), &folder.join("gone"));
        fs::remove_dir(folder.join("gone")).unwrap();
        let listing = |prefix: &str| {
            let key = StorageKey {
                place: StoragePlace::global(StorageRoot::PluginFiles),
                key: prefix.into(),
            };
            list(&roots, &caller, &key)
        };
        #[cfg(windows)]
        {
            assert_eq!(
                listing("list").unwrap(),
                ["list/linked/deep/r.nav", "list/top.nav"]
            );
            assert!(listing("list/dangling").is_err());
        }
        #[cfg(unix)]
        {
            assert_eq!(
                listing("list").unwrap(),
                ["list/dangling", "list/linked/deep/r.nav", "list/top.nav"]
            );
            assert!(listing("list/dangling").unwrap().is_empty());
        }
        let _ = fs::remove_dir_all(&folder);
    }

    /// A plugin whose own folder is `folder`, listed through the read-only
    /// `plugin-files` root.
    fn listing_root(folder: &Path) -> (StorageRoots, PluginManifest) {
        fs::create_dir_all(folder).unwrap();
        let manifest = PluginManifest::parse(
            r#"{"id":"openac.lister","displayName":"L","version":"0.1.0","entry":"l.wasm","apiVersion":"1.0"}"#,
        )
        .expect("a manifest");
        (StorageRoots::default(), manifest)
    }

    // OpenAC `ScopedPluginStorage.ValidateKey`.
    #[test]
    fn keys_stay_inside_their_root() {
        assert!(key_parts("a/b.txt").is_ok());
        for bad in ["", "  ", "/abs", "C:/x", "a/../b", "..", "a\\b"] {
            assert!(key_parts(bad).is_err(), "{bad:?}");
        }
        assert_eq!(scope_folder("+Horan").unwrap(), "+Horan");
        assert_eq!(scope_folder("a/b").unwrap(), "a_b");
        assert!(scope_folder("..").is_err());
    }

    // OpenAC `tests/AcDream.Core.Tests/Plugins/FilePluginResourceCatalog
    // Tests.cs`: package resources read and traversal is refused; data
    // files are unioned across the player's layer and the package.
    #[test]
    fn the_resource_catalog_reads_the_package_and_layers_data_files() {
        let folder = std::env::temp_dir().join(format!("openac-resources-{}", std::process::id()));
        let package = folder.join("package");
        let data = folder.join("data");
        let manifest = PluginManifest::parse(
            r#"{"id":"openac.routes","displayName":"R","version":"0.1.0","entry":"r.wasm","apiVersion":"1.0"}"#,
        )
        .expect("a manifest");
        fs::create_dir_all(package.join("maps")).unwrap();
        fs::create_dir_all(package.join("routes")).unwrap();
        fs::create_dir_all(
            data.join("openac.routes")
                .join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME)
                .join("routes"),
        )
        .unwrap();
        fs::write(package.join("maps").join("dereth.dat"), "map").unwrap();
        fs::write(package.join("routes").join("default.json"), "default").unwrap();
        fs::write(package.join("routes").join("Shared.json"), "package").unwrap();
        let user = data
            .join("openac.routes")
            .join(ac_host::paths::PLUGIN_FILES_FOLDER_NAME)
            .join("routes");
        fs::write(user.join("custom.json"), "custom").unwrap();
        fs::write(user.join("shared.json"), "user").unwrap();
        let roots = StorageRoots {
            plugin_data: Some(data),
            profiles: None,
        };
        let caller = Caller {
            manifest: &manifest,
            directory: &package,
        };
        let resource = |key: &str| StorageKey {
            place: StoragePlace::global(StorageRoot::PluginFiles),
            key: key.into(),
        };
        assert_eq!(
            read(&roots, &caller, &resource("maps/dereth.dat")).unwrap(),
            Some(b"map".to_vec())
        );
        assert!(read(&roots, &caller, &resource("../secret")).is_err());
        assert_eq!(
            list_data_files(&roots, &caller, "routes").unwrap(),
            [
                "routes/custom.json",
                "routes/default.json",
                "routes/shared.json"
            ]
        );
        assert!(list_data_files(&roots, &caller, "../routes").is_err());
        // No player layer: the package's alone.
        let without = StorageRoots::default();
        assert_eq!(
            list_data_files(&without, &caller, "routes").unwrap(),
            ["routes/default.json", "routes/Shared.json"]
        );
        let _ = fs::remove_dir_all(&folder);
    }
}
