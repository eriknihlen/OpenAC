//! The install root's folder layout over time: moving an older root into
//! the current layout once, the session folders launcher-started clients
//! log into, and clearing out logs that are too old to matter.
//!
//! The current layout (version 2) is described in [`crate::paths`]. A root
//! records its layout in `layout.json` (`{ "version": 2 }`). [`migrate`]
//! moves an older root into it before anything reads these folders: both
//! the launcher and every client run it at startup. It holds `layout.lock`
//! (so a second mover waits for the first) and takes the install's
//! session lock alone (so it never runs while any client of this install
//! runs). Each step can be run again after an
//! interruption; the marker is written last. Nothing reads the old places
//! afterwards.
//!
//! The steps, in order:
//! 1. `data/` becomes `game/`, and the content records that named files in
//!    `data/` name them in `game/`.
//! 2. Each plugin's `files/` becomes its `data/`.
//! 3. `vtank/` moves into MossTank's plugin folder.
//! 4. MossTank's old macro debug logs move to `logs/old plugin logs/`.
//! 5. The old session folders in `logs/launcher/` (named by their UTC start
//!    and a unique id) older than [`KEEP_LOGS`] are deleted and the others
//!    move to `logs/<server>/<character>/<local start time>/`; one that
//!    cannot be understood goes to `logs/unsorted/`.
//!
//! A file moved onto one already there never replaces it: the newer stays
//! in place and the other is kept beside it as `<name>.old<ext>`.

use std::fs::{File, OpenOptions, TryLockError};
use std::io;
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use crate::install_lease::InstallSessionLease;
use crate::json::{self, Node, Value};
use crate::local_time::LocalZone;
use crate::paths::{
    PLUGIN_FILES_FOLDER_NAME, PathSet, RESERVED_LOG_FOLDER_NAMES, SESSION_CONFIG_FILE_NAME,
    SESSION_STATUS_FILE_NAME, VTANK_FOLDER_NAME, VTANK_PLUGIN_ID, same_folder,
};

/// The layout this program uses.
pub const CURRENT_VERSION: i32 = 2;

/// How long session folders, crash reports and old plugin logs are kept.
pub const KEEP_LOGS: Duration = Duration::from_secs(7 * 24 * 60 * 60);

/// What the older layout called a plugin's own files folder.
const OLD_PLUGIN_FILES_FOLDER_NAME: &str = "files";

/// The content records in the game folder that name files inside it.
const CONTENT_RECORDS: [&str; 3] = [
    "install.json",
    "install.verification.json",
    "retail-content.json",
];

/// How a migration went.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Migration {
    /// Whether this call moved the root (false: it was already current).
    pub moved: bool,
    /// One line per thing worth a log entry: a name collision, an old
    /// session folder deleted or set aside.
    pub notes: Vec<String>,
}

/// Why a migration did not finish.
#[derive(Debug, thiserror::Error)]
pub enum MigrationError {
    /// Something of this install is running (a client, an update, a move,
    /// or another process migrating), so nothing was moved.
    #[error("the install is in use")]
    Busy,
    /// Another process is moving the files now.
    #[error("another process is moving the files")]
    Moving,
    /// A step could not complete; nothing more was moved, and the next
    /// start goes on from here.
    #[error("{}: {error}", path.display())]
    Failed {
        /// What could not be moved or written.
        path: PathBuf,
        /// Why.
        error: io::Error,
        /// What the steps that ran noted.
        notes: Vec<String>,
    },
}

impl MigrationError {
    /// The player-facing text: what is in use when something holds a file
    /// or the install, otherwise the error itself and its path.
    pub fn message(&self, paths: &PathSet) -> String {
        match self {
            Self::Busy => in_use_message(paths.root_directory()),
            Self::Moving => format!(
                "Could not move your files to the new layout: another OpenAC program is \
                 moving the files in {} now. Start again when it has finished.",
                paths.root_directory().display()
            ),
            Self::Failed { path, error, .. } if is_in_use(error) => in_use_message(path),
            Self::Failed { path, error, .. } => format!(
                "Could not move your files to the new layout: {} ({:?}: {error}). Start the \
                 launcher again once that is fixed.",
                path.display(),
                error.kind()
            ),
        }
    }

    /// The text a client shows when it cannot move the files: a client
    /// that finds the install held by others was started beside an older
    /// launcher or older clients, which only the new launcher clears up.
    pub fn client_message(&self, paths: &PathSet) -> String {
        match self {
            Self::Busy => format!(
                "This client needs the new launcher. Restart the launcher to finish the \
                 update. (The install folder {} is in use by another OpenAC program, so its \
                 files could not be moved to the new layout.)",
                paths.root_directory().display()
            ),
            other => other.message(paths),
        }
    }
}

fn in_use_message(path: &Path) -> String {
    format!(
        "Could not move your files to the new layout: {} is in use. Close all OpenAC \
         clients and start the launcher again.",
        path.display()
    )
}

/// Whether an error says another program holds the file.
fn is_in_use(error: &io::Error) -> bool {
    if matches!(
        error.kind(),
        io::ErrorKind::ResourceBusy | io::ErrorKind::ExecutableFileBusy
    ) {
        return true;
    }
    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION.
    cfg!(windows) && matches!(error.raw_os_error(), Some(32 | 33))
}

/// Whether the root's marker says it is in the current layout.
pub fn is_current(paths: &PathSet) -> bool {
    std::fs::read(paths.layout_marker_file())
        .ok()
        .and_then(|bytes| json::parse(&bytes).ok())
        .and_then(|document| {
            let members = json::object_members(&document).ok()?;
            let version = members.iter().find(|member| member.name == "version")?;
            json::int32(&version.value).ok()
        })
        .is_some_and(|version| version >= CURRENT_VERSION)
}

