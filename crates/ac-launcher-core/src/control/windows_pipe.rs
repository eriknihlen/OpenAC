//! The control endpoint on Windows: the named pipe `\\.\pipe\<name>`, one
//! instance, reserved at creation (a second launcher asking for the same
//! name fails), refusing remote clients, its access list granting only the
//! user the launcher runs as. All waits are overlapped, so a connection's
//! deadline and the server's stop both end a wait.

#![allow(unsafe_code)]

use std::io;
use std::time::Instant;

use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_IO_PENDING, ERROR_PIPE_CONNECTED, HANDLE, INVALID_HANDLE_VALUE, LocalFree,
    WAIT_OBJECT_0,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER,
    TokenUser,
};
use windows_sys::Win32::Storage::FileSystem::{
    FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, PIPE_ACCESS_DUPLEX, ReadFile, WriteFile,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE,
    PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, GetCurrentProcess, INFINITE, OpenProcessToken, SetEvent, WaitForMultipleObjects,
};

/// The pipe's buffer sizes, in bytes.
const BUFFER_BYTES: u32 = 64 * 1024;

/// A kernel handle closed once, when dropped.
struct Owned(HANDLE);

// SAFETY: a kernel handle may be used and closed from any thread.
unsafe impl Send for Owned {}
// SAFETY: as above; the operations used on it are thread-safe.
unsafe impl Sync for Owned {}

