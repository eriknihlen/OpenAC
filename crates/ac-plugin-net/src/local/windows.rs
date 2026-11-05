//! The local endpoint on Windows: the named pipe `\\.\pipe\<name>`, up to
//! 64 instances, refusing remote clients, its access list granting only the
//! user this process runs as (OpenAC `PipeOptions.CurrentUserOnly`). Every
//! handle is overlapped, so one thread can wait on a read while another
//! writes, and a shutdown cancels both.

#![allow(unsafe_code)]

use std::io;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_BROKEN_PIPE, ERROR_FILE_NOT_FOUND, ERROR_IO_PENDING, ERROR_PIPE_BUSY,
    ERROR_PIPE_CONNECTED, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE, LocalFree,
    WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetSecurityInfo,
    SDDL_REVISION_1, SE_KERNEL_OBJECT,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID,
    SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER, TokenUser,
};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, FILE_FLAG_FIRST_PIPE_INSTANCE, FILE_FLAG_OVERLAPPED, OPEN_EXISTING,
    PIPE_ACCESS_DUPLEX, ReadFile, SECURITY_IDENTIFICATION, SECURITY_SQOS_PRESENT, WriteFile,
};
use windows_sys::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS,
    PIPE_TYPE_BYTE, PIPE_WAIT, WaitNamedPipeW,
};
use windows_sys::Win32::System::Threading::{
    CreateEventW, GetCurrentProcess, OpenProcessToken, WaitForSingleObject,
};

/// The pipe's buffer sizes, in bytes.
const BUFFER_BYTES: u32 = 64 * 1024;
/// The most instances (connections) the pipe has at once.
const INSTANCES: u32 = 64;

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

