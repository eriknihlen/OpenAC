//! File-system helpers shared by the stores: full paths, platform path
//! comparison, atomic replacement, owner-only files, and deleting a tree
//! without following links.

use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use crate::ids::unique_hex32;

/// `path` made absolute against the working directory, `.` and `..`
/// resolved by text alone (OpenAC's `Path.GetFullPath`).
pub fn full_path(path: &Path) -> PathBuf {
    let base = std::env::current_dir().unwrap_or_default();
    ac_host_core::paths::full_path(path, &base)
}

/// Whether two paths name the same place: full paths compared ignoring case
/// on Windows and exactly elsewhere.
pub fn paths_equal(left: &Path, right: &Path) -> bool {
    text_equal(
        &full_path(left).to_string_lossy(),
        &full_path(right).to_string_lossy(),
    )
}

/// Path text compared the platform's way (ignoring case on Windows).
pub fn text_equal(left: &str, right: &str) -> bool {
    if cfg!(windows) {
        left.to_lowercase() == right.to_lowercase()
    } else {
        left == right
    }
}

/// True for a symbolic link, a junction or any other reparse point. Missing
/// paths are an error.
pub fn is_link(path: &Path) -> io::Result<bool> {
    let metadata = fs::symlink_metadata(path)?;
    Ok(metadata_is_link(&metadata))
}

/// True when the (not followed) metadata is a link or reparse point.
pub fn metadata_is_link(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        metadata.file_type().is_symlink()
            || metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

/// Deletes a file, ignoring every failure (a missing file included).
pub fn try_delete_file(path: &Path) {
    if fs::symlink_metadata(path).is_ok_and(|metadata| !metadata.is_dir()) {
        let _ = fs::remove_file(path);
    }
}

/// Deletes a directory tree without following links inside it; ignores every
/// failure (OpenAC's `TryDeleteDirectory`: leftover names are reclaimed by a
/// later recovery pass).
pub fn try_delete_tree(path: &Path) {
    if fs::symlink_metadata(path).is_ok() {
        let _ = delete_tree_no_follow(path);
    }
}

/// Deletes a directory tree; a link is removed itself, never followed.
pub fn delete_tree_no_follow(path: &Path) -> io::Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata_is_link(&metadata) {
        return delete_link(path);
    }
    if !metadata.is_dir() {
        return fs::remove_file(path);
    }
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        let child = entry.path();
        let child_metadata = fs::symlink_metadata(&child)?;
        if metadata_is_link(&child_metadata) {
            delete_link(&child)?;
        } else if child_metadata.is_dir() {
            delete_tree_no_follow(&child)?;
        } else {
            fs::remove_file(&child)?;
        }
    }
    fs::remove_dir(path)
}

fn delete_link(path: &Path) -> io::Result<()> {
    fs::remove_file(path).or_else(|_| fs::remove_dir(path))
}

/// Whether a directory exists and has no entries.
pub fn is_empty_directory(path: &Path) -> bool {
    fs::read_dir(path).is_ok_and(|mut entries| entries.next().is_none())
}

/// Deletes a directory only when it is empty.
pub fn try_delete_if_empty(path: &Path) {
    if is_empty_directory(path) {
        try_delete_tree(path);
    }
}

/// Creates a new file (failing when it exists). `owner_only` creates it with
/// mode 0600 on Unix.
pub fn create_new(path: &Path, owner_only: bool) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        if owner_only {
            options.mode(0o600);
        }
    }
    #[cfg(not(unix))]
    let _ = owner_only;
    options.open(path)
}

/// The permission bits of a file (Unix); `None` elsewhere.
pub fn unix_mode(path: &Path) -> io::Result<Option<u32>> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        Ok(Some(fs::metadata(path)?.permissions().mode() & 0o7777))
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        Ok(None)
    }
}

