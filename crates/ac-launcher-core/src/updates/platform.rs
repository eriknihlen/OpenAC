//! The two operating-system calls the launcher's self-update needs that
//! `std` does not offer: replacing a file while keeping the replaced one as
//! a backup, and waiting for a process that is not our child to end.
//!
//! The Windows halves are the crate's second unsafe module (the first is the
//! console glue): two file and process calls with string and handle
//! arguments only.
#![cfg_attr(windows, allow(unsafe_code))]

use std::io;
use std::path::Path;

/// Puts `replacement` where `replaced` is and keeps the replaced file as
/// `backup` (which must not exist). `replaced` must exist.
///
/// Windows replaces the file in one call that also moves the old one to the
/// backup name. Elsewhere the old file is first hard-linked (or copied) to
/// the backup name and the replacement is then renamed over it, so the
/// path never stops naming a whole file.
pub fn replace_file(replacement: &Path, replaced: &Path, backup: &Path) -> io::Result<()> {
    #[cfg(windows)]
    {
        windows::replace_file(replacement, replaced, backup)
    }
    #[cfg(not(windows))]
    {
        if std::fs::hard_link(replaced, backup).is_err() {
            std::fs::copy(replaced, backup)?;
        }
        std::fs::rename(replacement, replaced)
    }
}

/// Blocks until process `pid` has ended. Returns at once when no such
/// process can be opened (it already ended).
pub fn wait_for_process_exit(pid: u32) {
    #[cfg(windows)]
    windows::wait_for_process_exit(pid);
    #[cfg(unix)]
    {
        use nix::errno::Errno;
        use nix::sys::signal::kill;
        use nix::unistd::Pid;
        let Ok(raw) = i32::try_from(pid) else {
            return;
        };
        // Signal 0 only asks whether the process exists; EPERM means it
        // exists but belongs to someone else.
        while matches!(kill(Pid::from_raw(raw), None), Ok(()) | Err(Errno::EPERM)) {
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
    }
    #[cfg(not(any(windows, unix)))]
    let _ = pid;
}

#[cfg(windows)]
mod windows {
    use std::io;
    use std::os::windows::ffi::OsStrExt;
    use std::path::Path;

    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        REPLACEFILE_IGNORE_ACL_ERRORS, REPLACEFILE_IGNORE_MERGE_ERRORS, ReplaceFileW,
    };
    use windows_sys::Win32::System::Threading::{
        INFINITE, OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    };

    fn wide(path: &Path) -> Vec<u16> {
        path.as_os_str().encode_wide().chain(Some(0)).collect()
    }

    pub(super) fn replace_file(
        replacement: &Path,
        replaced: &Path,
        backup: &Path,
    ) -> io::Result<()> {
        let (replacement, replaced, backup) = (wide(replacement), wide(replaced), wide(backup));
        // SAFETY: the three names are NUL-terminated UTF-16 buffers that
        // outlive the call; the two reserved pointers are null as required.
        let ok = unsafe {
            ReplaceFileW(
                replaced.as_ptr(),
                replacement.as_ptr(),
                backup.as_ptr(),
                REPLACEFILE_IGNORE_MERGE_ERRORS | REPLACEFILE_IGNORE_ACL_ERRORS,
                std::ptr::null(),
                std::ptr::null(),
            )
        };
        if ok == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    pub(super) fn wait_for_process_exit(pid: u32) {
        // SAFETY: plain integer arguments; a null handle reports failure.
        let handle = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
        if handle.is_null() {
            return;
        }
        // SAFETY: `handle` is a process handle opened above with SYNCHRONIZE
        // access and closed exactly once after the wait.
        unsafe {
            WaitForSingleObject(handle, INFINITE);
            CloseHandle(handle);
        }
    }
}