impl Drop for Owned {
    fn drop(&mut self) {
        // SAFETY: the handle was returned open by the system and is closed
        // exactly once, here.
        unsafe {
            CloseHandle(self.0);
        }
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

/// A manual-reset event, not signalled.
fn event() -> io::Result<Owned> {
    // SAFETY: null attributes and name are allowed; a null result is an
    // error.
    let handle = unsafe { CreateEventW(std::ptr::null(), 1, 0, std::ptr::null()) };
    if handle.is_null() {
        Err(io::Error::last_os_error())
    } else {
        Ok(Owned(handle))
    }
}

/// The current user's SID as text (`S-1-5-...`).
fn current_user_sid() -> io::Result<String> {
    let mut token: HANDLE = std::ptr::null_mut();
    // SAFETY: the process pseudo-handle needs no closing; `token` receives
    // an open handle on success, owned below.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw mut token) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let token = Owned(token);
    let mut needed = 0u32;
    // SAFETY: a null buffer of length 0 asks for the size only.
    unsafe {
        GetTokenInformation(token.0, TokenUser, std::ptr::null_mut(), 0, &raw mut needed);
    }
    // The buffer is u64-aligned, as a TOKEN_USER (pointers) needs.
    let mut buffer = vec![0u64; (needed as usize).div_ceil(8).max(1)];
    let length = u32::try_from(buffer.len() * 8).unwrap_or(u32::MAX);
    // SAFETY: `buffer` holds `length` writable bytes.
    if unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            buffer.as_mut_ptr().cast(),
            length,
            &raw mut needed,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: the call above filled the buffer with a TOKEN_USER whose SID
    // points into the same buffer, which outlives its use here.
    let user: TOKEN_USER = unsafe { std::ptr::read(buffer.as_ptr().cast()) };
    let mut text: windows_sys::core::PWSTR = std::ptr::null_mut();
    // SAFETY: the SID is valid (above); `text` receives a LocalAlloc'd
    // string on success, freed below.
    if unsafe { ConvertSidToStringSidW(user.User.Sid, &raw mut text) } == 0 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: `text` is a NUL-terminated UTF-16 string from the system.
    let sid = unsafe {
        let mut length = 0;
        while *text.add(length) != 0 {
            length += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(text, length))
    };
    // SAFETY: allocated by ConvertSidToStringSidW with LocalAlloc.
    unsafe {
        LocalFree(text.cast());
    }
    Ok(sid)
}

/// Creates the pipe `name`, reserved for this process and open to the
/// current user only.
pub(super) fn bind(name: &str) -> io::Result<(Endpoint, Stopper)> {
    let sddl = wide(&format!("D:P(A;;GA;;;{})", current_user_sid()?));
    let mut descriptor: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: `sddl` is NUL-terminated; `descriptor` receives a LocalAlloc'd
    // descriptor on success, freed after the pipe is created.
    if unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            SDDL_REVISION_1,
            &raw mut descriptor,
            std::ptr::null_mut(),
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    let attributes = SECURITY_ATTRIBUTES {
        nLength: u32::try_from(std::mem::size_of::<SECURITY_ATTRIBUTES>()).unwrap_or(0),
        lpSecurityDescriptor: descriptor,
        bInheritHandle: 0,
    };
    let path = wide(&format!(r"\\.\pipe\{name}"));
    // SAFETY: `path` is NUL-terminated and `attributes` points at a valid
    // descriptor for the duration of the call.
    let pipe = unsafe {
        CreateNamedPipeW(
            path.as_ptr(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            1,
            BUFFER_BYTES,
            BUFFER_BYTES,
            0,
            &raw const attributes,
        )
    };
    let created = io::Error::last_os_error();
    // SAFETY: allocated by the conversion above; the pipe keeps its own copy.
    unsafe {
        LocalFree(descriptor.cast());
    }
    if pipe == INVALID_HANDLE_VALUE {
        return Err(created);
    }
    let pipe = Owned(pipe);
    let stop = std::sync::Arc::new(event()?);
    let io_event = event()?;
    Ok((
        Endpoint {
            pipe,
            stop: std::sync::Arc::clone(&stop),
            io_event,
            connected: false,
        },
        Stopper { stop },
    ))
}

/// Ends the server's waits.
pub(super) struct Stopper {
    stop: std::sync::Arc<Owned>,
}

impl Stopper {
    pub(super) fn stop(&self) {
        // SAFETY: a valid event handle, alive while either side holds it.
        unsafe {
            SetEvent(self.stop.0);
        }
    }
}

/// The pipe, serving one client at a time.
pub(super) struct Endpoint {
    pipe: Owned,
    stop: std::sync::Arc<Owned>,
    io_event: Owned,
    connected: bool,
}

/// How an overlapped operation's wait ended.
enum Waited {
    Done(u32),
    Stopped,
    TimedOut,
}

impl Endpoint {
    /// Waits for the next client: `false` once the server is stopped.
    pub(super) fn accept(&mut self) -> io::Result<bool> {
        let mut overlapped = OVERLAPPED {
            hEvent: self.io_event.0,
            ..OVERLAPPED::default()
        };
        // SAFETY: `overlapped` and its event outlive the operation: it is
        // finished (or cancelled and finished) before this returns.
        let started = unsafe { ConnectNamedPipe(self.pipe.0, &raw mut overlapped) };
        if started == 0 {
            let error = io::Error::last_os_error();
            match error
                .raw_os_error()
                .and_then(|code| u32::try_from(code).ok())
            {
                Some(ERROR_PIPE_CONNECTED) => {}
                Some(ERROR_IO_PENDING) => match self.wait(&overlapped, None)? {
                    Waited::Done(_) => {}
                    Waited::Stopped | Waited::TimedOut => return Ok(false),
                },
                _ => return Err(error),
            }
        }
        self.connected = true;
        Ok(true)
    }

    /// Waits for `overlapped` until it completes, the server stops, or
    /// `deadline`; a wait that ends early cancels the operation and lets it
    /// finish before returning.
    fn wait(&self, overlapped: &OVERLAPPED, deadline: Option<Instant>) -> io::Result<Waited> {
        let handles = [self.io_event.0, self.stop.0];
        let milliseconds = deadline.map_or(INFINITE, |deadline| {
            let left = deadline
                .saturating_duration_since(Instant::now())
                .as_millis();
            u32::try_from(left).unwrap_or(INFINITE - 1)
        });
        // SAFETY: two valid event handles.
        let woke = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, milliseconds) };
        let early = match woke {
            WAIT_OBJECT_0 => None,
            woke if woke == WAIT_OBJECT_0 + 1 => Some(Waited::Stopped),
            _ => Some(Waited::TimedOut),
        };
        if early.is_some() {
            // SAFETY: the operation was started on this pipe with this
            // OVERLAPPED, which is still alive.
            unsafe {
                CancelIoEx(self.pipe.0, overlapped);
            }
        }
        let mut transferred = 0u32;
        // SAFETY: as above; waiting (TRUE) lets a cancelled operation
        // finish so the OVERLAPPED is no longer in use.
        let ok = unsafe { GetOverlappedResult(self.pipe.0, overlapped, &raw mut transferred, 1) };
        if let Some(early) = early {
            return Ok(early);
        }
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Waited::Done(transferred))
    }