/// Moves the root into the current layout unless the marker says it is
/// already there. `now` and `zone` date the old session folders.
///
/// # Errors
/// [`MigrationError::Moving`] while another process moves the files;
/// [`MigrationError::Busy`] while anything else of this install runs;
/// [`MigrationError::Failed`] when a step could not complete.
pub fn migrate(
    paths: &PathSet,
    now: SystemTime,
    zone: &LocalZone,
) -> Result<Migration, MigrationError> {
    migrate_with(paths, now, zone, rename_or_copy)
}

/// [`migrate`] with the way one item is moved replaced (tests).
fn migrate_with(
    paths: &PathSet,
    now: SystemTime,
    zone: &LocalZone,
    rename: Rename,
) -> Result<Migration, MigrationError> {
    if is_current(paths) {
        return Ok(Migration::default());
    }
    // The mover's own lock first: it tells a second mover (who waits for
    // the marker) from a client or update that holds the install (who
    // keeps the files from being moved at all).
    let lock_path = paths.layout_lock_file();
    let layout_lock = (|| {
        std::fs::create_dir_all(paths.root_directory())?;
        OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .open(&lock_path)
    })()
    .map_err(|error| MigrationError::Failed {
        path: lock_path.clone(),
        error,
        notes: Vec::new(),
    })?;
    match layout_lock.try_lock() {
        Ok(()) => {}
        Err(TryLockError::WouldBlock) => return Err(MigrationError::Moving),
        Err(TryLockError::Error(error)) => {
            return Err(MigrationError::Failed {
                path: lock_path,
                error,
                notes: Vec::new(),
            });
        }
    }
    let lease = match InstallSessionLease::try_acquire_exclusive(paths) {
        Ok(Some(lease)) => lease,
        Ok(None) => return Err(MigrationError::Busy),
        Err(error) => {
            return Err(MigrationError::Failed {
                path: InstallSessionLease::lock_path(paths),
                error,
                notes: Vec::new(),
            });
        }
    };
    // Another process may have finished it while this one waited.
    if is_current(paths) {
        return Ok(Migration::default());
    }
    let mut mover = Mover {
        notes: Vec::new(),
        rename,
        merging: Vec::new(),
    };
    let result = (|| {
        remove_leftover_copies(paths)?;
        mover.game_folder(paths)?;
        mover.plugin_files_folders(paths)?;
        mover.vtank_folder(paths)?;
        mover.old_macro_logs(paths)?;
        mover.old_session_folders(paths, now, zone)?;
        write_marker(paths).map_err(|error| failed(&paths.layout_marker_file(), error))
    })();
    // Released before the result is read. (Built for a browser, where
    // there are no file locks, these hold nothing.)
    #[cfg_attr(target_arch = "wasm32", allow(clippy::drop_non_drop))]
    {
        drop(lease);
        drop(layout_lock);
    }
    match result {
        Ok(()) => Ok(Migration {
            moved: true,
            notes: mover.notes,
        }),
        Err((path, error)) => Err(MigrationError::Failed {
            path,
            error,
            notes: mover.notes,
        }),
    }
}

/// [`migrate`] for a client: while another process moves the files, it
/// waits up to `patience` for that to finish (the marker appears). When
/// the install is held by others instead (an older launcher's session, an
/// older client), it does not wait: only the new launcher can finish the
/// update, and the client says so at once.
///
/// # Errors
/// The player-facing message (see [`MigrationError::client_message`]).
pub fn migrate_waiting(paths: &PathSet, patience: Duration) -> Result<Migration, String> {
    let zone = LocalZone::system();
    let deadline = std::time::Instant::now() + patience;
    loop {
        match migrate(paths, SystemTime::now(), &zone) {
            Ok(migration) => return Ok(migration),
            Err(MigrationError::Moving) if std::time::Instant::now() < deadline => {
                std::thread::sleep(Duration::from_millis(250));
            }
            Err(error) => return Err(error.client_message(paths)),
        }
    }
}

/// How one file or folder is moved to a path that is free.
type Rename = fn(&Path, &Path) -> io::Result<()>;

/// A rename, or, where the two places are on different volumes (a plugins
/// folder linked to another drive), a copy that is checked before the
/// original goes.
fn rename_or_copy(source: &Path, destination: &Path) -> io::Result<()> {
    match std::fs::rename(source, destination) {
        Err(error) if error.kind() == io::ErrorKind::CrossesDevices => {
            copy_then_delete(source, destination)
        }
        other => other,
    }
}

/// The end of the name a file is copied to before it takes its own name.
const COPYING_SUFFIX: &str = ".copying";

/// Copies `source` to `destination` (a file, or a folder and everything in
/// it), checks every file reads back the same, then deletes `source`. Each
/// file is written under a temporary name beside its destination and takes
/// its own name only once it reads back the same, so a copy cut short
/// never leaves a partial file under a real name. A failure part-way
/// leaves `source` whole; the partial copy is removed.
fn copy_then_delete(source: &Path, destination: &Path) -> io::Result<()> {
    let mut visited = std::collections::HashSet::new();
    let copied = copy_checked(source, destination, &mut visited);
    if let Err(error) = copied {
        let _ = remove_no_follow(destination);
        return Err(error);
    }
    remove_no_follow(source)
}

/// `<path>.copying`.
fn copying_name(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_owned();
    name.push(COPYING_SUFFIX);
    PathBuf::from(name)
}

