//! The lock that keeps an update from replacing files a running session
//! uses: every session holds a shared lease on one file, an update or a
//! plugin install holds it exclusively. Works across processes.

use std::fs::{File, OpenOptions, TryLockError};
use std::path::{Path, PathBuf};

use super::UpdateError;
use crate::fsutil;

/// The lock file's name in `<data>/app`.
pub const UPDATE_LOCK_FILE_NAME: &str = ac_host_core::install_lease::LOCK_FILE_NAME;

/// The barrier for one data directory.
#[derive(Debug, Clone)]
pub struct UpdateSessionBarrier {
    lock_path: PathBuf,
}

/// A running session's shared hold; released when dropped.
#[derive(Debug)]
pub struct SessionLease {
    file: File,
}

/// An update's exclusive hold; released when dropped.
#[derive(Debug)]
pub struct ExclusiveLease {
    file: File,
    lock_path: PathBuf,
}

/// Releases a lease's lock. Closing the file alone releases it only once
/// every handle to that open file is closed, and a process being started
/// holds a copy of each until it has loaded its program: the lock would
/// outlive the lease by that long, and a lease asked for right after would
/// be refused. Nothing can be done about a failure here; the close that
/// follows releases the lock as before.
fn release(file: &File) {
    let _ = file.unlock();
}

impl Drop for SessionLease {
    fn drop(&mut self) {
        release(&self.file);
    }
}

impl Drop for ExclusiveLease {
    fn drop(&mut self) {
        release(&self.file);
    }
}

impl ExclusiveLease {
    /// Whether this lease was taken from `barrier`.
    pub fn is_held_by(&self, barrier: &UpdateSessionBarrier) -> bool {
        fsutil::paths_equal(&self.lock_path, &barrier.lock_path)
    }
}

const SESSION_REFUSAL: &str = "A client update is in progress.";
const EXCLUSIVE_REFUSAL: &str = "A launcher session or another update transaction is running. \
                                 Stop every launcher session before updating.";

impl UpdateSessionBarrier {
    /// The barrier for `<data_directory>/app/.update-session.lock`.
    pub fn new(data_directory: &Path) -> Self {
        Self {
            lock_path: fsutil::full_path(data_directory)
                .join("app")
                .join(UPDATE_LOCK_FILE_NAME),
        }
    }

    /// The lock file.
    pub fn lock_path(&self) -> &Path {
        &self.lock_path
    }

    fn open(&self) -> Result<File, UpdateError> {
        let parent = self.lock_path.parent().ok_or_else(|| {
            UpdateError::failed("The update/session lock path has no parent directory.")
        })?;
        std::fs::create_dir_all(parent).map_err(|error| {
            UpdateError::io(
                format!("The update/session lease could not be opened: {error}"),
                error,
            )
        })?;
        OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&self.lock_path)
            .map_err(|error| {
                UpdateError::io(
                    format!("The update/session lease could not be opened: {error}"),
                    error,
                )
            })
    }

    /// Takes a shared lease, or `None` while an update holds the barrier.
    pub fn try_acquire_session(&self) -> Result<Option<SessionLease>, UpdateError> {
        let file = self.open()?;
        match file.try_lock_shared() {
            Ok(()) => Ok(Some(SessionLease { file })),
            Err(TryLockError::WouldBlock) => Ok(None),
            Err(TryLockError::Error(error)) => Err(UpdateError::io(
                format!("The update/session lease could not be opened: {error}"),
                error,
            )),
        }
    }

    /// Takes a shared lease; refused while an update holds the barrier.
    pub fn acquire_session(&self) -> Result<SessionLease, UpdateError> {
        self.try_acquire_session()?.ok_or_else(|| {
            UpdateError::io(
                SESSION_REFUSAL,
                std::io::Error::from(std::io::ErrorKind::WouldBlock),
            )
        })
    }

    /// Takes the exclusive lease, or `None` while a session or another
    /// update holds the barrier.
    pub fn try_acquire_exclusive(&self) -> Result<Option<ExclusiveLease>, UpdateError> {
        let file = self.open()?;
        match file.try_lock() {
            Ok(()) => Ok(Some(ExclusiveLease {
                file,
                lock_path: self.lock_path.clone(),
            })),
            Err(TryLockError::WouldBlock) => Ok(None),
            Err(TryLockError::Error(error)) => Err(UpdateError::io(
                format!("The update/session lease could not be opened: {error}"),
                error,
            )),
        }
    }

    /// Takes the exclusive lease; refused while a session or another update
    /// holds the barrier. The refusal carries an I/O cause, which callers
    /// use to tell "busy" from other failures.
    pub fn acquire_exclusive(&self) -> Result<ExclusiveLease, UpdateError> {
        self.try_acquire_exclusive()?.ok_or_else(|| {
            UpdateError::io(
                EXCLUSIVE_REFUSAL,
                std::io::Error::from(std::io::ErrorKind::WouldBlock),
            )
        })
    }

    /// Refuses a lease taken from another barrier.
    pub fn require_owned(&self, lease: &ExclusiveLease) -> Result<(), UpdateError> {
        if lease.is_held_by(self) {
            Ok(())
        } else {
            Err(UpdateError::failed(
                "The cleanup operation does not hold this update barrier's exclusive lease.",
            ))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ids::unique_hex32;

    // A process being started holds a copy of every open file of this one
    // until it has loaded its program. Closing a lease's file does not
    // release the lock while such a copy is alive, so a lease taken right
    // after one was dropped (staging, then a client start) was refused now
    // and then. Dropping a lease releases the lock itself.
    #[test]
    fn a_dropped_lease_is_released_while_a_copy_of_its_file_is_open() {
        let root = std::env::temp_dir().join(format!("acl-barrier-{}", unique_hex32()));
        let barrier = UpdateSessionBarrier::new(&root);

        let update = barrier.acquire_exclusive().unwrap();
        let copy = update.file.try_clone().unwrap();
        drop(update);
        let session = barrier.acquire_session().expect("the update is over");

        let copy_of_session = session.file.try_clone().unwrap();
        drop(session);
        barrier.acquire_exclusive().expect("no session is left");

        drop((copy, copy_of_session));
        crate::fsutil::try_delete_tree(&root);
    }
}