fn pipe_path(name: &str) -> Vec<u16> {
    wide(&format!(r"\\.\pipe\{name}"))
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

fn last_error_is(code: u32) -> bool {
    io::Error::last_os_error()
        .raw_os_error()
        .and_then(|c| u32::try_from(c).ok())
        == Some(code)
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
    sid_text(user.User.Sid)
}

/// A SID as text.
fn sid_text(sid: PSID) -> io::Result<String> {
    let mut text: windows_sys::core::PWSTR = std::ptr::null_mut();
    // SAFETY: the caller hands a valid SID; `text` receives a LocalAlloc'd
    // string on success, freed below.
    if unsafe { ConvertSidToStringSidW(sid, &raw mut text) } == 0 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: `text` is a NUL-terminated UTF-16 string from the system,
    // allocated with LocalAlloc and freed once here.
    let sid = unsafe {
        let mut length = 0;
        while *text.add(length) != 0 {
            length += 1;
        }
        let sid = String::from_utf16_lossy(std::slice::from_raw_parts(text, length));
        LocalFree(text.cast());
        sid
    };
    Ok(sid)
}

/// The SID that owns the pipe at `pipe` (its server set it to its user),
/// as text.
fn pipe_owner(pipe: HANDLE) -> io::Result<String> {
    let mut owner: PSID = std::ptr::null_mut();
    let mut descriptor: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: a valid pipe handle; `owner` points into `descriptor`, which
    // the system allocates with LocalAlloc and is freed below after use.
    let failed = unsafe {
        GetSecurityInfo(
            pipe,
            SE_KERNEL_OBJECT,
            OWNER_SECURITY_INFORMATION,
            &raw mut owner,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            &raw mut descriptor,
        )
    };
    if failed != 0 {
        return Err(io::Error::from_raw_os_error(
            i32::try_from(failed).unwrap_or(i32::MAX),
        ));
    }
    let text = sid_text(owner);
    // SAFETY: allocated by GetSecurityInfo; the owner SID inside it is no
    // longer used.
    unsafe {
        LocalFree(descriptor.cast());
    }
    text
}

/// One connected pipe end. Reads and writes may run on two threads at
/// once; [`Stream::shutdown`] ends both.
pub(super) struct Stream {
    pipe: Owned,
    closed: AtomicBool,
}

impl Stream {
    /// One overlapped operation started by `start` (handed its OVERLAPPED),
    /// waited for to the end: the bytes it moved.
    fn complete(&self, start: impl FnOnce(*mut OVERLAPPED) -> i32) -> io::Result<usize> {
        if self.closed.load(Ordering::SeqCst) {
            return Err(io::ErrorKind::NotConnected.into());
        }
        let done = event()?;
        let mut overlapped = OVERLAPPED {
            hEvent: done.0,
            ..OVERLAPPED::default()
        };
        if start(&raw mut overlapped) == 0 && !last_error_is(ERROR_IO_PENDING) {
            return Err(io::Error::last_os_error());
        }
        let mut moved = 0u32;
        // SAFETY: the operation was started on this pipe with `overlapped`,
        // which lives until the wait (TRUE) has seen it finish, even when a
        // shutdown cancels it.
        if unsafe { GetOverlappedResult(self.pipe.0, &raw const overlapped, &raw mut moved, 1) }
            == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(moved as usize)
    }

    pub(super) fn read(&self, buffer: &mut [u8]) -> io::Result<usize> {
        let length = u32::try_from(buffer.len()).unwrap_or(u32::MAX);
        let pipe = self.pipe.0;
        let target = buffer.as_mut_ptr();
        match self.complete(|overlapped| {
            // SAFETY: `buffer` outlives the operation, which `complete`
            // waits out.
            unsafe { ReadFile(pipe, target, length, std::ptr::null_mut(), overlapped) }
        }) {
            // The other end closed: the end of the stream.
            Err(error)
                if error
                    .raw_os_error()
                    .and_then(|code| u32::try_from(code).ok())
                    == Some(ERROR_BROKEN_PIPE) =>
            {
                Ok(0)
            }
            result => result,
        }
    }

    pub(super) fn write(&self, bytes: &[u8]) -> io::Result<usize> {
        let length = u32::try_from(bytes.len()).unwrap_or(u32::MAX);
        let pipe = self.pipe.0;
        let source = bytes.as_ptr();
        self.complete(|overlapped| {
            // SAFETY: `bytes` outlives the operation, which `complete`
            // waits out.
            unsafe { WriteFile(pipe, source, length, std::ptr::null_mut(), overlapped) }
        })
    }

    pub(super) fn shutdown(&self) {
        self.closed.store(true, Ordering::SeqCst);
        // SAFETY: a valid pipe handle; a null OVERLAPPED cancels every
        // operation on it, which their waits then see.
        unsafe {
            CancelIoEx(self.pipe.0, std::ptr::null());
        }
    }
}

/// Opens the pipe `name`, waiting up to `timeout` while every instance is
/// busy; `NotFound` when no server has created it.
pub(super) fn connect(name: &str, timeout: Duration) -> io::Result<Stream> {
    let path = pipe_path(name);
    let deadline = Instant::now() + timeout;
    loop {
        // SAFETY: `path` is NUL-terminated; the identification level keeps
        // the server from acting as this user.
        let pipe = unsafe {
            CreateFileW(
                path.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                0,
                std::ptr::null(),
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                std::ptr::null_mut(),
            )
        };
        if pipe != INVALID_HANDLE_VALUE {
            let pipe = Owned(pipe);
            // A server of another user could have taken the name first
            // (OpenAC `PipeOptions.CurrentUserOnly` on the client).
            if pipe_owner(pipe.0)? != current_user_sid()? {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "the local endpoint belongs to another user",
                ));
            }
            return Ok(Stream {
                pipe,
                closed: AtomicBool::new(false),
            });
        }
        if last_error_is(ERROR_FILE_NOT_FOUND) {
            return Err(io::Error::new(
                io::ErrorKind::NotFound,
                "no local endpoint is listening",
            ));
        }
        if !last_error_is(ERROR_PIPE_BUSY) {
            return Err(io::Error::last_os_error());
        }
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "every instance of the local endpoint is busy",
            ));
        }
        let wait = u32::try_from(left.as_millis()).unwrap_or(u32::MAX).max(1);
        // SAFETY: `path` is NUL-terminated.
        unsafe {
            WaitNamedPipeW(path.as_ptr(), wait);
        }
    }
}

/// The server side: instances created one at a time, each waiting for its
/// client.
pub(super) struct Listener {
    name: String,
    sddl: Vec<u16>,
    first: bool,
    /// The instance waiting for the next client. It outlives an `accept`
    /// that timed out: with no instance the name does not exist, and a
    /// client arriving between two `accept` calls would be turned away.
    waiting: Option<Waiting>,
}

/// An instance and its pending connect: the event it signals and the
/// OVERLAPPED the system holds while the connect is under way.
struct Waiting {
    pipe: Option<Owned>,
    done: Owned,
    // Boxed so its address stays put while the system holds it.
    overlapped: Box<OVERLAPPED>,
    pending: bool,
}

// SAFETY: the OVERLAPPED holds only the event handle, which may be used from
// any thread; the operation it describes belongs to the pipe, not a thread.
unsafe impl Send for Waiting {}

