//! Moving the whole install folder somewhere the player picked, then
//! pointing the default folder at it. Nothing may be running from the
//! install while it moves, so the move holds the update/session lock the
//! launcher's sessions, updates and every running client share. On one
//! volume each entry is renamed (and renamed back if one refuses); across
//! volumes everything is copied first and the old folder is deleted only
//! after the copy is complete and the pointer written, so a failure leaves
//! the old install untouched. The rebuildable cache is not carried.

use std::path::{Path, PathBuf};

use ac_host_core::json::{self as host_json, Value};
use ac_host_core::paths::{root_pointer, same_folder, same_or_inside};
use ac_host_core::{PathSet, RootSource};

use crate::updates::UpdateSessionBarrier;

/// How a move of the install folder ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstallRootMoveResult {
    /// True when the install now lives at the new folder.
    pub moved: bool,
    /// What to tell the player.
    pub message: String,
    /// Old entries that could not be deleted after a successful move, and
    /// warnings about records left for the next start.
    pub leftovers: Vec<String>,
}

impl InstallRootMoveResult {
    fn refused(message: impl Into<String>) -> Self {
        Self {
            moved: false,
            message: message.into(),
            leftovers: Vec::new(),
        }
    }
}

/// Why a move was refused while something runs from the install.
pub const MOVE_SESSION_REFUSAL: &str =
    "Close every OpenAC client and bot before moving the install folder.";

const CACHE_FOLDER_NAME: &str = "cache";

/// Whether a rename can move between two paths.
pub type SameVolume = Box<dyn Fn(&Path, &Path) -> bool + Send + Sync>;

/// See the module documentation.
pub struct InstallRootMover {
    paths: PathSet,
    default_root: PathBuf,
    barrier: UpdateSessionBarrier,
    same_volume: SameVolume,
}

impl std::fmt::Debug for InstallRootMover {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InstallRootMover")
            .field("paths", &self.paths)
            .field("default_root", &self.default_root)
            .finish_non_exhaustive()
    }
}

impl InstallRootMover {
    /// The mover for the install as this launcher resolved it; the pointer
    /// lives in `default_root`. `same_volume` says whether a rename can
    /// move between two paths (tests force the copy path); `None` asks the
    /// system.
    pub fn new(paths: &PathSet, default_root: &Path, same_volume: Option<SameVolume>) -> Self {
        Self {
            paths: paths.clone(),
            default_root: ac_host_core::paths::full_path(default_root, Path::new("")),
            barrier: UpdateSessionBarrier::new(&paths.data_directory),
            same_volume: same_volume.unwrap_or_else(|| Box::new(default_same_volume)),
        }
    }

    /// The folder being moved.
    pub fn root_directory(&self) -> &Path {
        self.paths.root_directory()
    }

