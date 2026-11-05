//! The install's session lock, `app/.update-session.lock`. Every running
//! client, windowless host and launcher session holds it shared for its
//! whole lifetime; anything that rewrites the install underneath them (a
//! client update, moving the install folder) needs it exclusively, and so
//! refuses while any of them runs, however it was started. The launcher's
//! own barrier opens the same file the same way.

use std::fs::{File, OpenOptions, TryLockError};
use std::path::PathBuf;

use crate::paths::PathSet;

/// The lock file's name inside `app/`.
pub const LOCK_FILE_NAME: &str = ".update-session.lock";

/// A hold on the install's session lock; released when dropped.
#[derive(Debug)]
pub struct InstallSessionLease {
    _file: File,
}

impl InstallSessionLease {
    /// The lock file of an install.
    pub fn lock_path(paths: &PathSet) -> PathBuf {
        paths.app_directory().join(LOCK_FILE_NAME)
    }

    /// Takes the lock alongside every other running session, or `None`
    /// while something holds it exclusively (an update or a move).
    pub fn try_acquire_shared(paths: &PathSet) -> std::io::Result<Option<Self>> {
        Self::try_open(paths, false)
    }

    /// Takes the lock alone, or `None` while any session or update holds it.
    pub fn try_acquire_exclusive(paths: &PathSet) -> std::io::Result<Option<Self>> {
        Self::try_open(paths, true)
    }

    /// What a starting client or bot says when it cannot take the lock.
    pub fn busy_message(paths: &PathSet) -> String {
        format!(
            "A client update or a move of the install folder is running in {}. \
             Start again when it has finished.",
            paths.root_directory().display()
        )
    }

    fn try_open(paths: &PathSet, exclusive: bool) -> std::io::Result<Option<Self>> {
        let path = Self::lock_path(paths);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)?;
        let locked = if exclusive {
            file.try_lock()
        } else {
            file.try_lock_shared()
        };
        match locked {
            Ok(()) => Ok(Some(Self { _file: file })),
            // Held in a way this request cannot share: that is the answer.
            Err(TryLockError::WouldBlock) => Ok(None),
            Err(TryLockError::Error(error)) => Err(error),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::paths::RootSource;

    // OpenAC InstallSessionLeaseTests.SessionsShareTheLockAndAWriterNeedsItAlone.
    #[test]
    fn sessions_share_the_lock_and_a_writer_needs_it_alone() {
        let root = std::env::temp_dir().join(format!(
            "openac-lease-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |d| d.as_nanos())
        ));
        let paths = PathSet::for_root(&root, RootSource::Explicit);
        {
            let first = InstallSessionLease::try_acquire_shared(&paths).unwrap();
            let second = InstallSessionLease::try_acquire_shared(&paths).unwrap();
            assert!(first.is_some());
            assert!(second.is_some());
            assert!(
                InstallSessionLease::try_acquire_exclusive(&paths)
                    .unwrap()
                    .is_none()
            );
        }
        {
            let writer = InstallSessionLease::try_acquire_exclusive(&paths).unwrap();
            assert!(writer.is_some());
            assert!(
                InstallSessionLease::try_acquire_shared(&paths)
                    .unwrap()
                    .is_none()
            );
        }
        assert_eq!(
            InstallSessionLease::lock_path(&paths),
            root.join("app").join(".update-session.lock")
        );
        let _ = std::fs::remove_dir_all(&root);
    }
}