/// Sets the permission bits of a file on Unix; does nothing elsewhere.
pub fn set_unix_mode(path: &Path, mode: u32) -> io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(path, fs::Permissions::from_mode(mode))
    }
    #[cfg(not(unix))]
    {
        let _ = (path, mode);
        Ok(())
    }
}

/// Makes an existing credential-bearing file readable and writable by its
/// owner only (mode 0600) on Unix; checks it stuck.
pub fn ensure_owner_only(path: &Path) -> io::Result<()> {
    if cfg!(unix) {
        if unix_mode(path)? != Some(0o600) {
            set_unix_mode(path, 0o600)?;
        }
        let mode = unix_mode(path)?;
        if mode != Some(0o600) {
            return Err(io::Error::other(format!(
                "Mode remained {:o} after normalization.",
                mode.unwrap_or_default()
            )));
        }
    }
    Ok(())
}

/// Writes `bytes` to `path` through `path.tmp` and a rename, as the profile
/// and plugin-record stores do: a stale temp file is removed first, the temp
/// file is owner-only when asked, and a failure removes it again.
pub fn write_via_tmp(path: &Path, bytes: &[u8], owner_only: bool) -> io::Result<()> {
    if let Some(directory) = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(directory)?;
    }
    let mut temp = path.as_os_str().to_owned();
    temp.push(".tmp");
    let temp = PathBuf::from(temp);
    try_delete_file(&temp);
    let result = (|| {
        {
            let mut file = create_new(&temp, owner_only)?;
            if owner_only {
                set_unix_mode(&temp, 0o600)?;
            }
            file.write_all(bytes)?;
            file.flush()?;
        }
        if owner_only && unix_mode(&temp)?.is_some_and(|mode| mode != 0o600) {
            return Err(io::Error::other(
                "The temp file could not be secured to mode 0600.",
            ));
        }
        fs::rename(&temp, path)
    })();
    if result.is_err() {
        try_delete_file(&temp);
    }
    result
}

/// Writes `bytes` to `path` through a uniquely named hidden temp file beside
/// it (`.<name>.<hex>.tmp`), flushed to disk before the rename (OpenAC's
/// `AtomicJsonFile`).
pub fn write_atomic_unique(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let full = full_path(path);
    let directory = full
        .parent()
        .ok_or_else(|| io::Error::other("The file path has no parent directory."))?;
    fs::create_dir_all(directory)?;
    let name = full
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default();
    let temp = directory.join(format!(".{name}.{}.tmp", unique_hex32()));
    let result = (|| {
        {
            let mut file = create_new(&temp, false)?;
            file.write_all(bytes)?;
            file.flush()?;
            file.sync_all()?;
        }
        fs::rename(&temp, &full)
    })();
    try_delete_file(&temp);
    result
}

/// Reads at most `maximum` bytes from the start of a file.
pub fn read_at_most(path: &Path, maximum: usize) -> io::Result<Vec<u8>> {
    use std::io::Read;
    let file = File::open(path)?;
    let mut buffer = Vec::new();
    file.take(maximum as u64).read_to_end(&mut buffer)?;
    Ok(buffer)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn write_via_tmp_replaces_and_cleans_up() {
        let root = std::env::temp_dir().join(format!("acl-fs-{}", unique_hex32()));
        let target = root.join("a").join("file.json");
        write_via_tmp(&target, b"one", true).unwrap();
        write_via_tmp(&target, b"two", true).unwrap();
        assert_eq!(fs::read(&target).unwrap(), b"two");
        assert!(!root.join("a").join("file.json.tmp").exists());
        if cfg!(unix) {
            assert_eq!(unix_mode(&target).unwrap(), Some(0o600));
        }
        write_atomic_unique(&target, b"three").unwrap();
        assert_eq!(fs::read(&target).unwrap(), b"three");
        assert_eq!(fs::read_dir(root.join("a")).unwrap().count(), 1);
        try_delete_tree(&root);
        assert!(!root.exists());
    }
}