fn copy_checked(
    source: &Path,
    destination: &Path,
    visited: &mut std::collections::HashSet<PathBuf>,
) -> io::Result<()> {
    let metadata = std::fs::metadata(source)?;
    if metadata.is_dir() {
        // A link back to a folder this copy is already inside would be
        // copied without end; it is left out.
        if !visited.insert(std::fs::canonicalize(source)?) {
            return Ok(());
        }
        std::fs::create_dir(destination)?;
        for entry in std::fs::read_dir(source)? {
            let entry = entry?;
            copy_checked(&entry.path(), &destination.join(entry.file_name()), visited)?;
        }
        return Ok(());
    }
    let temporary = copying_name(destination);
    match std::fs::remove_file(&temporary) {
        Err(error) if error.kind() != io::ErrorKind::NotFound => return Err(error),
        _ => {}
    }
    std::fs::copy(source, &temporary)?;
    if !same_contents(source, &temporary)? {
        let _ = std::fs::remove_file(&temporary);
        return Err(io::Error::other(format!(
            "the copy of {} does not read back the same",
            source.display()
        )));
    }
    if let Ok(written) = metadata.modified() {
        let _ = File::options()
            .write(true)
            .open(&temporary)
            .and_then(|file| file.set_modified(written));
    }
    std::fs::rename(&temporary, destination)
}

/// Deletes what an earlier run's copies cut short left (`*.copying`) in
/// the folders the move writes into, before anything else runs: their
/// originals are still in place and are copied again.
fn remove_leftover_copies(paths: &PathSet) -> StepResult {
    let mut visited = std::collections::HashSet::new();
    for folder in [
        paths.game_data_directory(),
        paths.plugins_directory(),
        paths.logs_directory(),
    ] {
        remove_copies_in(&folder, &mut visited)?;
    }
    Ok(())
}

fn remove_copies_in(folder: &Path, visited: &mut std::collections::HashSet<PathBuf>) -> StepResult {
    let Ok(canonical) = std::fs::canonicalize(folder) else {
        return Ok(());
    };
    if !visited.insert(canonical) {
        return Ok(());
    }
    let entries = match std::fs::read_dir(folder) {
        Ok(entries) => entries,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(failed(folder, error)),
    };
    for entry in entries {
        let path = entry.map_err(|error| failed(folder, error))?.path();
        if std::fs::metadata(&path).is_ok_and(|metadata| metadata.is_dir()) {
            remove_copies_in(&path, visited)?;
        } else if path
            .file_name()
            .is_some_and(|name| name.to_string_lossy().ends_with(COPYING_SUFFIX))
        {
            std::fs::remove_file(&path).map_err(|error| failed(&path, error))?;
        }
    }
    Ok(())
}

fn same_contents(left: &Path, right: &Path) -> io::Result<bool> {
    use std::io::Read as _;
    let (mut left, mut right) = (File::open(left)?, File::open(right)?);
    if left.metadata()?.len() != right.metadata()?.len() {
        return Ok(false);
    }
    let (mut a, mut b) = (vec![0u8; 64 * 1024], vec![0u8; 64 * 1024]);
    loop {
        let read = left.read(&mut a)?;
        if read == 0 {
            return Ok(true);
        }
        right.read_exact(&mut b[..read])?;
        if a[..read] != b[..read] {
            return Ok(false);
        }
    }
}

/// Whether `path` is a link (a symbolic link or a junction).
fn is_link(path: &Path) -> bool {
    std::fs::symlink_metadata(path).is_ok_and(|metadata| metadata.file_type().is_symlink())
}

/// Removes `path`: a link itself (never what it points at), a file, or a
/// folder with everything in it.
fn remove_no_follow(path: &Path) -> io::Result<()> {
    let metadata = std::fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() {
        // A link to a folder is removed as a folder on Windows (a
        // junction or a directory symbolic link), as a file elsewhere.
        return if cfg!(windows) && std::fs::metadata(path).is_ok_and(|m| m.is_dir()) {
            std::fs::remove_dir(path)
        } else {
            std::fs::remove_file(path).or_else(|_| std::fs::remove_dir(path))
        };
    }
    if metadata.is_dir() {
        std::fs::remove_dir_all(path)
    } else {
        std::fs::remove_file(path)
    }
}

fn write_marker(paths: &PathSet) -> io::Result<()> {
    let path = paths.layout_marker_file();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let mut temporary = path.as_os_str().to_owned();
    temporary.push(".tmp");
    std::fs::write(
        &temporary,
        format!("{{ \"version\": {CURRENT_VERSION} }}\n"),
    )?;
    std::fs::rename(&temporary, &path)
}

type StepResult = Result<(), (PathBuf, io::Error)>;

fn failed(path: &Path, error: io::Error) -> (PathBuf, io::Error) {
    (path.to_path_buf(), error)
}

/// Runs the steps and keeps their notes.
struct Mover {
    notes: Vec<String>,
    rename: Rename,
    /// The folders (as their real paths) being merged now, outermost
    /// first: a link back to one of them is a cycle, not more to merge.
    merging: Vec<PathBuf>,
}

impl Mover {
    /// Step 1: `data/` becomes `game/`, and the records naming files in it
    /// are rewritten. The records are checked on every run, so a run that
    /// stopped between the two finishes the job.
    fn game_folder(&mut self, paths: &PathSet) -> StepResult {
        let old = paths.root_directory().join("data");
        let new = paths.game_data_directory();
        if old.is_dir() {
            self.move_item(&old, &new)?;
        }
        for record in CONTENT_RECORDS {
            let path = new.join(record);
            if path.is_file() {
                rewrite_record(&path, &old, &new).map_err(|error| failed(&path, error))?;
            }
        }
        Ok(())
    }