    /// Why this install cannot be moved from here at all, or `None`. A
    /// folder named on the command line or in the environment is not the
    /// pointer's to change: the next start would use that name again.
    pub fn unavailable_reason(&self) -> Option<&'static str> {
        match self.paths.root_source {
            RootSource::Pointer | RootSource::Default => None,
            RootSource::Explicit | RootSource::Environment => Some(
                "This install folder was set on the command line or in the environment, so it \
                 has to be changed there.",
            ),
        }
    }

    /// Why `target` cannot receive the install, or `None`.
    pub fn validate_target(&self, target: &Path) -> Option<String> {
        if target.as_os_str().to_string_lossy().trim().is_empty() || !target.is_absolute() {
            return Some("Choose a full folder path.".into());
        }
        let full = ac_host_core::paths::full_path(target, Path::new(""));
        let root = self.root_directory();
        if same_folder(&full, root) {
            return Some("The install is already in that folder.".into());
        }
        if same_or_inside(&full, root) {
            return Some("The new folder cannot be inside the current install folder.".into());
        }
        if same_or_inside(root, &full) {
            return Some("The new folder cannot contain the current install folder.".into());
        }
        if full.is_dir() {
            let occupied = std::fs::read_dir(&full).map_or(true, |entries| {
                entries
                    .flatten()
                    .any(|entry| !self.is_pointer_of_default(&entry.path()))
            });
            if occupied {
                return Some("Choose an empty folder, or one that does not exist yet.".into());
            }
        }
        full.is_file()
            .then(|| "A file already has that name.".to_owned())
    }

    /// Moves the install to `target`; `progress` is told the name of each
    /// entry as it moves.
    pub fn move_to(&self, target: &Path, progress: &mut dyn FnMut(&str)) -> InstallRootMoveResult {
        if let Some(reason) = self.unavailable_reason() {
            return InstallRootMoveResult::refused(reason);
        }
        if let Some(invalid) = self.validate_target(target) {
            return InstallRootMoveResult::refused(invalid);
        }
        let destination = ac_host_core::paths::full_path(target, Path::new(""));
        let lease = match self.barrier.try_acquire_exclusive() {
            Ok(Some(lease)) => lease,
            Ok(None) => return InstallRootMoveResult::refused(MOVE_SESSION_REFUSAL),
            Err(error) => return InstallRootMoveResult::refused(error.to_string()),
        };
        let same_volume = (self.same_volume)(self.root_directory(), &destination);
        let mut warnings = Vec::new();
        {
            let _lease = lease;
            let mut created = Vec::new();
            let mut renamed = Vec::new();
            let attempt = (|| -> std::io::Result<()> {
                create_directory_tracked(&destination, &mut created)?;
                if same_volume {
                    self.rename_into(&destination, &mut renamed, &mut created, progress)?;
                } else {
                    self.copy_into(&destination, progress)?;
                }
                // The pointer goes down the moment the install is whole at
                // the new folder: from here on every start finds it.
                root_pointer::write(&self.default_root, &destination)
            })();
            if let Err(failure) = attempt {
                let stranded = if same_volume {
                    undo_renames(&renamed, &created)
                } else {
                    self.undo_copy(&destination, &created)
                };
                let mut message = format!("The install folder was not moved: {failure}");
                if !stranded.is_empty() {
                    message.push_str(&format!(
                        " Some items could not be put back and are still in {}: {}",
                        destination.display(),
                        stranded.join("; ")
                    ));
                }
                return InstallRootMoveResult::refused(message);
            }
            // Also done at every launcher start, so a failure here only
            // defers it.
            if let Err(error) =
                repair_content_records(&PathSet::for_root(&destination, RootSource::Pointer))
            {
                warnings.push(format!(
                    "The install record will be updated at the next start: {error}"
                ));
            }
        }
        // The lock is released before the old folder goes: it lives there.
        let mut leftovers = warnings;
        leftovers.extend(self.delete_old_root());
        let message = if leftovers.is_empty() {
            format!("The install folder is now {}.", destination.display())
        } else {
            format!(
                "The install folder is now {}. Some old files could not be deleted from {}; \
                 they are no longer used.",
                destination.display(),
                self.root_directory().display()
            )
        };
        InstallRootMoveResult {
            moved: true,
            message,
            leftovers,
        }
    }

    /// The entries that move: everything but the cache, the pointer and
    /// the lock file, in name order.
    fn entries_to_move(&self, folder: &Path, is_root: bool) -> std::io::Result<Vec<PathBuf>> {
        let mut entries: Vec<PathBuf> = std::fs::read_dir(folder)?
            .map(|entry| entry.map(|entry| entry.path()))
            .collect::<Result<_, _>>()?;
        entries.sort();
        Ok(entries
            .into_iter()
            .filter(|entry| {
                let name = entry
                    .file_name()
                    .map(|name| name.to_string_lossy().into_owned())
                    .unwrap_or_default();
                let skipped_at_root = is_root
                    && (name.eq_ignore_ascii_case(CACHE_FOLDER_NAME)
                        || self.is_pointer_of_default(entry));
                !skipped_at_root && !same_folder(entry, self.barrier.lock_path())
            })
            .collect())
    }

    fn rename_into(
        &self,
        destination: &Path,
        renamed: &mut Vec<(PathBuf, PathBuf)>,
        created: &mut Vec<PathBuf>,
        progress: &mut dyn FnMut(&str),
    ) -> std::io::Result<()> {
        let app = self.paths.app_directory();
        for entry in self.entries_to_move(self.root_directory(), true)? {
            let name = file_name(&entry);
            let to = destination.join(&name);
            if same_folder(&entry, &app) {
                // The app folder holds the lock this move is holding, so
                // its entries move one by one around it.
                create_directory_tracked(&to, created)?;
                for child in self.entries_to_move(&entry, false)? {
                    let child_name = file_name(&child);
                    let child_to = to.join(&child_name);
                    progress(&format!("{name}/{child_name}"));
                    std::fs::rename(&child, &child_to)?;
                    renamed.push((child, child_to));
                }
                continue;
            }
            progress(&name);
            std::fs::rename(&entry, &to)?;
            renamed.push((entry, to));
        }
        Ok(())
    }

    fn copy_into(&self, destination: &Path, progress: &mut dyn FnMut(&str)) -> std::io::Result<()> {
        for entry in self.entries_to_move(self.root_directory(), true)? {
            let name = file_name(&entry);
            progress(&name);
            self.copy_entry(&entry, &destination.join(&name))?;
        }
        Ok(())
    }

    fn copy_entry(&self, source: &Path, destination: &Path) -> std::io::Result<()> {
        if source.is_file() {
            if destination.exists() {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::AlreadyExists,
                    format!("'{}' already exists.", destination.display()),
                ));
            }
            std::fs::copy(source, destination)?;
            let modified = std::fs::metadata(source)?.modified()?;
            std::fs::File::options()
                .write(true)
                .open(destination)?
                .set_modified(modified)?;
            return Ok(());
        }
        std::fs::create_dir_all(destination)?;
        for child in self.entries_to_move(source, false)? {
            self.copy_entry(&child, &destination.join(file_name(&child)))?;
        }
        Ok(())
    }

    /// Removes only what the copy created; the old install is untouched
    /// and still the one in use. Returns what could not be removed.
    fn undo_copy(&self, destination: &Path, created: &[PathBuf]) -> Vec<String> {
        let mut stranded = Vec::new();
        for entry in self
            .entries_to_move(self.root_directory(), true)
            .unwrap_or_default()
        {
            let copied = destination.join(file_name(&entry));
            let removed = if copied.is_dir() {
                std::fs::remove_dir_all(&copied)
            } else if copied.is_file() {
                std::fs::remove_file(&copied)
            } else {
                Ok(())
            };
            if let Err(error) = removed {
                stranded.push(format!("{} ({error})", copied.display()));
            }
        }
        remove_created_folders(created, &mut stranded);
        stranded
    }

    /// Deletes what is left of the old install. The default folder keeps
    /// its pointer, which is what now leads every start to the new folder.
    fn delete_old_root(&self) -> Vec<String> {
        let root = self.root_directory();
        let mut leftovers = Vec::new();
        let Ok(entries) = std::fs::read_dir(root) else {
            return leftovers;
        };
        for entry in entries.flatten().map(|entry| entry.path()) {
            if self.is_pointer_of_default(&entry) {
                continue;
            }
            let removed = if entry.is_dir() {
                std::fs::remove_dir_all(&entry)
            } else {
                std::fs::remove_file(&entry)
            };
            if let Err(error) = removed {
                leftovers.push(format!("{}: {error}", entry.display()));
            }
        }
        if leftovers.is_empty()
            && !same_folder(root, &self.default_root)
            && let Err(error) = std::fs::remove_dir(root)
        {
            leftovers.push(format!("{}: {error}", root.display()));
        }
        leftovers
    }

    fn is_pointer_of_default(&self, entry: &Path) -> bool {
        same_folder(entry, &root_pointer::path_for(&self.default_root))
    }
}