    fn timed_out() -> io::Error {
        io::Error::new(
            io::ErrorKind::TimedOut,
            "the connection outlived its deadline",
        )
    }

    fn stopped() -> io::Error {
        io::Error::new(io::ErrorKind::Interrupted, "the control server is stopping")
    }

    /// Reads exactly `buffer.len()` bytes before `deadline`.
    pub(super) fn read_exact(&mut self, buffer: &mut [u8], deadline: Instant) -> io::Result<()> {
        let mut filled = 0;
        while filled < buffer.len() {
            let mut overlapped = OVERLAPPED {
                hEvent: self.io_event.0,
                ..OVERLAPPED::default()
            };
            let rest = &mut buffer[filled..];
            let length = u32::try_from(rest.len()).unwrap_or(u32::MAX);
            // SAFETY: `rest` and `overlapped` outlive the operation, which
            // `wait` finishes before returning.
            let started = unsafe {
                ReadFile(
                    self.pipe.0,
                    rest.as_mut_ptr(),
                    length,
                    std::ptr::null_mut(),
                    &raw mut overlapped,
                )
            };
            if started == 0 {
                let error = io::Error::last_os_error();
                if error
                    .raw_os_error()
                    .and_then(|code| u32::try_from(code).ok())
                    != Some(ERROR_IO_PENDING)
                {
                    return Err(error);
                }
            }
            match self.wait(&overlapped, Some(deadline))? {
                Waited::Done(0) => return Err(io::ErrorKind::UnexpectedEof.into()),
                Waited::Done(count) => filled += count as usize,
                Waited::Stopped => return Err(Self::stopped()),
                Waited::TimedOut => return Err(Self::timed_out()),
            }
        }
        Ok(())
    }

    /// Writes all of `bytes` before `deadline`.
    pub(super) fn write_all(&mut self, bytes: &[u8], deadline: Instant) -> io::Result<()> {
        let mut written = 0;
        while written < bytes.len() {
            let mut overlapped = OVERLAPPED {
                hEvent: self.io_event.0,
                ..OVERLAPPED::default()
            };
            let rest = &bytes[written..];
            let length = u32::try_from(rest.len()).unwrap_or(u32::MAX);
            // SAFETY: as in `read_exact`.
            let started = unsafe {
                WriteFile(
                    self.pipe.0,
                    rest.as_ptr(),
                    length,
                    std::ptr::null_mut(),
                    &raw mut overlapped,
                )
            };
            if started == 0 {
                let error = io::Error::last_os_error();
                if error
                    .raw_os_error()
                    .and_then(|code| u32::try_from(code).ok())
                    != Some(ERROR_IO_PENDING)
                {
                    return Err(error);
                }
            }
            match self.wait(&overlapped, Some(deadline))? {
                Waited::Done(0) => return Err(io::ErrorKind::WriteZero.into()),
                Waited::Done(count) => written += count as usize,
                Waited::Stopped => return Err(Self::stopped()),
                Waited::TimedOut => return Err(Self::timed_out()),
            }
        }
        Ok(())
    }

    /// Lets the client read the reply before the connection ends: a
    /// disconnect discards what the client has not read, so this waits for
    /// the client to close its end, until `deadline` or a stop.
    pub(super) fn flush(&mut self, deadline: Instant) {
        let mut overlapped = OVERLAPPED {
            hEvent: self.io_event.0,
            ..OVERLAPPED::default()
        };
        let mut byte = [0u8; 1];
        // SAFETY: as in `read_exact`; `wait` finishes the operation.
        let started = unsafe {
            ReadFile(
                self.pipe.0,
                byte.as_mut_ptr(),
                1,
                std::ptr::null_mut(),
                &raw mut overlapped,
            )
        };
        let pending = started != 0
            || io::Error::last_os_error()
                .raw_os_error()
                .and_then(|code| u32::try_from(code).ok())
                == Some(ERROR_IO_PENDING);
        if pending {
            let _ = self.wait(&overlapped, Some(deadline));
        }
    }

    /// Ends the current client's connection, if any.
    pub(super) fn disconnect(&mut self) {
        if std::mem::take(&mut self.connected) {
            // SAFETY: a valid pipe handle; disconnecting an idle pipe fails
            // harmlessly.
            unsafe {
                DisconnectNamedPipe(self.pipe.0);
            }
        }
    }
}