    /// Step 2: every plugin's `files/` becomes its `data/`, including the
    /// copies an interrupted install or removal left in the plugins
    /// folder's `.staging` and `.trash`.
    fn plugin_files_folders(&mut self, paths: &PathSet) -> StepResult {
        let plugins = paths.plugins_directory();
        for folder in subfolders(&plugins).map_err(|error| failed(&plugins, error))? {
            let hidden = folder
                .file_name()
                .is_some_and(|name| name.to_string_lossy().starts_with('.'));
            let owners = if hidden {
                subfolders(&folder).map_err(|error| failed(&folder, error))?
            } else {
                vec![folder]
            };
            for owner in owners {
                let old = owner.join(OLD_PLUGIN_FILES_FOLDER_NAME);
                if old.is_dir() {
                    self.move_item(&old, &owner.join(PLUGIN_FILES_FOLDER_NAME))?;
                }
            }
        }
        Ok(())
    }

    /// Step 3: the shared VTank profiles move into MossTank's folder, made
    /// for them if MossTank is not installed.
    fn vtank_folder(&mut self, paths: &PathSet) -> StepResult {
        let old = paths.root_directory().join(VTANK_FOLDER_NAME);
        if old.is_dir() {
            self.move_item(&old, &paths.vtank_profiles_directory())?;
        }
        Ok(())
    }

    /// Step 4: MossTank's macro debug logs leave its data for
    /// `logs/old plugin logs/`.
    fn old_macro_logs(&mut self, paths: &PathSet) -> StepResult {
        let debug = paths.plugin_files_directory(VTANK_PLUGIN_ID).join("debug");
        let Ok(entries) = std::fs::read_dir(&debug) else {
            return Ok(());
        };
        let destination = paths.old_plugin_logs_directory();
        for entry in entries {
            let entry = entry.map_err(|error| failed(&debug, error))?;
            let name = entry.file_name().to_string_lossy().into_owned();
            let lower = name.to_ascii_lowercase();
            if entry.path().is_file() && lower.starts_with("macro-") && lower.ends_with(".log") {
                self.move_item(&entry.path(), &destination.join(&name))?;
            }
        }
        remove_if_empty(&debug).map_err(|error| failed(&debug, error))
    }

    /// Step 5: the old session folders.
    fn old_session_folders(
        &mut self,
        paths: &PathSet,
        now: SystemTime,
        zone: &LocalZone,
    ) -> StepResult {
        let launcher = paths.launcher_log_directory();
        let servers = ServerNames::read(paths);
        for folder in subfolders(&launcher).map_err(|error| failed(&launcher, error))? {
            let name = folder
                .file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_default();
            let Some(started_ms) = parse_old_session_id(&name) else {
                continue;
            };
            let age = unix_ms(now).saturating_sub(started_ms);
            if age > i64::try_from(KEEP_LOGS.as_millis()).unwrap_or(i64::MAX) {
                remove_no_follow(&folder).map_err(|error| failed(&folder, error))?;
                self.notes
                    .push(format!("Deleted the old session folder {name}."));
                continue;
            }
            let Some((server, character)) = describe_old_session(&folder, &servers) else {
                let destination = paths.unsorted_logs_directory().join(&name);
                self.move_item(&folder, &destination)?;
                self.notes.push(format!(
                    "The old session folder {name} could not be read; it is now {}.",
                    destination.display()
                ));
                continue;
            };
            let parent = paths.session_logs_directory(&server, &character);
            std::fs::create_dir_all(&parent).map_err(|error| failed(&parent, error))?;
            let local_ms = started_ms + i64::from(zone.utc_offset_seconds(started_ms)) * 1000;
            let destination = unused_child(&parent, &session_folder_name(local_ms));
            (self.rename)(&folder, &destination).map_err(|error| failed(&folder, error))?;
        }
        Ok(())
    }

    /// Moves a file or folder to `destination`. A folder onto a folder
    /// merges, child by child; anything onto something already there keeps
    /// the newer in place and the other beside it (`<name>.old<ext>`).
    fn move_item(&mut self, source: &Path, destination: &Path) -> StepResult {
        let rename = self.rename;
        if std::fs::symlink_metadata(destination).is_err() {
            if let Some(parent) = destination.parent() {
                std::fs::create_dir_all(parent).map_err(|error| failed(parent, error))?;
            }
            return rename(source, destination).map_err(|error| failed(source, error));
        }
        // Judged through links: a linked folder is a folder.
        let incoming = std::fs::metadata(source)
            .or_else(|_| std::fs::symlink_metadata(source))
            .map_err(|error| failed(source, error))?;
        let existing = std::fs::metadata(destination)
            .or_else(|_| std::fs::symlink_metadata(destination))
            .map_err(|error| failed(destination, error))?;
        let real_source = std::fs::canonicalize(source).ok();
        // A link can make the two the same place: nothing to move.
        if real_source.is_some() && real_source == std::fs::canonicalize(destination).ok() {
            return Ok(());
        }
        if incoming.is_dir() && existing.is_dir() {
            let real_source = real_source.unwrap_or_else(|| source.to_path_buf());
            if self.merging.contains(&real_source) {
                // A link back to a folder being merged: following it would
                // never end, and what it points at is moved already.
                remove_no_follow(source).map_err(|error| failed(source, error))?;
                self.notes.push(format!(
                    "{} led back into a folder being moved; the link was removed.",
                    source.display()
                ));
                return Ok(());
            }
            self.merging.push(real_source);
            let merged = (|| {
                for child in std::fs::read_dir(source).map_err(|error| failed(source, error))? {
                    let child = child.map_err(|error| failed(source, error))?;
                    self.move_item(&child.path(), &destination.join(child.file_name()))?;
                }
                Ok(())
            })();
            self.merging.pop();
            merged?;
            let emptied = if is_link(source) {
                remove_no_follow(source)
            } else {
                std::fs::remove_dir(source)
            };
            return emptied.map_err(|error| failed(source, error));
        }
        // The same file already there: the one moving is not needed.
        if !incoming.is_dir()
            && !existing.is_dir()
            && same_contents(source, destination).map_err(|error| failed(source, error))?
        {
            return remove_no_follow(source).map_err(|error| failed(source, error));
        }
        // A folder keeps the name over a file (it holds what the name is
        // for); otherwise the newer one keeps it.
        let incoming_wins = if incoming.is_dir() != existing.is_dir() {
            incoming.is_dir()
        } else {
            match (incoming.modified(), existing.modified()) {
                (Ok(incoming), Ok(existing)) => incoming > existing,
                _ => false,
            }
        };
        let beside = beside_name(destination);
        if incoming_wins {
            rename(destination, &beside).map_err(|error| failed(destination, error))?;
            rename(source, destination).map_err(|error| failed(source, error))?;
        } else {
            rename(source, &beside).map_err(|error| failed(source, error))?;
        }
        self.notes.push(format!(
            "{} and {} were both there; {} keeps the name (a folder over a file, else the \
             newer) and the other is {}.",
            source.display(),
            destination.display(),
            destination.display(),
            beside.display()
        ));
        Ok(())
    }
}