/// Points a root's install and verification records at that root's own
/// prepared content when they still name the content under another root:
/// the root was moved and the records came along unchanged. Run at every
/// launcher start, so a move interrupted after the pointer was written is
/// finished the next time. A record naming a path inside this root, or one
/// whose canonical content is not there, is left alone for the launcher to
/// judge. Returns the records it rewrote.
pub fn repair_content_records(paths: &PathSet) -> std::io::Result<Vec<PathBuf>> {
    let data = paths.game_data_directory();
    let package = data.join("pak").join("acdream.pak");
    let mut repaired = Vec::new();
    for (record, property) in [
        (data.join("install.json"), "preparedAssetPath"),
        (data.join("install.verification.json"), "path"),
    ] {
        if !record.is_file() || !package.is_file() {
            continue;
        }
        let bytes = std::fs::read(&record)?;
        let mut document = host_json::parse(&bytes).map_err(|error| {
            std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                format!("'{}' is not readable: {error}", record.display()),
            )
        })?;
        let Value::Object(members) = &mut document.value else {
            continue;
        };
        let Some(member) = members.iter_mut().find(|member| member.name == property) else {
            continue;
        };
        let Value::String(recorded) = &member.value.value else {
            continue;
        };
        let recorded = Path::new(recorded);
        if recorded.as_os_str().to_string_lossy().trim().is_empty()
            || !recorded.is_absolute()
            || same_or_inside(recorded, paths.root_directory())
        {
            continue;
        }
        // The launcher refuses a record whose content path is not the
        // canonical one for its root, so a moved record that still named
        // the old path would read as a broken install.
        member.value = host_json::Node::made(Value::String(package.to_string_lossy().into_owned()));
        let mut text = String::new();
        host_json::write_value(&mut text, &document, 0);
        let mut temporary = record.as_os_str().to_owned();
        temporary.push(".repair.tmp");
        std::fs::write(&temporary, text)?;
        std::fs::rename(&temporary, &record)?;
        repaired.push(record);
    }
    Ok(repaired)
}