impl Drop for Waiting {
    fn drop(&mut self) {
        if !self.pending {
            return;
        }
        if let Some(pipe) = &self.pipe {
            let mut moved = 0u32;
            // SAFETY: the connect was started with this OVERLAPPED, still
            // alive; it is cancelled and waited for before it goes.
            unsafe {
                CancelIoEx(pipe.0, &raw const *self.overlapped);
                GetOverlappedResult(pipe.0, &raw const *self.overlapped, &raw mut moved, 1);
            }
        }
    }
}

impl Listener {
    pub(super) fn bind(name: &str) -> io::Result<Self> {
        let user = current_user_sid()?;
        // Owned by this user (a client checks that), and open to it alone.
        let sddl = wide(&format!("O:{user}D:P(A;;GA;;;{user})"));
        Ok(Self {
            name: name.to_owned(),
            sddl,
            first: true,
            waiting: None,
        })
    }

    fn instance(&mut self) -> io::Result<Owned> {
        let mut descriptor: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
        // SAFETY: `sddl` is NUL-terminated; `descriptor` receives a
        // LocalAlloc'd descriptor on success, freed after the pipe is made.
        if unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                self.sddl.as_ptr(),
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
        let path = pipe_path(&self.name);
        let first = if self.first {
            FILE_FLAG_FIRST_PIPE_INSTANCE
        } else {
            0
        };
        // SAFETY: `path` is NUL-terminated and `attributes` points at a
        // valid descriptor for the duration of the call.
        let pipe = unsafe {
            CreateNamedPipeW(
                path.as_ptr(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | first,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                INSTANCES,
                BUFFER_BYTES,
                BUFFER_BYTES,
                0,
                &raw const attributes,
            )
        };
        let created = io::Error::last_os_error();
        // SAFETY: allocated by the conversion above; the pipe keeps its own
        // copy.
        unsafe {
            LocalFree(descriptor.cast());
        }
        if pipe == INVALID_HANDLE_VALUE {
            return Err(created);
        }
        self.first = false;
        Ok(Owned(pipe))
    }

    /// A new instance with its connect started.
    fn wait_for_client(&mut self) -> io::Result<Waiting> {
        let pipe = self.instance()?;
        let done = event()?;
        let mut overlapped = Box::new(OVERLAPPED {
            hEvent: done.0,
            ..OVERLAPPED::default()
        });
        // SAFETY: the boxed OVERLAPPED and its event stay with the instance
        // until the connect has finished, or been cancelled and finished
        // (`Waiting`'s drop).
        let started = unsafe { ConnectNamedPipe(pipe.0, &raw mut *overlapped) };
        let pending = if started != 0 || last_error_is(ERROR_PIPE_CONNECTED) {
            false
        } else if last_error_is(ERROR_IO_PENDING) {
            true
        } else {
            return Err(io::Error::last_os_error());
        };
        Ok(Waiting {
            pipe: Some(pipe),
            done,
            overlapped,
            pending,
        })
    }

    /// The next client, or none when `timeout` passes first. The instance
    /// keeps waiting after a timeout, for the next call to take.
    pub(super) fn accept(&mut self, timeout: Duration) -> io::Result<Option<Stream>> {
        if self.waiting.is_none() {
            self.waiting = Some(self.wait_for_client()?);
        }
        let Some(waiting) = self.waiting.as_mut() else {
            return Ok(None);
        };
        if waiting.pending {
            let milliseconds = u32::try_from(timeout.as_millis()).unwrap_or(u32::MAX);
            // SAFETY: a valid event handle.
            let woke = unsafe { WaitForSingleObject(waiting.done.0, milliseconds) };
            if woke == WAIT_TIMEOUT {
                return Ok(None);
            }
            if woke != WAIT_OBJECT_0 {
                return Err(io::Error::last_os_error());
            }
            let Some(pipe) = waiting.pipe.as_ref() else {
                return Ok(None);
            };
            let mut moved = 0u32;
            // SAFETY: the connect has finished (its event is set) and its
            // OVERLAPPED is still alive.
            let ok = unsafe {
                GetOverlappedResult(pipe.0, &raw const *waiting.overlapped, &raw mut moved, 0)
            };
            waiting.pending = false;
            if ok == 0 {
                let error = io::Error::last_os_error();
                self.waiting = None;
                return Err(error);
            }
        }
        let Some(pipe) = self.waiting.take().and_then(|mut w| w.pipe.take()) else {
            return Ok(None);
        };
        Ok(Some(Stream {
            pipe,
            closed: AtomicBool::new(false),
        }))
    }
}