/// `<name>.old<ext>` beside `path` (then `.old2`, `.old3`, ... while taken).
fn beside_name(path: &Path) -> PathBuf {
    let stem = path
        .file_stem()
        .map(|stem| stem.to_string_lossy().into_owned())
        .unwrap_or_default();
    let extension = path
        .extension()
        .map(|extension| format!(".{}", extension.to_string_lossy()))
        .unwrap_or_default();
    let parent = path.parent().unwrap_or(Path::new(""));
    (1..)
        .map(|n| {
            let mark = if n == 1 {
                ".old".to_owned()
            } else {
                format!(".old{n}")
            };
            parent.join(format!("{stem}{mark}{extension}"))
        })
        .find(|candidate| std::fs::symlink_metadata(candidate).is_err())
        .unwrap_or_else(|| path.to_path_buf())
}

/// The folders directly in `folder`; none when it does not exist. A link to
/// a folder (a symbolic link or a junction) counts as a folder.
fn subfolders(folder: &Path) -> io::Result<Vec<PathBuf>> {
    let entries = match std::fs::read_dir(folder) {
        Ok(entries) => entries,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => return Err(error),
    };
    let mut folders = Vec::new();
    for entry in entries {
        let entry = entry?;
        if std::fs::metadata(entry.path()).is_ok_and(|metadata| metadata.is_dir()) {
            folders.push(entry.path());
        }
    }
    folders.sort();
    Ok(folders)
}