fn file_name(path: &Path) -> String {
    path.file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn create_directory_tracked(path: &Path, created: &mut Vec<PathBuf>) -> std::io::Result<()> {
    if path.is_dir() {
        return Ok(());
    }
    std::fs::create_dir_all(path)?;
    created.push(path.to_path_buf());
    Ok(())
}

/// Renames everything back and removes the folders the move created, so
/// the same target can be tried again. Returns what could not be undone.
fn undo_renames(renamed: &[(PathBuf, PathBuf)], created: &[PathBuf]) -> Vec<String> {
    let mut stranded = Vec::new();
    for (from, to) in renamed.iter().rev() {
        if let Err(error) = std::fs::rename(to, from) {
            stranded.push(format!("{} ({error})", to.display()));
        }
    }
    remove_created_folders(created, &mut stranded);
    stranded
}

fn remove_created_folders(created: &[PathBuf], stranded: &mut Vec<String>) {
    for folder in created.iter().rev() {
        let empty = std::fs::read_dir(folder).is_ok_and(|mut entries| entries.next().is_none());
        if empty && let Err(error) = std::fs::remove_dir(folder) {
            stranded.push(format!("{} ({error})", folder.display()));
        }
    }
}

/// Whether a rename can move between two paths. There is no portable way
/// to ask whether two paths share a device, so outside Windows the install
/// is copied: the copy path is the one that can never leave it half moved.
fn default_same_volume(left: &Path, right: &Path) -> bool {
    if !cfg!(windows) {
        return false;
    }
    let prefix = |path: &Path| {
        path.components()
            .next()
            .map(|component| component.as_os_str().to_string_lossy().to_lowercase())
    };
    prefix(left) == prefix(right)
}