fn remove_if_empty(folder: &Path) -> io::Result<()> {
    match std::fs::read_dir(folder) {
        Ok(entries) => {
            let mut entries = entries;
            if entries.next().is_none() {
                std::fs::remove_dir(folder)
            } else {
                Ok(())
            }
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
    }
}

/// Points every top-level path in a content record that lies inside `old`
/// at the same place inside `new`. A record that does not parse is left as
/// it is for the launcher's own checks to judge.
fn rewrite_record(record: &Path, old: &Path, new: &Path) -> io::Result<()> {
    let bytes = std::fs::read(record)?;
    let Ok(mut document) = json::parse(&bytes) else {
        return Ok(());
    };
    let Value::Object(members) = &mut document.value else {
        return Ok(());
    };
    let mut changed = false;
    for member in members.iter_mut() {
        let Value::String(text) = &member.value.value else {
            continue;
        };
        if let Some(moved) = moved_path(Path::new(text), old, new) {
            member.value = Node::made(Value::String(moved.to_string_lossy().into_owned()));
            changed = true;
        }
    }
    if !changed {
        return Ok(());
    }
    let mut text = String::new();
    json::write_value(&mut text, &document, 0);
    let mut temporary = record.as_os_str().to_owned();
    temporary.push(".layout.tmp");
    std::fs::write(&temporary, text)?;
    std::fs::rename(&temporary, record)
}

/// `path` inside `new` when it lies inside `old` (the platform's case rule).
fn moved_path(path: &Path, old: &Path, new: &Path) -> Option<PathBuf> {
    if !path.is_absolute() {
        return None;
    }
    let path = crate::paths::full_path(path, Path::new(""));
    let inside = path
        .ancestors()
        .find(|ancestor| same_folder(ancestor, old))?;
    let relative = path.strip_prefix(inside).ok()?;
    Some(if relative.as_os_str().is_empty() {
        new.to_path_buf()
    } else {
        new.join(relative)
    })
}

// --- Session folders ---------------------------------------------------------

/// Milliseconds since the Unix epoch.
fn unix_ms(at: SystemTime) -> i64 {
    match at.duration_since(UNIX_EPOCH) {
        Ok(after) => i64::try_from(after.as_millis()).unwrap_or(i64::MAX),
        Err(before) => -i64::try_from(before.duration().as_millis()).unwrap_or(i64::MAX),
    }
}

/// Days since 1970-01-01 of a proleptic Gregorian date.
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let year_of_era = year - era * 400;
    let month_index = (month + 9) % 12;
    let day_of_year = (153 * month_index + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// The folder name of a session started at `local_ms` (milliseconds since
/// the epoch on the local clock's face): `yyyy-MM-dd HH-mm-ss`.
pub fn session_folder_name(local_ms: i64) -> String {
    let seconds = local_ms.div_euclid(1000);
    let (year, month, day) = crate::status::civil_from_days(seconds.div_euclid(86_400));
    let second_of_day = seconds.rem_euclid(86_400);
    format!(
        "{year:04}-{month:02}-{day:02} {:02}-{:02}-{:02}",
        second_of_day / 3600,
        second_of_day / 60 % 60,
        second_of_day % 60
    )
}

/// The local start of a session folder named by [`session_folder_name`],
/// with or without a ` (n)` after it, as milliseconds since the epoch on
/// the local clock's face; `None` for any other name.
pub fn parse_session_folder_name(name: &str) -> Option<i64> {
    let stamp = match name.find(" (") {
        Some(at) => {
            let rest = &name[at + 2..];
            let number = rest.strip_suffix(')')?;
            if number.is_empty() || !number.bytes().all(|b| b.is_ascii_digit()) {
                return None;
            }
            &name[..at]
        }
        None => name,
    };
    let bytes = stamp.as_bytes();
    if bytes.len() != 19 {
        return None;
    }
    let shape_ok = bytes.iter().enumerate().all(|(index, byte)| match index {
        4 | 7 | 13 | 16 => *byte == b'-',
        10 => *byte == b' ',
        _ => byte.is_ascii_digit(),
    });
    if !shape_ok {
        return None;
    }
    let number = |range: std::ops::Range<usize>| stamp[range].parse::<i64>().ok();
    let (year, month, day) = (number(0..4)?, number(5..7)?, number(8..10)?);
    let (hour, minute, second) = (number(11..13)?, number(14..16)?, number(17..19)?);
    if !(1..=12).contains(&month)
        || !(1..=31).contains(&day)
        || hour > 23
        || minute > 59
        || second > 59
    {
        return None;
    }
    let days = days_from_civil(year, month, day);
    Some(((days * 86_400) + hour * 3600 + minute * 60 + second) * 1000)
}

/// The UTC start of an older layout's session folder, named
/// `<yyyyMMddHHmmssfff>-<32 hex digits>`, as milliseconds since the epoch.
pub fn parse_old_session_id(name: &str) -> Option<i64> {
    let (stamp, id) = name.split_once('-')?;
    if stamp.len() != 17
        || !stamp.bytes().all(|b| b.is_ascii_digit())
        || id.len() != 32
        || !id.bytes().all(|b| b.is_ascii_hexdigit())
    {
        return None;
    }
    let number = |range: std::ops::Range<usize>| stamp[range].parse::<i64>().ok();
    let (year, month, day) = (number(0..4)?, number(4..6)?, number(6..8)?);
    let (hour, minute, second) = (number(8..10)?, number(10..12)?, number(12..14)?);
    let millisecond = number(14..17)?;
    if !(1..=12).contains(&month) || !(1..=31).contains(&day) || hour > 23 || minute > 59 {
        return None;
    }
    let days = days_from_civil(year, month, day);
    Some((days * 86_400 + hour * 3600 + minute * 60 + second) * 1000 + millisecond)
}

/// `name`, or `name (2)`, `name (3)`, ... : the first not in `parent`.
fn unused_child(parent: &Path, name: &str) -> PathBuf {
    (1..)
        .map(|n| {
            if n == 1 {
                parent.join(name)
            } else {
                parent.join(format!("{name} ({n})"))
            }
        })
        .find(|candidate| std::fs::symlink_metadata(candidate).is_err())
        .unwrap_or_else(|| parent.join(name))
}

/// Makes the folder of a session started now by the launcher:
/// `logs/<server>/<character>/<local start time>`, with ` (2)`, ` (3)`, ...
/// after the time when a session of the same character started in the same
/// second. `character` is the account's name for a session started without
/// a character.
///
/// # Errors
/// The folder could not be made.
pub fn create_session_folder(
    paths: &PathSet,
    server: &str,
    character: &str,
    now: SystemTime,
    zone: &LocalZone,
) -> io::Result<PathBuf> {
    let now_ms = unix_ms(now);
    let name = session_folder_name(now_ms + i64::from(zone.utc_offset_seconds(now_ms)) * 1000);
    claim_session_folder(&paths.session_logs_directory(server, character).join(name))
}

/// Makes the session folder `wanted`, or, when a folder of that name is
/// already there, the first free one of `<name> (2)`, `<name> (3)`, ...
/// beside it; returns the folder made. Its parents are made as needed.
///
/// # Errors
/// The folder could not be made.
pub fn claim_session_folder(wanted: &Path) -> io::Result<PathBuf> {
    let (Some(parent), Some(name)) = (wanted.parent(), wanted.file_name()) else {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("'{}' is not a session folder", wanted.display()),
        ));
    };
    let name = name.to_string_lossy();
    std::fs::create_dir_all(parent)?;
    for n in 1..=1000 {
        let candidate = if n == 1 {
            parent.join(name.as_ref())
        } else {
            parent.join(format!("{name} ({n})"))
        };
        match std::fs::create_dir(&candidate) {
            Ok(()) => return Ok(candidate),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {}
            Err(error) => return Err(error),
        }
    }
    Err(io::Error::new(
        io::ErrorKind::AlreadyExists,
        format!("no free session folder name in {}", parent.display()),
    ))
}

/// The session folder of a client the launcher started, held for as long
/// as this lives so the launcher's cleanup leaves it alone.
#[derive(Debug)]
pub struct LauncherSessionFolder {
    directory: PathBuf,
    _hold: Option<File>,
}

impl LauncherSessionFolder {
    /// The folder.
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// Where the session's plugin logs go: `plugins/<plugin id>.log`.
    pub fn plugin_logs_directory(&self) -> PathBuf {
        self.directory
            .join(crate::paths::SESSION_PLUGIN_LOGS_FOLDER_NAME)
    }

    /// The session folder `config_file` was written in, when it is the
    /// session document of a launcher session folder
    /// (`logs/<server>/<character>/<start>/session.json` under this
    /// install); `None` for any other document, such as one written by
    /// hand. The document is held (a shared lock) while the folder is.
    pub fn claim(paths: &PathSet, config_file: &Path) -> Option<Self> {
        let config_file = crate::paths::full_path(config_file, &std::env::current_dir().ok()?);
        let named_right = config_file
            .file_name()
            .is_some_and(|name| name.eq_ignore_ascii_case(SESSION_CONFIG_FILE_NAME));
        let directory = config_file.parent()?;
        let logs = directory.parent()?.parent()?.parent()?;
        if !named_right || !same_folder(logs, &paths.logs_directory()) {
            return None;
        }
        let hold = File::open(&config_file)
            .ok()
            .filter(|file| file.try_lock_shared().is_ok());
        Some(Self {
            directory: directory.to_path_buf(),
            _hold: hold,
        })
    }
}

/// Whether a client holds the session folder (see
/// [`LauncherSessionFolder::claim`]).
fn is_held(folder: &Path) -> bool {
    let Ok(file) = File::open(folder.join(SESSION_CONFIG_FILE_NAME)) else {
        return false;
    };
    matches!(file.try_lock(), Err(TryLockError::WouldBlock))
}

/// The launcher profile's server names, by endpoint.
struct ServerNames(Vec<(String, i32, String)>);

impl ServerNames {
    /// The servers of the launcher's profile document (the version 2 file
    /// while the launcher has not written the current one yet); none when
    /// it cannot be read.
    fn read(paths: &PathSet) -> Self {
        let servers = std::fs::read(paths.launcher_profiles_file())
            .or_else(|_| std::fs::read(paths.older_launcher_profiles_file()))
            .ok()
            .and_then(|bytes| json::parse(&bytes).ok())
            .map(|document| {
                let mut found = Vec::new();
                let Ok(members) = json::object_members(&document) else {
                    return found;
                };
                let Some(servers) = members.iter().find(|member| member.name == "servers") else {
                    return found;
                };
                for server in json::array_items(&servers.value).unwrap_or_default() {
                    let Ok(fields) = json::object_members(server) else {
                        continue;
                    };
                    let field = |name: &str| fields.iter().find(|m| m.name == name);
                    let text = |name: &str| {
                        field(name).and_then(|m| json::string_or_null(&m.value).ok().flatten())
                    };
                    let port = field("port").and_then(|m| json::int32(&m.value).ok());
                    if let (Some(name), Some(host), Some(port)) = (text("name"), text("host"), port)
                    {
                        found.push((host, port, name));
                    }
                }
                found
            })
            .unwrap_or_default();
        Self(servers)
    }

    /// The server's name for an endpoint, else the host.
    fn name_for(&self, host: &str, port: i32) -> String {
        self.0
            .iter()
            .find(|(known_host, known_port, _)| {
                known_host.eq_ignore_ascii_case(host) && *known_port == port
            })
            .map_or_else(|| host.to_owned(), |(_, _, name)| name.clone())
    }
}

/// The server and character an old session folder belongs to: the server
/// from the document's endpoint, the character from the first time it
/// entered the world, else the account. `None` without a readable document.
fn describe_old_session(folder: &Path, servers: &ServerNames) -> Option<(String, String)> {
    let bytes = std::fs::read(folder.join(SESSION_CONFIG_FILE_NAME)).ok()?;
    let document = json::parse(&bytes).ok()?;
    let members = json::object_members(&document).ok()?;
    let sessions = members.iter().find(|member| member.name == "sessions")?;
    let session = json::array_items(&sessions.value).ok()?.first()?;
    let fields = json::object_members(session).ok()?;
    let field = |name: &str| fields.iter().find(|member| member.name == name);
    let account = json::string_or_null(&field("account")?.value).ok()??;
    let endpoint = json::object_members(&field("endpoint")?.value).ok()?;
    let endpoint_field = |name: &str| endpoint.iter().find(|member| member.name == name);
    let host = json::string_or_null(&endpoint_field("host")?.value).ok()??;
    let port = json::int32(&endpoint_field("port")?.value).ok()?;
    if account.trim().is_empty() || host.trim().is_empty() {
        return None;
    }
    let character = first_character(&folder.join(SESSION_STATUS_FILE_NAME)).unwrap_or(account);
    Some((servers.name_for(&host, port), character))
}

/// The `characterName` of a status file's first `enteredWorld` line.
fn first_character(status_file: &Path) -> Option<String> {
    let text = std::fs::read_to_string(status_file).ok()?;
    text.lines().find_map(|line| {
        let document = json::parse(line.as_bytes()).ok()?;
        let members = json::object_members(&document).ok()?;
        let field = |name: &str| members.iter().find(|member| member.name == name);
        if json::string_or_null(&field("e")?.value).ok()?? != "enteredWorld" {
            return None;
        }
        json::string_or_null(&field("characterName")?.value)
            .ok()?
            .filter(|name| !name.trim().is_empty())
    })
}

// --- Cleanup -----------------------------------------------------------------

/// Deletes what is older than [`KEEP_LOGS`]: launcher session folders (by
/// the start time in their name; never one a running client holds),
/// crash reports and the old plugin logs (by their last write). Server and
/// character folders left empty go too. A deletion that fails is noted and
/// skipped. Returns one line per failure.
pub fn clean_old_logs(paths: &PathSet, now: SystemTime, zone: &LocalZone) -> Vec<String> {
    let mut failures = Vec::new();
    let now_ms = unix_ms(now);
    let local_now_ms = now_ms + i64::from(zone.utc_offset_seconds(now_ms)) * 1000;
    let keep_ms = i64::try_from(KEEP_LOGS.as_millis()).unwrap_or(i64::MAX);
    let logs = paths.logs_directory();
    for server in subfolders(&logs).unwrap_or_default() {
        let reserved = server.file_name().is_some_and(|name| {
            let name = name.to_string_lossy();
            RESERVED_LOG_FOLDER_NAMES
                .iter()
                .any(|reserved| reserved.eq_ignore_ascii_case(&name))
        });
        // Nothing is deleted through a linked server or character folder:
        // what it points at is not this install's to clear out.
        if reserved || is_link(&server) {
            continue;
        }
        for character in subfolders(&server).unwrap_or_default() {
            if is_link(&character) {
                continue;
            }
            for session in subfolders(&character).unwrap_or_default() {
                let Some(started) = session
                    .file_name()
                    .and_then(|name| parse_session_folder_name(&name.to_string_lossy()))
                else {
                    continue;
                };
                if local_now_ms.saturating_sub(started) <= keep_ms || is_held(&session) {
                    continue;
                }
                // A linked session folder goes as a link; what it points at stays.
                if let Err(error) = remove_no_follow(&session) {
                    failures.push(format!(
                        "Could not delete the old session folder {}: {error}",
                        session.display()
                    ));
                }
            }
            let _ = remove_if_empty(&character);
        }
        let _ = remove_if_empty(&server);
    }
    for folder in [
        paths.crash_reports_directory(),
        paths.old_plugin_logs_directory(),
    ] {
        let Ok(entries) = std::fs::read_dir(&folder) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let old = entry
                .metadata()
                .ok()
                .filter(std::fs::Metadata::is_file)
                .and_then(|metadata| metadata.modified().ok())
                .is_some_and(|written| now_ms.saturating_sub(unix_ms(written)) > keep_ms);
            if old && let Err(error) = std::fs::remove_file(&path) {
                failures.push(format!(
                    "Could not delete the old log {}: {error}",
                    path.display()
                ));
            }
        }
    }
    failures
}

/// Opens a file for appending, making its folder.
///
/// # Errors
/// The folder or the file could not be made.
pub fn open_append(path: &Path) -> io::Result<File> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    OpenOptions::new().create(true).append(true).open(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A rename that always finds the two places on different volumes,
    /// as one into a plugins folder linked to another drive does.
    fn other_volume(_: &Path, _: &Path) -> io::Result<()> {
        Err(io::Error::from(io::ErrorKind::CrossesDevices))
    }

    /// [`rename_or_copy`] with [`other_volume`] as the rename.
    fn across_volumes(source: &Path, destination: &Path) -> io::Result<()> {
        match other_volume(source, destination) {
            Err(error) if error.kind() == io::ErrorKind::CrossesDevices => {
                copy_then_delete(source, destination)
            }
            other => other,
        }
    }

    #[test]
    fn a_move_to_another_volume_copies_checks_and_then_deletes() {
        let root = std::env::temp_dir().join(format!(
            "openac-layout-volumes-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let paths = PathSet::for_root(&root, crate::paths::RootSource::Explicit);
        let old = root.join("vtank").join("mosstank").join("metas");
        std::fs::create_dir_all(&old).unwrap();
        std::fs::write(old.join("a.met"), "meta").unwrap();
        let big: Vec<u8> = (0..200_000u32).map(|n| (n % 251) as u8).collect();
        std::fs::write(old.join("big.met"), &big).unwrap();

        let migration = migrate_with(
            &paths,
            SystemTime::now(),
            &LocalZone::fixed(0),
            across_volumes,
        )
        .unwrap();

        assert!(migration.moved);
        let new = paths
            .vtank_profiles_directory()
            .join("mosstank")
            .join("metas");
        assert_eq!(std::fs::read_to_string(new.join("a.met")).unwrap(), "meta");
        assert_eq!(std::fs::read(new.join("big.met")).unwrap(), big);
        assert!(!root.join("vtank").exists());
        assert!(is_current(&paths));
        let _ = std::fs::remove_dir_all(&root);
    }

    fn temporary_root(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "openac-layout-{name}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ))
    }

    /// A link to a folder, or `false` where the platform makes none.
    fn link_folder(link: &Path, target: &Path) -> bool {
        #[cfg(unix)]
        {
            std::os::unix::fs::symlink(target, link).is_ok()
        }
        #[cfg(windows)]
        {
            if std::os::windows::fs::symlink_dir(target, link).is_ok() {
                return true;
            }
            std::process::Command::new("cmd.exe")
                .args(["/d", "/c", "mklink", "/J"])
                .arg(link)
                .arg(target)
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .status()
                .is_ok_and(|status| status.success())
        }
        #[cfg(not(any(unix, windows)))]
        {
            false
        }
    }

    #[test]
    fn a_copy_to_another_volume_does_not_follow_a_link_back_into_itself() {
        let root = temporary_root("copy-cycle");
        let paths = PathSet::for_root(&root, crate::paths::RootSource::Explicit);
        let old = root.join("vtank");
        std::fs::create_dir_all(old.join("metas")).unwrap();
        std::fs::write(old.join("metas").join("a.met"), "meta").unwrap();
        if !link_folder(&old.join("metas").join("loop"), &old) {
            let _ = std::fs::remove_dir_all(&root);
            return;
        }

        migrate_with(
            &paths,
            SystemTime::now(),
            &LocalZone::fixed(0),
            across_volumes,
        )
        .unwrap();

        let new = paths.vtank_profiles_directory().join("metas");
        assert_eq!(std::fs::read_to_string(new.join("a.met")).unwrap(), "meta");
        assert!(!old.exists());
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn a_copy_to_another_volume_goes_through_a_temporary_name() {
        let root = temporary_root("copy-temporary");
        let source = root.join("source.met");
        let destination = root.join("destination.met");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(&source, "meta").unwrap();
        // What a copy stopped part-way left.
        std::fs::write(root.join("destination.met.copying"), "me").unwrap();

        copy_then_delete(&source, &destination).unwrap();

        assert_eq!(std::fs::read_to_string(&destination).unwrap(), "meta");
        assert!(!source.exists());
        assert!(!root.join("destination.met.copying").exists());
        let _ = std::fs::remove_dir_all(&root);
    }
}
